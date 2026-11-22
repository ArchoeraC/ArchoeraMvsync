// ArchoeraMusic UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词墙播放时钟：把 ~20Hz 的播放位置事件插值到 vsync。
///
/// 新引擎不依赖旧 `LyricClock`，行为保持一致：非播放态严格等于锚点；
/// 播放态按 tick 外推，单次外推受 [kWallClockMaxExtrapolateMs] 钳制，
/// 避免播放器卡顿后歌词墙「冲出去」。
library;

import 'dart:math' as math;

/// 单次外推上限（毫秒）。
const int kWallClockMaxExtrapolateMs = 150;

class WallClock {
  int _anchorMs = 0;
  double _extrapolatedMs = 0;
  bool _running = false;

  /// 当前对外时间（毫秒）。
  int get valueMs => _running ? _anchorMs + _extrapolatedMs.round() : _anchorMs;

  bool get isRunning => _running;

  /// 锚点位置（不含外推）。
  int get anchorMs => _anchorMs;

  /// 重置到新的播放位置。
  void reset(int positionMs, {required bool playing}) {
    _anchorMs = positionMs;
    _extrapolatedMs = 0;
    _running = playing;
  }

  /// 推进一帧，返回当前时间。
  int tick(double dtSec) {
    if (_running) {
      _extrapolatedMs = math.min(
        kWallClockMaxExtrapolateMs.toDouble(),
        _extrapolatedMs + dtSec * 1000,
      );
    }
    return valueMs;
  }
}
