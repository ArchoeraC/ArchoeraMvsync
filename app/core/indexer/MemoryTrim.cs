// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>
/// 一次性内存回收（T3.14 <c>rss_query</c>）。
///
/// <para>查询侧分析器（ToolGood.Words 拼音词库 + Unihan 释义表）在首次中文分析时
/// 一次性分配几十 MB 临时对象；常规 GC 只回收对象、不归还已提交的堆段，NativeAOT
/// 进程因此长期保持 ≈100 MB 匿名 RSS。实测 <see cref="GCCollectionMode.Aggressive"/>
/// 会压缩并归还这些段（NativeAOT / framework-dependent 都是），把首次加载后的
/// 匿名 RSS 降到 ≈55–60 MB。</para>
///
/// <para>触发方式是「惰性表加载后置位，查询结束统一回收」：一次查询可能连续加载
/// 拼音 / 释义 / 兼容表，只在响应写完后做**一次**回收；之后普通查询不再强制 GC
/// （避免给每查增加 ≈8 ms 的抖动）。构建路径在收尾执行一次 <see cref="TrimOnce"/>，
/// 归还分析 30k 曲产生的暂存堆。</para>
/// </summary>
internal static class MemoryTrim
{
    private static int _pending;
    private static int _sinceTrim;

    /// <summary>轻查询每 16 条回收一次：摊薄 ≈0.5 ms/查，把堆高水位钉在基线附近。</summary>
    private const int QueryTrimInterval = 16;

    /// <summary>惰性词库加载完成：标记「本查询结束后需要回收一次」。</summary>
    public static void Request() => Interlocked.Exchange(ref _pending, 1);

    /// <summary>查询结束调用：词库加载或累计 16 条查询时回收一次，否则零开销返回。</summary>
    public static void AfterQuery()
    {
        if (Interlocked.Exchange(ref _pending, 0) != 0)
        {
            Volatile.Write(ref _sinceTrim, 0);
            TrimCore();
            return;
        }
        if (Interlocked.Increment(ref _sinceTrim) >= QueryTrimInterval)
        {
            Volatile.Write(ref _sinceTrim, 0);
            TrimCore();
        }
    }

    /// <summary>
    /// 构建收尾回收：**每次**构建 / 增量后都执行（不再是每进程一次）。
    ///
    /// 索引更新会在进程内产生大量临时对象（脏集 / 文档 / 词典累加器 / 旧世代 mmap 视图）。
    /// NativeAOT 的 GC 即使回收对象也不一定立刻把堆段归还 OS；如果只在第一次构建后
    /// trim，后续更新的高水位会一层层叠加，表现为「每次更新索引 RSS 上涨」。
    /// 这里每次构建结束都做一次 compacting GC（≈10–20 ms），把构建期峰值还回去。
    /// </summary>
    public static void TrimAfterBuild() => TrimCore();

    /// <summary>兼容旧调用名：构建收尾回收。</summary>
    public static void TrimOnce() => TrimAfterBuild();

    /// <summary>测试接缝：允许下一次调用重新触发。</summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _sinceTrim, 0);
    }

    private static void TrimCore()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
                blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
                blocking: true, compacting: true);
        }
        catch (Exception)
        {
            // NativeAOT 之外的回收器可能拒绝 Aggressive；不影响查询正确性。
        }
    }
}
