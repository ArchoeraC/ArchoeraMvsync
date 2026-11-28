// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:convert';
import 'dart:ffi';
import 'dart:io';

import 'package:flutter/foundation.dart' show visibleForTesting;

import 'indexer_ffi.dart';
import 'query_log.dart';
import 'search_models.dart';

/// 本地搜索索引（`search.era/`）的只读查询视图（**薄 FFI 客户端**）。
///
/// 查询能力（词法 / 拼音 / 首字母 / 日文读音 / 跨语言表达式编译、渐进兜底、
/// bm25 排序、`doc_id → track_id` 映射）全部在 C# 索引器内完成（ABI v2：
/// `era_search_open` 句柄 + `era_search_run`，请求 / 响应 JSON 均为 `api: 2`）。
/// Dart 只负责：
///   1. 组装查询请求 JSON（[search]）；
///   2. 把结果 JSON 映射为 [LocalSearchHit]；
///   3. 累加兜底趟计数器（诊断 / 测试接缝，S1 过渡字段 `passes`）；
///   4. 生命周期（[close] 关闭 C# 侧查询句柄）。
///
/// 句柄在 [open] 时建立；重建 / 删除索引前必须 [close]
/// （Windows 上残留只读句柄会阻塞 `File.Move`）。
class SearchIndex {
  SearchIndex._(this._dbPath, this._lib, this._handle);

  final String _dbPath;
  final IndexerLibrary _lib;

  /// `era_search_open` 返回的查询句柄；[close] 后置 null。
  Pointer<Void>? _handle;

  bool _closed = false;

  /// 本连接已执行的**模糊**（拉丁编辑距离）查询次数（诊断 / 测试接缝）。
  @visibleForTesting
  int fuzzyQueries = 0;

  /// 本连接已执行的**中文 CJK 容错**查询次数（诊断 / 测试接缝）。
  @visibleForTesting
  int cjkFuzzyQueries = 0;

  /// 本连接已执行的**跨语言兜底**查询次数（诊断 / 测试接缝）。
  @visibleForTesting
  int crossLingualQueries = 0;

  /// 本连接已执行的**拼音宽松兜底**查询次数（诊断 / 测试接缝）。
  @visibleForTesting
  int pinyinLooseQueries = 0;

  /// 本连接已执行的**丢词降级**查询次数（诊断 / 测试接缝）。
  @visibleForTesting
  int dropTokenQueries = 0;

  /// 本连接已执行的**日文读音兜底**查询次数（诊断 / 测试接缝）。
  @visibleForTesting
  int readingQueries = 0;

  /// 当前**累加**的兜底趟计数器快照。
  ///
  /// 各字段是累加值（每查一次 `+=`），调用方若只想要「本次查询实际执行了哪些趟」，
  /// 需在查询前后各取一次并相减（见 `SearchService.search`）。
  PassCounts get passCounts => PassCounts(
    pinyinLoose: pinyinLooseQueries,
    dropTokens: dropTokenQueries,
    reading: readingQueries,
    crossLingual: crossLingualQueries,
    fuzzy: fuzzyQueries,
    cjkFuzzy: cjkFuzzyQueries,
  );

  /// 打开 `<数据库目录>/search.era/`（只读）。索引缺失 / 句柄打开失败返回 null。
  ///
  /// T3.13：查询只依赖 `search.era/CURRENT`；`search.db` 仅作为历史路径参数保留。
  /// 真正的就绪检查（Manifest / metaVersion）由 `era_search_open` 在 C# 侧完成。
  ///
  /// [libraryDbPath] 非空时经 `options_json.libraryDb` 传给索引器，查询期过滤源曲库
  /// 已删除的曲目（T2.9）；库不可读时索引器失败开放（不过滤）。
  static SearchIndex? open(
    String dbPath, {
    required IndexerLibrary lib,
    String? libraryDbPath,
  }) {
    if (!isAvailable(dbPath)) return null;
    final optionsJson = (libraryDbPath == null || libraryDbPath.isEmpty)
        ? ''
        : jsonEncode({'libraryDb': libraryDbPath});
    final handle = lib.openSearch(dbPath, optionsJson: optionsJson);
    if (handle == nullptr) return null;
    return SearchIndex._(dbPath, lib, handle);
  }

  /// EraStore 是否已就绪（`<dir>/search.era/CURRENT` 存在）。
  ///
  /// 不解析 Manifest / 不读 SQLite：版本与损坏由 `era_search_open` 判定，
  /// Dart 只做「是否值得尝试打开」的轻量门禁。
  static bool isAvailable(String dbPath) {
    final dir = File(dbPath).parent.path;
    final sep = Platform.pathSeparator;
    return File('$dir${sep}search.era${sep}CURRENT').existsSync();
  }

  /// 当前视图是否可用（未关闭、句柄在、EraStore 仍存在）。
  ///
  /// T3.13 起生产索引是 `<dir>/search.era/`，**不再产生 `search.db`**；
  /// 这里曾经检查 `File(_dbPath).existsSync()`，会让 [available] 恒为 false，
  /// 进而让 [SearchService] 每次搜索都重新走「构建 + 重开」。
  bool get available => !_closed && _handle != null && isAvailable(_dbPath);

  /// 执行一次搜索，返回按 BM25 相关度升序（最佳在前）的命中列表。
  ///
  /// 只发送**非废弃**字段：`api=2` / `query` / `scope` / `limit` / `total=none`
  /// / `explain=false` / `highlight=false` / `snippet=false`。渐进兜底（服务端
  /// 默认 `default@1` 方案，行为等同旧 `fuzzy:true`）全部在 C# 内完成；本方法
  /// 只传参、解析结果并累加计数器。
  ///
  /// 空查询返回空列表；原生调用失败或响应 `api` 版本不符抛 [StateError]
  /// （调用方按既有约定降级）。
  List<LocalSearchHit> search(
    String query, {
    LocalSearchScope scope = LocalSearchScope.all,
    int limit = 200,
  }) {
    if (_closed) return const [];
    final keyword = query.trim();
    if (keyword.isEmpty) return const [];

    final handle = _handle;
    if (handle == null) return const [];

    final request = jsonEncode({
      'api': IndexerLibrary.apiVersion,
      'query': keyword,
      'scope': scope.name,
      'limit': limit,
      'total': 'none',
      'explain': false,
      'highlight': false,
      'snippet': false,
    });
    final (code, text) = _lib.search(handle, request);
    if (code != 0) throw StateError(_errorMessage(code, text));

    final root = jsonDecode(text) as Map<String, dynamic>;
    final api = root['api'];
    if (api != IndexerLibrary.apiVersion) {
      throw StateError(
        '搜索响应 ABI 版本不符: 期望 ${IndexerLibrary.apiVersion}，实际 $api',
      );
    }

    // `passes` 是 S1 过渡字段（v2 最终由 explain / trace 取代），此处照旧累加
    // 供查询日志与测试接缝使用；缺省按 0。
    final passes = root['passes'];
    if (passes is Map<String, dynamic>) {
      pinyinLooseQueries += _intOf(passes['pinyinLoose']);
      dropTokenQueries += _intOf(passes['dropTokens']);
      readingQueries += _intOf(passes['reading']);
      crossLingualQueries += _intOf(passes['crossLingual']);
      fuzzyQueries += _intOf(passes['fuzzy']);
      cjkFuzzyQueries += _intOf(passes['cjkFuzzy']);
    }

    final items = root['items'];
    if (items is! List) return const [];
    final hits = <LocalSearchHit>[];
    for (final raw in items) {
      if (raw is! Map<String, dynamic>) continue;
      final trackId = raw['trackId'];
      final score = raw['score'];
      if (trackId is! String || score is! num) continue;
      hits.add(LocalSearchHit(
        trackId: trackId,
        score: score.toDouble(),
        // 命中原由（精度审计 §6.3）：S1 过渡期仍由 `pass` 携带。
        pass: LocalSearchPass.fromName(raw['pass']),
      ));
    }
    return hits;
  }

  /// 关闭 C# 侧查询句柄（重建 / 删除索引前必须调用）。
  void close() {
    if (_closed) return;
    _closed = true;
    final handle = _handle;
    _handle = null;
    if (handle != null) _lib.closeSearch(handle);
  }

  /// 从错误响应里取 `error.message`；解析失败时退回原始文本 / 退出码。
  static String _errorMessage(int code, String text) {
    try {
      final root = jsonDecode(text);
      if (root is Map<String, dynamic>) {
        final error = root['error'];
        if (error is Map<String, dynamic>) {
          final message = error['message'];
          if (message is String && message.isNotEmpty) return message;
        }
      }
    } catch (_) {
      // 非 JSON 错误文本：走下面的兜底。
    }
    return text.isEmpty ? 'era_search_run 失败（exit $code）' : text;
  }

  static int _intOf(Object? value) => value is num ? value.toInt() : 0;

}
