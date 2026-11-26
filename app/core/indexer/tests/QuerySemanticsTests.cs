// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 查询语义批次（Q3 / Q4 / Q8）：短拼音前缀门槛、短路要求强证据、步骤 df 预算。
///
/// <para>三项都是**策略开关**而不是无条件语义改变，所以测试同时断言「开」与「关」两侧：
/// Q3 抬门槛后 3 字符完整音节不再扩到另一个音节；Q4 弱证据主趟继续跑兜底、强证据仍然短路；
/// Q8 预算只跳过宽命中步骤、窄查询结果不变。默认值另由 <c>SearchPlanTests.DefaultsSnapshot</c>
/// 把关。</para>
/// </summary>
internal static class QuerySemanticsTests
{
    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-query-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var indexed = BuildFixture(db, index);

            ShortPinyinPrefix(index);
            ShortCircuitRequiresStrongKind(index);
            DfBudget(index);
            Program.Check("query semantics: 夹具索引曲目数", indexed == 16, indexed.ToString());
        }
        finally
        {
            Program.Check("query semantics: 临时目录清理", TryDelete(root), root);
        }
    }

    /// <summary>
    /// 夹具：一个 Partial 主趟（<c>hello world</c>）、两个只差拼音前缀的 CJK 标题
    /// （<c>晚</c> / <c>王</c>）、一个强证据精确命中（<c>晴天</c>），以及 12 个宽 <c>爱</c> 标题
    /// （Q8 需要一个估算 df 明显高于阈值的步骤）。
    /// </summary>
    private static int BuildFixture(string db, string index)
    {
        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            Insert(conn, "h1", "hello world.mp3", "Hello World", 1000);
            Insert(conn, "w1", "wan.mp3", "晚", 2000);
            Insert(conn, "w2", "wang.mp3", "王", 3000);
            Insert(conn, "q1", "qingtian.mp3", "晴天", 4000);
            var wide = new[] { "爱我", "爱你", "爱他", "爱她", "爱谁", "爱恨" };
            for (var i = 0; i < wide.Length; i++)
                Insert(conn, "a" + i, $"ai{i}.mp3", wide[i], 5000 + i * 100);
            var more = new[] { "爱心", "爱国", "爱情", "爱人", "爱意", "爱慕" };
            for (var i = 0; i < more.Length; i++)
                Insert(conn, "b" + i, $"bj{i}.mp3", more[i], 6000 + i * 100);
        }

        return new IndexerEngine().Run(
            db, index, full: true, new IndexerOptions { Pinyin = true }).Indexed;
    }

    private static void Insert(SqliteConnection conn, string id, string path, string title, long mtime)
        => EndToEnd.InsertTrack(conn, id, path, title, "[]", null, null, mtime);

    /* ------------------------------------------------------------------ */
    /* Q3：单音节拼音前缀门槛                                             */
    /* ------------------------------------------------------------------ */

    private static void ShortPinyinPrefix(string index)
    {
        // 编译层：门槛 3 → 单音节拼音支为 ("wan" OR "wan"*)；门槛 4 → 只有 "wan"。
        // 这是 Q3 的全部作用面（只作用于拼音支，字面 / 模糊支的门槛不变）。
        var tokens = Analyzer.AnalyzeQuery("wan");
        var at3 = SearchQuery.PinyinBranch(tokens, includePrefix: true, minPinyinPrefixLength: 3) ?? "";
        var at4 = SearchQuery.PinyinBranch(tokens, includePrefix: true, minPinyinPrefixLength: 4) ?? "";
        Program.Check("Q3: 门槛 3 的拼音支带前缀",
            at3.Contains("\"wan\"*", StringComparison.Ordinal), at3);
        Program.Check("Q3: 门槛 4 的拼音支不带前缀",
            !at4.Contains('*') && at4.Contains("\"wan\"", StringComparison.Ordinal), at4);
        Program.Check("Q3: 门槛只影响拼音支，字面支仍为前缀",
            (SearchQuery.CompileLexical(tokens) ?? "").Contains('*'),
            SearchQuery.CompileLexical(tokens) ?? "<null>");

        // 多音节拼音不受影响：连写（woaini）与词前缀（woai）都走短语，不加前缀。
        var joined = SearchQuery.PinyinBranch(
            Analyzer.AnalyzeQuery("woaini"), includePrefix: true, minPinyinPrefixLength: 4) ?? "";
        var wordPrefix = SearchQuery.PinyinBranch(
            Analyzer.AnalyzeQuery("woai"), includePrefix: true, minPinyinPrefixLength: 4) ?? "";
        Program.Check("Q3: woaini 在门槛 4 下仍是相邻短语",
            joined == "{pinyin_meta_tok pinyin_lyrics_tok} : \"wo ai ni\"" && !joined.Contains('*'),
            joined);
        Program.Check("Q3: woai 在门槛 4 下仍是相邻短语",
            wordPrefix == "{pinyin_meta_tok pinyin_lyrics_tok} : \"wo ai\"" && !wordPrefix.Contains('*'),
            wordPrefix);

        // 端到端：默认方案（字面 + 拼音前缀）里 `wan` 同时命中 晚 与 王（wang）；
        // exact-only 方案（minimal@1，Q5）没有任何 `*`，只命中 晚（wan）。
        var defaults = Query(index, "wan");
        Program.Check("Q3: 默认方案 wan 命中 晚",
            defaults.Items.Any(hit => hit.TrackId == "w1"), Ids(defaults));
        Program.Check("Q3: 默认方案 wan 经字面前缀命中 王（wang）",
            defaults.Items.Any(hit => hit.TrackId == "w2"), Ids(defaults));

        var minimal = Query(index, "wan", scheme: SchemeIds.Minimal);
        Program.Check("Q5/Q3: minimal@1 的 wan 只精确命中 晚",
            minimal.Items.Any(hit => hit.TrackId == "w1")
            && !minimal.Items.Any(hit => hit.TrackId == "w2"), Ids(minimal));
    }

    /* ------------------------------------------------------------------ */
    /* Q4：短路要求强证据                                                 */
    /* ------------------------------------------------------------------ */

    private static void ShortCircuitRequiresStrongKind(string index)
    {
        var weakOff = Query(index, "hello world", SearchPolicy.Default, explain: true);
        Program.Check("Q4: 关闭时弱证据主趟直接短路",
            weakOff.Passes.PinyinLoose == 0, Passes(weakOff));
        Program.Check("Q4: 关闭时跳过原因仍是 min_exact",
            weakOff.Trace?.Skipped.Any(skip => skip.Reason == "min_exact") == true,
            SkipReasons(weakOff));

        Program.Check("Q4: 关闭时弱证据主趟也不跑丢词趟",
            weakOff.Passes.DropTokens == 0, Passes(weakOff));

        var weakOn = Query(index, "hello world",
            SearchPolicy.Default with { RequireStrongKindForShortCircuit = true }, explain: true);
        Program.Check("Q4: 开启时弱证据（Partial）继续跑拼音宽松趟",
            weakOn.Passes.PinyinLoose == 1, Passes(weakOn));
        Program.Check("Q4: 开启时拼音宽松步记 ran",
            weakOn.Trace?.Steps.Any(step => step.Step == "pinyin-loose@1" && step.State == "ran")
                == true,
            States(weakOn));
        Program.Check("Q4: 开启时弱证据（Partial）继续跑丢词趟",
            weakOn.Passes.DropTokens == 1, Passes(weakOn));
        Program.Check("Q4: 开启后弱证据轨仍在结果里",
            weakOn.Items.Any(hit => hit.TrackId == "h1"), Ids(weakOn));

        // 生产默认方案（default@1）已启用 Q4 策略 → 同样的弱证据查询会跑兜底趟。
        var production = Query(index, "hello world", explain: true);
        Program.Check("Q4: default@1 默认启用强证据短路策略",
            production.Passes.PinyinLoose == 1, Passes(production));

        // 强证据（晴天 = 单个 bigram 精确词元，Exact）仍然短路。
        var strong = Query(index, "晴天",
            SearchPolicy.Default with { RequireStrongKindForShortCircuit = true }, explain: true);
        Program.Check("Q4: 开启时强证据仍短路",
            strong.Passes.PinyinLoose == 0, Passes(strong));
        Program.Check("Q4: 强证据短路原因保持 min_exact",
            strong.Trace?.Skipped.Any(skip =>
                skip.Reason == "min_exact"
                && strong.Trace.Steps[skip.I].Step == "pinyin-loose@1") == true,
            SkipReasons(strong));
    }

    /* ------------------------------------------------------------------ */
    /* Q8：步骤 df 预算（默认关闭）                                       */
    /* ------------------------------------------------------------------ */

    private static void DfBudget(string index)
    {
        // 阈值 = 1 × LiteralCap(2) × 1 = 2；宽查询（12 首 爱*）估算 12 > 2。
        var budget = SearchPolicy.Default with
        {
            LiteralCap = 2,
            DfBudgetFactor = 1,
            DfBudgetCapMultiplier = 1,
        };

        var broad = Query(index, "爱", budget, explain: true);
        Program.Check("Q8: 宽查询主趟被 df_budget 跳过",
            broad.Trace?.Skipped.Any(skip => skip.Reason == "df_budget") == true,
            SkipReasons(broad));
        Program.Check("Q8: 宽查询不返回结果", broad.Items.Count == 0, Ids(broad));

        // 窄查询（晴天 估算约 3 ≤ 阈值 8）照常执行，top-1 与未开预算时一致。
        var narrow = SearchPolicy.Default with
        {
            LiteralCap = 2,
            DfBudgetFactor = 1,
            DfBudgetCapMultiplier = 4,
        };
        var narrowBudget = Query(index, "晴天", narrow, explain: true);
        Program.Check("Q8: 窄查询不触发 df_budget",
            narrowBudget.Trace?.Skipped.All(skip => skip.Reason != "df_budget") == true,
            SkipReasons(narrowBudget));
        Program.Check("Q8: 窄查询 top-1 命中 晴天",
            narrowBudget.Items.Count > 0 && narrowBudget.Items[0].TrackId == "q1",
            Ids(narrowBudget));

        var plain = Query(index, "晴天");
        Program.Check("Q8: 开 / 关预算的窄查询 top-1 一致",
            plain.Items.Count > 0 && narrowBudget.Items[0].TrackId == plain.Items[0].TrackId,
            $"{Ids(plain)} / {Ids(narrowBudget)}");
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private static SearchResult Query(
        string index, string query, SearchPolicy? policy = null, bool explain = false,
        string? scheme = null)
    {
        try
        {
            SearchSession.DebugPolicyOverride = policy;
            return SearchSession.Run(index, new SearchRequest
            {
                Query = query,
                Scheme = scheme,
                Limit = 50,
                Fuzzy = false,
                Explain = explain,
                Cache = false,
            });
        }
        finally
        {
            SearchSession.DebugPolicyOverride = null;
            SearchSession.CloseCached();
        }
    }

    private static string Ids(SearchResult result)
        => string.Join(',', result.Items.Select(hit => hit.TrackId));

    private static string Passes(SearchResult result)
        => $"drop={result.Passes.DropTokens} pinyin={result.Passes.PinyinLoose} "
           + $"reading={result.Passes.Reading} cross={result.Passes.CrossLingual} "
           + $"fuzzy={result.Passes.Fuzzy} cjk={result.Passes.CjkFuzzy}";

    private static string SkipReasons(SearchResult result)
        => result.Trace is null
            ? "(no trace)"
            : string.Join(',', result.Trace.Skipped.Select(skip => skip.Reason));

    private static string States(SearchResult result)
        => result.Trace is null
            ? "(no trace)"
            : string.Join(',', result.Trace.Steps.Select(step => step.Step + ":" + step.State));

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
