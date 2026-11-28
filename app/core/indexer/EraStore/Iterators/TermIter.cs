// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore.Iterators;

/// <summary>
/// 单 term 迭代器：词典精确命中后包装 <see cref="PostingsCursor"/>。
/// 词典未命中时是空迭代器（<see cref="Current"/> = -1，<see cref="Next"/> /
/// <see cref="Advance"/> 恒 false）。
///
/// <see cref="Advance"/> 的整块跳过由游标按块头 <c>last_doc</c> 完成（设计文档 §3.5.5）。
/// </summary>
internal sealed class TermIter : IDocIter
{
    private readonly PostingsCursor? _cursor;

    /// <summary>该 term 在词典中的文档频率（df）；词典未命中时为 0。</summary>
    public int Df { get; }

    public TermIter(TermDictReader dict, PostingsReader postings, FieldId field, string term)
    {
        ArgumentNullException.ThrowIfNull(dict);
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(term);

        if (dict.TryFind(field, term, out var info))
        {
            _cursor = postings.Open(info.PostOffset, info.Df);
            Df = (int)info.Df;
        }
    }

    /// <summary>直接以已命中的词典条目建游标（供 <see cref="PrefixIter"/> 复用，避免二次查找）。</summary>
    internal TermIter(PostingsReader postings, TermInfo info)
    {
        ArgumentNullException.ThrowIfNull(postings);
        _cursor = postings.Open(info.PostOffset, info.Df);
        Df = (int)info.Df;
    }

    /// <inheritdoc />
    public int Current => _cursor?.CurrentDoc ?? -1;

    /// <inheritdoc />
    public bool Next() => Advance(Current + 1);

    /// <inheritdoc />
    public bool Advance(int target)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        return _cursor is not null && _cursor.Advance(target);
    }

    /// <inheritdoc />
    public ReadOnlySpan<int> Positions() =>
        _cursor is null ? ReadOnlySpan<int>.Empty : _cursor.CurrentPositions;

    /// <summary>游标无可释放资源，仅满足契约（幂等）。</summary>
    public void Dispose()
    {
    }
}
