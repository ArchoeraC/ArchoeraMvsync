// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// SystemWindow 契约：窗口状态探测（最小化/前台/最大化/全屏/隐藏）。
///
/// 事件由原生桥接（GTK 信号 / WndProc 子类化 / NSWindow 通知）探测并回传；
/// ABI v9 起 `flags` 是权威值，Dart 侧窗口「监听」全部走本契约，不再用
/// window_manager 的事件监听（window_manager 仅保留窗口控制写入，如
/// minimize/maximize/setFullScreen）。
///
/// 桥接未置能力位（纯 Wayland 等）时由工厂注入 [NoopSystemWindow]，调用方
/// 可回退 window_manager 监听（既有路径）。
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

/// 窗口状态事件订阅。
abstract interface class SystemWindow {
  /// 开/关事件订阅。返回是否成功生效（失败时调用方仅 debug 日志回落，
  /// 不 toast——后台优化类，见 facade §5 降级两档）。
  Future<bool> setEvents(bool on);

  /// 状态快照流（订阅即收到后续状态；能力缺失时空流）。
  Stream<SystemWindowState> get state;

  /// 最近一次已知状态（订阅方应立即应用一次，避免错过订阅前的快照）。
  SystemWindowState get current;

  /// 执行失败 / 后端断连上抛（UI toast 由消费方决定；本能力默认仅日志）。
  Stream<PlatformCapabilityFailure> get failures;

  /// 释放底层资源；幂等。
  Future<void> dispose();
}

/// 空实现：无事件（调用方回退 window_manager），静默降级。
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
  Stream<PlatformCapabilityFailure> get failures => const Stream.empty();

  @override
  Future<void> dispose() async {}
}
