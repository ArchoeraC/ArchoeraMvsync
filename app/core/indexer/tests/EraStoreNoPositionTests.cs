// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 免位置字段（<see cref="BuildOptions.NoPositionFields"/>）的字节级回归：
/// 词典 flags / 倒排块 flags 与块尾 pos_off / <see cref="PostingsCursor"/> 位置语义 /
/// 短语在无位置字段上的降级 / 文件体积与 DocStore 段一致性。
///
/// 固定 2 doc / 1 段：PinyinLyrics 免位置时倒排块 flags bit0=0、pos_off=0、
/// Positions 段不再为它追加任何字节；再以默认（带位置）构建同一语料逐条对照。
/// </summary>
internal static class EraStoreNoPositionTests
{
    private const int DocsPerSegment = 64;
    private const ulong CreatedMs = 1_760_000_000_000;
    private const ushort Segment = 0;
    private const byte FlagHasPositions = 0x01;

    public static void Run()
    {
        var dir = NewDir();
        try
        {
            RunCore(dir);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    private static void RunCore(string dir)
    {
        // ---- 免位置构建：PinyinLyrics 不写位置块 ----
        var noPosPath = Path.Combine(dir, "nopos", "search-1.eraindex");
        var (noPosResult, noPosRaw) = Build(noPosPath, noPositions: true);
        Program.Check("免位置: 2 doc / 1 段",
            noPosResult.SegmentCount == 1, noPosResult.SegmentCount.ToString());

        var (noPosDict, noPosPostings, noPosPostingsBody) = Readers(noPosRaw, noPosResult);

        var foundPy = noPosDict.TryFind(FieldId.PinyinLyrics, "tian", out var pyInfo);
        Program.Check("免位置: PinyinLyrics 词条存在且 flags bit0 = 0",
            foundPy && (pyInfo.Flags & FlagHasPositions) == 0,
            foundPy ? $"flags=0x{pyInfo.Flags:X2}" : "未命中");
        Program.Check("免位置: PinyinLyrics 词条 df = 2", foundPy && pyInfo.Df == 2,
            pyInfo.Df.ToString());

        var foundTitle = noPosDict.TryFind(FieldId.Title, "晴天", out var titleInfo);
        Program.Check("免位置: Title 词条 flags bit0 = 1",
            foundTitle && (titleInfo.Flags & FlagHasPositions) == FlagHasPositions,
            foundTitle ? $"flags=0x{titleInfo.Flags:X2}" : "未命中");

        // 按 §3.3.0.3 逐字节解析块头与块体，pos_off 在块最后 4B。
        var block = FirstBlock(noPosPostingsBody, pyInfo.PostOffset);
        Program.Check("免位置: 倒排块 doc_count = 2", block.DocCount == 2,
            block.DocCount.ToString());
        Program.Check("免位置: 倒排块头 flags bit0 = 0",
            (block.Flags & FlagHasPositions) == 0, $"flags=0x{block.Flags:X2}");
        Program.Check("免位置: 块头 flags 位于块偏移 +8（bit0 = 0）",
            (noPosPostingsBody[(int)pyInfo.PostOffset + 8] & FlagHasPositions) == 0,
            noPosPostingsBody[(int)pyInfo.PostOffset + 8].ToString());
        Program.Check("免位置: 块尾 pos_off = 0（逐字节读块最后 4B）",
            block.PosOff == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(
                    noPosPostingsBody.AsSpan(block.BlockEnd - 4, 4)) == 0,
            $"posOff={block.PosOff} end={block.BlockEnd}");

        var noPosCursor = noPosPostings.Open(pyInfo.PostOffset, pyInfo.Df);
        Program.Check("免位置: 游标首个 doc 为 0 且 HasPositions = false",
            noPosCursor.Next() && noPosCursor.CurrentDoc == 0 && !noPosCursor.HasPositions,
            $"doc={noPosCursor.CurrentDoc} hasPos={noPosCursor.HasPositions}");
        Program.Check("免位置: doc0 CurrentPositions 为空且 CurrentTf = 1",
            noPosCursor.CurrentPositions.IsEmpty && noPosCursor.CurrentTf == 1,
            $"tf={noPosCursor.CurrentTf}");
        Program.Check("免位置: doc1 无位置且 CurrentTf = 1",
            noPosCursor.Next() && noPosCursor.CurrentDoc == 1
                && noPosCursor.CurrentPositions.IsEmpty && noPosCursor.CurrentTf == 1,
            $"doc={noPosCursor.CurrentDoc} tf={noPosCursor.CurrentTf}");
        Program.Check("免位置: 游标耗尽", !noPosCursor.Next(), noPosCursor.CurrentDoc.ToString());

        using (var tian = new TermIter(noPosDict, noPosPostings, FieldId.PinyinLyrics, "tian"))
        {
            var docs = Show(Drain(tian));
            Program.Check("免位置: TermIter 'tian' 召回 doc0 与 doc1", docs == "0,1", docs);
        }

        using (var phrase = new PhraseIter(new IDocIter[]
               {
                   new TermIter(noPosDict, noPosPostings, FieldId.PinyinLyrics, "qing"),
                   new TermIter(noPosDict, noPosPostings, FieldId.PinyinLyrics, "tian"),
               }, positioned: true))
        {
            var docs = Show(Drain(phrase));
            // 引擎契约：任一子迭代器无位置 → 不校验相邻、按纯交集命中，Positioned 置 false。
            Program.Check("免位置: 短语无位置可校验时降级为纯交集（doc0、Positioned=false）",
                docs == "0" && !phrase.Positioned, $"{docs}/{phrase.Positioned}");
        }

        var noPosReport = IndexVerifier.Verify(noPosPath);
        Program.Check("免位置: IndexVerifier.Verify 通过", noPosReport.Ok,
            string.Join("; ", noPosReport.Errors));

        // ---- 对照组：默认带位置 ----
        var posPath = Path.Combine(dir, "pos", "search-1.eraindex");
        var (posResult, posRaw) = Build(posPath, noPositions: false);
        Program.Check("对照组: 2 doc / 1 段",
            posResult.SegmentCount == 1, posResult.SegmentCount.ToString());

        var (posDict, posPostings, posPostingsBody) = Readers(posRaw, posResult);
        var foundPosPy = posDict.TryFind(FieldId.PinyinLyrics, "tian", out var posPyInfo);
        Program.Check("对照组: PinyinLyrics 词条 flags bit0 = 1",
            foundPosPy && (posPyInfo.Flags & FlagHasPositions) == FlagHasPositions,
            foundPosPy ? $"flags=0x{posPyInfo.Flags:X2}" : "未命中");

        var posBlock = FirstBlock(posPostingsBody, posPyInfo.PostOffset);
        Program.Check("对照组: 倒排块头 flags bit0 = 1 且 pos_off 非 0",
            (posBlock.Flags & FlagHasPositions) != 0 && posBlock.PosOff != 0,
            $"flags=0x{posBlock.Flags:X2} posOff={posBlock.PosOff}");

        var posCursor = posPostings.Open(posPyInfo.PostOffset, posPyInfo.Df);
        Program.Check("对照组: 游标 HasPositions = true",
            posCursor.Next() && posCursor.HasPositions && posCursor.CurrentDoc == 0,
            $"doc={posCursor.CurrentDoc} hasPos={posCursor.HasPositions}");
        Program.Check("对照组: doc0 位置序列为 [1]",
            posCursor.CurrentDoc == 0
                && posCursor.CurrentPositions.SequenceEqual(new[] { 1 }),
            Show(posCursor.CurrentPositions.ToArray()));

        using (var phrase = new PhraseIter(new IDocIter[]
               {
                   new TermIter(posDict, posPostings, FieldId.PinyinLyrics, "qing"),
                   new TermIter(posDict, posPostings, FieldId.PinyinLyrics, "tian"),
               }, positioned: true))
        {
            var docs = Show(Drain(phrase));
            Program.Check("对照组: 短语 [qing,tian] 命中 doc0 且保持位置校验",
                docs == "0" && phrase.Positioned, $"{docs}/{phrase.Positioned}");
        }

        // ---- 两版对照：体积与与位置无关的段 ----
        Program.Check("两版对照: 免位置文件更短",
            noPosRaw.Length < posRaw.Length, $"{noPosRaw.Length}/{posRaw.Length}");

        var noPosDocStore = Body(noPosRaw, Section(noPosResult.Manifest.Sections, SectionKind.DocStore));
        var posDocStore = Body(posRaw, Section(posResult.Manifest.Sections, SectionKind.DocStore));
        Program.Check("两版对照: DocStore 段字节一致",
            noPosDocStore.AsSpan().SequenceEqual(posDocStore),
            $"{noPosDocStore.Length}/{posDocStore.Length}");

        Program.Check("两版对照: Dict / Postings 段长度不变",
            Section(noPosResult.Manifest.Sections, SectionKind.Dict).Length
                == Section(posResult.Manifest.Sections, SectionKind.Dict).Length
            && Section(noPosResult.Manifest.Sections, SectionKind.Postings).Length
                == Section(posResult.Manifest.Sections, SectionKind.Postings).Length,
            $"{Section(noPosResult.Manifest.Sections, SectionKind.Postings).Length}"
                + $"/{Section(posResult.Manifest.Sections, SectionKind.Postings).Length}");

        Program.Check("两版对照: Positions 段因免位置变短",
            Section(noPosResult.Manifest.Sections, SectionKind.Positions).Length
                < Section(posResult.Manifest.Sections, SectionKind.Positions).Length,
            $"{Section(noPosResult.Manifest.Sections, SectionKind.Positions).Length}"
                + $"/{Section(posResult.Manifest.Sections, SectionKind.Positions).Length}");
    }

    /* ------------------------------------------------------------------ */

    /// <summary>固定语料：doc0 = 晴天 / qing tian；doc1 = 雨天 / yu tian。</summary>
    private static List<AnalyzedDoc> Corpus()
    {
        var doc0 = Doc(0, new Dictionary<FieldId, string[]>
        {
            [FieldId.Title] = new[] { "晴天", "娃娃" },
            [FieldId.PinyinLyrics] = new[] { "qing", "tian" },
        });
        var doc1 = Doc(1, new Dictionary<FieldId, string[]>
        {
            [FieldId.Title] = new[] { "雨天" },
            [FieldId.PinyinLyrics] = new[] { "yu", "tian" },
        });
        return new List<AnalyzedDoc> { doc0, doc1 };
    }

    private static AnalyzedDoc Doc(int index, IReadOnlyDictionary<FieldId, string[]> tokens)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(index * 7 + i);
            rev[i] = (byte)(index * 13 + i * 3);
        }

        return new AnalyzedDoc(track, rev, (ushort)(index & 0x7), (byte)(index & 0x03), tokens,
            Text(tokens, FieldId.Title), string.Empty, string.Empty);
    }

    private static string Text(IReadOnlyDictionary<FieldId, string[]> tokens, FieldId field) =>
        tokens.TryGetValue(field, out var raw) ? string.Join(' ', raw) : string.Empty;

    /// <summary>固定 FileUuid / CreatedMs / 段大小；1 段即可容纳 2 doc。</summary>
    private static BuildOptions Options(bool noPositions)
    {
        var options = new BuildOptions
        {
            DocsPerSegment = DocsPerSegment,
            Gen = 1,
            FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray(),
            CreatedMs = CreatedMs,
        };
        if (noPositions) options.NoPositionFields.Add(FieldId.PinyinLyrics);
        return options;
    }

    private static (BuildResult Result, byte[] Raw) Build(string path, bool noPositions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = IndexBuilder.Build(path, Corpus(), Options(noPositions));
        return (result, File.ReadAllBytes(path));
    }

    private static (TermDictReader Dict, PostingsReader Postings, byte[] PostingsBody) Readers(
        byte[] raw, BuildResult result)
    {
        var sections = result.Manifest.Sections;
        var dict = new TermDictReader(
            Body(raw, Section(sections, SectionKind.Dict)),
            Body(raw, Section(sections, SectionKind.DictIndex)));
        var postingsBody = Body(raw, Section(sections, SectionKind.Postings));
        var positionsBody = OptionalBody(raw, sections, SectionKind.Positions);
        return (dict, new PostingsReader(postingsBody, positionsBody), postingsBody);
    }

    /// <summary>
    /// 按 §3.3.0.3 的 14B 块头 + 可变长块体解析首个倒排块，返回块头字段与块尾 pos_off：
    /// doc_count u16 | last_doc u32 | max_tf u16 | flags u8 | crc32c u32 | tf_exc_count u8 |
    /// 位图 16B | doc 序列 | tf 例外表 | pos_off u32（块最后 4B）。
    /// </summary>
    private static (int DocCount, int MaxTf, byte Flags, uint PosOff, int BlockEnd) FirstBlock(
        byte[] postingsBody, uint blockOffset)
    {
        var reader = new ByteReader(postingsBody, (int)blockOffset);
        var docCount = reader.ReadUInt16();
        reader.ReadUInt32(); // last_doc
        var maxTf = reader.ReadUInt16();
        var flags = reader.ReadByte();
        reader.ReadUInt32(); // crc32c
        var excCount = reader.ReadByte();
        // O10：tf 位图可裁剪；O9：doc 序列可位打包。
        var bitmapBytes = (flags & 0x04) != 0 ? (docCount + 7) / 8 : 16;
        reader.ReadBytes(bitmapBytes);
        if ((flags & 0x02) != 0)
        {
            var width = reader.ReadByte();
            reader.Skip((int)(((long)docCount * width + 7) / 8));
        }
        else
        {
            for (var i = 0; i < docCount; i++) reader.ReadVarint(); // doc_delta 序列
        }
        for (var i = 0; i < excCount; i++)
        {
            reader.ReadByte(); // doc_index
            reader.ReadVarint(); // tf
        }
        var posOff = reader.ReadUInt32(); // 块尾 pos_off
        return (docCount, maxTf, flags, posOff, reader.Position);
    }

    private static SectionRef Section(List<SectionRef> sections, SectionKind kind)
    {
        foreach (var section in sections)
            if (section.Kind == kind && section.SegmentId == Segment) return section;
        throw new InvalidOperationException($"缺少段 {kind}（seg={Segment}）");
    }

    private static byte[] Body(byte[] raw, SectionRef section) =>
        raw.AsSpan((int)section.Offset, (int)section.Length).ToArray();

    private static byte[] OptionalBody(byte[] raw, List<SectionRef> sections, SectionKind kind)
    {
        foreach (var section in sections)
            if (section.Kind == kind && section.SegmentId == Segment)
                return raw.AsSpan((int)section.Offset, (int)section.Length).ToArray();
        return Array.Empty<byte>();
    }

    private static List<int> Drain(IDocIter iter)
    {
        var docs = new List<int>();
        while (iter.Next()) docs.Add(iter.Current);
        return docs;
    }

    private static string Show(IEnumerable<int> docs) => string.Join(',', docs);

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-nopos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
