// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// SystemWindow 的 FFI 实现（能力位图含 WINDOW_STATE 位时由工厂启用；
/// 纯 Wayland 等场景位图不含该位 → Noop 静默降级）。
///
/// 状态事件与窗口控制均走原生 `apl_window_*`，不再经第三方窗口插件。
library;

import 'dart:async';
import 'dart:ffi';

import 'package:ffi/ffi.dart';

import 'platform_bindings.dart';
import 'platform_failure.dart';
import 'system_window.dart';

class FfiSystemWindow implements SystemWindow {
  FfiSystemWindow(this._b) {
    _winSub = _b.windowEvents.listen((e) {
      final snap = SystemWindowState(
        minimized: e.minimized,
        focused: e.focused,
        maximized: e.maximized,
        fullscreen: e.fullscreen,
        hidden: e.hidden,
      );
      _state = snap;
      _stateCtrl.add(snap);
    });
    _closeSub = _b.windowCloseEvents.listen((e) {
      _closeCtrl.add(
        WindowCloseRequest(
          e.reason == aplWindowQuit
              ? WindowCloseReason.quit
              : WindowCloseReason.close,
        ),
      );
    });
    _backendSub = _b.backendEvents.listen((e) {
      if (e.lost) {
        _failures.add(
          PlatformCapabilityFailure(
            capability: 'window',
            code: aplErrBackend,
            message: aplErrorMessage(aplErrBackend),
            lost: true,
          ),
        );
      }
    });
  }

  final PlatformBindings _b;

  late final StreamSubscription<AplWindowEvent> _winSub;
  late final StreamSubscription<AplWindowCloseEvent> _closeSub;
  late final StreamSubscription<AplBackendEvent> _backendSub;

  final _stateCtrl = StreamController<SystemWindowState>.broadcast();
  final _closeCtrl = StreamController<WindowCloseRequest>.broadcast();
  final _failures = StreamController<PlatformCapabilityFailure>.broadcast();

  SystemWindowState _state = const SystemWindowState(
    minimized: false,
    focused: true,
  );

  /// 订阅引用计数：power_saver / 窗口按钮 / 全屏状态共用同一单例，任一方
  /// 取消订阅不能把全局原生监听关掉（否则其余消费方静默失效）。
  int _eventRefs = 0;
  bool _eventsOn = false;

  @override
  Future<bool> setEvents(bool on) async {
    if (on) {
      _eventRefs++;
      if (!_eventsOn) {
        _eventsOn = _b.setWindowEvents(true) == aplOk;
        if (!_eventsOn) {
          _eventRefs--;
          return false;
        }
      }
      return true;
    }
    if (_eventRefs > 0) _eventRefs--;
    if (_eventRefs == 0 && _eventsOn) {
      _eventsOn = false;
      _b.setWindowEvents(false);
    }
    return true;
  }

  @override
  Stream<SystemWindowState> get state => _stateCtrl.stream;

  /// 最近一次已知状态（新订阅者可用作初值；桥接事件到达前为前台默认值）。
  @override
  SystemWindowState get current => _state;

  @override
  Stream<WindowCloseRequest> get closeRequests => _closeCtrl.stream;

  @override
  Stream<PlatformCapabilityFailure> get failures => _failures.stream;

  // ── 窗口控制 ──

  @override
  bool setVisible(bool visible) =>
      (visible ? _b.windowShow() : _b.windowHide()) == aplOk;

  @override
  bool minimize() => _b.windowMinimize() == aplOk;

  @override
  bool restore() => _b.windowRestore() == aplOk;

  @override
  bool setMaximized(bool on) => _b.windowSetMaximized(on) == aplOk;

  @override
  bool setFullscreen(bool on) => _b.windowSetFullscreen(on) == aplOk;

  @override
  bool setSize(int width, int height) =>
      _b.windowSetSize(width, height) == aplOk;

  @override
  bool setMinSize(int width, int height) =>
      _b.windowSetMinSize(width, height) == aplOk;

  @override
  bool center() => _b.windowCenter() == aplOk;

  @override
  bool activate() => _b.activateWindow() == aplOk;

  @override
  bool setAlwaysOnTop(bool on) => _b.windowSetAlwaysOnTop(on) == aplOk;

  @override
  bool setResizable(bool on) => _b.windowSetResizable(on) == aplOk;

  @override
  bool setDecor(WindowDecorMode mode) {
    final code = switch (mode) {
      WindowDecorMode.system => aplWindowDecorSystem,
      WindowDecorMode.caption => aplWindowDecorCaption,
      WindowDecorMode.borderless => aplWindowDecorBorderless,
    };
    return _b.windowSetDecor(code) == aplOk;
  }

  @override
  ({int x, int y})? get position {
    final x = calloc<Int32>();
    final y = calloc<Int32>();
    try {
      if (_b.windowGetPosition(x, y) != aplOk) return null;
      return (x: x.value, y: y.value);
    } finally {
      calloc.free(x);
      calloc.free(y);
    }
  }

  @override
  bool setPosition(int x, int y) => _b.windowSetPosition(x, y) == aplOk;

  @override
  bool startDrag() => _b.windowStartDrag() == aplOk;

  @override
  bool startResize(WindowResizeEdge edge) {
    final code = switch (edge) {
      WindowResizeEdge.topLeft => aplWindowEdgeTopLeft,
      WindowResizeEdge.top => aplWindowEdgeTop,
      WindowResizeEdge.topRight => aplWindowEdgeTopRight,
      WindowResizeEdge.left => aplWindowEdgeLeft,
      WindowResizeEdge.right => aplWindowEdgeRight,
      WindowResizeEdge.bottomLeft => aplWindowEdgeBottomLeft,
      WindowResizeEdge.bottom => aplWindowEdgeBottom,
      WindowResizeEdge.bottomRight => aplWindowEdgeBottomRight,
    };
    return _b.windowStartResize(code) == aplOk;
  }

  @override
  bool requestClose({WindowCloseReason reason = WindowCloseReason.close}) =>
      // 原生 ABI 无 reason 参数：桥接自行投递关闭请求，Dart 侧只表达意图。
      _b.windowRequestClose() == aplOk;

  @override
  Future<void> dispose() async {
    _eventRefs = 0;
    if (_eventsOn) {
      _eventsOn = false;
      _b.setWindowEvents(false);
    }
    await _winSub.cancel();
    await _closeSub.cancel();
    await _backendSub.cancel();
    await _stateCtrl.close();
    await _closeCtrl.close();
    await _failures.close();
  }
}
