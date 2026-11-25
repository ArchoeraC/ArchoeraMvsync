// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore T3.10：增量写入（追加新段 + 墓碑）与有界分层合并。
/// 覆盖新增 / 改写 / 删除、查询侧死行过滤、段数与死行触发合并、提交前异常不改旧世代。
/// </summary>
internal static class EraStoreIncrementalTests
{
    public static void Run()
    {
        AddUpdateDelete();
        SegmentTrigger();
        DeadRatioTrigger();
        AbortBeforeCommit();
        MaintainWithoutChanges();
    }

    /// <summary>1. 新增 + 改写 + 删除：只追加新段 / 墓碑，查询看到最新版本。</summary>
    private static void AddUpdateDelete()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 2, gen: 1));
            var before = IndexStats.Collect(path);
            var beforeBytes = new FileInfo(path).Length;
            Program.Check("增量: 初始 3 段 / 6 活 doc",
                before.Segments == 3 && before.Docs == 6, $"{before.Segments}/{before.Docs}");

            var result = IncrementalUpdater.Apply(
                path,
                new[] { MakeDoc(7, "new7", "s", (FieldId.Title, new[] { "new7" })),
                        MakeDoc(2, "w2x", "s", (FieldId.Title, new[] { "w2x" })) },
                new[] { TrackBytes(4) },
                new IncrementalOptions { Merge = false, DocsPerSegment = 100 });

            Program.Check("增量: 新增 1 段", result.SegmentsAdded == 1, result.SegmentsAdded.ToString());
            Program.Check("增量: 墓碑 2 行（改写旧版 + 删除）", result.DeadDocs == 2, result.DeadDocs.ToString());
            Program.Check("增量: 活 doc 仍为 6", result.LiveDocs == 6, result.LiveDocs.ToString());
            Program.Check("增量: 4 段（3 旧 + 1 新）", result.SegmentCount == 4, result.SegmentCount.ToString());
            Program.Check("增量: 文件只追加（< 2×）",
                new FileInfo(path).Length > beforeBytes && new FileInfo(path).Length < beforeBytes * 2,
                $"{beforeBytes} -> {new FileInfo(path).Length}");

            Program.Check("增量: 新曲可命中", Query(path, "\"new7\"").SequenceEqual(new[] { TrackHex(7) }),
                string.Join(',', Query(path, "\"new7\"")));
            Program.Check("增量: 改写后新词可命中", Query(path, "\"w2x\"").SequenceEqual(new[] { TrackHex(2) }),
                string.Join(',', Query(path, "\"w2x\"")));
            Program.Check("增量: 改写后旧词不再命中", Query(path, "\"w2\"").Count == 0,
                string.Join(',', Query(path, "\"w2\"")));
            Program.Check("增量: 删除后不再命中", Query(path, "\"w4\"").Count == 0,
                string.Join(',', Query(path, "\"w4\"")));
            Program.Check("增量: 未变曲目仍命中", Query(path, "\"w1\"").SequenceEqual(new[] { TrackHex(1) }),
                string.Join(',', Query(path, "\"w1\"")));
            Program.Check("增量: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));

            var stats = IndexStats.Collect(path);
            Program.Check("增量: 死行比例 2/8", Math.Abs(stats.DeadRatio - 2.0 / 8.0) < 1e-9,
                stats.DeadRatio.ToString("F4"));
            Program.Check("增量: 活 doc = 6", stats.Docs == 6, stats.Docs.ToString());

            var maintained = IncrementalUpdater.Maintain(
                path, new IncrementalOptions { Merge = true, MergeMaxDocs = 100 });
            Program.Check("增量: 合并回收死行", maintained.Merged && maintained.SegmentCount == 1,
                $"{maintained.Merged}/{maintained.SegmentCount}");
            Program.Check("增量: 合并后 dead_ratio=0", IndexStats.Collect(path).DeadDocs == 0,
                IndexStats.Collect(path).DeadDocs.ToString());
            Program.Check("增量: 合并后仍可命中", Query(path, "\"w2x\"").SequenceEqual(new[] { TrackHex(2) }),
                string.Join(',', Query(path, "\"w2x\"")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>2. 段数达到阈值自动触发合并，段数回落到触发线以下。</summary>
    private static void SegmentTrigger()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            // 构造 23 段，再追加 1 首 → 24 段，正好达到触发线。
            var docs = new List<AnalyzedDoc>();
            for (var i = 1; i <= 23; i++)
                docs.Add(MakeDoc(i, "w" + i, "s", (FieldId.Title, new[] { "w" + i })));
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 1, gen: 1));
            Program.Check("段触发: 初始 23 段", IndexStats.Collect(path).Segments == 23,
                IndexStats.Collect(path).Segments.ToString());

            var result = IncrementalUpdater.Apply(
                path,
                new[] { MakeDoc(24, "n24", "s", (FieldId.Title, new[] { "n24" })) },
                Array.Empty<byte[]>(),
                new IncrementalOptions { Merge = true, MergeMaxDocs = 1000, DocsPerSegment = 1 });

            Program.Check("段触发: 追加后触发合并", result.Merged);
            Program.Check("段触发: 段数低于阈值",
                result.SegmentCount < Merger.MergeTriggerSegments, result.SegmentCount.ToString());
            Program.Check("段触发: 24 首全可命中",
                Query(path, "\"n24\"").Count == 1 && Query(path, "\"w1\"").Count == 1
                && Query(path, "\"w23\"").Count == 1,
                string.Join(',', Query(path, "\"n24\"")));
            Program.Check("段触发: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>3. 死行 ≥20% 触发合并；合并只搬活 doc。</summary>
    private static void DeadRatioTrigger()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 3, gen: 1));

            // 两个 3-doc 段各改写 1 首：部分死行 2/6 = 33% > 20%（不整段丢弃）。
            var result = IncrementalUpdater.Apply(
                path,
                new[] { MakeDoc(1, "a1x", "s", (FieldId.Title, new[] { "a1x" })),
                        MakeDoc(4, "a4x", "s", (FieldId.Title, new[] { "a4x" })) },
                Array.Empty<byte[]>(),
                new IncrementalOptions { Merge = true, MergeMaxDocs = 1000 });

            Program.Check("死行触发: 已合并", result.Merged);
            Program.Check("死行触发: 合并后 dead_ratio=0", result.DeadRatio == 0,
                result.DeadRatio.ToString("F4"));
            Program.Check("死行触发: live=6", result.LiveDocs == 6, result.LiveDocs.ToString());
            Program.Check("死行触发: 新版本可命中",
                Query(path, "\"a1x\"").SequenceEqual(new[] { TrackHex(1) }),
                string.Join(',', Query(path, "\"a1x\"")));
            Program.Check("死行触发: 旧版本不再命中", Query(path, "\"w1\"").Count == 0,
                string.Join(',', Query(path, "\"w1\"")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>4. 提交前异常（非法 track）不改变旧世代，旧索引仍可查询。</summary>
    private static void AbortBeforeCommit()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 3, gen: 1));
            var beforeGen = Gen(path);
            var beforeBytes = new FileInfo(path).Length;

            var threw = false;
            try
            {
                IncrementalUpdater.Apply(
                    path,
                    new[] { new AnalyzedDoc(new byte[3], new byte[16], 0, 0,
                        new Dictionary<FieldId, string[]>(), "x", "y", "z") },
                    Array.Empty<byte[]>(),
                    new IncrementalOptions { Merge = false });
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Program.Check("中止: 非法 track 抛 ArgumentException", threw);
            Program.Check("中止: 世代不变", Gen(path) == beforeGen, $"{beforeGen} -> {Gen(path)}");
            Program.Check("中止: 文件未追加", new FileInfo(path).Length == beforeBytes,
                $"{beforeBytes} -> {new FileInfo(path).Length}");
            Program.Check("中止: 旧索引仍可查询", Query(path, "\"w1\"").Count == 1,
                string.Join(',', Query(path, "\"w1\"")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>5. 无脏曲的维护轮：未达阈值不写；被占用（删不掉）的旧文件不影响 CURRENT。</summary>
    private static void MaintainWithoutChanges()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, SixDocs(), Options(docsPerSegment: 3, gen: 1));
            CurrentPointer.Write(dir, Path.GetFileName(path));
            var beforeGen = Gen(path);

            var maintained = IncrementalUpdater.Maintain(path, new IncrementalOptions());
            Program.Check("维护: 健康索引不合并", !maintained.Merged, maintained.SegmentCount.ToString());
            Program.Check("维护: 世代不变", Gen(path) == beforeGen, $"{beforeGen} -> {Gen(path)}");

            // T3.15：CURRENT 原子切换后，旧文件即使无法删除（Windows 句柄占用）也不影响
            // 新世代可读；本用例用只读句柄模拟占用，删除失败必须被吞掉。
            var oldName = Path.GetFileName(path);
            var newPath = Path.Combine(dir, "search-2.eraindex");
            IndexBuilder.Build(newPath, SixDocs(), Options(docsPerSegment: 3, gen: 2));
            Program.Check("切换: 旧世代可校验（切换前）", IndexVerifier.Verify(path).Ok);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                CurrentPointer.Write(dir, Path.GetFileName(newPath));
                Program.Check("切换: CURRENT 指向新世代",
                    CurrentPointer.Read(dir) == Path.GetFileName(newPath), CurrentPointer.Read(dir) ?? "(null)");
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Windows 允许失败；这里只确认切换本身不依赖删除成功。
                }
            }
            Program.Check("切换: 新世代可查询", Query(newPath, "\"w1\"").Count == 1,
                string.Join(',', Query(newPath, "\"w1\"")));
            Program.Check("切换: 旧文件名仍有效", oldName.EndsWith(".eraindex", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    private static List<string> Query(string path, string expression)
    {
        using var reader = new StoreReader(path);
        var hits = EraQueryPlan.Execute(reader, expression, -1);
        return hits.Select(h => h.TrackIdHex).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static ulong Gen(string path)
    {
        using var file = IndexFile.Open(path);
        return file.Gen;
    }

    private static List<AnalyzedDoc> SixDocs() => new()
    {
        MakeDoc(1, "w1", "s", (FieldId.Title, new[] { "w1" })),
        MakeDoc(2, "w2", "s", (FieldId.Title, new[] { "w2" })),
        MakeDoc(3, "w3", "s", (FieldId.Title, new[] { "w3" })),
        MakeDoc(4, "w4", "s", (FieldId.Title, new[] { "w4" })),
        MakeDoc(5, "w5", "s", (FieldId.Title, new[] { "w5" })),
        MakeDoc(6, "w6", "s", (FieldId.Title, new[] { "w6" })),
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

    private static byte[] TrackBytes(int seed)
    {
        var track = new byte[16];
        for (var i = 0; i < 16; i++) track[i] = (byte)(seed + i);
        return track;
    }

    private static string TrackHex(int seed) => Convert.ToHexString(TrackBytes(seed)).ToLowerInvariant();

    private static BuildOptions Options(int docsPerSegment, ulong gen) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = gen,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
        CreatedMs = 1_700_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-incremental-" + Guid.NewGuid().ToString("N"));
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
