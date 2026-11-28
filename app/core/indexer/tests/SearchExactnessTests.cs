// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T3.11 字面精确度 / 邻近度复核。
///
/// <list type="number">
///   <item><b>矩阵</b>：相等 / 前缀 / 包含 / 无（CJK + 拉丁）、空白不敏感、
///         繁简（分析器归一）等价、空元数据、三列取最优并回传列；</item>
///   <item><b>端到端排序</b>：字面步（scope=metadata 与 scope=all 的混合主趟）按
///         「精确度 + 真实字段」排序；只命中歌词列的 doc 记 exact=none + 保守 field=lyrics；</item>
///   <item><b>成本护栏</b>：单步未截断命中 &gt; cap 时不读 DocStore，排序退回复核前的行为；</item>
///   <item><b>explain</b>：复核生效时 field == resolvedField，bucket 的第 4 / 5 元是真实值。</item>
/// </list>
/// </summary>
internal static class SearchExactnessTests
{
    public static void Run()
    {
        RunMatrix();
        RunEndToEnd();
    }

    /* ------------------------------------------------------------------ */
    /* 矩阵：Rank(query, field)                                            */
    /* ------------------------------------------------------------------ */

    private static void RunMatrix()
    {
        // CJK：相等 / 前缀 / 包含 / 无。
        Check("exact: CJK 字段 == 查询 → 0", "晴天", "晴天", SearchExactness.Equal);
        Check("exact: CJK 字段以查询开头 → 1", "晴天", "晴天娃娃", SearchExactness.StartsWith);
        Check("exact: CJK 字段包含查询 → 2", "晴天", "说好的晴天", SearchExactness.Contains);
        Check("exact: CJK 不相关 → 3", "晴天", "雨天", SearchExactness.None);

        // 空白不敏感：查询归一形去空白，字段侧比较时同样跳过 ASCII 空格。
        Check("exact: 空白不敏感（CJK）", "我爱你 中国", "我爱你中国", SearchExactness.Equal);
        Check("exact: 空白不敏感（拉丁多空格 / 大小写）", "  Hello   World ", "hello world",
            SearchExactness.Equal);
        Check("exact: 空白不敏感（拉丁包含）", "we need", "all we need", SearchExactness.Contains);

        // 拉丁：相等 / 前缀 / 包含 / 无。
        Check("exact: 拉丁字段 == 查询 → 0", "hello world", "hello world", SearchExactness.Equal);
        Check("exact: 拉丁字段以查询开头 → 1", "hello", "hello world", SearchExactness.StartsWith);
        Check("exact: 拉丁字段包含查询 → 2", "world", "hello world", SearchExactness.Contains);
        Check("exact: 拉丁不相关 → 3", "jazz", "hello world", SearchExactness.None);

        // 分析器归一：繁体查询 vs 简体字段（Normalize 里繁转简，两侧同源）。
        Check("exact: 繁体查询 == 简体字段", "時光", "时光", SearchExactness.Equal);
        Check("exact: 去音标（é → e）", "beyonce", "Beyoncé", SearchExactness.Equal);

        // 空元数据 / 空查询：一律近似档（不允许「空串包含空查询」误判成相等）。
        Check("exact: 字段为空 → 3", "晴天", "", SearchExactness.None);
        Check("exact: 查询为空 → 3", "", "晴天", SearchExactness.None);

        // 标点不做特殊处理（DocStore v3 的归一语义只有 Normalize + 空白折叠）：
        // 「歌唱。青春」不会被当成「歌唱青春」。这条是刻意钉住的行为，改了必须同步 norm 规则。
        Check("exact: 标点不跳过 → 3", "歌唱青春", "歌唱。青春！", SearchExactness.None);

        // 三列取最优：artist 相等即可拿 0，并回传产生该档位的列。
        var spans = new DocNormSpans(
            Encoding.UTF8.GetBytes("雨过天晴"),
            Encoding.UTF8.GetBytes("晴天"),
            ReadOnlySpan<byte>.Empty);
        var best = SearchExactness.RankBest(Query("晴天"), in spans);
        Program.Check("exact: 三列取最优（artist 相等 → 0 / field=artist）",
            best.Rank == SearchExactness.Equal && best.Field == FieldKind.Artist
            && best.Matched,
            $"{best.Rank}/{best.Field}");

        // 三列都不命中：Rank=none 且 Matched=false（调用方必须保留保守字段映射）。
        var missSpans = new DocNormSpans(
            Encoding.UTF8.GetBytes("雨天"),
            Encoding.UTF8.GetBytes("合辑"),
            ReadOnlySpan<byte>.Empty);
        var miss = SearchExactness.RankBest(Query("晴天"), in missSpans);
        Program.Check("exact: 三列都不命中 → none / Matched=false",
            miss.Rank == SearchExactness.None && !miss.Matched,
            $"{miss.Rank}/{miss.Field}/{miss.Matched}");

        // 单 term / 短语 / 散落多 term 的邻近度规则。
        var single = Step("lexical@1", FieldFamily.Meta, "\"hello\"", MatchOp.And);
        Program.Check("proximity: 单 term = 0",
            SearchRanker.FromStep(single, 1).Proximity == 0,
            SearchRanker.FromStep(single, 1).Proximity.ToString());
        // T2.5：positioned 不再由「步骤形状有没有短语」推断——引擎给出真实降级事实
        // （EraQueryHits.AnyPhraseDowngraded），SearchSession 覆盖它；形状层默认 true。
        Program.Check("positioned: 形状层默认 true（引擎覆盖）",
            SearchRanker.FromStep(single, 1).Positioned, "false");

        var phrase = Step("phonetic@1", FieldFamily.Meta, "\"how are you\"", MatchOp.Phrase);
        Program.Check("proximity: 单一多 token 短语 = token 数 - 1",
            SearchRanker.FromStep(phrase, 3).Proximity == 2,
            SearchRanker.FromStep(phrase, 3).Proximity.ToString());
        Program.Check("proximity: 短语步 positioned=true",
            SearchRanker.FromStep(phrase, 3).Positioned, "false");

        var scattered = Step("lexical@1", FieldFamily.Meta, "\"晴天\" AND \"晴\" AND \"天*\"",
            MatchOp.And);
        Program.Check("proximity: 散落多 term = 算不出",
            SearchRanker.FromStep(scattered, 3).Proximity == SearchRanker.ProximityUnknown,
            SearchRanker.FromStep(scattered, 3).Proximity.ToString());

        var lyrics = Step("lexical@1", FieldFamily.Mixed, "\"晴天\" AND \"晴\" AND \"天*\"",
            MatchOp.And);
        // Q2：形状层对散落多 term 不猜（任何族都记算不出），真实跨度由引擎读位置块填充。
        Program.Check("proximity: 形状层散落多 term = 算不出（引擎填充）",
            SearchRanker.FromStep(lyrics, 3).Proximity == SearchRanker.ProximityUnknown,
            SearchRanker.FromStep(lyrics, 3).Proximity.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 端到端                                                              */
    /* ------------------------------------------------------------------ */

    private static void RunEndToEnd()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-exactness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var audio = Path.Combine(root, "audio");
            Directory.CreateDirectory(audio);

            EndToEnd.CreateLibrary(db, generation: 1);
            using (var conn = EndToEnd.Open(db))
            {
                // 同一个词元「晴天」的四种字面精确度：相等 / 前缀 / 包含 / 无。
                // c1 给一段长歌词：只拉长行长度（bm25 变差），不改标题的相等档 ——
                // 这样「精确度压过 bm25」在 bm25 视角下是可见的（关掉复核顺序会变）。
                Insert(conn, audio, "c1", "晴天", "周杰伦", lyrics:
                    string.Concat(Enumerable.Repeat("梦想的家在无人的街上飘荡，", 40)));
                Insert(conn, audio, "c2", "晴天娃娃", "合辑一");
                // c3 重复三遍「晴天」：bm25 明显更好，但字面只是「包含」——
                // 精确度（contains）应该压过它的 bm25 优势。
                Insert(conn, audio, "c3", "说好的晴天说好的晴天说好的晴天", "合辑二");
                // c4 只能靠拼音同形命中（qing tian）：字面精确度必须是「无」。
                // 标题重复三遍让 bm25 明显偏好它 —— 用来证明精确度确实压过了 bm25。
                Insert(conn, audio, "c4", "青田青田青田", "合辑三");
                // 繁简：查询「時光」应命中简体的「时光」并拿到相等档。
                Insert(conn, audio, "t1", "时光", "罗大佑");
                Insert(conn, audio, "t2", "时光机器", "罗大佑");
                // 单词查询的邻近度 0 + 相等档。
                Insert(conn, audio, "b1", "BEYOND 精选", "BEYOND");
                Insert(conn, audio, "b2", "海阔天空", "BEYOND");
            }

            EndToEnd.RunEngine(db, index, full: true, lrc: false);
            SearchSession.CloseCached();

            MetadataExactnessBeatsBm25(index);
            MixedScopeUsesExactness(index);
            TraditionalQueryMatchesSimplified(index);
            FieldBuckets(root);
            CapSkipsWideSteps(index);
            ExplainWire(index);
            ExactnessPaging(root);
        }
        finally
        {
            SearchExactness.DebugCapOverride = null;
            SearchSession.CloseCached();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响断言。
            }
        }
    }

    /// <summary>Meta 步（scope=metadata）：精确度压过 bm25，顺序是 相等 → 前缀 → 包含 → 无。</summary>
    private static void MetadataExactnessBeatsBm25(string index)
    {
        var ordered = Query(index, "晴天", metadata: true, explain: true);
        var ids = Ids(ordered);
        Program.Check("exact: metadata 步按精确度排序（相等 → 前缀 → 包含 → 无）",
            ids == "c1,c2,c3,c4", ids);
        Program.Check("exact: metadata 步 field=title / bucket[3] 是真实档位",
            ids == "c1,c2,c3,c4"
            && ordered.Explanations["c1"].Bucket[3] == 0
            && ordered.Explanations["c2"].Bucket[3] == 1
            && ordered.Explanations["c3"].Bucket[3] == 2
            && ordered.Explanations["c4"].Bucket[3] == 3,
            BucketOf(ordered, "c1") + " " + BucketOf(ordered, "c2") + " "
            + BucketOf(ordered, "c3") + " " + BucketOf(ordered, "c4"));
        Program.Check("exact: exact 字符串跟随真实档位",
            ordered.Explanations["c1"].Exact == "equal"
            && ordered.Explanations["c2"].Exact == "startsWith"
            && ordered.Explanations["c3"].Exact == "contains"
            && ordered.Explanations["c4"].Exact == "none",
            $"{ordered.Explanations["c1"].Exact}/{ordered.Explanations["c2"].Exact}/"
            + $"{ordered.Explanations["c3"].Exact}/{ordered.Explanations["c4"].Exact}");

        // 关掉复核（cap=0）→ 退回 bm25 顺序；用来证明上面的顺序确实来自精确度。
        SearchExactness.DebugCapOverride = 0;
        var bm25Result = Query(index, "晴天", metadata: true, explain: false);
        var bm25 = Ids(bm25Result);
        SearchExactness.DebugCapOverride = null;
        Program.Check("exact: 关闭复核后退回 bm25（顺序与精确度不同）",
            bm25 != ids && bm25.Split(',').OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(ids.Split(',').OrderBy(x => x, StringComparer.Ordinal)),
            $"bm25=[{bm25}] exact=[{ids}]");
    }

    /// <summary>
    /// scope=all 的主趟是 <see cref="FieldFamily.Mixed"/>（字面 + 拼音 + 首字母的复合式）：
    /// 复核生效后它同样按「精确度 → 真实字段」排序，不再一律按歌词记。
    /// </summary>
    private static void MixedScopeUsesExactness(string index)
    {
        var result = Query(index, "晴天", metadata: false, explain: true);
        var ids = Ids(result);
        Program.Check("exact: scope=all 主趟按精确度排序（相等 → 前缀 → 包含 → 无）",
            ids == "c1,c2,c3,c4", ids);
        Program.Check("exact: 混合步命中标题 → field=title / exact=equal",
            result.Explanations.TryGetValue("c1", out var c1)
            && c1.Field == "title" && c1.FieldId == "0x01"
            && c1.Exact == "equal" && c1.Bucket[3] == SearchExactness.Equal,
            BucketOf(result, "c1"));
        // c4 只能靠拼音同形（qing tian）命中：三列归一形都不含查询 → exact=none，
        // field 保留字段族的保守映射（Mixed → lyrics），"宁可把歌词命中排低"。
        Program.Check("exact: 只命中转写列 → exact=none + 保守 field=lyrics",
            result.Explanations.TryGetValue("c4", out var c4)
            && c4.Exact == "none" && c4.Bucket[3] == SearchExactness.None
            && c4.Field == "lyrics" && c4.FieldId == "0x05"
            && c4.ResolvedField == "lyrics",
            BucketOf(result, "c4"));
        Program.Check("exact: 复核生效时 field 与 resolvedField 同值",
            result.Explanations.Values.All(e => e.Field == e.ResolvedField
                && e.FieldId == e.ResolvedFieldId),
            string.Join(';', result.Explanations.Values.Select(
                e => $"{e.Field}/{e.ResolvedField}")));
        Program.Check("exact: bucket[2] 与 field 档位一致",
            result.Explanations.Values.All(e => e.Bucket[2] == FieldRankOf(e.Field)),
            string.Join(';', result.Explanations.Values.Select(
                e => $"{e.Field}={e.Bucket[2]}")));
        Program.Check("exact: 混合步仍召回四条", result.Items.Count == 4, ids);
    }

    /// <summary>
    /// scope=all 的字段桶：同一查询分别命中标题 / 歌手 / 专辑 / 歌词四列时，
    /// 复核必须给出真实字段，只命中歌词列时退回保守映射；并验证宽命中步的零读取路径。
    /// </summary>
    private static void FieldBuckets(string root)
    {
        var dir = Path.Combine(root, "buckets");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "library.db");
        var index = Path.Combine(dir, "search.db");
        var audio = Path.Combine(dir, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            Insert(conn, audio, "f1", "晴天", "周杰伦", album: """{"name":"叶惠美"}""");
            // 标题不含「晴天」，歌手列才是相等档。
            Insert(conn, audio, "f2", "雨过天晴", "晴天", album: """{"name":"合辑"}""");
            // 只有专辑相等。
            Insert(conn, audio, "f3", "夜色", "某人", album: """{"name":"晴天"}""");
            // 三列元数据都不含查询，只有歌词列命中 → exact=none + 保守 lyrics。
            Insert(conn, audio, "f4", "无关", "某人", album: """{"name":"无关"}""",
                lyrics: "晴天 雨天");
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        var result = Query(index, "晴天", metadata: false, explain: true);
        var ids = Ids(result);
        Program.Check("field: 标题 / 歌手 / 专辑 / 歌词四桶按 FieldRank 排序",
            ids == "f1,f2,f3,f4", ids);
        Program.Check("field: 标题相等 → exact=equal / field=title / resolvedField=title",
            Explain(result, "f1") is { } f1
            && f1.Field == "title" && f1.ResolvedField == "title"
            && f1.Exact == "equal" && f1.Bucket[2] == 0 && f1.Bucket[3] == 0,
            BucketOf(result, "f1"));
        Program.Check("field: 歌手相等 → field=artist / 排在标题匹配之后",
            Explain(result, "f2") is { } f2
            && f2.Field == "artist" && f2.FieldId == "0x02"
            && f2.ResolvedField == "artist" && f2.Exact == "equal"
            && f2.Bucket[2] == 1
            && ids.IndexOf("f1", StringComparison.Ordinal) < ids.IndexOf("f2", StringComparison.Ordinal),
            BucketOf(result, "f2"));
        Program.Check("field: 专辑相等 → field=album / 0x03",
            Explain(result, "f3") is { } f3
            && f3.Field == "album" && f3.FieldId == "0x03"
            && f3.ResolvedField == "album" && f3.Exact == "equal"
            && f3.Bucket[2] == 2,
            BucketOf(result, "f3"));
        // 只命中歌词列：三列归一形都不含查询 → exact=none，field 保留 Mixed → lyrics。
        Program.Check("field: 只命中歌词列 → exact=none / field=lyrics（保守路径）",
            Explain(result, "f4") is { } f4
            && f4.Field == "lyrics" && f4.FieldId == "0x05"
            && f4.ResolvedField == "lyrics"
            && f4.Exact == "none" && f4.Bucket[2] == 4 && f4.Bucket[3] == 3,
            BucketOf(result, "f4"));
        Program.Check("field: 每条都满足 field==resolvedField 且 bucket[2]==FieldRank",
            result.Explanations.Values.All(e =>
                e.Field == e.ResolvedField && e.FieldId == e.ResolvedFieldId
                && e.Bucket[2] == FieldRankOf(e.Field)),
            string.Join(';', result.Explanations.Values.Select(
                e => $"{e.Field}/{e.ResolvedField}/{e.Bucket[2]}")));

        WideStepKeepsPreChangeOrder(index);
    }

    /// <summary>
    /// 单步未截断命中 &gt; cap（<see cref="SearchExactness.DebugCapOverride"/>）：
    /// 不读 DocStore、exactness 一律 none、field 退回保守映射，顺序与**复核前**
    /// （同表达式 + 无复核证据）的排序逐条一致；resolvedField 此时仍是诊断探测。
    /// </summary>
    private static void WideStepKeepsPreChangeOrder(string index)
    {
        SearchExactness.DebugResetDocReads();
        SearchExactness.DebugCapOverride = 0;
        SearchResult wide;
        try
        {
            wide = Query(index, "晴天", metadata: false, explain: true);
        }
        finally
        {
            SearchExactness.DebugCapOverride = null;
        }

        Program.Check("cap: 宽命中步零 DocStore 读取", SearchExactness.DebugDocReads == 0,
            SearchExactness.DebugDocReads.ToString());
        Program.Check("cap: 宽命中步 exactness=none / field 保守 lyrics",
            wide.Explanations.Values.All(e =>
                e.Exact == "none" && e.Bucket[3] == SearchExactness.None
                && e.Field == "lyrics" && e.Bucket[2] == FieldRankOf("lyrics")),
            string.Join(';', wide.Explanations.Values.Select(
                e => $"{e.Field}/{e.Exact}")));
        // 宽命中步没算过精确度 → resolvedField 仍走诊断探测（f1 的标题里确实有查询词）。
        Program.Check("cap: 宽命中步保留 resolvedField 诊断探测（field 仍是排序值）",
            Explain(wide, "f1") is { Field: "lyrics", ResolvedField: "title" },
            BucketOf(wide, "f1"));

        // 「复核前」的排序：同一真实表达式、同一批候选，用不带复核的证据排序。
        var expected = PreChangeOrder(index, "晴天");
        Program.Check("cap: 宽命中步顺序与复核前一致",
            Ids(wide) == expected, $"wide=[{Ids(wide)}] pre=[{expected}]");
    }

    /// <summary>
    /// 用与查询会话同源的表达式在真实 EraStore 上重算候选，并按**无复核**证据排序：
    /// 这是「复核前的排序」的独立重建（不是拿同一份结果自我比较）。
    /// </summary>
    private static string PreChangeOrder(string index, string text)
    {
        var eraName = EraStore.CurrentPointer.Read(EndToEnd.EraDir(index));
        using var reader = new EraStore.StoreReader(
            Path.Combine(EndToEnd.EraDir(index), eraName!));
        var tokens = Analyzer.AnalyzeQuery(text);
        var expression = SearchQuery.CompileMatch(
            tokens, metadata: false, lyricsInitials: true);
        var hits = EraStore.EraQueryPlan.ExecuteDetailed(reader, expression!, -1);
        var candidates = new List<Candidate>(hits.Candidates.Count);
        var step = new Step("lexical@1", MatchKind.Exact, FieldFamily.Mixed, MatchOp.And,
            tokens.Lexical.ToArray(), tokens.Lexical.Count, 2000,
            SearchPassNames.Lexical, StepGate.None, expression);
        var evidence = SearchRanker.FromStep(step, tokens.Lexical.Count);
        foreach (var ec in hits.Candidates)
        {
            var docId = (long)ec.SegmentId * 1_000_000L + ec.DocId;
            candidates.Add(new Candidate(docId, ec.TrackIdHex, evidence, ec.Score, step.Pass));
        }
        SearchRanker.Sort(candidates);
        return string.Join(',', candidates.Select(c => c.TrackId));
    }

    private static void TraditionalQueryMatchesSimplified(string index)
    {
        var result = Query(index, "時光", metadata: true, explain: true);
        Program.Check("exact: 繁体查询命中简体标题且拿相等档",
            Ids(result) == "t1,t2"
            && result.Explanations["t1"].Bucket[3] == SearchExactness.Equal
            && result.Explanations["t2"].Bucket[3] == SearchExactness.StartsWith,
            Ids(result) + " " + BucketOf(result, "t1") + " " + BucketOf(result, "t2"));
    }

    /// <summary>单步未截断命中 &gt; cap：不读 DocStore，档位一律 none，顺序退回 bm25。</summary>
    private static void CapSkipsWideSteps(string index)
    {
        SearchExactness.DebugResetDocReads();
        SearchExactness.DebugCapOverride = 2;
        SearchResult wide;
        try
        {
            wide = Query(index, "晴天", metadata: true, explain: true);
        }
        finally
        {
            SearchExactness.DebugCapOverride = null;
        }

        Program.Check("cap: 宽命中步不读 DocStore", SearchExactness.DebugDocReads == 0,
            SearchExactness.DebugDocReads.ToString());
        Program.Check("cap: 宽命中步 exactness 一律 none",
            wide.Explanations.Values.All(e => e.Bucket[3] == SearchExactness.None),
            string.Join(';', wide.Explanations.Values.Select(Bucket)));
        Program.Check("cap: 宽命中步退回 bm25 顺序",
            Ids(wide) != "c1,c2,c3,c4", Ids(wide));

        // 同一条查询在预算内时确实读了 DocStore（4 条候选各读一次）。
        SearchExactness.DebugResetDocReads();
        Query(index, "晴天", metadata: true, explain: false);
        Program.Check("cap: 预算内按候选读 DocStore", SearchExactness.DebugDocReads == 4,
            SearchExactness.DebugDocReads.ToString());
    }

    /// <summary>explain：bucket 5 元，第 4 / 5 元为真实值；单 term 邻近度 = 0。</summary>
    private static void ExplainWire(string index)
    {
        var result = Query(index, "BEYOND", metadata: true, explain: true);
        // b1 标题是「BEYOND 精选」（前缀）、artist 是「BEYOND」（相等）：
        // 三列取最优 → 相等档；单 term → proximity=0。
        Program.Check("explain: 单 term 命中（三列取最优 → 相等 / proximity=0）",
            result.Explanations.TryGetValue("b1", out var b1)
            && b1.Bucket.Length == 5
            && b1.Bucket[3] == SearchExactness.Equal
            && b1.Bucket[4] == 0
            && b1.Proximity == 0
            && b1.Exact == "equal",
            result.Explanations.TryGetValue("b1", out var hit) ? Bucket(hit) : Ids(result));
    }

    /* ------------------------------------------------------------------ */
    /* 翻页：exactness 生效时第 2 页与一次取全同序                          */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 精确度生效时的 keyset 翻页：游标按 <c>DocId</c> 切在同一全序上，
    /// 两页拼起来必须与一次取全逐条一致（T2.10 §4.4 的「跨分页 top-k 完全一致」）。
    /// </summary>
    private static void ExactnessPaging(string root)
    {
        var dir = Path.Combine(root, "paging");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "library.db");
        var index = Path.Combine(dir, "search.db");
        var audio = Path.Combine(dir, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            Insert(conn, audio, "e1", "hello", "artist");
            Insert(conn, audio, "e2", "hello world", "artist");
            Insert(conn, audio, "e3", "say hello", "artist");
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        var full = PagedQuery(index, cursorRaw: null, cursor: null, limit: 100);
        Program.Check("exact+分页: 一次取全按精确度排序", Ids(full) == "e1,e2,e3", Ids(full));

        var seen = new List<string>();
        string? raw = null;
        SearchCursor? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = PagedQuery(index, raw, cursor, limit: 1);
            pages++;
            seen.AddRange(page.Items.Select(hit => hit.TrackId));
            if (page.Next is null) break;
            raw = page.Next;
            cursor = SearchPaging.TryDecode(raw, out var decoded) ? decoded : null;
            if (pages > 5) break;
        }

        Program.Check("exact+分页: 三页取完", pages == 3, pages.ToString());
        Program.Check("exact+分页: 分页顺序与一次取全一致",
            seen.SequenceEqual(full.Items.Select(hit => hit.TrackId), StringComparer.Ordinal),
            string.Join(',', seen));
        Program.Check("exact+分页: 游标 proximity 与排序值一致（单 term = 0）",
            cursor is null || cursor.Value.Proximity == 0,
            cursor is null ? "null" : cursor.Value.Proximity.ToString());
    }

    private static SearchResult PagedQuery(
        string index, string? cursorRaw, SearchCursor? cursor, int limit)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = "hello",
            Metadata = true,
            Limit = limit,
            Fuzzy = true,
            MinExact = 1,
            Cache = false,
            Cursor = cursor,
            CursorRaw = cursorRaw,
            // capped 让每页看到同一候选集（take=10002），排序差异只可能来自精确度复核。
            TotalMode = SearchTotalMode.Capped,
        });

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private static Step Step(string id, FieldFamily family, string expression, MatchOp op)
        => new(id, MatchKind.Exact, family, op, Array.Empty<string>(),
            CoveredTerms: 1, Cap: 2000, Pass: SearchPassNames.Lexical,
            StepGate.None, Expression: expression);

    private static void Insert(
        Microsoft.Data.Sqlite.SqliteConnection conn, string audio, string id,
        string title, string artist, string? album = null, string? lyrics = null)
        => EndToEnd.InsertTrack(
            conn, id, Path.Combine(audio, id + ".mp3"), title,
            "[{\"name\":\"" + artist + "\"}]", album, lyrics, 1000);

    private static SearchResult Query(
        string index, string text, bool metadata, bool explain)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = text,
            Metadata = metadata,
            Limit = 50,
            Fuzzy = true,
            MinExact = 1,
            Explain = explain,
            Cache = false,
        });

    private static void Check(string name, string query, string field, int expected)
    {
        var actual = SearchExactness.Rank(Query(query), Encoding.UTF8.GetBytes(
            Analyzer.NormalizeMetadata(field)));
        Program.Check(name, actual == expected,
            $"{query} vs {field}: {actual} != {expected}");
    }

    private static byte[] Query(string raw) => SearchExactness.NormalizeQuery(raw);

    private static string Ids(SearchResult result)
        => string.Join(',', result.Items.Select(hit => hit.TrackId));

    private static string BucketOf(SearchResult result, string trackId)
        => result.Explanations.TryGetValue(trackId, out var explanation)
            ? Bucket(explanation) : "missing";

    private static SearchExplanation? Explain(SearchResult result, string trackId)
        => result.Explanations.TryGetValue(trackId, out var explanation) ? explanation : null;

    /// <summary>wire 字段名 → 排序第 3 键的档位（独立于内部枚举，兼作契约断言）。</summary>
    private static int FieldRankOf(string field) => field switch
    {
        "title" => 0,
        "artist" => 1,
        "album" => 2,
        "filename" => 3,
        "lyrics" => 4,
        "lyricsTr" => 5,
        "lyricsRom" => 6,
        _ => 4,
    };

    private static string Bucket(SearchExplanation explanation)
        => string.Join('/', explanation.Bucket.Select(
            value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
