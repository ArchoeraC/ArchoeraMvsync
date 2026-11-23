// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.Tests;

/// <summary>
/// P10 模糊测试的确定性变异工具（设计文档 §3.3.0.16 / §5.3 P10）。
/// 只用 xorshift，不依赖 <see cref="Random"/>，同一份基线在任意平台得到同样的变异序列。
/// </summary>
internal static class Fuzz
{
    /// <summary>确定性伪随机（xorshift64）。</summary>
    internal sealed class Rng
    {
        private ulong _state;

        public Rng(ulong seed) => _state = seed == 0 ? 0x9E37_79B9_7F4A_7C15UL : seed;

        /// <summary>下一个 64 位随机值。</summary>
        public ulong NextU64()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            _state = x;
            return x;
        }

        /// <summary><c>[0, exclusiveMax)</c>；非正数按 0 处理。</summary>
        public int NextInt(int exclusiveMax) =>
            exclusiveMax <= 1 ? 0 : (int)(NextU64() % (ulong)exclusiveMax);

        /// <summary><c>[start, end)</c> 内的偏移；空区间返回 <paramref name="start"/>。</summary>
        public int PickIn(int start, int end) => end <= start ? start : start + NextInt(end - start);
    }

    /// <summary>一次翻转变异：偏移集合 + 覆盖区域标签。</summary>
    internal readonly record struct FlipCase(int[] Offsets, string Region);

    /// <summary>截断到 <paramref name="length"/>（越界自动夹到文件长度）。</summary>
    internal static byte[] Truncate(byte[] source, int length)
    {
        ArgumentNullException.ThrowIfNull(source);
        var take = Math.Clamp(length, 0, source.Length);
        return source.AsSpan(0, take).ToArray();
    }

    /// <summary>对给定偏移逐字节异或非零随机掩码（越界偏移忽略）。</summary>
    internal static byte[] Flip(byte[] source, IReadOnlyList<int> offsets, Rng rng)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(rng);

        var copy = (byte[])source.Clone();
        foreach (var offset in offsets)
        {
            if ((uint)offset >= (uint)copy.Length) continue;
            copy[offset] ^= (byte)(1 + rng.NextInt(byte.MaxValue));
        }
        return copy;
    }
}
