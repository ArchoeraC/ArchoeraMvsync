// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

using Archoera.Lyrics;

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

    /// <summary>
    /// 三层歌词置信度（DocStore v4，T4.2）：每层 2 bit，
    /// <c>0..3</c> = none / low / medium / high（见 <see cref="LyricConfidence"/>）。
    /// 位序：原文 bit0–1、译文 bit2–3、音译 bit4–5。
    /// </summary>
    public byte LayerConf { get; set; }

    /// <summary>各字段 token 数；下标 = (FieldId - 1)，共 12 项。</summary>
    public ushort[] DocLen { get; set; } = new ushort[12];

    /// <summary>源曲目 <c>file_mtime</c>（DocStore v2 起，T3.10 增量脏集判定）。</summary>
    public long Mtime { get; set; }

    /// <summary>侧车聚合 mtime（DocStore v2）。</summary>
    public long LrcMtime { get; set; }

    /// <summary>侧车聚合字节（DocStore v2）。</summary>
    public long LrcSize { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;

    /// <summary>
    /// 歌名归一形（DocStore v3，T3.11 exactness 复核）。
    /// 由 <see cref="IndexBuilder"/> 用 <see cref="Analyzer.NormalizeMetadata"/> 生成；
    /// <see cref="Merger"/> 从旧段<b>原样拷贝</b>，不重新归一（合并前后字节不变）。
    /// 源字段为空时为空串。
    /// </summary>
    public string TitleNorm { get; set; } = string.Empty;

    /// <inheritdoc cref="TitleNorm"/>
    public string ArtistNorm { get; set; } = string.Empty;

    /// <inheritdoc cref="TitleNorm"/>
    public string AlbumNorm { get; set; } = string.Empty;

    /// <summary>
    /// 歌词行摘要（DocStore v5，Q1 歌词精确度）：每行歌词归一后的 64 位哈希，
    /// **严格递增去重**（见 <see cref="LyricDigest"/>）。只覆盖原文 / 译文 / 显式音译
    /// 三层中实际参与索引的行；生成的罗马音（读音派生）不参与。
    /// 由 <see cref="IndexBuilder"/> 生成；<see cref="Merger"/> 从旧段原样拷贝，不重新哈希。
    /// </summary>
    public ulong[] LyricDigests { get; set; } = Array.Empty<ulong>();
}

/// <summary>
/// 列式 DocStore（设计文档 §3.3.0.4）。只服务精确度复核、结果字段与片段定位。
///
/// 段体布局（全部小端、无对齐填充）：
/// <code>
/// u32 doc_count | u16 version(=5) | u16 text_columns(=6)
/// track[16×N] | rev[16×N] | script_bits[u16×N] | flags[u8×N]
/// layer_conf[u8×N]                      # v4 三层置信度（每层 2 bit，T4.2）
/// mtime[u64×N] | lrc_mtime[u64×N] | lrc_size[u64×N]     # v2 增量状态
/// doc_len[u16×12×N]                      # 下标 = (FieldId - 1)
/// title  : u32 offsets[N+1] + utf8 blob   # 以下六列同构
/// artist
/// album
/// title_norm   # v3 归一形（T3.11；Analyzer.NormalizeMetadata）
/// artist_norm
/// album_norm
/// lyrics_digest: u32 offsets[N+1] + u64 sorted hashes   # v5（Q1；LyricDigest）
/// </code>
/// 文本偏移相对各自 blob 起点；空串长度为 0。v4 在 flags 后追加一列 <c>layer_conf</c>；
/// v5 在最后一列追加 <c>lyrics_digest</c>（每 doc 的行哈希偏移 + 有序哈希 blob）。
/// 其余列语义 / 顺序不变；旧 v1 / v2 / v3 / v4 布局不同，读取侧一律拒绝并提示全量重建。
/// </summary>
internal static class DocStoreWriter
{
    /// <summary>
    /// 段体版本（v2 增加增量状态列；v3 追加三列元数据归一形；v4 追加 <c>layer_conf</c>；
    /// v5 追加 <c>lyrics_digest</c> 歌词行哈希列，Q1）。
    /// 读取侧只接受本版本：旧索引必须全量重建，绝不按新布局误读。
    /// </summary>
    public const ushort Version = 5;

    /// <summary>文本列数：raw title / artist / album + 归一 title_norm / artist_norm / album_norm。</summary>
    public const ushort TextColumns = 6;

    /// <summary>写入文档列表，返回 DocStore 段体。</summary>
    public static byte[] Write(IReadOnlyList<DocRecord> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var n = docs.Count;
        var w = new ByteWriter(Math.Max(64, n * 64));

        w.WriteUInt32((uint)n);
        w.WriteUInt16(Version);
        w.WriteUInt16(TextColumns);

        foreach (var d in docs)
        {
            Validate(d);
            w.WriteBytes(d.Track);
        }
        foreach (var d in docs) w.WriteBytes(d.Rev);
        foreach (var d in docs) w.WriteUInt16(d.ScriptBits);
        foreach (var d in docs) w.WriteByte(d.Flags);
        foreach (var d in docs) w.WriteByte(d.LayerConf);
        foreach (var d in docs) w.WriteUInt64(unchecked((ulong)d.Mtime));
        foreach (var d in docs) w.WriteUInt64(unchecked((ulong)d.LrcMtime));
        foreach (var d in docs) w.WriteUInt64(unchecked((ulong)d.LrcSize));
        foreach (var d in docs)
            foreach (var len in d.DocLen)
                w.WriteUInt16(len);

        WriteTextColumn(w, docs, static d => d.Title);
        WriteTextColumn(w, docs, static d => d.Artist);
        WriteTextColumn(w, docs, static d => d.Album);
        WriteTextColumn(w, docs, static d => d.TitleNorm);
        WriteTextColumn(w, docs, static d => d.ArtistNorm);
        WriteTextColumn(w, docs, static d => d.AlbumNorm);
        WriteDigestColumn(w, docs);

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

    /// <summary>
    /// 歌词行摘要列（v5）：<c>u32 offsets[N+1]</c>（字节偏移，空 doc 与前一 doc 相同）
    /// + <c>u64×M</c> 小端有序哈希。偏移以字节计，便于和文本列一样用「末项 = blob 长度」
    /// 做段尾校验；哈希数量由相邻偏移差 / 8 得出。
    /// </summary>
    private static void WriteDigestColumn(ByteWriter w, IReadOnlyList<DocRecord> docs)
    {
        var offsets = new uint[docs.Count + 1];
        for (var i = 0; i < docs.Count; i++)
        {
            var digests = docs[i].LyricDigests;
            if (digests is { Length: > 0 })
            {
                if (!LyricDigest.IsSortedUnique(digests))
                    throw new ArgumentException("lyrics_digest 必须严格递增去重", nameof(docs));
            }
            offsets[i + 1] = offsets[i]
                + (digests is null ? 0u : checked((uint)digests.Length * 8u));
        }

        foreach (var off in offsets) w.WriteUInt32(off);
        foreach (var doc in docs)
        {
            if (doc.LyricDigests is null) continue;
            foreach (var hash in doc.LyricDigests) w.WriteUInt64(hash);
        }
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
/// 三列元数据归一形的 UTF-8 视图（零拷贝，指向段体内存；只在 reader 生命周期内有效）。
/// 供 ranker 做 exactness 复核时零分配读取（T3.11）。
/// </summary>
internal readonly ref struct DocNormSpans
{
    /// <summary>title_norm（源字段为空时为空 span）。</summary>
    public readonly ReadOnlySpan<byte> Title;

    /// <summary>artist_norm。</summary>
    public readonly ReadOnlySpan<byte> Artist;

    /// <summary>album_norm。</summary>
    public readonly ReadOnlySpan<byte> Album;

    public DocNormSpans(ReadOnlySpan<byte> title, ReadOnlySpan<byte> artist, ReadOnlySpan<byte> album)
    {
        Title = title;
        Artist = artist;
        Album = album;
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
    private readonly int _layerConfOff;
    private readonly int _stateOff;
    private readonly int _docLenOff;
    private readonly int _titleOff;
    private readonly int _artistOff;
    private readonly int _albumOff;
    private readonly int _titleNormOff;
    private readonly int _artistNormOff;
    private readonly int _albumNormOff;
    private readonly int _lyricsDigestOff;

    public DocStoreReader(ReadOnlyMemory<byte> body)
    {
        if (body.Length < 8) throw new InvalidDataException("DocStore 段过短");
        _body = body;
        var span = body.Span;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (count > EraFormat.MaxDocCount) throw new InvalidDataException("DocStore doc_count 超限");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var textColumns = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        if (version != DocStoreWriter.Version)
            throw new InvalidDataException(
                $"DocStore 版本 {version} 不支持（当前 {DocStoreWriter.Version}）；"
                + "旧 v1/v2/v3/v4 索引必须全量重建");
        if (textColumns != DocStoreWriter.TextColumns)
            throw new InvalidDataException(
                $"DocStore 文本列数 {textColumns} 不支持（当前 {DocStoreWriter.TextColumns}）；旧索引必须全量重建");

        _count = checked((int)count);
        // 每 doc 至少 16+16+2+1+24 字节；先挡掉会让偏移算术溢出的恶意计数。
        if ((long)_count * 32 > body.Length) throw new InvalidDataException("DocStore doc_count 与段长不符");
        var stateBytes = (long)_count * 24; // mtime + lrc_mtime + lrc_size
        var need = 8L
            + (long)_count * EraFormat.TrackIdBytes * 2
            + (long)_count * 2
            + _count                                // flags
            + _count                                // layer_conf（v4）
            + stateBytes
            + (long)_count * 2 * 12
            + (long)_count * 4 * DocStoreWriter.TextColumns // 每列 N+1 个偏移（末项由 blob 长度校验）
            + (long)_count * 4; // v5 lyrics_digest 偏移表（哈希 blob 由末项校验）
        if (need > body.Length) throw new InvalidDataException("DocStore 段长不足");

        _trackOff = 8;
        _revOff = _trackOff + _count * EraFormat.TrackIdBytes;
        _scriptOff = _revOff + _count * EraFormat.TrackIdBytes;
        _flagsOff = _scriptOff + _count * 2;
        _layerConfOff = _flagsOff + _count;
        _stateOff = _layerConfOff + _count;
        _docLenOff = _stateOff + (int)stateBytes;
        _titleOff = _docLenOff + _count * 2 * 12;

        // 六列依次为 offsets(N+1) + blob；从前往后依次算出各列起点（末列必须恰好收尾）。
        _artistOff = ReadColumnEnd(_titleOff, _count, "title");
        _albumOff = ReadColumnEnd(_artistOff, _count, "artist");
        _titleNormOff = ReadColumnEnd(_albumOff, _count, "album");
        _artistNormOff = ReadColumnEnd(_titleNormOff, _count, "title_norm");
        _albumNormOff = ReadColumnEnd(_artistNormOff, _count, "artist_norm");
        var end = ReadColumnEnd(_albumNormOff, _count, "album_norm");
        _lyricsDigestOff = end;
        var digestEnd = ReadBinaryColumnEnd(_lyricsDigestOff, _count, "lyrics_digest");
        if (digestEnd != body.Length) throw new InvalidDataException("DocStore 段长与内容不符");
    }

    /// <summary>文档数。</summary>
    public int Count => _count;

    /// <summary>文档 flags 的 bit2：track id 以 UTF-8 零填充保存（非 32-hex 的短 id）。</summary>
    public const byte FlagTextTrackId = 0x04;

    /// <summary>flags 里原文层来源的位移（2 bit，取值见 <see cref="LyricSource"/>）。</summary>
    public const int OriginalSourceShift = 3;

    /// <summary>flags 里译文层来源的位移（2 bit）。</summary>
    public const int TranslationSourceShift = 5;

    /// <summary>flags 里音译层来源的位移（1 bit：0 = 非显式，1 = 显式 <c>.romaji.lrc</c>）。</summary>
    public const int RomajiSourceShift = 7;

    /// <summary>把三层置信度打包成 <c>layer_conf</c> 字节（原文 bit0–1 / 译文 bit2–3 / 音译 bit4–5）。</summary>
    public static byte PackLayerConf(
        LyricConfidence original, LyricConfidence translation, LyricConfidence romaji)
        => (byte)((byte)original
            | ((byte)translation << 2)
            | ((byte)romaji << 4));

    /// <summary>把三层来源打包成 <c>flags</c> 的高 5 位（已左移，可直接 OR 进 flags）。</summary>
    public static byte PackLayerSources(
        LyricSource original, LyricSource translation, LyricSource romaji)
        => (byte)(((byte)original & 0x03) << OriginalSourceShift
            | ((byte)translation & 0x03) << TranslationSourceShift
            | ((byte)romaji & 0x01) << RomajiSourceShift);

    /// <summary>从 flags 读某层来源（原文 / 译文的 2 bit 原值；音译只有 1 bit：0 / 1）。</summary>
    public static LyricSource LayerSourceAt(byte flags, int layer) => layer switch
    {
        LrcReader.LayerOriginal => (LyricSource)((flags >> OriginalSourceShift) & 0x03),
        LrcReader.LayerTranslation => (LyricSource)((flags >> TranslationSourceShift) & 0x03),
        LrcReader.LayerRomaji => ((flags >> RomajiSourceShift) & 0x01) != 0
            ? LyricSource.ExplicitPair
            : LyricSource.Embedded,
        _ => LyricSource.None,
    };

    public ReadOnlySpan<byte> Track(int doc) => Slice(_trackOff + doc * EraFormat.TrackIdBytes, EraFormat.TrackIdBytes);

    /// <summary>
    /// 还原 track id：文本标志位 → 去尾零的 UTF-8；否则 16 字节转小写 hex。
    /// 生产曲目 id 恒为 32-hex，只有测试夹具 / 旧数据会走文本分支。
    /// </summary>
    public string TrackId(int doc)
    {
        var raw = Track(doc);
        if ((Flags(doc) & FlagTextTrackId) != 0)
            return Encoding.UTF8.GetString(raw).TrimEnd('\0');
        // 32-hex 小写：单次分配（旧实现 Convert.ToHexString + ToLowerInvariant 会分配两次）。
        return Convert.ToHexStringLower(raw);
    }

    public ReadOnlySpan<byte> Rev(int doc) => Slice(_revOff + doc * EraFormat.TrackIdBytes, EraFormat.TrackIdBytes);

    public ushort ScriptBits(int doc) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Slice(_scriptOff + doc * 2, 2));

    public byte Flags(int doc) => Slice(_flagsOff + doc, 1)[0];

    /// <summary>打包的三层置信度（原文 bit0–1 / 译文 bit2–3 / 音译 bit4–5）。</summary>
    public byte LayerConf(int doc) => Slice(_layerConfOff + doc, 1)[0];

    /// <summary>某层的置信度（<paramref name="layer"/> = <see cref="LrcReader.LayerOriginal"/> 等）。</summary>
    public LyricConfidence LayerConfidenceAt(int doc, int layer)
        => (LyricConfidence)((LayerConf(doc) >> (layer * 2)) & 0x03);

    /// <summary>源曲目 mtime（DocStore v2 起）。</summary>
    public long Mtime(int doc) => ReadState(_stateOff + doc * 8);

    /// <summary>侧车聚合 mtime。</summary>
    public long LrcMtime(int doc) => ReadState(_stateOff + _count * 8 + doc * 8);

    /// <summary>侧车聚合字节。</summary>
    public long LrcSize(int doc) => ReadState(_stateOff + _count * 16 + doc * 8);

    private long ReadState(int offset) =>
        unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8)));

    public ushort DocLen(int doc, FieldId field)
    {
        var index = (int)field - 1;
        if (index < 0 || index >= 12) throw new ArgumentOutOfRangeException(nameof(field));
        return BinaryPrimitives.ReadUInt16LittleEndian(Slice(_docLenOff + (doc * 12 + index) * 2, 2));
    }

    /// <summary>
    /// 单 doc 的 12 列 token 总数（BM25 的 <c>L</c>）。一次切片 + ushort 视图求和，
    /// 比 12 次 <see cref="DocLen"/> 少 11 次边界检查（宽命中集打分热点，T3.14）。
    /// </summary>
    public int RowLen(int doc)
    {
        var sum = 0;
        foreach (var value in Lengths(doc)) sum += value;
        return sum;
    }

    /// <summary>12 列长度（零拷贝视图，下标 = FieldId - 1）。</summary>
    public ReadOnlySpan<ushort> Lengths(int doc)
    {
        CheckDoc(doc);
        var span = Slice(_docLenOff + doc * 24, 24);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(span);
    }

    public string Title(int doc) => ReadText(_titleOff, doc);
    public string Artist(int doc) => ReadText(_artistOff, doc);
    public string Album(int doc) => ReadText(_albumOff, doc);

    /// <summary>
    /// 单个 doc 的三列归一形（UTF-8，零拷贝 span）。rank 的 exactness 复核入口：
    /// 给定段内 doc_id 直接读 <c>title_norm</c> / <c>artist_norm</c> / <c>album_norm</c>，
    /// 每次调用不分配（span 指向段体，reader 存活期间有效）。
    ///
    /// <para>列内空白规则见 <see cref="Analyzer.NormalizeMetadata"/>：词间保留单个 ASCII 空格。
    /// 拉丁字段按词级覆盖用空格切词；CJK 字段按码点覆盖，空格不参与命中。</para>
    /// </summary>
    public DocNormSpans NormSpans(int doc) => new(
        TextSpan(_titleNormOff, doc),
        TextSpan(_artistNormOff, doc),
        TextSpan(_albumNormOff, doc));

    /// <summary>该 doc 的歌词行哈希数量（<c>lyrics_digest</c> 列；v5 / Q1）。</summary>
    public int LyricDigestCount(int doc)
    {
        var span = DigestSpan(doc);
        return span.Length / 8;
    }

    /// <summary>
    /// 该 doc 的歌词行哈希**副本**（有序去重；合并 / 测试用）。查询热路径不调用它，
    /// 走 <see cref="ContainsLyricLine"/> 的二分查找（零分配）。
    /// </summary>
    public ulong[] LyricDigestArray(int doc)
    {
        var span = DigestSpan(doc);
        var count = span.Length / 8;
        var hashes = new ulong[count];
        for (var i = 0; i < count; i++)
            hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(span[(i * 8)..]);
        return hashes;
    }

    /// <summary>
    /// 该 doc 是否有一行歌词与查询整行相等（Q1）：在有序哈希数组里二分查找。
    /// 每次调用只读 O(log n) 个 8 字节，不做分配。
    /// </summary>
    public bool ContainsLyricLine(int doc, ulong hash)
    {
        var span = DigestSpan(doc);
        var count = span.Length / 8;
        var lo = 0;
        var hi = count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(span[(mid * 8)..]);
            if (value == hash) return true;
            if (value < hash) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    /// <summary>某一 lyrics_digest 列的 doc 切片（字节视图，长度恒为 8 的倍数）。</summary>
    private ReadOnlySpan<byte> DigestSpan(int doc)
    {
        CheckDoc(doc);
        var span = _body.Span;
        var offs = span.Slice(_lyricsDigestOff, (_count + 1) * 4);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(offs[(doc * 4)..]);
        var end = BinaryPrimitives.ReadUInt32LittleEndian(offs[((doc + 1) * 4)..]);
        if (end < start || (start & 7) != 0 || (end & 7) != 0)
            throw new InvalidDataException("DocStore lyrics_digest 偏移非法");

        var blobStart = _lyricsDigestOff + (_count + 1) * 4;
        return Slice(blobStart + (int)start, checked((int)(end - start)));
    }

    private string ReadText(int columnOff, int doc) => Encoding.UTF8.GetString(TextSpan(columnOff, doc));

    /// <summary>某一文本列的 doc 切片（UTF-8，零拷贝）。</summary>
    private ReadOnlySpan<byte> TextSpan(int columnOff, int doc)
    {
        CheckDoc(doc);
        var span = _body.Span;
        var offs = span.Slice(columnOff, (_count + 1) * 4);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(offs[(doc * 4)..]);
        var end = BinaryPrimitives.ReadUInt32LittleEndian(offs[((doc + 1) * 4)..]);
        if (end < start) throw new InvalidDataException("DocStore 文本偏移非法");

        var blobStart = columnOff + (_count + 1) * 4;
        return Slice(blobStart + (int)start, checked((int)(end - start)));
    }

    /// <summary>校验一列的偏移表 / blob 边界，返回下一列起点。</summary>
    private int ReadColumnEnd(int columnOff, int count, string name)
    {
        var offsLen = (count + 1) * 4;
        if ((long)columnOff + offsLen > _body.Length)
            throw new InvalidDataException($"DocStore {name} 偏移表越界");
        var blobLen = ReadColumnBlobLength(columnOff, count);
        if ((long)columnOff + offsLen + blobLen > _body.Length)
            throw new InvalidDataException($"DocStore {name} blob 越界");
        return columnOff + offsLen + blobLen;
    }

    /// <summary>校验二进制列（偏移表 + u64 blob）：blob 长度必须是 8 的倍数。</summary>
    private int ReadBinaryColumnEnd(int columnOff, int count, string name)
    {
        var offsLen = (count + 1) * 4;
        if ((long)columnOff + offsLen > _body.Length)
            throw new InvalidDataException($"DocStore {name} 偏移表越界");
        var blobLen = ReadColumnBlobLength(columnOff, count);
        if ((blobLen & 7) != 0)
            throw new InvalidDataException($"DocStore {name} blob 长度不是 8 的倍数");
        if ((long)columnOff + offsLen + blobLen > _body.Length)
            throw new InvalidDataException($"DocStore {name} blob 越界");
        return columnOff + offsLen + blobLen;
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
