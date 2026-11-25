// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>
/// 模糊层（T1-8）的词典工具：只做两件事——在前缀有序词表里定位区间、判定编辑距离。
///
/// <para><b>词典来源</b>：索引自身的 EraStore 词典，不额外维护派生词表，
/// 因此不递增 <see cref="Analyzer"/> 版本、也不改变索引体积。查询侧把词表按序缓存
/// （见 <c>SearchSession.LoadEraVocabulary</c>），变体只保留「索引里确实存在」的候选。</para>
///
/// <para><b>距离判定</b>：Myers 位并行算法（Levenshtein ≤ 1），另加一次相邻换位检查以覆盖
/// Damerau 语义（换位在生成侧本就是单独规则，否则 <c>hlelo → hello</c> 会被算成距离 2）。</para>
/// </summary>
internal static class FuzzyVocabulary
{
    /// <summary>在字典序有序词表中取「所有以 <paramref name="prefix"/> 开头」的半开区间。</summary>
    public static (int Start, int End) PrefixRange(IReadOnlyList<string> sorted, string prefix)
    {
        var start = LowerBound(sorted, prefix);
        if (start >= sorted.Count) return (start, start);

        var low = start;
        var high = sorted.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (sorted[mid].StartsWith(prefix, StringComparison.Ordinal)) low = mid + 1;
            else high = mid;
        }
        return (start, low);
    }

    /// <summary>Damerau（OSA）距离 ≤ 1：Myers 位并行 Levenshtein + 单次相邻换位。</summary>
    public static bool WithinOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        if (MyersWithinOne(left, right)) return true;
        return IsSingleAdjacentSwap(left, right);
    }

    /// <summary>Damerau（OSA）距离 ≤ 2（typo2 / recall@1 专用）。</summary>
    public static bool WithinTwo(string left, string right)
        => Math.Abs(left.Length - right.Length) <= 2 && BoundedDamerau(left, right, 2) <= 2;

    /// <summary>
    /// 词表里与 <paramref name="word"/> 的 Damerau 距离**恰为 2**、且首字符相同的词条。
    ///
    /// <para>首字符相同是 N23 的硬约束（首字符错等于两个错误，不纠）；距离恰为 2 是为了
    /// 不与 typo1 的候选重复。词表按字典序时用首字符前缀区间收敛扫描范围。</para>
    /// </summary>
    public static List<string> DistanceTwoCandidates(
        IReadOnlyList<string> sorted, string word, int max)
    {
        var output = new List<string>();
        if (sorted.Count == 0 || word.Length == 0 || max <= 0) return output;

        var (start, end) = PrefixRange(sorted, word[..1]);
        var scanned = 0;
        for (var i = start; i < end && output.Count < max; i++)
        {
            // 防御：异常大的首字母桶（如拼音单字母）只扫描有限条数。
            if (++scanned > MaxScanPerWord) break;

            var term = sorted[i];
            if (Math.Abs(term.Length - word.Length) > 2) continue;
            if (BoundedDamerau(word, term, 2) != 2) continue;
            output.Add(term);
        }
        return output;
    }

    /// <summary>单词扫描上限，防止首字母桶过大时拖慢 recall@1 的 P95。</summary>
    private const int MaxScanPerWord = 20_000;

    private static int LowerBound(IReadOnlyList<string> sorted, string key)
    {
        var low = 0;
        var high = sorted.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (string.CompareOrdinal(sorted[mid], key) < 0) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>
    /// Myers 位并行编辑距离，判定是否 ≤ 1。模式长度 ≤ 64 走位运算；
    /// 更长的查询（正常输入不会出现）退回有界 DP，保证正确性。
    /// </summary>
    private static bool MyersWithinOne(string pattern, string text)
    {
        if (pattern.Length == 0) return text.Length <= 1;
        if (pattern.Length > 64) return BoundedLevenshteinWithinOne(pattern, text);

        var masks = new Dictionary<char, ulong>(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            var bit = 1UL << i;
            masks[pattern[i]] = masks.TryGetValue(pattern[i], out var existing) ? existing | bit : bit;
        }

        var pv = ~0UL;
        var mv = 0UL;
        var score = pattern.Length;
        var top = 1UL << (pattern.Length - 1);

        foreach (var ch in text)
        {
            var eq = masks.TryGetValue(ch, out var mask) ? mask : 0UL;
            var xv = eq | mv;
            var xh = (((eq & pv) + pv) ^ pv) | eq;
            var ph = mv | ~(xh | pv);
            var mh = pv & xh;
            if ((ph & top) != 0) score++;
            if ((mh & top) != 0) score--;
            ph = (ph << 1) | 1UL;
            mh <<= 1;
            pv = mh | ~(xv | ph);
            mv = ph & xv;
        }

        return score <= 1;
    }

    private static bool BoundedLevenshteinWithinOne(string pattern, string text)
    {
        var previous = new int[text.Length + 1];
        var current = new int[text.Length + 1];
        for (var j = 0; j <= text.Length; j++) previous[j] = j;

        for (var i = 1; i <= pattern.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= text.Length; j++)
            {
                var cost = pattern[i - 1] == text[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
                current[j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > 1) return false;
            (previous, current) = (current, previous);
        }
        return previous[text.Length] <= 1;
    }

    private static bool IsSingleAdjacentSwap(string left, string right)
    {
        if (left.Length != right.Length || left.Length < 2) return false;

        var i = 0;
        while (i < left.Length && left[i] == right[i]) i++;
        if (i + 1 >= left.Length) return false;
        if (left[i] != right[i + 1] || left[i + 1] != right[i]) return false;

        for (var j = i + 2; j < left.Length; j++)
        {
            if (left[j] != right[j]) return false;
        }
        return true;
    }

    /// <summary>
    /// 有界 OSA 距离：返回 min(实际距离, <paramref name="max"/> + 1)。
    /// 行最小值超过上限即提前退出；覆盖删除 / 插入 / 替换 / 相邻换位。
    /// </summary>
    private static int BoundedDamerau(string pattern, string text, int max)
    {
        if (pattern.Length == 0) return Math.Min(text.Length, max + 1);
        if (text.Length == 0) return Math.Min(pattern.Length, max + 1);

        var prev2 = new int[text.Length + 1];
        var prev = new int[text.Length + 1];
        var current = new int[text.Length + 1];
        for (var j = 0; j <= text.Length; j++) prev[j] = j;

        for (var i = 1; i <= pattern.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= text.Length; j++)
            {
                var cost = pattern[i - 1] == text[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(prev[j] + 1, current[j - 1] + 1),
                    prev[j - 1] + cost);
                if (i > 1 && j > 1
                    && pattern[i - 1] == text[j - 2]
                    && pattern[i - 2] == text[j - 1])
                {
                    value = Math.Min(value, prev2[j - 2] + 1);
                }
                current[j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > max) return max + 1;
            (prev2, prev, current) = (prev, current, prev2);
        }
        return Math.Min(prev[text.Length], max + 1);
    }
}
