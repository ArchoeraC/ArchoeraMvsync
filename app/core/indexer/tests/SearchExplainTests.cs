// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S2 explain / trace（D.3.1 / D.3.3）：归因只读、不改变排序。
///
/// <para>覆盖：</para>
/// <list type="number">
///   <item>N8：标题带「(Cover)」修饰时按 <c>exact</c> / <c>title</c> 归因，
///         修饰词不计入 <c>matched</c>；T3.11 起排序字段就是真实字段（field == resolvedField）；</item>
///   <item>关闭 explain 时 items 的 trackId / score / pass 逐条一致（只读派生）；</item>
///   <item>trace 记录步骤状态与 <c>min_exact</c> 跳过原因；</item>
///   <item>计划侧可达的 kind：exact / partial（丢词）/ typo（编辑距离 1）；</item>
///   <item>计划侧尚未产出的四档 kind（<c>phrase</c> / <c>prefix</c> / <c>pinyinPhrase</c> /
///         <c>initials</c>）用**真实 EraStore 倒排 + DocStore** 上的合成步骤覆盖 explain 归因
///         （候选、字段探测都是真的，不是比较器层的假数据）。</item>
/// </list>
///
/// <para>v7 / T3 计划仍不生成这四步（BUCKET 排序侧也拿不到它们），真正的端到端可达性
/// 要等计划侧拆步骤；这里收口的是 explain 的 kind / field 映射契约。</para>
/// </summary>
internal static class SearchExplainTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-explain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (_, index) = Corpus(root);
            CoverAttribution(index);
            LyricsTranslationAttribution(root);
            ReadOnlyParity(index);
            Trace(index);
            ReachableKinds(index);
            SyntheticKindAttribution(index);
        }
        finally
        {
            SearchSession.CloseCached();
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
    /* 语料                                                                */
    /* ------------------------------------------------------------------ */

    private static (string Db, string Index) Corpus(string root)
    {
        var db = Path.Combine(root, "library.db");
        var index = Path.Combine(root, "search.db");
        var audio = Path.Combine(root, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            // N8：同一首歌的「(Cover)」版本，修饰词不得成为正向词。
            EndToEnd.InsertTrack(conn, "c1", Song(audio, "c1.mp3"), "晴天 (Cover)", "[]", null,
                "cover 版本只有翻唱", 1000);
            EndToEnd.InsertTrack(conn, "c2", Song(audio, "c2.mp3"), "晴天", "[]", null,
                "故事的小黄花", 2000);
            // typo：hello 与 query helo 的 Damerau 距离 = 1。
            EndToEnd.InsertTrack(conn, "t1", Song(audio, "t1.mp3"), "hello world", "[]", null,
                "hello hello", 3000);
        }

        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();
        return (db, index);
    }

    /* ------------------------------------------------------------------ */
    /* N8：Cover 修饰不参与 matched                                        */
    /* ------------------------------------------------------------------ */

    private static void CoverAttribution(string index)
    {
        var result = Query(index, "晴天", explain: true);
        Program.Check("explain: 「晴天」命中两条", result.Items.Count == 2, Hits(result));
        Program.Check("explain: explain 与 items 对齐",
            result.Explanations.Count == result.Items.Count, result.Explanations.Count.ToString());

        if (!result.Explanations.TryGetValue("c1", out var cover)
            || !result.Explanations.TryGetValue("c2", out var plain))
        {
            Program.Check("explain: c1 / c2 都有归因", false, string.Join(',', result.Explanations.Keys));
            return;
        }

        Program.Check("N8 Cover 版 kind=exact", cover.Kind == "exact", cover.Kind);
        // T3.11 第二步：精确度复核生效时排序字段就是真实字段（title），
        // resolvedField 不再是「诊断与排序不一致」的保守族。
        Program.Check("N8 Cover 版 field=title / 0x01（复核后不再是保守 lyrics）",
            cover.Field == "title" && cover.FieldId == "0x01", cover.Field + " " + cover.FieldId);
        Program.Check("N8 Cover 版 field 与 resolvedField 同值",
            cover.Field == cover.ResolvedField && cover.FieldId == cover.ResolvedFieldId,
            cover.Field + "/" + cover.ResolvedField);
        // 标题含查询但不相等（「(Cover)」后缀），复核给 startsWith。
        Program.Check("N8 Cover 版 exact=startsWith（标题只含查询）",
            cover.Exact == "startsWith" && cover.Bucket[3] == 1,
            cover.Exact + " " + cover.Bucket[3]);
        // 「(Cover)」只在标题里出现，不进入查询词元：命中词元数应等于计划词元总数。
        Program.Check("N8 「Cover」不是正向词（matched=total）",
            cover.Matched == cover.Total && cover.Total > 0, cover.Matched + "/" + cover.Total);
        Program.Check("N8 Cover 版与普通版同档",
            cover.Bucket.Length == 5 && cover.Bucket[0] == plain.Bucket[0]
            && cover.Bucket[1] == plain.Bucket[1] && cover.Bucket[2] == plain.Bucket[2],
            Bucket(cover) + " vs " + Bucket(plain));
        Program.Check("N8 bm25 与命中的 score 一致",
            Math.Abs(cover.Bm25 - Score(result, "c1")) < 1e-9,
            cover.Bm25.ToString("F6", CultureInfo.InvariantCulture));
    }

    /* ------------------------------------------------------------------ */
    /* N11（部分）：译文命中归因 field=lyricsTr                             */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 译文列可召回。T3.11 第二步起，scope=all 的混合步会复核字面精确度：
    /// 译文命中不含标题 / 歌手 / 专辑归一形 → <c>exact=none</c>，字段桶退回保守的
    /// <c>lyrics</c>（不再细到 <c>lyricsTr</c>，因为 DocStore 没有歌词原文列，
    /// 分辨歌词子列只能靠 explain 探针，而探针在复核生效时按契约关闭）。
    ///
    /// <para>N11 的**单字** `爱` 仍未满足（<c>RequireMetadataForSingleChar</c> 把单字查询
    /// 限定在可见文本列，见 <c>NegativeBaselineTests</c> 的 BLOCKED 理由）；这里只用两字查询
    /// 锁定「译文列可召回 + 复核口径」这一半契约，避免把未实现部分伪装成已通过。</para>
    /// </summary>
    private static void LyricsTranslationAttribution(string root)
    {
        // EraStore 目录名固定为 search.era（按**目录**而非文件名隔离），
        // 译文夹具必须单独子目录，避免覆盖主语料的 root/search.era。
        var dir = Path.Combine(root, "translation");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "library.db");
        var index = Path.Combine(dir, "search.db");
        var audio = Path.Combine(dir, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            // 原文只有英文；中文词只出现在 `.lrc` 译文层。
            EndToEnd.InsertTrack(conn, "tr1", Song(audio, "tr1.mp3"), "英文歌", "[]", null,
                "hello world", 1000);
            const string raw = "[00:01.00]hello world\n[00:01.00]你好世界\n";
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE tracks SET lyrics_lrc = @v, lrc_mtime = 100, lrc_size = @n WHERE id = 'tr1'";
            cmd.Parameters.AddWithValue("@v", raw);
            cmd.Parameters.AddWithValue("@n", raw.Length);
            cmd.ExecuteNonQuery();
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: true);
        SearchSession.CloseCached();

        var result = Query(index, "你好", explain: true);
        Program.Check("N11 仅译文含词时可召回", result.Items.Any(hit => hit.TrackId == "tr1"), Hits(result));
        if (!result.Explanations.TryGetValue("tr1", out var explanation))
        {
            Program.Check("N11 译文命中带 explain", false,
                string.Join(',', result.Explanations.Keys));
            return;
        }
        Program.Check("N11 译文命中时字段桶退到 field=lyrics（复核无元数据列命中）",
            explanation.Field == "lyrics" && explanation.FieldId == "0x05"
            && explanation.ResolvedField == "lyrics" && explanation.ResolvedFieldId == "0x05"
            && explanation.Exact == "none",
            $"{explanation.Field}/{explanation.ResolvedField}/{explanation.Exact}");
    }

    /* ------------------------------------------------------------------ */
    /* 只读派生：关掉 explain 不改变 items                                 */
    /* ------------------------------------------------------------------ */
    private static void ReadOnlyParity(string index)
    {
        var with = Query(index, "晴天", explain: true);
        var without = Query(index, "晴天", explain: false);
        var same = with.Items.Count == without.Items.Count
            && with.Items.Zip(without.Items).All(pair =>
                pair.First.TrackId == pair.Second.TrackId
                && Math.Abs(pair.First.Score - pair.Second.Score) < 1e-12
                && pair.First.Pass == pair.Second.Pass);
        Program.Check("explain 只读：items 与 explain=false 逐条一致", same, Hits(with));
        Program.Check("explain 关闭时不返回 explain / trace",
            without.Explanations.Count == 0 && without.Trace is null,
            without.Explanations.Count.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* trace                                                               */
    /* ------------------------------------------------------------------ */

    private static void Trace(string index)
    {
        var result = Query(index, "晴天", explain: true);
        var trace = result.Trace;
        Program.Check("trace: 仅 explain 时返回", trace is not null, "null");
        if (trace is null) return;

        Program.Check("trace: 至少一步执行", trace.Steps.Any(step => step.State == "ran"),
            string.Join(',', trace.Steps.Select(step => step.State)));
        Program.Check("trace: 主趟命中后兜底步标 min_exact",
            trace.Skipped.Any(skip => skip.Reason == "min_exact"),
            string.Join(',', trace.Skipped.Select(skip => skip.Reason)));
        Program.Check("trace: 步骤带 kind / fieldId",
            trace.Steps.All(step => step.Kind.Length > 0 && step.FieldId.StartsWith("0x")),
            string.Join(',', trace.Steps.Select(step => step.Kind)));
    }

    /* ------------------------------------------------------------------ */
    /* v7 可达的 kind                                                      */
    /* ------------------------------------------------------------------ */

    private static void ReachableKinds(string index)
    {
        // partial：两个词只命中一个，丢词步打开（coverage=1/2）。
        // v15（T3.9）：改用「CJK 缺词」而不是拉丁垃圾串——拉丁段已不进首字母列，
        // 若用 `zzzqqq`，CJK 部分的首字母支会独立命中 c2，主趟不再是 miss。
        var partial = Query(index, "晴天 银河", explain: true);
        Program.Check("kind: 丢词命中记 partial",
            partial.Items.Count > 0
            && partial.Explanations.TryGetValue("c2", out var drop)
            && drop.Kind == "partial" && drop.Matched < drop.Total && drop.Coverage < 1,
            Describe(partial, "c2"));

        // typo：helo → hello，编辑距离 1。
        var typo = Query(index, "helo", explain: true);
        Program.Check("kind: 编辑距离 1 记 typo",
            typo.Items.Any(hit => hit.TrackId == "t1")
            && typo.Explanations.TryGetValue("t1", out var fuzzy)
            && fuzzy.Kind == "typo" && fuzzy.Exact == "none",
            Describe(typo, "t1"));
        Program.Check("kind: typo 的逐列探测仍落在 title",
            typo.Explanations.TryGetValue("t1", out var fuzzy2) && fuzzy2.ResolvedField == "title",
            typo.Explanations.TryGetValue("t1", out var f3) ? f3.ResolvedField : "missing");
    }

    /* ------------------------------------------------------------------ */
    /* 计划侧未产出的四档 kind：真实 EraStore 上的合成步骤                  */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// phrase / prefix / pinyinPhrase / initials 的生产路径已由
    /// <see cref="SearchKindProximityTests"/>（真实查询 → SearchSession → explain）覆盖；
    /// 这里额外验证 explain 的 kind / field 映射能在**真实 EraStore** 上端到端工作：
    /// 候选来自真实 postings、字段探测来自真实词典 + DocStore，只有 <see cref="Step"/> 是合成的。
    /// 这比比较器层的 rank 向量更接近生产路径（探测 / 归因 / bucket 都是真代码）。
    /// </summary>
    private static void SyntheticKindAttribution(string index)
    {
        var dir = EndToEnd.EraDir(index);
        var name = EraStore.CurrentPointer.Read(dir);
        if (name is null)
        {
            Program.Check("kind: EraStore 存在", false, dir);
            return;
        }

        using var reader = new EraStore.StoreReader(Path.Combine(dir, name));
        var probe = new EraExplainProbe(reader);

        CheckSyntheticKind(reader, probe, MatchKind.Phrase, FieldFamily.Meta,
            pass: SearchPassNames.Lexical, expression: "{title_tok} : \"晴天\"",
            terms: new[] { "晴天" }, expectKind: "phrase", expectTrack: "c2");
        CheckSyntheticKind(reader, probe, MatchKind.Prefix, FieldFamily.Meta,
            pass: SearchPassNames.Lexical, expression: "{title_tok} : \"晴\"*",
            terms: new[] { "晴" }, expectKind: "prefix", expectTrack: "c2");
        CheckSyntheticKind(reader, probe, MatchKind.PinyinPhrase, FieldFamily.Pinyin,
            pass: SearchPassNames.PinyinLoose,
            expression: "{pinyin_meta_tok} : (\"qing\" AND \"tian\")",
            terms: new[] { "qing", "tian" }, expectKind: "pinyinPhrase", expectTrack: "c2");
        CheckSyntheticKind(reader, probe, MatchKind.Initials, FieldFamily.Initials,
            pass: SearchPassNames.Lexical, expression: "{initials_meta_tok} : \"qt\"",
            terms: new[] { "qt" }, expectKind: "initials", expectTrack: "c2");
    }

    /// <summary>
    /// 在真实倒排上执行一条表达式，取指定曲目，按合成步骤构造证据 + explain：
    /// kind 名、bucket[0]、字段族映射与逐列探测（title / 0x01）都必须成立。
    /// </summary>
    private static void CheckSyntheticKind(
        EraStore.StoreReader reader, EraExplainProbe probe, MatchKind kind,
        FieldFamily family, string pass, string expression, string[] terms,
        string expectKind, string expectTrack)
    {
        var step = new Step("synthetic@1", kind, family, MatchOp.Phrase, terms,
            terms.Length, 2000, pass, StepGate.None, expression);
        var hits = EraStore.EraQueryPlan.Execute(reader, expression, -1);
        var candidate = hits.FirstOrDefault(hit => hit.TrackIdHex == expectTrack);
        if (candidate.TrackIdHex != expectTrack)
        {
            Program.Check($"kind: {expectKind} 步骤在真实倒排上有 {expectTrack} 命中", false,
                string.Join(',', hits.Select(hit => hit.TrackIdHex)));
            return;
        }

        var docId = (long)candidate.SegmentId * 1_000_000L + candidate.DocId;
        var evidence = SearchRanker.FromStep(step, terms.Length);
        var built = new Candidate(docId, candidate.TrackIdHex, evidence, candidate.Score, pass);
        var probes = new Dictionary<string, List<StepProbe>>(StringComparer.Ordinal)
        {
            [pass] = new List<StepProbe> { new(terms, expression) },
        };
        var explanation = SearchExplainBuilder.Build(built, terms.Length, probes, probe);

        Program.Check($"kind: {expectKind} 归因 kind 名",
            explanation.Kind == expectKind, explanation.Kind);
        Program.Check($"kind: {expectKind} bucket[0] == KindRank({expectKind})",
            explanation.Bucket[0] == SearchRanker.KindRank(kind),
            explanation.Bucket[0].ToString(CultureInfo.InvariantCulture));
        Program.Check($"kind: {expectKind} 字段探测落在 title / 0x01",
            explanation.ResolvedField == "title" && explanation.ResolvedFieldId == "0x01"
            && explanation.Field == "title",
            $"{explanation.Field}/{explanation.ResolvedField}");
    }

    /* ------------------------------------------------------------------ */
    /* 断言小工具                                                          */
    /* ------------------------------------------------------------------ */

    private static string Song(string dir, string name) => Path.Combine(dir, name);

    private static SearchResult Query(string index, string query, bool explain)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Limit = 200,
            Fuzzy = true,
            MinExact = 1,
            Explain = explain,
        });

    private static double Score(SearchResult result, string trackId)
        => result.Items.First(hit => hit.TrackId == trackId).Score;

    private static string Hits(SearchResult result)
        => string.Join(',', result.Items.Take(10).Select(hit => hit.TrackId));

    private static string Bucket(SearchExplanation explanation)
        => string.Join('/', explanation.Bucket.Select(value => value.ToString("G", CultureInfo.InvariantCulture)));

    private static string Describe(SearchResult result, string trackId)
        => result.Explanations.TryGetValue(trackId, out var explanation)
            ? $"{explanation.Kind} {explanation.Matched}/{explanation.Total} {explanation.Coverage:G3}"
            : "missing: " + Hits(result);
}
