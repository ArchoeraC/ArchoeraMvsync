// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// O8：位置块位打包（<c>FlagPositionsPacked = 0x02</c>）的往返、自动选择、损坏拒绝、
/// 多 doc 游标切片与 <see cref="IndexVerifier"/>。
///
/// <para>打包块体仅为物理布局：解码值必须与 varint 块体逐位置一致，短语相邻语义不变。
/// 每个 doc 的 <c>width</c> 是最大 Δ 的位宽（0 只在单个位置 0 时出现），
/// Δpositions 按 LSB-first 打包。</para>
/// </summary>
internal static class EraStorePackedPositionsTests
{
    public static void Run()
    {
        DenseRoundTrip();
        SparseChoosesVarint();
        SingleZeroTieChoosesVarint();
        MaxDeltaPacked();
        MultiDocCursorSlices();
        PhraseAdjacency();
        CorruptPackedBodies();
        VerifierAcceptsPacked();
    }

    /* ------------------------------------------------------------------ */
    /* 写 / 读辅助                                                        */
    /* ------------------------------------------------------------------ */

    private static (byte[] Postings, byte[] Positions) Write(IReadOnlyList<PostingEntry> entries)
    {
        var postings = new ByteWriter();
        var positions = new ByteWriter();
        PostingsWriter.WriteTerm(postings, positions, entries);
        return (postings.ToArray(), positions.ToArray());
    }

    private static (byte[] Postings, byte[] Positions) WriteForced(
        bool packed, IReadOnlyList<PostingEntry> entries)
    {
        try
        {
            PostingsWriter.DebugForcePositionsPacked = packed;
            return Write(entries);
        }
        finally
        {
            PostingsWriter.DebugForcePositionsPacked = null;
        }
    }

    private static List<int[]> Decode(byte[] postings, byte[] positions, uint df)
    {
        var decoded = new PostingsReader(postings, positions).DecodeAll(0, df);
        return decoded.Select(e => e.Positions ?? Array.Empty<int>()).ToList();
    }

    private static bool Same(List<int[]> decoded, IReadOnlyList<int[]> expected)
    {
        if (decoded.Count != expected.Count) return false;
        for (var i = 0; i < decoded.Count; i++)
        {
            if (!decoded[i].AsSpan().SequenceEqual(expected[i])) return false;
        }
        return true;
    }

    private static string Show(IEnumerable<int[]> rows) =>
        string.Join(" | ", rows.Select(r => string.Join(',', r)));

    private static bool BlockIsPacked(byte[] positions) => (positions[6] & 0x02) != 0;

    /* ------------------------------------------------------------------ */
    /* 1. 往返                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>密集位置（Δ=1）：自动必须选打包，且块体小于 varint。</summary>
    private static void DenseRoundTrip()
    {
        var dense = Enumerable.Range(0, 200).ToArray();
        var entries = new[] { new PostingEntry(0, dense.Length, dense) };

        var (autoPost, autoPos) = Write(entries);
        var (varPost, varPos) = WriteForced(false, entries);

        Program.Check("O8 密集: 自动选位打包", BlockIsPacked(autoPos),
            $"flags=0x{autoPos[6]:X2}");
        Program.Check("O8 密集: 打包块体小于 varint",
            autoPos.Length < varPos.Length, $"{autoPos.Length}/{varPos.Length}");

        var decoded = Decode(autoPost, autoPos, 1);
        Program.Check("O8 密集: DecodeAll 位置逐项一致",
            Same(decoded, new[] { dense }), Show(decoded));

        var cursor = new PostingsReader(autoPost, autoPos).Open(0, 1);
        Program.Check("O8 密集: 游标 CurrentPositions 一致",
            cursor.Next() && cursor.CurrentPositions.SequenceEqual(dense),
            cursor.CurrentPositions.Length.ToString());

        var forcedPackedOut = WriteForced(true, entries);
        var forcedPacked = Decode(forcedPackedOut.Postings, forcedPackedOut.Positions, 1);
        Program.Check("O8 密集: 强制打包往返一致",
            Same(forcedPacked, new[] { dense }), Show(forcedPacked));
    }

    /// <summary>稀疏 / 大间隙：varint 更小，自动必须保留 varint；强制打包仍可无损解码。</summary>
    private static void SparseChoosesVarint()
    {
        var sparse = new[] { 0, 1_000, 5_000, 50_000 };
        var entries = new[] { new PostingEntry(0, sparse.Length, sparse) };

        var (autoPost, autoPos) = Write(entries);
        var (packedPost, packedPos) = WriteForced(true, entries);

        Program.Check("O8 稀疏: 自动保留 varint（flags 仅 bit0）", !BlockIsPacked(autoPos),
            $"flags=0x{autoPos[6]:X2}");
        Program.Check("O8 稀疏: varint 块体更小",
            autoPos.Length < packedPos.Length, $"{autoPos.Length}/{packedPos.Length}");
        Program.Check("O8 稀疏: varint 解码一致",
            Same(Decode(autoPost, autoPos, 1), new[] { sparse }),
            Show(Decode(autoPost, autoPos, 1)));
        Program.Check("O8 稀疏: 强制打包解码一致",
            Same(Decode(packedPost, packedPos, 1), new[] { sparse }),
            Show(Decode(packedPost, packedPos, 1)));
    }

    /// <summary>单个位置 0：varint 与打包等长（3B），平局保留 varint。</summary>
    private static void SingleZeroTieChoosesVarint()
    {
        var entries = new[] { new PostingEntry(0, 1, new[] { 0 }) };
        var (postings, positions) = Write(entries);

        Program.Check("O8 单点 0: 平局保留 varint", !BlockIsPacked(positions),
            $"flags=0x{positions[6]:X2}");
        Program.Check("O8 单点 0: 块体 00 01 00",
            Convert.ToHexString(positions.AsSpan(12)) == "000100",
            Convert.ToHexString(positions.AsSpan(12)));
        Program.Check("O8 单点 0: 解码一致",
            Same(Decode(postings, positions, 1), new[] { new[] { 0 } }),
            Show(Decode(postings, positions, 1)));

        // width=0 的唯一合法来源：强制打包时也必须写出 0 字节打包体并可解回 [0]。
        var (packedPost, packedPos) = WriteForced(true, entries);
        Program.Check("O8 单点 0: 强制打包 width=0 往返",
            BlockIsPacked(packedPos)
            && Same(Decode(packedPost, packedPos, 1), new[] { new[] { 0 } }),
            $"flags=0x{packedPos[6]:X2} body={Convert.ToHexString(packedPos.AsSpan(12))}");
    }

    /// <summary>接近 int 上限的大 Δ：打包仍无损，且位置累计不超过 int.MaxValue。</summary>
    private static void MaxDeltaPacked()
    {
        const int step = 200_000_000;
        var wide = Enumerable.Range(0, 10).Select(i => i * step).ToArray();
        Program.Check("O8 大 Δ: 最后一个位置 < int.MaxValue", wide[^1] < int.MaxValue,
            wide[^1].ToString());

        var entries = new[] { new PostingEntry(0, wide.Length, wide) };
        var (postings, positions) = Write(entries);
        Program.Check("O8 大 Δ: 自动选位打包", BlockIsPacked(positions),
            $"flags=0x{positions[6]:X2}");
        Program.Check("O8 大 Δ: 解码一致",
            Same(Decode(postings, positions, 1), new[] { wide }),
            Show(Decode(postings, positions, 1)));
    }

    /// <summary>
    /// 多 doc 位置块的游标切片：每个 doc 的 CurrentPositions 必须正好是自己的位置数组。
    /// 这条显式断言覆盖「packed / varint 两个分支的 flat 游标只能前进一次」。
    /// </summary>
    private static void MultiDocCursorSlices()
    {
        const int docCount = 300;
        var expected = new int[docCount][];
        var entries = new PostingEntry[docCount];
        for (var i = 0; i < docCount; i++)
        {
            // 偶数位置：Δ=2（首条 Δ=0），刚好跨 3 个 128-doc 块。
            expected[i] = Enumerable.Range(0, 50).Select(x => x * 2).ToArray();
            entries[i] = new PostingEntry(i, expected[i].Length, expected[i]);
        }

        var autoOut = Write(entries);
        var forcedPackedOut = WriteForced(true, entries);
        var forcedVarintOut = WriteForced(false, entries);
        var encodings = new (string Name, byte[] Postings, byte[] Positions)[]
        {
            ("自动", autoOut.Postings, autoOut.Positions),
            ("强制打包", forcedPackedOut.Postings, forcedPackedOut.Positions),
            ("强制 varint", forcedVarintOut.Postings, forcedVarintOut.Positions),
        };

        foreach (var (name, postings, positions) in encodings)
        {
            var decoded = Decode(postings, positions, docCount);
            Program.Check($"O8 多 doc({name}): DecodeAll 一致",
                Same(decoded, expected), $"{decoded.Count}/{docCount}");

            var cursor = new PostingsReader(postings, positions).Open(0, docCount);
            var ok = true;
            var seen = 0;
            while (cursor.Next())
            {
                if (cursor.CurrentDoc != seen
                    || !cursor.CurrentPositions.SequenceEqual(expected[seen]))
                {
                    ok = false;
                    break;
                }
                seen++;
            }
            Program.Check($"O8 多 doc({name}): 逐 doc CurrentPositions 一致",
                ok && seen == docCount, $"seen={seen} ok={ok}");
        }

        var auto = encodings[0];
        Program.Check("O8 多 doc: 自动首块 flags 打包",
            (auto.Positions[6] & 0x02) != 0, $"flags=0x{auto.Positions[6]:X2}");
    }

    /// <summary>短语相邻：打包块体下 [A,B] 命中，[B,A] 不命中，Positioned=true。</summary>
    private static void PhraseAdjacency()
    {
        var postings = new ByteWriter();
        var positions = new ByteWriter();
        var dict = new TermDictBuilder();

        var a = Enumerable.Range(0, 100).Select(i => i * 3).ToArray();
        var b = new[] { 1 };
        var aEntries = new[] { new PostingEntry(0, a.Length, a), new PostingEntry(1, 1, new[] { 0 }) };
        var bEntries = new[] { new PostingEntry(0, b.Length, b), new PostingEntry(1, 1, new[] { 5 }) };
        var aOff = PostingsWriter.WriteTerm(postings, positions, aEntries);
        var bOff = PostingsWriter.WriteTerm(postings, positions, bEntries);
        dict.Add(FieldId.Title, "a", (uint)aEntries.Length, aOff, 0x01);
        dict.Add(FieldId.Title, "b", (uint)bEntries.Length, bOff, 0x01);
        var (dictBytes, indexBytes) = dict.Build();
        var reader = new PostingsReader(postings.ToArray(), positions.ToArray());
        var termDict = new TermDictReader(dictBytes, indexBytes);

        Program.Check("O8 短语: 位置块 flags 打包",
            (positions.Span[6] & 0x02) != 0, $"flags=0x{positions.Span[6]:X2}");

        IDocIter[] AB() => new IDocIter[]
        {
            new TermIter(termDict, reader, FieldId.Title, "a"),
            new TermIter(termDict, reader, FieldId.Title, "b"),
        };

        using (var phrase = new PhraseIter(AB(), true))
        {
            Program.Check("O8 短语: [A,B] 命中 doc0 / Positioned=true",
                phrase.Next() && phrase.Current == 0 && phrase.Positioned,
                $"{phrase.Current}/{phrase.Positioned}");
            Program.Check("O8 短语: 只命中 doc0", !phrase.Next() && phrase.Current == -1,
                phrase.Current.ToString());
        }

        using (var reversed = new PhraseIter(new IDocIter[]
               {
                   new TermIter(termDict, reader, FieldId.Title, "b"),
                   new TermIter(termDict, reader, FieldId.Title, "a"),
               }, true))
        {
            Program.Check("O8 短语: [B,A] 不命中", !reversed.Next() && reversed.Current == -1,
                reversed.Current.ToString());
        }
    }

    /* ------------------------------------------------------------------ */
    /* 2. 损坏                                                            */
    /* ------------------------------------------------------------------ */

    private static void CorruptPackedBodies()
    {
        // 密集 Δ=1，width=1，块体 25B 恰好无尾部位。
        var dense = Enumerable.Range(0, 200).ToArray();
        var (densePostings, packed) = Write(new[] { new PostingEntry(0, dense.Length, dense) });
        var widthOffset = FirstPackedWidthOffset(packed);
        var dataOffset = widthOffset + 1;
        bool Corrupt(byte[] positions) => ThrowsInvalidData(
            () => _ = new PostingsReader(densePostings, positions).DecodeAll(0, 1));

        Program.Check("O8 损坏: 夹具位打包 width=1",
            packed[widthOffset] == 1, packed[widthOffset].ToString());

        // width > 31：结构检查必须在 CRC 之前拒绝。
        var badWidth = (byte[])packed.Clone();
        badWidth[widthOffset] = 32;
        RefreshCrc(badWidth);
        Program.Check("O8 损坏: width=32 抛 InvalidDataException", Corrupt(badWidth), "未抛");

        // 截断打包字节：ReadBytes 必须拒绝。
        var truncated = packed.AsSpan(0, packed.Length - 1).ToArray();
        Program.Check("O8 损坏: 打包字节截断抛 InvalidDataException", Corrupt(truncated), "未抛");

        // 后续 Δ=0：width=1、全 1 的打包体里制造一个 0 位。
        var zeroDelta = (byte[])packed.Clone();
        zeroDelta[dataOffset + 1] = 0xFE;
        RefreshCrc(zeroDelta);
        Program.Check("O8 损坏: 非首个 Δ=0 抛 InvalidDataException", Corrupt(zeroDelta), "未抛");

        // 未知 flags 位仍拒绝（只允许 0x01 | 0x02）。
        var badFlags = (byte[])packed.Clone();
        badFlags[6] |= 0x04;
        Program.Check("O8 损坏: 未知 flags 位抛 InvalidDataException", Corrupt(badFlags), "未抛");

        // CRC 不符：Δ=2（width=2）时把首个 Δ 从 0 改成 1，结构仍合法，只能由 CRC 拦下。
        var even = Enumerable.Range(0, 100).Select(i => i * 2).ToArray();
        var (evenPostings, evenPacked) = Write(new[] { new PostingEntry(0, even.Length, even) });
        var evenWidthOffset = FirstPackedWidthOffset(evenPacked);
        Program.Check("O8 损坏: 夹具位打包 width=2",
            evenPacked[evenWidthOffset] == 2, evenPacked[evenWidthOffset].ToString());
        var badCrc = (byte[])evenPacked.Clone();
        badCrc[evenWidthOffset + 1] |= 0x01; // Δ0: 0 → 1，结构合法
        Program.Check("O8 损坏: CRC 不符抛 InvalidDataException",
            ThrowsInvalidData(() => _ = new PostingsReader(evenPostings, badCrc).DecodeAll(0, 1)),
            "未抛");
    }

    private static int FirstPackedWidthOffset(byte[] positions)
    {
        var offset = 12;
        _ = Varint.Read(positions, ref offset); // doc_delta
        _ = Varint.Read(positions, ref offset); // tf
        return offset;
    }

    private static void RefreshCrc(byte[] positions)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            positions.AsSpan(8, 4), Crc32C.Compute(positions.AsSpan(12)));
    }

    private static bool ThrowsInvalidData(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 3. IndexVerifier                                                   */
    /* ------------------------------------------------------------------ */

    private static void VerifierAcceptsPacked()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "archoera-o8-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var tokens = Enumerable.Repeat("a", 200).ToArray();
            var doc = new AnalyzedDoc(
                MakeBytes(0xC0), MakeBytes(0x80), 1, 2,
                new Dictionary<FieldId, string[]> { [FieldId.Title] = tokens },
                "w1", "s1", "a1");
            IndexBuilder.Build(path, new[] { doc }, new BuildOptions
            {
                DocsPerSegment = 64,
                Gen = 1,
                FileUuid = MakeBytes(0xA0),
                CreatedMs = 1_700_000_000_000,
                NormalizerVersion = 1,
                TokenizerVersion = 2,
            });

            Program.Check("O8 verify: 位打包索引 verify 通过",
                IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));

            using (var file = IndexFile.Open(path))
            {
                var section = file.Current!.Sections.Single(s => s.Kind == SectionKind.Positions);
                var raw = File.ReadAllBytes(path);
                Program.Check("O8 verify: 位置段首块 flags 打包",
                    (raw[(int)section.Offset + 6] & 0x02) != 0,
                    $"flags=0x{raw[(int)section.Offset + 6]:X2}");

                var corrupt = Path.Combine(dir, "corrupt.eraindex");
                raw[(int)section.Offset + 12] ^= 0xFF;
                File.WriteAllBytes(corrupt, raw);
                var report = IndexVerifier.Verify(corrupt);
                Program.Check("O8 verify: 位置段损坏仍被检出", !report.Ok
                    && report.Errors.Any(e => e.Contains("Positions") && e.Contains("crc")),
                    string.Join(" | ", report.Errors));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Windows 上 mmap 未释放时忽略清理失败。
            }
        }
    }

    private static byte[] MakeBytes(byte start) =>
        Enumerable.Range(0, 16).Select(i => (byte)(start + i)).ToArray();
}
