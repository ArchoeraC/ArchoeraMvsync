// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/platform/system_window.dart';
import 'package:archoera_mvsync/services/platform/window_resize.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';

void main() {
  const start = WindowRect(x: 100, y: 80, width: 1000, height: 700);

  group('resizeWindowRect', () {
    test('right / bottom 只改尺寸', () {
      expect(
        resizeWindowRect(
          edge: WindowResizeEdge.right,
          start: start,
          dx: 120,
          dy: 0,
          minWidth: 640,
          minHeight: 420,
        ),
        const WindowRect(x: 100, y: 80, width: 1120, height: 700),
      );
      expect(
        resizeWindowRect(
          edge: WindowResizeEdge.bottom,
          start: start,
          dx: 0,
          dy: -300,
          minWidth: 640,
          minHeight: 420,
        ),
        const WindowRect(x: 100, y: 80, width: 1000, height: 420),
      );
    });

    test('left / top 同步移动原点', () {
      expect(
        resizeWindowRect(
          edge: WindowResizeEdge.left,
          start: start,
          dx: 50,
          dy: 0,
          minWidth: 640,
          minHeight: 420,
        ),
        const WindowRect(x: 150, y: 80, width: 950, height: 700),
      );
      expect(
        resizeWindowRect(
          edge: WindowResizeEdge.top,
          start: start,
          dx: 0,
          dy: 20,
          minWidth: 640,
          minHeight: 420,
        ),
        const WindowRect(x: 100, y: 100, width: 1000, height: 680),
      );
    });

    test('触底时对侧坐标保持不动', () {
      final out = resizeWindowRect(
        edge: WindowResizeEdge.left,
        start: start,
        dx: 5000,
        dy: 0,
        minWidth: 640,
        minHeight: 420,
      );
      expect(out.width, 640);
      expect(out.x, start.x + start.width - 640);
      expect(out.x + out.width, start.x + start.width);
    });

    test('角同时作用两轴', () {
      expect(
        resizeWindowRect(
          edge: WindowResizeEdge.topLeft,
          start: start,
          dx: 100,
          dy: 50,
          minWidth: 640,
          minHeight: 420,
        ),
        const WindowRect(x: 200, y: 130, width: 900, height: 650),
      );
      expect(
        resizeWindowRect(
          edge: WindowResizeEdge.bottomRight,
          start: start,
          dx: 40,
          dy: 60,
          minWidth: 640,
          minHeight: 420,
        ),
        const WindowRect(x: 100, y: 80, width: 1040, height: 760),
      );
    });
  });

  group('窗口几何偏好', () {
    test('往返 + 缺省位置', () {
      final prefs = AppPrefs().copyWithWindowGeometry(
        width: 1280,
        height: 720,
        x: 40,
        y: 60,
      );
      expect(prefs.windowGeometry, (width: 1280, height: 720, x: 40, y: 60));
      final noPos = AppPrefs().copyWithWindowGeometry(width: 900, height: 600);
      expect(noPos.windowGeometry, (width: 900, height: 600, x: null, y: null));
    });

    test('非法 / 越界值忽略', () {
      final tooSmall = AppPrefs(
        initialData: {
          windowGeometryKey: {'width': 100, 'height': 100},
        },
      );
      expect(tooSmall.windowGeometry, isNull);
      final wrongType = AppPrefs(
        initialData: {
          windowGeometryKey: {'width': '1280', 'height': 720},
        },
      );
      expect(wrongType.windowGeometry, isNull);
    });
  });
}
