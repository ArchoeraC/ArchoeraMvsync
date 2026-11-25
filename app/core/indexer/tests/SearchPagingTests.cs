// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T2.10 分页 + total：32B 游标编解码、翻页无重复无遗漏、与一次取全顺序一致、
/// 世代 / 方案失配触发 restart、<c>total=none|capped|exact</c> 三模式与缓存键。
/// </summary>
internal static class SearchPagingTests
{
    private const string Query = "晴天";
    private const int Tracks = 30;

    public static void Run()
    {
        CursorCodec();

        var root = Path.Combine(
            Path.GetTempPath(), "archoera-search-paging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            EndToEnd.CreateLibrary(db, generation: 1);
            using (var conn = EndToEnd.Open(db))
            {
                for (var i = 0; i < Tracks; i++)
                {
                    var id = "p" + (i + 1).ToString("000");
                    EndToEnd.InsertTrack(
                        conn, id, Path.Combine(root, id + ".mp3"), "晴天 " + (i + 1).ToString("00"),
                        """[{"name":"合成"}]""", null, null, 1000 + i);
                }
            }
            EndToEnd.RunEngine(db, index, full: true, lrc: false);

            Pages(db, index);
            TotalModes(db, index);
            RestartOnGeneration(db, index);
            RestartOnScheme(db, index);
            CacheKeySeparatesCursor(db, index);
            ApiShape();
        }
        finally
        {
            SearchSession.CloseCached();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言。
            }
        }
    }

    /* ------------------------------------------------------------------ */
    /* 游标编解码                                                          */
    /* ------------------------------------------------------------------ */

    private static void CursorCodec()
    {
        var candidate = new Candidate(42, "track", new Evidence(
            MatchKind.Exact, FieldKind.Title, 0.5, 1, true), -3.25, SearchPassNames.Lexical);
        var hash = SearchPaging.SchemeHash("default@1");
        var encoded = SearchPaging.Encode(candidate, gen: 7, hash);

        Program.Check("分页: 游标是不补位 base64url（43 字符）",
            encoded.Length == 43 && encoded.All(c =>
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c == '-' || c == '_'),
            encoded);

        var ok = SearchPaging.TryDecode(encoded, out var decoded);
        Program.Check("分页: 游标往返（gen / scheme / docId / bm25）",
            ok && decoded.Gen == 7 && decoded.SchemeHash == hash && decoded.DocId == 42
            && Math.Abs(decoded.Bm25 - -3.25) < 0.0001 && decoded.KindRank == (int)MatchKind.Exact,
            $"{ok} gen={decoded.Gen} doc={decoded.DocId}");

        // T3.11：proximity 槽（D.5 的 int16 预留位）承载真实值；算不出写 -1，解码还原哨兵。
        var near = new Candidate(9, "near", new Evidence(
            MatchKind.Phrase, FieldKind.Title, 1.0, 1, true, 2), -1, "test");
        var far = new Candidate(9, "far", new Evidence(
            MatchKind.Phrase, FieldKind.Title, 1.0, 1, true,
            SearchRanker.ProximityUnknown), -1, "test");
        SearchPaging.TryDecode(SearchPaging.Encode(near, gen: 1, hash), out var nearCursor);
        SearchPaging.TryDecode(SearchPaging.Encode(far, gen: 1, hash), out var farCursor);
        Program.Check("分页: 游标 proximity 往返（真实值）", nearCursor.Proximity == 2,
            nearCursor.Proximity.ToString());
        Program.Check("分页: 游标 proximity 往返（算不出 → -1 → 哨兵）",
            farCursor.Proximity == SearchRanker.ProximityUnknown,
            farCursor.Proximity.ToString());

        Program.Check("分页: 非法 base64 被拒", !SearchPaging.TryDecode("***", out _), "***");
        Program.Check("分页: 长度不是 32 字节被拒",
            !SearchPaging.TryDecode(Convert.ToBase64String(new byte[31]), out _), "31B");
        var wrongVersion = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=");
        wrongVersion[0] = 2;
        Program.Check("分页: 版本不是 1 被拒",
            !SearchPaging.TryDecode(Base64Url(wrongVersion), out _), "v2");
        Program.Check("分页: 超长游标被拒",
            !SearchPaging.TryDecode(new string('A', SearchPaging.MaxCursorChars + 1), out _), "long");
    }

    /* ------------------------------------------------------------------ */
    /* 翻页连续性                                                          */
    /* ------------------------------------------------------------------ */

    private static void Pages(string db, string index)
    {
        var full = SearchSession.Run(index, Request("", SearchTotalMode.Capped, limit: 100), db);
        Program.Check("分页: 一次取全 30 条", full.Items.Count == Tracks,
            full.Items.Count.ToString());

        var seen = new List<string>();
        string? cursorRaw = null;
        var pages = 0;
        while (true)
        {
            var page = SearchSession.Run(
                index, Request(cursorRaw, SearchTotalMode.Capped, limit: 10), db);
            pages++;
            Program.Check($"分页: 第 {pages} 页 restart=false / totalExact=false",
                !page.Restart && !page.TotalExact, $"restart={page.Restart}");
            Program.Check($"分页: 第 {pages} 页 total=30", page.Total == Tracks, page.Total.ToString());
            seen.AddRange(page.Items.Select(hit => hit.TrackId));
            if (page.Next is null) break;
            cursorRaw = page.Next;
            if (pages > 10) break;
        }

        Program.Check("分页: 三页取完（10+10+10）", pages == 3, pages.ToString());
        Program.Check("分页: 两页之间无重复", seen.Distinct(StringComparer.Ordinal).Count() == seen.Count,
            seen.Count.ToString());
        Program.Check("分页: 无遗漏且顺序与一次取全一致",
            seen.SequenceEqual(full.Items.Select(hit => hit.TrackId), StringComparer.Ordinal),
            $"{seen.Count}/{full.Items.Count}");
    }

    /* ------------------------------------------------------------------ */
    /* total 三模式                                                        */
    /* ------------------------------------------------------------------ */

    private static void TotalModes(string db, string index)
    {
        var none = SearchSession.Run(index, Request(null, SearchTotalMode.None, limit: 10), db);
        Program.Check("分页: total=none → -1", none.Total == -1 && !none.TotalExact, none.Total.ToString());

        var capped = SearchSession.Run(index, Request(null, SearchTotalMode.Capped, limit: 10), db);
        Program.Check("分页: total=capped → 30 且 totalExact=false",
            capped.Total == Tracks && !capped.TotalExact, capped.Total.ToString());

        var exact = SearchSession.Run(index, Request(null, SearchTotalMode.Exact, limit: 10), db);
        Program.Check("分页: total=exact → 30 且 totalExact=true",
            exact.Total == Tracks && exact.TotalExact, exact.Total.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 游标失效                                                            */
    /* ------------------------------------------------------------------ */

    private static void RestartOnGeneration(string db, string index)
    {
        var first = SearchSession.Run(index, Request(null, SearchTotalMode.Capped, limit: 10), db);
        Program.Check("分页: 第一页有 next", first.Next is not null, "next=null");

        var stale = Rebake(first.Next!, gen: 9999, schemeHash: null);
        var page = SearchSession.Run(index, Request(stale, SearchTotalMode.Capped, limit: 10), db);
        Program.Check("分页: 世代不符 → restart / items=[] / next=null",
            page.Restart && page.Items.Count == 0 && page.Next is null,
            $"restart={page.Restart} items={page.Items.Count} next={page.Next}");
    }

    private static void RestartOnScheme(string db, string index)
    {
        var first = SearchSession.Run(index, Request(null, SearchTotalMode.Capped, limit: 10), db);
        var stale = Rebake(first.Next!, gen: null, schemeHash: 123456u);
        var page = SearchSession.Run(index, Request(stale, SearchTotalMode.Capped, limit: 10), db);
        Program.Check("分页: 方案不符 → restart / items=[]",
            page.Restart && page.Items.Count == 0,
            $"restart={page.Restart} items={page.Items.Count}");
    }

    /* ------------------------------------------------------------------ */
    /* 缓存键                                                              */
    /* ------------------------------------------------------------------ */

    private static void CacheKeySeparatesCursor(string db, string index)
    {
        SearchSession.CloseCached();
        SearchSession.DebugResetCacheStats();
        var first = SearchSession.Run(index, Request(null, SearchTotalMode.Capped, limit: 10), db);
        SearchSession.Run(index, Request(null, SearchTotalMode.Capped, limit: 10), db);
        var missesAfterHit = SearchSession.DebugCacheMisses;
        SearchSession.Run(index, Request(first.Next, SearchTotalMode.Capped, limit: 10), db);
        Program.Check("分页: 缓存键区分 cursor（第一页命中、第二页未命中）",
            missesAfterHit == 1 && SearchSession.DebugCacheMisses == 2,
            $"{missesAfterHit} → {SearchSession.DebugCacheMisses}");
    }

    /* ------------------------------------------------------------------ */
    /* API 请求 / 响应形状                                                 */
    /* ------------------------------------------------------------------ */

    private static void ApiShape()
    {
        var bad = SearchApiRequest.Parse("""{"api":2,"query":"晴天","cursor":"***"}""");
        Program.Check("分页: 非法 cursor → 码 1 / reason=invalid_cursor",
            bad.Error?.Code == SearchApiCodes.InvalidRequest
            && bad.Error.Reason == "invalid_cursor" && bad.Error.Field == "cursor",
            bad.Error?.Reason ?? "(ok)");

        var badTotal = SearchApiRequest.Parse("""{"api":2,"query":"晴天","total":"weird"}""");
        Program.Check("分页: 非法 total → 码 1 / reason=invalid_total",
            badTotal.Error?.Code == SearchApiCodes.InvalidRequest
            && badTotal.Error.Reason == "invalid_total" && badTotal.Error.Field == "total",
            badTotal.Error?.Reason ?? "(ok)");

        var ok = SearchApiRequest.Parse("""{"api":2,"query":"晴天","total":"capped"}""");
        Program.Check("分页: total=capped 解析进请求",
            ok.Error is null && ok.Request!.Request.TotalMode == SearchTotalMode.Capped,
            ok.Error?.Reason ?? "none");

        var items = new List<SearchHit> { new("0123456789abcdef0123456789abcdef", -1, SearchPassNames.Lexical) };
        var json = SearchApiJson.Success(
            items, new PassCounts(), gen: 12, SearchPolicy.SchemeId, "default@1",
            clamped: false, queryTruncated: false,
            total: 30, totalExact: true, restart: false, next: "abc-_");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Program.Check("分页: 响应写出 total / totalExact / restart / next",
            root.GetProperty("total").GetInt64() == 30
            && root.GetProperty("totalExact").GetBoolean()
            && !root.GetProperty("restart").GetBoolean()
            && root.GetProperty("next").GetString() == "abc-_", json);
    }

    /* ------------------------------------------------------------------ */
    /* 工具                                                                */
    /* ------------------------------------------------------------------ */

    private static SearchRequest Request(string? cursorRaw, SearchTotalMode total, int limit)
    {
        SearchCursor? cursor = null;
        if (cursorRaw is not null && SearchPaging.TryDecode(cursorRaw, out var decoded))
            cursor = decoded;
        return new SearchRequest
        {
            Query = Query,
            Limit = limit,
            Fuzzy = true,
            MinExact = 1,
            Cursor = cursor,
            CursorRaw = cursorRaw,
            TotalMode = total,
        };
    }

    /// <summary>改写游标里的 gen / scheme_hash（模拟旧世代 / 旧方案的游标）。</summary>
    private static string Rebake(string cursor, long? gen, uint? schemeHash)
    {
        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        var bytes = Convert.FromBase64String(padded);
        if (gen.HasValue) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1, 8), gen.Value);
        if (schemeHash.HasValue) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9, 4), schemeHash.Value);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
