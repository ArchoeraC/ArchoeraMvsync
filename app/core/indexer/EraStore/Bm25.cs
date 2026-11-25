// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore;

/// <summary>单条命中详情：某字段上的词频（bm25 用）。</summary>
internal sealed class EraHit
{
    public FieldId Field { get; init; }
    public int Tf { get; init; }
}

/// <summary>一个查询短语（FTS5 的一个 phrase）在各字段上的命中；<see cref="Df"/> 为该作用域内的文档频率。</summary>
internal sealed class EraPhraseHit
{
    public int Df { get; init; }
    public List<EraHit> Hits { get; } = new();
}

/// <summary>
/// EraStore 侧 bm25 计算器（S3 / T3.0）。
///
/// <para><b>公式经 SQLite 3.54.1 实测标定</b>，与 FTS5 <c>bm25()</c> 严格一致：</para>
/// <code>
/// idf(p)      = ln((N - df(p) + 0.5) / (df(p) + 0.5))，小于 1e-6 时取 1e-6
/// tf(p)       = Σ 列权重[field] × 该短语在 field 内的词频        // 权重折进 tf，跨列求和
/// L           = 该文档在**全部 12 列**上的 token 总数（不是查询列的子集）
/// avgL        = 全库 12 列 token 总数 / 文档数
/// 贡献        = idf(p) × tf(p) × (k1 + 1) / (tf(p) + k1 × (1 - b + b × L / avgL))
/// score       = -Σ 贡献                                          // FTS5 bm25() 返回负值
/// </code>
///
/// <para>标定要点（3.54.1 两列受控语料）：<c>L</c> 与 <c>avgL</c> 是**整行**口径——同一短语
/// 命中两列时 tf 跨列相加、分母只出现一次（实测 doc 命中 a/b 各 1 次、两列等长时得分为
/// 单列 tf=2 的 1.375 倍）；列权重乘进 tf；列作用域只影响 df 与命中的 tf，不影响 L。</para>
///
/// <para><c>df</c> 是短语作用域内命中的**文档数**（同一文档命中多列只计一次）。</para>
///
/// <para>k1 = 1.2、b = 0.75 与 FTS5 默认一致；12 字段权重按 <see cref="FieldId"/> 的 1..12 排列
/// （与 <c>FtsStore.Bm25Weights</c> 的 FTS 列顺序**不同**，映射见注释）。</para>
/// </summary>
internal static class EraBm25
{
    /// <summary>
    /// 12 字段权重，下标 = FieldId - 1。
    ///
    /// <para>FtsStore 的权重表按 FTS 列声明顺序（title, artist, album, lyrics, lyrics_tr,
    /// lyrics_rom, pinyin_meta, pinyin_lyrics, reading_meta, filename, initials_meta,
    /// initials_lyrics）书写；这里按 <see cref="FieldId"/> 重新排列。</para>
    /// </summary>
    public static readonly double[] Weights =
    {
        4.0, //  1 Title
        3.0, //  2 Artist
        2.0, //  3 Album
        1.0, //  4 Filename
        0.6, //  5 Lyrics
        1.0, //  6 LyricsTranslation
        0.5, //  7 LyricsRomanization
        2.0, //  8 PinyinMeta
        0.5, //  9 PinyinLyrics
        2.0, // 10 InitialsMeta
        0.5, // 11 InitialsLyrics
        2.0, // 12 ReadingMeta
    };

    /// <summary>bm25 词频饱和参数（与 FTS5 默认一致）。</summary>
    public const double K1 = 1.2;

    /// <summary>bm25 长度归一参数（与 FTS5 默认一致）。</summary>
    public const double B = 0.75;

    /// <summary>idf 下限：FTS5 对覆盖过半文档的词给出 ≈1e-6 的极小正 idf。</summary>
    private const double MinIdf = 1e-6;

    /// <summary>文档频率 → idf。</summary>
    public static double Idf(int df, uint n)
    {
        if (n == 0 || df <= 0) return 0;
        var idf = Math.Log((n - df + 0.5) / (df + 0.5));
        return idf < MinIdf ? MinIdf : idf;
    }

    /// <summary>
    /// 计算单个文档的 bm25 分数（负值，越小越相关）。
    /// </summary>
    /// <param name="phrases">该文档命中的短语集合（每项含作用域 df 与各字段 tf）</param>
    /// <param name="rowLen">该文档 12 列 token 总数</param>
    /// <param name="avgRowLen">全库平均每文档 12 列 token 总数</param>
    /// <param name="n">全库文档数</param>
    public static double Score(
        IReadOnlyList<EraPhraseHit> phrases,
        int rowLen,
        double avgRowLen,
        uint n)
    {
        if (avgRowLen <= 0) return 0;

        double total = 0;
        foreach (var phrase in phrases)
        {
            if (phrase.Df <= 0 || phrase.Hits.Count == 0) continue;
            var idf = Idf(phrase.Df, n);

            double tf = 0;
            foreach (var hit in phrase.Hits)
            {
                var fi = (int)hit.Field - 1;
                if (fi < 0 || fi >= 12 || hit.Tf <= 0) continue;
                tf += hit.Tf * Weights[fi];
            }
            if (tf <= 0) continue;

            total += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * rowLen / avgRowLen));
        }
        return -total;
    }
}
