// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// E5：健康诊断（设置页展示 gen / schema / analyzer、dataPack、歌词置信度）。
//
// 纯格式化器单测不碰 FFI；服务侧用既有 `debugDataPackHealthOverride` seam
// 验证快照填充 / 失败保留，不加载原生库。

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/l10n/generated/app_localizations.dart';
import 'package:archoera_mvsync/services/search/search_index_diagnostics.dart';
import 'package:archoera_mvsync/services/search/search_models.dart';
import 'package:archoera_mvsync/services/search/search_service.dart';
import 'package:archoera_mvsync/settings/search_section.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';

class _Prefs extends AppPrefsNotifier {
  _Prefs(this._initial);
  final AppPrefs _initial;

  @override
  AppPrefs build() => _initial;
}

const _packId =
    'sha256:3583c58d4fc83729badd02d9cb5766e2f991f9b93b135d13fa09b6381a94f3ea';

Map<String, dynamic> _readyHealth({
  int gen = 7,
  int schema = 4,
  int analyzer = 17,
  bool match = true,
  bool present = true,
  bool foundationOk = true,
  List<String> degraded = const [],
  int high = 3,
  int medium = 2,
  int low = 1,
  int none = 4,
}) => {
  'api': 2,
  'ready': true,
  'gen': gen,
  'schemaVersion': schema,
  'analyzerVersion': analyzer,
  'initialsLyrics': false,
  'lyricsConfidence': {
    'high': high,
    'medium': medium,
    'low': low,
    'none': none,
  },
  'dataPack': {
    'id': _packId,
    'storedId': _packId,
    'match': match,
    'present': present,
    'foundationOk': foundationOk,
    'missing': const <String>[],
    'corrupt': const <String>[],
    'degradedCapabilities': degraded,
  },
  if (degraded.isNotEmpty) 'missing': degraded,
};

void main() {
  setUp(() => appPrefsSnapshot = null);
  tearDown(() {
    appPrefsSnapshot = null;
    SearchService.instance.debugReset();
  });

  group('SearchIndexDiagnostics.fromHealth', () {
    test('未就绪载荷 → null（设置页不显示诊断块）', () {
      expect(
        SearchIndexDiagnostics.fromHealth({'api': 2, 'ready': false}),
        isNull,
      );
      expect(SearchIndexDiagnostics.fromHealth(null), isNull);
    });

    test('缺键载荷 → 字段为空、行可省略', () {
      final d = SearchIndexDiagnostics.fromHealth({'ready': true})!;
      expect(d.generation, isNull);
      expect(d.schemaVersion, isNull);
      expect(d.analyzerVersion, isNull);
      expect(d.dataPackId, isNull);
      expect(d.shortDataPackId, isNull);
      expect(d.dataPackMatch, isNull);
      expect(d.dataPackPresent, isNull);
      expect(d.dataPackFoundationOk, isNull);
      expect(d.degradedCapabilities, 0);
      expect(d.missingCapabilities, 0);
      expect(d.capabilityIssueCount, 0);
      expect(d.confidenceTotal, 0);
    });

    test('完整载荷 → 解析世代 / 版本 / 数据包 / 置信度', () {
      final d = SearchIndexDiagnostics.fromHealth(_readyHealth())!;
      expect(d.generation, 7);
      expect(d.schemaVersion, 4);
      expect(d.analyzerVersion, 17);
      expect(d.dataPackMatch, isTrue);
      expect(d.dataPackPresent, isTrue);
      expect(d.dataPackFoundationOk, isTrue);
      expect(d.confidenceHigh, 3);
      expect(d.confidenceMedium, 2);
      expect(d.confidenceLow, 1);
      expect(d.confidenceNone, 4);
      expect(d.confidenceTotal, 10);
    });

    test('数据包短 id：去掉 sha256: 前缀后取前 12 个十六进制字符', () {
      final d = SearchIndexDiagnostics.fromHealth(_readyHealth())!;
      expect(d.dataPackId, _packId);
      expect(d.shortDataPackId, '3583c58d4fc8');
    });

    test('非 sha256 前缀 / 非十六进制 id 安全退化', () {
      final d = SearchIndexDiagnostics.fromHealth({
        'ready': true,
        'dataPack': {'id': 'pack-2026-11-26-abcdef'},
      })!;
      expect(d.shortDataPackId, 'pack-2026-11');

      final shortId = SearchIndexDiagnostics.fromHealth({
        'ready': true,
        'dataPack': {'id': 'abc'},
      })!;
      expect(shortId.shortDataPackId, 'abc');
    });

    test('歌词置信度全 0 → 不显示分布行（total == 0）', () {
      final d = SearchIndexDiagnostics.fromHealth(
        _readyHealth(high: 0, medium: 0, low: 0, none: 0),
      )!;
      expect(d.confidenceTotal, 0);
      expect(d.confidenceHigh, 0);
    });

    test('降级能力按去重后的数量统计，并回退顶层 missing', () {
      final d = SearchIndexDiagnostics.fromHealth(
        _readyHealth(
          degraded: const ['cross_lingual', 'cross_lingual', 'pinyin'],
        ),
      )!;
      expect(d.degradedCapabilities, 2);
      expect(d.missingCapabilities, 2);
      expect(d.capabilityIssueCount, 2);

      final onlyTopLevel = SearchIndexDiagnostics.fromHealth({
        'ready': true,
        'missing': const ['fuzzy', 'reading'],
      })!;
      expect(onlyTopLevel.degradedCapabilities, 0);
      expect(onlyTopLevel.missingCapabilities, 2);
      expect(onlyTopLevel.capabilityIssueCount, 2);
    });

    test('dataPack 状态 false 也完整保留（由 UI 显示为缺失 / 不同源）', () {
      final d = SearchIndexDiagnostics.fromHealth(
        _readyHealth(match: false, present: false, foundationOk: false),
      )!;
      expect(d.dataPackMatch, isFalse);
      expect(d.dataPackPresent, isFalse);
      expect(d.dataPackFoundationOk, isFalse);
    });
  });

  group('SearchService 健康快照', () {
    test('refreshHealth 用 debug seam 填充缓存，不触碰 FFI', () async {
      SearchService.instance.debugDataPackHealthOverride = _readyHealth;
      final snapshot = await SearchService.instance.refreshHealth();
      expect(snapshot, isNotNull);
      expect(SearchService.instance.healthSnapshot, isNotNull);
      expect(SearchIndexDiagnostics.fromHealth(snapshot!)!.generation, 7);
    });

    test('refreshHealth 接缝抛异常 → 保留旧快照且不抛到调用方', () async {
      SearchService.instance.debugDataPackHealthOverride = _readyHealth;
      await SearchService.instance.refreshHealth();
      expect(SearchService.instance.healthSnapshot, isNotNull);

      SearchService.instance.debugDataPackHealthOverride = () =>
          throw StateError('boom');
      final snapshot = await SearchService.instance.refreshHealth();
      expect(snapshot, isNotNull, reason: '失败时返回旧缓存');
      expect(SearchService.instance.healthSnapshot, isNotNull);
    });

    test('refreshHealth 接缝返回 null → 清空缓存（旧原生库 / 未就绪）', () async {
      SearchService.instance.debugDataPackHealthOverride = _readyHealth;
      await SearchService.instance.refreshHealth();
      SearchService.instance.debugDataPackHealthOverride = () => null;
      final snapshot = await SearchService.instance.refreshHealth();
      expect(snapshot, isNull);
      expect(SearchService.instance.healthSnapshot, isNull);
    });
  });

  group('SearchSection 诊断块', () {
    Widget wrap() => ProviderScope(
      overrides: [
        appPrefsProvider.overrideWith(
          () => _Prefs(AppPrefs().copyWithSearchIndex(enabled: true)),
        ),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(
          body: SingleChildScrollView(child: SearchSection()),
        ),
      ),
    );

    testWidgets('索引就绪 + 快照 → 显示版本 / 数据包 / 置信度行', (tester) async {
      SearchService.instance.debugSetStatus(LocalSearchStatus.ready);
      SearchService.instance.debugDataPackHealthOverride = () =>
          _readyHealth(degraded: const ['cross_lingual']);
      await SearchService.instance.refreshHealth();

      await tester.pumpWidget(wrap());
      await tester.pumpAndSettle();

      expect(find.text('Index generation & versions'), findsOneWidget);
      expect(find.textContaining('Gen 7'), findsOneWidget);
      expect(find.textContaining('Schema 4'), findsOneWidget);
      expect(find.textContaining('Analyzer 17'), findsOneWidget);
      expect(find.text('Search data pack'), findsOneWidget);
      expect(find.textContaining('3583c58d4fc8'), findsOneWidget);
      expect(find.textContaining('1 degraded capabilities'), findsOneWidget);
      expect(find.text('Lyrics confidence'), findsOneWidget);
      expect(find.textContaining('High 3'), findsOneWidget);
    });

    testWidgets('快照为 null / 未就绪 → 诊断行整块省略', (tester) async {
      SearchService.instance.debugSetStatus(LocalSearchStatus.ready);
      // 未就绪载荷：formatter 返回 null。
      SearchService.instance.debugDataPackHealthOverride = () => {
        'api': 2,
        'ready': false,
      };
      await SearchService.instance.refreshHealth();

      await tester.pumpWidget(wrap());
      await tester.pumpAndSettle();

      expect(find.text('Index generation & versions'), findsNothing);
      expect(find.text('Search data pack'), findsNothing);
      expect(find.text('Lyrics confidence'), findsNothing);
    });
  });
}
