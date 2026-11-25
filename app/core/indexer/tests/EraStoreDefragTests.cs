// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// A2：EraStore 去碎片 / 世代重写。
///
/// <para>覆盖：多段 + 垃圾索引整理后 <c>fileBytes ≈ liveBytes + Manifest</c>、
/// <c>stats.overheadBytes</c> 只剩 Manifest、<c>verify</c> 通过、查询（track id + score）
/// 与整理前完全一致；提交前中断不改 CURRENT / 旧文件；整理后增量写入仍正常；
/// 自动触发只在「可见垃圾」时发生（纯净小索引不动）。</para>
/// </summary>
internal static class EraStoreDefragTests
{
    private const int DocCount = 36;

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
        DefragsGarbageAndPreservesQueries();
        AbortBeforeSwitchKeepsOldGeneration();
        IncrementalAfterDefrag();
        AutoTriggerRequiresVisibleGarbage();
    }

    /// <summary>
    /// 1. 36 个 1-doc 段 → 合并成 1 段（文件里留下 36 段旧字节）→ 去碎片：
    /// 文件缩回活数据大小、overhead 只剩 Manifest、查询完全一致。
    /// </summary>
    private static void DefragsGarbageAndPreservesQueries()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(DocCount), Options(docsPerSegment: 1));
            var originalQueries = CaptureQueries(dir);
            var built = IndexStats.Collect(dir);
            var originalGen = built.Gen;

            MergeAll(path, DocCount);
            var garbage = IndexStats.Collect(dir);
            Program.Check("去碎片: 合并后确实有垃圾",
                garbage.OverheadBytes > garbage.LiveBytes * 0.2,
                $"overhead={garbage.OverheadBytes} live={garbage.LiveBytes}");
            Program.Check("去碎片: 合并后文件明显大于活数据",
                garbage.FileBytes > garbage.LiveBytes * 1.5,
                $"{garbage.LiveBytes} / {garbage.FileBytes}");
            CheckQueriesIdentical("去碎片(合并后)", originalQueries, CaptureQueries(dir));

            var result = Defragmenter.Defrag(path);

            Program.Check("去碎片: 执行过整理", result.Ran, result.Ran.ToString());
            Program.Check("去碎片: Gen 递增", result.Gen > originalGen, $"{originalGen}->{result.Gen}");
            Program.Check("去碎片: 返回的文件名就是 CURRENT",
                CurrentName(dir) == result.GenerationName, $"{CurrentName(dir)}/{result.GenerationName}");
            Program.Check("去碎片: 目录只留一个世代文件",
                GenerationFiles(dir).Length == 1, string.Join(',', GenerationFiles(dir)));

            var after = IndexStats.Collect(dir);
            Program.Check("去碎片: liveBytes 不变",
                after.LiveBytes == garbage.LiveBytes, $"{garbage.LiveBytes}->{after.LiveBytes}");
            Program.Check("去碎片: fileBytes 回落到活数据 + Manifest",
                after.FileBytes < garbage.FileBytes * 0.75
                    && after.FileBytes <= after.LiveBytes + EraFormat.DataStart + 64 * 1024,
                $"{garbage.FileBytes}->{after.FileBytes}（live={after.LiveBytes}）");

            var manifest = StoreReader.TryReadManifest(IndexPaths.Resolve(dir))
                ?? throw new InvalidDataException("整理后无有效 Manifest");
            var manifestBytes = manifest.ToJsonBytes().Length;
            Program.Check("去碎片: overheadBytes 只剩 Manifest",
                after.OverheadBytes == manifestBytes,
                $"overhead={after.OverheadBytes} manifest={manifestBytes}");
            Program.Check("去碎片: DocCount 不变", after.Docs == DocCount, after.Docs.ToString());

            CheckQueriesIdentical("去碎片", originalQueries, CaptureQueries(dir));
            var report = IndexVerifier.Verify(dir);
            Program.Check("去碎片: verify 通过", report.Ok, string.Join(" | ", report.Errors));
            Program.Check("去碎片: 段数保持 1", after.Segments == 1, after.Segments.ToString());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 2. 在 CURRENT 原子切换前注入中止：旧世代仍是当前、文件字节不变、
    /// 新文件（尚未被引用）被尽力清理。
    /// </summary>
    private static void AbortBeforeSwitchKeepsOldGeneration()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(DocCount), Options(docsPerSegment: 1));
            MergeAll(path, DocCount);

            var beforeQueries = CaptureQueries(dir);
            var beforeGen = IndexStats.Collect(dir).Gen;
            var beforeBytes = new FileInfo(IndexPaths.Resolve(dir)).Length;
            var beforeName = CurrentName(dir);
            var beforeFiles = GenerationFiles(dir);

            var threw = false;
            try
            {
                Defragmenter.Defrag(path, new DefragOptions
                {
                    Progress = p =>
                    {
                        // 新文件已提交、CURRENT 尚未切换：模拟进程在此刻崩溃。
                        if (p.Phase == "switch") throw new OperationCanceledException("模拟中断");
                    },
                });
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            Program.Check("去碎片(中断): 向上抛", threw, threw.ToString());
            Program.Check("去碎片(中断): CURRENT 未切换",
                CurrentName(dir) == beforeName, $"{beforeName}->{CurrentName(dir)}");
            Program.Check("去碎片(中断): 旧文件字节不变",
                new FileInfo(IndexPaths.Resolve(dir)).Length == beforeBytes,
                $"{beforeBytes}->{new FileInfo(IndexPaths.Resolve(dir)).Length}");
            Program.Check("去碎片(中断): 世代号不变",
                IndexStats.Collect(dir).Gen == beforeGen, IndexStats.Collect(dir).Gen.ToString());
            Program.Check("去碎片(中断): 未留下新世代",
                GenerationFiles(dir).SequenceEqual(beforeFiles, StringComparer.Ordinal),
                string.Join(',', GenerationFiles(dir)));

            CheckQueriesIdentical("去碎片(中断)", beforeQueries, CaptureQueries(dir));
            Program.Check("去碎片(中断): verify 通过", IndexVerifier.Verify(dir).Ok,
                string.Join(" | ", IndexVerifier.Verify(dir).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 3. 整理后的增量写入：新段号（旧最大 + 1）、段引用 / 锚点 / Manifest 元数据仍正常。
    /// </summary>
    private static void IncrementalAfterDefrag()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(DocCount), Options(docsPerSegment: 1));
            MergeAll(path, DocCount);
            var beforeDefrag = StoreReader.TryReadManifest(path)
                ?? throw new InvalidDataException("整理前无有效 Manifest");
            var maxSegment = MaxSegment(beforeDefrag);

            var result = Defragmenter.Defrag(path);
            Program.Check("去碎片(增量): 整理执行", result.Ran, result.Ran.ToString());

            var newDoc = MakeDoc(900, "afterdefrag", "singer",
                (FieldId.Title, new[] { "afterdefrag" }));
            var inc = IncrementalUpdater.Apply(
                dir, new[] { newDoc }, Array.Empty<byte[]>(),
                new IncrementalOptions { Merge = false, DocsPerSegment = 100 });

            Program.Check("去碎片(增量): 只新增 1 段", inc.SegmentsAdded == 1, inc.SegmentsAdded.ToString());
            Program.Check("去碎片(增量): 活 doc = 37", inc.LiveDocs == DocCount + 1, inc.LiveDocs.ToString());

            var manifest = StoreReader.TryReadManifest(IndexPaths.Resolve(dir))
                ?? throw new InvalidDataException("增量后无有效 Manifest");
            Program.Check("去碎片(增量): 新段号 = 旧最大 + 1",
                MaxSegment(manifest) == maxSegment + 1, $"{maxSegment}->{MaxSegment(manifest)}");
            Program.Check("去碎片(增量): anchors 保留",
                manifest.Anchors.AnalyzerVersion == beforeDefrag.Anchors.AnalyzerVersion
                && manifest.Anchors.TokenizerVersion == beforeDefrag.Anchors.TokenizerVersion
                && manifest.Anchors.DataPackId == beforeDefrag.Anchors.DataPackId,
                $"analyzer={manifest.Anchors.AnalyzerVersion} dataPack={manifest.Anchors.DataPackId}");
            Program.Check("去碎片(增量): metaVersion / optionsBits 保留",
                manifest.MetaVersion == beforeDefrag.MetaVersion
                && manifest.OptionsBits == beforeDefrag.OptionsBits,
                $"{manifest.MetaVersion}/{manifest.OptionsBits}");
            Program.Check("去碎片(增量): 新词可命中",
                Query(dir, "\"afterdefrag\"").SequenceEqual(new[] { TrackHex(900) }),
                string.Join(',', Query(dir, "\"afterdefrag\"")));
            Program.Check("去碎片(增量): 旧曲仍可命中",
                Query(dir, "\"uniq17\"").SequenceEqual(new[] { TrackHex(17) }),
                string.Join(',', Query(dir, "\"uniq17\"")));
            Program.Check("去碎片(增量): verify 通过", IndexVerifier.Verify(dir).Ok,
                string.Join(" | ", IndexVerifier.Verify(dir).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 4. 自动触发：纯净小索引（overhead 只是 Manifest）不动；出现垃圾且超过
    /// 20 % 活数据时整理；比例门槛生效（即使有垃圾，门槛调高也不动）。
    /// </summary>
    private static void AutoTriggerRequiresVisibleGarbage()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, Corpus(6), Options(docsPerSegment: 3));
            var clean = IndexStats.Collect(dir);
            Program.Check("自动触发: 纯净夹具 2 段 / 6 活 doc",
                clean.Segments == 2 && clean.Docs == 6, $"{clean.Segments}/{clean.Docs}");

            var bytes = new FileInfo(path).Length;
            var gen = clean.Gen;
            var skipped = Defragmenter.DefragIfGarbage(path, minOverheadRatio: 0.2);
            var afterSkip = new FileInfo(path);
            Program.Check("自动触发: 纯净小索引不动",
                !skipped.Ran && afterSkip.Length == bytes && IndexStats.Collect(dir).Gen == gen,
                $"{skipped.Ran}/{bytes}->{afterSkip.Length}/{gen}->{IndexStats.Collect(dir).Gen}");

            var skippedZero = Defragmenter.DefragIfGarbage(
                path, minOverheadRatio: 0.2, minGarbageBytes: 0);
            Program.Check("自动触发: 无垃圾时绝对下限为 0 也不动",
                !skippedZero.Ran && new FileInfo(path).Length == bytes,
                $"{skippedZero.Ran}/{bytes}->{new FileInfo(path).Length}");

            // 合并全部段：追加新段，旧段成为垃圾。
            MergeAll(path, clean.Segments);
            var garbled = IndexStats.Collect(dir);
            Program.Check("自动触发: 合并后产生可见垃圾",
                garbled.OverheadBytes > garbled.LiveBytes * 0.2,
                $"overhead={garbled.OverheadBytes} live={garbled.LiveBytes}");

            var greedy = Defragmenter.DefragIfGarbage(path, minOverheadRatio: 10.0, minGarbageBytes: 0);
            Program.Check("自动触发: 比例门槛未达到时不整理", !greedy.Ran, greedy.Ran.ToString());

            var ran = Defragmenter.DefragIfGarbage(path, minOverheadRatio: 0.2, minGarbageBytes: 0);
            Program.Check("自动触发: 超过 20% 活数据时整理", ran.Ran, $"{ran.FileBytesBefore}->{ran.FileBytesAfter}");
            Program.Check("自动触发: 整理后 overhead 只剩 Manifest",
                IndexStats.Collect(dir).OverheadBytes < 8 * 1024,
                IndexStats.Collect(dir).OverheadBytes.ToString());
            Program.Check("自动触发: verify 通过", IndexVerifier.Verify(dir).Ok,
                string.Join(" | ", IndexVerifier.Verify(dir).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    private static void MergeAll(string path, int segments)
    {
        Merger.Merge(path, Enumerable.Range(0, segments).ToList(), new MergeOptions());
    }

    private static Dictionary<string, Dictionary<string, double>> CaptureQueries(string indexPath)
    {
        using var reader = new StoreReader(IndexPaths.Resolve(indexPath));
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

    private static string[] Query(string indexPath, string expression)
    {
        using var reader = new StoreReader(IndexPaths.Resolve(indexPath));
        return EraQueryPlan.Execute(reader, expression, 0)
            .Select(candidate => candidate.TrackIdHex)
            .ToArray();
    }

    private static string CurrentName(string dir) => CurrentPointer.Read(dir) ?? "(null)";

    private static string[] GenerationFiles(string dir) =>
        Directory.GetFiles(dir, "search-*.eraindex")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static int MaxSegment(Manifest manifest) =>
        manifest.Sections.Select(s => (int)s.SegmentId).DefaultIfEmpty(-1).Max();

    /* ------------------------------------------------------------------ */
    /* 语料                                                                */
    /* ------------------------------------------------------------------ */

    private static List<AnalyzedDoc> Corpus(int count)
    {
        var docs = new List<AnalyzedDoc>(count);
        for (var i = 0; i < count; i++)
        {
            var doc = MakeDoc(i, "uniq" + i, i % 2 == 0 ? "singer" : "other",
                (FieldId.Title, new[] { new[] { "alpha", "beta", "gamma" }[i % 3], "uniq" + i }),
                (FieldId.Lyrics, new[] { "alpha", "beta", "uniq" + i }));
            docs.Add(doc);
        }
        return docs;
    }

    private static AnalyzedDoc MakeDoc(
        int index, string unique, string artist, params (FieldId Field, string[] Tokens)[] fields)
    {
        var tokens = new Dictionary<FieldId, string[]> { [FieldId.Artist] = new[] { artist } };
        foreach (var (field, values) in fields) tokens[field] = values;
        return new AnalyzedDoc(
            TrackBytes(index), RevBytes(index), (ushort)(index & 0x7), (byte)(index & 0x03), tokens,
            $"title {unique}", artist, $"album{index}");
    }

    private static byte[] TrackBytes(int index)
    {
        var track = new byte[16];
        var value = index + 1;
        for (var i = 0; i < 4; i++) track[i] = (byte)(value >> (8 * (3 - i)));
        for (var i = 4; i < 16; i++) track[i] = (byte)(index * 17 + i);
        return track;
    }

    private static string TrackHex(int index) =>
        Convert.ToHexString(TrackBytes(index)).ToLowerInvariant().PadLeft(32, '0');

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
        TokenizerVersion = 4,
        AnalyzerVersion = 15,
        OptionsBits = ManifestOptionsBits.IndexLrc | ManifestOptionsBits.HasHan,
        Capabilities = new[] { "surface_text", "phonetic_meta" },
    };

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-defrag-" + Guid.NewGuid().ToString("N"));
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
