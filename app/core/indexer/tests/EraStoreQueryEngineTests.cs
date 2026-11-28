// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S3 / T3.0 <see cref="EraQueryPlan"/> 回归（多段 bm25 缺口）：
///
/// <para>同一批 doc 分别构建成「全部一段」与「每 7 条一段」，对一组代表性 MATCH 表达式
/// （单词 / 前缀 / 短语 / AND / OR / 列作用域 / 空命中 / 语法错误）逐条执行，
/// 断言候选 <c>(TrackIdHex, Score)</c> 序列与顺序完全一致——即 df 跨段累加、
/// <c>N = TotalDocs</c>、<c>avgRowLen</c> 全库口径、先收集后排序这些多段不变量成立。</para>
///
/// <para>另验证 AND 分支门控（未满足的 AND 分支不计分）、列作用域优先级（作用域只绑定
/// 紧随其后的 primary）与 <c>perStepTake</c> 的全局截断。</para>
/// </summary>
internal static class EraStoreQueryEngineTests
{
    private const int SegmentDocs = 7;

    /// <summary>表达式清单：名称用于断言标签。</summary>
    private static readonly (string Name, string Expr)[] Expressions =
    {
        ("单词", "\"晴天\""),
        ("单词-低频", "\"moonstone\""),
        ("前缀", "\"moon\"*"),
        ("短语", "\"晴天 晚安\""),
        ("AND", "\"晴天\" AND \"晚安\""),
        ("OR", "\"晴天\" OR \"月亮\""),
        ("列作用域", "{title_tok} : (\"晴天\" AND \"晚安\")"),
        ("列作用域+AND", "{artist_tok} : \"晴天\" OR \"月亮\""),
        ("嵌套作用域交集", "{title_tok artist_tok} : \"晴天\""),
        ("空命中", "\"未登录词\""),
    };

    public static void Run()
    {
        var dir = NewDir();
        try
        {
            RunCore(dir);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void RunCore(string dir)
    {
        var docs = Corpus();
        var oneDir = Path.Combine(dir, "one");
        var multiDir = Path.Combine(dir, "multi");
        Directory.CreateDirectory(oneDir);
        Directory.CreateDirectory(multiDir);
        var onePath = Path.Combine(oneDir, "search-1.eraindex");
        var multiPath = Path.Combine(multiDir, "search-1.eraindex");
        var one = IndexBuilder.Build(onePath, docs, Options(docs.Count + 1));
        var multi = IndexBuilder.Build(multiPath, docs, Options(SegmentDocs));

        Program.Check("QueryEngine 语料: 单段 1 段 / 多段 4 段",
            one.SegmentCount == 1 && multi.SegmentCount == 4,
            $"{one.SegmentCount}/{multi.SegmentCount}");

        using var singleReader = new StoreReader(onePath);
        using var multiReader = new StoreReader(multiPath);
        Program.Check("QueryEngine 语料: TotalDocs 一致",
            singleReader.TotalDocs == (uint)docs.Count
                && multiReader.TotalDocs == singleReader.TotalDocs,
            $"{singleReader.TotalDocs}/{multiReader.TotalDocs}");

        foreach (var (name, expr) in Expressions)
        {
            var single = EraQueryPlan.Execute(singleReader, expr, 0);
            var segmented = EraQueryPlan.Execute(multiReader, expr, 0);
            CheckSameSequence($"QueryEngine 单段/多段一致: {name}", single, segmented);
        }

        CheckEmptyHit(multiReader);
        CheckSyntaxError(multiReader);
        CheckColumnScope(multiReader, docs);
        CheckScopePrecedence(multiReader, docs);
        CheckAndGate(multiReader, docs);
        CheckTruncation(singleReader, multiReader);
    }

    /* ------------------------------------------------------------------ */
    /* 专项断言                                                            */
    /* ------------------------------------------------------------------ */

    private static void CheckEmptyHit(StoreReader reader)
    {
        var result = EraQueryPlan.Execute(reader, "\"未登录词\"", 0);
        Program.Check("QueryEngine 空命中返回空列表", result.Count == 0, result.Count.ToString());
    }

    private static void CheckSyntaxError(StoreReader reader)
    {
        List<EraCandidate> result;
        var threw = false;
        try
        {
            result = EraQueryPlan.Execute(reader, "\"晴天", 0);
        }
        catch (Exception)
        {
            threw = true;
            result = new List<EraCandidate>();
        }
        Program.Check("QueryEngine 语法错误不抛异常且返回空列表",
            !threw && result.Count == 0, threw ? "threw" : result.Count.ToString());
    }

    /// <summary>列作用域只影响命中列：{artist_tok} 晴天 只命中 artist 带该词的 doc。</summary>
    private static void CheckColumnScope(StoreReader reader, List<AnalyzedDoc> docs)
    {
        var scoped = EraQueryPlan.Execute(reader, "{artist_tok} : \"晴天\"", 0);
        var expected = new[] { Track(docs[0]), Track(docs[5]) };
        Program.Check("QueryEngine 列作用域只命中 artist 列",
            scoped.Count == expected.Length
                && scoped.All(c => expected.Contains(c.TrackIdHex)),
            string.Join(',', scoped.Select(c => c.TrackIdHex)));
    }

    /// <summary>
    /// 作用域优先级（与 FTS5 一致）：<c>{title} : "a" AND "b"</c> 只把 a 限定到 title；
    /// doc22 的「晴天」在 title、「月亮」在 artist，因此前者命中而后者不命中。
    /// </summary>
    private static void CheckScopePrecedence(StoreReader reader, List<AnalyzedDoc> docs)
    {
        var precedence = EraQueryPlan.Execute(reader, "{title_tok} : \"晴天\" AND \"月亮\"", 0);
        var grouped = EraQueryPlan.Execute(reader, "{title_tok} : (\"晴天\" AND \"月亮\")", 0);

        Program.Check("QueryEngine 作用域只绑定紧随其后的 primary",
            precedence.Any(c => c.TrackIdHex == Track(docs[22]))
                && grouped.All(c => c.TrackIdHex != Track(docs[22])),
            $"{precedence.Count}/{grouped.Count}");
        Program.Check("QueryEngine 括号作用域限定整体",
            grouped.Count == 2
                && grouped.Any(c => c.TrackIdHex == Track(docs[1]))
                && grouped.Any(c => c.TrackIdHex == Track(docs[3])),
            string.Join(',', grouped.Select(c => c.TrackIdHex)));
    }

    /// <summary>AND 分支门控：未满足的 AND 分支即使个别词有出现也不计分。</summary>
    private static void CheckAndGate(StoreReader reader, List<AnalyzedDoc> docs)
    {
        var doc0 = Track(docs[0]);
        var doc1 = Track(docs[1]);

        var gate = EraQueryPlan.Execute(reader, "(\"晴天\" AND \"月亮\") OR \"晚安\"", 0);
        var wan = EraQueryPlan.Execute(reader, "\"晚安\"", 0);
        var qing = EraQueryPlan.Execute(reader, "\"晴天\"", 0);
        var both = EraQueryPlan.Execute(reader, "\"晴天\" AND \"晚安\"", 0);
        var all = EraQueryPlan.Execute(reader, "\"晴天\" AND \"月亮\" AND \"晚安\"", 0);

        var gateDoc0 = ScoreOf(gate, doc0);
        var wanDoc0 = ScoreOf(wan, doc0);
        var qingDoc0 = ScoreOf(qing, doc0);
        var bothDoc0 = ScoreOf(both, doc0);

        Program.Check("QueryEngine 门控: 未满足 AND 分支不计分（= 仅 c 的分数）",
            gateDoc0 is { } g && wanDoc0 is { } w && Close(g, w),
            $"{gateDoc0}/{wanDoc0}");
        Program.Check("QueryEngine 门控: a AND c 两词都计分（严格低于单词分数）",
            bothDoc0 is { } b && wanDoc0 is { } w2 && qingDoc0 is { } q
                && b < w2 - 1e-12 && b < q - 1e-12,
            $"{bothDoc0}/{wanDoc0}/{qingDoc0}");
        Program.Check("QueryEngine 门控: OR 分支都满足时全部累加（= 三词 AND 分数）",
            ScoreOf(gate, doc1) is { } g1 && ScoreOf(all, doc1) is { } a1 && Close(g1, a1),
            $"{ScoreOf(gate, doc1)}/{ScoreOf(all, doc1)}");
    }

    /// <summary>perStepTake 的全局排序：两段构建的 top-3 分数序列一致，且来自全量最高分。</summary>
    private static void CheckTruncation(StoreReader single, StoreReader multi)
    {
        const string expr = "\"晴天\" OR \"晚安\" OR \"月亮\" OR \"moon\"*";
        var fullSingle = EraQueryPlan.Execute(single, expr, 0);
        var fullMulti = EraQueryPlan.Execute(multi, expr, 0);
        var topSingle = EraQueryPlan.Execute(single, expr, 3);
        var topMulti = EraQueryPlan.Execute(multi, expr, 3);

        Program.Check("QueryEngine 截断: 两段构建 top-3 长度一致",
            topSingle.Count == 3 && topMulti.Count == 3,
            $"{topSingle.Count}/{topMulti.Count}");
        Program.Check("QueryEngine 截断: top-3 分数序列一致",
            topSingle.Count == topMulti.Count
                && topSingle.Zip(topMulti).All(p => Close(p.First.Score, p.Second.Score)),
            string.Join(',', topSingle.Select(c => c.Score.ToString("R"))));
        Program.Check("QueryEngine 截断: 两段构建 top-3 均为全量最高分",
            topSingle.All(c => fullSingle.Any(f => f.TrackIdHex == c.TrackIdHex && Close(f.Score, c.Score)))
                && topMulti.All(c => fullMulti.Any(f => f.TrackIdHex == c.TrackIdHex && Close(f.Score, c.Score))));
    }

    /* ------------------------------------------------------------------ */
    /* 断言工具                                                            */
    /* ------------------------------------------------------------------ */

    private static void CheckSameSequence(
        string label, List<EraCandidate> single, List<EraCandidate> segmented)
    {
        var ok = single.Count == segmented.Count;
        var detail = $"{single.Count}/{segmented.Count}";
        if (ok)
        {
            for (var i = 0; i < single.Count; i++)
            {
                if (single[i].TrackIdHex != segmented[i].TrackIdHex)
                {
                    ok = false;
                    detail = $"#{i} track {single[i].TrackIdHex}/{segmented[i].TrackIdHex}";
                    break;
                }
                if (!Close(single[i].Score, segmented[i].Score))
                {
                    ok = false;
                    detail = $"#{i} score {single[i].Score:R}/{segmented[i].Score:R}";
                    break;
                }
            }
        }
        Program.Check(label, ok, detail);
    }

    private static double? ScoreOf(List<EraCandidate> candidates, string trackHex)
    {
        foreach (var candidate in candidates)
            if (candidate.TrackIdHex == trackHex) return candidate.Score;
        return null;
    }

    private static bool Close(double a, double b)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-12 * scale;
    }

    /* ------------------------------------------------------------------ */
    /* 语料                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 23 条确定性语料：doc0 含「晴天 + 晚安」不含「月亮」（AND 门控用例），
    /// doc1 三词齐全，doc22 只在 title 有「晴天」、artist 有「月亮」（作用域优先级用例）。
    /// </summary>
    private static List<AnalyzedDoc> Corpus()
    {
        var titles = new[]
        {
            new[] { "晴天", "晚安" },
            new[] { "晴天", "月亮", "晚安" },
            new[] { "晚安" },
            new[] { "月亮", "晴天" },
            new[] { "晴天", "晚安", "海边" },
            new[] { "晚安", "月亮" },
            new[] { "海边", "晴天" },
            new[] { "月光", "晚安" },
            new[] { "moonlight", "晚安" },
            new[] { "moonstone", "晴天" },
            new[] { "月光", "海边" },
            new[] { "晚安", "心情" },
            new[] { "moonlight", "月亮" },
            new[] { "晴天", "温柔" },
            new[] { "晚安", "远方" },
            new[] { "月光", "星光" },
            new[] { "晴天", "moonstone" },
            new[] { "晚安", "海边" },
            new[] { "moonlight", "晚安", "晴天" },
            new[] { "月亮", "moonstone" },
            new[] { "晴天", "晚安", "心情" },
            new[] { "晚安", "温柔" },
            new[] { "晚安", "海边" },
        };

        var artistPalette = new[] { "歌者", "月光", "星光", "海边" };
        var lyricPalette = new[] { "晴天", "晚安", "月亮", "温柔", "远方", "心情" };

        var docs = new List<AnalyzedDoc>(titles.Length);
        for (var i = 0; i < titles.Length; i++)
        {
            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = titles[i],
            };

            if (i == 0)
            {
                tokens[FieldId.Artist] = new[] { "晴天", "歌者" };
                tokens[FieldId.Lyrics] = new[] { "月光", "晚安" };
            }
            else if (i == 5)
            {
                tokens[FieldId.Artist] = new[] { "晴天" };
                tokens[FieldId.Lyrics] = new[] { "远方" };
            }
            else if (i == 22)
            {
                // 晴天 在 title、月亮 只在 artist：`{title} : "晴天" AND "月亮"` 应命中，
                // `{title} : ("晴天" AND "月亮")` 不应命中（作用域绑定范围不同）。
                tokens[FieldId.Title] = new[] { "晴天", "海边" };
                tokens[FieldId.Artist] = new[] { "月亮" };
                tokens[FieldId.Lyrics] = new[] { "晚安", "温柔" };
            }
            else
            {
                tokens[FieldId.Artist] = new[] { artistPalette[i % artistPalette.Length] };
                if (i % 3 != 0)
                {
                    tokens[FieldId.Lyrics] = new[]
                    {
                        lyricPalette[i % lyricPalette.Length],
                        lyricPalette[(i + 2) % lyricPalette.Length],
                    };
                }
            }

            docs.Add(new AnalyzedDoc(
                TrackBytes(i), RevBytes(i), (ushort)(i & 0x7), (byte)(i & 0x03), tokens,
                string.Join(' ', titles[i]),
                tokens.TryGetValue(FieldId.Artist, out var artist) ? string.Join(' ', artist) : "",
                "album" + i));
        }
        return docs;
    }

    private static byte[] TrackBytes(int index)
    {
        var track = new byte[16];
        track[0] = (byte)(index + 1);
        track[1] = 0xC7;
        for (var i = 2; i < 16; i++) track[i] = (byte)(index * 13 + i);
        return track;
    }

    private static byte[] RevBytes(int index)
    {
        var rev = new byte[16];
        for (var i = 0; i < 16; i++) rev[i] = (byte)(index * 29 + i * 3);
        return rev;
    }

    private static string Track(AnalyzedDoc doc) => Convert.ToHexString(doc.Track).ToLowerInvariant();

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x60 + i)).ToArray(),
        CreatedMs = 1_760_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
