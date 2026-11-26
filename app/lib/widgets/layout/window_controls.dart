// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 无边框窗口（borderless）下的窗口按钮与拖拽背景。
///
/// 对齐 SPlayer-Next 的 `WindowControls`：不使用系统标题栏，最小化 /
/// 最大化 / 关闭以圆形幽灵按钮并入页面顶栏，空白处整行可拖动窗口。
library;

import 'dart:async';
import 'dart:io' show Platform;

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:window_manager/window_manager.dart';

import '../../app/app_quit.dart';
import '../../l10n/l10n.dart';
import '../../services/platform/platform_capabilities.dart';
import '../../services/platform/system_window.dart';
import '../common/tray_integration.dart';
import '../player/s_controls.dart';

/// 铺满父级的「拖动窗口」手势层，位于内容**之下**。
///
/// 用 Stack 把 [DragToMoveArea] 放在内容底部：交互控件（按钮 / 输入框）
/// 命中的区域不会落到拖拽层，等价 SPlayer-Next 的 `-webkit-app-region`
/// `drag` + `no-drag` 组合；空白区域拖动即移动窗口，双击即最大化/还原。
class WindowDragArea extends StatelessWidget {
  const WindowDragArea({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Stack(
      fit: StackFit.expand,
      children: [
        DragToMoveArea(child: const SizedBox.expand()),
        child,
      ],
    );
  }
}

/// 顶栏内的窗口按钮组（最小化 / 最大化或还原 / 关闭）。
///
/// 仅在 [WindowDragArea] 之外的独立区域使用：按钮点击不应被拖拽层抢占。
class WindowControls extends ConsumerStatefulWidget {
  const WindowControls({super.key});

  @override
  ConsumerState<WindowControls> createState() => _WindowControlsState();
}

class _WindowControlsState extends ConsumerState<WindowControls> {
  Window? _window;
  ListenerId? _listenerId; // window_manager 回退路径
  SystemWindow? _bridgeWindow;
  StreamSubscription<SystemWindowState>? _windowSub;
  bool _maximized = false;

  @override
  void initState() {
    super.initState();
    if (Platform.environment.containsKey('FLUTTER_TEST')) return;
    try {
      _window = WindowManager.instance.getCurrent();
    } catch (_) {
      // 无窗口环境（单测 / 桥接缺失）：按钮静默禁用，不影响布局。
    }
    _attachWindowState();
  }

  /// 最大化状态优先走原生桥接（ABI v9 flags）；能力缺失时回退 WM 监听。
  void _attachWindowState() {
    final SystemWindow window;
    try {
      window = ref.read(platformCapabilitiesProvider).window;
    } catch (_) {
      return;
    }
    _bridgeWindow = window;
    unawaited(() async {
      final ok = await window.setEvents(true);
      if (!mounted) {
        if (ok) unawaited(window.setEvents(false));
        return;
      }
      if (!ok) {
        _attachWindowManagerFallback();
        return;
      }
      _maximized = window.current.maximized;
      _windowSub = window.state.listen((s) {
        if (!mounted || s.maximized == _maximized) return;
        setState(() => _maximized = s.maximized);
      });
    }());
  }

  void _attachWindowManagerFallback() {
    final window = _window;
    if (window == null || _listenerId != null) return;
    try {
      _maximized = window.isMaximized;
      _listenerId = WindowManager.instance.addListener(_onWindowEvent);
    } catch (_) {
      _listenerId = null;
    }
  }

  @override
  void dispose() {
    final id = _listenerId;
    if (id != null) {
      try {
        WindowManager.instance.removeListener(id);
      } catch (_) {}
    }
    unawaited(_windowSub?.cancel());
    final bridge = _bridgeWindow;
    if (bridge != null) unawaited(bridge.setEvents(false));
    super.dispose();
  }

  void _onWindowEvent(WindowEvent event) {
    if (event is! WindowMaximizedEvent && event is! WindowRestoredEvent) {
      return;
    }
    final maximized = _window?.isMaximized ?? false;
    if (!mounted || maximized == _maximized) return;
    setState(() => _maximized = maximized);
  }

  void _minimize() => _window?.minimize();

  void _toggleMaximize() {
    final window = _window;
    if (window == null) return;
    // 用事件同步的本地状态决定方向：Windows 上 `isMaximized` 是跨线程
    // 同步读取（window_manager 会 PostToThread + 最多等 5s），热路径不能用。
    if (_maximized) {
      window.unmaximize();
    } else {
      window.maximize();
    }
  }

  /// 关闭窗口：**不得**直接调 `window.close()`。
  ///
  /// window_manager 0.7（cnativeapi）的原生关闭路径会在 AppKit 窗口通知里回调
  /// 已释放的监听目标——macOS 关窗 / 终止时必现 SIGSEGV（Windows 同类崩溃）。
  /// 这里转交给 TrayIntegration 注册的策略入口（后台常驻 / 退出 / 询问），
  /// 与系统关闭请求走同一条路。
  void _close() {
    final handler = appWindowCloseHandler;
    if (handler != null) {
      unawaited(handler());
      return;
    }
    // 托盘尚未挂载（极早期 / 测试 / 桥接缺失）：退回统一退出路径，
    // 依旧不触碰原生 window.close()。
    unawaited(
      quitApplicationWith(ProviderScope.containerOf(context)),
    );
  }

  Widget _button({
    required String tooltip,
    required IconData icon,
    required VoidCallback onPressed,
  }) {
    return Tooltip(
      message: tooltip,
      child: SButton(
        label: tooltip,
        icon: icon,
        variant: SButtonVariant.ghost,
        circle: true,
        onPressed: onPressed,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        _button(
          tooltip: l10n.windowMinimize,
          icon: Icons.remove,
          onPressed: _minimize,
        ),
        const SizedBox(width: 2),
        _button(
          tooltip: _maximized ? l10n.windowRestore : l10n.windowMaximize,
          icon: _maximized ? Icons.filter_none : Icons.crop_square,
          onPressed: _toggleMaximize,
        ),
        const SizedBox(width: 2),
        _button(
          tooltip: l10n.windowClose,
          icon: Icons.close,
          onPressed: _close,
        ),
      ],
    );
  }
}
