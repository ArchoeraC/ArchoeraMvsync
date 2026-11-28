// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore.Iterators;

/// <summary>
/// 并集迭代器（小顶堆归并，设计文档 §3.5.5）。
///
/// 堆顶是最小 doc；同一 doc 的多个子迭代器只产出一次——产出前把重复者各自
/// <see cref="IDocIter.Next"/> 一格再重新入堆。子迭代器耗尽后移出堆。
/// 空 children 是空迭代器（<see cref="Current"/> = -1，恒 false）。
///
/// <see cref="Positions"/> 恒为空（多词并集没有单一位置序列；短语走 <see cref="AndIter"/> 子迭代器）。
/// </summary>
internal sealed class OrIter : IDocIter
{
    private readonly IReadOnlyList<IDocIter> _children;
    private readonly bool[] _dead;
    private readonly int[] _heap;
    private int _heapCount;

    private int _current = -1;
    private bool _exhausted;
    private bool _disposed;

    public OrIter(IReadOnlyList<IDocIter> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children;
        _dead = new bool[children.Count];
        _heap = new int[children.Count];

        Rebuild();
        if (_heapCount == 0) _exhausted = true;
    }

    /// <inheritdoc />
    public int Current => _current;

    /// <inheritdoc />
    public bool Next() => Advance(_current + 1);

    /// <inheritdoc />
    public bool Advance(int target)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        if (_current >= target) return true;
        if (_exhausted) return false;

        // 只抬堆顶那些仍落后于 target 的子迭代器：堆按 Current 升序，落后的一定在
        // 堆顶连续出现。旧实现每次 Advance 线性扫全部 children 并 Rebuild（O(K)），
        // 前缀展开 1 万词条时单次查询要扫 300M 次（T3.14 的 dfs 热点）。
        while (_heapCount > 0)
        {
            var index = _heap[0];
            if (_children[index].Current >= target) break;
            if (_children[index].Advance(target)) SiftDown(0);
            else { _dead[index] = true; PopMin(); }
        }

        if (_heapCount == 0)
        {
            _exhausted = true;
            _current = -1;
            return false;
        }

        var minDoc = _children[_heap[0]].Current;

        // 同 doc 的重复者先各自前进一格，保证同一 doc 只产出一次。
        while (_heapCount > 0 && _children[_heap[0]].Current == minDoc)
        {
            var index = PopMin();
            if (_children[index].Next()) Push(index);
            else _dead[index] = true;
        }

        _current = minDoc;
        return true;
    }

    /// <inheritdoc />
    public ReadOnlySpan<int> Positions() => ReadOnlySpan<int>.Empty;

    /// <summary>递归释放子迭代器（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = 0; i < _children.Count; i++)
            _children[i].Dispose();
    }

    /* ------------------------------------------------------------------ */

    /// <summary>按「Current 升序，同 doc 按子迭代器下标」重建堆。</summary>
    private void Rebuild()
    {
        var n = 0;
        for (var i = 0; i < _children.Count; i++)
        {
            if (!_dead[i]) _heap[n++] = i;
        }
        _heapCount = n;
        for (var i = (n >> 1) - 1; i >= 0; i--)
            SiftDown(i);
    }

    private void Push(int index)
    {
        _heap[_heapCount++] = index;
        SiftUp(_heapCount - 1);
    }

    private int PopMin()
    {
        var root = _heap[0];
        _heap[0] = _heap[--_heapCount];
        if (_heapCount > 0) SiftDown(0);
        return root;
    }

    private void SiftUp(int at)
    {
        while (at > 0)
        {
            var parent = (at - 1) >> 1;
            if (Compare(_heap[parent], _heap[at]) <= 0) return;
            Swap(parent, at);
            at = parent;
        }
    }

    private void SiftDown(int at)
    {
        while (true)
        {
            var left = (at << 1) + 1;
            if (left >= _heapCount) return;
            var smallest = left;
            var right = left + 1;
            if (right < _heapCount && Compare(_heap[right], _heap[left]) < 0) smallest = right;
            if (Compare(_heap[at], _heap[smallest]) <= 0) return;
            Swap(at, smallest);
            at = smallest;
        }
    }

    private int Compare(int a, int b)
    {
        var ca = _children[a].Current;
        var cb = _children[b].Current;
        if (ca != cb) return ca < cb ? -1 : 1;
        return a.CompareTo(b);
    }

    private void Swap(int a, int b) => (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
}
