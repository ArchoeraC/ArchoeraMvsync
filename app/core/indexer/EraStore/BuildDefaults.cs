// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 生产构建与基准共用的默认参数，避免两套清单漂移（S2，2026-11-26）。
///
/// <para><b>免位置字段：只允许显式音译层（<c>LyricsRomanization</c>）。</b>
/// 它通常是一整行用户提供的拉丁转写，短语精度由歌词原文承担；免位置后
/// <c>PhraseIter</c> 降级为 AND，<c>Positioned=false</c> 由 <c>explain</c> 标注。</para>
///
/// <para><b><c>PinyinLyrics</c> 必须保留位置。</b> 5k 质量门（2026-11-26，qbench eval）
/// 实测两条派生层都免位置时：ALL P@1 0.973 → 0.838、NDCG@10 0.978 → 0.854；
/// <c>woaini</c> / <c>woai</c> 与歌词行题 P@1 1.000 → 0.000。拼音短语降级为 AND
/// 会把「音节相邻」变成「整首都有」，假阳性压过真实歌词命中。因此本体实验路线不
/// 发布；体积只能靠未来的位打包（O8/PForDelta，改格式 + 重建）继续压缩。
/// <c>PinyinMeta</c> 同理保留位置（元数据短语是精度路径）。</para>
///
/// <para>显式传入 <see cref="BuildOptions.NoPositionFields"/> / <see cref="IncrementalOptions.NoPositionFields"/>
/// 仍可覆盖本默认（测试 / 一次性诊断）。</para>
/// </summary>
internal static class BuildDefaults
{
    private static readonly FieldId[] NoPositionFieldsValue =
    {
        FieldId.LyricsRomanization,
    };

    /// <summary>生产默认免位置字段（只读清单，顺序固定）。</summary>
    public static IReadOnlyList<FieldId> NoPositionFields => NoPositionFieldsValue;

    /// <summary>每次构建拿到一份可变的独立集合，避免调用方修改共享状态。</summary>
    public static HashSet<FieldId> CreateNoPositionFields() => new(NoPositionFieldsValue);
}
