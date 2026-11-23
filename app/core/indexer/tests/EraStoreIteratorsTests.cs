// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P4：Term / Prefix / And / Or 迭代器 + <c>Advance</c> 契约。
///
/// 覆盖：全量遍历、跳块 <c>Advance</c>、未命中空迭代器、leapfrog / 小顶堆去重、
/// 前缀截断标志、<c>Next ≡ Advance(Current+1)</c>、<c>Advance</c> 单调性，
/// 以及与朴素 <see cref="HashSet{T}"/> / 排序列表实现逐条一致（P9 雏形）。
/// </summary>
internal static class EraStoreIteratorsTests
{
    public static void Run()
    {
        TermIterScan();
        TermIterAdvanceAndMiss();
        AndIterIntersection();
        AndIterLeapfrogAcrossBlocks();
        OrIterUnion();
        PrefixIterExpansion();
        PositionsForwarded();
        NextEqualsAdvance();
        AdvanceMonotonicity();
        MatchesNaive();
    }

    /* ------------------------------------------------------------------ */

    private static (TermDictReader Dict, PostingsReader Postings) Build(
        FieldId field, IReadOnlyList<(string Term, PostingEntry[] Entries)> terms)
    {
        var postings = new ByteWriter();
        var positions = new ByteWriter();
        var dict = new TermDictBuilder();
        foreach (var (term, entries) in terms)
        {
            var offset = PostingsWriter.WriteTerm(postings, positions, entries);
            dict.Add(field, term, (uint)entries.Length, offset,
                entries[0].Positions is null ? (byte)0 : (byte)1);
        }
        var (dictBytes, indexBytes) = dict.Build();
        return (new TermDictReader(dictBytes, indexBytes),
            new PostingsReader(postings.ToArray(), positions.ToArray()));
    }

    /// <summary>tf 条递增位置（0,2,4…），带位置。</summary>
    private static PostingEntry[] Entries(params (int Doc, int Tf)[] items)
    {
        var entries = new PostingEntry[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var positions = new int[items[i].Tf];
            for (var j = 0; j < positions.Length; j++) positions[j] = j * 2;
            entries[i] = new PostingEntry(items[i].Doc, items[i].Tf, positions);
        }
        return entries;
    }

    private static PostingEntry[] EntriesNoPos(params int[] docs)
    {
        var entries = new PostingEntry[docs.Length];
        for (var i = 0; i < docs.Length; i++) entries[i] = new PostingEntry(docs[i], 1, null);
        return entries;
    }

    private static List<int> Drain(IDocIter iter)
    {
        var docs = new List<int>();
        while (iter.Next()) docs.Add(iter.Current);
        return docs;
    }

    private static string Show(IEnumerable<int> docs) => string.Join(',', docs);

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    /* ------------------------------------------------------------------ */

    private static void TermIterScan()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("alpha", Entries((0, 1), (3, 2), (7, 1))),
            ("beta", Entries((1, 1), (3, 1))),
        });

        using var term = new TermIter(dict, postings, FieldId.Title, "alpha");
        Program.Check("TermIter: 初始 Current = -1", term.Current == -1);
        var docs = Drain(term);
        Program.Check("TermIter: 单 term 全量遍历", Show(docs) == "0,3,7", Show(docs));
        Program.Check("TermIter: 耗尽后 Current = -1", term.Current == -1, term.Current.ToString());
        Program.Check("TermIter: 空位置字段 Positions 为空", term.Positions().IsEmpty);
    }

    private static void TermIterAdvanceAndMiss()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("alpha", Entries((0, 1), (3, 2), (7, 1))),
        });

        using var term = new TermIter(dict, postings, FieldId.Title, "alpha");
        Program.Check("TermIter: Advance(2) 跳到 3", term.Advance(2) && term.Current == 3,
            term.Current.ToString());
        Program.Check("TermIter: Advance(3) 已满足不动", term.Advance(3) && term.Current == 3,
            term.Current.ToString());
        Program.Check("TermIter: Advance(9) 耗尽", !term.Advance(9) && term.Current == -1,
            term.Current.ToString());
        Program.Check("TermIter: 耗尽后 Next = false", !term.Next());
        Program.Check("TermIter: 耗尽后 Advance(0) = false", !term.Advance(0));
        Program.Check("TermIter: Advance(-1) 抛 ArgumentOutOfRangeException",
            Throws<ArgumentOutOfRangeException>(() => term.Advance(-1)));

        // 词典未命中：空迭代器（Current=-1、恒 false、无位置）。
        using var miss = new TermIter(dict, postings, FieldId.Title, "不存在的词");
        Program.Check("TermIter: 未命中为空迭代器",
            miss.Current == -1 && !miss.Next() && !miss.Advance(0) && miss.Positions().IsEmpty);

        // 字段族不同也不算命中（键 = field_id || term）。
        using var wrongField = new TermIter(dict, postings, FieldId.Artist, "alpha");
        Program.Check("TermIter: 其它字段族不命中", !wrongField.Next(), wrongField.Current.ToString());
    }

    private static void AndIterIntersection()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", Entries((0, 1), (3, 2), (7, 1))),
            ("b", Entries((1, 1), (3, 1), (4, 1))),
            ("c", Entries((3, 1), (9, 1))),
            ("x", Entries((8, 1))),
        });

        using (var and = new AndIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "b"),
               }))
        {
            Program.Check("AndIter: 构造后 Current = -1", and.Current == -1);
            Program.Check("AndIter: 首条为共同 doc 3",
                and.Next() && and.Current == 3 && and.Positions().IsEmpty, and.Current.ToString());
            Program.Check("AndIter: 只有一条共同 doc", !and.Next(), and.Current.ToString());
            Program.Check("AndIter: Advance(-1) 抛异常",
                Throws<ArgumentOutOfRangeException>(() => and.Advance(-1)));
        }

        using (var three = new AndIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "b"),
                   new TermIter(dict, postings, FieldId.Title, "c"),
               }))
        {
            var docs = Drain(three);
            Program.Check("AndIter: 三词交集 = {3}", Show(docs) == "3", Show(docs));
        }

        using (var disjoint = new AndIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "x"),
               }))
        {
            Program.Check("AndIter: 无交集为空", !disjoint.Next() && disjoint.Current == -1);
        }

        using (var none = new AndIter(Array.Empty<IDocIter>()))
        {
            Program.Check("AndIter: 空 children 视为无匹配",
                !none.Next() && !none.Advance(0) && none.Current == -1);
        }
    }

    private static void AndIterLeapfrogAcrossBlocks()
    {
        // 300 doc / 3 块；q 从 150 起。leapfrog 应整块跳过 p 的前两块。
        var p = new PostingEntry[300];
        var q = new PostingEntry[150];
        for (var i = 0; i < p.Length; i++) p[i] = new PostingEntry(i, 1, new[] { i });
        for (var i = 0; i < q.Length; i++) q[i] = new PostingEntry(150 + i, 1, new[] { i });

        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("p", p),
            ("q", q),
        });

        using var and = new AndIter(new IDocIter[]
        {
            new TermIter(dict, postings, FieldId.Title, "p"),
            new TermIter(dict, postings, FieldId.Title, "q"),
        });
        var docs = Drain(and);
        Program.Check("AndIter: leapfrog 跨块命中 150..299",
            docs.Count == 150 && docs[0] == 150 && docs[^1] == 299,
            $"{docs.Count}/{docs.FirstOrDefault()}/{docs.LastOrDefault()}");

        // 稀疏交集：r={5,200,250} ∩ s={200,300} = {200}。
        var (dict2, postings2) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("r", EntriesNoPos(5, 200, 250)),
            ("s", EntriesNoPos(200, 300)),
        });
        using var sparse = new AndIter(new IDocIter[]
        {
            new TermIter(dict2, postings2, FieldId.Title, "r"),
            new TermIter(dict2, postings2, FieldId.Title, "s"),
        });
        var jumped = Drain(sparse);
        Program.Check("AndIter: leapfrog 稀疏交集 = {200}", Show(jumped) == "200", Show(jumped));
    }

    private static void OrIterUnion()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", Entries((0, 1), (3, 1), (7, 1))),
            ("b", Entries((1, 1), (3, 1), (4, 1))),
            ("c", Entries((3, 1), (9, 1))),
        });

        using (var or = new OrIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "b"),
                   new TermIter(dict, postings, FieldId.Title, "c"),
               }))
        {
            var docs = Drain(or);
            Program.Check("OrIter: 并集去重且递增", Show(docs) == "0,1,3,4,7,9", Show(docs));
            Program.Check("OrIter: Advance(-1) 抛异常",
                Throws<ArgumentOutOfRangeException>(() => or.Advance(-1)));
        }

        // 同一 term 重复两次：仍只产出一次。
        using (var dup = new OrIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "a"),
               }))
        {
            var docs = Drain(dup);
            Program.Check("OrIter: 重复子迭代器去重", Show(docs) == "0,3,7", Show(docs));
        }

        using (var jump = new OrIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "c"),
               }))
        {
            Program.Check("OrIter: Advance(5) → 7", jump.Advance(5) && jump.Current == 7,
                jump.Current.ToString());
            Program.Check("OrIter: Positions 为空", jump.Positions().IsEmpty);
        }

        using (var none = new OrIter(Array.Empty<IDocIter>()))
            Program.Check("OrIter: 空 children 为空迭代器",
                !none.Next() && !none.Advance(0) && none.Current == -1);
    }

    private static void PrefixIterExpansion()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("ta", EntriesNoPos(0, 5)),
            ("tb", EntriesNoPos(1, 5)),
            ("tc", EntriesNoPos(2)),
            ("td", EntriesNoPos(3)),
            ("te", EntriesNoPos(4)),
            ("zz", EntriesNoPos(6)),
        });

        using (var truncated = new PrefixIter(dict, postings, FieldId.Title, "t", 2))
        {
            var docs = Drain(truncated);
            Program.Check("PrefixIter: maxExp=2 展开前两条", Show(docs) == "0,1,5", Show(docs));
            Program.Check("PrefixIter: maxExp=2 置 Truncated", truncated.Truncated);
        }

        using (var exact = new PrefixIter(dict, postings, FieldId.Title, "t", 5))
        {
            var docs = Drain(exact);
            Program.Check("PrefixIter: 恰好 maxExp 条不算截断",
                Show(docs) == "0,1,2,3,4,5" && !exact.Truncated,
                $"{Show(docs)}/{exact.Truncated}");
        }

        using (var roomy = new PrefixIter(dict, postings, FieldId.Title, "t", 10))
            Program.Check("PrefixIter: 余量充足时不截断", !roomy.Truncated);

        using (var zero = new PrefixIter(dict, postings, FieldId.Title, "t", 0))
            Program.Check("PrefixIter: maxExp=0 直接截断且为空",
                !zero.Next() && zero.Truncated, zero.Truncated.ToString());

        using (var single = new PrefixIter(dict, postings, FieldId.Title, "zz", 4))
        {
            var docs = Drain(single);
            Program.Check("PrefixIter: 单命中前缀", Show(docs) == "6" && !single.Truncated, Show(docs));
        }

        using (var absent = new PrefixIter(dict, postings, FieldId.Title, "qw", 4))
            Program.Check("PrefixIter: 无命中为空迭代器",
                !absent.Next() && !absent.Truncated && absent.Current == -1);

        Program.Check("PrefixIter: maxExpansions < 0 抛异常",
            Throws<ArgumentOutOfRangeException>(
                () => new PrefixIter(dict, postings, FieldId.Title, "t", -1)));
        using (var negative = new PrefixIter(dict, postings, FieldId.Title, "t", 4))
            Program.Check("PrefixIter: Advance(-1) 抛异常",
                Throws<ArgumentOutOfRangeException>(() => negative.Advance(-1)));
    }

    private static void PositionsForwarded()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("w", new[]
            {
                new PostingEntry(2, 2, new[] { 0, 3 }),
                new PostingEntry(5, 3, new[] { 1, 4, 9 }),
                new PostingEntry(9, 1, new[] { 7 }),
            }),
            ("n", EntriesNoPos(2, 5, 9)),
        });

        using var term = new TermIter(dict, postings, FieldId.Title, "w");
        var ok = true;
        var detail = string.Empty;
        var expected = new Dictionary<int, int[]>
        {
            [2] = new[] { 0, 3 },
            [5] = new[] { 1, 4, 9 },
            [9] = new[] { 7 },
        };
        while (term.Next())
        {
            if (!expected.TryGetValue(term.Current, out var want)
                || !term.Positions().SequenceEqual(want))
            {
                ok = false;
                detail = $"{term.Current}:[{string.Join(',', term.Positions().ToArray())}]";
                break;
            }
        }
        Program.Check("Positions: TermIter 转发游标位置", ok, detail);

        using var noPos = new TermIter(dict, postings, FieldId.Title, "n");
        var empty = true;
        while (noPos.Next())
        {
            if (!noPos.Positions().IsEmpty) empty = false;
        }
        Program.Check("Positions: 无位置字段恒为空", empty);
    }

    /* ------------------------------------------------------------------ */

    private static void NextEqualsAdvance()
    {
        var rng = new XorShift(0x51D0C0DE);
        var (dict, postings, _) = BuildRandom(rng, 5, 400);

        CheckNextEqualsAdvance("TermIter", () => new TermIter(dict, postings, FieldId.Title, "t1"));
        CheckNextEqualsAdvance("TermIter(缺词)", () => new TermIter(dict, postings, FieldId.Title, "无"));
        CheckNextEqualsAdvance("AndIter", () => new AndIter(new IDocIter[]
        {
            new TermIter(dict, postings, FieldId.Title, "t0"),
            new TermIter(dict, postings, FieldId.Title, "t1"),
            new TermIter(dict, postings, FieldId.Title, "t2"),
        }));
        CheckNextEqualsAdvance("OrIter", () => new OrIter(new IDocIter[]
        {
            new TermIter(dict, postings, FieldId.Title, "t0"),
            new TermIter(dict, postings, FieldId.Title, "t3"),
            new TermIter(dict, postings, FieldId.Title, "t4"),
        }));
        CheckNextEqualsAdvance("PrefixIter", () => new PrefixIter(dict, postings, FieldId.Title, "t", 8));
    }

    private static void CheckNextEqualsAdvance(string label, Func<IDocIter> factory)
    {
        using var byNext = factory();
        using var byAdvance = factory();
        var same = true;
        var steps = 0;
        while (true)
        {
            var a = byNext.Next();
            var b = byAdvance.Advance(byAdvance.Current + 1);
            if (a != b || byNext.Current != byAdvance.Current)
            {
                same = false;
                break;
            }
            if (!a) break;
            steps++;
        }
        Program.Check($"{label}: Next ≡ Advance(Current+1)", same, $"steps={steps}");
    }

    private static void AdvanceMonotonicity()
    {
        var rng = new XorShift(0xBEEF1234);
        var (dict, postings, _) = BuildRandom(rng, 5, 400);

        CheckAdvanceMonotone("TermIter", () => new TermIter(dict, postings, FieldId.Title, "t2"), rng);
        CheckAdvanceMonotone("AndIter", () => new AndIter(new IDocIter[]
        {
            new TermIter(dict, postings, FieldId.Title, "t1"),
            new TermIter(dict, postings, FieldId.Title, "t3"),
        }), rng);
        CheckAdvanceMonotone("OrIter", () => new OrIter(new IDocIter[]
        {
            new TermIter(dict, postings, FieldId.Title, "t0"),
            new TermIter(dict, postings, FieldId.Title, "t2"),
        }), rng);
        CheckAdvanceMonotone("PrefixIter", () => new PrefixIter(dict, postings, FieldId.Title, "t", 8), rng);
    }

    /// <summary>随机目标 <c>Advance</c>：输出严格递增且 ≥ target，耗尽后恒 false。</summary>
    private static void CheckAdvanceMonotone(string label, Func<IDocIter> factory, XorShift rng)
    {
        using var iter = factory();
        var last = -1;
        var ok = true;
        var produced = 0;
        while (true)
        {
            var target = last + 1 + rng.NextInt(4);
            if (!iter.Advance(target)) break;
            if (iter.Current < target || iter.Current <= last)
            {
                ok = false;
                break;
            }
            last = iter.Current;
            produced++;
            if (produced > 10000) break; // 防御：真耗尽前不会到这里
        }

        var stable = iter.Current == -1 && !iter.Next() && !iter.Advance(0);
        Program.Check($"{label}: Advance 单调 + 耗尽后恒 false", ok && stable,
            $"produced={produced} last={last} current={iter.Current}");
    }

    /* ------------------------------------------------------------------ */

    private static void MatchesNaive()
    {
        var rng = new XorShift(0x0DDBEEF);
        var (dict, postings, terms) = BuildRandom(rng, 6, 500);

        IDocIter Term(string t) => new TermIter(dict, postings, FieldId.Title, t);

        var docs = Drain(Term("t0"));
        Program.Check("朴素比对: Term t0", Show(docs) == Show(NaiveTerm(terms, "t0")),
            Show(docs));

        docs = Drain(Term("t5"));
        Program.Check("朴素比对: Term t5", Show(docs) == Show(NaiveTerm(terms, "t5")),
            Show(docs));

        using (var and = new AndIter(new IDocIter[] { Term("t0"), Term("t1"), Term("t2") }))
        {
            docs = Drain(and);
            var naive = NaiveAnd(
                NaiveTerm(terms, "t0"), NaiveTerm(terms, "t1"), NaiveTerm(terms, "t2"));
            Program.Check("朴素比对: And t0∩t1∩t2", Show(docs) == Show(naive), Show(docs));
        }

        using (var or = new OrIter(new IDocIter[] { Term("t0"), Term("t2"), Term("t4"), Term("t4") }))
        {
            docs = Drain(or);
            var naive = NaiveOr(
                NaiveTerm(terms, "t0"), NaiveTerm(terms, "t2"), NaiveTerm(terms, "t4"));
            Program.Check("朴素比对: Or t0∪t2∪t4", Show(docs) == Show(naive), Show(docs));
        }

        using (var prefix = new PrefixIter(dict, postings, FieldId.Title, "t", 8))
        {
            docs = Drain(prefix);
            var naive = NaivePrefix(terms, "t");
            Program.Check("朴素比对: Prefix t", Show(docs) == Show(naive) && !prefix.Truncated,
                Show(docs));
        }

        using (var prefix = new PrefixIter(dict, postings, FieldId.Title, "t3", 8))
        {
            docs = Drain(prefix);
            var naive = NaivePrefix(terms, "t3");
            Program.Check("朴素比对: Prefix t3", Show(docs) == Show(naive), Show(docs));
        }
    }

    private static List<int> NaiveTerm(
        IReadOnlyList<(string Term, PostingEntry[] Entries)> terms, string term)
    {
        foreach (var (name, entries) in terms)
        {
            if (name == term) return entries.Select(e => e.Doc).ToList();
        }
        return new List<int>();
    }

    private static List<int> NaiveAnd(params List<int>[] lists)
    {
        if (lists.Length == 0) return new List<int>();
        var sets = lists.Select(l => new HashSet<int>(l)).ToArray();
        return sets[0].Where(d => sets.All(s => s.Contains(d))).OrderBy(d => d).ToList();
    }

    private static List<int> NaiveOr(params List<int>[] lists)
    {
        var set = new SortedSet<int>();
        foreach (var list in lists)
            foreach (var doc in list)
                set.Add(doc);
        return set.ToList();
    }

    private static List<int> NaivePrefix(
        IReadOnlyList<(string Term, PostingEntry[] Entries)> terms, string prefix)
    {
        var set = new SortedSet<int>();
        foreach (var (term, entries) in terms)
        {
            if (!term.StartsWith(prefix, StringComparison.Ordinal)) continue;
            foreach (var entry in entries) set.Add(entry.Doc);
        }
        return set.ToList();
    }

    /* ------------------------------------------------------------------ */

    /// <summary>确定性伪随机（xorshift32），避免 <see cref="Random"/> 的实现差异。</summary>
    private sealed class XorShift
    {
        private uint _state;

        public XorShift(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

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

    private static (TermDictReader Dict, PostingsReader Postings,
        List<(string Term, PostingEntry[] Entries)>) BuildRandom(
        XorShift rng, int termCount, int docSpace)
    {
        var terms = new List<(string Term, PostingEntry[] Entries)>();
        for (var t = 0; t < termCount; t++)
        {
            var docs = new List<int>();
            var doc = rng.NextInt(5);
            while (doc < docSpace)
            {
                docs.Add(doc);
                doc += 1 + rng.NextInt(9);
            }

            var entries = new PostingEntry[docs.Count];
            for (var i = 0; i < docs.Count; i++)
            {
                var tf = 1 + rng.NextInt(3);
                var positions = new int[tf];
                var pos = rng.NextInt(3);
                for (var j = 0; j < tf; j++)
                {
                    positions[j] = pos;
                    pos += 1 + rng.NextInt(4);
                }
                entries[i] = new PostingEntry(docs[i], tf, positions);
            }
            terms.Add(($"t{t}", entries));
        }

        var (dict, postings) = Build(FieldId.Title, terms);
        return (dict, postings, terms);
    }
}
