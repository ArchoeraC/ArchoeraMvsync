// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P7：合并 + tombstone + 垃圾统计（设计文档 §3.3.0.7 / §3.8.2.3 / §5.3）。
/// 覆盖死行比例下降、内容正确、字节确定性、中止不半提交、重复 track 去重与输入校验。
/// </summary>
internal static class EraStoreMergeTests
{
    public static void Run()
    {
        DeadRowsDrop();
        ContentCorrect();
        ByteDeterminism();
        AbortBeforeCommit();
        DuplicateTrack();
        InputValidation();
        MergeTriggers();
        GarbageAccounting();
    }

    /// <summary>1. 墓碑 → 合并后死行清零，旧段不再被引用；verify 通过。</summary>
    private static void DeadRowsDrop()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = SixDocs();
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
            AddTombs(path, 0, new[] { 1 }); // seg0 的第 2 首死亡
            AddTombs(path, 1, new[] { 0 }); // seg1 的第 1 首死亡

            var before = IndexStats.Collect(path);
            Program.Check("合并前: DeadDocs=2", before.DeadDocs == 2, before.DeadDocs.ToString());
            Program.Check("合并前: DeadRatio=2/6",
                Math.Abs(before.DeadRatio - 2.0 / 6.0) < 1e-9, before.DeadRatio.ToString("F4"));
            Program.Check("合并前: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));

            var result = Merger.Merge(path, new[] { 0, 1, 2 });
            Program.Check("合并: LiveDocs=4", result.LiveDocs == 4, result.LiveDocs.ToString());
            Program.Check("合并: DroppedDead=2", result.DroppedDead == 2, result.DroppedDead.ToString());
            Program.Check("合并: NewSegment=3", result.NewSegment == 3, result.NewSegment.ToString());
            Program.Check("合并: NewGen=当前+1", result.NewGen == 4, result.NewGen.ToString());

            Manifest manifest;
            using (var file = IndexFile.Open(path))
            {
                manifest = file.Current!;
                Program.Check("合并: Gen 已提交", file.Gen == result.NewGen, file.Gen.ToString());
            }
            Program.Check("合并: 只引用新段",
                manifest.Sections.Count > 0 && manifest.Sections.All(s => s.SegmentId == 3),
                string.Join(',', manifest.Sections.Select(s => s.SegmentId).Distinct()));
            Program.Check("合并: DocCount=活 doc", manifest.DocCount == 4, manifest.DocCount.ToString());

            var stats = IndexStats.Collect(path);
            Program.Check("合并后: DeadDocs=0", stats.DeadDocs == 0, stats.DeadDocs.ToString());
            Program.Check("合并后: DeadRatio=0", stats.DeadRatio == 0, stats.DeadRatio.ToString("F4"));
            Program.Check("合并后: Docs=4", stats.Docs == 4, stats.Docs.ToString());

            var raw = File.ReadAllBytes(path);
            var store = new DocStoreReader(SectionBody(raw, Section(manifest, SectionKind.DocStore, 3)));
            Program.Check("合并后: 新段 DocStore=4", store.Count == 4, store.Count.ToString());

            var report = IndexVerifier.Verify(path);
            Program.Check("合并后: verify 通过", report.Ok, string.Join(" | ", report.Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>2. 合并后 DocStore 只含活 doc；df / postings / positions 与原始一致。</summary>
    private static void ContentCorrect()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, "alpha beta", "singer", (FieldId.Title, new[] { "alpha", "beta" })),
                MakeDoc(2, "alpha", "singer",
                    (FieldId.Title, new[] { "alpha" }), (FieldId.Artist, new[] { "singer" })),
                MakeDoc(3, "gamma", "other", (FieldId.Title, new[] { "gamma" })),
                MakeDoc(4, "alpha gamma", "other",
                    (FieldId.Title, new[] { "alpha", "gamma" }), (FieldId.Artist, new[] { "singer" })),
                MakeDoc(5, "delta", "other", (FieldId.Title, new[] { "delta" })),
            };
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
            AddTombs(path, 0, new[] { 0 }); // 丢掉 "alpha beta"

            var result = Merger.Merge(path, new[] { 0, 1, 2 });
            Program.Check("内容: LiveDocs=4", result.LiveDocs == 4, result.LiveDocs.ToString());

            var raw = File.ReadAllBytes(path);
            Manifest manifest;
            using (var file = IndexFile.Open(path))
                manifest = file.Current!;

            var store = new DocStoreReader(
                SectionBody(raw, Section(manifest, SectionKind.DocStore, (ushort)result.NewSegment)));
            Program.Check("内容: title 只含活 doc",
                store.Count == 4
                && store.Title(0) == "alpha" && store.Title(1) == "gamma"
                && store.Title(2) == "alpha gamma" && store.Title(3) == "delta",
                string.Join(',', Enumerable.Range(0, store.Count).Select(store.Title)));
            Program.Check("内容: track 按字节序重排",
                store.Track(0)[0] == 2 && store.Track(1)[0] == 3
                && store.Track(2)[0] == 4 && store.Track(3)[0] == 5,
                $"{store.Track(0)[0]}/{store.Track(1)[0]}/{store.Track(2)[0]}/{store.Track(3)[0]}");

            var dict = DictReader(raw, manifest, (ushort)result.NewSegment);
            var postings = new PostingsReader(
                SectionBody(raw, Section(manifest, SectionKind.Postings, (ushort)result.NewSegment)),
                SectionBody(raw, Section(manifest, SectionKind.Positions, (ushort)result.NewSegment)));

            var alpha = dict.TryFind(FieldId.Title, "alpha", out var alphaInfo);
            var alphaEntries = alpha ? postings.DecodeAll(alphaInfo.PostOffset, alphaInfo.Df) : new();
            Program.Check("内容: alpha df=2 且只含活 doc",
                alpha && alphaInfo.Df == 2
                && alphaEntries.Select(e => e.Doc).SequenceEqual(new[] { 0, 2 }),
                string.Join(',', alphaEntries.Select(e => e.Doc)));
            Program.Check("内容: alpha 位置与原始一致",
                alphaEntries.Count == 2
                && alphaEntries[0].Positions is [0] && alphaEntries[1].Positions is [0]);

            var gamma = dict.TryFind(FieldId.Title, "gamma", out var gammaInfo);
            var gammaEntries = gamma ? postings.DecodeAll(gammaInfo.PostOffset, gammaInfo.Df) : new();
            Program.Check("内容: gamma 位置 [0] / [1]",
                gamma && gammaInfo.Df == 2
                && gammaEntries[0].Positions is [0] && gammaEntries[1].Positions is [1],
                gamma ? string.Join('|', gammaEntries.Select(e => string.Join('.', e.Positions!))) : "miss");

            Program.Check("内容: 死 doc 的 beta 词条被丢弃",
                !dict.TryFind(FieldId.Title, "beta", out _));

            var singer = dict.TryFind(FieldId.Artist, "singer", out var singerInfo);
            var singerEntries = singer ? postings.DecodeAll(singerInfo.PostOffset, singerInfo.Df) : new();
            Program.Check("内容: artist singer df=2",
                singer && singerInfo.Df == 2 && singerEntries.Count == 2,
                singer ? $"df={singerInfo.Df}" : "miss");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>3. 相同输入重复构建 + 合并 → 逐字节一致。</summary>
    private static void ByteDeterminism()
    {
        var dirA = NewDir();
        var dirB = NewDir();
        try
        {
            var docs = SixDocs();
            var pathA = Path.Combine(dirA, "search-1.eraindex");
            var pathB = Path.Combine(dirB, "search-1.eraindex");
            foreach (var path in new[] { pathA, pathB })
            {
                IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
                AddTombs(path, 0, new[] { 0 });
                Merger.Merge(path, new[] { 0, 1, 2 }, new MergeOptions { Gen = 5 });
            }

            var bytesA = File.ReadAllBytes(pathA);
            var bytesB = File.ReadAllBytes(pathB);
            Program.Check("确定性: 合并后逐字节一致",
                bytesA.AsSpan().SequenceEqual(bytesB), $"{bytesA.Length}/{bytesB.Length}");
        }
        finally
        {
            Cleanup(dirA);
            Cleanup(dirB);
        }
    }

    /// <summary>4. Progress 在 commit 阶段抛异常 → 旧 Gen / 旧段不变，尾部垃圾不影响 verify。</summary>
    private static void AbortBeforeCommit()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 2));
            AddTombs(path, 0, new[] { 1 });

            Manifest before;
            using (var file = IndexFile.Open(path))
                before = file.Current!;
            var oldGen = before.Gen;
            var oldSections = before.Sections
                .Select(s => (s.Kind, s.SegmentId, s.Offset, s.Length, s.Docs))
                .ToList();

            var aborted = 0;
            var options = new MergeOptions
            {
                Progress = progress =>
                {
                    if (progress.Phase == "commit") { aborted++; throw new InvalidOperationException("测试中止"); }
                },
            };

            var threw = false;
            try
            {
                Merger.Merge(path, new[] { 0, 1, 2 }, options);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Program.Check("中止: 在 commit 回调抛出", threw && aborted == 1, $"threw={threw}/{aborted}");

            using (var file = IndexFile.Open(path))
            {
                Program.Check("中止: Gen 仍是旧值", file.Gen == oldGen, file.Gen.ToString());
                var now = file.Current!;
                Program.Check("中止: 段引用未变",
                    now.Sections.Select(s => (s.Kind, s.SegmentId, s.Offset, s.Length, s.Docs))
                        .SequenceEqual(oldSections),
                    $"{now.Sections.Count}/{oldSections.Count}");
            }

            var report = IndexVerifier.Verify(path);
            Program.Check("中止: verify 仍通过（尾部垃圾被忽略）", report.Ok,
                string.Join(" | ", report.Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>5. 重复 track：保留来源段号更大的版本，并计入 DroppedDuplicateTracks。</summary>
    private static void DuplicateTrack()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(7, "old", "s", (FieldId.Title, new[] { "old" })),
                MakeDoc(8, "eight", "s", (FieldId.Title, new[] { "eight" })),
                MakeDoc(9, "nine", "s", (FieldId.Title, new[] { "nine" })),
                MakeDoc(7, "new", "s", (FieldId.Title, new[] { "new" })),
            };
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));

            var result = Merger.Merge(path, new[] { 0, 1 });
            Program.Check("重复 track: LiveDocs=3", result.LiveDocs == 3, result.LiveDocs.ToString());
            Program.Check("重复 track: DroppedDuplicateTracks=1",
                result.DroppedDuplicateTracks == 1, result.DroppedDuplicateTracks.ToString());

            var raw = File.ReadAllBytes(path);
            Manifest manifest;
            using (var file = IndexFile.Open(path))
                manifest = file.Current!;
            var store = new DocStoreReader(
                SectionBody(raw, Section(manifest, SectionKind.DocStore, (ushort)result.NewSegment)));
            Program.Check("重复 track: 保留段号更大的副本",
                store.Count == 3 && store.Title(0) == "new"
                && Enumerable.Range(0, store.Count).All(i => store.Title(i) != "old"),
                string.Join(',', Enumerable.Range(0, store.Count).Select(store.Title)));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>6. 输入校验：空 / 重复 / 越界 / 不存在的段号一律 ArgumentException。</summary>
    private static void InputValidation()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 2));

            Program.Check("校验: 空列表抛 ArgumentException",
                ThrowsArgument(() => Merger.Merge(path, Array.Empty<int>())));
            Program.Check("校验: 重复段号抛 ArgumentException",
                ThrowsArgument(() => Merger.Merge(path, new[] { 0, 0 })));
            Program.Check("校验: 不存在段号抛 ArgumentException",
                ThrowsArgument(() => Merger.Merge(path, new[] { 9 })));
            Program.Check("校验: 负段号抛 ArgumentException",
                ThrowsArgument(() => Merger.Merge(path, new[] { -1 })));
            Program.Check("校验: 超过 ushort 抛 ArgumentException",
                ThrowsArgument(() => Merger.Merge(path, new[] { 70_000 })));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>7. ShouldMerge：段数 ≥8、死行 ≥20% 触发；健康小索引不触发。</summary>
    private static void MergeTriggers()
    {
        var dir = NewDir();
        try
        {
            var healthy = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(healthy, SixDocs(), Options(docsPerSegment: 2));
            Manifest healthyManifest;
            using (var file = IndexFile.Open(healthy))
                healthyManifest = file.Current!;

            Program.Check("触发: 健康小索引（3 段）不触发",
                !Merger.ShouldMerge(healthyManifest, 3));
            Program.Check("触发: 8 段触发", Merger.ShouldMerge(healthyManifest, 8));
            Program.Check("触发: DeadRatio 健康为 0", Merger.DeadRatio(healthyManifest) == 0);

            var dirty = Path.Combine(dir, "search-2.eraindex");
            IndexBuilder.Build(dirty, SixDocs(), Options(docsPerSegment: 2));
            AddTombs(dirty, 0, new[] { 0, 1 });
            Manifest dirtyManifest;
            using (var file = IndexFile.Open(dirty))
                dirtyManifest = file.Current!;

            Program.Check("触发: 死行 ≥20% 触发",
                Merger.DeadRatio(dirtyManifest) >= 0.2 && Merger.ShouldMerge(dirtyManifest, 3),
                Merger.DeadRatio(dirtyManifest).ToString("F4"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>8. 垃圾统计：FileBytes 增大、LiveBytes 只算新段、OverheadBytes &gt; 0。</summary>
    private static void GarbageAccounting()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 2));
            AddTombs(path, 0, new[] { 1 });
            AddTombs(path, 1, new[] { 0 });

            var before = IndexStats.Collect(path);
            var result = Merger.Merge(path, new[] { 0, 1, 2 });
            var after = IndexStats.Collect(path);

            Program.Check("垃圾: FileBytes 增大",
                after.FileBytes > before.FileBytes, $"{before.FileBytes} -> {after.FileBytes}");

            Manifest manifest;
            using (var file = IndexFile.Open(path))
                manifest = file.Current!;
            var liveFromSections = manifest.Sections.Sum(s => (long)s.Length);
            Program.Check("垃圾: LiveBytes 只含新段",
                after.LiveBytes == liveFromSections
                && manifest.Sections.All(s => s.SegmentId == result.NewSegment),
                $"{after.LiveBytes}/{liveFromSections}");
            Program.Check("垃圾: LiveBytes 小于合并前（旧段不计）",
                after.LiveBytes < before.LiveBytes,
                $"{before.LiveBytes} -> {after.LiveBytes}");
            Program.Check("垃圾: OverheadBytes > 0", after.OverheadBytes > 0,
                after.OverheadBytes.ToString());
            Program.Check("垃圾: Tombs 不再出现在 stats 段列表",
                after.Sections.All(s => s.Kind != SectionKind.Tombs));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    private static List<AnalyzedDoc> SixDocs() => new()
    {
        MakeDoc(1, "w1", "s", (FieldId.Title, new[] { "w1" })),
        MakeDoc(2, "w2", "s", (FieldId.Title, new[] { "w2" })),
        MakeDoc(3, "w3", "s", (FieldId.Title, new[] { "w3" })),
        MakeDoc(4, "w4", "s", (FieldId.Title, new[] { "w4" })),
        MakeDoc(5, "w5", "s", (FieldId.Title, new[] { "w5" })),
        MakeDoc(6, "w6", "s", (FieldId.Title, new[] { "w6" })),
    };

    /// <summary>向指定段追加墓碑并提交新 gen（模拟增量刷新的删除）。</summary>
    private static Manifest AddTombs(string path, ushort segmentId, int[] deadDocs)
    {
        using var file = IndexFile.Open(path);
        var manifest = file.Current ?? throw new InvalidOperationException("夹具没有有效提交");
        var body = Tombstones.Write(deadDocs);
        var appended = file.AppendSection(SectionKind.Tombs, body, (uint)deadDocs.Length);
        manifest.Sections.Add(new SectionRef
        {
            Kind = appended.Kind,
            SegmentId = segmentId,
            Offset = appended.Offset,
            Length = appended.Length,
            Crc = appended.Crc,
            Docs = appended.Docs,
        });
        manifest.Gen = file.Gen + 1;
        manifest.LiveBytes += appended.Length;
        IndexBuilder.FillFileBytes(manifest, file.Length);
        file.Commit(manifest);
        return manifest;
    }

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
        CreatedMs = 1_700_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static AnalyzedDoc MakeDoc(
        int seed, string title, string artist, params (FieldId Field, string[] Tokens)[] fields)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(seed + i);
            rev[i] = (byte)(seed * 7 + i);
        }

        var tokens = new Dictionary<FieldId, string[]>();
        foreach (var (field, terms) in fields) tokens[field] = terms;

        return new AnalyzedDoc(track, rev, (ushort)(seed & 0x7), (byte)(seed & 0x03), tokens,
            title, artist, "album" + seed);
    }

    private static TermDictReader DictReader(byte[] raw, Manifest manifest, ushort segmentId) =>
        new(SectionBody(raw, Section(manifest, SectionKind.Dict, segmentId)),
            SectionBody(raw, Section(manifest, SectionKind.DictIndex, segmentId)));

    private static SectionRef Section(Manifest manifest, SectionKind kind, ushort segmentId)
    {
        foreach (var section in manifest.Sections)
            if (section.Kind == kind && section.SegmentId == segmentId) return section;
        throw new InvalidOperationException($"缺少段 {kind}（seg={segmentId}）");
    }

    private static byte[] SectionBody(byte[] file, SectionRef section) =>
        file.AsSpan((int)section.Offset, (int)section.Length).ToArray();

    private static bool ThrowsArgument(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-merge-" + Guid.NewGuid().ToString("N"));
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
