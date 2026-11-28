// ArchoeraMvsync 平台能力原生桥接
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

//! archoera_platform —— C ABI 导出根（apl_*，契约见 include/archoera_platform.h）。
//! Dart（app/lib/services/platform/）仅依赖本 ABI；平台探测、转发、事件回传
//! 全部在 backend_* 内完成。零 JSON、零子进程、同进程动态链接。

#include "archoera_platform.h"

#include "backend.h"
#include "core.h"

#include <cstring>

namespace {
// v11：新增 apl_window_get_size（窗口逻辑尺寸读取，与 set_size 同口径）；
// 纯加法，事件 / 能力位 / 既有符号不变。
// v10：新增窗口控制 API（apl_window_set_size / set_min_size / center / show /
// hide / minimize / restore / set_maximized / set_fullscreen / set_always_on_top /
// set_resizable / set_decor / get_position / set_position / start_drag /
// start_resize / request_close / stop_events）与能力位 APL_CAP_WINDOW_CONTROL；
// 新增 APL_EVENT_WINDOW_CLOSE_REQUEST（u.window_close.reason：0=关闭 1=退出）。
// 关闭请求改由桥接上报、Dart 决策（含 macOS ⌘Q / terminate: 钩子）。
// v9：APL_EVENT_WINDOW_STATE 增加 flags（最小化/前台/最大化/全屏/隐藏），
// `u.window` 由 8 字节扩到 12 字节（仍在 16 字节 union 内，AplEvent 布局不变）；
// 窗口状态改为三端原生事件驱动，Dart 不再用 window_manager 监听。
// v8：新增 APL_EVENT_SESSION_END / APL_CAP_SESSION_END（系统关机/注销事件；
// 向后兼容的加法，但事件常量与能力位属于同一契约，版本号同步抬升）。
// v7：移除 apl_open_url / APL_CAP_OPEN_URL（破坏性 ABI 变更，2026-11-09）。
constexpr int32_t kAbiVersion = 11;

// SEC-31：reveal 路径输入契约——仅接受应用内已解析的**绝对本地路径**。
// 拒绝空串、URI（`scheme://`，含 `file://`/`http://`）与相对路径；C 串天然在 NUL 截断。
bool isAbsoluteLocalPath(const char* p) {
#ifdef _WIN32
    // `X:\` / `X:/` 盘符绝对路径，或 `\\server\share` UNC。
    if (p[0] == '\\' && p[1] == '\\') return true;
    const bool alpha = (p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z');
    return alpha && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
#else
    return p[0] == '/';
#endif
}

// 安全文件原语：拒绝 Windows 设备命名空间（`\\.\pipe\…`、`\\.\C:` 等），
// 避免把命名管道/设备当普通文件打开（打开命名管道可能阻塞）。
bool isDeviceNamespace(const char* p) {
#ifdef _WIN32
    return p[0] == '\\' && p[1] == '\\' && p[2] == '.' &&
           (p[3] == '\\' || p[3] == '\0');
#else
    (void)p;
    return false;
#endif
}

// 安全文件原语通用输入契约：绝对本地路径 + 非 URI + 非设备命名空间。
bool isSafeLocalPath(const char* path) {
    if (path == nullptr || *path == '\0') return false;
    if (std::strstr(path, "://") != nullptr) return false;
    if (isDeviceNamespace(path)) return false;
    return isAbsoluteLocalPath(path);
}
}  // namespace

extern "C" {

int32_t apl_abi_version(void) { return kAbiVersion; }

int32_t apl_init(void) {
    archoera::setInitialized(true);
    const int32_t rc = archoera::init();
    if (rc != archoera::OK) {
        archoera::setInitialized(false);
        return rc;
    }
    return archoera::OK;
}

int32_t apl_shutdown(void) {
    if (!archoera::isInitialized()) return archoera::OK;
    const int32_t rc = archoera::shutdown();
    archoera::setInitialized(false);
    return rc;
}
int32_t apl_harden_process(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::hardenProcess();
}

int32_t apl_process_elevation(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::processElevation();
}

uint32_t apl_capabilities(void) { return archoera::caps(); }

int32_t apl_set_event_callback(AplEventCallback cb, void* user_data) {
    archoera::setEventCallback(cb, user_data);
    return archoera::OK;
}

void apl_set_log_sink(AplLogFn fn) { archoera::setLogSink(fn); }

int32_t apl_power_set_sleep_inhibit(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::powerSetSleepInhibit(on);
}

int32_t apl_power_set_screen_events(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::powerSetScreenEvents(on);
}

int32_t apl_window_set_events(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowSetEvents(on);
}

// 窗口控制（ABI v10）。参数校验留在出口（尺寸/边缘/装饰模式越界即拒绝），
// 具体平台换算由 backend_* 负责；request_close 只投递事件，绝不自行关窗。
int32_t apl_window_set_size(int32_t w, int32_t h) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (w <= 0 || h <= 0) return archoera::ERR_STATE;
    return archoera::windowSetSize(w, h);
}

int32_t apl_window_set_min_size(int32_t w, int32_t h) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (w < 0 || h < 0) return archoera::ERR_STATE;
    return archoera::windowSetMinSize(w, h);
}

int32_t apl_window_center(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowCenter();
}

int32_t apl_window_show(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowShow();
}

int32_t apl_window_hide(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowHide();
}

int32_t apl_window_minimize(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowMinimize();
}

int32_t apl_window_restore(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowRestore();
}

int32_t apl_window_set_maximized(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowSetMaximized(on);
}

int32_t apl_window_set_fullscreen(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowSetFullscreen(on);
}

int32_t apl_window_set_always_on_top(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowSetAlwaysOnTop(on);
}

int32_t apl_window_set_resizable(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowSetResizable(on);
}

int32_t apl_window_set_decor(int32_t mode) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (mode < APL_WINDOW_DECOR_SYSTEM || mode > APL_WINDOW_DECOR_BORDERLESS) {
        return archoera::ERR_STATE;
    }
    return archoera::windowSetDecor(mode);
}

int32_t apl_window_get_position(int32_t* x, int32_t* y) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (x == nullptr || y == nullptr) return archoera::ERR_STATE;
    return archoera::windowGetPosition(x, y);
}

int32_t apl_window_get_size(int32_t* w, int32_t* h) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (w == nullptr || h == nullptr) return archoera::ERR_STATE;
    return archoera::windowGetSize(w, h);
}

int32_t apl_window_set_position(int32_t x, int32_t y) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowSetPosition(x, y);
}

int32_t apl_window_start_drag(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowStartDrag();
}

int32_t apl_window_start_resize(int32_t edge) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (edge < APL_WINDOW_EDGE_TOP_LEFT || edge > APL_WINDOW_EDGE_BOTTOM_RIGHT) {
        return archoera::ERR_STATE;
    }
    return archoera::windowStartResize(edge);
}

int32_t apl_window_request_close(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowRequestClose();
}

int32_t apl_window_stop_events(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowStopEvents();
}

int32_t apl_media_set_track(const AplTrackMeta* track) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::mediaSetTrack(track);
}

int32_t apl_media_set_playback(int32_t state, int64_t position_ms, double speed,
                               double volume, int32_t loop, int32_t shuffle) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::mediaSetPlayback(state, position_ms, speed, volume, loop,
                                      shuffle);
}

int32_t apl_media_set_window(int64_t window) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::mediaSetWindow(window);
}

int32_t apl_instance_acquire(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::appInstanceAcquire();
}

int32_t apl_protocol_register(const char* scheme) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (scheme == nullptr || *scheme == '\0') return archoera::ERR_STATE;
    return archoera::protocolRegister(scheme);
}

int32_t apl_protocol_unregister(const char* scheme) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (scheme == nullptr || *scheme == '\0') return archoera::ERR_STATE;
    return archoera::protocolUnregister(scheme);
}

int32_t apl_deep_link_take(AplString* out) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (out == nullptr) return archoera::ERR_STATE;
    return archoera::deepLinkTake(out);
}

int32_t apl_deep_link_forward(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::deepLinkForward();
}

int32_t apl_window_activate(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::windowActivate();
}

int32_t apl_system_accent(int32_t* r, int32_t* g, int32_t* b) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (!archoera::systemAccent(r, g, b)) return archoera::ERR_BACKEND;
    return archoera::OK;
}

int32_t apl_system_accent_set_events(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::systemAccentSetEvents(on != 0);
}

int32_t apl_system_theme_set_events(int32_t on) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::systemThemeSetEvents(on != 0);
}

int32_t apl_notify(const char* title, const char* body) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (title == nullptr) return archoera::ERR_BACKEND;
    return archoera::notify(title, body != nullptr ? body : "");
}

int32_t apl_reveal_path(const char* path) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (path == nullptr || *path == '\0') return archoera::ERR_STATE;
    // SEC-31：仅接受应用内已解析的绝对本地路径；拒绝 URI 与相对路径。
    if (std::strstr(path, "://") != nullptr) return archoera::ERR_STATE;
    if (!isAbsoluteLocalPath(path)) return archoera::ERR_STATE;
    return archoera::revealPath(path);
}

int32_t apl_file_read(const char* path, int32_t allow_symlink, int64_t max_bytes,
                      uint8_t* out, size_t* inout_len) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (inout_len == nullptr || max_bytes <= 0) return archoera::ERR_STATE;
    // 安全文件契约：绝对本地路径 + 非 URI + 非设备命名空间。
    if (!isSafeLocalPath(path)) return archoera::ERR_STATE;
    return archoera::fileRead(path, allow_symlink != 0, max_bytes, out, inout_len);
}

int32_t apl_file_write_atomic(const char* path, const uint8_t* data, size_t len) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (len > 0 && data == nullptr) return archoera::ERR_STATE;
    if (!isSafeLocalPath(path)) return archoera::ERR_STATE;
    return archoera::fileWriteAtomic(path, data, len);
}

int32_t apl_tray_create(const char* icon_path) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (icon_path == nullptr || *icon_path == '\0') return archoera::ERR_STATE;
    return archoera::trayCreate(icon_path);
}

int32_t apl_tray_destroy(void) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::trayDestroy();
}

int32_t apl_tray_set_icon(const char* icon_path) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (icon_path == nullptr || *icon_path == '\0') return archoera::ERR_STATE;
    return archoera::traySetIcon(icon_path);
}

int32_t apl_tray_set_tooltip(const char* tooltip) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::traySetTooltip(tooltip != nullptr ? tooltip : "");
}

int32_t apl_tray_set_visible(int32_t visible) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::traySetVisible(visible != 0);
}

int32_t apl_tray_set_menu(const AplTrayMenuItem* items, int32_t count) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    if (count < 0 || (count > 0 && items == nullptr)) return archoera::ERR_STATE;
    return archoera::traySetMenu(items, count);
}

int32_t apl_tray_set_menu_trigger(int32_t trigger) {
    if (!archoera::isInitialized()) return archoera::ERR_STATE;
    return archoera::traySetMenuTrigger(trigger);
}

}  // extern "C"
