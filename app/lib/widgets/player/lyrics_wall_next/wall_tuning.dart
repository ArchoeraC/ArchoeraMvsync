// ArchoeraMvsumc UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 新一代歌词墙的档位、时间常量与弹簧参数。
///
/// 与旧 `lyrics_v7` 并行的独立实现：这里不引用旧墙体库，便于验收后整体
/// 替换。观感目标沿用 docs/lyrics-amll-alignment.md，但内部模型重写为
/// 「时间轴 / 几何 / 运动 / 渲染」四层。
library;

import 'dart:io' show Platform;
import 'dart:math' as math;

import '../../../utils/spring.dart';

/// 非激活行最大失焦半径（逻辑像素，对应 AMLL `blur(1+distance)` 的 5px 上限）。
const double kWallMaxBlurPx = 5.0;

/// 整层失焦的等效高斯 σ。
const double kWallPanelBlurSigma = 4.0;

/// 整层档稳态烘焙的降采样倍率（1/4 逻辑分辨率）。
const double kWallPanelBlurDownsample = 0.25;

/// 失焦画法档位。
enum WallBlurMode {
  /// 整层一次失焦：非激活行合成到同一离屏层后统一高斯。
  panel,

  /// 逐行失焦：每行独立 σ = `min(5, 1+距离)`，最贴 AMLL，也最吃 GPU。
  perLine,

  /// 轻量近似：放大 + 低透明重绘，不跑高斯。
  lite,

  /// 不失焦。
  off,
}

/// 用户可选的失焦档位。
enum WallBlurQuality {
  auto('auto'),
  fast('fast'),
  lite('lite'),
  quality('quality'),
  off('off');

  const WallBlurQuality(this.key);

  final String key;

  static WallBlurQuality parse(String? raw) {
    for (final q in values) {
      if (q.key == raw) return q;
    }
    return WallBlurQuality.auto;
  }
}

/// 诊断用环境变量：`ARCHOERA_LYRICS_BLUR=perline|panel|lite|off`。
WallBlurMode? wallBlurOverrideFrom(String? raw) {
  switch (raw?.trim().toLowerCase()) {
    case 'perline':
    case 'per-line':
    case 'per_line':
      return WallBlurMode.perLine;
    case 'panel':
      return WallBlurMode.panel;
    case 'lite':
      return WallBlurMode.lite;
    case 'off':
    case 'none':
    case '0':
      return WallBlurMode.off;
    default:
      return null;
  }
}

/// 进程启动时的环境变量覆盖（只读一次）。
final WallBlurMode? wallBlurOverride = wallBlurOverrideFrom(
  Platform.environment['ARCHOERA_LYRICS_BLUR'],
);

/// 解析最终生效的失焦画法。环境变量优先；仅 auto 吃自动降级。
WallBlurMode resolveWallBlurMode({
  required WallBlurQuality quality,
  WallBlurMode? override,
  required bool autoDegraded,
}) {
  if (override != null) return override;
  switch (quality) {
    case WallBlurQuality.off:
      return WallBlurMode.off;
    case WallBlurQuality.fast:
      return WallBlurMode.panel;
    case WallBlurQuality.lite:
      return WallBlurMode.lite;
    case WallBlurQuality.quality:
      return WallBlurMode.perLine;
    case WallBlurQuality.auto:
      return autoDegraded ? WallBlurMode.off : WallBlurMode.panel;
  }
}

/// 视口窗口余量比例与下限。
const double kWallWindowMarginRatio = 0.6;
const double kWallWindowMarginMinPx = 180.0;

/// 滚动预滚提前量（毫秒）——对齐 AMLL `applyScrollPreroll`。
const int kWallPrerollNoOverlapMs = 600;
const int kWallPrerollOverlapMs = 400;
const double kWallPrerollOverlapBoundary = 0.3;

/// 逐字行提前换行理想值（毫秒）。
const int kWallWordSwitchLeadMs = 500;

/// 激活淡入/淡出时间常数（秒）。
const double kWallActivateTauIn = 0.09;
const double kWallActivateTauOut = 0.15;

/// 失焦强度过渡时间常数（秒）。
const double kWallBlurTau = 0.22;

/// 间奏最小空隙（毫秒）。
const int kWallMinInterludeGapMs = 7000;

/// 弹簧预设：与旧引擎保持同一组手感基线。
enum WallSpringPreset { balanced, smooth, responsive, jello, heavy }

/// 旧设置项的值到新预设的映射。
WallSpringPreset wallSpringPresetFrom(String raw) {
  switch (raw) {
    case 'smooth':
      return WallSpringPreset.smooth;
    case 'responsive':
      return WallSpringPreset.responsive;
    case 'jello':
      return WallSpringPreset.jello;
    case 'heavy':
      return WallSpringPreset.heavy;
    default:
      return WallSpringPreset.balanced;
  }
}

/// 高速换行阈值（新引擎用单一滚动偏移，只影响刚度选择）。
const int kWallFastLineMs = 350;

/// 解析弹簧参数：换行越密、位移越大，刚度越高；seek 用临界阻尼直接追上。
SpringParams resolveWallSpring({
  required WallSpringPreset preset,
  required bool seeking,
  required bool interlude,
  int? intervalMs,
  bool endOfSong = false,
  double shiftPx = 0,
  double viewH = 0,
}) {
  if (seeking) {
    return const SpringParams(stiffness: 300, damping: 30, mass: 1);
  }
  final base = switch (preset) {
    WallSpringPreset.smooth => const SpringParams(
      mass: 1.2,
      damping: 22,
      stiffness: 80,
    ),
    WallSpringPreset.responsive => const SpringParams(
      mass: 0.5,
      damping: 18,
      stiffness: 150,
    ),
    WallSpringPreset.jello => const SpringParams(
      mass: 0.6,
      damping: 8,
      stiffness: 120,
    ),
    WallSpringPreset.heavy => const SpringParams(
      mass: 2.0,
      damping: 25,
      stiffness: 60,
    ),
    WallSpringPreset.balanced => const SpringParams(
      stiffness: 170,
      damping: 20,
      mass: 1,
    ),
  };
  if (interlude || endOfSong) {
    return SpringParams(
      stiffness: math.max(60, base.stiffness * 0.55),
      damping: base.damping,
      mass: base.mass,
    );
  }
  final interval = intervalMs ?? 0;
  if (interval > 0 && interval <= kWallFastLineMs) {
    // 密集换行：整墙一起滚动，提高刚度缩短追赶时间。
    return SpringParams(
      stiffness: base.stiffness * 1.35,
      damping: base.damping * 1.1,
      mass: base.mass,
    );
  }
  // 跨屏/远距离跳转：先慢后快容易拖影，这里直接用更硬的基线。
  if (viewH > 0 && shiftPx.abs() > viewH * 0.4) {
    return const SpringParams(stiffness: 260, damping: 26, mass: 1);
  }
  return base;
}
