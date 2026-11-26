// ArchoeraMvsync 平台能力原生桥接
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

//! Linux 后端（C++ + libdbus）：MPRIS2 媒体会话（系统媒体键/蓝牙 AVRCP/桌面
//! 媒体面板）、ScreenSaver 防休眠抑制、熄屏/锁屏事件、系统主题色（DE accent）、
//! 单实例文件锁；窗口状态经 dlopen GTK/GDK 轻轮询（X11/Wayland 统一）。
//!
//! D-Bus 连接懒建立：首次需要时连会话总线、申请 MPRIS 服务名、注册 match；
//! 泵线程 `dbus_connection_read_write_dispatch` 分发信号与方法调用。
//! `dbus_threads_init_default()` 保证 Dart 线程 send 与泵线程 dispatch 并发安全。

#include "backend.h"

#include "core.h"
#include "safe_file_posix.h"  // Linux/macOS 共用安全文件原语（header-only）

#include <dbus/dbus.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <signal.h>
#include <sys/file.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace archoera {

// 异步刷新系统强调色缓存（定义见文件后部）；供 DBus 信号过滤器调用。
void requestAccent();

// 异步查询系统深浅色并推送（定义见文件后部）；供 DBus 信号过滤器调用。
void requestTheme();

namespace {

// ── 常量 ──────────────────────────────────────────────────────────
constexpr const char* kObjectPath = "/org/mpris/MediaPlayer2";
constexpr const char* kIfaceRoot = "org.mpris.MediaPlayer2";
constexpr const char* kIfacePlayer = "org.mpris.MediaPlayer2.Player";
constexpr const char* kIfaceProps = "org.freedesktop.DBus.Properties";
constexpr const char* kIfaceIntrospect = "org.freedesktop.DBus.Introspectable";

constexpr const char* kSsFreedesktop = "org.freedesktop.ScreenSaver";
constexpr const char* kSsFreedesktopPath = "/org/freedesktop/ScreenSaver";
constexpr const char* kSsGnome = "org.gnome.ScreenSaver";
constexpr const char* kSsGnomePath = "/org/gnome/ScreenSaver";

constexpr const char* kIntrospectXml =
    "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" "
    "\"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">"
    "<node><interface name=\"org.mpris.MediaPlayer2.Player\">"
    "<method name=\"Play\"/><method name=\"Pause\"/><method name=\"PlayPause\"/>"
    "<method name=\"Stop\"/><method name=\"Next\"/><method name=\"Previous\"/>"
    "<method name=\"Seek\"><arg direction=\"in\" type=\"x\"/></method>"
    "<method name=\"SetPosition\"><arg direction=\"in\" type=\"o\"/><arg direction=\"in\" type=\"x\"/></method>"
    "</interface><interface name=\"org.mpris.MediaPlayer2\">"
    "<method name=\"Raise\"/><method name=\"Quit\"/></interface></node>";

int64_t nowMs() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch())
        .count();
}

// ── 全局状态 ──────────────────────────────────────────────────────
std::mutex g_mtx;
DBusConnection* g_conn = nullptr;
std::atomic<bool> g_running{false};
// 堆指针：即使宿主未调 apl_shutdown 就退出，也不触发静态 std::thread 析构的
// std::terminate（仅泄漏，不崩）。
std::thread* g_pump = nullptr;

std::string g_service = "org.mpris.MediaPlayer2.archoera";
std::string g_ss_service = kSsFreedesktop;
std::string g_ss_path = kSsFreedesktopPath;

int g_state = 0;  // 0=stopped 1=playing 2=paused
int g_loop = 0;   // 0=Playlist 1=Track
bool g_shuffle = false;
double g_volume = 1.0;
int64_t g_position_ms = 0;
int64_t g_position_base = 0;
double g_rate = 1.0;

uint32_t g_track_serial = 0;
std::string g_title, g_artist, g_album, g_art;
int64_t g_duration_ms = -1;

uint32_t g_cookie = 0;
bool g_inhibit_on = false;

// 防休眠注册/释放的异步请求：Dart/主线程只登记目标状态，实际阻塞式 DBus
// 调用由泵线程执行，避免播放时主线程同步等待导致 UI「未响应」。
std::atomic<bool> g_inhibit_want{false};
std::atomic<bool> g_inhibit_pending{false};

std::atomic<bool> g_screen_events{false};
std::atomic<bool> g_accent_events{false};
// 订阅时请求泵线程立即推送一次当前强调色（连接就绪后消费）。
std::atomic<bool> g_accent_pending{false};

// 系统深浅色（light/dark）事件订阅 + 推送缓存。
std::atomic<bool> g_theme_events{false};
std::atomic<bool> g_theme_pending{false};
std::atomic<bool> g_theme_dark{false};
std::atomic<bool> g_theme_valid{false};

// 深链接收常驻：与主题/防休眠订阅无关，启动即建立会话总线连接并注册
// OpenUri，保证次实例转发的 archoera:// 总能成功送达。
std::atomic<bool> g_deeplink_events{false};

// 系统会话结束（关机/重启/注销）：SIGTERM 由 systemd/logind 终止用户会话时
// 投递。**休眠/待机不会发送 SIGTERM**，因此不会误判为结束会话。
// 信号处理器只允许 async-signal-safe 操作 → 仅置原子位，实际事件由泵线程派发。
std::atomic<bool> g_session_end_requested{false};
bool g_sigterm_installed = false;
struct sigaction g_prev_sigterm {};

void onSessionEndSignal(int) {
    g_session_end_requested.store(true, std::memory_order_release);
}

void installSessionEndSignal() {
    if (g_sigterm_installed) return;
    struct sigaction sa {};
    sa.sa_handler = onSessionEndSignal;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_RESTART;  // 不打断 D-Bus 读等系统调用
    if (::sigaction(SIGTERM, &sa, &g_prev_sigterm) == 0) {
        g_sigterm_installed = true;
    }
}

void restoreSessionEndSignal() {
    if (!g_sigterm_installed) return;
    ::sigaction(SIGTERM, &g_prev_sigterm, nullptr);
    g_sigterm_installed = false;
}

// 系统强调色缓存（异步查询结果）：apl_system_accent 立即返回缓存，避免在
// Dart/主线程同步等待 XDG portal（切回应用时会重读，同步调用会卡住 UI）。
std::mutex g_accent_mtx;
int32_t g_accent_r = 0, g_accent_g = 0, g_accent_b = 0;
bool g_accent_valid = false;

// ── 小工具 ────────────────────────────────────────────────────────
DBusConnection* conn() {
    std::lock_guard<std::mutex> lock(g_mtx);
    return g_conn;
}

void appendSvString(DBusMessageIter* arr, const char* key, const char* value) {
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(arr, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "s", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_STRING, &value);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(arr, &entry);
}

void appendSvBool(DBusMessageIter* arr, const char* key, dbus_bool_t value) {
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(arr, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "b", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_BOOLEAN, &value);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(arr, &entry);
}

void appendSvDouble(DBusMessageIter* arr, const char* key, double value) {
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(arr, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "d", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_DOUBLE, &value);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(arr, &entry);
}

void appendSvInt64(DBusMessageIter* arr, const char* key, int64_t value) {
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(arr, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "x", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_INT64, &value);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(arr, &entry);
}

int64_t currentPositionMs() {
    std::lock_guard<std::mutex> lock(g_mtx);
    if (g_state == 1) return g_position_ms + (nowMs() - g_position_base);
    return g_position_ms;
}

// ── 属性读写 ──────────────────────────────────────────────────────
const char* propSignature(const char* iface, const char* name) {
    if (std::strcmp(iface, kIfaceRoot) == 0) {
        if (std::strcmp(name, "CanQuit") == 0 ||
            std::strcmp(name, "CanRaise") == 0 ||
            std::strcmp(name, "HasTrackList") == 0) {
            return "b";
        }
        if (std::strcmp(name, "Identity") == 0 ||
            std::strcmp(name, "DesktopEntry") == 0) {
            return "s";
        }
        if (std::strcmp(name, "SupportedUriSchemes") == 0 ||
            std::strcmp(name, "SupportedMimeTypes") == 0) {
            return "as";
        }
        return nullptr;
    }
    if (std::strcmp(iface, kIfacePlayer) == 0) {
        if (std::strcmp(name, "PlaybackStatus") == 0 ||
            std::strcmp(name, "LoopStatus") == 0) {
            return "s";
        }
        if (std::strcmp(name, "Rate") == 0 || std::strcmp(name, "Volume") == 0 ||
            std::strcmp(name, "MinimumRate") == 0 ||
            std::strcmp(name, "MaximumRate") == 0) {
            return "d";
        }
        if (std::strcmp(name, "Shuffle") == 0 ||
            std::strcmp(name, "CanGoNext") == 0 ||
            std::strcmp(name, "CanGoPrevious") == 0 ||
            std::strcmp(name, "CanPlay") == 0 ||
            std::strcmp(name, "CanPause") == 0 ||
            std::strcmp(name, "CanSeek") == 0 ||
            std::strcmp(name, "CanControl") == 0) {
            return "b";
        }
        if (std::strcmp(name, "Metadata") == 0) return "a{sv}";
        if (std::strcmp(name, "Position") == 0) return "x";
        return nullptr;
    }
    return nullptr;
}

// 写属性值到 iter（不含外层 variant 包装）。
void writePropValue(DBusMessageIter* it, const char* iface, const char* name) {
    if (std::strcmp(iface, kIfaceRoot) == 0) {
        if (std::strcmp(name, "CanQuit") == 0) {
            dbus_bool_t v = FALSE;
            dbus_message_iter_append_basic(it, DBUS_TYPE_BOOLEAN, &v);
        } else if (std::strcmp(name, "CanRaise") == 0) {
            dbus_bool_t v = TRUE;
            dbus_message_iter_append_basic(it, DBUS_TYPE_BOOLEAN, &v);
        } else if (std::strcmp(name, "HasTrackList") == 0) {
            dbus_bool_t v = FALSE;
            dbus_message_iter_append_basic(it, DBUS_TYPE_BOOLEAN, &v);
        } else if (std::strcmp(name, "Identity") == 0) {
            const char* v = "ArchoeraMvsync";
            dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
        } else if (std::strcmp(name, "DesktopEntry") == 0) {
            const char* v = "awa.archoera.betastudio2.archoera_mvsync";
            dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
        } else {
            DBusMessageIter arr;
            dbus_message_iter_open_container(it, DBUS_TYPE_ARRAY, "s", &arr);
            dbus_message_iter_close_container(it, &arr);
        }
        return;
    }
    // Player
    if (std::strcmp(name, "PlaybackStatus") == 0) {
        const char* v = "Stopped";
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            v = g_state == 1 ? "Playing" : (g_state == 2 ? "Paused" : "Stopped");
        }
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "LoopStatus") == 0) {
        int loop;
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            loop = g_loop;
        }
        const char* v = loop == 1 ? "Track" : "Playlist";
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "Rate") == 0 ||
               std::strcmp(name, "MinimumRate") == 0 ||
               std::strcmp(name, "MaximumRate") == 0) {
        double v = 1.0;
        dbus_message_iter_append_basic(it, DBUS_TYPE_DOUBLE, &v);
    } else if (std::strcmp(name, "Shuffle") == 0) {
        dbus_bool_t v;
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            v = g_shuffle ? TRUE : FALSE;
        }
        dbus_message_iter_append_basic(it, DBUS_TYPE_BOOLEAN, &v);
    } else if (std::strcmp(name, "Volume") == 0) {
        double v;
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            v = g_volume;
        }
        dbus_message_iter_append_basic(it, DBUS_TYPE_DOUBLE, &v);
    } else if (std::strcmp(name, "Position") == 0) {
        int64_t v = currentPositionMs() * 1000;
        dbus_message_iter_append_basic(it, DBUS_TYPE_INT64, &v);
    } else if (std::strcmp(name, "Metadata") == 0) {
        std::string title, artist, album, art;
        int64_t dur;
        uint32_t serial;
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            title = g_title;
            artist = g_artist;
            album = g_album;
            art = g_art;
            dur = g_duration_ms;
            serial = g_track_serial;
        }
        char trackid[64];
        std::snprintf(trackid, sizeof(trackid),
                      "/org/mpris/MediaPlayer2/Track/%u", serial);
        DBusMessageIter arr;
        dbus_message_iter_open_container(it, DBUS_TYPE_ARRAY, "{sv}", &arr);
        const char* tid = trackid;
        {
            DBusMessageIter entry, var;
            dbus_message_iter_open_container(&arr, DBUS_TYPE_DICT_ENTRY, nullptr,
                                             &entry);
            const char* k = "mpris:trackid";
            dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &k);
            dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "o", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_OBJECT_PATH, &tid);
            dbus_message_iter_close_container(&entry, &var);
            dbus_message_iter_close_container(&arr, &entry);
        }
        if (dur > 0) appendSvInt64(&arr, "mpris:length", dur * 1000);
        if (!title.empty()) appendSvString(&arr, "xesam:title", title.c_str());
        if (!artist.empty()) {
            DBusMessageIter entry, var, list;
            dbus_message_iter_open_container(&arr, DBUS_TYPE_DICT_ENTRY, nullptr,
                                             &entry);
            const char* k = "xesam:artist";
            dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &k);
            dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "as",
                                             &var);
            dbus_message_iter_open_container(&var, DBUS_TYPE_ARRAY, "s", &list);
            const char* a = artist.c_str();
            dbus_message_iter_append_basic(&list, DBUS_TYPE_STRING, &a);
            dbus_message_iter_close_container(&var, &list);
            dbus_message_iter_close_container(&entry, &var);
            dbus_message_iter_close_container(&arr, &entry);
        }
        if (!album.empty()) appendSvString(&arr, "xesam:album", album.c_str());
        if (!art.empty()) appendSvString(&arr, "mpris:artUrl", art.c_str());
        dbus_message_iter_close_container(it, &arr);
    } else {
        // Can*：首期恒真
        dbus_bool_t v = TRUE;
        dbus_message_iter_append_basic(it, DBUS_TYPE_BOOLEAN, &v);
    }
}

const char* const kPlayerProps[] = {
    "PlaybackStatus", "LoopStatus", "Rate",  "Shuffle", "Metadata",
    "Volume",         "Position",   "MinimumRate", "MaximumRate",
    "CanGoNext",      "CanGoPrevious", "CanPlay", "CanPause", "CanSeek",
    "CanControl"};
const char* const kRootProps[] = {
    "CanQuit",      "CanRaise",           "HasTrackList",
    "Identity",     "DesktopEntry",       "SupportedUriSchemes",
    "SupportedMimeTypes"};

// ── 回复助手 ──────────────────────────────────────────────────────
void send(DBusConnection* c, DBusMessage* reply) {
    if (reply == nullptr) return;
    // 只入队，不在此处 flush：dbus_connection_flush 会同步等待 socket 可写，
    // 在 Dart/主线程上调用会阻塞 UI（播放时推送 MPRIS PropertiesChanged 即触发
    // 「应用未响应」）。泵线程的 dbus_connection_read_write_dispatch 会持续把
    // 发送队列写出，无需调用方阻塞 flush。
    dbus_connection_send(c, reply, nullptr);
    dbus_message_unref(reply);
}

void replyEmpty(DBusConnection* c, DBusMessage* msg) {
    send(c, dbus_message_new_method_return(msg));
}

void replyError(DBusConnection* c, DBusMessage* msg, const char* name,
                const char* text) {
    send(c, dbus_message_new_error(msg, name, text));
}

void replyString(DBusConnection* c, DBusMessage* msg, const char* s) {
    DBusMessage* reply = dbus_message_new_method_return(msg);
    dbus_message_append_args(reply, DBUS_TYPE_STRING, &s, DBUS_TYPE_INVALID);
    send(c, reply);
}

// ── Properties ────────────────────────────────────────────────────
void propGet(DBusConnection* c, DBusMessage* msg) {
    DBusError err;
    dbus_error_init(&err);
    const char* iface = nullptr;
    const char* name = nullptr;
    if (!dbus_message_get_args(msg, &err, DBUS_TYPE_STRING, &iface,
                               DBUS_TYPE_STRING, &name, DBUS_TYPE_INVALID)) {
        dbus_error_free(&err);
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Get");
    }
    const char* sig = propSignature(iface, name);
    if (sig == nullptr) {
        return replyError(c, msg, "org.freedesktop.DBus.Error.UnknownProperty",
                          name);
    }
    DBusMessage* reply = dbus_message_new_method_return(msg);
    DBusMessageIter it, var;
    dbus_message_iter_init_append(reply, &it);
    dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, sig, &var);
    writePropValue(&var, iface, name);
    dbus_message_iter_close_container(&it, &var);
    send(c, reply);
}

void propGetAll(DBusConnection* c, DBusMessage* msg) {
    DBusError err;
    dbus_error_init(&err);
    const char* iface = nullptr;
    if (!dbus_message_get_args(msg, &err, DBUS_TYPE_STRING, &iface,
                               DBUS_TYPE_INVALID)) {
        dbus_error_free(&err);
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "GetAll");
    }
    const char* const* names = nullptr;
    size_t count = 0;
    if (std::strcmp(iface, kIfacePlayer) == 0) {
        names = kPlayerProps;
        count = sizeof(kPlayerProps) / sizeof(kPlayerProps[0]);
    } else if (std::strcmp(iface, kIfaceRoot) == 0) {
        names = kRootProps;
        count = sizeof(kRootProps) / sizeof(kRootProps[0]);
    } else {
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_INTERFACE, iface);
    }
    DBusMessage* reply = dbus_message_new_method_return(msg);
    DBusMessageIter it, arr;
    dbus_message_iter_init_append(reply, &it);
    dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "{sv}", &arr);
    for (size_t i = 0; i < count; i++) {
        const char* sig = propSignature(iface, names[i]);
        if (sig == nullptr) continue;
        DBusMessageIter entry, var;
        dbus_message_iter_open_container(&arr, DBUS_TYPE_DICT_ENTRY, nullptr,
                                         &entry);
        dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &names[i]);
        dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, sig, &var);
        writePropValue(&var, iface, names[i]);
        dbus_message_iter_close_container(&entry, &var);
        dbus_message_iter_close_container(&arr, &entry);
    }
    dbus_message_iter_close_container(&it, &arr);
    send(c, reply);
}

void propSet(DBusConnection* c, DBusMessage* msg) {
    DBusMessageIter it;
    if (!dbus_message_iter_init(msg, &it)) {
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Set");
    }
    if (dbus_message_iter_get_arg_type(&it) != DBUS_TYPE_STRING) {
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Set");
    }
    const char* name = nullptr;
    dbus_message_iter_get_basic(&it, &name);
    dbus_message_iter_next(&it);
    if (dbus_message_iter_get_arg_type(&it) != DBUS_TYPE_STRING) {
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Set");
    }
    dbus_message_iter_next(&it);
    if (dbus_message_iter_get_arg_type(&it) != DBUS_TYPE_VARIANT) {
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Set");
    }
    DBusMessageIter var;
    dbus_message_iter_recurse(&it, &var);
    const int type = dbus_message_iter_get_arg_type(&var);
    bool read_only = false;
    {
        std::lock_guard<std::mutex> lock(g_mtx);
        if (std::strcmp(name, "Volume") == 0 && type == DBUS_TYPE_DOUBLE) {
            double v = 0;
            dbus_message_iter_get_basic(&var, &v);
            g_volume = v;
        } else if (std::strcmp(name, "LoopStatus") == 0 &&
                   type == DBUS_TYPE_STRING) {
            const char* v = nullptr;
            dbus_message_iter_get_basic(&var, &v);
            g_loop = (v != nullptr && std::strcmp(v, "Track") == 0) ? 1 : 0;
        } else if (std::strcmp(name, "Shuffle") == 0 &&
                   type == DBUS_TYPE_BOOLEAN) {
            dbus_bool_t v = FALSE;
            dbus_message_iter_get_basic(&var, &v);
            g_shuffle = v != FALSE;
        } else {
            read_only = true;
        }
    }
    if (read_only) {
        return replyError(c, msg, "org.freedesktop.DBus.Error.PropertyReadOnly",
                          name);
    }
    replyEmpty(c, msg);
}

// ── PropertiesChanged 推送 ────────────────────────────────────────
void emitChanged(DBusConnection* c,
                 const std::vector<const char*>& changed) {
    if (c == nullptr) return;
    DBusMessage* sig = dbus_message_new_signal(kObjectPath, kIfaceProps,
                                               "PropertiesChanged");
    DBusMessageIter it, arr, invalid;
    dbus_message_iter_init_append(sig, &it);
    const char* iface = kIfacePlayer;
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &iface);
    dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "{sv}", &arr);
    for (const char* name : changed) {
        const char* psig = propSignature(kIfacePlayer, name);
        if (psig == nullptr) continue;
        DBusMessageIter entry, var;
        dbus_message_iter_open_container(&arr, DBUS_TYPE_DICT_ENTRY, nullptr,
                                         &entry);
        dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &name);
        dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, psig, &var);
        writePropValue(&var, kIfacePlayer, name);
        dbus_message_iter_close_container(&entry, &var);
        dbus_message_iter_close_container(&arr, &entry);
    }
    dbus_message_iter_close_container(&it, &arr);
    dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "s", &invalid);
    dbus_message_iter_close_container(&it, &invalid);
    send(c, sig);
}

// ── MPRIS 方法分发 ────────────────────────────────────────────────
void playerCommand(DBusConnection* c, DBusMessage* msg, int32_t command) {
    dispatch(makeCommand(command));
    replyEmpty(c, msg);
}

void handleMethodCall(DBusConnection* c, DBusMessage* msg) {
    const char* path = dbus_message_get_path(msg);
    if (path == nullptr || std::strcmp(path, kObjectPath) != 0) {
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_OBJECT,
                          path != nullptr ? path : "");
    }
    const char* iface = dbus_message_get_interface(msg);
    const char* member = dbus_message_get_member(msg);
    if (iface == nullptr || member == nullptr) {
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_METHOD, "");
    }
    if (std::strcmp(iface, kIfaceProps) == 0) {
        if (std::strcmp(member, "Get") == 0) return propGet(c, msg);
        if (std::strcmp(member, "GetAll") == 0) return propGetAll(c, msg);
        if (std::strcmp(member, "Set") == 0) return propSet(c, msg);
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_METHOD, member);
    }
    if (std::strcmp(iface, kIfaceIntrospect) == 0) {
        return replyString(c, msg, kIntrospectXml);
    }
    if (std::strcmp(iface, kIfaceRoot) == 0) {
        if (std::strcmp(member, "Raise") == 0) return replyEmpty(c, msg);
        if (std::strcmp(member, "Quit") == 0) {
            dispatch(makeCommand(CMD_STOP));
            return replyEmpty(c, msg);
        }
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_METHOD, member);
    }
    if (std::strcmp(iface, kIfacePlayer) == 0) {
        if (std::strcmp(member, "Play") == 0) return playerCommand(c, msg, CMD_PLAY);
        if (std::strcmp(member, "Pause") == 0) return playerCommand(c, msg, CMD_PAUSE);
        if (std::strcmp(member, "PlayPause") == 0) return playerCommand(c, msg, CMD_TOGGLE);
        if (std::strcmp(member, "Stop") == 0) return playerCommand(c, msg, CMD_STOP);
        if (std::strcmp(member, "Next") == 0) return playerCommand(c, msg, CMD_NEXT);
        if (std::strcmp(member, "Previous") == 0) return playerCommand(c, msg, CMD_PREV);
        if (std::strcmp(member, "Seek") == 0) {
            DBusError err;
            dbus_error_init(&err);
            int64_t offset = 0;
            if (dbus_message_get_args(msg, &err, DBUS_TYPE_INT64, &offset,
                                      DBUS_TYPE_INVALID)) {
                dispatch(makeSeek(offset / 1000, 0));
            }
            dbus_error_free(&err);
            return replyEmpty(c, msg);
        }
        if (std::strcmp(member, "SetPosition") == 0) {
            DBusMessageIter it;
            if (dbus_message_iter_init(msg, &it)) {
                dbus_message_iter_next(&it);  // 跳过 trackid
                if (dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_INT64) {
                    int64_t pos = 0;
                    dbus_message_iter_get_basic(&it, &pos);
                    dispatch(makeSeek(0, pos / 1000));
                }
            }
            return replyEmpty(c, msg);
        }
        if (std::strcmp(member, "OpenUri") == 0) return replyEmpty(c, msg);
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_METHOD, member);
    }
    return replyError(c, msg, DBUS_ERROR_UNKNOWN_INTERFACE, iface);
}

// ── 信号过滤 ──────────────────────────────────────────────────────
void setPendingDeepLink(const char* uri);

DBusHandlerResult filter(DBusConnection* c, DBusMessage* msg, void*) {
    const int type = dbus_message_get_type(msg);
    if (type == DBUS_MESSAGE_TYPE_METHOD_CALL) {
        handleMethodCall(c, msg);
        return DBUS_HANDLER_RESULT_HANDLED;
    }
    if (type == DBUS_MESSAGE_TYPE_SIGNAL) {
        const char* member = dbus_message_get_member(msg);
        if (member == nullptr) return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
        if (dbus_message_is_signal(msg, "org.archoera.DeepLink", "Uri")) {
            const char* uri = nullptr;
            if (dbus_message_get_args(msg, nullptr, DBUS_TYPE_STRING, &uri,
                                      DBUS_TYPE_INVALID)) {
                setPendingDeepLink(uri);
            }
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (g_screen_events.load(std::memory_order_acquire)) {
            if (std::strcmp(member, "ActiveChanged") == 0) {
                dbus_bool_t active = FALSE;
                if (dbus_message_get_args(msg, nullptr, DBUS_TYPE_BOOLEAN,
                                          &active, DBUS_TYPE_INVALID)) {
                    dispatch(makeScreenState(active != FALSE));
                }
                return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
            }
            if (std::strcmp(member, "Lock") == 0) {
                dispatch(makeScreenState(true));
                return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
            }
            if (std::strcmp(member, "Unlock") == 0) {
                dispatch(makeScreenState(false));
                return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
            }
        }
        if (std::strcmp(member, "SettingChanged") == 0 ||
            std::strcmp(member, "notifyChange") == 0) {
            // 异步重查并缓存；值变化时由各自 reply 回调发事件通知 Dart。
            if (g_accent_events.load(std::memory_order_acquire)) requestAccent();
            if (g_theme_events.load(std::memory_order_acquire)) requestTheme();
        }
    }
    return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
}

// ── DeepLink / 协议唤醒（per-user desktop + GIO 注册 + D-Bus OpenUri）──
const char* kDeepLinkIface = "org.archoera.ArchooeraMusic";
const char* kDeepLinkPath = "/org/archoera/ArchooeraMusic";
const char* kDeepLinkName = "org.archoera.ArchooeraMusic";

std::mutex g_deeplink_mutex;
std::string g_deeplink_pending;   // UTF-8；单条待取
std::string g_deeplink_take_buf;  // take 返回缓冲

void setPendingDeepLink(const char* uri) {
    if (uri == nullptr || *uri == '\0') return;
    {
        std::lock_guard<std::mutex> lock(g_deeplink_mutex);
        g_deeplink_pending = uri;
    }
    dispatch(makeDeepLink());
}

int32_t deepLinkTakeLinux(AplString* out) {
    if (out == nullptr) return ERR_STATE;
    std::lock_guard<std::mutex> lock(g_deeplink_mutex);
    if (g_deeplink_pending.empty()) {
        out->data = nullptr;
        out->len = 0;
        return 0;
    }
    g_deeplink_take_buf = g_deeplink_pending;
    g_deeplink_pending.clear();
    out->data = g_deeplink_take_buf.c_str();
    out->len = g_deeplink_take_buf.size();
    return 1;
}

DBusHandlerResult deepLinkObjectHandler(DBusConnection* c, DBusMessage* msg,
                                        void*) {
    if (dbus_message_is_method_call(msg, kDeepLinkIface, "OpenUri")) {
        const char* uri = nullptr;
        if (dbus_message_get_args(msg, nullptr, DBUS_TYPE_STRING, &uri,
                                  DBUS_TYPE_INVALID)) {
            setPendingDeepLink(uri);
        }
        DBusMessage* reply = dbus_message_new_method_return(msg);
        dbus_connection_send(c, reply, nullptr);
        dbus_message_unref(reply);
        return DBUS_HANDLER_RESULT_HANDLED;
    }
    return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
}

dbus_bool_t registerDeepLinkObject(DBusConnection* c) {
    static const DBusObjectPathVTable vtable = {
        nullptr, deepLinkObjectHandler, nullptr,
        nullptr, nullptr, nullptr};
    return dbus_connection_register_object_path(c, kDeepLinkPath, &vtable,
                                               nullptr);
}

std::string readCmdlineUrl() {
    FILE* f = ::fopen("/proc/self/cmdline", "rb");
    if (f == nullptr) return std::string();
    std::string data;
    char buf[4096];
    size_t r;
    while ((r = std::fread(buf, 1, sizeof(buf), f)) > 0) data.append(buf, r);
    std::fclose(f);
    size_t start = 0;
    while (start < data.size()) {
        size_t end = data.find('\0', start);
        if (end == std::string::npos) end = data.size();
        const std::string arg = data.substr(start, end - start);
        if (arg.rfind("archoera://", 0) == 0) return arg;
        start = end + 1;
    }
    return std::string();
}

std::string desktopFilePath() {
    const char* home = std::getenv("HOME");
    if (home == nullptr || *home == '\0') return std::string();
    // 只用固定文件名（历史兼容）；当前 ABI 仅注册 archoera:// 一个方案。
    return std::string(home) + "/.local/share/applications/archoera_mvsync.desktop";
}

// scheme 会写进 .desktop 的 MimeType（外部输入）：只允许小写字母 / 数字 / 连字符。
bool isValidScheme(const char* scheme) {
    if (scheme == nullptr || *scheme == '\0') return false;
    for (const char* p = scheme; *p != '\0'; ++p) {
        const char c = *p;
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
              c == '-')) {
            return false;
        }
    }
    return true;
}

// ── 连接管理 ──────────────────────────────────────────────────────
void addMatch(DBusConnection* c, const char* rule) {
    DBusError err;
    dbus_error_init(&err);
    dbus_bus_add_match(c, rule, &err);
    dbus_error_free(&err);
}

bool tryRequestName(DBusConnection* c, const char* name) {
    DBusError err;
    dbus_error_init(&err);
    const int rc = dbus_bus_request_name(c, name, DBUS_NAME_FLAG_DO_NOT_QUEUE,
                                         &err);
    dbus_error_free(&err);
    return rc == DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER ||
           rc == DBUS_REQUEST_NAME_REPLY_ALREADY_OWNER;
}

void pickScreensaver(DBusConnection* c) {
    const struct {
        const char* name;
        const char* path;
    } candidates[] = {{kSsFreedesktop, kSsFreedesktopPath},
                      {kSsGnome, kSsGnomePath}};
    for (const auto& cand : candidates) {
        DBusError err;
        dbus_error_init(&err);
        DBusMessage* msg = dbus_message_new_method_call(
            "org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "GetNameOwner");
        dbus_message_append_args(msg, DBUS_TYPE_STRING, &cand.name,
                                 DBUS_TYPE_INVALID);
        DBusMessage* reply =
            dbus_connection_send_with_reply_and_block(c, msg, 3000, &err);
        dbus_message_unref(msg);
        if (reply != nullptr) {
            std::lock_guard<std::mutex> lock(g_mtx);
            g_ss_service = cand.name;
            g_ss_path = cand.path;
            dbus_message_unref(reply);
            dbus_error_free(&err);
            return;
        }
        dbus_error_free(&err);
    }
}

DBusConnection* ensureConn() {
    {
        std::lock_guard<std::mutex> lock(g_mtx);
        if (g_conn != nullptr) return g_conn;
    }
    DBusError err;
    dbus_error_init(&err);
    DBusConnection* c = dbus_bus_get(DBUS_BUS_SESSION, &err);
    if (c == nullptr) {
        dbus_error_free(&err);
        return nullptr;
    }
    dbus_error_free(&err);
    dbus_connection_set_exit_on_disconnect(c, FALSE);

    if (!tryRequestName(c, "org.mpris.MediaPlayer2.archoera")) {
        char fallback[96];
        std::snprintf(fallback, sizeof(fallback),
                      "org.mpris.MediaPlayer2.archoera.instance%d",
                      static_cast<int>(::getpid()));
        if (tryRequestName(c, fallback)) {
            std::lock_guard<std::mutex> lock(g_mtx);
            g_service = fallback;
        }
    }

    // 协议唤醒：第二个 well-known name（与 MPRIS 名不同，互不冲突），
    // 导出 OpenUri 供次实例转发（见 backend 接口 deepLinkForward）。
    const bool dl_name_ok = tryRequestName(c, kDeepLinkName);
    const dbus_bool_t dl_obj_ok = registerDeepLinkObject(c);
    log(1, "platform", dl_name_ok ? "deeplink name ok"
                                  : "deeplink name busy (another instance)");
    log(1, "platform", dl_obj_ok ? "deeplink object ok"
                                 : "deeplink object register FAILED");

    pickScreensaver(c);
    addMatch(c,
             "type='signal',interface='org.freedesktop.ScreenSaver',member='ActiveChanged'");
    addMatch(c,
             "type='signal',interface='org.gnome.ScreenSaver',member='ActiveChanged'");
    addMatch(c,
             "type='signal',interface='org.freedesktop.login1.Session',member='Lock'");
    addMatch(c,
             "type='signal',interface='org.freedesktop.login1.Session',member='Unlock'");
    addMatch(c,
             "type='signal',interface='org.freedesktop.portal.Settings',member='SettingChanged'");
    addMatch(c,
             "type='signal',interface='org.kde.KGlobalSettings',member='notifyChange'");
    addMatch(c,
             "type='signal',interface='org.archoera.DeepLink',member='Uri'");
    dbus_connection_add_filter(c, filter, nullptr, nullptr);

    {
        std::lock_guard<std::mutex> lock(g_mtx);
        g_conn = c;
    }
    return c;
}

void applyInhibit(DBusConnection* c);

void pumpLoop() {
    while (g_running.load(std::memory_order_acquire)) {
        // 会话结束（SIGTERM，见 onSessionEndSignal）：信号处理器只置位，这里
        // 负责派发到 Dart。只发一次，避免重复触发退出流程。
        if (g_session_end_requested.exchange(false, std::memory_order_acq_rel)) {
            log(1, "platform", "收到 SIGTERM（系统关机/注销）→ 会话结束");
            dispatch(makeSessionEnd());
        }
        DBusConnection* c = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            c = g_conn;
        }
        if (c == nullptr) {
            if (g_screen_events.load(std::memory_order_acquire) ||
                g_accent_events.load(std::memory_order_acquire) ||
                g_theme_events.load(std::memory_order_acquire) ||
                g_deeplink_events.load(std::memory_order_acquire) ||
                g_inhibit_pending.load(std::memory_order_acquire) ||
                g_inhibit_on) {
                c = ensureConn();
            }
        }
        if (c != nullptr) {
            // 防休眠注册/释放：阻塞式 DBus 调用只在泵线程执行。
            if (g_inhibit_pending.exchange(false, std::memory_order_acq_rel)) {
                applyInhibit(c);
            }
            // 订阅强调色/深浅色：连接就绪后立即异步推送一次当前值。
            if (g_accent_pending.exchange(false, std::memory_order_acq_rel)) {
                requestAccent();
            }
            if (g_theme_pending.exchange(false, std::memory_order_acq_rel)) {
                requestTheme();
            }
            dbus_connection_read_write_dispatch(c, 50);
        } else {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
        }
    }
}

// ── 防休眠抑制 ────────────────────────────────────────────────────
DBusMessage* inhibitCall(DBusConnection* c, const char* method,
                         const char* a, const char* b, uint32_t cookie) {
    std::string svc, path;
    {
        std::lock_guard<std::mutex> lock(g_mtx);
        svc = g_ss_service;
        path = g_ss_path;
    }
    DBusMessage* msg = dbus_message_new_method_call(svc.c_str(), path.c_str(),
                                                    svc.c_str(), method);
    if (std::strcmp(method, "Inhibit") == 0) {
        dbus_message_append_args(msg, DBUS_TYPE_STRING, &a, DBUS_TYPE_STRING, &b,
                                 DBUS_TYPE_INVALID);
    } else {
        dbus_message_append_args(msg, DBUS_TYPE_UINT32, &cookie,
                                 DBUS_TYPE_INVALID);
    }
    DBusError err;
    dbus_error_init(&err);
    DBusMessage* reply =
        dbus_connection_send_with_reply_and_block(c, msg, 5000, &err);
    dbus_message_unref(msg);
    dbus_error_free(&err);
    return reply;
}

// 在泵线程按 g_inhibit_want 执行 Inhibit/UnInhibit（阻塞式 DBus 调用）。
void applyInhibit(DBusConnection* c) {
    const bool want = g_inhibit_want.load(std::memory_order_acquire);
    bool on;
    {
        std::lock_guard<std::mutex> lock(g_mtx);
        on = g_inhibit_on;
    }
    if (want == on) return;
    if (want) {
        DBusMessage* reply =
            inhibitCall(c, "Inhibit", "ArchoeraMvsync", "playback", 0);
        if (reply == nullptr) return;
        uint32_t cookie = 0;
        const bool ok = dbus_message_get_args(reply, nullptr, DBUS_TYPE_UINT32,
                                              &cookie, DBUS_TYPE_INVALID);
        dbus_message_unref(reply);
        if (!ok || cookie == 0) return;
        std::lock_guard<std::mutex> lock(g_mtx);
        g_cookie = cookie;
        g_inhibit_on = true;
    } else {
        uint32_t cookie;
        {
            std::lock_guard<std::mutex> lock(g_mtx);
            cookie = g_cookie;
        }
        if (cookie != 0) {
            DBusMessage* reply =
                inhibitCall(c, "UnInhibit", nullptr, nullptr, cookie);
            if (reply != nullptr) dbus_message_unref(reply);
        }
        std::lock_guard<std::mutex> lock(g_mtx);
        g_cookie = 0;
        g_inhibit_on = false;
    }
}

// ── 单实例（文件锁）───────────────────────────────────────────────
int g_instance_fd = -1;

// ── 窗口状态（dlopen GTK/GDK 轻轮询）──────────────────────────────
namespace gtkwin {

constexpr int kGdkStateIconified = 1 << 1;

void* g_gtk = nullptr;
void* g_gdk = nullptr;
void* g_glib = nullptr;
void* g_gio = nullptr;
void* g_gobject = nullptr;
bool g_loaded = false;

// 用 g_idle_add 延后到主循环空闲执行：g_main_context_invoke 会同步调用，
// 在 Dart FFI / Flutter 派发过程中重入 GTK，实测导致 GTK 内部类型检查死循环、
// 主线程 100% CPU 卡死。延后到空闲即避开重入。
unsigned (*g_idle_add)(int (*)(void*), void*) = nullptr;
unsigned (*g_timeout_add)(unsigned, int (*)(void*), void*) = nullptr;
int (*g_source_remove)(unsigned) = nullptr;
// GTK 信号（事件驱动；替代 500ms 轮询）。用 dlopen 的 gobject 符号连接，
// 不引入编译期 GTK 头依赖（与现有 dlopen 策略一致）。
unsigned long (*g_signal_connect_data)(void* instance, const char* signal,
                                       void* handler, void* data,
                                       void* destroy, int flags) = nullptr;
void (*g_signal_handler_disconnect)(void* instance, unsigned long id) = nullptr;
// 取窗口：优先应用自身窗口列表（gtk_application_get_windows，官方 API，列表归
// application 所有、无需释放）；避免 gtk_window_list_toplevels() 遍历全局顶层
// 窗口列表（实测会陷入无上限遍历，导致主线程 100% CPU + 内存暴涨卡死）。
void* (*g_application_get_default)() = nullptr;
void* (*gtk_application_get_windows)(void*) = nullptr;
int (*gtk_widget_get_visible)(void*) = nullptr;
int (*gtk_window_is_active)(void*) = nullptr;
int (*gtk_window_is_maximized)(void*) = nullptr;
int (*gtk_window_is_fullscreen)(void*) = nullptr;
void* (*gtk_widget_get_window)(void*) = nullptr;
int (*gdk_window_get_state)(void*) = nullptr;
void (*gtk_window_present)(void*) = nullptr;
unsigned (*gdk_event_get_time)(void*) = nullptr;

// ── 窗口控制（ABI v10；GTK 只能主循环线程调用）────────────────────
// 全部经 g_idle_add 调度（理由同 activateOnMain）；位置缓存为原子，供任意线程读。
constexpr int kGdkHintMinSize = 1 << 1;  // GDK_HINT_MIN_SIZE
void (*gtk_window_resize)(void*, int, int) = nullptr;
void (*gtk_window_move)(void*, int, int) = nullptr;
void (*gtk_window_get_position)(void*, int*, int*) = nullptr;
void (*gtk_window_get_size)(void*, int*, int*) = nullptr;
void (*gtk_window_iconify)(void*) = nullptr;
void (*gtk_window_deiconify)(void*) = nullptr;
void (*gtk_window_maximize)(void*) = nullptr;
void (*gtk_window_unmaximize)(void*) = nullptr;
void (*gtk_window_fullscreen)(void*) = nullptr;
void (*gtk_window_unfullscreen)(void*) = nullptr;
void (*gtk_window_set_keep_above)(void*, int) = nullptr;
void (*gtk_window_set_resizable)(void*, int) = nullptr;
void (*gtk_window_set_decorated)(void*, int) = nullptr;
void (*gtk_window_show)(void*) = nullptr;
void (*gtk_window_hide)(void*) = nullptr;
void (*gtk_window_set_geometry_hints)(void*, void*, void*, int) = nullptr;
void (*gtk_window_begin_move_drag)(void*, int, int, int, unsigned) = nullptr;
void (*gtk_window_begin_resize_drag)(void*, int, int, int, int, unsigned) = nullptr;
unsigned (*gtk_get_current_event_time)() = nullptr;
void* (*gdk_display_get_default)() = nullptr;
void* (*gdk_display_get_default_seat)(void*) = nullptr;
void* (*gdk_seat_get_pointer)(void*) = nullptr;
void (*gdk_device_get_position)(void*, void**, int*, int*) = nullptr;
void* (*gdk_screen_get_default)() = nullptr;
int (*gdk_screen_get_monitor_at_window)(void*, void*) = nullptr;
void (*gdk_screen_get_monitor_workarea)(void*, int, void*) = nullptr;

// GdkGeometry（GTK3）：只需 min_width/min_height 位于偏移 0/4；仍按完整布局声明，
// 避免 GTK 整结构拷贝时越界读。
struct GdkGeometryLocal {
    int32_t min_width;
    int32_t min_height;
    int32_t max_width;
    int32_t max_height;
    int32_t base_width;
    int32_t base_height;
    int32_t width_inc;
    int32_t height_inc;
    double min_aspect;
    double max_aspect;
    int32_t win_gravity;
};

// GdkRectangle：x / y / width / height。
struct GdkRectangleLocal {
    int32_t x;
    int32_t y;
    int32_t width;
    int32_t height;
};

void* g_win = nullptr;
// g_win 只在主循环写入；本标志供任意线程判断「是否已发现窗口」（避免跨线程
// 直接读裸指针构成数据竞争）。
std::atomic<bool> g_have_window{false};
unsigned g_timer = 0;      // 信号不可用时的轮询兜底
unsigned g_retry = 0;      // 启动早期窗口尚未创建时的重试 source
unsigned long g_handlers[10] = {};
int g_handler_count = 0;
std::atomic<bool> g_enabled{false};
std::atomic<bool> g_minimized{false};
std::atomic<bool> g_focused{true};
std::atomic<bool> g_maximized{false};
std::atomic<bool> g_fullscreen{false};
std::atomic<bool> g_hidden{false};
// 窗口位置（根坐标）：poll 里从 GTK 刷新；set_position 先乐观写缓存，
// 供 Dart 任意线程 apl_window_get_position 立即往返。
std::atomic<int32_t> g_pos_x{0};
std::atomic<int32_t> g_pos_y{0};
// 窗口尺寸（内容区，GTK 口径）：poll / configure-event 刷新，供任意线程
// apl_window_get_size 读取（GTK 只能在主循环线程调用）。
std::atomic<int32_t> g_size_w{0};
std::atomic<int32_t> g_size_h{0};
// 最近一次鼠标按下的 GTK 事件时间（Wayland 的 begin_move_drag 需要有效 serial；
// 拖拽在 idle 回调里启动，`gtk_get_current_event_time()` 此时通常已为 0）。
std::atomic<unsigned> g_last_event_time{0};

template <typename T>
T sym(void* handle, const char* name) {
    return reinterpret_cast<T>(dlsym(handle, name));
}

bool load() {
    if (g_loaded) return true;
    g_gtk = dlopen("libgtk-3.so.0", RTLD_NOW);
    if (g_gtk == nullptr) g_gtk = dlopen(nullptr, RTLD_NOW);
    g_gdk = dlopen("libgdk-3.so.0", RTLD_NOW);
    if (g_gdk == nullptr) g_gdk = g_gtk;
    g_glib = dlopen("libglib-2.0.so.0", RTLD_NOW);
    if (g_glib == nullptr) g_glib = g_gtk;
    g_gio = dlopen("libgio-2.0.so.0", RTLD_NOW);
    if (g_gio == nullptr) g_gio = dlopen(nullptr, RTLD_NOW);
    if (g_gio == nullptr) g_gio = g_gtk;
    g_gobject = dlopen("libgobject-2.0.so.0", RTLD_NOW);
    if (g_gobject == nullptr) g_gobject = g_gtk;
    if (g_gtk == nullptr || g_glib == nullptr) return false;
    g_idle_add = sym<decltype(g_idle_add)>(g_glib, "g_idle_add");
    g_timeout_add = sym<decltype(g_timeout_add)>(g_glib, "g_timeout_add");
    g_source_remove = sym<decltype(g_source_remove)>(g_glib, "g_source_remove");
    g_signal_connect_data = sym<decltype(g_signal_connect_data)>(
        g_gobject, "g_signal_connect_data");
    g_signal_handler_disconnect = sym<decltype(g_signal_handler_disconnect)>(
        g_gobject, "g_signal_handler_disconnect");
    g_application_get_default = sym<decltype(g_application_get_default)>(
        g_gio, "g_application_get_default");
    gtk_application_get_windows = sym<decltype(gtk_application_get_windows)>(
        g_gtk, "gtk_application_get_windows");
    gtk_widget_get_visible = sym<decltype(gtk_widget_get_visible)>(
        g_gtk, "gtk_widget_get_visible");
    gtk_window_is_active = sym<decltype(gtk_window_is_active)>(
        g_gtk, "gtk_window_is_active");
    gtk_window_is_maximized = sym<decltype(gtk_window_is_maximized)>(
        g_gtk, "gtk_window_is_maximized");
    gtk_window_is_fullscreen = sym<decltype(gtk_window_is_fullscreen)>(
        g_gtk, "gtk_window_is_fullscreen");
    gtk_widget_get_window = sym<decltype(gtk_widget_get_window)>(
        g_gtk, "gtk_widget_get_window");
    gdk_window_get_state = sym<decltype(gdk_window_get_state)>(
        g_gdk, "gdk_window_get_state");
    gtk_window_present =
        sym<decltype(gtk_window_present)>(g_gtk, "gtk_window_present");
    gdk_event_get_time =
        sym<decltype(gdk_event_get_time)>(g_gdk, "gdk_event_get_time");
    // 窗口控制（部分符号缺失时，对应 API 返回 ERR_UNSUPPORTED；load 仍成功）。
    gtk_window_resize =
        sym<decltype(gtk_window_resize)>(g_gtk, "gtk_window_resize");
    gtk_window_move = sym<decltype(gtk_window_move)>(g_gtk, "gtk_window_move");
    gtk_window_get_position = sym<decltype(gtk_window_get_position)>(
        g_gtk, "gtk_window_get_position");
    gtk_window_get_size =
        sym<decltype(gtk_window_get_size)>(g_gtk, "gtk_window_get_size");
    gtk_window_iconify =
        sym<decltype(gtk_window_iconify)>(g_gtk, "gtk_window_iconify");
    gtk_window_deiconify =
        sym<decltype(gtk_window_deiconify)>(g_gtk, "gtk_window_deiconify");
    gtk_window_maximize =
        sym<decltype(gtk_window_maximize)>(g_gtk, "gtk_window_maximize");
    gtk_window_unmaximize =
        sym<decltype(gtk_window_unmaximize)>(g_gtk, "gtk_window_unmaximize");
    gtk_window_fullscreen =
        sym<decltype(gtk_window_fullscreen)>(g_gtk, "gtk_window_fullscreen");
    gtk_window_unfullscreen = sym<decltype(gtk_window_unfullscreen)>(
        g_gtk, "gtk_window_unfullscreen");
    gtk_window_set_keep_above = sym<decltype(gtk_window_set_keep_above)>(
        g_gtk, "gtk_window_set_keep_above");
    gtk_window_set_resizable = sym<decltype(gtk_window_set_resizable)>(
        g_gtk, "gtk_window_set_resizable");
    gtk_window_set_decorated = sym<decltype(gtk_window_set_decorated)>(
        g_gtk, "gtk_window_set_decorated");
    gtk_window_show = sym<decltype(gtk_window_show)>(g_gtk, "gtk_window_show");
    gtk_window_hide = sym<decltype(gtk_window_hide)>(g_gtk, "gtk_window_hide");
    gtk_window_set_geometry_hints = sym<decltype(gtk_window_set_geometry_hints)>(
        g_gtk, "gtk_window_set_geometry_hints");
    gtk_window_begin_move_drag = sym<decltype(gtk_window_begin_move_drag)>(
        g_gtk, "gtk_window_begin_move_drag");
    gtk_window_begin_resize_drag = sym<decltype(gtk_window_begin_resize_drag)>(
        g_gtk, "gtk_window_begin_resize_drag");
    gtk_get_current_event_time = sym<decltype(gtk_get_current_event_time)>(
        g_gtk, "gtk_get_current_event_time");
    gdk_display_get_default = sym<decltype(gdk_display_get_default)>(
        g_gdk, "gdk_display_get_default");
    gdk_display_get_default_seat = sym<decltype(gdk_display_get_default_seat)>(
        g_gdk, "gdk_display_get_default_seat");
    gdk_seat_get_pointer =
        sym<decltype(gdk_seat_get_pointer)>(g_gdk, "gdk_seat_get_pointer");
    gdk_device_get_position = sym<decltype(gdk_device_get_position)>(
        g_gdk, "gdk_device_get_position");
    gdk_screen_get_default = sym<decltype(gdk_screen_get_default)>(
        g_gdk, "gdk_screen_get_default");
    gdk_screen_get_monitor_at_window =
        sym<decltype(gdk_screen_get_monitor_at_window)>(
            g_gdk, "gdk_screen_get_monitor_at_window");
    gdk_screen_get_monitor_workarea =
        sym<decltype(gdk_screen_get_monitor_workarea)>(
            g_gdk, "gdk_screen_get_monitor_workarea");
    g_loaded = g_idle_add != nullptr && g_timeout_add != nullptr &&
               g_source_remove != nullptr &&
               g_application_get_default != nullptr &&
               gtk_application_get_windows != nullptr &&
               gtk_widget_get_visible != nullptr &&
               gtk_window_is_active != nullptr &&
               gtk_widget_get_window != nullptr &&
               gdk_window_get_state != nullptr;
    return g_loaded;
}

bool available() { return load(); }

// 置前主窗口：GTK 只在主循环线程安全，故经 g_idle_add 调度。
int activateOnMain(void*) {
    struct GList {
        void* data;
        GList* next;
        GList* prev;
    };
    void* win = g_win;
    if (win == nullptr && g_application_get_default != nullptr &&
        gtk_application_get_windows != nullptr) {
        void* app = g_application_get_default();
        if (app != nullptr) {
            auto* it = static_cast<GList*>(gtk_application_get_windows(app));
            while (it != nullptr) {
                if (it->data != nullptr) {
                    win = it->data;
                    break;
                }
                it = it->next;
            }
        }
    }
    if (win != nullptr && gtk_window_present != nullptr) {
        gtk_window_present(win);
    }
    return 0;  // G_SOURCE_REMOVE
}

void activate() {
    if (!load() || gtk_window_present == nullptr ||
        g_idle_add == nullptr) {
        return;
    }
    g_idle_add(activateOnMain, nullptr);
}

void emit() {
    if (!g_enabled.load(std::memory_order_acquire)) return;
    dispatch(makeWindowState(
        g_minimized.load(std::memory_order_acquire),
        g_focused.load(std::memory_order_acquire),
        g_maximized.load(std::memory_order_acquire),
        g_fullscreen.load(std::memory_order_acquire),
        g_hidden.load(std::memory_order_acquire)));
}

// 从 GTK/GDK 读取完整窗口状态；信号回调与轮询兜底共用。
int poll(void*) {
    if (g_win == nullptr) return 1;  // G_SOURCE_CONTINUE
    const bool active = gtk_window_is_active(g_win) != 0;
    bool minimized = false;
    void* gdkwin = gtk_widget_get_window(g_win);
    if (gdkwin != nullptr) {
        minimized = (gdk_window_get_state(gdkwin) & kGdkStateIconified) != 0;
    }
    // 位置缓存：根坐标；configure-event / 轮询都会刷新，供任意线程读取。
    if (gtk_window_get_position != nullptr) {
        int px = 0;
        int py = 0;
        gtk_window_get_position(g_win, &px, &py);
        g_pos_x.store(px, std::memory_order_release);
        g_pos_y.store(py, std::memory_order_release);
    }
    if (gtk_window_get_size != nullptr) {
        int ww = 0;
        int wh = 0;
        gtk_window_get_size(g_win, &ww, &wh);
        if (ww > 0 && wh > 0) {
            g_size_w.store(ww, std::memory_order_release);
            g_size_h.store(wh, std::memory_order_release);
        }
    }
    const bool hidden = gtk_widget_get_visible(g_win) == 0;
    const bool maximized =
        gtk_window_is_maximized != nullptr && gtk_window_is_maximized(g_win) != 0;
    const bool fullscreen = gtk_window_is_fullscreen != nullptr &&
                            gtk_window_is_fullscreen(g_win) != 0;
    if (active != g_focused.load(std::memory_order_acquire) ||
        minimized != g_minimized.load(std::memory_order_acquire) ||
        hidden != g_hidden.load(std::memory_order_acquire) ||
        maximized != g_maximized.load(std::memory_order_acquire) ||
        fullscreen != g_fullscreen.load(std::memory_order_acquire)) {
        g_focused.store(active, std::memory_order_release);
        g_minimized.store(minimized, std::memory_order_release);
        g_hidden.store(hidden, std::memory_order_release);
        g_maximized.store(maximized, std::memory_order_release);
        g_fullscreen.store(fullscreen, std::memory_order_release);
        emit();
    }
    return 1;  // G_SOURCE_CONTINUE
}

// GTK 事件信号：只作「状态可能变化」的触发，真实状态仍由 poll 查询。
// 返回值 FALSE 让 GTK 继续默认处理（不吞掉聚焦/状态事件）。
int onStateSignal(void*, void*, void*) {
    poll(nullptr);
    return 0;
}

int onVisibilitySignal(void*, void*) {
    poll(nullptr);
    return 0;
}

// delete-event（关闭按钮 / Alt+F4 / WM_DELETE_WINDOW）：只上报关闭请求，
// 返回 TRUE 阻止 GTK 默认 destroy；隐藏还是退出由 Dart 决策。
int onDeleteEvent(void*, void*, void*) {
    if (!g_enabled.load(std::memory_order_acquire)) return 0;  // FALSE：已停订阅
    dispatch(makeWindowCloseRequest(WINDOW_CLOSE));
    return 1;  // TRUE：不销毁窗口
}

// 记录鼠标按下时间：拖拽/缩放请求在 idle 回调里执行，届时 current event 已失效，
// Wayland 合成器要求 timestamp 来自触发拖拽的那次按下。返回 FALSE 不吞事件。
int onButtonPress(void*, void* event, void*) {
    if (event != nullptr && gdk_event_get_time != nullptr) {
        g_last_event_time.store(gdk_event_get_time(event),
                                std::memory_order_release);
    }
    return 0;
}

void connectSignals() {
    if (g_win == nullptr || g_signal_connect_data == nullptr ||
        g_signal_handler_disconnect == nullptr || g_handler_count > 0) {
        return;
    }
    auto add = [](const char* name, void* handler) {
        if (g_handler_count >= 10) return;
        g_handlers[g_handler_count++] = g_signal_connect_data(
            g_win, name, handler, nullptr, nullptr, 0);
    };
    // 事件驱动的焦点 / 状态变化（最大化 / 全屏 / 图标化都走 window-state-event）。
    add("focus-in-event", reinterpret_cast<void*>(&onStateSignal));
    add("focus-out-event", reinterpret_cast<void*>(&onStateSignal));
    add("window-state-event", reinterpret_cast<void*>(&onStateSignal));
    add("configure-event", reinterpret_cast<void*>(&onStateSignal));
    // 显示 / 隐藏（托盘隐藏、最小化到托盘）：GTK show/hide 信号无事件参数。
    add("show", reinterpret_cast<void*>(&onVisibilitySignal));
    add("hide", reinterpret_cast<void*>(&onVisibilitySignal));
    // 鼠标按下时间（拖拽/缩放的 Wayland serial；不吞事件）。
    add("button-press-event", reinterpret_cast<void*>(&onButtonPress));
    // 关闭请求：拦截 delete-event（返回 TRUE），由 Dart 决定隐藏 / 退出。
    add("delete-event", reinterpret_cast<void*>(&onDeleteEvent));
}

void disconnectSignals() {
    if (g_win != nullptr && g_signal_handler_disconnect != nullptr) {
        for (int i = 0; i < g_handler_count; i++) {
            g_signal_handler_disconnect(g_win, g_handlers[i]);
        }
    }
    g_handler_count = 0;
}

int setupOnMainThread(void*) {
    // setEvents(false) 与已排队的 idle 竞争：停订阅后不得再挂信号 /
    // 重新开启 delete-event 拦截，否则窗口将无法关闭。
    if (!g_enabled.load(std::memory_order_acquire)) return 0;  // G_SOURCE_REMOVE
    struct GList {
        void* data;
        GList* next;
        GList* prev;
    };
    // 本次调用消耗掉调度它的 source（idle 或上一次 retry timeout）；失败时
    // 重新挂一个新的 timeout，返回 0 只移除当前 source，不影响新挂的。
    g_retry = 0;
    if (g_win == nullptr) {
        void* app = g_application_get_default != nullptr
                        ? g_application_get_default()
                        : nullptr;
        if (app != nullptr) {
            // 应用自身窗口列表（按最近聚焦排序，首元素为当前聚焦窗口）；列表归
            // application 所有，只读、不可修改/释放。
            auto* it = static_cast<GList*>(gtk_application_get_windows(app));
            while (it != nullptr) {
                void* win = it->data;
                if (win != nullptr && gtk_widget_get_visible(win) != 0) {
                    g_win = win;
                    g_have_window.store(true, std::memory_order_release);
                    break;
                }
                it = it->next;
            }
        }
    }
    if (g_win == nullptr) {
        // 启动早期窗口还没创建：每 500ms 重试，而不是永久静默（旧实现直接
        // G_SOURCE_REMOVE，桥接之后再也没事件）。该 source 由 setEvents(false) 撤销。
        if (g_retry == 0) g_retry = g_timeout_add(500, setupOnMainThread, nullptr);
        return 0;  // 本次 timeout 结束；下一次在 g_retry 里继续
    }
    g_retry = 0;
    connectSignals();
    poll(nullptr);
    // 信号可用时不装轮询；仅当 gobject 缺失（极少见的裁剪环境）保留兜底。
    if (g_handler_count == 0 && g_timer == 0) {
        g_timer = g_timeout_add(500, poll, nullptr);
    }
    return 0;  // G_SOURCE_REMOVE（若作为重试 timeout，找到窗口后自然停止）
}

int32_t setEvents(bool on) {
    if (!load()) return ERR_UNSUPPORTED;
    if (on) {
        g_enabled.store(true, std::memory_order_release);
        // 延后到主循环空闲执行（非同步重入 GTK）；见 g_idle_add 声明处说明。
        g_idle_add(setupOnMainThread, nullptr);
        return OK;
    }
    g_enabled.store(false, std::memory_order_release);
    if (g_timer != 0) {
        g_source_remove(g_timer);
        g_timer = 0;
    }
    if (g_retry != 0) {
        g_source_remove(g_retry);
        g_retry = 0;
    }
    disconnectSignals();
    return OK;
}

// ── 窗口控制（ABI v10）────────────────────────────────────────────
// 线程模型：入口函数只做符号检查 + 把操作排到 GTK 主循环（g_idle_add），
// 绝不从 Dart 线程直接调用 GTK；位置缓存为原子，读接口无需主循环。
enum TaskOp {
    kTaskResize,
    kTaskMinSize,
    kTaskCenter,
    kTaskMove,
    kTaskIconify,
    kTaskDeiconify,
    kTaskMaximize,
    kTaskUnmaximize,
    kTaskFullscreen,
    kTaskUnfullscreen,
    kTaskKeepAbove,
    kTaskResizable,
    kTaskDecorated,
    kTaskShow,
    kTaskHide,
    kTaskDrag,
    kTaskResizeDrag,
};

struct WindowTask {
    int32_t op;
    int32_t a;
    int32_t b;
};

// GList 布局（glib）：data / next / prev。
struct GListLocal {
    void* data;
    GListLocal* next;
    GListLocal* prev;
};

// 主循环上解析目标窗口：g_win 可能尚未发现（控制调用早于窗口创建）。
void* resolveWindow() {
    if (g_win != nullptr) return g_win;
    if (g_application_get_default == nullptr ||
        gtk_application_get_windows == nullptr) {
        return nullptr;
    }
    void* app = g_application_get_default();
    if (app == nullptr) return nullptr;
    auto* it = static_cast<GListLocal*>(gtk_application_get_windows(app));
    while (it != nullptr) {
        if (it->data != nullptr) {
            g_win = it->data;
            g_have_window.store(true, std::memory_order_release);
            break;
        }
        it = it->next;
    }
    return g_win;
}

// 指针根坐标（拖拽/缩放热区用）。GTK 3.20+ 的 seat/pointer API；取不到时回退
// 到窗口缓存中心（无鼠标事件时 begin_*_drag 本来也不会生效）。
bool pointerRootPos(int* x, int* y) {
    if (gdk_display_get_default == nullptr ||
        gdk_display_get_default_seat == nullptr ||
        gdk_seat_get_pointer == nullptr || gdk_device_get_position == nullptr) {
        return false;
    }
    void* display = gdk_display_get_default();
    if (display == nullptr) return false;
    void* seat = gdk_display_get_default_seat(display);
    if (seat == nullptr) return false;
    void* pointer = gdk_seat_get_pointer(seat);
    if (pointer == nullptr) return false;
    int px = 0;
    int py = 0;
    gdk_device_get_position(pointer, nullptr, &px, &py);
    *x = px;
    *y = py;
    return true;
}

int applyTask(void* p) {
    WindowTask* t = static_cast<WindowTask*>(p);
    void* win = resolveWindow();
    if (win != nullptr) {
        switch (t->op) {
            case kTaskResize:
                if (gtk_window_resize != nullptr) {
                    gtk_window_resize(win, t->a, t->b);
                }
                break;
            case kTaskMinSize:
                if (gtk_window_set_geometry_hints != nullptr) {
                    GdkGeometryLocal geo{};
                    geo.min_width = t->a;
                    geo.min_height = t->b;
                    gtk_window_set_geometry_hints(win, nullptr, &geo,
                                                  kGdkHintMinSize);
                }
                break;
            case kTaskCenter: {
                int ww = 0;
                int wh = 0;
                if (gtk_window_get_size != nullptr) {
                    gtk_window_get_size(win, &ww, &wh);
                }
                GdkRectangleLocal wa{};
                if (gdk_screen_get_default != nullptr &&
                    gdk_screen_get_monitor_workarea != nullptr) {
                    void* screen = gdk_screen_get_default();
                    int mon = 0;
                    if (screen != nullptr &&
                        gdk_screen_get_monitor_at_window != nullptr &&
                        gtk_widget_get_window != nullptr) {
                        void* gdkwin = gtk_widget_get_window(win);
                        if (gdkwin != nullptr) {
                            mon = gdk_screen_get_monitor_at_window(screen,
                                                                   gdkwin);
                        }
                    }
                    if (screen != nullptr) {
                        gdk_screen_get_monitor_workarea(screen, mon, &wa);
                    }
                }
                const bool has_area = wa.width > 0 && wa.height > 0;
                const int nx = has_area ? wa.x + (wa.width - ww) / 2 : 0;
                const int ny = has_area ? wa.y + (wa.height - wh) / 2 : 0;
                if (gtk_window_move != nullptr) gtk_window_move(win, nx, ny);
                g_pos_x.store(nx, std::memory_order_release);
                g_pos_y.store(ny, std::memory_order_release);
                break;
            }
            case kTaskMove:
                if (gtk_window_move != nullptr) {
                    gtk_window_move(win, t->a, t->b);
                }
                break;
            case kTaskIconify:
                if (gtk_window_iconify != nullptr) gtk_window_iconify(win);
                break;
            case kTaskDeiconify:
                if (gtk_window_deiconify != nullptr) gtk_window_deiconify(win);
                if (gtk_window_present != nullptr) gtk_window_present(win);
                break;
            case kTaskMaximize:
                if (gtk_window_maximize != nullptr) gtk_window_maximize(win);
                break;
            case kTaskUnmaximize:
                if (gtk_window_unmaximize != nullptr) {
                    gtk_window_unmaximize(win);
                }
                break;
            case kTaskFullscreen:
                if (gtk_window_fullscreen != nullptr) {
                    gtk_window_fullscreen(win);
                }
                break;
            case kTaskUnfullscreen:
                if (gtk_window_unfullscreen != nullptr) {
                    gtk_window_unfullscreen(win);
                }
                break;
            case kTaskKeepAbove:
                if (gtk_window_set_keep_above != nullptr) {
                    gtk_window_set_keep_above(win, t->a);
                }
                break;
            case kTaskResizable:
                if (gtk_window_set_resizable != nullptr) {
                    gtk_window_set_resizable(win, t->a);
                }
                break;
            case kTaskDecorated:
                if (gtk_window_set_decorated != nullptr) {
                    gtk_window_set_decorated(win, t->a);
                }
                break;
            case kTaskShow:
                if (gtk_window_show != nullptr) gtk_window_show(win);
                if (gtk_window_present != nullptr) gtk_window_present(win);
                break;
            case kTaskHide:
                if (gtk_window_hide != nullptr) gtk_window_hide(win);
                break;
            case kTaskDrag: {
                int px = 0;
                int py = 0;
                if (!pointerRootPos(&px, &py)) {
                    int ww = 0;
                    int wh = 0;
                    if (gtk_window_get_size != nullptr) {
                        gtk_window_get_size(win, &ww, &wh);
                    }
                    px = g_pos_x.load(std::memory_order_acquire) + ww / 2;
                    py = g_pos_y.load(std::memory_order_acquire) + wh / 2;
                }
                const unsigned ts =
                    g_last_event_time.load(std::memory_order_acquire) != 0
                        ? g_last_event_time.load(std::memory_order_acquire)
                        : (gtk_get_current_event_time != nullptr
                               ? gtk_get_current_event_time()
                               : 0u);
                if (gtk_window_begin_move_drag != nullptr) {
                    gtk_window_begin_move_drag(win, 1, px, py, ts);
                }
                break;
            }
            case kTaskResizeDrag: {
                int px = 0;
                int py = 0;
                if (!pointerRootPos(&px, &py)) {
                    int ww = 0;
                    int wh = 0;
                    if (gtk_window_get_size != nullptr) {
                        gtk_window_get_size(win, &ww, &wh);
                    }
                    px = g_pos_x.load(std::memory_order_acquire) + ww / 2;
                    py = g_pos_y.load(std::memory_order_acquire) + wh / 2;
                }
                const unsigned ts =
                    g_last_event_time.load(std::memory_order_acquire) != 0
                        ? g_last_event_time.load(std::memory_order_acquire)
                        : (gtk_get_current_event_time != nullptr
                               ? gtk_get_current_event_time()
                               : 0u);
                // GdkWindowEdge 的枚举顺序与 APL_WINDOW_EDGE_* 完全一致。
                if (gtk_window_begin_resize_drag != nullptr) {
                    gtk_window_begin_resize_drag(win, t->a, 1, px, py, ts);
                }
                break;
            }
            default:
                break;
        }
    }
    delete t;
    return 0;  // G_SOURCE_REMOVE
}

int32_t schedule(int32_t op, int32_t a, int32_t b) {
    if (g_idle_add == nullptr) return ERR_UNSUPPORTED;
    WindowTask* t = new WindowTask{op, a, b};
    const unsigned id = g_idle_add(applyTask, t);
    if (id == 0) {
        delete t;
        return ERR_BACKEND;
    }
    return OK;
}

// 是否已发现主窗口（原子标志，任意线程可读；未发现时控制 API 报后端不可用，
// 避免 Dart 拿到 0,0 的假位置）。
bool hasWindow() { return g_have_window.load(std::memory_order_acquire); }

int32_t setSize(int32_t w, int32_t h) {
    if (!load() || gtk_window_resize == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskResize, w, h);
}

int32_t setMinSize(int32_t w, int32_t h) {
    if (!load() || gtk_window_set_geometry_hints == nullptr) {
        return ERR_UNSUPPORTED;
    }
    return schedule(kTaskMinSize, w, h);
}

int32_t center() {
    if (!load() || gtk_window_move == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskCenter, 0, 0);
}

int32_t show() {
    if (!load() || gtk_window_show == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskShow, 0, 0);
}

int32_t hide() {
    if (!load() || gtk_window_hide == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskHide, 0, 0);
}

int32_t minimize() {
    if (!load() || gtk_window_iconify == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskIconify, 0, 0);
}

int32_t restore() {
    if (!load() || gtk_window_deiconify == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskDeiconify, 0, 0);
}

int32_t setMaximized(int32_t on) {
    if (!load()) return ERR_UNSUPPORTED;
    if (on != 0) {
        if (gtk_window_maximize == nullptr) return ERR_UNSUPPORTED;
        return schedule(kTaskMaximize, 0, 0);
    }
    if (gtk_window_unmaximize == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskUnmaximize, 0, 0);
}

int32_t setFullscreen(int32_t on) {
    if (!load()) return ERR_UNSUPPORTED;
    if (on != 0) {
        if (gtk_window_fullscreen == nullptr) return ERR_UNSUPPORTED;
        return schedule(kTaskFullscreen, 0, 0);
    }
    if (gtk_window_unfullscreen == nullptr) return ERR_UNSUPPORTED;
    return schedule(kTaskUnfullscreen, 0, 0);
}

int32_t setAlwaysOnTop(int32_t on) {
    if (!load() || gtk_window_set_keep_above == nullptr) {
        return ERR_UNSUPPORTED;
    }
    return schedule(kTaskKeepAbove, on != 0 ? 1 : 0, 0);
}

int32_t setResizable(int32_t on) {
    if (!load() || gtk_window_set_resizable == nullptr) {
        return ERR_UNSUPPORTED;
    }
    return schedule(kTaskResizable, on != 0 ? 1 : 0, 0);
}

// GTK 只有「有/无装饰」两档：SYSTEM 保留系统标题栏；CAPTION 与 BORDERLESS
// 都只能去掉全部服务端装饰（无编译期 GTK 头可做 CSD 定制），窗口按钮由自绘 UI
// 接管。语义差异在 macOS/Windows 实现，Linux 侧如实降级。
int32_t setDecor(int32_t mode) {
    if (!load() || gtk_window_set_decorated == nullptr) {
        return ERR_UNSUPPORTED;
    }
    const int32_t decorated = mode == APL_WINDOW_DECOR_SYSTEM ? 1 : 0;
    return schedule(kTaskDecorated, decorated, 0);
}

int32_t setPosition(int32_t x, int32_t y) {
    if (!load() || gtk_window_move == nullptr) return ERR_UNSUPPORTED;
    // 乐观更新缓存：Dart 紧接着 get_position 时应立即看到新值；后续
    // configure-event / poll 再以 GTK 实际值校正。
    g_pos_x.store(x, std::memory_order_release);
    g_pos_y.store(y, std::memory_order_release);
    return schedule(kTaskMove, x, y);
}

int32_t startDrag() {
    if (!load() || gtk_window_begin_move_drag == nullptr) {
        return ERR_UNSUPPORTED;
    }
    return schedule(kTaskDrag, 0, 0);
}

int32_t startResize(int32_t edge) {
    if (!load() || gtk_window_begin_resize_drag == nullptr) {
        return ERR_UNSUPPORTED;
    }
    return schedule(kTaskResizeDrag, edge, 0);
}

}  // namespace gtkwin

// ── 系统托盘（StatusNotifierItem + com.canonical.dbusmenu）─────────
// 纯 libdbus，不依赖 GTK/Qt。对象 `/StatusNotifierItem` 提供图标/状态/工具提示与
// 点击方法，`/MenuBar` 提供 com.canonical.dbusmenu 菜单；向会话总线的
// org.kde.StatusNotifierWatcher 注册后由面板托管。
//
// 注册方式（标准路径形式）：不申请 org.kde.StatusNotifierItem-<pid>-<id> 这类
// well-known 名，而是把对象路径 `/StatusNotifierItem` 传给
// RegisterStatusNotifierItem——监听方据此改用调用者的唯一连接名（:1.x）定位本项。
// 因此沙箱无需 --own-name=org.kde.*（见 Flatpak 桌面集成文档 StatusNotifier 一节）。
//
// 连接独立性：托盘使用**私有** DBusConnection（dbus_bus_get_private）。MPRIS 的
// 连接过滤器对所有方法调用返回 HANDLED（libdbus 先于对象路径回调调用过滤器），
// 若共用连接会遮蔽托盘的对象路径回调，故必须另开连接（关闭时 close 便于唤醒泵）。
//
// 线程模型：Dart 的 tray* 可在任意线程调用，泵线程分发 DBus 方法。共享状态
// （菜单/图标/可见性/trigger）由 g_tray_mtx 保护；DBus 发送经
// dbus_threads_init_default 保证并发安全。绝不在持锁时 dispatch 回 Dart。
constexpr const char* kSniPath = "/StatusNotifierItem";
constexpr const char* kSniIface = "org.kde.StatusNotifierItem";
constexpr const char* kSniWatcherIface = "org.kde.StatusNotifierWatcher";
constexpr const char* kSniWatcherPath = "/StatusNotifierWatcher";
constexpr const char* kMenuPath = "/MenuBar";
constexpr const char* kMenuIface = "com.canonical.dbusmenu";

struct TrayItem {
    int32_t id = 0;      // 0=分隔符
    bool enabled = true;
    int32_t checked = -1;  // -1=非复选
    std::string label;     // UTF-8 拷贝
};

std::mutex g_tray_mtx;
DBusConnection* g_tray_conn = nullptr;
// 堆指针：未 shutdown 即退出时不触发静态 std::thread 析构终止（仅泄漏）。
std::thread* g_tray_pump = nullptr;
std::atomic<bool> g_tray_running{false};
bool g_tray_created = false;
std::string g_tray_tooltip = "ArchoeraMvsync";
bool g_tray_visible = true;
int32_t g_tray_trigger = 0;  // 0=左键展开菜单；1=右键
uint32_t g_tray_revision = 0;
std::vector<TrayItem> g_tray_menu;

bool g_tray_has_pixmap = false;
int32_t g_tray_icon_w = 0;
int32_t g_tray_icon_h = 0;
std::vector<uint8_t> g_tray_pixmap;  // ARGB32，网络字节序
std::string g_tray_icon_name;        // 回退 IconName

// 一次性快照：泵线程取快照后在锁外构造回复/分发，避免持锁回调 Dart。
struct TraySnapshot {
    bool visible = true;
    int32_t trigger = 0;
    uint32_t revision = 0;
    std::string tooltip;
    std::vector<TrayItem> menu;
    bool hasPixmap = false;
    int32_t iconW = 0;
    int32_t iconH = 0;
    std::vector<uint8_t> pixmap;
    std::string iconName;
};

TraySnapshot traySnapshot() {
    std::lock_guard<std::mutex> lock(g_tray_mtx);
    TraySnapshot s;
    s.visible = g_tray_visible;
    s.trigger = g_tray_trigger;
    s.revision = g_tray_revision;
    s.tooltip = g_tray_tooltip;
    s.menu = g_tray_menu;
    s.hasPixmap = g_tray_has_pixmap;
    s.iconW = g_tray_icon_w;
    s.iconH = g_tray_icon_h;
    s.pixmap = g_tray_pixmap;
    s.iconName = g_tray_icon_name;
    return s;
}

DBusConnection* trayConn() {
    std::lock_guard<std::mutex> lock(g_tray_mtx);
    return g_tray_conn;
}

// 释放 setter 在锁内取得的额外连接引用（见各 traySet*：锁内 ref、锁外发送后
// unref，以免并发 trayDestroy 关闭连接时发生释放后使用）。
void trayConnUnref(DBusConnection* c) {
    if (c != nullptr) dbus_connection_unref(c);
}

// ── 图标解码（dlopen gdk-pixbuf，无需 GTK 主循环）──────────────────
struct PixbufApi {
    void* pixbuf = nullptr;
    void* gobject = nullptr;
    void* (*new_from_file)(const char*, void**) = nullptr;
    int (*get_width)(const void*) = nullptr;
    int (*get_height)(const void*) = nullptr;
    int (*get_n_channels)(const void*) = nullptr;
    int (*get_rowstride)(const void*) = nullptr;
    unsigned char* (*get_pixels)(const void*) = nullptr;
    int (*get_has_alpha)(const void*) = nullptr;
    void (*object_unref)(void*) = nullptr;

    bool ok() const {
        return pixbuf != nullptr && new_from_file != nullptr &&
               get_width != nullptr && get_height != nullptr &&
               get_n_channels != nullptr && get_rowstride != nullptr &&
               get_pixels != nullptr && get_has_alpha != nullptr &&
               object_unref != nullptr;
    }
};

const PixbufApi& pixbufApi() {
    static const PixbufApi api = [] {
        PixbufApi a;
        a.pixbuf = dlopen("libgdk_pixbuf-2.0.so.0", RTLD_NOW);
        a.gobject = dlopen("libgobject-2.0.so.0", RTLD_NOW);
        if (a.pixbuf == nullptr || a.gobject == nullptr) {
            if (a.pixbuf != nullptr) dlclose(a.pixbuf);
            if (a.gobject != nullptr) dlclose(a.gobject);
            return PixbufApi{};
        }
        a.new_from_file = reinterpret_cast<decltype(a.new_from_file)>(
            dlsym(a.pixbuf, "gdk_pixbuf_new_from_file"));
        a.get_width = reinterpret_cast<decltype(a.get_width)>(
            dlsym(a.pixbuf, "gdk_pixbuf_get_width"));
        a.get_height = reinterpret_cast<decltype(a.get_height)>(
            dlsym(a.pixbuf, "gdk_pixbuf_get_height"));
        a.get_n_channels = reinterpret_cast<decltype(a.get_n_channels)>(
            dlsym(a.pixbuf, "gdk_pixbuf_get_n_channels"));
        a.get_rowstride = reinterpret_cast<decltype(a.get_rowstride)>(
            dlsym(a.pixbuf, "gdk_pixbuf_get_rowstride"));
        a.get_pixels = reinterpret_cast<decltype(a.get_pixels)>(
            dlsym(a.pixbuf, "gdk_pixbuf_get_pixels"));
        a.get_has_alpha = reinterpret_cast<decltype(a.get_has_alpha)>(
            dlsym(a.pixbuf, "gdk_pixbuf_get_has_alpha"));
        a.object_unref = reinterpret_cast<decltype(a.object_unref)>(
            dlsym(a.gobject, "g_object_unref"));
        if (!a.ok()) {
            dlclose(a.pixbuf);
            dlclose(a.gobject);
            return PixbufApi{};
        }
        return a;
    }();
    return api;
}

// PNG → ARGB32（网络字节序：A,R,G,B）；无 alpha 时 alpha=0xFF。
bool decodePng(const char* path, int32_t* outW, int32_t* outH,
               std::vector<uint8_t>* out) {
    const PixbufApi& api = pixbufApi();
    if (!api.ok()) return false;
    void* px = api.new_from_file(path, nullptr);
    if (px == nullptr) return false;
    const int w = api.get_width(px);
    const int h = api.get_height(px);
    const int n = api.get_n_channels(px);
    const int stride = api.get_rowstride(px);
    const unsigned char* pixels =
        static_cast<const unsigned char*>(api.get_pixels(px));
    const bool alpha = api.get_has_alpha(px) != 0;
    bool ok = false;
    if (w > 0 && h > 0 && n >= 3 && pixels != nullptr) {
        out->resize(static_cast<size_t>(w) * static_cast<size_t>(h) * 4u);
        size_t o = 0;
        for (int y = 0; y < h; y++) {
            const unsigned char* row =
                pixels + static_cast<size_t>(y) * static_cast<size_t>(stride);
            for (int x = 0; x < w; x++) {
                const unsigned char* p =
                    row + static_cast<size_t>(x) * static_cast<size_t>(n);
                (*out)[o++] = alpha ? p[3] : 0xFFu;
                (*out)[o++] = p[0];
                (*out)[o++] = p[1];
                (*out)[o++] = p[2];
            }
        }
        *outW = w;
        *outH = h;
        ok = true;
    }
    api.object_unref(px);
    return ok;
}

void ensureDirs(const std::string& path) {
    std::string cur;
    for (char ch : path) {
        cur.push_back(ch);
        if (ch == '/' && cur.size() > 1) ::mkdir(cur.c_str(), 0700);
    }
    ::mkdir(path.c_str(), 0700);
}

// dlopen 失败时的回退：把 PNG 拷到用户图标主题目录并返回 IconName。
std::string installIconFallback(const char* srcPath) {
    const char* home = std::getenv("HOME");
    if (home == nullptr || *home == '\0') return std::string();
    const std::string dir =
        std::string(home) + "/.local/share/icons/hicolor/48x48/apps";
    ensureDirs(dir);
    const std::string dst = dir + "/archoeramvsync.png";
    FILE* in = ::fopen(srcPath, "rb");
    if (in == nullptr) return std::string();
    FILE* out = ::fopen(dst.c_str(), "wb");
    if (out == nullptr) {
        std::fclose(in);
        return std::string();
    }
    char buf[8192];
    size_t r;
    while ((r = std::fread(buf, 1, sizeof(buf), in)) > 0) {
        std::fwrite(buf, 1, r, out);
    }
    std::fclose(in);
    std::fclose(out);
    return std::string("archoeramvsync");
}

struct IconData {
    bool hasPixmap = false;
    int32_t w = 0;
    int32_t h = 0;
    std::vector<uint8_t> pixels;
    std::string name;
};

IconData loadIcon(const char* path) {
    IconData d;
    if (decodePng(path, &d.w, &d.h, &d.pixels)) {
        d.hasPixmap = true;
        return d;
    }
    d.name = installIconFallback(path);
    return d;
}

// ── 信号 ──────────────────────────────────────────────────────────
void emitSimpleSignal(DBusConnection* c, const char* path, const char* iface,
                      const char* member) {
    DBusMessage* sig = dbus_message_new_signal(path, iface, member);
    send(c, sig);
}

void emitNewStatus(DBusConnection* c, bool visible) {
    const char* st = visible ? "Active" : "Passive";
    DBusMessage* sig = dbus_message_new_signal(kSniPath, kSniIface, "NewStatus");
    if (sig == nullptr) return;
    dbus_message_append_args(sig, DBUS_TYPE_STRING, &st, DBUS_TYPE_INVALID);
    send(c, sig);
}

void emitLayoutUpdated(DBusConnection* c, uint32_t rev) {
    DBusMessage* sig =
        dbus_message_new_signal(kMenuPath, kMenuIface, "LayoutUpdated");
    if (sig == nullptr) return;
    int32_t parent = 0;
    dbus_message_append_args(sig, DBUS_TYPE_UINT32, &rev, DBUS_TYPE_INT32,
                             &parent, DBUS_TYPE_INVALID);
    send(c, sig);
}

// ── 属性 ──────────────────────────────────────────────────────────
void appendSvInt32(DBusMessageIter* arr, const char* key, int32_t value) {
    DBusMessageIter entry, var;
    dbus_message_iter_open_container(arr, DBUS_TYPE_DICT_ENTRY, nullptr, &entry);
    dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &key);
    dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, "i", &var);
    dbus_message_iter_append_basic(&var, DBUS_TYPE_INT32, &value);
    dbus_message_iter_close_container(&entry, &var);
    dbus_message_iter_close_container(arr, &entry);
}

void appendIconPixmap(DBusMessageIter* it, const TraySnapshot& s) {
    DBusMessageIter arr;
    dbus_message_iter_open_container(it, DBUS_TYPE_ARRAY, "(iiay)", &arr);
    if (s.hasPixmap && !s.pixmap.empty()) {
        DBusMessageIter st, bytes;
        dbus_message_iter_open_container(&arr, DBUS_TYPE_STRUCT, nullptr, &st);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &s.iconW);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &s.iconH);
        // 注意：本机 libdbus 1.16.2 的 append_fixed_array 对 `ay` 会段错误，
        // 故逐字节 append_basic（图标数据量小，循环开销可忽略）。
        dbus_message_iter_open_container(&st, DBUS_TYPE_ARRAY, "y", &bytes);
        for (uint8_t b : s.pixmap) {
            dbus_message_iter_append_basic(&bytes, DBUS_TYPE_BYTE, &b);
        }
        dbus_message_iter_close_container(&st, &bytes);
        dbus_message_iter_close_container(&arr, &st);
    }
    dbus_message_iter_close_container(it, &arr);
}

// ToolTip 签名 (sa(iiay)ss)：图标名、图标位图、标题、副标题。
void appendToolTip(DBusMessageIter* it, const TraySnapshot& s) {
    DBusMessageIter st;
    dbus_message_iter_open_container(it, DBUS_TYPE_STRUCT, nullptr, &st);
    const char* iconName = s.iconName.c_str();
    dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &iconName);
    appendIconPixmap(&st, s);
    const char* title = "ArchoeraMvsync";
    const char* subtitle = s.tooltip.c_str();
    dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &title);
    dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &subtitle);
    dbus_message_iter_close_container(it, &st);
}

const char* sniPropSig(const char* name) {
    if (std::strcmp(name, "Category") == 0 || std::strcmp(name, "Id") == 0 ||
        std::strcmp(name, "Title") == 0 || std::strcmp(name, "Status") == 0 ||
        std::strcmp(name, "IconName") == 0) {
        return "s";
    }
    if (std::strcmp(name, "IconPixmap") == 0) return "a(iiay)";
    if (std::strcmp(name, "ToolTip") == 0) return "(sa(iiay)ss)";
    if (std::strcmp(name, "ItemIsMenu") == 0) return "b";
    if (std::strcmp(name, "Menu") == 0) return "o";
    if (std::strcmp(name, "WindowId") == 0) return "i";
    return nullptr;
}

void writeSniProp(DBusMessageIter* it, const char* name,
                  const TraySnapshot& s) {
    if (std::strcmp(name, "Category") == 0) {
        const char* v = "ApplicationStatus";
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "Id") == 0) {
        const char* v = "archoeramvsync";
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "Title") == 0) {
        const char* v = s.tooltip.empty() ? "ArchoeraMvsync" : s.tooltip.c_str();
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "Status") == 0) {
        const char* v = s.visible ? "Active" : "Passive";
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "IconName") == 0) {
        const char* v = s.iconName.c_str();
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "IconPixmap") == 0) {
        appendIconPixmap(it, s);
    } else if (std::strcmp(name, "ToolTip") == 0) {
        appendToolTip(it, s);
    } else if (std::strcmp(name, "ItemIsMenu") == 0) {
        dbus_bool_t v = TRUE;
        dbus_message_iter_append_basic(it, DBUS_TYPE_BOOLEAN, &v);
    } else if (std::strcmp(name, "Menu") == 0) {
        const char* v = kMenuPath;
        dbus_message_iter_append_basic(it, DBUS_TYPE_OBJECT_PATH, &v);
    } else if (std::strcmp(name, "WindowId") == 0) {
        int32_t v = 0;
        dbus_message_iter_append_basic(it, DBUS_TYPE_INT32, &v);
    }
}

const char* menuPropSig(const char* name) {
    if (std::strcmp(name, "Version") == 0) return "u";
    if (std::strcmp(name, "Status") == 0) return "s";
    if (std::strcmp(name, "TextDirection") == 0) return "s";
    if (std::strcmp(name, "IconThemePath") == 0) return "as";
    return nullptr;
}

void writeMenuProp(DBusMessageIter* it, const char* name) {
    if (std::strcmp(name, "Version") == 0) {
        uint32_t v = 3;
        dbus_message_iter_append_basic(it, DBUS_TYPE_UINT32, &v);
    } else if (std::strcmp(name, "Status") == 0) {
        const char* v = "normal";
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "TextDirection") == 0) {
        const char* v = "ltr";
        dbus_message_iter_append_basic(it, DBUS_TYPE_STRING, &v);
    } else if (std::strcmp(name, "IconThemePath") == 0) {
        DBusMessageIter arr;
        dbus_message_iter_open_container(it, DBUS_TYPE_ARRAY, "s", &arr);
        dbus_message_iter_close_container(it, &arr);
    }
}

void trayPropGet(DBusConnection* c, DBusMessage* msg) {
    DBusError err;
    dbus_error_init(&err);
    const char* iface = nullptr;
    const char* name = nullptr;
    if (!dbus_message_get_args(msg, &err, DBUS_TYPE_STRING, &iface,
                               DBUS_TYPE_STRING, &name, DBUS_TYPE_INVALID)) {
        dbus_error_free(&err);
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Get");
    }
    dbus_error_free(&err);
    const char* sig = nullptr;
    if (std::strcmp(iface, kSniIface) == 0) {
        sig = sniPropSig(name);
    } else if (std::strcmp(iface, kMenuIface) == 0) {
        sig = menuPropSig(name);
    }
    if (sig == nullptr) {
        return replyError(c, msg, "org.freedesktop.DBus.Error.UnknownProperty",
                          name);
    }
    const TraySnapshot s = traySnapshot();
    DBusMessage* reply = dbus_message_new_method_return(msg);
    if (reply == nullptr) return;
    DBusMessageIter it, var;
    dbus_message_iter_init_append(reply, &it);
    dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, sig, &var);
    if (std::strcmp(iface, kSniIface) == 0) {
        writeSniProp(&var, name, s);
    } else {
        writeMenuProp(&var, name);
    }
    dbus_message_iter_close_container(&it, &var);
    send(c, reply);
}

void trayPropGetAll(DBusConnection* c, DBusMessage* msg) {
    DBusError err;
    dbus_error_init(&err);
    const char* iface = nullptr;
    if (!dbus_message_get_args(msg, &err, DBUS_TYPE_STRING, &iface,
                               DBUS_TYPE_INVALID)) {
        dbus_error_free(&err);
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "GetAll");
    }
    dbus_error_free(&err);
    const char* const* names = nullptr;
    size_t count = 0;
    static const char* const kSniNames[] = {
        "Category",  "Id",     "Title",   "Status",   "IconName",
        "IconPixmap", "ToolTip", "ItemIsMenu", "Menu", "WindowId"};
    static const char* const kMenuNames[] = {"Version", "Status",
                                             "TextDirection", "IconThemePath"};
    const bool isSni = std::strcmp(iface, kSniIface) == 0;
    if (isSni) {
        names = kSniNames;
        count = sizeof(kSniNames) / sizeof(kSniNames[0]);
    } else if (std::strcmp(iface, kMenuIface) == 0) {
        names = kMenuNames;
        count = sizeof(kMenuNames) / sizeof(kMenuNames[0]);
    } else {
        return replyError(c, msg, DBUS_ERROR_UNKNOWN_INTERFACE, iface);
    }
    const TraySnapshot s = traySnapshot();
    DBusMessage* reply = dbus_message_new_method_return(msg);
    if (reply == nullptr) return;
    DBusMessageIter it, arr;
    dbus_message_iter_init_append(reply, &it);
    dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "{sv}", &arr);
    for (size_t i = 0; i < count; i++) {
        const char* sig =
            isSni ? sniPropSig(names[i]) : menuPropSig(names[i]);
        if (sig == nullptr) continue;
        DBusMessageIter entry, var;
        dbus_message_iter_open_container(&arr, DBUS_TYPE_DICT_ENTRY, nullptr,
                                         &entry);
        dbus_message_iter_append_basic(&entry, DBUS_TYPE_STRING, &names[i]);
        dbus_message_iter_open_container(&entry, DBUS_TYPE_VARIANT, sig, &var);
        if (isSni) {
            writeSniProp(&var, names[i], s);
        } else {
            writeMenuProp(&var, names[i]);
        }
        dbus_message_iter_close_container(&entry, &var);
        dbus_message_iter_close_container(&arr, &entry);
    }
    dbus_message_iter_close_container(&it, &arr);
    send(c, reply);
}

// ── dbusmenu 布局 ─────────────────────────────────────────────────
// 属性：label/enabled/visible/type；复选项补 toggle-type/toggle-state。
void appendMenuProps(DBusMessageIter* props, const TrayItem& item) {
    const bool sep = item.id == 0;
    appendSvString(props, "label", sep ? "" : item.label.c_str());
    appendSvBool(props, "enabled",
                 (!sep && item.enabled) ? static_cast<dbus_bool_t>(TRUE)
                                        : static_cast<dbus_bool_t>(FALSE));
    appendSvBool(props, "visible", TRUE);
    appendSvString(props, "type", sep ? "separator" : "standard");
    if (!sep && item.checked >= 0) {
        appendSvString(props, "toggle-type", "checkmark");
        appendSvInt32(props, "toggle-state", item.checked != 0 ? 1 : 0);
    }
}

// 向 it 追加一个 `(ia{sv}av)` 结构；root 时 children 为主菜单。
void appendMenuStruct(DBusMessageIter* it, int32_t id, const TrayItem* item,
                      const std::vector<TrayItem>* children, bool root) {
    DBusMessageIter st, props, kids;
    dbus_message_iter_open_container(it, DBUS_TYPE_STRUCT, nullptr, &st);
    dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &id);
    dbus_message_iter_open_container(&st, DBUS_TYPE_ARRAY, "{sv}", &props);
    if (root) {
        appendSvString(&props, "children-display", "submenu");
    } else if (item != nullptr) {
        appendMenuProps(&props, *item);
    }
    dbus_message_iter_close_container(&st, &props);
    dbus_message_iter_open_container(&st, DBUS_TYPE_ARRAY, "v", &kids);
    if (children != nullptr) {
        for (const TrayItem& child : *children) {
            DBusMessageIter var;
            dbus_message_iter_open_container(&kids, DBUS_TYPE_VARIANT,
                                             "(ia{sv}av)", &var);
            appendMenuStruct(&var, child.id, &child, nullptr, false);
            dbus_message_iter_close_container(&kids, &var);
        }
    }
    dbus_message_iter_close_container(&st, &kids);
    dbus_message_iter_close_container(it, &st);
}

void appendMenuPropsStruct(DBusMessageIter* arr, int32_t id,
                           const TrayItem* item, bool root) {
    DBusMessageIter st, props;
    dbus_message_iter_open_container(arr, DBUS_TYPE_STRUCT, nullptr, &st);
    dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &id);
    dbus_message_iter_open_container(&st, DBUS_TYPE_ARRAY, "{sv}", &props);
    if (root) {
        appendSvString(&props, "children-display", "submenu");
    } else if (item != nullptr) {
        appendMenuProps(&props, *item);
    }
    dbus_message_iter_close_container(&st, &props);
    dbus_message_iter_close_container(arr, &st);
}

void menuGetLayout(DBusConnection* c, DBusMessage* msg) {
    int32_t parentId = 0;
    DBusMessageIter it;
    if (dbus_message_iter_init(msg, &it)) {
        if (dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_INT32) {
            dbus_message_iter_get_basic(&it, &parentId);
        }
    }
    const TraySnapshot s = traySnapshot();
    DBusMessage* reply = dbus_message_new_method_return(msg);
    if (reply == nullptr) return;
    DBusMessageIter out;
    dbus_message_iter_init_append(reply, &out);
    dbus_message_iter_append_basic(&out, DBUS_TYPE_UINT32, &s.revision);
    if (parentId == 0) {
        appendMenuStruct(&out, 0, nullptr, &s.menu, true);
    } else {
        const TrayItem* found = nullptr;
        for (const TrayItem& item : s.menu) {
            if (item.id == parentId) {
                found = &item;
                break;
            }
        }
        appendMenuStruct(&out, parentId, found, nullptr, false);
    }
    send(c, reply);
}

void menuEvent(DBusConnection* c, DBusMessage* msg) {
    DBusMessageIter it;
    if (!dbus_message_iter_init(msg, &it) ||
        dbus_message_iter_get_arg_type(&it) != DBUS_TYPE_INT32) {
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Event");
    }
    int32_t id = 0;
    dbus_message_iter_get_basic(&it, &id);
    dbus_message_iter_next(&it);
    if (dbus_message_iter_get_arg_type(&it) != DBUS_TYPE_STRING) {
        return replyError(c, msg, DBUS_ERROR_INVALID_ARGS, "Event");
    }
    const char* eventId = nullptr;
    dbus_message_iter_get_basic(&it, &eventId);
    if (eventId != nullptr && std::strcmp(eventId, "clicked") == 0 && id > 0) {
        dispatch(makeTrayMenuCommand(id));
    }
    replyEmpty(c, msg);
}

void menuAboutToShow(DBusConnection* c, DBusMessage* msg) {
    dbus_bool_t yes = TRUE;
    DBusMessage* reply = dbus_message_new_method_return(msg);
    if (reply == nullptr) return;
    dbus_message_append_args(reply, DBUS_TYPE_BOOLEAN, &yes, DBUS_TYPE_INVALID);
    send(c, reply);
}

void menuGetGroupProperties(DBusConnection* c, DBusMessage* msg) {
    std::vector<int32_t> ids;
    DBusMessageIter it;
    if (dbus_message_iter_init(msg, &it) &&
        dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_ARRAY) {
        DBusMessageIter arr;
        dbus_message_iter_recurse(&it, &arr);
        while (dbus_message_iter_get_arg_type(&arr) == DBUS_TYPE_INT32) {
            int32_t v = 0;
            dbus_message_iter_get_basic(&arr, &v);
            ids.push_back(v);
            dbus_message_iter_next(&arr);
        }
    }
    const TraySnapshot s = traySnapshot();
    DBusMessage* reply = dbus_message_new_method_return(msg);
    if (reply == nullptr) return;
    DBusMessageIter out, arr;
    dbus_message_iter_init_append(reply, &out);
    dbus_message_iter_open_container(&out, DBUS_TYPE_ARRAY, "(ia{sv})", &arr);
    if (ids.empty()) {
        appendMenuPropsStruct(&arr, 0, nullptr, true);
        for (const TrayItem& item : s.menu) {
            appendMenuPropsStruct(&arr, item.id, &item, false);
        }
    } else {
        for (int32_t id : ids) {
            const TrayItem* found = nullptr;
            for (const TrayItem& item : s.menu) {
                if (item.id == id) {
                    found = &item;
                    break;
                }
            }
            appendMenuPropsStruct(&arr, id, found, id == 0);
        }
    }
    dbus_message_iter_close_container(&out, &arr);
    send(c, reply);
}

const char* trayIntrospect(const char* path) {
    if (std::strcmp(path, kSniPath) == 0) {
        return "<node><interface name=\"org.kde.StatusNotifierItem\">"
               "<method name=\"Activate\"><arg direction=\"in\" type=\"i\"/>"
               "<arg direction=\"in\" type=\"i\"/></method>"
               "<method name=\"SecondaryActivate\"><arg direction=\"in\" "
               "type=\"i\"/><arg direction=\"in\" type=\"i\"/></method>"
               "<method name=\"ContextMenu\"><arg direction=\"in\" type=\"i\"/>"
               "<arg direction=\"in\" type=\"i\"/></method>"
               "<method name=\"Scroll\"><arg direction=\"in\" type=\"i\"/>"
               "<arg direction=\"in\" type=\"s\"/></method>"
               "<signal name=\"NewIcon\"/><signal name=\"NewToolTip\"/>"
               "<signal name=\"NewStatus\"><arg type=\"s\"/></signal>"
               "</interface></node>";
    }
    return "<node><interface name=\"com.canonical.dbusmenu\">"
           "<method name=\"GetLayout\"><arg direction=\"in\" type=\"i\"/>"
           "<arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"as\"/>"
           "<arg direction=\"out\" type=\"u\"/>"
           "<arg direction=\"out\" type=\"(ia{sv}av)\"/></method>"
           "<method name=\"Event\"><arg direction=\"in\" type=\"i\"/>"
           "<arg direction=\"in\" type=\"s\"/><arg direction=\"in\" type=\"v\"/>"
           "<arg direction=\"in\" type=\"u\"/></method>"
           "<method name=\"AboutToShow\"><arg direction=\"in\" type=\"i\"/>"
           "<arg direction=\"out\" type=\"b\"/></method>"
           "</interface></node>";
}

// ── 对象路径分发 ──────────────────────────────────────────────────
DBusHandlerResult trayObjectHandler(DBusConnection* c, DBusMessage* msg, void*) {
    if (dbus_message_get_type(msg) != DBUS_MESSAGE_TYPE_METHOD_CALL) {
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    }
    const char* path = dbus_message_get_path(msg);
    const char* iface = dbus_message_get_interface(msg);
    const char* member = dbus_message_get_member(msg);
    if (path == nullptr || iface == nullptr || member == nullptr) {
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    }
    if (std::strcmp(iface, kIfaceProps) == 0) {
        if (std::strcmp(member, "Get") == 0) {
            trayPropGet(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "GetAll") == 0) {
            trayPropGetAll(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "Set") == 0) {
            replyError(c, msg, "org.freedesktop.DBus.Error.PropertyReadOnly",
                       "");
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    }
    if (std::strcmp(iface, kIfaceIntrospect) == 0 &&
        std::strcmp(member, "Introspect") == 0) {
        replyString(c, msg, trayIntrospect(path));
        return DBUS_HANDLER_RESULT_HANDLED;
    }
    if (std::strcmp(path, kSniPath) == 0 &&
        std::strcmp(iface, kSniIface) == 0) {
        if (std::strcmp(member, "Activate") == 0) {
            // trigger==1（右键唤菜单）时左键才作为单击事件回传；trigger==0 交由
            // 面板自行展开 dbusmenu。
            if (traySnapshot().trigger == 1) {
                dispatch(makeTrayClick(EVENT_TRAY_CLICK));
            }
            replyEmpty(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "SecondaryActivate") == 0) {
            dispatch(makeTrayClick(EVENT_TRAY_DOUBLE_CLICK));
            replyEmpty(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "ContextMenu") == 0) {
            dispatch(makeTrayClick(EVENT_TRAY_RIGHT_CLICK));
            replyEmpty(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "Scroll") == 0) {
            replyEmpty(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    }
    if (std::strcmp(path, kMenuPath) == 0 &&
        std::strcmp(iface, kMenuIface) == 0) {
        if (std::strcmp(member, "GetLayout") == 0) {
            menuGetLayout(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "Event") == 0) {
            menuEvent(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "AboutToShow") == 0) {
            menuAboutToShow(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        if (std::strcmp(member, "GetGroupProperties") == 0) {
            menuGetGroupProperties(c, msg);
            return DBUS_HANDLER_RESULT_HANDLED;
        }
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    }
    return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
}

// ── 生命周期 ──────────────────────────────────────────────────────
void trayPumpLoop() {
    while (g_tray_running.load(std::memory_order_acquire)) {
        DBusConnection* c = trayConn();
        if (c == nullptr) break;
        // 用有限超时（而非 -1）：让泵线程周期性检查 g_tray_running，使 trayDestroy
        // 能可靠 join；同时每轮把 Dart 侧排队的信号 flush 出去。
        dbus_connection_read_write_dispatch(c, 50);
    }
}

// 幂等销毁：注销对象路径、等待并 join 泵线程、释放连接与菜单拷贝。
void trayTeardownInternal() {
    DBusConnection* c = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        c = g_tray_conn;
        g_tray_conn = nullptr;
        g_tray_created = false;
        g_tray_has_pixmap = false;
        g_tray_pixmap.clear();
        g_tray_icon_name.clear();
        g_tray_menu.clear();
        g_tray_revision = 0;
    }
    g_tray_running.store(false, std::memory_order_release);
    if (c != nullptr) {
        dbus_connection_unregister_object_path(c, kSniPath);
        dbus_connection_unregister_object_path(c, kMenuPath);
    }
    if (g_tray_pump != nullptr) {
        if (g_tray_pump->joinable()) g_tray_pump->join();
        delete g_tray_pump;
        g_tray_pump = nullptr;
    }
    if (c != nullptr) {
        dbus_connection_close(c);
        dbus_connection_unref(c);
    }
}

// ── 系统提示 ──────────────────────────────────────────────────────
bool notifyViaDbus(const char* title, const char* body) {
    DBusConnection* c = conn();
    if (c == nullptr) return false;
    DBusMessage* msg = dbus_message_new_method_call(
        "org.freedesktop.Notifications", "/org/freedesktop/Notifications",
        "org.freedesktop.Notifications", "Notify");
    DBusMessageIter it, actions, hints;
    dbus_message_iter_init_append(msg, &it);
    const char* app = "ArchoeraMvsync";
    uint32_t replaces = 0;
    const char* icon = "";
    const char* summary = title;
    const char* text = body;
    int32_t timeout = -1;
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &app);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_UINT32, &replaces);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &icon);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &summary);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &text);
    dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "s", &actions);
    dbus_message_iter_close_container(&it, &actions);
    dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "{sv}", &hints);
    dbus_message_iter_close_container(&it, &hints);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_INT32, &timeout);
    DBusError err;
    dbus_error_init(&err);
    DBusMessage* reply =
        dbus_connection_send_with_reply_and_block(c, msg, 3000, &err);
    dbus_message_unref(msg);
    const bool ok = reply != nullptr;
    if (reply != nullptr) dbus_message_unref(reply);
    dbus_error_free(&err);
    return ok;
}

}  // namespace

// ── 后端接口 ──────────────────────────────────────────────────────
uint32_t caps() {
    uint32_t c = CAP_POWER_INHIBIT | CAP_POWER_SCREEN_STATE | CAP_MEDIA_SESSION |
                 CAP_MEDIA_SEEK | CAP_MEDIA_ARTWORK | CAP_APP_INSTANCE |
                 CAP_SYSTEM_ACCENT | CAP_SYSTEM_THEME | CAP_DEEP_LINK |
                 CAP_REVEAL_PATH | CAP_TRAY | CAP_SECURE_FILE | CAP_ELEVATION |
                 CAP_SESSION_END;
    const bool hasDisplay = std::getenv("WAYLAND_DISPLAY") != nullptr ||
                            std::getenv("DISPLAY") != nullptr;
    if (hasDisplay && gtkwin::available()) c |= CAP_WINDOW_STATE | CAP_WINDOW_CONTROL;
    return c;
}

int32_t hardenProcess() {
#ifdef NDEBUG
    // 仅禁 core：core dump 会把宿主内存中的明文凭据**落盘**（SEC-48 实测：
    // `kill -SEGV` → systemd-coredump 落盘并对属主附加可读 ACL → 明文 cookie 直接可取，
    // 且泄露超出进程生命周期）。core 限制由子进程继承。
    //
    // **刻意不设 `PR_SET_DUMPABLE=0`**：它会让「子进程读父进程 `/proc/<ppid>/exe`」被内核
    // 拒绝，**直接打断 vault 的父进程血缘校验**（实测报错：独立运行被拒：无法读取父进程
    // 可执行文件路径），导致凭据库整体不可用——安全机制之间不能互相拆台。
    // 等价的内存读取路径（ptrace / process_vm_readv / /proc/pid/mem）由内核
    // `kernel.yama.ptrace_scope=1` 阻断（同用户**非父**进程不可 attach；本机即为 1）；
    // 这是已声明的 L2 残余（SEC-25），不以牺牲可用性换取。
    struct rlimit rl;
    rl.rlim_cur = 0;
    rl.rlim_max = 0;
    setrlimit(RLIMIT_CORE, &rl);
#endif
    return OK;
}

int32_t processElevation() {
    // sudo / root 登录 / setuid 均使有效 uid 为 0；普通启动恒为非 0。
    return ::geteuid() == 0 ? APL_ELEVATION_ROOT : APL_ELEVATION_NONE;
}

int32_t init() {
    dbus_threads_init_default();
    installSessionEndSignal();  // 系统关机/注销 → SIGTERM（休眠不投递）
    g_running.store(true, std::memory_order_release);
    // 深链服务常驻：即使主题/防休眠从不订阅，也要建立会话总线并注册
    // OpenUri，否则次实例转发的 archoera:// 会因找不到服务而失败。
    g_deeplink_events.store(true, std::memory_order_release);
    g_pump = new std::thread(pumpLoop);
    // 冷启动：命令行中的 archoera:// URL（协议处理程序把 URL 作为 argv 传入）。
    const std::string cold = readCmdlineUrl();
    if (!cold.empty()) {
        std::lock_guard<std::mutex> lock(g_deeplink_mutex);
        g_deeplink_pending = cold;
    }
    return OK;
}

int32_t shutdown() {
    trayDestroy();  // 注销 SNI/dbusmenu 对象并 join 托盘泵线程
    g_running.store(false, std::memory_order_release);
    if (g_pump != nullptr) {
        if (g_pump->joinable()) g_pump->join();
        delete g_pump;
        g_pump = nullptr;
    }
    gtkwin::setEvents(false);
    std::lock_guard<std::mutex> lock(g_mtx);
    if (g_conn != nullptr) {
        dbus_connection_remove_filter(g_conn, filter, nullptr);
        dbus_connection_unref(g_conn);
        g_conn = nullptr;
    }
    g_cookie = 0;
    g_inhibit_on = false;
    g_screen_events.store(false, std::memory_order_release);
    g_accent_events.store(false, std::memory_order_release);
    g_session_end_requested.store(false, std::memory_order_release);
    restoreSessionEndSignal();
    return OK;
}

int32_t powerSetSleepInhibit(int32_t on) {
    // 只登记目标状态并立即返回：实际 Inhibit/UnInhibit 是阻塞式 DBus 调用，
    // 由泵线程异步执行（见 applyInhibit），避免在 Dart/主线程同步等待而卡住 UI。
    g_inhibit_want.store(on != 0, std::memory_order_release);
    g_inhibit_pending.store(true, std::memory_order_release);
    return OK;
}

int32_t powerSetScreenEvents(int32_t on) {
    // 只登记标志：连接由泵线程建立（见 pumpLoop），避免主线程启动期同步建连。
    g_screen_events.store(on != 0, std::memory_order_release);
    return OK;
}

int32_t windowSetEvents(int32_t on) { return gtkwin::setEvents(on != 0); }

// ── 窗口控制（ABI v10）────────────────────────────────────────────
int32_t windowSetSize(int32_t w, int32_t h) { return gtkwin::setSize(w, h); }
int32_t windowSetMinSize(int32_t w, int32_t h) {
    return gtkwin::setMinSize(w, h);
}
int32_t windowCenter() { return gtkwin::center(); }
int32_t windowShow() { return gtkwin::show(); }
int32_t windowHide() { return gtkwin::hide(); }
int32_t windowMinimize() { return gtkwin::minimize(); }
int32_t windowRestore() { return gtkwin::restore(); }
int32_t windowSetMaximized(int32_t on) { return gtkwin::setMaximized(on); }
int32_t windowSetFullscreen(int32_t on) { return gtkwin::setFullscreen(on); }
int32_t windowSetAlwaysOnTop(int32_t on) {
    return gtkwin::setAlwaysOnTop(on);
}
int32_t windowSetResizable(int32_t on) { return gtkwin::setResizable(on); }
int32_t windowSetDecor(int32_t mode) { return gtkwin::setDecor(mode); }

int32_t windowGetPosition(int32_t* x, int32_t* y) {
    if (x == nullptr || y == nullptr) return ERR_STATE;
    if (!gtkwin::hasWindow()) return ERR_BACKEND;
    *x = gtkwin::g_pos_x.load(std::memory_order_acquire);
    *y = gtkwin::g_pos_y.load(std::memory_order_acquire);
    return OK;
}

// ABI v11：内容区尺寸（GTK 口径，与 windowSetSize 的 gtk_window_resize 一致）。
int32_t windowGetSize(int32_t* w, int32_t* h) {
    if (w == nullptr || h == nullptr) return ERR_STATE;
    if (!gtkwin::hasWindow()) return ERR_BACKEND;
    const int32_t ww = gtkwin::g_size_w.load(std::memory_order_acquire);
    const int32_t wh = gtkwin::g_size_h.load(std::memory_order_acquire);
    if (ww <= 0 || wh <= 0) return ERR_STATE;
    *w = ww;
    *h = wh;
    return OK;
}

int32_t windowSetPosition(int32_t x, int32_t y) {
    return gtkwin::setPosition(x, y);
}

int32_t windowStartDrag() { return gtkwin::startDrag(); }
int32_t windowStartResize(int32_t edge) { return gtkwin::startResize(edge); }

// 只投递关闭请求；桥接不关窗（Dart 决定隐藏到托盘 / 退出）。
int32_t windowRequestClose() {
    dispatch(makeWindowCloseRequest(WINDOW_CLOSE));
    return OK;
}

// 退出前显式摘观察者：等价 windowSetEvents(0)（幂等）。
int32_t windowStopEvents() { return gtkwin::setEvents(false); }

int32_t mediaSetTrack(const AplTrackMeta* meta) {
    DBusConnection* c = conn();
    {
        std::lock_guard<std::mutex> lock(g_mtx);
        g_title.clear();
        g_artist.clear();
        g_album.clear();
        g_art.clear();
        g_track_serial++;
        g_duration_ms = -1;
        if (meta != nullptr) {
            auto str = [](const AplString& s) {
                return (s.data != nullptr && s.len > 0)
                           ? std::string(s.data, s.len)
                           : std::string();
            };
            g_title = str(meta->title);
            g_artist = str(meta->artist);
            g_album = str(meta->album);
            g_art = str(meta->art_url);
            g_duration_ms = meta->duration_ms;
            g_position_ms = 0;
            g_position_base = nowMs();
        }
    }
    emitChanged(c, {"Metadata"});
    return OK;
}

int32_t mediaSetPlayback(int32_t state, int64_t position_ms, double speed,
                         double volume, int32_t loop, int32_t shuffle) {
    DBusConnection* c = conn();
    bool state_changed;
    {
        std::lock_guard<std::mutex> lock(g_mtx);
        const int new_state = ((state % 3) + 3) % 3;
        state_changed = new_state != g_state;
        g_state = new_state;
        g_position_ms = position_ms;
        g_position_base = nowMs();
        g_rate = speed;
        g_volume = volume;
        g_loop = loop;
        g_shuffle = shuffle != 0;
    }
    std::vector<const char*> changed;
    if (state_changed) changed.push_back("PlaybackStatus");
    changed.push_back("Position");
    changed.push_back("Volume");
    changed.push_back("LoopStatus");
    changed.push_back("Shuffle");
    emitChanged(c, changed);
    return OK;
}

int32_t mediaSetWindow(int64_t) { return OK; }

int32_t appInstanceAcquire() {
    if (g_instance_fd >= 0) return 1;
    const char* runtime = std::getenv("XDG_RUNTIME_DIR");
    std::string dir = runtime != nullptr ? runtime : "/tmp";
    std::string path = dir + "/archoera_mvsync.lock";
    const int fd = ::open(path.c_str(), O_RDWR | O_CREAT | O_CLOEXEC, 0600);
    if (fd < 0) return ERR_BACKEND;
    if (::flock(fd, LOCK_EX | LOCK_NB) != 0) {
        ::close(fd);
        return 0;
    }
    g_instance_fd = fd;
    return 1;
}

int32_t protocolRegister(const char* scheme) {
    if (!isValidScheme(scheme)) return ERR_STATE;
    const std::string path = desktopFilePath();
    if (path.empty()) return ERR_BACKEND;
    char exe[4096] = {0};
    const ssize_t n = ::readlink("/proc/self/exe", exe, sizeof(exe) - 1);
    if (n <= 0) return ERR_BACKEND;
    exe[n] = '\0';
    const size_t slash = path.find_last_of('/');
    if (slash != std::string::npos) {
        const std::string dir = path.substr(0, slash);
        // 只建最后一级；父目录（~/.local/share/applications）通常已存在。
        ::mkdir(dir.c_str(), 0700);
    }
    FILE* f = ::fopen(path.c_str(), "w");
    if (f == nullptr) return ERR_BACKEND;
    std::fprintf(f,
                 "[Desktop Entry]\n"
                 "Type=Application\n"
                 "Name=ArchoeraMvsync\n"
                 "Exec=\"%s\" %%U\n"
                 "Terminal=false\n"
                 "NoDisplay=true\n"
                 "MimeType=x-scheme-handler/%s;\n",
                 exe, scheme);
    std::fclose(f);
    // GIO 注册默认处理（best-effort，无 GIO 时仅写 desktop 文件）。
    void* gio = dlopen("libgio-2.0.so.0", RTLD_NOW);
    if (gio != nullptr) {
        auto new_from_file = reinterpret_cast<void* (*)(const char*)>(
            dlsym(gio, "g_desktop_app_info_new_from_filename"));
        auto set_default =
            reinterpret_cast<int (*)(void*, const char*, void*)>(
                dlsym(gio, "g_app_info_set_as_default_for_type"));
        if (new_from_file != nullptr && set_default != nullptr) {
            void* info = new_from_file(path.c_str());
            if (info != nullptr) {
                const std::string ct = std::string("x-scheme-handler/") + scheme;
                set_default(info, ct.c_str(), nullptr);
            }
        }
    }
    return OK;
}

int32_t protocolUnregister(const char* scheme) {
    if (!isValidScheme(scheme)) return ERR_STATE;
    const std::string path = desktopFilePath();
    if (!path.empty()) ::unlink(path.c_str());
    return OK;
}

int32_t deepLinkTake(AplString* out) { return deepLinkTakeLinux(out); }

int32_t deepLinkForward() {
    const std::string url = readCmdlineUrl();
    if (url.empty()) return 0;
    // 纯单向：广播信号后立即返回，不等首实例回复、不重试等待。
    DBusError err;
    dbus_error_init(&err);
    DBusConnection* c = dbus_bus_get(DBUS_BUS_SESSION, &err);
    if (c == nullptr) {
        dbus_error_free(&err);
        return ERR_BACKEND;
    }
    DBusMessage* msg = dbus_message_new_signal(
        kDeepLinkPath, "org.archoera.DeepLink", "Uri");
    if (msg == nullptr) {
        dbus_connection_unref(c);
        return ERR_BACKEND;
    }
    const char* u = url.c_str();
    dbus_message_append_args(msg, DBUS_TYPE_STRING, &u, DBUS_TYPE_INVALID);
    dbus_uint32_t serial = 0;
    const bool queued = dbus_connection_send(c, msg, &serial) != 0;
    dbus_message_unref(msg);
    if (queued) dbus_connection_flush(c);
    dbus_connection_unref(c);
    return queued ? 1 : ERR_BACKEND;
}

int32_t windowActivate() {
    gtkwin::activate();
    return OK;
}

// 系统主题色（DE accent）：XDG Desktop Portal（跨 DE 标准）：
//   org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop
//   org.freedesktop.portal.Settings.ReadOne("org.freedesktop.appearance","accent-color")
//   → v 内含 (ddd)（sRGB，[0,1]）。
//
// 关键：查询走**异步** DBus（send_with_reply + notify），绝不在 Dart/主线程
// 同步等待——切回应用会重读强调色，同步 send_with_reply_and_block 会阻塞 UI。
// 结果缓存；就绪/变化时发 SYSTEM_ACCENT 事件通知 Dart 重读。
void onAccentReply(DBusPendingCall* pending, void*) {
    DBusMessage* reply = dbus_pending_call_steal_reply(pending);
    if (reply == nullptr) return;
    bool ok = false;
    double rr = 0, gg = 0, bb = 0;
    DBusMessageIter it, var, st;
    if (dbus_message_iter_init(reply, &it) &&
        dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_VARIANT) {
        dbus_message_iter_recurse(&it, &var);
        if (dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_STRUCT) {
            dbus_message_iter_recurse(&var, &st);
            if (dbus_message_iter_get_arg_type(&st) == DBUS_TYPE_DOUBLE) {
                dbus_message_iter_get_basic(&st, &rr);
                dbus_message_iter_next(&st);
                dbus_message_iter_get_basic(&st, &gg);
                dbus_message_iter_next(&st);
                dbus_message_iter_get_basic(&st, &bb);
                ok = true;
            }
        }
    }
    dbus_message_unref(reply);
    if (!ok) return;
    auto toU8 = [](double v) -> int32_t {
        if (v <= 0.0) return 0;
        if (v >= 1.0) return 255;
        return static_cast<int32_t>(v * 255.0 + 0.5);
    };
    const int32_t r8 = toU8(rr), g8 = toU8(gg), b8 = toU8(bb);
    bool changed;
    {
        std::lock_guard<std::mutex> lock(g_accent_mtx);
        changed = !g_accent_valid || g_accent_r != r8 || g_accent_g != g8 ||
                  g_accent_b != b8;
        g_accent_r = r8;
        g_accent_g = g8;
        g_accent_b = b8;
        g_accent_valid = true;
    }
    if (changed) dispatch(makeSystemAccent(r8, g8, b8));
}

// 触发一次异步强调色查询（非阻塞）；结果由 onAccentReply 缓存并发事件。
void requestAccent() {
    DBusConnection* c = conn();
    if (c == nullptr) return;
    DBusMessage* msg = dbus_message_new_method_call(
        "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.Settings", "ReadOne");
    if (msg == nullptr) return;
    const char* ns = "org.freedesktop.appearance";
    const char* key = "accent-color";
    dbus_message_append_args(msg, DBUS_TYPE_STRING, &ns, DBUS_TYPE_STRING, &key,
                             DBUS_TYPE_INVALID);
    DBusPendingCall* pending = nullptr;
    if (dbus_connection_send_with_reply(c, msg, &pending, 3000) &&
        pending != nullptr) {
        dbus_pending_call_set_notify(pending, onAccentReply, nullptr, nullptr);
        dbus_pending_call_unref(pending);
    }
    dbus_message_unref(msg);
}

// 系统深浅色：解析 portal color-scheme（0=未指定 1=深色 2=浅色）并推送。
void onThemeReply(DBusPendingCall* pending, void*) {
    DBusMessage* reply = dbus_pending_call_steal_reply(pending);
    if (reply == nullptr) return;
    bool ok = false;
    uint32_t v = 0;
    DBusMessageIter it, var;
    if (dbus_message_iter_init(reply, &it) &&
        dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_VARIANT) {
        dbus_message_iter_recurse(&it, &var);
        if (dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_UINT32) {
            dbus_message_iter_get_basic(&var, &v);
            ok = true;
        }
    }
    dbus_message_unref(reply);
    if (!ok) return;
    const bool dark = (v == 1);
    const bool first = !g_theme_valid.exchange(true, std::memory_order_acq_rel);
    const bool changed =
        dark != g_theme_dark.exchange(dark, std::memory_order_acq_rel);
    if (first || changed) dispatch(makeSystemTheme(dark));
}

// 触发一次异步深浅色查询（非阻塞）。
void requestTheme() {
    DBusConnection* c = conn();
    if (c == nullptr) return;
    DBusMessage* msg = dbus_message_new_method_call(
        "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.Settings", "ReadOne");
    if (msg == nullptr) return;
    const char* ns = "org.freedesktop.appearance";
    const char* key = "color-scheme";
    dbus_message_append_args(msg, DBUS_TYPE_STRING, &ns, DBUS_TYPE_STRING, &key,
                             DBUS_TYPE_INVALID);
    DBusPendingCall* pending = nullptr;
    if (dbus_connection_send_with_reply(c, msg, &pending, 3000) &&
        pending != nullptr) {
        dbus_pending_call_set_notify(pending, onThemeReply, nullptr, nullptr);
        dbus_pending_call_unref(pending);
    }
    dbus_message_unref(msg);
}

bool systemAccent(int32_t* r, int32_t* g, int32_t* b) {
    // 立即返回缓存（不阻塞）；无缓存时触发一次异步查询并返回 false（Dart
    // 回退默认色，待事件到达后重读）。
    int32_t rr, gg, bb;
    bool valid;
    {
        std::lock_guard<std::mutex> lock(g_accent_mtx);
        valid = g_accent_valid;
        rr = g_accent_r;
        gg = g_accent_g;
        bb = g_accent_b;
    }
    if (valid) {
        if (r != nullptr) *r = rr;
        if (g != nullptr) *g = gg;
        if (b != nullptr) *b = bb;
        return true;
    }
    requestAccent();
    return false;
}

int32_t systemAccentSetEvents(bool on) {
    // 只登记标志：连接由泵线程建立（见 pumpLoop），避免主线程同步建连。
    g_accent_events.store(on, std::memory_order_release);
    if (on) {
        // 订阅即推送当前值（泵线程连接就绪后异步查询并发 SYSTEM_ACCENT 事件）。
        g_accent_pending.store(true, std::memory_order_release);
    }
    return OK;
}

int32_t systemThemeSetEvents(bool on) {
    g_theme_events.store(on, std::memory_order_release);
    if (on) {
        // 订阅即推送当前值（泵线程连接就绪后异步查询并发 SYSTEM_THEME 事件）。
        g_theme_pending.store(true, std::memory_order_release);
    }
    return OK;
}

int32_t notify(const char* title, const char* body) {
    // 仅走 D-Bus org.freedesktop.Notifications（零子进程）。
    return notifyViaDbus(title, body) ? OK : ERR_BACKEND;
}

// ── 文件管理器定位（零子进程：FileManager1 D-Bus + GIO 回退）─────────
namespace {

// GIO（dlopen）：path → file:// URI（含百分号转义）与「用默认应用打开」回退。
struct GioReveal {
    void* handle = nullptr;
    void* (*file_new_for_path)(const char*) = nullptr;
    char* (*file_get_uri)(void*) = nullptr;
    void (*object_unref)(void*) = nullptr;
    void (*gfree)(void*) = nullptr;
    int (*launch_default_for_uri)(const char*, void*, void**) = nullptr;
    void (*error_free)(void*) = nullptr;

    bool ok() const {
        return handle != nullptr && file_new_for_path != nullptr &&
               file_get_uri != nullptr && launch_default_for_uri != nullptr;
    }
};

const GioReveal& gioReveal() {
    static const GioReveal api = [] {
        GioReveal a;
        a.handle = dlopen("libgio-2.0.so.0", RTLD_NOW);
        if (a.handle == nullptr) a.handle = dlopen(nullptr, RTLD_NOW);
        if (a.handle != nullptr) {
            a.file_new_for_path =
                reinterpret_cast<decltype(a.file_new_for_path)>(
                    dlsym(a.handle, "g_file_new_for_path"));
            a.file_get_uri = reinterpret_cast<decltype(a.file_get_uri)>(
                dlsym(a.handle, "g_file_get_uri"));
            a.object_unref = reinterpret_cast<decltype(a.object_unref)>(
                dlsym(a.handle, "g_object_unref"));
            a.gfree = reinterpret_cast<decltype(a.gfree)>(
                dlsym(a.handle, "g_free"));
            a.launch_default_for_uri =
                reinterpret_cast<decltype(a.launch_default_for_uri)>(
                    dlsym(a.handle, "g_app_info_launch_default_for_uri"));
            a.error_free = reinterpret_cast<decltype(a.error_free)>(
                dlsym(a.handle, "g_error_free"));
        }
        return a;
    }();
    return api;
}

std::string pathToUri(const std::string& path) {
    const auto& gio = gioReveal();
    if (!gio.ok()) return std::string();
    void* f = gio.file_new_for_path(path.c_str());
    if (f == nullptr) return std::string();
    char* uri = gio.file_get_uri(f);
    std::string out = uri != nullptr ? uri : std::string();
    if (uri != nullptr && gio.gfree != nullptr) gio.gfree(uri);
    if (gio.object_unref != nullptr) gio.object_unref(f);
    return out;
}

std::string parentDir(const std::string& path) {
    const auto pos = path.find_last_of('/');
    if (pos == std::string::npos) return path;
    if (pos == 0) return std::string("/");
    return path.substr(0, pos);
}

bool gioOpenDir(const std::string& dir) {
    const auto& gio = gioReveal();
    if (!gio.ok()) return false;
    const std::string uri = pathToUri(dir);
    if (uri.empty()) return false;
    void* err = nullptr;
    const int ok = gio.launch_default_for_uri(uri.c_str(), nullptr, &err);
    if (err != nullptr && gio.error_free != nullptr) gio.error_free(err);
    return ok != 0;
}

// org.freedesktop.FileManager1.ShowItems(as uris, s startup_id)
bool fileManagerShowItems(const std::string& uri) {
    DBusError err;
    dbus_error_init(&err);
    DBusConnection* c = dbus_bus_get(DBUS_BUS_SESSION, &err);
    if (c == nullptr) {
        dbus_error_free(&err);
        return false;
    }
    DBusMessage* msg = dbus_message_new_method_call(
        "org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
        "org.freedesktop.FileManager1", "ShowItems");
    bool ok = false;
    if (msg != nullptr) {
        // ShowItems(as uris, s startup_id)：数组用迭代器构造（append_args 的
        // 数组变参极易写错指针层级，实测会段错误）。
        DBusMessageIter it;
        DBusMessageIter arr;
        dbus_message_iter_init_append(msg, &it);
        const char* u = uri.c_str();
        if (dbus_message_iter_open_container(
                &it, DBUS_TYPE_ARRAY, DBUS_TYPE_STRING_AS_STRING, &arr) &&
            dbus_message_iter_append_basic(&arr, DBUS_TYPE_STRING, &u) &&
            dbus_message_iter_close_container(&it, &arr)) {
            const char* startup = "";
            if (dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING,
                                               &startup)) {
                DBusError e2;
                dbus_error_init(&e2);
                DBusMessage* reply =
                    dbus_connection_send_with_reply_and_block(c, msg, 3000,
                                                              &e2);
                ok = reply != nullptr;
                if (reply != nullptr) dbus_message_unref(reply);
                dbus_error_free(&e2);
            }
        }
        dbus_message_unref(msg);
    }
    dbus_error_free(&err);
    dbus_connection_unref(c);
    return ok;
}

}  // namespace

int32_t revealPath(const char* path) {
    if (path == nullptr || *path == '\0') return ERR_STATE;
    struct stat st;
    if (::stat(path, &st) != 0) return ERR_BACKEND;  // 路径不存在
    const std::string p(path);
    const bool isDir = S_ISDIR(st.st_mode) != 0;
    // 优先 FileManager1 定位（文件会被选中；目录被打开）。
    const std::string uri = pathToUri(p);
    if (!uri.empty() && fileManagerShowItems(uri)) return OK;
    // 回退：GIO 用默认应用打开所在目录（文件）或本目录（目录）。
    const std::string dir = isDir ? p : parentDir(p);
    if (!dir.empty() && gioOpenDir(dir)) return OK;
    return ERR_BACKEND;
}

// ── 安全文件原语（POSIX open(2)）───────────────────────────────────
int32_t fileRead(const char* path, bool allow_symlink, int64_t max_bytes, uint8_t* out,
                 size_t* inout_len) {
    return safeFileReadPosix(path, allow_symlink, max_bytes, out, inout_len);
}

int32_t fileWriteAtomic(const char* path, const uint8_t* data, size_t len) {
    return safeFileWriteAtomicPosix(path, data, len);
}

// ── 系统托盘后端接口 ──────────────────────────────────────────────
int32_t trayCreate(const char* icon_path) {
    if (icon_path == nullptr || *icon_path == '\0') return ERR_STATE;
    bool already = false;
    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        already = g_tray_created;
    }
    if (already) return traySetIcon(icon_path);  // 幂等：已存在则更新图标

    const IconData ic = loadIcon(icon_path);

    dbus_threads_init_default();
    DBusError err;
    dbus_error_init(&err);
    // 私有连接：与 MPRIS 共享连接隔离（见上方过滤器遮蔽说明）。
    DBusConnection* c = dbus_bus_get_private(DBUS_BUS_SESSION, &err);
    if (c == nullptr) {
        log(2, "platform", "系统托盘：无法连接会话总线，能力降级");
        dbus_error_free(&err);
        return ERR_BACKEND;
    }
    dbus_error_free(&err);
    dbus_connection_set_exit_on_disconnect(c, FALSE);

    static const DBusObjectPathVTable vtable = {
        nullptr, trayObjectHandler, nullptr, nullptr, nullptr, nullptr};
    const bool regSni =
        dbus_connection_register_object_path(c, kSniPath, &vtable, nullptr);
    const bool regMenu =
        regSni &&
        dbus_connection_register_object_path(c, kMenuPath, &vtable, nullptr);
    if (!regSni || !regMenu) {
        log(2, "platform", "系统托盘：注册 D-Bus 对象路径失败");
        dbus_connection_unregister_object_path(c, kSniPath);
        dbus_connection_unregister_object_path(c, kMenuPath);
        dbus_connection_close(c);
        dbus_connection_unref(c);
        return ERR_BACKEND;
    }

    // 向 Watcher 注册；不存在则整体失败（能力降级）。以对象路径形式注册，
    // Watcher 改用本连接的唯一名定位（因此不需要任何 --own-name 权限）。
    bool watcherOk = false;
    DBusMessage* reg = dbus_message_new_method_call(
        kSniWatcherIface, kSniWatcherPath, kSniWatcherIface,
        "RegisterStatusNotifierItem");
    if (reg != nullptr) {
        const char* svc = kSniPath;  // 路径形式：Watcher 改用调用者唯一连接名
        dbus_message_append_args(reg, DBUS_TYPE_STRING, &svc,
                                 DBUS_TYPE_INVALID);
        DBusError e2;
        dbus_error_init(&e2);
        DBusMessage* reply =
            dbus_connection_send_with_reply_and_block(c, reg, 2000, &e2);
        watcherOk = reply != nullptr;
        if (reply != nullptr) dbus_message_unref(reply);
        dbus_error_free(&e2);
        dbus_message_unref(reg);
    }
    if (!watcherOk) {
        log(2, "platform", "系统托盘：StatusNotifierWatcher 不可用，能力降级");
        dbus_connection_unregister_object_path(c, kSniPath);
        dbus_connection_unregister_object_path(c, kMenuPath);
        dbus_connection_close(c);
        dbus_connection_unref(c);
        return ERR_BACKEND;
    }

    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        g_tray_conn = c;
        g_tray_created = true;
        g_tray_has_pixmap = ic.hasPixmap;
        g_tray_pixmap = ic.pixels;
        g_tray_icon_w = ic.w;
        g_tray_icon_h = ic.h;
        g_tray_icon_name = ic.name;
    }
    g_tray_running.store(true, std::memory_order_release);
    g_tray_pump = new std::thread(trayPumpLoop);
    log(1, "platform", "系统托盘已创建（StatusNotifierItem + dbusmenu）");
    return OK;
}

int32_t trayDestroy() {
    trayTeardownInternal();
    return OK;
}

int32_t traySetIcon(const char* icon_path) {
    if (icon_path == nullptr || *icon_path == '\0') return ERR_STATE;
    const IconData ic = loadIcon(icon_path);
    DBusConnection* c = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        g_tray_has_pixmap = ic.hasPixmap;
        g_tray_pixmap = ic.pixels;
        g_tray_icon_w = ic.w;
        g_tray_icon_h = ic.h;
        g_tray_icon_name = ic.name;
        c = g_tray_conn;
        if (c != nullptr) dbus_connection_ref(c);
    }
    if (c != nullptr) {
        emitSimpleSignal(c, kSniPath, kSniIface, "NewIcon");
        trayConnUnref(c);
    }
    return OK;
}

int32_t traySetTooltip(const char* tooltip) {
    DBusConnection* c = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        g_tray_tooltip = tooltip != nullptr ? tooltip : "";
        c = g_tray_conn;
        if (c != nullptr) dbus_connection_ref(c);
    }
    if (c != nullptr) {
        emitSimpleSignal(c, kSniPath, kSniIface, "NewToolTip");
        trayConnUnref(c);
    }
    return OK;
}

int32_t traySetVisible(bool visible) {
    DBusConnection* c = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        g_tray_visible = visible;
        c = g_tray_conn;
        if (c != nullptr) dbus_connection_ref(c);
    }
    if (c != nullptr) {
        emitNewStatus(c, visible);
        trayConnUnref(c);
    }
    return OK;
}

int32_t traySetMenu(const AplTrayMenuItem* items, int32_t count) {
    if (count < 0 || (count > 0 && items == nullptr)) return ERR_STATE;
    DBusConnection* c = nullptr;
    uint32_t rev = 0;
    {
        std::lock_guard<std::mutex> lock(g_tray_mtx);
        g_tray_menu.clear();
        if (count > 0) {
            g_tray_menu.reserve(static_cast<size_t>(count));
            for (int32_t i = 0; i < count; i++) {
                TrayItem item;
                item.id = items[i].id;
                item.enabled = items[i].enabled != 0;
                item.checked = items[i].checked;
                const AplString& label = items[i].label;
                if (label.data != nullptr && label.len > 0) {
                    item.label.assign(label.data, label.len);
                }
                g_tray_menu.push_back(std::move(item));
            }
        }
        g_tray_revision++;
        rev = g_tray_revision;
        c = g_tray_conn;
        if (c != nullptr) dbus_connection_ref(c);
    }
    if (c != nullptr) {
        emitLayoutUpdated(c, rev);
        trayConnUnref(c);
    }
    return OK;
}

int32_t traySetMenuTrigger(int32_t trigger) {
    std::lock_guard<std::mutex> lock(g_tray_mtx);
    g_tray_trigger = trigger;
    return OK;
}

}  // namespace archoera
