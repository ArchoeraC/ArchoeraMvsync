// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 新一代四层歌词墙的回归测试：
/// 时间轴（预滚/间奏/逐字提前）、几何（中心/可见窗口）、运动（单一滚动
/// 偏移 + 刚性行距）与渲染档位切换。
library;

import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/gestures.dart'
    show PointerDeviceKind, PointerScrollEvent;
import 'package:flutter/rendering.dart' show RenderRepaintBoundary;
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

import 'package:archoera_mvsync/services/lyrics/lyric_line.dart';
import 'package:archoera_mvsync/widgets/player/lyrics_v7/lyrics_physics_wall.dart';
import 'package:archoera_mvsync/widgets/player/lyrics_wall_next/lyrics_wall_next.dart';
import 'package:archoera_mvsync/widgets/player/lyrics_wall_next/wall_geometry.dart';
import 'package:archoera_mvsync/widgets/player/lyrics_wall_next/wall_motion.dart';
import 'package:archoera_mvsync/widgets/player/lyrics_wall_next/wall_timeline.dart';
import 'package:archoera_mvsync/utils/spring.dart';

List<LyricGroup> buildGroups(
  int count, {
  int stepMs = 1000,
  int durationMs = 1000,
}) => [
  for (var i = 0; i < count; i++)
    LyricGroup(
      original: LyricLine(timeMs: i * stepMs, text: '第 $i 行'),
      endMs: i * stepMs + durationMs,
    ),
];

Widget buildWall(
  List<LyricGroup> groups,
  int pos, {
  bool playing = false,
  double width = 400,
  double height = 500,
  WallBlurQuality blurQuality = WallBlurQuality.auto,
  ValueChanged<int>? onSeek,
}) => MaterialApp(
  home: Scaffold(
    body: SizedBox(
      width: width,
      height: height,
      child: LyricsWallNext(
        groups: groups,
        positionMs: pos,
        playing: playing,
        blurQuality: blurQuality,
        onSeek: onSeek ?? (_) {},
      ),
    ),
  ),
);

Future<void> settle(WidgetTester tester, {int frames = 90}) async {
  for (var i = 0; i < frames; i++) {
    await tester.pump(const Duration(milliseconds: 16));
  }
}

void main() {
  group('WallTimeline', () {
    test('严格覆盖与背景行优先级', () {
      final timeline = WallTimeline.build([
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '主行'),
          endMs: 1000,
        ),
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '背景'),
          endMs: 1000,
          isBG: true,
        ),
      ]);
      expect(timeline.activeAt(500), 0);
      expect(timeline.activeAt(1500), -1);
    });

    test('仅背景行覆盖时接管高亮', () {
      final timeline = WallTimeline.build([
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '背景'),
          endMs: 1000,
          isBG: true,
        ),
      ]);
      expect(timeline.activeAt(500), 0);
    });

    test('预滚在行间隙前提前 600ms 起滚', () {
      final timeline = WallTimeline.build([
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '前'),
          endMs: 1000,
        ),
        const LyricGroup(
          original: LyricLine(timeMs: 3000, text: '后'),
          endMs: 4000,
        ),
      ]);
      expect(timeline.lines[1].scrollStartMs, 2400);
      expect(timeline.scrollAt(2500), 1);
      expect(timeline.activeAt(2500), -1);
    });

    test('长空隙识别为间奏并停在空隙前一行', () {
      final timeline = WallTimeline.build([
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '前'),
          endMs: 1000,
        ),
        const LyricGroup(
          original: LyricLine(timeMs: 12000, text: '后'),
          endMs: 13000,
        ),
      ]);
      final idx = timeline.interludeAt(5000);
      expect(idx, 0);
      expect(timeline.interludes[idx].anchorIndex, 0);
      expect(timeline.anchorAt(pos: 5000, previous: 0, seek: false), 0);
    });

    test('逐字行在下一行开始前 500ms 提前高亮', () {
      final timeline = WallTimeline.build([
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '前'),
          endMs: 1000,
        ),
        const LyricGroup(
          original: LyricLine(timeMs: 1000, text: '逐字'),
          endMs: 2000,
          fragments: [
            LyricFragment(text: '逐', startMs: 0, durationMs: 500),
            LyricFragment(text: '字', startMs: 500, durationMs: 500),
          ],
        ),
      ]);
      expect(timeline.highlightAt(600, wordSweep: false), 0);
      expect(timeline.highlightAt(600, wordSweep: true), 1);
    });
  });

  group('WallGeometry', () {
    test('中心按半高与行间隙累计', () {
      final groups = buildGroups(3);
      final g = WallGeometry(
        groups: groups,
        style: const WallGeometryStyle(fontSize: 18, maxWidth: 300),
      );
      final gap = 18 * 1.0;
      expect(g.centers[0], closeTo(g.heights[0] / 2, 0.01));
      expect(
        g.centers[1],
        closeTo(g.centers[0] + g.heights[0] / 2 + gap + g.heights[1] / 2, 0.01),
      );
    });

    test('可见窗口覆盖锚点附近', () {
      final g = WallGeometry(
        groups: buildGroups(30),
        style: const WallGeometryStyle(fontSize: 18, maxWidth: 300),
      );
      final offset = g.targetOffset(15, 500, 0.5);
      final (s, e) = g.visibleRange(
        offset: offset,
        viewHeight: 500,
        margin: 180,
      );
      expect(s, lessThan(15));
      expect(e, greaterThan(15));
      expect(e - s, lessThan(30));
    });
  });

  group('WallMotion', () {
    test('单一弹簧收敛到目标且速度连续', () {
      final motion = WallMotion();
      motion.hardSet(0);
      motion.setTarget(
        120,
        const SpringParams(stiffness: 200, damping: 22, mass: 1),
      );
      var peak = 0.0;
      for (var i = 0; i < 240; i++) {
        motion.update(1 / 60);
        peak = math.max(peak, motion.offset);
      }
      expect(motion.offset, closeTo(120, 0.5));
      // 轻微过冲允许，但不能「冲出去半行」。
      expect(peak, lessThan(120 * 1.05));
    });

    test('新进入窗口的行吸附到目标过渡态', () {
      final v = WallVisuals();
      v.ensureRange(from: 3, to: 6, active: 4, blurTarget: (i) => 1.0);
      expect(v.fadeAt(4), 1);
      expect(v.fadeAt(3), 0);
      expect(v.fadeAt(5), 0);
      expect(v.blurAt(5), 1);
    });
  });

  group('LyricsWallNext', () {
    testWidgets('整墙刚性滚动：任意行间距恒等于几何间距', (tester) async {
      final groups = buildGroups(30);
      await tester.pumpWidget(buildWall(groups, 10000));
      await settle(tester);
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugActive(), 10);
      expect(state.debugAnchor(), 10);
      final y = state.debugY();
      final c = state.debugCenters();
      for (var i = 1; i < y.length; i++) {
        expect(y[i] - y[i - 1], closeTo(c[i] - c[i - 1], 1e-6));
      }
      final target = 500 * 0.5 - c[10];
      expect(state.debugOffset(), closeTo(target, 1.5));
      // painter 必须读同一个滚动偏移，否则换行后整墙按旧偏移绘制（空白）。
      expect(state.debugFrameOffset(), closeTo(state.debugOffset(), 1e-6));
    });

    testWidgets('换行后画面仍有歌词（painter 使用实时滚动偏移）', (tester) async {
      final key = GlobalKey();
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: SizedBox(
              width: 400,
              height: 500,
              child: RepaintBoundary(
                key: key,
                child: LyricsWallNext(
                  groups: buildGroups(30),
                  positionMs: 29000,
                  blurQuality: WallBlurQuality.off,
                  onSeek: (_) {},
                ),
              ),
            ),
          ),
        ),
      );
      await settle(tester);
      final boundary =
          key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final image = await tester.runAsync(boundary.toImage);
      if (image == null) fail('截图失败');
      final data = await tester.runAsync(
        () => image.toByteData(format: ui.ImageByteFormat.rawRgba),
      );
      image.dispose();
      if (data == null) fail('读取像素失败');
      final bytes = data.buffer.asUint8List();
      var painted = 0;
      for (var i = 3; i < bytes.length; i += 4) {
        if (bytes[i] > 8) painted++;
      }
      expect(painted, greaterThan(0), reason: '换行后歌词不应整片空白');
    });

    testWidgets('seek 后锚点/高亮直接落到新行', (tester) async {
      final groups = buildGroups(30);
      await tester.pumpWidget(buildWall(groups, 1000));
      await settle(tester, frames: 30);
      await tester.pumpWidget(buildWall(groups, 25000));
      await tester.pump(const Duration(milliseconds: 16));
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugActive(), 25);
      expect(state.debugAnchor(), 25);
      await settle(tester);
      final c = state.debugCenters();
      expect(state.debugOffset(), closeTo(250 - c[25], 1.5));
    });

    testWidgets('播放推进时行距不塌陷', (tester) async {
      final groups = buildGroups(40, stepMs: 400, durationMs: 400);
      var pos = 0;
      Widget wall() => buildWall(groups, pos, playing: true);
      await tester.pumpWidget(wall());
      for (var i = 0; i < 80; i++) {
        pos += 50;
        await tester.pumpWidget(wall());
        await tester.pump(const Duration(milliseconds: 16));
        final state = tester.state<LyricsWallNextState>(
          find.byType(LyricsWallNext),
        );
        final y = state.debugY();
        final c = state.debugCenters();
        for (var k = 1; k < math.min(y.length, 12); k++) {
          expect(y[k] - y[k - 1], closeTo(c[k] - c[k - 1], 1e-6));
        }
      }
    });

    testWidgets('悬停时整层失焦强度平滑归零', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(20), 5000));
      await settle(tester);
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugPanelBlur(), greaterThan(0.9));

      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);
      await gesture.moveTo(tester.getCenter(find.byType(LyricsWallNext)));
      await settle(tester, frames: 45);
      expect(state.debugPanelBlur(), lessThan(0.1));
    });

    testWidgets('自动档连续掉帧后关闭失焦', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(20), 5000, playing: true));
      await tester.pump(const Duration(milliseconds: 16));
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugBlurMode(), WallBlurMode.panel);
      for (var i = 0; i < 48; i++) {
        state.debugNoteFrame(rasterMs: 20, uiMs: 3);
      }
      await tester.pump(const Duration(milliseconds: 16));
      expect(state.debugBlurDegraded(), isTrue);
      expect(state.debugBlurMode(), WallBlurMode.off);
    });

    testWidgets('拖拽浏览偏移有界且不破坏行距', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(30), 10000));
      await settle(tester);
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      final center = tester.getCenter(find.byType(LyricsWallNext));
      final gesture = await tester.startGesture(center);
      await gesture.moveBy(const Offset(0, -120));
      await tester.pump(const Duration(milliseconds: 16));
      final (min, max) = state.debugUserBounds();
      expect(state.debugUser(), greaterThanOrEqualTo(min));
      expect(state.debugUser(), lessThanOrEqualTo(max));
      expect(state.debugUserScrolling(), isTrue);
      final y = state.debugY();
      final c = state.debugCenters();
      for (var i = 1; i < math.min(y.length, 10); i++) {
        expect(y[i] - y[i - 1], closeTo(c[i] - c[i - 1], 1e-6));
      }
      await gesture.up();
    });

    testWidgets('滚轮浏览改变整墙偏移且保持浏览态', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(30), 10000));
      await settle(tester);
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      final before = state.debugOffset();
      final center = tester.getCenter(find.byType(LyricsWallNext));
      await tester.sendEventToBinding(
        PointerScrollEvent(
          position: center,
          scrollDelta: const Offset(0, 120),
          kind: PointerDeviceKind.mouse,
        ),
      );
      await settle(tester, frames: 45);
      expect(state.debugUserScrolling(), isTrue);
      expect(state.debugUser(), lessThan(0));
      expect(
        state.debugOffset(),
        lessThan(before - 1),
        reason: '滚轮向下应把整墙内容向上推',
      );
    });

    testWidgets('旧 AMLL 入口仍能挂载绘制', (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: SizedBox(
              width: 400,
              height: 500,
              child: AmllPhysicsWall(
                groups: buildGroups(20),
                positionMs: 5000,
                playing: true,
                onSeek: (_) {},
              ),
            ),
          ),
        ),
      );
      await settle(tester, frames: 30);
      expect(tester.takeException(), isNull);
    });

    testWidgets('逐字/译文/音译在整层与逐行档下都能绘制', (tester) async {
      final groups = [
        const LyricGroup(
          original: LyricLine(timeMs: 0, text: '逐字歌词'),
          endMs: 2000,
          translation: 'translation',
          translationFragments: [
            LyricFragment(text: 'trans', startMs: 0, durationMs: 1000),
            LyricFragment(text: 'lation', startMs: 1000, durationMs: 1000),
          ],
          romaji: 'romaji line',
          romajiFragments: [
            LyricFragment(text: 'romaji', startMs: 0, durationMs: 1000),
            LyricFragment(text: ' line', startMs: 1000, durationMs: 1000),
          ],
          fragments: [
            LyricFragment(text: '逐', startMs: 0, durationMs: 700),
            LyricFragment(text: '字', startMs: 700, durationMs: 700),
            LyricFragment(text: '歌', startMs: 1400, durationMs: 300),
            LyricFragment(text: '词', startMs: 1700, durationMs: 300),
          ],
        ),
        const LyricGroup(
          original: LyricLine(timeMs: 2000, text: '下一行'),
          endMs: 3000,
        ),
      ];
      for (final quality in [
        WallBlurQuality.fast,
        WallBlurQuality.quality,
        WallBlurQuality.lite,
        WallBlurQuality.off,
      ]) {
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(
              body: SizedBox(
                width: 400,
                height: 500,
                child: LyricsWallNext(
                  groups: groups,
                  positionMs: 900,
                  playing: true,
                  showRomanization: true,
                  blurQuality: quality,
                  onSeek: (_) {},
                ),
              ),
            ),
          ),
        );
        await settle(tester, frames: 20);
        expect(tester.takeException(), isNull);
      }
    });

    testWidgets('点击行 seek 到该行起点', (tester) async {
      int? seeked;
      await tester.pumpWidget(
        buildWall(buildGroups(20), 0, onSeek: (ms) => seeked = ms),
      );
      await settle(tester);
      final rect = tester.getRect(find.byType(LyricsWallNext));
      await tester.tapAt(Offset(rect.center.dx, rect.top + 40));
      await tester.pump();
      expect(seeked, isNotNull);
    });

    testWidgets('稳态帧走烘焙贴图路径', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(30), 2500));
      await tester.pumpAndSettle();
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugStaticIdle(), isTrue);
      expect(state.debugBakeActive(), isTrue);
      expect(state.debugBakedLineCount(), greaterThan(0));
    });

    testWidgets('换行过渡回退现行路径，稳定后恢复贴图', (tester) async {
      final groups = buildGroups(30);
      await tester.pumpWidget(buildWall(groups, 2500));
      await tester.pumpAndSettle();
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugBakeActive(), isTrue);

      // 推进一行（1000ms，不触发 seek 吸附）：弹簧开始滚动。
      await tester.pumpWidget(buildWall(groups, 3500));
      await tester.pump(const Duration(milliseconds: 16));
      expect(state.debugBakeActive(), isFalse);

      await tester.pumpAndSettle();
      expect(state.debugBakeActive(), isTrue);
    });

    testWidgets('悬停时取消烘焙贴图', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(20), 5000));
      await tester.pumpAndSettle();
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugBakeActive(), isTrue);

      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);
      await gesture.moveTo(tester.getCenter(find.byType(LyricsWallNext)));
      await settle(tester, frames: 45);
      expect(state.debugBakeActive(), isFalse);
    });

    testWidgets('关闭烘焙开关后不再走贴图路径', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(20), 5000));
      await tester.pumpAndSettle();
      final state = tester.state<LyricsWallNextState>(
        find.byType(LyricsWallNext),
      );
      expect(state.debugBakeActive(), isTrue);
      state.debugSetBakeEnabled(false);
      await tester.pumpAndSettle();
      expect(state.debugBakeActive(), isFalse);
    });

    testWidgets('烘焙图随组件销毁释放且不抛异常', (tester) async {
      await tester.pumpWidget(buildWall(buildGroups(30), 2500));
      await tester.pumpAndSettle();
      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
    });
  });
}
