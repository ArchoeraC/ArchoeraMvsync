// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore.Iterators;

/// <summary>
/// 段内 doc 迭代器契约（设计文档 §3.5.5）。
///
/// 统一语义：
/// <list type="bullet">
///   <item><see cref="Current"/> 是段内局部 doc_id；未开始（或已耗尽）为 -1；</item>
///   <item><c>Next()</c> 恒等价于 <c>Advance(Current + 1)</c>；</item>
///   <item><c>Advance(target)</c>：<c>Current &gt;= target</c> 时不动并返回 true，
///         否则前进到第一个 ≥ target 的 doc；耗尽返回 false；</item>
///   <item>已耗尽的迭代器继续 <c>Next()</c> / <c>Advance()</c> 恒返回 false，不抛；</item>
///   <item><c>Advance(target &lt; 0)</c> 抛 <see cref="ArgumentOutOfRangeException"/>；</item>
///   <item><see cref="Positions"/> 只对 <see cref="Current"/> 有效，前进后立即失效。</item>
/// </list>
///
/// 迭代器绑定单个段读取器，<b>不跨线程</b>；<c>Dispose()</c> 只递归释放子迭代器，
/// 不关闭 mmap（生命周期归索引句柄），且幂等。
/// </summary>
internal interface IDocIter : IDisposable
{
    /// <summary>当前段内 doc_id；未开始 / 耗尽为 -1。</summary>
    int Current { get; }

    /// <summary>前进一个 doc，等价于 <c>Advance(Current + 1)</c>；false 表示耗尽。</summary>
    bool Next();

    /// <summary>前进到 ≥ <paramref name="target"/> 的 doc；false 表示耗尽。</summary>
    bool Advance(int target);

    /// <summary>当前 doc 的位置序列（段内 token 序号递增）；无位置返回空。</summary>
    ReadOnlySpan<int> Positions();
}
