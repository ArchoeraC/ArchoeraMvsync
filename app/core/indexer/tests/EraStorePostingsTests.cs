// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P2：倒排 + 位置 + tf 位图 + 跳块。
/// 黄金夹具直接取设计文档 §3.3.0.14 的 <c>晴天</c>（doc0 / doc1），要求逐字节一致。
/// </summary>
internal static class EraStorePostingsTests
{
    public static void Run()
    {
        SingleBlockRoundTrip();
        TfExceptionTable();
        MultiBlockAndAdvance();
        PositionDeltas();
        PostingsCorruption();
        GoldenQingTian();
    }

    private static (byte[] Postings, byte[] Positions) Write(IReadOnlyList<PostingEntry> entries)
    {
        var postings = new ByteWriter();
        var positions = new ByteWriter();
        PostingsWriter.WriteTerm(postings, positions, entries);
        return (postings.ToArray(), positions.ToArray());
    }

    /// <summary>全 tf=1 + 位置，单块往返。</summary>
    private static void SingleBlockRoundTrip()
    {
        var entries = new[]
        {
            new PostingEntry(0, 1, new[] { 0 }),
            new PostingEntry(2, 2, new[] { 0, 1 }),
            new PostingEntry(3, 1, new[] { 5 }),
        };
        var (postings, positions) = Write(entries);
        var reader = new PostingsReader(postings, positions);

        Program.Check("倒排单块: 块头 doc_count=3", postings[0] == 3 && postings[1] == 0,
            $"{postings[0]},{postings[1]}");

        var decoded = reader.DecodeAll(0, 3);
        var ok = decoded.Count == 3;
        for (var i = 0; i < decoded.Count && ok; i++)
        {
            ok = decoded[i].Doc == entries[i].Doc
                && decoded[i].Tf == entries[i].Tf
                && decoded[i].Positions is not null
                && decoded[i].Positions!.SequenceEqual(entries[i].Positions!);
        }
        Program.Check("倒排单块: 往返", ok, decoded.Count.ToString());
    }

    /// <summary>tf&gt;1 例外表：doc5 tf=3，位图 bit5=0，例外表含 index 5 / tf 3。</summary>
    private static void TfExceptionTable()
    {
        var entries = new PostingEntry[10];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new PostingEntry(i, i == 5 ? 3 : 1, null);

        var (postings, positions) = Write(entries);
        var reader = new PostingsReader(postings, positions);

        // 块头 14 + 位图 16 + 10 条 doc（各 1B）+ 例外（index 1B + tf 1B）+ pos_off 4。
        Program.Check("tf 例外: 块长", postings.Length == 14 + 16 + 10 + 2 + 4,
            postings.Length.ToString());

        var bitmap0 = postings[14];
        Program.Check("tf 例外: 位 bitmap[0]=0xDF 且 bit5=0",
            bitmap0 == 0xDF && (bitmap0 & 0x20) == 0, $"0x{bitmap0:X2}");
        Program.Check("tf 例外: 位 bitmap[1]=0x03", postings[15] == 0x03, $"0x{postings[15]:X2}");
        Program.Check("tf 例外: tf_exc_count=1", postings[13] == 1, postings[13].ToString());

        var excAt = 14 + 16 + 10;
        Program.Check("tf 例外: 表项 index=5 tf=3",
            postings[excAt] == 5 && postings[excAt + 1] == 3,
            $"{postings[excAt]},{postings[excAt + 1]}");

        var decoded = reader.DecodeAll(0, 10);
        var tfOk = decoded.Count == 10;
        for (var i = 0; i < decoded.Count && tfOk; i++)
            tfOk = decoded[i].Tf == (i == 5 ? 3 : 1) && decoded[i].Positions is null;
        Program.Check("tf 例外: 解码 tf 正确", tfOk, decoded.Count.ToString());
    }

    /// <summary>300 doc 跨 3 块；DecodeAll 顺序正确，Advance(150) 落到 150。</summary>
    private static void MultiBlockAndAdvance()
    {
        var entries = new PostingEntry[300];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new PostingEntry(i, 1, null);

        var (postings, positions) = Write(entries);
        var reader = new PostingsReader(postings, positions);

        var decoded = reader.DecodeAll(0, 300);
        var ordered = decoded.Count == 300;
        for (var i = 0; i < decoded.Count && ordered; i++)
            ordered = decoded[i].Doc == i && decoded[i].Tf == 1;
        Program.Check("倒排多块: 300 条顺序正确", ordered, decoded.Count.ToString());

        var cursor = reader.Open(0, 300);
        var advanced = cursor.Advance(150);
        Program.Check("倒排多块: Advance(150) 命中 doc=150",
            advanced && cursor.CurrentDoc == 150 && cursor.CurrentTf == 1,
            $"{advanced}/{cursor.CurrentDoc}");
        Program.Check("倒排多块: Advance(9999) 耗尽", !cursor.Advance(9999),
            cursor.CurrentDoc.ToString());
    }

    /// <summary>位置 Δ 编码往返 [0,2,5,9]。</summary>
    private static void PositionDeltas()
    {
        var entries = new[] { new PostingEntry(7, 4, new[] { 0, 2, 5, 9 }) };
        var (postings, positions) = Write(entries);
        var decoded = new PostingsReader(postings, positions).DecodeAll(0, 1);

        Program.Check("位置 Δ: 往返 [0,2,5,9]",
            decoded.Count == 1 && decoded[0].Positions is not null
            && decoded[0].Positions!.SequenceEqual(new[] { 0, 2, 5, 9 }),
            decoded.Count == 1 && decoded[0].Positions is not null
                ? string.Join(',', decoded[0].Positions!)
                : "miss");

        // 位置块体：doc_delta=7 → 07；pos_count=4 → 04；位置 Δ = 0,2,3,4。
        Program.Check("位置 Δ: 块体逐字节", Convert.ToHexString(positions.AsSpan(12)) == "070400020304",
            Convert.ToHexString(positions.AsSpan(12)));
    }

    private static void PostingsCorruption()
    {
        var entries = new[] { new PostingEntry(0, 1, new[] { 0 }) };
        var (postings, positions) = Write(entries);

        postings[^1] ^= 0xFF; // 翻转 pos_off 最低字节，CRC 必然不符
        var threw = false;
        try
        {
            _ = new PostingsReader(postings, positions).DecodeAll(0, 1);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Program.Check("倒排损坏: CRC 不符抛 InvalidDataException", threw);
    }

    /// <summary>§3.3.0.14：<c>晴天</c>（doc0 / doc1，tf=1，位置各 [0]）逐字节。</summary>
    private static void GoldenQingTian()
    {
        var entries = new[]
        {
            new PostingEntry(0, 1, new[] { 0 }),
            new PostingEntry(1, 1, new[] { 0 }),
        };
        var (postings, positions) = Write(entries);

        Program.Check("倒排黄金: 段长 36B", postings.Length == 36, postings.Length.ToString());
        Program.Check("位置黄金: 段长 18B", positions.Length == 18, positions.Length.ToString());

        var postingsCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(postings.AsSpan(9));
        var expectedPostings =
            "0200" +                    // doc_count = 2
            "01000000" +                // last_doc = 1
            "0100" +                    // max_tf = 1
            "01" +                      // flags = has_positions
            Convert.ToHexString(postings.AsSpan(9, 4)) + // crc32c（覆盖块头之后，按存储字节序）
            "00" +                      // tf_exc_count = 0
            "03" + new string('0', 30) + // tf 位图 16B：bit0/bit1
            "0001" +                    // doc 序列：0, 1（delta 1）
            "00000000";                 // pos_off = 0（位置段首块）
        var actualPostings = Convert.ToHexString(postings);
        Program.Check("倒排黄金: 逐字节", actualPostings == expectedPostings, actualPostings);
        Program.Check("倒排黄金: CRC 覆盖块体",
            postingsCrc == Crc32C.Compute(postings.AsSpan(14)), $"0x{postingsCrc:X8}");

        var positionsCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(positions.AsSpan(8));
        var expectedPositions =
            "0200" +                    // doc_count = 2
            "01000000" +                // last_doc = 1
            "01" +                      // flags = has_positions
            "00" +                      // reserved
            Convert.ToHexString(positions.AsSpan(8, 4)) + // crc32c（覆盖块头之后，按存储字节序）
            "000100" +                  // doc0: delta 0 / pos_count 1 / pos 0
            "010100";                   // doc1: delta 1 / pos_count 1 / pos 0
        var actualPositions = Convert.ToHexString(positions);
        Program.Check("位置黄金: 逐字节", actualPositions == expectedPositions, actualPositions);
        Program.Check("位置黄金: CRC 覆盖块体",
            positionsCrc == Crc32C.Compute(positions.AsSpan(12)), $"0x{positionsCrc:X8}");
    }
}
