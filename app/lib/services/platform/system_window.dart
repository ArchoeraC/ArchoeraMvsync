// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// SystemWindow 契约：窗口状态探测与窗口控制（尺寸/显隐/最大化/全屏/装饰/
/// 拖拽/缩放/关闭请求）。
///
/// 事件由原生桥接（GTK 信号 / WndProc 子类化 / NSWindow 通知）探测并回传，
/// 控制同样经 `apl_window_*` 落到原生窗口；Dart 侧窗口「监听」与「控制」全部
/// 走本契约（它既不监听也不控制窗口）。
///
/// ABI v9 起 `flags` 是权威状态值；ABI v10 起新增窗口控制 API 与关闭请求事件
/// （能力位 `APL_CAP_WINDOW_CONTROL`）；ABI v11 起补 `size`（`apl_window_get_size`）。
///
/// 桥接未置能力位（纯 Wayland 等）时由工厂注入 [NoopSystemWindow]，调用方
/// 可回退既有路径（Noop 全部控制返回 false、事件为空流）。
///
/// 见 docs/platform-capability-facade.md §2.1 / docs/platform-native-bridge.md §3.4。
library;

import 'dart:async';

import 'platform_failure.dart';

/// 窗口状态快照（桥接按事件即时推送，无轮询）。
class SystemWindowState {
  const SystemWindowState({
    required this.minimized,
    required this.focused,
    this.maximized = false,
    this.fullscreen = false,
    this.hidden = false,
  });

  final bool minimized;
  final bool focused;
  final bool maximized;
  final bool fullscreen;

  /// 窗口不可见（托盘隐藏 / 从任务栏隐藏）。与 [minimized] 独立：
  /// 最小化仍算可见，隐藏才是用户看不到窗口。
  final bool hidden;

  @override
  bool operator ==(Object other) =>
      other is SystemWindowState &&
      other.minimized == minimized &&
      other.focused == focused &&
      other.maximized == maximized &&
      other.fullscreen == fullscreen &&
      other.hidden == hidden;

  @override
  int get hashCode =>
      Object.hash(minimized, focused, maximized, fullscreen, hidden);
}

/// 关闭请求来源：普通关窗（系统关闭按钮 / Alt+F4）或明确退出（⌘Q / 菜单退出）。
enum WindowCloseReason { close, quit }

/// 装饰模式（对齐 `APL_WINDOW_DECOR_*`）。
enum WindowDecorMode {
  /// 系统原生标题栏 + 边框。
  system,

  /// 隐藏标题栏，保留可缩放边框；系统窗口按钮能保留则保留，否则由自绘 UI 接管。
  caption,

  /// 无边框，内容铺满（窗口按钮由自绘 UI 接管）。
  borderless,
}

/// 缩放热区（顺序对齐 `APL_WINDOW_EDGE_*`）。
enum WindowResizeEdge {
  topLeft,
  top,
  topRight,
  left,
  right,
  bottomLeft,
  bottom,
  bottomRight,
}

/// 一次性关闭请求（桥接只上报，由 Dart 决定隐藏到托盘还是退出）。
class WindowCloseRequest {
  const WindowCloseRequest(this.reason);

  final WindowCloseReason reason;
}

/// 窗口逻辑尺寸（DIP；ABI v11 `apl_window_get_size`）。
///
/// 与 [SystemWindow.setSize] 同一口径：Windows 为含边框外框、Linux/macOS 为内容区。
class WindowSize {
  const WindowSize(this.width, this.height);

  final int width;
  final int height;

  @override
  bool operator ==(Object other) =>
      other is WindowSize && other.width == width && other.height == height;

  @override
  int get hashCode => Object.hash(width, height);

  @override
  String toString() => 'WindowSize($width×$height)';
}

/// 窗口状态与控制契约。
abstract interface class SystemWindow {
  /// 开/关事件订阅。返回是否成功生效（失败时调用方仅 debug 日志回落，
  /// 不 toast——后台优化类，见 facade §5 降级两档）。
  ///
  /// 关闭后平台**不再拦截系统关闭请求**（关闭事件随之停发），这是预期行为。
  Future<bool> setEvents(bool on);

  /// 状态快照流（订阅即收到后续状态；能力缺失时空流）。
  Stream<SystemWindowState> get state;

  /// 最近一次已知状态（订阅方应立即应用一次，避免错过订阅前的快照）。
  SystemWindowState get current;

  /// 用户发起关闭时的请求流（桥接不自行关窗；订阅前需 [setEvents]）。
  Stream<WindowCloseRequest> get closeRequests;

  /// 执行失败 / 后端断连上抛（UI toast 由消费方决定；本能力默认仅日志）。
  Stream<PlatformCapabilityFailure> get failures;

  /// 显示/隐藏窗口（逻辑像素无关）。返回是否成功。
  bool setVisible(bool visible);

  /// 最小化窗口（还原用 [restore]）。
  bool minimize();

  /// 从最小化还原（不改变最大化状态）。
  bool restore();

  bool setMaximized(bool on);

  bool setFullscreen(bool on);

  /// 设置窗口尺寸（逻辑像素）。
  bool setSize(int width, int height);

  /// 设置最小尺寸（逻辑像素）。
  bool setMinSize(int width, int height);

  bool center();

  /// 置前/激活窗口。
  bool activate();

  bool setAlwaysOnTop(bool on);

  bool setResizable(bool on);

  bool setDecor(WindowDecorMode mode);

  /// 当前窗口位置（逻辑像素；桥接不可用/查询失败返回 null）。
  ({int x, int y})? get position;

  bool setPosition(int x, int y);

  /// 当前窗口尺寸（ABI v11；与 [setSize] 同口径，失败 / 不可用返回 null）。
  WindowSize? get size;

  /// 原生标题栏拖拽（鼠标按下时调用；自绘标题栏 / 无边框窗口）。
  bool startDrag();

  /// 原生边缘缩放（鼠标按下时调用）。
  bool startResize(WindowResizeEdge edge);

  /// 请求关闭：原生只投递 [WindowCloseRequest]，不自行关窗 / 退出。
  bool requestClose({WindowCloseReason reason = WindowCloseReason.close});

  /// 释放底层资源；幂等。
  Future<void> dispose();
}

/// 空实现：无事件、所有控制失败，静默降级。
class NoopSystemWindow implements SystemWindow {
  static final NoopSystemWindow instance = NoopSystemWindow._();

  NoopSystemWindow._();

  @override
  Future<bool> setEvents(bool on) async => false;

  @override
  Stream<SystemWindowState> get state => const Stream.empty();

  @override
  SystemWindowState get current =>
      const SystemWindowState(minimized: false, focused: true);

  @override
  Stream<WindowCloseRequest> get closeRequests => const Stream.empty();

  @override
  Stream<PlatformCapabilityFailure> get failures => const Stream.empty();

  @override
  bool setVisible(bool visible) => false;

  @override
  bool minimize() => false;

  @override
  bool restore() => false;

  @override
  bool setMaximized(bool on) => false;

  @override
  bool setFullscreen(bool on) => false;

  @override
  bool setSize(int width, int height) => false;

  @override
  bool setMinSize(int width, int height) => false;

  @override
  bool center() => false;

  @override
  bool activate() => false;

  @override
  bool setAlwaysOnTop(bool on) => false;

  @override
  bool setResizable(bool on) => false;

  @override
  bool setDecor(WindowDecorMode mode) => false;

  @override
  ({int x, int y})? get position => null;

  @override
  bool setPosition(int x, int y) => false;

  @override
  WindowSize? get size => null;

  @override
  bool startDrag() => false;

  @override
  bool startResize(WindowResizeEdge edge) => false;

  @override
  bool requestClose({WindowCloseReason reason = WindowCloseReason.close}) =>
      false;

  @override
  Future<void> dispose() async {}
}
