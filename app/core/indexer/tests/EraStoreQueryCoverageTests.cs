// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T2.14 查询覆盖差分（设计文档 §5.0 S2.7）：字面精确 / 字面前缀 / 元数据拼音短语 /
/// 歌词拼音 / 首字母 / 截断六类查询，与 <see cref="NaiveIndex"/> 逐条对拍。
///
/// <para>与 <see cref="EraStoreDifferentialTests"/> 的差别是语料覆盖面：400 doc / 4 段，
/// 覆盖 <see cref="FieldId"/> 全部 12 个字段族（字面 4 + 歌词三层 + 拼音 / 首字母 / 读音），
/// 且每类查询按语义显式命名，而不是只做随机 AST。比较口径仍是存储层迭代器产出的
/// <b>段内文档完整序列</b>（逐条比对，不是只看集合）。</para>
/// </summary>
internal static class EraStoreQueryCoverageTests
{
    private const int DocCount = 400;
    private const int SegmentDocs = 100;
    private const int QueriesPerCategory = 300;
    private const int TopK = 20;

    private static readonly FieldId[] AllFields =
    {
        FieldId.Title, FieldId.Artist, FieldId.Album, FieldId.Filename,
        FieldId.Lyrics, FieldId.LyricsTranslation, FieldId.LyricsRomanization,
        FieldId.PinyinMeta, FieldId.PinyinLyrics,
        FieldId.InitialsMeta, FieldId.InitialsLyrics, FieldId.ReadingMeta,
    };

    private static readonly string[] CjkWords =
        { "晴天", "月亮", "晚安", "北风", "心情", "海边", "星光", "远方", "回忆", "温柔" };

    private static readonly string[] AsciiWords =
        { "love", "night", "rain", "moon", "heart", "dream", "sun", "sky", "blue", "fire" };

    /// <summary>真实拼音音节（相邻音节可构成短语查询）。</summary>
    private static readonly string[] PinyinSyllables =
    {
        "wo", "ai", "ni", "yue", "liang", "qing", "tian",
        "de", "xin", "yong", "yuan", "fang", "hai", "shang", "xing", "guang",
    };

    private static readonly string[] InitialLetters =
        { "w", "a", "n", "y", "l", "q", "t", "d", "x", "h" };

    /// <summary>读音列 2 字符 bigram。</summary>
    private static readonly string[] ReadBigrams =
        { "ky", "yu", "ra", "ri", "in", "si", "tu", "ga", "ko", "nn" };

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
        var corpus = BuildCorpus(new Rng(0x7C0F_2026u));
        var path = Path.Combine(dir, "search-1.eraindex");
        var result = IndexBuilder.Build(path, corpus.Docs, Options());
        Program.Check("T2.14 语料: 400 doc / 4 段 / 12 字段族",
            corpus.Docs.Count == DocCount
                && result.SegmentCount == DocCount / SegmentDocs
                && corpus.SeenFields.Count == AllFields.Length,
            $"{corpus.Docs.Count}/{result.SegmentCount}/{corpus.SeenFields.Count}");

        var raw = File.ReadAllBytes(path);
        var sections = result.Manifest.Sections;
        var segs = new SegmentReaders[result.SegmentCount];
        for (var s = 0; s < segs.Length; s++)
            segs[s] = SegmentReaders.Open(raw, sections, (ushort)s);

        var counts = segs.Select(x => x.Docs.Count).ToArray();
        Program.Check("T2.14 段: 每段 DocStore 计数 100",
            counts.Length == 4 && counts.All(c => c == SegmentDocs), string.Join(',', counts));

        var naive = new NaiveIndex(corpus.Segments);
        Program.Check("T2.14 段: 朴素索引段数与引擎一致", naive.SegmentCount == segs.Length,
            $"{naive.SegmentCount}/{segs.Length}");

        var rng = new Rng(0x51D3_2026u);

        CompareCategory("literal-exact", LiteralExactQueries(rng), segs, naive);
        CompareCategory("literal-prefix", LiteralPrefixQueries(rng), segs, naive);
        CompareCategory("meta-pinyin-phrase", MetaPinyinPhraseQueries(rng, corpus), segs, naive);
        CompareCategory("lyrics-pinyin", LyricsPinyinQueries(rng), segs, naive);
        CompareCategory("initials", InitialsQueries(rng), segs, naive);

        var truncated = TruncatedQueries(rng);
        CompareCategory("truncated", truncated, segs, naive);
        TruncatedZeroIsEmpty(truncated, segs);
    }

    /// <summary>
    /// 单类查询：每条查询在每段都跑一次「引擎迭代器 vs 朴素 Eval」，完整序列逐条比对；
    /// 同时统计引擎侧非空查询数（任一非空段即算非空）。
    /// </summary>
    private static void CompareCategory(
        string name, IReadOnlyList<NaiveQuery> queries, SegmentReaders[] segs, NaiveIndex naive)
    {
        var nonEmpty = 0;
        var bad = 0;
        var detail = string.Empty;

        for (var q = 0; q < queries.Count; q++)
        {
            var query = queries[q];
            var hit = false;
            for (var s = 0; s < segs.Length; s++)
            {
                var engine = DrainEngine(query, segs[s].Dict, segs[s].Postings);
                var expected = naive.Eval(s, query);
                if (engine.Count > 0) hit = true;
                if (engine.SequenceEqual(expected)) continue;

                bad++;
                if (detail.Length == 0)
                    detail = $"q#{q} seg{s} 引擎[{Show(engine, TopK)}] "
                        + $"朴素[{Show(expected, TopK)}] {query}";
            }
            if (hit) nonEmpty++;
        }

        Program.Check($"{name}: 引擎与朴素完整序列一致（{queries.Count} 查询 × {segs.Length} 段）",
            bad == 0, Truncate(detail, 500));
        // 防假绿：全部空集时两边也会「一致」，非空比例必须够高。
        Program.Check($"{name}: 非空覆盖率 ≥10%（{nonEmpty}/{queries.Count}）",
            queries.Count == 0 || nonEmpty * 10 >= queries.Count,
            $"{nonEmpty}/{queries.Count}");

        var percent = queries.Count == 0 ? 0 : nonEmpty * 100 / queries.Count;
        Console.WriteLine($"T2.14 {name}: {queries.Count} 查询，非空 {nonEmpty}（{percent}%）");
    }

    /// <summary><c>MaxExpansions = 0</c> 的截断查询必须恒为空（两侧都不允许召回）。</summary>
    private static void TruncatedZeroIsEmpty(IReadOnlyList<NaiveQuery> queries, SegmentReaders[] segs)
    {
        var cases = 0;
        var engineNonEmpty = 0;
        for (var q = 0; q < queries.Count; q++)
        {
            if (queries[q] is not NaivePrefix { MaxExpansions: 0 } prefix) continue;
            cases++;
            for (var s = 0; s < segs.Length; s++)
            {
                if (DrainEngine(prefix, segs[s].Dict, segs[s].Postings).Count > 0)
                    engineNonEmpty++;
            }
        }

        Program.Check($"truncated: MaxExpansions=0 恒为空（{cases} 查询 × {segs.Length} 段）",
            engineNonEmpty == 0, $"非空 {engineNonEmpty}");
    }

    /* ------------------------------------------------------------------ */
    /* 六类查询生成                                                        */
    /* ------------------------------------------------------------------ */

    /// <summary>字面精确：Title / Artist / Album / Filename / Lyrics，含语料词与未登录词。</summary>
    private static List<NaiveQuery> LiteralExactQueries(Rng rng)
    {
        var fields = new[]
            { FieldId.Title, FieldId.Artist, FieldId.Album, FieldId.Filename, FieldId.Lyrics };
        var queries = new List<NaiveQuery>(QueriesPerCategory);
        for (var i = 0; i < QueriesPerCategory; i++)
        {
            var field = fields[rng.NextInt(fields.Length)];
            var term = rng.NextInt(100) < 60 ? PickLiteral(rng) : OovTerm(rng);
            queries.Add(new NaiveTerm(field, term));
        }
        return queries;
    }

    /// <summary>字面前缀：Title / Lyrics 的 1–2 字符前缀，MaxExpansions 取 int.MaxValue / 1 / 3。</summary>
    private static List<NaiveQuery> LiteralPrefixQueries(Rng rng)
    {
        var fields = new[] { FieldId.Title, FieldId.Lyrics };
        var maxes = new[] { int.MaxValue, 1, 3 };
        var queries = new List<NaiveQuery>(QueriesPerCategory);
        for (var i = 0; i < QueriesPerCategory; i++)
        {
            var field = fields[rng.NextInt(fields.Length)];
            var word = PickLiteral(rng);
            var take = 1 + rng.NextInt(2);
            var prefix = take >= word.Length ? word : word[..take];
            var max = maxes[rng.NextInt(maxes.Length)];
            queries.Add(new NaivePrefix(field, prefix, max));
        }
        return queries;
    }

    /// <summary>元数据拼音短语：语料中真实相邻的 2 / 3 音节。</summary>
    private static List<NaiveQuery> MetaPinyinPhraseQueries(Rng rng, Corpus corpus)
    {
        var queries = new List<NaiveQuery>(QueriesPerCategory);
        if (corpus.Phrases.Count == 0) return queries; // 语料生成保证非空，这里只做防御。
        for (var i = 0; i < QueriesPerCategory; i++)
        {
            var picked = corpus.Phrases[rng.NextInt(corpus.Phrases.Count)];
            queries.Add(new NaivePhrase(picked.Field, picked.Terms));
        }
        return queries;
    }

    /// <summary>歌词拼音：PinyinLyrics 的精确音节词条与前缀。</summary>
    private static List<NaiveQuery> LyricsPinyinQueries(Rng rng)
    {
        var queries = new List<NaiveQuery>(QueriesPerCategory);
        for (var i = 0; i < QueriesPerCategory; i++)
        {
            var syllable = PickSyllable(rng);
            if (rng.NextInt(100) < 50)
            {
                queries.Add(new NaiveTerm(FieldId.PinyinLyrics, syllable));
                continue;
            }

            var take = syllable.Length >= 2 ? 1 + rng.NextInt(2) : 1;
            var prefix = take >= syllable.Length ? syllable : syllable[..take];
            var max = rng.NextInt(2) == 0 ? int.MaxValue : 1 + rng.NextInt(2);
            queries.Add(new NaivePrefix(FieldId.PinyinLyrics, prefix, max));
        }
        return queries;
    }

    /// <summary>首字母：InitialsMeta / InitialsLyrics 的单字母精确词条与前缀。</summary>
    private static List<NaiveQuery> InitialsQueries(Rng rng)
    {
        var queries = new List<NaiveQuery>(QueriesPerCategory);
        for (var i = 0; i < QueriesPerCategory; i++)
        {
            var field = rng.NextInt(2) == 0 ? FieldId.InitialsMeta : FieldId.InitialsLyrics;
            var letter = InitialLetters[rng.NextInt(InitialLetters.Length)];
            if (rng.NextInt(100) < 50)
            {
                queries.Add(new NaiveTerm(field, letter));
                continue;
            }

            var max = rng.NextInt(2) == 0 ? int.MaxValue : 3;
            queries.Add(new NaivePrefix(field, letter, max));
        }
        return queries;
    }

    /// <summary>截断：Title / PinyinLyrics 前缀，MaxExpansions ∈ {0,1,2}（每档各 100 条）。</summary>
    private static List<NaiveQuery> TruncatedQueries(Rng rng)
    {
        var fields = new[] { FieldId.Title, FieldId.PinyinLyrics };
        var maxes = new[] { 0, 1, 2 };
        var queries = new List<NaiveQuery>(QueriesPerCategory);
        for (var i = 0; i < QueriesPerCategory; i++)
        {
            var field = fields[rng.NextInt(fields.Length)];
            string prefix;
            if (field == FieldId.Title)
            {
                var word = PickLiteral(rng);
                var take = 1 + rng.NextInt(2);
                prefix = take >= word.Length ? word : word[..take];
            }
            else
            {
                var syllable = PickSyllable(rng);
                var take = syllable.Length >= 2 ? 1 + rng.NextInt(2) : 1;
                prefix = take >= syllable.Length ? syllable : syllable[..take];
            }
            queries.Add(new NaivePrefix(field, prefix, maxes[i % maxes.Length]));
        }
        return queries;
    }

    /* ------------------------------------------------------------------ */
    /* 语料                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>确定性合成语料（xorshift，固定种子）。</summary>
    private sealed class Corpus
    {
        public readonly List<AnalyzedDoc> Docs = new();
        public readonly List<List<IReadOnlyDictionary<FieldId, string[]>>> Segments = new();
        public readonly List<(FieldId Field, string[] Terms)> Phrases = new();
        public readonly HashSet<FieldId> SeenFields = new();
    }

    private static Corpus BuildCorpus(Rng rng)
    {
        var corpus = new Corpus();
        var segment = new List<IReadOnlyDictionary<FieldId, string[]>>();

        for (var d = 0; d < DocCount; d++)
        {
            // Title 必有；其余字段按固定概率出现（400 doc 足以覆盖全部 12 个字段族）。
            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = LiteralTokens(rng, 2 + rng.NextInt(3)),
            };
            if (rng.NextInt(100) < 70) tokens[FieldId.Artist] = LiteralTokens(rng, 2 + rng.NextInt(2));
            if (rng.NextInt(100) < 70) tokens[FieldId.Album] = LiteralTokens(rng, 2 + rng.NextInt(2));
            if (rng.NextInt(100) < 60) tokens[FieldId.Filename] = LiteralTokens(rng, 2 + rng.NextInt(3));
            if (rng.NextInt(100) < 80) tokens[FieldId.Lyrics] = LyricLines(rng);
            if (rng.NextInt(100) < 50) tokens[FieldId.LyricsTranslation] = LyricLines(rng);
            if (rng.NextInt(100) < 50) tokens[FieldId.LyricsRomanization] = LyricLines(rng);
            if (rng.NextInt(100) < 75) tokens[FieldId.PinyinMeta] = SyllableTokens(rng, 2 + rng.NextInt(4));
            if (rng.NextInt(100) < 65) tokens[FieldId.PinyinLyrics] = SyllableLines(rng);
            if (rng.NextInt(100) < 70) tokens[FieldId.InitialsMeta] = LetterTokens(rng, 2 + rng.NextInt(3));
            if (rng.NextInt(100) < 55) tokens[FieldId.InitialsLyrics] = LetterTokens(rng, 3 + rng.NextInt(3));
            if (rng.NextInt(100) < 55) tokens[FieldId.ReadingMeta] = BigramTokens(rng, 2 + rng.NextInt(3));

            foreach (var field in tokens.Keys) corpus.SeenFields.Add(field);
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

    /// <summary>抽取 PinyinMeta 的真实 2 / 3 音节相邻片段（位置连续且都非空），供短语命中。</summary>
    private static void CollectPhrases(Corpus corpus, IReadOnlyDictionary<FieldId, string[]> tokens)
    {
        if (!tokens.TryGetValue(FieldId.PinyinMeta, out var raw)) return;
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i].Length == 0 || raw[i + 1].Length == 0) continue;
            corpus.Phrases.Add((FieldId.PinyinMeta, new[] { raw[i], raw[i + 1] }));
            if (i + 2 < raw.Length && raw[i + 2].Length != 0)
                corpus.Phrases.Add((FieldId.PinyinMeta, new[] { raw[i], raw[i + 1], raw[i + 2] }));
        }
    }

    /// <summary>字面字段 token（CJK 双字词 + ASCII 词混合，少量空 token 占位）。</summary>
    private static string[] LiteralTokens(Rng rng, int count)
    {
        var list = new string[count];
        for (var i = 0; i < count; i++)
            list[i] = rng.NextInt(100) < 8 ? string.Empty : PickLiteral(rng);
        return list;
    }

    /// <summary>歌词行：2–3 行，换行处插空 token（占位置但不产倒排），每行 2–3 个 token。</summary>
    private static string[] LyricLines(Rng rng)
    {
        var list = new List<string>();
        var lines = 2 + rng.NextInt(2);
        for (var line = 0; line < lines; line++)
        {
            if (line > 0) list.Add(string.Empty);
            var count = 2 + rng.NextInt(2);
            for (var i = 0; i < count; i++)
                list.Add(rng.NextInt(100) < 8 ? string.Empty : PickLiteral(rng));
        }
        return list.ToArray();
    }

    private static string[] SyllableTokens(Rng rng, int count)
    {
        var list = new string[count];
        for (var i = 0; i < count; i++) list[i] = PickSyllable(rng);
        return list;
    }

    /// <summary>歌词拼音行：多行、换行插空 token，与歌词原文同构。</summary>
    private static string[] SyllableLines(Rng rng)
    {
        var list = new List<string>();
        var lines = 2 + rng.NextInt(2);
        for (var line = 0; line < lines; line++)
        {
            if (line > 0) list.Add(string.Empty);
            var count = 2 + rng.NextInt(2);
            for (var i = 0; i < count; i++)
                list.Add(rng.NextInt(100) < 6 ? string.Empty : PickSyllable(rng));
        }
        return list.ToArray();
    }

    private static string[] LetterTokens(Rng rng, int count)
    {
        var list = new string[count];
        for (var i = 0; i < count; i++)
            list[i] = rng.NextInt(100) < 8 ? string.Empty : PickLetter(rng);
        return list;
    }

    private static string[] BigramTokens(Rng rng, int count)
    {
        var list = new string[count];
        for (var i = 0; i < count; i++) list[i] = PickBigram(rng);
        return list;
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

    private static string PickLiteral(Rng rng) =>
        rng.NextInt(100) < 55
            ? CjkWords[rng.NextInt(CjkWords.Length)]
            : AsciiWords[rng.NextInt(AsciiWords.Length)];

    private static string PickSyllable(Rng rng) => PinyinSyllables[rng.NextInt(PinyinSyllables.Length)];

    private static string PickLetter(Rng rng) => InitialLetters[rng.NextInt(InitialLetters.Length)];

    private static string PickBigram(Rng rng) => ReadBigrams[rng.NextInt(ReadBigrams.Length)];

    private static string OovTerm(Rng rng) =>
        rng.NextInt(2) == 0 ? "未登录" + rng.NextInt(200) : "zz" + rng.NextInt(200);

    /* ------------------------------------------------------------------ */
    /* 引擎迭代器映射                                                      */
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
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray(),
        CreatedMs = 1_760_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static string Show(IReadOnlyList<int> docs, int limit)
    {
        var n = Math.Min(limit, docs.Count);
        return string.Join(',', docs.Take(n)) + (docs.Count > limit ? ",…" : string.Empty);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

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
        var dir = Path.Combine(Path.GetTempPath(), "archoera-cov-" + Guid.NewGuid().ToString("N"));
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
