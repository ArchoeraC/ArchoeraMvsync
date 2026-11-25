// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>合并参数（设计文档 §3.8.2.3）。</summary>
internal sealed class MergeOptions
{
    /// <summary>新世代号；0 = 当前 gen + 1。</summary>
    public ulong Gen = 0;

    /// <summary>阶段回调（read / write / commit）；异常向上抛，调用方据此中止。</summary>
    public Action<MergeProgress>? Progress = null;
}

/// <summary>合并进度：阶段名、已处理的源段数、当前活 doc 数。</summary>
internal readonly record struct MergeProgress(string Phase, int SegmentsDone, int LiveDocs);

/// <summary>合并结果。</summary>
internal sealed class MergeResult
{
    /// <summary>提交后的世代号。</summary>
    public ulong NewGen;

    /// <summary>新段号。</summary>
    public int NewSegment;

    /// <summary>合并后的活 doc 数。</summary>
    public int LiveDocs;

    /// <summary>丢弃的墓碑行数。</summary>
    public int DroppedDead;

    /// <summary>因重复 track_id 丢弃的旧副本数。</summary>
    public int DroppedDuplicateTracks;
}

/// <summary>
/// 段合并 / 压缩（设计文档 §3.3.0.7 / §3.8.2.3）。
///
/// 规则：
/// <list type="bullet">
///   <item><b>只搬活 doc</b>：丢弃墓碑行，计入 <see cref="MergeResult.DroppedDead"/>；</item>
///   <item>按 term 归并已有倒排 / 位置，不重新分词，重算 df / max_tf；</item>
///   <item>活 doc 按 track_id 字节序从 0 编号；同一 track 保留 (源段号, 源 doc) 最大的副本；</item>
///   <item>向同一世代文件追加新段，新 Manifest 不再引用旧段（旧段字节成为垃圾）；</item>
///   <item><b>原子性</b>：<see cref="IndexFile.Commit"/> 之前任何异常都不改动旧 Manifest / 槽。</item>
/// </list>
/// </summary>
internal static class Merger
{
    /// <summary>
    /// 段数触发阈值。设计稿写 8，但真实中文歌词按 4 MB 累加器落段时单段约 4k 首；
    /// 8 段阈值会让每次增量（+1 段）都重写一个 4k 段（实测 1.2–1.4 s）。改 24 后
    /// 增量先积累小段，到 24 段再一次性归并（死行 ≥20% 仍立即触发，不受此限）。
    /// 全量构建后的自动压缩（<see cref="BuildCompactor"/>）复用同一阈值：
    /// 低于 24 段不做额外重写，高于 24 段也只做有界归并。
    /// </summary>
    public const int MergeTriggerSegments = 24;

    /// <summary>死行比例触发阈值。</summary>
    public const double DeadRatioTrigger = 0.20;

    /// <summary>硬上限：合并后总段数（每段最多 5 条 section 引用）。</summary>
    public const int MaxSegments = Manifest.MaxSections / 5;

    /// <summary>
    /// 是否需要压缩：段数 ≥8、超过 <paramref name="maxSegments"/>，或死行比例 ≥20%。
    /// 死行比例只读 Manifest 段引用（<c>Tombs.Docs / DocStore.Docs</c>），不读段体。
    /// </summary>
    public static bool ShouldMerge(Manifest manifest, int segmentCount, int maxSegments = MaxSegments)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (segmentCount >= MergeTriggerSegments || segmentCount > maxSegments) return true;
        return DeadRatio(manifest) >= DeadRatioTrigger;
    }

    /// <summary>从 Manifest 段引用估算死行比例 <c>dead / (live + dead)</c>。</summary>
    public static double DeadRatio(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        long total = 0;
        long dead = 0;
        foreach (var section in manifest.Sections)
        {
            if (section.Kind == SectionKind.DocStore) total += section.Docs;
            else if (section.Kind == SectionKind.Tombs) dead += section.Docs;
        }
        if (total <= 0) return 0;
        return Math.Min(1.0, (double)dead / total);
    }

    /// <summary>
    /// 合并 <paramref name="segmentIds"/> 指定的段，产出同文件内追加的新段并提交新 Manifest。
    /// 输入非法抛 <see cref="ArgumentException"/>；提交前异常不改变旧索引。
    /// </summary>
    public static MergeResult Merge(
        string indexPath, IReadOnlyList<int> segmentIds, MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
            throw new ArgumentException("至少需要合并一个段", nameof(segmentIds));

        var requested = new HashSet<int>();
        foreach (var id in segmentIds)
        {
            if (id < 0 || id > ushort.MaxValue)
                throw new ArgumentException($"段号 {id} 超出 0..65535", nameof(segmentIds));
            if (!requested.Add(id))
                throw new ArgumentException($"段号 {id} 重复", nameof(segmentIds));
        }

        var opt = options ?? new MergeOptions();
        var path = IndexPaths.Resolve(indexPath);

        using var reader = new StoreReader(path);
        var old = reader.Manifest;
        using var file = IndexFile.Open(path);
        if (!file.HasCommit || file.Current is null)
            throw new InvalidDataException("索引没有有效提交");
        if (file.Gen != old.Gen)
            throw new InvalidDataException("索引在打开期间被并发提交");
        var existing = old.Sections.Select(s => (int)s.SegmentId).Distinct().ToArray();
        foreach (var id in requested)
            if (Array.IndexOf(existing, id) < 0)
                throw new ArgumentException($"段号 {id} 不存在", nameof(segmentIds));

        var newSegmentCount = existing.Length - requested.Count + 1;
        if (newSegmentCount > MaxSegments)
            throw new InvalidOperationException($"合并后段数 {newSegmentCount} 超过 {MaxSegments}");
        var maxSegment = existing.Length == 0 ? -1 : existing.Max();
        if (maxSegment >= ushort.MaxValue)
            throw new InvalidOperationException("段号超过 ushort 上限");

        var newGen = opt.Gen != 0 ? opt.Gen : checked(file.Gen + 1);
        if (newGen <= file.Gen)
            throw new ArgumentException($"新 gen {newGen} 必须大于当前 {file.Gen}", nameof(options));

        Progress(opt, "read", 0, 0);

        var sources = new List<Source>(requested.Count);
        var droppedDead = 0;
        foreach (var id in requested.Order())
        {
            var source = ReadSource(reader, old, (ushort)id);
            droppedDead += source.Dead.Count;
            sources.Add(source);
            var liveSoFar = 0;
            foreach (var s in sources) liveSoFar += s.Store.Count - s.Dead.Count;
            Progress(opt, "read", sources.Count, liveSoFar);
        }

        var (kept, duplicates) = Deduplicate(sources);

        var newDoc = new Dictionary<(int Segment, int Doc), int>(kept.Count);
        var records = new List<DocRecord>(kept.Count);
        var storeBySegment = new Dictionary<int, DocStoreReader>(sources.Count);
        foreach (var source in sources) storeBySegment[source.SegmentId] = source.Store;
        for (var i = 0; i < kept.Count; i++)
        {
            var candidate = kept[i];
            newDoc[(candidate.Segment, candidate.Doc)] = i;
            records.Add(MakeRecord(storeBySegment[candidate.Segment], candidate.Doc, candidate.Track));
        }

        Progress(opt, "write", sources.Count, kept.Count);

        var newSegmentId = (ushort)(maxSegment + 1);
        var newSections = new List<SectionRef>();
        IndexBuilder.WriteSegmentStreaming(
            file, records, newSegmentId,
            (dict, postings, positions, writer) =>
            {
                foreach (var (key, entries) in MergeTerms(sources, newDoc))
                {
                    var offset = writer.Write(postings, positions, entries);
                    dict.AddKey((FieldId)key[0], key.AsSpan(1), (uint)entries.Count, offset,
                        entries[0].Positions is not null ? (byte)0x01 : (byte)0);
                }
            },
            newSections);

        var manifest = BuildManifest(old, requested, newGen, newSections);
        IndexBuilder.FillFileBytes(manifest, file.Length);

        Progress(opt, "commit", sources.Count, kept.Count);
        file.Commit(manifest);

        return new MergeResult
        {
            NewGen = newGen,
            NewSegment = newSegmentId,
            LiveDocs = kept.Count,
            DroppedDead = droppedDead,
            DroppedDuplicateTracks = duplicates,
        };
    }

    /* ------------------------------------------------------------------ */

    private static void Progress(MergeOptions options, string phase, int segmentsDone, int liveDocs) =>
        options.Progress?.Invoke(new MergeProgress(phase, segmentsDone, liveDocs));

    /// <summary>活 doc 候选：(源段号, 源 doc, track)。</summary>
    private readonly record struct Candidate(int Segment, int Doc, byte[] Track);

    private sealed class Source
    {
        public ushort SegmentId;
        public DocStoreReader Store = null!;
        public ReadOnlyMemory<byte> Dict;
        public ReadOnlyMemory<byte> DictIndex;
        public ReadOnlyMemory<byte> Postings;
        public ReadOnlyMemory<byte> Positions;
        public HashSet<int> Dead = new();
    }

    private static Source ReadSource(StoreReader reader, Manifest manifest, ushort segmentId)
    {
        var storeRef = Find(manifest, segmentId, SectionKind.DocStore)
            ?? throw new InvalidDataException($"seg={segmentId} 缺少 DocStore 段");
        var store = new DocStoreReader(reader.GetSectionBody(SectionKind.DocStore, segmentId));
        if (store.Count != storeRef.Docs)
            throw new InvalidDataException($"seg={segmentId} DocStore 文档数与声明不符");

        var source = new Source
        {
            SegmentId = segmentId,
            Store = store,
            Dict = reader.GetSectionBody(SectionKind.Dict, segmentId),
            DictIndex = reader.GetSectionBody(SectionKind.DictIndex, segmentId),
            Postings = reader.GetSectionBody(SectionKind.Postings, segmentId),
        };

        if (Find(manifest, segmentId, SectionKind.Positions) is not null)
            source.Positions = reader.GetSectionBody(SectionKind.Positions, segmentId);

        if (Find(manifest, segmentId, SectionKind.Tombs) is not null)
        {
            foreach (var doc in Tombstones.Read(
                         reader.GetSectionBody(SectionKind.Tombs, segmentId), store.Count))
                source.Dead.Add(doc);
        }
        return source;
    }

    /// <summary>
    /// 按 track 去重并确定新 doc 顺序：track 字节序升序，同 track 时 (源段号, 源 doc) 升序兜底。
    /// 重复 track 保留 (源段号, 源 doc) 最大的副本（最新写入）。
    /// </summary>
    private static (List<Candidate> Kept, int Duplicates) Deduplicate(List<Source> sources)
    {
        var byTrack = new Dictionary<TrackKey, Candidate>();
        var duplicates = 0;

        foreach (var source in sources)
        {
            for (var doc = 0; doc < source.Store.Count; doc++)
            {
                if (source.Dead.Contains(doc)) continue;
                var candidate = new Candidate(
                    source.SegmentId, doc, source.Store.Track(doc).ToArray());
                var key = new TrackKey(candidate.Track);
                if (byTrack.ContainsKey(key)) duplicates++;
                byTrack[key] = candidate;
            }
        }

        var kept = new List<Candidate>(byTrack.Count);
        foreach (var candidate in byTrack.Values) kept.Add(candidate);
        kept.Sort(static (a, b) =>
        {
            var cmp = a.Track.AsSpan().SequenceCompareTo(b.Track);
            if (cmp != 0) return cmp;
            cmp = a.Segment.CompareTo(b.Segment);
            if (cmp != 0) return cmp;
            return a.Doc.CompareTo(b.Doc);
        });
        return (kept, duplicates);
    }

    private static DocRecord MakeRecord(DocStoreReader store, int doc, byte[] track)
    {
        // v3 归一列从旧段原样拷贝（不调用 Analyzer.NormalizeMetadata）：合并只搬物理数据，
        // 重复归一既浪费又会在归一规则演进时悄悄改写列内容。
        var norm = store.NormSpans(doc);
        var record = new DocRecord
        {
            Track = track,
            Rev = store.Rev(doc).ToArray(),
            ScriptBits = store.ScriptBits(doc),
            Flags = store.Flags(doc),
            LayerConf = store.LayerConf(doc),
            Title = store.Title(doc),
            Artist = store.Artist(doc),
            Album = store.Album(doc),
            TitleNorm = Encoding.UTF8.GetString(norm.Title),
            ArtistNorm = Encoding.UTF8.GetString(norm.Artist),
            AlbumNorm = Encoding.UTF8.GetString(norm.Album),
            Mtime = store.Mtime(doc),
            LrcMtime = store.LrcMtime(doc),
            LrcSize = store.LrcSize(doc),
        };
        for (var field = 1; field <= 12; field++)
            record.DocLen[field - 1] = store.DocLen(doc, (FieldId)field);
        return record;
    }

    /// <summary>
    /// 流式 k 路归并各源段倒排：词典本身按编码键有序，逐词条取同一键的所有 posting，
    /// 映射到新 doc 后立即交给写出器。内存只与「单个词条的跨段 posting」成正比，
    /// 不再把全段「term → postings」物化（T3.10 合并 RSS 实测从 ~1.1 GB 降到可控）。
    /// </summary>
    private static IEnumerable<(byte[] Key, List<PostingEntry> Entries)> MergeTerms(
        List<Source> sources, Dictionary<(int Segment, int Doc), int> newDoc)
    {
        var readers = new List<(Source Source, IEnumerator<TermInfo> Terms, PostingsReader Postings)>();
        // 段号 → 词典游标：每个词条 pop 后都要重新入队该源的下一个词条，这里必须是 O(1)
        // 查找。全量构建后的自动压缩会一次归并数百个小段（数百个游标），线性 Find
        // 会把合并成本推高数倍（T3.15 实测 286 段 × ~50 万词条的 Find 占比明显）。
        var cursors = new Dictionary<int, IEnumerator<TermInfo>>(sources.Count);
        var queue = new PriorityQueue<(Source Source, TermInfo Term, PostingsReader Postings), byte[]>(
            ByteArrayPriorityComparer.Instance);

        foreach (var source in sources)
        {
            var terms = new TermDictReader(source.Dict, source.DictIndex).Enumerate().GetEnumerator();
            var postings = new PostingsReader(source.Postings, source.Positions);
            if (terms.MoveNext())
                queue.Enqueue((source, terms.Current, postings), terms.Current.Key);
            readers.Add((source, terms, postings));
            cursors[source.SegmentId] = terms;
        }

        try
        {
            while (queue.TryPeek(out _, out var key))
            {
                var entries = new List<PostingEntry>();
                while (queue.TryPeek(out _, out var next) && next.AsSpan().SequenceEqual(key))
                {
                    var (source, term, postings) = queue.Dequeue();
                    foreach (var entry in postings.DecodeAll(term.PostOffset, term.Df))
                    {
                        if (!newDoc.TryGetValue((source.SegmentId, entry.Doc), out var mapped))
                            continue;
                        entries.Add(new PostingEntry(mapped, entry.Tf, entry.Positions));
                    }
                    // 该源的下一个词条重新入队。
                    var enumerator = cursors[source.SegmentId];
                    if (enumerator.MoveNext())
                        queue.Enqueue((source, enumerator.Current, postings), enumerator.Current.Key);
                }

                if (entries.Count == 0) continue; // 该词条只存在于被丢弃的旧副本
                entries.Sort(static (a, b) => a.Doc.CompareTo(b.Doc));
                yield return (key, entries);
            }
        }
        finally
        {
            foreach (var (_, terms, _) in readers) terms.Dispose();
        }
    }

    /// <summary>byte[] 键的字节序优先队列比较器（键在入队后不再修改）。</summary>
    private sealed class ByteArrayPriorityComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayPriorityComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y);
        }
    }

    private static Manifest BuildManifest(
        Manifest old, HashSet<int> merged, ulong gen, List<SectionRef> newSections)
    {
        var manifest = new Manifest
        {
            Gen = gen,
            Format = old.Format,
            // 合并只搬物理数据，查询元数据（T3.12）必须原样保留；丢失会让下一次增量
            // 误判「元数据过旧」而整库重建。
            MetaVersion = old.MetaVersion,
            Anchors = old.Anchors,
            OptionsBits = old.OptionsBits,
            Capabilities = new List<string>(old.Capabilities),
        };

        long liveBytes = 0;
        long liveDocs = 0;
        foreach (var section in old.Sections)
        {
            if (merged.Contains(section.SegmentId)) continue;
            manifest.Sections.Add(section);
            liveBytes += section.Length;
            if (section.Kind == SectionKind.DocStore) liveDocs += section.Docs;
            else if (section.Kind == SectionKind.Tombs) liveDocs -= section.Docs;
        }

        foreach (var section in newSections)
        {
            manifest.Sections.Add(section);
            liveBytes += section.Length;
            if (section.Kind == SectionKind.DocStore) liveDocs += section.Docs;
        }

        manifest.LiveBytes = liveBytes;
        manifest.DocCount = liveDocs < 0 ? 0 : (uint)liveDocs;
        return manifest;
    }

    private static SectionRef? Find(Manifest manifest, ushort segmentId, SectionKind kind)
    {
        foreach (var section in manifest.Sections)
            if (section.SegmentId == segmentId && section.Kind == kind) return section;
        return null;
    }

    /// <summary>track_id 内容相等的字典键（HashCode 随机种子不影响输出，输出前显式排序）。</summary>
    private readonly struct TrackKey : IEquatable<TrackKey>
    {
        private readonly byte[] _track;

        public TrackKey(byte[] track) => _track = track;

        public bool Equals(TrackKey other) => _track.AsSpan().SequenceEqual(other._track);

        public override bool Equals(object? obj) => obj is TrackKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(_track);
            return hash.ToHashCode();
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
