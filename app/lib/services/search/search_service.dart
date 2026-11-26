// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:io';

import 'package:ffi/ffi.dart';
import 'package:flutter/foundation.dart' show ValueNotifier, visibleForTesting;

import '../../stores/app_prefs.dart';
import '../log/log.dart';
import '../scanner/library_scanner.dart';
import 'data_pack_check.dart';
import 'indexer_ffi.dart';
import 'query_log.dart';
import 'query_log_file.dart';
import 'search_index.dart';
import 'search_models.dart';

/// 本地曲库搜索索引服务（单例编排器，**总开关门控**）。
///
/// 设计文档 `docs/local-search-index-plan.md` §2.4 / §9.3：
///   - 总开关 `enableLocalSearchIndex` **默认关**；关闭时**不解析 / 不加载**
///     indexer 模块、**不建库**、**不查询**，[search] 直接返回 null（调用方
///     回退现有 `LIKE`），零成本；
///   - 开启时按需加载 indexer（NativeAOT 常驻、不 unload），`search.db` 缺失或
///     schema 版本不符时在**后台 isolate** 跑 `era_index_run`（全量 / 增量），
///     完成后以只读连接打开 [SearchIndex]；
///   - 任何失败（模块缺失 / 构建失败 / 查询异常）都降级为返回 null，**绝不抛到
///     UI**（索引是派生数据，可随时重建）。
///
/// **构建串行化（build gate）**：`rebuild` / `reindex` / `deleteIndex` 以及
/// [ensureIndex] 的「构建 + 重开」关键段都是**互斥**的——全部经单一异步门
/// [_serialize] 排队，后到者等待而非并发执行。并发调用 NativeAOT `era_index_run`
/// （同一 `search.db` / `-wal` / `-shm`）会互相破坏，故必须串行。
/// 该门只是 Dart 侧 `Future` 链式串行（不是内核等待式竞态判定），门内**绝不**
/// 调用会再次取门的公开方法，避免自锁。
///
/// 路径：索引目录 = `<数据目录>/database/search.era/`（`search.db` 仅作目录锚点）；`library.db` 走
/// [LibraryScanner.defaultDbPath]（与 scanner 同一真值库）。
class SearchService {
  SearchService._();

  /// 全局单例（进程内一个 indexer 句柄 / 一份只读连接）。
  static final SearchService instance = SearchService._();

  /// 索引目录锚点（与 `library.db` 同目录；实际数据在 `search.era/`）。
  static String defaultSearchDbPath() {
    final override = debugSearchDbPathOverride;
    if (override != null && override.isNotEmpty) return override;
    return '${LibraryScanner.defaultDataDir()}/database/search.db';
  }

  /// 测试接缝：非空时覆盖 [defaultSearchDbPath]（让单测不触碰真实数据目录）。
  @visibleForTesting
  static String? debugSearchDbPathOverride;

  /// 测试接缝：非空时覆盖**源曲库**（`library.db`）路径（见 [LibraryScanner.defaultDbPath]）。
  ///
  /// 「真实构建路径」的单测需要它，否则会去索引用户真实曲库（慢且会写真实
  /// `search.db`）。生产恒为 null。
  @visibleForTesting
  static String? debugLibraryDbPathOverride;

  /// 构建进度（主 isolate 消费；无进行中构建时为 null）。
  ///
  /// 非 `final`：便于测试重置（见 [debugReset]）后重新绑定监听。生产代码
  /// 只读访问，不重新赋值。
  ValueNotifier<IndexerProgress?> progress =
      ValueNotifier<IndexerProgress?>(null);

  IndexerLibrary? _lib;
  String? _soPath;
  SearchIndex? _index;
  bool _disposed = false;
  bool _progressDisposed = false;

  // ------------------------------------------------------- 构建门（build gate）

  /// 门尾：所有经门的关键段串行链在其后。**永不携带错误**（见 [_serialize]）。
  Future<void> _gateTail = Future<void>.value();

  /// 门内「已排队 + 执行中」的关键段数量。>0 即门被占用（构建/重开/删除）。
  int _gateOccupancy = 0;

  bool get _gateBusy => _gateOccupancy > 0;

  /// 把 [action] 排入构建门，严格串行执行并返回其结果。
  ///
  /// FIFO：后到者链在前一关键段完成之后；**不并发、不丢弃**。
  /// [action] **不得**调用任何会再次取门的公开方法（否则自锁）——内部关键段
  /// （`_buildAndOpenLocked` / 删除逻辑）只使用无门私有原语。
  Future<T> _serialize<T>(Future<T> Function() action) {
    _gateOccupancy++;
    final previous = _gateTail;
    final result = previous.then((_) => action());
    // 门尾吞掉错误：门链本身不会因单段失败而断裂；占用计数在段结束后回落。
    _gateTail = result.then<void>((_) {}, onError: (_) {}).whenComplete(() {
      _gateOccupancy--;
    });
    return result;
  }

  /// 测试接缝：非空时 [reindex] 直接委托之（**不触碰 FFI / search.db**）。
  ///
  /// 单测用它验证「`.lrc` 子开关切换 → 触发增量重建」的接线，避免真实原生调用。
  @visibleForTesting
  Future<void> Function()? debugReindexOverride;

  /// 测试接缝：非空时替代 [ensureIndex] / [rebuild] / [reindex] 的**真实构建 +
  /// 开库**关键段（仍在构建门内被调用，故可用于验证串行化）。返回就绪索引
  /// （测试可返回 null，不触碰 FFI / search.db）。
  @visibleForTesting
  Future<SearchIndex?> Function({required bool full})? debugBuildExecutor;

  /// 测试接缝：非空时 [cancel] 委托之（**不触碰 FFI**），供单测验证
  /// 「关闭总开关 → 取消在途构建」的接线。
  @visibleForTesting
  bool Function()? debugCancelOverride;

  /// 测试接缝：非空时用这个假 health 载荷替代 `era_search_health`（T4.3）。
  /// 供单测验证「数据包不一致 → 设置页可操作提示」的接线，不触碰原生库。
  @visibleForTesting
  Map<String, dynamic>? Function()? debugDataPackHealthOverride;

  /// 启动预构建是否已触发（幂等门：仅首次 [warmUpOnStartup] 真正生效）。
  bool _startupWarmUpTriggered = false;

  LocalSearchStatus _status = LocalSearchStatus.disabled;
  LocalSearchStatus get status => _status;

  /// 最近一次构建结果摘要（诊断 / 设置页展示用）。
  IndexerRunResult? lastResult;

  /// 最近一次 `era_search_health` 的原始载荷（设置页诊断块的数据来源）。
  ///
  /// 在索引打开 / 数据包校验时填充（[_checkDataPackLocked]），**UI 线程只读缓存**，
  /// 不在 `build` 里发起 FFI / IO。未就绪或调用失败时为 null。
  Map<String, dynamic>? _healthSnapshot;

  /// 健康快照（只读；可为 null）。供 `SearchIndexDiagnostics.fromHealth` 解析。
  Map<String, dynamic>? get healthSnapshot => _healthSnapshot;

  /// 刷新健康快照（诊断用）：在**后台 isolate**读 `era_search_health`，
  /// 绝不抛异常。
  ///
  /// - 未启用 / 模块未加载 / 构建门被占用：只返回现有缓存（可能为 null），
  ///   不抢占构建资源；
  /// - 读取失败：记录 warn 后返回旧缓存；成功则替换缓存。
  ///
  /// 生产常态是：索引打开时 [_checkDataPackLocked] 已填充快照，这里只用于
  /// 「设置页打开 / 重建完成」时按需补一次，避免 UI 线程做 IO。
  Future<Map<String, dynamic>?> refreshHealth() async {
    if (_disposed) return _healthSnapshot;

    // 测试接缝优先：用假载荷直接填充，不触碰原生库（与数据包校验同一 seam）。
    final override = debugDataPackHealthOverride;
    if (override != null) {
      try {
        _healthSnapshot = override();
      } catch (err) {
        Log.w('search', '健康快照接缝失败（保留旧值）: $err');
      }
      return _healthSnapshot;
    }

    if (!enabled || _gateBusy) return _healthSnapshot;
    final soPath = _soPath;
    if (soPath == null) return _healthSnapshot;
    try {
      final snapshot = await IndexerLibrary.healthInIsolate(
        soPath: soPath,
        indexPath: defaultSearchDbPath(),
      );
      if (_disposed) return null;
      _healthSnapshot = snapshot;
      return snapshot;
    } catch (err) {
      Log.w('search', '健康快照刷新失败（保留旧值）: $err');
      return _healthSnapshot;
    }
  }

  /// 最近一次构建失败的**具体**原因（`era_index_run` 退出信息 / isolate 异常文本）；
  /// 未构建或成功时为 null。
  ///
  /// 由 [_buildAndOpenLocked] 转述进 [status]（error 态的 `message`），使设置页
  /// 显示具体原因（如「源曲库不存在: …」）而非通用文案。
  String? _buildFailure;

  /// 总开关是否开启（默认 false；关闭时整套能力零成本）。
  bool get enabled => appPrefsSnapshot?.enableLocalSearchIndex ?? false;

  /// 只读索引是否**已打开且可用**（schema 版本匹配、未关闭）。
  ///
  /// 用于「曲库变更后是否需要让索引收敛」的判定：已就绪说明查询会走索引（不再回退
  /// `LIKE`），此时曲库变了就必须增量收敛，否则新曲目搜不到；未就绪时首次构建
  /// （[ensureIndex] / 启动预构建）本就基于最新曲库，无需在扫描后抢占资源。
  bool get indexReady => _index?.available ?? false;

  /// `.lrc` 侧车歌词子开关（仅总开关开启时才有意义）。
  bool get indexLrc => appPrefsSnapshot?.indexLrc ?? false;

  /// 歌词罗马音生成子开关（仅总开关开启时才有意义；T2-9）。
  bool get romajiLyrics => appPrefsSnapshot?.romajiLyrics ?? false;

  /// 歌词首字母列子开关（仅总开关开启时才有意义；T3.4 V4，默认关）。
  bool get initialsLyrics => appPrefsSnapshot?.initialsLyrics ?? false;

  /// 构建期传给索引器的 `options_json`（纯函数，供单测断言接线）。
  ///
  /// 键名与 FFI 解析一致：`initialsLyrics` 为 T3.4 V4 新增（缺省 false）。
  @visibleForTesting
  static Map<String, dynamic> buildOptionsMap({
    required bool indexLrc,
    required bool romajiLyrics,
    required bool initialsLyrics,
  }) => {
    'index_lrc': indexLrc,
    'pinyin': true,
    'romaji_lyrics': romajiLyrics,
    'initialsLyrics': initialsLyrics,
  };

  /// 搜索：返回命中的 `trackId`（已按相关度排序）；未开启 / 未就绪 / 失败
  /// 一律返回 null，调用方回退 `LIKE`。空关键字同样返回 null（等于原列表）。
  ///
  /// 索引就绪时直接用只读连接查询（不排队）；**构建门被占用时不排队阻塞**，
  /// 直接返回 null 回退 `LIKE`（构建完成后再次搜索即命中）；其余情况经
  /// [ensureIndex] 走构建门。任何异常都降级返回 null，**绝不抛到 UI**。
  ///
  /// [scope] 取 `all` / `metadata` / `lyrics`，直接映射为请求 JSON 的 `scope`。
  ///
  /// 服务端默认 `default@1` 方案已启用兜底（渐进趟 / 容错 / 跨语言），行为与旧
  /// `fuzzy: true` 一致；Dart 不再传 `fuzzy` / `minExact` / `offset` 等废弃字段。
  /// 失败仍降级返回 null，不抛到 UI。
  Future<List<LocalSearchHit>?> search(
    String query, {
    LocalSearchScope scope = LocalSearchScope.all,
    int limit = 200,
  }) async {
    // 总开关关闭：仍尝试 C# 参数化 LIKE 降级（T2.11 决定，接受加载原生库），
    // 失败再返回 null 让调用方回退既有 LIKE。分页器在关闭态本就不会走到这里，
    // 故生产路径仍保持零成本。
    if (!enabled) return await searchFallback(query, limit: limit);
    final keyword = query.trim();
    if (keyword.isEmpty) return null;
    try {
      final index = await _resolveIndexForSearch();
      // 索引未就绪（构建中 / 构建失败 / 版本不符）：走 C# 参数化 LIKE 降级（T2.12），
      // 而不是回 Dart 拼 SQL；本次降级失败才返回 null 让调用方走既有 LIKE。
      if (index == null) return await searchFallback(keyword, limit: limit);
      final started = QueryLogger.enabled ? DateTime.now() : null;
      // 计数器是累加值：记录前先取快照，查询后再求差，才能得到**本次**实际趟次。
      final before = index.passCounts;
      final hits = index.search(keyword, scope: scope, limit: limit);
      if (started != null) {
        _recordQuery(
          keyword,
          hits,
          index.passCounts - before,
          started,
          scope == LocalSearchScope.metadata,
        );
      }
      return hits;
    } catch (err) {
      Log.w('search', '本地索引搜索失败，回退 LIKE: $err');
      _status = LocalSearchStatus.error('$err');
      return null;
    }
  }

  /// 无索引时的降级检索（`era_search_fallback`，T2.12）：由 C# 只读 `library.db`，
  /// 参数化 LIKE + 字段权重（title > artist > album），Dart 不再拼 SQL。
  ///
  /// - 空关键字 / 原生模块不可用 / 库不存在 → 返回 null；
  /// - 返回 null 表示调用方应回退既有 Dart LIKE；空列表是**真实空结果**。
  ///
  /// 不检查总开关（T2.11）：开关关闭时也允许显式调用本方法（会加载原生库），
  /// 是否调用由上层决定。
  ///
  /// 任何异常都降级返回 null，绝不抛到 UI。
  Future<List<LocalSearchHit>?> searchFallback(
    String query, {
    int limit = 200,
  }) async {
    if (_disposed) return null;
    final keyword = query.trim();
    if (keyword.isEmpty) return null;
    final lib = _ensureLib();
    if (lib == null) return null;
    final libraryDb =
        debugLibraryDbPathOverride ?? LibraryScanner.defaultDbPath();
    if (!File(libraryDb).existsSync()) return null;
    try {
      final request = jsonEncode({
        'api': IndexerLibrary.apiVersion,
        'query': keyword,
        'scope': 'metadata',
        'limit': limit,
        'total': 'exact',
        'explain': false,
        'highlight': false,
        'snippet': false,
      });
      final (code, text) = lib.fallbackSearch(libraryDb, request);
      if (code != 0) {
        Log.w('search', '降级检索失败（code=$code），回退 Dart LIKE: $text');
        return null;
      }
      final root = jsonDecode(text) as Map<String, dynamic>;
      if (root['api'] != IndexerLibrary.apiVersion) return null;
      final items = root['items'];
      if (items is! List) return const [];
      final hits = <LocalSearchHit>[];
      for (final raw in items) {
        if (raw is! Map<String, dynamic>) continue;
        final trackId = raw['trackId'];
        final score = raw['score'];
        if (trackId is! String || score is! num) continue;
        hits.add(
          LocalSearchHit(
            trackId: trackId,
            score: score.toDouble(),
            pass: LocalSearchPass.fromName(raw['pass']),
          ),
        );
      }
      return hits;
    } catch (err) {
      Log.w('search', '降级检索异常，回退 Dart LIKE: $err');
      return null;
    }
  }

  /// 记录一次查询（**只在本机、仅日志落盘开启时**；T2-7 助词容错矩阵的数据来源）。
  ///
  /// 失败一律吞掉：查询日志是诊断增强，绝不阻断搜索主路径。
  void _recordQuery(
    String keyword,
    List<LocalSearchHit>? hits,
    PassCounts passes,
    DateTime started,
    bool metadataOnly,
  ) {
    try {
      if (!shouldLogQuery(keyword)) return;
      QueryLogger.record(
        QueryLogEntry(
          query: keyword,
          hitCount: hits?.length ?? 0,
          passes: passesOf(passes),
          elapsedMs: DateTime.now().difference(started).inMilliseconds,
          metadataOnly: metadataOnly,
        ),
      );
    } catch (err) {
      Log.d('search', '查询日志写入失败（已忽略）: $err');
    }
  }

  /// 按 trackId 批量补高亮 / 歌词片段（`era_search_decorate`，T2.11）。
  ///
  /// 由分页器在载入一页（≤300 行）后调用：只读 `library.db`，不重跑排名。
  /// 返回 null 表示装饰不可用（模块 / 库缺失或调用失败），调用方保持无高亮。
  /// 片段已按「元数据已命中不展示片段」过滤（与旧 Dart 行为一致）。
  Future<SearchDecorations?> decorate(
    String query,
    List<String> trackIds,
  ) async {
    if (_disposed) return null;
    final keyword = query.trim();
    if (keyword.isEmpty || trackIds.isEmpty) return null;
    final lib = _ensureLib();
    if (lib == null) return null;
    final libraryDb =
        debugLibraryDbPathOverride ?? LibraryScanner.defaultDbPath();
    if (!File(libraryDb).existsSync()) return null;
    try {
      final request = jsonEncode({
        'api': IndexerLibrary.apiVersion,
        'query': keyword,
        'trackIds': trackIds,
        'snippet': true,
      });
      final (code, text) = lib.decorateSearch(libraryDb, request);
      if (code != 0) {
        Log.w('search', '结果装饰失败（code=$code），保持无高亮: $text');
        return null;
      }
      final root = jsonDecode(text) as Map<String, dynamic>;
      if (root['api'] != IndexerLibrary.apiVersion) return null;

      final highlights = <String, LocalTrackHighlight>{};
      final rawHighlights = root['highlights'];
      if (rawHighlights is Map) {
        for (final entry in rawHighlights.entries) {
          final id = entry.key;
          if (id is! String) continue;
          final parsed = LocalTrackHighlight.tryParse(entry.value);
          if (parsed != null) highlights[id] = parsed;
        }
      }

      final snippets = <String, String>{};
      final rawSnippets = root['snippets'];
      if (rawSnippets is Map) {
        for (final entry in rawSnippets.entries) {
          final id = entry.key;
          if (id is! String) continue;
          final parsed = LocalLyricSnippet.tryParse(entry.value);
          if (parsed == null) continue;
          final highlight = highlights[id];
          if (highlight != null && highlight.hasMetadataMatch) continue;
          snippets[id] = parsed.text;
        }
      }
      return SearchDecorations(highlights: highlights, snippets: snippets);
    } catch (err) {
      Log.w('search', '结果装饰异常，保持无高亮: $err');
      return null;
    }
  }

  /// 搜索侧的索引选取：就绪即用；构建门占用（构建 / 重开 / 删除进行中）则
  /// **不排队**，返回 null 由调用方回退 `LIKE`；否则按需构建。
  Future<SearchIndex?> _resolveIndexForSearch() {
    if (_disposed) return Future<SearchIndex?>.value();
    final current = _index;
    if (current != null && current.available) {
      return Future<SearchIndex?>.value(current);
    }
    if (_gateBusy) return Future<SearchIndex?>.value();
    return ensureIndex();
  }

  /// 确保索引就绪（必要时构建）；不可用返回 null。绝不抛异常。
  ///
  /// 需要构建 / 重开时经构建门串行；门内**再次**检查是否已就绪（可能被前一个
  /// 关键段建好），避免重复构建。
  Future<SearchIndex?> ensureIndex() async {
    if (_disposed) return null;
    if (!enabled) {
      _status = LocalSearchStatus.disabled;
      return null;
    }
    final current = _index;
    if (current != null && current.available) return current;
    return _serialize(() async {
      if (_disposed) return null;
      if (!enabled) {
        _status = LocalSearchStatus.disabled;
        return null;
      }
      // 门内复查：前一个关键段可能已建好索引，直接复用（不重复构建）。
      final existing = _index;
      if (existing != null && existing.available) return existing;
      return _buildAndOpenLocked(
        full: _needsFullBuild(),
        failMessage: '索引构建失败',
      );
    });
  }

  /// 强制全量重建索引（设置页「重建索引」）。经构建门串行；失败降级 error。
  Future<void> rebuild() async {
    if (_disposed || !enabled) return;
    await _serialize(() async {
      if (_disposed || !enabled) return;
      await _buildAndOpenLocked(full: true, failMessage: '索引重建失败');
    });
  }

  /// 增量重建索引（`.lrc` 子开关切换后调用）：只处理脏文档，完成后重开只读连接。
  ///
  /// `full:false` 交给 indexer 侧按 `(mtime, size)` 脏集增量——内嵌歌词变更、
  /// `.lrc` 开启 / 关闭（含清理）都能被识别，只重算受影响的曲目，
  /// **无需**用户手动「重建索引」（P3b-1 遗留风险，P4 修复）。
  ///
  /// 与 [rebuild] 一样经构建门串行、**失败降级**：任何失败置为 error 态并返回，
  /// 绝不抛到 UI；`search.db` 缺失 / schema 不符时回退全量，保证可用。
  Future<void> reindex() async {
    if (_disposed) return;
    final override = debugReindexOverride;
    if (override != null) return override();
    if (!enabled) return;
    await _serialize(() async {
      if (_disposed || !enabled) return;
      await _buildAndOpenLocked(
        full: _needsFullBuild(),
        failMessage: '索引增量更新失败',
      );
    });
  }

  /// 请求取消进行中的构建（透传 `era_index_cancel`）。
  bool cancel() {
    final override = debugCancelOverride;
    if (override != null) return override();
    try {
      return _lib?.cancel() ?? false;
    } catch (_) {
      return false;
    }
  }

  /// 应用启动时的**后台预构建**（总开关开启才触发；幂等；失败静默）。
  ///
  /// 由启动引导（`app/bootstrap.dart` 的 [AuthBootstrap]）在偏好就绪后调用：
  /// - 总开关关闭 / 快照缺失：**不触碰**模块 / `search.db`（零成本，直接返回）；
  /// - 首次调用且总开关开启：后台跑 [ensureIndex]（不阻塞首帧）；后续调用因
  ///   [_startupWarmUpTriggered] 短路，且 [ensureIndex] 本身门内复查复用已有
  ///   构建，不会重复构建；
  /// - 任何失败（模块缺失 / 构建失败）由 [ensureIndex] 内部降级，这里再兜底
  ///   `catch`，**绝不抛到启动路径**（索引是派生数据，可随时重建）。
  Future<void> warmUpOnStartup() async {
    if (_startupWarmUpTriggered) return;
    if (!enabled) return; // 关闭态零成本：不加载模块 / 不建库 / 不查询。
    _startupWarmUpTriggered = true;
    try {
      await ensureIndex();
    } catch (err) {
      Log.w('search', '启动预构建索引失败（静默降级）: $err');
    }
  }

  /// 停止接管（总开关关闭时调用）：取消在途构建、关闭只读连接、置为未启用。
  ///
  /// **不删除** `search.db`（派生数据默认保留，避免每次开关都全量重建）；
  /// 需要释放空间走 [deleteIndex]。总开关关闭后 [search] 本就返回 null，
  /// 这里额外：
  /// - 经 [cancel] 请求取消在途 `era_index_run`（透传 `era_index_cancel`；无在途
  ///   构建时为 no-op）——**不绕过构建门**，只是取消信号，门内关键段仍串行；
  /// - 释放连接；进行中的构建会在其门内段结束时发现总开关已关（`!enabled`）
  ///   而**放弃重开**（不复活连接），状态回到 [LocalSearchStatus.disabled]。
  void deactivate() {
    if (_disposed) return;
    cancel();
    _closeIndexLocked();
    _healthSnapshot = null;
    _status = LocalSearchStatus.disabled;
  }

  /// 删除派生索引（`search.db` 及其 WAL 侧车）；总开关仍开启时下次搜索重建。
  ///
  /// 经构建门串行：**不与构建并发**（避免删除正在进行写入的 `search.db`）。
  Future<void> deleteIndex() async {
    await _serialize(() async {
      if (_disposed) return;
      _closeIndexLocked();
      _healthSnapshot = null;
      final path = defaultSearchDbPath();
      // T3.13 起索引本体是 search.era/（CURRENT + 世代文件）；旧的 search.db
      // 及其 WAL 侧车只作残留清理，不能只删 search.db。
      final eraDir = Directory(
        '${File(path).parent.path}${Platform.pathSeparator}search.era',
      );
      try {
        if (eraDir.existsSync()) eraDir.deleteSync(recursive: true);
      } catch (_) {
        // 删除失败不阻断（下次构建覆盖 / 交由用户处理）
      }
      for (final suffix in const ['', '-wal', '-shm']) {
        try {
          final file = File('$path$suffix');
          if (file.existsSync()) file.deleteSync();
        } catch (_) {
          // 删除失败不阻断（下次构建覆盖 / 交由用户处理）
        }
      }
      _status = LocalSearchStatus.disabled;
    });
  }

  /// 关闭只读连接并释放回调。
  ///
  /// 设置关闭标志（后续入口拒绝开新连接）、请求取消在途 `era_index_run`；在途
  /// 关键段发现已关闭后**不再开新连接**。绝不抛异常。
  void dispose() {
    if (_disposed) return;
    _disposed = true;
    cancel();
    _closeIndexLocked();
    _healthSnapshot = null;
    if (!_progressDisposed) {
      _progressDisposed = true;
      progress.dispose();
    }
    _status = LocalSearchStatus.disabled;
  }

  /// 测试接缝：直接设置状态（仅单测使用；供 UI 断言错误文案）。
  @visibleForTesting
  void debugSetStatus(LocalSearchStatus status) => _status = status;

  /// 测试接缝：把单例恢复到初始可用状态（仅单测使用）。
  ///
  /// `dispose()` 不可逆；需要在其后继续用单例的测试在此重置。
  @visibleForTesting
  void debugReset() {
    _index?.close();
    _index = null;
    _lib = null;
    _soPath = null;
    _disposed = false;
    _progressDisposed = false;
    _gateTail = Future<void>.value();
    _gateOccupancy = 0;
    _status = LocalSearchStatus.disabled;
    lastResult = null;
    _buildFailure = null;
    _healthSnapshot = null;
    debugReindexOverride = null;
    debugBuildExecutor = null;
    debugCancelOverride = null;
    debugDataPackHealthOverride = null;
    debugSearchDbPathOverride = null;
    debugLibraryDbPathOverride = null;
    _startupWarmUpTriggered = false;
    progress = ValueNotifier<IndexerProgress?>(null);
  }

  // ---------------------------------------------------------------- 内部

  /// EraStore 缺失 / 未就绪 → 需要全量构建。
  bool _needsFullBuild() => !SearchIndex.isAvailable(defaultSearchDbPath());

  /// 构建 + 开库关键段（**必须**在构建门内调用；自身不取门）。
  ///
  /// 步骤：先确认索引器可用（模块缺失早退）→ 关闭旧连接（构建期间 [search]
  /// 回退 LIKE，不读陈旧数据）→ 跑构建 → 成功后开**唯一**新连接。
  /// 全程 `_disposed` / `enabled` 复查，关闭或总开关断开时不复活连接。
  Future<SearchIndex?> _buildAndOpenLocked({
    required bool full,
    required String failMessage,
  }) async {
    if (_disposed || !enabled) return null;
    final executor = debugBuildExecutor;
    if (executor == null) {
      final lib = _ensureLib();
      if (lib == null) {
        _status = LocalSearchStatus.error('未找到索引器模块');
        return null;
      }
    }
    // 替换前先关旧连接，保证任一时刻至多一个有效 `_index`。
    _closeIndexLocked();
    _status = LocalSearchStatus.building;

    if (executor != null) {
      try {
        final index = await executor(full: full);
        if (_disposed || !enabled) {
          index?.close();
          _status = LocalSearchStatus.disabled;
          return null;
        }
        _index = index;
        _status = index != null
            ? LocalSearchStatus.ready
            : LocalSearchStatus.error(failMessage);
        return index;
      } catch (err) {
        if (!_disposed) _status = LocalSearchStatus.error('$err');
        return null;
      }
    }

    final built = await _runBuild(full: full);
    if (_disposed) return null;
    if (!enabled) {
      _status = LocalSearchStatus.disabled;
      return null;
    }
    if (!built) {
      _status = LocalSearchStatus.error(_buildFailure ?? failMessage);
      return null;
    }
    return _openIndexLocked();
  }

  IndexerLibrary? _ensureLib() {
    if (_disposed) return null;
    final existing = _lib;
    if (existing != null) return existing;
    try {
      _soPath ??= IndexerLibrary.resolveSoPath();
      final lib = IndexerLibrary.load(soPath: _soPath);
      _lib = lib;
      return lib;
    } catch (err) {
      Log.w('search', '加载 indexer 模块失败: $err');
      return null;
    }
  }

  void _closeIndexLocked() {
    _index?.close();
    _index = null;
    // 查询句柄由 [SearchIndex.close]（→ `era_search_close`）释放：重建 / 删除 /
    // 关闭前必须显式关闭，否则 Windows 上残留句柄会阻塞 search.db 的替换。
  }

  SearchIndex? _openIndexLocked() {
    final lib = _lib;
    if (lib == null) {
      _status = LocalSearchStatus.error('未找到索引器模块');
      return null;
    }
    _closeIndexLocked();
    final libraryDb =
        debugLibraryDbPathOverride ?? LibraryScanner.defaultDbPath();
    final index = SearchIndex.open(
      defaultSearchDbPath(),
      lib: lib,
      libraryDbPath: libraryDb,
    );
    if (index == null) {
      _status = LocalSearchStatus.error('EraStore 索引不可用（缺失或元数据不符）');
      return null;
    }
    // T4.3 §5.5.3：数据包与索引 / 原生库必须同源（只比 id，不在 Dart 重算哈希）。
    // 不一致时不使用该索引（关闭句柄 → 搜索回退 LIKE），并在设置页给出可操作提示。
    final dataPackProblem = _checkDataPackLocked();
    if (dataPackProblem != null) {
      index.close();
      _status = LocalSearchStatus.error(dataPackProblem);
      Log.w('search', '数据包校验失败，索引停用: $dataPackProblem');
      return null;
    }
    _index = index;
    _status = LocalSearchStatus.ready;
    return index;
  }

  /// 数据包一致性检查（T4.3）：Dart 只比 id，不重算哈希。
  ///
  /// 返回 `null` 表示一致 / 无法校验（旧原生库无 `dataPack` 字段：记一条 warn 后放行，
  /// 不阻断既有行为）；否则返回设置页要展示的可操作文案。
  String? _checkDataPackLocked() {
    final override = debugDataPackHealthOverride;
    if (override != null) {
      final payload = override();
      // 无论是否一致都保留载荷：设置页诊断块需要它展示版本 / 数据包状态。
      _healthSnapshot = payload;
      return payload == null ? null : checkDataPackHealth(payload);
    }
    final lib = _lib;
    if (lib == null) return null;
    try {
      final health = lib.health(defaultSearchDbPath());
      // 打开 / 校验即缓存健康快照，UI 只读缓存（不在设置页做 IO）。
      _healthSnapshot = health;
      if (health == null) {
        Log.w('search', 'era_search_health 无可用载荷：跳过数据包校验');
        return null;
      }
      return checkDataPackHealth(health);
    } catch (err) {
      _healthSnapshot = null;
      Log.w('search', '数据包校验异常（已忽略）: $err');
      return null;
    }
  }

  /// 在后台 isolate 跑 `era_index_run`；进度经 [progress] 暴露（不阻塞 UI）。
  ///
  /// isolate 入口是 [IndexerLibrary.runInIsolate]（**静态**）：`Isolate.run` 的闭包
  /// 若留在本实例方法内，会经上下文链连带捕获本服务（持有 `_gateTail`（Future）等
  /// 不可发送对象），发送必然失败——注释与成因见该方法。
  ///
  /// 失败原因写入 [_buildFailure]（供 [status] 展示具体原因，而非通用文案）。
  Future<bool> _runBuild({required bool full}) async {
    final lib = _lib;
    final soPath = _soPath;
    if (lib == null || soPath == null) {
      _buildFailure = '索引器模块未加载';
      return false;
    }

    final libraryDb =
        debugLibraryDbPathOverride ?? LibraryScanner.defaultDbPath();
    final searchDb = defaultSearchDbPath();
    File(searchDb).parent.createSync(recursive: true);
    final optionsJson = jsonEncode(
      buildOptionsMap(
        indexLrc: indexLrc,
        romajiLyrics: romajiLyrics,
        initialsLyrics: initialsLyrics,
      ),
    );

    final callable = NativeCallable<Void Function(Pointer<Void>)>.listener(
      _handleProgress,
    );
    _setProgress(null);
    _buildFailure = null;
    try {
      final cb = callable.nativeFunction.cast<Void>();
      final (code, text) = await IndexerLibrary.runInIsolate(
        soPath: soPath,
        dbPath: libraryDb,
        indexPath: searchDb,
        full: full,
        optionsJson: optionsJson,
        onProgress: cb,
      );
      if (_disposed) return false;
      if (code != 0) {
        _buildFailure = _buildErrorMessage(code, text);
        Log.w('search', '索引构建失败（exit=$code）: $text');
        return false;
      }
      lastResult = IndexerRunResult.fromJson(
        jsonDecode(text) as Map<String, dynamic>,
      );
      return true;
    } catch (err) {
      _buildFailure = '$err';
      Log.w('search', '索引构建异常: $err');
      return false;
    } finally {
      callable.close();
      _setProgress(null);
    }
  }

  /// 构建失败文本：ABI v2 返回错误 JSON 时取 `error.message`，否则用原始文本。
  static String _buildErrorMessage(int code, String text) {
    try {
      final decoded = jsonDecode(text);
      if (decoded is Map<String, dynamic>) {
        final error = decoded['error'];
        if (error is Map<String, dynamic>) {
          final message = error['message'];
          if (message is String && message.isNotEmpty) return message;
        }
      }
    } catch (_) {
      // 非 JSON：退回原始文本。
    }
    return text.isEmpty ? 'era_index_run 退出码 $code' : text;
  }

  /// 仅在未关闭时写进度（`dispose` 后 [progress] 已释放，写入会抛）。
  void _setProgress(IndexerProgress? value) {
    if (_disposed || _progressDisposed) return;
    progress.value = value;
  }

  /// 进度回调入口（主 isolate 事件循环执行）：解析 JSON 并释放 C# 内存。
  void _handleProgress(Pointer<Void> ptr) {
    String? text;
    try {
      text = ptr.cast<Utf8>().toDartString();
    } catch (_) {}
    // 句柄内存始终释放（`dispose` 后 `_lib` 仍常驻，避免泄漏）。
    try {
      _lib?.free(ptr);
    } catch (_) {}
    if (text == null || _disposed) return;
    try {
      _setProgress(
        IndexerProgress.fromJson(jsonDecode(text) as Map<String, dynamic>),
      );
    } catch (_) {
      // 进度帧解析失败忽略（不中断构建）
    }
  }
}
