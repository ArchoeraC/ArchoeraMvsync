// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Archoera.Indexer.Ffi;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T2.12 无索引降级检索（<c>era_search_fallback</c>）：匹配集合与旧 Dart LIKE 一致、
/// 字段权重排序、total 不受分页影响、空查询返回整库、高亮区间、库缺失 / 非法 JSON 错误码。
/// </summary>
internal static unsafe class SearchFallbackTests
{
    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-search-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            EndToEnd.CreateLibrary(db, generation: 1);
            using (var conn = EndToEnd.Open(db))
            {
                EndToEnd.InsertTrack(
                    conn, "f1", Path.Combine(root, "晴天.mp3"), "晴天",
                    """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""", null, 1000);
                EndToEnd.InsertTrack(
                    conn, "f2", Path.Combine(root, "七里香.mp3"), "七里香",
                    """[{"name":"晴天乐队"}]""", null, null, 2000);
                EndToEnd.InsertTrack(
                    conn, "f3", Path.Combine(root, "稻香.mp3"), "稻香",
                    """[{"name":"周杰伦"}]""", """{"name":"晴天合集"}""", null, 3000);
                EndToEnd.InsertTrack(
                    conn, "f4", Path.Combine(root, "other.mp3"), "无关",
                    """[{"name":"无关"}]""", null, null, 4000);
            }

            MatchesOldDartLike(db);
            FieldWeightOrder(db);
            TotalAndPaging(db);
            EmptyQuery(db);
            HighlightRanges(db);
            DecorateTests(root);
            FfiContract(db, root);
        }
        finally
        {
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

    /// <summary>命中集合与旧 Dart（title/artists/album LIKE）逐条一致。</summary>
    private static void MatchesOldDartLike(string db)
    {
        var result = SearchFallback.Execute(db, new SearchRequest { Query = "晴天", Limit = 300 });
        var expected = OldLikeIds(db, "晴天");
        Program.Check("fallback: 命中集合与旧 Dart LIKE 一致",
            new HashSet<string>(result.Items.Select(i => i.TrackId), StringComparer.Ordinal)
                .SetEquals(expected),
            $"expected={string.Join(',', expected.OrderBy(x => x))} " +
            $"actual={string.Join(',', result.Items.Select(i => i.TrackId))}");
        Program.Check("fallback: pass=fallback", result.Items.All(i => i.Pass == SearchFallback.PassName));
    }

    /// <summary>title(3) &gt; artist(2) &gt; album(1)；同权重按 title / id。</summary>
    private static void FieldWeightOrder(string db)
    {
        var ids = SearchFallback.Execute(db, new SearchRequest { Query = "晴天", Limit = 300 })
            .Items.Select(i => i.TrackId).ToList();
        Program.Check("fallback: 权重 title > artist > album",
            ids.Count == 3 && ids[0] == "f1" && ids[1] == "f2" && ids[2] == "f3",
            string.Join(',', ids));
        var scores = SearchFallback.Execute(db, new SearchRequest { Query = "晴天", Limit = 300 })
            .Items.Select(i => i.Score).ToList();
        Program.Check("fallback: score 为字段权重 3/2/1",
            scores.Count == 3 && scores[0] == 3 && scores[1] == 2 && scores[2] == 1,
            string.Join(',', scores));
    }

    private static void TotalAndPaging(string db)
    {
        var page = SearchFallback.Execute(db, new SearchRequest { Query = "晴天", Limit = 2 });
        Program.Check("fallback: total 为全量匹配数（不受 limit 影响）",
            page.Total == 3 && page.Items.Count == 2, $"total={page.Total} items={page.Items.Count}");

        var tail = SearchFallback.Execute(db, new SearchRequest { Query = "晴天", Limit = 2, Offset = 2 });
        Program.Check("fallback: offset 切片且 total 不变",
            tail.Total == 3 && tail.Items.Count == 1 && tail.Items[0].TrackId == "f3",
            $"total={tail.Total} items={string.Join(',', tail.Items.Select(i => i.TrackId))}");
    }

    private static void EmptyQuery(string db)
    {
        var all = SearchFallback.Execute(db, new SearchRequest { Query = "", Limit = 300 });
        Program.Check("fallback: 空查询返回整库",
            all.Total == 4 && all.Items.Count == 4,
            $"total={all.Total} items={all.Items.Count}");
    }

    private static void HighlightRanges(string db)
    {
        var result = SearchFallback.Execute(
            db, new SearchRequest { Query = "晴天", Limit = 300, Highlight = true });

        Program.Check("fallback: 标题高亮 [0,2) literal",
            HasRange(result.Highlights, "f1", "title", 0, 2, "literal"));
        Program.Check("fallback: 歌手高亮 [0,2) literal",
            HasRange(result.Highlights, "f2", "artist", 0, 2, "literal"));
        Program.Check("fallback: 专辑高亮 [0,2) literal",
            HasRange(result.Highlights, "f3", "album", 0, 2, "literal"));
        Program.Check("fallback: 未开启 snippet 时不产出歌词片段",
            result.Snippets.Count == 0 && result.Items.Count == 3);
    }

    /// <summary>T2.11 按 trackId 装饰：高亮 / 片段、多歌手展示连接、失败开放、FFI 契约。</summary>
    private static void DecorateTests(string root)
    {
        var db = Path.Combine(root, "decorate.db");
        const string hexId = "0123456789abcdef0123456789abcdef";
        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(
                conn, hexId, Path.Combine(root, "装饰.mp3"), "晴天",
                """[{"name":"A"},{"name":"B"}]""", """{"name":"专辑"}""",
                "[00:01.00]今天晴天适合听歌", 1000);
        }

        var both = SearchDecorations.Build(
            db, "晴天", new[] { hexId }, includeLyrics: true);
        Program.Check("decorate: 标题高亮 [0,2)",
            both.Highlights.TryGetValue(hexId, out var highlight)
            && highlight.Ranges.Any(r => r.Field == "title" && r.Start == 0 && r.End == 2
                && r.Mode == "literal"),
            both.Highlights.TryGetValue(hexId, out var h) ? h.Ranges.Count.ToString() : "missing");
        Program.Check("decorate: 片段含查询",
            both.Snippets.TryGetValue(hexId, out var snippet)
            && snippet.Text.Contains("晴天", StringComparison.Ordinal),
            both.Snippets.Count.ToString());

        var artist = SearchDecorations.Build(
            db, "B", new[] { hexId }, includeLyrics: false);
        Program.Check("decorate: 多歌手按 \" / \" 连接定位 B[4,5)",
            artist.Highlights.TryGetValue(hexId, out var track)
            && track.Ranges.Any(r => r.Field == "artist" && r.Start == 4 && r.End == 5),
            artist.Highlights.TryGetValue(hexId, out var t) ? t.Ranges.Count.ToString() : "missing");

        Program.Check("decorate: 空 ids / 空查询 / 库缺失均失败开放",
            SearchDecorations.Build(db, "晴天", Array.Empty<string>(), true).Highlights.Count == 0
            && SearchDecorations.Build(db, "", new[] { hexId }, true).Highlights.Count == 0
            && SearchDecorations.Build(
                Path.Combine(root, "missing.db"), "晴天", new[] { hexId }, true)
                .Highlights.Count == 0);

        var request = $$"""{"api":2,"query":"晴天","trackIds":["{{hexId}}"],"snippet":true}""";
        var code = CallDecorate(db, request, out var json);
        Program.Check("decorate FFI: 成功返回 0", code == 0, $"code={code} json={json}");
        using (var doc = JsonDocument.Parse(json))
        {
            var rootEl = doc.RootElement;
            Program.Check("decorate FFI: api=2 / highlights / snippets",
                rootEl.GetProperty("api").GetInt32() == 2
                && rootEl.GetProperty("highlights").TryGetProperty(hexId, out _)
                && rootEl.GetProperty("snippets").TryGetProperty(hexId, out _),
                json);
        }

        Program.Check("decorate FFI: 库缺失 → 码 2",
            CallDecorate(Path.Combine(root, "missing.db"), request, out _) == 2);

        var tooMany = new StringBuilder("""{"api":2,"query":"晴天","trackIds":[""");
        for (var i = 0; i < SearchDecorations.MaxIds + 1; i++)
        {
            if (i > 0) tooMany.Append(',');
            tooMany.Append('"').Append(hexId).Append('"');
        }
        tooMany.Append("]}");
        Program.Check("decorate FFI: 超过 300 个 id → 码 1",
            CallDecorate(db, tooMany.ToString(), out _) == 1);

        Program.Check("decorate FFI: 非 32 位十六进制 → 码 1",
            CallDecorate(db, """{"api":2,"query":"晴天","trackIds":["f1"]}""", out _) == 1);
    }

    /// <summary>导出符号：成功 JSON 形状、库缺失 / 非法 JSON 的错误码。</summary>
    private static void FfiContract(string db, string root)
    {
        var request = """{"api":2,"query":"晴天","limit":300}""";
        var code = CallFallback(db, request, out var json);
        Program.Check("fallback FFI: 成功返回 0", code == 0, $"code={code} json={json}");
        using (var doc = JsonDocument.Parse(json))
        {
            var rootEl = doc.RootElement;
            var items = rootEl.GetProperty("items");
            Program.Check("fallback FFI: api=2 / scheme=fallback@1",
                rootEl.GetProperty("api").GetInt32() == 2
                && rootEl.GetProperty("scheme").GetString() == "fallback@1");
            Program.Check("fallback FFI: items / total / next=null / 无 explain",
                items.GetArrayLength() == 3
                && rootEl.GetProperty("total").GetInt64() == 3
                && rootEl.GetProperty("next").ValueKind == JsonValueKind.Null
                && rootEl.GetProperty("trace").ValueKind == JsonValueKind.Null
                && !items[0].TryGetProperty("explain", out _));
        }

        var missing = Path.Combine(root, "missing.db");
        Program.Check("fallback FFI: 库缺失 → 码 2",
            CallFallback(missing, request, out _) == 2);

        Program.Check("fallback FFI: 非法 JSON → 码 1",
            CallFallback(db, "{not json", out _) == 1);
    }

    /* ------------------------------------------------------------------ */

    private static int CallFallback(string libraryDb, string requestJson, out string json)
    {
        var pathPtr = (byte*)Marshal.StringToHGlobalAnsi(libraryDb);
        var reqPtr = (byte*)Marshal.StringToHGlobalAnsi(requestJson);
        byte* outPtr = null;
        var outLen = 0;
        try
        {
            var code = IndexerFfi.Fallback(pathPtr, reqPtr, &outPtr, &outLen);
            json = outPtr == null ? "" : Encoding.UTF8.GetString(outPtr, outLen);
            if (outPtr != null) IndexerFfi.Free(outPtr);
            return code;
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)pathPtr);
            Marshal.FreeHGlobal((IntPtr)reqPtr);
        }
    }

    private static int CallDecorate(string libraryDb, string requestJson, out string json)
    {
        var pathPtr = (byte*)Marshal.StringToHGlobalAnsi(libraryDb);
        var reqPtr = (byte*)Marshal.StringToHGlobalAnsi(requestJson);
        byte* outPtr = null;
        var outLen = 0;
        try
        {
            var code = IndexerFfi.Decorate(pathPtr, reqPtr, &outPtr, &outLen);
            json = outPtr == null ? "" : Encoding.UTF8.GetString(outPtr, outLen);
            if (outPtr != null) IndexerFfi.Free(outPtr);
            return code;
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)pathPtr);
            Marshal.FreeHGlobal((IntPtr)reqPtr);
        }
    }

    /// <summary>旧 Dart <c>_searchWhere</c> 的等价 SQL：结果集（排序前）。</summary>
    private static HashSet<string> OldLikeIds(string db, string query)
    {
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id FROM tracks WHERE title LIKE @p OR artists LIKE @p OR album LIKE @p";
        cmd.Parameters.AddWithValue("@p", "%" + query + "%");
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        }
        return ids;
    }

    private static bool HasRange(
        IReadOnlyDictionary<string, TrackHighlight> highlights,
        string id, string field, int start, int end, string mode)
        => highlights.TryGetValue(id, out var highlight)
           && highlight.Ranges.Any(r =>
               r.Field == field && r.Start == start && r.End == end && r.Mode == mode);
}
