// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Archoera.Lyrics;
using Archoera.Text;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>阶段一（脏集探测）轻量行：仅 <c>id/path/file_mtime</c>，**不含歌词 / 元数据大列**。</summary>
internal sealed class TrackProbe
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public long Mtime { get; init; }

    /// <summary><c>tracks.lrc_mtime</c>（scanner 写入的侧车 mtime；0 = 无侧车 / 未探测）。</summary>
    public long LrcMtime { get; init; }

    /// <summary><c>tracks.lrc_size</c>（scanner 写入的侧车聚合字节；0 = 无侧车 / 未探测）。</summary>
    public long LrcSize { get; init; }
}

/// <summary>曲库 <c>tracks</c> 行的分析期只读投影（阶段二按脏 id 取内容）。</summary>
internal sealed class TrackRecord
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public string ArtistText { get; init; } = "";
    public string AlbumText { get; init; } = "";
    public string Lyrics { get; init; } = "";
    public long Mtime { get; init; }

    /// <summary>主 <c>.lrc</c> 侧车**原始文本**（scanner 写入；空 = 无侧车）。</summary>
    public string LrcText { get; init; } = "";

    /// <summary>显式译文侧车 <c>&lt;name&gt;.trans.lrc</c> 原始文本（可空）。</summary>
    public string LrcTransText { get; init; } = "";

    /// <summary>显式音译侧车 <c>&lt;name&gt;.romaji.lrc</c> 原始文本（可空）。</summary>
    public string LrcRomajiText { get; init; } = "";

    /// <summary>侧车聚合 mtime（scanner 写入；与阶段一脏集判定同源）。</summary>
    public long LrcMtime { get; init; }

    /// <summary>侧车聚合字节大小（scanner 写入）。</summary>
    public long LrcSize { get; init; }
}

/// <summary>
/// 增量调度 + 分层分析 + 写 EraStore（设计文档 §3 / §4 / §6）。
///
/// 数据源：只读 <c>library.db</c> 的 <c>tracks</c> 表（scanner 写的源真值）；
/// 输出：<c>&lt;index 目录&gt;/search.era/</c>。**不修改 scanner / library.db**。
///
/// **两阶段**（设计文档 §2.2「增量 ≈ K 成正比」）：
/// <list type="number">
///   <item><b>脏集探测</b>：只读轻量列 <c>id/path/file_mtime</c>（不含 lyrics/title/artists/album），
///         结合既有 DocStore 状态与 <c>.lrc</c> <c>(mtime,size)</c> 判定脏 <c>track_id</c>；
///         全量模式所有 id 皆脏。</item>
///   <item><b>取内容 + 分析</b>：仅按脏 id 分块取完整列（<c>WHERE id IN (…)</c>，块大小
///         <see cref="ContentChunkSize"/>）并写入 EraStore；无脏曲零内容读取。</item>
/// </list>
/// 删除判定仍只用阶段一的 id 集合（<c>state</c> 中不在 <c>tracks</c> 的 <c>track_id</c>）。
/// 脏集规则（确定性，无 sleep / 轮询）：
/// <list type="bullet">
///   <item>新增/变更 = <c>track_id</c> 不在索引，或 <c>file_mtime ≠ indexed_mtime</c>；</item>
///   <item><c>.lrc</c> 侧车 <c>(mtime,size)</c> 与已索引不符（含子开关关→开 / 开→关的清理）；</item>
///   <item>Manifest 元数据版本 / analyzer 锚 / 拼音开关不一致 → 强制全量。</item>
/// </list>
/// 同步执行（run-to-completion），由调用方在后台线程调度；通过
/// <see cref="Action{T}"/> 上报进度、<see cref="CancellationToken"/> 取消。
/// </summary>
public sealed class IndexerEngine
{
    private const int ProgressEvery = 50;

    /// <summary>EraStore 索引目录名（与 search.db 同目录；内含 CURRENT + search-&lt;gen&gt;.eraindex）。</summary>
    internal const string EraStoreDirName = "search.era";

    /// <summary>阶段二按脏 id 取内容的单次分块大小（减少 SQL 往返；参数化、id 为 TEXT）。</summary>
    private const int ContentChunkSize = 500;

    /// <summary>全量 EraStore 构建的取内容分块（比增量更小：歌词常驻 + 分析结果同时占内存）。</summary>
    private const int EraBuildChunkSize = 200;

    /// <summary>
    /// 全量构建后自动压缩（<see cref="EraStore.BuildCompactor"/>）的时间预算占本次构建耗时的比例。
    ///
    /// <para>取值是「查询延迟 vs 全量构建耗时」的显式取舍，两组实测（30k / 286 段，
    /// 构建本体 ≈27 s，本机 SSD）：</para>
    /// <list type="bullet">
    /// <item>0.10（§8.0「构建不快于现状 10%」）：≈2.7 s → 180 段，查询 p50 ≈19 ms（不达门禁）。</item>
    /// <item>0.30（默认）：≈6.8 s → 2 段，查询 p50 ≈7.7 ms（达门禁），构建 +25%。</item>
    /// </list>
    ///
    /// <para>选 0.30 的理由：全量构建是**一次性、后台、启动预热**路径（重建 / 首次建库），
    /// 而查询延迟在每次搜索都可见；§8.0 的 10% 条款针对「去重不得让构建变慢」，不是
    /// 禁止一次有界的收尾整理。若某平台必须严守 10%，把本常量改回 0.10 即可，代价是
    /// 稳态段数停在 ≈180，需等后续增量构建的自动合并继续收敛。</para>
    /// </summary>
    private const double FullBuildCompactBudgetFraction = 0.30;

    /// <summary>自动压缩预算下限 / 上限（秒）；极小构建至少给 1 s，大构建最多 15 s。</summary>
    private const double FullBuildCompactMinBudgetSeconds = 1.0;
    private const double FullBuildCompactMaxBudgetSeconds = 15.0;

    /// <summary>自动压缩最多轮数（每轮一次独立提交，轮间不持有写句柄）。</summary>
    private const int FullBuildCompactMaxRounds = 2;

    /// <summary>
    /// 全量构建后自动去碎片的触发线（仅当自动压缩**未走新世代路径**、退化为同文件追加时
    /// 兜底）：garbage &gt; <c>0.20 × liveBytes</c>（且不少于
    /// <see cref="EraStore.Defragmenter.MinAutoGarbageBytes"/>）。S1 起正常压缩直接产出
    /// 只含活段体的新世代，不再进入本分支。
    /// </summary>
    private const double FullBuildDefragOverheadRatio = 0.20;

    private readonly Action<IndexerProgress>? _progress;

    /// <summary>
    /// 阶段二「取内容」SQL 的执行次数（分块查询计数）。
    /// 测试用：增量无脏曲时应为 0，证明未读取歌词 / 元数据大列。
    /// </summary>
    internal int ContentQueries { get; private set; }

    public IndexerEngine(Action<IndexerProgress>? progress = null)
    {
        _progress = progress;
    }

    /// <summary>
    /// 构建 / 增量更新 EraStore 索引（唯一写入路径）。
    /// </summary>
    /// <param name="libraryPath">源 <c>library.db</c> 路径（只读）。</param>
    /// <param name="indexPath">目标索引路径（<c>search.db</c> 仅作目录锚点）。</param>
    /// <param name="full">true = 强制全量；false = 增量（版本不符时自动升级为全量）。</param>
    public IndexerResult Run(
        string libraryPath, string indexPath, bool full, IndexerOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!File.Exists(libraryPath))
            throw new FileNotFoundException("源曲库不存在", libraryPath);

        // §5.5.6 基础表硬门禁：nfkc / accent_fold / variant_fold 缺一即拒绝构建。
        // 这三张表影响**所有**词元，缺了就不能「降级成可用的索引」——宁可报错让用户重装。
        var dataPack = DataPack.Current;
        dataPack.ThrowIfFoundationMissing();

        if (options.IndexLrc && !LibraryHasLrcColumns(libraryPath))
        {
            Log.LogWarn("indexer",
                "library.db 无 .lrc 侧车列（lyrics_lrc/lrc_mtime）：本次按「无侧车」构建。"
                + "请先运行一次媒体扫描（扫描器会把侧车解析入库），再重跑索引。");
        }

        var eraDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? ".", EraStoreDirName);
        var currentName = EraStore.CurrentPointer.Read(eraDir);
        var eraPath = currentName is null ? null : Path.Combine(eraDir, currentName);

        EraStore.Manifest? manifest = null;
        var state = new Dictionary<string, EraStore.EraTrackState>(StringComparer.Ordinal);
        if (eraPath is not null && File.Exists(eraPath))
        {
            manifest = EraStore.StoreReader.TryReadManifest(eraPath);
            if (manifest is not null)
            {
                try
                {
                    using var reader = new EraStore.StoreReader(eraPath);
                    state = reader.LoadTrackState();
                }
                catch (Exception ex)
                {
                    Log.LogWarn("indexer", $"读取 EraStore 增量状态失败，按全量重建: {ex.Message}");
                    manifest = null;
                    state.Clear();
                }
            }
        }

        var eraFull = full
            || manifest is null
            || manifest.MetaVersion < EraStore.Manifest.CurrentMetaVersion
            || !EraAnchorsMatch(manifest, options, dataPack);
        if (eraFull)
        {
            var detail = $"full={full} manifest={(manifest is null ? "null" : "ok")}"
                + (manifest is null
                    ? ""
                    : $" meta={manifest.MetaVersion} analyzer={manifest.Anchors.AnalyzerVersion}"
                        + $" bits={manifest.OptionsBits} dataPack={manifest.Anchors.DataPackId}"
                        + $" pack={dataPack.Id}");
            if (full) Log.LogInfo("indexer", $"EraStore 全量重建: {detail}");
            else Log.LogWarn("indexer", $"EraStore 未就绪 / 元数据不符，全量重建: {detail}");
            manifest = null;
            state.Clear();
        }

        var profile = manifest is null ? new ScriptProfile() : ScriptProfile.FromManifest(manifest);
        long generation = ReadScannerGeneration(libraryPath);
        var total = CountTracks(libraryPath);
        int processed = 0, indexed = 0, deleted = 0;
        // V6 护栏触发计数：layers = (曲目, 层) 维度；tracks = 受影响曲目数（去重）。
        int lyricsTruncatedLayers = 0, lyricsTruncatedTracks = 0;

        void Fire(string phase, string current) => _progress?.Invoke(new IndexerProgress
        {
            Phase = phase,
            Total = total,
            Processed = processed,
            Indexed = indexed,
            Deleted = deleted,
            Current = current,
            LyricsTruncatedLayers = lyricsTruncatedLayers,
            LyricsTruncatedTracks = lyricsTruncatedTracks,
        });

        Fire("collect", eraFull ? "full" : "incremental");

        var dirty = new List<DirtyTrack>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var probe in ReadTrackProbes(libraryPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            seen.Add(probe.Id);

            var isDirty = eraFull;
            if (!isDirty)
            {
                if (!state.TryGetValue(probe.Id, out var existing))
                {
                    isDirty = true;
                }
                else
                {
                    isDirty = existing.Mtime != probe.Mtime
                        || (options.IndexLrc
                            ? existing.LrcMtime != probe.LrcMtime || existing.LrcSize != probe.LrcSize
                            : existing.LrcMtime != 0 || existing.LrcSize != 0);
                }
            }

            if (isDirty) dirty.Add(new DirtyTrack { Id = probe.Id, Path = probe.Path });
            if (processed % ProgressEvery == 0) Fire("collect", probe.Path);
        }
        total = seen.Count;

        var eraDocs = new List<EraStore.AnalyzedDoc>(eraFull ? 0 : dirty.Count);
        if (!eraFull)
        {
            // 增量路径只分析脏曲；全量构建由 BuildFullEra 分块流式分析，
            // 不能先把全库 AnalyzedDoc 收进 List（48.7k 真实歌词会到 GiB 级）。
            for (var offset = 0; offset < dirty.Count; offset += ContentChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = dirty.GetRange(offset, Math.Min(ContentChunkSize, dirty.Count - offset));
                var contents = ReadTrackContents(libraryPath, chunk, cancellationToken);
                ContentQueries++;
                foreach (var item in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!contents.TryGetValue(item.Id, out var track))
                    {
                        Log.LogWarn("indexer", $"曲目在索引过程中消失，跳过: {item.Id}");
                        continue;
                    }
                    var (tokens, truncatedLayers, lyricLines) = BuildTokens(track, options, generation);
                    if (truncatedLayers > 0)
                    {
                        lyricsTruncatedLayers += truncatedLayers;
                        lyricsTruncatedTracks++;
                    }
                    profile.Add(tokens);
                    eraDocs.Add(BuildEraDoc(track, tokens, lyricLines));
                    indexed++;
                    if (indexed % ProgressEvery == 0) Fire("index", item.Path);
                }
            }
        }

        var deletes = new List<byte[]>();
        foreach (var trackId in state.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Contains(trackId)) continue;
            deletes.Add(EncodeTrackId(trackId).Bytes);
            deleted++;
        }

        Fire("commit", "");

        var wroteEra = false;
        var eraIncremental = false;
        var eraMerged = false;
        var eraSegments = 0;
        var eraLive = 0;
        var eraDead = 0;
        var eraCompacted = false;
        var eraSegmentsBeforeCompact = 0;
        var eraCompactMs = 0L;
        var eraDefragged = false;
        var eraDefragMs = 0L;
        var eraFileBytesAfter = 0L;

        (int Live, int Segments) BuildFullNow()
        {
            var eraName = $"search-{DateTime.UtcNow.Ticks}.eraindex";
            var eraPath = Path.Combine(eraDir, eraName);
            var build = BuildFullEra(
                eraDir, eraName, libraryPath, seen, options, generation,
                0, Array.Empty<string>(), new ScriptProfile());
            profile = build.Profile;
            lyricsTruncatedLayers = build.TruncatedLayers;
            lyricsTruncatedTracks = build.TruncatedTracks;
            var fullManifest = EraStore.StoreReader.TryReadManifest(eraPath);
            if (fullManifest is not null)
            {
                var meta = MetaFromProfile(profile, options);
                UpdateEraManifestOptions(
                    eraPath,
                    BuildOptionsBits(meta), EffectiveCapabilities(meta));
            }

            // 全量构建按 4 MB 累加器落段，真实歌词库会留下数百个小段。这里在**构建已提交、
            // CURRENT 已指向新世代之后**做一次有界压缩（见 BuildCompactor）：压缩失败 / 被
            // 中断只损失本次压缩，构建产物本身仍然有效；旧世代文件在压缩完成后才清理。
            var segments = build.Segments;
            var compaction = CompactEraAfterFullBuild(eraPath, stopwatch.Elapsed.TotalSeconds);
            if (compaction is { Compacted: true })
            {
                eraCompacted = true;
                eraSegmentsBeforeCompact = compaction.SegmentsBefore;
                eraCompactMs = compaction.ElapsedMs;
                segments = compaction.SegmentsAfter;
                eraMerged = true;
                Fire("compact", $"{compaction.SegmentsBefore}->{compaction.SegmentsAfter}");

                // S1：压缩直接写只含活段体的新世代并切换 CURRENT，产出文件没有旧段 / 旧
                // Manifest 垃圾，因此不需要再去碎片复制一遍。仅当某些路径退回「就地追加」
                // （RewroteGeneration=false）时才走原来的 defrag 兜底。
                if (compaction.RewroteGeneration)
                {
                    eraFileBytesAfter = compaction.FileBytes;
                }
                else
                {
                    var defrag = DefragEraAfterFullBuild(eraPath);
                    if (defrag is { Ran: true })
                    {
                        eraDefragged = true;
                        eraDefragMs = defrag.ElapsedMs;
                        eraFileBytesAfter = defrag.FileBytesAfter;
                    }
                }
            }

            // 去碎片会把 CURRENT 指向新的世代文件名；清理必须以切换后的 CURRENT 为准，
            // 否则会误删刚整理好的文件。
            var keep = EraStore.CurrentPointer.Read(eraDir) ?? eraName;
            CleanupEraFiles(eraDir, keep);
            return (build.LiveDocs, segments);
        }

        try
        {
            Directory.CreateDirectory(eraDir);
            if (eraFull)
            {
                var built = BuildFullNow();
                eraSegments = built.Segments;
                eraLive = built.Live;
                indexed = built.Live;
                wroteEra = true;
            }
            else if (eraDocs.Count > 0 || deletes.Count > 0)
            {
                if (eraPath is null || !File.Exists(eraPath))
                    throw new InvalidDataException("EraStore CURRENT 缺失，回退全量构建");
                var meta = MetaFromProfile(profile, options);
                var inc = EraStore.IncrementalUpdater.Apply(
                    eraPath, eraDocs, deletes,
                    new EraStore.IncrementalOptions
                    {
                        OptionsBits = BuildOptionsBits(meta),
                        Capabilities = EffectiveCapabilities(meta),
                        // 增量新段必须与全量同一套免位置布局，否则同一曲库会按写入时间
                        // 出现两种短语语义；旧段不受影响（既有索引仍可读，无需重建）。
                        NoPositionFields = EraStore.BuildDefaults.CreateNoPositionFields(),
                    });
                eraIncremental = true;
                eraMerged = inc.Merged;
                eraSegments = inc.SegmentCount;
                eraLive = (int)inc.LiveDocs;
                eraDead = inc.DeadDocs;
                wroteEra = true;
            }
            else if (options.AutoMaintain && eraPath is not null && File.Exists(eraPath))
            {
                // S7：没有任何脏曲 / 删除时，仍按阈值做一次有界维护（回收墓碑行与小段）。
                // Maintain 先读 Manifest；未达阈值时不写文件，因此干净索引的重复运行仍零写入。
                var meta = MetaFromProfile(profile, options);
                var maintained = EraStore.IncrementalUpdater.Maintain(
                    eraPath,
                    new EraStore.IncrementalOptions
                    {
                        OptionsBits = BuildOptionsBits(meta),
                        Capabilities = EffectiveCapabilities(meta),
                        NoPositionFields = EraStore.BuildDefaults.CreateNoPositionFields(),
                    });
                eraMerged = maintained.Merged;
                eraSegments = maintained.SegmentCount;
                wroteEra = maintained.Merged;
                if (maintained.Merged)
                {
                    Fire("maintain", maintained.SegmentCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                    Log.LogInfo("indexer",
                        $"EraStore 无脏曲维护: 合并后 {maintained.SegmentCount} 段");
                }
            }

            var keep = EraStore.CurrentPointer.Read(eraDir);
            if (keep is not null) CleanupEraFiles(eraDir, keep);
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"EraStore 写入失败，回退全量重建: {ex.Message}");
            try
            {
                var built = BuildFullNow();
                eraIncremental = false;
                eraMerged = false;
                eraSegments = built.Segments;
                eraLive = built.Live;
                indexed = built.Live;
                wroteEra = true;
            }
            catch (Exception inner)
            {
                Log.LogWarn("indexer", $"EraStore 全量重建也失败，下次运行重试: {inner.Message}");
            }
        }

        stopwatch.Stop();
        Fire("done", "");
        return new IndexerResult
        {
            Total = total,
            Indexed = indexed,
            Deleted = deleted,
            Skipped = total - indexed,
            Full = eraFull,
            Generation = generation,
            DurationMs = stopwatch.ElapsedMilliseconds,
            EraWritten = wroteEra,
            EraIncremental = eraIncremental,
            EraMerged = eraMerged,
            EraSegments = eraSegments,
            EraSegmentsBeforeCompact = eraCompacted ? eraSegmentsBeforeCompact : eraSegments,
            EraCompacted = eraCompacted,
            EraCompactMs = eraCompactMs,
            EraDefragged = eraDefragged,
            EraDefragMs = eraDefragMs,
            EraFileBytesAfter = eraFileBytesAfter,
            EraLiveDocs = eraLive,
            EraDeadDocs = eraDead,
            LyricsTruncatedLayers = lyricsTruncatedLayers,
            LyricsTruncatedTracks = lyricsTruncatedTracks,
            // 超长词条已改为哈希词条（EraStore.TermCodec），不再丢弃；字段保留 ABI 兼容。
            OversizeTokens = 0,
        };
    }

    /// <summary>
    /// 全量构建提交之后的有界自动压缩。预算按**构建耗时**折算
    /// （<see cref="FullBuildCompactBudgetFraction"/>，钳在
    /// <see cref="FullBuildCompactMinBudgetSeconds"/>–<see cref="FullBuildCompactMaxBudgetSeconds"/>），
    /// 最多 <see cref="FullBuildCompactMaxRounds"/> 轮；目标是「从数百个小段压回两位数」，
    /// 而不是追求单段——单段不是查询正确性的前提。
    ///
    /// <para>T3.15 实测（30k / 286 段，无 .lrc、本机 SSD）：构建本体 27–32 s；
    /// 默认 30% 预算（≈6.8 s）可归并到 2 段、查询 p50 ≈7.7 ms；若改回 §8.0 的 10%
    /// 预算（≈2.7 s）则停在 ≈180 段、p50 ≈19 ms。两种取值的取舍见
    /// <see cref="FullBuildCompactBudgetFraction"/>。</para>
    ///
    /// <para>失败只记 warn：压缩纯属物理整理，构建产物的 Manifest 已提交，
    /// 中断 / 异常不会让索引不可读。</para>
    /// </summary>
    private static EraStore.BuildCompactionResult? CompactEraAfterFullBuild(
        string eraPath, double buildSeconds)
    {
        var budget = Math.Clamp(
            buildSeconds * FullBuildCompactBudgetFraction,
            FullBuildCompactMinBudgetSeconds,
            FullBuildCompactMaxBudgetSeconds);
        try
        {
            var result = EraStore.BuildCompactor.Compact(eraPath, new EraStore.BuildCompactionOptions
            {
                BudgetSeconds = budget,
                MaxRounds = FullBuildCompactMaxRounds,
            });
            if (result.Compacted)
            {
                Log.LogInfo("indexer",
                    $"EraStore 全量构建后自动压缩: {result.SegmentsBefore} → {result.SegmentsAfter} 段"
                    + $"（{result.Rounds} 轮 / 归并 {result.MergedSegments} 段 / {result.ElapsedMs}ms，"
                    + $"构建 {buildSeconds:0.0}s，预算 {budget:0.0}s）");
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer",
                $"EraStore 全量构建后自动压缩失败（构建产物仍可用）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 全量构建 + 自动压缩之后的**一次性去碎片**（A2）。只在压缩确实留下
    /// &gt;20% 活数据的垃圾时执行；任何失败只记 warn，不影响已提交的构建产物。
    /// </summary>
    private static EraStore.DefragResult? DefragEraAfterFullBuild(string eraPath)
    {
        try
        {
            var result = EraStore.Defragmenter.DefragIfGarbage(
                eraPath, FullBuildDefragOverheadRatio);
            if (result.Ran)
            {
                Log.LogInfo("indexer",
                    $"EraStore 全量构建后去碎片: {result.FileBytesBefore} → {result.FileBytesAfter} 字节"
                    + $"（活 {result.LiveBytes} 字节 / 回收 {result.OverheadBytesBefore - result.OverheadBytesAfter} 字节"
                    + $" / {result.ElapsedMs}ms）");
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer",
                $"EraStore 全量构建后去碎片失败（构建产物仍可用）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 版本锚 + 选项位 + **数据包 id** 是否全部与当前构建条件一致。
    ///
    /// <para>数据包 id 单独成项（§5.5.2）：任一 tsv 内容变化 → 新 id → 这里返回 false →
    /// 即使 analyzer / options 都没变也强制全量重建。反过来，生成脚本变了但输出字节不变 →
    /// id 不变 → 不重建。</para>
    /// </summary>
    internal static bool EraAnchorsMatch(
        EraStore.Manifest manifest, IndexerOptions options, DataPack dataPack) =>
        manifest.Anchors.AnalyzerVersion == Analyzer.AnalyzerVersion
        && EraOptionsMatch(manifest, options)
        && string.Equals(manifest.Anchors.DataPackId, dataPack.Id, StringComparison.Ordinal);

    private static bool EraOptionsMatch(EraStore.Manifest manifest, IndexerOptions options)
    {
        var bits = manifest.OptionsBits;
        bool Has(int bit) => (bits & bit) != 0;
        return Has(EraStore.ManifestOptionsBits.IndexLrc) == options.IndexLrc
            && Has(EraStore.ManifestOptionsBits.Pinyin) == options.Pinyin
            && Has(EraStore.ManifestOptionsBits.RomajiLyrics) == options.RomajiLyrics
            && Has(EraStore.ManifestOptionsBits.InitialsLyrics) == options.InitialsLyrics;
    }

    private static StoreMeta MetaFromProfile(ScriptProfile profile, IndexerOptions options) => new()
    {
        // 能力推导需要真实分析器版本（initials_bigram 等按版本声明）；默认 0 会漏报。
        AnalyzerVersion = Analyzer.AnalyzerVersion,
        IndexLrc = options.IndexLrc,
        Pinyin = options.Pinyin,
        RomajiLyrics = options.RomajiLyrics,
        InitialsLyrics = options.InitialsLyrics,
        HasJapanese = profile.Japanese,
        HasKorean = profile.Korean,
        HasHan = profile.Han,
        HasLatin = profile.Latin,
    };

    /// <summary>
    /// 写入索引 Manifest 的能力集合：在 <see cref="IndexCapabilities.FromMeta"/> 基础上
    /// 去掉当前数据包已降级的能力（§5.5.6），让索引不宣称自己做不到的事。
    /// </summary>
    private static string[] EffectiveCapabilities(StoreMeta meta)
    {
        var caps = IndexCapabilities.FromMeta(meta).ToArray();
        var degraded = DataPack.Current.DegradedCapabilities;
        if (degraded.Count == 0) return caps;
        return caps.Where(cap => !degraded.Contains(cap)).ToArray();
    }

    /// <summary>全量构建收尾后把语言画像 / 能力写回 Manifest（构建时未知，需二次提交）。</summary>
    private static void UpdateEraManifestOptions(
        string eraPath, int optionsBits, IReadOnlyList<string> capabilities)
    {
        using var file = EraStore.IndexFile.Open(eraPath);
        var manifest = file.Current;
        if (manifest is null) return;
        if (manifest.OptionsBits == optionsBits
            && manifest.Capabilities.SequenceEqual(capabilities, StringComparer.Ordinal))
            return;
        manifest.OptionsBits = optionsBits;
        manifest.Capabilities.Clear();
        manifest.Capabilities.AddRange(capabilities);
        manifest.Gen = file.Gen + 1;
        EraStore.IndexBuilder.FillFileBytes(manifest, file.Length);
        file.Commit(manifest);
    }

    /// <summary>
    /// 全量构建 EraStore：按 <see cref="ContentChunkSize"/> 分块读库 + 分析并**流式**喂给
    /// <see cref="EraStore.IndexBuilder"/>。不得先把全库 <see cref="EraStore.AnalyzedDoc"/>
    /// 收进 List——48.7k 真实歌词下那会是 GiB 级常驻内存（T3.10 实测 5k 已近 1 GB）。
    /// </summary>
    private static (int LiveDocs, int Segments, ScriptProfile Profile, int TruncatedLayers, int TruncatedTracks)
        BuildFullEra(
            string eraDir, string eraName, string libraryPath, HashSet<string> seen,
            IndexerOptions options, long generation, int optionsBits, IReadOnlyList<string> capabilities,
            ScriptProfile seed)
    {
        var allIds = seen.ToList();
        var live = 0;
        var profile = seed;
        var truncatedLayers = 0;
        var truncatedTracks = 0;

        IEnumerable<EraStore.AnalyzedDoc> Enumerate()
        {
            for (var offset = 0; offset < allIds.Count; offset += EraBuildChunkSize)
            {
                var take = Math.Min(EraBuildChunkSize, allIds.Count - offset);
                var chunk = new List<DirtyTrack>(take);
                for (var i = offset; i < offset + take; i++)
                    chunk.Add(new DirtyTrack { Id = allIds[i] });

                var contents = ReadTrackContents(libraryPath, chunk, CancellationToken.None);
                foreach (var item in chunk)
                {
                    if (!contents.TryGetValue(item.Id, out var track)) continue;
                    var (tokens, truncated, lyricLines) = BuildTokens(track, options, generation);
                    if (truncated > 0)
                    {
                        truncatedLayers += truncated;
                        truncatedTracks++;
                    }
                    profile.Add(tokens);
                    live++;
                    yield return BuildEraDoc(track, tokens, lyricLines);
                }
            }
        }

        var eraPath = Path.Combine(eraDir, eraName);
        EraStore.IndexBuilder.Build(
            eraPath, Enumerate(),
            new EraStore.BuildOptions
            {
                Gen = 1,
                AnalyzerVersion = Analyzer.AnalyzerVersion,
                DataPackId = DataPack.Current.Id,
                OptionsBits = optionsBits,
                Capabilities = capabilities,
                // S2：生产默认给两条派生转写层免位置（与 bench 共用同一份清单）。
                // 显式改 BuildOptions.NoPositionFields 仍可覆盖，用于测试 / 诊断。
                NoPositionFields = EraStore.BuildDefaults.CreateNoPositionFields(),
                // 真实中文歌词（2–8 KB/曲、12 字段族）下单段累加器的实际内存约为估算值的
                // 6–8 倍；32 MB 估算会把峰值推到 200 MB+。4 MB 估算 ⇒ 约 25–35 MB 实际，
                // 段数随之增加（2048 条 section 上限可容纳 ~400 段）。
                // 测试可调小上限，在小型夹具上覆盖「多段 → 全量构建收尾压缩」路径。
                MaxSegmentBytes = options.FullBuildMaxSegmentBytesOverride > 0
                    ? options.FullBuildMaxSegmentBytesOverride
                    : 4L << 20,
            });
        var segments = EraStore.StoreReader.TryReadManifest(eraPath) is { } built
            ? CountEraSegments(built)
            : 0;
        return (live, segments, profile, truncatedLayers, truncatedTracks);
    }

    private static int CountEraSegments(EraStore.Manifest manifest)
    {
        var segments = new HashSet<ushort>();
        foreach (var section in manifest.Sections)
            if (section.Kind == EraStore.SectionKind.DocStore) segments.Add(section.SegmentId);
        return segments.Count;
    }

    /// <summary>阶段一脏曲项：轻量探测结果 + 已 stat 的 <c>.lrc</c> 供阶段二复用（免重复探测）。</summary>
    private sealed class DirtyTrack
    {
        public string Id { get; init; } = "";
        public string Path { get; init; } = "";
    }

    /* ------------------------------------------------------------------ */
    /* 分层分析                                                            */
    /* ------------------------------------------------------------------ */

    private static (DocTokens Tokens, int TruncatedLayers, IReadOnlyList<string> LyricLines) BuildTokens(
        TrackRecord track, IndexerOptions options, long generation)
    {
        // 「整首是否日文」由任一源含假名判定：真实日文曲库里歌手 / 专辑常是**纯汉字**
        // （米津玄師 / ヨルシカ），需借书目标题里的假名把整首标记为日文，才给这些纯汉字段查读音表。
        var hasKana = Japanese.ContainsKana(track.Title) || Japanese.ContainsKana(track.ArtistText)
            || Japanese.ContainsKana(track.AlbumText) || Japanese.ContainsKana(track.Lyrics)
            || Japanese.ContainsKana(Path.GetFileNameWithoutExtension(track.Path));
        var japanese = options.Pinyin && hasKana;

        var title = Analyzer.Analyze(track.Title, japanese);
        var artist = Analyzer.Analyze(track.ArtistText, japanese);
        var album = Analyzer.Analyze(track.AlbumText, japanese);

        // 内嵌歌词可能本身是 LRC（含时间轴 / 多语言同时间戳行），与 .lrc 侧车**一致**
        // 走同一分层启发式（LrcReader.FromRawTexts）：原文 / 译文 / 音译分别入库。
        // 侧车文本来自 **library.db**（扫描器写入的原始文本，保时间轴），索引器不碰文件系统。
        var hasSidecar = options.IndexLrc &&
            (track.LrcText.Length > 0 || track.LrcTransText.Length > 0 || track.LrcRomajiText.Length > 0);

        // T4.2：先按 §5.6.2 给三层打分，再**每层只保留一份**（内嵌 vs 侧车取置信度高者，
        // 不拼接）。译文 / 音译按 §5.6.5 的配对规则判定（显式侧车 + 时间戳对齐）。
        var scored = LrcReader.ScoreLayers(
            track.Lyrics,
            hasSidecar ? track.LrcText : null,
            hasSidecar && track.LrcTransText.Length > 0 ? track.LrcTransText : null,
            hasSidecar && track.LrcRomajiText.Length > 0 ? track.LrcRomajiText : null);

        // 三个字面歌词层先经**同一**预处理（T3.5 去重 + T3.8 V6 护栏）再分析：
        //   · 去重键 = Analyzer.Normalize（与词元归一同一套语义），只在本层内去重；
        //   · 护栏 4096 行 / 128 KiB 逐层独立，超限截尾并计数（每曲最多 warn 一次）。
        // 段 / 高亮仍读 library.db 原文，本处只改**索引词元**。
        var originalLayer = LyricsPreprocess.Apply(scored.Original.Text);
        var translationLayer = LyricsPreprocess.Apply(scored.Translation.Text);
        var romajiLayer = LyricsPreprocess.Apply(scored.Romaji.Text);

        // 字面歌词层不产 unigram（T3.7）：单字歌词查询被查询侧列门控排除，unigram 只占体积。
        // 音译层同样按「字面歌词」处理：它通常是拉丁整词（本就不产 unigram），若含假名 / 谚文
        // 也只是用户提供的表层文本，没有理由保留单字倒排；生成的歌词读音（lyrics.Readings）
        // 属于派生列，仍由完整原文产出、不受影响。
        // 原文层仍按整首是否日文取读音；拼音 / 首字母 / 读音列都由完整文本派生，不受影响。
        var lyrics = Analyzer.Analyze(originalLayer.Text, japanese, includeUnigrams: false);
        var translation = Analyzer.Analyze(translationLayer.Text, includeUnigrams: false);
        var romaji = Analyzer.Analyze(romajiLayer.Text, includeUnigrams: false);
        // 歌词语音罗马音（T2-9 可选增强）：无 LRC 音译层时，用已算好的歌词读音 bigram 填充；
        // 有用户提供的音译整词时**不覆盖**。
        var lyricsRom = romaji.Tokens;
        var generatedRomaji = false;
        if (lyricsRom.Length == 0 && options.RomajiLyrics && hasKana && lyrics.Readings.Length > 0)
        {
            lyricsRom = lyrics.Readings;
            generatedRomaji = true;
        }
        var filename = Analyzer.Analyze(Path.GetFileNameWithoutExtension(track.Path), japanese);
        // 日文读音只索引元数据段（歌词读音不索引，见 DocTokens.ReadingMetaTok 说明）；
        // 同一串另存一份原文副本供兜底趟分档（T1-4，EraStore 词典不保存整段原文）。
        var reading = options.Pinyin ? CombineReadings(title, artist, album, filename) : "";

        // v5 歌词行摘要（Q1）的原料：**实际参与索引的**三个字面歌词层（原文 / 译文 /
        // 显式音译），取预处理（去重 + V6 护栏）之后的文本。生成的罗马音由读音派生，
        // 不是用户可搜的字面行，不参与摘要；重复行跨层由 LyricDigest.Build 去重。
        var lyricLines = new List<string>();
        CollectLines(originalLayer.Text, lyricLines);
        CollectLines(translationLayer.Text, lyricLines);
        CollectLines(romajiLayer.Text, lyricLines);

        // 每曲最多一条 warn：列出被截断的层与触发维度，便于定位病态歌词来源。
        var truncatedLayers = 0;
        var truncatedDetail = "";
        void NoteTruncated(string layer, LyricLayerText result)
        {
            if (!result.Truncated) return;
            truncatedLayers++;
            if (truncatedDetail.Length > 0) truncatedDetail += "；";
            truncatedDetail += layer + "("
                + (result.TruncatedByLines ? $"{result.UniqueLines}行>{LyricsPreprocess.MaxUniqueLines}" : "")
                + (result.TruncatedByLines && result.TruncatedByBytes ? ", " : "")
                + (result.TruncatedByBytes ? $"{result.DedupedBytes}B>{LyricsPreprocess.MaxUtf8Bytes}" : "")
                + ")";
        }
        NoteTruncated("original", originalLayer);
        NoteTruncated("translation", translationLayer);
        NoteTruncated("romaji", romajiLayer);
        if (truncatedLayers > 0)
        {
            Log.LogWarn("indexer",
                $"歌词层截断（V6 护栏，仅索引词元受影响）: track={track.Id} {truncatedDetail}");
        }

        // T4.2：层置信度（每层 2 bit，DocStore v4 的 layer_conf）与来源位（flags bit3–7）。
        // 生成的罗马音（T2-9，来自原文读音）不是「LRC 音译层」，它从原文派生，因此**继承原文层
        // 置信度**（原文低 → 生成列也低）；读音趟本身不按置信度过滤，读音检索仍可用。
        var romajiConfidence = scored.Romaji.Confidence;
        var romajiSource = scored.Romaji.Source;
        if (generatedRomaji)
        {
            romajiConfidence = scored.Original.Confidence;
            romajiSource = LyricSource.Embedded;
        }

        var tokens = new DocTokens
        {
            TrackId = track.Id,
            LayerConf = EraStore.DocStoreReader.PackLayerConf(
                scored.Original.Confidence, scored.Translation.Confidence, romajiConfidence),
            LayerSourceBits = EraStore.DocStoreReader.PackLayerSources(
                scored.Original.Source, scored.Translation.Source, romajiSource),
            TitleTok = title.Tokens,
            ArtistTok = artist.Tokens,
            AlbumTok = album.Tokens,
            LyricsTok = lyrics.Tokens,
            LyricsTrTok = translation.Tokens,
            LyricsRomTok = lyricsRom,
            // 拼音 / 首字母**按元数据段与歌词段分列**（v4）：元数据查询（列过滤）才收得住。
            //
            // **文件名不进这两列**（精度审计 D-4）：文件名已被 <see cref="FilenameTok"/> 独立索引
            // （整词 + bigram），再混进拼音 / 首字母列会带来两重污染——
            // ① 文件名里的数字 / 序号（`t164` / `track01`）会产出 `t` / `164` 这类 token，
            //    单字母恰是「单字查询整库命中」的燃料（见 SearchQuery.InitialsBranch 门控）；
            // ② `01 - 某人 - 恨` 这类文件名会把标题之外的信息塞进首字母串，令首字母检索失真。
            // 去掉后「用文件名搜歌」仍完整可用（走 filename_tok）。
            PinyinMetaTok = options.Pinyin
                ? CombinePinyin(title, artist, album)
                : "",
            PinyinLyricsTok = options.Pinyin ? CombinePinyin(lyrics) : "",
            InitialsMetaTok = options.Pinyin
                ? CombineInitials(title, artist, album)
                : "",
            InitialsLyricsTok = options.Pinyin && options.InitialsLyrics
                ? CombineInitials(lyrics)
                : "",
            ReadingMetaTok = reading,
            ReadingText = reading,
            FilenameTok = filename.Tokens,
            IndexedMtime = track.Mtime,
            // 侧车锚点取 **DB 里的聚合值**（与阶段一脏集判定同源，避免索引侧与扫描侧各算一套）。
            IndexedLrcMtime = hasSidecar ? track.LrcMtime : 0,
            IndexedLrcSize = hasSidecar ? track.LrcSize : 0,
            IndexedGeneration = generation,
        };
        return (tokens, truncatedLayers, lyricLines);
    }

    /// <summary>把一个预处理后的歌词层按行加入摘要原料（空层 / 空行跳过）。</summary>
    private static void CollectLines(string layerText, List<string> lines)
    {
        if (layerText.Length == 0) return;
        foreach (var line in layerText.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        }
    }

    /// <summary>
    /// track id → 16 字节：32-hex 直接解码；非 hex 且 UTF-8 长度 ≤16 的短 id 零填充
    /// （标记为文本 id，查询时还原）；更长的非常规 id 退回 MD5（不可还原，仅防御）。
    /// </summary>
    internal static (byte[] Bytes, bool IsText) EncodeTrackId(string id)
    {
        if (id.Length == 32 && IsHex(id)) return (Convert.FromHexString(id), false);

        var utf8 = System.Text.Encoding.UTF8.GetBytes(id);
        if (utf8.Length <= EraStore.EraFormat.TrackIdBytes)
        {
            var padded = new byte[EraStore.EraFormat.TrackIdBytes];
            utf8.CopyTo(padded, 0);
            return (padded, true);
        }
        return (System.Security.Cryptography.MD5.HashData(utf8), false);

        static bool IsHex(string value)
        {
            foreach (var c in value)
            {
                if (!char.IsAsciiHexDigit(c)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 把 <see cref="TrackRecord"/> + <see cref="DocTokens"/> 转成 EraStore 的
    /// <see cref="EraStore.AnalyzedDoc"/>。12 字段 token 串与 EraStore 的字段族一一对应。
    ///
    /// 词条硬上限（<see cref="EraStore.EraFormat.MaxTermBytes"/> = 255B）由
    /// <see cref="EraStore.TermCodec"/> 在写入前统一处理：超长单 token（超长无分隔拉丁词 /
    /// URL / 长串）换成不透明哈希词条（<c>0x01 + MD5 hex</c>，33B），查询侧走同一个 helper，
    /// 因此仍然可检索。词条不再丢弃，其余字段与整库构建也不受影响。
    /// </summary>
    private static EraStore.AnalyzedDoc BuildEraDoc(
        TrackRecord track, DocTokens tokens, IReadOnlyList<string> lyricLines)
    {
        string[] SplitTok(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<string>();
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
                parts[i] = EraStore.TermCodec.Canonicalize(parts[i]);
            return parts;
        }

        var fields = new Dictionary<EraStore.FieldId, string[]>
        {
            { EraStore.FieldId.Title, SplitTok(tokens.TitleTok) },
            { EraStore.FieldId.Artist, SplitTok(tokens.ArtistTok) },
            { EraStore.FieldId.Album, SplitTok(tokens.AlbumTok) },
            { EraStore.FieldId.Filename, SplitTok(tokens.FilenameTok) },
            { EraStore.FieldId.Lyrics, SplitTok(tokens.LyricsTok) },
            { EraStore.FieldId.LyricsTranslation, SplitTok(tokens.LyricsTrTok) },
            { EraStore.FieldId.LyricsRomanization, SplitTok(tokens.LyricsRomTok) },
            { EraStore.FieldId.PinyinMeta, SplitTok(tokens.PinyinMetaTok) },
            { EraStore.FieldId.PinyinLyrics, SplitTok(tokens.PinyinLyricsTok) },
            { EraStore.FieldId.InitialsMeta, SplitTok(tokens.InitialsMetaTok) },
            { EraStore.FieldId.InitialsLyrics, SplitTok(tokens.InitialsLyricsTok) },
            { EraStore.FieldId.ReadingMeta, SplitTok(tokens.ReadingMetaTok) },
        };

        // tracks.id 是 32 字符 hex（MD5），转 16 字节；非 hex 的短 id（测试夹具 / 旧数据）
        // 以 UTF-8 零填充保存，并置文本标志位，查询时原样还原（生产恒为 hex，不占标志位）。
        var (trackBytes, textId) = EncodeTrackId(track.Id);
        // Rev：内容指纹（不是完整内容哈希，但 EraStore 只用于去重 / 判「文档内容变了」）。
        // T4.2 起把层置信度与来源位也计入——`layer_confidence` 属于文档内容，变化必须重写该曲
        // （§5.6.6）。
        var revInput = System.Text.Encoding.UTF8.GetBytes(
            track.Mtime + "|" + track.Path + "|" + tokens.TitleTok
            + "|" + tokens.LayerSourceBits + "|" + tokens.LayerConf);
        var rev = System.Security.Cryptography.MD5.HashData(revInput);

        byte flags = 0;
        if (tokens.LyricsTrTok.Length > 0) flags |= 1;
        if (tokens.LyricsRomTok.Length > 0) flags |= 2;
        if (textId) flags |= EraStore.DocStoreReader.FlagTextTrackId;
        flags |= tokens.LayerSourceBits;

        // title / artist / album 保存原文（片段 / explain 依赖）；DocStore v3 的归一列
        // （title_norm / artist_norm / album_norm）由 IndexBuilder.MakeRecord 统一生成（T3.11），
        // v4 的 layer_conf 由 BuildTokens 给出，单曲不再各写一份归一化规则。
        return new EraStore.AnalyzedDoc(
            trackBytes, rev, 0, flags, fields,
            track.Title, track.ArtistText, track.AlbumText,
            tokens.IndexedMtime, tokens.IndexedLrcMtime, tokens.IndexedLrcSize,
            tokens.LayerConf, lyricLines);
    }

    /// <summary>
    /// 拼接内嵌层与侧车层文本（非空者以 <c>\n</c> 连接）；两者皆空返回空串。
    ///
    /// **归一后相同则只取一份**：真实曲库里内嵌歌词与同名 <c>.lrc</c> 常是同一份内容
    /// （实测 126/151 首归一后逐字相同），重复并入会让同一段文本在倒排里存两遍
    /// （体积 + bm25 词频翻倍）。只有**完全相同**才去重；部分重叠（例如侧车另带
    /// 「作词/作曲」行）仍按并集处理，不丢信息。
    /// </summary>
    internal static string JoinLayers(string embedded, string sidecar)
    {
        if (embedded.Length == 0) return sidecar;
        if (sidecar.Length == 0) return embedded;
        if (SameTextIgnoringWhitespace(embedded, sidecar)) return embedded;
        return embedded + "\n" + sidecar;
    }

    /// <summary>忽略空白（含换行 / 全角空格）的逐字符相等判定；不分配。</summary>
    private static bool SameTextIgnoringWhitespace(string a, string b)
    {
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && char.IsWhiteSpace(a[i])) i++;
            while (j < b.Length && char.IsWhiteSpace(b[j])) j++;
            if (i >= a.Length || j >= b.Length) return i >= a.Length && j >= b.Length;
            if (a[i] != b[j]) return false;
            i++;
            j++;
        }
    }

    /// <summary>
    /// 拼音列 = 各源的拼音音节（空格连接）。空串段自动跳过，避免多余空格。
    ///
    /// 调用方传入 title / artist / album（元数据）与 lyrics（歌词段），**不含文件名**——
    /// 文件名走独立的 <c>filename_tok</c> 列（见精度审计 D-4）。
    /// </summary>
    private static string CombinePinyin(params AnalysisResult[] sources)
    {
        var parts = new List<string>();
        foreach (var source in sources)
        {
            if (source.Pinyin.Length > 0) parts.Add(source.Pinyin);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 读音列（**日文**）= 各源的读音罗马字 bigram（空格连接）。
    /// 空源自动跳过（纯中文曲库全部为空 → 列留空，零体积 / 零噪声）。
    /// </summary>
    private static string CombineReadings(params AnalysisResult[] sources)
    {
        var parts = new List<string>();
        foreach (var source in sources)
        {
            if (source.Readings.Length > 0) parts.Add(source.Readings);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 首字母列 = 各源的首字母 **bigram**（T3.9），源与源之间用
    /// <see cref="Analyzer.InitialsBoundaryToken"/> 隔开。
    ///
    /// v3 起**独立列**：v2 把首字母串与全拼音节混在 <c>pinyin_tok</c>，导致首字母查询
    /// （`"wan"*`）命中拼音音节（完 / 万 / 晚…）——30k 语料实测命中 29985/30000、单条 56ms。
    /// 拆列后首字母只与首字母互比，命中集回归「真正含该首字母序列的短语」。
    ///
    /// v15 起列内是**相邻两两 bigram**（<c>wanzg</c> → <c>wa an nz zg</c>），段中间也能命中；
    /// 每段的 bigram 已在 <see cref="AnalysisResult.InitialsBigrams"/> 内用边界词元分好，
    /// 这里只需在字段之间补同一个边界词元：短语查询的相邻性被边界位置打断，
    /// title 末尾的 bigram 不能与 artist 开头的 bigram 拼成「假短语」。
    ///
    /// 调用方传入 title / artist / album（元数据）与 lyrics（歌词段），**不含文件名**：
    /// 文件名里的序号（`t164` / `track01`）会产出单字母 token，正是单字查询整库命中的燃料
    /// （见精度审计 D-4 与 <see cref="SearchQuery.MinInitialsBranchLength"/>）。
    /// </summary>
    private static string CombineInitials(params AnalysisResult[] sources)
    {
        var parts = new List<string>();
        foreach (var source in sources)
        {
            if (source.InitialsBigrams.Length == 0) continue;
            if (parts.Count > 0) parts.Add(Analyzer.InitialsBoundaryToken);
            parts.Add(source.InitialsBigrams);
        }
        return string.Join(' ', parts);
    }

    /* ------------------------------------------------------------------ */
    /* library.db 只读访问                                                 */
    /* ------------------------------------------------------------------ */

    private static SqliteConnection OpenLibraryReadOnly(string libraryPath)
    {
        var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryPath, readOnly: true));
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 轻量曲目计数（仅用于首条进度上报，不含任何列载荷）；失败时回退 0（不影响主流程）。
    /// </summary>
    private static int CountTracks(string libraryPath)
    {
        try
        {
            using var conn = OpenLibraryReadOnly(libraryPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tracks";
            var value = cmd.ExecuteScalar();
            return value is null || value is DBNull
                ? 0
                : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"统计 tracks 失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>阶段一：流式读取轻量列（<c>id/path/file_mtime</c>），不含歌词 / 元数据大列。</summary>
    private static IEnumerable<TrackProbe> ReadTrackProbes(string libraryPath, CancellationToken ct)
    {
        using var conn = OpenLibraryReadOnly(libraryPath);
        using var cmd = conn.CreateCommand();
        // .lrc 侧车由**扫描器**解析写入 library.db（v3 起）：
        //   lyrics_lrc / lyrics_lrc_trans / lyrics_lrc_romaji 存**原始文本**（保时间轴给播放侧），
        //   lrc_mtime / lrc_size 供脏集判定，lrc_checked 标识「本轮扫描已探测过侧车」。
        // 旧库（无这些列）自动降级为「无侧车」，不报错。
        var hasLrc = HasLrcColumns(conn);
        cmd.CommandText = hasLrc
            ? "SELECT id, path, file_mtime, lrc_mtime, lrc_size FROM tracks ORDER BY id"
            : "SELECT id, path, file_mtime FROM tracks ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            yield return new TrackProbe
            {
                Id = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Path = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Mtime = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                LrcMtime = hasLrc && !reader.IsDBNull(3) ? reader.GetInt64(3) : 0,
                LrcSize = hasLrc && !reader.IsDBNull(4) ? reader.GetInt64(4) : 0,
            };
        }
    }

    /// <summary>library.db 是否带 .lrc 侧车列（旧库 / 未扫描过的新库为 false → 降级无侧车）。</summary>
    private static bool LibraryHasLrcColumns(string libraryPath)
    {
        try
        {
            using var conn = OpenLibraryReadOnly(libraryPath);
            return HasLrcColumns(conn);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开的连接上判断 <c>tracks</c> 是否带 <c>.lrc</c> 侧车列（两者都在才算）。</summary>
    private static bool HasLrcColumns(SqliteConnection conn)
    {
        try
        {
            using var probe = conn.CreateCommand();
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('tracks') WHERE name IN ('lyrics_lrc','lrc_mtime')";
            return Convert.ToInt64(probe.ExecuteScalar() ?? 0L) >= 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 阶段二：按脏 id 分块取完整列（<c>WHERE id IN (…)</c>，参数化、<c>id</c> 为 TEXT），
    /// 返回按 <c>id</c> 索引的字典。仅对脏曲调用。
    /// </summary>
    private static Dictionary<string, TrackRecord> ReadTrackContents(
        string libraryPath, List<DirtyTrack> chunk, CancellationToken ct)
    {
        var map = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        using var conn = OpenLibraryReadOnly(libraryPath);
        using var cmd = conn.CreateCommand();

        var placeholders = new string[chunk.Count];
        for (int i = 0; i < chunk.Count; i++)
        {
            placeholders[i] = "@p" + i;
            cmd.Parameters.AddWithValue(placeholders[i], chunk[i].Id);
        }
        var hasLrc = HasLrcColumns(conn);
        cmd.CommandText = hasLrc
            ? $"SELECT id, path, title, artists, album, lyrics, file_mtime, " +
              $"lyrics_lrc, lyrics_lrc_trans, lyrics_lrc_romaji, lrc_mtime, lrc_size " +
              $"FROM tracks WHERE id IN ({string.Join(',', placeholders)})"
            : $"SELECT id, path, title, artists, album, lyrics, file_mtime FROM tracks WHERE id IN ({string.Join(',', placeholders)})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var record = new TrackRecord
            {
                Id = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Path = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ArtistText = ParseArtists(reader.IsDBNull(3) ? null : reader.GetString(3)),
                AlbumText = ParseAlbum(reader.IsDBNull(4) ? null : reader.GetString(4)),
                Lyrics = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Mtime = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                LrcText = hasLrc && !reader.IsDBNull(7) ? reader.GetString(7) : "",
                LrcTransText = hasLrc && !reader.IsDBNull(8) ? reader.GetString(8) : "",
                LrcRomajiText = hasLrc && !reader.IsDBNull(9) ? reader.GetString(9) : "",
                LrcMtime = hasLrc && !reader.IsDBNull(10) ? reader.GetInt64(10) : 0,
                LrcSize = hasLrc && !reader.IsDBNull(11) ? reader.GetInt64(11) : 0,
            };
            map[record.Id] = record;
        }
        return map;
    }

    /// <summary>解析 scanner 写入的 <c>artists</c> JSON 数组 <c>[{"name":"…"}]</c> 为空格连接文本。</summary>
    internal static string ParseArtists(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
            var names = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    var value = name.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
                }
            }
            return string.Join(' ', names);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>解析 scanner 写入的 <c>album</c> JSON 对象 <c>{"name":"…"}</c> 的 name。</summary>
    internal static string ParseAlbum(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString() ?? "";
            return "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// 读取 <c>_scanner_meta(generation INTEGER)</c> 的世代水位线（只读）。
    /// 表/行缺失时返回 0（前向兼容，不报错）。
    /// </summary>
    private static long ReadScannerGeneration(string libraryPath)
    {
        try
        {
            using var conn = OpenLibraryReadOnly(libraryPath);
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='_scanner_meta' LIMIT 1";
                if (probe.ExecuteScalar() is null) return 0;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT generation FROM _scanner_meta LIMIT 1";
            var value = cmd.ExecuteScalar();
            return value is null || value is DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"读取 _scanner_meta 失败（按世代 0 处理）: {ex.Message}");
            return 0;
        }
    }

    /// <summary>删除上一代 EraStore 文件（保留 <paramref name="keep"/>）；读取中的旧文件失败则忽略。</summary>
    private static void CleanupEraFiles(string eraDir, string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(eraDir, "search-*.eraindex"))
            {
                if (string.Equals(Path.GetFileName(file), keep, StringComparison.Ordinal)) continue;
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>把构建选项 / 语言画像打包成 Manifest 选项位（与 <see cref="ManifestOptionsBits"/> 对应）。</summary>
    private static int BuildOptionsBits(StoreMeta meta)
    {
        var bits = 0;
        if (meta.IndexLrc) bits |= EraStore.ManifestOptionsBits.IndexLrc;
        if (meta.Pinyin) bits |= EraStore.ManifestOptionsBits.Pinyin;
        if (meta.RomajiLyrics) bits |= EraStore.ManifestOptionsBits.RomajiLyrics;
        if (meta.InitialsLyrics) bits |= EraStore.ManifestOptionsBits.InitialsLyrics;
        if (meta.HasJapanese) bits |= EraStore.ManifestOptionsBits.HasJapanese;
        if (meta.HasKorean) bits |= EraStore.ManifestOptionsBits.HasKorean;
        if (meta.HasHan) bits |= EraStore.ManifestOptionsBits.HasHan;
        if (meta.HasLatin) bits |= EraStore.ManifestOptionsBits.HasLatin;
        return bits;
    }

    /// <summary>
    /// 曲库语言画像累加器。
    ///
    /// 只扫**索引内容列**（标题 / 歌手 / 专辑 / 文件名 / 歌词 / 译文 / 音译），
    /// **不扫**由它们派生的拼音 / 首字母 / 读音列——否则纯中文库会因拼音字母被误判为「含拉丁」，
    /// 中文歌的罗马字读音也会把纯英文库误判为「含日文」。
    ///
    /// 增量构建从旧画像起步（OR 语义）：删掉最后一首日文歌后位仍可能为 true，
    /// 但那只意味着「不裁剪兜底趟」，结果依旧正确，不需要为此做全库重算。
    /// </summary>
    private struct ScriptProfile
    {
        public bool Japanese;
        public bool Korean;
        public bool Han;
        public bool Latin;

        public static ScriptProfile FromManifest(EraStore.Manifest? manifest)
        {
            var bits = manifest?.OptionsBits ?? (EraStore.ManifestOptionsBits.HasJapanese
                | EraStore.ManifestOptionsBits.HasKorean
                | EraStore.ManifestOptionsBits.HasHan
                | EraStore.ManifestOptionsBits.HasLatin);
            bool Has(int bit) => (bits & bit) != 0;
            return new ScriptProfile
            {
                Japanese = Has(EraStore.ManifestOptionsBits.HasJapanese),
                Korean = Has(EraStore.ManifestOptionsBits.HasKorean),
                Han = Has(EraStore.ManifestOptionsBits.HasHan),
                Latin = Has(EraStore.ManifestOptionsBits.HasLatin),
            };
        }

        public void Add(DocTokens doc)
        {
            if (Japanese && Korean && Han && Latin) return;
            Scan(doc.TitleTok);
            Scan(doc.ArtistTok);
            Scan(doc.AlbumTok);
            Scan(doc.FilenameTok);
            Scan(doc.LyricsTok);
            Scan(doc.LyricsTrTok);
            Scan(doc.LyricsRomTok);
        }

        private void Scan(string text)
        {
            foreach (var ch in text)
            {
                if (!Han && ScriptRanges.IsCjk(ch)) Han = true;
                else if (!Japanese && ScriptRanges.IsKana(ch)) Japanese = true;
                else if (!Korean && ScriptRanges.IsHangul(ch)) Korean = true;
                else if (!Latin && ScriptRanges.IsLatinLetter(ch)) Latin = true;
            }
        }
    }
}

/* ------------------------------------------------------------------ */
/* 歌词层共享预处理（T3.5 去重 / T3.8 V6 护栏）                        */
/* ------------------------------------------------------------------ */

/// <summary>
/// 一个歌词层（原文 / 译文 / 音译）预处理后的结果。
///
/// 统计项供 <see cref="LyricsStats"/> 与索引器共用：去重键、护栏触发口径必须**同源**，
/// 否则 CLI 诊断会与真实索引行为漂移。
/// </summary>
internal readonly struct LyricLayerText
{
    /// <summary>去重 + 护栏后的文本（<c>\n</c> 连接；空层为空串）。</summary>
    public string Text { get; init; }

    /// <summary>trim 后非空的行数（去重前；<c>dedupeRatio</c> 的分子）。</summary>
    public int RawLines { get; init; }

    /// <summary>归一化键去重后的行数（**截断前**；护栏判定的依据）。</summary>
    public int UniqueLines { get; init; }

    /// <summary>去重后、截断前的 UTF-8 字节数（护栏判定的依据）。</summary>
    public int DedupedBytes { get; init; }

    /// <summary>实际保留文本的 UTF-8 字节数。</summary>
    public int RetainedBytes { get; init; }

    /// <summary>唯一行数超过 <see cref="LyricsPreprocess.MaxUniqueLines"/>（已截尾）。</summary>
    public bool TruncatedByLines { get; init; }

    /// <summary>去重后文本超过 <see cref="LyricsPreprocess.MaxUtf8Bytes"/>（已截尾）。</summary>
    public bool TruncatedByBytes { get; init; }

    /// <summary>该层是否被任一护栏截断。</summary>
    public bool Truncated => TruncatedByLines || TruncatedByBytes;
}

/// <summary>
/// 歌词层共享预处理（T3.5 / T3.8 V6）：**去重**与**病态文本护栏**。
///
/// <para><b>为什么需要去重</b>：真实歌词里副歌 / 重复句极多，同一行文本在倒排里
/// 每出现一次就多一份 token + 位置（tf 与体积双涨），但检索语义完全等价。按行去重
/// 是无损压缩：命中集合与短语语义不变，只是不再重复存储。</para>
///
/// <para><b>去重键</b>：每行先 <see cref="string.Trim()"/>，再用
/// <see cref="Analyzer.Normalize"/> 归一（NFKC 兼容折叠 → 音标 / 异体折叠 → 小写 →
/// 繁转简），与索引词元的归一**同一函数**。故「大小写 / 全半角 / 异体 / 繁简」不同的行
/// 收敛为同一键，只保留**首次出现**（顺序 = 原文行序）；不同行不会误合并。
/// **只在单层内去重**，原文 / 译文 / 音译三层互不影响。</para>
///
/// <para><b>护栏</b>（§8.1 V6，逐层独立）：去重后最多
/// <see cref="MaxUniqueLines"/> 个唯一行、最多 <see cref="MaxUtf8Bytes"/> UTF-8 字节。
/// 超限从**尾部**截断（行数先截，再按 UTF-8 rune 边界截字节），并置对应 trigger。
/// 这一步是有损的，只对病态输入（脚本灌入 / 整页抓取）兜底，正常歌词不会触发
/// （30k 合成语料实测 p50 88 / p95 135 / 最大 151 行，远低于 4096）。</para>
///
/// <para><b>调用位置</b>：<see cref="IndexerEngine"/> 的 <c>BuildTokens</c> 在
/// <see cref="Analyzer.Analyze"/> 之前对三个歌词层各调用一次；<see cref="LyricsStats"/>
/// 用同一函数统计去重率与护栏触发数。<b>不触碰</b> <c>library.db</c> 源文本，
/// 片段 / 高亮仍读库原文。</para>
/// </summary>
internal static class LyricsPreprocess
{
    /// <summary>V6 护栏：每层去重后的唯一行上限（§8.1 V6）。</summary>
    internal const int MaxUniqueLines = 4096;

    /// <summary>V6 护栏：每层去重后的 UTF-8 字节上限（128 KiB，§8.1 V6）。</summary>
    internal const int MaxUtf8Bytes = 128 * 1024;

    /// <summary>
    /// 预处理一个歌词层：trim / 丢空行 → 归一化键去重（保序）→ 行数 / 字节护栏截尾。
    /// </summary>
    public static LyricLayerText Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new LyricLayerText { Text = "" };
        }

        var rawLines = 0;
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            rawLines++;
            // 键与索引词元同源：Analyzer.Normalize（NFKC + 音标 / 异体折叠 + 小写 + 繁简）。
            if (!seen.Add(Analyzer.Normalize(line))) continue;
            unique.Add(line);
        }

        var truncatedByLines = unique.Count > MaxUniqueLines;
        var uniqueCount = unique.Count;
        if (truncatedByLines) unique.RemoveRange(MaxUniqueLines, unique.Count - MaxUniqueLines);

        var joined = string.Join('\n', unique);
        var dedupedBytes = Encoding.UTF8.GetByteCount(joined);
        var truncatedByBytes = dedupedBytes > MaxUtf8Bytes;
        var output = truncatedByBytes ? CutUtf8Tail(joined, MaxUtf8Bytes) : joined;

        return new LyricLayerText
        {
            Text = output,
            RawLines = rawLines,
            UniqueLines = uniqueCount,
            DedupedBytes = dedupedBytes,
            RetainedBytes = truncatedByBytes ? Encoding.UTF8.GetByteCount(output) : dedupedBytes,
            TruncatedByLines = truncatedByLines,
            TruncatedByBytes = truncatedByBytes,
        };
    }

    /// <summary>
    /// 按 UTF-8 字节预算截取前缀（不切断 rune / 代理对）。只保留完整行式尾部：
    /// 截断点若落在换行前会留下一个空行，这里顺手去掉末尾换行（纯格式整理，不改内容）。
    /// </summary>
    private static string CutUtf8Tail(string text, int maxBytes)
    {
        var bytes = 0;
        var utf16 = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes) break;
            bytes += size;
            utf16 += rune.Utf16SequenceLength;
        }
        return text[..utf16].TrimEnd('\n');
    }
}
