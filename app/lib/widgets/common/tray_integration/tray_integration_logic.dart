// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

part of '../tray_integration.dart';

/// 托盘菜单项 id（>0 可点击；0 为分隔符）。
const int _menuShow = 1;
const int _menuPlayPause = 2;
const int _menuPrevious = 3;
const int _menuNext = 4;
const int _menuQuit = 5;

extension _TrayIntegrationLogic on _TrayIntegrationState {
  Future<void> _setup() async {
    try {
      await _initTray();
      _trayReady = true;
    } catch (e) {
      Log.w('tray', '初始化失败，降级为正常关闭退出: $e');
      _trayReady = false;
      _disposeTray();
      return;
    }
    // 关闭拦截是托盘常驻的附加能力：窗口关闭请求经原生桥接上报（桥接已
    // 拦截系统关闭，不会自行退出），失败只降级为直接关闭，不影响托盘本身
    // （例如无窗口环境 / 原生库不可用）。
    try {
      final window = ref.read(platformCapabilitiesProvider).window;
      _window = window;
      if (!await window.setEvents(true)) {
        Log.w('tray', '窗口状态订阅不可用（关闭请求将无法拦截）');
      }
      _closeSub = window.closeRequests.listen((request) {
        unawaited(_onCloseRequest(request));
      });
    } catch (e) {
      Log.w('tray', '窗口关闭拦截不可用，关闭将直接退出: $e');
    }
  }

  Future<void> _initTray() async {
    final tray = ref.read(platformCapabilitiesProvider).tray;
    _tray = tray;

    // 图标资源 → 临时文件（桥接按文件路径加载；Windows 用 .ico）。
    final iconAsset = Platform.isWindows
        ? 'assets/icons/tray.ico'
        : 'assets/icons/tray.png';
    final ext = Platform.isWindows ? 'ico' : 'png';
    final data = await rootBundle.load(iconAsset);
    final path =
        '${Directory.systemTemp.path}/archoera-tray-$pid-'
        '${DateTime.now().microsecondsSinceEpoch}.$ext';
    await File(path).writeAsBytes(
      data.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes),
      flush: true,
    );
    _iconTempPath = path;

    if (tray.create(path) != 0) {
      throw StateError('无法创建系统托盘图标');
    }
    tray.setTooltip('ArchoeraMvsync');
    // 菜单唤出方式：Linux 的 StatusNotifierItem 由面板在左键时展开菜单；
    // Windows / macOS 用右键唤出，左键留给「显示主窗口」。
    tray.setMenuTrigger(Platform.isLinux);
    _traySubs.add(tray.clicks.listen((_) => unawaited(_showWindow())));
    _traySubs.add(tray.doubleClicks.listen((_) => unawaited(_showWindow())));
    _traySubs.add(tray.menuCommands.listen(_onMenuCommand));
    _trayFailures = tray.failures.listen((f) => Log.w('tray', f.toString()));
    tray.setMenu(_buildMenuItems());
    tray.setVisible(true);
  }

  void _onMenuCommand(int id) {
    switch (id) {
      case _menuShow:
        unawaited(_showWindow());
      case _menuPlayPause:
        ref.read(playbackProvider.notifier).toggle();
      case _menuPrevious:
        unawaited(ref.read(playbackProvider.notifier).playPrevious());
      case _menuNext:
        unawaited(ref.read(playbackProvider.notifier).playNext());
      case _menuQuit:
        unawaited(_quit());
    }
  }

  List<SystemTrayMenuItem> _buildMenuItems() {
    final l10n = ref.read(l10nProvider);
    return <SystemTrayMenuItem>[
      SystemTrayMenuItem(id: _menuShow, label: l10n.trayShow),
      const SystemTrayMenuItem(id: 0),
      SystemTrayMenuItem(id: _menuPlayPause, label: l10n.trayPlayPause),
      SystemTrayMenuItem(id: _menuPrevious, label: l10n.trayPrevious),
      SystemTrayMenuItem(id: _menuNext, label: l10n.trayNext),
      const SystemTrayMenuItem(id: 0),
      SystemTrayMenuItem(id: _menuQuit, label: l10n.trayQuit),
    ];
  }

  void _rebuildMenu() {
    if (!_trayReady) return;
    _tray?.setMenu(_buildMenuItems());
  }

  void _disposeTray() {
    for (final s in _traySubs) {
      unawaited(s.cancel());
    }
    _traySubs.clear();
    unawaited(_trayFailures?.cancel());
    _trayFailures = null;
    _tray?.destroy();
    _tray = null;
    final path = _iconTempPath;
    if (path != null) {
      try {
        File(path).deleteSync();
      } catch (_) {
        // 临时文件清理失败可忽略。
      }
      _iconTempPath = null;
    }
  }

  Future<void> _showWindow() async {
    final window = _window;
    if (window == null) return;
    window.setVisible(true);
    window.activate();
    // 桥接在部分合成器上不保证派发 show 事件：显式通知节能服务恢复前台档。
    ref.read(powerSaverProvider).setHidden(false);
  }

  /// 隐藏到托盘（显式通知节能服务降到 0 帧档；桥接同时会推送 hidden flag）。
  ///
  /// 隐藏失败（桥接无控制能力）时保持窗口可见并**不**切后台档：窗口仍在
  /// 屏幕上却停止渲染会表现成「卡死」，不如维持前台正常播放。
  void _hideToTray() {
    final window = _window;
    if (window == null) return;
    if (!window.setVisible(false)) {
      Log.w('tray', '窗口隐藏失败（桥接无控制能力），保持窗口可见');
      return;
    }
    ref.read(powerSaverProvider).setHidden(true);
  }

  /// 窗口关闭请求：桥接已拦截系统关闭，这里只按「关闭应用时」偏好处理。
  ///
  /// `reason=quit`（macOS ⌘Q / 应用菜单 Quit）语义就是退出，不再询问「后台常驻」；
  /// 普通关窗（关闭按钮 / Alt+F4）仍走统一策略。
  Future<void> _onCloseRequest(WindowCloseRequest request) async {
    if (_handlingClose) return;
    _handlingClose = true;
    try {
      if (request.reason == WindowCloseReason.quit) {
        await _quit();
        return;
      }
      await _handleWindowClose();
    } finally {
      _handlingClose = false;
    }
  }

  Future<void> _quit() async {
    await quitApplication(ref);
  }

  Future<void> _handleWindowClose() async {
    final behavior = ref.read(appPrefsProvider).closeBehavior;
    switch (behavior) {
      case 'quit':
        await _quit();
      case 'background':
        _hideToTray();
      default:
        await _askCloseBehavior();
    }
  }

  Future<void> _askCloseBehavior() async {
    _closeRemember = false;
    final notifier = ref.read(playbackProvider.notifier);
    await notifier.duckVolume();
    try {
      final nav = rootNavigatorKey.currentContext;
      if (nav == null) return;
      final l10n = ref.read(l10nProvider);
      if (!nav.mounted) return;
      final choice = await SDialog.show<(String, bool)>(
        nav,
        title: l10n.commonCloseConfirmTitle,
        description: l10n.commonCloseConfirmMessage,
        child: StatefulBuilder(
          builder: (dialogContext, setDialogState) => Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _closeOption(
                dialogContext,
                l10n.settingsCloseBehaviorBackground,
                EtaIcons.headphoneOutline,
                'background',
              ),
              const SizedBox(height: 8),
              _closeOption(
                dialogContext,
                l10n.settingsCloseBehaviorQuit,
                EtaIcons.powerOutline,
                'quit',
              ),
              const SizedBox(height: 12),
              InkWell(
                borderRadius: BorderRadius.circular(6),
                onTap: () =>
                    setDialogState(() => _closeRemember = !_closeRemember),
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 2,
                    vertical: 2,
                  ),
                  child: Row(
                    children: [
                      Checkbox(
                        value: _closeRemember,
                        onChanged: (v) =>
                            setDialogState(() => _closeRemember = v ?? false),
                      ),
                      const SizedBox(width: 4),
                      Expanded(
                        child: Text(
                          l10n.commonCloseConfirmRemember,
                          style: TextStyle(
                            fontSize: 12.5,
                            color: Theme.of(dialogContext)
                                .colorScheme
                                .onSurfaceVariant,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      );
      if (choice == null) return;
      final (behavior, remember) = choice;
      if (remember) {
        ref.read(appPrefsProvider.notifier).setCloseBehavior(behavior);
      }
      if (behavior == 'quit') {
        await _quit();
      } else {
        _hideToTray();
      }
    } finally {
      await notifier.restoreVolume();
    }
  }

  Widget _closeOption(
    BuildContext dialogContext,
    String label,
    IconData icon,
    String value,
  ) {
    final scheme = Theme.of(dialogContext).colorScheme;
    return Material(
      color: scheme.onSurface.withValues(alpha: 0.06),
      borderRadius: BorderRadius.circular(10),
      child: InkWell(
        borderRadius: BorderRadius.circular(10),
        onTap: () => Navigator.of(dialogContext).pop((value, _closeRemember)),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          child: Row(
            children: [
              Icon(icon, size: 18, color: scheme.onSurfaceVariant),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  label,
                  style: TextStyle(
                    fontSize: 13.5,
                    fontWeight: FontWeight.w600,
                    color: scheme.onSurface,
                  ),
                ),
              ),
              Icon(
                EtaIcons.rightSmall,
                size: 18,
                color: scheme.onSurfaceVariant.withValues(alpha: 0.5),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildTrayIntegration(BuildContext context) {
    ref.listen(localeProvider, (prev, next) {
      if (prev != next) _rebuildMenu();
    });
    return widget.child;
  }
}
