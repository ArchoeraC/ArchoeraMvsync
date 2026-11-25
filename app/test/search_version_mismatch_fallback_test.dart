// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// N19 索引版本不符 → 降级 LIKE（FFI / Dart 集成）。
//
// 合同：EraStore Manifest 的 `metaVersion` 低于当前版本时，`era_search_open`
// 必须拒绝打开（返回 3 / 句柄 NULL），Dart 侧 [SearchIndex.open] 得到 null；
// 查询链路不得抛到 UI，而是走 `era_search_fallback`（参数化 LIKE，T2.12）返回结果。
//
// 夹具：先用真实 indexer 建索引，再**原地**把最新提交槽里的 `metaVersion` 改成 0
// （同长度改写 + 重算 CRC32C），模拟旧格式索引；随后向 `library.db` 追加一首
// 未进索引的曲目，用「LIKE 能找到、索引找不到」来证明走的是降级路径。

import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:sqlite3/sqlite3.dart';

import 'package:archoera_mvsync/services/native_lib_paths.dart';
import 'package:archoera_mvsync/services/search/indexer_ffi.dart';
import 'package:archoera_mvsync/services/search/search_index.dart';
import 'package:archoera_mvsync/services/search/search_service.dart';
import 'package:archoera_mvsync/stores/app_prefs.dart';

void main() {
  final soPath = NativeLibPaths.resolve(NativeModule.indexer);
  final skip = soPath == null
      ? '未找到 indexer-ffi 共享库（先构建 app/core/indexer/build）'
      : false;

  setUp(() {
    SearchService.instance.debugReset();
    appPrefsSnapshot = null;
  });

  tearDown(() {
    SearchService.instance.debugReset();
    appPrefsSnapshot = null;
  });

  test('版本不符：open 拒绝（不抛错）→ era_search_fallback 仍返回结果', () {
    final lib = IndexerLibrary.load(soPath: soPath);
    final fixture = _Fixture.build(lib);

    // 对照：版本匹配时索引可打开（证明夹具本身可用）。
    final control = SearchIndex.open(fixture.searchDb, lib: lib);
    expect(control, isNotNull, reason: '版本匹配时应能打开 EraStore');
    control!.close();

    // 追加一首**只存在于 library.db、不在索引里**的曲目：降级 LIKE 才看得见。
    fixture.addTrack('t3', '晴天未索引');

    _downgradeMetaVersion(fixture.currentEraPath);
    expect(
      SearchIndex.isAvailable(fixture.searchDb),
      isTrue,
      reason: 'CURRENT 仍在：不可用必须是版本门禁，而不是索引缺失',
    );
    expect(
      SearchIndex.open(fixture.searchDb, lib: lib),
      isNull,
      reason: 'metaVersion 过旧时 era_search_open 应拒绝（契约失败即 NULL）',
    );

    // 降级路径本身（服务端 LIKE）：能找到索引里没有的 t3，且方案为 fallback@1。
    final (code, text) = lib.fallbackSearch(
      fixture.libraryDb,
      jsonEncode({
        'api': IndexerLibrary.apiVersion,
        'query': '晴天',
        'scope': 'metadata',
        'limit': 50,
      }),
    );
    expect(code, 0, reason: 'era_search_fallback 应成功，实际: $code / $text');
    final root = jsonDecode(text) as Map<String, dynamic>;
    expect(root['scheme'], 'fallback@1');
    final ids = (root['items'] as List)
        .map((raw) => (raw as Map)['trackId'] as String)
        .toList();
    expect(ids, containsAll(<String>['t1', 't3']), reason: 'LIKE 命中索引外曲目');

    // 重建后恢复：证明拒绝原因就是 metaVersion，而不是文件损坏。
    final (rebuildCode, rebuildText) = lib.runBlocking(
      dbPath: fixture.libraryDb,
      indexPath: fixture.searchDb,
      full: true,
      onProgress: nullptr.cast<Void>(),
    );
    expect(rebuildCode, 0, reason: '重建应成功，实际: $rebuildText');
    final healed = SearchIndex.open(fixture.searchDb, lib: lib);
    expect(healed, isNotNull, reason: 'metaVersion 回到当前版本后应能重新打开');
    healed!.close();
  }, skip: skip);

  test('版本不符 + 无法重建：SearchService 降级 LIKE，不抛到 UI', () async {
    final lib = IndexerLibrary.load(soPath: soPath);
    final fixture = _Fixture.build(lib);
    fixture.addTrack('t3', '晴天未索引');
    _downgradeMetaVersion(fixture.currentEraPath);

    SearchService.debugSearchDbPathOverride = fixture.searchDb;
    SearchService.debugLibraryDbPathOverride = fixture.libraryDb;
    // 版本门禁生效后服务会尝试重建；这里让重建不可用，观察纯降级链路。
    SearchService.instance.debugBuildExecutor = ({required bool full}) async => null;
    appPrefsSnapshot = AppPrefs(
      initialData: {enableLocalSearchIndexKey: true},
    );

    final hits = await SearchService.instance.search('晴天');
    expect(hits, isNotNull, reason: '索引不可用必须降级 LIKE，而不是返回 null / 抛错');
    final ids = hits!.map((hit) => hit.trackId).toSet();
    expect(ids, contains('t3'), reason: 'LIKE 命中索引外曲目 t3，证明未走索引');
    expect(ids, contains('t1'));
    expect(SearchService.instance.indexReady, isFalse);
  }, skip: skip);
}

/* ---------------------------------------------------------------------- */
/* 夹具                                                                    */
/* ---------------------------------------------------------------------- */

class _Fixture {
  _Fixture(this.dir);

  final Directory dir;

  String get libraryDb => '${dir.path}/library.db';
  String get searchDb => '${dir.path}/search.db';
  String get eraDir => '${dir.path}/search.era';

  String get currentEraPath {
    final name = File('$eraDir/CURRENT').readAsStringSync().trim();
    return '$eraDir/$name';
  }

  /// 建最小 `library.db`（t1/t2）并跑一次真实全量构建。
  static _Fixture build(IndexerLibrary lib) {
    final dir = Directory.systemTemp.createTempSync('indexer_version_mismatch');
    addTearDown(() => dir.deleteSync(recursive: true));

    final db = sqlite3.open('${dir.path}/library.db');
    db.execute('''
      CREATE TABLE tracks (
        id TEXT PRIMARY KEY, path TEXT, title TEXT, artists TEXT,
        album TEXT, lyrics TEXT, file_mtime INTEGER
      )
    ''');
    db.execute('CREATE TABLE _scanner_meta (generation INTEGER)');
    db.execute('INSERT INTO _scanner_meta (generation) VALUES (1)');
    for (final (id, title) in const [('t1', '晴天'), ('t2', '稻香')]) {
      db.execute(
        'INSERT INTO tracks (id, path, title, artists, album, lyrics, file_mtime) '
        'VALUES (?, ?, ?, ?, ?, ?, ?)',
        [id, '${dir.path}/$id.mp3', title, '[]', null, null, 1],
      );
    }
    db.close();

    final (code, text) = lib.runBlocking(
      dbPath: '${dir.path}/library.db',
      indexPath: '${dir.path}/search.db',
      full: true,
      onProgress: nullptr.cast<Void>(),
    );
    expect(code, 0, reason: 'era_index_run 应返回 0，实际: $text');
    return _Fixture(dir);
  }

  /// 索引构建后追加曲目（模拟「曲库新了、索引还是旧的」窗口）。
  void addTrack(String id, String title) {
    final db = sqlite3.open(libraryDb);
    db.execute(
      'INSERT INTO tracks (id, path, title, artists, album, lyrics, file_mtime) '
      'VALUES (?, ?, ?, ?, ?, ?, ?)',
      [id, '${dir.path}/$id.mp3', title, '[]', null, null, 2],
    );
    db.close();
  }
}

/* ---------------------------------------------------------------------- */
/* EraStore Manifest 降级（测试夹具专用）                                   */
/* ---------------------------------------------------------------------- */

const int _headerSize = 64;
const int _commitSlotSize = 32;
const int _slotCount = 2;

/// 把最新提交槽的 Manifest `metaVersion` 改成 0（同长度），并重算 CRC32C。
///
/// 只动测试临时文件：读入整文件 → 改 Manifest 一个字节 → 回写 Manifest 与槽 CRC。
void _downgradeMetaVersion(String eraPath) {
  final file = File(eraPath);
  final bytes = Uint8List.fromList(file.readAsBytesSync());

  var bestSeq = -1;
  var bestSlot = -1;
  var manifestOffset = 0;
  var manifestLength = 0;

  for (var slot = 0; slot < _slotCount; slot++) {
    final at = _headerSize + slot * _commitSlotSize;
    final gen = _readU64(bytes, at);
    final offset = _readU64(bytes, at + 8);
    final length = _readU32(bytes, at + 16);
    final crc = _readU32(bytes, at + 20);
    final seq = _readU64(bytes, at + 24);
    if (gen < 1 || length == 0 || offset + length > bytes.length) continue;
    final manifest = bytes.sublist(offset, offset + length);
    if (_crc32c(manifest) != crc) continue;
    if (seq <= bestSeq) continue;
    bestSeq = seq;
    bestSlot = slot;
    manifestOffset = offset;
    manifestLength = length;
  }
  expect(bestSlot, greaterThanOrEqualTo(0), reason: 'EraStore 夹具应有有效提交');

  final manifest = Uint8List.fromList(
    bytes.sublist(manifestOffset, manifestOffset + manifestLength),
  );
  // 不写死版本号：Manifest.CurrentMetaVersion 会随格式演进（DocStore v3 起为 2）。
  // 定位 `"metaVersion":` 后的数字并把整段数字改成 0（同长度改写，保持 CRC 可重算）。
  final needle = ascii.encode('"metaVersion":');
  final at = _indexOf(manifest, needle);
  expect(at, greaterThanOrEqualTo(0), reason: 'Manifest 应含 metaVersion');
  var digit = at + needle.length;
  var written = false;
  while (digit < manifest.length) {
    final c = manifest[digit];
    if (c < 0x30 || c > 0x39) break;
    manifest[digit] = 0x30;
    digit++;
    written = true;
  }
  expect(written, isTrue, reason: 'metaVersion 应为非空数字');

  bytes.setRange(manifestOffset, manifestOffset + manifestLength, manifest);
  _writeU32(
    bytes,
    _headerSize + bestSlot * _commitSlotSize + 20,
    _crc32c(manifest),
  );
  file.writeAsBytesSync(bytes, flush: true);
}

int _readU32(List<int> bytes, int at) =>
    bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);

int _readU64(List<int> bytes, int at) =>
    _readU32(bytes, at) | (_readU32(bytes, at + 4) << 32);

void _writeU32(Uint8List bytes, int at, int value) {
  bytes[at] = value & 0xFF;
  bytes[at + 1] = (value >> 8) & 0xFF;
  bytes[at + 2] = (value >> 16) & 0xFF;
  bytes[at + 3] = (value >> 24) & 0xFF;
}

int _indexOf(List<int> haystack, List<int> needle) {
  if (needle.isEmpty || needle.length > haystack.length) return -1;
  for (var i = 0; i <= haystack.length - needle.length; i++) {
    var match = true;
    for (var j = 0; j < needle.length; j++) {
      if (haystack[i + j] != needle[j]) {
        match = false;
        break;
      }
    }
    if (match) return i;
  }
  return -1;
}

/// 与 C# `EraStore.Crc32C` 同参数（Castagnoli，初值 0xFFFFFFFF，末尾取反）。
final List<int> _crc32cTable = _buildCrc32cTable();

List<int> _buildCrc32cTable() {
  const poly = 0x82F63B78;
  final table = List<int>.filled(256, 0);
  for (var i = 0; i < 256; i++) {
    var c = i;
    for (var k = 0; k < 8; k++) {
      c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
    }
    table[i] = c;
  }
  return table;
}

int _crc32c(List<int> data) {
  var crc = 0xFFFFFFFF;
  for (final b in data) {
    crc = _crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
  }
  return (~crc) & 0xFFFFFFFF;
}
