// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 窗口按钮、自绘标题条与拖拽/缩放热区。
///
/// 全部经 [SystemWindow]（原生桥接）驱动：拖拽 / 双击最大化 / 边缘缩放 /
/// 最小化 / 最大化 / 关闭都不再经过第三方窗口插件。
///
/// 对齐 SPlayer-Next 的 `WindowControls`：不使用系统标题栏，最小化 /
/// 最大化 / 关闭以圆形幽灵按钮并入页面顶栏，空白处整行可拖动窗口。
library;

import 'dart:async';
import 'dart:io' show Platform;

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/app_quit.dart';
import '../../l10n/l10n.dart';
import '../../services/platform/platform_capabilities.dart';
import '../../services/platform/system_window.dart';
import '../common/tray_integration.dart';
import '../player/s_controls.dart';

/// 自绘标题条高度（逻辑像素），与旧第三方窗口插件的 `kWindowCaptionHeight` 一致。
const double kWindowCaptionHeight = 32;

/// 铺满父级的「拖动窗口」手势层，位于内容**之下**。
///
/// 用 Stack 把 [WindowMoveArea] 放在内容底部：交互控件（按钮 / 输入框）
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
        const WindowMoveArea(child: SizedBox.expand()),
        child,
      ],
    );
  }
}

/// 拖动窗口的手势层（原生标题栏拖拽）。
///
/// 按下即调用 [SystemWindow.startDrag]（Wayland 等合成器不允许客户端移动
/// 窗口时静默无效果）；双击在最大化 / 还原之间切换。
class WindowMoveArea extends StatelessWidget {
  const WindowMoveArea({super.key, required this.child});

  final Widget child;

  void _startDrag() {
    PlatformCapabilities.instance().window.startDrag();
  }

  void _toggleMaximize() {
    final window = PlatformCapabilities.instance().window;
    window.setMaximized(!window.current.maximized);
  }

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onPanStart: (_) => _startDrag(),
      onDoubleTap: _toggleMaximize,
      child: child,
    );
  }
}

/// 自绘标题条（32px）：左侧拖动 / 双击最大化区 + 右侧窗口按钮。
///
/// 用于 `caption` 模式（系统边框仍在、标题栏被隐藏）。[title] 为空时只留
/// 空白拖动区，不显示文字。
class WindowCaptionBar extends StatelessWidget {
  const WindowCaptionBar({
    super.key,
    required this.brightness,
    required this.backgroundColor,
    this.title,
  });

  final Brightness brightness;
  final Color backgroundColor;
  final String? title;

  @override
  Widget build(BuildContext context) {
    final title = this.title;
    final titleColor = brightness == Brightness.dark
        ? Colors.white.withValues(alpha: 0.85)
        : Colors.black.withValues(alpha: 0.75);
    return ColoredBox(
      color: backgroundColor,
      child: SizedBox(
        height: kWindowCaptionHeight,
        child: Row(
          children: [
            Expanded(
              child: WindowMoveArea(
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 12),
                  child: Align(
                    alignment: Alignment.centerLeft,
                    child: title == null
                        ? const SizedBox.expand()
                        : Text(
                            title,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              fontSize: 12.5,
                              fontWeight: FontWeight.w500,
                              color: titleColor,
                            ),
                          ),
                  ),
                ),
              ),
            ),
            const Padding(
              padding: EdgeInsets.only(right: 6),
              child: WindowControls(),
            ),
          ],
        ),
      ),
    );
  }
}

/// 无边框窗口的边缘缩放热区（8 条边 / 角）。
///
/// [enableResizeEdges] 为 null 时检查全部边缘；[resizeEdgeMargin] 用于热区
/// 内缩（例如避开标题条）。按下即调用 [SystemWindow.startResize]。
class WindowResizeArea extends StatelessWidget {
  const WindowResizeArea({
    super.key,
    required this.child,
    this.enableResizeEdges,
    this.resizeEdgeSize = 8,
    this.resizeEdgeMargin,
  });

  final Widget child;

  /// 需要启用的边 / 角；null 表示全部启用。
  final Set<WindowResizeEdge>? enableResizeEdges;

  /// 热区厚度（逻辑像素）。
  final double resizeEdgeSize;

  /// 热区相对窗口边缘的内缩量；null 表示贴边。
  final EdgeInsets? resizeEdgeMargin;

  bool _enabled(WindowResizeEdge edge) =>
      enableResizeEdges?.contains(edge) ?? true;

  void _startResize(WindowResizeEdge edge) {
    PlatformCapabilities.instance().window.startResize(edge);
  }

  Widget _handle(WindowResizeEdge edge, MouseCursor cursor) {
    return MouseRegion(
      cursor: cursor,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onPanStart: (_) => _startResize(edge),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final margin = resizeEdgeMargin ?? EdgeInsets.zero;
    final s = resizeEdgeSize;
    final left = margin.left;
    final top = margin.top;
    final right = margin.right;
    final bottom = margin.bottom;
    return Stack(
      fit: StackFit.expand,
      children: [
        child,
        if (_enabled(WindowResizeEdge.topLeft))
          Positioned(
            left: left,
            top: top,
            width: s,
            height: s,
            child: _handle(
              WindowResizeEdge.topLeft,
              SystemMouseCursors.resizeUpLeft,
            ),
          ),
        if (_enabled(WindowResizeEdge.topRight))
          Positioned(
            right: right,
            top: top,
            width: s,
            height: s,
            child: _handle(
              WindowResizeEdge.topRight,
              SystemMouseCursors.resizeUpRight,
            ),
          ),
        if (_enabled(WindowResizeEdge.bottomLeft))
          Positioned(
            left: left,
            bottom: bottom,
            width: s,
            height: s,
            child: _handle(
              WindowResizeEdge.bottomLeft,
              SystemMouseCursors.resizeDownLeft,
            ),
          ),
        if (_enabled(WindowResizeEdge.bottomRight))
          Positioned(
            right: right,
            bottom: bottom,
            width: s,
            height: s,
            child: _handle(
              WindowResizeEdge.bottomRight,
              SystemMouseCursors.resizeDownRight,
            ),
          ),
        if (_enabled(WindowResizeEdge.top))
          Positioned(
            left: left + s,
            right: right + s,
            top: top,
            height: s,
            child: _handle(WindowResizeEdge.top, SystemMouseCursors.resizeUp),
          ),
        if (_enabled(WindowResizeEdge.bottom))
          Positioned(
            left: left + s,
            right: right + s,
            bottom: bottom,
            height: s,
            child: _handle(
              WindowResizeEdge.bottom,
              SystemMouseCursors.resizeDown,
            ),
          ),
        if (_enabled(WindowResizeEdge.left))
          Positioned(
            left: left,
            top: top + s,
            bottom: bottom + s,
            width: s,
            child: _handle(
              WindowResizeEdge.left,
              SystemMouseCursors.resizeLeft,
            ),
          ),
        if (_enabled(WindowResizeEdge.right))
          Positioned(
            right: right,
            top: top + s,
            bottom: bottom + s,
            width: s,
            child: _handle(
              WindowResizeEdge.right,
              SystemMouseCursors.resizeRight,
            ),
          ),
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
  SystemWindow? _window;
  StreamSubscription<SystemWindowState>? _windowSub;
  bool _maximized = false;

  @override
  void initState() {
    super.initState();
    if (Platform.environment.containsKey('FLUTTER_TEST')) return;
    try {
      final window = ref.read(platformCapabilitiesProvider).window;
      _window = window;
      _maximized = window.current.maximized;
      unawaited(_attachWindowState(window));
    } catch (_) {
      // 无窗口环境（单测 / 桥接缺失）：按钮静默禁用，不影响布局。
    }
  }

  /// 最大化状态只走原生桥接（ABI v9 flags）；能力缺失时保持默认外观。
  Future<void> _attachWindowState(SystemWindow window) async {
    final ok = await window.setEvents(true);
    if (!mounted) {
      // 首帧前卸载：撤销刚建立的订阅，避免占用全局单例。
      if (ok) unawaited(window.setEvents(false));
      return;
    }
    if (!ok) return;
    _windowSub = window.state.listen((s) {
      if (!mounted || s.maximized == _maximized) return;
      setState(() => _maximized = s.maximized);
    });
  }

  @override
  void dispose() {
    unawaited(_windowSub?.cancel());
    final window = _window;
    if (window != null) unawaited(window.setEvents(false));
    super.dispose();
  }

  void _minimize() => _window?.minimize();

  void _toggleMaximize() {
    // 用事件同步的本地状态决定方向；`current` 是桥接缓存的纯内存快照，
    // 不是跨线程同步读取。
    _window?.setMaximized(!_maximized);
  }

  /// 关闭窗口：走统一关闭策略（后台常驻 / 退出 / 询问）。
  ///
  /// 原生关闭请求由桥接拦截后经 [SystemWindow.closeRequests] 上报，这里
  /// 不直接触碰原生窗口；托盘已注册 [appWindowCloseHandler] 时复用其策略。
  void _close() {
    final handler = appWindowCloseHandler;
    if (handler != null) {
      unawaited(handler());
      return;
    }
    // 托盘尚未挂载（极早期 / 测试 / 桥接缺失）：退回统一退出路径。
    unawaited(quitApplicationWith(ProviderScope.containerOf(context)));
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
