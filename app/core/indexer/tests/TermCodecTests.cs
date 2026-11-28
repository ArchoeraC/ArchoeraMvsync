// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S3：超长词条规范化（<see cref="TermCodec"/>）的边界 / 确定性 / 查询侧接线。
///
/// <para>索引侧与查询侧必须得到同一个不透明词条，超长单词才既可写入词典又可被检索；
/// 这里锁住 255 / 256 字节边界、多字节 CJK 的字节数口径、含空白短语不整体哈希，
/// 以及查询表达式解析器实际看到的词条。</para>
/// </summary>
internal static class TermCodecTests
{
    public static void Run()
    {
        Boundary();
        Deterministic();
        QueryExpression();
    }

    /// <summary>255B 原样、256B / 多字节 CJK >255B 规范化；含空白不哈希；幂等。</summary>
    private static void Boundary()
    {
        var ascii255 = new string('a', EraFormat.MaxTermBytes);
        Program.Check("TermCodec: 255B 原样返回",
            TermCodec.Canonicalize(ascii255) == ascii255 && !TermCodec.IsCanonical(ascii255),
            TermCodec.Canonicalize(ascii255).Length.ToString());

        var ascii256 = new string('a', EraFormat.MaxTermBytes + 1);
        var canonical256 = TermCodec.Canonicalize(ascii256);
        Program.Check("TermCodec: 256B → 0x01 + 32 hex（33B）",
            canonical256.Length == TermCodec.CanonicalBytes
            && canonical256[0] == TermCodec.CanonicalPrefix
            && canonical256[1..].All(char.IsAsciiHexDigitLower),
            canonical256.Length.ToString());

        // 多字节口径按 UTF-8 字节：85 个汉字 = 255B（原样），86 个 = 258B（规范化）。
        var cjk255 = new string('歌', 85);
        var cjk258 = new string('歌', 86);
        Program.Check("TermCodec: 多字节 CJK 恰好 255B 原样",
            TermCodec.Canonicalize(cjk255) == cjk255, "255B 被改写");
        Program.Check("TermCodec: 多字节 CJK >255B 规范化",
            TermCodec.IsCanonical(TermCodec.Canonicalize(cjk258)), "258B 未规范化");

        // 含空白的是多个 token 的拼接（短语 / 读音 bigram 序列），整体哈希没有意义。
        var phrase = string.Join(' ', Enumerable.Repeat(new string('w', 10), 30));
        Program.Check("TermCodec: 含空白短语不做整体哈希",
            TermCodec.Canonicalize(phrase) == phrase, "短语被改写");

        var canonical = TermCodec.Canonicalize(ascii256);
        Program.Check("TermCodec: 已规范化词条幂等",
            TermCodec.Canonicalize(canonical) == canonical && TermCodec.IsCanonical(canonical),
            "幂等失败");
    }

    /// <summary>
    /// 确定性：同一输入跨调用 / 跨平台得到同一串（硬编码 MD5 锚点，不依赖本机实现），
    /// 不同输入得到不同词条。
    /// </summary>
    private static void Deterministic()
    {
        var z256 = new string('z', 256);
        var expected = "\u0001" + "422e2b4e027b430225b3cff67247be64";
        Program.Check("TermCodec: 256B 'z' 锚点 == 0x01 + MD5 hex",
            TermCodec.Canonicalize(z256) == expected, TermCodec.Canonicalize(z256));

        var again = TermCodec.Canonicalize(z256);
        Program.Check("TermCodec: 重复调用稳定", again == expected, again);

        var y256 = new string('y', 256);
        Program.Check("TermCodec: 不同输入 → 不同词条",
            TermCodec.Canonicalize(y256) != expected, TermCodec.Canonicalize(y256));
    }

    /// <summary>
    /// 查询侧：&gt;255B 的查询词元编译成规范化引号词条，表达式解析器看到的是哈希词条，
    /// 原始 300B 词条不会出现在任何 FTS 词位；词典层仍拒绝原始超长键。
    /// </summary>
    private static void QueryExpression()
    {
        var longWord = new string('z', 300);
        var canonical = "\u0001" + "62a457719101124d52a9c4fe5211f52a";

        var tokens = Analyzer.AnalyzeQuery(longWord);
        var lexical = SearchQuery.CompileLexical(tokens);
        Program.Check("TermCodec: 查询词法支 == 规范化引号词条",
            lexical == "\"" + canonical + "\"", lexical);

        // 表达式解析器必须看到规范化形（而不是原始 300B 串），且是精确词条（无前缀）。
        var node = FtsExpression.Parse(lexical!);
        Program.Check("TermCodec: 表达式解析器看到规范化词条",
            node is FtsTerm { Token: var term, Prefix: false } && term == canonical,
            lexical);

        // 主趟含拼音 / 首字母支；无论哪一支，原始 300B 词条都不得出现。
        var full = SearchQuery.CompileMatch(tokens, metadata: false);
        Program.Check("TermCodec: 主趟不包含原始 300B 词条",
            full is not null && !full.Contains(longWord, StringComparison.Ordinal),
            full);
        Program.Check("TermCodec: 主趟包含规范化词条",
            full is not null && full.Contains(SearchQuery.Quote(canonical), StringComparison.Ordinal),
            full);

        // 词典层：规范化词条可写入 / 往返；原始超长键仍被拒绝（不会把 300B 写进段）。
        var builder = new TermDictBuilder();
        builder.Add(FieldId.Lyrics, canonical, 1, 0);
        var (dict, index) = builder.Build();
        var reader = new TermDictReader(dict, index);
        Program.Check("TermCodec: 规范化词条词典往返",
            reader.TryFind(FieldId.Lyrics, canonical, out var info) && info.Df == 1,
            "往返失败");

        var rejected = false;
        try
        {
            builder.Add(FieldId.Lyrics, longWord, 1, 0);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Program.Check("TermCodec: 原始 300B 词条仍被词典拒绝", rejected, "未拒绝");
    }
}
