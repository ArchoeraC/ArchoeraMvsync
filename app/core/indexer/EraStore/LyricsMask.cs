// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Lyrics;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 查询期歌词层置信度过滤策略（T4.2 / §5.6.3）。
///
/// <para>只作用于三个**字面歌词列**（<c>lyrics_tok</c> / <c>lyrics_tr_tok</c> /
/// <c>lyrics_rom_tok</c>）；拼音 / 首字母 / 读音列是派生数据，按 §5.6.3 的「读音」行
/// 对所有置信度都参与。低置信层仍写入倒排，只是这里把它们从结果位图里剔除。</para>
/// </summary>
internal enum LyricsConfidencePolicy : byte
{
    /// <summary>不过滤（<c>scope=lyrics</c> / <c>scope=metadata</c> 的普通步骤）。</summary>
    None = 0,

    /// <summary><c>scope=all</c> 的字面 / 容错步骤：只保留中 / 高置信（低置信不污染全局搜索）。</summary>
    MediumOrBetter = 1,

    /// <summary>跨语言步骤：只保留高置信（§5.6.3：中 / 低不参与跨语言）。</summary>
    HighOnly = 2,
}

/// <summary>
/// 单个段的三层歌词置信度位图（T4.2）。由 <see cref="DocStoreReader"/> 的
/// <c>layer_conf</c> 列一次性构建，按段缓存在 <see cref="StoreReader"/> 上（随提交世代失效）。
///
/// <para>每层两张位图：中置信及以上、仅高置信。位图只含**活行**，与倒排 / 短语路径的
/// 墓碑口径一致。</para>
/// </summary>
internal sealed class LyricsMask
{
    private readonly ulong[][] _mediumOrBetter;
    private readonly ulong[][] _highOnly;

    private LyricsMask(ulong[][] mediumOrBetter, ulong[][] highOnly)
    {
        _mediumOrBetter = mediumOrBetter;
        _highOnly = highOnly;
    }

    /// <summary>该字段是否是受置信度门控的歌词列（普通 <c>scope=all</c> 只门控字面三列）。</summary>
    public static bool IsGatedField(FieldId field)
        => field is FieldId.Lyrics or FieldId.LyricsTranslation or FieldId.LyricsRomanization;

    /// <summary>
    /// 字面歌词列 → 层下标（0 原文 / 1 译文 / 2 音译）；非字面歌词列返回 -1。
    /// </summary>
    public static int LayerOf(FieldId field) => field switch
    {
        FieldId.Lyrics => LrcReader.LayerOriginal,
        FieldId.LyricsTranslation => LrcReader.LayerTranslation,
        FieldId.LyricsRomanization => LrcReader.LayerRomaji,
        _ => -1,
    };

    /// <summary>
    /// 跨语言步的层下标：除字面三列外，歌词拼音 / 首字母也归到**原文层**
    /// （它们是从原文派生的；不门控会让中 / 低置信层原样漏进跨语言结果）。
    ///
    /// <para>普通 <c>scope=all</c> 不门控拼音 / 首字母列——§5.6.3 的「读音（拼音）」
    /// 行要求各置信度都可用拼音查询，且拼音首字母是既有产品能力（N21 之后仍保留）。</para>
    /// </summary>
    public static int CrossLingualLayerOf(FieldId field) => field switch
    {
        FieldId.Lyrics => LrcReader.LayerOriginal,
        FieldId.PinyinLyrics => LrcReader.LayerOriginal,
        FieldId.InitialsLyrics => LrcReader.LayerOriginal,
        FieldId.LyricsTranslation => LrcReader.LayerTranslation,
        FieldId.LyricsRomanization => LrcReader.LayerRomaji,
        _ => -1,
    };

    /// <summary>按策略取某层的位图；<see cref="LyricsConfidencePolicy.None"/> 返回 <c>null</c>。</summary>
    public ulong[]? For(FieldId field, LyricsConfidencePolicy policy)
    {
        if (policy == LyricsConfidencePolicy.None) return null;
        var layer = policy == LyricsConfidencePolicy.HighOnly
            ? CrossLingualLayerOf(field)
            : LayerOf(field);
        if (layer < 0) return null;
        return policy == LyricsConfidencePolicy.HighOnly ? _highOnly[layer] : _mediumOrBetter[layer];
    }

    /// <summary>扫描一个段 DocStore 的 <c>layer_conf</c>，构建两层三列位图。</summary>
    public static LyricsMask Build(DocStoreReader docs)
    {
        var words = Math.Max(1, (docs.Count + 63) >> 6);
        var medium = new ulong[3][];
        var high = new ulong[3][];
        for (var layer = 0; layer < 3; layer++)
        {
            medium[layer] = new ulong[words];
            high[layer] = new ulong[words];
        }

        for (var doc = 0; doc < docs.Count; doc++)
        {
            var packed = docs.LayerConf(doc);
            var word = doc >> 6;
            var bit = 1UL << (doc & 63);
            for (var layer = 0; layer < 3; layer++)
            {
                var level = (packed >> (layer * 2)) & 0x03;
                if (level >= (byte)LyricConfidence.Medium) medium[layer][word] |= bit;
                if (level == (byte)LyricConfidence.High) high[layer][word] |= bit;
            }
        }
        return new LyricsMask(medium, high);
    }
}
