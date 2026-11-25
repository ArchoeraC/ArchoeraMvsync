// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// EraStore 查询计划执行器（S3 / T3.0）：解析 <see cref="Step.Expression"/>（FTS5 MATCH 子集）
/// 成 AST，映射到 EraStore 迭代器树跨段召回，并在 C# 侧用 <see cref="EraBm25"/> 复算分数。
///
/// <para>查询语义与 FTS5 <c>bm25()</c> 严格对齐：列作用域只影响短语的 df 与命中列，
/// 行长度 <c>L</c> 取整行 12 列 token 总数（实测标定见 <see cref="EraBm25"/>）。</para>
/// </summary>
internal static class EraQueryPlan
{
    private static readonly FieldId[] AllFields =
        Enumerable.Range(1, 12).Select(i => (FieldId)i).ToArray();

    /// <summary>单个前缀最多展开的词条数（与原型一致）。</summary>
    private const int MaxPrefixExpansions = 10_000;

    /// <summary>top-k 堆的最大容量；超过则视为「不截断」，走普通 List（防御超大 offset）。</summary>
    private const int MaxTopK = 16_384;

    /// <summary>FTS 列名 → <see cref="FieldId"/>（FTS 声明顺序与 FieldId 编号不同）。</summary>
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

    /// <summary>
    /// 执行一条 MATCH 表达式，返回每段内的候选（含 bm25 分数）。
    /// 调用方负责按 trackId 跨段去重，再交给 <see cref="SearchRanker"/> 做桶序排序。
    /// </summary>
    public static List<EraCandidate> Execute(
        StoreReader reader, string expression, int perStepTake,
        IReadOnlyList<string[]>? readingSequences = null,
        LyricsConfidencePolicy lyricsPolicy = LyricsConfidencePolicy.None)
        => ExecuteDetailed(reader, expression, perStepTake, readingSequences, lyricsPolicy).Candidates;

    /// <summary>
    /// 同 <see cref="Execute"/>，另回传**未截断**的命中数（T3.11 的精确度成本护栏）：
    /// 精确度复核只在单步命中 ≤ <see cref="SearchExactness.Cap"/> 时执行，该判定不能依赖
    /// 请求的 <c>take</c>（翻页时 take=-1、首页 take=limit+offset），否则同一查询的不同
    /// 页会对同一步给出不同的精确度结论，破坏 keyset 分页的一致性。
    /// </summary>
    public static EraQueryHits ExecuteDetailed(
        StoreReader reader, string expression, int perStepTake,
        IReadOnlyList<string[]>? readingSequences = null,
        LyricsConfidencePolicy lyricsPolicy = LyricsConfidencePolicy.None)
    {
        FtsNode root;
        try
        {
            root = FtsExpression.Parse(expression);
        }
        catch (FormatException ex)
        {
            // 表达式由 SearchQuery 生成，语法错误说明两侧实现失配；返回空而不是让整个查询失败。
            Log.LogWarn("indexer", $"EraStore 表达式解析失败: {ex.Message}");
            return new EraQueryHits
            {
                Candidates = new List<EraCandidate>(),
                TotalHits = 0,
                AnyPhraseDowngraded = false,
            };
        }

        var atoms = new List<Atom>();
        var scoreTree = BuildScoreTree(root, AllFields, atoms);
        if (atoms.Count == 0)
        {
            return new EraQueryHits
            {
                Candidates = new List<EraCandidate>(),
                TotalHits = 0,
                AnyPhraseDowngraded = false,
            };
        }

        var segments = reader.OpenPerSegment();
        var n = reader.TotalDocs;
        var avgDls = reader.AverageDocLengths().ToArray();
        var avgRowLen = 0.0;
        foreach (var avg in avgDls) avgRowLen += avg;

        // 每个 atom 先按 bitset 建好文档集合：df = popcount（一次扫描同时得到 df 与候选），
        // 布尔树用位运算求值；之后只对结果位图里的 doc 打分。
        // df 缓存挂在 StoreReader 上（键 = 完整表达式），而 reader 随提交世代重开
        // （SearchSession 比对 CURRENT + gen），因此增量写入 / 墓碑变化不会命中旧缓存。
        var haveDf = reader.TryGetDf(expression, lyricsPolicy, atoms.Count, out var cachedDfs);
        var dfs = haveDf ? cachedDfs : new int[atoms.Count];
        var resultBits = new ulong[segments.Count][];
        var tfBySegment = new double[]?[segments.Count][];
        var totalHits = 0;
        // 短语降级事实（T2.5 / positioned）：任一多词 atom 落在**无位置**字段上，
        // PhraseIter 就不校验相邻、按纯交集命中。这里在扫倒排时一并记录。
        var phraseDowngraded = false;
        for (var si = 0; si < segments.Count; si++)
        {
            var seg = segments[si];
            var docCount = seg.Docs.Count;
            var wordCount = Math.Max(1, (docCount + 63) >> 6);
            var atomBits = new ulong[atoms.Count][];
            var atomTf = new double[]?[atoms.Count];
            for (var i = 0; i < atoms.Count; i++)
            {
                // 单词 atom 在扫倒排时顺手累计加权 tf；词组仍由评分器按位置计算。
                var tf = atoms[i].Tokens.Count == 1 ? new double[docCount] : null;
                atomBits[i] = BuildAtomBits(
                    seg, atoms[i], docCount, tf, ref phraseDowngraded, lyricsPolicy, reader);
                atomTf[i] = tf;
            }
            if (!haveDf)
            {
                for (var i = 0; i < atoms.Count; i++)
                    dfs[i] += PopCountBits(atomBits[i]);
            }
            resultBits[si] = EvalBits(scoreTree, atomBits, wordCount);
            tfBySegment[si] = atomTf;
            // 未截断命中数：位图只含活行（倒排 / 短语路径都跳过墓碑），popcount 即命中数。
            totalHits += PopCountBits(resultBits[si]);
        }
        if (!haveDf) reader.PutDf(expression, lyricsPolicy, dfs);

        // 先只收集 (段, doc, score)；TrackId 十六进制串在截断后才生成——宽命中集下
        // 能省掉被截掉那部分的 32-hex 字符串分配（T3.14）。
        // 固定大小 top-k（每步上限 = limit + offset）：宽命中不再物化 2.9 万条再全排序，
        // 堆内只保留最终上限，顺带消掉最大的 LOH 分配与排序抖动（T3.14）。
        var useTopK = perStepTake > 0 && perStepTake <= MaxTopK;
        var heap = useTopK ? System.Buffers.ArrayPool<ScoredHit>.Shared.Rent(perStepTake) : null;
        var kept = 0;
        var all = useTopK ? null : new List<ScoredHit>();
        var heapBuilt = false;
        var truncated = false;

        void Offer(ScoredHit hit)
        {
            if (!useTopK)
            {
                all!.Add(hit);
                return;
            }
            if (kept < perStepTake)
            {
                // 先按扫描顺序填满；堆推迟到第一次真正截断时才建，保证 hits ≤ k 时
                // 输出顺序与旧扫描实现完全一致（建堆会原地打乱数组）。
                heap![kept++] = hit;
            }
            else
            {
                if (!heapBuilt)
                {
                    BuildHeap(heap!, kept);
                    heapBuilt = true;
                }
                // 已有 k 条后仍有命中：旧实现会按分数全排序再截断，输出顺序也必须一致。
                truncated = true;
                if (CompareHits(in hit, in heap![0]) < 0)
                {
                    heap[0] = hit;
                    SiftDown(heap, kept);
                }
            }
        }

        for (var si = 0; si < segments.Count; si++)
        {
            var seg = segments[si];
            var bits = resultBits[si];
            if (!bits.Any(b => b != 0)) continue;

            var scorer = new SegmentScorer(seg, atoms, dfs, n, tfBySegment[si]);
            for (var wi = 0; wi < bits.Length; wi++)
            {
                var word = bits[wi];
                while (word != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    var docId = (wi << 6) + bit;
                    if (docId >= seg.Docs.Count || !seg.IsLive(docId)) continue;
                    Offer(new ScoredHit
                    {
                        DocId = docId,
                        SegmentId = seg.SegmentId,
                        SegmentIndex = si,
                        Score = scorer.Score(docId, atoms, scoreTree, avgRowLen),
                    });
                }
            }
        }

        // 截断后堆内是无序的：按与比较键相同的顺序重排，保证输出是确定性的 top-k
        // （未截断时保持扫描顺序，与旧实现一致）。
        if (useTopK && truncated) Array.Sort(heap!, 0, kept, ScoredHitOrder.Instance);

        try
        {
            var count = useTopK ? kept : all!.Count;
            var matches = new List<EraCandidate>(count);
            for (var index = 0; index < count; index++)
            {
                var hit = useTopK ? heap![index] : all![index];
                var seg = segments[hit.SegmentIndex];
                var trackId = seg.Docs.TrackId(hit.DocId);
                if (trackId.Length == 0) continue;
                matches.Add(new EraCandidate
                {
                    DocId = hit.DocId,
                    SegmentId = hit.SegmentId,
                    TrackIdHex = trackId,
                    Score = hit.Score,
                });
            }

        // 读音趟分档：FTS 路径读 search_docs.reading_tok 原文比较前缀 / 包含；
        // 该串与 ReadingMeta 字段的 token 序列同源，可直接用位置块复算，无需回读 SQLite。
            if (readingSequences is { Count: > 0 } && matches.Count > 0)
            {
                var bySegment = new Dictionary<ushort, SegmentReaders>();
                foreach (var seg in segments) bySegment[seg.SegmentId] = seg;
                for (var i = 0; i < matches.Count; i++)
                {
                    var candidate = matches[i];
                    matches[i] = new EraCandidate
                    {
                        DocId = candidate.DocId,
                        SegmentId = candidate.SegmentId,
                        TrackIdHex = candidate.TrackIdHex,
                        Score = candidate.Score,
                        Tier = ReadingTier(bySegment[candidate.SegmentId], readingSequences, candidate.DocId),
                    };
                }
            }

            return new EraQueryHits
            {
                Candidates = matches,
                TotalHits = totalHits,
                AnyPhraseDowngraded = phraseDowngraded,
            };
        }
        finally
        {
            if (heap is not null) System.Buffers.ArrayPool<ScoredHit>.Shared.Return(heap);
        }
    }

    /// <summary>top-k 比较键：分数 → 段 → doc（越小越好）。</summary>
    private static int CompareHits(in ScoredHit a, in ScoredHit b)
    {
        var byScore = a.Score.CompareTo(b.Score);
        if (byScore != 0) return byScore;
        var bySeg = a.SegmentIndex.CompareTo(b.SegmentIndex);
        return bySeg != 0 ? bySeg : a.DocId.CompareTo(b.DocId);
    }

    /// <summary>把截断后的堆按 <see cref="CompareHits"/> 升序重排（确定性的 top-k 输出）。</summary>
    private sealed class ScoredHitOrder : IComparer<ScoredHit>
    {
        public static readonly ScoredHitOrder Instance = new();

        public int Compare(ScoredHit a, ScoredHit b) => CompareHits(in a, in b);
    }

    /// <summary>最大堆：父节点是当前 top-k 里最差的一条。</summary>
    private static void BuildHeap(ScoredHit[] heap, int count)
    {
        for (var i = (count >> 1) - 1; i >= 0; i--) SiftDownFrom(heap, count, i);
    }

    private static void SiftDown(ScoredHit[] heap, int count) => SiftDownFrom(heap, count, 0);

    private static void SiftDownFrom(ScoredHit[] heap, int count, int at)
    {
        while (true)
        {
            var left = (at << 1) + 1;
            if (left >= count) return;
            var largest = left;
            var right = left + 1;
            if (right < count && CompareHits(in heap[right], in heap[left]) > 0) largest = right;
            if (CompareHits(in heap[at], in heap[largest]) >= 0) return;
            (heap[at], heap[largest]) = (heap[largest], heap[at]);
            at = largest;
        }
    }


    /// <summary>
    /// 读音串分档（与 <see cref="ReadingRanker.Score"/> 同口径）：查询序列在读音串开头 = 前缀档，
    /// 中间出现 = 包含档，未出现（或该文档无读音）= 兜底档。
    /// </summary>
    private static int ReadingTier(
        SegmentReaders seg, IReadOnlyList<string[]> sequences, int docId)
    {
        var tier = ReadingRanker.OtherTier;
        foreach (var sequence in sequences)
        {
            if (sequence.Length == 0) continue;
            var starts = PhraseStarts(seg, FieldId.ReadingMeta, sequence, docId);
            if (starts.Length == 0) continue;
            if (Array.BinarySearch(starts, 0) >= 0) return ReadingRanker.PrefixTier;
            tier = ReadingRanker.ContainsTier;
        }
        return tier;
    }

    /// <summary>短语在该字段该文档内的全部起始位置（位置升序）。</summary>
    private static int[] PhraseStarts(
        SegmentReaders seg, FieldId field, IReadOnlyList<string> tokens, int docId)
    {
        if (tokens.Count == 1) return TermPositions(seg, field, tokens[0], docId);

        var positions = new int[tokens.Count][];
        for (var i = 0; i < tokens.Count; i++)
        {
            positions[i] = TermPositions(seg, field, tokens[i], docId);
            if (positions[i].Length == 0) return Array.Empty<int>();
        }

        var starts = new List<int>();
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
            if (matched) starts.Add(start);
        }
        return starts.ToArray();
    }

    /* ------------------------------------------------------------------ */
    /* 表达式 → 迭代器                                                     */
    /* ------------------------------------------------------------------ */

    /// <summary>AST 展开后的评分单元：一个短语 + 生效列集合（嵌套作用域已取交集）。</summary>
    private sealed record Atom(IReadOnlyList<string> Tokens, bool PrefixLast, FieldId[] Fields);

    /// <summary>
    /// 评分用布尔树（叶子 = <see cref="Atom"/> 下标）。
    ///
    /// <para>FTS5 的 bm25 只累加**参与本次命中**的短语：若某个 AND 分支未被满足，
    /// 即使其中个别短语在该行有出现，也不计入分数（实测 <c>(a AND b) OR c</c> 的行只含
    /// <c>a</c>、<c>c</c> 时只算 <c>c</c>；多个 OR 分支同时命中则全部累加）。</para>
    /// </summary>
    private abstract record ScoreNode;

    private sealed record ScoreLeaf(int AtomIndex) : ScoreNode;

    private sealed record ScoreAnd(IReadOnlyList<ScoreNode> Children) : ScoreNode;

    private sealed record ScoreOr(IReadOnlyList<ScoreNode> Children) : ScoreNode;

    private static ScoreNode BuildScoreTree(FtsNode node, FieldId[] scope, List<Atom> atoms)
    {
        switch (node)
        {
            case FtsScope scopeNode:
                return BuildScoreTree(scopeNode.Child, Intersect(scope, scopeNode.Columns), atoms);
            case FtsAnd and:
                return new ScoreAnd(
                    and.Children.Select(child => BuildScoreTree(child, scope, atoms)).ToList());
            case FtsOr or:
                return new ScoreOr(
                    or.Children.Select(child => BuildScoreTree(child, scope, atoms)).ToList());
            case FtsTerm term:
                atoms.Add(new Atom(new[] { term.Token }, term.Prefix, scope));
                return new ScoreLeaf(atoms.Count - 1);
            case FtsPhrase phrase:
                atoms.Add(new Atom(phrase.Tokens, phrase.PrefixLast, scope));
                return new ScoreLeaf(atoms.Count - 1);
            default:
                throw new InvalidOperationException($"未知 AST 节点 {node?.GetType().Name}");
        }
    }

    /// <summary>
    /// 单 atom 的段内文档集合（bitset）；df = popcount，无需再扫一遍倒排。
    ///
    /// <para>T4.2：<paramref name="policy"/> 非 <see cref="LyricsConfidencePolicy.None"/> 时，
    /// 三个字面歌词列按段级置信度位图**逐字段**取交集（同一 atom 可能同时扫元数据与歌词列，
    /// 不能先并集再统一过滤）。位图按段缓存在 <see cref="StoreReader"/> 上，每查询只建一次。</para>
    /// </summary>
    private static ulong[] BuildAtomBits(
        SegmentReaders seg, Atom atom, int docCount, double[]? weightedTf, ref bool phraseDowngraded,
        LyricsConfidencePolicy policy, StoreReader reader)
    {
        var bits = new ulong[Math.Max(1, (docCount + 63) >> 6)];
        if (atom.Fields.Length == 0) return bits;

        var mask = policy == LyricsConfidencePolicy.None ? null : reader.GetLyricsMask(seg);

        if (atom.Tokens.Count == 1)
        {
            foreach (var field in atom.Fields)
            {
                var weight = EraBm25.Weights[(int)field - 1];
                var fieldMask = mask?.For(field, policy);
                if (atom.PrefixLast)
                {
                    foreach (var info in seg.Dict.ScanPrefix(field, atom.Tokens[0], MaxPrefixExpansions))
                        OrPostings(seg, info, bits, weightedTf, weight, fieldMask);
                }
                else if (seg.Dict.TryFind(field, atom.Tokens[0], out var hit))
                {
                    OrPostings(seg, hit, bits, weightedTf, weight, fieldMask);
                }
            }
            return bits;
        }

        // 词组：该字段没有位置块时 PhraseIter 会降级为纯交集（EraStore/Iterators/PhraseIter）。
        // 事实来源是词典 flags（同一词条在字段内位置要么全有要么全无），无需逐个 doc 探测。
        foreach (var field in atom.Fields)
        {
            if (seg.Dict.TryFind(field, atom.Tokens[0], out var probe) && !probe.HasPositions)
            {
                phraseDowngraded = true;
                break;
            }
        }

        // 词组：位置校验逻辑复用 PhraseIter（字段并集）。有置信度门控时必须按字段分别迭代，
        // 否则无法区分「这个 doc 的命中来自哪个歌词列」。
        if (mask is null)
        {
            using var iter = BuildAtomIter(seg, atom);
            if (!iter.Advance(0)) return bits;
            do
            {
                var doc = iter.Current;
                if (seg.IsLive(doc)) bits[doc >> 6] |= 1UL << (doc & 63);
            }
            while (iter.Next());
            return bits;
        }

        foreach (var field in atom.Fields)
        {
            var fieldMask = mask.For(field, policy);
            using var iter = BuildFieldIter(seg, field, atom);
            if (!iter.Advance(0)) continue;
            do
            {
                var doc = iter.Current;
                if (!seg.IsLive(doc)) continue;
                if (fieldMask is not null && (fieldMask[doc >> 6] & (1UL << (doc & 63))) == 0)
                    continue;
                bits[doc >> 6] |= 1UL << (doc & 63);
            }
            while (iter.Next());
        }
        return bits;
    }

    private static int PopCountBits(ulong[] bits)
    {
        var count = 0;
        foreach (var word in bits) count += BitOperations.PopCount(word);
        return count;
    }

    private static void OrPostings(
        SegmentReaders seg, TermInfo info, ulong[] bits, double[]? weightedTf, double weight,
        ulong[]? mask = null)
    {
        var cursor = seg.Postings.Open(info.PostOffset, info.Df);
        while (cursor.Next())
        {
            var doc = cursor.CurrentDoc;
            if (!seg.IsLive(doc)) continue;
            if (mask is not null && (mask[doc >> 6] & (1UL << (doc & 63))) == 0) continue;
            bits[doc >> 6] |= 1UL << (doc & 63);
            if (weightedTf is not null) weightedTf[doc] += cursor.CurrentTf * weight;
        }
    }

    /// <summary>布尔树按位求值：OR / AND / 叶子。</summary>
    private static ulong[] EvalBits(ScoreNode node, ulong[][] atomBits, int wordCount)
    {
        switch (node)
        {
            case ScoreLeaf leaf:
                return atomBits[leaf.AtomIndex];
            case ScoreOr or:
            {
                var acc = new ulong[wordCount];
                foreach (var child in or.Children)
                {
                    var childBits = EvalBits(child, atomBits, wordCount);
                    for (var i = 0; i < wordCount; i++) acc[i] |= childBits[i];
                }
                return acc;
            }
            case ScoreAnd and:
            {
                var acc = new ulong[wordCount];
                Array.Fill(acc, ulong.MaxValue);
                foreach (var child in and.Children)
                {
                    var childBits = EvalBits(child, atomBits, wordCount);
                    for (var i = 0; i < wordCount; i++) acc[i] &= childBits[i];
                }
                return acc;
            }
            default:
                return new ulong[wordCount];
        }
    }

    private static IDocIter BuildNode(FtsNode node, FieldId[] scope, SegmentReaders seg) => node switch
    {
        FtsScope scopeNode => BuildNode(
            scopeNode.Child, Intersect(scope, scopeNode.Columns), seg),
        FtsAnd and => new AndIter(
            and.Children.Select(child => BuildNode(child, scope, seg)).ToList()),
        FtsOr or => new OrIter(
            or.Children.Select(child => BuildNode(child, scope, seg)).ToList()),
        FtsTerm term => BuildAtomIter(seg, new Atom(new[] { term.Token }, term.Prefix, scope)),
        FtsPhrase phrase => BuildAtomIter(seg, new Atom(phrase.Tokens, phrase.PrefixLast, scope)),
        _ => throw new InvalidOperationException($"未知 AST 节点 {node?.GetType().Name}"),
    };

    private static IDocIter BuildAtomIter(SegmentReaders seg, Atom atom)
    {
        if (atom.Fields.Length == 0) return new OrIter(Array.Empty<IDocIter>());
        var iters = new List<IDocIter>(atom.Fields.Length);
        foreach (var field in atom.Fields) iters.Add(BuildFieldIter(seg, field, atom));
        return iters.Count == 1 ? iters[0] : new OrIter(iters);
    }

    /// <summary>单字段迭代器：单词 = Term / Prefix；多词 = 位置短语（末词可前缀）。</summary>
    private static IDocIter BuildFieldIter(SegmentReaders seg, FieldId field, Atom atom)
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
        {
            if (ColumnFields.TryGetValue(column, out var field)) allowed.Add(field);
        }

        var result = new List<FieldId>(scope.Length);
        foreach (var field in scope)
        {
            if (allowed.Contains(field)) result.Add(field);
        }
        return result.ToArray();
    }

    /* ------------------------------------------------------------------ */
    /* bm25 支持：文档频率 / 词频 / 行长度                                  */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 段内评分器：为每个 atom × field 预建倒排游标，候选 doc 按升序推进时直接
    /// <see cref="PostingsCursor.Advance"/>。旧实现对每个候选重新二分词典 / 扫前缀，
    /// 宽命中查询（hello ≈5k 行）会退化成 200ms 级；这里把查找成本压到每段一次。
    /// </summary>
    private sealed class SegmentScorer
    {
        private readonly SegmentReaders _seg;
        private readonly AtomPlan[] _plans;
        private readonly double[] _tf;
        private readonly double[] _idf;
        private readonly bool[] _computed;

        private readonly double[]?[]? _precomputedTf;

        public SegmentScorer(
            SegmentReaders seg, List<Atom> atoms, int[] dfs, uint n, double[]?[]? precomputedTf = null)
        {
            _precomputedTf = precomputedTf;
            _seg = seg;
            _plans = new AtomPlan[atoms.Count];
            _tf = new double[atoms.Count];
            _idf = new double[atoms.Count];
            for (var i = 0; i < atoms.Count; i++)
            {
                // 已有预计算 tf 的 atom（单词）不再建游标；词组才需要位置游标。
                if (precomputedTf is null || precomputedTf[i] is null)
                    _plans[i] = BuildPlan(seg, atoms[i]);
                // df / n 在一次 Execute 内是常量：逐 doc 调 Math.Log 是宽命中集的明显浪费。
                _idf[i] = EraBm25.Idf(dfs[i], n);
            }
            _computed = new bool[atoms.Count];
        }

        public double Score(
            int docId, List<Atom> atoms, ScoreNode scoreTree, double avgRowLen)
        {
            var lens = _seg.Docs.Lengths(docId);
            var rowLen = 0;
            foreach (var len in lens) rowLen += len;
            if (avgRowLen <= 0) return 0;

            // 逐 doc 复用预分配缓冲：宽命中（上万行）下不能每次 new 数组 / List。
            Array.Clear(_computed);
            double total = 0;
            AddContributions(scoreTree, docId, lens, rowLen, avgRowLen, ref total);
            return -total;
        }

        /// <summary>按需计算某 atom 在该 doc 的加权 tf（同一 doc 内只算一次）。</summary>
        private void EnsureTf(int index, int docId, ReadOnlySpan<ushort> lens)
        {
            if (_computed[index]) return;
            _computed[index] = true;
            if (_precomputedTf is { } pre && pre[index] is { } values)
            {
                _tf[index] = values[docId];
                return;
            }
            if (_idf[index] == 0) return;

            var plan = _plans[index];
            var weightedTf = 0.0;
            foreach (var field in plan.Fields)
            {
                // 该字段根本没有该词条（或该 doc 该字段为空）→ 不可能命中，免去游标推进。
                if (!field.HasCursors || lens[(int)field.Field - 1] == 0) continue;
                var tf = Tf(plan, field, docId);
                if (tf <= 0) continue;
                weightedTf += tf * EraBm25.Weights[(int)field.Field - 1];
            }
            _tf[index] = weightedTf;
        }

        /// <summary>节点在该 doc 是否有命中（AND 短路：第一个缺席的子节点之后不再算）。</summary>
        private bool Present(ScoreNode node, int docId, ReadOnlySpan<ushort> lens)
        {
            switch (node)
            {
                case ScoreLeaf leaf:
                    EnsureTf(leaf.AtomIndex, docId, lens);
                    return _tf[leaf.AtomIndex] > 0;
                case ScoreOr or:
                    foreach (var child in or.Children)
                        if (Present(child, docId, lens)) return true;
                    return false;
                case ScoreAnd and:
                    foreach (var child in and.Children)
                        if (!Present(child, docId, lens)) return false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 累加参与计分的叶子。AND 先做存在性判定：不满足的分支整支不计分，
        /// 且只算到第一个缺席子节点（宽命中集里大量文档只命中外层 OR 的另一支）。
        /// </summary>
        private void AddContributions(
            ScoreNode node, int docId, ReadOnlySpan<ushort> lens, int rowLen,
            double avgRowLen, ref double total)
        {
            switch (node)
            {
                case ScoreLeaf leaf:
                    EnsureTf(leaf.AtomIndex, docId, lens);
                    var tf = _tf[leaf.AtomIndex];
                    if (tf <= 0) return;
                    total += _idf[leaf.AtomIndex] * tf * (EraBm25.K1 + 1)
                        / (tf + EraBm25.K1 * (1 - EraBm25.B + EraBm25.B * rowLen / avgRowLen));
                    return;
                case ScoreOr or:
                    foreach (var child in or.Children)
                        AddContributions(child, docId, lens, rowLen, avgRowLen, ref total);
                    return;
                case ScoreAnd and:
                    foreach (var child in and.Children)
                        if (!Present(child, docId, lens)) return;
                    foreach (var child in and.Children)
                        AddContributions(child, docId, lens, rowLen, avgRowLen, ref total);
                    return;
            }
        }

        private sealed class AtomPlan
        {
            public Atom Atom = null!;
            public List<FieldPlan> Fields = new();
        }

        private sealed class FieldPlan
        {
            public FieldId Field;
            public List<TokenCursors> Tokens = new();

            /// <summary>该字段是否存在至少一个词条游标（否则打分时整列跳过）。</summary>
            public bool HasCursors;
        }

        private sealed class TokenCursors
        {
            public List<PostingsCursor> Cursors = new();

            /// <summary>游标较多时（前缀展开）改用按 doc 归并的 tf 游标，避免每 doc 线性扫全部展开。</summary>
            public MergedTf? Merged;
        }

        /// <summary>超过该游标数才启用归并（少量游标时线性扫更快）。</summary>
        private const int MergedTfThreshold = 4;

        /// <summary>
        /// 多个倒排游标的 tf 归并（前缀展开）：按 doc 升序维护小顶堆，查询某个 doc 时
        /// 只推进堆顶，把该 doc 上所有展开词条的 tf 求和。旧实现每 doc 线性扫全部
        /// 游标（1 万展开 × 3 万 doc），是宽前缀查询的主要热点。
        /// </summary>
        private sealed class MergedTf
        {
            private readonly List<PostingsCursor> _cursors;
            private readonly int[] _heap;
            private int _count;
            private bool _started;

            public MergedTf(List<PostingsCursor> cursors)
            {
                _cursors = cursors;
                _heap = new int[cursors.Count];
            }

            public int TfAt(int docId)
            {
                if (!_started)
                {
                    _started = true;
                    for (var i = 0; i < _cursors.Count; i++)
                    {
                        if (_cursors[i].Advance(0)) _heap[_count++] = i;
                    }
                    for (var i = (_count >> 1) - 1; i >= 0; i--) SiftDown(i);
                }

                while (_count > 0)
                {
                    var top = _heap[0];
                    if (_cursors[top].CurrentDoc >= docId) break;
                    if (_cursors[top].Advance(docId)) SiftDown(0);
                    else RemoveRoot();
                }

                var sum = 0;
                while (_count > 0 && _cursors[_heap[0]].CurrentDoc == docId)
                {
                    var index = RemoveRoot();
                    var cursor = _cursors[index];
                    sum += cursor.CurrentTf;
                    if (cursor.Next()) Push(index);
                }
                return sum;
            }

            private void Push(int index)
            {
                _heap[_count] = index;
                _count++;
                SiftUp(_count - 1);
            }

            private int RemoveRoot()
            {
                var root = _heap[0];
                _heap[0] = _heap[--_count];
                if (_count > 0) SiftDown(0);
                return root;
            }

            private void SiftUp(int at)
            {
                while (at > 0)
                {
                    var parent = (at - 1) >> 1;
                    if (Compare(_heap[parent], _heap[at]) <= 0) return;
                    (_heap[parent], _heap[at]) = (_heap[at], _heap[parent]);
                    at = parent;
                }
            }

            private void SiftDown(int at)
            {
                while (true)
                {
                    var left = (at << 1) + 1;
                    if (left >= _count) return;
                    var smallest = left;
                    var right = left + 1;
                    if (right < _count && Compare(_heap[right], _heap[left]) < 0) smallest = right;
                    if (Compare(_heap[at], _heap[smallest]) <= 0) return;
                    (_heap[at], _heap[smallest]) = (_heap[smallest], _heap[at]);
                    at = smallest;
                }
            }

            private int Compare(int a, int b)
            {
                var ca = _cursors[a].CurrentDoc;
                var cb = _cursors[b].CurrentDoc;
                return ca != cb ? ca.CompareTo(cb) : a.CompareTo(b);
            }
        }

        private static AtomPlan BuildPlan(SegmentReaders seg, Atom atom)
        {
            var plan = new AtomPlan { Atom = atom };
            foreach (var field in atom.Fields)
            {
                var fieldPlan = new FieldPlan { Field = field };
                for (var i = 0; i < atom.Tokens.Count; i++)
                {
                    var group = new TokenCursors();
                    var last = i == atom.Tokens.Count - 1;
                    if (last && atom.PrefixLast)
                    {
                        foreach (var info in seg.Dict.ScanPrefix(
                                     field, atom.Tokens[i], MaxPrefixExpansions))
                            group.Cursors.Add(seg.Postings.Open(info.PostOffset, info.Df));
                    }
                    else if (seg.Dict.TryFind(field, atom.Tokens[i], out var hit))
                    {
                        group.Cursors.Add(seg.Postings.Open(hit.PostOffset, hit.Df));
                    }
                    fieldPlan.Tokens.Add(group);
                }
                foreach (var group in fieldPlan.Tokens)
                {
                    if (group.Cursors.Count > MergedTfThreshold)
                        group.Merged = new MergedTf(group.Cursors);
                }
                fieldPlan.HasCursors = fieldPlan.Tokens.Any(t => t.Cursors.Count > 0);
                plan.Fields.Add(fieldPlan);
            }
            return plan;
        }

        /// <summary>与旧 <c>TfInField</c> 等价：单词求和、词组按位置连续性计数。</summary>
        private static int Tf(AtomPlan plan, FieldPlan field, int docId)
        {
            var atom = plan.Atom;
            if (field.Tokens.Count == 0) return 0;

            if (atom.Tokens.Count == 1)
            {
                var group = field.Tokens[0];
                if (group.Merged is { } merged) return merged.TfAt(docId);

                var sum = 0;
                foreach (var cursor in group.Cursors)
                {
                    if (cursor.Advance(docId) && cursor.CurrentDoc == docId)
                        sum += cursor.CurrentTf;
                }
                return sum;
            }

            // 词组：首 token 的每个位置向后找 start+i；全程不分配 List。
            var count = 0;
            foreach (var cursor in field.Tokens[0].Cursors)
            {
                if (!cursor.Advance(docId) || cursor.CurrentDoc != docId) continue;
                foreach (var start in cursor.CurrentPositions)
                {
                    var matched = true;
                    for (var i = 1; i < field.Tokens.Count; i++)
                    {
                        if (!HasPosition(field.Tokens[i], docId, start + i))
                        {
                            matched = false;
                            break;
                        }
                    }
                    if (matched) count++;
                }
            }
            return count;
        }

        private static bool HasPosition(TokenCursors group, int docId, int target)
        {
            foreach (var cursor in group.Cursors)
            {
                if (!cursor.Advance(docId) || cursor.CurrentDoc != docId) continue;
                if (cursor.CurrentPositions.Contains(target)) return true;
            }
            return false;
        }
    }

    private static int[] TermPositions(SegmentReaders seg, FieldId field, string term, int docId)
    {
        if (!seg.Dict.TryFind(field, term, out var info)) return Array.Empty<int>();
        var cursor = seg.Postings.Open(info.PostOffset, info.Df);
        return cursor.Advance(docId) && cursor.CurrentDoc == docId
            ? cursor.CurrentPositions.ToArray()
            : Array.Empty<int>();
    }
}

/// <summary>EraStore 查询返回的候选集条目（跨段合并前的原始条目）。</summary>
/// <summary>截断前的轻量命中：只带段索引 / doc / 分数，不分配 TrackId 字符串。</summary>
internal readonly struct ScoredHit
{
    public int DocId { get; init; }
    public ushort SegmentId { get; init; }
    public int SegmentIndex { get; init; }
    public double Score { get; init; }
}

/// <summary>
/// 一步查询的结果：截断后的候选 + **未截断**的命中数（见
/// <see cref="EraQueryPlan.ExecuteDetailed"/>）。<see cref="TotalHits"/> 只用于
/// 「该步是否值得做精确度复核」的判定，不参与排序。
/// </summary>
internal readonly struct EraQueryHits
{
    public List<EraCandidate> Candidates { get; init; }

    /// <summary>该表达式在全库的活行命中数（与 <c>take</c> 无关）。</summary>
    public int TotalHits { get; init; }

    /// <summary>
    /// 本步是否有**短语 atom 被降级为纯交集**（T2.5 / positioned）。
    ///
    /// <para>只有「字段缺位置块」才会让 <see cref="Iterators.PhraseIter"/> 放弃相邻校验；
    /// 与候选打分无关，只用于 <c>explain.positioned</c>：false 表示该步的「短语」其实只是
    /// AND。无短语 atom 的步骤恒为 false（positioned 默认成立）。</para>
    /// </summary>
    public bool AnyPhraseDowngraded { get; init; }
}

internal struct EraCandidate
{
    /// <summary>显式无参构造：保证默认实例的 <see cref="TrackIdHex"/> 非 null。</summary>
    public EraCandidate() => TrackIdHex = "";

    /// <summary>段内 docId（0-based）。</summary>
    public int DocId { get; set; }

    /// <summary>track_id 十六进制（小写，与 SearchSession.Candidate.TrackId 对齐）。</summary>
    public string TrackIdHex { get; set; } = "";

    /// <summary>来自哪一段。</summary>
    public ushort SegmentId { get; set; }

    /// <summary>该文档的 bm25 分数（负值，与 FTS5 同号）。</summary>
    public double Score { get; set; }

    /// <summary>
    /// 读音趟分档（<see cref="ReadingRanker"/> 的 Prefix / Contains / Other）；
    /// 非读音趟恒为 0，不参与排序。
    /// </summary>
    public int Tier { get; set; }

    /// <summary>
    /// 多 term 元数据步的最小跨度（T3.11 邻近度，由
    /// <see cref="EraProximity.Fill"/> 写入）；未计算 = <c>int.MaxValue</c>。
    /// </summary>
    public int Proximity { get; set; } = SearchRanker.ProximityUnknown;
}
