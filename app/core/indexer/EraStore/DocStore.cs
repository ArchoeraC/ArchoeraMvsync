// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 一条文档记录（写入侧）。<see cref="Track"/> / <see cref="Rev"/> 必须是 16 字节。
/// </summary>
internal sealed class DocRecord
{
    /// <summary>track_id（MD5，16B）。跨模块唯一键。</summary>
    public byte[] Track { get; set; } = new byte[EraFormat.TrackIdBytes];

    /// <summary>内容指纹（16B）。</summary>
    public byte[] Rev { get; set; } = new byte[EraFormat.TrackIdBytes];

    /// <summary>脚本画像。</summary>
    public ushort ScriptBits { get; set; }

    /// <summary>是否有译文 / 音译 / 侧车。</summary>
    public byte Flags { get; set; }

    /// <summary>各字段 token 数；下标 = (FieldId - 1)，共 12 项。</summary>
    public ushort[] DocLen { get; set; } = new ushort[12];

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
}

/// <summary>
/// 列式 DocStore（设计文档 §3.3.0.4）。只服务精确度复核、结果字段与片段定位。
///
/// 段体布局（全部小端、无对齐填充）：
/// <code>
/// u32 doc_count | u16 version(=1) | u16 text_columns(=3)
/// track[16×N] | rev[16×N] | script_bits[u16×N] | flags[u8×N]
/// doc_len[u16×12×N]                      # 下标 = (FieldId - 1)
/// title  : u32 offsets[N+1] + utf8 blob
/// artist : u32 offsets[N+1] + utf8 blob
/// album  : u32 offsets[N+1] + utf8 blob
/// </code>
/// 文本偏移相对各自 blob 起点；空串长度为 0。
/// </summary>
internal static class DocStoreWriter
{
    /// <summary>段体版本。</summary>
    public const ushort Version = 1;

    /// <summary>写入文档列表，返回 DocStore 段体。</summary>
    public static byte[] Write(IReadOnlyList<DocRecord> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var n = docs.Count;
        var w = new ByteWriter(Math.Max(64, n * 64));

        w.WriteUInt32((uint)n);
        w.WriteUInt16(Version);
        w.WriteUInt16(3);

        foreach (var d in docs)
        {
            Validate(d);
            w.WriteBytes(d.Track);
        }
        foreach (var d in docs) w.WriteBytes(d.Rev);
        foreach (var d in docs) w.WriteUInt16(d.ScriptBits);
        foreach (var d in docs) w.WriteByte(d.Flags);
        foreach (var d in docs)
            foreach (var len in d.DocLen)
                w.WriteUInt16(len);

        WriteTextColumn(w, docs, static d => d.Title);
        WriteTextColumn(w, docs, static d => d.Artist);
        WriteTextColumn(w, docs, static d => d.Album);

        return w.ToArray();
    }

    private static void WriteTextColumn(ByteWriter w, IReadOnlyList<DocRecord> docs, Func<DocRecord, string> pick)
    {
        var blobs = new byte[docs.Count][];
        for (var i = 0; i < docs.Count; i++)
            blobs[i] = Encoding.UTF8.GetBytes(pick(docs[i]) ?? string.Empty);

        var offs = new uint[docs.Count + 1];
        for (var i = 0; i < blobs.Length; i++)
            offs[i + 1] = offs[i] + (uint)blobs[i].Length;

        foreach (var off in offs) w.WriteUInt32(off);
        foreach (var blob in blobs) w.WriteBytes(blob);
    }

    private static void Validate(DocRecord d)
    {
        if (d.Track.Length != EraFormat.TrackIdBytes)
            throw new ArgumentException($"track 必须是 {EraFormat.TrackIdBytes} 字节", nameof(d));
        if (d.Rev.Length != EraFormat.TrackIdBytes)
            throw new ArgumentException($"rev 必须是 {EraFormat.TrackIdBytes} 字节", nameof(d));
        if (d.DocLen.Length != 12)
            throw new ArgumentException("doc_len 必须覆盖 12 个字段", nameof(d));
    }
}

/// <summary>
/// DocStore 只读视图。所有访问先做边界校验。
/// </summary>
internal sealed class DocStoreReader
{
    private readonly ReadOnlyMemory<byte> _body;
    private readonly int _count;
    private readonly int _trackOff;
    private readonly int _revOff;
    private readonly int _scriptOff;
    private readonly int _flagsOff;
    private readonly int _docLenOff;
    private readonly int _titleOff;
    private readonly int _artistOff;
    private readonly int _albumOff;

    public DocStoreReader(ReadOnlyMemory<byte> body)
    {
        if (body.Length < 8) throw new InvalidDataException("DocStore 段过短");
        _body = body;
        var span = body.Span;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (count > EraFormat.MaxDocCount) throw new InvalidDataException("DocStore doc_count 超限");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var textColumns = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        if (version != DocStoreWriter.Version) throw new InvalidDataException($"DocStore 版本 {version} 不支持");
        if (textColumns != 3) throw new InvalidDataException($"DocStore 文本列数 {textColumns} 不支持");

        _count = checked((int)count);
        // 每 doc 至少 16+16+2+1+24 字节；先挡掉会让偏移算术溢出的恶意计数。
        if ((long)_count * 32 > body.Length) throw new InvalidDataException("DocStore doc_count 与段长不符");
        var need = 8L
            + (long)_count * EraFormat.TrackIdBytes * 2
            + (long)_count * 2
            + _count
            + (long)_count * 2 * 12
            + (long)_count * 4 * 3; // 三列各 N 个偏移（最后一个由 blob 长度校验）
        if (need > body.Length) throw new InvalidDataException("DocStore 段长不足");

        _trackOff = 8;
        _revOff = _trackOff + _count * EraFormat.TrackIdBytes;
        _scriptOff = _revOff + _count * EraFormat.TrackIdBytes;
        _flagsOff = _scriptOff + _count * 2;
        _docLenOff = _flagsOff + _count;
        _titleOff = _docLenOff + _count * 2 * 12;

        // 三列依次为 offsets(N+1) + blob；从前往后依次算出各列起点。
        var titleOffsLen = (_count + 1) * 4;
        if (_titleOff + titleOffsLen > body.Length) throw new InvalidDataException("DocStore title 偏移表越界");
        var titleBlobLen = ReadColumnBlobLength(_titleOff, _count);
        _artistOff = _titleOff + titleOffsLen + titleBlobLen;

        if (_artistOff + titleOffsLen > body.Length) throw new InvalidDataException("DocStore artist 偏移表越界");
        var artistBlobLen = ReadColumnBlobLength(_artistOff, _count);
        _albumOff = _artistOff + titleOffsLen + artistBlobLen;

        if (_albumOff + titleOffsLen > body.Length) throw new InvalidDataException("DocStore album 偏移表越界");
        var albumBlobLen = ReadColumnBlobLength(_albumOff, _count);
        var end = (long)_albumOff + titleOffsLen + albumBlobLen;
        if (end != body.Length) throw new InvalidDataException("DocStore 段长与内容不符");
    }

    /// <summary>文档数。</summary>
    public int Count => _count;

    public ReadOnlySpan<byte> Track(int doc) => Slice(_trackOff + doc * EraFormat.TrackIdBytes, EraFormat.TrackIdBytes);

    public ReadOnlySpan<byte> Rev(int doc) => Slice(_revOff + doc * EraFormat.TrackIdBytes, EraFormat.TrackIdBytes);

    public ushort ScriptBits(int doc) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Slice(_scriptOff + doc * 2, 2));

    public byte Flags(int doc) => Slice(_flagsOff + doc, 1)[0];

    public ushort DocLen(int doc, FieldId field)
    {
        var index = (int)field - 1;
        if (index < 0 || index >= 12) throw new ArgumentOutOfRangeException(nameof(field));
        return BinaryPrimitives.ReadUInt16LittleEndian(Slice(_docLenOff + (doc * 12 + index) * 2, 2));
    }

    public string Title(int doc) => ReadText(_titleOff, doc);
    public string Artist(int doc) => ReadText(_artistOff, doc);
    public string Album(int doc) => ReadText(_albumOff, doc);

    private string ReadText(int columnOff, int doc)
    {
        CheckDoc(doc);
        var offs = _body.Span.Slice(columnOff, (_count + 1) * 4);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(offs[(doc * 4)..]);
        var end = BinaryPrimitives.ReadUInt32LittleEndian(offs[((doc + 1) * 4)..]);
        if (end < start) throw new InvalidDataException("DocStore 文本偏移非法");

        var blobStart = columnOff + (_count + 1) * 4;
        var len = checked((int)(end - start));
        return Encoding.UTF8.GetString(Slice(blobStart + (int)start, len));
    }

    private int ReadColumnBlobLength(int columnOff, int count)
    {
        var span = _body.Span;
        var last = BinaryPrimitives.ReadUInt32LittleEndian(span[(columnOff + count * 4)..]);
        if (last > int.MaxValue) throw new InvalidDataException("DocStore blob 超长");
        return (int)last;
    }

    private ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > _body.Length)
            throw new InvalidDataException("DocStore 读取越界");
        return _body.Span.Slice(offset, length);
    }

    private void CheckDoc(int doc)
    {
        if ((uint)doc >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(doc));
    }
}
