// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

part of '../settings_sections.dart';

// ── 性能 / 渲染 ───────────────────────────────────────────────────────

/// 性能 / 渲染分类：水纹渲染器 / 动态层半分辨率 / 损伤区裁剪 / 歌词边缘渐隐 /
/// macOS 独立显卡加速。
class RenderSection extends ConsumerWidget {
  RenderSection({super.key, bool? showMacOSGPU})
    : showMacOSGPU = showMacOSGPU ?? Platform.isMacOS;

  /// 是否展示「独立显卡加速」（macOS 专属；测试可注入，避免依赖宿主平台）。
  final bool showMacOSGPU;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final prefs = ref.watch(appPrefsProvider);
    final scheme = Theme.of(context).colorScheme;
    final fps = prefs.renderFps;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SettingSection(
          title: l10n.settingsCatRender,
          children: [
            SettingSliderTile(
              icon: EtaIcons.stopwatchOutline,
              title: l10n.settingsRenderFps,
              // 超过 60 FPS 追加说明：滑块变色（黄/红）不是主题色，需要解释。
              subtitle: fps > kRenderFpsWarnFrom
                  ? '${l10n.settingsRenderFpsDesc(fps: fps)} '
                        '${l10n.settingsRenderFpsWarn}'
                  : l10n.settingsRenderFpsDesc(fps: fps),
              value: fps.toDouble(),
              min: kRenderFpsMin.toDouble(),
              max: kRenderFpsMax.toDouble(),
              divisions: kRenderFpsMax - kRenderFpsMin,
              label: '$fps FPS',
              accentColor: renderFpsSliderColor(scheme, fps),
              onChanged: (v) =>
                  ref.read(appPrefsProvider.notifier).setRenderFps(v.round()),
            ),
            SettingSwitchTile(
              icon: EtaIcons.dashboard4Outline,
              title: l10n.settingsRippleShader,
              subtitle: l10n.settingsRippleShaderDesc,
              value: prefs.rippleShaderEnabled,
              onChanged: (value) => ref
                  .read(appPrefsProvider.notifier)
                  .setRippleRender(shader: value),
            ),
            SettingSwitchTile(
              icon: EtaIcons.magic2Outline,
              title: l10n.settingsRippleLowRes,
              subtitle: l10n.settingsRippleLowResDesc,
              value: prefs.rippleLowRes,
              onChanged: (value) => ref
                  .read(appPrefsProvider.notifier)
                  .setRippleRender(lowRes: value),
            ),
            SettingSwitchTile(
              icon: EtaIcons.flashOutline,
              title: l10n.settingsRippleDamageClip,
              subtitle: l10n.settingsRippleDamageClipDesc,
              value: prefs.rippleDamageClip,
              onChanged: (value) => ref
                  .read(appPrefsProvider.notifier)
                  .setRippleRender(damageClip: value),
            ),
            SettingTile(
              icon: EtaIcons.blurOnOutline,
              title: l10n.settingsLyricsEdgeFade,
              subtitle: l10n.settingsLyricsEdgeFadeDesc,
              trailing: DropdownButton<String>(
                value: prefs.lyricsEdgeFade,
                isDense: true,
                underline: const SizedBox.shrink(),
                borderRadius: BorderRadius.circular(10),
                style: TextStyle(
                  fontSize: 12.5,
                  fontWeight: FontWeight.w600,
                  color: scheme.onSurface,
                ),
                icon: Icon(EtaIcons.downSmall, color: scheme.onSurfaceVariant),
                onChanged: (v) {
                  if (v == null) return;
                  ref.read(appPrefsProvider.notifier).setLyricsEdgeFade(v);
                },
                items: [
                  DropdownMenuItem(
                    value: 'wall',
                    child: Text(l10n.settingsLyricsEdgeFadeWall),
                  ),
                  DropdownMenuItem(
                    value: 'shader',
                    child: Text(l10n.settingsLyricsEdgeFadeShader),
                  ),
                  DropdownMenuItem(
                    value: 'off',
                    child: Text(l10n.settingsLyricsEdgeFadeOff),
                  ),
                ],
              ),
            ),
            // 双显卡 Intel Mac 上引擎默认「优先集显」（flutter#164569），
            // 由 Runner 的 GPUSelection.m 在启动时按本偏好改写。三档而不是
            // 开关：独显的 Metal 能力可能低于集显（如集显 Metal 3 / 独显
            // Metal 2），需要「自动」这一档避免降级。
            // gen-settings-index: skip（仅 macOS 显示，其它平台不参与搜索索引）
            if (showMacOSGPU)
              SettingTile(
                icon: EtaIcons.chipOutline,
                title: l10n.settingsMacOSGPU,
                subtitle: l10n.settingsMacOSGPUDesc,
                trailing: DropdownButton<String>(
                  value: prefs.macosGPU,
                  isDense: true,
                  underline: const SizedBox.shrink(),
                  borderRadius: BorderRadius.circular(10),
                  style: TextStyle(
                    fontSize: 12.5,
                    fontWeight: FontWeight.w600,
                    color: scheme.onSurface,
                  ),
                  icon: Icon(
                    EtaIcons.downSmall,
                    color: scheme.onSurfaceVariant,
                  ),
                  onChanged: (v) {
                    if (v == null) return;
                    ref.read(appPrefsProvider.notifier).setMacosGPU(v);
                  },
                  items: [
                    DropdownMenuItem(
                      value: 'auto',
                      child: Text(l10n.settingsMacOSGPUAuto),
                    ),
                    DropdownMenuItem(
                      value: 'discrete',
                      child: Text(l10n.settingsMacOSGPUDiscrete),
                    ),
                    DropdownMenuItem(
                      value: 'integrated',
                      child: Text(l10n.settingsMacOSGPUIntegrated),
                    ),
                  ],
                ),
              ),
          ],
        ),
        const SizedBox(height: 20),
        // 节能 / 休眠属于渲染与功耗策略，放在「性能 / 渲染」内（原在「播放」）。
        SettingSection(
          title: l10n.settingsSectionPower,
          children: [
            SettingSwitchTile(
              icon: prefs.powerSaver ? EtaIcons.leaf : EtaIcons.leafOutline,
              title: l10n.settingsPowerSaver,
              subtitle: prefs.powerSaver
                  ? l10n.settingsPowerSaverOn
                  : l10n.settingsPowerSaverOff,
              value: prefs.powerSaver,
              onChanged: (value) =>
                  ref.read(appPrefsProvider.notifier).setPowerSaver(value),
            ),
            SettingSwitchTile(
              icon: prefs.suppressSleep
                  ? EtaIcons.bedtimeOffOutline
                  : EtaIcons.moonStarsOutline,
              title: l10n.settingsSuppressSleep,
              subtitle: prefs.suppressSleep
                  ? l10n.settingsSuppressSleepOn
                  : l10n.settingsSuppressSleepOff,
              value: prefs.suppressSleep,
              onChanged: (value) =>
                  ref.read(appPrefsProvider.notifier).setSuppressSleep(value),
            ),
          ],
        ),
      ],
    );
  }
}
