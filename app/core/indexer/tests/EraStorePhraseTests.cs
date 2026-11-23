// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P5：<see cref="PhraseIter"/> 短语校验、<c>seg_sep</c> 间隙、
/// 无位置字段自动降级为 <see cref="AndIter"/>（<c>positioned=false</c>）。
/// </summary>
internal static class EraStorePhraseTests
{
    public static void Run()
    {
        PhraseHits();
        PhraseMisses();
        SegSepGap();
        RepeatedTerm();
        JumpAndNext();
        DegradeWithoutPositions();
        ExplicitlyUnpositioned();
        Boundaries();
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

    /// <summary>带位置的一条 posting（位置必须严格递增）。</summary>
    private static PostingEntry P(int doc, params int[] positions) =>
        new(doc, positions.Length, positions);

    /// <summary>无位置的一条 posting（模拟未启用位置的字段 / 列）。</summary>
    private static PostingEntry NoPos(int doc) => new(doc, 1, null);

    private static PhraseIter Phrase(
        TermDictReader dict, PostingsReader postings, bool positioned, params string[] terms)
    {
        var iters = new IDocIter[terms.Length];
        for (var i = 0; i < terms.Length; i++)
            iters[i] = new TermIter(dict, postings, FieldId.Title, terms[i]);
        return new PhraseIter(iters, positioned);
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

    /// <summary>doc 0 的 token 序列为 [A,B,C]：短语 [A,B] / [A,B,C] 命中。</summary>
    private static void PhraseHits()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 0) }),
            ("b", new[] { P(0, 1) }),
            ("c", new[] { P(0, 2) }),
        });

        using (var iter = Phrase(dict, postings, true, "a", "b"))
        {
            Program.Check("PhraseIter: 构造后 Current = -1", iter.Current == -1,
                iter.Current.ToString());
            Program.Check("PhraseIter: 正向命中 doc 0",
                iter.Next() && iter.Current == 0 && iter.Positioned, $"{iter.Current}/{iter.Positioned}");
            Program.Check("PhraseIter: Positions 恒为空", iter.Positions().IsEmpty);
            Program.Check("PhraseIter: 命中后耗尽且 Current = -1",
                !iter.Next() && iter.Current == -1, iter.Current.ToString());
        }

        using (var three = Phrase(dict, postings, true, "a", "b", "c"))
        {
            var docs = Drain(three);
            Program.Check("PhraseIter: 三词短语命中 doc 0", Show(docs) == "0", Show(docs));
        }
    }

    /// <summary>[A,B,C] 对 [A,C]（不相邻）与 [B,A]（逆序）都不命中。</summary>
    private static void PhraseMisses()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 0) }),
            ("b", new[] { P(0, 1) }),
            ("c", new[] { P(0, 2) }),
        });

        using (var gap = Phrase(dict, postings, true, "a", "c"))
        {
            Program.Check("PhraseIter: [A,C] 不相邻不命中",
                !gap.Next() && gap.Current == -1, gap.Current.ToString());
        }

        using (var reversed = Phrase(dict, postings, true, "b", "a"))
        {
            Program.Check("PhraseIter: [B,A] 逆序不命中",
                !reversed.Next() && reversed.Current == -1, reversed.Current.ToString());
        }
    }

    /// <summary>A 在 0、B 在 2（跨段 seg_sep 占位）：[A,B] 不命中；差为 1 的 doc 仍命中。</summary>
    private static void SegSepGap()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 0), P(1, 0) }),
            ("b", new[] { P(0, 2), P(1, 1) }),
        });

        using var iter = Phrase(dict, postings, true, "a", "b");
        var docs = Drain(iter);
        Program.Check("PhraseIter: 位置差 ≥2（seg_sep）不命中、差 1 命中",
            Show(docs) == "1", Show(docs));
    }

    /// <summary>同一词重复（A 在 2、3）：[A,A] 命中；位置差 2 时 [A,A] 不命中。</summary>
    private static void RepeatedTerm()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 2, 3), P(1, 2, 4) }),
        });

        using (var twice = Phrase(dict, postings, true, "a", "a"))
        {
            var docs = Drain(twice);
            Program.Check("PhraseIter: 重复词 [A,A] 枚举所有起点", Show(docs) == "0", Show(docs));
        }

        using (var thrice = Phrase(dict, postings, true, "a", "a", "a"))
        {
            Program.Check("PhraseIter: [A,A,A] 只有两个位置时不命中", !thrice.Next());
        }
    }

    /// <summary>命中 doc 3 / 10（doc 7 不相邻）：Advance 跳跃，Next ≡ Advance(Current+1)。</summary>
    private static void JumpAndNext()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(3, 0), P(7, 0), P(10, 0) }),
            ("b", new[] { P(3, 1), P(7, 5), P(10, 1) }),
        });

        using (var iter = Phrase(dict, postings, true, "a", "b"))
        {
            Program.Check("PhraseIter: Advance(0) → 3", iter.Advance(0) && iter.Current == 3,
                iter.Current.ToString());
            Program.Check("PhraseIter: Advance(3) 已满足不动",
                iter.Advance(3) && iter.Current == 3, iter.Current.ToString());
            Program.Check("PhraseIter: Advance(4) 跳过非短语 doc 7 → 10",
                iter.Advance(4) && iter.Current == 10, iter.Current.ToString());
            Program.Check("PhraseIter: Advance(5) 不动（Current ≥ target）",
                iter.Advance(5) && iter.Current == 10, iter.Current.ToString());
            Program.Check("PhraseIter: Advance(11) 耗尽",
                !iter.Advance(11) && iter.Current == -1, iter.Current.ToString());
            Program.Check("PhraseIter: Advance(-1) 抛 ArgumentOutOfRangeException",
                Throws<ArgumentOutOfRangeException>(() => iter.Advance(-1)));
        }

        using (var byNext = Phrase(dict, postings, true, "a", "b"))
        using (var byAdvance = Phrase(dict, postings, true, "a", "b"))
        {
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
            Program.Check("PhraseIter: Next ≡ Advance(Current+1)", same, $"steps={steps}");
        }

        using (var drained = Phrase(dict, postings, true, "a", "b"))
        {
            var docs = Drain(drained);
            Program.Check("PhraseIter: 短语命中序列 = {3,10}", Show(docs) == "3,10", Show(docs));
        }
    }

    /// <summary>任一 term 无位置 → 永久降级，结果与 AndIter 逐条一致（Positioned=false）。</summary>
    private static void DegradeWithoutPositions()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 0), P(4, 0) }),
            ("b", new[] { NoPos(0), NoPos(4) }),
        });

        var naive = new List<int>();
        using (var and = new AndIter(new IDocIter[]
               {
                   new TermIter(dict, postings, FieldId.Title, "a"),
                   new TermIter(dict, postings, FieldId.Title, "b"),
               }))
        {
            naive = Drain(and);
        }

        using (var iter = Phrase(dict, postings, true, "a", "b"))
        {
            Program.Check("PhraseIter: 首次产出前 Positioned 按入参", iter.Positioned);
            var docs = Drain(iter);
            Program.Check("PhraseIter: 无位置字段自动降级为 AndIter",
                Show(docs) == Show(naive) && Show(docs) == "0,4", $"{Show(docs)} vs {Show(naive)}");
            Program.Check("PhraseIter: 降级后 Positioned=false", !iter.Positioned);
        }

        using (var iter = Phrase(dict, postings, false, "a", "b"))
        {
            var docs = Drain(iter);
            Program.Check("PhraseIter: positioned=false 与 AndIter 一致",
                Show(docs) == Show(naive) && !iter.Positioned, Show(docs));
        }
    }

    /// <summary>位置存在但 positioned=false：不校验相邻，等价 AndIter。</summary>
    private static void ExplicitlyUnpositioned()
    {
        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 0) }),
            ("b", new[] { P(0, 5) }),
        });

        using (var strict = Phrase(dict, postings, true, "a", "b"))
            Program.Check("PhraseIter: 位置差 5 且 positioned=true 不命中", !strict.Next());

        using (var loose = Phrase(dict, postings, false, "a", "b"))
        {
            var docs = Drain(loose);
            Program.Check("PhraseIter: positioned=false 不校验相邻",
                Show(docs) == "0" && !loose.Positioned, Show(docs));
        }
    }

    /// <summary>空 terms / 单词 terms 的边界语义。</summary>
    private static void Boundaries()
    {
        using (var empty = new PhraseIter(Array.Empty<IDocIter>(), true))
        {
            Program.Check("PhraseIter: 空 terms 为空迭代器",
                empty.Current == -1 && !empty.Next() && !empty.Advance(0));
            Program.Check("PhraseIter: 空 terms 的 Advance(-1) 抛异常",
                Throws<ArgumentOutOfRangeException>(() => empty.Advance(-1)));
            empty.Dispose();
            empty.Dispose();
            Program.Check("PhraseIter: Dispose 幂等", !empty.Next());
        }

        var (dict, postings) = Build(FieldId.Title, new (string, PostingEntry[])[]
        {
            ("a", new[] { P(0, 0), P(3, 1) }),
            ("b", new[] { NoPos(3) }),
        });

        using (var single = Phrase(dict, postings, true, "a"))
        {
            var docs = Drain(single);
            Program.Check("PhraseIter: 单词直接产出于 AndIter",
                Show(docs) == "0,3" && single.Positioned, $"{Show(docs)}/{single.Positioned}");
        }

        using (var single = Phrase(dict, postings, false, "a"))
            Program.Check("PhraseIter: 单词 Positioned 仍按入参", !single.Positioned);

        using (var missing = Phrase(dict, postings, true, "a", "不存在"))
            Program.Check("PhraseIter: 任一 term 未命中则无结果", !missing.Next());
    }
}
