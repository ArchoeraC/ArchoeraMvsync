// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Archoera.Indexer.Tests;

/// <summary>
/// <see cref="LyricsPreprocess"/> 的回归断言（T3.5 去重 / T3.8 V6 护栏）：
/// 行裁剪口径、归一化键（与 <see cref="Analyzer.Normalize"/> 同源）、保序首现、
/// 4096 行 / 128 KiB 截尾、正常文本零触发且输出不变。
///
/// 索引路径的集成断言（metric / warn / 短语位置 / library.db 原文不变）在
/// <see cref="EndToEnd"/> 的 <c>LyricsPreprocessIntegration</c> 中。
/// </summary>
internal static class LyricsPreprocessTests
{
    public static void Run()
    {
        Basics();
        NormalizedDedupe();
        LineGuard();
        ByteGuard();
        NormalPathUntouched();
    }

    /// <summary>trim + 丢空行 + 保序首现去重。</summary>
    private static void Basics()
    {
        var result = LyricsPreprocess.Apply("  第一句 \n\n第二句\r\n第一句\n第三句\n   \n");
        Program.Check("预处理: trim / 丢空 / 去重后保序",
            result.Text == "第一句\n第二句\n第三句", Escape(result.Text));
        Program.Check("预处理: raw=4 / unique=3",
            result.RawLines == 4 && result.UniqueLines == 3,
            $"{result.RawLines}/{result.UniqueLines}");
        Program.Check("预处理: 正常文本不触发护栏", !result.Truncated,
            $"{result.TruncatedByLines}/{result.TruncatedByBytes}");

        var empty = LyricsPreprocess.Apply("");
        Program.Check("预处理: 空层零值",
            empty.Text.Length == 0 && empty.RawLines == 0 && empty.UniqueLines == 0
            && !empty.Truncated,
            $"{empty.RawLines}/{empty.UniqueLines}/{empty.Truncated}");
    }

    /// <summary>
    /// 归一化键：大小写 / 全半角（NFKC）/ 异体 / 繁简 / 音标只差一个变体的行合并；
    /// 语义不同的行不合并。保留的是**首次出现**的原行（不是归一化后的行）。
    /// </summary>
    private static void NormalizedDedupe()
    {
        var result = LyricsPreprocess.Apply(
            "Hello World\nHELLO WORLD\n"
            + "ＡＢＣ\nabc\n"
            + "愛してる\n爱してる\n"
            + "Beyoncé\nBeyonce\n"
            + "晴天\n雨天\n");

        const string expected = "Hello World\nＡＢＣ\n愛してる\nBeyoncé\n晴天\n雨天";
        Program.Check("预处理: 归一化键合并大小写 / 全半角 / 异体 / 音标",
            result.Text == expected, Escape(result.Text));
        Program.Check("预处理: 归一化合并后 raw=10 / unique=6",
            result.RawLines == 10 && result.UniqueLines == 6,
            $"{result.RawLines}/{result.UniqueLines}");
        Program.Check("预处理: 不同行不误合并（晴天 / 雨天都在）",
            result.Text.Contains("晴天", StringComparison.Ordinal)
            && result.Text.Contains("雨天", StringComparison.Ordinal),
            Escape(result.Text));
    }

    /// <summary>4097 个唯一短行：只触发行数护栏，保留前 4096 行（截尾）。</summary>
    private static void LineGuard()
    {
        var text = string.Join('\n', Enumerable.Range(0, 4097).Select(i => "L" + i));
        var result = LyricsPreprocess.Apply(text);

        Program.Check("预处理: 4097 行触发行数护栏",
            result.TruncatedByLines && !result.TruncatedByBytes
            && result.UniqueLines == 4097,
            $"{result.UniqueLines}/{result.TruncatedByLines}/{result.TruncatedByBytes}");
        var lines = result.Text.Split('\n');
        Program.Check("预处理: 行数护栏保留前 4096 行",
            lines.Length == LyricsPreprocess.MaxUniqueLines
            && lines[0] == "L0" && lines[^1] == "L4095",
            $"{lines.Length}/{lines[^1]}");
    }

    /// <summary>100 个唯一长行（>128 KiB）：只触发字节护栏，按 UTF-8 字节截尾且不切断 rune。</summary>
    private static void ByteGuard()
    {
        var longLines = Enumerable.Range(0, 100)
            .Select(i => new string('A', 1490) + i.ToString("D10"))
            .ToArray();
        var text = string.Join('\n', longLines);
        var result = LyricsPreprocess.Apply(text);

        Program.Check("预处理: >128 KiB 触发字节护栏",
            result.TruncatedByBytes && !result.TruncatedByLines
            && result.UniqueLines == 100
            && result.DedupedBytes > LyricsPreprocess.MaxUtf8Bytes,
            $"{result.DedupedBytes}/{result.TruncatedByBytes}/{result.TruncatedByLines}");
        Program.Check("预处理: 字节护栏保留 UTF-8 前缀且不超限",
            result.RetainedBytes <= LyricsPreprocess.MaxUtf8Bytes
            && text.StartsWith(result.Text, StringComparison.Ordinal),
            $"{result.RetainedBytes}/{Encoding.UTF8.GetByteCount(result.Text)}");

        // 多字节 rune：预算落在汉字中间时不得切出半个字符（替换字符即失败）。
        // 行数 < 4096 保证只走字节分支（行数护栏先截行，会掩盖字节路径）。
        var cjkText = string.Join('\n', Enumerable.Range(0, 4000)
            .Select(i => new string('歌', 40) + i.ToString("D4")));
        var cjk = LyricsPreprocess.Apply(cjkText);
        Program.Check("预处理: 字节截断不破坏 UTF-8 / rune",
            cjk.TruncatedByBytes && !cjk.TruncatedByLines
            && Encoding.UTF8.GetByteCount(cjk.Text) <= LyricsPreprocess.MaxUtf8Bytes
            && !cjk.Text.Contains('\uFFFD', StringComparison.Ordinal),
            $"{cjk.RetainedBytes}/{cjk.Text.Length}");
    }

    /// <summary>正常文本：零触发，输出与输入逐字节一致（护栏不参与的正常路径）。</summary>
    private static void NormalPathUntouched()
    {
        const string text = "晴天\n雨天\n阴天\n第一行\n第二行";
        var result = LyricsPreprocess.Apply(text);
        Program.Check("预处理: 正常文本输出不变",
            result.Text == text
            && result.RawLines == 5 && result.UniqueLines == 5
            && result.DedupedBytes == Encoding.UTF8.GetByteCount(text)
            && result.RetainedBytes == result.DedupedBytes
            && !result.Truncated,
            Escape(result.Text));
    }

    private static string Escape(string text) => text.Replace("\n", "\\n");
}
