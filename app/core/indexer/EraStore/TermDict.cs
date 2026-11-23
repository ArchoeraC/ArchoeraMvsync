// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 词典条目（只读视图）。<see cref="Key"/> = <c>field_id(1B) || term_utf8</c>。
/// </summary>
internal readonly struct TermInfo
{
    public TermInfo(byte[] key, uint df, uint postOffset, byte flags)
    {
        Key = key;
        Df = df;
        PostOffset = postOffset;
        Flags = flags;
    }

    /// <summary>完整键（字段字节 + UTF-8 词条）。</summary>
    public byte[] Key { get; }

    /// <summary>文档频率。</summary>
    public uint Df { get; }

    /// <summary>倒排块在 Postings 段内的相对偏移。</summary>
    public uint PostOffset { get; }

    /// <summary>词条标志（has_positions 等）。</summary>
    public byte Flags { get; }

    /// <summary>字段族。</summary>
    public FieldId Field => (FieldId)Key[0];

    /// <summary>词条文本（不含字段字节）。</summary>
    public string Term => Encoding.UTF8.GetString(Key, 1, Key.Length - 1);
}

/// <summary>
/// 前编码词典写入（设计文档 §3.3.0.2）。
///
/// 块结构（每 ≤64 条一块）：
/// <code>
/// 块头 8B : term_count u8 | first_len u8 | flags u8 | reserved u8 | crc32c u32
/// 词条 × n: shared u8 | suffix_len u8（255 → 追加 u16 实数）| suffix | df varint | post_off u32 | flags u8
/// </code>
/// <list type="bullet">
///   <item><c>first_len</c> 是块内首词的 <b>词条字节数</b>（不含字段字节），语义固定便于诊断；</item>
///   <item><c>shared</c> 相对块内上一条键（首条为 0），因此首条是完整键；</item>
///   <item><c>crc32c</c> 覆盖块头之后到块尾的全部字节；</item>
///   <item>键按字节序排序；同一字段族的词条天然连续。</item>
/// </list>
///
/// 块索引（独立段）：每块 16B = <c>prefix8[8] | block_off u32 | block_len u32</c>，
/// 偏移相对 Dict 段起点。
/// </summary>
internal sealed class TermDictBuilder
{
    private readonly List<(byte[] Key, uint Df, uint PostOffset, byte Flags)> _entries = new();

    /// <summary>加入一条词条。重复键、空词条、超长词条直接拒绝。</summary>
    public void Add(FieldId field, string term, uint df, uint postOffset, byte flags = 0)
    {
        ArgumentNullException.ThrowIfNull(term);
        var termBytes = Encoding.UTF8.GetBytes(term);
        AddKey(field, termBytes, df, postOffset, flags);
    }

    /// <summary>加入一条原始键（供合并 / 差分测试使用）。</summary>
    public void AddKey(FieldId field, ReadOnlySpan<byte> termBytes, uint df, uint postOffset, byte flags = 0)
    {
        if (termBytes.Length == 0) throw new ArgumentException("词条不能为空", nameof(termBytes));
        if (termBytes.Length > EraFormat.MaxTermBytes)
            throw new ArgumentException($"词条超过 {EraFormat.MaxTermBytes}B", nameof(termBytes));

        var key = new byte[1 + termBytes.Length];
        key[0] = (byte)field;
        termBytes.CopyTo(key.AsSpan(1));
        _entries.Add((key, df, postOffset, flags));
    }

    /// <summary>
    /// 构建词典与块索引。同样的输入必得同样的字节（按字节序排序后分块）。
    /// </summary>
    public (byte[] Dict, byte[] Index) Build()
    {
        _entries.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        // 重复键会让 df / 倒排归属产生歧义，直接拒绝而不是静默合并。
        for (var i = 1; i < _entries.Count; i++)
        {
            if (_entries[i].Key.AsSpan().SequenceEqual(_entries[i - 1].Key))
                throw new InvalidOperationException(
                    $"词典键重复: field={(FieldId)_entries[i].Key[0]} term={Encoding.UTF8.GetString(_entries[i].Key, 1, _entries[i].Key.Length - 1)}");
        }

        var dict = new ByteWriter(Math.Max(256, _entries.Count * 12));
        var index = new ByteWriter(Math.Max(16, (_entries.Count / EraFormat.DictBlockTerms + 1) * 16));

        var prevKey = Array.Empty<byte>();
        for (var start = 0; start < _entries.Count; start += EraFormat.DictBlockTerms)
        {
            var count = Math.Min(EraFormat.DictBlockTerms, _entries.Count - start);
            var firstKey = _entries[start].Key;

            var blockStart = dict.Length;
            dict.WriteByte((byte)count);
            dict.WriteByte((byte)(firstKey.Length - 1)); // 词条字节数（不含字段字节）
            dict.WriteByte(0);
            dict.WriteByte(0);
            dict.WriteUInt32(0); // CRC 占位

            prevKey = Array.Empty<byte>();
            for (var i = 0; i < count; i++)
            {
                var e = _entries[start + i];
                var shared = CommonPrefix(prevKey, e.Key);
                if (shared > byte.MaxValue) throw new InvalidOperationException("shared 超过 255");

                dict.WriteByte((byte)shared);
                var suffixLen = e.Key.Length - shared;
                if (suffixLen >= byte.MaxValue)
                {
                    dict.WriteByte(byte.MaxValue);
                    dict.WriteUInt16((ushort)suffixLen);
                }
                else
                {
                    dict.WriteByte((byte)suffixLen);
                }
                dict.WriteBytes(e.Key.AsSpan(shared));
                dict.WriteVarint(e.Df);
                dict.WriteUInt32(e.PostOffset);
                dict.WriteByte(e.Flags);
                prevKey = e.Key;
            }

            var blockLen = dict.Length - blockStart;
            var crc = Crc32C.Compute(dict.Span[(blockStart + 8)..]);
            dict.PatchUInt32(blockStart + 4, crc);

            var p8 = Prefix8(firstKey);
            index.WriteBytes(p8);
            index.WriteUInt32((uint)blockStart);
            index.WriteUInt32((uint)blockLen);

            if ((uint)blockStart > uint.MaxValue || (uint)blockLen > uint.MaxValue)
                throw new InvalidOperationException("词典段超过 4GB");
        }

        return (dict.ToArray(), index.ToArray());
    }

    private static int CommonPrefix(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static ReadOnlySpan<byte> Prefix8(ReadOnlySpan<byte> key)
    {
        var buf = new byte[8];
        key[..Math.Min(8, key.Length)].CopyTo(buf);
        return buf;
    }
}

/// <summary>
/// 前编码词典读取（二分块索引 + 块内线性扫）。
/// 所有长度 / 偏移先校验再使用；损坏文件只允许抛 <see cref="InvalidDataException"/>。
/// </summary>
internal sealed class TermDictReader
{
    private const int BlockHeaderSize = 8;
    private const int IndexEntrySize = 16;

    private readonly ReadOnlyMemory<byte> _dict;
    private readonly ReadOnlyMemory<byte> _index;

    public TermDictReader(ReadOnlyMemory<byte> dict, ReadOnlyMemory<byte> index)
    {
        if (index.Length % IndexEntrySize != 0)
            throw new InvalidDataException("词典块索引长度不是 16 的整数倍");
        _dict = dict;
        _index = index;
    }

    /// <summary>块数。</summary>
    public int BlockCount => _index.Length / IndexEntrySize;

    /// <summary>精确查找。找到返回 true；键不存在返回 false。</summary>
    public bool TryFind(FieldId field, string term, out TermInfo info)
    {
        ArgumentNullException.ThrowIfNull(term);
        return TryFind(BuildKey(field, Encoding.UTF8.GetBytes(term)), out info);
    }

    /// <summary>按完整键精确查找。</summary>
    public bool TryFind(byte[] key, out TermInfo info)
    {
        info = default;
        if (key.Length == 0 || key.Length > 1 + EraFormat.MaxTermBytes) return false;

        var key8 = Pad8(key);
        if (BlockCount == 0) return false;
        var start = StartBlockFor(key8);

        for (var b = start; b < BlockCount; b++)
        {
            if (Compare8(BlockPrefix8(b), key8) > 0) return false;

            var hit = TryFindInBlock(b, key, out info);
            if (hit > 0) return true;
            if (hit < 0) return false; // 块内已越过目标，后续块只会更大
        }
        return false;
    }

    /// <summary>
    /// 前缀扫描：返回同字段族内以 <paramref name="prefix"/> 开头的词条，最多 <paramref name="max"/> 条。
    /// 结果按字节序排列。
    /// </summary>
    public List<TermInfo> ScanPrefix(FieldId field, string prefix, int max)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (max <= 0) return new List<TermInfo>();

        var prefixKey = BuildKey(field, Encoding.UTF8.GetBytes(prefix));
        if (BlockCount == 0) return new List<TermInfo>();
        var start = StartBlockFor(Pad8(prefixKey));

        var results = new List<TermInfo>(Math.Min(max, EraFormat.DictBlockTerms));
        for (var b = start; b < BlockCount && results.Count < max; b++)
        {
            var body = OpenBlock(b, out var count);
            var reader = new ByteReader(body, BlockHeaderSize);
            var keyBuf = new byte[256];
            var keyLen = 0;

            for (var i = 0; i < count && results.Count < max; i++)
            {
                var (df, postOff, flags) = ReadEntry(ref reader, keyBuf, ref keyLen);
                var key = keyBuf.AsSpan(0, keyLen);

                if (!key.StartsWith(prefixKey))
                {
                    if (key.SequenceCompareTo(prefixKey) > 0) return results;
                    continue;
                }

                results.Add(new TermInfo(key.ToArray(), df, postOff, flags));
            }
        }
        return results;
    }

    /// <summary>按字节序枚举全部词条（dump / 差分测试）。</summary>
    public List<TermInfo> ReadAll()
    {
        var results = new List<TermInfo>();
        for (var b = 0; b < BlockCount; b++)
        {
            var body = OpenBlock(b, out var count);
            var reader = new ByteReader(body, BlockHeaderSize);
            var keyBuf = new byte[256];
            var keyLen = 0;
            for (var i = 0; i < count; i++)
            {
                var (df, postOff, flags) = ReadEntry(ref reader, keyBuf, ref keyLen);
                results.Add(new TermInfo(keyBuf.AsSpan(0, keyLen).ToArray(), df, postOff, flags));
            }
        }
        return results;
    }

    /* ------------------------------------------------------------------ */

    private static byte[] BuildKey(FieldId field, ReadOnlySpan<byte> term)
    {
        var key = new byte[1 + term.Length];
        key[0] = (byte)field;
        term.CopyTo(key.AsSpan(1));
        return key;
    }

    private static byte[] Pad8(ReadOnlySpan<byte> key)
    {
        var buf = new byte[8];
        key[..Math.Min(8, key.Length)].CopyTo(buf);
        return buf;
    }

    private static int Compare8(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);

    private ReadOnlySpan<byte> BlockPrefix8(int block)
    {
        var at = block * IndexEntrySize;
        return _index.Span.Slice(at, 8);
    }

    private (int Offset, int Length) BlockLocation(int block)
    {
        var at = block * IndexEntrySize;
        var off = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_index.Span[(at + 8)..]);
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_index.Span[(at + 12)..]);
        if (off < 0 || len < BlockHeaderSize || (long)off + len > _dict.Length)
            throw new InvalidDataException("词典块越界");
        return (off, len);
    }

    /// <summary>
    /// 返回查找起点块：第一个 prefix8 ≥ target 的块的前一块（目标可能落在该块尾部），
    /// 全都小于 target 时取最后一块。调用方保证 <see cref="BlockCount"/> &gt; 0。
    /// </summary>
    private int StartBlockFor(byte[] target8)
    {
        var lo = 0;
        var hi = BlockCount;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Compare8(BlockPrefix8(mid), target8) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo >= BlockCount ? BlockCount - 1 : Math.Max(0, lo - 1);
    }

    /// <summary>校验块头并返回块体。</summary>
    private ReadOnlySpan<byte> OpenBlock(int block, out int count)
    {
        var (off, len) = BlockLocation(block);
        var span = _dict.Span.Slice(off, len);
        var reader = new ByteReader(span, 4);
        var crc = reader.ReadUInt32();
        var computed = Crc32C.Compute(span[BlockHeaderSize..]);
        if (crc != computed) throw new InvalidDataException("词典块 CRC 不符");

        count = span[0];
        var firstLen = span[1];
        if (count == 0 || count > EraFormat.DictBlockTerms)
            throw new InvalidDataException("词典块词条数非法");
        if (firstLen > EraFormat.MaxTermBytes)
            throw new InvalidDataException("词典块首词长度非法");
        return span;
    }

    /// <summary>
    /// 在块内查找。返回 1 = 命中，-1 = 块内已越过目标（目标不存在），0 = 块内全部小于目标。
    /// </summary>
    private int TryFindInBlock(int block, byte[] target, out TermInfo info)
    {
        info = default;
        var body = OpenBlock(block, out var count);
        var reader = new ByteReader(body, BlockHeaderSize);
        var keyBuf = new byte[256];
        var keyLen = 0;

        for (var i = 0; i < count; i++)
        {
            var (df, postOff, flags) = ReadEntry(ref reader, keyBuf, ref keyLen);
            var cmp = keyBuf.AsSpan(0, keyLen).SequenceCompareTo(target);
            if (cmp == 0)
            {
                info = new TermInfo(keyBuf.AsSpan(0, keyLen).ToArray(), df, postOff, flags);
                return 1;
            }
            if (cmp > 0) return -1;
        }
        return 0;
    }

    /// <summary>解码一条词条；键写入 <paramref name="keyBuf"/>，返回 df / post_off / flags。</summary>
    private static (uint Df, uint PostOffset, byte Flags) ReadEntry(
        ref ByteReader reader, Span<byte> keyBuf, ref int keyLen)
    {
        var shared = reader.ReadByte();
        if (shared > keyLen) throw new InvalidDataException("词典 shared 超过上一条键长");

        int suffixLen = reader.ReadByte();
        if (suffixLen == byte.MaxValue) suffixLen = reader.ReadUInt16();

        if (shared + suffixLen > keyBuf.Length)
            throw new InvalidDataException("词典词条超过 256B");

        var suffix = reader.ReadBytes(suffixLen);
        suffix.CopyTo(keyBuf[shared..]);
        keyLen = shared + suffixLen;

        var df = (uint)reader.ReadVarint();
        var postOff = reader.ReadUInt32();
        var flags = reader.ReadByte();
        return (df, postOff, flags);
    }
}
