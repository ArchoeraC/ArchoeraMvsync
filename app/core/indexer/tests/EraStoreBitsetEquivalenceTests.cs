// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore bitset 召回路径（<c>BuildAtomBits</c> / <c>OrPostings</c> / <c>EvalBits</c> /
/// <c>PopCountBits</c> / top-k 堆）与旧迭代器树的语义等价回归。
///
/// <para>参考实现是本文件里逐行保留的旧 <c>EraQueryPlan</c>（迭代器召回 + 每候选二分词典 /
/// 位置校验 + <see cref="EraBm25.Score"/>），唯一有意的差异是：df 与结果都跳过墓碑死行——
/// 这正是新路径要落实的契约（旧实现在有墓碑后会多算 df）。对拍覆盖：单词 / 前缀（含
/// <c>MaxPrefixExpansions</c> 截断）/ 短语 / 多字段 / 列作用域与嵌套 / AND-OR 嵌套 /
/// 死行 / 单段与多段 / top-k 截断与 <c>MaxTopK</c> 溢出保护。</para>
///
/// <para><b>df 缓存失效</b>：缓存挂在 <see cref="StoreReader"/> 实例上（键 = 完整表达式），
/// 而 <c>SearchSession</c> 在 CURRENT / 提交世代变化时重开 reader；因此增量写入后新 reader
/// 不会读到期 df。本文件 <c>CheckDeadDocs</c> 先在旧 reader 上查询（写入缓存），再跨世代
/// 重开并断言结果等于「只含活行的重建索引」。</para>
/// </summary>
internal static class EraStoreBitsetEquivalenceTests
{
    /// <summary>与 <c>EraQueryPlan.MaxPrefixExpansions</c> 同步的契约值。</summary>
    private const int MaxPrefixExpansions = 10_000;

    /// <summary>与 <c>EraQueryPlan.MaxTopK</c> 同步的契约值（超过即退化为不截断）。</summary>
    private const int MaxTopK = 16_384;

    private const int SegmentDocs = 6;

    private static readonly (string Name, string Expr)[] Expressions =
    {
        ("单词", "\"alpha\""),
        ("单词-多字段", "\"beta\""),
        ("单词-重复词频", "\"echo\""),
        ("前缀", "\"moon\"*"),
        ("精确词", "\"moonstone\""),
        ("精确与前缀并集", "\"moon\"* OR \"alpha\""),
        ("短语", "\"night sky\""),
        ("三词短语", "\"rain falls down\""),
        ("重复词短语", "\"echo echo\""),
        ("末词前缀短语", "\"night s\"*"),
        ("列作用域", "{title_tok} : \"alpha\""),
        ("列作用域-多列", "{title_tok artist_tok} : \"alpha\""),
        ("嵌套作用域交集", "{title_tok artist_tok} : {title_tok} : \"alpha\""),
        ("作用域优先级", "{title_tok} : \"alpha\" AND \"beta\""),
        ("括号作用域", "{title_tok} : (\"alpha\" AND \"beta\")"),
        ("AND-OR 嵌套", "(\"alpha\" AND \"beta\") OR \"gamma\""),
        ("OR-AND 嵌套", "\"alpha\" AND (\"beta\" OR \"gamma\")"),
        ("未满足 AND", "\"alpha\" AND \"nosuchterm\""),
        ("空命中", "\"nosuchterm\""),
        ("未知列", "{nope_tok} : \"alpha\""),
        ("多列短语", "{title_tok artist_tok} : \"night sky\""),
        ("作用域内前缀", "{title_tok} : \"moon\"*"),
        ("多分支 OR", "\"alpha\" OR \"beta\" OR \"gamma\" OR \"moon\"*"),
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
        var singleDir = Path.Combine(dir, "single");
        var multiDir = Path.Combine(dir, "multi");
        Directory.CreateDirectory(singleDir);
        Directory.CreateDirectory(multiDir);
        var singlePath = Path.Combine(singleDir, "search-1.eraindex");
        var multiPath = Path.Combine(multiDir, "search-1.eraindex");
        var singleBuild = IndexBuilder.Build(singlePath, docs, Options(docs.Count + 1));
        var multiBuild = IndexBuilder.Build(multiPath, docs, Options(SegmentDocs));

        Program.Check("Bitset 语料: 单段 1 段 / 多段 4 段",
            singleBuild.SegmentCount == 1 && multiBuild.SegmentCount == 4,
            $"{singleBuild.SegmentCount}/{multiBuild.SegmentCount}");

        using var single = new StoreReader(singlePath);
        using var multi = new StoreReader(multiPath);
        Program.Check("Bitset 语料: 两个构建 TotalDocs 一致",
            single.TotalDocs == (uint)docs.Count && multi.TotalDocs == single.TotalDocs,
            $"{single.TotalDocs}/{multi.TotalDocs}");

        CompareMatrix("单段", single);
        CompareMatrix("多段", multi);
        CheckExplicitSemantics(single, docs);
        CheckManualScores(single, docs);
        CheckTieBreak(dir);
        CheckMaxTopKGuard(dir);
        CheckDeadDocs(dir);
        CheckPhraseDowngrade(dir);
        CheckPrefixTruncation(dir);
    }

    /* ------------------------------------------------------------------ */
    /* 主对拍：矩阵 + top-k                                               */
    /* ------------------------------------------------------------------ */

    private static void CompareMatrix(string label, StoreReader reader)
    {
        var nonEmpty = 0;
        var topKCases = 0;
        foreach (var (name, expr) in Expressions)
        {
            var full = EraQueryPlan.Execute(reader, expr, 0);
            var reference = ReferenceExecute(reader, expr, 0);
            CheckSameSequence($"Bitset {label} 全量 vs 参考: {name}", full, reference);
            if (full.Count == 0) continue;
            nonEmpty++;

            foreach (var take in TopKCases(full.Count))
            {
                var actual = EraQueryPlan.Execute(reader, expr, take);
                var expected = take >= full.Count ? full : FullSortTake(full, take);
                CheckSameSequence(
                    $"Bitset {label} top-k({take}) vs 全排序: {name}", actual, expected);
                topKCases++;
            }
        }

        // 防假绿：矩阵里大部分表达式必须有真实命中。
        Program.Check($"Bitset {label}: 非空表达式 ≥18（{nonEmpty}/{Expressions.Length}）",
            nonEmpty >= 18, $"{nonEmpty}/{Expressions.Length}");
        Program.Check($"Bitset {label}: top-k 用例覆盖 ≥80", topKCases >= 80,
            topKCases.ToString());
    }

    private static int[] TopKCases(int count)
    {
        var cases = new HashSet<int> { 1, 3, 5, count, count + 1 };
        if (count > 1) cases.Add(count - 1);
        var list = cases.Where(k => k > 0).ToArray();
        Array.Sort(list);
        return list;
    }

    /// <summary>参考口径：按 (Score, SegmentId, DocId) 全排序后取前 k（旧实现 stable sort 的平局序）。</summary>
    private static List<EraCandidate> FullSortTake(List<EraCandidate> full, int take)
    {
        var copy = new List<EraCandidate>(full);
        copy.Sort(static (a, b) =>
        {
            var byScore = a.Score.CompareTo(b.Score);
            if (byScore != 0) return byScore;
            var bySegment = a.SegmentId.CompareTo(b.SegmentId);
            return bySegment != 0 ? bySegment : a.DocId.CompareTo(b.DocId);
        });
        if (take < copy.Count) copy.RemoveRange(take, copy.Count - take);
        return copy;
    }

    /* ------------------------------------------------------------------ */
    /* 列作用域 / 前缀 / 短语的显式断言                                     */
    /* ------------------------------------------------------------------ */

    private static void CheckExplicitSemantics(StoreReader reader, List<AnalyzedDoc> docs)
    {
        // 作用域只影响 atom 的 Fields：artist 限定不会命中只有 title 有该词的 doc。
        var artistOnly = EraQueryPlan.Execute(reader, "{artist_tok} : \"moonstone\"", 0);
        Program.Check("Bitset 列作用域: 只命中 artist 列",
            artistOnly.Count == 1 && artistOnly[0].TrackIdHex == Track(docs[8]),
            string.Join(',', artistOnly.Select(c => c.TrackIdHex)));

        // 嵌套作用域取交集：外层 {title artist} 再叠 {title} = 仅 title。
        var titleOnly = EraQueryPlan.Execute(reader, "{title_tok} : \"moonstone\"", 0);
        var nested = EraQueryPlan.Execute(
            reader, "{title_tok artist_tok} : {title_tok} : \"moonstone\"", 0);
        CheckSameSequence("Bitset 嵌套作用域: 交集等价于内层", nested, titleOnly);

        // 作用域优先级：只绑定紧随 primary；doc1 的 beta 在 artist。
        var precedence = EraQueryPlan.Execute(
            reader, "{title_tok} : \"alpha\" AND \"beta\"", 0);
        var grouped = EraQueryPlan.Execute(
            reader, "{title_tok} : (\"alpha\" AND \"beta\")", 0);
        Program.Check("Bitset 作用域优先级: 未加括号时 beta 不限列（doc1 命中）",
            ContainsTrack(precedence, Track(docs[1])), precedence.Count.ToString());
        Program.Check("Bitset 作用域优先级: 括号限定整体（doc1 不命中）",
            !ContainsTrack(grouped, Track(docs[1])), grouped.Count.ToString());

        // 前缀 atom 的 Fields 同样生效：title 前缀不含只在 artist 命中的人。
        var titlePrefix = EraQueryPlan.Execute(reader, "{title_tok} : \"moon\"*", 0);
        var artistPrefix = EraQueryPlan.Execute(reader, "{artist_tok} : \"moon\"*", 0);
        Program.Check("Bitset 前缀作用域: doc4 只在 artist 有 moonlight",
            !ContainsTrack(titlePrefix, Track(docs[4]))
                && ContainsTrack(artistPrefix, Track(docs[4])),
            $"{titlePrefix.Count}/{artistPrefix.Count}");

        // 精确词 vs 前缀并集：精确 "moon" 只是前缀展开里的一个词条。
        var exact = EraQueryPlan.Execute(reader, "\"moon\"", 0);
        var prefix = EraQueryPlan.Execute(reader, "\"moon\"*", 0);
        Program.Check("Bitset 前缀: 精确词严格包含于前缀并集",
            exact.Count > 0 && prefix.Count > exact.Count
                && exact.All(e => ContainsTrack(prefix, e.TrackIdHex)),
            $"{exact.Count}/{prefix.Count}");

        // 短语：title-only 不含 doc4（night sky 只在 artist），title+artist 含。
        var phraseTitle = EraQueryPlan.Execute(reader, "{title_tok} : \"night sky\"", 0);
        var phraseTwo = EraQueryPlan.Execute(
            reader, "{title_tok artist_tok} : \"night sky\"", 0);
        Program.Check("Bitset 短语作用域: 多列并集包含只在 artist 相邻的 doc4",
            !ContainsTrack(phraseTitle, Track(docs[4]))
                && ContainsTrack(phraseTwo, Track(docs[4])),
            $"{phraseTitle.Count}/{phraseTwo.Count}");

        // 短语自身仍然校验顺序：sky night 不是 night sky。
        var reversed = EraQueryPlan.Execute(reader, "\"sky night\"", 0);
        Program.Check("Bitset 短语: 逆序不命中", reversed.Count == 0,
            reversed.Count.ToString());

        // 重复词短语按位置枚举；doc8 的 echo 不相邻。
        var repeated = EraQueryPlan.Execute(reader, "\"echo echo\"", 0);
        Program.Check("Bitset 短语: 重复词枚举相邻起点",
            ContainsTrack(repeated, Track(docs[7]))
                && ContainsTrack(repeated, Track(docs[19]))
                && !ContainsTrack(repeated, Track(docs[8])),
            string.Join(',', repeated.Select(c => c.TrackIdHex)));

        // 末词前缀短语：night star / stone / sky 都是 night s*。
        var prefixLast = EraQueryPlan.Execute(reader, "\"night s\"*", 0);
        Program.Check("Bitset 短语: 末词前缀展开", ContainsTrack(prefixLast, Track(docs[14])),
            prefixLast.Count.ToString());

        // 空作用域列 / 未满足 AND / 未登录词都不产生候选。
        var unknown = EraQueryPlan.Execute(reader, "{nope_tok} : \"alpha\"", 0);
        var andMiss = EraQueryPlan.Execute(reader, "\"alpha\" AND \"nosuchterm\"", 0);
        var oov = EraQueryPlan.Execute(reader, "\"nosuchterm\"", 0);
        Program.Check("Bitset 空作用域列返回空", unknown.Count == 0, unknown.Count.ToString());
        Program.Check("Bitset 未满足 AND 返回空", andMiss.Count == 0, andMiss.Count.ToString());
        Program.Check("Bitset 未登录词返回空", oov.Count == 0, oov.Count.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 加权 tf / df 的手工校验（多字段、多词条展开）                         */
    /* ------------------------------------------------------------------ */

    private static void CheckManualScores(StoreReader reader, List<AnalyzedDoc> docs)
    {
        // doc9 的 moonstone 同时出现在 title 与 artist：tf = 1×4 + 1×3。
        var exact = EraQueryPlan.Execute(reader, "\"moonstone\"", 0);
        var exactExpected = ManualScore(docs, reader, 9, "moonstone", prefix: false);
        var exactActual = ScoreOf(exact, Track(docs[9]));
        Program.Check("Bitset 手工分数: 多字段精确词 = Σ tf×weight", exactActual is { } e
            && Close(e, exactExpected), $"{exactActual}/{exactExpected}");

        // 前缀展开跨字段累加：doc9 的 moon* 在 title（moonstone）与 artist（moonstone）各 1 次。
        var prefix = EraQueryPlan.Execute(reader, "\"moon\"*", 0);
        var prefixExpected = ManualScore(docs, reader, 9, "moon", prefix: true);
        var prefixActual = ScoreOf(prefix, Track(docs[9]));
        Program.Check("Bitset 手工分数: 前缀多词条 / 多字段 tf 与 df 一致",
            prefixActual is { } p && Close(p, prefixExpected),
            $"{prefixActual}/{prefixExpected}");

        // df 是「命中的活文档数」，不是字段命中数：moonstone 出现在 3 个 doc（其中 doc9 两列）。
        var df = 0;
        foreach (var doc in docs)
            if (HasField(doc, FieldId.Title, "moonstone", prefix: false)
                || HasField(doc, FieldId.Artist, "moonstone", prefix: false)
                || HasField(doc, FieldId.Album, "moonstone", prefix: false))
                df++;
        Program.Check("Bitset df: 多字段命中同一 doc 只计一次（df=3）", df == 3, df.ToString());
    }

    /// <summary>按 bm25 文档公式复算 doc 的单 atom 分数（所有列作用域 = AllFields）。</summary>
    private static double ManualScore(
        List<AnalyzedDoc> docs, StoreReader reader, int docIndex, string term, bool prefix)
    {
        var df = 0;
        foreach (var doc in docs)
            if (HasField(doc, FieldId.Title, term, prefix)
                || HasField(doc, FieldId.Artist, term, prefix)
                || HasField(doc, FieldId.Album, term, prefix)
                || HasField(doc, FieldId.Filename, term, prefix)
                || HasField(doc, FieldId.Lyrics, term, prefix)
                || HasField(doc, FieldId.LyricsTranslation, term, prefix)
                || HasField(doc, FieldId.LyricsRomanization, term, prefix)
                || HasField(doc, FieldId.PinyinMeta, term, prefix)
                || HasField(doc, FieldId.PinyinLyrics, term, prefix)
                || HasField(doc, FieldId.InitialsMeta, term, prefix)
                || HasField(doc, FieldId.InitialsLyrics, term, prefix)
                || HasField(doc, FieldId.ReadingMeta, term, prefix))
                df++;

        var hit = new EraPhraseHit { Df = df };
        for (var i = 1; i <= 12; i++)
        {
            var field = (FieldId)i;
            var tf = CountField(docs[docIndex], field, term, prefix);
            if (tf > 0) hit.Hits.Add(new EraHit { Field = field, Tf = tf });
        }

        var rowLen = 0;
        foreach (var tokens in docs[docIndex].Tokens.Values) rowLen += tokens.Length;
        var avgRowLen = 0.0;
        foreach (var avg in reader.AverageDocLengths()) avgRowLen += avg;
        return EraBm25.Score(new[] { hit }, rowLen, avgRowLen, reader.TotalDocs);
    }

    /* ------------------------------------------------------------------ */
    /* top-k：平局键 / 边界 / 溢出保护                                       */
    /* ------------------------------------------------------------------ */

    private static void CheckTieBreak(string dir)
    {
        var tieDir = Path.Combine(dir, "tie");
        Directory.CreateDirectory(tieDir);
        var path = Path.Combine(tieDir, "search-1.eraindex");
        var docs = new List<AnalyzedDoc>();
        for (var i = 0; i < 6; i++) docs.Add(SimpleDoc(i, (FieldId.Title, new[] { "t" })));
        IndexBuilder.Build(path, docs, Options(3));

        using var reader = new StoreReader(path);
        var full = EraQueryPlan.Execute(reader, "\"t\"", 0);
        var scanOrder = docs.Select(Track).ToList();
        Program.Check("Bitset top-k: 未截断输出 = 旧扫描顺序（段→doc）",
            SameSequence(full.Select(c => c.TrackIdHex), scanOrder),
            string.Join(',', full.Select(c => c.TrackIdHex)));

        // 分数完全相同时，截断键必须按 (SegmentId, DocId) 稳定下落。
        for (var take = 1; take <= 5; take++)
        {
            var actual = EraQueryPlan.Execute(reader, "\"t\"", take);
            var expected = FullSortTake(full, take);
            CheckSameSequence($"Bitset top-k 平局: k={take} 取最早的段/doc", actual, expected);
        }

        // hits == k 与 hits < k 两条路径都保持扫描顺序。
        foreach (var take in new[] { 6, 7, 100 })
        {
            var actual = EraQueryPlan.Execute(reader, "\"t\"", take);
            CheckSameSequence($"Bitset top-k: hits≤k({take}) 保持扫描顺序", actual, full);
        }

        // 溢出 / 不截断路径：> MaxTopK、int.MaxValue、0、负数都不应崩溃或漏召回。
        foreach (var take in new[] { MaxTopK + 1, int.MaxValue, 0, -1 })
        {
            var actual = EraQueryPlan.Execute(reader, "\"t\"", take);
            CheckSameSequence($"Bitset top-k: take={take} 走不截断路径", actual, full);
        }
    }

    /// <summary>
    /// <c>MaxTopK</c> 边界：正好 16384 仍走堆并取前 k；16385 起按设计退化为不截断
    /// （防御超大 limit+offset），不能溢出或漏召回。
    /// </summary>
    private static void CheckMaxTopKGuard(string dir)
    {
        var guardDir = Path.Combine(dir, "max-topk");
        Directory.CreateDirectory(guardDir);
        var path = Path.Combine(guardDir, "search-1.eraindex");
        var docs = new List<AnalyzedDoc>(MaxTopK + 16);
        for (var i = 0; i < MaxTopK + 16; i++)
            docs.Add(SimpleDoc(i, (FieldId.Title, new[] { "t" })));
        IndexBuilder.Build(path, docs, Options(docs.Count));

        using var reader = new StoreReader(path);
        var full = EraQueryPlan.Execute(reader, "\"t\"", 0);
        Program.Check("Bitset MaxTopK: 全量命中 16400", full.Count == MaxTopK + 16,
            full.Count.ToString());

        var capped = EraQueryPlan.Execute(reader, "\"t\"", MaxTopK);
        var expected = FullSortTake(full, MaxTopK);
        CheckSameSequence("Bitset MaxTopK: take=16384 走堆并取前 k", capped, expected);
        Program.Check("Bitset MaxTopK: take=16384 恰好截断 16 条",
            capped.Count == MaxTopK && full.Count == MaxTopK + 16,
            $"{capped.Count}/{full.Count}");

        var guard = EraQueryPlan.Execute(reader, "\"t\"", MaxTopK + 1);
        CheckSameSequence("Bitset MaxTopK: take=16385 超限退化为不截断", guard, full);
        var huge = EraQueryPlan.Execute(reader, "\"t\"", int.MaxValue);
        CheckSameSequence("Bitset MaxTopK: huge offset 不溢出", huge, full);
    }

    /* ------------------------------------------------------------------ */
    /* 墓碑：df / tf / 结果都不含死行；跨世代重开 reader 不读旧 df 缓存       */
    /* ------------------------------------------------------------------ */

    private static void CheckDeadDocs(string dir)
    {
        var deadDir = Path.Combine(dir, "dead");
        var freshDir = Path.Combine(dir, "dead-fresh");
        Directory.CreateDirectory(deadDir);
        Directory.CreateDirectory(freshDir);
        var path = Path.Combine(deadDir, "search-1.eraindex");
        var freshPath = Path.Combine(freshDir, "search-1.eraindex");

        IndexBuilder.Build(path, DeadInitialDocs(), Options(4));

        ulong beforeGen;
        using (var before = new StoreReader(path))
        {
            beforeGen = before.Gen;
            var beforeHits = EraQueryPlan.Execute(before, "\"ghost\"", 0);
            Program.Check("Bitset 墓碑: 更新前 ghost 含待删 / 待改曲目",
                ContainsTrack(beforeHits, TrackHex(0))
                    && ContainsTrack(beforeHits, TrackHex(9)),
                beforeHits.Count.ToString());
        }

        // 删除 track0 / track9；改写 track1（ghost→phantom）与 track4（保留 ghost，tf 变化）。
        // 旧版本只加墓碑，不重写旧段 —— 查询必须过滤它们。
        IncrementalUpdater.Apply(
            path,
            new[]
            {
                SimpleDoc(1, (FieldId.Title, new[] { "phantom" })),
                SimpleDoc(4, (FieldId.Title, new[] { "ghost", "ghost" })),
            },
            new[] { TrackBytes(0), TrackBytes(9) },
            new IncrementalOptions { Merge = false, DocsPerSegment = 4 });

        IndexBuilder.Build(freshPath, DeadLiveDocs(), Options(3));
        using var reader = new StoreReader(path);
        using var fresh = new StoreReader(freshPath);

        Program.Check("Bitset 墓碑: 世代递增", reader.Gen > beforeGen,
            $"{beforeGen}->{reader.Gen}");
        Program.Check("Bitset 墓碑: TotalDocs 只计活行（8）",
            reader.TotalDocs == 8 && fresh.TotalDocs == 8,
            $"{reader.TotalDocs}/{fresh.TotalDocs}");

        var ghost = EraQueryPlan.Execute(reader, "\"ghost\"", 0);
        var ghostSet = new HashSet<string>(ghost.Select(c => c.TrackIdHex), StringComparer.Ordinal);
        Program.Check("Bitset 墓碑: ghost 只返回 3 个活文档",
            ghost.Count == 3
                && ghostSet.SetEquals(new[] { TrackHex(2), TrackHex(3), TrackHex(4) }),
            string.Join(',', ghostSet));
        Program.Check("Bitset 墓碑: 被删除 / 改写的旧 ghost 不召回",
            !ghostSet.Contains(TrackHex(0))
                && !ghostSet.Contains(TrackHex(1))
                && !ghostSet.Contains(TrackHex(9)),
            string.Join(',', ghostSet));

        // 同一 reader 第二次查询走 df 缓存，结果必须与第一次逐条一致。
        var ghostAgain = EraQueryPlan.Execute(reader, "\"ghost\"", 0);
        CheckSameSequence("Bitset 墓碑: df 缓存复用结果一致", ghostAgain, ghost);

        var freshGhost = EraQueryPlan.Execute(fresh, "\"ghost\"", 0);
        CheckSameScoreSet("Bitset 墓碑: df/tf/N/avgL 与只含活行的重建索引一致", ghost, freshGhost);

        var phantom = EraQueryPlan.Execute(reader, "\"phantom\"", 0);
        Program.Check("Bitset 墓碑: 改写后的新词命中且旧曲目 id 保留",
            phantom.Count == 2
                && ContainsTrack(phantom, TrackHex(1))
                && ContainsTrack(phantom, TrackHex(6)),
            phantom.Count.ToString());

        var phrase = EraQueryPlan.Execute(reader, "\"ghost ghost\"", 0);
        Program.Check("Bitset 墓碑: 短语只在新版本位置命中",
            ContainsTrack(phrase, TrackHex(2))
                && ContainsTrack(phrase, TrackHex(4))
                && !ContainsTrack(phrase, TrackHex(3)),
            string.Join(',', phrase.Select(c => c.TrackIdHex)));

        var deadExpressions = new[]
        {
            "\"ghost\"", "\"gh\"*", "{title_tok} : \"ghost\"",
            "\"ghost\" AND \"alpha\"", "\"ghost\" OR \"phantom\"",
            "{title_tok} : \"gh*\" OR \"phantom\"",
        };
        foreach (var expr in deadExpressions)
        {
            var actual = EraQueryPlan.Execute(reader, expr, 0);
            CheckSameSequence($"Bitset 墓碑 vs 参考: {expr}",
                actual, ReferenceExecute(reader, expr, 0));
            CheckSameScoreSet($"Bitset 墓碑 vs 活行重建: {expr}",
                actual, EraQueryPlan.Execute(fresh, expr, 0));
        }
    }

    private static List<AnalyzedDoc> DeadInitialDocs() => new()
    {
        SimpleDoc(0, (FieldId.Title, new[] { "ghost", "ghost", "ghost" }),
            (FieldId.Artist, new[] { "ghost" })),
        SimpleDoc(1, (FieldId.Title, new[] { "ghost" })),
        SimpleDoc(2, (FieldId.Title, new[] { "ghost", "alpha" }),
            (FieldId.Lyrics, new[] { "ghost", "ghost" })),
        SimpleDoc(3, (FieldId.Title, new[] { "ghost" }), (FieldId.Album, new[] { "ghost" })),
        SimpleDoc(4, (FieldId.Title, new[] { "ghost", "ghost" })),
        SimpleDoc(5, (FieldId.Title, new[] { "alpha" }), (FieldId.Artist, new[] { "beta" })),
        SimpleDoc(6, (FieldId.Title, new[] { "phantom" })),
        SimpleDoc(7, (FieldId.Title, new[] { "moonstone" })),
        SimpleDoc(8, (FieldId.Title, new[] { "moonlight" })),
        SimpleDoc(9, (FieldId.Title, new[] { "ghost" })),
    };

    private static List<AnalyzedDoc> DeadLiveDocs() => new()
    {
        SimpleDoc(1, (FieldId.Title, new[] { "phantom" })),
        SimpleDoc(2, (FieldId.Title, new[] { "ghost", "alpha" }),
            (FieldId.Lyrics, new[] { "ghost", "ghost" })),
        SimpleDoc(3, (FieldId.Title, new[] { "ghost" }), (FieldId.Album, new[] { "ghost" })),
        SimpleDoc(4, (FieldId.Title, new[] { "ghost", "ghost" })),
        SimpleDoc(5, (FieldId.Title, new[] { "alpha" }), (FieldId.Artist, new[] { "beta" })),
        SimpleDoc(6, (FieldId.Title, new[] { "phantom" })),
        SimpleDoc(7, (FieldId.Title, new[] { "moonstone" })),
        SimpleDoc(8, (FieldId.Title, new[] { "moonlight" })),
    };

    private static string TrackHex(int index) =>
        Convert.ToHexString(TrackBytes(index)).ToLowerInvariant();

    /* ------------------------------------------------------------------ */
    /* 无位置字段的短语降级                                                 */
    /* ------------------------------------------------------------------ */

    private static void CheckPhraseDowngrade(string dir)
    {
        var noPosDir = Path.Combine(dir, "nopos");
        var posDir = Path.Combine(dir, "pos");
        Directory.CreateDirectory(noPosDir);
        Directory.CreateDirectory(posDir);
        var noPosPath = Path.Combine(noPosDir, "search-1.eraindex");
        var posPath = Path.Combine(posDir, "search-1.eraindex");

        var docs = new List<AnalyzedDoc>
        {
            SimpleDoc(0, (FieldId.Title, new[] { "doc0" }),
                (FieldId.PinyinLyrics, new[] { "qing", "tian" })),
            SimpleDoc(1, (FieldId.Title, new[] { "doc1" }),
                (FieldId.PinyinLyrics, new[] { "qing", "yu", "tian" })),
            SimpleDoc(2, (FieldId.Title, new[] { "doc2" }),
                (FieldId.PinyinLyrics, new[] { "tian", "qing" })),
            SimpleDoc(3, (FieldId.Title, new[] { "doc3" }),
                (FieldId.PinyinLyrics, new[] { "qing" })),
        };
        var noPositions = new HashSet<FieldId> { FieldId.PinyinLyrics };
        IndexBuilder.Build(noPosPath, docs, Options(4, noPositions));
        IndexBuilder.Build(posPath, docs, Options(4));

        using var noPos = new StoreReader(noPosPath);
        using var pos = new StoreReader(posPath);

        const string phrase = "{pinyin_lyrics_tok} : \"qing tian\"";
        var relaxed = EraQueryPlan.Execute(noPos, phrase, 0);
        var strict = EraQueryPlan.Execute(pos, phrase, 0);
        var expected = new[]
        { Track(docs[0]), Track(docs[1]), Track(docs[2]) };
        Program.Check("Bitset 短语降级: 无位置字段按 AND 召回（含乱序 / 不相邻）",
            relaxed.Count == expected.Length
                && new HashSet<string>(relaxed.Select(c => c.TrackIdHex)).SetEquals(expected),
            string.Join(',', relaxed.Select(c => c.TrackIdHex)));
        Program.Check("Bitset 短语降级: 有位置字段仍校验相邻（只有 doc0）",
            strict.Count == 1 && strict[0].TrackIdHex == expected[0],
            string.Join(',', strict.Select(c => c.TrackIdHex)));
        Program.Check("Bitset 短语降级: 有位置时短语分数非 0（位置可计数）",
            strict.Count == 1 && strict[0].Score < -1e-12,
            strict.Count == 1 ? strict[0].Score.ToString("R") : "missing");

        var relaxedAnd = EraQueryPlan.Execute(
            noPos, "{pinyin_lyrics_tok} : (\"qing\" AND \"tian\")", 0);
        CheckSameTrackSet("Bitset 短语降级: 召回集合等于纯 AND", relaxedAnd, relaxed);
        Program.Check("Bitset 短语降级: 无位置时无法计短语 tf，分数保持 0",
            relaxed.All(c => Math.Abs(c.Score) < 1e-12),
            string.Join(',', relaxed.Select(c => c.Score.ToString("R"))));

        CheckSameSequence(
            "Bitset 短语降级: 与参考实现一致", relaxed, ReferenceExecute(noPos, phrase, 0));
        CheckSameSequence(
            "Bitset 位置短语: 与参考实现一致", strict, ReferenceExecute(pos, phrase, 0));
    }

    /* ------------------------------------------------------------------ */
    /* 前缀展开截断：第 10001 个词条不得参与召回                              */
    /* ------------------------------------------------------------------ */

    private static void CheckPrefixTruncation(string dir)
    {
        var truncDir = Path.Combine(dir, "prefix-trunc");
        Directory.CreateDirectory(truncDir);
        var path = Path.Combine(truncDir, "search-1.eraindex");

        var terms = new string[MaxPrefixExpansions];
        for (var i = 0; i < terms.Length; i++) terms[i] = "p" + i.ToString("D5");
        var docs = new List<AnalyzedDoc>
        {
            // 前 10000 个词条（p00000..p09999）。
            SimpleDoc(0, (FieldId.Title, terms)),
            // 第 10001 个词条：排序在边界后，前缀展开必须丢弃。
            SimpleDoc(1, (FieldId.Title, new[] { "p10004" })),
            // 同时含边界内 / 外词条：前缀查询只能看到边界内的 p00001。
            SimpleDoc(2, (FieldId.Title, new[] { "p00001", "p10004" })),
        };
        IndexBuilder.Build(path, docs, Options(3));
        using var reader = new StoreReader(path);

        var prefix = EraQueryPlan.Execute(reader, "\"p\"*", 0);
        CheckSameSequence(
            "Bitset 前缀截断: 与参考实现一致", prefix, ReferenceExecute(reader, "\"p\"*", 0));
        Program.Check("Bitset 前缀截断: 边界外词条不召回（恰好 2 个 doc）",
            prefix.Count == 2
                && ContainsTrack(prefix, Track(docs[0]))
                && ContainsTrack(prefix, Track(docs[2]))
                && !ContainsTrack(prefix, Track(docs[1])),
            string.Join(',', prefix.Select(c => c.TrackIdHex)));

        var boundary = EraQueryPlan.Execute(reader, "\"p09999\"", 0);
        Program.Check("Bitset 前缀截断: 边界内最后一个词条仍可精确命中",
            boundary.Count == 1 && boundary[0].TrackIdHex == Track(docs[0]),
            boundary.Count.ToString());

        var exactBeyond = EraQueryPlan.Execute(reader, "\"p10004\"", 0);
        CheckSameSequence(
            "Bitset 前缀截断: 精确词越过截断仍命中",
            exactBeyond, ReferenceExecute(reader, "\"p10004\"", 0));
        Program.Check("Bitset 前缀截断: 精确词越过截断返回 doc1/doc2",
            exactBeyond.Count == 2
                && ContainsTrack(exactBeyond, Track(docs[1]))
                && ContainsTrack(exactBeyond, Track(docs[2])),
            exactBeyond.Count.ToString());

        var union = EraQueryPlan.Execute(reader, "\"p\"* OR \"p10004\"", 0);
        CheckSameSequence(
            "Bitset 前缀截断: 前缀与精确词并集", union,
            ReferenceExecute(reader, "\"p\"* OR \"p10004\"", 0));
        Program.Check("Bitset 前缀截断: 并集覆盖 3 个 doc", union.Count == 3,
            union.Count.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 旧迭代器参考实现（从重写前版本逐行保留；唯一差异 = 过滤墓碑）          */
    /* ------------------------------------------------------------------ */

    private sealed record RefAtom(IReadOnlyList<string> Tokens, bool PrefixLast, FieldId[] Fields);

    private abstract record RefNode;

    private sealed record RefLeaf(int AtomIndex) : RefNode;

    private sealed record RefAnd(IReadOnlyList<RefNode> Children) : RefNode;

    private sealed record RefOr(IReadOnlyList<RefNode> Children) : RefNode;

    private static readonly FieldId[] AllFields =
        Enumerable.Range(1, 12).Select(i => (FieldId)i).ToArray();

    /// <summary>
    /// 旧路径：逐 atom <c>CountDocs</c> 得 df → 迭代器树跨段召回 → 每候选重新二分词典 /
    /// 位置校验 → <see cref="EraBm25.Score"/>。
    ///
    /// <para>与历史二进制相比只改了死行处理：<c>CountDocs</c> 与结果循环都跳过
    /// <see cref="SegmentReaders.IsLive"/> 为 false 的 doc（历史实现没有墓碑概念）。</para>
    /// </summary>
    private static List<EraCandidate> ReferenceExecute(
        StoreReader reader, string expression, int perStepTake)
    {
        FtsNode root;
        try
        {
            root = FtsExpression.Parse(expression);
        }
        catch (FormatException)
        {
            return new List<EraCandidate>();
        }

        var atoms = new List<RefAtom>();
        var tree = RefBuildTree(root, AllFields, atoms);
        if (atoms.Count == 0) return new List<EraCandidate>();

        var segments = reader.OpenPerSegment();
        var n = reader.TotalDocs;
        var avgDls = reader.AverageDocLengths().ToArray();
        var avgRowLen = 0.0;
        foreach (var avg in avgDls) avgRowLen += avg;

        var dfs = new int[atoms.Count];
        for (var i = 0; i < atoms.Count; i++)
        {
            foreach (var seg in segments) dfs[i] += RefCountDocs(seg, atoms[i]);
        }

        var results = new List<EraCandidate>();
        foreach (var seg in segments)
        {
            using var iter = RefBuildNode(root, AllFields, seg);
            if (!iter.Advance(0)) continue;
            do
            {
                var doc = iter.Current;
                if (!seg.IsLive(doc)) continue;
                var trackId = seg.Docs.TrackId(doc);
                if (trackId.Length == 0) continue;
                results.Add(new EraCandidate
                {
                    DocId = doc,
                    SegmentId = seg.SegmentId,
                    TrackIdHex = trackId,
                    Score = RefScoreDoc(seg, atoms, dfs, tree, doc, avgRowLen, n),
                });
            }
            while (iter.Next());
        }

        if (perStepTake > 0 && results.Count > perStepTake)
        {
            results.Sort(static (a, b) => a.Score.CompareTo(b.Score));
            results.RemoveRange(perStepTake, results.Count - perStepTake);
        }
        return results;
    }

    private static RefNode RefBuildTree(FtsNode node, FieldId[] scope, List<RefAtom> atoms)
    {
        switch (node)
        {
            case FtsScope scopeNode:
                return RefBuildTree(scopeNode.Child, Intersect(scope, scopeNode.Columns), atoms);
            case FtsAnd and:
                return new RefAnd(
                    and.Children.Select(child => RefBuildTree(child, scope, atoms)).ToList());
            case FtsOr or:
                return new RefOr(
                    or.Children.Select(child => RefBuildTree(child, scope, atoms)).ToList());
            case FtsTerm term:
                atoms.Add(new RefAtom(new[] { term.Token }, term.Prefix, scope));
                return new RefLeaf(atoms.Count - 1);
            case FtsPhrase phrase:
                atoms.Add(new RefAtom(phrase.Tokens, phrase.PrefixLast, scope));
                return new RefLeaf(atoms.Count - 1);
            default:
                throw new InvalidOperationException($"未知 AST 节点 {node?.GetType().Name}");
        }
    }

    private static bool RefIsSatisfied(RefNode node, bool[] present) => node switch
    {
        RefLeaf leaf => present[leaf.AtomIndex],
        RefOr or => or.Children.Any(child => RefIsSatisfied(child, present)),
        RefAnd and => and.Children.All(child => RefIsSatisfied(child, present)),
        _ => false,
    };

    private static void RefMarkContributing(
        RefNode node, bool enabled, bool[] present, bool[] contributing)
    {
        switch (node)
        {
            case RefLeaf leaf:
                if (enabled && present[leaf.AtomIndex]) contributing[leaf.AtomIndex] = true;
                return;
            case RefOr or:
                foreach (var child in or.Children)
                    RefMarkContributing(child, enabled, present, contributing);
                return;
            case RefAnd and:
                var enabled2 = enabled && and.Children.All(child => RefIsSatisfied(child, present));
                foreach (var child in and.Children)
                    RefMarkContributing(child, enabled2, present, contributing);
                return;
        }
    }

    private static IDocIter RefBuildNode(FtsNode node, FieldId[] scope, SegmentReaders seg) =>
        node switch
        {
            FtsScope scopeNode => RefBuildNode(
                scopeNode.Child, Intersect(scope, scopeNode.Columns), seg),
            FtsAnd and => new AndIter(
                and.Children.Select(child => RefBuildNode(child, scope, seg)).ToList()),
            FtsOr or => new OrIter(
                or.Children.Select(child => RefBuildNode(child, scope, seg)).ToList()),
            FtsTerm term => RefBuildAtomIter(
                seg, new RefAtom(new[] { term.Token }, term.Prefix, scope)),
            FtsPhrase phrase => RefBuildAtomIter(
                seg, new RefAtom(phrase.Tokens, phrase.PrefixLast, scope)),
            _ => throw new InvalidOperationException($"未知 AST 节点 {node?.GetType().Name}"),
        };

    private static IDocIter RefBuildAtomIter(SegmentReaders seg, RefAtom atom)
    {
        if (atom.Fields.Length == 0) return new OrIter(Array.Empty<IDocIter>());
        var iters = new List<IDocIter>(atom.Fields.Length);
        foreach (var field in atom.Fields) iters.Add(RefBuildFieldIter(seg, field, atom));
        return iters.Count == 1 ? iters[0] : new OrIter(iters);
    }

    private static IDocIter RefBuildFieldIter(SegmentReaders seg, FieldId field, RefAtom atom)
    {
        if (atom.Tokens.Count == 1)
        {
            return atom.PrefixLast
                ? new PrefixIter(seg.Dict, seg.Postings, field, atom.Tokens[0], MaxPrefixExpansions)
                : new TermIter(seg.Dict, seg.Postings, field, atom.Tokens[0]);
        }

        var children = new List<IDocIter>(atom.Tokens.Count);
        for (var i = 0; i < atom.Tokens.Count; i++)
        {
            children.Add(i == atom.Tokens.Count - 1 && atom.PrefixLast
                ? new PrefixIter(seg.Dict, seg.Postings, field, atom.Tokens[i], MaxPrefixExpansions)
                : new TermIter(seg.Dict, seg.Postings, field, atom.Tokens[i]));
        }
        return new PhraseIter(children, positioned: true);
    }

    private static FieldId[] Intersect(FieldId[] scope, IReadOnlyList<string> columns)
    {
        var allowed = new HashSet<FieldId>();
        foreach (var column in columns)
            if (ColumnFields.TryGetValue(column, out var field)) allowed.Add(field);

        var result = new List<FieldId>(scope.Length);
        foreach (var field in scope)
            if (allowed.Contains(field)) result.Add(field);
        return result.ToArray();
    }

    private static int RefCountDocs(SegmentReaders seg, RefAtom atom)
    {
        using var iter = RefBuildAtomIter(seg, atom);
        if (!iter.Advance(0)) return 0;
        var count = 0;
        do
        {
            if (seg.IsLive(iter.Current)) count++;
        }
        while (iter.Next());
        return count;
    }

    private static double RefScoreDoc(
        SegmentReaders seg, List<RefAtom> atoms, int[] dfs, RefNode tree, int docId,
        double avgRowLen, uint n)
    {
        var rowLen = 0;
        for (var i = 0; i < 12; i++) rowLen += seg.Docs.DocLen(docId, (FieldId)(i + 1));

        var hits = new EraPhraseHit?[atoms.Count];
        var present = new bool[atoms.Count];
        for (var i = 0; i < atoms.Count; i++)
        {
            if (dfs[i] == 0) continue;
            var hit = new EraPhraseHit { Df = dfs[i] };
            foreach (var field in atoms[i].Fields)
            {
                var tf = RefTfInField(seg, field, atoms[i], docId);
                if (tf > 0) hit.Hits.Add(new EraHit { Field = field, Tf = tf });
            }
            if (hit.Hits.Count == 0) continue;
            hits[i] = hit;
            present[i] = true;
        }

        var contributing = new bool[atoms.Count];
        RefMarkContributing(tree, enabled: true, present, contributing);
        var phrases = new List<EraPhraseHit>(atoms.Count);
        for (var i = 0; i < atoms.Count; i++)
            if (contributing[i] && hits[i] is { } hit) phrases.Add(hit);
        return EraBm25.Score(phrases, rowLen, avgRowLen, n);
    }

    private static int RefTfInField(
        SegmentReaders seg, FieldId field, RefAtom atom, int docId)
    {
        if (atom.Tokens.Count == 1)
        {
            if (!atom.PrefixLast)
            {
                if (!seg.Dict.TryFind(field, atom.Tokens[0], out var info)) return 0;
                var cursor = seg.Postings.Open(info.PostOffset, info.Df);
                return cursor.Advance(docId) && cursor.CurrentDoc == docId ? cursor.CurrentTf : 0;
            }

            var sum = 0;
            foreach (var info in seg.Dict.ScanPrefix(field, atom.Tokens[0], MaxPrefixExpansions))
            {
                var cursor = seg.Postings.Open(info.PostOffset, info.Df);
                if (cursor.Advance(docId) && cursor.CurrentDoc == docId) sum += cursor.CurrentTf;
            }
            return sum;
        }

        var positions = new int[atom.Tokens.Count][];
        for (var i = 0; i < atom.Tokens.Count; i++)
        {
            positions[i] = i == atom.Tokens.Count - 1 && atom.PrefixLast
                ? RefPrefixPositions(seg, field, atom.Tokens[i], docId)
                : RefTermPositions(seg, field, atom.Tokens[i], docId);
            if (positions[i].Length == 0) return 0;
        }

        var count = 0;
        foreach (var start in positions[0])
        {
            var matched = true;
            for (var i = 1; i < positions.Length; i++)
            {
                if (Array.BinarySearch(positions[i], start + i) < 0)
                {
                    matched = false;
                    break;
                }
            }
            if (matched) count++;
        }
        return count;
    }

    private static int[] RefTermPositions(
        SegmentReaders seg, FieldId field, string term, int docId)
    {
        if (!seg.Dict.TryFind(field, term, out var info)) return Array.Empty<int>();
        var cursor = seg.Postings.Open(info.PostOffset, info.Df);
        return cursor.Advance(docId) && cursor.CurrentDoc == docId
            ? cursor.CurrentPositions.ToArray()
            : Array.Empty<int>();
    }

    private static int[] RefPrefixPositions(
        SegmentReaders seg, FieldId field, string prefix, int docId)
    {
        var positions = new List<int>();
        foreach (var info in seg.Dict.ScanPrefix(field, prefix, MaxPrefixExpansions))
        {
            var cursor = seg.Postings.Open(info.PostOffset, info.Df);
            if (cursor.Advance(docId) && cursor.CurrentDoc == docId)
                positions.AddRange(cursor.CurrentPositions.ToArray());
        }
        positions.Sort();
        return positions.ToArray();
    }

    private static readonly Dictionary<string, FieldId> ColumnFields = new(StringComparer.Ordinal)
    {
        ["title_tok"] = FieldId.Title,
        ["artist_tok"] = FieldId.Artist,
        ["album_tok"] = FieldId.Album,
        ["lyrics_tok"] = FieldId.Lyrics,
        ["lyrics_tr_tok"] = FieldId.LyricsTranslation,
        ["lyrics_rom_tok"] = FieldId.LyricsRomanization,
        ["pinyin_meta_tok"] = FieldId.PinyinMeta,
        ["pinyin_lyrics_tok"] = FieldId.PinyinLyrics,
        ["reading_meta_tok"] = FieldId.ReadingMeta,
        ["filename_tok"] = FieldId.Filename,
        ["initials_meta_tok"] = FieldId.InitialsMeta,
        ["initials_lyrics_tok"] = FieldId.InitialsLyrics,
    };

    /* ------------------------------------------------------------------ */
    /* 断言工具                                                            */
    /* ------------------------------------------------------------------ */

    private static void CheckSameSequence(
        string label, List<EraCandidate> actual, List<EraCandidate> expected)
    {
        var ok = actual.Count == expected.Count;
        var detail = $"{actual.Count}/{expected.Count}";
        if (ok)
        {
            for (var i = 0; i < actual.Count; i++)
            {
                if (actual[i].TrackIdHex != expected[i].TrackIdHex)
                {
                    ok = false;
                    detail = $"#{i} track {actual[i].TrackIdHex}/{expected[i].TrackIdHex}";
                    break;
                }
                if (!Close(actual[i].Score, expected[i].Score))
                {
                    ok = false;
                    detail = $"#{i} score {actual[i].Score:R}/{expected[i].Score:R}";
                    break;
                }
            }
        }
        Program.Check(label, ok, detail);
    }

    /// <summary>只比召回集合（忽略分数）：用于「降级召回 = AND」这类分数必然不同的断言。</summary>
    private static void CheckSameTrackSet(
        string label, List<EraCandidate> actual, List<EraCandidate> expected)
    {
        var actualTracks = new HashSet<string>(
            actual.Select(c => c.TrackIdHex), StringComparer.Ordinal);
        var expectedTracks = new HashSet<string>(
            expected.Select(c => c.TrackIdHex), StringComparer.Ordinal);
        Program.Check(label, actualTracks.SetEquals(expectedTracks),
            string.Join(',', actualTracks));
    }

    private static void CheckSameScoreSet(
        string label, List<EraCandidate> actual, List<EraCandidate> expected)
    {        var actualByTrack = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in actual) actualByTrack[candidate.TrackIdHex] = candidate.Score;
        var expectedByTrack = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in expected) expectedByTrack[candidate.TrackIdHex] = candidate.Score;

        var ok = actualByTrack.Count == expectedByTrack.Count;
        var detail = $"{actualByTrack.Count}/{expectedByTrack.Count}";
        if (ok)
        {
            foreach (var (track, score) in expectedByTrack)
            {
                if (!actualByTrack.TryGetValue(track, out var hit))
                {
                    ok = false;
                    detail = $"missing {track}";
                    break;
                }
                if (!Close(hit, score))
                {
                    ok = false;
                    detail = $"{track} {hit:R}/{score:R}";
                    break;
                }
            }
        }
        Program.Check(label, ok, detail);
    }

    private static bool Close(double a, double b)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-9 * scale;
    }

    private static bool SameSequence(IEnumerable<string> actual, IReadOnlyList<string> expected)
    {
        var i = 0;
        foreach (var item in actual)
        {
            if (i >= expected.Count || item != expected[i]) return false;
            i++;
        }
        return i == expected.Count;
    }

    private static bool ContainsTrack(List<EraCandidate> candidates, string trackHex) =>
        candidates.Any(c => c.TrackIdHex == trackHex);

    private static double? ScoreOf(List<EraCandidate> candidates, string trackHex)
    {
        foreach (var candidate in candidates)
            if (candidate.TrackIdHex == trackHex) return candidate.Score;
        return null;
    }

    /* ------------------------------------------------------------------ */
    /* 语料                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 24 条确定性语料：覆盖多字段同词、重复词频、6 个 moon* 词条、相邻 / 不相邻 / 逆序 /
    /// 重复词短语、末词前缀、title/artist 交叉作用域。所有 doc 都是活行。
    /// </summary>
    private static List<AnalyzedDoc> Corpus()
    {
        var titles = new[]
        {
            new[] { "alpha", "beta" },
            new[] { "alpha", "alpha", "gamma" },
            new[] { "beta", "gamma" },
            new[] { "alpha", "night", "sky" },
            new[] { "night", "glow", "sky" },
            new[] { "rain", "falls", "down" },
            new[] { "rain", "down", "falls" },
            new[] { "echo", "echo" },
            new[] { "echo", "alpha", "echo" },
            new[] { "moonstone", "beta" },
            new[] { "moonlight", "gamma" },
            new[] { "moonshine", "alpha" },
            new[] { "moonbeam" },
            new[] { "moonrise", "beta" },
            new[] { "night", "star" },
            new[] { "night", "stone" },
            new[] { "alpha", "alpha", "alpha" },
            new[] { "gamma", "delta" },
            new[] { "moon", "alpha" },
            new[] { "delta", "echo" },
            new[] { "alpha", "moonstone" },
            new[] { "beta", "gamma", "alpha" },
            new[] { "night", "sky", "sky" },
            new[] { "rain", "falls", "down", "quiet" },
        };

        var artists = new[]
        {
            new[] { "alpha" }, new[] { "beta", "alpha" }, new[] { "alpha", "gamma" },
            new[] { "moonlight" }, new[] { "night", "sky", "moonlight" }, new[] { "gamma" },
            new[] { "alpha", "beta" }, new[] { "beta" }, new[] { "moonstone", "alpha" },
            new[] { "alpha", "beta" }, new[] { "alpha" }, new[] { "beta" },
            new[] { "alpha" }, new[] { "gamma" }, new[] { "alpha", "moonlight" },
            new[] { "beta" }, new[] { "alpha" }, new[] { "alpha", "gamma" },
            new[] { "gamma" }, new[] { "delta" }, new[] { "moonbeam" },
            Array.Empty<string>(), new[] { "alpha" }, new[] { "beta", "alpha" },
        };

        var lyrics = new[]
        {
            new[] { "gamma", "delta" }, new[] { "alpha", "gamma" }, new[] { "beta" },
            new[] { "rain", "falls", "down" }, new[] { "night", "sky" },
            new[] { "alpha", "alpha" }, new[] { "gamma" }, new[] { "alpha" },
            new[] { "beta", "beta" }, new[] { "gamma", "delta" },
            new[] { "alpha", "alpha", "alpha" }, new[] { "beta" }, new[] { "gamma" },
            new[] { "alpha" }, new[] { "beta", "beta" }, new[] { "alpha", "beta" },
            new[] { "gamma", "gamma" }, new[] { "alpha" }, new[] { "beta" },
            new[] { "echo", "echo" }, new[] { "gamma" }, new[] { "alpha", "beta" },
            new[] { "sky" }, new[] { "rain", "down" },
        };

        var docs = new List<AnalyzedDoc>(titles.Length);
        for (var i = 0; i < titles.Length; i++)
        {
            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = titles[i],
                [FieldId.Artist] = artists[i],
                [FieldId.Lyrics] = lyrics[i],
            };
            if (i == 0) tokens[FieldId.Album] = new[] { "alpha" };
            if (i == 4) tokens[FieldId.Album] = new[] { "night", "sky" };
            if (i == 5 || i == 6 || i == 20) tokens[FieldId.Filename] = new[] { "alpha" };
            docs.Add(SimpleDoc(i, tokens));
        }
        return docs;
    }

    private static AnalyzedDoc SimpleDoc(
        int index, params (FieldId Field, string[] Tokens)[] fields)
    {
        var tokens = new Dictionary<FieldId, string[]>();
        foreach (var (field, values) in fields) tokens[field] = values;
        return SimpleDoc(index, tokens);
    }

    private static AnalyzedDoc SimpleDoc(int index, IReadOnlyDictionary<FieldId, string[]> tokens) =>
        new(TrackBytes(index), RevBytes(index), (ushort)(index & 0x7), (byte)(index & 0x03), tokens,
            Text(tokens, FieldId.Title), Text(tokens, FieldId.Artist), Text(tokens, FieldId.Album));

    private static string Text(IReadOnlyDictionary<FieldId, string[]> tokens, FieldId field) =>
        tokens.TryGetValue(field, out var raw) ? string.Join(' ', raw) : string.Empty;

    private static bool HasField(
        AnalyzedDoc doc, FieldId field, string term, bool prefix)
    {
        if (!doc.Tokens.TryGetValue(field, out var tokens)) return false;
        foreach (var token in tokens)
        {
            if (prefix
                    ? token.StartsWith(term, StringComparison.Ordinal)
                    : string.Equals(token, term, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static int CountField(
        AnalyzedDoc doc, FieldId field, string term, bool prefix)
    {
        if (!doc.Tokens.TryGetValue(field, out var tokens)) return 0;
        var count = 0;
        foreach (var token in tokens)
        {
            if (prefix
                    ? token.StartsWith(term, StringComparison.Ordinal)
                    : string.Equals(token, term, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static byte[] TrackBytes(int index)
    {
        var track = new byte[16];
        // 前 4B 小端存 index：测试语料最多 16k+ 条，旧 pattern 每 256 条会撞尾字节。
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(track, index);
        for (var i = 4; i < 16; i++) track[i] = (byte)(index * 13 + i);
        return track;
    }

    private static byte[] RevBytes(int index)
    {
        var rev = new byte[16];
        for (var i = 0; i < 16; i++) rev[i] = (byte)(index * 29 + i * 3);
        return rev;
    }

    private static string Track(AnalyzedDoc doc) => Convert.ToHexString(doc.Track).ToLowerInvariant();

    private static BuildOptions Options(int docsPerSegment, HashSet<FieldId>? noPositions = null) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x80 + i)).ToArray(),
        CreatedMs = 1_760_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
        NoPositionFields = noPositions ?? new HashSet<FieldId>(),
    };

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-bitset-" + Guid.NewGuid().ToString("N"));
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
