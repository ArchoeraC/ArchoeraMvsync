// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore.Iterators;

/// <summary>
/// 交集迭代器（leapfrog，设计文档 §3.5.5）：取所有子迭代器 <see cref="IDocIter.Current"/>
/// 的最大值 <c>m</c>，把 <c>&lt; m</c> 的子迭代器 <c>Advance(m)</c>；
/// 全部相等才产出，任一子迭代器耗尽即整体耗尽。
///
/// 构造后 <see cref="Current"/> = -1，首次 <see cref="Next"/> 找最小共同 doc。
/// 空 children 视为<b>无匹配</b>（永远返回 false），而不是全集。
///
/// <see cref="Positions"/> 恒为空：短语由子迭代器各自取位置（P5）。
/// </summary>
internal sealed class AndIter : IDocIter
{
    private readonly IReadOnlyList<IDocIter> _children;
    private int _current = -1;
    private bool _exhausted;
    private bool _disposed;

    public AndIter(IReadOnlyList<IDocIter> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children;
        if (children.Count == 0) _exhausted = true;
    }

    /// <inheritdoc />
    public int Current => _current;

    /// <inheritdoc />
    public bool Next() => Advance(_current + 1);

    /// <inheritdoc />
    public bool Advance(int target)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        if (_exhausted) return false;
        if (_current >= target) return true;

        var t = target;
        while (true)
        {
            var max = int.MinValue;
            for (var i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                if (!child.Advance(t)) return Fail();
                if (child.Current > max) max = child.Current;
            }

            var aligned = true;
            for (var i = 0; i < _children.Count; i++)
            {
                if (_children[i].Current != max)
                {
                    aligned = false;
                    break;
                }
            }
            if (aligned)
            {
                _current = max;
                return true;
            }

            t = max;
        }
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

    private bool Fail()
    {
        _exhausted = true;
        _current = -1;
        return false;
    }
}
