// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:async';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/platform/platform_capabilities.dart';
import '../services/platform/system_window.dart';

/// 进程级窗口全屏状态（**唯一一处**注册原生窗口事件监听）。
///
/// ## 为什么必须集中管理（Windows 下整个应用卡死）
///
/// 旧的第三方窗口插件在**非窗口线程**调用时会先 `PostToThread`
/// 再 `wait_for(..., 5s)` **同步等待**窗口线程执行（见其原生层
/// `OnWindowThread`，Linux/macOS 不触发该分支，故只有 Windows 会中招）。
///
/// 播放页原实现有两条叠加路径：
/// 1. 每次 `initState` 都**同步**读 `isFullScreen` 做初始校准；
/// 2. **每个实例各自** `addListener` / `removeListener` 原生窗口监听。
///
/// 关闭后迅速重开时，新旧 `PlayerPage` 实例短暂重叠，上述同步跨线程调用会成倍
/// 叠加；一旦窗口线程正忙，UI 线程就被阻塞数秒——用户看到的就是「整个软件卡死」。
///
/// ## 现在的契约
///
/// - 全屏状态由**原生桥接**的窗口 flags（`fullscreen`）更新（ABI v9：GTK 信号 /
///   WndProc 子类化 / NSWindow 通知），Dart 不注册任何第三方窗口事件监听；
/// - 窗口控制（写入全屏）同样经 [SystemWindow]（`apl_window_*`），不再经过
///   第三方窗口插件；桥接不可用时写入静默失败，状态仍以事件为准；
/// - 进程内只订阅一次桥接窗口状态，随 provider 存活，页面反复挂载/卸载不再
///   增删原生监听；
/// - 读状态是纯内存操作（桥接缓存的快照），热路径没有任何跨线程原生调用；
/// - 唯一的原生写入是用户点「全屏」按钮（[toggle]），不在挂载路径上。
///
/// 初始值取 `false`：应用没有「启动即全屏」的偏好，且刻意不去读会阻塞的
/// 同步 getter；若启动时真有全屏事件，监听器会立即校正。
final windowFullScreenProvider =
    NotifierProvider<WindowFullScreenNotifier, bool>(
      WindowFullScreenNotifier.new,
    );

class WindowFullScreenNotifier extends Notifier<bool> {
  SystemWindow? _bridgeWindow;
  StreamSubscription<SystemWindowState>? _windowSub;
  bool _attaching = false;
  bool _knownFullscreen = false;
  bool _disposed = false;

  /// 单元测试（`flutter_tester`）里没有真实窗口：FFI 调用可能停在原生侧不
  /// 返回（Dart 侧超时都触发不了），挂载播放页的 widget 测试会直接卡死。
  /// 测试环境只保留纯内存状态（与仓库其它 FFI/烘焙路径同一约定）。
  static bool get _nativeWindowAvailable =>
      !Platform.environment.containsKey('FLUTTER_TEST');

  @override
  bool build() {
    final initial = _attach();
    ref.onDispose(() {
      _disposed = true;
      unawaited(_windowSub?.cancel());
      _windowSub = null;
      final bridge = _bridgeWindow;
      _bridgeWindow = null;
      if (bridge != null) unawaited(bridge.setEvents(false));
    });
    return initial;
  }

  /// 订阅一次原生桥接窗口状态。返回订阅前的初值（桥接尚未推送快照时为 false）。
  bool _attach() {
    if (!_nativeWindowAvailable) return false;
    if (_windowSub != null || _attaching) return _knownFullscreen;
    _attaching = true;
    try {
      final bridge = ref.read(platformCapabilitiesProvider).window;
      _bridgeWindow = bridge;
      unawaited(() async {
        try {
          final ok = await bridge.setEvents(true);
          if (_disposed) {
            // build 后立刻卸载：撤销刚建立的订阅，避免 provider 释放后仍占用单例。
            if (ok) unawaited(bridge.setEvents(false));
            return;
          }
          if (!ok) return;
          _windowSub = bridge.state.listen((s) {
            _knownFullscreen = s.fullscreen;
            if (s.fullscreen == state) return;
            state = s.fullscreen;
          });
        } finally {
          _attaching = false;
        }
      }());
      _knownFullscreen = bridge.current.fullscreen;
      return _knownFullscreen;
    } catch (_) {
      _attaching = false;
      return _knownFullscreen;
    }
  }

  /// 用户点「全屏」：先乐观置位（图标即时反馈），随后由窗口事件校准。
  ///
  /// 写入只发生在**用户主动点击**时，不在页面挂载 / 转场路径上，不参与
  /// 「快速开关」的叠加。
  void toggle() {
    final target = !state;
    state = target;
    _attach();
    final window = _bridgeWindow;
    if (window == null) return;
    // 原生写入失败：状态仍以事件为准，这里不回滚以免图标闪烁。
    window.setFullscreen(target);
  }
}
