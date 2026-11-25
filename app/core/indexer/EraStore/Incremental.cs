// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;

namespace Archoera.Indexer.EraStore;

/// <summary>增量写入参数（T3.10）。</summary>
internal sealed class IncrementalOptions
{
    /// <summary>单个新段的 doc 上限（与 <see cref="MaxSegmentBytes"/> 先到先得）。</summary>
    public int DocsPerSegment = 16_000;

    /// <summary>单个新段的倒排累加器估算内存上限。</summary>
    public long MaxSegmentBytes = 32L << 20;

    /// <summary>新世代号；0 = 当前 gen + 1。</summary>
    public ulong Gen = 0;

    /// <summary>
    /// 是否在写完后按阈值（段数 ≥8 / 死行 ≥20%）自动合并。测试可关掉以观察原始段数。
    /// </summary>
    public bool Merge = true;

    /// <summary>
    /// 单次增量最多合并的源 doc 数（估算时间预算，§3.8.2.3「单次合并 ≤2s」）。
    /// 合并吞吐约 10k doc/s 量级，默认 12k 把单次合并压在 2s 内。
    /// </summary>
    public int MergeMaxDocs = 4_000;

    /// <summary>一轮增量里花在合并上的总时间预算（秒）；超时保留当前索引，下次继续。</summary>
    public double MergeBudgetSeconds = 2.0;

    /// <summary>本次构建的选项位（含语言画像）；null = 沿用旧 Manifest。</summary>
    public int? OptionsBits = null;

    /// <summary>本次构建的能力集合；null = 沿用旧 Manifest。</summary>
    public IReadOnlyList<string>? Capabilities = null;

    /// <summary>进度回调（apply / merge）；异常向上抛，调用方据此中止。</summary>
    public Action<IncrementalProgress>? Progress = null;
}

/// <summary>增量进度：阶段、已处理 upsert 数、当前段数。</summary>
internal readonly record struct IncrementalProgress(string Phase, int DocsDone, int Segments);

/// <summary>增量写入结果。</summary>
internal sealed class IncrementalResult
{
    /// <summary>提交后的世代号。</summary>
    public ulong NewGen;

    /// <summary>本次新增的段数。</summary>
    public int SegmentsAdded;

    /// <summary>本次标记为死亡的旧文档数（改写 + 删除）。</summary>
    public int DeadDocs;

    /// <summary>提交后的物理段数。</summary>
    public int SegmentCount;

    /// <summary>提交后的死行比例。</summary>
    public double DeadRatio;

    /// <summary>是否执行过自动合并。</summary>
    public bool Merged;

    /// <summary>自动合并消耗的段数（所有轮次之和）。</summary>
    public int MergedSegments;

    /// <summary>提交后 Manifest 声明的活 doc 数。</summary>
    public uint LiveDocs;
}

/// <summary>
/// EraStore 增量写入（T3.10）：**追加新段 + 墓碑标记旧版本**，不再整库重写。
///
/// <para>步骤：读 Manifest → 扫各段 DocStore 的 track_id 建立 <c>track → (seg, doc)</c>
/// 映射（只读 track 列，不碰倒排 / 歌词）→ 把 upsert 写成新段、把旧版本与删除项写成
/// 墓碑段 → 提交新 Manifest。整个过程只与脏曲数（以及一次 track 列扫描）有关，与
/// 歌词 / 倒排规模无关。</para>
///
/// <para>提交后若满足合并阈值（<see cref="Merger.ShouldMerge"/>）则做**有界合并**：
/// 单次最多合并 <see cref="IncrementalOptions.MergeMaxDocs"/> 个源 doc，整轮合并预算
/// <see cref="IncrementalOptions.MergeBudgetSeconds"/>；超预算就停，下次增量继续收敛。
/// 任何提交前异常都不会改变旧 Manifest（旧世代继续服务）。</para>
/// </summary>
internal static class IncrementalUpdater
{
    /// <summary>
    /// 把 <paramref name="upserts"/> 与 <paramref name="deletes"/> 应用到既有索引。
    /// 索引不存在 / 无有效提交抛 <see cref="InvalidDataException"/>，由调用方回退全量构建。
    /// </summary>
    public static IncrementalResult Apply(
        string indexPath,
        IReadOnlyList<AnalyzedDoc> upserts,
        IReadOnlyCollection<byte[]> deletes,
        IncrementalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);
        var opt = options ?? new IncrementalOptions();

        var path = IndexPaths.Resolve(indexPath);
        var applyWatch = Stopwatch.StartNew();
        var (segmentsAdded, deadDocs, newGen) = ApplyCore(path, upserts, deletes, opt);
        applyWatch.Stop();

        var merged = 0;
        var mergedSegments = 0;
        var mergeWatch = Stopwatch.StartNew();
        if (opt.Merge)
            (merged, mergedSegments) = MergeUntilSettled(path, opt);
        mergeWatch.Stop();
        Log.LogInfo("indexer",
            $"EraStore 增量: apply {applyWatch.ElapsedMilliseconds}ms / merge {mergeWatch.ElapsedMilliseconds}ms"
            + $"（{merged} 轮 / {mergedSegments} 段），upserts={upserts.Count} deletes={deletes.Count}");

        var final = StoreReader.TryReadManifest(path)
            ?? throw new InvalidDataException("增量提交后无法读取 Manifest");
        var segmentCount = CountSegments(final);
        return new IncrementalResult
        {
            NewGen = final.Gen == 0 ? newGen : final.Gen,
            SegmentsAdded = segmentsAdded,
            DeadDocs = deadDocs,
            SegmentCount = segmentCount,
            DeadRatio = Merger.DeadRatio(final),
            Merged = merged > 0,
            MergedSegments = mergedSegments,
            LiveDocs = final.DocCount,
        };
    }

    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 只做合并维护（没有脏曲时的定期压缩 / 垃圾回收）。返回是否合并过及最终段数。
    /// </summary>
    public static (bool Merged, int SegmentCount) Maintain(string indexPath, IncrementalOptions? options = null)
    {
        var opt = options ?? new IncrementalOptions();
        var path = IndexPaths.Resolve(indexPath);
        var (rounds, _) = MergeUntilSettled(path, opt);
        var manifest = StoreReader.TryReadManifest(path)
            ?? throw new InvalidDataException("无法读取 Manifest");
        return (rounds > 0, CountSegments(manifest));
    }

    private static (int SegmentsAdded, int DeadDocs, ulong NewGen) ApplyCore(
        string path,
        IReadOnlyList<AnalyzedDoc> upserts,
        IReadOnlyCollection<byte[]> deletes,
        IncrementalOptions opt)
    {
        using var file = IndexFile.Open(path);
        if (!file.HasCommit || file.Current is null)
            throw new InvalidDataException("EraStore 索引没有有效提交");
        var old = file.Current;

        // 1) track → (seg, doc)；只读 DocStore 的 track 列，顺带读墓碑避免把已死行当活行。
        var docsBySegment = new Dictionary<ushort, int>();
        var deadBySegment = new Dictionary<ushort, List<int>>();
        ReadSegmentLayout(file, old, docsBySegment, deadBySegment);

        var trackMap = new Dictionary<string, (ushort Segment, int Doc)>(StringComparer.Ordinal);
        foreach (var section in old.Sections)
        {
            if (section.Kind != SectionKind.DocStore) continue;
            if (deadBySegment.TryGetValue(section.SegmentId, out var deadSeg))
            {
                var store = new DocStoreReader(file.ReadSection(section));
                var deadSet = new HashSet<int>(deadSeg);
                for (var doc = 0; doc < store.Count; doc++)
                {
                    if (deadSet.Contains(doc)) continue;
                    trackMap[TrackKey(store.Track(doc))] = (section.SegmentId, doc);
                }
            }
            else
            {
                var store = new DocStoreReader(file.ReadSection(section));
                for (var doc = 0; doc < store.Count; doc++)
                    trackMap[TrackKey(store.Track(doc))] = (section.SegmentId, doc);
            }
        }

        // 2) 归并 upsert（同 track 以最后一次为准），并统计要墓碑的旧版本。
        var fresh = new Dictionary<string, AnalyzedDoc>(StringComparer.Ordinal);
        foreach (var doc in upserts)
        {
            if (doc.Track is null || doc.Track.Length != EraFormat.TrackIdBytes)
                throw new ArgumentException($"track 必须是 {EraFormat.TrackIdBytes} 字节", nameof(upserts));
            fresh[TrackKey(doc.Track)] = doc;
        }

        var deadNew = new Dictionary<ushort, HashSet<int>>();
        void Kill(ushort segment, int doc)
        {
            if (!deadNew.TryGetValue(segment, out var set))
            {
                set = new HashSet<int>();
                if (deadBySegment.TryGetValue(segment, out var existing)) set.UnionWith(existing);
                deadNew[segment] = set;
            }
            set.Add(doc);
        }

        var deletedTracks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in deletes)
        {
            if (track is null || track.Length != EraFormat.TrackIdBytes)
                throw new ArgumentException($"delete track 必须是 {EraFormat.TrackIdBytes} 字节", nameof(deletes));
            var key = TrackKey(track);
            deletedTracks.Add(key);
            if (trackMap.TryGetValue(key, out var hit)) Kill(hit.Segment, hit.Doc);
        }

        foreach (var key in fresh.Keys)
        {
            if (deletedTracks.Contains(key)) continue;
            if (trackMap.TryGetValue(key, out var hit)) Kill(hit.Segment, hit.Doc);
        }

        // 3) 丢弃整段全死的段（不写墓碑，直接不再引用；字节成为垃圾）。
        var dropped = new HashSet<ushort>();
        foreach (var (segment, dead) in deadNew)
        {
            if (docsBySegment.TryGetValue(segment, out var segmentDocs) && dead.Count >= segmentDocs)
                dropped.Add(segment);
        }
        foreach (var segment in dropped)
            deadNew.Remove(segment);

        // 4) 写出新 doc 段。
        var sections = new List<SectionRef>();
        var ordered = fresh.Values
            .Where(d => !deletedTracks.Contains(TrackKey(d.Track)))
            .OrderBy(d => TrackKey(d.Track), StringComparer.Ordinal)
            .ToList();
        var nextSegment = NextSegmentId(old);
        var (segmentsAdded, _) = IndexBuilder.WriteDocs(
            file, ordered, nextSegment, opt.DocsPerSegment, opt.MaxSegmentBytes,
            new HashSet<FieldId>(), sections);

        // 5) 写墓碑段（只对仍有活 doc 的段）；同一段先删旧墓碑引用，再追加新墓碑。
        var tombSections = new List<SectionRef>();
        foreach (var (segment, dead) in deadNew.OrderBy(pair => pair.Key))
        {
            var body = Tombstones.Write(dead.ToArray());
            var appended = file.AppendSection(SectionKind.Tombs, body, (uint)dead.Count);
            tombSections.Add(new SectionRef
            {
                Kind = appended.Kind,
                SegmentId = segment,
                Offset = appended.Offset,
                Length = appended.Length,
                Crc = appended.Crc,
                Docs = appended.Docs,
            });
        }

        // 6) 组装新 Manifest：旧段保留，替换墓碑引用，追加新段。
        var manifest = new Manifest
        {
            Gen = opt.Gen != 0 ? opt.Gen : checked(file.Gen + 1),
            Format = old.Format,
            MetaVersion = old.MetaVersion,
            Anchors = old.Anchors,
            OptionsBits = opt.OptionsBits ?? old.OptionsBits,
            DocCount = 0,
            Capabilities = opt.Capabilities is { } caps
                ? new List<string>(caps)
                : new List<string>(old.Capabilities),
        };

        foreach (var section in old.Sections)
        {
            if (dropped.Contains(section.SegmentId)) continue;
            if (section.Kind == SectionKind.Tombs && deadNew.ContainsKey(section.SegmentId)) continue;
            manifest.Sections.Add(section);
        }
        manifest.Sections.AddRange(tombSections);
        manifest.Sections.AddRange(sections);

        long liveBytes = 0;
        long docs = 0;
        foreach (var section in manifest.Sections)
        {
            liveBytes += section.Length;
            if (section.Kind == SectionKind.DocStore) docs += section.Docs;
            else if (section.Kind == SectionKind.Tombs) docs -= section.Docs;
        }
        manifest.LiveBytes = liveBytes;
        manifest.DocCount = docs < 0 ? 0 : (uint)docs;
        if (manifest.Sections.Count > Manifest.MaxSections)
            throw new InvalidOperationException(
                $"增量后的段引用 {manifest.Sections.Count} 超过 {Manifest.MaxSections}，需先合并 / 全量重建");

        IndexBuilder.FillFileBytes(manifest, file.Length);
        file.Commit(manifest);

        var deadDocs = 0;
        foreach (var dead in deadNew.Values) deadDocs += dead.Count;
        // 全段删除时也算“本轮的删除”，用于日志 / 指标。
        foreach (var segment in dropped)
            if (docsBySegment.TryGetValue(segment, out var docs2)) deadDocs += docs2;

        return (segmentsAdded, deadDocs, manifest.Gen);
    }

    /// <summary>读各段 DocStore 文档数与墓碑，供 track 映射与全段删除判定。</summary>
    private static void ReadSegmentLayout(
        IndexFile file, Manifest manifest,
        Dictionary<ushort, int> docsBySegment,
        Dictionary<ushort, List<int>> deadBySegment)
    {
        foreach (var section in manifest.Sections)
        {
            if (section.Kind == SectionKind.DocStore)
                docsBySegment[section.SegmentId] = (int)section.Docs;
            else if (section.Kind == SectionKind.Tombs && docsBySegment.TryGetValue(section.SegmentId, out var docs))
            {
                var decoded = Tombstones.Read(file.ReadSection(section), docs);
                if (deadBySegment.TryGetValue(section.SegmentId, out var existing)) existing.AddRange(decoded);
                else deadBySegment[section.SegmentId] = decoded;
            }
        }
    }

    /* ------------------------------------------------------------------ */
    /* 有界分层合并                                                        */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 按阈值做有界合并：单轮最多 <see cref="IncrementalOptions.MergeBudgetSeconds"/> 秒 /
    /// 每次最多 <see cref="IncrementalOptions.MergeMaxDocs"/> 个源 doc。返回 (合并轮数, 消耗段数)。
    /// </summary>
    private static (int Rounds, int MergedSegments) MergeUntilSettled(string path, IncrementalOptions opt)
    {
        var rounds = 0;
        var mergedSegments = 0;
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var manifest = StoreReader.TryReadManifest(path);
            if (manifest is null) break;
            var segmentCount = CountSegments(manifest);
            if (!Merger.ShouldMerge(manifest, segmentCount)) break;

            // 段引用逼近硬上限时不能再等预算：必须合并，否则下一次增量写不进去。
            var safety = manifest.Sections.Count > Manifest.MaxSections - 10;
            if (!safety && watch.Elapsed.TotalSeconds >= opt.MergeBudgetSeconds) break;

            // 分层合并：常态只归并「小段」（新写入 / 上次增量的段），避免每次更新都重写
            // 4k 级的大段；死行比例过阈值时才动大段回收空间。段数异常多时兜底放宽。
            var deadHeavy = Merger.DeadRatio(manifest) >= Merger.DeadRatioTrigger;
            var set = SelectMergeSet(manifest, opt.MergeMaxDocs, smallOnly: !deadHeavy);
            if (set.Count == 0 && segmentCount >= Merger.MergeTriggerSegments * 3)
                set = SelectMergeSet(manifest, opt.MergeMaxDocs, smallOnly: false);
            if (set.Count == 0) break;

            opt.Progress?.Invoke(new IncrementalProgress("merge", 0, segmentCount));
            var result = Merger.Merge(path, set, new MergeOptions
            {
                Gen = 0, // 当前 gen + 1
                Progress = p => opt.Progress?.Invoke(
                    new IncrementalProgress("merge-" + p.Phase, p.LiveDocs, segmentCount)),
            });
            rounds++;
            mergedSegments += set.Count;

            if (!safety && watch.Elapsed.TotalSeconds >= opt.MergeBudgetSeconds) break;
            if (result.LiveDocs == 0) break;
        }

        return (rounds, mergedSegments);
    }

    /// <summary>
    /// 选择一次合并的段集合：死行多的段优先（回收空间），否则从最小段开始（分层归并）。
    /// 返回空集合表示「只有单个且无死行的段」——不值得重写。
    /// </summary>
    internal static List<int> SelectMergeSet(Manifest manifest, int maxDocs, bool smallOnly = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (maxDocs < 1) maxDocs = 1;

        var deadBySegment = new Dictionary<ushort, int>();
        foreach (var section in manifest.Sections)
        {
            if (section.Kind == SectionKind.Tombs)
                deadBySegment[section.SegmentId] =
                    deadBySegment.TryGetValue(section.SegmentId, out var n) ? n + (int)section.Docs : (int)section.Docs;
        }

        var stats = new List<(int Segment, int Docs, int Dead)>();
        foreach (var section in manifest.Sections)
        {
            if (section.Kind != SectionKind.DocStore) continue;
            stats.Add(((int)section.SegmentId, (int)section.Docs,
                deadBySegment.TryGetValue(section.SegmentId, out var dead) ? dead : 0));
        }
        if (stats.Count == 0) return new List<int>();

        if (smallOnly)
        {
            var largest = stats.Max(s => s.Docs);
            var smallLimit = Math.Max(64, largest / 4);
            stats = stats
                .Where(s => s.Docs <= smallLimit || s.Dead > 0)
                .ToList();
            if (stats.Count == 0) return new List<int>();
        }

        stats.Sort(static (a, b) =>
        {
            var cmp = b.Dead.CompareTo(a.Dead);
            if (cmp != 0) return cmp;
            return a.Docs.CompareTo(b.Docs);
        });

        var picked = new List<int>();
        long pickedDocs = 0;
        foreach (var s in stats)
        {
            if (picked.Count > 0 && pickedDocs + s.Docs > maxDocs) break;
            picked.Add(s.Segment);
            pickedDocs += s.Docs;
            if (pickedDocs >= maxDocs) break;
        }

        if (picked.Count == 1)
        {
            // 单个大段已经吃掉预算：有死行就单独回收（空间回收）；否则不重写干净段。
            if (stats[0].Dead > 0) return picked;
            if (smallOnly) return new List<int>();
            if (stats.Count > 1)
            {
                var smallest = stats.OrderBy(s => s.Docs).Take(2).ToList();
                picked.Clear();
                foreach (var s in smallest) picked.Add(s.Segment);
                return picked;
            }
            return new List<int>();
        }
        return picked;
    }

    private static int CountSegments(Manifest manifest)
    {
        var set = new HashSet<ushort>();
        foreach (var section in manifest.Sections)
            if (section.Kind == SectionKind.DocStore) set.Add(section.SegmentId);
        return set.Count;
    }

    private static int NextSegmentId(Manifest manifest)
    {
        var max = -1;
        foreach (var section in manifest.Sections)
            if (section.SegmentId > max) max = section.SegmentId;
        if (max >= ushort.MaxValue)
            throw new InvalidOperationException("段号超过 ushort 上限，需全量重建");
        return max + 1;
    }

    /// <summary>16 字节 track → 定长十六进制串（字典键，避免自定义比较器）。</summary>
    private static string TrackKey(ReadOnlySpan<byte> track) => Convert.ToHexString(track);
}
