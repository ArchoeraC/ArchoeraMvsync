// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Lyrics;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 健康页的歌词层置信度分布（T4.2 / §5.6.6）：按**曲目**计数，取该曲三层里的最高置信度
/// （任一层高即计高）；三层都为空计 <see cref="None"/>。只统计活行（墓碑跳过）。
/// </summary>
internal sealed class LayerConfidenceCounts
{
    public int High { get; private set; }
    public int Medium { get; private set; }
    public int Low { get; private set; }
    public int None { get; private set; }

    public int Total => High + Medium + Low + None;

    public void Add(LyricConfidence highest)
    {
        switch (highest)
        {
            case LyricConfidence.High: High++; break;
            case LyricConfidence.Medium: Medium++; break;
            case LyricConfidence.Low: Low++; break;
            default: None++; break;
        }
    }
}

/// <summary>只读统计一个 EraStore 文件的层置信度分布（健康检查调用，O(文档数)）。</summary>
internal static class LayerConfidenceStats
{
    /// <summary>打开 <paramref name="indexPath"/> 统计；打开 / 读取失败由调用方按无数据处理。</summary>
    public static LayerConfidenceCounts Collect(string indexPath)
    {
        var counts = new LayerConfidenceCounts();
        using var reader = new StoreReader(indexPath);
        foreach (var segment in reader.OpenPerSegment())
        {
            var docs = segment.Docs;
            for (var doc = 0; doc < docs.Count; doc++)
            {
                if (!segment.IsLive(doc)) continue;
                var packed = docs.LayerConf(doc);
                var highest = LyricConfidence.None;
                for (var layer = 0; layer < 3; layer++)
                {
                    var value = (LyricConfidence)((packed >> (layer * 2)) & 0x03);
                    if (value > highest) highest = value;
                }
                counts.Add(highest);
            }
        }
        return counts;
    }
}
