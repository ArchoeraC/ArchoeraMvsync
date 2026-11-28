// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 窗口几何持久化判定：只有「普通窗口」才写偏好。
library;

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/platform/system_window.dart';
import 'package:archoera_mvsync/services/platform/window_geometry.dart';

void main() {
  const size = WindowSize(1280, 720);
  const pos = (x: 120, y: 90);

  test('普通窗口：尺寸 + 位置都写', () {
    expect(
      windowGeometryToPersist(
        size: size,
        position: pos,
        maximized: false,
        fullscreen: false,
      ),
      (width: 1280, height: 720, x: 120, y: 90),
    );
  });

  test('无位置（Wayland / 查询失败）：只写尺寸', () {
    expect(
      windowGeometryToPersist(
        size: size,
        position: null,
        maximized: false,
        fullscreen: false,
      ),
      (width: 1280, height: 720, x: null, y: null),
    );
  });

  test('最大化 / 全屏不写（避免下次启动先撑满再跳回）', () {
    for (final flags in [(true, false), (false, true), (true, true)]) {
      expect(
        windowGeometryToPersist(
          size: size,
          position: pos,
          maximized: flags.$1,
          fullscreen: flags.$2,
        ),
        isNull,
        reason: 'maximized=${flags.$1} fullscreen=${flags.$2}',
      );
    }
  });

  test('尺寸缺失 / 非正数不写', () {
    expect(
      windowGeometryToPersist(
        size: null,
        position: pos,
        maximized: false,
        fullscreen: false,
      ),
      isNull,
    );
    expect(
      windowGeometryToPersist(
        size: const WindowSize(0, 720),
        position: pos,
        maximized: false,
        fullscreen: false,
      ),
      isNull,
    );
  });
}
