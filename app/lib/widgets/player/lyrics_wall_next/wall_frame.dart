// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词墙一帧的只读输入：状态层写入，渲染层消费。
///
/// 位置只传一个 `offset`，行位置一律由 `geometry.centers[i] + offset` 推出；
/// 这样渲染层无需关心滚动/级联/浏览偏移的实现差异。
library;

import 'package:material_ui/material_ui.dart';

import '../../../services/lyrics/lyric_line.dart';
import '../lyrics_v7/lyrics_paragraph_cache.dart';
import '../lyrics_v7/lyrics_fragment_render.dart';
import 'wall_bake.dart';
import 'wall_geometry.dart';
import 'wall_motion.dart';
import 'wall_timeline.dart';
import 'wall_tuning.dart';

/// 一帧的渲染上下文。
class WallFrame {
  List<LyricGroup> groups = const [];
  WallTimeline? timeline;
  WallGeometry? geometry;

  int positionMs = 0;
  int active = -1;
  int anchor = -1;
  int winStart = 0;
  int winEnd = 0;

  /// 整墙滚动偏移：由状态层的 [WallMotion] 实时提供。
  ///
  /// 不要在状态层另存一份快照——painter 与滚动动画必须读同一个值，
  /// 否则换行后整墙会按旧的偏移绘制（表现为歌词区空白）。
  WallMotion? motion;

  double get offset => motion?.offset ?? 0;
  double width = 0;
  double height = 0;
  double align = 0.5;

  double fontSize = 18;
  String? fontFamily;
  FontWeight fontWeight = FontWeight.w600;
  Color played = const Color(0xFFFFFFFF);
  Color unplayed = const Color(0xFFFFFFFF);
  double inactiveAlpha = 0.45;
  double wordFadeWidth = 0.5;
  TextAlign textAlign = TextAlign.center;

  bool wordSweep = true;
  bool hidePassed = false;
  bool showTranslation = true;
  bool showRomanization = false;
  bool enableScale = true;
  bool edgeFade = false;
  bool userScrolling = false;

  WallBlurMode blurMode = WallBlurMode.panel;
  double blurStrength = 0;

  /// 设备像素比（烘焙按物理分辨率栅格）。
  double dpr = 1;

  /// 是否允许使用稳态逐行烘焙（诊断可关）。
  bool bakeEnabled = true;

  /// 整墙弹簧/过渡是否已静止（状态层逐帧维护）。
  bool staticIdle = false;

  /// 整层失焦强度是否已到目标（状态层逐帧维护）。
  bool blurStrengthSettled = false;

  /// 上一帧是否走了烘焙贴图路径（诊断/测试）。
  bool lastFrameBaked = false;

  final WallVisuals visuals = WallVisuals();
  final LyricsParagraphCache cache = LyricsParagraphCache();
  final LyricsFragmentCache fragCache = LyricsFragmentCache();
  final LyricsFragmentCache transFragCache = LyricsFragmentCache();
  final LyricsFragmentCache romaFragCache = LyricsFragmentCache();
  final WallBake bake = WallBake();

  bool get ready => geometry != null && groups.isNotEmpty;

  void clearCaches() {
    cache.clear();
    fragCache.clear();
    transFragCache.clear();
    romaFragCache.clear();
    bake.dispose();
  }
}
