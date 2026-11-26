// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 元数据内容哈希（S6 脏集判定）：把 <c>tracks</c> 的 <c>title / artists / album</c>
/// 三段**原始文本**压成 64 位指纹，写进 DocStore v6 的 <c>meta_hash</c> 列。
///
/// <para><b>为什么需要</b>：增量脏集原本只比较 <c>file_mtime</c> 与 <c>.lrc</c>
/// 侧车 <c>(mtime,size)</c>。用户直接在 <c>library.db</c> 改标题 / 歌手 / 专辑
/// （不碰音频文件）时，mtime 不变，旧规则会漏更新。构建期把原始元数据哈希写进索引，
/// 阶段一探测再算一遍同样的哈希，就能发现「文件没变、DB 文本变了」。</para>
///
/// <para><b>哈希定义</b>：UTF-8 字节上的 <b>FNV-1a 64</b>，输入为
/// <c>title + "\u0000" + artistsRaw + "\u0000" + album</c>。其中
/// <c>artistsRaw</c> 是 <c>tracks.artists</c> 的原始 JSON 文本（不是解析后的
/// <c>ArtistText</c>），<c>album</c> 同样是原始列文本；NULL 一律按空串。</para>
///
/// <para><b>不做归一</b>：只折叠 NULL / 空串，不调用 <see cref="Analyzer.NormalizeMetadata"/>。
/// 目标是发现**原始文本**的编辑；因此仅空白 / 大小写差异也会被当成脏（保守，多重建一次
/// 不会丢数据）。反过来，哈希与 analyzer 版本无关：分析器升级不会让全部曲目误判为脏，
/// 全量重建仍由 Manifest 的 analyzer 锚单独触发。</para>
///
/// <para><b>唯一实现</b>：全量 / 增量构建（<see cref="IndexBuilder.MakeRecord"/> 的上游）
/// 与阶段一探测（<c>IndexerEngine.ReadTrackProbes</c>）都调用本类，避免两侧漂移。</para>
/// </summary>
internal static class MetaHash
{
    /// <summary>FNV-1a 64 位偏移基数。</summary>
    private const ulong FnvOffsetBasis = 14695981039346656037UL;

    /// <summary>FNV-1a 64 位质数。</summary>
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>三段之间的分隔字节（NUL，不会出现在正常文本里）。</summary>
    private const byte Separator = 0;

    /// <summary>
    /// 计算元数据内容哈希：<c>title \0 artistsRaw \0 album</c> 的 UTF-8 FNV-1a 64。
    /// NULL 与空串等价；输出对所有输入都有定义（空元数据也返回确定值）。
    /// </summary>
    public static ulong Compute(string? title, string? artistsRaw, string? album)
    {
        var hash = FnvOffsetBasis;
        hash = Append(hash, title);
        hash = AppendByte(hash, Separator);
        hash = Append(hash, artistsRaw);
        hash = AppendByte(hash, Separator);
        return Append(hash, album);
    }

    /// <summary>把字符串的 UTF-8 字节逐字节喂给 FNV-1a（空 / NULL 原样返回）。</summary>
    private static ulong Append(ulong hash, string? value)
    {
        if (string.IsNullOrEmpty(value)) return hash;

        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            var length = rune.EncodeToUtf8(buffer);
            for (var i = 0; i < length; i++)
                hash = AppendByte(hash, buffer[i]);
        }
        return hash;
    }

    private static ulong AppendByte(ulong hash, byte value)
    {
        hash ^= value;
        return hash * FnvPrime;
    }
}
