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
/// flags    : 0x01 has_positions | 0x02 docs 位打包（O11）| 0x04 tf 位图裁剪（O12）
/// tf 位图     : 0x04 置位时 ceil(doc_count/8) B，否则固定 16B；bit i = 1 表示块内第 i 个 doc 的 tf == 1
/// doc 序列    : varint 块体：doc_delta varint（首条相对 0，之后与前一条的差）
///               packed 块体：width u8 | 打包 doc_delta（width bit, LSB-first）
/// tf 例外表   : doc_index u8 | tf varint（仅 tf &gt; 1）
/// pos_off     : u32，该块位置块在 Positions 段内的偏移；无位置写 0
/// </code>
/// <para>O11：逐块比较 varint 与位打包的 doc 序列字节数，取较小者（相等保留 varint）。
/// <c>width</c> 为该块最大 doc_delta 的位宽（0..31；单 doc 且 delta=0 时为 0），
/// 打包长度 <c>ceil(doc_count*width/8)</c>。位打包是 PForDelta 的退化形式：
/// 不设例外帧，离群 delta 抬高 width 时整块自然回落到 varint。</para>
///
/// 位置块（仅 has_positions，与倒排块按相同 doc 顺序成对写出）：
/// <code>
/// 块头 12B : doc_count u16 | last_doc u32 | flags u8 | reserved u8 | crc32c u32
/// flags    : 0x01 has_positions | 0x02 positions 位打包（O8）
/// 块体 varint : 每 doc：doc_delta varint（首条相对 0）| pos_count varint | 逐个位置 Δvarint
/// 块体 packed : 每 doc：doc_delta varint | pos_count varint | width u8 | 打包 Δpositions
/// </code>
/// <para>O8：逐块比较 varint 与位打包的块体字节数，取较小者（相等保留 varint）。
/// <c>width</c> 为 0..31（该 doc 最大 Δ 的位宽；只有单个位置 0 时才为 0），
/// Δpositions 按 LSB-first 连续打包进 <c>ceil(tf*width/8)</c> 字节；
/// <c>Δpos = pos[i] - pos[i-1]</c>，<c>pos[-1]=0</c>。doc / tf 仍用 varint。
/// 两种块体的解码结果完全一致，不改变短语相邻语义。</para>
/// CRC 均覆盖块头之后到块尾的全部字节；块内偏移是本块内相对量，跳块不依赖上一块。
/// </summary>
internal sealed class PostingsWriter
{
    private const int BitmapSize = 16;
    private const byte FlagHasPositions = 0x01;
    private const byte FlagPositionsPacked = 0x02;
    private const byte FlagDocsPacked = 0x02;
    private const byte FlagTrimmedBitmap = 0x04;

    /// <summary>
    /// 测试接缝：<c>null</c> = 按块体大小自动选择；<c>false</c> = 强制 varint；
    /// <c>true</c> = 强制位打包。生产路径不得设置。
    /// </summary>
    internal static bool? DebugForcePositionsPacked = null;

    /// <summary>测试接缝：doc 序列是否强制位打包（语义同上）。</summary>
    internal static bool? DebugForceDocsPacked = null;

    /// <summary>测试接缝：tf 位图是否强制写满 16B（旧格式黄金夹具用）。</summary>
    internal static bool DebugForceFullBitmap = false;

    /// <summary>块体复用缓冲：避免每 128 doc 新建两个 ByteWriter（构建期主要分配来源之一）。</summary>
    private readonly ByteWriter _postBody = new(512);
    private readonly ByteWriter _posBody = new(2_048);

    /// <summary>doc 位打包临时缓冲（O11）；按块内最大打包字节数增长后复用。</summary>
    private byte[] _docBits = new byte[128];

    /// <summary>位置位打包临时缓冲（O8）；按块内最大 doc 的打包字节数增长后复用。</summary>
    private byte[] _posBits = new byte[256];

    /// <summary>位置 Δ 临时缓冲（O8）；按块内最大 tf 增长后复用。</summary>
    private int[] _posDelta = new int[16];

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

        // O11：先算两种 doc 序列大小，再按较小者写（相等保留 varint）。
        // 例外表的 varint tf 与 pos_off 两种编码相同，只比较 doc 序列本体。
        Span<int> deltas = stackalloc int[EraFormat.PostingsBlockDocs];
        var maxDelta = 0;
        var varintBytes = 0;
        var prevDoc = 0;
        for (var i = 0; i < count; i++)
        {
            var doc = docs[start + i];
            var delta = doc - prevDoc;
            deltas[i] = delta;
            prevDoc = doc;
            if (delta > maxDelta) maxDelta = delta;
            varintBytes += Varint.Size((ulong)delta);
        }
        var width = DeltaWidth(maxDelta);
        var packedBytes = 1 + (int)(((long)count * width + 7) / 8);
        var docsPacked = DebugForceDocsPacked ?? (packedBytes < varintBytes);

        var body = _postBody;
        body.Reset();
        var bitmapBytes = DebugForceFullBitmap ? BitmapSize : (count + 7) / 8;
        body.WriteBytes(bitmap[..bitmapBytes]);

        if (docsPacked)
        {
            body.WriteByte((byte)width);
            WritePackedValues(body, deltas[..count], width, ref _docBits);
        }
        else
        {
            for (var i = 0; i < count; i++) body.WriteVarint((ulong)deltas[i]);
        }

        for (var i = 0; i < count; i++)
        {
            var tf = tfs[start + i];
            if (tf == 1) continue;
            body.WriteByte((byte)i);
            body.WriteVarint((ulong)tf);
        }

        body.WriteUInt32(posOff);

        var flags = hasPositions ? FlagHasPositions : (byte)0;
        if (docsPacked) flags |= FlagDocsPacked;
        if (!DebugForceFullBitmap) flags |= FlagTrimmedBitmap;

        dst.WriteUInt16((ushort)count);
        dst.WriteUInt32((uint)docs[start + count - 1]);
        dst.WriteUInt16((ushort)maxTf);
        dst.WriteByte(flags);
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
        // O8：先算两种块体大小，再按较小者写（相等保留 varint）。
        // widths 供打包写出复用，避免第三次扫描位置。
        Span<byte> widths = stackalloc byte[EraFormat.PostingsBlockDocs];
        var varintBytes = 0;
        var packedBytes = 0;
        var prevDoc = 0;
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            var docDelta = docs[index] - prevDoc;
            prevDoc = docs[index];
            var tf = tfs[index];

            var prefix = Varint.Size((ulong)docDelta) + Varint.Size((ulong)tf);
            varintBytes += prefix;
            packedBytes += prefix + 1; // width u8

            var maxDelta = 0;
            var prevPos = 0;
            foreach (var pos in posFlat.Slice(posStart[index], posCount[index]))
            {
                var delta = pos - prevPos;
                if (delta > maxDelta) maxDelta = delta;
                varintBytes += Varint.Size((ulong)delta);
                prevPos = pos;
            }

            var width = DeltaWidth(maxDelta);
            widths[i] = (byte)width;
            packedBytes += (int)(((long)tf * width + 7) / 8);
        }

        var packed = DebugForcePositionsPacked ?? (packedBytes < varintBytes);

        var body = _posBody;
        body.Reset();
        prevDoc = 0;
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            body.WriteVarint((ulong)(docs[index] - prevDoc));
            prevDoc = docs[index];

            body.WriteVarint((ulong)tfs[index]);
            var positions = posFlat.Slice(posStart[index], posCount[index]);
            if (packed)
            {
                body.WriteByte(widths[i]);
                EnsureDeltaCapacity(positions.Length);
                var prevPos = 0;
                for (var j = 0; j < positions.Length; j++)
                {
                    _posDelta[j] = positions[j] - prevPos;
                    prevPos = positions[j];
                }
                WritePackedValues(body, _posDelta.AsSpan(0, positions.Length), widths[i], ref _posBits);
            }
            else
            {
                var prevPos = 0;
                foreach (var pos in positions)
                {
                    body.WriteVarint((ulong)(pos - prevPos));
                    prevPos = pos;
                }
            }
        }

        dst.WriteUInt16((ushort)count);
        dst.WriteUInt32((uint)docs[start + count - 1]);
        dst.WriteByte(packed ? (byte)(FlagHasPositions | FlagPositionsPacked) : FlagHasPositions);
        dst.WriteByte(0);
        dst.WriteUInt32(Crc32C.Compute(body.Span));
        dst.WriteBytes(body.Span);
    }

    /// <summary>最大 Δ 的位宽：0 只在单个位置为 0 时出现，其余 1..31。</summary>
    private static int DeltaWidth(int maxDelta) =>
        maxDelta == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)maxDelta);

    /// <summary>把一组非负整数按 width 位 LSB-first 连续写入块体（O8 位置 / O11 doc 共用）。</summary>
    private static void WritePackedValues(
        ByteWriter body, ReadOnlySpan<int> values, int width, ref byte[] scratch)
    {
        var byteCount = (int)(((long)values.Length * width + 7) / 8);
        EnsureCapacity(ref scratch, byteCount);

        var bits = scratch;
        var outPos = 0;
        var accBits = 0;
        ulong acc = 0;
        foreach (var value in values)
        {
            acc |= (ulong)(uint)value << accBits;
            accBits += width;
            while (accBits >= 8)
            {
                bits[outPos++] = (byte)acc;
                acc >>= 8;
                accBits -= 8;
            }
        }
        if (accBits > 0) bits[outPos++] = (byte)acc;
        body.WriteBytes(bits.AsSpan(0, byteCount));
    }

    private static void EnsureCapacity(ref byte[] buffer, int needed)
    {
        if (needed <= buffer.Length) return;
        var next = buffer.Length;
        while (next < needed) next = next >= int.MaxValue / 2 ? needed : next * 2;
        Array.Resize(ref buffer, next);
    }

    private void EnsureDeltaCapacity(int needed)
    {
        if (needed <= _posDelta.Length) return;
        var next = _posDelta.Length;
        while (next < needed) next = next >= int.MaxValue / 2 ? needed : next * 2;
        Array.Resize(ref _posDelta, next);
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
    private const byte FlagPositionsPacked = 0x02;
    private const byte FlagDocsPacked = 0x02;
    private const byte FlagTrimmedBitmap = 0x04;
    private const byte PositionsFlagsMask = FlagHasPositions | FlagPositionsPacked;
    private const byte PostingsFlagsMask = FlagHasPositions | FlagDocsPacked | FlagTrimmedBitmap;

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
        if ((flags & ~PostingsFlagsMask) != 0)
            throw new InvalidDataException("倒排块 flags 非法");
        if (docCount > _remainingDocs)
            throw new InvalidDataException("倒排块 doc 数超过 df");
        if (maxTf == 0)
            throw new InvalidDataException("倒排块 max_tf 非法");

        var bitmapBytes = (flags & FlagTrimmedBitmap) != 0 ? (docCount + 7) / 8 : 16;
        var bitmap = reader.ReadBytes(bitmapBytes);

        var docsPacked = (flags & FlagDocsPacked) != 0;
        var prevDoc = 0;
        if (docsPacked)
        {
            var width = reader.ReadByte();
            if (width > 31)
                throw new InvalidDataException("倒排块打包位宽非法");
            var byteCount = (int)(((long)docCount * width + 7) / 8);
            DecodePackedDocs(reader.ReadBytes(byteCount), width, docCount);
            prevDoc = _docs[docCount - 1];
        }
        else
        {
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
        }
        if (prevDoc != lastDoc)
            throw new InvalidDataException("倒排块 last_doc 与解码结果不符");

        // 位图：bit=1 → tf=1；doc_count 之后的位必须为 0（规范形式）。
        for (var i = 0; i < docCount; i++)
            _tfs[i] = (bitmap[i >> 3] & (1 << (i & 7))) != 0 ? 1 : 0;
        for (var i = docCount; i < bitmapBytes * 8; i++)
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

    /// <summary>解码 O11 位打包的 doc 序列（Δ 编码，LSB-first），写入 <c>_docs</c>。</summary>
    private void DecodePackedDocs(ReadOnlySpan<byte> packed, int width, int count)
    {
        var mask = width == 0 ? 0UL : (1UL << width) - 1;
        var prevDoc = 0;
        var byteIndex = 0;
        var accBits = 0;
        ulong acc = 0;
        for (var i = 0; i < count; i++)
        {
            while (accBits < width)
            {
                if (byteIndex >= packed.Length)
                    throw new InvalidDataException("倒排 doc 打包字节截断");
                acc |= (ulong)packed[byteIndex++] << accBits;
                accBits += 8;
            }

            var delta = (long)(acc & mask);
            acc >>= width;
            accBits -= width;
            if (i > 0 && delta == 0)
                throw new InvalidDataException("倒排 doc 未严格递增");
            var doc = i == 0 ? delta : prevDoc + delta;
            if (doc > int.MaxValue)
                throw new InvalidDataException("倒排 doc 越界");
            _docs[i] = prevDoc = (int)doc;
        }
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
        if ((flags & FlagHasPositions) == 0 || (flags & ~PositionsFlagsMask) != 0 || reserved != 0)
            throw new InvalidDataException("位置块 flags / reserved 非法");
        var packed = (flags & FlagPositionsPacked) != 0;

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
            if (packed)
            {
                var width = reader.ReadByte();
                if (width > 31)
                    throw new InvalidDataException("位置块打包位宽非法");
                var byteCount = (int)(((long)count * width + 7) / 8);
                DecodePackedPositions(reader.ReadBytes(byteCount), width, (int)count, flat);
                flat += (int)count;
            }
            else
            {
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
        }

        var bodyStart = start + PositionsHeaderSize;
        if (Crc32C.Compute(span.Slice(bodyStart, reader.Position - bodyStart)) != crc)
            throw new InvalidDataException("位置块 CRC 不符");

        _positionsLoaded = true;
    }

    private void DecodePackedPositions(
        ReadOnlySpan<byte> packed, int width, int count, int flatStart)
    {
        var mask = width == 0 ? 0UL : (1UL << width) - 1;
        var flat = flatStart;
        var prevPos = 0;
        var byteIndex = 0;
        var accBits = 0;
        ulong acc = 0;
        for (var j = 0; j < count; j++)
        {
            while (accBits < width)
            {
                if (byteIndex >= packed.Length)
                    throw new InvalidDataException("位置打包字节截断");
                acc |= (ulong)packed[byteIndex++] << accBits;
                accBits += 8;
            }

            var delta = (long)(acc & mask);
            acc >>= width;
            accBits -= width;
            if (j > 0 && delta == 0)
                throw new InvalidDataException("位置未严格递增");
            var pos = prevPos + delta;
            if (pos > int.MaxValue)
                throw new InvalidDataException("位置越界");
            prevPos = (int)pos;
            _posFlat[flat++] = prevPos;
        }
    }

    private void EnsurePosCapacity(int needed)
    {
        if (needed <= _posFlat.Length) return;
        var next = _posFlat.Length;
        while (next < needed) next = next >= int.MaxValue / 2 ? needed : next * 2;
        Array.Resize(ref _posFlat, next);
    }
}
