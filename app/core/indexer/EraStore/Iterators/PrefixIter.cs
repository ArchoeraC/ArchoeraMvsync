// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore.Iterators;

/// <summary>
/// 前缀迭代器：<see cref="TermDictReader.ScanPrefix"/> 展开为多个 <see cref="TermIter"/>，
/// 再用 <see cref="OrIter"/> 归并（设计文档 §3.5.5）。
///
/// 展开数超过 <c>maxExpansions</c> 时截断并置 <see cref="Truncated"/> = true
/// （进 <c>explain</c>，不静默少召回）。无命中是空迭代器。
/// </summary>
internal sealed class PrefixIter : IDocIter
{
    private readonly OrIter _source;

    /// <summary>是否因 <c>maxExpansions</c> / <c>maxTermLen</c> 截断（有词条未展开）。</summary>
    public bool Truncated { get; }

    /// <summary>
    /// 展开 <paramref name="prefix"/> 下的词条并归并。
    /// <paramref name="maxTermLen"/> 是词条 UTF-8 字节数上限（防御性；格式本身限制 255B），
    /// 超长词条不参与展开并置 <see cref="Truncated"/>。
    /// </summary>
    public PrefixIter(
        TermDictReader dict,
        PostingsReader postings,
        FieldId field,
        string prefix,
        int maxExpansions,
        int maxTermLen = 256)
    {
        ArgumentNullException.ThrowIfNull(dict);
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(prefix);
        if (maxExpansions < 0) throw new ArgumentOutOfRangeException(nameof(maxExpansions));
        if (maxTermLen < 1) throw new ArgumentOutOfRangeException(nameof(maxTermLen));

        // 多扫一条即可判定「还有更多」，避免恰好 maxExpansions 条时误报截断。
        var probe = maxExpansions == int.MaxValue ? int.MaxValue : maxExpansions + 1;
        var infos = dict.ScanPrefix(field, prefix, probe);

        var truncated = infos.Count > maxExpansions;
        var children = new List<IDocIter>(Math.Min(infos.Count, maxExpansions));
        foreach (var info in infos)
        {
            if (children.Count >= maxExpansions) break;
            if (info.Key.Length - 1 > maxTermLen)
            {
                truncated = true;
                continue;
            }
            children.Add(new TermIter(postings, info));
        }

        Truncated = truncated;
        _source = new OrIter(children);
    }

    /// <inheritdoc />
    public int Current => _source.Current;

    /// <inheritdoc />
    public bool Next() => _source.Next();

    /// <inheritdoc />
    public bool Advance(int target) => _source.Advance(target);

    /// <inheritdoc />
    public ReadOnlySpan<int> Positions() => _source.Positions();

    /// <summary>递归释放子树（幂等）。</summary>
    public void Dispose() => _source.Dispose();
}
