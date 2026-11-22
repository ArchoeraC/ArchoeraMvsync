// ArchoeraMvsumc UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词墙时间轴层：把 [LyricGroup] 列表预计算成只读的时间索引。
///
/// 只回答四类问题，不持有任何 UI / 动效状态：
/// - [activeAt]：严格覆盖位置的高亮行（背景行只在独立时段接管）；
/// - [highlightAt]：在覆盖之上为逐字行提前换行；
/// - [scrollAt]：应用预滚后的滚动行；
/// - [anchorAt]：布局锚点（间奏/覆盖/保持上一个/seek 边界）。
///
/// 与旧实现相比，预滚、间奏、逐字提前量的判定被集中到这里，组件只消费
/// 结果；后续若要替换索引结构（区间树/游标）不会影响渲染层。
library;

import 'dart:math' as math;

import '../../../services/lyrics/lyric_line.dart';
import 'wall_tuning.dart';

/// 一行歌词的时间信息。
class WallLine {
  const WallLine({
    required this.startMs,
    required this.coverEndMs,
    required this.sungEndMs,
    required this.scrollStartMs,
    required this.isBG,
    required this.hasWordTiming,
  });

  /// 原始起始时间。
  final int startMs;

  /// 覆盖窗口结束（`endMs`）；null 表示覆盖到后续行接管。
  final int? coverEndMs;

  /// 真实唱完时间（逐字末字优先，否则退回覆盖窗口）。
  final int sungEndMs;

  /// 预滚后的滚动起点。
  final int scrollStartMs;

  /// 背景人声行。
  final bool isBG;

  /// 是否有真实字级时间轴。
  final bool hasWordTiming;

  bool covers(int pos) =>
      pos >= startMs && (coverEndMs == null || pos < coverEndMs!);
}

/// 一段间奏。
class WallInterlude {
  const WallInterlude({
    required this.startMs,
    required this.endMs,
    required this.anchorIndex,
  });

  final int startMs;
  final int endMs;
  final int anchorIndex;

  bool contains(int pos) => pos >= startMs && pos < endMs;
}

/// 预计算后的歌词时间轴。
class WallTimeline {
  WallTimeline._({
    required this.groups,
    required this.lines,
    required this.interludes,
  });

  final List<LyricGroup> groups;
  final List<WallLine> lines;
  final List<WallInterlude> interludes;

  int get length => lines.length;
  bool get isEmpty => lines.isEmpty;

  /// 末行真实唱完时间（用于判定歌曲结尾）。
  int get lastSungEndMs => lines.isEmpty ? 0 : lines.last.sungEndMs;

  /// 从歌词组构建时间轴；纯函数，可安全缓存。
  factory WallTimeline.build(List<LyricGroup> groups) {
    final n = groups.length;
    final lines = List<WallLine>.filled(n, _emptyLine);
    if (n == 0) {
      return WallTimeline._(groups: groups, lines: lines, interludes: const []);
    }

    // 预滚起点：与前行有间隙提前 600ms（不早于上一组结束）；重叠（对唱）
    // 提前 400ms（不早于前一行时长的 30% 处）；背景行随主行。
    final scrollStarts = List<int>.filled(n, 0);
    var prevStart = 0;
    var prevEnd = 0;
    var groupStart = 0;
    var groupEnd = 0;
    var hasPrev = false;

    for (var i = 0; i < n; i++) {
      final group = groups[i];
      final start = group.original.timeMs;
      final sungEnd = _sungEndMs(group);
      if (group.isBG) {
        scrollStarts[i] = i > 0 ? scrollStarts[i - 1] : start;
      } else {
        int advance;
        int boundary;
        if (hasPrev) {
          final hadGap = start >= prevEnd;
          if (hadGap) {
            advance = kWallPrerollNoOverlapMs;
            boundary = groupEnd;
          } else {
            advance = kWallPrerollOverlapMs;
            boundary =
                prevStart +
                ((prevEnd - prevStart) * kWallPrerollOverlapBoundary).round();
          }
        } else {
          advance = kWallPrerollNoOverlapMs;
          boundary = 0;
        }
        final next = math.max(boundary, start - advance);
        scrollStarts[i] = next < start ? next : start;
        if (hasPrev && start < groupEnd && sungEnd > groupStart) {
          groupStart = math.min(groupStart, start);
          groupEnd = math.max(groupEnd, sungEnd);
        } else {
          groupStart = start;
          groupEnd = sungEnd;
        }
        prevStart = start;
        prevEnd = sungEnd;
        hasPrev = true;
      }

      lines[i] = WallLine(
        startMs: start,
        coverEndMs: group.endMs,
        sungEndMs: sungEnd,
        scrollStartMs: scrollStarts[i],
        isBG: group.isBG,
        hasWordTiming: hasRealWordTiming(group.fragments),
      );
    }

    return WallTimeline._(
      groups: groups,
      lines: lines,
      interludes: _buildInterludes(groups, lines),
    );
  }

  /// 严格覆盖位置的高亮行。
  ///
  /// 同时覆盖时优先非背景行；仅当没有任何非背景行覆盖时才返回背景行。
  int activeAt(int pos) {
    var res = -1;
    var bgRes = -1;
    for (var i = 0; i < lines.length; i++) {
      final line = lines[i];
      if (pos < line.startMs) break;
      if (!line.covers(pos)) continue;
      if (line.isBG) {
        bgRes = i;
      } else {
        res = i;
      }
    }
    return res >= 0 ? res : bgRes;
  }

  /// 高亮行：逐字模式下允许在下一行开始前提前切换。
  int highlightAt(int pos, {required bool wordSweep}) {
    final active = activeAt(pos);
    if (!wordSweep || lines.isEmpty) return active;

    var next = active + 1;
    while (next < lines.length &&
        (lines[next].isBG || lines[next].startMs <= pos)) {
      next++;
    }
    if (next >= lines.length) return active;
    if (!lines[next].hasWordTiming) return active;

    var switchAt = lines[next].startMs - kWallWordSwitchLeadMs;
    if (active >= 0) {
      final sweepEnd = _sweepEndMs(groups[active]);
      if (sweepEnd != null && sweepEnd > switchAt) switchAt = sweepEnd;
    }
    return pos >= switchAt ? next : active;
  }

  /// 应用预滚后的滚动行。
  int scrollAt(int pos) {
    var res = -1;
    var bgRes = -1;
    for (var i = 0; i < lines.length; i++) {
      final line = lines[i];
      if (pos < line.scrollStartMs) break;
      // 预滚窗口 = [scrollStart, coverEnd)：比原始起点更早接管，让视野在
      // 开唱前先滚到位。
      if (line.coverEndMs != null && pos >= line.coverEndMs!) continue;
      if (line.isBG) {
        bgRes = i;
      } else {
        res = i;
      }
    }
    return res >= 0 ? res : bgRes;
  }

  /// 播放位置是否落在间奏内；返回间奏下标，否则 -1。
  int interludeAt(int pos) {
    for (var i = 0; i < interludes.length; i++) {
      if (interludes[i].contains(pos)) return i;
    }
    return -1;
  }

  /// 解析布局锚点。
  ///
  /// - 间奏：保持空隙前一行；
  /// - 有覆盖行：用该行；
  /// - 无行覆盖：非 seek 保持上一个锚点，seek 越界才定位到最近边界行。
  int anchorAt({required int pos, required int previous, required bool seek}) {
    if (lines.isEmpty) return -1;
    final it = interludeAt(pos);
    if (it >= 0) {
      return interludes[it].anchorIndex.clamp(0, lines.length - 1);
    }
    final active = scrollAt(pos);
    if (active >= 0) return active.clamp(0, lines.length - 1);
    if (!seek && previous >= 0 && previous < lines.length) return previous;
    return futureIndex(pos).clamp(0, lines.length - 1);
  }

  /// 第一个起始时间 >= pos 的行；全部之后取末行。
  int futureIndex(int pos) {
    for (var i = 0; i < lines.length; i++) {
      if (lines[i].startMs >= pos) return i;
    }
    return lines.isEmpty ? 0 : lines.length - 1;
  }

  /// 相邻行间隔（毫秒）；非正/越界返回 null。
  int? intervalMs(int index) {
    if (index <= 0 || index >= lines.length) return null;
    final gap = lines[index].startMs - lines[index - 1].startMs;
    return gap > 0 ? gap : null;
  }

  /// 跳过背景行的相邻间隔（用于高速换行判定）。
  int? gapMs(int index, {required bool forward}) {
    if (index < 0 || index >= lines.length) return null;
    final base = lines[index].startMs;
    if (forward) {
      for (var j = index + 1; j < lines.length; j++) {
        if (lines[j].isBG) continue;
        final gap = lines[j].startMs - base;
        return gap > 0 ? gap : null;
      }
    } else {
      for (var j = index - 1; j >= 0; j--) {
        if (lines[j].isBG) continue;
        final gap = base - lines[j].startMs;
        return gap > 0 ? gap : null;
      }
    }
    return null;
  }

  static const WallLine _emptyLine = WallLine(
    startMs: 0,
    coverEndMs: 0,
    sungEndMs: 0,
    scrollStartMs: 0,
    isBG: false,
    hasWordTiming: false,
  );

  /// 是否拥有真实字级时间轴（普通 LRC 的合成片段不算）。
  static bool hasRealWordTiming(List<LyricFragment>? frags) =>
      frags != null && frags.isNotEmpty;

  /// 逐字扫亮结束时刻（绝对毫秒）；无字级时间轴返回 null。
  static int? _sweepEndMs(LyricGroup group) {
    final frags = group.fragments;
    if (frags == null || frags.isEmpty) return null;
    var end = 0;
    for (final f in frags) {
      final e = f.startMs + (f.durationMs ?? 0);
      if (e > end) end = e;
    }
    return group.original.timeMs + end;
  }

  /// 真实唱完时间：逐字末字优先，否则退回行结束/起始。
  static int _sungEndMs(LyricGroup group) {
    final start = group.original.timeMs;
    final sweep = _sweepEndMs(group);
    var end = sweep ?? (group.endMs ?? start);
    if (end < start) end = start;
    return end;
  }

  static List<WallInterlude> _buildInterludes(
    List<LyricGroup> groups,
    List<WallLine> lines,
  ) {
    if (groups.isEmpty) return const [];
    final out = <WallInterlude>[];
    var maxEnd = 0;
    for (var i = 0; i < groups.length; i++) {
      final start = groups[i].original.timeMs;
      if (start - maxEnd >= kWallMinInterludeGapMs) {
        out.add(
          WallInterlude(startMs: maxEnd, endMs: start, anchorIndex: i - 1),
        );
      }
      final end = lines[i].sungEndMs;
      if (end > maxEnd) maxEnd = end;
    }
    return out;
  }
}
