// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 「性能 / 渲染」偏好读写测试（水纹着色器 / 动态层降分辨率 / 损伤区裁剪）。
library;

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/stores/app_prefs.dart';

void main() {
  group('性能 / 渲染偏好', () {
    test('默认值：着色器开、降分辨率关、损伤区裁剪关', () {
      final prefs = AppPrefs();
      expect(prefs.rippleShaderEnabled, defaultRippleShader);
      expect(prefs.rippleShaderEnabled, isTrue);
      expect(prefs.rippleLowRes, isFalse);
      expect(prefs.rippleDamageClip, isFalse);
    });

    test('copyWithRippleRender 读写三个开关', () {
      final prefs = AppPrefs().copyWithRippleRender(
        shader: false,
        lowRes: true,
        damageClip: true,
      );
      expect(prefs.rippleShaderEnabled, isFalse);
      expect(prefs.rippleLowRes, isTrue);
      expect(prefs.rippleDamageClip, isTrue);
    });

    test('未指定字段保持原值', () {
      final base = AppPrefs().copyWithRippleRender(lowRes: true);
      final next = base.copyWithRippleRender(shader: false);
      expect(next.rippleShaderEnabled, isFalse);
      expect(next.rippleLowRes, isTrue);
      expect(next.rippleDamageClip, isFalse);
    });

    test('歌词边缘渐隐：默认逐行（wall），非法值回退默认', () {
      expect(AppPrefs().lyricsEdgeFade, 'wall');
      expect(AppPrefs().lyricsEdgeFade, defaultLyricsEdgeFade);

      final shader = AppPrefs().copyWithLyricsEdgeFade('shader');
      expect(shader.lyricsEdgeFade, 'shader');
      expect(shader.copyWithLyricsEdgeFade('off').lyricsEdgeFade, 'off');

      // 非法值（含旧版本残留/手改配置）一律回退默认，不抛错。
      expect(AppPrefs().copyWithLyricsEdgeFade('nope').lyricsEdgeFade, 'wall');
      expect(
        AppPrefs(initialData: const {lyricsEdgeFadeKey: 42}).lyricsEdgeFade,
        'wall',
      );
    });

    test('歌词边缘渐隐不影响其它渲染开关', () {
      final p = AppPrefs()
          .copyWithRippleRender(shader: false)
          .copyWithLyricsEdgeFade('off');
      expect(p.rippleShaderEnabled, isFalse);
      expect(p.lyricsEdgeFade, 'off');
    });

    test('macOS 显卡选择：默认 auto，三档可切，非法值回退默认', () {
      expect(AppPrefs().macosGPU, defaultMacosGPU);
      expect(AppPrefs().macosGPU, 'auto');

      for (final mode in ['discrete', 'integrated', 'auto']) {
        expect(AppPrefs().copyWithMacosGPU(mode).macosGPU, mode);
      }

      // 旧版本残留 / 手改配置：非法值回退默认，不抛错。
      expect(
        AppPrefs(initialData: const {macosGPUKey: 'nope'}).macosGPU,
        'auto',
      );
    });

    test('旧布尔键迁移到三档（true = 强制独显 / false = 集显）', () {
      expect(
        AppPrefs(initialData: const {legacyMacosDiscreteGPUKey: true}).macosGPU,
        'discrete',
      );
      expect(
        AppPrefs(initialData: const {legacyMacosDiscreteGPUKey: false})
            .macosGPU,
        'integrated',
      );
      // 新键优先于旧键。
      expect(
        AppPrefs(
          initialData: const {
            macosGPUKey: 'auto',
            legacyMacosDiscreteGPUKey: true,
          },
        ).macosGPU,
        'auto',
      );
    });

    test('显卡选择不影响其它渲染开关', () {
      final p = AppPrefs()
          .copyWithRippleRender(shader: false)
          .copyWithMacosGPU('integrated');
      expect(p.rippleShaderEnabled, isFalse);
      expect(p.macosGPU, 'integrated');
      expect(p.renderFps, kRenderFpsDefault);
    });
  });
}
