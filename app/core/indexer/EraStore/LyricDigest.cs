// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 歌词行摘要（Q1 歌词精确度）：把每一行歌词归一后压成 64 位哈希，
/// 在 <see cref="DocStoreReader"/> 里按 doc 存一份**有序去重**的数组。
///
/// <para><b>为什么是哈希而不是原文</b>：DocStore 的体积预算只有几 KB / 曲，
/// 存整段歌词会把索引推回 FTS 时代的量级；64 位行哈希只够回答「这一整行是否
/// 等于查询」，正好补上歌词族 <c>exactness = none</c> 的缺口。前缀 / 包含关系
/// 无法从哈希恢复，歌词族仍只在**整行相等**时记 <c>Equal</c>（见 gate 文档 §9.1 Q1）。</para>
///
/// <para><b>归一与哈希</b>：一行先走 <see cref="Analyzer.NormalizeMetadata"/>
/// （与查询归一形、元数据归一列同一函数：NFKC / 音标 / 异体 / 小写 / 繁简，
/// 空白折叠为单个 ASCII 空格并去首尾），再按 UTF-8 字节做 <b>FNV-1a 64</b>，
/// 跳过 ASCII 空格（<c>0x20</c>）——这与 <see cref="SearchExactness.NormalizeQuery"/>
/// 给查询算哈希的口径完全一致，因此「我爱你 中国」与「我爱你中国」等值。</para>
///
/// <para><b>碰撞</b>：64 位在单曲 100 行量级上的碰撞概率可以忽略；即便碰撞也只
/// 影响排序的第 4 键（多一条行相等），不会改变命中集合 / 高亮 / 片段。哈希是
/// 构建期一次算定、合并期原样搬运的物理数据，不依赖运行期随机种子。</para>
/// </summary>
internal static class LyricDigest
{
    /// <summary>FNV-1a 64 位偏移基数。</summary>
    private const ulong FnvOffsetBasis = 14695981039346656037UL;

    /// <summary>FNV-1a 64 位质数。</summary>
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>归一形里唯一保留的空白（<see cref="Analyzer.NormalizeMetadata"/> 折叠后）。</summary>
    private const int Space = 0x20;

    /// <summary>
    /// 一行歌词的哈希：<see cref="Analyzer.NormalizeMetadata"/> → 去 ASCII 空格 →
    /// UTF-8 字节 FNV-1a。空行 / 纯空白行返回 <c>null</c>（调用方跳过，不写 0）。
    /// </summary>
    public static ulong? HashLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var normalized = Analyzer.NormalizeMetadata(line);
        if (normalized.Length == 0) return null;

        ulong hash = FnvOffsetBasis;
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value == Space) continue;
            var length = rune.EncodeToUtf8(buffer);
            for (var i = 0; i < length; i++)
            {
                hash ^= buffer[i];
                hash *= FnvPrime;
            }
        }
        return hash;
    }

    /// <summary>
    /// 查询归一形的哈希（输入是 <see cref="SearchExactness.NormalizeQuery"/> 的产物，
    /// 空格已去掉；这里仍防御性地跳过 <c>0x20</c>）。空查询返回 <c>null</c>。
    /// </summary>
    public static ulong? HashQuery(ReadOnlySpan<byte> normalizedUtf8)
    {
        if (normalizedUtf8.Length == 0) return null;

        ulong hash = FnvOffsetBasis;
        foreach (var b in normalizedUtf8)
        {
            if (b == Space) continue;
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// 把多行文本压成**有序去重**的哈希数组（构建 / 增量写入的唯一生成点）。
    /// 同一行在不同歌词层重复出现时只保留一个哈希；空行跳过。
    /// </summary>
    public static ulong[] Build(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0) return Array.Empty<ulong>();

        var hashes = new ulong[lines.Count];
        var count = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (HashLine(lines[i]) is { } hash) hashes[count++] = hash;
        }

        Array.Sort(hashes, 0, count);
        var unique = 0;
        for (var i = 0; i < count; i++)
        {
            if (i > 0 && hashes[i] == hashes[i - 1]) continue;
            hashes[unique++] = hashes[i];
        }
        if (unique == 0) return Array.Empty<ulong>();
        return unique == hashes.Length ? hashes : hashes[..unique];
    }

    /// <summary>
    /// 在有序数组里二分查找；命中 = 该 doc 有一行与查询整行相等。
    /// </summary>
    public static bool Contains(ulong[] hashes, ulong target)
    {
        var lo = 0;
        var hi = hashes.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var value = hashes[mid];
            if (value == target) return true;
            if (value < target) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    /// <summary>校验数组严格递增（写入侧防御：乱序会让二分查找静默漏命中）。</summary>
    public static bool IsSortedUnique(ReadOnlySpan<ulong> hashes)
    {
        for (var i = 1; i < hashes.Length; i++)
        {
            if (hashes[i] <= hashes[i - 1]) return false;
        }
        return true;
    }
}
