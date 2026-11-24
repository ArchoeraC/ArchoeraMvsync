// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S0 负例对照基线（rewrite-plan §6.2 N1–N20）：在**当前 v7 实现**上把能执行的负例写成断言，
/// 用于记录「哪些必须不命中的查询已经成立、哪些仍是已知缺口」。
///
/// 两类结果分开报：
/// <list type="bullet">
///   <item><b>Check</b>：要求已经成立，行为变化会直接失败；</item>
///   <item><b>KnownGap</b>：要求尚未成立（S1/S2 修），只记录、不计入 FAIL；
///         若后续修好，会自动变成 PASS（提示可以删掉缺口标记）。</item>
/// </list>
///
/// 依赖 `plan` / `explain` / `typo2` 的条目（N8、N10、N11、N18–N20、N22、N23）
/// 在 S1/S2 之前无法断言归因档位，统一打印 BLOCKED，不伪造期望。
/// </summary>
internal static class NegativeBaselineTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-negative-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            MixedCorpus(root);
            PureEnglishCorpus(root);
            StaleTrack(root);
            Unreachable();
        }
        finally
        {
            SearchSession.CloseCached();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响断言结果。
            }
        }
    }

    /* ------------------------------------------------------------------ */
    /* 混合语料：N1–N5 / N7 / N9 / N13–N17                                 */
    /* ------------------------------------------------------------------ */

    private static void MixedCorpus(string root)
    {
        var db = Path.Combine(root, "mixed-library.db");
        var index = Path.Combine(root, "mixed-search.db");
        var audio = Path.Combine(root, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            // N1：全库无「痛」；N2：有「恨」无「爱」。
            EndToEnd.InsertTrack(conn, "n1", Song(audio, "n1.mp3"), "夜空", """[{"name":"合成"}]""",
                "夜曲", "夜色温柔\n星光点点", 1000);
            EndToEnd.InsertTrack(conn, "n2", Song(audio, "n2.mp3"), "恨", """[{"name":"合成"}]""",
                "恨", "恨意难平", 2000);
            // N3：单字母标题（不得因首字母 / 拼音支被「爱」召回）。
            EndToEnd.InsertTrack(conn, "n3a", Song(audio, "A.mp3"), "A", "[]", null,
                "alpha", 3000);
            EndToEnd.InsertTrack(conn, "n3b", Song(audio, "I.mp3"), "I", "[]", null,
                "iota", 4000);
            // N4：整行歌词只有「啊」（不得因首字母支被「爱」整库召回）。
            EndToEnd.InsertTrack(conn, "n4", Song(audio, "n4.mp3"), "无声", "[]", null,
                "啊\n啊\n啊", 5000);
            // N5：只有「爱」没有「爱情」，两字查询不得退化成单字。
            EndToEnd.InsertTrack(conn, "n5", Song(audio, "n5.mp3"), "爱心", "[]", null,
                "爱心传递", 6000);
            // N9：文件名含年份数字，不得成为「爱」的召回来源（D-4 / 首字母数字污染）。
            EndToEnd.InsertTrack(conn, "n9", Song(audio, "song-2024.mp3"), "无关歌曲", "[]", null,
                "没有那个字", 7000);
            // N13：查询元字符必须按普通字符处理，不报错、不清空结果。
            EndToEnd.InsertTrack(conn, "n13", Song(audio, "n13.mp3"), "a\" OR b*", "[]", null,
                "literal", 8000);
            // N16：只有 emoji 的查询不得报错。
            EndToEnd.InsertTrack(conn, "n16", Song(audio, "n16.mp3"), "音符", "[]", null,
                "🎵🎵", 9000);
            // N17：字面含 z 的歌必须命中；首字母以 z 开头（早晨 → zc）不得被单字母 z 召回。
            EndToEnd.InsertTrack(conn, "n17z", Song(audio, "n17z.mp3"), "Zero", "[]", null,
                "zero", 10000);
            EndToEnd.InsertTrack(conn, "n17c", Song(audio, "n17c.mp3"), "早晨", "[]", null,
                "早晨好", 11000);
        }

        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        // N1：全库无「痛」→ 空。
        var n1 = Query(index, "痛");
        Program.Check("N1 全库无「痛」→ 空", n1.Items.Count == 0, Count(n1));

        // N2：有「恨」无「爱」→ 不返回「恨」。
        var n2 = Query(index, "爱");
        Program.Check("N2 「爱」不返回「恨」", !Has(n2, "n2"), Hits(n2));
        Program.Check("N2 「爱」仍能命中字面含「爱」的曲目", Has(n2, "n5"), Hits(n2));

        // N3：单字母标题不被「爱」召回。
        var n3 = Query(index, "爱");
        Program.Check("N3 「爱」不返回单字母标题 A/I",
            !Has(n3, "n3a") && !Has(n3, "n3b"), Hits(n3));

        // N4：歌词只有「啊」不被「爱」召回。
        var n4 = Query(index, "爱");
        Program.Check("N4 「爱」不因歌词单字整库命中", !Has(n4, "n4"), Hits(n4));

        // N5：两字查询无命中 → 空（不得退化成只含「爱」的 n5）。
        var n5 = Query(index, "爱情");
        Program.Check("N5 「爱情」→ 空（两个 term 必须同时命中）", n5.Items.Count == 0, Count(n5));

        // N7：多词部分命中（夜 雨 恨）→ n=3 的 minMatch=2，本库无 2 词同曲 → 空。
        var n7 = Query(index, "夜 雨 恨");
        Program.Check("N7 「夜 雨 恨」不整库放宽 → 空", n7.Items.Count == 0, Count(n7));

        // N9：文件名年份不得成为「爱」的来源。
        var n9 = Query(index, "爱");
        Program.Check("N9 文件名数字（2024）不参与「爱」召回", !Has(n9, "n9"), Hits(n9));

        // N13：查询元字符按普通字符进入词元，不抛错；字面含该串的曲目仍可命中。
        string? n13Error = null;
        SearchResult? n13 = null;
        try { n13 = Query(index, "a\" OR b*"); }
        catch (Exception ex) { n13Error = ex.Message; }
        Program.Check("N13 查询元字符不抛错", n13Error is null, n13Error ?? "ok");
        if (n13 is not null)
        {
            Program.Check("N13 元字符按字面命中（a\" OR b*）", Has(n13, "n13"), Hits(n13));
            Program.Check("N13 元字符不扩散到无关曲目",
                !Has(n13, "n2") && !Has(n13, "n5") && !Has(n13, "n17c"), Hits(n13));
        }

        // N14：空 / 纯空白 → 空结果，且不执行任何兜底趟。
        var n14 = Query(index, "   ");
        Program.Check("N14 纯空白 → 空且零兜底趟",
            n14.Items.Count == 0 && NoPasses(n14), Count(n14) + " / " + PassSummary(n14));

        // N15：200 码点不得超时 / 抛错；当前实现未截断且会跑模糊趟 → 记为已知缺口。
        var longQuery = new string('a', 200);
        var watch = Stopwatch.StartNew();
        string? n15Error = null;
        SearchResult? n15 = null;
        try { n15 = Query(index, longQuery); }
        catch (Exception ex) { n15Error = ex.Message; }
        watch.Stop();
        Program.Check("N15 200 码点不抛错", n15Error is null, n15Error ?? "ok");
        Program.Check("N15 200 码点不超时（< 5s）", watch.ElapsedMilliseconds < 5000, watch.ElapsedMilliseconds.ToString());
        if (n15 is not null)
        {
            Program.KnownGap("N15 截断并只跑字面步", n15.Passes.Fuzzy == 0,
                "当前未截断，实际执行 fuzzy=1（" + watch.ElapsedMilliseconds + " ms）");
        }

        // N16：纯 emoji → 空。
        var n16 = Query(index, "🎵🎵");
        Program.Check("N16 纯 emoji → 空", n16.Items.Count == 0, Count(n16));

        // N17：单字母 z 只按字面处理，不展开首字母支（早晨 → zc 不得命中）。
        var n17 = Query(index, "z");
        Program.Check("N17 单字母 z 命中字面 Zero", Has(n17, "n17z"), Hits(n17));
        Program.Check("N17 单字母 z 不命中首字母 zc（早晨）", !Has(n17, "n17c"), Hits(n17));

        // N21：scope=lyrics 只扫歌词列——「夜空」只在 n1 的标题里，歌词是「夜色温柔」。
        var n21 = Query(index, "夜空", lyrics: true);
        Program.Check("N21 scope=lyrics 词只在标题 → 空", n21.Items.Count == 0, Count(n21));
        var n21All = Query(index, "夜空");
        Program.Check("N21 同词 scope=all 命中标题曲目 n1", Has(n21All, "n1"), Hits(n21All));

        // N24：scope=lyrics + 单 CJK 字：歌词里的「爱」（n5「爱心传递」）不得整库召回。
        var n24 = Query(index, "爱", lyrics: true);
        Program.Check("N24 scope=lyrics 单 CJK 字 → 空", n24.Items.Count == 0, Count(n24));
        Program.Check("N24 scope=lyrics 单 CJK 字 → 零兜底趟", NoPasses(n24), PassSummary(n24));
    }

    /* ------------------------------------------------------------------ */
    /* 纯英文语料：N6                                                      */
    /* ------------------------------------------------------------------ */

    private static void PureEnglishCorpus(string root)
    {
        var db = Path.Combine(root, "english-library.db");
        var index = Path.Combine(root, "english-search.db");
        var audio = Path.Combine(root, "en-audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "e1", Song(audio, "e1.mp3"), "love", "[]", null,
                "love me forever", 1000);
            EndToEnd.InsertTrack(conn, "e2", Song(audio, "e2.mp3"), "hello", "[]", null,
                "hello world", 2000);
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        var n6 = Query(index, "爱");
        Program.Check("N6 纯英文库 + 单汉字 → 空", n6.Items.Count == 0, Count(n6));
    }

    /* ------------------------------------------------------------------ */
    /* 陈旧索引：N12                                                        */
    /* ------------------------------------------------------------------ */

    private static void StaleTrack(string root)
    {
        var db = Path.Combine(root, "stale-library.db");
        var index = Path.Combine(root, "stale-search.db");
        var audio = Path.Combine(root, "stale-audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "s1", Song(audio, "s1.mp3"), "爱心", "[]", null,
                "爱心传递", 1000);
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        // 只删源库，不重建搜索索引：模拟「曲目已删除、索引尚未增量」的窗口。
        using (var conn = EndToEnd.Open(db))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM tracks WHERE id = 's1'";
            cmd.ExecuteNonQuery();
        }

        var ghost = Query(index, "爱");
        Program.KnownGap("N12 结果侧过滤已删除曲目", !Has(ghost, "s1"),
            "搜索索引仍返回幽灵 s1（查询期未回查 library.db）");
    }

    /* ------------------------------------------------------------------ */
    /* 当前无法断言的条目：明确 BLOCKED，不伪造期望                        */
    /* ------------------------------------------------------------------ */

    private static void Unreachable()
    {
        const string prop = "需要 plan / explain / 字段归因";
        Program.Blocked("N8 「Cover」不当正向词", prop + "（rank 层）");
        Program.Blocked("N10 日文汉字标题读音归因", prop);
        Program.Blocked("N11 译文命中必须 field=lyricsTr", prop);
        Program.Blocked("N18 高频词受预算裁剪", "需要计划层 df 预算");
        Program.Blocked("N19 索引版本不符降级 LIKE", "属 FFI / Dart 集成层；C# 只看到 schema 抛错");
        Program.Blocked("N20 构建中读取旧快照", "需并发构建 / 读取集成测试");
        Program.Blocked("N22 3 字符词不启用 typo2", "typo2 属 S2");
        Program.Blocked("N23 7+ 字符词允许 2 错", "typo2 属 S2");
    }

    /* ------------------------------------------------------------------ */
    /* 断言小工具                                                          */
    /* ------------------------------------------------------------------ */

    private static string Song(string dir, string name) => Path.Combine(dir, name);

    /// <summary>走生产渐进查询路径（fuzzy=true、MinExact=1，与桌面端默认一致）。</summary>
    private static SearchResult Query(string index, string query, bool lyrics = false)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Lyrics = lyrics,
            Limit = 200,
            Fuzzy = true,
            MinExact = 1,
        });

    private static bool Has(SearchResult result, string trackId)
        => result.Items.Any(hit => string.Equals(hit.TrackId, trackId, StringComparison.Ordinal));

    private static bool NoPasses(SearchResult result)
        => result.Passes.DropTokens == 0
        && result.Passes.PinyinLoose == 0
        && result.Passes.Reading == 0
        && result.Passes.CrossLingual == 0
        && result.Passes.Fuzzy == 0
        && result.Passes.CjkFuzzy == 0;

    private static string Count(SearchResult result)
        => result.Items.Count.ToString(CultureInfo.InvariantCulture);

    private static string Hits(SearchResult result)
        => string.Join(',', result.Items.Take(10).Select(hit => hit.TrackId));

    private static string PassSummary(SearchResult result)
    {
        var passes = result.Passes;
        return $"drop={passes.DropTokens} pinyin={passes.PinyinLoose} reading={passes.Reading} "
            + $"cross={passes.CrossLingual} fuzzy={passes.Fuzzy} cjk={passes.CjkFuzzy}";
    }
}
