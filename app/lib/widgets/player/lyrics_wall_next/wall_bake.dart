// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 整层档（panel）稳态「逐行烘焙」缓存。
///
/// 稳态时非激活行的模糊外观只取决于行内容与样式，与屏幕 y、激活行、
/// 淡入权重无关。这里把每行按物理分辨率栅格、降采样到 1/4 逻辑网格、
/// 在 1/4 网格上做 blur(σ·k)，缓存成 [ui.Image]；每帧只贴图 + 重绘激活行，
/// 从而消掉整层高斯 saveLayer 与全部非激活行的段落重放。
///
/// 与旧引擎的 D2a 实现同源，但缓存键与绘制入口按新四层模型调整：
/// - 位置无关：滚动/换行不会使缓存失效，只对新进入视口的行补烘；
/// - 每帧最多烘一张（`prefetchOne`），避免换行首帧批量烘焙尖峰；
/// - 失败（纹理超限等）在本样式周期内降级为逐行高斯，不重试。
library;

import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/painting.dart';

import 'wall_frame.dart';
import 'wall_tuning.dart';

/// 一行烘焙结果：1/4 逻辑分辨率的失焦外观 + 相对行中心的纵向范围。
class BakedWallLine {
  const BakedWallLine({
    required this.image,
    required this.top,
    required this.bottom,
  });

  final ui.Image image;

  /// 相对行中心 `cy` 的上下边界（逻辑像素，已含内容 pad 与模糊余量）。
  final double top;
  final double bottom;
}

/// 烘焙一行的基座绘制回调：在已缩放/平移的画布上画第 [index] 行。
typedef WallBakeBasePainter = void Function(ui.Canvas canvas, int index);

/// 逐行烘焙缓存。
class WallBake {
  final Map<int, BakedWallLine> _lines = <int, BakedWallLine>{};
  final Set<int> _failed = <int>{};
  Object? _styleKey;

  /// 累计烘焙行数（诊断：换行不应使其大幅增长）。
  int bakedCount = 0;

  /// 当前缓存的烘焙图数量。
  int get cachedCount => _lines.length;

  /// 内容/样式键：**不含** y、激活行、淡入权重、可见集合。
  Object _keyOf(WallFrame f) => (
    groups: identityHashCode(f.groups),
    w: f.width,
    fontSize: f.fontSize,
    fontFamily: f.fontFamily,
    weight: f.fontWeight.value,
    played: f.played.toARGB32(),
    unplayed: f.unplayed.toARGB32(),
    inactiveAlpha: f.inactiveAlpha,
    textAlign: f.textAlign,
    showTranslation: f.showTranslation,
    showRomanization: f.showRomanization,
    sigma: kWallPanelBlurSigma * f.blurStrength.clamp(0.0, 1.0),
    scale: f.enableScale ? 0.97 : 1.0,
    dpr: f.dpr,
  );

  /// 取某行的烘焙图（**只查不烘**）。
  BakedWallLine? lookup(WallFrame f, int index) {
    _ensureKey(f);
    return _lines[index];
  }

  /// 稳态帧预取：为可见范围上下各 2 行中尚未烘焙的行烘一张。
  ///
  /// 每帧最多烘一张，把成本摊到稳态帧，避免新行进入视口那一帧的尖峰。
  bool prefetchOne({
    required WallFrame f,
    required Set<int> visible,
    required WallBakeBasePainter paintBase,
  }) {
    if (visible.isEmpty) return false;
    _ensureKey(f);
    final n = f.groups.length;
    var minV = n;
    var maxV = -1;
    for (final i in visible) {
      if (i < minV) minV = i;
      if (i > maxV) maxV = i;
    }
    final from = math.max(0, minV - 2);
    final to = math.min(n - 1, maxV + 2);
    for (var i = from; i <= to; i++) {
      if (_lines.containsKey(i) || _failed.contains(i)) continue;
      final baked = _bakeLine(f, i, paintBase);
      if (baked != null) {
        _lines[i] = baked;
        bakedCount += 1;
      } else {
        _failed.add(i);
      }
      return true;
    }
    return false;
  }

  void _ensureKey(WallFrame f) {
    final key = _keyOf(f);
    if (key == _styleKey) return;
    _styleKey = key;
    _clear();
    _failed.clear();
  }

  BakedWallLine? _bakeLine(
    WallFrame f,
    int index,
    WallBakeBasePainter paintBase,
  ) {
    const k = kWallPanelBlurDownsample;
    final sigma = kWallPanelBlurSigma * f.blurStrength.clamp(0.0, 1.0);
    final geometry = f.geometry;
    if (geometry == null || index < 0 || index >= geometry.length) return null;

    // 行 bounds 与位置无关（以 cy = 0 计算），再扩「内容 pad + 3σ」余量。
    final h = geometry.heights[index];
    final pad = f.fontSize * 0.5;
    final margin = pad + 3 * sigma;
    final top = -h / 2 - pad - margin;
    final bottom = h / 2 + pad + margin;
    final height = bottom - top;
    if (height <= 0 || f.width <= 0) return null;

    // 物理分辨率上限保护（极端超宽视口时降低精度，不崩）。
    final dpr = math.min(f.dpr, 8192 / math.max(1.0, f.width));
    final wPx = math.max(1, (f.width * dpr).round());
    final hPx = math.max(1, (height * dpr).round());
    final kw = math.max(1, (f.width * k).round());
    final kh = math.max(1, (height * k).round());

    try {
      // 1) 行内容按物理分辨率栅格；透明度按 1.0 烘焙，绘制时再乘实际
      //    alpha——高斯对 alpha 是线性的，二者等价。
      final rec1 = ui.PictureRecorder();
      final cv1 = ui.Canvas(
        rec1,
        Rect.fromLTWH(0, 0, wPx.toDouble(), hPx.toDouble()),
      );
      cv1.scale(dpr);
      cv1.translate(0, -top);
      paintBase(cv1, index);
      final content = rec1.endRecording();
      final img1 = content.toImageSync(wPx, hPx);
      content.dispose();

      // 2) 双线性降采样到 1/4 逻辑网格。
      final rec2 = ui.PictureRecorder();
      final cv2 = ui.Canvas(
        rec2,
        Rect.fromLTWH(0, 0, kw.toDouble(), kh.toDouble()),
      );
      cv2.drawImageRect(
        img1,
        Rect.fromLTWH(0, 0, wPx.toDouble(), hPx.toDouble()),
        Rect.fromLTWH(0, 0, kw.toDouble(), kh.toDouble()),
        Paint()..filterQuality = ui.FilterQuality.low,
      );
      final imgDown = rec2.endRecording().toImageSync(kw, kh);
      img1.dispose();

      // 3) 在 1/4 网格上 blur(σ·k)。
      final rec3 = ui.PictureRecorder();
      final cv3 = ui.Canvas(
        rec3,
        Rect.fromLTWH(0, 0, kw.toDouble(), kh.toDouble()),
      );
      cv3.saveLayer(
        Rect.fromLTWH(0, 0, kw.toDouble(), kh.toDouble()),
        Paint()
          ..imageFilter = ui.ImageFilter.blur(
            sigmaX: sigma * k,
            sigmaY: sigma * k,
          ),
      );
      cv3.drawImage(imgDown, Offset.zero, Paint());
      cv3.restore();
      final baked = rec3.endRecording().toImageSync(kw, kh);
      imgDown.dispose();

      return BakedWallLine(image: baked, top: top, bottom: bottom);
    } on Object {
      return null;
    }
  }

  /// 只保留可见行及其上下各 2 行的烘焙图（常驻内存 O(视口)）。
  void pruneTo(Set<int> visible) {
    if (_lines.isEmpty) return;
    if (visible.isEmpty) {
      _clear();
      return;
    }
    var minV = 1 << 30;
    var maxV = -1;
    for (final i in visible) {
      if (i < minV) minV = i;
      if (i > maxV) maxV = i;
    }
    final from = math.max(0, minV - 2);
    final to = maxV + 2;
    _lines.removeWhere((index, baked) {
      if (index >= from && index <= to) return false;
      baked.image.dispose();
      return true;
    });
  }

  void _clear() {
    for (final baked in _lines.values) {
      baked.image.dispose();
    }
    _lines.clear();
  }

  /// 释放全部烘焙图。
  void dispose() {
    _clear();
    _failed.clear();
    _styleKey = null;
  }
}
