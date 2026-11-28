// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// <c>indexer replay compare</c>（§6.4 / §6.7.4 / §6.7.5）测试：
/// 两份 §6.7.4 基线 JSON 的逐条 diff——完整 ids 数组的名次变化（不只看前 10）、
/// <c>overlap@10</c>（分母固定 10）、空态、<c>queries.hash</c> 可比性、参数 / 数据错误
/// 与 S1 门禁退出码（完全一致才 0）。
///
/// 运行器是自研 <see cref="Program.Check"/>（非 xUnit）；临时目录为
/// <c>Path.GetTempPath()</c> + Guid，结束时清理。
/// </summary>
internal static class ReplayCompareTests
{
    /// <summary>三条查询（含中文），每条 12 个 id：确保「只裁前 10 比较」的陷阱被覆盖。</summary>
    private const int IdsPerQuery = 12;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-replay-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Identical(root);
            RankChanges(root);
            EmptyStates(root);
            HashMismatch(root);
            RejectMismatchedQuerySets(root);
            AllowSubset(root);
            InvalidInputs(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响断言结果。
            }
        }
    }

    /* ------------------------------------------------------------------ */
    /* 1. 完全一致：退出 0 / identical=true / changed=0 / 前 10 只截断     */
    /* ------------------------------------------------------------------ */

    private static void Identical(string root)
    {
        var queries = StandardQueries();
        var hash = HashOf(queries);
        var baseline = WriteBaseline(root, "identical-baseline.json", hash, queries);
        var run = WriteBaseline(root, "identical-run.json", hash, queries);

        var (code, stdout) = RunReplay("compare", "--baseline", baseline, "--run", run);
        Program.Check("compare 一致: 退出 0（S1 门禁）", code == 0, code.ToString(CultureInfo.InvariantCulture));

        var aggregate = ReadAggregate(stdout);
        Program.Check("compare 一致: aggregate.identical=true", aggregate.Identical, stdout);
        Program.Check("compare 一致: changed=0", aggregate.Changed == 0,
            aggregate.Changed.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 一致: emptyOld / emptyNew 均为 0",
            aggregate.EmptyOld == 0 && aggregate.EmptyNew == 0,
            $"{aggregate.EmptyOld}/{aggregate.EmptyNew}");
        Program.Check("compare 一致: overlap@10=1.0", aggregate.Overlap == 1.0,
            aggregate.Overlap.ToString("R", CultureInfo.InvariantCulture));

        using (var document = JsonDocument.Parse(stdout))
        {
            var documentRoot = document.RootElement;
            var queryBlock = documentRoot.GetProperty("queries");
            Program.Check("compare 一致: queries.count=3", queryBlock.GetProperty("count").GetInt32() == 3,
                queryBlock.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture));
            Program.Check("compare 一致: queries.hash 沿用基线", queryBlock.GetProperty("hash").GetString() == hash,
                queryBlock.GetProperty("hash").GetString() ?? "(null)");

            var results = documentRoot.GetProperty("results");
            Program.Check("compare 一致: 逐条输出 3 条查询",
                results.EnumerateObject().Count() == 3,
                results.EnumerateObject().Count().ToString(CultureInfo.InvariantCulture));

            var first = results.GetProperty("Q0001");
            Program.Check("compare 一致: 回填查询文本 晴天", first.GetProperty("query").GetString() == "晴天",
                first.GetProperty("query").GetString() ?? "(null)");
            Program.Check("compare 一致: oldTop / newTop 只留前 10",
                first.GetProperty("oldTop").GetArrayLength() == 10
                && first.GetProperty("newTop").GetArrayLength() == 10,
                $"{first.GetProperty("oldTop").GetArrayLength()}/{first.GetProperty("newTop").GetArrayLength()}");
            Program.Check("compare 一致: 单条 changed 为空",
                first.GetProperty("changed").GetArrayLength() == 0,
                first.GetProperty("changed").GetArrayLength().ToString(CultureInfo.InvariantCulture));

            var allChanged = results.EnumerateObject()
                .Sum(entry => entry.Value.GetProperty("changed").GetArrayLength());
            Program.Check("compare 一致: 全部 changed 为空", allChanged == 0,
                allChanged.ToString(CultureInfo.InvariantCulture));
        }

        /* --out：完整报告落文件，stdout 只留可解析的汇总 */
        var outPath = Path.Combine(root, "nested", "deeper", "diff.json");
        var (outCode, outStdout) = RunReplay(
            "compare", "--baseline", baseline, "--run", run, "--out", outPath);
        Program.Check("compare --out: 退出 0", outCode == 0,
            outCode.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare --out: 自动建目录并写文件", File.Exists(outPath), outPath);

        var outAggregate = ReadAggregate(outStdout);
        Program.Check("compare --out: stdout 汇总可解析且 unchanged",
            outAggregate.Identical && outAggregate.Changed == 0, outStdout);

        var reportText = File.ReadAllText(outPath);
        using (var report = JsonDocument.Parse(reportText))
        {
            var reportRoot = report.RootElement;
            Program.Check("compare --out: 文件 schema=1", reportRoot.GetProperty("schema").GetInt32() == 1,
                reportRoot.GetProperty("schema").GetInt32().ToString(CultureInfo.InvariantCulture));
            Program.Check("compare --out: 文件含 results", reportRoot.TryGetProperty("results", out _));
            Program.Check("compare --out: 文件 aggregate.identical=true",
                reportRoot.GetProperty("aggregate").GetProperty("identical").GetBoolean(), reportText);
            Program.Check("compare --out: 文件带 out 路径",
                reportRoot.GetProperty("out").GetString() == Path.GetFullPath(outPath),
                reportRoot.GetProperty("out").GetString() ?? "(null)");
        }

        using (var summary = JsonDocument.Parse(outStdout))
        {
            Program.Check("compare --out: stdout 不含完整 results（只汇总）",
                !summary.RootElement.TryGetProperty("results", out _), outStdout);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 2. 名次变化：交换 / 少一条（前 10 内与外）/ 多一条                  */
    /* ------------------------------------------------------------------ */

    private static void RankChanges(string root)
    {
        var queries = StandardQueries();
        var hash = HashOf(queries);
        var baseline = WriteBaseline(root, "rank-baseline.json", hash, queries);

        // 2a. 前两名互换：changed 两条，overlap@10 仍 1.0。
        var swapped = CloneQueries(queries);
        (swapped[0].Ids[0], swapped[0].Ids[1]) = (swapped[0].Ids[1], swapped[0].Ids[0]);
        var swapRun = WriteBaseline(root, "rank-swap.json", hash, swapped);

        var (swapCode, swapStdout) = RunReplay("compare", "--baseline", baseline, "--run", swapRun);
        Program.Check("compare 顺序: 交换前两名退出 1（S1 要求 changed 为空）", swapCode == 1,
            swapCode.ToString(CultureInfo.InvariantCulture));
        var swapAggregate = ReadAggregate(swapStdout);
        Program.Check("compare 顺序: changed=2", swapAggregate.Changed == 2,
            swapAggregate.Changed.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 顺序: overlap@10 不受交换影响=1.0", swapAggregate.Overlap == 1.0,
            swapAggregate.Overlap.ToString("R", CultureInfo.InvariantCulture));
        Program.Check("compare 顺序: identical=false", !swapAggregate.Identical, swapStdout);

        using (var document = JsonDocument.Parse(swapStdout))
        {
            var changes = ReadChanges(document.RootElement.GetProperty("results").GetProperty("Q0001"));
            Program.Check("compare 顺序: changed 定位 id 与 old/new rank",
                changes.Count == 2
                && changes.Contains(("q1-01", 1, 2))
                && changes.Contains(("q1-02", 2, 1)),
                DescribeChanges(changes));
        }

        // 2b. 少一条，且缺的是第 12 名（前 10 之外）：只有比较完整数组才会发现。
        var trimmed = CloneQueries(queries);
        trimmed[1].Ids = trimmed[1].Ids.Take(IdsPerQuery - 1).ToArray();
        var trimRun = WriteBaseline(root, "rank-trim-tail.json", hash, trimmed);

        var (trimCode, trimStdout) = RunReplay("compare", "--baseline", baseline, "--run", trimRun);
        Program.Check("compare 少一条(第12名): 退出 1", trimCode == 1,
            trimCode.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 少一条(第12名): changed=1，证明比较完整数组",
            ReadAggregate(trimStdout).Changed == 1, trimStdout);
        Program.Check("compare 少一条(第12名): overlap@10 仍 1.0",
            ReadAggregate(trimStdout).Overlap == 1.0,
            ReadAggregate(trimStdout).Overlap.ToString("R", CultureInfo.InvariantCulture));

        using (var document = JsonDocument.Parse(trimStdout))
        {
            var changes = ReadChanges(document.RootElement.GetProperty("results").GetProperty("Q0002"));
            Program.Check("compare 少一条(第12名): oldRank=12 / newRank=0",
                changes.Count == 1 && changes[0] == ("q2-12", 12, 0), DescribeChanges(changes));
        }

        // 2c. 少一条，且缺的是第 10 名：后面的 id 各前进一名，overlap@10 = 9/10（分母仍 10）。
        var dropTopTen = CloneQueries(queries);
        dropTopTen[2].Ids = dropTopTen[2].Ids.Where(id => id != "q3-10").ToArray();
        var dropRun = WriteBaseline(root, "rank-drop-top10.json", hash, dropTopTen);

        var (dropCode, dropStdout) = RunReplay("compare", "--baseline", baseline, "--run", dropRun);
        Program.Check("compare 少一条(第10名): 退出 1", dropCode == 1,
            dropCode.ToString(CultureInfo.InvariantCulture));
        var dropAggregate = ReadAggregate(dropStdout);
        Program.Check("compare 少一条(第10名): changed=3（缺口 + 两条顺位前移）",
            dropAggregate.Changed == 3,
            dropAggregate.Changed.ToString(CultureInfo.InvariantCulture));

        using (var document = JsonDocument.Parse(dropStdout))
        {
            var entry = document.RootElement.GetProperty("results").GetProperty("Q0003");
            // 单条 overlap@10 = 9/10；aggregate 是三条查询的均值 (1+1+0.9)/3。
            Program.Check("compare 少一条(第10名): 单条 overlap@10=0.9",
                Math.Abs(entry.GetProperty("overlap@10").GetDouble() - 0.9) < 1e-9,
                entry.GetProperty("overlap@10").GetDouble().ToString("R", CultureInfo.InvariantCulture));
            Program.Check("compare 少一条(第10名): aggregate 均值为 0.9667",
                Math.Abs(dropAggregate.Overlap - 0.9667) < 1e-9,
                dropAggregate.Overlap.ToString("R", CultureInfo.InvariantCulture));
            Program.Check("compare 少一条(第10名): newTop 补进第 11 名、挤掉 q3-10",
                entry.GetProperty("newTop").GetArrayLength() == 10
                && !entry.GetProperty("newTop").EnumerateArray().Any(id => id.GetString() == "q3-10")
                && entry.GetProperty("newTop")[9].GetString() == "q3-11",
                entry.GetProperty("newTop").GetRawText());
            var changes = ReadChanges(entry);
            Program.Check("compare 少一条(第10名): 名次变化可定位",
                changes.Count == 3
                && changes[0] == ("q3-10", 10, 0)
                && changes[1] == ("q3-11", 11, 10)
                && changes[2] == ("q3-12", 12, 11),
                DescribeChanges(changes));
        }

        // 2d. 多一条（追加到第 13 名）：changed 一条，oldRank=0。
        var appended = CloneQueries(queries);
        appended[2].Ids = appended[2].Ids.Append("q3-13").ToArray();
        var appendRun = WriteBaseline(root, "rank-append.json", hash, appended);

        var (appendCode, appendStdout) = RunReplay("compare", "--baseline", baseline, "--run", appendRun);
        Program.Check("compare 多一条: 退出 1", appendCode == 1,
            appendCode.ToString(CultureInfo.InvariantCulture));
        using (var document = JsonDocument.Parse(appendStdout))
        {
            var changes = ReadChanges(document.RootElement.GetProperty("results").GetProperty("Q0003"));
            Program.Check("compare 多一条: oldRank=0 / newRank=13",
                changes.Count == 1 && changes[0] == ("q3-13", 0, 13), DescribeChanges(changes));
        }
        Program.Check("compare 多一条: overlap@10 仍 1.0",
            ReadAggregate(appendStdout).Overlap == 1.0,
            ReadAggregate(appendStdout).Overlap.ToString("R", CultureInfo.InvariantCulture));
    }

    /* ------------------------------------------------------------------ */
    /* 3. 空态变化：old 空 / new 非空 → 退出 1                             */
    /* ------------------------------------------------------------------ */

    private static void EmptyStates(string root)
    {
        var baselineQueries = StandardQueries();
        baselineQueries[1].Ids = Array.Empty<string>();
        var hash = HashOf(baselineQueries);
        var baseline = WriteBaseline(root, "empty-baseline.json", hash, baselineQueries);

        // 3a. old 空、new 非空。
        var filledQueries = CloneQueries(baselineQueries);
        filledQueries[1].Ids = new[] { "x1" };
        var filledRun = WriteBaseline(root, "empty-filled.json", hash, filledQueries);

        var (code, stdout) = RunReplay("compare", "--baseline", baseline, "--run", filledRun);
        Program.Check("compare 空态: old 空 / new 非空退出 1", code == 1,
            code.ToString(CultureInfo.InvariantCulture));

        var aggregate = ReadAggregate(stdout);
        Program.Check("compare 空态: aggregate emptyOld=1 / emptyNew=0",
            aggregate.EmptyOld == 1 && aggregate.EmptyNew == 0, $"{aggregate.EmptyOld}/{aggregate.EmptyNew}");
        Program.Check("compare 空态: changed=1（新增 id 名次 0→1）", aggregate.Changed == 1,
            aggregate.Changed.ToString(CultureInfo.InvariantCulture));
        // 单条 overlap@10=0.0；aggregate 是三条查询的均值 (1+0+1)/3。
        Program.Check("compare 空态: aggregate overlap@10=0.6667",
            Math.Abs(aggregate.Overlap - 0.6667) < 1e-9,
            aggregate.Overlap.ToString("R", CultureInfo.InvariantCulture));

        using (var document = JsonDocument.Parse(stdout))
        {
            var entry = document.RootElement.GetProperty("results").GetProperty("Q0002");
            Program.Check("compare 空态: 单条 overlap@10=0.0",
                entry.GetProperty("overlap@10").GetDouble() == 0.0,
                entry.GetProperty("overlap@10").GetDouble().ToString("R", CultureInfo.InvariantCulture));
            Program.Check("compare 空态: 单条 emptyOld=true / emptyNew=false",
                entry.GetProperty("emptyOld").GetBoolean() && !entry.GetProperty("emptyNew").GetBoolean(),
                entry.GetRawText());
            var changes = ReadChanges(entry);
            Program.Check("compare 空态: changed 记录 x1 0→1",
                changes.Count == 1 && changes[0] == ("x1", 0, 1), DescribeChanges(changes));
        }

        // 3b. 两边都空：属于一致，不应误报。
        var sameRun = WriteBaseline(root, "empty-both.json", hash, CloneQueries(baselineQueries));
        var (bothCode, bothStdout) = RunReplay("compare", "--baseline", baseline, "--run", sameRun);
        Program.Check("compare 空态: 两边都空退出 0", bothCode == 0,
            bothCode.ToString(CultureInfo.InvariantCulture));
        var bothAggregate = ReadAggregate(bothStdout);
        Program.Check("compare 空态: 两边都空 identical=true / emptyOld=emptyNew=1",
            bothAggregate.Identical && bothAggregate.EmptyOld == 1 && bothAggregate.EmptyNew == 1,
            bothStdout);

        // 3c. old 非空、new 空：同样退出 1，且方向相反。
        var (reverseCode, reverseStdout) = RunReplay("compare", "--baseline", filledRun, "--run", baseline);
        Program.Check("compare 空态: new 空退出 1", reverseCode == 1,
            reverseCode.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 空态: 反向 emptyOld=0 / emptyNew=1",
            ReadAggregate(reverseStdout).EmptyOld == 0 && ReadAggregate(reverseStdout).EmptyNew == 1,
            reverseStdout);
    }

    /* ------------------------------------------------------------------ */
    /* 4. queries.hash 不同：退出 1，且不误报成 id 差异                     */
    /* ------------------------------------------------------------------ */

    private static void HashMismatch(string root)
    {
        var queries = StandardQueries();
        var baseline = WriteBaseline(root, "hash-baseline.json", HashOf(queries), queries);
        // ids 完全一样，只有查询集 hash 不同：不可比，不能把它报成 changed。
        var otherHashRun = WriteBaseline(root, "hash-other.json", "0000000000000000", queries);

        var outPath = Path.Combine(root, "hash-mismatch-diff.json");
        var (code, stdout) = RunReplay(
            "compare", "--baseline", baseline, "--run", otherHashRun, "--out", outPath);
        Program.Check("compare hash: 不同退出 1", code == 1,
            code.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare hash: 不输出假的 diff", stdout.Length == 0, stdout);
        Program.Check("compare hash: 不写 --out 文件", !File.Exists(outPath), outPath);
    }

    /* ------------------------------------------------------------------ */
    /* 5. 查询集不同（未加 --allow-subset）：退出 1，不误报 id 差异         */
    /* ------------------------------------------------------------------ */

    private static void RejectMismatchedQuerySets(string root)
    {
        var queries = StandardQueries();
        var baseline = WriteBaseline(root, "set-baseline.json", HashOf(queries), queries);

        var extra = queries.Append(("Q0004", "五月天", Ids("q4-", IdsPerQuery))).ToArray();
        var extraRun = WriteBaseline(root, "set-extra.json", HashOf(extra), extra);

        var (code, stdout) = RunReplay("compare", "--baseline", baseline, "--run", extraRun);
        Program.Check("compare 查询集: 多一条查询退出 1", code == 1,
            code.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 查询集: 不输出假的 diff", stdout.Length == 0, stdout);
    }

    /* ------------------------------------------------------------------ */
    /* 6. --allow-subset：只比较交集                                       */
    /* ------------------------------------------------------------------ */

    private static void AllowSubset(string root)
    {
        var queries = StandardQueries();
        var baseline = WriteBaseline(root, "subset-baseline.json", HashOf(queries), queries);

        // 6a. run 只保留 Q0001 / Q0003（ids 相同）：交集内完全一致 → 0。
        var subset = new[] { queries[0], queries[2] };
        var subsetRun = WriteBaseline(root, "subset-run.json", HashOf(subset), subset);

        var (strictCode, strictStdout) = RunReplay("compare", "--baseline", baseline, "--run", subsetRun);
        Program.Check("compare 子集: 未加开关时查询集不同退出 1", strictCode == 1,
            strictCode.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 子集: 未加开关不输出 diff", strictStdout.Length == 0, strictStdout);

        var (code, stdout) = RunReplay(
            "compare", "--baseline", baseline, "--run", subsetRun, "--allow-subset");
        Program.Check("compare 子集: --allow-subset 交集一致退出 0", code == 0,
            code.ToString(CultureInfo.InvariantCulture));
        var aggregate = ReadAggregate(stdout);
        Program.Check("compare 子集: 只统计交集 2 条", aggregate.Identical && aggregate.Changed == 0, stdout);
        using (var document = JsonDocument.Parse(stdout))
        {
            Program.Check("compare 子集: queries.count=2",
                document.RootElement.GetProperty("queries").GetProperty("count").GetInt32() == 2, stdout);
            Program.Check("compare 子集: results 只含交集 id",
                document.RootElement.GetProperty("results").EnumerateObject()
                    .Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "Q0001", "Q0003" }),
                stdout);
        }

        // 6b. 交集内有差异：仍然退出 1，diff 只针对交集。
        var subsetChanged = CloneQueries(subset);
        (subsetChanged[0].Ids[0], subsetChanged[0].Ids[1]) =
            (subsetChanged[0].Ids[1], subsetChanged[0].Ids[0]);
        var changedRun = WriteBaseline(root, "subset-changed.json", HashOf(subsetChanged), subsetChanged);
        var (changedCode, changedStdout) = RunReplay(
            "compare", "--baseline", baseline, "--run", changedRun, "--allow-subset");
        Program.Check("compare 子集: 交集内有差异退出 1", changedCode == 1,
            changedCode.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 子集: 交集差异被定位", ReadAggregate(changedStdout).Changed == 2,
            changedStdout);

        // 6c. 没有任何共同查询：报错退出 1，不比较空集。
        var disjoint = new[] { ("Q9999", "不同查询", Ids("q9-", IdsPerQuery)) };
        var disjointRun = WriteBaseline(root, "subset-disjoint.json", HashOf(disjoint), disjoint);
        var (disjointCode, disjointStdout) = RunReplay(
            "compare", "--baseline", baseline, "--run", disjointRun, "--allow-subset");
        Program.Check("compare 子集: 交集为空退出 1", disjointCode == 1,
            disjointCode.ToString(CultureInfo.InvariantCulture));
        Program.Check("compare 子集: 交集为空不输出 diff", disjointStdout.Length == 0, disjointStdout);
    }

    /* ------------------------------------------------------------------ */
    /* 7. 参数 / 数据错误                                                  */
    /* ------------------------------------------------------------------ */

    private static void InvalidInputs(string root)
    {
        var queries = StandardQueries();
        var hash = HashOf(queries);
        var baseline = WriteBaseline(root, "invalid-baseline.json", hash, queries);
        var run = WriteBaseline(root, "invalid-run.json", hash, queries);
        var missing = Path.Combine(root, "nope.json");

        Program.Check("compare 参数: 无参数返回 1", RunReplay("compare").Code == 1);
        Program.Check("compare 参数: 缺 --baseline 返回 1",
            RunReplay("compare", "--run", run).Code == 1);
        Program.Check("compare 参数: 缺 --run 返回 1",
            RunReplay("compare", "--baseline", baseline).Code == 1);
        Program.Check("compare 参数: --baseline 路径缺值返回 1",
            RunReplay("compare", "--baseline").Code == 1);
        Program.Check("compare 参数: 未知参数返回 1",
            RunReplay("compare", "--baseline", baseline, "--run", run, "--oops").Code == 1);
        Program.Check("compare 参数: 基线文件不存在返回 1",
            RunReplay("compare", "--baseline", missing, "--run", run).Code == 1);
        Program.Check("compare 参数: 结果文件不存在返回 1",
            RunReplay("compare", "--baseline", baseline, "--run", missing).Code == 1);

        var broken = WriteText(root, "invalid-broken.json", "{ not json");
        Program.Check("compare 数据: JSON 损坏返回 1",
            RunReplay("compare", "--baseline", broken, "--run", run).Code == 1);

        var schema2 = WriteText(root, "invalid-schema2.json",
            BaselineJson(hash, queries).Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal));
        Program.Check("compare 数据: schema != 1 返回 1",
            RunReplay("compare", "--baseline", schema2, "--run", run).Code == 1);

        var noSchema = WriteText(root, "invalid-noschema.json",
            BaselineJson(hash, queries).Replace("\"schema\":1,", "", StringComparison.Ordinal));
        Program.Check("compare 数据: 缺 schema 返回 1",
            RunReplay("compare", "--baseline", noSchema, "--run", run).Code == 1);

        var resultsArray = WriteText(root, "invalid-results-array.json",
            "{\"schema\":1,\"queries\":{\"count\":0,\"hash\":\"" + hash + "\"},\"results\":[]}");
        Program.Check("compare 数据: results 非对象返回 1",
            RunReplay("compare", "--baseline", resultsArray, "--run", run).Code == 1);

        var noResults = WriteText(root, "invalid-noresults.json",
            "{\"schema\":1,\"queries\":{\"count\":0,\"hash\":\"" + hash + "\"}}");
        Program.Check("compare 数据: 缺 results 返回 1",
            RunReplay("compare", "--baseline", noResults, "--run", run).Code == 1);

        var emptyResults = WriteText(root, "invalid-emptyresults.json",
            "{\"schema\":1,\"queries\":{\"count\":0,\"hash\":\"" + hash + "\"},\"results\":{}}");
        Program.Check("compare 数据: results 为空返回 1",
            RunReplay("compare", "--baseline", emptyResults, "--run", run).Code == 1);

        var idsNotArray = WriteText(root, "invalid-ids-not-array.json",
            "{\"schema\":1,\"queries\":{\"count\":1,\"hash\":\"" + hash
            + "\"},\"results\":{\"Q0001\":{\"ids\":\"a\",\"query\":\"晴天\"}}}");
        Program.Check("compare 数据: ids 非数组返回 1",
            RunReplay("compare", "--baseline", baseline, "--run", idsNotArray).Code == 1);

        var idsNotString = WriteText(root, "invalid-ids-not-string.json",
            "{\"schema\":1,\"queries\":{\"count\":1,\"hash\":\"" + hash
            + "\"},\"results\":{\"Q0001\":{\"ids\":[1,2],\"query\":\"晴天\"}}}");
        Program.Check("compare 数据: ids 含非字符串返回 1",
            RunReplay("compare", "--baseline", baseline, "--run", idsNotString).Code == 1);

        var noHash = WriteText(root, "invalid-nohash.json",
            "{\"schema\":1,\"queries\":{\"count\":3},\"results\":{\"Q0001\":{\"ids\":[]}}}");
        Program.Check("compare 数据: 缺 queries.hash 返回 1",
            RunReplay("compare", "--baseline", noHash, "--run", run).Code == 1);
    }

    /* ------------------------------------------------------------------ */
    /* 夹具                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>标准查询集：三条（含中文），每条 12 个 id。</summary>
    private static (string Id, string Query, string[] Ids)[] StandardQueries() => new[]
    {
        ("Q0001", "晴天", Ids("q1-", IdsPerQuery)),
        ("Q0002", "周杰伦", Ids("q2-", IdsPerQuery)),
        ("Q0003", "woaini", Ids("q3-", IdsPerQuery)),
    };

    private static string[] Ids(string prefix, int count) => Enumerable.Range(1, count)
        .Select(index => prefix + index.ToString("00", CultureInfo.InvariantCulture))
        .ToArray();

    /// <summary>深拷贝（数组另建），避免各用例互相污染夹具。</summary>
    private static (string Id, string Query, string[] Ids)[] CloneQueries(
        IReadOnlyList<(string Id, string Query, string[] Ids)> queries)
        => queries.Select(query => (query.Id, query.Query, query.Ids.ToArray())).ToArray();

    /// <summary>查询集 hash 走生产实现（与 <c>replay run</c> 同口径：id\tquery\tscope）。</summary>
    private static string HashOf(IEnumerable<(string Id, string Query, string[] Ids)> queries)
        => Replay.QueriesHash(queries.Select(query => (query.Id, query.Query, "all")));

    private static string WriteBaseline(
        string root, string name, string hash, IReadOnlyList<(string Id, string Query, string[] Ids)> queries)
        => WriteText(root, name, BaselineJson(hash, queries));

    private static string WriteText(string root, string name, string text)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>按 §6.7.4 结构造一份基线 JSON（含 schema / commit / corpus / queries / results）。</summary>
    private static string BaselineJson(
        string hash, IReadOnlyList<(string Id, string Query, string[] Ids)> queries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("commit", "test-commit");
            writer.WriteString("builtAt", "2026-11-24T00:00:00Z");

            writer.WritePropertyName("corpus");
            writer.WriteStartObject();
            writer.WriteString("kind", "replay");
            writer.WriteNumber("tracks", 0);
            writer.WriteString("hash", "corpus-test");
            writer.WriteEndObject();

            writer.WritePropertyName("queries");
            writer.WriteStartObject();
            writer.WriteNumber("count", queries.Count);
            writer.WriteString("hash", hash);
            writer.WriteEndObject();

            writer.WritePropertyName("results");
            writer.WriteStartObject();
            foreach (var query in queries)
            {
                writer.WritePropertyName(query.Id);
                writer.WriteStartObject();
                writer.WriteString("query", query.Query);
                writer.WritePropertyName("ids");
                writer.WriteStartArray();
                foreach (var id in query.Ids) writer.WriteStringValue(id);
                writer.WriteEndArray();
                writer.WriteNumber("total", -1);
                writer.WritePropertyName("p50Ms");
                writer.WriteRawValue("2.100");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private sealed record Aggregate(int Changed, int EmptyOld, int EmptyNew, double Overlap, bool Identical);

    /// <summary>运行子命令并捕获 stdout（diff 汇总 / 完整报告）；stderr 日志保持原样。</summary>
    private static (int Code, string Stdout) RunReplay(params string[] args)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var code = Replay.Run(args);
            return (code, writer.ToString().Trim());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static Aggregate ReadAggregate(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        var aggregate = document.RootElement.GetProperty("aggregate");
        return new Aggregate(
            aggregate.GetProperty("changed").GetInt32(),
            aggregate.GetProperty("emptyOld").GetInt32(),
            aggregate.GetProperty("emptyNew").GetInt32(),
            aggregate.GetProperty("overlap@10").GetDouble(),
            aggregate.GetProperty("identical").GetBoolean());
    }

    /// <summary>读单条查询的 changed 数组为 (id, oldRank, newRank) 列表。</summary>
    private static List<(string Id, int OldRank, int NewRank)> ReadChanges(JsonElement entry)
        => entry.GetProperty("changed").EnumerateArray()
            .Select(change => (
                change.GetProperty("id").GetString() ?? "",
                change.GetProperty("oldRank").GetInt32(),
                change.GetProperty("newRank").GetInt32()))
            .ToList();

    private static string DescribeChanges(IReadOnlyList<(string Id, int OldRank, int NewRank)> changes)
        => string.Join(',', changes.Select(change => $"{change.Id}:{change.OldRank}->{change.NewRank}"));
}
