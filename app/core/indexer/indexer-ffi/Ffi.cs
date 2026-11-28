// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Ffi;

/// <summary>
/// NativeAOT 导出层：供 Flutter 通过 FFI 直接调用本地曲库搜索索引器（C ABI v2）。
///
/// 与 CLI（Program.cs）共用同一套 IndexerEngine / SearchSession / EraStore 源码，
/// 差异仅在进度上报（CLI=stdout JSON，FFI=回调函数指针）与取消
/// （CLI=SIGINT，FFI=era_index_cancel）。
///
/// <para><b>ABI（D.1 / §2.2.1；旧 <c>indexer_*</c> 已整体删除，不做 api:1 兼容）</b>：</para>
/// <code>
/// int32_t era_search_api_version(void);                 // 2，启动握手
/// int32_t era_search_open(const char* index_path, const char* options_json, void** out);
/// int32_t era_search_run(void* h, const char* request_json, char** out_json, int32_t* out_len);
/// int32_t era_search_fallback(const char* library_path, const char* request_json,
///                             char** out_json, int32_t* out_len);   // 无索引时的 LIKE 降级
/// int32_t era_search_decorate(const char* library_path, const char* request_json,
///                             char** out_json, int32_t* out_len);   // 按 trackId 补高亮 / 片段
/// void    era_search_free(char* ptr);                   // run / health / index_run / analyze 的输出都归它
/// void    era_search_close(void* h);                    // 空句柄 / 重复调用安全
/// int32_t era_search_health(const char* index_dir, char** out_json, int32_t* out_len);
/// int32_t era_index_run(const char* db_path, const char* index_path, int full,
///                       const char* options_json, progress_cb, char** out_result, int32_t* out_len);
/// int32_t era_index_cancel(void);
/// int32_t era_analyze(const char* text, int field, char** out_json, int32_t* out_len);
/// void    era_set_log_sink(sink, int min_level);
/// </code>
///
/// <para><b>过渡形态（S1）</b>：构建 / 分析 / 日志仍是**同步入口**（<c>era_index_run</c> 阻塞
/// 到整库写完、<c>era_analyze</c> 同步返回）。最终形态是 D.1 的任务句柄
/// <c>era_index_start / era_index_wait / era_index_cancel / era_index_free</c>，
/// 由 S3 落地；在那之前调用方须在子 isolate / 后台线程里调 <c>era_index_run</c>。</para>
///
/// <para><b>并发（S1 限制）</b>：D.1 要求「不同句柄可并发、同一句柄串行」。S1 的只读连接与
/// 结果缓存都挂在 <see cref="SearchSession"/> 的进程级静态锁下，因此**所有** <c>era_search_run</c>
/// 实际是全局串行的（语义安全，只是不能并行吃满多核）。per-handle 并发随 S3 的句柄化缓存一起落地。</para>
///
/// <para><b>失败也写 JSON</b>：<c>run</c> / <c>health</c> / <c>index_run</c> / <c>analyze</c>
/// 失败时 <c>out_json</c> 指向 D.4 错误 JSON（不是 NULL），返回值即错误码（1/2/3/5/6/7/8）。
/// 只有一个例外：<c>era_search_open</c> 失败时 <c>*out</c> 为 NULL。</para>
///
/// <para><b>托管入口与导出入口成对</b>：<c>[UnmanagedCallersOnly]</c> 方法不能被托管代码
/// 直接调用，因此每个导出符号都是一个只做转发的 <c>*Export</c> 方法，真正实现在同名的
/// 托管方法里（测试直接调托管方法，省掉一次 P/Invoke 往返）。</para>
/// </summary>
public static unsafe class IndexerFfi
{
    /// <summary>进行中的构建 CTS（era_index_cancel 从任意线程取消）。</summary>
    private static CancellationTokenSource? _currentCts;

    /// <summary>
    /// 存活的查询句柄（GCHandle 指针 → 占位）。
    ///
    /// 只用于让 <c>era_search_close</c> 对**同一指针重复调用**变成 no-op：
    /// 直接对已释放的 GCHandle 调 <see cref="GCHandle.FromIntPtr"/> 是未定义行为。
    /// </summary>
    private static readonly ConcurrentDictionary<nint, byte> LiveHandles = new();

    /* ------------------------------------------------------------------ */
    /* ABI 版本                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>当前 C ABI / 载荷版本（Dart 启动时握手，不一致直接拒绝而不是「猜着用」）。</summary>
    public static int ApiVersion() => SearchApiRequest.ApiVersion;

    [UnmanagedCallersOnly(EntryPoint = "era_search_api_version")]
    public static int ApiVersionExport() => ApiVersion();

    /// <summary>
    /// 只读当前 EraStore Manifest（查询 / 健康检查共用）。
    /// T3.13：查询打开不再依赖 <c>search.db</c> 的 v7 元数据表；索引是否存在由
    /// <c>search.era/CURRENT</c> 判定。
    /// </summary>
    internal static EraStore.Manifest? ReadEraManifest(string indexDbPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(indexDbPath)) ?? ".";
        var eraDir = Path.Combine(directory, IndexerEngine.EraStoreDirName);
        var name = EraStore.CurrentPointer.Read(eraDir);
        return name is null ? null : EraStore.StoreReader.TryReadManifest(Path.Combine(eraDir, name));
    }

    /// <summary>CURRENT 指向的 EraStore 文件绝对路径（不存在 / 无指针返回 <c>null</c>）。</summary>
    private static string? EraFilePath(string indexDbPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(indexDbPath)) ?? ".";
        var eraDir = Path.Combine(directory, IndexerEngine.EraStoreDirName);
        var name = EraStore.CurrentPointer.Read(eraDir);
        return name is null ? null : Path.Combine(eraDir, name);
    }

    /* ------------------------------------------------------------------ */
    /* 查询句柄                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 打开只读查询句柄。
    ///
    /// <para>返回码：0=成功；1=参数 / options_json 非法；2=EraStore 索引不存在或没有
    /// 有效提交（未就绪）；3=Manifest 元数据版本过旧（须重建）；8=内部错误。
    /// 失败时 <c>*outHandle</c> 恒为 NULL。</para>
    ///
    /// <para><c>options_json</c> 可空 / 对象：未知键一律忽略（内部接口不做严格 schema 拒绝）。
    /// <c>libraryDb</c>（字符串）为源曲库路径，用于查询期过滤已删除曲目（T2.9）；
    /// 库不可读时失败开放（不过滤）。scheme / cache 容量 / mmap 的真正语义随 S3 落地。</para>
    /// </summary>
    public static int Open(byte* indexPath, byte* optionsJson, void** outHandle)
    {
        if (outHandle != null) *outHandle = null;
        if (outHandle == null) return SearchApiCodes.InvalidRequest;

        try
        {
            var path = PtrToString(indexPath);
            if (string.IsNullOrWhiteSpace(path)) return SearchApiCodes.InvalidRequest;
            if (!TryReadOpenOptions(optionsJson, out var libraryDbPath)) return SearchApiCodes.InvalidRequest;

            // T3.13：查询打开只看 EraStore Manifest（v7 的 search.db 元数据表不再是门禁）。
            var manifest = ReadEraManifest(path!);
            if (manifest is null) return SearchApiCodes.NotReady;
            if (manifest.MetaVersion < EraStore.Manifest.CurrentMetaVersion)
                return SearchApiCodes.VersionMismatch;

            // T4.3 数据包门禁（§5.5.3 运行期）：
            // - 包缺失 → 未就绪（Dart 回退 LIKE；不读旧索引）；
            // - 基础归一化表缺失 / 索引记录的 id 与当前包不同 → 版本不符（须重建）。
            var dataPack = DataPack.Current;
            if (!dataPack.Present)
            {
                Log.LogWarn("indexer", dataPack.FoundationError);
                return SearchApiCodes.NotReady;
            }
            if (!dataPack.FoundationOk)
            {
                Log.LogWarn("indexer", dataPack.FoundationError);
                return SearchApiCodes.VersionMismatch;
            }
            if (!string.Equals(manifest.Anchors.DataPackId, dataPack.Id, StringComparison.Ordinal))
            {
                Log.LogWarn("indexer",
                    $"索引数据包 id 不匹配（索引 {manifest.Anchors.DataPackId} / 当前 {dataPack.Id}）："
                    + "索引作废，下次构建将全量重建");
                return SearchApiCodes.VersionMismatch;
            }

            var apiHandle = new SearchApiHandle(
                Path.GetFullPath(path!),
                SearchSession.MetaFromManifest(manifest),
                string.IsNullOrWhiteSpace(libraryDbPath) ? null : Path.GetFullPath(libraryDbPath!));
            var gcHandle = GCHandle.Alloc(apiHandle, GCHandleType.Normal);
            var pointer = (void*)GCHandle.ToIntPtr(gcHandle);
            LiveHandles[(nint)pointer] = 0;
            *outHandle = pointer;
            return SearchApiCodes.Ok;
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"era_search_open 失败: {ex.Message}");
            return SearchApiCodes.Internal;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_open")]
    public static int OpenExport(byte* indexPath, byte* optionsJson, void** outHandle)
        => Open(indexPath, optionsJson, outHandle);

    /// <summary>
    /// 在句柄上执行一次查询（请求 v2 → 响应 v2）。
    ///
    /// <para>**成功与失败都写 <c>out_json</c>**（失败为 D.4 错误 JSON），返回值即错误码：
    /// 0=成功；1=句柄 / request_json 无效（含出现旧字段）；2=索引文件已消失（未就绪）；
    /// 3=schema / 版本异常；5=IO；6=内存不足；7=取消；8=其它内部错误。</para>
    ///
    /// <para><c>filter.trackIds</c> 在**结果上做后过滤**（先按库范围召回再裁剪）：
    /// S1 不按 <c>2×limit</c> 放宽召回，过滤后不足 <c>limit</c> 也不置 <c>partial</c>；
    /// 放宽召回留到 T3（D.2「filter.trackIds 的语义」）。</para>
    /// </summary>
    public static int Run(void* handle, byte* requestJson, byte** outJson, int* outLen)
    {
        if (outJson != null) *outJson = null;
        if (outLen != null) *outLen = 0;
        if (outJson == null || outLen == null) return SearchApiCodes.InvalidRequest;

        var apiHandle = ResolveHandle(handle);
        if (apiHandle is null)
        {
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "查询句柄无效或已关闭（era_search_run 收到空 / 过期句柄）", "invalid_handle"));
        }

        var requestStr = PtrToString(requestJson);
        if (string.IsNullOrWhiteSpace(requestStr))
        {
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "request_json 为空", "empty_request"));
        }

        var (request, requestError) = SearchApiRequest.Parse(requestStr!);
        if (requestError is not null || request is null)
        {
            return FailJson(outJson, outLen,
                requestError ?? SearchApiError.Invalid("请求解析失败", "invalid_request"));
        }

        // T3.13：查询只依赖 EraStore。open 之后 CURRENT 被删除 / 替换时按「未就绪」上报，
        // 不再检查 v7 的 search.db（生产路径本就不写它）。
        var eraDir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(apiHandle.IndexPath)) ?? ".",
            IndexerEngine.EraStoreDirName);
        if (EraStore.CurrentPointer.Read(eraDir) is null)
        {
            return FailJson(outJson, outLen, SearchApiError.NotReady("EraStore 索引不存在"));
        }

        try
        {
            var result = SearchSession.Run(
                apiHandle.IndexPath, request.Request, apiHandle.LibraryDbPath);
            var items = ApplyTrackFilter(result.Items, request.FilterTrackIds);
            var outJsonPtr = (nint)outJson;
            var outLenPtr = (nint)outLen;
            SearchApiJson.Success(
                items,
                result.Passes,
                apiHandle.Generation,
                result.SchemeId,
                request.SchemeRequested,
                request.Clamped,
                request.QueryTruncated,
                result.Explanations.Count == 0 ? null : result.Explanations,
                result.Trace,
                result.Snippets.Count == 0 ? null : result.Snippets,
                result.Highlights.Count == 0 ? null : result.Highlights,
                result.Total,
                result.TotalExact,
                result.Restart,
                result.Next,
                sink: bytes =>
                {
                    SetUtf8((byte**)outJsonPtr, (int*)outLenPtr, bytes);
                    // 宽结果集（≥1 MB）会留下候选 / 结果对象的 LOH；一次压缩回收把
                    // 进程匿名 RSS 拉回基线（只在真正的大响应上付 ≈8 ms）。
                    if (bytes.Length >= 128 * 1024) MemoryTrim.Request();
                });
            // 本查询若首次加载了拼音 / 释义词库（≈40 MB 活对象 + 暂存堆），响应已拷到
            // 非托管缓冲，此时做一次压缩回收，避免进程长期保留 ≈100 MB 匿名 RSS（T3.14）。
            MemoryTrim.AfterQuery();
            return SearchApiCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            return FailJson(outJson, outLen, SearchApiError.Canceled());
        }
        catch (OutOfMemoryException)
        {
            return FailJson(outJson, outLen, SearchApiError.OutOfMemory());
        }
        catch (InvalidOperationException ex)
        {
            // SearchSession 的构造函数对 schema 不符抛 InvalidOperationException。
            return FailJson(outJson, outLen, SearchApiError.VersionMismatch(ex.Message));
        }
        catch (SqliteException ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailJson(outJson, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Internal(ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_run")]
    public static int RunExport(void* handle, byte* requestJson, byte** outJson, int* outLen)
        => Run(handle, requestJson, outJson, outLen);

    /// <summary>
    /// 索引关闭 / 未就绪时的降级检索（T2.12）：只读 <c>library.db</c>，参数化 LIKE。
    ///
    /// <para>响应与 <see cref="Run"/> 同构（<c>api:2</c>），<c>scheme</c> 固定
    /// <c>fallback@1</c>，不产出 <c>explain</c> / <c>trace</c>；<c>highlight</c> /
    /// <c>snippet</c> 与正式路径共用同一实现。</para>
    ///
    /// <para>返回码：0=成功；1=参数 / request_json 非法；2=源曲库不存在；
    /// 5=IO / SQLite；6=内存不足；8=内部错误。</para>
    /// </summary>
    public static int Fallback(byte* libraryPath, byte* requestJson, byte** outJson, int* outLen)
    {
        if (outJson != null) *outJson = null;
        if (outLen != null) *outLen = 0;
        if (outJson == null || outLen == null) return SearchApiCodes.InvalidRequest;

        var path = PtrToString(libraryPath);
        if (string.IsNullOrWhiteSpace(path))
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "library_path 为空", "invalid_argument", "library_path"));

        var requestStr = PtrToString(requestJson);
        if (string.IsNullOrWhiteSpace(requestStr))
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "request_json 为空", "empty_request"));

        var (request, requestError) = SearchApiRequest.Parse(requestStr!);
        if (requestError is not null || request is null)
        {
            return FailJson(outJson, outLen,
                requestError ?? SearchApiError.Invalid("请求解析失败", "invalid_request"));
        }

        if (!File.Exists(path))
            return FailJson(outJson, outLen, SearchApiError.NotReady("源曲库不存在"));

        try
        {
            var result = SearchFallback.Execute(path!, request.Request);
            var outJsonPtr = (nint)outJson;
            var outLenPtr = (nint)outLen;
            SearchApiJson.Fallback(
                result.Items,
                request.SchemeRequested,
                request.Clamped,
                request.QueryTruncated,
                result.Total,
                result.Snippets.Count == 0 ? null : result.Snippets,
                result.Highlights.Count == 0 ? null : result.Highlights,
                sink: bytes =>
                {
                    SetUtf8((byte**)outJsonPtr, (int*)outLenPtr, bytes);
                    // 宽结果集（≥1 MB）会留下候选 / 结果对象的 LOH；一次压缩回收把
                    // 进程匿名 RSS 拉回基线（只在真正的大响应上付 ≈8 ms）。
                    if (bytes.Length >= 128 * 1024) MemoryTrim.Request();
                });
            return SearchApiCodes.Ok;
        }
        catch (OutOfMemoryException)
        {
            return FailJson(outJson, outLen, SearchApiError.OutOfMemory());
        }
        catch (SqliteException ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailJson(outJson, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Internal(ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_fallback")]
    public static int FallbackExport(byte* libraryPath, byte* requestJson, byte** outJson, int* outLen)
        => Fallback(libraryPath, requestJson, outJson, outLen);

    /// <summary>
    /// 按 trackId 批量补高亮 / 片段（T2.11）：只读 <c>library.db</c>，不重跑排名。
    ///
    /// <para>请求：<c>{api:2, query, trackIds:[...], snippet:bool}</c>，<c>trackIds</c>
    /// 最多 <see cref="SearchDecorations.MaxIds"/> 项且每项为 32 位十六进制。响应：
    /// <c>{api:2, highlights:{trackId:[...]}, snippets:{trackId:{...}}}</c>。</para>
    ///
    /// <para>返回码与 <see cref="Fallback"/> 一致；库不存在 / 不可读时失败开放为空集合。</para>
    /// </summary>
    public static int Decorate(byte* libraryPath, byte* requestJson, byte** outJson, int* outLen)
    {
        if (outJson != null) *outJson = null;
        if (outLen != null) *outLen = 0;
        if (outJson == null || outLen == null) return SearchApiCodes.InvalidRequest;

        var path = PtrToString(libraryPath);
        if (string.IsNullOrWhiteSpace(path))
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "library_path 为空", "invalid_argument", "library_path"));

        var requestStr = PtrToString(requestJson);
        if (string.IsNullOrWhiteSpace(requestStr))
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "request_json 为空", "empty_request"));

        string query;
        bool snippet;
        List<string> trackIds;
        try
        {
            using var doc = JsonDocument.Parse(requestStr);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return FailJson(outJson, outLen, SearchApiError.Invalid(
                    "request_json 必须是 JSON 对象", "invalid_request"));

            query = root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? ""
                : "";
            snippet = root.TryGetProperty("snippet", out var s)
                && s.ValueKind is JsonValueKind.True;

            if (!root.TryGetProperty("trackIds", out var idsElement)
                || idsElement.ValueKind != JsonValueKind.Array)
            {
                return FailJson(outJson, outLen, SearchApiError.Invalid(
                    "trackIds 必须是字符串数组", "invalid_request", "trackIds"));
            }
            if (idsElement.GetArrayLength() > SearchDecorations.MaxIds)
            {
                return FailJson(outJson, outLen, SearchApiError.Invalid(
                    $"trackIds 超过 {SearchDecorations.MaxIds} 项上限", "too_many_ids", "trackIds"));
            }

            trackIds = new List<string>(idsElement.GetArrayLength());
            foreach (var element in idsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    return FailJson(outJson, outLen, SearchApiError.Invalid(
                        "trackIds 的元素必须是字符串", "invalid_request", "trackIds"));
                var id = element.GetString() ?? "";
                if (!SearchApiRequest.IsHex32(id))
                    return FailJson(outJson, outLen, SearchApiError.Invalid(
                        "trackIds 的元素必须是 32 位十六进制", "invalid_request", "trackIds"));
                trackIds.Add(id);
            }
        }
        catch (JsonException ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Invalid(
                "request_json 不是合法 JSON: " + ex.Message, "invalid_json"));
        }

        if (!File.Exists(path))
            return FailJson(outJson, outLen, SearchApiError.NotReady("源曲库不存在"));

        try
        {
            var result = SearchDecorations.Build(path!, query, trackIds, snippet);
            SetUtf8(outJson, outLen, SearchApiJson.Decorate(result.Highlights, result.Snippets));
            return SearchApiCodes.Ok;
        }
        catch (OutOfMemoryException)
        {
            return FailJson(outJson, outLen, SearchApiError.OutOfMemory());
        }
        catch (SqliteException ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailJson(outJson, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Internal(ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_decorate")]
    public static int DecorateExport(byte* libraryPath, byte* requestJson, byte** outJson, int* outLen)
        => Decorate(libraryPath, requestJson, outJson, outLen);

    /// <summary>
    /// 关闭查询句柄：释放 GCHandle，并丢弃 <see cref="SearchSession"/> 的**静态**只读会话缓存。
    ///
    /// <para>空句柄 / 重复调用都是 no-op（参见 <see cref="LiveHandles"/>）。</para>
    ///
    /// <para><b>S1 限制</b>：<see cref="SearchSession"/> 的结果缓存是**进程级静态**的，
    /// 多句柄并存时共享同一份缓存，因此关闭任一句柄都会把别人的缓存一起丢掉
    /// （只是下次查询重新建连接，不影响正确性）。S3 改成 per-handle 的 mmap / 缓存后
    /// 这里的 <see cref="SearchSession.CloseCached"/> 会挪进句柄自身。</para>
    /// </summary>
    public static void Close(void* handle)
    {
        if (handle == null) return;
        if (!LiveHandles.TryRemove((nint)handle, out _)) return;

        try
        {
            var gcHandle = GCHandle.FromIntPtr((IntPtr)handle);
            gcHandle.Free();
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"era_search_close 释放句柄失败: {ex.Message}");
        }
        finally
        {
            SearchSession.CloseCached();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_close")]
    public static void CloseExport(void* handle) => Close(handle);

    /* ------------------------------------------------------------------ */
    /* 健康 / 诊断                                                         */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 只读健康检查（设置页与 CLI 共用）。参数是 `search.db` 的路径（D.1 写作
    /// <c>index_dir</c>，索引实际位于同目录的 <c>search.era/</c>）。
    ///
    /// <para>返回码：0=就绪，JSON 含 <c>gen</c> / <c>schemaVersion</c> / <c>analyzerVersion</c>；
    /// 2=EraStore 缺失（JSON 为 <c>{"api":2,"ready":false}</c>）；3=Manifest 元数据版本过旧
    /// （同样是 ready:false）；1=参数非法。**不返回绝对路径**（D.4「detail 不放路径」）。</para>
    /// </summary>
    public static int Health(byte* indexPath, byte** outJson, int* outLen)
    {
        if (outJson != null) *outJson = null;
        if (outLen != null) *outLen = 0;
        if (outJson == null || outLen == null) return SearchApiCodes.InvalidRequest;

        try
        {
            var path = PtrToString(indexPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return FailJson(outJson, outLen, SearchApiError.Invalid("index_path 为空", "invalid_argument"));
            }

            // T3.13：健康检查只读 EraStore Manifest，不碰 v7 的 search_meta。
            var manifest = ReadEraManifest(path!);
            var dataPack = DataPack.Current.Health(manifest?.Anchors.DataPackId ?? "");
            if (manifest is null)
            {
                SetUtf8(outJson, outLen, SearchApiJson.HealthNotReady(dataPack));
                return SearchApiCodes.NotReady;
            }
            if (manifest.MetaVersion < EraStore.Manifest.CurrentMetaVersion)
            {
                SetUtf8(outJson, outLen, SearchApiJson.HealthNotReady(dataPack));
                return SearchApiCodes.VersionMismatch;
            }

            SetUtf8(outJson, outLen, SearchApiJson.HealthReady(
                SearchSession.MetaFromManifest(manifest), CollectConfidence(path!), dataPack));
            return SearchApiCodes.Ok;
        }
        catch (Exception ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Internal(ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_health")]
    public static int HealthExport(byte* indexPath, byte** outJson, int* outLen)
        => Health(indexPath, outJson, outLen);

    /// <summary>
    /// 健康页的置信度分布（T4.2）。失败开放：统计失败只少一个诊断字段，不让健康检查报错。
    /// </summary>
    private static EraStore.LayerConfidenceCounts? CollectConfidence(string indexDbPath)
    {
        try
        {
            var file = EraFilePath(indexDbPath);
            return file is null ? null : EraStore.LayerConfidenceStats.Collect(file);
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"健康页置信度统计跳过: {ex.Message}");
            return null;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 索引构建（S3 前保持同步）                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 同步构建 / 增量索引（过渡形态，见类型注释）：阻塞到整库写完，期间通过
    /// <paramref name="onProgress"/> 上报进度 JSON。
    ///
    /// <para>成功时 <c>out_result</c> 为 <see cref="IndexerJson.Result"/> 摘要（旧格式，
    /// Dart 侧既有解析不变）；失败时为 D.4 错误 JSON，返回码 1/5/7/8。</para>
    /// </summary>
    public static int IndexRun(
        byte* dbPath,
        byte* indexPath,
        int full,
        byte* optionsJson,
        delegate* unmanaged[Cdecl]<byte*, void> onProgress,
        byte** outResult,
        int* outLen)
    {
        if (outResult != null) *outResult = null;
        if (outLen != null) *outLen = 0;
        if (outResult == null || outLen == null) return SearchApiCodes.InvalidRequest;

        try
        {
            var dbPathStr = PtrToString(dbPath);
            var indexPathStr = PtrToString(indexPath);
            if (string.IsNullOrEmpty(dbPathStr))
                return FailJson(outResult, outLen, SearchApiError.Invalid("db_path 为空", "invalid_argument"));
            if (string.IsNullOrEmpty(indexPathStr))
                return FailJson(outResult, outLen, SearchApiError.Invalid("index_path 为空", "invalid_argument"));
            if (!File.Exists(dbPathStr))
                return FailJson(outResult, outLen, SearchApiError.Invalid("源曲库不存在", "library_missing"));

            if (!TryParseOptions(optionsJson, out var options, out var error))
                return FailJson(outResult, outLen, SearchApiError.Invalid(error, "invalid_options"));

            // 重建 / 增量写入前必须释放只读查询会话：Windows 下打开的只读句柄会阻塞
            // 旧 EraStore 世代文件的清理。随后立刻做一次紧凑回收，把上一个会话的
            // 倒排缓存 / mmap 视图 / 查询结果从堆里还回去，避免与本次构建的峰值叠加。
            SearchSession.CloseCached();
            MemoryTrim.TrimAfterBuild();

            var engine = new IndexerEngine(onProgress != null
                ? p => FireProgress(onProgress, p)
                : null);

            using var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _currentCts, cts);
            try
            {
                var result = engine.Run(dbPathStr, indexPathStr, full != 0, options, cts.Token);
                SetUtf8(outResult, outLen, IndexerJson.Result(result));
                // 构建期分析 30k 曲后词库与暂存对象已无用；每次构建都归还堆段（≈10–20 ms）。
                MemoryTrim.TrimAfterBuild();
                return SearchApiCodes.Ok;
            }
            finally
            {
                Interlocked.Exchange(ref _currentCts, null);
            }
        }
        catch (OperationCanceledException)
        {
            return FailJson(outResult, outLen, SearchApiError.Canceled());
        }
        catch (OutOfMemoryException)
        {
            return FailJson(outResult, outLen, SearchApiError.OutOfMemory());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return FailJson(outResult, outLen, SearchApiError.Io(ex.Message));
        }
        catch (Exception ex)
        {
            return FailJson(outResult, outLen, SearchApiError.Internal(ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_index_run")]
    public static int IndexRunExport(
        byte* dbPath,
        byte* indexPath,
        int full,
        byte* optionsJson,
        delegate* unmanaged[Cdecl]<byte*, void> onProgress,
        byte** outResult,
        int* outLen)
        => IndexRun(dbPath, indexPath, full, optionsJson, onProgress, outResult, outLen);

    /// <summary>请求取消进行中的构建。0=已请求取消，1=当前无构建。</summary>
    public static int IndexCancel()
    {
        var cts = Volatile.Read(ref _currentCts);
        if (cts == null) return 1;
        cts.Cancel();
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "era_index_cancel")]
    public static int IndexCancelExport() => IndexCancel();

    /* ------------------------------------------------------------------ */
    /* 分析 / 日志                                                         */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 查询侧分析（过渡同步入口）：返回与索引侧同形的
    /// <c>{"tokens","pinyin","initials","defs"}</c> JSON。
    ///
    /// <para><c>field</c> 对齐 <see cref="Field"/>（0=Title .. 6=Filename），当前只做
    /// 契约校验；失败返回码 1，<c>out_json</c> 为 D.4 错误 JSON。</para>
    /// </summary>
    public static int Analyze(byte* text, int field, byte** outJson, int* outLen)
    {
        if (outJson != null) *outJson = null;
        if (outLen != null) *outLen = 0;
        if (outJson == null || outLen == null) return SearchApiCodes.InvalidRequest;

        try
        {
            var value = PtrToString(text);
            if (value is null)
                return FailJson(outJson, outLen, SearchApiError.Invalid("text 为空", "invalid_argument", "text"));
            if (field < 0 || field > (int)Field.Filename)
            {
                return FailJson(outJson, outLen, SearchApiError.Invalid(
                    $"非法 field: {field}", "invalid_argument", "field"));
            }
            // 查询侧分析（P4）：含连写全拼切分（woaini → wo ai ni）；
            // 索引侧词元输出随 v4（拼音改走 ToolGood.Words 词级消歧）变化，AnalyzerVersion 已递增。
            SetUtf8(outJson, outLen, IndexerJson.Analysis(Analyzer.AnalyzeQuery(value)));
            return SearchApiCodes.Ok;
        }
        catch (Exception ex)
        {
            return FailJson(outJson, outLen, SearchApiError.Internal(ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "era_analyze")]
    public static int AnalyzeExport(byte* text, int field, byte** outJson, int* outLen)
        => Analyze(text, field, outJson, outLen);

    /// <summary>
    /// 注入统一日志 sink（宿主 Dart 传入 libarchoera_log 的 archoera_log_write 指针）。
    /// 调用约定 C：void (*)(int level, const char* tag, const char* message)。
    /// sink=NULL 注销并回退 stderr；[minLevel] 数值对齐 archoera_log.h：0..4。
    /// </summary>
    public static void SetLogSink(delegate* unmanaged[Cdecl]<int, byte*, byte*, void> sink, int minLevel)
        => Log.SetSink(sink, minLevel);

    [UnmanagedCallersOnly(EntryPoint = "era_set_log_sink")]
    public static void SetLogSinkExport(delegate* unmanaged[Cdecl]<int, byte*, byte*, void> sink, int minLevel)
        => SetLogSink(sink, minLevel);

    /* ------------------------------------------------------------------ */
    /* 输出内存                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>释放本模块 <see cref="NativeMemory"/> 分配的输出（**不要**混用 free / CoTaskMemFree）。</summary>
    public static void Free(byte* ptr)
    {
        if (ptr != null) NativeMemory.Free(ptr);
    }

    [UnmanagedCallersOnly(EntryPoint = "era_search_free")]
    public static void FreeExport(byte* ptr) => Free(ptr);

    /* ------------------------------------------------------------------ */
    /* 工具                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>从指针取回句柄；空 / 已关闭 / 指针非法一律返回 <c>null</c>。</summary>
    private static SearchApiHandle? ResolveHandle(void* handle)
    {
        if (handle == null || !LiveHandles.ContainsKey((nint)handle)) return null;
        try
        {
            return GCHandle.FromIntPtr((IntPtr)handle).Target as SearchApiHandle;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>filter.trackIds</c> 后过滤（S1，保序）。
    ///
    /// 空数组按「交集为空」处理（显式过滤集合为空 → 零命中），而不是「不过滤」：
    /// 后者会让调用方的一个空列表静默变成整库搜索。过滤本身是查询期 HashSet
    /// （≤50k），不参与排序键。
    /// </summary>
    private static List<SearchHit> ApplyTrackFilter(List<SearchHit> items, IReadOnlyList<string>? filter)
    {
        if (filter is null) return items;
        var allowed = new HashSet<string>(filter, StringComparer.OrdinalIgnoreCase);
        var kept = new List<SearchHit>(Math.Min(items.Count, allowed.Count));
        foreach (var hit in items)
        {
            if (allowed.Contains(hit.TrackId)) kept.Add(hit);
        }
        return kept;
    }

    /// <summary>
    /// 解析 <c>era_search_open</c> 的 options_json：只校验「是 JSON 对象 / null」，
    /// 未知键忽略（内部接口不做严格 schema 拒绝）。null 指针 / 空白 / JSON null 视为全部缺省。
    /// </summary>
    /// <summary>
    /// 校验并读取查询句柄选项：对象 / null；未知键忽略。
    /// 支持 <c>libraryDb</c>（字符串，可空）：源曲库路径，用于查询期过滤已删除曲目（T2.9）。
    /// </summary>
    private static bool TryReadOpenOptions(byte* optionsJson, out string? libraryDbPath)
    {
        libraryDbPath = null;
        if (optionsJson == null) return true;
        var raw = PtrToString(optionsJson);
        if (string.IsNullOrWhiteSpace(raw)) return true;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Null) return true;
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("libraryDb", out var value)
                && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.String) return false;
                libraryDbPath = value.GetString();
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 <c>options_json</c>：未知键忽略；非法 JSON 返回 <c>false</c> 与错误文本。
    /// 缺省：index_lrc=false、pinyin=false、lyrics_pinyin=false、romaji_lyrics=false、
    /// initialsLyrics=false、batch=0。所有派生列默认关闭，由设置页显式开启后重建生效。
    /// 键名同时接受 <c>lyrics_pinyin</c> / <c>lyricsPinyin</c>、<c>initials_lyrics</c> / <c>initialsLyrics</c>。
    /// </summary>
    internal static bool TryParseOptions(byte* optionsJson, out IndexerOptions options, out string error)
    {
        options = new IndexerOptions();
        error = "";
        if (optionsJson == null) return true;

        var raw = PtrToString(optionsJson);
        if (string.IsNullOrWhiteSpace(raw)) return true;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "options_json 必须是 JSON 对象";
                return false;
            }
            var root = doc.RootElement;
            if (root.TryGetProperty("index_lrc", out var lrc)
                && (lrc.ValueKind == JsonValueKind.True || lrc.ValueKind == JsonValueKind.False))
                options.IndexLrc = lrc.GetBoolean();
            if (root.TryGetProperty("pinyin", out var py)
                && (py.ValueKind == JsonValueKind.True || py.ValueKind == JsonValueKind.False))
                options.Pinyin = py.GetBoolean();
            if ((root.TryGetProperty("lyrics_pinyin", out var lyricsPinyin)
                    || root.TryGetProperty("lyricsPinyin", out lyricsPinyin))
                && (lyricsPinyin.ValueKind == JsonValueKind.True
                    || lyricsPinyin.ValueKind == JsonValueKind.False))
                options.LyricsPinyin = lyricsPinyin.GetBoolean();
            if (root.TryGetProperty("romaji_lyrics", out var romaji)
                && (romaji.ValueKind == JsonValueKind.True || romaji.ValueKind == JsonValueKind.False))
                options.RomajiLyrics = romaji.GetBoolean();
            if ((root.TryGetProperty("initialsLyrics", out var initialsLyrics)
                    || root.TryGetProperty("initials_lyrics", out initialsLyrics)
                    || root.TryGetProperty("lyricsInitials", out initialsLyrics)
                    || root.TryGetProperty("lyrics_initials", out initialsLyrics))
                && (initialsLyrics.ValueKind == JsonValueKind.True
                    || initialsLyrics.ValueKind == JsonValueKind.False))
                options.InitialsLyrics = initialsLyrics.GetBoolean();
            if (root.TryGetProperty("batch", out var batch) && batch.ValueKind == JsonValueKind.Number)
                options.Batch = batch.GetInt32();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"options_json 非法: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 进度回调：JSON 只在回调期间有效（缓冲不在回调后释放，S1 沿用旧行为——
    /// 每次构建至多几十次回调，泄漏量可忽略；S3 的任务句柄一并改为调用方负责）。
    /// </summary>
    private static void FireProgress(delegate* unmanaged[Cdecl]<byte*, void> cb, IndexerProgress p)
    {
        var json = IndexerJson.Progress(p);
        var bytes = Encoding.UTF8.GetBytes(json);
        var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        Marshal.Copy(bytes, 0, (IntPtr)ptr, bytes.Length);
        ptr[bytes.Length] = 0;
        cb(ptr);
    }

    private static string? PtrToString(byte* ptr)
        => ptr == null ? null : Marshal.PtrToStringUTF8((IntPtr)ptr);

    private static void SetUtf8(byte** dst, int* len, string s)
    {
        // 直接编码到非托管缓冲，避免 UTF-8 大数组再进一次 LOH。
        var count = Encoding.UTF8.GetByteCount(s);
        var ptr = (byte*)NativeMemory.Alloc((nuint)count + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(ptr, count));
        ptr[count] = 0;
        *dst = ptr;
        *len = count;
    }

    /// <summary>
    /// 由 <see cref="SearchApiJson"/> 的复用缓冲直接落盘到非托管内存：
    /// 大结果集不再产生「JSON string + byte[]」两次 LOH 分配（T3.14 rss_query）。
    /// </summary>
    private static void SetUtf8(byte** dst, int* len, ReadOnlySpan<byte> bytes)
    {
        var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(ptr, bytes.Length));
        ptr[bytes.Length] = 0;
        *dst = ptr;
        *len = bytes.Length;
    }

    /// <summary>写 D.4 错误 JSON 并返回其错误码（失败路径共用）。</summary>
    private static int FailJson(byte** dst, int* len, SearchApiError error)
    {
        SetUtf8(dst, len, SearchApiJson.Error(error));
        return error.Code;
    }
}
