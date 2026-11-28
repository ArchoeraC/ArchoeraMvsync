// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// 回归：派生列子开关仅在总开关开启时可用；全部默认关。
// 子开关只写偏好、**不自动重建**（现有索引保留，下次重建生效）。

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/l10n/generated/app_localizations.dart';
import 'package:archoera_mvsync/services/search/search_service.dart';
import 'package:archoera_mvsync/settings/search_section.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';

class _Prefs extends AppPrefsNotifier {
  _Prefs(this._initial);
  final AppPrefs _initial;

  bool? lastIndexLrc;
  bool? lastPinyin;
  bool? lastLyricsPinyin;
  bool? lastRomajiLyrics;
  bool? lastInitialsLyrics;
  int setSearchIndexCalls = 0;

  @override
  AppPrefs build() => _initial;

  @override
  void setSearchIndex({
    bool? enabled,
    bool? indexLrc,
    bool? pinyin,
    bool? lyricsPinyin,
    bool? romajiLyrics,
    bool? initialsLyrics,
  }) {
    setSearchIndexCalls++;
    lastIndexLrc = indexLrc ?? lastIndexLrc;
    lastPinyin = pinyin ?? lastPinyin;
    lastLyricsPinyin = lyricsPinyin ?? lastLyricsPinyin;
    lastRomajiLyrics = romajiLyrics ?? lastRomajiLyrics;
    lastInitialsLyrics = initialsLyrics ?? lastInitialsLyrics;
    state = state.copyWithSearchIndex(
      enabled: enabled,
      indexLrc: indexLrc,
      pinyin: pinyin,
      lyricsPinyin: lyricsPinyin,
      romajiLyrics: romajiLyrics,
      initialsLyrics: initialsLyrics,
    );
  }
}

Widget _wrap(AppPrefs prefs) => ProviderScope(
  overrides: [appPrefsProvider.overrideWith(() => _Prefs(prefs))],
  child: MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: const Scaffold(
      body: SingleChildScrollView(child: SearchSection()),
    ),
  ),
);

void main() {
  setUp(() => appPrefsSnapshot = null);
  tearDown(() {
    appPrefsSnapshot = null;
    SearchService.instance.debugReindexOverride = null;
  });

  testWidgets('总开关默认关：全部子开关禁用且默认关', (tester) async {
    await tester.pumpWidget(_wrap(AppPrefs()));
    await tester.pumpAndSettle();

    final switches = tester.widgetList<Switch>(find.byType(Switch)).toList();
    expect(switches, hasLength(6),
        reason: '总开关 + .lrc + 元数据拼音 + 歌词拼音 + 歌词首字母 + 歌词罗马音');
    expect(switches[0].value, isFalse, reason: '总开关默认关');
    expect(switches[0].onChanged, isNotNull);
    for (var i = 1; i < switches.length; i++) {
      expect(switches[i].value, isFalse, reason: '子开关默认关（index=$i）');
      expect(switches[i].onChanged, isNull,
          reason: '总开关关闭时子开关不可用（index=$i）');
    }
    expect(find.text('Rebuild index'), findsOneWidget);
    expect(find.text('Delete index'), findsOneWidget);
  });

  testWidgets('总开关开启：全部子开关可用', (tester) async {
    await tester.pumpWidget(
      _wrap(AppPrefs().copyWithSearchIndex(enabled: true)),
    );
    await tester.pumpAndSettle();

    final switches = tester.widgetList<Switch>(find.byType(Switch)).toList();
    expect(switches[0].value, isTrue);
    for (var i = 1; i < switches.length; i++) {
      expect(switches[i].onChanged, isNotNull, reason: 'index=$i');
    }
  });

  testWidgets('切换 .lrc → 只写偏好，不自动重建', (tester) async {
    final prefs = _Prefs(AppPrefs().copyWithSearchIndex(enabled: true));
    var reindexCalls = 0;
    SearchService.instance.debugReindexOverride = () async {
      reindexCalls++;
    };

    await tester.pumpWidget(
      ProviderScope(
        overrides: [appPrefsProvider.overrideWith(() => prefs)],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(
            body: SingleChildScrollView(child: SearchSection()),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byType(Switch).at(1));
    await tester.pumpAndSettle();

    expect(prefs.lastIndexLrc, isTrue);
    expect(reindexCalls, 0, reason: '子开关不自动重建，保留现有索引');
  });

  testWidgets('切换元数据 / 歌词拼音 → 分别写偏好', (tester) async {
    final prefs = _Prefs(AppPrefs().copyWithSearchIndex(enabled: true));
    await tester.pumpWidget(
      ProviderScope(
        overrides: [appPrefsProvider.overrideWith(() => prefs)],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(
            body: SingleChildScrollView(child: SearchSection()),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byType(Switch).at(2));
    await tester.pumpAndSettle();
    expect(prefs.lastPinyin, isTrue);

    await tester.tap(find.byType(Switch).at(3));
    await tester.pumpAndSettle();
    expect(prefs.lastLyricsPinyin, isTrue);
  });

  testWidgets('切换歌词首字母 / 罗马音 → 分别写偏好', (tester) async {
    final prefs = _Prefs(AppPrefs().copyWithSearchIndex(enabled: true));
    await tester.pumpWidget(
      ProviderScope(
        overrides: [appPrefsProvider.overrideWith(() => prefs)],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(
            body: SingleChildScrollView(child: SearchSection()),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byType(Switch).at(4));
    await tester.pumpAndSettle();
    expect(prefs.lastInitialsLyrics, isTrue);

    await tester.tap(find.byType(Switch).at(5));
    await tester.pumpAndSettle();
    expect(prefs.lastRomajiLyrics, isTrue);
  });
}
