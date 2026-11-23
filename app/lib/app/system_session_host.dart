// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 系统会话结束（关机 / 重启 / 注销）宿主。
///
/// 背景：窗口关闭请求（标题栏 × / 托盘「退出」）是**用户主动操作**，按
/// 「关闭应用时」偏好处理（后台常驻 / 退出 / 询问）。系统关机或注销则不同
/// ——此时用户已经在结束会话，应用再弹「关闭到后台还是退出」既打断关机，
/// 也会让系统等待超时。故这里单独订阅原生会话结束事件，直接走统一退出：
/// 保存播放现场 → flush 凭据队列 → 平台桥接收尾 → exit(0)。
///
/// 事件边界（见 `archoera_platform.h` 的 `APL_EVENT_SESSION_END`）：
/// - Linux：SIGTERM（systemd/logind 终止用户会话时投递）；
/// - Windows：WM_QUERYENDSESSION / WM_ENDSESSION；
/// - macOS：NSWorkspaceWillPowerOffNotification。
/// **休眠 / 待机 / 锁屏 / 熄屏不产生该事件**，仍由节能服务降频处理。
library;

import 'dart:async';
import 'dart:io' show exit;

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/log/log.dart';
import '../services/platform/platform_bindings.dart' show AplSessionEndEvent;
import '../services/platform/platform_capabilities.dart';
import 'app_quit.dart';

class SystemSessionHost extends ConsumerStatefulWidget {
  const SystemSessionHost({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<SystemSessionHost> createState() => _SystemSessionHostState();
}

class _SystemSessionHostState extends ConsumerState<SystemSessionHost> {
  StreamSubscription<AplSessionEndEvent>? _sub;

  /// 防止 WM_QUERYENDSESSION + WM_ENDSESSION 双事件重复进入退出流程。
  bool _quitting = false;

  @override
  void initState() {
    super.initState();
    _sub = ref
        .read(platformCapabilitiesProvider)
        .sessionEndEvents
        .listen((_) => unawaited(_onSessionEnd()));
  }

  Future<void> _onSessionEnd() async {
    if (_quitting) return;
    _quitting = true;
    Log.i('lifecycle', '收到系统关机/注销信号，保存状态后主动退出');
    try {
      await quitApplication(ref, reason: '系统关机/注销');
    } catch (e, st) {
      // 落盘/桥接收尾失败也不能把进程留在关机流程里：系统随后会 SIGKILL，
      // 这里主动结束，至少保证不再弹窗阻塞关机。
      Log.e('lifecycle', '会话结束退出失败，强制结束：$e\n$st');
      exit(0);
    }
  }

  @override
  void dispose() {
    unawaited(_sub?.cancel());
    _sub = null;
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
