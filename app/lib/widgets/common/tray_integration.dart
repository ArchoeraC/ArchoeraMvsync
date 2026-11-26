// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:async';
import 'dart:io' show Directory, File, Platform, pid;

import 'package:flutter/services.dart' show rootBundle;
import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:archoera_mvsync/eta/icon/eta_icons.dart';

import '../../services/log/log.dart';
import '../../services/platform/platform_capabilities.dart';
import '../../services/platform/platform_failure.dart';
import '../../services/platform/system_tray.dart';
import '../../services/platform/system_window.dart';
import '../../services/playback/playback_notifier.dart';
import '../../services/power/power_saver.dart' show powerSaverProvider;
import '../../stores/app_prefs.dart';
import '../../l10n/l10n.dart';
import '../../app/app_quit.dart';
import '../../app/router.dart';
import '../dialogs/s_dialog.dart';

part 'tray_integration/tray_integration_logic.dart';

/// 自绘标题栏「关闭」按钮的统一入口（由 [TrayIntegration] 注册）。
///
/// 关闭按钮不直接触碰原生窗口：系统关闭请求由原生桥接拦截后经
/// [SystemWindow.closeRequests] 上报，本回调复用同一条策略（后台常驻 /
/// 直接退出 / 询问），保证两种关闭方式行为一致。
///
/// 未注册（托盘尚未挂载 / 单元测试）时为 null，调用方应回退到统一退出路径。
Future<void> Function()? appWindowCloseHandler;

/// 后台常驻：系统托盘集成（自研平台桥接 `apl_tray_*`）。
///
/// 2026-10-02 由 Flutter 插件 `tray_manager` 迁至自研桥接：`tray_manager` 0.6+
/// 引入的第三方原生依赖在 MSVC 下有告警（`strcpy` 弃用 C4996 /
/// `size_t→unsigned long` 收窄 C4267），且按仓库约定「平台/系统能力一律在
/// app/native/platform 实现、Dart 只经 apl_* 调用」，故改由桥接承载托盘并移除该依赖。
///
/// 关闭窗口按「关闭应用时」偏好处理（后台播放 / 直接退出；默认每次询问，
/// 弹确认框含记忆勾选）。托盘菜单：显示主窗口 / 播放暂停 / 上一首 / 下一首 /
/// 退出。托盘初始化失败时降级为正常关闭退出。
class TrayIntegration extends ConsumerStatefulWidget {
  const TrayIntegration({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<TrayIntegration> createState() => _TrayIntegrationState();
}

class _TrayIntegrationState extends ConsumerState<TrayIntegration> {
  /// 托盘是否就绪（决定关闭窗口是否隐藏到托盘）。
  bool _trayReady = false;

  /// 窗口控制 / 事件句柄（原生桥接；无窗口环境为 null）。
  SystemWindow? _window;

  /// 关闭请求订阅（桥接已拦截系统关闭，这里只负责按偏好处理）。
  StreamSubscription<WindowCloseRequest>? _closeSub;

  /// 关闭确认弹窗中「记住我的选择」复选框状态（每次弹窗前重置）。
  bool _closeRemember = false;

  /// 关闭请求处理中（避免连点关闭叠加多个确认框）。
  bool _handlingClose = false;

  /// 托盘能力实现（进程级单例；本组件只 create/destroy，不 dispose 服务）。
  SystemTray? _tray;

  /// 托盘事件订阅与失败订阅。
  final List<StreamSubscription<void>> _traySubs = <StreamSubscription<void>>[];
  StreamSubscription<PlatformCapabilityFailure>? _trayFailures;

  /// 落到临时文件的图标路径（销毁时清理）。
  String? _iconTempPath;

  /// 注册到 [appWindowCloseHandler] 的闭包（dispose 时按同一实例注销）。
  Future<void> Function()? _registeredCloseHandler;

  @override
  void initState() {
    super.initState();
    _registeredCloseHandler = _onCloseButton;
    appWindowCloseHandler = _registeredCloseHandler;
    _setup();
  }

  /// 自绘标题栏关闭按钮：与系统关闭请求共用 `_handleWindowClose` 的策略。
  Future<void> _onCloseButton() async {
    if (_handlingClose) return;
    _handlingClose = true;
    try {
      await _handleWindowClose();
    } finally {
      _handlingClose = false;
    }
  }

  @override
  void dispose() {
    if (identical(appWindowCloseHandler, _registeredCloseHandler)) {
      appWindowCloseHandler = null;
    }
    unawaited(_closeSub?.cancel());
    _closeSub = null;
    final window = _window;
    _window = null;
    if (window != null) unawaited(window.setEvents(false));
    _disposeTray();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => _buildTrayIntegration(context);
}
