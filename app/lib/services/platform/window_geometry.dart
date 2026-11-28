// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 窗口几何持久化：判定 + 落盘。
///
/// 尺寸 / 位置来自 ABI v11 的 `apl_window_get_size` / `apl_window_get_position`；
/// 只有「普通窗口」状态才写偏好：最大化 / 全屏时拿到的是屏幕尺寸，写进偏好会让
/// 下次启动先撑满屏幕，再回到普通尺寸就会跳一下。
///
/// 写入时机有三处：统一退出（`quitApplicationWith`）、关闭到托盘（`_hideToTray`；
/// 之后进程可能被系统杀掉，几何不该丢）。启动恢复在 `main()`（先 `setMinSize`，
/// 再 `setSize` + `setPosition`）。
library;

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../log/log.dart';
import '../../stores/app_prefs.dart';
import 'platform_capabilities.dart';
import 'system_window.dart';

/// 应写入偏好的窗口几何；返回 null 表示本次不写（无尺寸 / 最大化 / 全屏）。
///
/// [position] 为空（桥接不可用 / Wayland 不支持客户端定位）时只存尺寸，
/// 启动时仍按尺寸恢复、位置交给窗口管理器。
({int width, int height, int? x, int? y})? windowGeometryToPersist({
  required WindowSize? size,
  required ({int x, int y})? position,
  required bool maximized,
  required bool fullscreen,
}) {
  if (size == null || maximized || fullscreen) return null;
  if (size.width <= 0 || size.height <= 0) return null;
  return (
    width: size.width,
    height: size.height,
    x: position?.x,
    y: position?.y,
  );
}

/// 读当前窗口几何并落盘；桥接不可用 / 查询失败 / 最大化 / 全屏一律静默跳过。
void saveWindowGeometry(ProviderContainer container) {
  try {
    final window = container.read(platformCapabilitiesProvider).window;
    final geometry = windowGeometryToPersist(
      size: window.size,
      position: window.position,
      maximized: window.current.maximized,
      fullscreen: window.current.fullscreen,
    );
    if (geometry == null) return;
    container
        .read(appPrefsProvider.notifier)
        .setWindowGeometry(
          width: geometry.width,
          height: geometry.height,
          x: geometry.x,
          y: geometry.y,
        );
  } catch (err) {
    Log.w('lifecycle', '窗口几何保存失败（忽略）: $err');
  }
}
