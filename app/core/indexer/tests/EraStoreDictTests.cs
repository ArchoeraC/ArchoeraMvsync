// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P1：DocStore + 前编码词典。
/// 黄金夹具直接取设计文档 §3.3.0.14 的 3 首小曲（title 字段），要求逐字节一致。
/// </summary>
internal static class EraStoreDictTests
{
    public static void Run()
    {
        DictGoldenThreeTracks();
        DictLookup();
        DictPrefixAndFields();
        DictLongTermEscape();
        DictCorruption();
        DocStoreRoundTrip();
        DocStoreLayout();
    }

    /// <summary>§3.3.0.14：3 首小曲的 8 条 title 词典条目。</summary>
    private static readonly (string Term, uint Df)[] ThreeTrackTerms =
    {
        ("晴天", 2),
        ("天娃", 1),
        ("娃", 1),
        ("娃娃", 1),
        ("晴", 1),
        ("雨天", 1),
        ("雨", 1),
        ("天", 2),
    };

    private static TermDictBuilder BuildThreeTrackDict()
    {
        var b = new TermDictBuilder();
        foreach (var (term, df) in ThreeTrackTerms)
            b.Add(FieldId.Title, term, df, 0);
        return b;
    }

    private static void DictGoldenThreeTracks()
    {
        var (dict, index) = BuildThreeTrackDict().Build();

        // 块头 8B + 8 条共 88B = 96B；块索引 1 条 16B。
        Program.Check("词典黄金: 段长 96B", dict.Length == 96, dict.Length.ToString());
        Program.Check("词典黄金: 块索引 1 条",
            index.Length == 16
            && Convert.ToHexString(index) == "01E5A4A9000000000000000060000000",
            Convert.ToHexString(index));

        Program.Check("词典黄金: 块头 count=8 first_len=3",
            dict[0] == 8 && dict[1] == 3 && dict[2] == 0 && dict[3] == 0,
            $"{dict[0]},{dict[1]},{dict[2]},{dict[3]}");

        // 条目按字节序：天 / 天娃 / 娃 / 娃娃 / 晴 / 晴天 / 雨 / 雨天。
        const string expectedEntries =
            "000401E5A4A9020000000000" +
            "0403E5A883010000000000" +
            "0202A883010000000000" +
            "0403E5A883010000000000" +
            "0103E699B4010000000000" +
            "0403E5A4A9020000000000" +
            "0103E99BA8010000000000" +
            "0403E5A4A9010000000000";
        var actualEntries = Convert.ToHexString(dict.AsSpan(8));
        Program.Check("词典黄金: 8 条条目逐字节", actualEntries == expectedEntries, actualEntries);

        var crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dict.AsSpan(4));
        Program.Check("词典黄金: 块 CRC 覆盖块体", crc == Crc32C.Compute(dict.AsSpan(8)),
            $"0x{crc:X8}");
    }

    private static void DictLookup()
    {
        var (dict, index) = BuildThreeTrackDict().Build();
        var reader = new TermDictReader(dict, index);

        Program.Check("词典读取: 晴天 df=2",
            reader.TryFind(FieldId.Title, "晴天", out var qingtian) && qingtian.Df == 2,
            reader.TryFind(FieldId.Title, "晴天", out var q2) ? q2.Df.ToString() : "miss");
        Program.Check("词典读取: 天 df=2",
            reader.TryFind(FieldId.Title, "天", out var tian) && tian.Df == 2,
            reader.TryFind(FieldId.Title, "天", out var t2) ? t2.Df.ToString() : "miss");
        Program.Check("词典读取: 娃娃 df=1",
            reader.TryFind(FieldId.Title, "娃娃", out var wawa) && wawa.Df == 1,
            reader.TryFind(FieldId.Title, "娃娃", out var w2) ? w2.Df.ToString() : "miss");
        Program.Check("词典读取: 不存在返回 false",
            !reader.TryFind(FieldId.Title, "雪", out _));

        var all = reader.ReadAll().Select(x => x.Term).ToArray();
        Program.Check("词典读取: 全量顺序",
            string.Join('|', all) == "天|天娃|娃|娃娃|晴|晴天|雨|雨天",
            string.Join('|', all));
    }

    private static void DictPrefixAndFields()
    {
        var b = BuildThreeTrackDict();
        b.Add(FieldId.Artist, "晴天", 9, 0);
        b.Add(FieldId.Artist, "晴天娃娃", 4, 0);
        var (dict, index) = b.Build();
        var reader = new TermDictReader(dict, index);

        var titlePrefix = reader.ScanPrefix(FieldId.Title, "晴", 10).Select(x => x.Term).ToArray();
        Program.Check("词典前缀: title 晴 → 晴/晴天",
            string.Join('|', titlePrefix) == "晴|晴天", string.Join('|', titlePrefix));

        var artistPrefix = reader.ScanPrefix(FieldId.Artist, "晴", 10).Select(x => x.Term).ToArray();
        Program.Check("词典前缀: artist 晴 → 晴天/晴天娃娃",
            string.Join('|', artistPrefix) == "晴天|晴天娃娃", string.Join('|', artistPrefix));

        Program.Check("词典读取: 同词不同字段互不串",
            reader.TryFind(FieldId.Title, "晴天", out var t) && t.Df == 2
            && reader.TryFind(FieldId.Artist, "晴天", out var a) && a.Df == 9,
            "field 隔离失败");
    }

    private static void DictLongTermEscape()
    {
        // 255B 词条 → 键 256B，块内首条 shared=0，suffix_len 需 u16 转义。
        var longTerm = new string('z', EraFormat.MaxTermBytes);
        var b = new TermDictBuilder();
        b.Add(FieldId.Title, longTerm, 7, 0);
        var (dict, index) = b.Build();

        Program.Check("词典长词: 触发 255 转义",
            dict[9] == byte.MaxValue, $"suffix_len={dict[9]}");
        var reader = new TermDictReader(dict, index);
        Program.Check("词典长词: 往返",
            reader.TryFind(FieldId.Title, longTerm, out var info) && info.Df == 7,
            "往返失败");
    }

    private static void DictCorruption()
    {
        var (dict, index) = BuildThreeTrackDict().Build();
        dict[20] ^= 0xFF;
        var reader = new TermDictReader(dict, index);
        var threw = false;
        try
        {
            reader.TryFind(FieldId.Title, "晴天", out _);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Program.Check("词典损坏: CRC 不符抛异常", threw);

        var badIndex = false;
        try
        {
            _ = new TermDictReader(dict, index.AsSpan(0, 15).ToArray());
        }
        catch (InvalidDataException)
        {
            badIndex = true;
        }
        Program.Check("词典损坏: 块索引长度非法被拒", badIndex);
    }

    private static void DocStoreRoundTrip()
    {
        var docs = new[]
        {
            MakeDoc(1, "晴天", "周杰伦", "叶惠美"),
            MakeDoc(2, "晴天娃娃", "某歌手", "某专辑"),
            MakeDoc(3, "雨天", "孙燕姿", ""),
        };
        var body = DocStoreWriter.Write(docs);
        var reader = new DocStoreReader(body);

        Program.Check("DocStore: 文档数", reader.Count == 3, reader.Count.ToString());
        var ok = true;
        var detail = new StringBuilder();
        for (var i = 0; i < docs.Length; i++)
        {
            if (!reader.Track(i).SequenceEqual(docs[i].Track)) { ok = false; detail.Append($"track{i};"); }
            if (!reader.Rev(i).SequenceEqual(docs[i].Rev)) { ok = false; detail.Append($"rev{i};"); }
            if (reader.ScriptBits(i) != docs[i].ScriptBits) { ok = false; detail.Append($"script{i};"); }
            if (reader.Flags(i) != docs[i].Flags) { ok = false; detail.Append($"flags{i};"); }
            if (reader.LayerConf(i) != docs[i].LayerConf) { ok = false; detail.Append($"layerconf{i};"); }
            if (reader.Title(i) != docs[i].Title) { ok = false; detail.Append($"title{i};"); }
            if (reader.Artist(i) != docs[i].Artist) { ok = false; detail.Append($"artist{i};"); }
            if (reader.Album(i) != docs[i].Album) { ok = false; detail.Append($"album{i};"); }
            var norm = reader.NormSpans(i);
            if (Encoding.UTF8.GetString(norm.Title) != docs[i].TitleNorm) { ok = false; detail.Append($"tnorm{i};"); }
            if (Encoding.UTF8.GetString(norm.Artist) != docs[i].ArtistNorm) { ok = false; detail.Append($"anorm{i};"); }
            if (Encoding.UTF8.GetString(norm.Album) != docs[i].AlbumNorm) { ok = false; detail.Append($"lnorm{i};"); }
            if (reader.DocLen(i, FieldId.Title) != docs[i].DocLen[0]) { ok = false; detail.Append($"len{i};"); }
            if (reader.Mtime(i) != docs[i].Mtime) { ok = false; detail.Append($"mtime{i};"); }
            if (reader.LrcMtime(i) != docs[i].LrcMtime) { ok = false; detail.Append($"lrcm{i};"); }
            if (reader.LrcSize(i) != docs[i].LrcSize) { ok = false; detail.Append($"lrcs{i};"); }
        }
        Program.Check("DocStore: 全列往返", ok, detail.ToString());
    }

    private static void DocStoreLayout()
    {
        var body = DocStoreWriter.Write(new[] { MakeDoc(1, "晴天", "周杰伦", "叶惠美") });
        // 头 8B：count / version / text_columns。
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body);
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
        var cols = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
        Program.Check("DocStore 布局: 头字段",
            count == 1 && version == DocStoreWriter.Version && cols == DocStoreWriter.TextColumns,
            $"{count}/{version}/{cols}");
        Program.Check("DocStore 布局: 版本 v6 / 六列",
            DocStoreWriter.Version == 6 && DocStoreWriter.TextColumns == 6,
            $"{DocStoreWriter.Version}/{DocStoreWriter.TextColumns}");

        // 头(8) + track(16) + rev(16) + script(2) + flags(1) + layer_conf(1) + state(24) + doclen(24) = 92。
        var titleOffsAt = 8 + 16 + 16 + 2 + 1 + 1 + 24 + 24;
        var first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(titleOffsAt));
        var second = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(titleOffsAt + 4));
        Program.Check("DocStore 布局: title 偏移表", first == 0 && second == 6,
            $"{first}/{second}");
    }

    private static DocRecord MakeDoc(byte seed, string title, string artist, string album)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(seed + i);
            rev[i] = (byte)(seed * 31 + i);
        }
        var doc = new DocRecord
        {
            Track = track,
            Rev = rev,
            ScriptBits = (ushort)(seed << 4),
            Flags = (byte)(seed & 0x03),
            LayerConf = (byte)(seed & 0x3F),
            Title = title,
            Artist = artist,
            Album = album,
            TitleNorm = Analyzer.NormalizeMetadata(title),
            ArtistNorm = Analyzer.NormalizeMetadata(artist),
            AlbumNorm = Analyzer.NormalizeMetadata(album),
            Mtime = 1_700_000_000_000 + seed,
            LrcMtime = 1_700_000_100_000 + seed,
            LrcSize = 4_096 + seed,
        };
        doc.DocLen[0] = (ushort)Encoding.UTF8.GetByteCount(title);
        doc.DocLen[1] = (ushort)Encoding.UTF8.GetByteCount(artist);
        return doc;
    }
}
