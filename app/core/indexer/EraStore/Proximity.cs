// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 多 term **字面步**的邻近度（SearchSpec v1 §4.2 第 5 键 / T3.11 / Q2）。
///
/// <para><b>为什么在引擎里算</b>：邻近度是「同一字段内覆盖每个 term 的最小窗口」，
/// 只有倒排位置块能回答。计划编译器 / 排序器不持有倒排，按步骤形状只能给出
/// 「恰好一个短语」这一特例（<see cref="SearchRanker.ProximityOf"/>）；
/// 散落 AND 的真实跨度必须在这里读位置。</para>
///
/// <para><b>成本护栏</b>：只对**有字段集映射**的族（元数据 / 歌词 / 混合 / 读音 / 模糊）
/// 且 term 数 ≥2 的步骤、且单步候选 ≤ <see cref="SearchExactness.Cap"/> 时计算；游标按
/// <c>(segment, field, term)</c> 在**一次调用内**缓存并随候选 doc 升序推进，
/// 不重开、不重扫（位置缓冲整次调用复用）。字段 / 词条无位置、单字段位置过多、
/// 词元重复（去重后 &lt;2 个词）或计算不出完整窗口时，该候选保持
/// <see cref="SearchRanker.ProximityUnknown"/>（wire 上仍是 -1）。
/// 拼音 / 首字母 / 跨语言族不走这里：<see cref="Step.Terms"/> 不是这些列里的字面 token，
/// 位置语义不同源，调用方按 spec 直接记算不出。</para>
/// </summary>
internal static class EraProximity
{
    /// <summary>参与邻近度计算的元数据字面字段（title / artist / album / filename）。</summary>
    private static readonly FieldId[] MetaFields =
    {
        FieldId.Title,
        FieldId.Artist,
        FieldId.Album,
        FieldId.Filename,
    };

    /// <summary>
    /// 歌词字面层（原文 / 译文 / 音译）。音译在生产默认免位置
    /// （<see cref="BuildDefaults.NoPositionFields"/>），取不到位置时自然跳过该列。
    /// </summary>
    private static readonly FieldId[] LyricFields =
    {
        FieldId.Lyrics,
        FieldId.LyricsTranslation,
        FieldId.LyricsRomanization,
    };

    /// <summary>混合 scope：元数据 + 歌词两套字面列，逐列取最小窗口后再跨列取最小。</summary>
    private static readonly FieldId[] MixedFields = MetaFields.Concat(LyricFields).ToArray();

    /// <summary>读音趟的原文列（元数据读音 bigram）。</summary>
    private static readonly FieldId[] ReadingFields =
    {
        FieldId.ReadingMeta,
    };

    /// <summary>单 (doc, field, term) 的位置数上限：超过视为噪声（高频词），不参与计算。</summary>
    private const int MaxPositionsPerTerm = 1_024;

    /// <summary>
    /// 步骤族 → 参与邻近度的字面字段集；<c>null</c> 表示该族的 <see cref="Step.Terms"/>
    /// 不是字面 token（拼音 / 首字母 / 跨语言）或没有位置语义，邻近度算不出。
    /// </summary>
    public static IReadOnlyList<FieldId>? FieldsFor(FieldFamily family) => family switch
    {
        FieldFamily.Meta => MetaFields,
        FieldFamily.Lyrics => LyricFields,
        FieldFamily.Mixed => MixedFields,
        FieldFamily.Reading => ReadingFields,
        // 模糊趟的表达式由词表变体 OR 组成，但 step.Terms 仍是用户键入的字面词元：
        // 变体命中只影响召回，邻近度仍按原文出现位置计算。
        FieldFamily.Fuzzy => MixedFields,
        _ => null,
    };

    /// <summary>该步骤族是否支持真实邻近度（<see cref="FieldsFor"/> 有映射）。</summary>
    public static bool Supports(FieldFamily family) => FieldsFor(family) is not null;

    /// <summary>
    /// 为 <paramref name="candidates"/> 就地写入最小跨度；不满足门禁时原样返回（保持 sentinel）。
    ///
    /// <para><paramref name="terms"/> 是步骤的字面词元（<see cref="Step.Terms"/> 去掉了
    /// analyzer 追加的首尾 unigram）；只有全部 term 都在**同一个**字段里出现时才算得出窗口，
    /// 先逐字段取最小值，再在字段集内取最小值。单 term（term 数 ≤1）由调用方直接记 0，
    /// 不支持的族（<see cref="FieldsFor"/> 为 <c>null</c>）不进本函数。</para>
    /// </summary>
    public static void Fill(
        StoreReader reader, IReadOnlyList<string> terms, List<EraCandidate> candidates, int cap,
        FieldFamily family)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(candidates);
        if (terms.Count <= 1 || candidates.Count == 0 || candidates.Count > cap) return;
        var fields = FieldsFor(family);
        if (fields is null) return;

        // 重复词元只保留一份：同一词条的位置游标只能前进一次（否则第二份会复读同一位置，
        // 把「两次出现」误算成跨度 0）。去重后 <2 个词时算不出跨词窗口，保持 sentinel。
        var distinct = new List<string>(terms.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (seen.Add(term)) distinct.Add(term);
        }
        if (distinct.Count <= 1) return;

        var segments = reader.OpenPerSegment();
        var bySegment = new Dictionary<ushort, SegmentReaders>(segments.Count);
        foreach (var segment in segments) bySegment[segment.SegmentId] = segment;

        // PostingsCursor 只能前进：按 (segment, doc) 升序访问候选，游标才不会退。
        var order = new int[candidates.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var bySeg = candidates[a].SegmentId.CompareTo(candidates[b].SegmentId);
            return bySeg != 0 ? bySeg : candidates[a].DocId.CompareTo(candidates[b].DocId);
        });

        var cursors = new Dictionary<CursorKey, PostingsCursor?>();
        var positions = new int[distinct.Count][];
        var lengths = new int[distinct.Count];
        Span<int> indexes = distinct.Count <= 16
            ? stackalloc int[distinct.Count]
            : new int[distinct.Count];

        foreach (var index in order)
        {
            var candidate = candidates[index];
            if (!bySegment.TryGetValue(candidate.SegmentId, out var segment)) continue;
            if ((uint)candidate.DocId >= (uint)segment.Docs.Count) continue;

            var best = SearchRanker.ProximityUnknown;
            foreach (var field in fields)
            {
                if (!TryPositions(
                        segment, field, distinct, candidate.DocId, cursors, positions, lengths)) continue;
                var span = MinWindow(positions, lengths, indexes);
                if (span < best) best = span;
                if (best == 0) break;
            }

            if (best >= SearchRanker.ProximityUnknown) continue;
            var updated = candidate;
            updated.Proximity = best;
            candidates[index] = updated;
        }
    }

    /// <summary>
    /// 把该字段里全部 term 的位置取到复用缓冲 <paramref name="positions"/>；任一 term 缺位置
    /// （词条不存在 / 无位置块 / 位置过多）返回 false。
    /// </summary>
    private static bool TryPositions(
        SegmentReaders segment, FieldId field, IReadOnlyList<string> terms, int docId,
        Dictionary<CursorKey, PostingsCursor?> cursors, int[][] positions, int[] lengths)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            var cursor = Cursor(segment, field, terms[i], cursors);
            if (cursor is null) return false;
            if (!cursor.Advance(docId) || cursor.CurrentDoc != docId) return false;
            var current = cursor.CurrentPositions;
            if (current.IsEmpty || current.Length > MaxPositionsPerTerm) return false;

            var buffer = positions[i];
            if (buffer is null || buffer.Length < current.Length)
            {
                buffer = new int[Math.Max(current.Length, 8)];
                positions[i] = buffer;
            }
            current.CopyTo(buffer);
            lengths[i] = current.Length;
        }
        return true;
    }

    /// <summary>按 (segment, field, term) 缓存的游标；首次使用时打开并复用。</summary>
    private static PostingsCursor? Cursor(
        SegmentReaders segment, FieldId field, string term,
        Dictionary<CursorKey, PostingsCursor?> cursors)
    {
        var key = new CursorKey(segment.SegmentId, field, term);
        if (cursors.TryGetValue(key, out var cached)) return cached;

        PostingsCursor? cursor = null;
        if (segment.Dict.TryFind(field, term, out var info)
            && info.HasPositions && info.Df > 0)
        {
            cursor = segment.Postings.Open(info.PostOffset, info.Df);
        }
        cursors[key] = cursor;
        return cursor;
    }

    /// <summary>
    /// 每组有序位置里各取一个、覆盖全部组的最小跨度（pos 差；相邻 = 1）。
    /// 经典 k 指针扫：每次前进当前最小位置所在组。
    /// </summary>
    private static int MinWindow(int[][] positions, int[] lengths, Span<int> indexes)
    {
        var count = positions.Length;
        indexes.Clear();
        var best = SearchRanker.ProximityUnknown;
        while (true)
        {
            var minPosition = int.MaxValue;
            var maxPosition = int.MinValue;
            var minAt = -1;
            for (var i = 0; i < count; i++)
            {
                var position = positions[i][indexes[i]];
                if (position < minPosition)
                {
                    minPosition = position;
                    minAt = i;
                }
                if (position > maxPosition) maxPosition = position;
            }

            var span = maxPosition - minPosition;
            if (span < best) best = span;
            if (best == 0) return 0;

            indexes[minAt]++;
            if (indexes[minAt] >= lengths[minAt]) return best;
        }
    }

    private readonly record struct CursorKey(ushort Segment, FieldId Field, string Term);
}
