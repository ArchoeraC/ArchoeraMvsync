// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.Tests;

/// <summary>
/// T2.8 歌词片段（<c>snippet</c>）端到端：查询期只读 <c>library.db</c> 截取，
/// 只覆盖前 300 条（分页容量）、长度 ≤512 UTF-16 code unit、库缺失失败开放、缓存命中仍返回。
/// </summary>
internal static class SearchSnippetTests
{
    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-search-snippet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var media = Path.Combine(root, "media");
            Directory.CreateDirectory(media);

            EndToEnd.CreateLibrary(db, generation: 1);
            using (var conn = EndToEnd.Open(db))
            {
                // 305 首歌词都含「晴天」，标题不含 → 命中只来自歌词，可验证前 300 条截断。
                for (var i = 1; i <= 305; i++)
                {
                    EndToEnd.InsertTrack(
                        conn, "s" + i.ToString("D3"), Path.Combine(media, $"g{i}.mp3"), $"曲目{i:D3}",
                        """[{"name":"合成"}]""", null,
                        $"[00:01.00]第{i}首的歌词\n[00:02.00]今天晴天适合听歌", 1000 + i);
                }
                // 元数据命中但歌词不含查询：不得构造片段。
                EndToEnd.InsertTrack(conn, "m1", Path.Combine(media, "meta.mp3"), "UniqueTitle",
                    """[{"name":"合成"}]""", null, "no match here", 9000);
            }

            var built = EndToEnd.RunEngine(db, index, full: true, lrc: false);
            Program.Check("片段: 建库 306 首", built.Indexed == 306, built.Indexed.ToString());

            TopTen(index, db);
            NoFlag(index, db);
            MetadataOnlyNoSnippet(index, db);
            CacheKeepsSnippet(index, db);
            MissingLibrary(index, root);
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

    private static void TopTen(string index, string db)
    {
        var result = Query(index, db, "晴天", snippet: true);
        Program.Check("片段: 查询命中 305 首", result.Items.Count == 305, result.Items.Count.ToString());
        Program.Check("片段: 只前 300 条有片段", result.Snippets.Count == 300,
            result.Snippets.Count.ToString());

        var firstPage = result.Items.Take(300).Select(hit => hit.TrackId).ToList();
        Program.Check("片段: 片段键恰为前 300 条",
            firstPage.All(id => result.Snippets.ContainsKey(id))
            && result.Items.Skip(300).All(hit => !result.Snippets.ContainsKey(hit.TrackId)),
            string.Join(',', result.Snippets.Keys));

        var allOk = true;
        var detail = "";
        foreach (var (id, snippet) in result.Snippets)
        {
            var lengthOk = snippet.Text.Length is > 0 and <= SearchSnippet.MaxTextLength;
            var rangeOk = snippet.Ranges.Count > 0
                && snippet.Ranges.All(range =>
                    range.Start >= 0 && range.Start < range.End && range.End <= snippet.Text.Length);
            var contains = snippet.Text.Contains("晴天", StringComparison.Ordinal);
            if (!lengthOk || !rangeOk || !contains)
            {
                allOk = false;
                detail = $"{id}: len={snippet.Text.Length} ranges={snippet.Ranges.Count} text={snippet.Text}";
                break;
            }
        }
        Program.Check("片段: 文本 ≤512 / 区间在文本内 / 窗口含查询", allOk, detail);
        Program.Check("片段: field==lyrics",
            result.Snippets.Values.All(snippet => snippet.Field == "lyrics"),
            string.Join(',', result.Snippets.Values.Select(snippet => snippet.Field).Distinct()));
    }

    private static void NoFlag(string index, string db)
    {
        var result = Query(index, db, "晴天", snippet: false);
        Program.Check("片段: snippet=false 不构造", result.Snippets.Count == 0,
            result.Snippets.Count.ToString());
    }

    private static void MetadataOnlyNoSnippet(string index, string db)
    {
        var result = Query(index, db, "UniqueTitle", snippet: true);
        Program.Check("片段: 元数据命中仍能返回结果",
            result.Items.Any(hit => hit.TrackId == "m1"),
            string.Join(',', result.Items.Select(hit => hit.TrackId)));
        Program.Check("片段: 歌词不含查询 → 无片段",
            !result.Snippets.ContainsKey("m1"), result.Snippets.Count.ToString());
    }

    private static void CacheKeepsSnippet(string index, string db)
    {
        SearchSession.DebugResetCacheStats();
        var first = Query(index, db, "晴天", snippet: true);
        var hitsBefore = SearchSession.DebugCacheHits;
        var second = Query(index, db, "晴天", snippet: true);

        Program.Check("片段: 第二次查询命中缓存",
            SearchSession.DebugCacheHits == hitsBefore + 1,
            $"{hitsBefore} → {SearchSession.DebugCacheHits}");
        Program.Check("片段: 缓存命中仍带片段",
            second.Snippets.Count == first.Snippets.Count && second.Snippets.Count == 300,
            $"{first.Snippets.Count}/{second.Snippets.Count}");
    }

    private static void MissingLibrary(string index, string root)
    {
        var missing = Path.Combine(root, "does-not-exist.db");
        var result = Query(index, missing, "晴天", snippet: true);
        Program.Check("片段: 库缺失失败开放（结果仍在）", result.Items.Count == 305,
            result.Items.Count.ToString());
        Program.Check("片段: 库缺失时无片段、不报错", result.Snippets.Count == 0,
            result.Snippets.Count.ToString());
    }

    private static SearchResult Query(string index, string libraryDb, string query, bool snippet)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Limit = 400,
            Fuzzy = true,
            MinExact = 1,
            Snippet = snippet,
        }, libraryDb);
}
