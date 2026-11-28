// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 朴素查询 AST（P9 参考实现侧）。节点语义与 EraStore 迭代器一一对应：
/// <see cref="NaiveTerm"/> ↔ <c>TermIter</c>、<see cref="NaivePrefix"/> ↔ <c>PrefixIter</c>、
/// <see cref="NaiveAnd"/> ↔ <c>AndIter</c>、<see cref="NaiveOr"/> ↔ <c>OrIter</c>、
/// <see cref="NaivePhrase"/> ↔ <c>PhraseIter</c>（位置严格相邻）。
/// </summary>
internal abstract record NaiveQuery;

/// <summary>段内精确词条（字段族 + 词条文本）。</summary>
internal sealed record NaiveTerm(FieldId Field, string Term) : NaiveQuery;

/// <summary>段内前缀展开：按词条 UTF-8 字节序取前 <paramref name="MaxExpansions"/> 条。</summary>
internal sealed record NaivePrefix(FieldId Field, string Prefix, int MaxExpansions) : NaiveQuery;

/// <summary>交集（空 children 视为无匹配，与 <c>AndIter</c> 一致）。</summary>
internal sealed record NaiveAnd(IReadOnlyList<NaiveQuery> Children) : NaiveQuery;

/// <summary>并集（去重后升序）。</summary>
internal sealed record NaiveOr(IReadOnlyList<NaiveQuery> Children) : NaiveQuery;

/// <summary>短语：同字段词条按数组顺序、位置差恰为 1。</summary>
internal sealed record NaivePhrase(FieldId Field, IReadOnlyList<string> Terms) : NaiveQuery;

/// <summary>
/// P9 参考实现：直接从 <see cref="AnalyzedDoc.Tokens"/> 构建
/// <c>(FieldId, term) → [(doc, positions)]</c>，不经过 EraStore 的任何编码 / 解码。
///
/// 与构建器保持同构的两条细节：
/// <list type="bullet">
///   <item>空 token 不产倒排，但仍占用位置下标（builder 的 <c>SegmentAccumulator</c> 同语义）；</item>
///   <item>前缀展开按词条 UTF-8 字节序截断（等价 <c>TermDictReader.ScanPrefix</c>）。</item>
/// </list>
/// 每个 <see cref="Eval(int, NaiveQuery)"/> 返回段内升序 doc 列表（即"文档序列"，不含任何排序打分）。
/// </summary>
internal sealed class NaiveIndex
{
    private sealed class Segment
    {
        public readonly Dictionary<(FieldId Field, string Term), List<(int Doc, int[] Positions)>> Postings = new();
        public readonly Dictionary<FieldId, List<(string Term, byte[] Bytes)>> Terms = new();
    }

    private readonly Segment[] _segments;

    /// <param name="segments">按段分组的 doc token 表（下标即段号，段内下标即 doc_id）。</param>
    public NaiveIndex(IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<FieldId, string[]>>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = new Segment[segments.Count];
        for (var i = 0; i < segments.Count; i++) _segments[i] = BuildSegment(segments[i]);
    }

    /// <summary>段数。</summary>
    public int SegmentCount => _segments.Length;

    /// <summary>求值一条查询，返回段内升序 doc 列表。</summary>
    public List<int> Eval(int segment, NaiveQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return EvalIn(SegmentAt(segment), query);
    }

    /// <summary>
    /// <paramref name="doc"/> 在 <paramref name="field"/> 的 <paramref name="term"/> 位置序列；
    /// 不存在（含无位置）返回空数组。位置顺序与写入侧一致（token 数组下标）。
    /// </summary>
    public int[] Positions(int segment, int doc, FieldId field, string term)
    {
        var seg = SegmentAt(segment);
        if (seg.Postings.TryGetValue((field, term), out var list))
        {
            foreach (var (d, positions) in list)
                if (d == doc) return positions;
        }
        return Array.Empty<int>();
    }

    /* ------------------------------------------------------------------ */

    private Segment SegmentAt(int segment)
    {
        if ((uint)segment >= (uint)_segments.Length)
            throw new ArgumentOutOfRangeException(nameof(segment));
        return _segments[segment];
    }

    private static Segment BuildSegment(IReadOnlyList<IReadOnlyDictionary<FieldId, string[]>> docs)
    {
        var seg = new Segment();
        for (var doc = 0; doc < docs.Count; doc++)
        {
            foreach (var (field, tokens) in docs[doc])
            {
                if (!seg.Terms.TryGetValue(field, out var terms))
                {
                    terms = new List<(string Term, byte[] Bytes)>();
                    seg.Terms[field] = terms;
                }

                for (var pos = 0; pos < tokens.Length; pos++)
                {
                    var text = tokens[pos];
                    if (text.Length == 0) continue; // 空 token 跳过但仍占位置
                    if (!seg.Postings.TryGetValue((field, text), out var list))
                    {
                        list = new List<(int Doc, int[] Positions)>();
                        seg.Postings[(field, text)] = list;
                        terms.Add((text, Encoding.UTF8.GetBytes(text)));
                    }
                    AppendPosition(list, doc, pos);
                }
            }
        }

        foreach (var terms in seg.Terms.Values)
            terms.Sort(static (a, b) => a.Bytes.AsSpan().SequenceCompareTo(b.Bytes));
        return seg;
    }

    /// <summary>同 doc 多个位置合并进同一条 posting（doc 升序、位置升序）。</summary>
    private static void AppendPosition(List<(int Doc, int[] Positions)> list, int doc, int pos)
    {
        if (list.Count > 0 && list[^1].Doc == doc)
        {
            var last = list[^1];
            var grown = new int[last.Positions.Length + 1];
            last.Positions.CopyTo(grown, 0);
            grown[^1] = pos;
            list[^1] = (doc, grown);
            return;
        }
        list.Add((doc, new[] { pos }));
    }

    private static List<int> EvalIn(Segment seg, NaiveQuery query) => query switch
    {
        NaiveTerm term => TermDocs(seg, term.Field, term.Term),
        NaivePrefix prefix => PrefixDocs(seg, prefix.Field, prefix.Prefix, prefix.MaxExpansions),
        NaiveAnd and => AndDocs(seg, and.Children),
        NaiveOr or => OrDocs(seg, or.Children),
        NaivePhrase phrase => PhraseDocs(seg, phrase.Field, phrase.Terms),
        _ => throw new ArgumentException($"未知查询节点 {query.GetType().Name}", nameof(query)),
    };

    private static List<int> TermDocs(Segment seg, FieldId field, string term)
    {
        var docs = new List<int>();
        if (!seg.Postings.TryGetValue((field, term), out var list)) return docs;
        foreach (var (doc, _) in list) docs.Add(doc);
        return docs;
    }

    private static List<int> PrefixDocs(Segment seg, FieldId field, string prefix, int maxExpansions)
    {
        var docs = new SortedSet<int>();
        if (maxExpansions <= 0 || prefix.Length == 0) return new List<int>();
        if (!seg.Terms.TryGetValue(field, out var terms)) return new List<int>();

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var expanded = 0;
        foreach (var (term, bytes) in terms)
        {
            var cmp = bytes.AsSpan().SequenceCompareTo(prefixBytes);
            if (cmp < 0) continue;
            // 列表按 UTF-8 字节序：一旦越过前缀区间，后续词条都不可能命中。
            if (!bytes.AsSpan().StartsWith(prefixBytes)) break;
            if (expanded >= maxExpansions) break;
            expanded++;
            foreach (var (doc, _) in seg.Postings[(field, term)]) docs.Add(doc);
        }
        return docs.ToList();
    }

    private static List<int> AndDocs(Segment seg, IReadOnlyList<NaiveQuery> children)
    {
        if (children.Count == 0) return new List<int>();
        var sets = new List<HashSet<int>>(children.Count);
        foreach (var child in children) sets.Add(new HashSet<int>(EvalIn(seg, child)));

        var docs = new List<int>();
        foreach (var doc in sets[0])
            if (sets.TrueForAll(set => set.Contains(doc))) docs.Add(doc);
        docs.Sort();
        return docs;
    }

    private static List<int> OrDocs(Segment seg, IReadOnlyList<NaiveQuery> children)
    {
        var docs = new SortedSet<int>();
        foreach (var child in children)
            foreach (var doc in EvalIn(seg, child)) docs.Add(doc);
        return docs.ToList();
    }

    private static List<int> PhraseDocs(Segment seg, FieldId field, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return new List<int>();

        var perOccurrence = new List<Dictionary<int, int[]>>(terms.Count);
        foreach (var term in terms) perOccurrence.Add(PositionsByDoc(seg, field, term));

        var docs = new List<int>();
        foreach (var doc in perOccurrence[0].Keys)
        {
            var present = true;
            for (var i = 1; i < perOccurrence.Count && present; i++)
                present = perOccurrence[i].ContainsKey(doc);
            if (present && MatchesPhrase(perOccurrence, doc)) docs.Add(doc);
        }
        docs.Sort();
        return docs;
    }

    private static Dictionary<int, int[]> PositionsByDoc(Segment seg, FieldId field, string term)
    {
        var map = new Dictionary<int, int[]>();
        if (!seg.Postings.TryGetValue((field, term), out var list)) return map;
        foreach (var (doc, positions) in list) map[doc] = positions;
        return map;
    }

    /// <summary>
    /// 与 <c>PhraseIter</c> 同构：任一位置序列为空时降级为纯交集（视为命中）；
    /// 否则对首词每个起点，后续词必须恰好在 <c>cur + 1</c> 处首次出现。
    /// </summary>
    private static bool MatchesPhrase(List<Dictionary<int, int[]>> per, int doc)
    {
        foreach (var map in per)
            if (map[doc].Length == 0) return true;
        if (per.Count == 1) return true;

        foreach (var start in per[0][doc])
        {
            var cur = start;
            var ok = true;
            for (var i = 1; i < per.Count; i++)
            {
                var next = -1;
                foreach (var pos in per[i][doc])
                {
                    if (pos > cur)
                    {
                        next = pos;
                        break;
                    }
                }
                if (next != cur + 1)
                {
                    ok = false;
                    break;
                }
                cur = next;
            }
            if (ok) return true;
        }
        return false;
    }
}
