// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 回归：播放页均衡器快捷面板。
///
/// 覆盖：
///   1. 面板文案 / 11 根推子 / 频段标签；
///   2. 开关写回偏好并下发引擎；
///   3. 预设选择写回 10 段增益并标记预设；
///   4. 手动推子写回自定义增益；
///   5. 重置回平坦 + 0 预增益；
///   6. 英文 locale 文案。
library;

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/l10n/generated/app_localizations.dart';
import 'package:archoera_mvsync/services/playback/playback_notifier.dart';
import 'package:archoera_mvsync/services/playback/playback_state.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';
import 'package:archoera_mvsync/widgets/player/equalizer_quick_panel.dart';

/// 不落盘、不读真实 prefs.json 的偏好控制器。
class _FakePrefsNotifier extends AppPrefsNotifier {
  @override
  AppPrefs build() => AppPrefs();

  @override
  void setEq({
    bool? enabled,
    List<double>? gains,
    double? preampDb,
    String? preset,
    bool? limiter,
  }) {
    state = state.copyWithEq(
      enabled: enabled,
      gains: gains,
      preampDb: preampDb,
      preset: preset,
      limiter: limiter,
    );
  }
}

/// 只记录下发次数，不建引擎/不写会话记忆。
class _FakePlaybackNotifier extends PlaybackNotifier {
  int applyCount = 0;

  @override
  PlaybackState build() => const PlaybackState();

  @override
  Future<void> applyAudioEffects() async {
    applyCount++;
  }
}

Widget _wrap(ProviderContainer container, Locale locale) {
  return UncontrolledProviderScope(
    container: container,
    child: MaterialApp(
      locale: locale,
      localizationsDelegates: const [
        AppLocalizations.delegate,
        ...GlobalMaterialLocalizations.delegates,
      ],
      supportedLocales: AppLocalizations.supportedLocales,
      home: const Scaffold(body: Center(child: EqualizerQuickButton())),
    ),
  );
}

Future<void> _openPanel(WidgetTester tester) async {
  await tester.tap(find.byType(EqualizerQuickButton));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('面板显示预设 / 开关 / 11 根推子', (tester) async {
    final container = ProviderContainer(
      overrides: [
        appPrefsProvider.overrideWith(_FakePrefsNotifier.new),
        playbackProvider.overrideWith(_FakePlaybackNotifier.new),
      ],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(_wrap(container, const Locale('zh', 'CN')));
    await _openPanel(tester);

    expect(find.text('均衡器'), findsOneWidget);
    expect(find.text('启用均衡器'), findsOneWidget);
    expect(find.text('预增益'), findsOneWidget);
    expect(find.byType(Slider), findsNWidgets(11));
    expect(find.text('31Hz'), findsOneWidget);
    expect(find.text('16kHz'), findsOneWidget);
    expect(find.text('重置'), findsOneWidget);
    expect(find.text('完成'), findsOneWidget);
  });

  testWidgets('开关启用后写偏好并下发引擎', (tester) async {
    final playback = _FakePlaybackNotifier();
    final container = ProviderContainer(
      overrides: [
        appPrefsProvider.overrideWith(_FakePrefsNotifier.new),
        playbackProvider.overrideWith(() => playback),
      ],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(_wrap(container, const Locale('zh', 'CN')));
    await _openPanel(tester);

    final switchFinder = find.byType(Switch);
    expect(tester.widget<Switch>(switchFinder).value, isFalse);
    await tester.tap(switchFinder);
    await tester.pumpAndSettle();

    expect(container.read(appPrefsProvider).eqEnabled, isTrue);
    expect(tester.widget<Switch>(switchFinder).value, isTrue);
    expect(playback.applyCount, 1);
  });

  testWidgets('选择预设写回 10 段增益并标记预设', (tester) async {
    final playback = _FakePlaybackNotifier();
    final container = ProviderContainer(
      overrides: [
        appPrefsProvider.overrideWith(_FakePrefsNotifier.new),
        playbackProvider.overrideWith(() => playback),
      ],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(_wrap(container, const Locale('zh', 'CN')));
    await _openPanel(tester);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();

    await tester.tap(find.byType(DropdownButton<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('摇滚').last);
    await tester.pumpAndSettle();

    final prefs = container.read(appPrefsProvider);
    expect(prefs.eqPreset, 'rock');
    expect(prefs.eqGains, eqPresets['rock']);
    expect(playback.applyCount, 2); // 启用 + 预设
  });

  testWidgets('手动推子写回自定义增益，重置回平坦', (tester) async {
    final playback = _FakePlaybackNotifier();
    final container = ProviderContainer(
      overrides: [
        appPrefsProvider.overrideWith(_FakePrefsNotifier.new),
        playbackProvider.overrideWith(() => playback),
      ],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(_wrap(container, const Locale('zh', 'CN')));
    await _openPanel(tester);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();

    // 推子顺序：预增益 + 31/62/125/250/500/1k/2k/4k/8k/16k。
    final slider = tester.widget<Slider>(find.byType(Slider).at(6));
    slider.onChanged!(6);
    slider.onChangeEnd!(6);
    await tester.pumpAndSettle();

    var prefs = container.read(appPrefsProvider);
    expect(prefs.eqGains[5], 6.0);
    expect(prefs.eqPreset, 'custom');
    expect(playback.applyCount, 2);

    await tester.tap(find.text('重置'));
    await tester.pumpAndSettle();

    prefs = container.read(appPrefsProvider);
    expect(prefs.eqGains, List<double>.filled(10, 0));
    expect(prefs.eqPreampDb, 0);
    expect(prefs.eqPreset, 'flat');
    expect(playback.applyCount, 3);
  });

  testWidgets('小窗口下竖向拖动推子改变增益', (tester) async {
    tester.view.physicalSize = const Size(640, 420);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final container = ProviderContainer(
      overrides: [
        appPrefsProvider.overrideWith(_FakePrefsNotifier.new),
        playbackProvider.overrideWith(_FakePlaybackNotifier.new),
      ],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(_wrap(container, const Locale('zh', 'CN')));
    await _openPanel(tester);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();

    final fader = find.byType(Slider).at(6);
    await tester.ensureVisible(fader);
    await tester.pumpAndSettle();
    await tester.drag(fader, const Offset(0, -40));
    await tester.pumpAndSettle();

    expect(container.read(appPrefsProvider).eqGains[5], greaterThan(0));
  });

  testWidgets('面板文案有英文翻译', (tester) async {
    final container = ProviderContainer(
      overrides: [
        appPrefsProvider.overrideWith(_FakePrefsNotifier.new),
        playbackProvider.overrideWith(_FakePlaybackNotifier.new),
      ],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(_wrap(container, const Locale('en')));
    await _openPanel(tester);

    expect(find.text('Equalizer'), findsOneWidget);
    expect(find.text('Enable equalizer'), findsOneWidget);
    expect(find.text('Preamp'), findsOneWidget);
    expect(find.text('Reset'), findsOneWidget);
    expect(find.text('Done'), findsOneWidget);
  });
}
