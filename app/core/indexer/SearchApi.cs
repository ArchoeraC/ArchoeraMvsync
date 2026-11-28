// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Archoera.Indexer;

/// <summary>
/// `era_search_*` 统一返回码（D.4）。所有 <c>era_*</c> 入口共用这一张表，
/// 调用方据此决定「修请求 / 重建 / 降级 LIKE / 重试」。
/// </summary>
internal static class SearchApiCodes
{
    /// <summary>成功。</summary>
    public const int Ok = 0;

    /// <summary>请求 / JSON 无效（旧字段出现也走这里）。</summary>
    public const int InvalidRequest = 1;

    /// <summary>索引不存在或未就绪。</summary>
    public const int NotReady = 2;

    /// <summary>版本 / 能力不符。</summary>
    public const int VersionMismatch = 3;

    /// <summary>忙（写任务进行中 / 文件锁被占）。S1 尚未使用，留给 S3 的写任务。</summary>
    public const int Busy = 4;

    /// <summary>IO / 磁盘满 / 只读。</summary>
    public const int Io = 5;

    /// <summary>内存不足。</summary>
    public const int OutOfMemory = 6;

    /// <summary>已取消（正常路径，不告警）。</summary>
    public const int Canceled = 7;

    /// <summary>内部错误（bug）。</summary>
    public const int Internal = 8;

    /// <summary>D.4 的 <c>retryable</c> 列：2 / 4 / 6 / 8 可重试。</summary>
    public static bool IsRetryable(int code)
        => code is NotReady or Busy or OutOfMemory or Internal;
}

/// <summary>
/// 错误载荷（D.4）。<c>detail</c> 只放**可安全展示 / 记录**的结构化信息：
/// 不放绝对路径、不放用户输入原文（<see cref="Field"/> 只放字段名）。
/// </summary>
internal sealed class SearchApiError
{
    public int Code { get; init; } = SearchApiCodes.InvalidRequest;
    public string Message { get; init; } = "";

    /// <summary>D.4 的 <c>retryable</c>（由 <see cref="Code"/> 推出，不单独传）。</summary>
    public bool Retryable => SearchApiCodes.IsRetryable(Code);

    /// <summary><c>detail.reason</c>：机器可读的细分原因（<c>deprecated_field</c> 等）。</summary>
    public string? Reason { get; init; }

    /// <summary><c>detail.field</c>：出错的请求字段名。</summary>
    public string? Field { get; init; }

    /// <summary><c>detail.expected</c>：期望值（仅标量；表述不了时留空）。</summary>
    public long? Expected { get; init; }

    /// <summary><c>detail.actual</c>：实际值（仅标量；表述不了时留空）。</summary>
    public long? Actual { get; init; }

    public static SearchApiError Invalid(string message, string? reason = null, string? field = null)
        => new() { Code = SearchApiCodes.InvalidRequest, Message = message, Reason = reason, Field = field };

    /// <summary>旧字段（<c>minExact</c> / <c>candidateCap</c> / <c>fuzzy</c> / <c>ranking</c> / <c>engine</c>）出现。</summary>
    public static SearchApiError Deprecated(string field)
        => new()
        {
            Code = SearchApiCodes.InvalidRequest,
            Message = $"请求字段 {field} 在 ABI v2 已废弃（不要传旧字段，语义由 api:2 定义）",
            Reason = "deprecated_field",
            Field = field,
        };

    public static SearchApiError UnsupportedApi(long? actual)
        => new()
        {
            Code = SearchApiCodes.InvalidRequest,
            Message = "请求 api 必须为 2（不做 api:1 兼容）",
            Reason = "unsupported_api",
            Field = "api",
            Expected = SearchApiRequest.ApiVersion,
            Actual = actual,
        };

    public static SearchApiError NotReady(string message)
        => new() { Code = SearchApiCodes.NotReady, Message = message, Reason = "index_not_ready" };

    public static SearchApiError VersionMismatch(string message)
        => new() { Code = SearchApiCodes.VersionMismatch, Message = message, Reason = "version_mismatch" };

    public static SearchApiError Io(string message)
        => new() { Code = SearchApiCodes.Io, Message = message, Reason = "io_error" };

    public static SearchApiError OutOfMemory()
        => new() { Code = SearchApiCodes.OutOfMemory, Message = "内存不足", Reason = "out_of_memory" };

    public static SearchApiError Canceled()
        => new() { Code = SearchApiCodes.Canceled, Message = "已取消", Reason = "canceled" };

    public static SearchApiError Internal(string message)
        => new() { Code = SearchApiCodes.Internal, Message = message, Reason = "internal_error" };
}

/// <summary>
/// 查询句柄（<c>era_search_open</c> 的产物）：持有索引路径与打开时的元数据快照。
///
/// <para><b>S1 过渡</b>：句柄只存路径 + 快照，真正的只读连接仍由
/// <see cref="SearchSession"/> 的**进程级静态缓存**持有（按路径复用）；
/// <c>close</c> 会连同缓存一起丢弃。多句柄并存时因此共享同一份缓存——
/// S3 改成 per-handle 的 mmap / 结果 LRU 后再解除这个限制。</para>
/// </summary>
internal sealed class SearchApiHandle
{
    public SearchApiHandle(string indexPath, StoreMeta meta, string? libraryDbPath = null)
    {
        IndexPath = indexPath;
        Meta = meta;
        LibraryDbPath = libraryDbPath;
    }

    /// <summary>索引文件的绝对路径（<see cref="SearchSession"/> 按此复用会话）。</summary>
    public string IndexPath { get; }

    /// <summary>
    /// 源曲库路径（`options_json.libraryDb`，可空）。非空时查询期用它过滤已删除曲目（T2.9）；
    /// 库不可读则**不过滤**（失败开放），见 <see cref="SearchSession"/>。
    /// </summary>
    public string? LibraryDbPath { get; }

    /// <summary>打开时的元数据快照（<c>gen</c> / <c>schemaVersion</c> 从这里取）。</summary>
    public StoreMeta Meta { get; }

    /// <summary>响应里的 <c>gen</c>。</summary>
    public long Generation => Meta.AppliedGeneration;
}

/// <summary>
/// 查询请求 v2 的**纯解析**（D.2）：只做 JSON → 强类型，不碰存储、不查库。
///
/// <para>解析结果的 <see cref="Request"/> 是既有 <see cref="SearchRequest"/>（**不改其语义**）：
/// S1 默认开启兜底趟（<c>Fuzzy=true</c> / <c>MinExact=1</c> / 默认 candidateCap），
/// 等价于旧的 <c>default@1</c>。v2 新增的 <c>api</c> / <c>limit</c> 截断 / <c>filter</c> 等
/// 只存在于本类的附加字段里，由 FFI 层用于响应与后过滤。</para>
///
/// <para><b>不做 api:1 兼容</b>（§2.2）：<c>api</c> 必须为 2；<c>minExact</c> /
/// <c>candidateCap</c> / <c>fuzzy</c> / <c>ranking</c> / <c>engine</c> 出现即码 1，
/// 目的就是让「忘了删旧字段」的调用方立刻暴露，而不是静默按新语义跑。</para>
/// </summary>
internal sealed class SearchApiRequest
{
    /// <summary>当前 C ABI / 载荷版本。</summary>
    public const int ApiVersion = 2;

    /// <summary><c>query</c> 的码点上限（超出截断并置 <c>queryTruncated</c>）。</summary>
    public const int MaxQueryCodePoints = 200;

    /// <summary><c>limit</c> 缺省 / <c>&lt;1</c> 时的条数。</summary>
    public const int DefaultLimit = 20;

    /// <summary><c>limit</c> 上限（超出压到该值并置 <c>clamped</c>）。</summary>
    public const int MaxLimit = 10000;

    /// <summary><c>filter.trackIds</c> 的元素上限。</summary>
    public const int MaxFilterTrackIds = 50000;

    /// <summary>出现即拒绝的旧字段（D.2「拒绝的旧字段」）。</summary>
    private static readonly string[] DeprecatedFields =
    {
        "minExact", "candidateCap", "fuzzy", "ranking", "engine",
    };

    /// <summary>交给 <see cref="SearchSession"/> 的既有强类型请求（默认值见类注释）。</summary>
    public SearchRequest Request { get; init; } = new();

    /// <summary>请求里的 <c>api</c> 值：解析成功即恒为 2（随对象保存，供响应头复用）。</summary>
    public int Api => ApiVersion;

    /// <summary>实际生效的方案 id（S1 恒为 <see cref="SearchPolicy.SchemeId"/>）。</summary>
    public string Scheme { get; init; } = SearchPolicy.SchemeId;

    /// <summary>请求里的原值（缺省 / 未知值都原样回显，供诊断）。</summary>
    public string SchemeRequested { get; init; } = SearchPolicy.SchemeId;

    /// <summary><c>query</c> 超过 200 码点被截断。</summary>
    public bool QueryTruncated { get; init; }

    /// <summary><c>limit</c> 被压到 <see cref="MaxLimit"/>。</summary>
    public bool Clamped { get; init; }

    /// <summary>
    /// <c>filter.trackIds</c>（已校验：≤50k 且每项 32 hex）。
    ///
    /// <para>S1 只在**结果上做后过滤**（先按库范围召回，再按集合裁剪）；
    /// 不按 <c>2×limit</c> 放宽召回，因此过滤后可能不足 <c>limit</c> 条——
    /// 这是用户显式过滤的结果，不置 <c>partial</c>。放宽召回留到 T3。</para>
    /// </summary>
    public IReadOnlyList<string>? FilterTrackIds { get; init; }

    /// <summary>返回前 50 条命中的 <c>explain</c> 与执行 <c>trace</c>（S2 / T2.3）。</summary>
    public bool Explain { get; init; }

    /// <summary>解析后保留但 S1 不产出对应字段（T2.7）。</summary>
    public bool Highlight { get; init; }

    /// <summary>返回前 10 条命中的歌词片段（T2.8：查询期只读 <c>library.db</c> 截取）。</summary>
    public bool Snippet { get; init; }

    /// <summary>
    /// 解析请求 JSON；成功返回 <c>(request, null)</c>，失败返回 <c>(null, error)</c>（码 1）。
    /// 非法 JSON / 非对象同样是码 1（不是异常）。
    /// </summary>
    public static (SearchApiRequest? Request, SearchApiError? Error) Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, SearchApiError.Invalid("request_json 不是合法 JSON", "invalid_json"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, SearchApiError.Invalid("request_json 必须是 JSON 对象", "invalid_request"));

            // 旧字段先查：它们的出现本身就是「调用方还没跟上 v2」的强信号，
            // 优先于其它字段错误报出来（detail.field 直接指名字段）。
            foreach (var name in DeprecatedFields)
            {
                if (root.TryGetProperty(name, out _)) return (null, SearchApiError.Deprecated(name));
            }

            // api：必须存在且 == 2。
            if (!root.TryGetProperty("api", out var apiElement))
                return (null, SearchApiError.UnsupportedApi(null));
            if (apiElement.ValueKind != JsonValueKind.Number || !apiElement.TryGetInt64(out var apiValue)
                || apiValue != ApiVersion)
            {
                return (null, SearchApiError.UnsupportedApi(
                    apiElement.ValueKind == JsonValueKind.Number && apiElement.TryGetInt64(out var actual)
                        ? actual
                        : null));
            }

            // query：trim 后按**码点**截断到 200。
            var query = "";
            if (root.TryGetProperty("query", out var queryElement))
            {
                if (queryElement.ValueKind != JsonValueKind.String)
                    return (null, SearchApiError.Invalid("query 必须是字符串", "invalid_type", "query"));
                query = (queryElement.GetString() ?? "").Trim();
            }
            query = TruncateCodePoints(query, MaxQueryCodePoints, out var queryTruncated);

            // scope：all | metadata | lyrics。
            var metadata = false;
            var lyrics = false;
            if (root.TryGetProperty("scope", out var scopeElement))
            {
                if (scopeElement.ValueKind != JsonValueKind.String)
                    return (null, SearchApiError.Invalid("scope 必须是字符串", "invalid_type", "scope"));
                var scope = scopeElement.GetString();
                if (string.Equals(scope, "metadata", StringComparison.OrdinalIgnoreCase))
                {
                    metadata = true;
                }
                else if (string.Equals(scope, "lyrics", StringComparison.OrdinalIgnoreCase))
                {
                    lyrics = true;
                }
                else if (!string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, SearchApiError.Invalid(
                        "scope 只能是 all / metadata / lyrics", "invalid_scope", "scope"));
                }
            }

            // limit：<1 → 20；>10000 → 10000 + clamped。
            var limit = DefaultLimit;
            var clamped = false;
            if (root.TryGetProperty("limit", out var limitElement))
            {
                if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetDouble(out var limitValue))
                    return (null, SearchApiError.Invalid("limit 必须是数字", "invalid_type", "limit"));
                var truncatedLimit = limitValue >= long.MaxValue ? long.MaxValue : (long)Math.Truncate(limitValue);
                if (truncatedLimit < 1)
                {
                    limit = DefaultLimit;
                }
                else if (truncatedLimit > MaxLimit)
                {
                    limit = MaxLimit;
                    clamped = true;
                }
                else
                {
                    limit = (int)truncatedLimit;
                }
            }

            // offset：≥0（S1 保留扩展位，负数按参数错误拒绝）。
            var offset = 0;
            if (root.TryGetProperty("offset", out var offsetElement))
            {
                if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetDouble(out var offsetValue))
                    return (null, SearchApiError.Invalid("offset 必须是数字", "invalid_type", "offset"));
                if (offsetValue < 0)
                    return (null, SearchApiError.Invalid("offset 不能为负数", "invalid_offset", "offset"));
                offset = offsetValue >= int.MaxValue ? int.MaxValue : (int)Math.Truncate(offsetValue);
            }

            // total：none | capped | exact（缺省 / null → none）。
            var totalMode = SearchTotalMode.None;
            if (root.TryGetProperty("total", out var totalElement)
                && totalElement.ValueKind != JsonValueKind.Null)
            {
                if (totalElement.ValueKind != JsonValueKind.String)
                    return (null, SearchApiError.Invalid("total 必须是字符串", "invalid_type", "total"));
                var totalText = totalElement.GetString();
                if (!SearchPaging.TryParseTotalMode(totalText, out totalMode))
                {
                    return (null, SearchApiError.Invalid(
                        "total 只能是 none / capped / exact", "invalid_total", "total"));
                }
            }

            // cursor：不透明 base64url(32B)；非法 / 超长 → 码 1（D.2 / D.5）。
            string? cursorRaw = null;
            SearchCursor? cursor = null;
            if (root.TryGetProperty("cursor", out var cursorElement)
                && cursorElement.ValueKind != JsonValueKind.Null)
            {
                if (cursorElement.ValueKind != JsonValueKind.String)
                    return (null, SearchApiError.Invalid("cursor 必须是字符串或 null", "invalid_type", "cursor"));
                cursorRaw = cursorElement.GetString() ?? "";
                if (cursorRaw.Length == 0)
                    return (null, SearchApiError.Invalid("cursor 不能为空串", "invalid_cursor", "cursor"));
                if (cursorRaw.Length > SearchPaging.MaxCursorChars)
                {
                    return (null, SearchApiError.Invalid(
                        $"cursor 超过 {SearchPaging.MaxCursorChars} 字符上限", "invalid_cursor", "cursor"));
                }
                if (!SearchPaging.TryDecode(cursorRaw, out var decoded))
                    return (null, SearchApiError.Invalid("cursor 非法（不是 v1 的 32 字节游标）", "invalid_cursor", "cursor"));
                cursor = decoded;
            }

            // explain / highlight / snippet：接受但 S1 不产出（T2.7 / T2.8）。
            if (!TryReadBool(root, "explain", out var explain, out var boolError)
                || !TryReadBool(root, "highlight", out var highlight, out boolError)
                || !TryReadBool(root, "snippet", out var snippet, out boolError))
            {
                return (null, boolError);
            }

            // scheme：未知值回退默认执行，但响应里保留 schemeRequested（T2.4）。
            var schemeRequested = SearchPolicy.SchemeId;
            if (root.TryGetProperty("scheme", out var schemeElement))
            {
                if (schemeElement.ValueKind != JsonValueKind.String)
                    return (null, SearchApiError.Invalid("scheme 必须是字符串", "invalid_type", "scheme"));
                schemeRequested = schemeElement.GetString() ?? "";
            }
            var scheme = SchemeRegistry.TryGet(schemeRequested, out var resolvedScheme)
                ? resolvedScheme
                : SchemeRegistry.Default;

            var (filterIds, filterError) = ParseFilter(root);
            if (filterError is not null) return (null, filterError);

            var request = new SearchRequest
            {
                Query = query,
                Metadata = metadata,
                Lyrics = lyrics,
                Scheme = schemeRequested,
                Limit = limit,
                Offset = offset,
                // S2 默认启用兜底趟（default@1）：v2 不再暴露 fuzzy / minExact 开关。
                Fuzzy = true,
                MinExact = 1,
                CandidateCap = SearchRequest.DefaultCandidateCap,
                Explain = explain,
                Snippet = snippet,
                Highlight = highlight,
                Cursor = cursor,
                CursorRaw = cursorRaw,
                TotalMode = totalMode,
            };

            return (new SearchApiRequest
            {
                Request = request,
                Scheme = scheme.Id,
                SchemeRequested = schemeRequested,
                QueryTruncated = queryTruncated,
                Clamped = clamped,
                FilterTrackIds = filterIds,
                Explain = explain,
                Highlight = highlight,
                Snippet = snippet,
            }, null);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 解析工具                                                            */
    /* ------------------------------------------------------------------ */

    private static (List<string>? Ids, SearchApiError? Error) ParseFilter(JsonElement root)
    {
        if (!root.TryGetProperty("filter", out var filterElement)
            || filterElement.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }
        if (filterElement.ValueKind != JsonValueKind.Object)
            return (null, SearchApiError.Invalid("filter 必须是对象或 null", "invalid_filter", "filter"));

        if (!filterElement.TryGetProperty("trackIds", out var idsElement)
            || idsElement.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }
        if (idsElement.ValueKind != JsonValueKind.Array)
            return (null, SearchApiError.Invalid(
                "filter.trackIds 必须是字符串数组", "invalid_filter", "filter.trackIds"));

        var length = idsElement.GetArrayLength();
        if (length > MaxFilterTrackIds)
        {
            return (null, SearchApiError.Invalid(
                $"filter.trackIds 超过 {MaxFilterTrackIds} 项上限", "invalid_filter", "filter.trackIds"));
        }

        var ids = new List<string>(length);
        foreach (var item in idsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return (null, SearchApiError.Invalid(
                    "filter.trackIds 的元素必须是字符串", "invalid_filter", "filter.trackIds"));
            var id = item.GetString() ?? "";
            if (!IsHex32(id))
            {
                return (null, SearchApiError.Invalid(
                    "filter.trackIds 的元素必须是 32 位十六进制", "invalid_filter", "filter.trackIds"));
            }
            ids.Add(id);
        }
        return (ids, null);
    }

    /// <summary>读可选 bool；出现但不是 bool → 码 1。</summary>
    private static bool TryReadBool(JsonElement root, string name, out bool value, out SearchApiError? error)
    {
        value = false;
        error = null;
        if (!root.TryGetProperty(name, out var element)) return true;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = SearchApiError.Invalid($"{name} 必须是布尔值", "invalid_type", name);
            return false;
        }
        value = element.GetBoolean();
        return true;
    }

    /// <summary>严格 32 位十六进制（大小写皆可；track_id 是 scanner 的纯托管 MD5）。</summary>
    internal static bool IsHex32(string value)
    {
        if (value.Length != 32) return false;
        foreach (var c in value)
        {
            var hex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>
    /// 按**码点**（而非 UTF-16 code unit）截断：Dart 的 <c>String.length</c> 是 UTF-16，
    /// 但契约写的是「码点」，emoji / 补充平面字符不能被切半个代理对。
    /// </summary>
    private static string TruncateCodePoints(string text, int max, out bool truncated)
    {
        truncated = false;
        if (text.Length <= max) return text;

        var count = 0;
        var builder = new StringBuilder(Math.Min(text.Length, max * 2));
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == max)
            {
                truncated = true;
                break;
            }
            builder.Append(rune.ToString());
            count++;
        }
        return truncated ? builder.ToString() : text;
    }
}

/// <summary>
/// 查询响应 v2 的序列化（D.3 / D.4）。与 <see cref="IndexerJson"/> 一样手写
/// <see cref="Utf8JsonWriter"/>（AOT 安全、无源生成依赖），但形状独立：
/// CLI 继续用 <see cref="IndexerJson.QueryResult"/> 的旧格式，FFI 用这里的 v2。
/// </summary>
internal static class SearchApiJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// 线程内复用的 JSON 缓冲。10k 命中的响应约 2–3 MB，若每次都新建
    /// <see cref="ArrayBufferWriter{T}"/> 会反复进 LOH，NativeAOT 进程的匿名 RSS
    /// 会随查询次数上涨；这里保留一块「高水位」缓冲，下一次查询直接覆盖。
    /// </summary>
    [ThreadStatic] private static ScratchWriter? t_scratch;

    private static string Write(Action<Utf8JsonWriter> body, Action<ReadOnlySpan<byte>>? sink = null)
    {
        if (sink is not null)
        {
            var scratch = t_scratch ??= new ScratchWriter();
            scratch.Reset();
            using (var writer = new Utf8JsonWriter(scratch, WriterOptions))
            {
                body(writer);
                writer.Flush();
            }
            sink(scratch.WrittenSpan);
            // 宽响应缓冲（~0.7 MB）用完即收：保留会让匿名 RSS 多占 0.5–1 MB，
            // 而下一次宽查询重新增长的成本远低于多留一整个大对象。
            scratch.ShrinkRetention();
            return string.Empty;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var w2 = new Utf8JsonWriter(buffer, WriterOptions);
        body(w2);
        w2.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>可增长、线程内复用的 <see cref="IBufferWriter{T}"/>（只增不减）。</summary>
    private sealed class ScratchWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[64 * 1024];
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Reset() => _written = 0;

        /// <summary>超过 512 KB 的缓冲缩回 64 KB（由调用方在响应写出后调用）。</summary>
        public void ShrinkRetention()
        {
            if (_buffer.Length <= 512 * 1024) return;
            _buffer = new byte[64 * 1024];
            _written = 0;
        }

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            var need = Math.Max(sizeHint, 1);
            if (_buffer.Length - _written >= need) return;
            var next = Math.Max(_buffer.Length * 2, _written + need);
            Array.Resize(ref _buffer, next);
        }
    }

    /// <summary>
    /// 成功响应（D.3 的 S1 子集）。
    ///
    /// <para><b>过渡字段 <c>passes</c></b>：S1 仍输出（Dart 查询日志在读它）；
    /// S2.5 改成 <c>explain</c> / <c>trace</c> 后删除。同理 <c>score</c> / <c>pass</c>
    /// 只作诊断与归因，顺序才是契约。</para>
    ///
    /// <para>S2 已产出 <c>explain</c> / <c>trace</c>（仅前 50 条、仅请求开启时）、
    /// <c>highlight</c>（仅前 50 条、仅请求开启时）与 <c>snippet</c>（仅前 10 条、仅请求开启时）；
    /// T2.10 起 <c>total</c> / <c>next</c> / <c>restart</c> 由会话计算后原样写出。</para>
    /// </summary>
    public static string Success(
        IReadOnlyList<SearchHit> items,
        PassCounts passes,
        long gen,
        string scheme,
        string schemeRequested,
        bool clamped,
        bool queryTruncated,
        IReadOnlyDictionary<string, SearchExplanation>? explanations = null,
        SearchTrace? trace = null,
        IReadOnlyDictionary<string, LyricSnippet>? snippets = null,
        IReadOnlyDictionary<string, TrackHighlight>? highlights = null,
        long total = -1,
        bool totalExact = false,
        bool restart = false,
        string? next = null,
        Action<ReadOnlySpan<byte>>? sink = null) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WriteNumber("gen", gen);
        w.WriteString("scheme", scheme);
        w.WriteString("schemeRequested", schemeRequested);

        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var hit in items)
        {
            w.WriteStartObject();
            w.WriteString("trackId", hit.TrackId);
            w.WriteNumber("score", hit.Score);
            // 命中原由归因（精度审计 §6.3）：Dart 侧据此区分字面命中与兜底命中。
            w.WriteString("pass", hit.Pass);
            if (explanations is not null
                && explanations.TryGetValue(hit.TrackId, out var explanation))
            {
                w.WritePropertyName("explain");
                WriteExplain(w, explanation);
            }
            if (highlights is not null
                && highlights.TryGetValue(hit.TrackId, out var highlight))
            {
                w.WritePropertyName("highlight");
                WriteHighlight(w, highlight);
                w.WriteBoolean("highlightTruncated", highlight.Truncated);
            }
            if (snippets is not null
                && snippets.TryGetValue(hit.TrackId, out var snippet))
            {
                w.WritePropertyName("snippet");
                WriteSnippet(w, snippet);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // S1 过渡诊断字段（S2.5 由 explain / trace 取代后删除）。
        w.WritePropertyName("passes");
        w.WriteStartObject();
        w.WriteNumber("dropTokens", passes.DropTokens);
        w.WriteNumber("pinyinLoose", passes.PinyinLoose);
        w.WriteNumber("reading", passes.Reading);
        w.WriteNumber("crossLingual", passes.CrossLingual);
        w.WriteNumber("fuzzy", passes.Fuzzy);
        w.WriteNumber("cjkFuzzy", passes.CjkFuzzy);
        w.WriteEndObject();

        // T2.10：total / next / restart 由 SearchSession 计算（none → -1）。
        w.WriteNumber("total", total);
        w.WriteBoolean("totalExact", totalExact);
        w.WriteBoolean("partial", false);
        w.WriteBoolean("clamped", clamped);
        w.WriteBoolean("queryTruncated", queryTruncated);
        w.WriteBoolean("restart", restart);
        if (next is null)
        {
            w.WriteNull("next");
        }
        else
        {
            w.WriteString("next", next);
        }
        if (trace is null)
        {
            w.WriteNull("trace");
        }
        else
        {
            w.WritePropertyName("trace");
            WriteTrace(w, trace);
        }
        w.WriteEndObject();
    }, sink);

    /// <summary>
    /// 降级检索响应（T2.12，<c>era_search_fallback</c>）：与 <see cref="Success"/> 同形，
    /// 但 <c>scheme</c> 固定 <c>fallback@1</c>，不产出 <c>explain</c> / <c>trace</c>，
    /// 不实现游标（<c>next=null</c> / <c>restart=false</c>），<c>total</c> 为全量匹配数。
    /// </summary>
    public static string Fallback(
        IReadOnlyList<SearchHit> items,
        string schemeRequested,
        bool clamped,
        bool queryTruncated,
        long total,
        IReadOnlyDictionary<string, LyricSnippet>? snippets = null,
        IReadOnlyDictionary<string, TrackHighlight>? highlights = null,
        Action<ReadOnlySpan<byte>>? sink = null) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WriteNumber("gen", 0);
        w.WriteString("scheme", SearchFallback.SchemeId);
        w.WriteString("schemeRequested", schemeRequested);

        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var hit in items)
        {
            w.WriteStartObject();
            w.WriteString("trackId", hit.TrackId);
            w.WriteNumber("score", hit.Score);
            w.WriteString("pass", hit.Pass);
            if (highlights is not null
                && highlights.TryGetValue(hit.TrackId, out var highlight))
            {
                w.WritePropertyName("highlight");
                WriteHighlight(w, highlight);
                w.WriteBoolean("highlightTruncated", highlight.Truncated);
            }
            if (snippets is not null
                && snippets.TryGetValue(hit.TrackId, out var snippet))
            {
                w.WritePropertyName("snippet");
                WriteSnippet(w, snippet);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // 与正式查询同形的诊断字段（fallback 不执行任何兜底趟，全部为 0）。
        w.WritePropertyName("passes");
        w.WriteStartObject();
        w.WriteNumber("dropTokens", 0);
        w.WriteNumber("pinyinLoose", 0);
        w.WriteNumber("reading", 0);
        w.WriteNumber("crossLingual", 0);
        w.WriteNumber("fuzzy", 0);
        w.WriteNumber("cjkFuzzy", 0);
        w.WriteEndObject();

        w.WriteNumber("total", total);
        w.WriteBoolean("totalExact", true);
        w.WriteBoolean("partial", false);
        w.WriteBoolean("clamped", clamped);
        w.WriteBoolean("queryTruncated", queryTruncated);
        w.WriteBoolean("restart", false);
        w.WriteNull("next");
        w.WriteNull("trace");
        w.WriteEndObject();
    }, sink);

    /// <summary>
    /// 装饰响应（T2.11，<c>era_search_decorate</c>）：按 trackId 给出高亮 / 片段，
    /// 不含命中列表与排序信息。缺失的曲目直接不出现在对象里（失败开放）。
    /// </summary>
    public static string Decorate(
        IReadOnlyDictionary<string, TrackHighlight> highlights,
        IReadOnlyDictionary<string, LyricSnippet> snippets) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);

        w.WritePropertyName("highlights");
        w.WriteStartObject();
        foreach (var pair in highlights)
        {
            w.WritePropertyName(pair.Key);
            WriteHighlight(w, pair.Value);
        }
        w.WriteEndObject();

        w.WritePropertyName("snippets");
        w.WriteStartObject();
        foreach (var pair in snippets)
        {
            w.WritePropertyName(pair.Key);
            WriteSnippet(w, pair.Value);
        }
        w.WriteEndObject();

        w.WriteEndObject();
    });

    /// <summary>单条命中的 <c>highlight</c>（D.3.2）：字段 / UTF-16 半开区间 / 模式。</summary>
    public static void WriteHighlight(Utf8JsonWriter w, TrackHighlight highlight)
    {
        w.WriteStartArray();
        foreach (var range in highlight.Ranges)
        {
            w.WriteStartObject();
            w.WriteString("field", range.Field);
            w.WriteNumber("start", range.Start);
            w.WriteNumber("end", range.End);
            w.WriteString("mode", range.Mode);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>单条命中的 <c>snippet</c>（D.3.2）：字段 / 窗口文本 / 相对区间 / 截断标记。</summary>
    public static void WriteSnippet(Utf8JsonWriter w, LyricSnippet snippet)
    {
        w.WriteStartObject();
        w.WriteString("field", snippet.Field);
        w.WriteString("text", snippet.Text);
        w.WritePropertyName("ranges");
        w.WriteStartArray();
        foreach (var range in snippet.Ranges)
        {
            w.WriteStartObject();
            w.WriteNumber("start", range.Start);
            w.WriteNumber("end", range.End);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("truncated", snippet.Truncated);
        w.WriteEndObject();
    }

    /// <summary>单条命中的 <c>explain</c>（D.3.1）。</summary>
    public static void WriteExplain(Utf8JsonWriter w, SearchExplanation explanation)
    {
        w.WriteStartObject();
        w.WriteNumber("version", explanation.Version);
        w.WriteString("kind", explanation.Kind);
        w.WriteString("field", explanation.Field);
        w.WriteString("fieldId", explanation.FieldId);
        // S2 诊断：排序用的保守字段之外的逐列探测结果（S3 与 field 合并）。
        w.WriteString("resolvedField", explanation.ResolvedField);
        w.WriteString("resolvedFieldId", explanation.ResolvedFieldId);
        // T4.2：歌词命中的层 / 置信度（additive；非歌词命中 layer 为空串、confidence=none）。
        w.WriteString("layer", explanation.Layer);
        w.WriteString("confidence", explanation.Confidence);
        w.WriteNumber("matched", explanation.Matched);
        w.WriteNumber("total", explanation.Total);
        w.WriteNumber("coverage", explanation.Coverage);
        w.WriteString("exact", explanation.Exact);
        w.WriteNumber("proximity", explanation.Proximity);
        w.WriteBoolean("positioned", explanation.Positioned);
        w.WritePropertyName("bucket");
        w.WriteStartArray();
        foreach (var value in explanation.Bucket) w.WriteNumberValue(value);
        w.WriteEndArray();
        w.WriteNumber("bm25", explanation.Bm25);
        w.WritePropertyName("lexicalSources");
        w.WriteStartArray();
        foreach (var source in explanation.LexicalSources)
        {
            w.WriteStartObject();
            w.WriteString("step", source.Step);
            w.WriteString("kind", source.Kind);
            w.WriteString("field", source.Field);
            w.WriteNumber("matched", source.Matched);
            w.WriteBoolean("positioned", source.Positioned);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>执行轨迹（D.3.3；S2 只输出 steps / skipped）。</summary>
    public static void WriteTrace(Utf8JsonWriter w, SearchTrace trace)
    {
        w.WriteStartObject();
        w.WritePropertyName("steps");
        w.WriteStartArray();
        foreach (var step in trace.Steps)
        {
            w.WriteStartObject();
            w.WriteNumber("i", step.I);
            w.WriteString("step", step.Step);
            w.WriteString("kind", step.Kind);
            w.WriteString("fieldId", step.FieldId);
            w.WriteString("op", step.Op);
            w.WriteNumber("returned", step.Returned);
            w.WriteString("state", step.State);
            if (step.Reason is not null) w.WriteString("reason", step.Reason);
            w.WriteNumber("ms", Math.Round(step.Ms, 3));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("skipped");
        w.WriteStartArray();
        foreach (var skip in trace.Skipped)
        {
            w.WriteStartObject();
            w.WriteNumber("i", skip.I);
            w.WriteString("reason", skip.Reason);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        // T4.2（additive）：被层置信度过滤的步骤。旧字段 steps / skipped 语义不变。
        w.WritePropertyName("confidence");
        w.WriteStartArray();
        foreach (var filtered in trace.Confidence)
        {
            w.WriteStartObject();
            w.WriteNumber("i", filtered.I);
            w.WriteString("policy", filtered.Policy);
            w.WritePropertyName("layers");
            w.WriteStartArray();
            foreach (var layer in filtered.Layers) w.WriteStringValue(layer);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("truncated");
        w.WriteStartArray();
        w.WriteEndArray();
        w.WriteBoolean("deadlineHit", trace.DeadlineHit);
        w.WriteEndObject();
    }

    /// <summary>错误响应（D.4）：失败也是 JSON（不是 NULL），方便调用方记日志。</summary>
    public static string Error(SearchApiError error) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WritePropertyName("error");
        w.WriteStartObject();
        w.WriteNumber("code", error.Code);
        w.WriteString("message", error.Message);
        w.WriteBoolean("retryable", error.Retryable);
        w.WritePropertyName("detail");
        w.WriteStartObject();
        if (error.Reason is not null) w.WriteString("reason", error.Reason);
        if (error.Field is not null) w.WriteString("field", error.Field);
        if (error.Expected.HasValue) w.WriteNumber("expected", error.Expected.Value);
        if (error.Actual.HasValue) w.WriteNumber("actual", error.Actual.Value);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    });

    /// <summary>
    /// 健康检查（就绪）：不泄露绝对路径，只给调用方需要的版本与世代。
    /// T4.2 追加 <c>lyricsConfidence</c> 计数（高 / 中 / 低 / 无；按曲目取最高层置信度）。
    /// T4.3 追加 <c>dataPack</c>（数据包 id / 文件状态 / 降级能力）与顶层 <c>missing</c>
    /// （数据包降级导致的能力缺失，§5.5.6：不能静默少召回）。
    /// </summary>
    public static string HealthReady(
        StoreMeta meta, EraStore.LayerConfidenceCounts? confidence = null,
        DataPackHealth? dataPack = null) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WriteBoolean("ready", true);
        w.WriteNumber("gen", meta.AppliedGeneration);
        w.WriteNumber("schemaVersion", meta.SchemaVersion);
        w.WriteNumber("analyzerVersion", meta.AnalyzerVersion);
        // 构建选项（只加不删；旧调用方忽略未知键）：T3.4 V4 开关随之可见。
        w.WriteBoolean("initialsLyrics", meta.InitialsLyrics);
        w.WritePropertyName("lyricsConfidence");
        w.WriteStartObject();
        w.WriteNumber("high", confidence?.High ?? 0);
        w.WriteNumber("medium", confidence?.Medium ?? 0);
        w.WriteNumber("low", confidence?.Low ?? 0);
        w.WriteNumber("none", confidence?.None ?? 0);
        w.WriteEndObject();
        if (dataPack is not null) WriteDataPack(w, dataPack);
        w.WriteEndObject();
    });

    /// <summary>数据包诊断块（additive；键名固定，Dart 侧按 <c>dataPack.id</c> 比对）。</summary>
    private static void WriteDataPack(Utf8JsonWriter w, DataPackHealth dataPack)
    {
        w.WritePropertyName("dataPack");
        w.WriteStartObject();
        w.WriteString("id", dataPack.Id);
        w.WriteString("storedId", dataPack.StoredId);
        w.WriteBoolean("match", dataPack.Match);
        w.WriteBoolean("present", dataPack.Present);
        w.WriteBoolean("foundationOk", dataPack.FoundationOk);
        w.WriteStartArray("missing");
        foreach (var name in dataPack.Missing) w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("corrupt");
        foreach (var name in dataPack.Corrupt) w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("degradedCapabilities");
        foreach (var capability in dataPack.Degraded) w.WriteStringValue(capability);
        w.WriteEndArray();
        w.WriteEndObject();

        // 与 explain 的 `missing` 同名：设置页 / 健康页不用再理解「文件 → 能力」的映射。
        w.WriteStartArray("missing");
        foreach (var capability in dataPack.Degraded) w.WriteStringValue(capability);
        w.WriteEndArray();
    }

    /// <summary>健康检查（未就绪 / 版本不符）：保持 <c>api</c> + <c>ready</c> 的最小同构形状。</summary>
    public static string HealthNotReady(DataPackHealth? dataPack = null) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WriteBoolean("ready", false);
        if (dataPack is not null) WriteDataPack(w, dataPack);
        w.WriteEndObject();
    });
}
