// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 「性能 / 渲染」分类的歌词边缘渐隐选项：三段控件与偏好接线。
library;

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

import 'package:archoera_mvsync/l10n/generated/app_localizations.dart';
import 'package:archoera_mvsync/settings/settings_sections.dart';
import 'package:archoera_mvsync/settings/settings_widgets.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';

/// 不落盘的偏好容器（避免测试写入真实 prefs.json）。
class _NoSavePrefs extends AppPrefsNotifier {
  @override
  AppPrefs build() => AppPrefs();

  @override
  void setLyricsEdgeFade(String value) {
    state = state.copyWithLyricsEdgeFade(value);
  }

  @override
  void setRippleRender({bool? shader, bool? lowRes, bool? damageClip}) {
    state = state.copyWithRippleRender(
      shader: shader,
      lowRes: lowRes,
      damageClip: damageClip,
    );
  }

  @override
  void setRenderFps(int fps) {
    state = state.copyWithRenderFps(fps);
  }

  @override
  void setMacosGPU(String value) {
    state = state.copyWithMacosGPU(value);
  }
}

void main() {
  testWidgets('歌词边缘渐隐：默认逐行，可切到遮罩/关闭并写入偏好', (tester) async {
    final container = ProviderContainer(
      overrides: [appPrefsProvider.overrideWith(_NoSavePrefs.new)],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: SingleChildScrollView(child: RenderSection())),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(container.read(appPrefsProvider).lyricsEdgeFade, 'wall');
    // 下拉当前值显示「逐行」。
    expect(find.text('Per-line'), findsOneWidget);

    Future<void> pick(String current, String next) async {
      await tester.tap(find.text(current).last);
      await tester.pumpAndSettle();
      await tester.tap(find.text(next).last);
      await tester.pumpAndSettle();
    }

    await pick('Per-line', 'Mask');
    expect(container.read(appPrefsProvider).lyricsEdgeFade, 'shader');

    await pick('Mask', 'Off');
    expect(container.read(appPrefsProvider).lyricsEdgeFade, 'off');

    await pick('Off', 'Per-line');
    expect(container.read(appPrefsProvider).lyricsEdgeFade, 'wall');
    expect(tester.takeException(), isNull);
  });

  testWidgets('渲染帧数：30~165 滑条，>60 转黄、>100 转红', (tester) async {
    final container = ProviderContainer(
      overrides: [appPrefsProvider.overrideWith(_NoSavePrefs.new)],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: SingleChildScrollView(child: RenderSection())),
        ),
      ),
    );
    await tester.pumpAndSettle();

    final sliderFinder = find.byType(Slider);
    expect(sliderFinder, findsOneWidget);
    final slider = tester.widget<Slider>(sliderFinder);
    expect(slider.min, 30);
    expect(slider.max, 165);
    expect(slider.value, 60);
    expect(slider.divisions, 135);

    Color? accent() =>
        SliderTheme.of(tester.element(sliderFinder)).activeTrackColor;

    final scheme = Theme.of(tester.element(sliderFinder)).colorScheme;
    // ≤60：主题色（与其它设置滑条一致）。
    expect(accent(), scheme.primary);

    container.read(appPrefsProvider.notifier).setRenderFps(90);
    await tester.pump();
    expect(tester.widget<Slider>(sliderFinder).value, 90);
    expect(accent(), kRenderFpsWarnColor);

    container.read(appPrefsProvider.notifier).setRenderFps(144);
    await tester.pump();
    expect(accent(), scheme.error);
    expect(tester.takeException(), isNull);
  });

  testWidgets('窄窗口不溢出（下拉控件比分段控件更省宽）', (tester) async {
    tester.view.physicalSize = const Size(380, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final container = ProviderContainer(
      overrides: [appPrefsProvider.overrideWith(_NoSavePrefs.new)],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: SingleChildScrollView(child: RenderSection())),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  testWidgets('独立显卡加速：仅 macOS 显示，默认 auto，可切三档', (tester) async {
    final container = ProviderContainer(
      overrides: [appPrefsProvider.overrideWith(_NoSavePrefs.new)],
    );
    addTearDown(container.dispose);

    Widget build({required bool showMacOSGPU}) => UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: SingleChildScrollView(
            child: RenderSection(showMacOSGPU: showMacOSGPU),
          ),
        ),
      ),
    );

    await tester.pumpWidget(build(showMacOSGPU: false));
    await tester.pumpAndSettle();
    expect(
      find.text('Discrete GPU acceleration'),
      findsNothing,
      reason: '非 macOS 不展示该开关',
    );

    await tester.pumpWidget(build(showMacOSGPU: true));
    await tester.pumpAndSettle();

    expect(find.text('Discrete GPU acceleration'), findsOneWidget);
    expect(
      container.read(appPrefsProvider).macosGPU,
      'auto',
      reason: '默认 auto：独显 Metal 能力不低于集显时才用独显（避免 Metal 3 → Metal 2 降级）',
    );
    expect(find.text('Auto (recommended)'), findsOneWidget);

    Future<void> pick(String current, String next) async {
      await tester.tap(find.text(current).last);
      await tester.pumpAndSettle();
      await tester.tap(find.text(next).last);
      await tester.pumpAndSettle();
    }

    await pick('Auto (recommended)', 'Prefer discrete GPU');
    expect(container.read(appPrefsProvider).macosGPU, 'discrete');

    await pick('Prefer discrete GPU', 'Integrated GPU');
    expect(container.read(appPrefsProvider).macosGPU, 'integrated');

    await pick('Integrated GPU', 'Auto (recommended)');
    expect(container.read(appPrefsProvider).macosGPU, 'auto');
    expect(tester.takeException(), isNull);
  });
}
