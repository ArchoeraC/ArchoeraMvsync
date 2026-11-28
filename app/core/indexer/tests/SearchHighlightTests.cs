// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.Tests;

/// <summary>
/// T2.7 结果高亮（<c>highlight</c>）：字面 / 读音 / 首字母 UTF-16 区间、
/// 每字段 32 段 / 最多 4 字段上限、歌词仅 snippet=true、库缺失失败开放、缓存键区分开关。
/// </summary>
internal static class SearchHighlightTests
{
    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-search-highlight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            EndToEnd.CreateLibrary(db, generation: 1);
            using (var conn = EndToEnd.Open(db))
            {
                EndToEnd.InsertTrack(
                    conn, "h1", Path.Combine(root, "晴天.mp3"), "晴天",
                    """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""",
                    "[00:01.00]今天晴天适合听歌", 1000);
                EndToEnd.InsertTrack(
                    conn, "h2", Path.Combine(root, "🌞晴天.mp3"), "🌞晴天",
                    """[{"name":"合成"}]""", null, null, 2000);
                EndToEnd.InsertTrack(
                    conn, "h3", Path.Combine(root, "long.mp3"),
                    string.Join("-", Enumerable.Repeat("晴", 40)),
                    """[{"name":"合成"}]""", null, null, 3000);
                EndToEnd.InsertTrack(
                    conn, "h4", Path.Combine(root, "晴晴.mp3"), "晴",
                    """[{"name":"晴"}]""", """{"name":"晴"}""", "晴", 4000);
                EndToEnd.InsertTrack(
                    conn, "h5", Path.Combine(root, "other.mp3"), "无关",
                    """[{"name":"无关"}]""", null, "歌词含晴天", 5000);
                EndToEnd.InsertTrack(
                    conn, "h6", Path.Combine(root, "multi.mp3"), "无关二",
                    """[{"name":"A"},{"name":"B"}]""", null, null, 6000);
            }

            LiteralTitle(db);
            EmojiUtf16(db);
            ArtistLiteral(db);
            ArtistDisplayJoin(db);
            ReadingMode(db);
            InitialMode(db);
            LyricsOnlyWithSnippet(db);
            RangeCap(db);
            FieldCap(db);
            MissingLibrary(root);
            CacheKeySeparatesFlag(db, index);
            PageCapConstant();
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

    private static void LiteralTitle(string db)
    {
        var ranges = Highlight(db, "h1", "晴天", includeLyrics: false);
        Program.Check("高亮: 标题字面 [0,2) literal",
            HasRange(ranges, "title", 0, 2, "literal"), Describe(ranges));
    }

    private static void EmojiUtf16(string db)
    {
        var ranges = Highlight(db, "h2", "晴天", includeLyrics: false);
        // 🌞 占 2 个 UTF-16 code unit，晴天从下标 2 开始。
        Program.Check("高亮: emoji 后区间按 UTF-16 偏移",
            HasRange(ranges, "title", 2, 4, "literal"), Describe(ranges));
    }

    private static void ArtistLiteral(string db)
    {
        var ranges = Highlight(db, "h1", "周杰伦", includeLyrics: false);
        Program.Check("高亮: 歌手字面 [0,3) literal",
            HasRange(ranges, "artist", 0, 3, "literal"), Describe(ranges));
    }

    private static void ArtistDisplayJoin(string db)
    {
        var ranges = Highlight(db, "h6", "B", includeLyrics: false);
        // 高亮展示文本与 Dart 一致按 " / " 连接：A / B → B 在 [4,5)。
        Program.Check("高亮: 多歌手按 \" / \" 连接定位",
            HasRange(ranges, "artist", 4, 5, "literal"), Describe(ranges));
    }

    private static void PageCapConstant()
    {
        Program.Check("高亮: 覆盖上限与分页容量一致（300）",
            SearchHighlight.MaxRows == 300, SearchHighlight.MaxRows.ToString());
    }

    private static void ReadingMode(string db)
    {
        var ranges = Highlight(db, "h1", "qing", includeLyrics: false);
        Program.Check("高亮: 拉丁查询命中汉字 → reading",
            HasRange(ranges, "title", 0, 1, "reading"), Describe(ranges));
    }

    private static void InitialMode(string db)
    {
        var ranges = Highlight(db, "h1", "qt", includeLyrics: false);
        Program.Check("高亮: 首字母查询 → initial",
            HasRange(ranges, "title", 0, 2, "initial"), Describe(ranges));
    }

    private static void LyricsOnlyWithSnippet(string db)
    {
        var off = SearchHighlight.Build(
            db, new[] { new SearchHit("h5", 0, "lexical") }, "晴天", includeLyrics: false);
        Program.Check("高亮: snippet=false 不返回歌词高亮", !off.ContainsKey("h5"),
            string.Join(',', off.Keys));

        var on = SearchHighlight.Build(
            db, new[] { new SearchHit("h5", 0, "lexical") }, "晴天", includeLyrics: true);
        Program.Check("高亮: snippet=true 返回歌词 literal",
            on.TryGetValue("h5", out var track)
            && track.Ranges.Any(range => range.Field == "lyrics" && range.Mode == "literal"),
            on.TryGetValue("h5", out var t) ? Describe(t.Ranges) : "missing");
    }

    private static void RangeCap(string db)
    {
        var result = SearchHighlight.Build(
            db, new[] { new SearchHit("h3", 0, "lexical") }, "晴", includeLyrics: false);
        Program.Check("高亮: 单字段 40 段截到 32 且 truncated",
            result.TryGetValue("h3", out var track)
            && track.Truncated
            && track.Ranges.Count(range => range.Field == "title") == SearchHighlight.MaxRangesPerField,
            result.TryGetValue("h3", out var t) ? $"{t.Ranges.Count}/{t.Truncated}" : "missing");
    }

    private static void FieldCap(string db)
    {
        var result = SearchHighlight.Build(
            db, new[] { new SearchHit("h4", 0, "lexical") }, "晴", includeLyrics: true);
        var fields = result.TryGetValue("h4", out var track)
            ? track.Ranges.Select(range => range.Field).Distinct().ToList()
            : new List<string>();
        Program.Check("高亮: 最多 4 个字段并置 truncated",
            fields.Count == SearchHighlight.MaxFields
            && !fields.Contains("lyrics")
            && result["h4"].Truncated,
            $"{string.Join(',', fields)} truncated={(result.TryGetValue("h4", out var t) ? t.Truncated.ToString() : "missing")}");
    }

    private static void MissingLibrary(string root)
    {
        var result = SearchHighlight.Build(
            Path.Combine(root, "does-not-exist.db"),
            new[] { new SearchHit("h1", 0, "lexical") }, "晴天", includeLyrics: true);
        Program.Check("高亮: 库缺失失败开放", result.Count == 0, result.Count.ToString());
    }

    private static void CacheKeySeparatesFlag(string db, string index)
    {
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.DebugResetCacheStats();
        SearchSession.Run(index, new SearchRequest
        {
            Query = "晴天",
            Limit = 50,
            Fuzzy = true,
            MinExact = 1,
            Highlight = true,
        }, db);
        var afterFirst = SearchSession.DebugCacheMisses;
        SearchSession.Run(index, new SearchRequest
        {
            Query = "晴天",
            Limit = 50,
            Fuzzy = true,
            MinExact = 1,
            Highlight = false,
        }, db);
        Program.Check("高亮: 缓存键区分 highlight 开关",
            afterFirst == 1 && SearchSession.DebugCacheMisses == 2,
            $"{afterFirst} → {SearchSession.DebugCacheMisses}");
    }

    /* ------------------------------------------------------------------ */
    /* 工具                                                                */
    /* ------------------------------------------------------------------ */

    private static IReadOnlyList<HighlightRange> Highlight(
        string db, string trackId, string query, bool includeLyrics)
    {
        var result = SearchHighlight.Build(
            db, new[] { new SearchHit(trackId, 0, "lexical") }, query, includeLyrics);
        return result.TryGetValue(trackId, out var track)
            ? track.Ranges
            : Array.Empty<HighlightRange>();
    }

    private static bool HasRange(
        IReadOnlyList<HighlightRange> ranges, string field, int start, int end, string mode)
        => ranges.Any(range => range.Field == field
            && range.Start == start && range.End == end && range.Mode == mode);

    private static string Describe(IReadOnlyList<HighlightRange> ranges)
        => ranges.Count == 0
            ? "empty"
            : string.Join(',', ranges.Select(range =>
                $"{range.Field}[{range.Start},{range.End})/{range.Mode}"));
}
