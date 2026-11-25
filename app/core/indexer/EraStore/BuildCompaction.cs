// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;

namespace Archoera.Indexer.EraStore;

/// <summary>全量构建后自动压缩参数（T3.15）。</summary>
internal sealed class BuildCompactionOptions
{
    /// <summary>低于该段数直接跳过；与增量合并触发线一致，正常增量库不会被额外重写。</summary>
    public int MinSegments = Merger.MergeTriggerSegments;

    /// <summary>
    /// 目标段数：达到（严格小于）后停止。默认同 <see cref="MinSegments"/>，
    /// 即「压回增量合并触发线以下」而不是追求单段——单段不是查询正确性的前提。
    /// </summary>
    public int TargetSegments = Merger.MergeTriggerSegments;

    /// <summary>最多合并轮数；每轮是一次独立提交（轮间不持有写句柄）。</summary>
    public int MaxRounds = 2;

    /// <summary>
    /// 本次压缩的总时间预算（秒）；0 = 不限时（运维 / 测试）。
    /// 调用方按构建耗时折算，保证全量构建的额外耗时占比可控。
    /// </summary>
    public double BudgetSeconds = 2.0;

    /// <summary>单轮最多归并的源 doc 数（单次提交的内存 / 规模上限）。</summary>
    public int MaxDocsPerRound = 50_000;

    /// <summary>
    /// 首轮的预估合并吞吐（doc/s），用于把剩余时间折算成单轮 doc 预算；
    /// 后续轮次用实测吞吐校正。T3.15 30k 语料实测：独立进程全段归并 ≈5.3k doc/s，
    /// 刚写完全量构建的进程 ≈3.6–4.8k doc/s，取 4.5k。
    /// </summary>
    public double InitialDocsPerSecond = 4_500;

    /// <summary>进度回调（round / merge 阶段）；异常向上抛，调用方据此中止。</summary>
    public Action<BuildCompactionProgress>? Progress = null;
}

/// <summary>压缩进度：阶段、当前段数、已归并 doc 数。</summary>
internal readonly record struct BuildCompactionProgress(string Phase, int Segments, int Docs);

/// <summary>全量构建后自动压缩结果。</summary>
internal sealed class BuildCompactionResult
{
    /// <summary>压缩前（全量构建产物）的物理段数。</summary>
    public int SegmentsBefore;

    /// <summary>压缩后的物理段数。</summary>
    public int SegmentsAfter;

    /// <summary>实际执行的合并轮数（0 = 未达到触发线或预算不足）。</summary>
    public int Rounds;

    /// <summary>所有轮次消耗的源段数之和。</summary>
    public int MergedSegments;

    /// <summary>压缩耗时（毫秒）。</summary>
    public long ElapsedMs;

    /// <summary>是否发生过合并。</summary>
    public bool Compacted => Rounds > 0;
}

/// <summary>
/// 全量构建后的**有界自动压缩**（T3.15）。
///
/// <para>为什么需要：全量构建按 4 MB 估算累加器落段，真实歌词库（30k 曲）会留下
/// ~286 个 ~105 doc 的小段；小段数量直接抬高查询侧逐段 df / bm25 汇总开销
/// （实测 465 段 p50 46.7 ms，压到 13 段后 13.6–15.0 ms）。手动 <c>era-index compact</c>
/// 能解决，但不该让用户自己跑。</para>
///
/// <para>边界：段数 &lt; <see cref="BuildCompactionOptions.MinSegments"/> 直接跳过；
/// 每轮一次 <see cref="Merger.Merge"/>（独立提交），轮数 ≤
/// <see cref="BuildCompactionOptions.MaxRounds"/>，总耗时以
/// <see cref="BuildCompactionOptions.BudgetSeconds"/> 为目标（按预估吞吐折算单轮 doc 数，
/// 实测偏差只落在单轮内，不会无界延长）；单轮另有
/// <see cref="BuildCompactionOptions.MaxDocsPerRound"/> 的规模上限。
/// 已在目标段数以下立即停止，不追求「合成一段」。</para>
///
/// <para>崩溃安全：本类只调用 <see cref="Merger.Merge"/>，后者在
/// <see cref="IndexFile.Commit"/> 前不改变任何已提交 Manifest / 提交槽。调用时机是
/// 全量构建**已经提交**（<c>CURRENT</c> 已指向新世代）之后、清理旧世代之前：
/// 压缩中断 / 抛异常时，新世代里「全量构建」那一版 Manifest 仍然有效，旧世代文件也还在，
/// 查询不受影响。压缩不做 CURRENT 切换（同一文件内追加段），因此不引入新的切换点。</para>
/// </summary>
internal static class BuildCompactor
{
    /// <summary>
    /// 对 <paramref name="indexPath"/> 指向的世代文件做有界压缩。
    /// 未达触发线返回 <c>Rounds = 0</c>；合并失败向上抛（调用方决定是否降级为「保留构建产物」）。
    /// </summary>
    public static BuildCompactionResult Compact(string indexPath, BuildCompactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        var opt = options ?? new BuildCompactionOptions();
        if (opt.MinSegments < 2) opt.MinSegments = 2;
        if (opt.TargetSegments < 1) opt.TargetSegments = 1;
        if (opt.MaxRounds < 0) opt.MaxRounds = 0;
        if (opt.MaxDocsPerRound < 2) opt.MaxDocsPerRound = 2;
        if (opt.InitialDocsPerSecond < 100) opt.InitialDocsPerSecond = 100;

        var path = IndexPaths.Resolve(indexPath);
        var result = new BuildCompactionResult();
        var before = StoreReader.TryReadManifest(path);
        if (before is null) return result; // 无有效提交：交给调用方按构建失败处理
        result.SegmentsBefore = CountSegments(before);
        result.SegmentsAfter = result.SegmentsBefore;
        if (result.SegmentsBefore < opt.MinSegments || opt.MaxRounds == 0) return result;

        var total = Stopwatch.StartNew();
        var docsPerSecond = opt.InitialDocsPerSecond;

        while (result.Rounds < opt.MaxRounds)
        {
            var manifest = StoreReader.TryReadManifest(path);
            if (manifest is null) break;
            var segmentCount = CountSegments(manifest);
            if (segmentCount < opt.MinSegments || segmentCount < opt.TargetSegments) break;

            var remainingSeconds = opt.BudgetSeconds <= 0
                ? double.PositiveInfinity
                : opt.BudgetSeconds - total.Elapsed.TotalSeconds;
            // 预留 10% 余量给提交 / Manifest 解析，避免刚好用尽预算后还要再付一次 fsync。
            if (!double.IsPositiveInfinity(remainingSeconds) && remainingSeconds <= 0.05) break;

            var docsBudget = opt.MaxDocsPerRound;
            if (!double.IsPositiveInfinity(remainingSeconds))
            {
                var affordable = (int)Math.Floor(remainingSeconds * 0.90 * docsPerSecond);
                docsBudget = Math.Min(docsBudget, affordable);
            }
            if (docsBudget < 2) break;

            // 全量构建的段都是「同一批小段」（30k 真实歌词约 105 doc/段），不存在增量库里
            // 那种需要保护的成熟大段，因此不启用 smallOnly（它的相对阈值 largest/4 会在
            // 段大小均匀时把整库过滤空）。排序仍按 dead → docs 升序，小段优先。
            var set = IncrementalUpdater.SelectMergeSet(manifest, docsBudget, smallOnly: false);
            if (set.Count < 2) break;
            var sourceDocs = DocsInSegments(manifest, set);

            opt.Progress?.Invoke(new BuildCompactionProgress("round", segmentCount, 0));
            var roundWatch = Stopwatch.StartNew();
            Merger.Merge(path, set, new MergeOptions
            {
                Gen = 0, // 当前 gen + 1
                Progress = p => opt.Progress?.Invoke(
                    new BuildCompactionProgress("merge-" + p.Phase, segmentCount, p.LiveDocs)),
            });
            roundWatch.Stop();
            result.Rounds++;
            result.MergedSegments += set.Count;

            // 实测吞吐校正（带平滑），让下一轮在不慢于预算的前提下尽量多吃。
            if (sourceDocs > 0 && roundWatch.Elapsed.TotalSeconds > 0.01)
            {
                var measured = sourceDocs / roundWatch.Elapsed.TotalSeconds;
                docsPerSecond = (docsPerSecond + measured) / 2;
            }
        }

        total.Stop();
        result.ElapsedMs = total.ElapsedMilliseconds;
        var after = StoreReader.TryReadManifest(path);
        result.SegmentsAfter = after is null ? result.SegmentsBefore : CountSegments(after);
        return result;
    }

    private static int CountSegments(Manifest manifest)
    {
        var set = new HashSet<ushort>();
        foreach (var section in manifest.Sections)
            if (section.Kind == SectionKind.DocStore) set.Add(section.SegmentId);
        return set.Count;
    }

    private static int DocsInSegments(Manifest manifest, IReadOnlyCollection<int> segmentIds)
    {
        var wanted = new HashSet<int>(segmentIds);
        var docs = 0;
        foreach (var section in manifest.Sections)
            if (section.Kind == SectionKind.DocStore && wanted.Contains(section.SegmentId))
                docs += (int)section.Docs;
        return docs;
    }
}
