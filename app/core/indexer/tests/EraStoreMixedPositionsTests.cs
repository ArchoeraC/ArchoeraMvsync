// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S2 升级后的混合位置合并（2026-11-26）：
/// 旧段给 <c>LyricsRomanization</c> 写了位置，新增量段按
/// <see cref="BuildDefaults"/> 不再写。同一词条跨源合并时必须是「全有或全无」，
/// 否则 <see cref="PostingsWriter"/> 会拒绝；本测试同时锁定全有 / 全无两种正常路径。
/// </summary>
internal static class EraStoreMixedPositionsTests
{
    private const ulong CreatedMs = 1_700_000_000_000;
    private static readonly byte[] FileUuid =
        Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();

    public static void Run()
    {
        MixedOldNewViaIncrementalMerge();
        AllPositionedMerge();
        AllPositionFreeMerge();
    }

    /// <summary>
    /// 真实升级路径：23 个带位置旧段 + 1 个免位置增量段 → 24 段触发合并。
    /// 合并后同一词条必须整体免位置，短语降级为 AND 且不抛异常。
    /// </summary>
    private static void MixedOldNewViaIncrementalMerge()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var oldDocs = new List<AnalyzedDoc>();
            for (var i = 1; i <= 23; i++)
                oldDocs.Add(RomanDoc(i));
            IndexBuilder.Build(path, oldDocs, Options(docsPerSegment: 1, gen: 1, noPositions: false));
            Program.Check("混合位置: 旧索引 23 段全部带位置",
                IndexStats.Collect(path).Segments == 23,
                IndexStats.Collect(path).Segments.ToString());

            var result = IncrementalUpdater.Apply(
                path,
                new[] { RomanDoc(24) },
                Array.Empty<byte[]>(),
                new IncrementalOptions
                {
                    Merge = true,
                    MergeMaxDocs = 1000,
                    DocsPerSegment = 1,
                    NoPositionFields = BuildDefaults.CreateNoPositionFields(),
                });

            Program.Check("混合位置: 增量后触发合并且收敛到 1 段",
                result.Merged && result.SegmentCount == 1,
                $"merged={result.Merged} segs={result.SegmentCount}");
            Program.Check("混合位置: 合并后 verify 通过",
                IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));

            using var reader = new StoreReader(path);
            var found = false;
            var titleStillPositioned = false;
            foreach (var seg in reader.OpenPerSegment())
            {
                if (seg.Dict.TryFind(FieldId.Title, "w1", out var title))
                {
                    var titleCursor = seg.Postings.Open(title.PostOffset, title.Df);
                    titleStillPositioned = title.HasPositions
                        && titleCursor.Next() && titleCursor.HasPositions;
                }

                if (!seg.Dict.TryFind(FieldId.LyricsRomanization, "qing", out var info)) continue;
                found = true;
                Program.Check("混合位置: 合并后词条 dict flag bit0=0",
                    !info.HasPositions, $"flags=0x{info.Flags:X2}");
                var cursor = seg.Postings.Open(info.PostOffset, info.Df);
                Program.Check("混合位置: 合并后游标 HasPositions=false",
                    cursor.Next() && !cursor.HasPositions,
                    $"hasPos={cursor.HasPositions} tf={cursor.CurrentTf}");
            }
            Program.Check("混合位置: 合并后能找到 LyricsRomanization 词条", found);
            Program.Check("混合位置: 同段 Title 词条仍保留位置（仅该词条降级）",
                titleStillPositioned, titleStillPositioned.ToString());

            var phrase = EraQueryPlan.ExecuteDetailed(
                reader, "{lyrics_rom_tok} : \"qing tian\"", -1);
            Program.Check("混合位置: 短语降级为 AND（24 首全部召回 / Positioned=false）",
                phrase.Candidates.Count == 24 && phrase.AnyPhraseDowngraded,
                $"{phrase.Candidates.Count}/{phrase.AnyPhraseDowngraded}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>全有位置的合并（旧+旧）：位置块与短语相邻校验保持不变。</summary>
    private static void AllPositionedMerge()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            IndexBuilder.Build(path, TwoRomanDocs(), Options(docsPerSegment: 1, gen: 1, noPositions: false));

            var result = Merger.Merge(path, new[] { 0, 1 });
            Program.Check("全有位置: 合并 2 段", result.LiveDocs == 2, result.LiveDocs.ToString());
            Program.Check("全有位置: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));

            using var reader = new StoreReader(path);
            var seg = reader.OpenPerSegment()[0];
            Program.Check("全有位置: 词条 dict flag bit0=1",
                seg.Dict.TryFind(FieldId.LyricsRomanization, "qing", out var info) && info.HasPositions,
                "missing");
            var cursor = seg.Postings.Open(info.PostOffset, info.Df);
            Program.Check("全有位置: 游标 HasPositions=true",
                cursor.Next() && cursor.HasPositions, $"hasPos={cursor.HasPositions}");

            var phrase = EraQueryPlan.ExecuteDetailed(
                reader, "{lyrics_rom_tok} : \"qing tian\"", -1);
            Program.Check("全有位置: 短语仍校验相邻（只命中 doc0 / 未降级）",
                phrase.Candidates.Count == 1 && !phrase.AnyPhraseDowngraded,
                $"{phrase.Candidates.Count}/{phrase.AnyPhraseDowngraded}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 全无位置的合并（新+新）：整段没有 Positions section，合并段也没有。
    /// 读取侧必须把空 Positions 当作「该段所有词条无位置」，短语按 AND。
    /// </summary>
    private static void AllPositionFreeMerge()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            // 只有 LyricsRomanization（没有 Title 等带位置字段）→ 源段 / 合并段都不写 Positions section。
            IndexBuilder.Build(path, RomanOnlyDocs(),
                Options(docsPerSegment: 1, gen: 1, noPositions: true));

            var result = Merger.Merge(path, new[] { 0, 1 });
            Program.Check("全无位置: 合并 2 段", result.LiveDocs == 2, result.LiveDocs.ToString());
            Program.Check("全无位置: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));

            using var reader = new StoreReader(path);
            var seg = reader.OpenPerSegment()[0];
            Program.Check("全无位置: 整段没有 Positions section",
                !reader.TryGetSectionBody(SectionKind.Positions, seg.SegmentId, out var positions)
                    && positions.IsEmpty,
                "unexpected positions section");
            Program.Check("全无位置: 词条 dict flag bit0=0",
                seg.Dict.TryFind(FieldId.LyricsRomanization, "qing", out var info) && !info.HasPositions,
                "missing");
            var cursor = seg.Postings.Open(info.PostOffset, info.Df);
            Program.Check("全无位置: 游标 HasPositions=false",
                cursor.Next() && !cursor.HasPositions, $"hasPos={cursor.HasPositions}");

            var phrase = EraQueryPlan.ExecuteDetailed(
                reader, "{lyrics_rom_tok} : \"qing tian\"", -1);
            Program.Check("全无位置: 短语按 AND（2 首 / Positioned=false）",
                phrase.Candidates.Count == 2 && phrase.AnyPhraseDowngraded,
                $"{phrase.Candidates.Count}/{phrase.AnyPhraseDowngraded}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    /// <summary>
    /// doc0：qing + tian 相邻；doc1：qing + yu + tian（短语文本存在但不该相邻命中）。
    /// </summary>
    private static List<AnalyzedDoc> TwoRomanDocs() => new()
    {
        RomanDoc(1),
        MakeDoc(2, (FieldId.LyricsRomanization, new[] { "qing", "yu", "tian" })),
    };

    /// <summary>只有 LyricsRomanization 的文档：整段都不会有位置块。</summary>
    private static List<AnalyzedDoc> RomanOnlyDocs() => new()
    {
        BuildDoc(1, (FieldId.LyricsRomanization, new[] { "qing", "tian" })),
        BuildDoc(2, (FieldId.LyricsRomanization, new[] { "qing", "yu", "tian" })),
    };

    private static AnalyzedDoc RomanDoc(int seed) =>
        MakeDoc(seed, (FieldId.LyricsRomanization, new[] { "qing", "tian" }));

    private static AnalyzedDoc MakeDoc(int seed, params (FieldId Field, string[] Tokens)[] fields)
    {
        var tokens = new Dictionary<FieldId, string[]>();
        foreach (var (field, terms) in fields) tokens[field] = terms;
        // 保留一个带位置的字段：混合用例要验证「同段里 Title 仍带位置，只有混合的
        // LyricsRomanization 词条降级」；纯无位置用例则用 BuildDoc 故意不写 Title。
        if (!tokens.ContainsKey(FieldId.Title))
            tokens[FieldId.Title] = new[] { "w" + seed };
        return BuildDoc(seed, tokens);
    }

    private static AnalyzedDoc BuildDoc(int seed, params (FieldId Field, string[] Tokens)[] fields)
    {
        var tokens = new Dictionary<FieldId, string[]>();
        foreach (var (field, terms) in fields) tokens[field] = terms;
        return BuildDoc(seed, tokens);
    }

    private static AnalyzedDoc BuildDoc(int seed, IReadOnlyDictionary<FieldId, string[]> tokens)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(seed + i);
            rev[i] = (byte)(seed * 7 + i);
        }

        return new AnalyzedDoc(track, rev, (ushort)(seed & 0x7), (byte)(seed & 0x03),
            new Dictionary<FieldId, string[]>(tokens), "w" + seed, "s", "a" + seed);
    }

    private static BuildOptions Options(int docsPerSegment, ulong gen, bool noPositions)
    {
        var options = new BuildOptions
        {
            DocsPerSegment = docsPerSegment,
            Gen = gen,
            FileUuid = FileUuid,
            CreatedMs = CreatedMs,
            NormalizerVersion = 1,
            TokenizerVersion = 2,
        };
        if (noPositions) options.NoPositionFields = BuildDefaults.CreateNoPositionFields();
        return options;
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-mixed-pos-" + Guid.NewGuid().ToString("N"));
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
