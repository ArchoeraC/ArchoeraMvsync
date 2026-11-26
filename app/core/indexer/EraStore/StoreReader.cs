// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// EraStore 只读查询入口（S3 / T3.0 新增）。
///
/// <para>打开 EraStore 索引文件（单文件多段），恢复最新有效提交（CommitSlot CRC 校验），
/// 暴露每段的 <see cref="DocStoreReader"/> / <see cref="TermDictReader"/> /
/// <see cref="PostingsReader"/> 三元组，供上层（<see cref="SearchSession"/>）拼装迭代器跑查询。</para>
///
/// <para>简单版：整文件一次读入 <see cref="byte"/> 数组，各 section body 以
/// <see cref="ReadOnlyMemory{byte}"/> 切片返回（索引文件通常 ≤50MB，完全在托管堆预算内；
/// 后续 benchmark 若不够，换 <c>MemoryMappedFile</c> + unsafe mmap 方案）。Dispose 时释放文件句柄。</para>
/// </summary>
internal sealed class StoreReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly MemoryMappedFile _mapped;
    private readonly MemoryMappedViewAccessor _view;
    private readonly MappedMemoryManager _manager;
    private readonly ReadOnlyMemory<byte> _file;
    private readonly Manifest _manifest;
    private readonly ushort[] _segmentIds;
    private readonly Dictionary<(SectionKind, ushort), SectionRef> _sections;
    private readonly Dictionary<ushort, HashSet<int>> _deadBySegment;

    /// <summary>各字段平均长度缓存（见 <see cref="AverageDocLengths"/>）。</summary>
    private double[]? _avgDocLens;

    /// <summary>
    /// 表达式 → 逐 atom 全局 df 缓存（T3.14）。宽命中 / 前缀展开的 df 需要扫一遍倒排，
    /// 同一代内重复查询（翻页 / 输入过程 / 基准重复）可直接复用；reader 随世代重开，
    /// 无需额外失效。上限 128 条（每条 ≤ 32 atom）。
    /// </summary>
    private readonly Dictionary<string, int[]> _dfCache = new(StringComparer.Ordinal);
    private readonly object _dfLock = new();
    private const int DfCacheCapacity = 128;

    /// <summary>逐段歌词置信度位图缓存（T4.2）：键 = 段号；与 reader 生命周期一致。</summary>
    private readonly Dictionary<ushort, LyricsMask> _lyricsMasks = new();
    private readonly object _lyricsMaskLock = new();

    /// <summary>取表达式缓存的 df；atom 数不符（表达式相同但解析结果异常）视为未命中。</summary>
    internal bool TryGetDf(
        string expression, LyricsConfidencePolicy policy, int atomCount, out int[] dfs)
    {
        var key = DfKey(expression, policy);
        lock (_dfLock)
        {
            if (_dfCache.TryGetValue(key, out var cached) && cached.Length == atomCount)
            {
                dfs = cached;
                return true;
            }
        }
        dfs = Array.Empty<int>();
        return false;
    }

    /// <summary>写入表达式 df（容量满时整表清空，避免无界增长）。</summary>
    internal void PutDf(string expression, LyricsConfidencePolicy policy, int[] dfs)
    {
        var key = DfKey(expression, policy);
        lock (_dfLock)
        {
            if (_dfCache.Count >= DfCacheCapacity) _dfCache.Clear();
            _dfCache[key] = dfs;
        }
    }

    /// <summary>
    /// df 缓存键带上置信度策略：同一表达式的「全量 df」与「过滤后 df」不同，
    /// 混用会让 bm25 的 idf 错位。
    /// </summary>
    private static string DfKey(string expression, LyricsConfidencePolicy policy)
        => policy == LyricsConfidencePolicy.None
            ? expression
            : ((char)('0' + (int)policy)).ToString() + '\u0001' + expression;

    /// <summary>某段的歌词置信度位图（首次需要时构建并缓存）。</summary>
    internal LyricsMask GetLyricsMask(SegmentReaders segment)
    {
        lock (_lyricsMaskLock)
        {
            if (_lyricsMasks.TryGetValue(segment.SegmentId, out var mask)) return mask;
            mask = LyricsMask.Build(segment.Docs);
            _lyricsMasks[segment.SegmentId] = mask;
            return mask;
        }
    }

    /// <summary>打开一个 EraStore 索引文件。</summary>
    /// <exception cref="InvalidDataException">Header / CommitSlot / Manifest 损坏</exception>
    public StoreReader(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path 不能为空", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("EraStore 索引文件不存在", path);

        // 零拷贝 mmap：查询期只做文件页缺页，匿名 RSS 不随索引体积增长（§8.0 rss_query）。
        // 同一文件被增量写入时，本视图保持打开时的长度快照；提交世代变化由 SearchSession 重开。
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _mapped = MemoryMappedFile.CreateFromFile(
            _stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _view = _mapped.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _manager = new MappedMemoryManager(_view, checked((int)_stream.Length));
        _file = _manager.Memory;
        _manifest = RecoverManifest();
        _segmentIds = _manifest.Sections
            .Where(s => s.Kind == SectionKind.DocStore)
            .Select(s => s.SegmentId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        _sections = new Dictionary<(SectionKind, ushort), SectionRef>(_manifest.Sections.Count);
        foreach (var s in _manifest.Sections)
            _sections[(s.Kind, s.SegmentId)] = s;

        // 墓碑（T3.10）：增量写入把旧版本标成死行而不重写段，查询必须按段过滤。
        _deadBySegment = new Dictionary<ushort, HashSet<int>>();
        foreach (var s in _manifest.Sections)
        {
            if (s.Kind != SectionKind.Tombs) continue;
            var docStore = _sections.TryGetValue((SectionKind.DocStore, s.SegmentId), out var docs)
                ? (int)docs.Docs
                : 0;
            var dead = new HashSet<int>(Tombstones.Read(_file.Slice((int)s.Offset, (int)s.Length), docStore));
            if (_deadBySegment.TryGetValue(s.SegmentId, out var existing)) existing.UnionWith(dead);
            else _deadBySegment[s.SegmentId] = dead;
        }
    }

    /// <summary>
    /// 只读打开 Manifest（不加载段体）。索引器用它判断 EraStore 是否缺失 / 落后 / 缺元数据；
    /// 任何 IO / 格式问题都返回 null，由调用方决定是否全量重建。
    /// </summary>
    public static Manifest? TryReadManifest(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < EraFormat.DataStart) return null;

            var header = new byte[EraFormat.HeaderSize];
            stream.ReadExactly(header);
            ValidateHeader(header);

            var raw = new byte[EraFormat.CommitSlotCount * EraFormat.CommitSlotSize];
            stream.ReadExactly(raw);

            CommitSlot? best = null;
            Manifest? bestManifest = null;
            var hasBest = false;

            for (var i = 0; i < EraFormat.CommitSlotCount; i++)
            {
                var slot = ParseSlot(raw.AsSpan(i * EraFormat.CommitSlotSize, EraFormat.CommitSlotSize));
                if (slot.Gen < 1 || slot.ManifestLength == 0
                    || slot.ManifestLength > EraFormat.MaxManifestBytes
                    || slot.ManifestOffset < EraFormat.DataStart) continue;

                var end = slot.ManifestOffset + slot.ManifestLength;
                if (end < slot.ManifestOffset || end > (ulong)stream.Length) continue;

                stream.Position = (long)slot.ManifestOffset;
                var bytes = new byte[slot.ManifestLength];
                stream.ReadExactly(bytes);
                if (Crc32C.Compute(bytes) != slot.ManifestCrc) continue;

                Manifest manifest;
                try { manifest = Manifest.Parse(bytes); }
                catch (InvalidDataException) { continue; }
                if (manifest.Gen != slot.Gen) continue;

                if (!hasBest || slot.SlotSeq > best!.SlotSeq)
                {
                    best = slot;
                    bestManifest = manifest;
                    hasBest = true;
                }
            }
            return bestManifest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// 只读最新有效提交的 gen（不解析 Manifest）：供查询会话判断同一文件是否被增量更新。
    /// 文件缺失 / 损坏返回 false。
    /// </summary>
    public static bool TryReadCommitGen(string path, out ulong gen)
    {
        gen = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < EraFormat.DataStart) return false;

            var header = new byte[EraFormat.HeaderSize];
            stream.ReadExactly(header);
            ValidateHeader(header);

            var raw = new byte[EraFormat.CommitSlotCount * EraFormat.CommitSlotSize];
            stream.ReadExactly(raw);

            var bestSeq = ulong.MinValue;
            var found = false;
            for (var i = 0; i < EraFormat.CommitSlotCount; i++)
            {
                var slot = ParseSlot(raw.AsSpan(i * EraFormat.CommitSlotSize, EraFormat.CommitSlotSize));
                if (slot.Gen < 1 || slot.ManifestLength == 0
                    || slot.ManifestOffset < EraFormat.DataStart
                    || slot.ManifestOffset + slot.ManifestLength > (ulong)stream.Length) continue;
                if (!found || slot.SlotSeq >= bestSeq)
                {
                    bestSeq = slot.SlotSeq;
                    gen = slot.Gen;
                    found = true;
                }
            }
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>当前世代号（≥1 表示有有效提交）。</summary>
    public ulong Gen => _manifest.Gen;

    /// <summary>段数（DocStore 段数 = 物理段数）。</summary>
    public int SegmentCount => _segmentIds.Length;

    /// <summary>所有段 doc 数之和（bm25 需要）。</summary>
    public uint TotalDocs => _manifest.DocCount;

    /// <summary>原始 Manifest（供上层做 anchors 检查）。</summary>
    public Manifest Manifest => _manifest;

    /// <summary>
    /// 取某段的某类 section body（只读视图，引用整文件 byte[] 切片，零拷贝）。
    /// </summary>
    public ReadOnlyMemory<byte> GetSectionBody(SectionKind kind, ushort segmentId)
    {
        if (!_sections.TryGetValue((kind, segmentId), out var section))
            throw new InvalidOperationException($"缺少 {kind}（seg={segmentId}）");
        return _file.Slice((int)section.Offset, (int)section.Length);
    }

    /// <summary>
    /// 可选段体：存在时返回切片，否则返回空。用于 <see cref="SectionKind.Positions"/>——
    /// 一个段的所有词条都免位置时，Builder 不写 Positions 段（不是损坏）；
    /// 空内存交给 <see cref="PostingsReader"/> 时按「该段没有任何位置块」处理。
    /// </summary>
    public bool TryGetSectionBody(
        SectionKind kind, ushort segmentId, out ReadOnlyMemory<byte> body)
    {
        if (_sections.TryGetValue((kind, segmentId), out var section))
        {
            body = _file.Slice((int)section.Offset, (int)section.Length);
            return true;
        }
        body = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    /// <summary>
    /// 按 Manifest 里的段引用直接取段体（供去碎片等「逐引用原样复制」的路径使用：
    /// 不经过 <c>(kind, seg)</c> 字典，重复引用 / 未知顺序也能逐条复制）。
    /// </summary>
    public ReadOnlyMemory<byte> GetSectionBody(SectionRef section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var end = section.Offset + section.Length;
        if (end < section.Offset || end > (ulong)_file.Length)
            throw new InvalidDataException($"段引用越界（off={section.Offset}, len={section.Length}）");
        return _file.Slice((int)section.Offset, (int)section.Length);
    }

    /// <summary>
    /// 读取全部活文档的增量状态（T3.10）：<c>track_id → mtime / 侧车锚点</c>。
    /// 只读 DocStore 的 track 与状态列，不碰倒排 / 歌词；避免生产增量再依赖 SQLite 状态表。
    /// </summary>
    public Dictionary<string, EraTrackState> LoadTrackState()
    {
        var state = new Dictionary<string, EraTrackState>(StringComparer.Ordinal);
        foreach (var seg in OpenPerSegment())
        {
            var count = seg.Docs.Count;
            for (var doc = 0; doc < count; doc++)
            {
                if (!seg.IsLive(doc)) continue;
                var id = seg.Docs.TrackId(doc);
                if (id.Length == 0) continue;
                state[id] = new EraTrackState
                {
                    Mtime = seg.Docs.Mtime(doc),
                    LrcMtime = seg.Docs.LrcMtime(doc),
                    LrcSize = seg.Docs.LrcSize(doc),
                };
            }
        }
        return state;
    }

    /// <summary>每段三元组（按 SegmentId 升序）。</summary>
    public List<SegmentReaders> OpenPerSegment()
    {
        var list = new List<SegmentReaders>(_segmentIds.Length);
        foreach (var seg in _segmentIds)
        {
            // Positions 是可选段：整段所有词条免位置时不存在（Builder 只在非空时追加）。
            // 空内存 = 该段没有位置块；DocStore / Dict / DictIndex / Postings 仍必须存在。
            TryGetSectionBody(SectionKind.Positions, seg, out var positions);
            list.Add(new SegmentReaders(
                seg,
                new DocStoreReader(GetSectionBody(SectionKind.DocStore, seg)),
                new TermDictReader(
                    GetSectionBody(SectionKind.Dict, seg),
                    GetSectionBody(SectionKind.DictIndex, seg)),
                new PostingsReader(
                    GetSectionBody(SectionKind.Postings, seg),
                    positions),
                _deadBySegment.TryGetValue(seg, out var dead) ? dead : null));
        }
        return list;
    }

    /// <summary>
    /// 各字段平均文档长度（下标 = FieldId - 1），bm25 的 <c>avgdl</c>。
    ///
    /// <para>对齐 FTS5 <c>bm25()</c> 的语义：某列的平均长度 = 全库该列 token 总数 / 行数
    /// （**空列也计入分母**，用 10 行实测验证过）。首次调用时全量扫描 DocLen 表，之后缓存。</para>
    /// </summary>
    public ReadOnlySpan<double> AverageDocLengths()
    {
        if (_avgDocLens is not null) return _avgDocLens;

        var sums = new long[12];
        var docs = OpenPerSegment();
        foreach (var seg in docs)
        {
            var count = seg.Docs.Count;
            for (var doc = 0; doc < count; doc++)
            {
                if (!seg.IsLive(doc)) continue;
                for (var fi = 0; fi < 12; fi++)
                    sums[fi] += seg.Docs.DocLen(doc, (FieldId)(fi + 1));
            }
        }

        var avg = new double[12];
        if (TotalDocs > 0)
        {
            for (var fi = 0; fi < 12; fi++)
                avg[fi] = (double)sums[fi] / TotalDocs;
        }
        _avgDocLens = avg;
        return avg;
    }

    public void Dispose()
    {
        ((IDisposable)_manager).Dispose();
        _view.Dispose();
        _mapped.Dispose();
        // CreateFromFile 用了 leaveOpen: true：FileStream 必须在这里显式关闭。
        // 否则每次索引更新（打开新世代 / 读增量状态 / 合并 / 去碎片）都会漏一个
        // 文件句柄，长期运行后表现为句柄数上涨、Windows 上旧世代文件删不掉。
        _stream.Dispose();
    }

    /* ------------------------------------------------------------------ */
    /* Manifest 恢复（复用 EraStore.IndexFile 的 CommitSlot 逻辑）          */
    /* ------------------------------------------------------------------ */

    private Manifest RecoverManifest()
    {
        if (_file.Length < EraFormat.DataStart)
            throw new InvalidDataException("EraStore 索引文件短于 Header + 双槽");

        ValidateHeader(_file.Span[..EraFormat.HeaderSize]);

        CommitSlot? best = null;
        Manifest? bestManifest = null;
        var hasBest = false;

        for (var i = 0; i < EraFormat.CommitSlotCount; i++)
        {
            CommitSlot slot;
            try { slot = ReadSlot(i); }
            catch (InvalidDataException) { continue; }

            var manifest = TryReadManifest(slot);
            if (manifest is null || slot.Gen != manifest.Gen) continue;

            if (!hasBest || slot.SlotSeq > best!.SlotSeq)
            {
                best = slot;
                bestManifest = manifest;
                hasBest = true;
            }
        }
        if (!hasBest || bestManifest is null)
            throw new InvalidDataException("EraStore 索引无有效提交");

        return bestManifest;
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header) => IndexFile.ValidateHeader(header);

    private CommitSlot ReadSlot(int index)
    {
        if ((uint)index >= EraFormat.CommitSlotCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        var at = EraFormat.CommitSlotOffset + index * EraFormat.CommitSlotSize;
        if (at + EraFormat.CommitSlotSize > _file.Length)
            throw new InvalidDataException("提交槽越界");
        return ParseSlot(_file.Span.Slice(at, EraFormat.CommitSlotSize));
    }

    /// <summary>解析 32B 提交槽（布局见设计文档 §3.3.0.1）。</summary>
    private static CommitSlot ParseSlot(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < EraFormat.CommitSlotSize)
            throw new InvalidDataException("提交槽长度不足");
        return new CommitSlot
        {
            Gen = BinaryPrimitives.ReadUInt64LittleEndian(raw),
            ManifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]),
            ManifestLength = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]),
            ManifestCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]),
            SlotSeq = BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]),
        };
    }

    private Manifest? TryReadManifest(CommitSlot slot)
    {
        if (slot.Gen < 1) return null;
        if (slot.ManifestLength == 0 || slot.ManifestLength > EraFormat.MaxManifestBytes) return null;
        if (slot.ManifestOffset < EraFormat.DataStart) return null;
        var end = slot.ManifestOffset + slot.ManifestLength;
        if (end < slot.ManifestOffset || end > (ulong)_file.Length) return null;

        var bytes = _file.Slice((int)slot.ManifestOffset, (int)slot.ManifestLength);
        if (Crc32C.Compute(bytes.Span) != slot.ManifestCrc) return null;
        try { return Manifest.Parse(bytes.Span); }
        catch (InvalidDataException) { return null; }
    }
}

/// <summary>
/// 把只读 mmap 视图包装成 <see cref="ReadOnlyMemory{Byte}"/>，保持既有 section 切片 API 不变。
/// 生命周期由 <see cref="StoreReader"/> 的 Dispose 负责（先释放指针，再关视图 / 映射）。
/// </summary>
internal sealed unsafe class MappedMemoryManager : MemoryManager<byte>
{
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private readonly int _length;

    public MappedMemoryManager(MemoryMappedViewAccessor view, int length)
    {
        _view = view;
        _length = length;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    public override Span<byte> GetSpan()
    {
        if (_pointer == null) throw new ObjectDisposedException(nameof(MappedMemoryManager));
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (_pointer == null) throw new ObjectDisposedException(nameof(MappedMemoryManager));
        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (_pointer == null) return;
        if (disposing) _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null;
    }
}

/// <summary>一条活文档的增量状态（DocStore v2 状态列）。</summary>
internal sealed class EraTrackState
{
    public long Mtime { get; set; }
    public long LrcMtime { get; set; }
    public long LrcSize { get; set; }
}

/// <summary>一段 EraStore 的三元组（DocStore + TermDict + Postings）。</summary>
internal sealed class SegmentReaders
{
    public readonly ushort SegmentId;
    public readonly DocStoreReader Docs;
    public readonly TermDictReader Dict;
    public readonly PostingsReader Postings;

    /// <summary>该段墓碑标记的死 doc（null = 无墓碑）。</summary>
    private readonly HashSet<int>? _dead;

    public SegmentReaders(
        ushort segmentId, DocStoreReader docs, TermDictReader dict, PostingsReader postings,
        HashSet<int>? dead = null)
    {
        SegmentId = segmentId;
        Docs = docs;
        Dict = dict;
        Postings = postings;
        _dead = dead;
    }

    /// <summary>该 doc 是否仍是活行（T3.10：增量写入只加墓碑、不重写旧段）。</summary>
    public bool IsLive(int doc) => _dead is null || !_dead.Contains(doc);
}
