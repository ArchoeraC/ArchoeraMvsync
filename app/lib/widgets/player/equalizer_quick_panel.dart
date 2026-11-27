// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 播放页均衡器快捷面板。
///
/// 交互参考 SPlayer-Next 的 EqualizerDialog：预设 + 总开关 + 预增益 +
/// 10 段竖向推子，复位/完成；视觉按 ArchoeraMusic-UI skill（对话框
/// 16 圆角、面板底色、10 圆角控件、静音色推子）落地。
///
/// 数据仍走 [AudioFxPrefs]（与设置页共用同一份偏好）；拖动时只更新本地
/// 草稿保证顺滑，松手才落盘并调用 [PlaybackNotifier.applyAudioEffects]
/// 即时下发引擎。
library;

import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../l10n/l10n.dart';
import '../../services/playback/playback_notifier.dart';
import '../../stores/app_prefs.dart';
import '../../theme/app_theme.dart';
import '../common/anim.dart';
import '../dialogs/s_dialog.dart';
import 's_controls.dart';

import 'package:archoera_mvsync/eta/icon/eta_icons.dart';

/// 播放页顶栏的均衡器快捷入口（开启时高亮）。
class EqualizerQuickButton extends ConsumerWidget {
  const EqualizerQuickButton({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final enabled = ref.watch(appPrefsProvider.select((p) => p.eqEnabled));
    return IconButton(
      tooltip: l10n.settingsSectionEqualizer,
      onPressed: () => showQuickEqualizerDialog(context),
      icon: Icon(
        EtaIcons.soundLine,
        color: enabled ? Theme.of(context).colorScheme.primary : null,
      ),
    );
  }
}

/// 打开播放页均衡器快捷面板。
Future<void> showQuickEqualizerDialog(BuildContext context) {
  final l10n = context.l10n;
  return showGeneralDialog<void>(
    context: context,
    barrierDismissible: true,
    barrierLabel: l10n.settingsSectionEqualizer,
    barrierColor: Colors.black.withValues(alpha: 0.5),
    useRootNavigator: false,
    transitionDuration: animDuration(
      context,
      const Duration(milliseconds: 200),
    ),
    pageBuilder: (context, animation, secondaryAnimation) =>
        const _QuickEqualizerDialog(),
    transitionBuilder: (context, animation, secondaryAnimation, child) {
      // 对齐 skill：200ms 淡入 + 0.98 → 1 轻微放大；性能模式时长归零。
      final curved = CurvedAnimation(
        parent: animation,
        curve: Curves.easeOutCubic,
        reverseCurve: Curves.easeInCubic,
      );
      return FadeTransition(
        opacity: curved,
        child: ScaleTransition(
          scale: Tween<double>(begin: 0.98, end: 1).animate(curved),
          child: child,
        ),
      );
    },
  );
}

class _QuickEqualizerDialog extends ConsumerStatefulWidget {
  const _QuickEqualizerDialog();

  @override
  ConsumerState<_QuickEqualizerDialog> createState() =>
      _QuickEqualizerDialogState();
}

class _QuickEqualizerDialogState extends ConsumerState<_QuickEqualizerDialog> {
  /// 拖动中的 10 段增益草稿；null = 跟随偏好。
  List<double>? _draftGains;

  /// 拖动中的预增益草稿。
  double? _draftPreamp;

  List<double> get _gains => _draftGains ?? ref.read(appPrefsProvider).eqGains;

  double get _preamp => _draftPreamp ?? ref.read(appPrefsProvider).eqPreampDb;

  void _apply() {
    // ignore: discarded_futures
    ref.read(playbackProvider.notifier).applyAudioEffects();
  }

  void _setEnabled(bool value) {
    ref.read(appPrefsProvider.notifier).setEq(enabled: value);
    _apply();
  }

  void _selectPreset(String id) {
    final gains = eqPresets[id];
    if (gains == null) return;
    setState(() => _draftGains = [...gains]);
    ref.read(appPrefsProvider.notifier).setEq(gains: gains, preset: id);
    _apply();
  }

  void _commitBand(int index, double value) {
    final gains = [..._gains]..[index] = value;
    setState(() => _draftGains = gains);
    ref.read(appPrefsProvider.notifier).setEq(gains: gains, preset: 'custom');
    _apply();
  }

  void _commitPreamp(double value) {
    setState(() => _draftPreamp = value);
    ref.read(appPrefsProvider.notifier).setEq(preampDb: value);
    _apply();
  }

  void _reset() {
    final flat = eqPresets['flat']!;
    setState(() {
      _draftGains = [...flat];
      _draftPreamp = 0;
    });
    ref
        .read(appPrefsProvider.notifier)
        .setEq(gains: flat, preampDb: 0, preset: 'flat');
    _apply();
  }

  String _presetLabel(AppLocalizations l10n, String id) => switch (id) {
    'flat' => l10n.settingsEqPresetFlat,
    'pop' => l10n.settingsEqPresetPop,
    'rock' => l10n.settingsEqPresetRock,
    'jazz' => l10n.settingsEqPresetJazz,
    'classical' => l10n.settingsEqPresetClassical,
    'vocal' => l10n.settingsEqPresetVocal,
    'bass' => l10n.settingsEqPresetBass,
    _ => l10n.settingsEqPresetCustom,
  };

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final prefs = ref.watch(appPrefsProvider);
    final scheme = Theme.of(context).colorScheme;
    final enabled = prefs.eqEnabled;
    // 11 根推子需要比默认 480 更宽；小窗口按可用宽度收敛，避免溢出。
    final width = (MediaQuery.sizeOf(context).width - 96).clamp(400.0, 600.0);
    final presetValue = prefs.eqPreset == 'custom' ? null : prefs.eqPreset;

    return SDialog(
      title: l10n.settingsSectionEqualizer,
      description: l10n.equalizerQuickDesc,
      width: width,
      actions: [
        SButton(
          label: l10n.commonReset,
          icon: EtaIcons.refresh,
          variant: SButtonVariant.secondary,
          onPressed: _reset,
        ),
        SButton(
          label: l10n.commonDone,
          variant: SButtonVariant.primary,
          onPressed: () => Navigator.of(context).pop(),
        ),
      ],
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Tooltip(
                  message: l10n.settingsEqPreset,
                  child: Container(
                    height: 36,
                    padding: const EdgeInsets.symmetric(horizontal: 10),
                    decoration: BoxDecoration(
                      color: scheme.surfaceContainerHighest.withValues(
                        alpha: 0.35,
                      ),
                      borderRadius: BorderRadius.circular(AppRadius.control),
                      border: Border.all(
                        color: scheme.outline.withValues(alpha: 0.3),
                      ),
                    ),
                    child: DropdownButtonHideUnderline(
                      child: DropdownButton<String>(
                        value: presetValue,
                        isExpanded: true,
                        isDense: true,
                        borderRadius: BorderRadius.circular(AppRadius.card),
                        dropdownColor: scheme.surfaceContainerHigh,
                        style: Theme.of(context).textTheme.bodyMedium
                            ?.copyWith(fontSize: 13, color: scheme.onSurface),
                        hint: Text(
                          l10n.settingsEqPresetCustom,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: Theme.of(context).textTheme.bodyMedium
                              ?.copyWith(
                                fontSize: 13,
                                color: scheme.onSurfaceVariant,
                              ),
                        ),
                        icon: Icon(
                          EtaIcons.downSmall,
                          size: 18,
                          color: scheme.onSurfaceVariant,
                        ),
                        items: [
                          for (final id in eqPresetIds)
                            if (id != 'custom')
                              DropdownMenuItem<String>(
                                value: id,
                                child: Text(
                                  _presetLabel(l10n, id),
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                ),
                              ),
                        ],
                        onChanged: enabled
                            ? (v) {
                                if (v == null) return;
                                _selectPreset(v);
                              }
                            : null,
                      ),
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Text(
                l10n.settingsEqEnabled,
                style: TextStyle(
                  fontSize: 12.5,
                  color: scheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(width: 6),
              Switch(
                value: enabled,
                materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
                onChanged: _setEnabled,
              ),
            ],
          ),
          const SizedBox(height: 14),
          // 关闭时只做一层 IgnorePointer + 推子自身的禁用色，避免
          // Opacity 与 disabledColor 叠加后曲线淡到看不清。
          IgnorePointer(
            ignoring: !enabled,
            child: Container(
              padding: const EdgeInsets.fromLTRB(10, 12, 10, 10),
              decoration: BoxDecoration(
                color: scheme.surfaceContainerHighest.withValues(alpha: 0.35),
                borderRadius: BorderRadius.circular(AppRadius.card),
                border: Border.all(
                  color: scheme.outline.withValues(alpha: 0.3),
                ),
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: _EqFader(
                      label: l10n.settingsEqPreamp,
                      value: _preamp,
                      enabled: enabled,
                      onChanged: (v) => setState(() => _draftPreamp = v),
                      onChangeEnd: _commitPreamp,
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(4, 24, 4, 0),
                    child: Container(
                      width: 1,
                      height: 160,
                      color: scheme.outline.withValues(alpha: 0.35),
                    ),
                  ),
                  for (var i = 0; i < eqBandCount; i++)
                    Expanded(
                      child: _EqFader(
                        label: eqBandLabels[i],
                        value: _gains[i],
                        enabled: enabled,
                        onChanged: (v) =>
                            setState(() => _draftGains = [..._gains]..[i] = v),
                        onChangeEnd: (v) => _commitBand(i, v),
                      ),
                    ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// 单根竖向推子：数值 + 轨道 + 频段标签。
class _EqFader extends StatelessWidget {
  const _EqFader({
    required this.label,
    required this.value,
    required this.enabled,
    required this.onChanged,
    required this.onChangeEnd,
  });

  final String label;
  final double value;
  final bool enabled;
  final ValueChanged<double> onChanged;
  final ValueChanged<double> onChangeEnd;

  static String _formatDb(double v) {
    final rounded = v.round();
    return rounded > 0 ? '+$rounded' : '$rounded';
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final valueColor = value.abs() < 0.05
        ? scheme.onSurfaceVariant
        : scheme.onSurface;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          height: 18,
          child: FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(
              _formatDb(value),
              textAlign: TextAlign.center,
              style: TextStyle(
                fontSize: 11,
                fontWeight: FontWeight.w600,
                fontFeatures: const [FontFeature.tabularFigures()],
                color: enabled
                    ? valueColor
                    : scheme.onSurfaceVariant.withValues(alpha: 0.55),
              ),
            ),
          ),
        ),
        const SizedBox(height: 6),
        SizedBox(
          height: 160,
          child: RotatedBox(
            quarterTurns: 3,
            child: SliderTheme(
              data: SliderTheme.of(context).copyWith(
                trackHeight: 5,
                trackShape: const _CenterFillTrackShape(),
                activeTrackColor: scheme.primary,
                inactiveTrackColor: scheme.outline.withValues(alpha: 0.4),
                disabledActiveTrackColor: scheme.onSurfaceVariant.withValues(
                  alpha: 0.35,
                ),
                disabledInactiveTrackColor: scheme.outline.withValues(
                  alpha: 0.25,
                ),
                thumbColor: scheme.primary,
                disabledThumbColor: scheme.onSurfaceVariant.withValues(
                  alpha: 0.45,
                ),
                overlayColor: scheme.primary.withValues(alpha: 0.12),
                inactiveTickMarkColor: scheme.onSurfaceVariant.withValues(
                  alpha: 0.35,
                ),
                // 24 档刻度点会糊成一条，推子只保留中心 0 dB 基准。
                tickMarkShape: SliderTickMarkShape.noTickMark,
                thumbShape: const RoundSliderThumbShape(
                  enabledThumbRadius: 7,
                  disabledThumbRadius: 6,
                ),
                overlayShape: const RoundSliderOverlayShape(overlayRadius: 13),
              ),
              child: Slider(
                value: value.clamp(eqGainMinDb, eqGainMaxDb),
                min: eqGainMinDb,
                max: eqGainMaxDb,
                divisions: 24,
                semanticFormatterCallback: (v) => '${_formatDb(v)} dB',
                onChanged: enabled ? onChanged : null,
                onChangeEnd: enabled ? onChangeEnd : null,
              ),
            ),
          ),
        ),
        const SizedBox(height: 6),
        SizedBox(
          height: 16,
          child: FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(
              label,
              textAlign: TextAlign.center,
              maxLines: 1,
              style: TextStyle(
                fontSize: 10.5,
                color: scheme.onSurfaceVariant.withValues(
                  alpha: enabled ? 0.8 : 0.4,
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }
}

/// 以 0 dB 为中心向两侧填充的推子轨道（对齐 SPlayer-Next 的 center-fill）。
class _CenterFillTrackShape extends SliderTrackShape {
  const _CenterFillTrackShape();

  static const double _sideInset = 8;

  @override
  Rect getPreferredRect({
    required RenderBox parentBox,
    Offset offset = Offset.zero,
    required SliderThemeData sliderTheme,
    bool isEnabled = false,
    bool isDiscrete = false,
  }) {
    final trackHeight = sliderTheme.trackHeight ?? 4;
    final trackTop = offset.dy + (parentBox.size.height - trackHeight) / 2;
    return Rect.fromLTWH(
      offset.dx + _sideInset,
      trackTop,
      math.max(0, parentBox.size.width - _sideInset * 2),
      trackHeight,
    );
  }

  @override
  void paint(
    PaintingContext context,
    Offset offset, {
    required RenderBox parentBox,
    required SliderThemeData sliderTheme,
    required Animation<double> enableAnimation,
    required Offset thumbCenter,
    Offset? secondaryOffset,
    bool isEnabled = false,
    bool isDiscrete = false,
    required TextDirection textDirection,
  }) {
    final rect = getPreferredRect(
      parentBox: parentBox,
      offset: offset,
      sliderTheme: sliderTheme,
      isEnabled: isEnabled,
      isDiscrete: isDiscrete,
    );
    if (rect.width <= 0) return;

    final canvas = context.canvas;
    final radius = Radius.circular(rect.height / 2);
    final inactive = isEnabled
        ? sliderTheme.inactiveTrackColor!
        : sliderTheme.disabledInactiveTrackColor!;
    canvas.drawRRect(
      RRect.fromRectAndRadius(rect, radius),
      Paint()..color = inactive,
    );

    final centerX = rect.center.dx;
    final thumbX = thumbCenter.dx.clamp(rect.left, rect.right);
    if ((thumbX - centerX).abs() > 0.5) {
      final fill = Rect.fromLTRB(
        math.min(centerX, thumbX),
        rect.top,
        math.max(centerX, thumbX),
        rect.bottom,
      );
      canvas.drawRRect(
        RRect.fromRectAndRadius(fill, radius),
        Paint()
          ..color = isEnabled
              ? sliderTheme.activeTrackColor!
              : sliderTheme.disabledActiveTrackColor!,
      );
    }

    // 0 dB 基准刻度：短横线穿过轨道，帮助判断正负增益。
    final tick = sliderTheme.inactiveTickMarkColor ?? Colors.transparent;
    canvas.drawLine(
      Offset(centerX, rect.top - 3),
      Offset(centerX, rect.bottom + 3),
      Paint()
        ..color = tick
        ..strokeWidth = 1,
    );
  }

  @override
  bool get isRounded => true;
}
