// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 墓碑段（设计文档 §3.8.2.2 / §3.8.2.3）：记录同段 DocStore 中已被增量刷新标记删除的 doc。
///
/// 段体布局（全部小端）：
/// <code>
/// u32 dead_count | dead doc_id 的 Δvarint 序列（严格递增，首条相对 0）
/// </code>
/// 段引用里的 <see cref="SectionRef.Docs"/> 同时记录 <c>dead_count</c>，
/// 让 <see cref="Merger.ShouldMerge"/> 与 stats 无需读取段体即可算出死行比例。
/// 删除后的 doc 仍占用 DocStore 槽位，合并时丢弃；旧段字节成为垃圾。
/// </summary>
internal static class Tombstones
{
    /// <summary>编码死 doc 列表：排序去重后写成 Δvarint 序列。</summary>
    public static byte[] Write(IReadOnlyList<int> deadDocIds)
    {
        ArgumentNullException.ThrowIfNull(deadDocIds);

        var sorted = new List<int>(deadDocIds.Count);
        foreach (var id in deadDocIds)
        {
            if (id < 0) throw new ArgumentOutOfRangeException(nameof(deadDocIds), "doc_id 必须非负");
            sorted.Add(id);
        }
        sorted.Sort();

        var unique = new List<int>(sorted.Count);
        foreach (var id in sorted)
            if (unique.Count == 0 || unique[^1] != id) unique.Add(id);

        var w = new ByteWriter(4 + unique.Count * 2);
        w.WriteUInt32((uint)unique.Count);
        var prev = 0;
        foreach (var id in unique)
        {
            w.WriteVarint((ulong)(id - prev));
            prev = id;
        }
        return w.ToArray();
    }

    /// <summary>
    /// 解码墓碑段。<paramref name="liveDocCount"/> 是同段 DocStore 的物理文档数；
    /// 任何越界 / 非递增 / 尾随字节都抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static List<int> Read(ReadOnlyMemory<byte> body, int liveDocCount)
    {
        if (liveDocCount < 0) throw new ArgumentOutOfRangeException(nameof(liveDocCount));
        var span = body.Span;
        if (span.Length < 4) throw new InvalidDataException("Tombs 段过短");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (count > (uint)liveDocCount)
            throw new InvalidDataException($"Tombs 死行数 {count} 超过 DocStore 文档数 {liveDocCount}");

        var reader = new ByteReader(span, 4);
        var result = new List<int>((int)count);
        var prev = 0;
        for (var i = 0; i < (int)count; i++)
        {
            var delta = reader.ReadVarint();
            if (i > 0 && delta == 0)
                throw new InvalidDataException("Tombs doc_id 未严格递增");
            var id = (ulong)prev + delta;
            if (id > int.MaxValue || id >= (ulong)liveDocCount)
                throw new InvalidDataException($"Tombs doc_id {id} 越界");
            prev = (int)id;
            result.Add(prev);
        }
        if (reader.Remaining != 0)
            throw new InvalidDataException("Tombs 段存在尾随字节");
        return result;
    }
}
