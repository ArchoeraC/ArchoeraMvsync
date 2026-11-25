// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// T4.3 §5.5.3：Dart 侧「数据包与索引同源」检查。
//
// 只比 id（编译期常量 dataPackId vs era_search_health 的 dataPack.id），
// 不在 Dart 重算哈希。这里用假 health 载荷做纯函数断言，并验证设置页能
// 把可操作提示显示出来（不崩溃、不静默接受不一致）。

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/l10n/generated/app_localizations.dart';
import 'package:archoera_mvsync/services/search/data_pack.g.dart';
import 'package:archoera_mvsync/services/search/data_pack_check.dart';
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

Map<String, dynamic> _health({
  String id = dataPackId,
  String storedId = '',
  bool present = true,
  bool foundationOk = true,
  bool? match,
  List<String> degraded = const [],
}) => {
  'api': 2,
  'ready': present,
  'dataPack': {
    'id': id,
    'storedId': storedId.isEmpty ? id : storedId,
    'match': match ?? (storedId.isEmpty || storedId == id),
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

  test('数据包一致 → 无提示', () {
    expect(checkDataPackHealth(_health()), isNull);
  });

  test('原生库数据包 ≠ Dart 常量 → 可操作的重建 / 重装提示', () {
    final problem = checkDataPackHealth(
      _health(id: 'sha256:${'0'.padRight(64, '0')}'),
    );
    expect(problem, dataPackMismatchMessage);
    expect(problem, contains('重建索引'));
  });

  test('数据包缺失 → 降级提示（不崩溃）', () {
    final health = _health(present: false, foundationOk: false);
    (health['dataPack'] as Map)['id'] = '';
    expect(checkDataPackHealth(health), dataPackMissingMessage);
  });

  test('基础归一化表缺失 → 不可用提示', () {
    expect(
      checkDataPackHealth(_health(foundationOk: false)),
      dataPackFoundationMessage,
    );
  });

  test('索引由旧数据包构建（storedId 不同）→ 提示重建索引', () {
    final problem = checkDataPackHealth(
      _health(storedId: 'sha256:${'1'.padRight(64, '1')}'),
    );
    expect(problem, dataPackStaleIndexMessage);
  });

  test('缺 dataPack 字段（旧原生库）→ 不阻断', () {
    expect(checkDataPackHealth({'api': 2, 'ready': true}), isNull);
  });

  test('降级能力透传在 health.missing（不静默少召回）', () {
    final health = _health(degraded: const ['cross_lingual']);
    expect(checkDataPackHealth(health), isNull);
    expect(health['missing'], contains('cross_lingual'));
    expect(
      (health['dataPack'] as Map)['degradedCapabilities'],
      contains('cross_lingual'),
    );
  });

  testWidgets('设置页显示「数据包与索引不一致」的可操作状态', (tester) async {
    SearchService.instance.debugSetStatus(
      LocalSearchStatus.error(dataPackMismatchMessage),
    );
    await tester.pumpWidget(
      ProviderScope(
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
      ),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining('重建索引'), findsOneWidget);
    expect(find.textContaining('重装'), findsOneWidget);
  });
}
