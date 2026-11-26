// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// Q2：散落多 term 的真实邻近度扩展到**元数据族之外**。
///
/// <list type="number">
///   <item><b>歌词族</b>：同一字段内覆盖每个 term 的最小窗口——近对排在远对之前，
///         explain.proximity 给出真实跨度而不是 sentinel。</item>
///   <item><b>译文层</b>：<see cref="FieldId.LyricsTranslation"/> 与歌词原文共用同一套
///         字段集映射；只命中译文层的候选也能算出跨度。</item>
///   <item><b>CJK bigram</b>：直接对 <see cref="EraProximity.Fill"/> 喂两个相邻 bigram
///         词元（绕过查询期短语合并），近对 = 1、远对 = 5；重复词元去重后结论不变。</item>
///   <item><b>成本护栏</b>：候选 &gt; cap 时原样返回（保持 <see cref="SearchRanker.ProximityUnknown"/>），
///         不读位置块。</item>
///   <item><b>族门禁</b>：拼音 / 首字母 / 跨语言族没有字面字段集，永远算不出。</item>
/// </list>
/// </summary>
internal static class EraStoreLyricProximityTests
{
    private const ulong CreatedMs = 1_760_000_000_000;

    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-lyricprox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LyricAdjacentVsFar(BuildStore(Path.Combine(root, "lyrics"), LyricCorpus()));
            TranslationLayer(BuildStore(Path.Combine(root, "translation"), TranslationCorpus()));

            var bigram = Path.Combine(root, "bigram");
            var bigramIndex = BuildStore(bigram, BigramCorpus());
            BigramWindows(bigramIndex);
            CapKeepsUnknown(bigramIndex);
            FamilyGate();
        }
        finally
        {
            SearchSession.CloseCached();
            SearchExactness.DebugCapOverride = null;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响断言结果。
            }
        }
    }

    /* ------------------------------------------------------------------ */
    /* 1) 歌词族：近 / 远                                                   */
    /* ------------------------------------------------------------------ */

    private static void LyricAdjacentVsFar(string index)
    {
        var result = Query(index, "hello world", lyrics: true);
        if (!result.Explanations.TryGetValue(TrackHex(1), out var near)
            || !result.Explanations.TryGetValue(TrackHex(2), out var far))
        {
            Program.Check("Q2 歌词邻近度: 近 / 远曲都有归因", false,
                string.Join(',', result.Items.Select(hit => hit.TrackId)));
            return;
        }

        Program.Check("Q2 歌词邻近度: 近对 explain.proximity = 1", near.Proximity == 1,
            near.Proximity.ToString());
        Program.Check("Q2 歌词邻近度: 远对 explain.proximity = 4（不与近对相等）",
            far.Proximity == 4 && far.Proximity != near.Proximity, far.Proximity.ToString());
        Program.Check("Q2 歌词邻近度: 近对排在远对之前",
            IndexOf(result, TrackHex(1)) < IndexOf(result, TrackHex(2)),
            string.Join(',', result.Items.Select(hit => hit.TrackId)));
    }

    /* ------------------------------------------------------------------ */
    /* 2) 译文层                                                            */
    /* ------------------------------------------------------------------ */

    private static void TranslationLayer(string index)
    {
        var result = Query(index, "translated stars", lyrics: true);
        if (!result.Explanations.TryGetValue(TrackHex(1), out var near)
            || !result.Explanations.TryGetValue(TrackHex(2), out var far))
        {
            Program.Check("Q2 译文层邻近度: 近 / 远曲都有归因", false,
                string.Join(',', result.Items.Select(hit => hit.TrackId)));
            return;
        }

        Program.Check("Q2 译文层邻近度: 近对 = 1 / 远对 = 4",
            near.Proximity == 1 && far.Proximity == 4,
            $"{near.Proximity}/{far.Proximity}");
        Program.Check("Q2 译文层邻近度: 近对排在远对之前",
            IndexOf(result, TrackHex(1)) < IndexOf(result, TrackHex(2)),
            string.Join(',', result.Items.Select(hit => hit.TrackId)));
    }

    /* ------------------------------------------------------------------ */
    /* 3) CJK bigram 直接喂 Fill（查询期会把相邻 bigram 合并成短语）        */
    /* ------------------------------------------------------------------ */

    private static void BigramWindows(string index)
    {
        var file = EraFile(index);
        using var reader = new StoreReader(file);
        var candidates = Candidates(reader);

        var terms = new[] { "夜空", "空最" };
        EraProximity.Fill(reader, terms, candidates, cap: 100, FieldFamily.Lyrics);
        var near = ByDoc(candidates, 0);
        var far = ByDoc(candidates, 1);
        Program.Check("Q2 CJK bigram: 近对 = 1 / 远对 = 5",
            near.Proximity == 1 && far.Proximity == 5, $"{near.Proximity}/{far.Proximity}");

        // 重复词元不能把同一位置复读成跨度 0：去重后仍是 1 / 5。
        var duplicate = new[] { "夜空", "空最", "夜空" };
        var again = Candidates(reader);
        EraProximity.Fill(reader, duplicate, again, cap: 100, FieldFamily.Lyrics);
        Program.Check("Q2 CJK bigram: 重复词元去重后结论不变",
            ByDoc(again, 0).Proximity == 1 && ByDoc(again, 1).Proximity == 5,
            $"{ByDoc(again, 0).Proximity}/{ByDoc(again, 1).Proximity}");
    }

    /// <summary>候选 &gt; cap：不读位置块，全部保持 sentinel（成本护栏与元数据步同口径）。</summary>
    private static void CapKeepsUnknown(string index)
    {
        using var reader = new StoreReader(EraFile(index));
        var candidates = Candidates(reader);
        EraProximity.Fill(reader, new[] { "夜空", "空最" }, candidates, cap: 1, FieldFamily.Lyrics);
        Program.Check("Q2 超 cap: 候选 > cap 保持 ProximityUnknown",
            candidates.Count == 2 && candidates.All(candidate =>
                candidate.Proximity == SearchRanker.ProximityUnknown),
            string.Join(',', candidates.Select(candidate => candidate.Proximity)));
    }

    /* ------------------------------------------------------------------ */
    /* 4) 族门禁                                                            */
    /* ------------------------------------------------------------------ */

    private static void FamilyGate()
    {
        Program.Check("Q2 族映射: 元数据 / 歌词 / 混合 / 读音 / 模糊有字段集",
            EraProximity.Supports(FieldFamily.Meta)
            && EraProximity.Supports(FieldFamily.Lyrics)
            && EraProximity.Supports(FieldFamily.Mixed)
            && EraProximity.Supports(FieldFamily.Reading)
            && EraProximity.Supports(FieldFamily.Fuzzy));
        Program.Check("Q2 族映射: 拼音 / 首字母 / 跨语言算不出",
            !EraProximity.Supports(FieldFamily.Pinyin)
            && !EraProximity.Supports(FieldFamily.Initials)
            && !EraProximity.Supports(FieldFamily.CrossLingual));
        Program.Check("Q2 族映射: 歌词只含三层字面列",
            EraProximity.FieldsFor(FieldFamily.Lyrics) is { Count: 3 } fields
            && fields.SequenceEqual(new[]
            {
                FieldId.Lyrics, FieldId.LyricsTranslation, FieldId.LyricsRomanization,
            }));
    }

    /* ------------------------------------------------------------------ */
    /* 夹具 / 查询辅助                                                      */
    /* ------------------------------------------------------------------ */

    private static AnalyzedDoc[] LyricCorpus() => new[]
    {
        // 近对：hello@0 / world@1 → 跨度 1。
        MakeDoc(1, new Dictionary<FieldId, string[]>
        {
            [FieldId.Lyrics] = new[] { "hello", "world", "again" },
        }, "near", "N1"),
        // 远对：hello@0 / world@4 → 跨度 4。
        MakeDoc(2, new Dictionary<FieldId, string[]>
        {
            [FieldId.Lyrics] = new[] { "hello", "x", "y", "z", "world" },
        }, "far", "N2"),
    };

    private static AnalyzedDoc[] TranslationCorpus() => new[]
    {
        // 原文层为空，只命中译文层；近对 translated@0 / stars@1 → 跨度 1。
        MakeDoc(1, new Dictionary<FieldId, string[]>
        {
            [FieldId.LyricsTranslation] = new[] { "translated", "stars" },
        }, "tr-near", "T1"),
        // 远对 translated@0 / stars@4 → 跨度 4。
        MakeDoc(2, new Dictionary<FieldId, string[]>
        {
            [FieldId.LyricsTranslation] = new[] { "translated", "a", "b", "c", "stars" },
        }, "tr-far", "T2"),
    };

    private static AnalyzedDoc[] BigramCorpus() => new[]
    {
        // 夜空@0 / 空最@1（相邻 bigram，跨度 1）。
        MakeDoc(1, new Dictionary<FieldId, string[]>
        {
            [FieldId.Lyrics] = new[] { "夜空", "空最", "最亮" },
        }, "bigram-near", "B1"),
        // 夜空@0 / 空最@5（跨度 5）。
        MakeDoc(2, new Dictionary<FieldId, string[]>
        {
            [FieldId.Lyrics] = new[] { "夜空", "甲一", "乙二", "丙三", "丁四", "空最" },
        }, "bigram-far", "B2"),
    };

    private static AnalyzedDoc MakeDoc(
        int index, IReadOnlyDictionary<FieldId, string[]> tokens, string title, string artist)
        => new(TrackBytes(index), new byte[16], (ushort)(index & 0x7), 0, tokens,
            title, artist, string.Empty);

    private static byte[] TrackBytes(int index)
    {
        var track = new byte[16];
        for (var i = 0; i < 16; i++) track[i] = (byte)(index * 7 + i);
        return track;
    }

    private static string TrackHex(int index) => Convert.ToHexStringLower(TrackBytes(index));

    /// <summary>直建一份 EraStore（绕开索引器）；返回 <c>&lt;dir&gt;/search.db</c> 锚。</summary>
    private static string BuildStore(string dir, AnalyzedDoc[] docs)
    {
        var eraDir = Path.Combine(dir, IndexerEngine.EraStoreDirName);
        Directory.CreateDirectory(eraDir);
        var options = new BuildOptions
        {
            DocsPerSegment = 64,
            Gen = 1,
            FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray(),
            CreatedMs = CreatedMs,
            AnalyzerVersion = Analyzer.AnalyzerVersion,
            OptionsBits = ManifestOptionsBits.Pinyin
                | ManifestOptionsBits.InitialsLyrics
                | ManifestOptionsBits.HasHan
                | ManifestOptionsBits.HasLatin,
        };
        IndexBuilder.Build(Path.Combine(eraDir, "search-1.eraindex"), docs, options);
        return Path.Combine(dir, "search.db");
    }

    private static string EraFile(string index)
    {
        var dir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(index)) ?? ".", IndexerEngine.EraStoreDirName);
        var name = CurrentPointer.Read(dir)
            ?? throw new InvalidOperationException($"EraStore CURRENT 缺失: {dir}");
        return Path.Combine(dir, name);
    }

    private static List<EraCandidate> Candidates(StoreReader reader)
    {
        var candidates = new List<EraCandidate>();
        foreach (var segment in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < segment.Docs.Count; doc++)
            {
                if (!segment.IsLive(doc)) continue;
                candidates.Add(new EraCandidate
                {
                    SegmentId = segment.SegmentId,
                    DocId = doc,
                    TrackIdHex = segment.Docs.TrackId(doc),
                    Score = 0,
                });
            }
        }
        return candidates;
    }

    private static EraCandidate ByDoc(List<EraCandidate> candidates, int docId)
        => candidates.First(candidate => candidate.DocId == docId);

    private static SearchResult Query(string index, string query, bool lyrics)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Metadata = !lyrics,
            Lyrics = lyrics,
            Fuzzy = false,
            MinExact = 1,
            Limit = 50,
            Explain = true,
            // 能力覆盖测试会改全局 Cap：结果缓存必须关掉，否则会命中旧解释。
            Cache = false,
        });

    private static int IndexOf(SearchResult result, string trackId)
    {
        for (var i = 0; i < result.Items.Count; i++)
        {
            if (result.Items[i].TrackId == trackId) return i;
        }
        return int.MaxValue;
    }
}
