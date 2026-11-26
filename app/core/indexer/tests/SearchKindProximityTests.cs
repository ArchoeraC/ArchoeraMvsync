// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// A3 三项收口（T2.5 遗留）的端到端回归：
///
/// <list type="number">
///   <item><b>kind 分类矩阵</b>：exact / phrase / prefix / partial / pinyinExact /
///         pinyinPhrase / initials 七档都由**真实查询**走
///         <see cref="SearchSession"/> 编译 → EraStore 执行 → explain 断言，
///         不经合成 <see cref="Step"/>（避免分类漂移时测试仍绿）。</item>
///   <item><b>positioned 来源</b>：短语 atom 落在无位置字段 → false；
///         同一步在有位置字段 → true；散落 AND（无短语 atom）→ true。
///         用 <see cref="BuildOptions.NoPositionFields"/> 直建两份 EraStore 对照。</item>
///   <item><b>邻近度</b>：同一字段内覆盖全部 term 的最小跨度——近对排在远对之前
///         （两曲 bm25 逐位相同，只能靠 proximity 分开）；单 term = 0；
///         歌词族 / 超 Cap → 算不出（wire -1）。</item>
/// </list>
///
/// 接线入口：<see cref="Run"/>；由 <c>tests/Program.cs</c> 的 Main 调用。
/// </summary>
internal static class SearchKindProximityTests
{
    private const int DocsPerSegment = 64;
    private const ulong CreatedMs = 1_760_000_000_000;

    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-kindprox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var index = Fixture(root);
            KindMatrix(index);
            Proximity(index);
            Positioned(root);
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
    /* 夹具                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 主夹具（真实索引器路径）：覆盖七档 kind 与邻近度所需的最小曲库。
    ///
    /// <list type="bullet">
    ///   <item>k1 晴天 / 周杰伦（qt 首字母、晴天二字 bigram）</item>
    ///   <item>k2 我爱你 / 王菲（phrase、woaini 拼音短语、wo 单音节、爱 单字前缀）</item>
    ///   <item>k3 hello world（散落 AND；歌词含同一对词供歌词族邻近度对照）</item>
    ///   <item>p1 / p2 alpha…beta：4 个 title token、alpha/beta tf 均为 1、行长度相同，
    ///         因此 bm25 逐位相同，只有邻近度能分开（近 2 / 远 3）。</item>
    /// </list>
    /// </summary>
    private static string Fixture(string root)
    {
        var db = Path.Combine(root, "library.db");
        var index = Path.Combine(root, "search.db");
        var audio = Path.Combine(root, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "k1", Path.Combine(audio, "k1.mp3"), "晴天", "[\"周杰伦\"]",
                "叶惠美", "故事的小黄花", 1000);
            EndToEnd.InsertTrack(conn, "k2", Path.Combine(audio, "k2.mp3"), "我爱你", "[\"王菲\"]",
                "将爱", "我愿意为你", 2000);
            EndToEnd.InsertTrack(conn, "k3", Path.Combine(audio, "k3.mp3"), "hello world", "[\"Adele\"]",
                "25", "hello world hello", 3000);
            EndToEnd.InsertTrack(conn, "p1", Path.Combine(audio, "p1.mp3"), "alpha x beta z", "[\"N1\"]",
                "PA", null, 4000);
            EndToEnd.InsertTrack(conn, "p2", Path.Combine(audio, "p2.mp3"), "alpha x y beta", "[\"N2\"]",
                "PB", null, 5000);
        }

        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();
        return index;
    }

    /* ------------------------------------------------------------------ */
    /* 1) kind 分类矩阵（真实查询 → explain）                              */
    /* ------------------------------------------------------------------ */

    private static void KindMatrix(string index)
    {
        CheckKind(index, "晴天", metadata: true, track: "k1", expect: "exact");
        CheckKind(index, "我爱你", metadata: true, track: "k2", expect: "phrase");
        // 单 CJK 字：索引里没有中间字 unigram，编译成 `"爱"*` 前缀。
        CheckKind(index, "爱", metadata: true, track: "k2", expect: "prefix");
        // 两个真实词元、无短语语义 → 散落 AND。
        CheckKind(index, "hello world", metadata: false, track: "k3", expect: "partial");
        // 单音节连写 → PinyinExact；≥2 音节 → PinyinPhrase（主趟与拼音宽松趟同档）。
        CheckKind(index, "wo", metadata: false, track: "k2", expect: "pinyinExact");
        CheckKind(index, "woaini", metadata: false, track: "k2", expect: "pinyinPhrase");
        // 首字母串（qt = 晴天 的首字母 bigram）。
        CheckKind(index, "qt", metadata: true, track: "k1", expect: "initials");
    }

    private static void CheckKind(string index, string query, bool metadata, string track, string expect)
    {
        var result = Query(index, query, metadata, explain: true);
        if (!result.Explanations.TryGetValue(track, out var explanation))
        {
            Program.Check($"kind 矩阵: 「{query}」解释里有 {track}（期望 {expect}）", false,
                "hits=" + string.Join(',', result.Items.Select(hit => hit.TrackId)));
            return;
        }
        Program.Check($"kind 矩阵: 「{query}」→ {expect}",
            explanation.Kind == expect && explanation.Bucket[0] == KindRank(expect),
            $"{explanation.Kind} bucket[0]={explanation.Bucket[0]}");
    }

    /// <summary>kind wire 名 → 比较器档位（与 <see cref="SearchExplainBuilder.KindName"/> 互逆）。</summary>
    private static int KindRank(string wire) => wire switch
    {
        "exact" => SearchRanker.KindRank(MatchKind.Exact),
        "phrase" => SearchRanker.KindRank(MatchKind.Phrase),
        "prefix" => SearchRanker.KindRank(MatchKind.Prefix),
        "partial" => SearchRanker.KindRank(MatchKind.Partial),
        "pinyinExact" => SearchRanker.KindRank(MatchKind.PinyinExact),
        "pinyinPhrase" => SearchRanker.KindRank(MatchKind.PinyinPhrase),
        "initials" => SearchRanker.KindRank(MatchKind.Initials),
        _ => -1,
    };

    /* ------------------------------------------------------------------ */
    /* 2) 邻近度（最小跨度）                                               */
    /* ------------------------------------------------------------------ */

    private static void Proximity(string index)
    {
        // 两曲 bm25 相同：近对（alpha x beta z → 跨度 2）必须排在远对（alpha x y beta → 跨度 3）前。
        // 只跑 lexical（fast@1）：邻近度是字面步的第 5 键，不应被 Q4 追加的
        // 丢词证据（单 term、proximity 0）覆盖。
        var result = Query(index, "alpha beta", metadata: true, explain: true,
            scheme: SchemeIds.Fast);
        if (!result.Explanations.TryGetValue("p1", out var near)
            || !result.Explanations.TryGetValue("p2", out var far))
        {
            Program.Check("proximity: p1 / p2 都有归因", false,
                string.Join(',', result.Items.Select(hit => hit.TrackId)));
            return;
        }

        Program.Check("proximity: 近对 = 2 / 远对 = 3",
            near.Proximity == 2 && far.Proximity == 3,
            $"{near.Proximity}/{far.Proximity}");
        Program.Check("proximity: bm25 逐位相同（顺序只由邻近度决定）",
            Math.Abs(Score(result, "p1") - Score(result, "p2")) < 1e-9,
            Score(result, "p1").ToString("F9", CultureInfo.InvariantCulture) + " / "
            + Score(result, "p2").ToString("F9", CultureInfo.InvariantCulture));
        Program.Check("proximity: 近对排在远对之前",
            IndexOf(result, "p1") < IndexOf(result, "p2"),
            string.Join(',', result.Items.Take(10).Select(hit => hit.TrackId)));

        // 单 term：不需要位置 → 0。CJK 单词会被 analyzer 追加首尾 unigram 兜底词元，
        // 但「真实词」只有一个 → 同样记 0（否则会按词在标题里的位置给出假惩罚）。
        var single = Query(index, "alpha", metadata: true, explain: true);
        Program.Check("proximity: 单 term = 0",
            single.Explanations.TryGetValue("p1", out var one) && one.Proximity == 0,
            single.Explanations.TryGetValue("p1", out var one2) ? one2.Proximity.ToString() : "missing");
        var singleCjk = Query(index, "晴天", metadata: true, explain: true);
        Program.Check("proximity: 单 CJK 词（含兜底 unigram）= 0",
            singleCjk.Explanations.TryGetValue("k1", out var oneCjk) && oneCjk.Proximity == 0,
            singleCjk.Explanations.TryGetValue("k1", out var oneCjk2)
                ? oneCjk2.Proximity.ToString()
                : "missing");

        // 歌词族（Q2）：散落 AND 同样按真实位置算跨度。k3 歌词 “hello world hello”
        // → hello@2 / world@1，最小窗口 = 1（wire 上是真实值，不再是 -1）。
        var lyrics = Query(index, "hello world", metadata: false, lyrics: true, explain: true);
        Program.Check("proximity: 歌词散落 AND = 最小跨度（1）",
            lyrics.Explanations.TryGetValue("k3", out var lyric) && lyric.Proximity == 1,
            lyrics.Explanations.TryGetValue("k3", out var lyric2) ? lyric2.Proximity.ToString() : "missing");

        // 超 Cap：单步候选 > SearchExactness.Cap 时不读位置块 → 保持 sentinel。
        SearchExactness.DebugCapOverride = 1;
        try
        {
            var capped = Query(index, "alpha beta", metadata: true, explain: true,
                scheme: SchemeIds.Fast);
            Program.Check("proximity: 超 Cap = 算不出（-1）",
                capped.Explanations.TryGetValue("p1", out var capped2) && capped2.Proximity == -1,
                capped.Explanations.TryGetValue("p1", out var capped3)
                    ? capped3.Proximity.ToString()
                    : "missing");
        }
        finally
        {
            SearchExactness.DebugCapOverride = null;
        }

        SearchSession.CloseCached();
    }

    /* ------------------------------------------------------------------ */
    /* 3) positioned 来自引擎                                              */
    /* ------------------------------------------------------------------ */

    private static void Positioned(string root)
    {
        var unpositioned = BuildStore(Path.Combine(root, "nopos"), noPositions: true);
        var positioned = BuildStore(Path.Combine(root, "pos"), noPositions: false);

        // 短语 atom（"我爱 爱你"）：无位置字段 → 降级为纯交集 → positioned=false。
        var down = Query(unpositioned, "我爱你", metadata: true, explain: true);
        Program.Check("positioned: 无位置短语仍召回（降级为 AND）",
            down.Items.Count > 0, down.Items.Count.ToString(CultureInfo.InvariantCulture));
        Program.Check("positioned: 无位置短语 → false",
            down.Explanations.Values.All(item => !item.Positioned) && down.Explanations.Count > 0,
            string.Join(',', down.Explanations.Values.Select(item => item.Positioned)));

        // 同一表达式在有位置字段上 → 保留相邻校验 → positioned=true。
        var up = Query(positioned, "我爱你", metadata: true, explain: true);
        Program.Check("positioned: 有位置短语 → true",
            up.Explanations.Count > 0 && up.Explanations.Values.All(item => item.Positioned),
            string.Join(',', up.Explanations.Values.Select(item => item.Positioned)));

        // 散落 AND（没有短语 atom）→ 永远 true，即使字段无位置。
        foreach (var (index, label) in new[] { (positioned, "有位置"), (unpositioned, "无位置") })
        {
            var scattered = Query(index, "hello world", metadata: true, explain: true);
            Program.Check($"positioned: 散落 AND（{label}字段）→ true",
                scattered.Explanations.Count > 0
                && scattered.Explanations.Values.All(item => item.Positioned),
                string.Join(',', scattered.Explanations.Values.Select(item => item.Positioned)));
        }

        SearchSession.CloseCached();
    }

    /// <summary>
    /// 直建一份 EraStore（绕开索引器，才能指定 <see cref="BuildOptions.NoPositionFields"/>）。
    /// 返回会话用的路径锚：<c>&lt;dir&gt;/search.db</c>（文件本身不必存在）。
    /// </summary>
    private static string BuildStore(string dir, bool noPositions)
    {
        var eraDir = Path.Combine(dir, IndexerEngine.EraStoreDirName);
        Directory.CreateDirectory(eraDir);
        var options = new BuildOptions
        {
            DocsPerSegment = DocsPerSegment,
            Gen = 1,
            FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray(),
            CreatedMs = CreatedMs,
            AnalyzerVersion = Analyzer.AnalyzerVersion,
            OptionsBits = ManifestOptionsBits.Pinyin
                | ManifestOptionsBits.InitialsLyrics
                | ManifestOptionsBits.HasHan
                | ManifestOptionsBits.HasLatin,
        };
        if (noPositions) options.NoPositionFields.Add(FieldId.Title);

        IndexBuilder.Build(Path.Combine(eraDir, "search-1.eraindex"), StoreCorpus(), options);
        return Path.Combine(dir, "search.db");
    }

    /// <summary>固定语料：一条短语曲（我爱你）+ 一条拉丁散落曲（hello world）。</summary>
    private static List<AnalyzedDoc> StoreCorpus()
    {
        var phrase = new Dictionary<FieldId, string[]>
        {
            // analyzer 形态：bigram（位置连续）+ 段首段尾 unigram。
            [FieldId.Title] = new[] { "我爱", "爱你", "我", "你" },
            [FieldId.Artist] = new[] { "王菲" },
            [FieldId.PinyinMeta] = new[] { "wo", "ai", "ni" },
            [FieldId.InitialsMeta] = new[] { "wa", "an" },
        };
        var latin = new Dictionary<FieldId, string[]>
        {
            [FieldId.Title] = new[] { "hello", "world" },
            [FieldId.Artist] = new[] { "adele" },
        };
        return new List<AnalyzedDoc>
        {
            MakeDoc(1, phrase, "我爱你", "王菲"),
            MakeDoc(2, latin, "hello world", "Adele"),
        };
    }

    private static AnalyzedDoc MakeDoc(
        int index, IReadOnlyDictionary<FieldId, string[]> tokens, string title, string artist)
    {
        var track = new byte[16];
        for (var i = 0; i < 16; i++) track[i] = (byte)(index * 7 + i);
        return new AnalyzedDoc(track, new byte[16], (ushort)(index & 0x7), 0, tokens,
            title, artist, string.Empty);
    }

    /* ------------------------------------------------------------------ */
    /* 查询 / 断言辅助                                                     */
    /* ------------------------------------------------------------------ */

    private static SearchResult Query(
        string index, string query, bool metadata, bool lyrics = false, bool explain = false,
        string? scheme = null)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Scheme = scheme,
            Metadata = metadata,
            Lyrics = lyrics && !metadata,
            Fuzzy = false,
            MinExact = 1,
            Limit = 50,
            Explain = explain,
            // 能力覆盖测试会改全局 Cap：结果缓存必须关掉，否则会命中旧解释。
            Cache = false,
        });

    private static double Score(SearchResult result, string trackId)
        => result.Items.First(hit => hit.TrackId == trackId).Score;

    private static int IndexOf(SearchResult result, string trackId)
    {
        for (var i = 0; i < result.Items.Count; i++)
        {
            if (result.Items[i].TrackId == trackId) return i;
        }
        return int.MaxValue;
    }
}
