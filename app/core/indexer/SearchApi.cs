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
    public SearchApiHandle(string indexPath, StoreMeta meta)
    {
        IndexPath = indexPath;
        Meta = meta;
    }

    /// <summary>索引文件的绝对路径（<see cref="SearchSession"/> 按此复用会话）。</summary>
    public string IndexPath { get; }

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

    /// <summary>解析后保留但 S1 不产出对应字段（T2.7 explain / T2.8 snippet）。</summary>
    public bool Explain { get; init; }

    /// <summary>解析后保留但 S1 不产出对应字段（T2.7）。</summary>
    public bool Highlight { get; init; }

    /// <summary>解析后保留但 S1 不产出对应字段（T2.8）。</summary>
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

            // total：只接受缺省 / null / "none"（S1 只做 none）。
            if (root.TryGetProperty("total", out var totalElement)
                && totalElement.ValueKind != JsonValueKind.Null
                && !(totalElement.ValueKind == JsonValueKind.String
                     && string.Equals(totalElement.GetString(), "none", StringComparison.OrdinalIgnoreCase)))
            {
                return (null, SearchApiError.Invalid(
                    "total 在 S1 只支持 none（缺省）", "unsupported_total", "total"));
            }

            // cursor：S1 没有分页游标，只接受缺省 / null。
            if (root.TryGetProperty("cursor", out var cursorElement)
                && cursorElement.ValueKind != JsonValueKind.Null)
            {
                return (null, SearchApiError.Invalid(
                    "cursor 在 S1 尚未实现（只接受 null）", "unsupported_cursor", "cursor"));
            }

            // explain / highlight / snippet：接受但 S1 不产出（T2.7 / T2.8）。
            if (!TryReadBool(root, "explain", out var explain, out var boolError)
                || !TryReadBool(root, "highlight", out var highlight, out boolError)
                || !TryReadBool(root, "snippet", out var snippet, out boolError))
            {
                return (null, boolError);
            }

            // scheme：未知值原样回显（S1 恒用 SearchPolicy.SchemeId 执行）。
            var schemeRequested = SearchPolicy.SchemeId;
            if (root.TryGetProperty("scheme", out var schemeElement))
            {
                if (schemeElement.ValueKind != JsonValueKind.String)
                    return (null, SearchApiError.Invalid("scheme 必须是字符串", "invalid_type", "scheme"));
                schemeRequested = schemeElement.GetString() ?? "";
            }

            var (filterIds, filterError) = ParseFilter(root);
            if (filterError is not null) return (null, filterError);

            var request = new SearchRequest
            {
                Query = query,
                Metadata = metadata,
                Lyrics = lyrics,
                Limit = limit,
                Offset = offset,
                // S1 默认启用兜底趟（等价 default@1）：v2 不再暴露 fuzzy / minExact 开关。
                Fuzzy = true,
                MinExact = 1,
                CandidateCap = FtsStore.DefaultCandidateCap,
            };

            return (new SearchApiRequest
            {
                Request = request,
                Scheme = SearchPolicy.SchemeId,
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
    private static bool IsHex32(string value)
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
    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        body(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 成功响应（D.3 的 S1 子集）。
    ///
    /// <para><b>过渡字段 <c>passes</c></b>：S1 仍输出（Dart 查询日志在读它）；
    /// S2.5 改成 <c>explain</c> / <c>trace</c> 后删除。同理 <c>score</c> / <c>pass</c>
    /// 只作诊断与归因，顺序才是契约。</para>
    ///
    /// <para>S1 不产出 <c>explain</c> / <c>highlight</c> / <c>snippet</c>（T2.7 / T2.8）、
    /// 不分页（<c>next</c> 恒 null / <c>restart</c> 恒 false）、<c>total</c> 恒 -1。</para>
    /// </summary>
    public static string Success(
        IReadOnlyList<SearchHit> items,
        PassCounts passes,
        long gen,
        string scheme,
        string schemeRequested,
        bool clamped,
        bool queryTruncated) => Write(w =>
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

        // S1 只实现 total=none / 不分页。
        w.WriteNumber("total", -1);
        w.WriteBoolean("totalExact", false);
        w.WriteBoolean("partial", false);
        w.WriteBoolean("clamped", clamped);
        w.WriteBoolean("queryTruncated", queryTruncated);
        w.WriteBoolean("restart", false);
        w.WriteNull("next");
        w.WriteNull("trace");
        w.WriteEndObject();
    });

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

    /// <summary>健康检查（就绪）：不泄露绝对路径，只给调用方需要的版本与世代。</summary>
    public static string HealthReady(StoreMeta meta) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WriteBoolean("ready", true);
        w.WriteNumber("gen", meta.AppliedGeneration);
        w.WriteNumber("schemaVersion", meta.SchemaVersion);
        w.WriteNumber("analyzerVersion", meta.AnalyzerVersion);
        w.WriteEndObject();
    });

    /// <summary>健康检查（未就绪 / 版本不符）：保持 <c>api</c> + <c>ready</c> 的最小同构形状。</summary>
    public static string HealthNotReady() => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("api", SearchApiRequest.ApiVersion);
        w.WriteBoolean("ready", false);
        w.WriteEndObject();
    });
}
