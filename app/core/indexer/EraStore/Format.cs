// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// EraStore 物理格式常量（设计文档 §3.3.0.1）。
///
/// 所有多字节整数显式小端；偏移一律 64 位；varint 为无符号 LEB128。
/// 这些常量是 <c>index_format</c> = 1 的一部分，改动语义必须升 <see cref="FormatVersion"/>。
/// </summary>
internal static class EraFormat
{
    /// <summary>文件头 magic：<c>ERAIDX\x01\x00</c>（8 字节）。</summary>
    public static ReadOnlySpan<byte> Magic => "ERAIDX\x01\x00"u8;

    /// <summary>物理格式主版本；与旧 v7 schema 无关。</summary>
    public const ushort FormatVersion = 1;

    /// <summary>文件头固定大小（字节）。</summary>
    public const int HeaderSize = 64;

    /// <summary>提交槽大小（字节）。</summary>
    public const int CommitSlotSize = 32;

    /// <summary>提交槽数量（双槽交替写）。</summary>
    public const int CommitSlotCount = 2;

    /// <summary>第一个提交槽的偏移。</summary>
    public const int CommitSlotOffset = HeaderSize;

    /// <summary>数据区起始偏移（Header + 两槽）。</summary>
    public const int DataStart = HeaderSize + CommitSlotCount * CommitSlotSize;

    /// <summary>Manifest 上限（64 KB），超出直接拒绝。</summary>
    public const int MaxManifestBytes = 512 * 1024;

    /// <summary>词典块内词条数上限。</summary>
    public const int DictBlockTerms = 64;

    /// <summary>倒排块内 doc 数上限。</summary>
    public const int PostingsBlockDocs = 128;

    /// <summary>
    /// 词条长度上限（UTF-8 字节）。写入前超长单 token 必须经
    /// <see cref="TermCodec.Canonicalize"/> 换成 <c>0x01 + MD5 hex</c>（33B）的
    /// 不透明词条；<see cref="TermDictBuilder"/> 仍拒绝直接写入的超长原文。
    /// </summary>
    public const int MaxTermBytes = 255;

    /// <summary>doc 数上限（防御恶意文件触发巨量分配）。</summary>
    public const uint MaxDocCount = uint.MaxValue;

    /// <summary>文件 UUID 长度。</summary>
    public const int FileUuidBytes = 16;

    /// <summary>MD5 / 内容指纹长度。</summary>
    public const int TrackIdBytes = 16;
}

/// <summary>
/// 段类型。Manifest 用数值引用段；旧读者遇到未知类型按规则跳过。
/// </summary>
internal enum SectionKind : byte
{
    /// <summary>列式文档存储（§3.3.0.4）。</summary>
    DocStore = 1,

    /// <summary>前编码词典（§3.3.0.2）。</summary>
    Dict = 2,

    /// <summary>词典块索引（§3.3.0.2）。</summary>
    DictIndex = 3,

    /// <summary>倒排块（§3.3.0.3）。</summary>
    Postings = 4,

    /// <summary>位置块（§3.3.0.3）。</summary>
    Positions = 5,

    /// <summary>统计摘要。</summary>
    Stats = 6,

    /// <summary>墓碑。</summary>
    Tombs = 7,
}

/// <summary>
/// 字段族编号。词典键 = <c>field_id(1B) || term_utf8</c>，因此同一字段族的词条连续。
/// 数值写入磁盘，新增字段只能追加（不得重排）。
/// </summary>
internal enum FieldId : byte
{
    /// <summary>歌名（字面）。</summary>
    Title = 1,

    /// <summary>歌手（字面）。</summary>
    Artist = 2,

    /// <summary>专辑（字面）。</summary>
    Album = 3,

    /// <summary>文件名（字面）。</summary>
    Filename = 4,

    /// <summary>歌词原文。</summary>
    Lyrics = 5,

    /// <summary>歌词译文。</summary>
    LyricsTranslation = 6,

    /// <summary>歌词音译。</summary>
    LyricsRomanization = 7,

    /// <summary>元数据拼音。</summary>
    PinyinMeta = 8,

    /// <summary>歌词拼音。</summary>
    PinyinLyrics = 9,

    /// <summary>元数据首字母。</summary>
    InitialsMeta = 10,

    /// <summary>歌词首字母。</summary>
    InitialsLyrics = 11,

    /// <summary>假名 / 谚文读音 bigram。</summary>
    ReadingMeta = 12,
}

/// <summary>
/// 无符号 LEB128（设计文档 §3.3.0.1：<b>varint = 无符号 LEB128</b>）。
///
/// 契约：
/// <list type="bullet">
///   <item>只编码非负整数；最大 10 字节（64 位）；</item>
///   <item>第 10 字节只能携带最低 1 位，其余位必须为 0，否则视为溢出；</item>
///   <item>截断（末字节仍带 continuation 位）抛 <see cref="InvalidDataException"/>；</item>
///   <item>不做 zig-zag——调用方保证差值为非负。</item>
/// </list>
/// </summary>
internal static class Varint
{
    /// <summary>64 位无符号 varint 的最大字节数。</summary>
    public const int MaxBytes = 10;

    /// <summary>写入并返回字节数；目标空间不足时抛 <see cref="ArgumentException"/>。</summary>
    public static int Write(Span<byte> dst, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            if (i >= dst.Length) throw new ArgumentException("varint 目标缓冲区不足", nameof(dst));
            dst[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        if (i >= dst.Length) throw new ArgumentException("varint 目标缓冲区不足", nameof(dst));
        dst[i++] = (byte)value;
        return i;
    }

    /// <summary>编码长度（1–10 字节）。</summary>
    public static int Size(ulong value)
    {
        var n = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            n++;
        }
        return n;
    }

    /// <summary>
    /// 从 <paramref name="src"/> 的 <paramref name="offset"/> 解码，成功后推进偏移。
    /// 失败（截断 / 溢出）抛 <see cref="InvalidDataException"/>，不改变偏移。
    /// </summary>
    public static ulong Read(ReadOnlySpan<byte> src, ref int offset)
    {
        if (!TryRead(src, ref offset, out var value, out var error))
            throw new InvalidDataException(error);
        return value;
    }

    /// <summary>尝试解码；失败时不推进偏移，返回 false 并给出原因。</summary>
    public static bool TryRead(ReadOnlySpan<byte> src, ref int offset, out ulong value, out string error)
    {
        value = 0;
        error = string.Empty;
        var pos = offset;
        var shift = 0;

        for (var i = 0; i < MaxBytes; i++)
        {
            if (pos >= src.Length)
            {
                error = "varint 截断";
                return false;
            }

            var b = src[pos++];
            if (i == MaxBytes - 1 && (b & 0xFE) != 0)
            {
                error = "varint 溢出（超过 64 位）";
                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                offset = pos;
                return true;
            }
            shift += 7;
        }

        error = "varint 过长";
        return false;
    }
}

/// <summary>
/// CRC-32C（Castagnoli，多项式 0x1EDC6F41，反射形式 0x82F63B78）。
/// 用于文件头 / 段 / 块 / 提交槽校验；固定向量见单测。
/// </summary>
internal static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint poly = 0x82F63B78u;
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>计算 CRC-32C。空输入返回 0。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}

/// <summary>
/// 只追加的字节缓冲（块编码用）。所有整数显式小端。
/// </summary>
internal sealed class ByteWriter
{
    private byte[] _buf;

    public ByteWriter(int capacity = 256)
    {
        _buf = new byte[Math.Max(capacity, 16)];
    }

    /// <summary>已写字节数。</summary>
    public int Length { get; private set; }

    /// <summary>清空内容但保留已分配容量（块编码复用缓冲用）。</summary>
    public void Reset() => Length = 0;

    /// <summary>当前内容视图（调用方不得在再次写入后继续持有）。</summary>
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, Length);

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buf[Length++] = value;
    }

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Length), value);
        Length += 2;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(Length), value);
        Length += 4;
    }

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(Length), value);
        Length += 8;
    }

    public void WriteVarint(ulong value)
    {
        Ensure(Varint.Size(value));
        Length += Varint.Write(_buf.AsSpan(Length), value);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buf.AsSpan(Length));
        Length += value.Length;
    }

    /// <summary>就地回填（用于先占位、后写入的偏移 / CRC 字段）。</summary>
    public void PatchUInt32(int offset, uint value)
    {
        if (offset < 0 || offset + 4 > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset), value);
    }

    /// <summary>就地回填 64 位值。</summary>
    public void PatchUInt64(int offset, ulong value)
    {
        if (offset < 0 || offset + 8 > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(offset), value);
    }

    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();

    private void Ensure(int extra)
    {
        var need = Length + extra;
        if (need <= _buf.Length) return;
        var next = _buf.Length;
        while (next < need)
            next = next >= int.MaxValue / 2 ? need : next * 2;
        Array.Resize(ref _buf, next);
    }
}

/// <summary>
/// 带边界校验的只读游标。所有越界读一律抛 <see cref="InvalidDataException"/>，
/// 不允许越界或静默截断（恶意 / 损坏文件只能报错或降级）。
/// </summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _src;
    private int _pos;

    public ByteReader(ReadOnlySpan<byte> src, int start = 0)
    {
        if ((uint)start > (uint)src.Length) throw new InvalidDataException("读取起点越界");
        _src = src;
        _pos = start;
    }

    public readonly int Position => _pos;
    public readonly int Remaining => _src.Length - _pos;

    public byte ReadByte()
    {
        if (Remaining < 1) throw new InvalidDataException("读取越界（byte）");
        return _src[_pos++];
    }

    public ushort ReadUInt16()
    {
        if (Remaining < 2) throw new InvalidDataException("读取越界（u16）");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_src[_pos..]);
        _pos += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        if (Remaining < 4) throw new InvalidDataException("读取越界（u32）");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_src[_pos..]);
        _pos += 4;
        return v;
    }

    public ulong ReadUInt64()
    {
        if (Remaining < 8) throw new InvalidDataException("读取越界（u64）");
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_src[_pos..]);
        _pos += 8;
        return v;
    }

    public ulong ReadVarint() => Varint.Read(_src, ref _pos);

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || Remaining < count) throw new InvalidDataException("读取越界（bytes）");
        var slice = _src.Slice(_pos, count);
        _pos += count;
        return slice;
    }

    public void Skip(int count)
    {
        if (count < 0 || Remaining < count) throw new InvalidDataException("读取越界（skip）");
        _pos += count;
    }
}
