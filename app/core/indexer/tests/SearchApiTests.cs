// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Archoera.Indexer.Ffi;

namespace Archoera.Indexer.Tests;

/// <summary>
/// C ABI v2（`era_search_*`）契约测试：请求解析（D.2）、响应 / 错误 JSON（D.3 / D.4）、
/// 句柄生命周期与错误码映射（D.1）。
///
/// <para>导出层以同源方式编入测试程序集，因此这里直接调 <see cref="IndexerFfi"/> 的
/// **托管入口**（<c>[UnmanagedCallersOnly]</c> 方法不能被托管代码调用，导出符号只是
/// 转发同名托管方法）。指针参数用 <c>fixed</c> 传入，输出一律经
/// <c>era_search_free</c>（<see cref="IndexerFfi.Free"/>）释放。</para>
///
/// <para>入口是 <see cref="Run"/>（由 <c>tests/Program.cs</c> 接线，本文件不改它）。</para>
/// </summary>
internal static class SearchApiTests
{
    public static void Run()
    {
        ApiVersionHandshake();
        RequestParsing();
        ScopeMapping();
        ResponseShape();
        ErrorShape();
        HandleLifecycle();
    }

    /* ------------------------------------------------------------------ */
    /* 版本握手                                                            */
    /* ------------------------------------------------------------------ */

    private static void ApiVersionHandshake()
        => Program.Check("ABI: era_search_api_version()==2", IndexerFfi.ApiVersion() == 2,
            IndexerFfi.ApiVersion().ToString());

    /* ------------------------------------------------------------------ */
    /* 请求解析（D.2）                                                     */
    /* ------------------------------------------------------------------ */

    private static void RequestParsing()
    {
        var (request, error) = Parse("""{"query":"爱"}""");
        Program.Check("请求: api 缺失 → 码 1", request is null && error?.Code == 1,
            error?.Message ?? "(no error)");
        Program.Check("请求: api 缺失 detail.reason==unsupported_api",
            error?.Reason == "unsupported_api" && error.Field == "api",
            error?.Reason ?? "(null)");

        (request, error) = Parse("""{"api":1,"query":"爱"}""");
        Program.Check("请求: api=1 → 码 1（不做 api:1 兼容）",
            request is null && error?.Code == 1 && error.Reason == "unsupported_api",
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"  晴天  "}""");
        Program.Check("请求: 最小合法请求（query 去空白）",
            error is null && request?.Request.Query == "晴天", error?.Message ?? "(no error)");
        Program.Check("请求: 缺省 limit==20 / offset==0",
            request?.Request.Limit == 20 && request.Request.Offset == 0,
            $"{request?.Request.Limit}/{request?.Request.Offset}");
        Program.Check("请求: S1 默认 Fuzzy=true / MinExact=1",
            request?.Request.Fuzzy == true && request.Request.MinExact == 1,
            $"{request?.Request.Fuzzy}/{request?.Request.MinExact}");
        Program.Check("请求: CandidateCap 取默认（S1 不暴露）",
            request?.Request.CandidateCap == FtsStore.DefaultCandidateCap,
            request?.Request.CandidateCap.ToString());
        Program.Check("请求: scheme 缺省 = s1-compat@1",
            request?.SchemeRequested == SearchPolicy.SchemeId && request.Scheme == SearchPolicy.SchemeId,
            request?.SchemeRequested ?? "(null)");
        Program.Check("请求: 未给 filter 时 FilterTrackIds==null", request?.FilterTrackIds is null);

        (request, error) = Parse("""{"api":2,"query":"爱","scope":"lyric"}""");
        Program.Check("请求: 非法 scope → 码 1",
            request is null && error?.Code == 1 && error.Field == "scope",
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","limit":0}""");
        Program.Check("请求: limit=0 → 20", request?.Request.Limit == 20, request?.Request.Limit.ToString());

        (request, error) = Parse("""{"api":2,"query":"爱","limit":20000}""");
        Program.Check("请求: limit=20000 → 10000 + clamped",
            request?.Request.Limit == SearchApiRequest.MaxLimit && request.Clamped == true,
            $"{request?.Request.Limit}/{request?.Clamped}");

        (request, error) = Parse("""{"api":2,"query":"爱","limit":50}""");
        Program.Check("请求: limit=50 不置 clamped", request?.Clamped == false && request.Request.Limit == 50);

        // 超长查询按**码点**截断（不是 UTF-16 code unit）。
        var longCjk = new string('爱', SearchApiRequest.MaxQueryCodePoints + 50);
        (request, error) = Parse($$"""{"api":2,"query":"{{longCjk}}"}""");
        Program.Check("请求: 250 字截断到 200 码点 + queryTruncated",
            request?.Request.Query.Length == SearchApiRequest.MaxQueryCodePoints && request.QueryTruncated,
            $"{request?.Request.Query.Length}/{request?.QueryTruncated}");

        var emoji = string.Concat(Enumerable.Repeat("🎵", SearchApiRequest.MaxQueryCodePoints + 50));
        (request, error) = Parse($$"""{"api":2,"query":"{{emoji}}"}""");
        Program.Check("请求: 截断不切代理对（250 emoji → 200 码点 / 400 code unit）",
            request?.Request.Query.Length == SearchApiRequest.MaxQueryCodePoints * 2
            && !char.IsHighSurrogate(request.Request.Query[^1]),
            $"{request?.Request.Query.Length}");

        (request, error) = Parse("""{"api":2,"query":"爱","limit":0,"offset":5}""");
        Program.Check("请求: 合法 offset 透传", request?.Request.Offset == 5, request?.Request.Offset.ToString());
        (request, error) = Parse("""{"api":2,"query":"爱","offset":-1}""");
        Program.Check("请求: 负 offset → 码 1", request is null && error?.Code == 1, error?.Message);

        // 旧字段：5 个逐个拒绝（含值为 null 也算「出现」）。
        foreach (var field in new[] { "minExact", "candidateCap", "fuzzy", "ranking", "engine" })
        {
            (request, error) = Parse($$"""{"api":2,"query":"爱","{{field}}":null}""");
            var rejected = request is null && error is not null
                && error.Code == 1 && error.Reason == "deprecated_field" && error.Field == field;
            var detail = Root(SearchApiJson.Error(error ?? SearchApiError.Internal("(missing)")))
                .GetProperty("error").GetProperty("detail");
            Program.Check($"请求: 旧字段 {field} → 码 1 + detail.field",
                rejected && Str(detail, "reason") == "deprecated_field" && Str(detail, "field") == field,
                error?.Message ?? "(no error)");
        }

        // filter.trackIds：≤50k、每项 32 hex。
        const string validId = "0123456789abcdef0123456789abcdef";
        (request, error) = Parse(
            "{\"api\":2,\"query\":\"爱\",\"filter\":{\"trackIds\":[\"" + validId + "\"]}}");
        Program.Check("请求: 合法 filter.trackIds 透传",
            error is null && request?.FilterTrackIds is { Count: 1 } ids && ids[0] == validId,
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","filter":{"trackIds":["zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"]}}""");
        Program.Check("请求: filter 非 32hex → 码 1",
            request is null && error?.Code == 1 && error.Reason == "invalid_filter",
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","filter":{"trackIds":["abc"]}}""");
        Program.Check("请求: filter 长度不足 32 → 码 1", request is null && error?.Code == 1);

        // 手工拼 50001 项（每项 32 hex）：只测上限拒绝，不构造 50k 个不同 id。
        var oversized = new StringBuilder("""{"api":2,"query":"爱","filter":{"trackIds":""");
        oversized.Append('[');
        for (var i = 0; i <= SearchApiRequest.MaxFilterTrackIds; i++)
        {
            if (i > 0) oversized.Append(',');
            oversized.Append('"').Append(validId).Append('"');
        }
        oversized.Append("]}}");
        (request, error) = Parse(oversized.ToString());
        Program.Check("请求: filter 超过 50k → 码 1",
            request is null && error?.Code == 1 && error.Reason == "invalid_filter",
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","cursor":"abc"}""");
        Program.Check("请求: 非空 cursor → 码 1",
            request is null && error?.Code == 1 && error.Field == "cursor", error?.Message);

        (request, error) = Parse("""{"api":2,"query":"爱","cursor":null}""");
        Program.Check("请求: cursor=null 允许", error is null && request is not null, error?.Message);

        (request, error) = Parse("""{"api":2,"query":"爱","total":"capped"}""");
        Program.Check("请求: total=capped → 码 1",
            request is null && error?.Code == 1 && error.Field == "total", error?.Message);

        (request, error) = Parse("""{"api":2,"query":"爱","total":"none"}""");
        Program.Check("请求: total=none 允许", error is null && request is not null, error?.Message);

        (request, error) = Parse("""{"api":2,"query":"爱","scheme":"bogus@9"}""");
        Program.Check("请求: 未知 scheme 原样回显、执行仍用 s1-compat@1",
            request?.SchemeRequested == "bogus@9" && request.Scheme == SearchPolicy.SchemeId,
            request?.SchemeRequested ?? "(null)");

        (request, error) = Parse("""{"api":2,"query":"爱","explain":true,"highlight":true,"snippet":true}""");
        Program.Check("请求: explain/highlight/snippet 接受但 S1 不产出",
            error is null && request is { Explain: true, Highlight: true, Snippet: true },
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","unknownField":1}""");
        Program.Check("请求: 未知键忽略", error is null && request is not null, error?.Message);

        (request, error) = Parse("not json");
        Program.Check("请求: 非法 JSON → 码 1",
            request is null && error?.Code == 1 && error.Reason == "invalid_json", error?.Message);

        (request, error) = Parse("[1,2]");
        Program.Check("请求: 非对象 → 码 1", request is null && error?.Code == 1, error?.Message);
    }

    /* ------------------------------------------------------------------ */
    /* scope 映射                                                          */
    /* ------------------------------------------------------------------ */

    private static void ScopeMapping()
    {
        var (request, error) = Parse("""{"api":2,"query":"爱","scope":"lyrics"}""");
        Program.Check("scope: lyrics → Lyrics=true / Metadata=false",
            error is null && request?.Request.Lyrics == true && request.Request.Metadata == false,
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","scope":"metadata"}""");
        Program.Check("scope: metadata → Metadata=true / Lyrics=false",
            error is null && request?.Request.Metadata == true && request.Request.Lyrics == false,
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱","scope":"all"}""");
        Program.Check("scope: all → 两者皆 false",
            error is null && request?.Request.Metadata == false && request.Request.Lyrics == false,
            error?.Message ?? "(no error)");

        (request, error) = Parse("""{"api":2,"query":"爱"}""");
        Program.Check("scope: 缺省等同 all",
            error is null && request?.Request.Metadata == false && request.Request.Lyrics == false,
            error?.Message ?? "(no error)");
    }

    /* ------------------------------------------------------------------ */
    /* 响应 / 错误 JSON（D.3 / D.4）                                        */
    /* ------------------------------------------------------------------ */

    private static void ResponseShape()
    {
        var result = new SearchResult();
        result.Items.Add(new SearchHit("0123456789abcdef0123456789abcdef", -1.2, SearchPassNames.Lexical));
        result.Items.Add(new SearchHit("fedcba9876543210fedcba9876543210", -0.5, SearchPassNames.PinyinLoose));
        result.Passes.DropTokens = 1;
        result.Passes.PinyinLoose = 1;

        var json = SearchApiJson.Success(
            result.Items, result.Passes, gen: 12, SearchPolicy.SchemeId, "default@1",
            clamped: false, queryTruncated: true);
        var root = Root(json);

        Program.Check("响应: api==2", Int(root, "api") == 2, json);
        Program.Check("响应: gen 来自句柄 meta", Int(root, "gen") == 12, json);
        Program.Check("响应: scheme / schemeRequested",
            Str(root, "scheme") == SearchPolicy.SchemeId && Str(root, "schemeRequested") == "default@1", json);

        var items = root.GetProperty("items");
        Program.Check("响应: items 条数与顺序", items.GetArrayLength() == 2
            && Str(items[0], "trackId") == "0123456789abcdef0123456789abcdef"
            && Str(items[1], "trackId") == "fedcba9876543210fedcba9876543210", json);
        Program.Check("响应: item.score 原样浮点",
            items[0].GetProperty("score").GetDouble() == -1.2
            && items[1].GetProperty("score").GetDouble() == -0.5, json);
        Program.Check("响应: item.pass 保留归因",
            Str(items[0], "pass") == SearchPassNames.Lexical
            && Str(items[1], "pass") == SearchPassNames.PinyinLoose, json);

        var passes = root.GetProperty("passes");
        Program.Check("响应: passes 六个过渡诊断计数",
            Int(passes, "dropTokens") == 1 && Int(passes, "pinyinLoose") == 1
            && Int(passes, "reading") == 0 && Int(passes, "crossLingual") == 0
            && Int(passes, "fuzzy") == 0 && Int(passes, "cjkFuzzy") == 0, json);

        Program.Check("响应: total=-1 / totalExact=false",
            Int(root, "total") == -1 && Bool(root, "totalExact") == false, json);
        Program.Check("响应: partial / clamped / queryTruncated",
            Bool(root, "partial") == false && Bool(root, "clamped") == false
            && Bool(root, "queryTruncated") == true, json);
        Program.Check("响应: restart=false / next=null / trace=null",
            Bool(root, "restart") == false
            && root.GetProperty("next").ValueKind == JsonValueKind.Null
            && root.GetProperty("trace").ValueKind == JsonValueKind.Null, json);
    }

    private static void ErrorShape()
    {
        var json = SearchApiJson.Error(SearchApiError.Deprecated("minExact"));
        var root = Root(json);
        var error = root.GetProperty("error");

        Program.Check("错误: api==2 + code==1", Int(root, "api") == 2 && Int(error, "code") == 1, json);
        Program.Check("错误: message 非空", Str(error, "message").Length > 0, json);
        Program.Check("错误: code 1 不可重试", Bool(error, "retryable") == false, json);

        var detail = error.GetProperty("detail");
        Program.Check("错误: detail.reason + detail.field",
            Str(detail, "reason") == "deprecated_field" && Str(detail, "field") == "minExact", json);

        // D.4 错误码 → retryable 映射（2/4/6/8 为 true）。
        var expected = new (int Code, bool Retryable)[]
        {
            (SearchApiCodes.InvalidRequest, false),
            (SearchApiCodes.NotReady, true),
            (SearchApiCodes.VersionMismatch, false),
            (SearchApiCodes.Busy, true),
            (SearchApiCodes.Io, false),
            (SearchApiCodes.OutOfMemory, true),
            (SearchApiCodes.Canceled, false),
            (SearchApiCodes.Internal, true),
        };
        var mappingOk = true;
        var detailText = new StringBuilder();
        foreach (var (code, retryable) in expected)
        {
            var candidate = new SearchApiError { Code = code, Message = "x", Reason = "r" };
            var body = Root(SearchApiJson.Error(candidate)).GetProperty("error");
            var ok = Int(body, "code") == code && Bool(body, "retryable") == retryable;
            mappingOk &= ok;
            if (!ok) detailText.Append(code).Append(' ');
        }
        Program.Check("错误: 错误码 → retryable 映射（1/2/3/4/5/6/7/8）", mappingOk, detailText.ToString());

        // 错误 JSON 里 detail 不夹带路径 / 用户输入原文。
        Program.Check("错误: detail 不含路径",
            !json.Contains(Path.GetTempPath(), StringComparison.Ordinal), json);
    }

    /* ------------------------------------------------------------------ */
    /* 句柄生命周期 + 真实小库                                             */
    /* ------------------------------------------------------------------ */

    private static unsafe void HandleLifecycle()
    {
        const string idA = "0123456789abcdef0123456789abcdef";
        const string idB = "fedcba9876543210fedcba9876543210";

        var root = Path.Combine(
            Path.GetTempPath(), "archoera-search-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");

            EndToEnd.CreateLibrary(db, generation: 7);
            using (var conn = EndToEnd.Open(db))
            {
                // 单字查询会被限定在可见文本列（SearchQuery.RequireMetadataForSingleChar），
                // 故两首曲目的**标题**都含「爱」，保证 `query=爱` 有两条命中。
                EndToEnd.InsertTrack(conn, idA, Path.Combine(root, "a.mp3"), "爱你",
                    """[{"name":"周杰伦"}]""", null, "我爱你中国", 1);
                EndToEnd.InsertTrack(conn, idB, Path.Combine(root, "b.mp3"), "爱情",
                    """[{"name":"周杰伦"}]""", null, "为爱飞行", 2);
            }
            EndToEnd.RunEngine(db, index, full: true, lrc: false);

            // ---- 未就绪 / 参数错误 ----
            var code = Open(Path.Combine(root, "missing.db"), out var handle);
            Program.Check("句柄: 缺失索引 Open→2 且 out=NULL", code == 2 && handle == null, code.ToString());

            var badOptions = Open(index, out var badHandle, "{not json");
            Program.Check("句柄: 非法 options_json → 1 且 out=NULL",
                badOptions == 1 && badHandle == null, badOptions.ToString());

            var unknownOptions = Open(index, out var unknownHandle,
                """{"scheme":"x","cache_capacity":0,"whatever":1}""");
            Program.Check("句柄: 未知 options 键忽略 → 0",
                unknownOptions == 0 && unknownHandle != null, unknownOptions.ToString());
            IndexerFfi.Close(unknownHandle);

            var nullOptions = Open(index, out var nullOptionsHandle, "null");
            Program.Check("句柄: options_json=null 视为缺省 → 0",
                nullOptions == 0 && nullOptionsHandle != null, nullOptions.ToString());
            IndexerFfi.Close(nullOptionsHandle);

            // ---- 健康检查 ----
            var (missingHealthCode, missingHealthJson) = Health(Path.Combine(root, "missing.db"));
            var missingHealth = Root(missingHealthJson);
            Program.Check("健康: 缺失索引 → 码 2 + ready=false",
                missingHealthCode == 2 && Int(missingHealth, "api") == 2 && Bool(missingHealth, "ready") == false,
                missingHealthJson);

            var (healthCode, healthJson) = Health(index);
            var health = Root(healthJson);
            Program.Check("健康: 就绪 → api/gen/schemaVersion/analyzerVersion",
                healthCode == 0 && Int(health, "api") == 2 && Bool(health, "ready")
                && Int(health, "gen") == 7 && Int(health, "schemaVersion") == FtsStore.SchemaVersionValue
                && Int(health, "analyzerVersion") == Analyzer.AnalyzerVersion, healthJson);
            Program.Check("健康: 不泄露绝对路径",
                !healthJson.Contains(root, StringComparison.Ordinal), healthJson);

            // ---- 打开 + 查询 ----
            var openCode = Open(index, out var opened);
            Program.Check("句柄: Open→0 且 out 非空", openCode == 0 && opened != null, openCode.ToString());

            var (runCode, runJson) = RunSearch(opened, """{"api":2,"query":"爱","scope":"all","limit":10}""");
            var run = Root(runJson);
            var items = run.GetProperty("items");
            Program.Check("查询: Run→0", runCode == 0, runJson);
            Program.Check("查询: 「爱」命中 2 条", items.GetArrayLength() == 2, runJson);
            Program.Check("查询: api / gen / scheme 来自句柄",
                Int(run, "api") == 2 && Int(run, "gen") == 7
                && Str(run, "scheme") == SearchPolicy.SchemeId
                && Str(run, "schemeRequested") == SearchPolicy.SchemeId, runJson);
            Program.Check("查询: 命中 trackId 与顺序无关地属于集合",
                items.EnumerateArray().All(item => Str(item, "trackId") is idA or idB), runJson);
            Program.Check("查询: item 带 score / pass",
                items[0].GetProperty("score").ValueKind == JsonValueKind.Number
                && Str(items[0], "pass").Length > 0, runJson);
            Program.Check("查询: passes 是 S1 过渡字段",
                run.GetProperty("passes").TryGetProperty("dropTokens", out _)
                && run.GetProperty("passes").TryGetProperty("cjkFuzzy", out _), runJson);

            // ---- filter.trackIds 后过滤 ----
            var (filterCode, filterJson) = RunSearch(opened,
                "{\"api\":2,\"query\":\"爱\",\"filter\":{\"trackIds\":[\"" + idA + "\"]}}");
            var filterItems = Root(filterJson).GetProperty("items");
            Program.Check("查询: filter 只留命中集合内的 trackId",
                filterCode == 0 && filterItems.GetArrayLength() == 1
                && Str(filterItems[0], "trackId") == idA, filterJson);

            var (emptyFilterCode, emptyFilterJson) = RunSearch(opened,
                """{"api":2,"query":"爱","filter":{"trackIds":[]}}""");
            Program.Check("查询: 空 filter 集合 → 零命中（不置 partial）",
                emptyFilterCode == 0
                && Root(emptyFilterJson).GetProperty("items").GetArrayLength() == 0
                && Bool(Root(emptyFilterJson), "partial") == false, emptyFilterJson);

            // ---- 失败路径：旧字段 / 空句柄 ----
            var (legacyCode, legacyJson) = RunSearch(opened, """{"api":2,"query":"爱","minExact":1}""");
            var legacyDetail = Root(legacyJson).GetProperty("error").GetProperty("detail");
            Program.Check("查询: 旧字段 → 码 1 且 out_json 非 NULL（错误 JSON）",
                legacyCode == 1 && legacyJson.Length > 0
                && Str(legacyDetail, "reason") == "deprecated_field"
                && Str(legacyDetail, "field") == "minExact", legacyJson);

            var (nullCode, nullJson) = RunSearch(null, """{"api":2,"query":"爱"}""");
            Program.Check("查询: 空句柄 → 码 1 + 错误 JSON",
                nullCode == 1 && Root(nullJson).GetProperty("error").GetProperty("detail")
                    .GetProperty("reason").GetString() == "invalid_handle", nullJson);

            var (emptyRequestCode, emptyRequestJson) = RunSearch(opened, "");
            Program.Check("查询: 空 request_json → 码 1",
                emptyRequestCode == 1 && emptyRequestJson.Length > 0, emptyRequestJson);

            var (emptyQueryCode, emptyQueryJson) = RunSearch(opened, """{"api":2,"query":"   "}""");
            Program.Check("查询: 空 query 返回空结果（不报错）",
                emptyQueryCode == 0
                && Root(emptyQueryJson).GetProperty("items").GetArrayLength() == 0, emptyQueryJson);

            var (deadIndexCode, _) = RunSearch(opened, """{"api":2,"query":"爱","scope":"lyrics"}""");
            Program.Check("查询: scope=lyrics 可执行", deadIndexCode == 0, deadIndexCode.ToString());

            // ---- close：幂等 / 空句柄安全 ----
            IndexerFfi.Close(opened);
            IndexerFfi.Close(opened);
            IndexerFfi.Close(null);
            Program.Check("句柄: close 幂等 / 空句柄安全", true);

            var (closedCode, closedJson) = RunSearch(opened, """{"api":2,"query":"爱"}""");
            Program.Check("查询: 关闭后的句柄 → 码 1",
                closedCode == 1 && closedJson.Length > 0
                && Root(closedJson).GetProperty("error").GetProperty("detail")
                    .GetProperty("reason").GetString() == "invalid_handle", closedJson);

            // ---- schema 版本不符 → Open 3 ----
            SearchSession.CloseCached();
            using (var conn = EndToEnd.Open(index))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE search_meta SET value='999' WHERE key='schema_version'";
                cmd.ExecuteNonQuery();
            }
            var schemaCode = Open(index, out var schemaHandle);
            Program.Check("句柄: schema=999 → Open→3 且 out=NULL",
                schemaCode == 3 && schemaHandle == null, schemaCode.ToString());

            var (badHealthCode, badHealthJson) = Health(index);
            Program.Check("健康: schema=999 → 码 3 + ready=false",
                badHealthCode == 3 && Bool(Root(badHealthJson), "ready") == false, badHealthJson);

            using (var conn = EndToEnd.Open(index))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE search_meta SET value='7' WHERE key='schema_version'";
                cmd.ExecuteNonQuery();
            }
            var restoredCode = Open(index, out var restoredHandle);
            Program.Check("句柄: schema 还原 7 后 Open→0", restoredCode == 0 && restoredHandle != null,
                restoredCode.ToString());
            IndexerFfi.Close(restoredHandle);
        }
        finally
        {
            SearchSession.CloseCached();
            EndToEnd.TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* FFI 调用辅助（fixed 传指针；输出统一经 era_search_free 释放）        */
    /* ------------------------------------------------------------------ */

    private static (SearchApiRequest? Request, SearchApiError? Error) Parse(string json)
        => SearchApiRequest.Parse(json);

    private static byte[] Utf8Z(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static unsafe string Consume(byte* ptr, int length)
    {
        if (ptr == null) return "";
        var text = Encoding.UTF8.GetString(ptr, length);
        IndexerFfi.Free(ptr);
        return text;
    }

    private static unsafe int Open(string indexPath, out void* handle, string? optionsJson = null)
    {
        void* local = null;
        int code;
        fixed (byte* path = Utf8Z(indexPath))
        {
            if (optionsJson is null)
            {
                code = IndexerFfi.Open(path, null, &local);
            }
            else
            {
                fixed (byte* options = Utf8Z(optionsJson))
                {
                    code = IndexerFfi.Open(path, options, &local);
                }
            }
        }
        handle = local;
        return code;
    }

    private static unsafe (int Code, string Json) RunSearch(void* handle, string requestJson)
    {
        fixed (byte* request = Utf8Z(requestJson))
        {
            byte* outJson = null;
            var outLen = 0;
            var code = IndexerFfi.Run(handle, request, &outJson, &outLen);
            return (code, Consume(outJson, outLen));
        }
    }

    private static unsafe (int Code, string Json) Health(string indexPath)
    {
        fixed (byte* path = Utf8Z(indexPath))
        {
            byte* outJson = null;
            var outLen = 0;
            var code = IndexerFfi.Health(path, &outJson, &outLen);
            return (code, Consume(outJson, outLen));
        }
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private static long Int(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : long.MinValue;

    private static bool Bool(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();

    private static string Str(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
