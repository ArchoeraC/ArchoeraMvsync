// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 桶序测试（S2 / T2.1–T2.2）：证据合并规则 + 7 键比较器 + <c>vectors/rank/*.jsonl</c>。
///
/// <para>向量只断言**排序后的 trackId 顺序**，不断言 bm25 数值或内部 docId；
/// 合并规则（同 track 多步取最高 kind）用代码断言，避免把结构固化进 JSON。</para>
/// </summary>
internal static class SearchRankerTests
{
    public static void Run()
    {
        RunRankOrder();
        RunMergeRules();
        RunStepEvidence();
        RunVectors();
    }

    /* ------------------------------------------------------------------ */
    /* 档位与比较器                                                        */
    /* ------------------------------------------------------------------ */

    private static void RunRankOrder()
    {
        var kinds = Enum.GetValues<MatchKind>();
        for (var i = 0; i < kinds.Length; i++)
            Program.Check($"rank KindRank({kinds[i]}) == {i}", SearchRanker.KindRank(kinds[i]) == i,
                SearchRanker.KindRank(kinds[i]).ToString());

        var fields = Enum.GetValues<FieldKind>();
        for (var i = 0; i < fields.Length; i++)
            Program.Check($"rank FieldRank({fields[i]}) == {i}", SearchRanker.FieldRank(fields[i]) == i,
                SearchRanker.FieldRank(fields[i]).ToString());

        // 同档同覆盖同字段时：bm25 小者在前，bm25 相同用 docId 兜底。
        var lowBm25 = Make("a", 5, MatchKind.Exact, FieldKind.Title, 1.0, bm25: -3);
        var highBm25 = Make("b", 1, MatchKind.Exact, FieldKind.Title, 1.0, bm25: 1);
        Program.Check("rank bm25 优先于 docId", SearchRanker.Compare(lowBm25, highBm25) < 0);

        var docA = Make("a", 2, MatchKind.Exact, FieldKind.Title, 1.0, bm25: 0);
        var docB = Make("b", 7, MatchKind.Exact, FieldKind.Title, 1.0, bm25: 0);
        Program.Check("rank docId 最终稳定键", SearchRanker.Compare(docA, docB) < 0);

        // kind 优先于 bm25：高档即使 bm25 更差也排在前面。
        var exact = Make("a", 1, MatchKind.Exact, FieldKind.Lyrics, 1.0, bm25: 10);
        var typo = Make("b", 2, MatchKind.Typo, FieldKind.Title, 1.0, bm25: -10);
        Program.Check("rank kind 优先于 bm25", SearchRanker.Compare(exact, typo) < 0);

        // coverage 优先于 field 与 bm25。
        var full = Make("a", 1, MatchKind.Phrase, FieldKind.Lyrics, 1.0, bm25: 5);
        var half = Make("b", 2, MatchKind.Phrase, FieldKind.Title, 0.5, bm25: -5);
        Program.Check("rank coverage 优先于 field/bm25", SearchRanker.Compare(full, half) < 0);

        // 确定性：同一集合排序 10 次顺序一致。
        var list = new List<Candidate>
        {
            Make("c", 3, MatchKind.Prefix, FieldKind.Title, 1.0, bm25: 1),
            Make("a", 1, MatchKind.Exact, FieldKind.Lyrics, 1.0, bm25: 3),
            Make("b", 2, MatchKind.Exact, FieldKind.Title, 1.0, bm25: 2),
        };
        SearchRanker.Sort(list);
        var first = string.Join(',', list.Select(c => c.TrackId));
        var stable = true;
        for (var i = 0; i < 10; i++)
        {
            SearchRanker.Sort(list);
            stable &= string.Equals(first, string.Join(',', list.Select(c => c.TrackId)),
                StringComparison.Ordinal);
        }
        Program.Check("rank 排序确定性", stable, first);

        // T3.11：exactness 压过 bm25（同 kind / coverage / field）。
        var equalField = Make("a", 9, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: 10, exactness: SearchExactness.Equal);
        var approximate = Make("b", 1, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: -10, exactness: SearchExactness.None);
        Program.Check("rank exactness 优先于 bm25",
            SearchRanker.Compare(equalField, approximate) < 0,
            $"{Bucket(equalField)} vs {Bucket(approximate)}");

        // proximity 只在 exactness 打平时起作用（这里是打平：都 contains）。
        var near = Make("a", 9, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: 10, exactness: SearchExactness.Contains, proximity: 1);
        var far = Make("b", 1, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: -10, exactness: SearchExactness.Contains, proximity: 4);
        Program.Check("rank proximity 仅破 exactness 平局", SearchRanker.Compare(near, far) < 0,
            $"{Bucket(near)} vs {Bucket(far)}");

        // exactness 不同时 proximity 不能反转结论。
        var containsFar = Make("a", 9, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: 10, exactness: SearchExactness.StartsWith, proximity: 4);
        var approxNear = Make("b", 1, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: -10, exactness: SearchExactness.None, proximity: 0);
        Program.Check("rank exactness 优先于 proximity",
            SearchRanker.Compare(containsFar, approxNear) < 0,
            $"{Bucket(containsFar)} vs {Bucket(approxNear)}");

        // field 仍在 exactness 之前：标题「近似」排在艺人「相等」之前。
        var titleApprox = Make("a", 9, MatchKind.Exact, FieldKind.Title, 1.0,
            bm25: 10, exactness: SearchExactness.None);
        var artistEqual = Make("b", 1, MatchKind.Exact, FieldKind.Artist, 1.0,
            bm25: -10, exactness: SearchExactness.Equal);
        Program.Check("rank field 优先于 exactness",
            SearchRanker.Compare(titleApprox, artistEqual) < 0,
            $"{Bucket(titleApprox)} vs {Bucket(artistEqual)}");
    }

    /* ------------------------------------------------------------------ */
    /* 合并规则                                                            */
    /* ------------------------------------------------------------------ */

    private static void RunMergeRules()
    {
        // 高 kind 覆盖低 kind，并带走 pass。
        var target = Make("t", 9, MatchKind.Typo, FieldKind.Lyrics, 1.0, bm25: 0, pass: "fuzzy");
        var incoming = Make("t", 3, MatchKind.Exact, FieldKind.Title, 1.0, bm25: -2, pass: "lexical");
        SearchRanker.Merge(target, incoming);
        Program.Check("merge 高 kind 覆盖", target.Evidence.Kind == MatchKind.Exact,
            target.Evidence.Kind.ToString());
        Program.Check("merge pass 跟随证据", target.Pass == "lexical", target.Pass);
        Program.Check("merge bm25 取最小", target.Bm25 == -2, target.Bm25.ToString());
        Program.Check("merge docId 取最小", target.DocId == 3, target.DocId.ToString());

        // 低 kind 不得覆盖高 kind。
        var exact = Make("t", 1, MatchKind.Exact, FieldKind.Title, 1.0, bm25: 0, pass: "lexical");
        SearchRanker.Merge(exact, Make("t", 2, MatchKind.Typo, FieldKind.Title, 1.0, bm25: -5));
        Program.Check("merge 低 kind 不覆盖", exact.Evidence.Kind == MatchKind.Exact
            && exact.Pass == "lexical", $"{exact.Evidence.Kind}/{exact.Pass}");

        // 同 kind：coverage 取大、field 取小、exactness 取小。
        var sameKind = Make("t", 1, MatchKind.Prefix, FieldKind.Lyrics, 0.5, exactness: 2,
            pass: "first");
        SearchRanker.Merge(sameKind,
            Make("t", 2, MatchKind.Prefix, FieldKind.Title, 1.0, exactness: 1, pass: "second"));
        Program.Check("merge 同 kind coverage 取大", sameKind.Evidence.Coverage == 1.0,
            sameKind.Evidence.Coverage.ToString());
        Program.Check("merge 同 kind field 取小", sameKind.Evidence.Field == FieldKind.Title,
            sameKind.Evidence.Field.ToString());
        Program.Check("merge 同 kind exactness 取小", sameKind.Evidence.Exactness == 1,
            sameKind.Evidence.Exactness.ToString());
        Program.Check("merge 同 kind pass 跟随更优证据", sameKind.Pass == "second", sameKind.Pass);

        // Positioned：只保留两段证据都成立的结论。
        var positioned = Make("t", 1, MatchKind.Phrase, FieldKind.Lyrics, 1.0, positioned: true);
        SearchRanker.Merge(positioned,
            Make("t", 1, MatchKind.Phrase, FieldKind.Lyrics, 1.0, positioned: false));
        Program.Check("merge positioned 取 AND", !positioned.Evidence.Positioned,
            positioned.Evidence.Positioned.ToString());

        // 同 kind：proximity 取更小（更近）。
        var near = Make("t", 1, MatchKind.Prefix, FieldKind.Title, 1.0, proximity: 1);
        SearchRanker.Merge(near, Make("t", 2, MatchKind.Prefix, FieldKind.Title, 1.0, proximity: 4));
        Program.Check("merge 同 kind proximity 取更小", near.Evidence.Proximity == 1,
            near.Evidence.Proximity.ToString());
        Program.Check("merge 同 kind pass 跟随更近来源", near.Pass == "test", near.Pass);

        // T3.11 第二步：真实字段（Title/Artist/Album）参与同 kind 合并时仍取 FieldRank 更小者，
        // 并把 exactness / ExactChecked 一起带过来（演唱者相等不得被歌词档的 none 盖掉）。
        var artistHit = Make("t", 1, MatchKind.Exact, FieldKind.Artist, 1.0,
            exactness: SearchExactness.Equal, pass: "lexical", exactChecked: true);
        SearchRanker.Merge(artistHit, Make("t", 2, MatchKind.Exact, FieldKind.Lyrics, 1.0,
            exactness: SearchExactness.None, pass: "lyrics", exactChecked: true));
        Program.Check("merge 真实字段取 FieldRank 更小（Artist 胜过 Lyrics）",
            artistHit.Evidence.Field == FieldKind.Artist
            && artistHit.Evidence.Exactness == SearchExactness.Equal
            && artistHit.Evidence.ExactChecked,
            $"{artistHit.Evidence.Field}/{artistHit.Evidence.Exactness}/{artistHit.Evidence.ExactChecked}");
        // 反向顺序（先 Lyrics 后 Artist）同样收敛到 Artist 相等档 + 复核标志。
        var lyricsFirst = Make("t", 9, MatchKind.Exact, FieldKind.Lyrics, 1.0,
            exactness: SearchExactness.None, pass: "lyrics", exactChecked: true);
        SearchRanker.Merge(lyricsFirst, Make("t", 2, MatchKind.Exact, FieldKind.Artist, 1.0,
            exactness: SearchExactness.Equal, pass: "lexical", exactChecked: true));
        Program.Check("merge 真实字段与顺序无关（Lyrics → Artist）",
            lyricsFirst.Evidence.Field == FieldKind.Artist
            && lyricsFirst.Evidence.Exactness == SearchExactness.Equal
            && lyricsFirst.Evidence.ExactChecked,
            $"{lyricsFirst.Evidence.Field}/{lyricsFirst.Evidence.Exactness}");
        Program.Check("merge 未复核 + 复核：标志取 OR（explain 不再探测）",
            MergeFlag(), "false");
    }

    /// <summary>未复核证据与复核证据同 kind 合并后，<c>ExactChecked</c> 必须为真。</summary>
    private static bool MergeFlag()
    {
        var uncheckedEvidence = Make("t", 3, MatchKind.Prefix, FieldKind.Lyrics, 1.0);
        SearchRanker.Merge(uncheckedEvidence, Make("t", 4, MatchKind.Prefix, FieldKind.Title, 1.0,
            exactness: SearchExactness.None, exactChecked: true));
        return uncheckedEvidence.Evidence.ExactChecked
            && uncheckedEvidence.Evidence.Field == FieldKind.Title;
    }

    /* ------------------------------------------------------------------ */
    /* 步骤 → 证据                                                         */
    /* ------------------------------------------------------------------ */

    private static void RunStepEvidence()
    {
        var step = new Step("lexical@1", MatchKind.Exact, FieldFamily.Meta, MatchOp.And,
            Array.Empty<string>(), CoveredTerms: 2, Cap: 2000, Pass: "lexical",
            StepGate.None, Expression: "\"晴天\" AND \"晴\" AND \"天*\"");
        var evidence = SearchRanker.FromStep(step, termCount: 4);
        Program.Check("evidence coverage 2/4", evidence.Coverage == 0.5,
            evidence.Coverage.ToString());
        Program.Check("evidence family Meta → Title", evidence.Field == FieldKind.Title,
            evidence.Field.ToString());
        // T3.11 / T2.5：positioned 的事实来自引擎（短语 atom 被降级到纯交集时为 false），
        // 形状层不再推断，默认 true；SearchSession 用 EraQueryHits.AnyPhraseDowngraded 覆盖。
        Program.Check("evidence positioned 默认 true（引擎覆盖）", evidence.Positioned,
            evidence.Positioned.ToString());
        Program.Check("evidence 未复核 exactness=none",
            evidence.Exactness == SearchExactness.None, evidence.Exactness.ToString());

        var clamp = SearchRanker.FromStep(step, termCount: 1);
        Program.Check("evidence coverage 上限 1.0", clamp.Coverage == 1.0, clamp.Coverage.ToString());
        var zero = SearchRanker.FromStep(step, termCount: 0);
        Program.Check("evidence termCount=0 → coverage 0", zero.Coverage == 0, zero.Coverage.ToString());

        Program.Check("field Lyrics → Lyrics", SearchRanker.FieldOf(FieldFamily.Lyrics) == FieldKind.Lyrics);
        Program.Check("field Mixed 按歌词保守", SearchRanker.FieldOf(FieldFamily.Mixed) == FieldKind.Lyrics);
        Program.Check("field Reading 按歌词保守", SearchRanker.FieldOf(FieldFamily.Reading) == FieldKind.Lyrics);

        Program.Check("reading prefix → exactness 1", SearchRanker.ReadingExactness(ReadingRanker.PrefixTier) == 1);
        Program.Check("reading contains → exactness 2", SearchRanker.ReadingExactness(ReadingRanker.ContainsTier) == 2);
        Program.Check("reading other → exactness 3", SearchRanker.ReadingExactness(ReadingRanker.OtherTier) == 3);
    }

    /* ------------------------------------------------------------------ */
    /* 向量：vectors/rank/*.jsonl                                          */
    /* ------------------------------------------------------------------ */

    private static void RunVectors()
    {
        var dir = ResolveRankDir();
        if (dir is null)
        {
            Program.Check("rank vectors 目录存在", false, "找不到 tests/vectors/rank");
            return;
        }

        var files = Directory.GetFiles(dir, "*.jsonl");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Program.Check("rank vectors 非空", false, dir);
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var cases = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            Console.WriteLine($"-- rank/{fileName}");
            var lineNumber = 0;
            foreach (var raw in File.ReadAllLines(file, System.Text.Encoding.UTF8))
            {
                lineNumber++;
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;

                try
                {
                    RunVector(line, seenIds, $"{fileName}:{lineNumber}");
                    cases++;
                }
                catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
                {
                    Program.Check($"rank {fileName}:{lineNumber}", false, ex.Message);
                }
            }
        }

        if (cases == 0)
            Program.Check("rank vectors 含用例", false, dir);
        else
            Console.WriteLine($"rank vectors: {cases} cases / {files.Length} file(s)");
    }

    private static void RunVector(string line, HashSet<string> seenIds, string context)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = RequireString(root, "id", context);
        if (!seenIds.Add(id))
            throw new FormatException($"{context}: 重复 id {id}");

        var candidatesElement = RequireArray(root, "candidates", context);
        var candidates = new List<Candidate>(candidatesElement.GetArrayLength());
        foreach (var element in candidatesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{context}: candidates 元素必须是对象");

            var trackId = RequireString(element, "trackId", context);
            var docId = RequireInt(element, "docId", context);
            var kind = Enum.Parse<MatchKind>(RequireString(element, "kind", context), ignoreCase: false);
            var field = Enum.Parse<FieldKind>(RequireString(element, "field", context), ignoreCase: false);
            var coverage = RequireDouble(element, "coverage", context);
            var bm25 = RequireDouble(element, "bm25", context);
            var exactness = element.TryGetProperty("exactness", out _)
                ? RequireInt(element, "exactness", context)
                : 0;
            candidates.Add(new Candidate(docId, trackId,
                new Evidence(kind, field, coverage, exactness, Positioned: true), bm25, "test"));
        }

        var expect = new List<string>();
        foreach (var element in RequireArray(root, "expect", context).EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                throw new FormatException($"{context}: expect 必须是字符串数组");
            expect.Add(element.GetString()!);
        }

        SearchRanker.Sort(candidates);
        var actual = candidates.Select(c => c.TrackId).ToArray();
        Program.Check($"{id} 顺序", actual.SequenceEqual(expect, StringComparer.Ordinal),
            $"expected [{string.Join(',', expect)}] / actual [{string.Join(',', actual)}]");
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private static Candidate Make(
        string trackId, long docId, MatchKind kind, FieldKind field, double coverage,
        double bm25 = 0, int exactness = 0, bool positioned = true, string pass = "test",
        int proximity = SearchRanker.ProximityUnknown, bool exactChecked = false)
        => new(docId, trackId,
            new Evidence(kind, field, coverage, exactness, positioned, proximity, exactChecked),
            bm25, pass);

    /// <summary>前五键的可读形式（kind / coverage / field / exactness / proximity）。</summary>
    private static string Bucket(Candidate candidate)
    {
        var evidence = candidate.Evidence;
        return $"{SearchRanker.KindRank(evidence.Kind)}/{evidence.Coverage:G3}/"
            + $"{SearchRanker.FieldRank(evidence.Field)}/{evidence.Exactness}/{evidence.Proximity}";
    }

    private static string? ResolveRankDir()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(SearchRankerTests).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            var beside = Path.Combine(assemblyDir, "vectors", "rank");
            if (Directory.Exists(beside)) return beside;

            var found = WalkUp(new DirectoryInfo(assemblyDir));
            if (found is not null) return found;
        }
        return WalkUp(new DirectoryInfo(AppContext.BaseDirectory));
    }

    private static string? WalkUp(DirectoryInfo? start)
    {
        var dir = start;
        while (dir is not null)
        {
            var fromTests = Path.Combine(dir.FullName, "tests", "vectors", "rank");
            if (Directory.Exists(fromTests)) return fromTests;
            var fromVectors = Path.Combine(dir.FullName, "vectors", "rank");
            if (Directory.Exists(fromVectors)) return fromVectors;
            dir = dir.Parent;
        }
        return null;
    }

    private static string RequireString(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"{context}: 缺少字符串字段 {name}");
        return value.GetString()!;
    }

    private static int RequireInt(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
            throw new FormatException($"{context}: 缺少整数字段 {name}");
        return result;
    }

    private static double RequireDouble(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var result))
            throw new FormatException($"{context}: 缺少数字字段 {name}");
        return result;
    }

    private static JsonElement RequireArray(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{context}: 缺少数组字段 {name}");
        return value;
    }
}
