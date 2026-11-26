// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// SystemWindow 的 FFI 实现（能力位图含 WINDOW_STATE 位时由工厂启用；
/// 纯 Wayland 等场景位图不含该位 → Noop 回退 window_manager）。
library;

import 'dart:async';

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
  late final StreamSubscription<AplBackendEvent> _backendSub;

  final _stateCtrl = StreamController<SystemWindowState>.broadcast();
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
  Stream<PlatformCapabilityFailure> get failures => _failures.stream;

  @override
  Future<void> dispose() async {
    _eventRefs = 0;
    if (_eventsOn) {
      _eventsOn = false;
      _b.setWindowEvents(false);
    }
    await _winSub.cancel();
    await _backendSub.cancel();
    await _stateCtrl.close();
    await _failures.close();
  }
}
