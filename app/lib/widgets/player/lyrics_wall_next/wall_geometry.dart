// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词墙几何层：可变行高 + 自然中心 + 可见窗口。
///
/// 只做纯算术与按需测量，不持有动画状态：
/// - 远行用估算行高，进入视口附近后才用 [computeLineHeight] 实测；
/// - 中心间距 = 前后半高 + 行间隙（背景行间隙 0.4×）；
/// - 可见窗口用中心列表二分定位，成本 O(log N + 窗口)。
library;

import 'dart:math' as math;

import 'package:flutter/painting.dart';

import '../../../services/lyrics/lyric_line.dart';
import '../lyrics_v7/lyrics_layout.dart';

/// 几何层配置。
class WallGeometryStyle {
  const WallGeometryStyle({
    required this.fontSize,
    required this.maxWidth,
    this.fontFamily,
    this.fontWeight = FontWeight.w600,
    this.showTranslation = true,
    this.showRomanization = false,
  });

  final double fontSize;
  final double maxWidth;
  final String? fontFamily;
  final FontWeight fontWeight;
  final bool showTranslation;
  final bool showRomanization;

  double get gapPx => fontSize * kLyricLineGapEm;
}

/// 一组歌词的几何信息。
class WallGeometry {
  WallGeometry({required this.groups, required this.style})
    : heights = List<double>.filled(groups.length, 0),
      centers = List<double>.filled(groups.length, 0),
      measured = List<bool>.filled(groups.length, false) {
    for (var i = 0; i < groups.length; i++) {
      heights[i] = estimateLineHeight(
        groups[i],
        fontSize: style.fontSize,
        showTranslation: style.showTranslation,
        showRomanization: style.showRomanization,
      );
    }
    _recompute();
  }

  final List<LyricGroup> groups;
  final WallGeometryStyle style;

  /// 每行高度（逻辑像素）。
  final List<double> heights;

  /// 每行自然中心（从首行顶部起算，首行中心 = 首行半高）。
  final List<double> centers;

  /// 是否已实测。
  final List<bool> measured;

  int get length => heights.length;

  double _maxHeight = 0;

  /// 最大行高（可见窗口保守扩张用）。
  double get maxHeight => _maxHeight;

  /// 实测 [from, to) 内的行；返回是否有行高发生变化。
  ///
  /// 只在窗口第一次覆盖某行时排版一次；长行换行导致的高度变化会触发
  /// 中心重算，调用方据此重设滚动目标。
  bool measureRange(int from, int to) {
    final lo = math.max(0, from);
    final hi = math.min(length, to);
    var changed = false;
    for (var i = lo; i < hi; i++) {
      if (measured[i]) continue;
      measured[i] = true;
      final h = computeLineHeight(
        groups[i],
        fontSize: style.fontSize,
        fontFamily: style.fontFamily,
        maxWidth: style.maxWidth,
        showTranslation: style.showTranslation,
        showRomanization: style.showRomanization,
        fontWeight: style.fontWeight,
      );
      if ((h - heights[i]).abs() > 0.01) {
        heights[i] = h;
        changed = true;
      }
    }
    if (changed) _recompute();
    return changed;
  }

  /// 可见窗口 `[start, end)`（保守向两侧各扩一行）。
  (int, int) visibleRange({
    required double offset,
    required double viewHeight,
    required double margin,
  }) {
    if (length == 0) return (0, 0);
    final top = -offset - margin - _maxHeight;
    final bottom = viewHeight + margin - offset + _maxHeight;
    var lo = _lowerBound(centers, top) - 1;
    var hi = _upperBound(centers, bottom) + 1;
    lo = lo.clamp(0, length);
    hi = hi.clamp(0, length);
    if (hi < lo) hi = lo;
    return (lo, hi);
  }

  /// 锚点行对齐到 `align * viewHeight` 所需的滚动偏移。
  double targetOffset(int anchor, double viewHeight, double align) {
    if (anchor < 0 || anchor >= centers.length) return 0;
    return viewHeight * align - centers[anchor];
  }

  /// 某行在屏幕上的中心 y。
  double yAt(int index, double offset) => centers[index] + offset;

  void _recompute() {
    if (heights.isEmpty) {
      _maxHeight = 0;
      return;
    }
    var maxH = heights.first;
    centers[0] = heights[0] / 2;
    for (var i = 1; i < heights.length; i++) {
      // 背景行与主行同槽位、间距更紧（0.4×）。
      final gap = groups[i].isBG ? style.gapPx * 0.4 : style.gapPx;
      centers[i] = centers[i - 1] + heights[i - 1] / 2 + gap + heights[i] / 2;
      maxH = math.max(maxH, heights[i]);
    }
    _maxHeight = maxH;
  }

  static int _lowerBound(List<double> sorted, double value) {
    var lo = 0;
    var hi = sorted.length;
    while (lo < hi) {
      final mid = (lo + hi) >> 1;
      if (sorted[mid] < value) {
        lo = mid + 1;
      } else {
        hi = mid;
      }
    }
    return lo;
  }

  static int _upperBound(List<double> sorted, double value) {
    var lo = 0;
    var hi = sorted.length;
    while (lo < hi) {
      final mid = (lo + hi) >> 1;
      if (sorted[mid] <= value) {
        lo = mid + 1;
      } else {
        hi = mid;
      }
    }
    return lo;
  }
}
