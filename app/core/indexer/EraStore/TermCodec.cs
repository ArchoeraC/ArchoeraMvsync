// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 超长词条的统一规范化（S3，**索引侧 / 查询侧共用**）。
///
/// <para>EraStore 词典键上限是 <see cref="EraFormat.MaxTermBytes"/>（255B）。超长单 token
/// （超长无分隔拉丁词 / URL / 长串）既不能原样写入词典，也不能在查询侧原样查；两侧必须
/// 得到**同一个**不透明词条才能命中。规则：</para>
///
/// <list type="bullet">
///   <item>UTF-8 字节数 ≤ 255 → 原样返回；</item>
///   <item>否则 → <c>0x01 + 小写十六进制 MD5(UTF-8)</c>（1 + 32 = 33B）。</item>
/// </list>
///
/// <para>MD5 只做词典键的空间映射，不是安全哈希；键上 128 位碰撞只会让两个超长词共享一个
/// 不透明词条（实际不可达），不影响格式。结果只依赖输入字节，跨运行 / 跨平台稳定。</para>
///
/// <para>含空白的输入一律原样返回：短语字符串（<c>"我爱 爱你"</c> / 读音 bigram 序列）是
/// 多个词条的拼接，整体哈希没有意义。只有单 token 才规范化。</para>
///
/// <para>本变换**不改变 <see cref="EraFormat.FormatVersion"/>**：规范化结果是普通词典词条，
/// 仍在 255B 限内；旧索引照常可读（旧索引里超长词条本就缺席，查询行为与升级前一致）。</para>
/// </summary>
internal static class TermCodec
{
    /// <summary>规范化词条的前缀字符（U+0001，UTF-8 编码为单字节 <c>0x01</c>）。</summary>
    public const char CanonicalPrefix = '\u0001';

    /// <summary>十六进制 MD5 的长度。</summary>
    private const int HexLength = 32;

    /// <summary>规范化词条的总字节数（0x01 + 32 hex）。</summary>
    public const int CanonicalBytes = 1 + HexLength;

    /// <summary>
    /// 长度 ≤ 该值的字符串必然 ≤ 255B：单个 UTF-16 字符最多贡献 3B（4 字节码点由代理对
    /// 表示，两个字符共 4B），85 × 3 = 255。超过时才计算真实 UTF-8 字节数，避免给普通
    /// 词条逐个编码。
    /// </summary>
    private const int FastPathChars = EraFormat.MaxTermBytes / 3;

    /// <summary>该字符串是否是已规范化的不透明词条（以 <see cref="CanonicalPrefix"/> 开头）。</summary>
    public static bool IsCanonical(string? term)
        => !string.IsNullOrEmpty(term) && term[0] == CanonicalPrefix;

    /// <summary>该单 token 是否需要规范化（超长且不含空白）。</summary>
    public static bool NeedsCanonicalization(string term)
    {
        if (string.IsNullOrEmpty(term) || term.Length <= FastPathChars) return false;
        if (ContainsWhitespace(term)) return false;
        return Encoding.UTF8.GetByteCount(term) > EraFormat.MaxTermBytes;
    }

    /// <summary>
    /// 规范化单个词条：≤255B 原样返回，超长换 <c>0x01 + MD5 hex</c>。含空白原样返回。
    /// 幂等：已规范化的词条只有 33B，再次调用返回自身。
    /// </summary>
    public static string Canonicalize(string term)
    {
        if (!NeedsCanonicalization(term)) return term;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(term));
        return string.Concat("\u0001", Convert.ToHexStringLower(hash));
    }

    private static bool ContainsWhitespace(string term)
    {
        foreach (var ch in term)
        {
            if (char.IsWhiteSpace(ch)) return true;
        }
        return false;
    }
}
