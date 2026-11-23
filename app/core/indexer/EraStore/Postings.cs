// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 一条倒排记录（写入侧）：<paramref name="Doc"/> 为段内 local doc id，
/// <paramref name="Tf"/> 为词频；<paramref name="Positions"/> 为空表示该字段无位置，
/// 非空时长度必须等于 <paramref name="Tf"/> 且严格递增。
/// </summary>
internal readonly record struct PostingEntry(int Doc, int Tf, int[]? Positions);

/// <summary>
/// 倒排 / 位置块写入（设计文档 §3.3.0.3）。
///
/// 倒排块（每 ≤128 doc 一块，块体顺序固定）：
/// <code>
/// 块头 14B : doc_count u16 | last_doc u32 | max_tf u16 | flags u8 | crc32c u32 | tf_exc_count u8
/// tf 位图 16B : bit i = 1 表示块内第 i 个 doc 的 tf == 1
/// doc 序列    : doc_delta varint（首条相对 0，之后与前一条的差）
/// tf 例外表   : doc_index u8 | tf varint（仅 tf &gt; 1）
/// pos_off     : u32，该块位置块在 Positions 段内的偏移；无位置写 0
/// </code>
///
/// 位置块（仅 has_positions，与倒排块按相同 doc 顺序成对写出）：
/// <code>
/// 块头 12B : doc_count u16 | last_doc u32 | flags u8 | reserved u8 | crc32c u32
/// 块体     : 每 doc：doc_delta varint（首条相对 0）| pos_count varint | 逐个位置 Δvarint
/// </code>
/// CRC 均覆盖块头之后到块尾的全部字节；块内偏移是本块内相对量，跳块不依赖上一块。
/// </summary>
internal sealed class PostingsWriter
{
    private const int BitmapSize = 16;
    private const byte FlagHasPositions = 0x01;

    /// <summary>块体复用缓冲：避免每 128 doc 新建两个 ByteWriter（构建期主要分配来源之一）。</summary>
    private readonly ByteWriter _postBody = new(512);
    private readonly ByteWriter _posBody = new(2_048);

    /// <summary>
    /// 追加一条词条的倒排块序列（对象形式，合并 / 测试用），返回首个倒排块在
    /// <paramref name="postings"/> 段内的偏移。
    /// 输入非法（doc 非严格递增 / tf 越界 / 位置与 tf 不一致等）抛 <see cref="ArgumentException"/>。
    /// </summary>
    public static uint WriteTerm(ByteWriter postings, ByteWriter positions, IReadOnlyList<PostingEntry> entries) =>
        new PostingsWriter().Write(postings, positions, entries);

    /// <summary>
    /// 追加一条词条的倒排块序列（对象形式，合并 / 测试用），返回首个倒排块在
    /// <paramref name="postings"/> 段内的偏移。
    /// </summary>
    public uint Write(ByteWriter postings, ByteWriter positions, IReadOnlyList<PostingEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new ArgumentException("词条至少需要一个 posting", nameof(entries));

        var hasPositions = entries[0].Positions is not null;
        var prevDoc = -1;
        var flat = new List<int>();
        foreach (var entry in entries)
        {
            if (entry.Doc <= prevDoc)
                throw new ArgumentException("doc 必须严格递增且非负", nameof(entries));
            if (entry.Tf < 1 || entry.Tf > ushort.MaxValue)
                throw new ArgumentException("tf 必须在 1..65535", nameof(entries));
            if ((entry.Positions is not null) != hasPositions)
                throw new ArgumentException("同一词条的位置要么全有要么全无", nameof(entries));
            if (entry.Positions is not null)
            {
                if (entry.Positions.Length != entry.Tf)
                    throw new ArgumentException("位置数量必须等于 tf", nameof(entries));
                var prevPos = -1;
                foreach (var pos in entry.Positions)
                {
                    if (pos < 0 || pos <= prevPos)
                        throw new ArgumentException("位置必须非负且严格递增", nameof(entries));
                    prevPos = pos;
                }
                flat.AddRange(entry.Positions);
            }
            prevDoc = entry.Doc;
        }

        // 转成并行数组后复用热路径（合并不是热路径，额外一次拷贝可接受）。
        var count = entries.Count;
        var docs = new int[count];
        var tfs = new int[count];
        var starts = new int[count];
        var posCounts = new int[count];
        var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = entries[i];
            docs[i] = entry.Doc;
            tfs[i] = entry.Tf;
            starts[i] = cursor;
            posCounts[i] = entry.Positions?.Length ?? 0;
            cursor += posCounts[i];
        }
        return WriteTermCore(
            postings, positions, docs, tfs, starts, posCounts, flat.ToArray(), hasPositions);
    }

    /// <summary>
    /// 追加一条词条的倒排块序列（并行数组形式，构建热路径用：避免每条 posting 一个数组对象）。
    /// <paramref name="docs"/> 必须严格递增；<paramref name="posCount"/> 必须等于 tf；
    /// 位置在 <paramref name="posFlat"/> 中按 posting 连续排布且严格递增。
    /// </summary>
    public uint Write(
        ByteWriter postings,
        ByteWriter positions,
        ReadOnlySpan<int> docs,
        ReadOnlySpan<int> tfs,
        ReadOnlySpan<int> posStart,
        ReadOnlySpan<int> posCount,
        ReadOnlySpan<int> posFlat,
        bool hasPositions)
    {
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(positions);
        if (docs.Length == 0) throw new ArgumentException("词条至少需要一个 posting", nameof(docs));
        if (tfs.Length != docs.Length || posStart.Length != docs.Length || posCount.Length != docs.Length)
            throw new ArgumentException("docs / tfs / posStart / posCount 长度必须一致");

        var prevDoc = -1;
        for (var i = 0; i < docs.Length; i++)
        {
            if (docs[i] <= prevDoc)
                throw new ArgumentException("doc 必须严格递增且非负", nameof(docs));
            if (tfs[i] < 1 || tfs[i] > ushort.MaxValue)
                throw new ArgumentException("tf 必须在 1..65535", nameof(tfs));
            if (hasPositions)
            {
                if (posStart[i] < 0 || posCount[i] != tfs[i] || posStart[i] > posFlat.Length - posCount[i])
                    throw new ArgumentException("位置区间与 tf 不一致", nameof(posCount));
                var prevPos = -1;
                foreach (var pos in posFlat.Slice(posStart[i], posCount[i]))
                {
                    if (pos < 0 || pos <= prevPos)
                        throw new ArgumentException("位置必须非负且严格递增", nameof(posFlat));
                    prevPos = pos;
                }
            }
            else if (posStart[i] != 0 || posCount[i] != 0)
            {
                throw new ArgumentException("无位置词条的 posStart / posCount 必须为 0", nameof(posCount));
            }
            prevDoc = docs[i];
        }
        if (!hasPositions && posFlat.Length != 0)
            throw new ArgumentException("无位置词条不能携带位置数据", nameof(posFlat));

        return WriteTermCore(postings, positions, docs, tfs, posStart, posCount, posFlat, hasPositions);
    }

    /// <summary>块写入核心（调用方已完成校验）。</summary>
    private uint WriteTermCore(
        ByteWriter postings,
        ByteWriter positions,
        ReadOnlySpan<int> docs,
        ReadOnlySpan<int> tfs,
        ReadOnlySpan<int> posStart,
        ReadOnlySpan<int> posCount,
        ReadOnlySpan<int> posFlat,
        bool hasPositions)
    {
        var firstOffset = (uint)postings.Length;
        for (var start = 0; start < docs.Length; start += EraFormat.PostingsBlockDocs)
        {
            var count = Math.Min(EraFormat.PostingsBlockDocs, docs.Length - start);
            var posOff = 0u;
            if (hasPositions)
            {
                posOff = (uint)positions.Length;
                WritePositionsBlock(positions, docs, tfs, posStart, posCount, posFlat, start, count);
            }
            WritePostingsBlock(postings, docs, tfs, start, count, hasPositions, posOff);
        }
        return firstOffset;
    }

    private void WritePostingsBlock(
        ByteWriter dst,
        ReadOnlySpan<int> docs,
        ReadOnlySpan<int> tfs,
        int start,
        int count,
        bool hasPositions,
        uint posOff)
    {
        Span<byte> bitmap = stackalloc byte[BitmapSize];
        bitmap.Clear();
        var maxTf = 1;
        var excCount = 0;
        for (var i = 0; i < count; i++)
        {
            var tf = tfs[start + i];
            if (tf == 1) bitmap[i >> 3] |= (byte)(1 << (i & 7));
            else excCount++;
            if (tf > maxTf) maxTf = tf;
        }

        var body = _postBody;
        body.Reset();
        body.WriteBytes(bitmap);

        var prevDoc = 0;
        for (var i = 0; i < count; i++)
        {
            var doc = docs[start + i];
            body.WriteVarint((ulong)(doc - prevDoc));
            prevDoc = doc;
        }

        for (var i = 0; i < count; i++)
        {
            var tf = tfs[start + i];
            if (tf == 1) continue;
            body.WriteByte((byte)i);
            body.WriteVarint((ulong)tf);
        }

        body.WriteUInt32(posOff);

        dst.WriteUInt16((ushort)count);
        dst.WriteUInt32((uint)docs[start + count - 1]);
        dst.WriteUInt16((ushort)maxTf);
        dst.WriteByte(hasPositions ? FlagHasPositions : (byte)0);
        dst.WriteUInt32(Crc32C.Compute(body.Span));
        dst.WriteByte((byte)excCount);
        dst.WriteBytes(body.Span);
    }

    private void WritePositionsBlock(
        ByteWriter dst,
        ReadOnlySpan<int> docs,
        ReadOnlySpan<int> tfs,
        ReadOnlySpan<int> posStart,
        ReadOnlySpan<int> posCount,
        ReadOnlySpan<int> posFlat,
        int start,
        int count)
    {
        var body = _posBody;
        body.Reset();
        var prevDoc = 0;
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            body.WriteVarint((ulong)(docs[index] - prevDoc));
            prevDoc = docs[index];

            body.WriteVarint((ulong)tfs[index]);
            var prevPos = 0;
            foreach (var pos in posFlat.Slice(posStart[index], posCount[index]))
            {
                body.WriteVarint((ulong)(pos - prevPos));
                prevPos = pos;
            }
        }

        dst.WriteUInt16((ushort)count);
        dst.WriteUInt32((uint)docs[start + count - 1]);
        dst.WriteByte(FlagHasPositions);
        dst.WriteByte(0);
        dst.WriteUInt32(Crc32C.Compute(body.Span));
        dst.WriteBytes(body.Span);
    }
}

/// <summary>
/// 倒排 / 位置块读取入口。所有越界、CRC 不符、varint 截断、位图与例外表不一致
/// 一律抛 <see cref="InvalidDataException"/>。
/// </summary>
internal sealed class PostingsReader
{
    private readonly ReadOnlyMemory<byte> _postings;
    private readonly ReadOnlyMemory<byte> _positions;

    public PostingsReader(ReadOnlyMemory<byte> postings, ReadOnlyMemory<byte> positions)
    {
        _postings = postings;
        _positions = positions;
    }

    /// <summary>从 <paramref name="blockOffset"/> 起解码 <paramref name="df"/> 条记录（跨多块）。</summary>
    public List<PostingEntry> DecodeAll(uint blockOffset, uint df)
    {
        if (df > int.MaxValue) throw new InvalidDataException("df 超过 int 上限");
        var result = new List<PostingEntry>((int)Math.Min(df, 4096u));
        if (df == 0) return result;

        var cursor = Open(blockOffset, df);
        while (cursor.Next())
        {
            result.Add(new PostingEntry(
                cursor.CurrentDoc,
                cursor.CurrentTf,
                cursor.HasPositions ? cursor.CurrentPositions.ToArray() : null));
        }
        if (result.Count != (int)df)
            throw new InvalidDataException("倒排 doc 数与 df 不符");
        return result;
    }

    /// <summary>流式游标。多次 <c>Advance</c> 只在块头 <c>last_doc</c> 之后用整块跳过。</summary>
    public PostingsCursor Open(uint blockOffset, uint df) =>
        new(_postings, _positions, blockOffset, df);
}

/// <summary>
/// 倒排流式游标（P4 迭代器的基础）：顺序 <see cref="Next"/> 或带跳块的 <see cref="Advance"/>。
/// 位置块按需解码，缓冲在游标内复用，避免每 doc 分配。
/// </summary>
internal sealed class PostingsCursor
{
    private const int PositionsHeaderSize = 12;
    private const byte FlagHasPositions = 0x01;

    private readonly ReadOnlyMemory<byte> _postings;
    private readonly ReadOnlyMemory<byte> _positions;

    private readonly int[] _docs = new int[EraFormat.PostingsBlockDocs];
    private readonly int[] _tfs = new int[EraFormat.PostingsBlockDocs];
    private readonly bool[] _tfSeen = new bool[EraFormat.PostingsBlockDocs];

    private readonly int[] _posStart = new int[EraFormat.PostingsBlockDocs];
    private readonly int[] _posCounts = new int[EraFormat.PostingsBlockDocs];
    private int[] _posFlat = new int[16];

    private uint _nextBlockOffset;
    private int _remainingDocs;
    private bool _blockLoaded;

    private int _docCount;
    private int _lastDoc;
    private int _posOff;
    private bool _hasPositions;

    private int _docIndex = -1;
    private int _currentDoc = -1;
    private int _currentTf;

    private bool _positionsLoaded;

    internal PostingsCursor(
        ReadOnlyMemory<byte> postings, ReadOnlyMemory<byte> positions, uint blockOffset, uint df)
    {
        if (df > int.MaxValue) throw new InvalidDataException("df 超过 int 上限");
        _postings = postings;
        _positions = positions;
        _nextBlockOffset = blockOffset;
        _remainingDocs = (int)df;
    }

    /// <summary>当前 doc（尚未前进时为 -1）。</summary>
    public int CurrentDoc => _currentDoc;

    /// <summary>当前 doc 的词频（尚未前进时为 0）。</summary>
    public int CurrentTf => _currentTf;

    /// <summary>当前 doc 的位置序列；无位置字段返回空。</summary>
    public ReadOnlySpan<int> CurrentPositions
    {
        get
        {
            if (_docIndex < 0 || !_hasPositions) return ReadOnlySpan<int>.Empty;
            LoadPositions();
            return _posFlat.AsSpan(_posStart[_docIndex], _posCounts[_docIndex]);
        }
    }

    /// <summary>当前词条是否带位置（前进过一次后有效）。</summary>
    public bool HasPositions => _hasPositions;

    /// <summary>前进一个 doc；返回 false 表示耗尽。</summary>
    public bool Next()
    {
        if (_blockLoaded && _docIndex + 1 < _docCount)
        {
            AdvanceOne();
            return true;
        }
        while (LoadNextBlock())
        {
            AdvanceOne();
            return true;
        }
        _currentDoc = -1;
        _currentTf = 0;
        return false;
    }

    /// <summary>
    /// 前进到 doc ≥ <paramref name="target"/>；返回 false 表示耗尽。
    /// 整块落后于 target 时按块头 <c>last_doc</c> 跳过，不解码块内位置。
    /// </summary>
    public bool Advance(int target)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        if (_blockLoaded && _docIndex >= 0 && _currentDoc >= target) return true;

        while (true)
        {
            if (_blockLoaded && _lastDoc < target)
            {
                _remainingDocs -= _docCount - (_docIndex + 1);
                _blockLoaded = false;
                _docIndex = -1;
                continue;
            }

            if (_blockLoaded)
            {
                while (_docIndex + 1 < _docCount)
                {
                    AdvanceOne();
                    if (_currentDoc >= target) return true;
                }
                _blockLoaded = false;
                _docIndex = -1;
                continue;
            }

            if (!LoadNextBlock())
            {
                _currentDoc = -1;
                _currentTf = 0;
                return false;
            }
        }
    }

    /* ------------------------------------------------------------------ */

    private void AdvanceOne()
    {
        _docIndex++;
        _currentDoc = _docs[_docIndex];
        _currentTf = _tfs[_docIndex];
        _remainingDocs--;
    }

    private bool LoadNextBlock()
    {
        _blockLoaded = false;
        _docIndex = -1;
        _positionsLoaded = false;
        if (_remainingDocs <= 0) return false;
        LoadBlock();
        return true;
    }

    private void LoadBlock()
    {
        var span = _postings.Span;
        if (_nextBlockOffset > (uint)span.Length)
            throw new InvalidDataException("倒排块偏移越界");

        var reader = new ByteReader(span, (int)_nextBlockOffset);
        var blockStart = reader.Position;
        int docCount = reader.ReadUInt16();
        var lastDoc = reader.ReadUInt32();
        int maxTf = reader.ReadUInt16();
        var flags = reader.ReadByte();
        var crc = reader.ReadUInt32();
        int excCount = reader.ReadByte();

        if (docCount == 0 || docCount > EraFormat.PostingsBlockDocs)
            throw new InvalidDataException("倒排块 doc 数非法");
        if ((flags & ~FlagHasPositions) != 0)
            throw new InvalidDataException("倒排块 flags 非法");
        if (docCount > _remainingDocs)
            throw new InvalidDataException("倒排块 doc 数超过 df");
        if (maxTf == 0)
            throw new InvalidDataException("倒排块 max_tf 非法");

        var bitmap = reader.ReadBytes(16);

        var prevDoc = 0;
        for (var i = 0; i < docCount; i++)
        {
            var delta = reader.ReadVarint();
            if (i > 0 && delta == 0)
                throw new InvalidDataException("倒排 doc 未严格递增");
            var doc = i == 0 ? (long)delta : prevDoc + (long)delta;
            if (doc > int.MaxValue)
                throw new InvalidDataException("倒排 doc 越界");
            prevDoc = (int)doc;
            _docs[i] = prevDoc;
        }
        if (prevDoc != lastDoc)
            throw new InvalidDataException("倒排块 last_doc 与解码结果不符");

        // 位图：bit=1 → tf=1；doc_count 之后的位必须为 0（规范形式）。
        for (var i = 0; i < docCount; i++)
            _tfs[i] = (bitmap[i >> 3] & (1 << (i & 7))) != 0 ? 1 : 0;
        for (var i = docCount; i < EraFormat.PostingsBlockDocs; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) != 0)
                throw new InvalidDataException("倒排块 tf 位图越界位置被置位");
        }

        Array.Clear(_tfSeen, 0, _tfSeen.Length);
        var maxSeen = 1;
        for (var i = 0; i < excCount; i++)
        {
            int index = reader.ReadByte();
            if (index >= docCount || _tfSeen[index])
                throw new InvalidDataException("倒排 tf 例外下标非法");
            _tfSeen[index] = true;

            var tf = reader.ReadVarint();
            if (tf < 2 || tf > ushort.MaxValue)
                throw new InvalidDataException("倒排 tf 例外值非法");
            if (_tfs[index] != 0)
                throw new InvalidDataException("倒排 tf 位图与例外表冲突");
            _tfs[index] = (int)tf;
            if (tf > (ulong)maxSeen) maxSeen = (int)tf;
        }
        for (var i = 0; i < docCount; i++)
        {
            if (_tfs[i] == 0) throw new InvalidDataException("倒排 doc 缺少 tf");
            if (_tfs[i] > maxSeen) maxSeen = _tfs[i];
        }
        if (maxTf != maxSeen)
            throw new InvalidDataException("倒排块 max_tf 与解码结果不符");

        var posOff = reader.ReadUInt32();
        var blockEnd = reader.Position;

        var bodyStart = blockStart + 14;
        if (Crc32C.Compute(span.Slice(bodyStart, blockEnd - bodyStart)) != crc)
            throw new InvalidDataException("倒排块 CRC 不符");

        var hasPositions = (flags & FlagHasPositions) != 0;
        if (hasPositions)
        {
            if (posOff > (uint)_positions.Length
                || _positions.Length - (int)posOff < PositionsHeaderSize)
                throw new InvalidDataException("位置块偏移越界");
        }
        else if (posOff != 0)
        {
            throw new InvalidDataException("无位置块时 pos_off 必须为 0");
        }

        _docCount = docCount;
        _lastDoc = prevDoc;
        _posOff = (int)posOff;
        _hasPositions = hasPositions;
        _positionsLoaded = false;
        _nextBlockOffset = (uint)blockEnd;
        _blockLoaded = true;
    }

    /// <summary>按需解码当前位置块（含 CRC 校验），结果摊进复用缓冲。</summary>
    private void LoadPositions()
    {
        if (!_hasPositions || _positionsLoaded) return;

        var span = _positions.Span;
        var start = _posOff;
        var reader = new ByteReader(span, start);
        int docCount = reader.ReadUInt16();
        var lastDoc = reader.ReadUInt32();
        var flags = reader.ReadByte();
        var reserved = reader.ReadByte();
        var crc = reader.ReadUInt32();

        if (docCount != _docCount || lastDoc != (uint)_lastDoc)
            throw new InvalidDataException("位置块与倒排块 doc 序列不匹配");
        if ((flags & FlagHasPositions) == 0 || (flags & ~FlagHasPositions) != 0 || reserved != 0)
            throw new InvalidDataException("位置块 flags / reserved 非法");

        var flat = 0;
        var prevDoc = 0;
        for (var i = 0; i < docCount; i++)
        {
            var delta = reader.ReadVarint();
            if (i > 0 && delta == 0)
                throw new InvalidDataException("位置块 doc 未严格递增");
            var doc = i == 0 ? (long)delta : prevDoc + (long)delta;
            if (doc != _docs[i])
                throw new InvalidDataException("位置块 doc 序列与倒排不一致");
            prevDoc = (int)doc;

            var count = reader.ReadVarint();
            if (count != (ulong)_tfs[i])
                throw new InvalidDataException("位置块 pos_count 与 tf 不符");

            EnsurePosCapacity(flat + (int)count);
            _posStart[i] = flat;
            _posCounts[i] = (int)count;
            var prevPos = 0;
            for (var j = 0; j < (int)count; j++)
            {
                var posDelta = reader.ReadVarint();
                if (j > 0 && posDelta == 0)
                    throw new InvalidDataException("位置未严格递增");
                var pos = prevPos + (long)posDelta;
                if (pos > int.MaxValue)
                    throw new InvalidDataException("位置越界");
                prevPos = (int)pos;
                _posFlat[flat++] = prevPos;
            }
        }

        var bodyStart = start + PositionsHeaderSize;
        if (Crc32C.Compute(span.Slice(bodyStart, reader.Position - bodyStart)) != crc)
            throw new InvalidDataException("位置块 CRC 不符");

        _positionsLoaded = true;
    }

    private void EnsurePosCapacity(int needed)
    {
        if (needed <= _posFlat.Length) return;
        var next = _posFlat.Length;
        while (next < needed) next = next >= int.MaxValue / 2 ? needed : next * 2;
        Array.Resize(ref _posFlat, next);
    }
}
