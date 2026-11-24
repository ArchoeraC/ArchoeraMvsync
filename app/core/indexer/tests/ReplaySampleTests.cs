// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// <c>indexer replay sample</c>（§6.7.1 / §6.7.6）测试：查询日志容错与频次累计、
/// 固定 14 条、加权无放回抽样的条数 / 权重 / 排除、tags 推导、确定性、参数校验。
///
/// 运行器是自研 <see cref="Program.Check"/>（非 xUnit）；临时目录为
/// <c>Path.GetTempPath()</c> + Guid，结束时清理。
/// </summary>
internal static class ReplaySampleTests
{
    /// <summary>固定 14 条代表查询（与 <c>Replay.FixedQueries</c> / qbench 同文本、同顺序）。</summary>
    private static readonly string[] FixedQueries =
    {
        "\u7231\u4F60",              // 爱你
        "\u6211\u597D\u7231\u4F60",  // 我好爱你
        "\u611B",                    // 愛
        "\u6211\u7231\u4F60",        // 我爱你
        "woaini",
        "wo",
        "wan",
        "love",
        "hello",
        "zongguo",
        "kurarin",
        "helo",
        "hlelo",
        "\u4F26\u5468",              // 伦周
    };

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-replay-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FilteringAndFrequency(root);
            FixedSet(root);
            ReplaySelection(root);
            DerivedCorpus(root);
            Tags(root);
            Determinism(root);
            InvalidArguments(root);
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
    /* 1. 逐行容错 / 空串 / 超长 / 去重累计频次                            */
    /* ------------------------------------------------------------------ */

    private static void FilteringAndFrequency(string root)
    {
        var log = Path.Combine(root, "filter.log.jsonl");
        var tooLong = "{\"q\":\"" + new string('a', 201) + "\"}";
        File.WriteAllLines(log, new[]
        {
            "{\"q\":\"晴天\"}",
            "{\"q\":\"晴天\"}",
            "{\"q\":\"晴天\"}",
            "{\"query\":\"晴天\",\"scope\":\"all\"}", // §6.7.1 键，与上面的 q 合并
            "{\"q\":\"晴天\",\"sc\":\"m\"}",           // 桌面端 metadata 范围
            "{\"q\":\" 晴天 \"}",                     // trim 后同键
            "not json",                               // 坏行
            "{\"q\":\"\"}",                           // 空串
            "{\"q\":\"   \"}",                        // 纯空白
            tooLong,                                  // > 200
            "[1,2]",                                  // 非对象
            "{\"n\":5}",                              // 缺查询串
            "",                                       // 空行：不算一条记录
        });

        var outPath = Path.Combine(root, "filter-queries.jsonl");
        var (code, stdout) = RunReplay("sample", "--log", log, "--out", outPath);
        Program.Check("sample 容错: 正常退出", code == 0, code.ToString());
        Program.Check("sample 容错: dropped=6", SummaryNumber(stdout, "dropped") == 6,
            SummaryNumber(stdout, "dropped").ToString());

        var entries = ReadEntries(outPath);
        var all = entries.Single(e => e.Query == "晴天" && e.Scope == "all");
        Program.Check("sample 频次: 晴天/all weight=5", all.Weight == 5, all.Weight.ToString());
        var metadata = entries.Single(e => e.Query == "晴天" && e.Scope == "metadata");
        Program.Check("sample 频次: 晴天/metadata weight=1", metadata.Weight == 1,
            metadata.Weight.ToString());
        Program.Check("sample 容错: 超长查询未入集",
            !entries.Any(e => e.Query.Length > 200), entries.Count.ToString());
        Program.Check("sample 容错: 空串 / 空白未入集",
            !entries.Any(e => e.Query.Trim().Length == 0), entries.Count.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 2. 固定 14 条：文本、顺序、id、source / scope / weight             */
    /* ------------------------------------------------------------------ */

    private static void FixedSet(string root)
    {
        var log = Path.Combine(root, "fixed.log.jsonl");
        File.WriteAllLines(log, new[] { "{\"q\":\"zzzunique\"}" });

        var outPath = Path.Combine(root, "fixed-queries.jsonl");
        var (code, _) = RunReplay("sample", "--log", log, "--out", outPath);
        Program.Check("sample 固定: 正常退出", code == 0, code.ToString());

        var entries = ReadEntries(outPath);
        Program.Check("sample 固定: 共 15 条（14 固定 + 1 回放）", entries.Count == 15,
            entries.Count.ToString());

        var fixedEntries = entries.Where(e => e.Source == "fixed").ToList();
        Program.Check("sample 固定: 条数=14", fixedEntries.Count == 14, fixedEntries.Count.ToString());
        for (var i = 0; i < FixedQueries.Length; i++)
        {
            var entry = fixedEntries[i];
            var labels = entry.Query == FixedQueries[i]
                && entry.Id == "Q" + (i + 1).ToString("0000")
                && entry.Scope == "all"
                && entry.Weight == 1
                && entry.Limit == 20;
            Program.Check($"sample 固定: Q{i + 1:0000} 文本/顺序/id/scope/weight",
                labels, $"{entry.Id} {entry.Query} {entry.Scope} w={entry.Weight} l={entry.Limit}");
        }

        var love = entries.Single(e => e.Query == "愛" && e.Source == "fixed");
        Program.Check("sample tags: 固定 愛 为 cjk + single-char",
            string.Join(',', love.Tags) == "cjk,single-char", string.Join(',', love.Tags));

        var replay = entries.Single(e => e.Source == "replay");
        Program.Check("sample 固定: 回放 id 从 Q0101 起", replay.Id == "Q0101", replay.Id);
    }

    /* ------------------------------------------------------------------ */
    /* 3. 回放条数 / 权重 / 与固定条目不重复                                */
    /* ------------------------------------------------------------------ */

    private static void ReplaySelection(string root)
    {
        var log = Path.Combine(root, "selection.log.jsonl");
        var lines = new List<string>();
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        // sel1..sel12 的频次分别为 1..12；另加固定查询 爱你（必须被回放集排除）。
        for (var i = 1; i <= 12; i++)
        {
            var query = "sel" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            weights[query] = i;
            for (var repeat = 0; repeat < i; repeat++)
                lines.Add("{\"q\":\"" + query + "\"}");
        }
        for (var repeat = 0; repeat < 3; repeat++)
            lines.Add("{\"q\":\"爱你\"}");
        File.WriteAllLines(log, lines);

        var outPath = Path.Combine(root, "selection-queries.jsonl");
        var (code, _) = RunReplay(
            "sample", "--log", log, "--out", outPath, "--replay", "4", "--limit", "7");
        Program.Check("sample 抽样: 正常退出", code == 0, code.ToString());

        var entries = ReadEntries(outPath);
        var replay = entries.Where(e => e.Source == "replay").ToList();
        Program.Check("sample 抽样: --replay 限制条数为 4", replay.Count == 4, replay.Count.ToString());
        Program.Check("sample 抽样: 固定条目仍为 14", entries.Count(e => e.Source == "fixed") == 14,
            entries.Count(e => e.Source == "fixed").ToString());
        Program.Check("sample 抽样: --limit 写入每条", replay.All(e => e.Limit == 7),
            string.Join(',', replay.Select(e => e.Limit)));
        Program.Check("sample 抽样: weight 等于频次",
            replay.All(e => weights.TryGetValue(e.Query, out var weight) && e.Weight == weight),
            string.Join(',', replay.Select(e => $"{e.Query}={e.Weight}")));
        Program.Check("sample 抽样: 回放不含固定查询", replay.All(e => !FixedQueries.Contains(e.Query)),
            string.Join(',', replay.Select(e => e.Query)));
        Program.Check("sample 抽样: 爱你 只出现一次（固定）",
            entries.Count(e => e.Query == "爱你") == 1,
            entries.Count(e => e.Query == "爱你").ToString());

        var ids = replay.Select(e => e.Id).ToList();
        Program.Check("sample 抽样: id 连续 Q0101..Q0104",
            ids.SequenceEqual(new[] { "Q0101", "Q0102", "Q0103", "Q0104" }),
            string.Join(',', ids));
        Program.Check("sample 抽样: 回放按 (scope, query) 排序",
            replay.Select(e => (e.Scope, e.Query))
                .SequenceEqual(replay.Select(e => (e.Scope, e.Query)).OrderBy(x => x.Scope, StringComparer.Ordinal)
                    .ThenBy(x => x.Query, StringComparer.Ordinal)),
            string.Join(',', replay.Select(e => $"{e.Scope}/{e.Query}")));
    }

    /* ------------------------------------------------------------------ */
    /* 3.1 日志不足 → 从 library.db 派生补齐                                */
    /* ------------------------------------------------------------------ */

    private static void DerivedCorpus(string root)
    {
        var db = Path.Combine(root, "derive-library.db");
        var audio = Path.Combine(root, "derive-audio");
        Directory.CreateDirectory(audio);
        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "d1", Path.Combine(audio, "d1.mp3"), "晴天",
                """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""", "故事的小黄花", 1000);
            EndToEnd.InsertTrack(conn, "d2", Path.Combine(audio, "d2.mp3"), "Love Story",
                """[{"name":"Taylor Swift"}]""", """{"name":"Fearless"}""", "we were both young", 2000);
            EndToEnd.InsertTrack(conn, "d3", Path.Combine(audio, "d3.mp3"), "星空",
                """[{"name":"五月天"}]""", """{"name":"人生海海"}""", "星空下许愿", 3000);
        }

        var log = Path.Combine(root, "derive.log.jsonl");
        File.WriteAllLines(log, new[] { "{\"q\":\"logonly\"}" });
        var out1 = Path.Combine(root, "derive-1.jsonl");
        var out2 = Path.Combine(root, "derive-2.jsonl");

        var (code1, stdout) = RunReplay(
            "sample", "--log", log, "--out", out1,
            "--library", db, "--with-lyrics", "--replay", "6", "--seed", "20261013");
        var (code2, _) = RunReplay(
            "sample", "--log", log, "--out", out2,
            "--library", db, "--with-lyrics", "--replay", "6", "--seed", "20261013");

        Program.Check("sample 派生: 正常退出", code1 == 0 && code2 == 0, $"{code1}/{code2}");
        Program.Check("sample 派生: 汇总报告 derived 条数", SummaryNumber(stdout, "derived") > 0,
            SummaryNumber(stdout, "derived").ToString());
        Program.Check("sample 派生: 固定 14 + 回放 6", ReadEntries(out1).Count == 20,
            ReadEntries(out1).Count.ToString());

        var entries = ReadEntries(out1);
        var replay = entries.Where(e => e.Source == "replay").ToList();
        Program.Check("sample 派生: 回放含日志条目 logonly", replay.Any(e => e.Query == "logonly"),
            string.Join(',', replay.Select(e => e.Query)));
        Program.Check("sample 派生: 日志不足部分由曲库补齐",
            replay.Count(e => e.Tags.Contains("derived", StringComparer.Ordinal))
                == SummaryNumber(stdout, "derived"),
            string.Join(',', replay.Select(e => e.Query)));
        Program.Check("sample 派生: 派生含标题 晴天", replay.Any(e => e.Query == "晴天"),
            string.Join(',', replay.Select(e => e.Query)));
        Program.Check("sample 派生: 派生含歌词首行 故事的小黄花",
            replay.Any(e => e.Query == "故事的小黄花"),
            string.Join(',', replay.Select(e => e.Query)));
        Program.Check("sample 派生: 查询文本不重复",
            entries.Select(e => e.Query).Distinct(StringComparer.Ordinal).Count() == entries.Count,
            string.Join(',', entries.Select(e => e.Query).Where((q, i) =>
                entries.Take(i).Any(prev => prev.Query == q))));

        Program.Check("sample 派生: 同输入 + 同 seed 字节一致",
            File.ReadAllBytes(out1).SequenceEqual(File.ReadAllBytes(out2)),
            new FileInfo(out1).Length + "/" + new FileInfo(out2).Length);
    }

    /* ------------------------------------------------------------------ */
    /* 4. tags：cjk / latin / mixed / single-char                          */
    /* ------------------------------------------------------------------ */

    private static void Tags(string root)
    {
        var log = Path.Combine(root, "tags.log.jsonl");
        File.WriteAllLines(log, new[]
        {
            "{\"q\":\"月\"}",     // cjk + single-char
            "{\"q\":\"a\"}",      // latin + single-char
            "{\"q\":\"x我\"}",     // mixed
            "{\"q\":\"abc\"}",    // latin
            "{\"q\":\"き\"}",     // 假名 → cjk + single-char
            "{\"q\":\"한\"}",     // 谚文 → cjk + single-char
        });

        var outPath = Path.Combine(root, "tags-queries.jsonl");
        var (code, _) = RunReplay("sample", "--log", log, "--out", outPath);
        Program.Check("sample tags: 正常退出", code == 0, code.ToString());

        var entries = ReadEntries(outPath);
        Program.Check("sample tags: 月 = cjk,single-char",
            TagsEqual(entries, "月", "cjk,single-char"), TagsOf(entries, "月"));
        Program.Check("sample tags: a = latin,single-char",
            TagsEqual(entries, "a", "latin,single-char"), TagsOf(entries, "a"));
        Program.Check("sample tags: x我 = cjk,latin,mixed",
            TagsEqual(entries, "x我", "cjk,latin,mixed"), TagsOf(entries, "x我"));
        Program.Check("sample tags: abc = latin",
            TagsEqual(entries, "abc", "latin"), TagsOf(entries, "abc"));
        Program.Check("sample tags: き = cjk,single-char",
            TagsEqual(entries, "き", "cjk,single-char"), TagsOf(entries, "き"));
        Program.Check("sample tags: 한 = cjk,single-char",
            TagsEqual(entries, "한", "cjk,single-char"), TagsOf(entries, "한"));
    }

    /* ------------------------------------------------------------------ */
    /* 5. 确定性与 seed 敏感性                                             */
    /* ------------------------------------------------------------------ */

    private static void Determinism(string root)
    {
        var log = Path.Combine(root, "determinism.log.jsonl");
        File.WriteAllLines(log, Enumerable.Range(1, 30)
            .Select(i => "{\"q\":\"det" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "\"}"));

        var out1 = Path.Combine(root, "det-1.jsonl");
        var out2 = Path.Combine(root, "det-2.jsonl");
        var out3 = Path.Combine(root, "det-3.jsonl");

        var (code1, _) = RunReplay("sample", "--log", log, "--out", out1, "--replay", "5", "--seed", "20261013");
        var (code2, _) = RunReplay("sample", "--log", log, "--out", out2, "--replay", "5", "--seed", "20261013");
        var (code3, _) = RunReplay("sample", "--log", log, "--out", out3, "--replay", "5", "--seed", "20261014");
        Program.Check("sample 确定性: 三次运行均成功", code1 == 0 && code2 == 0 && code3 == 0,
            $"{code1}/{code2}/{code3}");

        Program.Check("sample 确定性: 同输入 + 同 seed 字节一致",
            File.ReadAllBytes(out1).SequenceEqual(File.ReadAllBytes(out2)),
            new FileInfo(out1).Length + "/" + new FileInfo(out2).Length);

        var first = ReadEntries(out1).Where(e => e.Source == "replay").Select(e => e.Query).ToHashSet(StringComparer.Ordinal);
        var other = ReadEntries(out3).Where(e => e.Source == "replay").Select(e => e.Query).ToHashSet(StringComparer.Ordinal);
        Program.Check("sample 确定性: 不同 seed 结果集不同", !first.SetEquals(other),
            string.Join(',', first) + " | " + string.Join(',', other));
    }

    /* ------------------------------------------------------------------ */
    /* 6. 参数校验                                                         */
    /* ------------------------------------------------------------------ */

    private static void InvalidArguments(string root)
    {
        Program.Check("sample 参数: 缺 --log / --out 返回 1", RunReplay("sample").Code == 1);

        var missing = Path.Combine(root, "missing.log.jsonl");
        Program.Check("sample 参数: 日志不存在返回 1",
            RunReplay("sample", "--log", missing, "--out", Path.Combine(root, "x.jsonl")).Code == 1);

        var log = Path.Combine(root, "invalid.log.jsonl");
        File.WriteAllLines(log, new[] { "not json", "{\"q\":\"\"}" });
        var outPath = Path.Combine(root, "invalid-out.jsonl");

        Program.Check("sample 参数: --replay 0 返回 1",
            RunReplay("sample", "--log", log, "--out", outPath, "--replay", "0").Code == 1);
        Program.Check("sample 参数: --limit 0 返回 1",
            RunReplay("sample", "--log", log, "--out", outPath, "--limit", "0").Code == 1);
        Program.Check("sample 参数: 日志全部无效返回 1",
            RunReplay("sample", "--log", log, "--out", outPath).Code == 1);
        Program.Check("sample 参数: 失败时不写输出", !File.Exists(outPath));
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private sealed record Entry(
        string Id, string Query, string Scope, int Limit, string Source, int Weight, string[] Tags);

    /// <summary>运行子命令并捕获 stdout（汇总 JSON）；stderr 日志保持原样。</summary>
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

    private static List<Entry> ReadEntries(string path)
    {
        var entries = new List<Entry>();
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            entries.Add(new Entry(
                root.GetProperty("id").GetString()!,
                root.GetProperty("query").GetString()!,
                root.GetProperty("scope").GetString()!,
                root.GetProperty("limit").GetInt32(),
                root.GetProperty("source").GetString()!,
                root.GetProperty("weight").GetInt32(),
                root.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray()));
        }
        return entries;
    }

    private static int SummaryNumber(string stdout, string name)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.GetProperty(name).GetInt32();
    }

    private static string TagsOf(List<Entry> entries, string query)
        => string.Join(',', entries.Single(e => e.Query == query).Tags);

    private static bool TagsEqual(List<Entry> entries, string query, string expected)
        => TagsOf(entries, query) == expected;
}
