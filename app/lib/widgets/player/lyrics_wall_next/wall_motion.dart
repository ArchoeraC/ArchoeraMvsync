// ArchoeraMvsumc UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词墙运动层：整墙共用一个滚动偏移 + 可见行视觉过渡。
///
/// 与旧实现的关键差异：
/// - **不再为每行维护弹簧**。所有行的位置恒为 `centers[i] + offset`，
///   换行只改一个目标偏移，因此行距天然不会塌陷、不会互相追赶；
/// - 密集换行 / 跨屏跳转只影响弹簧刚度（见 [resolveWallSpring]），
///   不再需要「级联延迟 / 刚性滚动切换」两套模式；
/// - 淡入、失焦、缩放的逐行过渡只对可见窗口内的行维护，窗口外的行
///   不产生任何计算或闭包分配。
library;

import 'dart:math' as math;

import '../../../utils/spring.dart';
import 'wall_tuning.dart';

/// 整墙滚动偏移。
class WallMotion {
  WallMotion({double initial = 0})
    : _spring = Spring1D(initialPosition: initial);

  final Spring1D _spring;

  double get offset => _spring.current;
  double get target => _spring.targetPosition;
  double get velocity => _spring.velocity;

  bool get moving => !_spring.arrived();

  /// 向新偏移过渡。弹簧速度连续，换行不会从 0 起步。
  void setTarget(double to, SpringParams params) {
    _spring.params = params;
    _spring.setTarget(to);
  }

  /// 立即到位（seek / 首帧 / 关闭动画）。
  void hardSet(double value) => _spring.hardSet(value);

  /// 推进一帧；返回本帧是否发生了位移。
  bool update(double dt) {
    if (_spring.arrived()) return false;
    _spring.update(dt);
    return true;
  }
}

/// 可见行的淡入/失焦过渡。
class WallVisuals {
  final Map<int, double> _fade = <int, double>{};
  final Map<int, double> _blur = <int, double>{};

  double fadeAt(int index) => _fade[index] ?? 0;
  double blurAt(int index) => _blur[index] ?? 0;

  /// 激活行的轻微缩放由淡入进度直接推导，无需单独状态。
  double scaleAt(int index, {required bool enableScale}) {
    if (!enableScale) return 1;
    return 1 - (1 - fadeAt(index)) * 0.03;
  }

  void clear() {
    _fade.clear();
    _blur.clear();
  }

  /// 把窗口内尚未建立过渡态的行吸附到目标值。
  ///
  /// 暂停/首帧时 ticker 可能不跑，必须由状态层在窗口变化后调用，否则
  /// 新进入窗口的激活行会停在 fade=0。
  bool ensureRange({
    required int from,
    required int to,
    required int active,
    required double Function(int index) blurTarget,
  }) {
    _pruneOutside(from, to);
    var changed = false;
    for (var i = from; i < to; i++) {
      if (_fade.containsKey(i)) continue;
      _fade[i] = i == active ? 1.0 : 0.0;
      _blur[i] = blurTarget(i);
      changed = true;
    }
    return changed;
  }

  /// 推进 [from, to) 的视觉过渡；返回本帧是否有可见变化。
  ///
  /// 新进入窗口的行直接吸附到目标值（它们本来就在视野边缘，突然出现
  /// 的淡入/失焦过渡没有意义）；已在窗口内的行按指数曲线靠拢。
  bool step({
    required double dt,
    required int from,
    required int to,
    required int active,
    required double Function(int index) blurTarget,
    required bool animate,
  }) {
    final lo = math.max(0, from);
    final hi = math.max(lo, to);
    _pruneOutside(lo, hi);

    var changed = false;
    for (var i = lo; i < hi; i++) {
      final fadeTo = i == active ? 1.0 : 0.0;
      final blurTo = blurTarget(i);
      final oldFade = _fade[i];
      final oldBlur = _blur[i];

      if (!animate || oldFade == null || oldBlur == null) {
        _fade[i] = fadeTo;
        _blur[i] = blurTo;
        if (oldFade != fadeTo || oldBlur != blurTo) changed = true;
        continue;
      }

      final fadeTau = fadeTo > oldFade
          ? kWallActivateTauIn
          : kWallActivateTauOut;
      final nextFade = _approach(oldFade, fadeTo, dt, fadeTau);
      final nextBlur = _approach(oldBlur, blurTo, dt, kWallBlurTau);
      if ((nextFade - oldFade).abs() > 1e-4 ||
          (nextBlur - oldBlur).abs() > 1e-4) {
        changed = true;
      }
      _fade[i] = nextFade;
      _blur[i] = nextBlur;
    }
    return changed;
  }

  void _pruneOutside(int from, int to) {
    _fade.removeWhere((k, _) => k < from || k >= to);
    _blur.removeWhere((k, _) => k < from || k >= to);
  }

  static double _approach(double value, double target, double dt, double tau) {
    if (tau <= 0) return target;
    final k = 1 - math.exp(-dt / tau);
    final next = value + (target - value) * k;
    if ((next - target).abs() < 0.002) return target;
    return next;
  }
}

/// 单个标量的指数平滑（整层失焦强度等）。
class WallSmoother {
  WallSmoother({double initial = 0}) : _value = initial;

  double _value;
  double get value => _value;

  bool step(double target, double dt, double tau) {
    final next = WallVisuals._approach(_value, target, dt, tau);
    final changed = (next - _value).abs() > 1e-4;
    _value = next;
    return changed;
  }

  void hardSet(double value) => _value = value;
}
