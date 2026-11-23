// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P0：格式原语（LEB128 / CRC32C / 端序 / 块读写）单测。
/// 对应设计文档 §3.3.0.1 与 §5.3 的 P0 验收：1 万随机值往返 + 固定向量。
/// </summary>
internal static class EraStoreFormatTests
{
    public static void Run()
    {
        VarintFixedVectors();
        VarintRoundTrip();
        VarintErrors();
        Crc32CFixedVectors();
        EndianFixedVectors();
        ReaderBounds();
        WriterPatch();
    }

    private static void VarintFixedVectors()
    {
        CheckBytes("varint 0", 0UL, "00");
        CheckBytes("varint 1", 1UL, "01");
        CheckBytes("varint 127", 127UL, "7F");
        CheckBytes("varint 128", 128UL, "80 01");
        CheckBytes("varint 300", 300UL, "AC 02");
        CheckBytes("varint 16383", 16383UL, "FF 7F");
        CheckBytes("varint 16384", 16384UL, "80 80 01");
        CheckBytes("varint u64.max", ulong.MaxValue, "FF FF FF FF FF FF FF FF FF 01");
    }

    private static void CheckBytes(string name, ulong value, string expectedHex)
    {
        var buf = new byte[Varint.MaxBytes];
        var n = Varint.Write(buf, value);
        var actual = Convert.ToHexString(buf.AsSpan(0, n)).Chunk(2).Select(c => new string(c));
        var actualHex = string.Join(' ', actual);
        Program.Check($"{name} 字节序列", actualHex == expectedHex, $"{actualHex} != {expectedHex}");
        Program.Check($"{name} Size 一致", Varint.Size(value) == n, $"{Varint.Size(value)} != {n}");
    }

    private static void VarintRoundTrip()
    {
        // 确定性伪随机（xorshift64*），不依赖 Random 实现。
        ulong state = 0x9E3779B97F4A7C15UL;
        var buf = new byte[Varint.MaxBytes];
        var ok = true;
        var detail = new StringBuilder();

        for (var i = 0; i < 10_000; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var value = state * 0x2545F4914F6CDD1DUL;

            var n = Varint.Write(buf, value);
            var offset = 0;
            var decoded = Varint.Read(buf.AsSpan(0, n), ref offset);
            if (decoded != value || offset != n)
            {
                ok = false;
                detail.Append($"#{i}: {value} -> {decoded} (off {offset}/{n}); ");
                if (detail.Length > 200) break;
            }
        }

        Program.Check("varint 1 万随机值往返", ok, detail.ToString());
    }

    private static void VarintErrors()
    {
        // 截断：末字节仍带 continuation 位。
        var truncated = new byte[] { 0x80, 0x80 };
        var offset = 0;
        var ok = Varint.TryRead(truncated, ref offset, out _, out _);
        Program.Check("varint 截断被拒绝", !ok && offset == 0, $"ok={ok} off={offset}");

        // 10 字节且第 10 字节高 7 位非零 → 溢出。
        var overflow = new byte[10];
        Array.Fill(overflow, (byte)0xFF);
        overflow[9] = 0x02;
        offset = 0;
        ok = Varint.TryRead(overflow, ref offset, out _, out _);
        Program.Check("varint 溢出被拒绝", !ok && offset == 0, $"ok={ok} off={offset}");

        // 合法 10 字节上界：9 个 0xFF + 0x01。
        var max = new byte[10];
        Array.Fill(max, (byte)0xFF);
        max[9] = 0x01;
        offset = 0;
        var value = Varint.Read(max, ref offset);
        Program.Check("varint u64.max 解码", value == ulong.MaxValue && offset == 10,
            $"{value} off={offset}");
    }

    private static void Crc32CFixedVectors()
    {
        // CRC-32C 标准 check 值：0xE3069283。
        var check = Crc32C.Compute("123456789"u8);
        Program.Check("CRC32C '123456789' = 0xE3069283", check == 0xE3069283u,
            $"0x{check:X8}");
        Program.Check("CRC32C 空输入 = 0", Crc32C.Compute(ReadOnlySpan<byte>.Empty) == 0,
            $"0x{Crc32C.Compute(ReadOnlySpan<byte>.Empty):X8}");

        // 单字节 0x00 的已知值（反射多项式）。
        var zero = Crc32C.Compute(new byte[] { 0x00 });
        Program.Check("CRC32C single 0x00", zero == 0x527D5351u, $"0x{zero:X8}");
    }

    private static void EndianFixedVectors()
    {
        var w = new ByteWriter();
        w.WriteUInt16(0x1234);
        w.WriteUInt32(0x89ABCDEF);
        w.WriteUInt64(0x0123456789ABCDEFUL);
        var hex = Convert.ToHexString(w.Span);
        Program.Check("显式小端写出",
            hex == "3412EFCDAB89EFCDAB8967452301", hex);
    }

    private static void ReaderBounds()
    {
        var data = new byte[] { 1, 2, 3 };
        var r = new ByteReader(data);
        Program.Check("ByteReader 读 u16", r.ReadUInt16() == 0x0201, r.Position.ToString());
        Program.Check("ByteReader 剩余 1", r.Remaining == 1, r.Remaining.ToString());

        var threw = false;
        try
        {
            r.ReadUInt32();
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Program.Check("ByteReader 越界抛 InvalidDataException", threw);
    }

    private static void WriterPatch()
    {
        var w = new ByteWriter();
        var at = w.Length;
        w.WriteUInt32(0);
        w.WriteByte(0xAA);
        w.PatchUInt32(at, 0xDEADBEEF);
        var hex = Convert.ToHexString(w.Span);
        Program.Check("ByteWriter 回填 u32", hex == "EFBEADDEAA", hex);

        var patched = true;
        try
        {
            w.PatchUInt32(w.Length - 3, 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            patched = false;
        }
        Program.Check("ByteWriter 回填越界抛异常", !patched);
    }
}
