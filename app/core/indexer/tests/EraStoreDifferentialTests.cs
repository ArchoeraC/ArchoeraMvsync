// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P9：<see cref="NaiveIndex"/> 差分（设计文档 §3.3.0.16 对拍要求 / §5.3 P9）。
///
/// <para><b>差分口径</b>：比较对象是存储层迭代器（Term / Prefix / And / Or / Phrase）产出的
/// <b>段内文档序列</b>——两侧都按 <c>(segment, doc_id)</c> 升序，完整序列逐条比对，并单独断言
/// 前 <see cref="TopK"/> 条顺序逐条一致（不是只比集合）。这里<b>不</b>比较 BM25 桶序 / 排序键：
/// 桶排序、打分与 <c>top-k</c> 游标属于 S3 的完整 Ranker（§3.6.4），本 PR 只锁存储层召回与顺序。</para>
///
/// <para>语料：600 doc / 6 段（<see cref="SegmentDocs"/>）、字段 Title / Artist / Album / Filename，
/// 词表混合 CJK 双字 / 单词与 ASCII；查询 10,000 条 xorshift 伪随机 AST（深度 ≤3）。</para>
/// </summary>
internal static class EraStoreDifferentialTests
{
    private const int DocCount = 600;
    private const int SegmentDocs = 100;
    private const int QueryCount = 10_000;
    private const int TopK = 20;
    private const int PositionSamples = 200;
    private const int EmptyProbeQueries = 1_000;

    private static readonly FieldId[] AllFields =
        { FieldId.Title, FieldId.Artist, FieldId.Album, FieldId.Filename };

    private static readonly string[] CjkWords =
        { "晴天", "娃娃", "雨", "周杰伦", "月亮", "夜", "爱", "心情", "晚安", "北风" };

    private static readonly string[] AsciiWords =
        { "love", "lonely", "night", "v2", "rain", "moon", "heart", "dream", "sun", "sunny" };

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

    /* ------------------------------------------------------------------ */

    private static void RunCore(string dir)
    {
        var corpus = BuildCorpus(new Rng(0x9E37_2026u));
        var path = Path.Combine(dir, "search-1.eraindex");
        var result = IndexBuilder.Build(path, corpus.Docs, Options());
        Program.Check("P9 语料: 600 doc / 6 段",
            corpus.Docs.Count == DocCount && result.SegmentCount == DocCount / SegmentDocs,
            $"{corpus.Docs.Count}/{result.SegmentCount}");

        var raw = File.ReadAllBytes(path);
        var sections = result.Manifest.Sections;
        var segs = new SegmentReaders[result.SegmentCount];
        for (var s = 0; s < segs.Length; s++)
            segs[s] = SegmentReaders.Open(raw, sections, (ushort)s);

        var counts = segs.Select(x => x.Docs.Count).ToArray();
        Program.Check("P9 段: 每段 DocStore 计数 100",
            counts.All(c => c == SegmentDocs), string.Join(',', counts));

        var naive = new NaiveIndex(corpus.Segments);
        Program.Check("P9 段: 朴素索引段数与引擎一致", naive.SegmentCount == segs.Length,
            $"{naive.SegmentCount}/{segs.Length}");

        var queries = new List<NaiveQuery>(QueryCount);
        var queryRng = new Rng(0x5EED_2026u);
        for (var i = 0; i < QueryCount; i++) queries.Add(RandomQuery(queryRng, corpus, 3));

        var empty = CompareSequences(segs, naive, queries);
        EmptyRatio(empty);
        PositionsAgreement(segs, naive, new Rng(0xA11CEu));
        PrefixTruncation(segs, naive, corpus);
    }

    /// <summary>10k 查询 × 每段：完整序列 + top-20 顺序逐条比对。</summary>
    private static (bool[] EngineEmpty, bool[] NaiveEmpty) CompareSequences(
        SegmentReaders[] segs, NaiveIndex naive, IReadOnlyList<NaiveQuery> queries)
    {
        var engineEmpty = new bool[queries.Count];
        var naiveEmpty = new bool[queries.Count];
        Array.Fill(engineEmpty, true);
        Array.Fill(naiveEmpty, true);

        var fullBad = new int[segs.Length];
        var topBad = new int[segs.Length];
        var fullDetail = new string?[segs.Length];
        var topDetail = new string?[segs.Length];

        for (var q = 0; q < queries.Count; q++)
        {
            var query = queries[q];
            for (var s = 0; s < segs.Length; s++)
            {
                var engine = DrainEngine(query, segs[s].Dict, segs[s].Postings);
                var expected = naive.Eval(s, query);
                if (engine.Count > 0) engineEmpty[q] = false;
                if (expected.Count > 0) naiveEmpty[q] = false;

                var diff = FirstDifference(engine, expected);
                if (diff >= 0)
                {
                    fullBad[s]++;
                    fullDetail[s] ??= Detail(q, s, diff, engine, expected, query);
                }

                if (!TopEqual(engine, expected, TopK))
                {
                    topBad[s]++;
                    topDetail[s] ??= Detail(q, s, diff, engine, expected, query);
                }
            }
        }

        for (var s = 0; s < segs.Length; s++)
        {
            Program.Check($"P9 差分 seg{s}: 完整序列一致（{queries.Count} 查询）",
                fullBad[s] == 0, Truncate(fullDetail[s] ?? string.Empty, 300));
            Program.Check($"P9 差分 seg{s}: top-20 顺序逐条一致",
                topBad[s] == 0, Truncate(topDetail[s] ?? string.Empty, 300));
        }

        return (engineEmpty, naiveEmpty);
    }

    /// <summary>前 1,000 条查询里引擎 / 朴素的空结果比例一致，且非空查询占多数（防假绿）。</summary>
    private static void EmptyRatio((bool[] EngineEmpty, bool[] NaiveEmpty) empty)
    {
        var n = Math.Min(EmptyProbeQueries, empty.EngineEmpty.Length);
        var engineEmpty = 0;
        var naiveEmpty = 0;
        var bothEmpty = 0;
        for (var i = 0; i < n; i++)
        {
            if (empty.EngineEmpty[i]) engineEmpty++;
            if (empty.NaiveEmpty[i]) naiveEmpty++;
            if (empty.EngineEmpty[i] && empty.NaiveEmpty[i]) bothEmpty++;
        }

        Program.Check($"P9 空结果: 引擎/朴素空查询数一致（{n} 条）",
            engineEmpty == naiveEmpty && bothEmpty == engineEmpty,
            $"engine={engineEmpty} naive={naiveEmpty} both={bothEmpty}");
        Program.Check("P9 空结果: 非空查询占多数（防止两边都没实现的假绿）",
            bothEmpty <= n * 4 / 5, $"both={bothEmpty}/{n}");
    }

    /// <summary>抽 200 个真实命中的 <c>(field, term, doc)</c>，比对 TermIter 与朴素的位置序列。</summary>
    private static void PositionsAgreement(SegmentReaders[] segs, NaiveIndex naive, Rng rng)
    {
        var terms = new List<(int Segment, FieldId Field, string Term)>();
        for (var s = 0; s < segs.Length; s++)
            foreach (var info in segs[s].Dict.ReadAll())
                terms.Add((s, info.Field, info.Term));

        var sampled = 0;
        var bad = 0;
        var detail = string.Empty;
        for (var i = 0; i < PositionSamples && terms.Count > 0; i++)
        {
            var pick = terms[rng.NextInt(terms.Count)];
            var reader = segs[pick.Segment];
            if (!reader.Dict.TryFind(pick.Field, pick.Term, out var info)) continue;
            var postings = reader.Postings.DecodeAll(info.PostOffset, info.Df);
            if (postings.Count == 0) continue;

            var entry = postings[rng.NextInt(postings.Count)];
            using var iter = new TermIter(reader.Dict, reader.Postings, pick.Field, pick.Term);
            sampled++;
            if (!iter.Advance(entry.Doc) || iter.Current != entry.Doc)
            {
                bad++;
                detail = $"seg{pick.Segment} {pick.Field}:{pick.Term} doc{entry.Doc} 未命中";
                continue;
            }

            var enginePos = iter.Positions().ToArray();
            var naivePos = naive.Positions(pick.Segment, entry.Doc, pick.Field, pick.Term);
            if (enginePos.AsSpan().SequenceEqual(naivePos)) continue;

            bad++;
            detail = $"seg{pick.Segment} {pick.Field}:{pick.Term} doc{entry.Doc} "
                + $"engine=[{string.Join(',', enginePos)}] naive=[{string.Join(',', naivePos)}]";
        }

        Program.Check($"P9 位置: TermIter 与朴素一致（{sampled} 抽样）",
            bad == 0 && sampled == PositionSamples, Truncate(detail, 300));
    }

    /// <summary>小 <c>MaxExpansions</c>（含 0 / 1）下两侧截断后结果一致。</summary>
    private static void PrefixTruncation(SegmentReaders[] segs, NaiveIndex naive, Corpus corpus)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal) { "未" };
        foreach (var word in corpus.Words)
        {
            if (word.Length == 0) continue;
            prefixes.Add(word[..1]);
            if (word.Length >= 2) prefixes.Add(word[..2]);
        }

        var maxes = new[] { 0, 1, 2, 3, 5 };
        var cases = 0;
        var bad = 0;
        var detail = string.Empty;
        foreach (var field in AllFields)
            foreach (var prefix in prefixes)
                foreach (var max in maxes)
                {
                    var query = new NaivePrefix(field, prefix, max);
                    for (var s = 0; s < segs.Length; s++)
                    {
                        var engine = DrainEngine(query, segs[s].Dict, segs[s].Postings);
                        var expected = naive.Eval(s, query);
                        cases++;
                        if (engine.SequenceEqual(expected)) continue;
                        bad++;
                        if (detail.Length == 0)
                            detail = $"seg{s} {field}:{prefix}* max={max} "
                                + $"engine=[{Show(engine, TopK)}] naive=[{Show(expected, TopK)}]";
                    }
                }

        Program.Check($"P9 前缀: MaxExpansions 截断一致（{cases} 组）", bad == 0, Truncate(detail, 300));
    }

    /* ------------------------------------------------------------------ */

    /// <summary>把朴素 AST 逐节点映射成 EraStore 迭代器树。</summary>
    private static IDocIter BuildEngine(NaiveQuery query, TermDictReader dict, PostingsReader postings)
        => query switch
        {
            NaiveTerm term => new TermIter(dict, postings, term.Field, term.Term),
            NaivePrefix prefix => new PrefixIter(
                dict, postings, prefix.Field, prefix.Prefix, prefix.MaxExpansions),
            NaivePhrase phrase => new PhraseIter(PhraseChildren(phrase, dict, postings), positioned: true),
            NaiveAnd and => new AndIter(
                and.Children.Select(child => BuildEngine(child, dict, postings)).ToList()),
            NaiveOr or => new OrIter(
                or.Children.Select(child => BuildEngine(child, dict, postings)).ToList()),
            _ => throw new ArgumentException($"未知查询节点 {query.GetType().Name}", nameof(query)),
        };

    /// <summary>重复词各建一个 TermIter（与 <c>PhraseIter</c> 的用例一致）。</summary>
    private static List<IDocIter> PhraseChildren(
        NaivePhrase phrase, TermDictReader dict, PostingsReader postings)
    {
        var children = new List<IDocIter>(phrase.Terms.Count);
        foreach (var term in phrase.Terms)
            children.Add(new TermIter(dict, postings, phrase.Field, term));
        return children;
    }

    private static List<int> DrainEngine(NaiveQuery query, TermDictReader dict, PostingsReader postings)
    {
        using var iter = BuildEngine(query, dict, postings);
        var docs = new List<int>();
        while (iter.Next()) docs.Add(iter.Current);
        return docs;
    }

    private static int FirstDifference(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) return i;
        return a.Count == b.Count ? -1 : n;
    }

    private static bool TopEqual(IReadOnlyList<int> a, IReadOnlyList<int> b, int k)
    {
        var n = Math.Min(k, Math.Max(a.Count, b.Count));
        for (var i = 0; i < n; i++)
        {
            var left = i < a.Count ? a[i] : -1;
            var right = i < b.Count ? b[i] : -1;
            if (left != right) return false;
        }
        return true;
    }

    /* ------------------------------------------------------------------ */

    /// <summary>确定性合成语料（xorshift，固定种子）。</summary>
    private sealed class Corpus
    {
        public readonly List<AnalyzedDoc> Docs = new();
        public readonly List<List<IReadOnlyDictionary<FieldId, string[]>>> Segments = new();
        public readonly List<(FieldId Field, string[] Terms)> Phrases = new();
        public readonly List<string> Words = new();
    }

    private static Corpus BuildCorpus(Rng rng)
    {
        var corpus = new Corpus();
        corpus.Words.AddRange(CjkWords);
        corpus.Words.AddRange(AsciiWords);

        var segment = new List<IReadOnlyDictionary<FieldId, string[]>>();
        for (var d = 0; d < DocCount; d++)
        {
            var fieldCount = 1 + rng.NextInt(4);
            var fields = new List<FieldId> { FieldId.Title };
            while (fields.Count < fieldCount)
            {
                var candidate = AllFields[rng.NextInt(AllFields.Length)];
                if (!fields.Contains(candidate)) fields.Add(candidate);
            }

            // 每 doc 3–8 token；先给每个字段各一个，再随机分配剩余。
            var total = Math.Max(3 + rng.NextInt(6), fields.Count);
            var perField = new Dictionary<FieldId, List<string>>();
            foreach (var field in fields) perField[field] = new List<string>();
            for (var t = 0; t < total; t++)
            {
                var field = t < fields.Count ? fields[t] : fields[rng.NextInt(fields.Count)];
                perField[field].Add(rng.NextInt(100) < 12 ? string.Empty : PickWord(rng, corpus));
            }

            var tokens = new Dictionary<FieldId, string[]>();
            foreach (var (field, list) in perField) tokens[field] = list.ToArray();

            corpus.Docs.Add(MakeDoc(d, tokens));
            segment.Add(tokens);
            CollectPhrases(corpus, tokens);

            if (segment.Count < SegmentDocs) continue;
            corpus.Segments.Add(segment);
            segment = new List<IReadOnlyDictionary<FieldId, string[]>>();
        }
        if (segment.Count > 0) corpus.Segments.Add(segment);
        return corpus;
    }

    /// <summary>抽取真实的 2 / 3 词相邻片段（位置下标连续且都非空），供短语查询命中。</summary>
    private static void CollectPhrases(Corpus corpus, IReadOnlyDictionary<FieldId, string[]> tokens)
    {
        foreach (var (field, raw) in tokens)
        {
            for (var i = 0; i + 1 < raw.Length; i++)
            {
                if (raw[i].Length == 0 || raw[i + 1].Length == 0) continue;
                corpus.Phrases.Add((field, new[] { raw[i], raw[i + 1] }));
                if (i + 2 < raw.Length && raw[i + 2].Length != 0)
                    corpus.Phrases.Add((field, new[] { raw[i], raw[i + 1], raw[i + 2] }));
            }
        }
    }

    private static AnalyzedDoc MakeDoc(int index, IReadOnlyDictionary<FieldId, string[]> tokens)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(index * 3 + i);
            rev[i] = (byte)(index * 11 + i * 5);
        }

        return new AnalyzedDoc(track, rev, (ushort)(index & 0x7), (byte)(index & 0x03), tokens,
            Text(tokens, FieldId.Title), Text(tokens, FieldId.Artist), Text(tokens, FieldId.Album));
    }

    private static string Text(IReadOnlyDictionary<FieldId, string[]> tokens, FieldId field) =>
        tokens.TryGetValue(field, out var raw)
            ? string.Join(' ', raw.Where(term => term.Length != 0))
            : string.Empty;

    private static string PickWord(Rng rng, Corpus corpus) => corpus.Words[rng.NextInt(corpus.Words.Count)];

    /* ------------------------------------------------------------------ */

    /// <summary>AST 深度 ≤3；容器节点 2–3 个子节点。</summary>
    private static NaiveQuery RandomQuery(Rng rng, Corpus corpus, int depth)
    {
        if (depth > 1 && rng.NextInt(100) < 35)
        {
            var count = 2 + rng.NextInt(2);
            var children = new NaiveQuery[count];
            for (var i = 0; i < count; i++) children[i] = RandomQuery(rng, corpus, depth - 1);
            return rng.NextInt(100) < 50 ? new NaiveAnd(children) : new NaiveOr(children);
        }

        return rng.NextInt(100) switch
        {
            < 60 => RandomTerm(rng, corpus),
            < 85 => RandomPrefix(rng, corpus),
            _ => RandomPhrase(rng, corpus),
        };
    }

    /// <summary>50% 命中语料词表，50% 未登录词（确保两侧都为空的分支也被覆盖）。</summary>
    private static NaiveQuery RandomTerm(Rng rng, Corpus corpus) =>
        rng.NextInt(100) < 50
            ? new NaiveTerm(RandomField(rng), PickWord(rng, corpus))
            : new NaiveTerm(RandomField(rng), "未登录" + rng.NextInt(5000));

    private static NaiveQuery RandomPrefix(Rng rng, Corpus corpus)
    {
        var word = PickWord(rng, corpus);
        var take = 1 + rng.NextInt(2);
        var prefix = take >= word.Length ? word : word[..take];
        if (rng.NextInt(100) < 20) prefix = "未";
        var max = rng.NextInt(100) < 20 ? int.MaxValue : 1 + rng.NextInt(6);
        return new NaivePrefix(RandomField(rng), prefix, max);
    }

    /// <summary>50% 取语料真实相邻片段（含重复词），50% 随机 2–3 词。</summary>
    private static NaiveQuery RandomPhrase(Rng rng, Corpus corpus)
    {
        if (corpus.Phrases.Count > 0 && rng.NextInt(100) < 50)
        {
            var picked = corpus.Phrases[rng.NextInt(corpus.Phrases.Count)];
            return new NaivePhrase(picked.Field, picked.Terms);
        }

        var count = 2 + rng.NextInt(2);
        var terms = new string[count];
        for (var i = 0; i < count; i++) terms[i] = PickWord(rng, corpus);
        return new NaivePhrase(RandomField(rng), terms);
    }

    private static FieldId RandomField(Rng rng) => AllFields[rng.NextInt(AllFields.Length)];

    private static string Describe(NaiveQuery query) => query switch
    {
        NaiveTerm term => $"T({term.Field}:{term.Term})",
        NaivePrefix prefix => $"P({prefix.Field}:{prefix.Prefix}*,{prefix.MaxExpansions})",
        NaivePhrase phrase => $"PH({phrase.Field}:{string.Join(' ', phrase.Terms)})",
        NaiveAnd and => $"AND({string.Join(',', and.Children.Select(Describe))})",
        NaiveOr or => $"OR({string.Join(',', or.Children.Select(Describe))})",
        _ => query.GetType().Name,
    };

    private static string Detail(
        int query, int segment, int diff, List<int> engine, List<int> expected, NaiveQuery ast)
    {
        var common = Math.Min(engine.Count, expected.Count);
        var at = diff < 0 ? "equal"
            : diff >= common && engine.Count != expected.Count
                ? $"count({engine.Count}/{expected.Count})"
                : $"firstDiff#{diff}";
        return $"q#{query} seg{segment} {at} engine[{Show(engine, TopK)}] "
            + $"naive[{Show(expected, TopK)}] {Describe(ast)}";
    }

    private static string Show(IReadOnlyList<int> docs, int limit)
    {
        var n = Math.Min(limit, docs.Count);
        return string.Join(',', docs.Take(n)) + (docs.Count > limit ? ",…" : string.Empty);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /* ------------------------------------------------------------------ */

    private sealed class SegmentReaders
    {
        public required DocStoreReader Docs { get; init; }
        public required TermDictReader Dict { get; init; }
        public required PostingsReader Postings { get; init; }

        public static SegmentReaders Open(byte[] raw, List<SectionRef> sections, ushort segmentId) => new()
        {
            Docs = new DocStoreReader(SectionBody(raw, Section(sections, SectionKind.DocStore, segmentId))),
            Dict = new TermDictReader(
                SectionBody(raw, Section(sections, SectionKind.Dict, segmentId)),
                SectionBody(raw, Section(sections, SectionKind.DictIndex, segmentId))),
            Postings = new PostingsReader(
                SectionBody(raw, Section(sections, SectionKind.Postings, segmentId)),
                SectionBody(raw, Section(sections, SectionKind.Positions, segmentId))),
        };
    }

    private static SectionRef Section(List<SectionRef> sections, SectionKind kind, ushort segmentId)
    {
        foreach (var section in sections)
            if (section.Kind == kind && section.SegmentId == segmentId) return section;
        throw new InvalidOperationException($"缺少段 {kind}（seg={segmentId}）");
    }

    private static byte[] SectionBody(byte[] file, SectionRef section) =>
        file.AsSpan((int)section.Offset, (int)section.Length).ToArray();

    private static BuildOptions Options() => new()
    {
        DocsPerSegment = SegmentDocs,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray(),
        CreatedMs = 1_760_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    /// <summary>确定性伪随机（xorshift32），避免 <see cref="Random"/> 的实现差异。</summary>
    private sealed class Rng
    {
        private uint _state;

        public Rng(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

        public uint NextUInt()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        public int NextInt(int exclusiveMax) =>
            exclusiveMax <= 1 ? 0 : (int)(NextUInt() % (uint)exclusiveMax);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-diff-" + Guid.NewGuid().ToString("N"));
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
