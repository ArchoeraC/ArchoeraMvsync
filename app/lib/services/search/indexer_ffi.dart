// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';

import 'package:ffi/ffi.dart';

import '../log/log.dart';
import '../native_integrity.dart';
import '../native_lib_paths.dart';
import '../scanner/sqlite_preload.dart';
import 'search_models.dart';

/// indexer-ffi NativeAOT 库的定位、加载与 FFI 绑定（镜像 `ScannerLibrary`）。
///
/// 纯 FFI 层：不持有任何 isolate 本地状态，可在任意 isolate 内 [load] 后调用
/// [runBlocking] / [cancel] / [analyze]（同一进程共享句柄）。
///
/// **ABI v2**：只认 `era_search_*` / `era_index_*` / `era_analyze` 符号
/// （旧 `indexer_*` 已删除，同包升级不兼容）。[load] 时用
/// `era_search_api_version()` 握手，版本不符直接抛出（不「猜着用」）。
///
/// 产物约定：`indexer/build/indexer-ffi.{so,dylib,dll}` + 同目录
/// `libe_sqlite3.{so,dylib}`（与 scanner 同版本同实例）。
///
/// 生命周期：与 scanner 一致，`indexer-ffi` 为 .NET NativeAOT（C# runtime），
/// `era_index_run` 返回后仍可能有后台线程存活且**无模块级 destroy/join**，
/// 立即 `DynamicLibrary.close()` 会 unmap 代码段导致崩溃；故**常驻、不 unload**。
class IndexerLibrary {
  IndexerLibrary._(this._lib);

  final DynamicLibrary _lib;

  /// 本库要求的 C ABI 版本（`era_search_api_version()` 的期望值）。
  static const int apiVersion = 2;

  // ---------------------------------------------------------------- 定位

  /// 解析 indexer-ffi 共享库路径：统一走 [NativeLibPaths]
  /// （环境变量 → ancestors 链 → dev 兜底）。
  static String resolveSoPath() {
    return NativeLibPaths.resolveRequired(
      NativeModule.indexer,
      hint: '请设置 ARCHOERA_INDEXER_FFI 环境变量',
    );
  }

  static String _sqliteLibName() {
    if (Platform.isWindows) return 'e_sqlite3.dll';
    if (Platform.isMacOS) return 'libe_sqlite3.dylib';
    return 'libe_sqlite3.so';
  }

  // ---------------------------------------------------------------- 加载

  /// 加载共享库。[soPath] 为空时走 [resolveSoPath]。
  ///
  /// 先预加载同目录的 `libe_sqlite3`（见 [preloadBundledSqlite]），否则
  /// SQLitePCLRaw 运行期会因找不到符号失败（Linux 需 RTLD_DEEPBIND
  /// 防系统 libsqlite3 全局符号遮蔽）。
  ///
  /// 加载后立即握手 `era_search_api_version()`：缺少符号（旧库）或版本 !=
  /// [apiVersion] 均抛 [StateError]——不做「猜着用」的兼容兜底。
  static IndexerLibrary load({String? soPath}) {
    final path = soPath ?? resolveSoPath();
    final dir = File(path).parent;
    final sqliteSo = File('${dir.path}/${_sqliteLibName()}');
    if (sqliteSo.existsSync()) {
      try {
        preloadBundledSqlite(path: sqliteSo.path);
      } catch (_) {
        // 系统库/已加载时忽略
      }
    }
    NativeIntegrity.verifyOrThrow(path);
    final lib = DynamicLibrary.open(path);
    int actual;
    try {
      actual = lib
          .lookupFunction<_ApiVersionNative, _ApiVersionDart>(
            'era_search_api_version',
          )();
    } on ArgumentError catch (err) {
      throw StateError('indexer-ffi 缺少 era_search_api_version 符号: $err');
    }
    if (actual != apiVersion) {
      throw StateError(
        'indexer-ffi ABI 版本不符: 期望 $apiVersion，实际 $actual',
      );
    }
    return IndexerLibrary._(lib)..installLogSink();
  }

  /// 注入统一日志 sink（缺失符号时静默跳过，兼容降级构建）。
  void installLogSink() {
    final sink = Log.nativeWritePointer;
    if (sink == null) return;
    try {
      _lib.lookupFunction<_SetLogSinkNative, _SetLogSinkDart>(
        'era_set_log_sink',
      )(sink, Log.effectiveLevel);
    } catch (_) {
      // 无此符号：保持 Console 兜底。
    }
  }

  // ---------------------------------------------------------------- FFI

  late final _RunDart _run = _lib.lookupFunction<_RunNative, _RunDart>(
    'era_index_run',
  );
  late final _CancelDart _cancel = _lib.lookupFunction<_CancelNative, _CancelDart>(
    'era_index_cancel',
  );
  late final _FreeDart _free = _lib.lookupFunction<_FreeNative, _FreeDart>(
    'era_search_free',
  );
  late final _AnalyzeDart _analyze = _lib.lookupFunction<_AnalyzeNative, _AnalyzeDart>(
    'era_analyze',
  );
  late final _OpenSearchDart _openSearchFn = _lib
      .lookupFunction<_OpenSearchNative, _OpenSearchDart>('era_search_open');
  late final _SearchDart _searchFn = _lib
      .lookupFunction<_SearchNative, _SearchDart>('era_search_run');
  late final _CloseSearchDart _closeSearchFn = _lib
      .lookupFunction<_CloseSearchNative, _CloseSearchDart>('era_search_close');
  late final _FallbackDart _fallbackFn = _lib
      .lookupFunction<_FallbackNative, _FallbackDart>('era_search_fallback');
  late final _HealthDart _healthFn = _lib
      .lookupFunction<_HealthNative, _HealthDart>('era_search_health');

  /// `era_search_decorate`（T2.11）：旧库可能没有该符号，缺失时返回 null。
  late final _DecorateDart? _decorateFn = _lookupDecorate();

  _DecorateDart? _lookupDecorate() {
    try {
      return _lib.lookupFunction<_DecorateNative, _DecorateDart>(
        'era_search_decorate',
      );
    } catch (_) {
      return null;
    }
  }

  /// 同步阻塞执行构建 / 增量（须在子 isolate 调用，避免阻塞 UI 线程）。
  ///
  /// [onProgress] 为 C# 侧回调函数指针（`NativeCallable.nativeFunction.cast()`，
  /// 可为 `nullptr.cast<Void>()` 关闭进度）。返回 `(exitCode, resultText)`：
  /// exitCode==0 时 resultText 为构建结果 JSON，否则为错误文本。进度回调
  /// JSON 与结果内存均须由调用方调 [free] 释放。
  (int, String) runBlocking({
    required String dbPath,
    required String indexPath,
    required bool full,
    String optionsJson = '',
    required Pointer<Void> onProgress,
  }) {
    final db = dbPath.toNativeUtf8();
    final index = indexPath.toNativeUtf8();
    final opts = optionsJson.toNativeUtf8();
    final out = calloc<Pointer<Utf8>>();
    final outLen = calloc<Int32>();
    try {
      final code = _run(
        db,
        index,
        full ? 1 : 0,
        opts,
        onProgress,
        out,
        outLen,
      );
      final resultPtr = out.value;
      final text = resultPtr == nullptr
          ? ''
          : resultPtr.cast<Utf8>().toDartString(length: outLen.value);
      if (resultPtr != nullptr) _free(resultPtr.cast());
      return (code, text);
    } finally {
      calloc.free(db);
      calloc.free(index);
      calloc.free(opts);
      calloc.free(out);
      calloc.free(outLen);
    }
  }

  /// 在**子 isolate** 内加载本库并执行 [runBlocking]（阻塞调用，须离开 UI 线程）。
  ///
  /// 参数与返回值和 [runBlocking] 一致（`(exitCode, resultText)`），全部为可跨
  /// isolate 发送的值（字符串 / bool / 原生函数指针）；子 isolate 内**重新
  /// load** 同一共享库（句柄进程共享，指针可跨 isolate 传递）。加载 / 执行异常
  /// 经 [Isolate.run] 回传，调用方按普通异常处理。
  ///
  /// **必须是静态方法**（关键约束，勿改成实例方法，也勿把 `Isolate.run` 的闭包
  /// 内联回调用方）：`Isolate.run` 会把闭包连同其**上下文**深拷贝后发送，闭包所
  /// 在函数里只要出现任何需要 `this` 的表达式——例如
  /// `NativeCallable.listener(某实例方法)` 这种 tear-off，或直接引用实例字段——
  /// `this` 就会被放进该上下文一并发送；调用方（服务编排器）通常持有 `Future` /
  /// `DynamicLibrary` / `NativeCallable` 等**不可发送**对象，发送随即失败：
  /// `Illegal argument in isolate message: object is unsendable`。症状是构建永远
  /// 起不来，日志里反复刷这条异常（见 `test/search_isolate_send_test.dart`）。
  static Future<(int, String)> runInIsolate({
    required String soPath,
    required String dbPath,
    required String indexPath,
    required bool full,
    String optionsJson = '',
    required Pointer<Void> onProgress,
  }) {
    return Isolate.run(() {
      final lib = IndexerLibrary.load(soPath: soPath);
      return lib.runBlocking(
        dbPath: dbPath,
        indexPath: indexPath,
        full: full,
        optionsJson: optionsJson,
        onProgress: onProgress,
      );
    });
  }

  /// 在**子 isolate** 内读 `era_search_health`（设置页诊断快照刷新用）。
  ///
  /// 与 [runInIsolate] 同因：`DynamicLibrary` 实例不可跨 isolate 发送，子 isolate
  /// 重新 load 同一共享库（句柄进程共享）。失败一律返回 null，绝不抛异常；
  /// 调用方应只在异步动作里使用，避免 UI 线程做 IO。
  static Future<Map<String, dynamic>?> healthInIsolate({
    required String soPath,
    required String indexPath,
  }) {
    return Isolate.run(() {
      try {
        final lib = IndexerLibrary.load(soPath: soPath);
        return lib.health(indexPath);
      } catch (_) {
        return null;
      }
    });
  }

  /// 请求取消进行中的构建（任意线程/isolate 可调）。返回 false 表示无进行中构建。
  bool cancel() => _cancel() == 0;

  /// 分析文本，返回与索引侧同构的结构化词元；失败返回 null。
  ///
  /// [field] 对齐 C# `Field` 枚举（0=Title .. 6=Filename）；当前实现不依赖它，
  /// 仅作 ABI 契约校验。
  AnalyzerTokens? analyze(String text, {int field = 0}) {
    final textPtr = text.toNativeUtf8();
    final out = calloc<Pointer<Utf8>>();
    final outLen = calloc<Int32>();
    try {
      final code = _analyze(textPtr, field, out, outLen);
      final resultPtr = out.value;
      if (code != 0 || resultPtr == nullptr) {
        if (resultPtr != nullptr) _free(resultPtr.cast());
        return null;
      }
      final json = resultPtr.cast<Utf8>().toDartString(length: outLen.value);
      _free(resultPtr.cast());
      final map = jsonDecode(json) as Map<String, dynamic>;
      return AnalyzerTokens(
        lexical: _splitTokens(map['tokens'] as String?),
        pinyin: _splitTokens(map['pinyin'] as String?),
        initials: _nullIfEmpty(map['initials'] as String?),
        crossLingual: _splitTokens(map['defs'] as String?),
      );
    } catch (_) {
      return null;
    } finally {
      calloc.free(textPtr);
      calloc.free(out);
      calloc.free(outLen);
    }
  }

  /// 打开只读查询句柄（`era_search_open`）；失败返回 `nullptr`。
  ///
  /// [optionsJson] 为句柄选项（scheme / 缓存 / mmap / 日志级别），空串表示
  /// 全部用 C# 侧缺省。句柄须由 [closeSearch] 释放（重建 / 删除索引前必须
  /// 关闭，Windows 上残留只读句柄会阻塞索引文件的替换）。
  Pointer<Void> openSearch(String indexPath, {String optionsJson = ''}) {
    final index = indexPath.toNativeUtf8();
    final opts = optionsJson.toNativeUtf8();
    final out = calloc<Pointer<Void>>();
    try {
      final code = _openSearchFn(index, opts, out);
      final handle = out.value;
      if (code != 0) {
        // 契约上失败时 out 为 nullptr；防御性地释放非空句柄避免泄漏。
        if (handle != nullptr) _closeSearchFn(handle);
        return nullptr;
      }
      return handle;
    } finally {
      calloc.free(index);
      calloc.free(opts);
      calloc.free(out);
    }
  }

  /// 在已打开句柄上执行一次查询（`era_search_run`）。
  ///
  /// 返回 `(exitCode, resultText)`：成功时 `resultText` 为响应 JSON（`api:2`），
  /// 失败时为错误 JSON（`error.message`）或错误文本。结果内存由本方法释放。
  (int, String) search(Pointer<Void> handle, String requestJson) {
    final request = requestJson.toNativeUtf8();
    final out = calloc<Pointer<Utf8>>();
    final outLen = calloc<Int32>();
    try {
      final code = _searchFn(handle, request, out, outLen);
      final resultPtr = out.value;
      final text = resultPtr == nullptr
          ? ''
          : resultPtr.cast<Utf8>().toDartString(length: outLen.value);
      if (resultPtr != nullptr) _free(resultPtr.cast());
      return (code, text);
    } finally {
      calloc.free(request);
      calloc.free(out);
      calloc.free(outLen);
    }
  }

  /// 关闭查询句柄（`era_search_close`）。空句柄为 no-op。
  void closeSearch(Pointer<Void> handle) {
    if (handle == nullptr) return;
    _closeSearchFn(handle);
  }

  /// 只读健康检查（`era_search_health`，T4.3）：返回解析后的 JSON，失败返回 null。
  ///
  /// 参数是 `search.db` 路径（索引实际位于同目录的 `search.era/`）。健康 JSON 含
  /// `dataPack.{id,match,present,missing[],corrupt[],degradedCapabilities[]}`，
  /// Dart 用 `dataPack.id` 与编译期常量 `dataPackId` 比对（见 `data_pack_check.dart`）。
  /// 返回码非 0 时仍尝试解析（`ready:false` 也是合法载荷）；解析失败 / 空串返回 null。
  Map<String, dynamic>? health(String indexPath) {
    final index = indexPath.toNativeUtf8();
    final out = calloc<Pointer<Utf8>>();
    final outLen = calloc<Int32>();
    try {
      _healthFn(index, out, outLen);
      final resultPtr = out.value;
      if (resultPtr == nullptr) return null;
      String text;
      try {
        text = resultPtr.cast<Utf8>().toDartString(length: outLen.value);
      } finally {
        _free(resultPtr.cast());
      }
      if (text.isEmpty) return null;
      final decoded = jsonDecode(text);
      return decoded is Map<String, dynamic> ? decoded : null;
    } catch (_) {
      return null;
    } finally {
      calloc.free(index);
      calloc.free(out);
      calloc.free(outLen);
    }
  }

  /// 无索引时的降级检索（`era_search_fallback`）：只读 [libraryPath]（`library.db`），
  /// 参数化 LIKE + 字段权重。返回 `(exitCode, resultText)`，内存释放约定同 [search]。
  (int, String) fallbackSearch(String libraryPath, String requestJson) {
    final path = libraryPath.toNativeUtf8();
    final request = requestJson.toNativeUtf8();
    final out = calloc<Pointer<Utf8>>();
    final outLen = calloc<Int32>();
    try {
      final code = _fallbackFn(path, request, out, outLen);
      final resultPtr = out.value;
      final text = resultPtr == nullptr
          ? ''
          : resultPtr.cast<Utf8>().toDartString(length: outLen.value);
      if (resultPtr != nullptr) _free(resultPtr.cast());
      return (code, text);
    } finally {
      calloc.free(path);
      calloc.free(request);
      calloc.free(out);
      calloc.free(outLen);
    }
  }

  /// 按 trackId 批量补高亮 / 片段（`era_search_decorate`）：只读 [libraryPath]，
  /// 不重跑排名。返回 `(exitCode, resultText)`；符号缺失返回 `(-1, '')`。
  (int, String) decorateSearch(String libraryPath, String requestJson) {
    final fn = _decorateFn;
    if (fn == null) return (-1, '');
    final path = libraryPath.toNativeUtf8();
    final request = requestJson.toNativeUtf8();
    final out = calloc<Pointer<Utf8>>();
    final outLen = calloc<Int32>();
    try {
      final code = fn(path, request, out, outLen);
      final resultPtr = out.value;
      final text = resultPtr == nullptr
          ? ''
          : resultPtr.cast<Utf8>().toDartString(length: outLen.value);
      if (resultPtr != nullptr) _free(resultPtr.cast());
      return (code, text);
    } finally {
      calloc.free(path);
      calloc.free(request);
      calloc.free(out);
      calloc.free(outLen);
    }
  }

  /// 释放 indexer-ffi 侧（NativeMemory.Alloc）分配的内存。
  void free(Pointer<Void> ptr) => _free(ptr);

  static List<String> _splitTokens(String? raw) => (raw ?? '')
      .split(' ')
      .where((t) => t.isNotEmpty)
      .toList(growable: false);

  static String? _nullIfEmpty(String? raw) {
    if (raw == null || raw.trim().isEmpty) return null;
    return raw;
  }
}

typedef _ApiVersionNative = Int32 Function();
typedef _ApiVersionDart = int Function();

typedef _RunNative =
    Int32 Function(
      Pointer<Utf8> dbPath,
      Pointer<Utf8> indexPath,
      Int32 full,
      Pointer<Utf8> optionsJson,
      Pointer<Void> onProgress,
      Pointer<Pointer<Utf8>> outResult,
      Pointer<Int32> outLen,
    );
typedef _RunDart =
    int Function(
      Pointer<Utf8>,
      Pointer<Utf8>,
      int,
      Pointer<Utf8>,
      Pointer<Void>,
      Pointer<Pointer<Utf8>>,
      Pointer<Int32>,
    );

typedef _CancelNative = Int32 Function();
typedef _CancelDart = int Function();

typedef _FreeNative = Void Function(Pointer<Void>);
typedef _FreeDart = void Function(Pointer<Void>);

typedef _AnalyzeNative =
    Int32 Function(
      Pointer<Utf8> text,
      Int32 field,
      Pointer<Pointer<Utf8>> outTokens,
      Pointer<Int32> outLen,
    );
typedef _AnalyzeDart =
    int Function(
      Pointer<Utf8>,
      int,
      Pointer<Pointer<Utf8>>,
      Pointer<Int32>,
    );

typedef _OpenSearchNative =
    Int32 Function(
      Pointer<Utf8> indexPath,
      Pointer<Utf8> optionsJson,
      Pointer<Pointer<Void>> out,
    );
typedef _OpenSearchDart =
    int Function(
      Pointer<Utf8>,
      Pointer<Utf8>,
      Pointer<Pointer<Void>>,
    );

typedef _SearchNative =
    Int32 Function(
      Pointer<Void> handle,
      Pointer<Utf8> requestJson,
      Pointer<Pointer<Utf8>> outResult,
      Pointer<Int32> outLen,
    );
typedef _SearchDart =
    int Function(
      Pointer<Void>,
      Pointer<Utf8>,
      Pointer<Pointer<Utf8>>,
      Pointer<Int32>,
    );

typedef _CloseSearchNative = Void Function(Pointer<Void>);
typedef _CloseSearchDart = void Function(Pointer<Void>);

typedef _FallbackNative =
    Int32 Function(
      Pointer<Utf8> libraryPath,
      Pointer<Utf8> requestJson,
      Pointer<Pointer<Utf8>> outResult,
      Pointer<Int32> outLen,
    );
typedef _FallbackDart =
    int Function(
      Pointer<Utf8>,
      Pointer<Utf8>,
      Pointer<Pointer<Utf8>>,
      Pointer<Int32>,
    );

typedef _DecorateNative =
    Int32 Function(
      Pointer<Utf8> libraryPath,
      Pointer<Utf8> requestJson,
      Pointer<Pointer<Utf8>> outResult,
      Pointer<Int32> outLen,
    );
typedef _DecorateDart =
    int Function(
      Pointer<Utf8>,
      Pointer<Utf8>,
      Pointer<Pointer<Utf8>>,
      Pointer<Int32>,
    );

typedef _HealthNative =
    Int32 Function(
      Pointer<Utf8> indexPath,
      Pointer<Pointer<Utf8>> outJson,
      Pointer<Int32> outLen,
    );
typedef _HealthDart =
    int Function(
      Pointer<Utf8>,
      Pointer<Pointer<Utf8>>,
      Pointer<Int32>,
    );

typedef _SetLogSinkNative =
    Void Function(Pointer<NativeFunction<LogWriteNative>> fn, Int32 minLevel);
typedef _SetLogSinkDart = void Function(
    Pointer<NativeFunction<LogWriteNative>> fn, int minLevel);
