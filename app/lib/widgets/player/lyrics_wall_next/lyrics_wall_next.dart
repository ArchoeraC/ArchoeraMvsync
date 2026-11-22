// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 新一代物理歌词墙（四层重写版）。
///
/// 分层：
/// - [WallTimeline]：预滚 / 间奏 / 高亮 / 锚点；
/// - [WallGeometry]：按需测量 / 自然中心 / 可见窗口；
/// - [WallMotion]：整墙单一滚动偏移 + 可见行视觉过渡；
/// - [LyricsWallNextPainter]：逐字扫亮 / 景深 / 上下渐隐。
///
/// 与旧 `AmllPhysicsWall` 并列，通过歌词偏好 `lyrics.engine` 选择
/// （`simple` / `wall` / `amll`，见 `PlayerLyricsBlock`）。
library;

import 'dart:async';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/gestures.dart' show PointerScrollEvent;
import 'package:flutter/scheduler.dart' show SchedulerBinding, Ticker;
import 'package:material_ui/material_ui.dart';

import '../../../l10n/l10n.dart';
import '../../../services/lyrics/lyric_line.dart';
import '../../../utils/spring.dart';
import '../lyrics_v7/lyrics_blur_budget.dart';
import 'lyrics_wall_next_painter.dart';
import 'wall_clock.dart';
import 'wall_frame.dart';
import 'wall_geometry.dart';
import 'wall_motion.dart';
import 'wall_timeline.dart';
import 'wall_tuning.dart';

export 'wall_tuning.dart' show WallBlurMode, WallBlurQuality, WallSpringPreset;

/// 新引擎入口。
class LyricsWallNext extends StatefulWidget {
  const LyricsWallNext({
    super.key,
    required this.groups,
    required this.positionMs,
    required this.onSeek,
    this.playing = false,
    this.fontSize = 18,
    this.fontFamily,
    this.fontWeight = FontWeight.w600,
    this.playedColor = const Color(0xFFD0D3DA),
    this.unplayedColor = const Color(0xFF9AA1B5),
    this.showTranslation = true,
    this.showRomanization = false,
    this.alignFraction = 0.5,
    this.inactiveAlpha = 0.45,
    this.wordSweep = true,
    this.hidePassed = false,
    this.enableScale = true,
    this.blurQuality = WallBlurQuality.auto,
    this.wordFadeWidth = 0.5,
    this.springPreset = 'default',
    this.textAlign = TextAlign.center,
    this.animate = true,
    this.edgeFade = false,
  });

  final List<LyricGroup> groups;
  final int positionMs;
  final ValueChanged<int> onSeek;
  final bool playing;

  final double fontSize;
  final String? fontFamily;
  final FontWeight fontWeight;
  final Color playedColor;
  final Color unplayedColor;
  final bool showTranslation;
  final bool showRomanization;
  final double alignFraction;
  final double inactiveAlpha;
  final bool wordSweep;
  final bool hidePassed;
  final bool enableScale;
  final WallBlurQuality blurQuality;
  final double wordFadeWidth;
  final String springPreset;
  final TextAlign textAlign;
  final bool animate;
  final bool edgeFade;

  @override
  State<LyricsWallNext> createState() => LyricsWallNextState();
}

/// 状态机：只负责把「播放位置 + 尺寸 + 手势」翻译成 [WallFrame]。
class LyricsWallNextState extends State<LyricsWallNext>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker;
  final WallClock _clock = WallClock();
  final WallFrame _f = WallFrame();
  final WallRepaint _repaint = WallRepaint();
  final WallMotion _motion = WallMotion();
  final WallSmoother _blurStrength = WallSmoother();
  final LyricsBlurBudget _blurBudget = LyricsBlurBudget();

  WallTimeline? _timeline;
  WallGeometry? _geometry;
  SpringParams _params = const SpringParams();

  int _anchorIdx = -1;
  int _winStart = 0;
  int _winEnd = 0;
  int _lastUs = 0;
  int? _lastPosMs;

  double _width = 0;
  double _height = 0;
  double _user = 0;

  bool _ever = false;
  bool _seekSnap = false;
  bool _flyIn = true;
  bool _metricsDirty = true;
  bool _hovering = false;
  bool _dragging = false;
  bool _blurDegraded = false;

  WallBlurMode? _debugModeOverride;
  WallSpringPreset _preset = WallSpringPreset.balanced;
  Timer? _userReset;

  bool get _wantsClock =>
      widget.playing && widget.animate && widget.groups.isNotEmpty;

  @override
  void initState() {
    super.initState();
    _ticker = createTicker(_tick);
    _clock.reset(widget.positionMs, playing: _wantsClock);
    _preset = wallSpringPresetFrom(widget.springPreset);
    _params = resolveWallSpring(
      preset: _preset,
      seeking: false,
      interlude: false,
    );
    _syncStyle();
    SchedulerBinding.instance.addTimingsCallback(_onTimings);
  }

  @override
  void dispose() {
    SchedulerBinding.instance.removeTimingsCallback(_onTimings);
    _userReset?.cancel();
    _ticker.dispose();
    _repaint.dispose();
    _f.bake.dispose();
    super.dispose();
  }

  @override
  void didUpdateWidget(LyricsWallNext oldWidget) {
    super.didUpdateWidget(oldWidget);
    _preset = wallSpringPresetFrom(widget.springPreset);
    _syncStyle();
    _clock.reset(widget.positionMs, playing: _wantsClock);
    final groupsChanged = oldWidget.groups != widget.groups;
    if (groupsChanged) {
      _seekSnap = true;
      _anchorIdx = -1;
      _ever = false;
      _metricsDirty = true;
      _flyIn = true;
      _visualsClear();
      _user = 0;
      _dragging = false;
    } else {
      final last = _lastPosMs ?? widget.positionMs;
      final delta = widget.positionMs - last;
      if (delta > 2000 || delta < -100) _seekSnap = true;
    }
    _lastPosMs = widget.positionMs;
    if (oldWidget.fontSize != widget.fontSize ||
        oldWidget.fontFamily != widget.fontFamily ||
        oldWidget.showTranslation != widget.showTranslation ||
        oldWidget.showRomanization != widget.showRomanization ||
        oldWidget.fontWeight != widget.fontWeight) {
      _metricsDirty = true;
    }
    if (!_metricsDirty) {
      _syncTimeline(force: groupsChanged);
      _ensureTicker();
    }
    _repaint.notify();
  }

  // ---- 样式与档位 ---------------------------------------------------------

  void _syncStyle() {
    // 每次同步样式都重新绑定：热重载会保留旧 State，initState 不会重跑，
    // 只在 initState 绑定会让旧实例的 painter 永远读 offset=0（歌词停在顶部、
    // 滚轮/拖拽看起来没反应）。
    _f.motion = _motion;
    _f.fontSize = widget.fontSize;
    _f.fontFamily = widget.fontFamily;
    _f.fontWeight = widget.fontWeight;
    _f.played = widget.playedColor;
    _f.unplayed = widget.unplayedColor;
    _f.inactiveAlpha = widget.inactiveAlpha;
    _f.align = widget.alignFraction;
    _f.wordSweep = widget.wordSweep;
    _f.hidePassed = widget.hidePassed;
    _f.showTranslation = widget.showTranslation;
    _f.showRomanization = widget.showRomanization;
    _f.enableScale = widget.enableScale;
    _f.textAlign = widget.textAlign;
    _f.edgeFade = widget.edgeFade;
    _f.wordFadeWidth = widget.wordFadeWidth;
    _f.blurMode = _blurModeResolved;
    if (!widget.animate) {
      // 性能模式没有 ticker，失焦强度必须直接落位。
      _blurStrength.hardSet(_panelBlurTarget());
      _f.staticIdle = true;
      _f.blurStrengthSettled = true;
    }
    _f.blurStrength = _blurStrength.value;
  }

  WallBlurMode get _blurModeResolved => resolveWallBlurMode(
    quality: widget.blurQuality,
    override: _debugModeOverride ?? wallBlurOverride,
    autoDegraded: _blurDegraded,
  );

  double _panelBlurTarget() {
    final m = _blurModeResolved;
    final wants = m == WallBlurMode.panel || m == WallBlurMode.lite;
    return wants && !_hovering && !_dragging ? 1.0 : 0.0;
  }

  double _blurTargetFor(int index) {
    if (_hovering || _dragging) return 0;
    if (_blurModeResolved == WallBlurMode.off) return 0;
    if (_anchorIdx < 0) return 0;
    final d = (index - _anchorIdx).abs();
    if (d == 0) return 0;
    return math.min(kWallMaxBlurPx, 1.0 + d);
  }

  // ---- 帧耗时与自动降级 ---------------------------------------------------

  void _onTimings(List<ui.FrameTiming> timings) {
    for (final t in timings) {
      _noteFrame(
        rasterMs: t.rasterDuration.inMicroseconds / 1000.0,
        uiMs: t.buildDuration.inMicroseconds / 1000.0,
      );
    }
  }

  void _noteFrame({required double rasterMs, required double uiMs}) {
    if (wallBlurOverride != null || _blurDegraded) return;
    if (widget.blurQuality != WallBlurQuality.auto) return;
    if (!_ticker.isActive) return;
    if (_blurBudget.onFrame(rasterMs, uiMs: uiMs)) {
      _blurDegraded = true;
      _blurBudget.reset();
      _syncStyle();
      _ensureTicker();
      _repaint.notify();
    }
  }

  // ---- 布局与时间轴 -------------------------------------------------------

  void _rebuildMetrics() {
    _f.clearCaches();
    _visualsClear();
    _f.groups = widget.groups;
    _timeline = WallTimeline.build(widget.groups);
    _geometry = WallGeometry(
      groups: widget.groups,
      style: WallGeometryStyle(
        fontSize: widget.fontSize,
        maxWidth: math.max(40.0, _width - 24),
        fontFamily: widget.fontFamily,
        fontWeight: widget.fontWeight,
        showTranslation: widget.showTranslation,
        showRomanization: widget.showRomanization,
      ),
    );
    _f.timeline = _timeline;
    _f.geometry = _geometry;
    _winStart = 0;
    _winEnd = 0;
    _f.winStart = 0;
    _f.winEnd = 0;
    _metricsDirty = false;
  }

  void _visualsClear() => _f.visuals.clear();

  /// 同步时间轴状态并重设滚动目标；返回是否改变。
  bool _syncTimeline({bool force = false, bool snapNow = false}) {
    final t = _timeline;
    final g = _geometry;
    if (t == null || g == null || g.length == 0) {
      _f.active = -1;
      _f.anchor = -1;
      return false;
    }
    final pos = _clock.valueMs;
    _f.positionMs = pos;

    var interlude = t.interludeAt(pos);
    var active = interlude >= 0
        ? -1
        : t.highlightAt(pos, wordSweep: widget.wordSweep);
    if (interlude >= 0) {
      final early = t.highlightAt(pos, wordSweep: widget.wordSweep);
      if (early >= 0) {
        interlude = -1;
        active = early;
      }
    }
    _f.active = active;
    _f.userScrolling = _dragging;

    if (_dragging) {
      _syncWindow();
      return true;
    }

    final seekSnap = _seekSnap;
    _seekSnap = false;
    final anchor = t.anchorAt(pos: pos, previous: _anchorIdx, seek: seekSnap);
    if (anchor < 0) return false;
    _f.anchor = anchor;

    final oldAnchor = _anchorIdx;
    final changed = anchor != oldAnchor || !_ever || force;
    _anchorIdx = anchor;
    if (!changed) return false;

    _params = resolveWallSpring(
      preset: _preset,
      seeking: seekSnap,
      interlude: interlude >= 0,
      intervalMs:
          t.gapMs(anchor, forward: false) ??
          t.gapMs(anchor, forward: true) ??
          t.intervalMs(anchor),
      endOfSong: t.lastSungEndMs > 0 && pos >= t.lastSungEndMs,
      shiftPx: oldAnchor >= 0 && oldAnchor < g.length
          ? (g.centers[anchor] - g.centers[oldAnchor]).abs()
          : 0,
      viewH: _height,
    );

    // 先定窗口并实测：测量会改变中心，必须在算目标前完成。
    _syncWindow();

    final target = g.targetOffset(anchor, _height, _f.align) + _user;
    if (widget.animate) _f.staticIdle = false;
    if (_flyIn) {
      final from = target + (_height > 0 ? _height : 400.0) * 1.5;
      _motion.hardSet(from);
      _motion.setTarget(target, _params);
      _flyIn = false;
    } else if (seekSnap || snapNow || !widget.animate || !_ever) {
      _motion.hardSet(target);
    } else {
      _motion.setTarget(target, _params);
    }
    _ever = true;
    _ensureTicker();
    return true;
  }

  /// 重算可见窗口并按需实测；返回是否有行高变化。
  bool _syncWindow() {
    final g = _geometry;
    if (g == null || g.length == 0) {
      _winStart = 0;
      _winEnd = 0;
      return false;
    }
    final h = _height > 0 ? _height : 400.0;
    final margin = math.max(h * kWallWindowMarginRatio, kWallWindowMarginMinPx);
    var (s, e) = g.visibleRange(
      offset: _motion.offset,
      viewHeight: h,
      margin: margin,
    );
    final measured = g.measureRange(s, e);
    if (measured) {
      if (widget.animate) _f.staticIdle = false;
      (s, e) = g.visibleRange(
        offset: _motion.offset,
        viewHeight: h,
        margin: margin,
      );
      if (_anchorIdx >= 0 && _anchorIdx < g.length) {
        final target = g.targetOffset(_anchorIdx, h, _f.align) + _user;
        if (widget.animate && !_seekSnap) {
          _motion.setTarget(target, _params);
        } else {
          _motion.hardSet(target);
        }
      }
    }
    _winStart = s;
    _winEnd = e;
    _f.winStart = s;
    _f.winEnd = e;
    _f.visuals.ensureRange(
      from: s,
      to: e,
      active: _f.active,
      blurTarget: _blurTargetFor,
    );
    return measured;
  }

  // ---- 用户浏览 -----------------------------------------------------------

  int get _browseAnchor =>
      (_anchorIdx >= 0 && _geometry != null && _anchorIdx < _geometry!.length)
      ? _anchorIdx
      : 0;

  double get _userMax {
    final c = _geometry?.centers;
    if (c == null || c.isEmpty) return 0;
    return c[_browseAnchor] - c.first;
  }

  double get _userMin {
    final c = _geometry?.centers;
    if (c == null || c.isEmpty) return 0;
    return c[_browseAnchor] - c.last;
  }

  void _beginUserScroll() {
    _dragging = true;
    _f.userScrolling = true;
    _f.staticIdle = false;
    _userReset?.cancel();
    _ensureTicker();
  }

  void _pushUserTargets({bool snap = false}) {
    final g = _geometry;
    if (g == null || g.length == 0 || _anchorIdx < 0) return;
    final target = g.targetOffset(_anchorIdx, _height, _f.align) + _user;
    _f.staticIdle = false;
    if (snap || !widget.animate) {
      _motion.hardSet(target);
    } else {
      _motion.setTarget(target, _params);
    }
    _ensureTicker();
    _repaint.notify();
  }

  void _armUserReset() {
    _userReset?.cancel();
    _userReset = Timer(const Duration(milliseconds: 5000), () {
      if (!mounted) return;
      _user = 0;
      _dragging = false;
      _f.userScrolling = false;
      _syncTimeline(force: true);
      _ensureTicker();
      _repaint.notify();
    });
  }

  void _seekAt(double y) {
    final g = _geometry;
    if (g == null || g.length == 0) return;
    var best = 0;
    var bestDist = double.infinity;
    for (var i = 0; i < g.length; i++) {
      final d = (y - (g.centers[i] + _motion.offset)).abs();
      if (d < bestDist) {
        bestDist = d;
        best = i;
      }
    }
    _userReset?.cancel();
    _user = 0;
    _dragging = false;
    _f.userScrolling = false;
    _pushUserTargets();
    widget.onSeek(widget.groups[best].original.timeMs);
  }

  void _setHovering(bool value) {
    if (_hovering == value) return;
    _hovering = value;
    _f.staticIdle = false;
    _f.blurStrengthSettled = false;
    _ensureTicker();
    _repaint.notify();
  }

  // ---- Ticker -------------------------------------------------------------

  void _ensureTicker() {
    if (_ticker.isActive || !widget.animate) return;
    _lastUs = 0;
    _ticker.start();
  }

  void _tick(Duration elapsed) {
    if (!mounted) return;
    final us = elapsed.inMicroseconds;
    if (_lastUs == 0) {
      _lastUs = us;
      return;
    }
    var dt = (us - _lastUs) / 1000000.0;
    _lastUs = us;
    if (dt <= 0) return;
    if (dt > 0.1) dt = 0.1;
    if (dt < 0.0005) return;

    var moving = false;
    var springsMoved = false;
    if (_clock.isRunning) {
      _clock.tick(dt);
      if (_syncTimeline()) moving = true;
      moving = true;
    }
    if (_motion.update(dt)) {
      springsMoved = true;
      moving = true;
    }
    if (_syncWindow()) moving = true;
    final visualsMoved = _f.visuals.step(
      dt: dt,
      from: _winStart,
      to: _winEnd,
      active: _f.active,
      blurTarget: _blurTargetFor,
      animate: widget.animate,
    );
    if (visualsMoved) moving = true;
    final blurTarget = _panelBlurTarget();
    final blurMoved = _blurStrength.step(blurTarget, dt, kWallBlurTau);
    if (blurMoved) moving = true;
    _f.blurStrength = _blurStrength.value;
    _f.blurMode = _blurModeResolved;

    // 静态层是否静止：与 ticker 保活正交——播放中时钟常开，但整墙早已
    // 停驻，稳态帧仍可用于烘焙贴图（见 wall_bake.dart）。
    final wasIdle = _f.staticIdle;
    _f.staticIdle = !springsMoved && !visualsMoved;
    _f.blurStrengthSettled = !blurMoved && _f.blurStrength == blurTarget;

    final sweeping = _clock.isRunning && _sweepActive();
    if (!moving && !sweeping && !_clock.isRunning) _ticker.stop();
    if (moving || sweeping) {
      _repaint.notify();
    } else if (!wasIdle) {
      // 收敛帧：动画刚停，补一次重绘，让 painter 在「静止 + 无过渡」
      // 状态下进入烘焙稳态（此后不再有 ticker 驱动）。
      _repaint.notify();
    }
  }

  bool _sweepActive() {
    final active = _f.active;
    if (active < 0 || active >= widget.groups.length) return false;
    final g = widget.groups[active];
    if (_clock.valueMs < g.original.timeMs) return false;
    return WallTimeline.hasRealWordTiming(g.fragments) ||
        (widget.showTranslation &&
            WallTimeline.hasRealWordTiming(g.translationFragments)) ||
        (widget.showRomanization &&
            WallTimeline.hasRealWordTiming(g.romajiFragments));
  }

  // ---- Build --------------------------------------------------------------

  @override
  Widget build(BuildContext context) {
    final groups = widget.groups;
    if (groups.isEmpty) {
      if (_ticker.isActive) _ticker.stop();
      _timeline = null;
      _geometry = null;
      _f.timeline = null;
      _f.geometry = null;
      return Center(
        child: Text(
          context.l10n.commonNoLyrics,
          style: TextStyle(
            color: Theme.of(context).colorScheme.onSurface
                .withValues(alpha: 0.35),
            fontSize: 16,
          ),
        ),
      );
    }
    _f.groups = groups;
    _f.dpr = MediaQuery.devicePixelRatioOf(context);
    return LayoutBuilder(
      builder: (context, constraints) {
        final rawW = constraints.maxWidth;
        final rawH = constraints.maxHeight;
        final w = (rawW.isFinite && rawW > 0) ? rawW : 400.0;
        final h = (rawH.isFinite && rawH > 0) ? rawH : 400.0;
        if (w != _width || h != _height || _metricsDirty) {
          _width = w;
          _height = h;
          _f.width = w;
          _f.height = h;
          _rebuildMetrics();
          _anchorIdx = -1;
          _ever = false;
          _syncTimeline(force: true);
        }
        _f.positionMs = _clock.valueMs;
        _syncStyle();
        _syncTimeline();
        _syncWindow();
        if (_clock.isRunning) _ensureTicker();

        return MouseRegion(
          onEnter: (_) => _setHovering(true),
          onExit: (_) => _setHovering(false),
          child: Listener(
            onPointerSignal: (e) {
              if (e is PointerScrollEvent) {
                _beginUserScroll();
                _user = (_user - e.scrollDelta.dy).clamp(_userMin, _userMax);
                _pushUserTargets();
                _armUserReset();
              }
            },
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTapUp: (d) => _seekAt(d.localPosition.dy),
              onVerticalDragStart: (d) {
                _beginUserScroll();
                _pushUserTargets(snap: true);
                _armUserReset();
              },
              onVerticalDragUpdate: (d) {
                final delta = d.primaryDelta ?? d.delta.dy;
                _user = (_user + delta).clamp(_userMin, _userMax);
                _pushUserTargets(snap: true);
                _armUserReset();
              },
              onVerticalDragEnd: (_) => _armUserReset(),
              onVerticalDragCancel: () => _armUserReset(),
              child: CustomPaint(
                size: Size(w, h),
                painter: LyricsWallNextPainter(_f, _repaint),
              ),
            ),
          ),
        );
      },
    );
  }

  // ---- 测试探针 -----------------------------------------------------------

  @visibleForTesting
  String debugEngine() => 'next';

  @visibleForTesting
  int debugAnchor() => _anchorIdx;

  @visibleForTesting
  int debugActive() => _f.active;

  @visibleForTesting
  double debugOffset() => _motion.offset;

  @visibleForTesting
  double debugFrameOffset() => _f.offset;

  @visibleForTesting
  double debugVelocity() => _motion.velocity;

  @visibleForTesting
  bool debugMoving() => _motion.moving;

  @visibleForTesting
  int debugClockMs() => _clock.valueMs;

  @visibleForTesting
  double debugUser() => _user;

  @visibleForTesting
  (double, double) debugUserBounds() => (_userMin, _userMax);

  @visibleForTesting
  bool debugUserScrolling() => _f.userScrolling;

  @visibleForTesting
  List<double> debugCenters() =>
      List.of(_geometry?.centers ?? const <double>[]);

  @visibleForTesting
  List<double> debugHeights() =>
      List.of(_geometry?.heights ?? const <double>[]);

  @visibleForTesting
  List<double> debugY() => [
    for (var i = 0; i < (_geometry?.length ?? 0); i++)
      _geometry!.centers[i] + _motion.offset,
  ];

  @visibleForTesting
  double debugFade(int index) => _f.visuals.fadeAt(index);

  @visibleForTesting
  double debugBlur(int index) => _f.visuals.blurAt(index);

  @visibleForTesting
  double debugScale(int index) =>
      _f.visuals.scaleAt(index, enableScale: widget.enableScale);

  @visibleForTesting
  WallBlurMode debugBlurMode() => _blurModeResolved;

  @visibleForTesting
  double debugPanelBlur() => _blurStrength.value;

  @visibleForTesting
  bool debugBlurDegraded() => _blurDegraded;

  @visibleForTesting
  void debugNoteFrame({required double rasterMs, double uiMs = 0}) =>
      _noteFrame(rasterMs: rasterMs, uiMs: uiMs);

  @visibleForTesting
  void debugForceBlurMode(WallBlurMode? mode) {
    _debugModeOverride = mode;
    _syncStyle();
    _ensureTicker();
    _repaint.notify();
  }

  @visibleForTesting
  int debugCacheEntries() => _f.cache.entryCount;

  @visibleForTesting
  int debugFragCacheEntries() => _f.fragCache.entryCount;

  @visibleForTesting
  int debugSubFragCacheEntries() =>
      _f.transFragCache.entryCount + _f.romaFragCache.entryCount;

  @visibleForTesting
  int debugMeasuredCount() => _geometry?.measured.where((m) => m).length ?? 0;

  @visibleForTesting
  int debugWindowStart() => _winStart;

  @visibleForTesting
  int debugWindowEnd() => _winEnd;

  @visibleForTesting
  bool debugBakeActive() => _f.lastFrameBaked;

  @visibleForTesting
  bool debugStaticIdle() => _f.staticIdle;

  @visibleForTesting
  int debugBakeCount() => _f.bake.bakedCount;

  @visibleForTesting
  int debugBakedLineCount() => _f.bake.cachedCount;

  @visibleForTesting
  void debugSetBakeEnabled(bool value) {
    _f.bakeEnabled = value;
    _ensureTicker();
    _repaint.notify();
  }

  @visibleForTesting
  bool debugFlyIn() => _flyIn;
}
