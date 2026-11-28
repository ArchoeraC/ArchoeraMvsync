// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/search/search_models.dart';
import 'package:archoera_mvsync/services/search/search_service.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';

/// 总开关门控：关闭时不构建 / 不触碰 search.db；`search` 仍会尝试 C# 参数化
/// LIKE 降级（T2.11 决定，可能加载原生模块），库缺失时返回 null（调用方回退
/// 既有 LIKE）。本文件把 `library.db` 指向不存在的路径，保持确定性。
void main() {
  setUp(() {
    appPrefsSnapshot = null;
    SearchService.debugLibraryDbPathOverride =
        '${Directory.systemTemp.path}/archoera-gate-no-library.db';
  });
  tearDown(() {
    appPrefsSnapshot = null;
    SearchService.instance.debugReindexOverride = null;
    SearchService.debugLibraryDbPathOverride = null;
  });

  test('快照缺失 / 总开关关：enabled=false，降级库缺失 → search 返回 null', () async {
    final service = SearchService.instance;
    expect(service.enabled, isFalse);
    expect(await service.search('周杰伦'), isNull);

    appPrefsSnapshot = AppPrefs(initialData: {enableLocalSearchIndexKey: false});
    expect(service.enabled, isFalse);
    expect(await service.search('周杰伦'), isNull);
  });

  test('空查询：即使总开关开启也返回 null（等于原列表）', () async {
    appPrefsSnapshot = AppPrefs(initialData: {enableLocalSearchIndexKey: true});
    final service = SearchService.instance;
    expect(service.enabled, isTrue);
    // 空 / 纯空白在门控处短路，不触碰模块 / search.db。
    expect(await service.search(''), isNull);
    expect(await service.search('   '), isNull);
  });

  test('增量重建：总开关关 → no-op（不加载模块 / 不抛异常）', () async {
    final service = SearchService.instance;
    expect(service.enabled, isFalse);
    await service.reindex();
    expect(service.status, LocalSearchStatus.disabled);

    appPrefsSnapshot = AppPrefs(initialData: {enableLocalSearchIndexKey: false});
    await service.reindex();
    expect(service.status, LocalSearchStatus.disabled);
  });

  test('增量重建：测试接缝直通（不触碰 FFI / search.db）', () async {
    var calls = 0;
    SearchService.instance.debugReindexOverride = () async {
      calls++;
    };
    await SearchService.instance.reindex();
    expect(calls, 1);
  });

  test('indexReady：未构建 / 关闭态一律 false（扫描后收敛的判定门）', () async {
    final service = SearchService.instance;
    expect(service.indexReady, isFalse);

    appPrefsSnapshot = AppPrefs(initialData: {enableLocalSearchIndexKey: true});
    // 总开关开启但尚未构建（未触碰模块 / search.db）→ 不作为收敛目标
    expect(service.enabled, isTrue);
    expect(service.indexReady, isFalse);

    // 增量重建（接缝直通）也不会凭空让索引「就绪」
    SearchService.instance.debugReindexOverride = () async {};
    await service.reindex();
    expect(service.indexReady, isFalse);
  });
}
