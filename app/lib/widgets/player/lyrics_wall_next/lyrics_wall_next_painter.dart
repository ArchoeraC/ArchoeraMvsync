// ArchoeraMusic UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词墙渲染层：只读取 [WallFrame]，不做任何时间/运动决策。
///
/// 失焦路径：
/// - [WallBlurMode.panel]：所有非激活行合成到**一个**离屏层统一高斯；
/// - [WallBlurMode.perLine]：逐行离屏（最贴 AMLL 的半径梯度，最贵）；
/// - [WallBlurMode.lite]：放大 4% + 低透明重绘，零高斯；
/// - [WallBlurMode.off]：只剩透明度景深。
///
/// 逐字扫亮只给正在扫过的那一个词开羽化遮罩，稳态每帧离屏层数 ≤ 2。
library;

import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:material_ui/material_ui.dart';

import '../../../services/lyrics/lyric_line.dart';
import '../lyrics_edge_fade.dart' show lyricsEdgeFadeAt;
import '../lyrics_v7/lyrics_fragment_render.dart';
import '../lyrics_v7/lyrics_layout.dart';
import '../lyrics_v7/lyrics_word_anim.dart';
import 'wall_bake.dart';
import 'wall_frame.dart';
import 'wall_geometry.dart';
import 'wall_tuning.dart';

class LyricsWallNextPainter extends CustomPainter {
  LyricsWallNextPainter(this.f, Listenable repaint) : super(repaint: repaint);

  final WallFrame f;

  static const double _widthPad = 24;
  static const double _unsungAlpha = 0.4;
  static const double _liteGhostScale = 1.04;
  static const double _liteGhostAlpha = 0.35;
  static const double _litAlpha = 1.0;

  @override
  void paint(Canvas canvas, Size size) {
    final g = f.geometry;
    if (g == null || f.groups.isEmpty || g.length == 0) return;

    final n = math.min(f.groups.length, g.length);
    final viewH = f.height > 0 ? f.height : size.height;
    final start = f.winStart.clamp(0, n);
    final end = f.winEnd.clamp(start, n);
    if (end <= start) return;

    final visible = <int>[];
    final keepFrag = <int>{};
    final fades = <int, double>{};
    final hideBoundary = f.active >= 0 ? f.active : (f.anchor + 1);

    for (var i = start; i < end; i++) {
      final cy = g.centers[i] + f.offset;
      final half = g.heights[i] / 2;
      if (cy + half < 0 || cy - half > viewH) continue;
      if (f.hidePassed && hideBoundary > 0 && i < hideBoundary) continue;
      final fade = f.visuals.fadeAt(i);
      if (fade > 0.02) keepFrag.add(i);
      visible.add(i);
      fades[i] = fade;
    }
    if (visible.isEmpty) {
      f.cache.pruneTo(const {});
      f.fragCache.pruneTo(const {});
      f.transFragCache.pruneTo(const {});
      f.romaFragCache.pruneTo(const {});
      f.bake.pruneTo(const {});
      f.lastFrameBaked = false;
      return;
    }

    final mode = f.blurMode;
    final sigma = kWallPanelBlurSigma * f.blurStrength.clamp(0.0, 1.0);
    if (mode == WallBlurMode.panel && sigma > 0.05) {
      _paintPanel(canvas, visible, fades, sigma);
    } else {
      f.lastFrameBaked = false;
      for (final i in visible) {
        final blur = mode == WallBlurMode.perLine && i != f.active
            ? f.visuals.blurAt(i)
            : 0.0;
        _drawLine(canvas, i, g.centers[i] + f.offset, fades[i]!, blur: blur);
      }
    }

    final keep = visible.toSet();
    f.cache.pruneTo(keep);
    f.fragCache.pruneTo(keepFrag);
    f.transFragCache.pruneTo(keepFrag);
    f.romaFragCache.pruneTo(keepFrag);
    f.bake.pruneTo(keep);
  }

  /// 整层档（panel）：稳态走逐行烘焙贴图，过渡帧基座优先复用烘焙图。
  void _paintPanel(
    Canvas canvas,
    List<int> visible,
    Map<int, double> fades,
    double sigma,
  ) {
    final g = f.geometry!;
    final steady = _panelSteady(visible, fades);
    var baked = false;
    if (f.bakeEnabled && steady) {
      // 稳态贴图：整层高斯已在烘焙图里，每帧只剩逐行贴图 + 激活行。
      baked = true;
      for (final i in visible) {
        if (i == f.active) continue;
        final cy = g.centers[i] + f.offset;
        if ((fades[i] ?? 0) > 0.005) continue;
        final line = f.bake.lookup(f, i);
        if (line == null) {
          // 尚未烘焙：本帧回退逐行高斯（观感等价），由下面预取摊开。
          _drawLine(canvas, i, cy, 0, blur: sigma);
          continue;
        }
        final alpha = (_alphaFor(i) * _edgeAlphaAt(cy)).clamp(0.0, 1.0);
        if (alpha > 0.002) _drawBaked(canvas, line, cy, alpha);
      }
      _drawActiveOverlay(canvas, visible, fades, g);
      // 每帧最多烘一张（视口上下各 2 行），把成本摊到稳态帧。
      f.bake.prefetchOne(
        f: f,
        visible: visible.toSet(),
        paintBase: (cv, index) => _drawBase(
          cv,
          index,
          0.0,
          0.0,
          alphaOverride: 1.0,
          scaleOverride: f.enableScale ? 0.97 : 1.0,
        ),
      );
    } else {
      // 过渡帧：基座优先用位置无关的烘焙图（行在动但模糊外观不变），
      // 无缓存/强度动画中才回退逐行高斯；激活外观始终逐帧重绘。
      final useCache = f.bakeEnabled && f.blurStrengthSettled;
      for (final i in visible) {
        final cy = g.centers[i] + f.offset;
        final fade = fades[i] ?? 0;
        final line = useCache ? f.bake.lookup(f, i) : null;
        if (line == null) {
          _drawLine(canvas, i, cy, fade, blur: i == f.active ? 0 : sigma);
          continue;
        }
        final baseAlpha = (_alphaFor(i) * (1 - fade) * _edgeAlphaAt(cy)).clamp(
          0.0,
          1.0,
        );
        if (baseAlpha > 0.004) _drawBaked(canvas, line, cy, baseAlpha);
        _drawActiveLayer(canvas, i, cy, fade, edge: _edgeAlphaAt(cy));
      }
    }
    f.lastFrameBaked = baked;
  }

  void _drawActiveOverlay(
    Canvas canvas,
    List<int> visible,
    Map<int, double> fades,
    WallGeometry g,
  ) {
    for (final i in visible) {
      final cy = g.centers[i] + f.offset;
      _drawActiveLayer(canvas, i, cy, fades[i] ?? 0, edge: _edgeAlphaAt(cy));
    }
  }

  /// 稳态判定：无动画、失焦强度已到目标、激活外观完整、其余行已熄灭。
  ///
  /// 缩放由 fade 直接推导（非激活恒 0.97），因此无需再单独比对。
  bool _panelSteady(List<int> visible, Map<int, double> fades) {
    if (!f.staticIdle || !f.blurStrengthSettled) return false;
    if (f.active >= f.groups.length) return false;
    if (f.active >= 0 && (fades[f.active] ?? 0) < 0.995) return false;
    for (final i in visible) {
      if (i == f.active) continue;
      if ((fades[i] ?? 0) > 0.005) return false;
    }
    return true;
  }

  void _drawBaked(Canvas canvas, BakedWallLine line, double cy, double alpha) {
    canvas.drawImageRect(
      line.image,
      Rect.fromLTWH(
        0,
        0,
        line.image.width.toDouble(),
        line.image.height.toDouble(),
      ),
      Rect.fromLTRB(0, cy + line.top, math.max(1.0, f.width), cy + line.bottom),
      Paint()
        ..filterQuality = ui.FilterQuality.low
        ..color = const Color(0xFFFFFFFF).withValues(alpha: alpha),
    );
  }

  double _alphaFor(int index) {
    final isBg = index < f.groups.length && f.groups[index].isBG;
    final focus = f.active >= 0 ? f.active : f.anchor;
    var a = 1.0;
    if (!f.userScrolling) {
      final d = focus < 0 ? 99.0 : (index - focus).abs().toDouble();
      a = math
          .max(f.inactiveAlpha, 1 - (math.max(0.0, d - 1) * 0.35))
          .clamp(0.0, 1.0);
    }
    if (isBg) a *= 0.4;
    return a;
  }

  double _edgeAlphaAt(double cy) {
    if (!f.edgeFade) return 1.0;
    final viewH = f.height;
    if (viewH <= 0) return 1.0;
    return lyricsEdgeFadeAt(cy / viewH);
  }

  double _lineHeightAt(int index) =>
      index < f.geometry!.heights.length ? f.geometry!.heights[index] : 40.0;

  Rect _lineBounds(int index, double cy) {
    final h = _lineHeightAt(index);
    final pad = f.fontSize * 0.5;
    return Rect.fromLTRB(
      0,
      cy - h / 2 - pad,
      math.max(1.0, f.width),
      cy + h / 2 + pad,
    );
  }

  void _drawLine(
    Canvas canvas,
    int index,
    double cy,
    double fade, {
    required double blur,
  }) {
    final blurLayer = blur > 0.05;
    if (blurLayer) {
      canvas.saveLayer(
        _lineBounds(index, cy),
        Paint()..imageFilter = ui.ImageFilter.blur(sigmaX: blur, sigmaY: blur),
      );
    }
    final edge = _edgeAlphaAt(cy);
    _drawBase(canvas, index, cy, fade, edge: edge);
    _drawActiveLayer(canvas, index, cy, fade, edge: edge);
    if (blurLayer) canvas.restore();
  }

  /// 基础层：非激活外观。`fade → 1` 时完全被激活外观盖住，可跳过。
  ///
  /// [alphaOverride] / [scaleOverride] 供烘焙使用：把透明度归一为 1、缩放固定
  /// 为稳态值后栅格一次，绘制时再乘实际 alpha（高斯对 alpha 线性，等价）。
  void _drawBase(
    Canvas canvas,
    int index,
    double cy,
    double fade, {
    double? alphaOverride,
    double? scaleOverride,
    double edge = 1.0,
  }) {
    if (fade >= 0.995) return;
    final group = f.groups[index];
    final fs = group.isBG ? f.fontSize * kBgFontScale : f.fontSize;
    final maxWidth = math.max(40.0, f.width - _widthPad);
    final alpha = (alphaOverride ?? _alphaFor(index)) * edge;
    if (alpha <= 0.002) return;
    final scale =
        scaleOverride ?? f.visuals.scaleAt(index, enableScale: f.enableScale);
    final base = _solidParagraph(
      index,
      group,
      active: false,
      alpha: alpha,
      fs: fs,
      maxWidth: maxWidth,
    );
    final crossfade = fade > 0.005;
    if (crossfade) {
      canvas.saveLayer(
        _lineBounds(index, cy),
        Paint()..color = const Color(0xFFFFFFFF).withValues(alpha: 1 - fade),
      );
    }
    final h = _lineHeightAt(index);
    canvas.save();
    canvas.translate(f.width / 2, cy);
    canvas.scale(scale);
    canvas.translate(-base.width / 2, -h / 2);
    canvas.drawParagraph(base, Offset.zero);
    canvas.restore();
    if (f.blurMode == WallBlurMode.lite && f.blurStrength > 0.02) {
      final ghost = _solidParagraph(
        index,
        group,
        active: false,
        alpha: alpha * _liteGhostAlpha * f.blurStrength,
        fs: fs,
        maxWidth: maxWidth,
      );
      canvas.save();
      canvas.translate(f.width / 2, cy);
      canvas.scale(scale * _liteGhostScale);
      canvas.translate(-ghost.width / 2, -h / 2);
      canvas.drawParagraph(ghost, Offset.zero);
      canvas.restore();
    }
    _drawSubLines(
      canvas,
      index,
      group,
      cy - h / 2 + base.height,
      active: false,
      alpha: alpha,
      fs: fs,
      maxWidth: maxWidth,
    );
    if (crossfade) canvas.restore();
  }

  void _drawActiveLayer(
    Canvas canvas,
    int index,
    double cy,
    double fade, {
    double edge = 1.0,
  }) {
    final a = (fade * edge).clamp(0.0, 1.0);
    if (a <= 0.005) return;
    final group = f.groups[index];
    final fs = group.isBG ? f.fontSize * kBgFontScale : f.fontSize;
    final maxWidth = math.max(40.0, f.width - _widthPad);
    final scale = f.visuals.scaleAt(index, enableScale: f.enableScale);
    if (a >= 0.995) {
      canvas.save();
      canvas.clipRect(_lineBounds(index, cy));
      _drawActive(canvas, index, group, cy, scale, fs, maxWidth);
      canvas.restore();
      return;
    }
    canvas.saveLayer(
      _lineBounds(index, cy),
      Paint()..color = const Color(0xFFFFFFFF).withValues(alpha: a),
    );
    _drawActive(canvas, index, group, cy, scale, fs, maxWidth);
    canvas.restore();
  }

  void _drawActive(
    Canvas canvas,
    int index,
    LyricGroup group,
    double cy,
    double scale,
    double fs,
    double maxWidth,
  ) {
    final h = _lineHeightAt(index);
    final fr = _fragRender(index, group, fs, maxWidth);
    if (fr != null) {
      canvas.save();
      canvas.translate(f.width / 2, cy);
      canvas.scale(scale);
      canvas.translate(-fr.width / 2, -h / 2);
      _drawFragments(canvas, group, fr, fs, maxWidth);
      canvas.restore();
      _drawSubLines(
        canvas,
        index,
        group,
        cy - h / 2 + fr.height,
        active: true,
        alpha: 1,
        fs: fs,
        maxWidth: maxWidth,
      );
      return;
    }
    final main = _solidParagraph(
      index,
      group,
      active: true,
      alpha: 1,
      fs: fs,
      maxWidth: maxWidth,
    );
    canvas.save();
    canvas.translate(f.width / 2, cy);
    canvas.scale(scale);
    canvas.translate(-main.width / 2, -h / 2);
    canvas.drawParagraph(main, Offset.zero);
    canvas.restore();
    _drawSubLines(
      canvas,
      index,
      group,
      cy - h / 2 + main.height,
      active: true,
      alpha: 1,
      fs: fs,
      maxWidth: maxWidth,
    );
  }

  void _drawFragments(
    Canvas canvas,
    LyricGroup group,
    LyricsFragmentRender fr,
    double fs,
    double maxWidth, {
    double lineHeightEm = kLyricLineHeightEm,
  }) {
    final lineStart = group.original.timeMs;
    final pos = f.positionMs;
    for (var k = 0; k < fr.length; k++) {
      final box = fr.boxes[k];
      final startAbs = lineStart + fr.startMs[k];
      final dur = fr.durationMs[k];
      final t = pos - startAbs;
      final anim = _wordAnim(k, fr, dur, pos - lineStart, fs);

      canvas.save();
      final cx = box.left + box.width / 2;
      final cy = box.top + box.height / 2;
      if (anim.dx != 0 || anim.dy != 0 || anim.scale != 1.0) {
        canvas.translate(cx + anim.dx, cy + anim.dy);
        canvas.scale(anim.scale);
        canvas.translate(-cx, -cy);
      }
      if (t >= dur) {
        canvas.drawParagraph(fr.lit[k], Offset(box.left, box.top));
      } else if (t <= 0) {
        canvas.drawParagraph(fr.dim[k], Offset(box.left, box.top));
      } else {
        canvas.drawParagraph(fr.dim[k], Offset(box.left, box.top));
        _drawSweepLit(canvas, fr, k, box, t / dur);
      }
      canvas.restore();
    }
  }

  void _drawSweepLit(
    Canvas canvas,
    LyricsFragmentRender fr,
    int k,
    FragBox box,
    double p,
  ) {
    final lit = fr.lit[k];
    final rect = Rect.fromLTWH(
      box.left - 1,
      box.top - 1,
      box.width + 2,
      box.height + 2,
    );
    final width = math.max(1.0, box.width);
    final feather = (f.wordFadeWidth * f.fontSize).clamp(1.0, width * 2);
    final x0 = width * p.clamp(0.0, 1.0);
    var a = ((x0 - feather / 2) / width).clamp(0.0, 1.0);
    var b = ((x0 + feather / 2) / width).clamp(0.0, 1.0);
    if (b <= a) b = math.min(1.0, a + 1e-3);
    final shader = ui.Gradient.linear(
      Offset(box.left, box.top),
      Offset(box.left + width, box.top),
      const [Color(0xFFFFFFFF), Color(0x00FFFFFF)],
      [a, b],
    );
    canvas.saveLayer(rect, Paint());
    canvas.drawParagraph(lit, Offset(box.left, box.top));
    canvas.drawRect(
      rect,
      Paint()
        ..blendMode = BlendMode.dstIn
        ..shader = shader,
    );
    canvas.restore();
  }

  WordAnim _wordAnim(
    int k,
    LyricsFragmentRender fr,
    int dur,
    int lineRelMs,
    double fs,
  ) => resolveWordAnim(
    text: fr.texts[k],
    index: k,
    count: fr.length,
    relStartMs: fr.startMs[k],
    durationMs: dur,
    lineRelMs: lineRelMs,
    fontSize: fs,
  );

  LyricsFragmentRender? _fragRender(
    int index,
    LyricGroup group,
    double fs,
    double maxWidth,
  ) {
    final frags = group.fragments;
    if (!f.wordSweep || frags == null || frags.isEmpty) return null;
    final key = (
      group.original.text,
      f.fontFamily,
      fs,
      f.fontWeight.value,
      maxWidth,
      f.played.toARGB32(),
      _unsungAlpha,
      _litAlpha,
      f.textAlign,
    );
    return f.fragCache.obtain(
      index,
      key,
      () => buildLyricsFragmentRender(
        fragments: frags,
        lineText: group.original.text,
        fontFamily: f.fontFamily,
        fontSize: fs,
        weight: f.fontWeight,
        maxWidth: maxWidth,
        played: f.played,
        unsungAlpha: _unsungAlpha,
        litAlpha: _litAlpha,
        textAlign: f.textAlign,
      ),
    );
  }

  ui.Paragraph _solidParagraph(
    int index,
    LyricGroup group, {
    required bool active,
    required double alpha,
    required double fs,
    required double maxWidth,
  }) {
    final weight = active ? f.fontWeight : FontWeight.w400;
    final color = active
        ? f.played.withValues(alpha: alpha)
        : f.unplayed.withValues(alpha: alpha);
    final key = (
      group.original.text,
      f.fontFamily,
      fs,
      weight.value,
      color.toARGB32(),
      maxWidth,
      f.textAlign,
    );
    return f.cache.obtainMain(index, key, () {
      final b = ui.ParagraphBuilder(
        _pStyle(f.fontFamily, fs, weight, textAlign: f.textAlign),
      );
      b.pushStyle(_uiStyle(f.fontFamily, fs, weight, color));
      b.addText(group.original.text);
      b.pop();
      return _layout(b, maxWidth);
    });
  }

  void _drawSubLines(
    Canvas canvas,
    int index,
    LyricGroup group,
    double mainBottom, {
    required bool active,
    required double alpha,
    required double fs,
    required double maxWidth,
  }) {
    var below = mainBottom;
    final subFs = lyricTranslationFontSize(fs);
    final tr = group.translation;
    if (f.showTranslation && tr != null && tr.isNotEmpty) {
      final fr = active
          ? _subFragRender(
              index,
              group,
              roman: false,
              fs: fs,
              maxWidth: maxWidth,
            )
          : null;
      if (fr != null) {
        canvas.save();
        canvas.translate(f.width / 2 - fr.width / 2, below);
        _drawFragments(
          canvas,
          group,
          fr,
          subFs,
          maxWidth,
          lineHeightEm: kLyricTranslationLineHeightEm,
        );
        canvas.restore();
        below += fr.height;
      } else {
        final sub = _translationParagraph(
          index,
          tr,
          active,
          alpha,
          fs,
          maxWidth,
        );
        canvas.drawParagraph(sub, Offset(f.width / 2 - sub.width / 2, below));
        below += sub.height;
      }
    }
    final ro = group.romaji;
    if (f.showRomanization && ro != null && ro.isNotEmpty) {
      final fr = active
          ? _subFragRender(
              index,
              group,
              roman: true,
              fs: fs,
              maxWidth: maxWidth,
            )
          : null;
      if (fr != null) {
        canvas.save();
        canvas.translate(f.width / 2 - fr.width / 2, below);
        _drawFragments(
          canvas,
          group,
          fr,
          subFs,
          maxWidth,
          lineHeightEm: kLyricTranslationLineHeightEm,
        );
        canvas.restore();
        below += fr.height;
      } else {
        final sub = _romajiParagraph(index, ro, active, alpha, fs, maxWidth);
        canvas.drawParagraph(sub, Offset(f.width / 2 - sub.width / 2, below));
        below += sub.height;
      }
    }
  }

  LyricsFragmentRender? _subFragRender(
    int index,
    LyricGroup group, {
    required bool roman,
    required double fs,
    required double maxWidth,
  }) {
    if (!f.wordSweep) return null;
    final frags = roman ? group.romajiFragments : group.translationFragments;
    final text = roman ? group.romaji : group.translation;
    if (frags == null || frags.isEmpty || text == null || text.isEmpty) {
      return null;
    }
    final subFs = lyricTranslationFontSize(fs);
    final litAlpha = roman ? 0.65 : 0.8;
    final unsungAlpha = litAlpha * _unsungAlpha;
    final cache = roman ? f.romaFragCache : f.transFragCache;
    final key = (
      text,
      f.fontFamily,
      subFs,
      maxWidth,
      f.played.toARGB32(),
      unsungAlpha,
      litAlpha,
      f.textAlign,
    );
    return cache.obtain(
      index,
      key,
      () => buildLyricsFragmentRender(
        fragments: frags,
        lineText: text,
        fontFamily: f.fontFamily,
        fontSize: subFs,
        weight: FontWeight.w400,
        maxWidth: maxWidth,
        played: f.played,
        unsungAlpha: unsungAlpha,
        litAlpha: litAlpha,
        lineHeightEm: kLyricTranslationLineHeightEm,
        textAlign: f.textAlign,
      ),
    );
  }

  ui.Paragraph _romajiParagraph(
    int index,
    String text,
    bool active,
    double alpha,
    double fs,
    double maxWidth,
  ) {
    final color = active
        ? f.played.withValues(alpha: 0.65)
        : f.unplayed.withValues(alpha: alpha * 0.85);
    final subFs = lyricTranslationFontSize(fs);
    final key = (
      text,
      f.fontFamily,
      subFs,
      color.toARGB32(),
      maxWidth,
      f.textAlign,
    );
    return f.cache.obtainRomanization(index, key, () {
      final b = ui.ParagraphBuilder(
        _pStyle(
          f.fontFamily,
          subFs,
          FontWeight.w400,
          height: kLyricTranslationLineHeightEm,
          textAlign: f.textAlign,
        ),
      );
      b.pushStyle(
        _uiStyle(
          f.fontFamily,
          subFs,
          FontWeight.w400,
          color,
          height: kLyricTranslationLineHeightEm,
        ),
      );
      b.addText(text);
      b.pop();
      return _layout(b, maxWidth);
    })!;
  }

  ui.Paragraph _translationParagraph(
    int index,
    String text,
    bool active,
    double alpha,
    double fs,
    double maxWidth,
  ) {
    final color = active
        ? f.played.withValues(alpha: 0.8)
        : f.unplayed.withValues(alpha: alpha);
    final subFs = lyricTranslationFontSize(fs);
    final key = (
      text,
      f.fontFamily,
      subFs,
      color.toARGB32(),
      maxWidth,
      f.textAlign,
    );
    return f.cache.obtainTranslation(index, key, () {
      final b = ui.ParagraphBuilder(
        _pStyle(
          f.fontFamily,
          subFs,
          FontWeight.w400,
          height: kLyricTranslationLineHeightEm,
          textAlign: f.textAlign,
        ),
      );
      b.pushStyle(
        _uiStyle(
          f.fontFamily,
          subFs,
          FontWeight.w400,
          color,
          height: kLyricTranslationLineHeightEm,
        ),
      );
      b.addText(text);
      b.pop();
      return _layout(b, maxWidth);
    })!;
  }

  static ui.ParagraphStyle _pStyle(
    String? family,
    double fs,
    FontWeight w, {
    double height = kLyricLineHeightEm,
    ui.TextAlign textAlign = ui.TextAlign.center,
  }) => ui.ParagraphStyle(
    textAlign: textAlign,
    textDirection: ui.TextDirection.ltr,
    fontFamily: family,
    fontSize: fs,
    fontWeight: w,
    height: height,
  );

  static ui.TextStyle _uiStyle(
    String? family,
    double fs,
    FontWeight w,
    Color color, {
    double height = kLyricLineHeightEm,
    List<ui.Shadow>? shadows,
  }) => ui.TextStyle(
    color: color,
    fontFamily: family,
    fontSize: fs,
    fontWeight: w,
    height: height,
    shadows: shadows,
  );

  static ui.Paragraph _layout(ui.ParagraphBuilder b, double maxWidth) {
    final p = b.build();
    p.layout(ui.ParagraphConstraints(width: maxWidth));
    return p;
  }

  @override
  bool shouldRepaint(LyricsWallNextPainter old) => false;
}

/// 画家重绘通知器。
class WallRepaint extends ChangeNotifier {
  void notify() => notifyListeners();
}
