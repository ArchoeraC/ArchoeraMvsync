// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore.Iterators;

/// <summary>
/// 短语迭代器（设计文档 §3.5.5.1）：先用 <see cref="AndIter"/> 求交，再逐 doc 校验各 term
/// 的位置是否严格相邻（段内 token 序号差恰为 1）。
///
/// 位置列表来自各子迭代器 <see cref="IDocIter.Positions"/>：对 <c>posLists[0]</c> 的每个
/// <c>start</c>，依次在后续列表中找第一个 <c>&gt; cur</c> 的位置，必须恰为 <c>cur + 1</c>。
/// <c>seg_sep</c> 占一个位置，因此跨段相邻差 ≥2，天然不命中。
///
/// 降级：<paramref name="positioned"/> = false，或任一子迭代器无位置（返回空位置序列）时，
/// 不再校验位置、等价 <see cref="AndIter"/>，并把 <see cref="Positioned"/> 置 false
/// （进 <c>explain</c>，不允许悄悄改变语义）。单词 / 空词按 <see cref="AndIter"/> 语义产出。
/// </summary>
internal sealed class PhraseIter : IDocIter
{
    private readonly IReadOnlyList<IDocIter> _terms;
    private readonly AndIter _and;
    private bool _positioned;
    private bool _disposed;

    /// <summary>
    /// <paramref name="terms"/> 按短语顺序排列（同一词重复出现时可用不同实例）；
    /// <paramref name="positioned"/> 表示来源字段是否带位置，false 时直接按纯交集处理。
    /// </summary>
    public PhraseIter(IReadOnlyList<IDocIter> terms, bool positioned)
    {
        ArgumentNullException.ThrowIfNull(terms);
        _terms = terms;
        _and = new AndIter(terms);
        _positioned = positioned;
    }

    /// <summary>实际是否按位置校验；无位置字段在首次产出后降级为 false。</summary>
    public bool Positioned => _positioned;

    /// <inheritdoc />
    public int Current => _and.Current;

    /// <inheritdoc />
    public bool Next() => Advance(Current + 1);

    /// <inheritdoc />
    public bool Advance(int target)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        if (_and.Current >= target) return true;
        if (!_and.Advance(target)) return false;

        while (!MatchesPhrase())
        {
            if (!_and.Next()) return false;
        }
        return true;
    }

    /// <inheritdoc />
    public ReadOnlySpan<int> Positions() => ReadOnlySpan<int>.Empty;

    /// <summary>释放 <see cref="AndIter"/>（它负责递归释放子迭代器）；幂等。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _and.Dispose();
    }

    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 校验 <see cref="AndIter"/> 当前 doc 是否满足短语；无位置字段时永久降级并视为命中。
    /// </summary>
    private bool MatchesPhrase()
    {
        if (!_positioned || _terms.Count <= 1) return true;

        // 位置可用性：同一词条的位置要么全有要么全无，命中一次即可永久降级。
        for (var i = 0; i < _terms.Count; i++)
        {
            if (_terms[i].Positions().IsEmpty)
            {
                _positioned = false;
                return true;
            }
        }

        var first = _terms[0].Positions();
        for (var s = 0; s < first.Length; s++)
        {
            var cur = first[s];
            var ok = true;
            for (var i = 1; i < _terms.Count; i++)
            {
                if (!TryNextAdjacent(_terms[i].Positions(), cur, out var next))
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

    /// <summary>在严格递增的 <paramref name="list"/> 中找第一个 <c>&gt; cur</c> 的位置，要求恰为 <c>cur + 1</c>。</summary>
    private static bool TryNextAdjacent(ReadOnlySpan<int> list, int cur, out int next)
    {
        var lo = 0;
        var hi = list.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (list[mid] <= cur) lo = mid + 1;
            else hi = mid;
        }

        if (lo < list.Length && list[lo] == cur + 1)
        {
            next = list[lo];
            return true;
        }
        next = 0;
        return false;
    }
}
