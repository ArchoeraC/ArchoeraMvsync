// ArchoeraMvsumc UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 品牌改名数据迁移：旧 ArchoeraMusic 目录 → ArchoeraMvsumc。
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsumc/stores/data_dir.dart';

void main() {
  late Directory temp;
  late String root;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('data-dir-migration-');
    root = temp.path;
  });

  tearDown(() async {
    try {
      await temp.delete(recursive: true);
    } catch (_) {}
  });

  test('迁移旧目录并删除旧文件夹', () async {
    final oldDir = Directory('$root/ArchoeraMusic')..createSync(recursive: true);
    File('${oldDir.path}/prefs.json').writeAsStringSync('{"a":1}');
    Directory('${oldDir.path}/cache').createSync();

    final leftover = await migrateLegacyDataDir(rootDir: root);

    expect(leftover, isNull);
    expect(Directory('$root/ArchoeraMusic').existsSync(), isFalse);
    expect(
      File('$root/ArchoeraMvsumc/prefs.json').readAsStringSync(),
      '{"a":1}',
    );
    expect(Directory('$root/ArchoeraMvsumc/cache').existsSync(), isTrue);
  });

  test('新目录已存在时不覆盖，返回旧目录路径', () async {
    Directory('$root/ArchoeraMusic').createSync();
    Directory('$root/ArchoeraMvsumc').createSync();

    final leftover = await migrateLegacyDataDir(rootDir: root);

    expect(leftover, '$root/ArchoeraMusic');
    expect(Directory('$root/ArchoeraMusic').existsSync(), isTrue);
  });

  test('没有旧目录时不做任何事', () async {
    expect(await migrateLegacyDataDir(rootDir: root), isNull);
    expect(Directory('$root/ArchoeraMvsumc').existsSync(), isFalse);
  });
}
