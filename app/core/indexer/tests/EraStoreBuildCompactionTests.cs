// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T3.15：全量构建后的**有界自动压缩**。
///
/// <para>覆盖：小段库压缩后段数下降、查询 <c>(track, score)</c> 完全一致、Manifest 查询元数据
/// （metaVersion / optionsBits / capabilities / anchors / generation）保持有效；低于触发线
/// 完全跳过；轮数 / 时间 / 单轮 doc 预算生效；合并提交前异常不改变上一版世代。</para>
/// </summary>
internal static class EraStoreBuildCompactionTests
{
    private const int DocCount = 180;
    private const int DocsPerSegment = 2;

    /// <summary>查询清单：单词 / AND / 前缀 / 列作用域 / 单 doc 唯一词 / 空命中。</summary>
    private static readonly (string Name, string Expr)[] Expressions =
    {
        ("单词", "\"alpha\""),
        ("AND", "\"alpha\" AND \"beta\""),
        ("前缀", "\"gam\"*"),
        ("列作用域", "{artist_tok} : \"singer\""),
        ("唯一词", "\"uniq17\""),
        ("空命中", "\"未登录词\""),
    };

    public static void Run()
    {
        CompactsTinySegments();
        CompactsUniformMediumSegments();
        SkipsSmallIndex();
        RespectsRoundAndDocBudget();
        AbortKeepsPreviousGeneration();
    }

    /// <summary>
    /// 1. 90 个 2-doc 小段 → 一轮压到 1 段：段数下降、每条查询的 track/score 不变、
    /// Manifest 的查询元数据与统计仍有效。
    /// </summary>
    private static void CompactsTinySegments()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(), Options(DocsPerSegment));
            var beforeManifest = ReadManifest(path, out var beforeGen);
            var beforeSegments = Segments(beforeManifest);
            Program.Check("构建压缩: 夹具为 90 个小段",
                beforeSegments == DocCount / DocsPerSegment, beforeSegments.ToString());

            var beforeQueries = CaptureQueries(path);

            var result = BuildCompactor.Compact(path, new BuildCompactionOptions
            {
                MinSegments = Merger.MergeTriggerSegments,
                TargetSegments = Merger.MergeTriggerSegments,
                MaxRounds = 2,
                BudgetSeconds = 60,
                MaxDocsPerRound = DocCount * 2,
                InitialDocsPerSecond = 1_000_000,
            });

            Program.Check("构建压缩: 执行过合并", result.Compacted, result.Rounds.ToString());
            Program.Check("构建压缩: SegmentsBefore=90",
                result.SegmentsBefore == beforeSegments, result.SegmentsBefore.ToString());
            Program.Check("构建压缩: 段数降至触发线以下",
                result.SegmentsAfter < Merger.MergeTriggerSegments
                    && result.SegmentsAfter < result.SegmentsBefore,
                $"{result.SegmentsBefore} -> {result.SegmentsAfter}");
            Program.Check("构建压缩: 一轮吃下全部小段",
                result.Rounds == 1 && result.MergedSegments == beforeSegments,
                $"{result.Rounds}/{result.MergedSegments}");

            CheckQueriesIdentical("构建压缩", beforeQueries, CaptureQueries(path));

            var afterManifest = ReadManifest(path, out var afterGen);
            Program.Check("构建压缩: 段数与结果一致",
                Segments(afterManifest) == result.SegmentsAfter,
                Segments(afterManifest).ToString());
            Program.Check("构建压缩: Gen 递增", afterGen > beforeGen, $"{beforeGen}->{afterGen}");
            CheckMetadataPreserved(beforeManifest, afterManifest);
            Program.Check("构建压缩: DocCount 不变",
                afterManifest.DocCount == DocCount, afterManifest.DocCount.ToString());

            var report = IndexVerifier.Verify(path);
            Program.Check("构建压缩: verify 通过", report.Ok,
                string.Join(" | ", report.Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 1b. 真实全量构建的形状：每段 70 doc（&gt;64 的绝对下限）且段大小均匀。
    /// 这类段不会被「只归并小段」的相对阈值选中，必须靠全量构建压缩策略显式覆盖。
    /// </summary>
    private static void CompactsUniformMediumSegments()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            const int docs = 70 * 30; // 30 个 70-doc 段
            IndexBuilder.Build(path, Corpus(docs), Options(70));
            var before = ReadManifest(path, out _);
            var beforeSegments = Segments(before);
            var beforeQueries = CaptureQueries(path);
            Program.Check("构建压缩(均匀段): 夹具 30 段 / 每段 70 doc",
                beforeSegments == 30, beforeSegments.ToString());

            var result = BuildCompactor.Compact(path, new BuildCompactionOptions
            {
                BudgetSeconds = 60,
                MaxDocsPerRound = docs * 2,
                InitialDocsPerSecond = 1_000_000,
            });

            Program.Check("构建压缩(均匀段): 段数下降",
                result.Compacted && result.SegmentsAfter < Merger.MergeTriggerSegments
                    && result.SegmentsAfter < result.SegmentsBefore,
                $"{result.SegmentsBefore} -> {result.SegmentsAfter}");
            CheckQueriesIdentical("构建压缩(均匀段)", beforeQueries, CaptureQueries(path));

            var report = IndexVerifier.Verify(path);
            Program.Check("构建压缩(均匀段): verify 通过", report.Ok,
                string.Join(" | ", report.Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>2. 段数低于触发线（24）时完全不动索引，连 Gen 都不变。</summary>
    private static void SkipsSmallIndex()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus().Take(6).ToList(), Options(docsPerSegment: 3));
            var before = ReadManifest(path, out var beforeGen);
            Program.Check("构建压缩: 小索引夹具 2 段", Segments(before) == 2, Segments(before).ToString());

            var result = BuildCompactor.Compact(path, new BuildCompactionOptions
            {
                BudgetSeconds = 60,
                InitialDocsPerSecond = 1_000_000,
            });

            var after = ReadManifest(path, out var afterGen);
            Program.Check("构建压缩: 小索引跳过",
                !result.Compacted && result.Rounds == 0 && result.SegmentsAfter == 2,
                $"{result.Compacted}/{result.Rounds}/{result.SegmentsAfter}");
            Program.Check("构建压缩: 小索引 Gen 不变", afterGen == beforeGen,
                $"{beforeGen}->{afterGen}");
            Program.Check("构建压缩: 小索引段数不变", Segments(after) == 2, Segments(after).ToString());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 3. 单轮 doc 预算 / 轮数上限生效：每轮只吃 20 doc，最多 2 轮——段数下降但不要求一轮到底，
    /// 查询语义仍与压缩前一致。
    /// </summary>
    private static void RespectsRoundAndDocBudget()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(), Options(DocsPerSegment));
            var beforeQueries = CaptureQueries(path);

            var result = BuildCompactor.Compact(path, new BuildCompactionOptions
            {
                MaxRounds = 2,
                BudgetSeconds = 60,
                MaxDocsPerRound = 20,
                InitialDocsPerSecond = 1_000_000,
            });

            Program.Check("构建压缩: 轮数上限=2", result.Rounds == 2, result.Rounds.ToString());
            Program.Check("构建压缩: 每轮只归并 20 doc",
                result.MergedSegments == 20, result.MergedSegments.ToString());
            Program.Check("构建压缩: 预算内未压到目标段数也停止",
                result.SegmentsAfter > Merger.MergeTriggerSegments
                    && result.SegmentsAfter < result.SegmentsBefore,
                $"{result.SegmentsBefore} -> {result.SegmentsAfter}");
            CheckQueriesIdentical("构建压缩(有界)", beforeQueries, CaptureQueries(path));

            var report = IndexVerifier.Verify(path);
            Program.Check("构建压缩(有界): verify 通过", report.Ok,
                string.Join(" | ", report.Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 4. 合并提交前抛异常（模拟崩溃 / 取消）：上一版 Manifest 仍有效，旧段仍可查询。
    /// </summary>
    private static void AbortKeepsPreviousGeneration()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(), Options(DocsPerSegment));
            var beforeQueries = CaptureQueries(path);
            var before = ReadManifest(path, out var beforeGen);
            var beforeSegments = Segments(before);

            var threw = false;
            try
            {
                BuildCompactor.Compact(path, new BuildCompactionOptions
                {
                    BudgetSeconds = 60,
                    MaxDocsPerRound = DocCount * 2,
                    InitialDocsPerSecond = 1_000_000,
                    Progress = p =>
                    {
                        // 「commit」阶段已写完全部追加数据，但尚未覆盖提交槽。
                        if (p.Phase == "merge-commit") throw new OperationCanceledException("模拟中断");
                    },
                });
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            Program.Check("构建压缩: 中断向上抛", threw, threw.ToString());
            var after = ReadManifest(path, out var afterGen);
            Program.Check("构建压缩: 中断后 Gen 不变", afterGen == beforeGen,
                $"{beforeGen}->{afterGen}");
            Program.Check("构建压缩: 中断后段数不变",
                Segments(after) == beforeSegments, Segments(after).ToString());
            CheckQueriesIdentical("构建压缩(中断)", beforeQueries, CaptureQueries(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    /// <summary>Manifest 查询元数据必须原样保留（Merger 只搬物理数据）。</summary>
    private static void CheckMetadataPreserved(Manifest before, Manifest after)
    {
        Program.Check("构建压缩: metaVersion 保留",
            after.MetaVersion == before.MetaVersion, $"{before.MetaVersion}/{after.MetaVersion}");
        Program.Check("构建压缩: optionsBits 保留",
            after.OptionsBits == before.OptionsBits, $"{before.OptionsBits}/{after.OptionsBits}");
        Program.Check("构建压缩: capabilities 保留",
            after.Capabilities.SequenceEqual(before.Capabilities, StringComparer.Ordinal),
            string.Join(',', after.Capabilities));
        Program.Check("构建压缩: anchors 保留",
            after.Anchors.IndexFormat == before.Anchors.IndexFormat
            && after.Anchors.NormalizerVersion == before.Anchors.NormalizerVersion
            && after.Anchors.TokenizerVersion == before.Anchors.TokenizerVersion
            && after.Anchors.AnalyzerVersion == before.Anchors.AnalyzerVersion
            && after.Anchors.SchemeVersion == before.Anchors.SchemeVersion
            && after.Anchors.DataPackId == before.Anchors.DataPackId,
            $"analyzer={after.Anchors.AnalyzerVersion} dataPack={after.Anchors.DataPackId}");
        Program.Check("构建压缩: LiveBytes 只统计保留段",
            after.LiveBytes > 0 && after.LiveBytes <= before.LiveBytes,
            $"{before.LiveBytes} -> {after.LiveBytes}");
    }

    /// <summary>同一表达式在压缩前后返回的 track 集合与 bm25 分数必须一致。</summary>
    private static Dictionary<string, Dictionary<string, double>> CaptureQueries(string path)
    {
        using var reader = new StoreReader(path);
        var captured = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (name, expr) in Expressions)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var candidate in EraQueryPlan.Execute(reader, expr, 0))
                map[candidate.TrackIdHex] = candidate.Score;
            captured[name] = map;
        }
        return captured;
    }

    private static void CheckQueriesIdentical(
        string label,
        Dictionary<string, Dictionary<string, double>> before,
        Dictionary<string, Dictionary<string, double>> after)
    {
        foreach (var (name, _) in Expressions)
        {
            var expected = before[name];
            var actual = after[name];
            var ok = expected.Count == actual.Count;
            var detail = $"{expected.Count}/{actual.Count}";
            if (ok)
            {
                foreach (var (track, score) in expected)
                {
                    if (!actual.TryGetValue(track, out var got))
                    {
                        ok = false;
                        detail = $"缺少 {track}";
                        break;
                    }
                    if (!Close(score, got))
                    {
                        ok = false;
                        detail = $"{track} {score:R}/{got:R}";
                        break;
                    }
                }
            }
            Program.Check($"{label}: 查询一致 {name}", ok, detail);
        }
    }

    private static bool Close(double a, double b)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-12 * scale;
    }

    private static Manifest ReadManifest(string path, out ulong gen)
    {
        using var file = IndexFile.Open(path);
        gen = file.Gen;
        return file.Current ?? throw new InvalidDataException("夹具没有有效提交");
    }

    private static int Segments(Manifest manifest)
    {
        var set = new HashSet<ushort>();
        foreach (var section in manifest.Sections)
            if (section.Kind == SectionKind.DocStore) set.Add(section.SegmentId);
        return set.Count;
    }

    /* ------------------------------------------------------------------ */
    /* 语料                                                                */
    /* ------------------------------------------------------------------ */

    private static List<AnalyzedDoc> Corpus(int count = DocCount)
    {
        var palette = new[] { "alpha", "beta", "gamma" };
        var artists = new[] { "singer", "other" };
        var docs = new List<AnalyzedDoc>(count);
        for (var i = 0; i < count; i++)
        {
            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = new[] { palette[i % 3], $"uniq{i}" },
                [FieldId.Artist] = new[] { artists[i % 2] },
                [FieldId.Lyrics] = new[]
                {
                    "alpha", "beta", palette[(i + 1) % 3], $"uniq{i}",
                },
            };
            docs.Add(new AnalyzedDoc(
                TrackBytes(i), RevBytes(i), (ushort)(i & 0x7), (byte)(i & 0x03), tokens,
                $"{palette[i % 3]} uniq{i}", artists[i % 2], $"album{i}"));
        }
        return docs;
    }

    private static byte[] TrackBytes(int index)
    {
        var track = new byte[16];
        // 前 4 字节大端写序号：>256 条语料的 track 也必须唯一，否则合并会按设计去重。
        var value = index + 1;
        for (var i = 0; i < 4; i++) track[i] = (byte)(value >> (8 * (3 - i)));
        for (var i = 4; i < 16; i++) track[i] = (byte)(index * 17 + i);
        return track;
    }

    private static byte[] RevBytes(int index)
    {
        var rev = new byte[16];
        for (var i = 0; i < 16; i++) rev[i] = (byte)(index * 31 + i * 7);
        return rev;
    }

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xE0 + i)).ToArray(),
        CreatedMs = 1_760_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
        AnalyzerVersion = 13,
        OptionsBits = ManifestOptionsBits.IndexLrc | ManifestOptionsBits.HasHan,
        Capabilities = new[] { "surface_text", "phonetic_meta" },
    };

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-buildcompact-" + Guid.NewGuid().ToString("N"));
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
