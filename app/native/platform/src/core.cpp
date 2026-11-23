// ArchoeraMvsync 平台能力原生桥接
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "core.h"

#include <atomic>
#include <cstdio>
#include <mutex>

namespace archoera {
namespace {

std::mutex g_mutex;
bool g_initialized = false;
AplEventCallback g_callback = nullptr;
void* g_user_data = nullptr;

// 统一日志 sink：宿主注入后由各后端经 log() 调用；原子读写避免与分发竞争。
std::atomic<AplLogFn> g_log_sink{nullptr};

// 进程级事件槽：回调可能是异步 NativeCallable.listener，Dart 稍后读取指针，
// 若指向栈内存届时已失效。事件低速率，高频状态事件覆盖竞争可接受；但**一次性
// 事件（deep link）必须由消费方从 pending 缓冲补取**，不能只依赖本信号
// （Dart FfiSystemDeepLink 在任意平台事件后 drain）。
AplEvent g_event_slot{};

// 系统会话结束是「必须送达」的一次性事件：独立粘性槽位。Dart listener 异步
// 读取，若与窗口/熄屏等高频事件共用 g_event_slot，可能在读取前被覆盖 →
// 关机时错过退出。独立槽位只被后续 SESSION_END 重写（语义等价，仍为 1）。
AplEvent g_session_end_slot{};

}  // namespace

bool isInitialized() {
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_initialized;
}

void setInitialized(bool v) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_initialized = v;
}

void setEventCallback(AplEventCallback cb, void* user_data) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_callback = cb;
    g_user_data = user_data;
}

void setLogSink(AplLogFn fn) { g_log_sink.store(fn, std::memory_order_release); }

AplLogFn logSink() { return g_log_sink.load(std::memory_order_acquire); }

void log(int level, const char* tag, const char* message) {
    AplLogFn fn = g_log_sink.load(std::memory_order_acquire);
    if (fn != nullptr) {
        fn(level, tag, message);
        return;
    }
    // 未注入：stderr 兜底（避免静默丢失桥接诊断）。
    std::fprintf(stderr, "[%s] %s\n", tag != nullptr ? tag : "platform",
                 message != nullptr ? message : "");
}

void dispatch(const AplEvent& event) {
    AplEventCallback cb;
    void* user;
    AplEvent* slot = &g_event_slot;
    if (event.type == APL_EVENT_SESSION_END) slot = &g_session_end_slot;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        *slot = event;
        cb = g_callback;
        user = g_user_data;
    }
    if (cb == nullptr) return;
    cb(slot, user);
}

AplEvent makeCommand(int32_t command) {
    AplEvent e{};
    e.type = APL_EVENT_MEDIA_COMMAND;
    e.u.command = command;
    return e;
}

AplEvent makeSeek(int64_t rel_ms, int64_t abs_ms) {
    AplEvent e{};
    e.type = APL_EVENT_MEDIA_SEEK;
    e.u.seek.rel_ms = rel_ms;
    e.u.seek.abs_ms = abs_ms;
    return e;
}

AplEvent makeScreenState(bool active) {
    AplEvent e{};
    e.type = APL_EVENT_SCREEN_STATE;
    e.u.active = active ? 1 : 0;
    return e;
}

AplEvent makeWindowState(bool minimized, bool focused) {
    AplEvent e{};
    e.type = APL_EVENT_WINDOW_STATE;
    e.u.window.minimized = minimized ? 1 : 0;
    e.u.window.focused = focused ? 1 : 0;
    return e;
}

AplEvent makeBackendLost(bool lost) {
    AplEvent e{};
    e.type = APL_EVENT_BACKEND_STATE;
    e.u.backend_lost = lost ? 1 : 0;
    return e;
}

AplEvent makeSystemAccent(int32_t r, int32_t g, int32_t b) {
    AplEvent e{};
    e.type = APL_EVENT_SYSTEM_ACCENT;
    e.u.accent.r = r;
    e.u.accent.g = g;
    e.u.accent.b = b;
    return e;
}

AplEvent makeSystemTheme(bool dark) {
    AplEvent e{};
    e.type = APL_EVENT_SYSTEM_THEME;
    e.u.theme.dark = dark ? 1 : 0;
    return e;
}

AplEvent makeDeepLink() {
    AplEvent e{};
    e.type = APL_EVENT_DEEP_LINK;
    e.u.deep_link = 1;
    return e;
}

AplEvent makeTrayClick(int32_t type) {
    AplEvent e{};
    e.type = type;
    e.u.tray = 1;
    return e;
}

AplEvent makeTrayMenuCommand(int32_t id) {
    AplEvent e{};
    e.type = APL_EVENT_TRAY_MENU_COMMAND;
    e.u.tray_command.id = id;
    return e;
}

AplEvent makeSessionEnd() {
    AplEvent e{};
    e.type = APL_EVENT_SESSION_END;
    e.u.session_end = 1;
    return e;
}

}  // namespace archoera
