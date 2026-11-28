// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace Archoera.Indexer;

/// <summary>
/// 策展跨语言词表（<c>data/lexicon_cross.tsv</c>，T4.1 / RP §5.5.4）的加载与查询。
///
/// <para><b>准入规则</b>（加载期强制执行，违反的行被拒绝并记 warn）：列数固定 6；
/// <c>weight ∈ [0.50, 1.00]</c>；<c>source</c> / <c>license</c> 非空；单字 CJK 源禁止；
/// 每源最多 <see cref="MaxTargetsPerSource"/> 个目标（按 weight 降序保留）；不允许控制字符。</para>
///
/// <para><b>匹配</b>：查询归一形与 <c>source</c> 归一形**完全相等**才展开（专名不做到前缀，
/// 避免 <c>li</c> → 李荣浩 / Li Ronghao 这类噪声）。目标串在加载期经
/// <see cref="Analyzer.Analyze"/> 切成索引同构的 token 短语（CJK 取 bigram，丢弃
/// 分析器补的首尾单字），以便跨语言支的引号短语能命中倒排位置。</para>
///
/// <para>缺失 / 损坏时由 <see cref="DataPack"/> 统一降级（<c>cross_lingual</c> 能力关闭），
/// 本类只返回空表，不抛异常。</para>
/// </summary>
internal static class LexiconCross
{
    /// <summary>数据包文件名（与 <see cref="DataPack.LexiconFile"/> 同值）。</summary>
    public const string FileName = DataPack.LexiconFile;

    /// <summary>每个 source 最多保留的目标数（§5.5.4）。</summary>
    public const int MaxTargetsPerSource = 4;

    /// <summary>允许的权重区间（§5.5.4）。</summary>
    public const double MinWeight = 0.50;

    public const double MaxWeight = 1.00;

    /// <summary>单行字节上限（与数据包校验口径一致）。</summary>
    public const int MaxLineBytes = 4096;

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> Table =
        new(LoadFromDataPack);

    /// <summary>测试接缝：把词表替换为给定行（<c>null</c> 恢复正常加载）。</summary>
    internal static Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>>? TestOverride { get; set; }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Current =>
        TestOverride is { } over ? over.Value : Table.Value;

    /// <summary>归一形查询是否命中策展词表；命中时按 weight 降序返回目标 token 短语。</summary>
    public static IReadOnlyList<string> Expand(string normalizedQuery)
    {
        if (string.IsNullOrEmpty(normalizedQuery)) return Array.Empty<string>();
        return Current.TryGetValue(normalizedQuery, out var targets)
            ? targets
            : Array.Empty<string>();
    }

    /// <summary>已加载的 source 数（诊断 / 测试）。</summary>
    public static int Count => Current.Count;

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadFromDataPack()
    {
        if (!DataPack.Current.TryOpen(FileName, out var stream) || stream is null)
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
            return Parse(lines);
        }
    }

    /// <summary>
    /// 解析并校验 TSV 行（测试可直接喂文本）。返回 source（归一形）→ 目标 token 短语。
    /// 非法行被拒绝：这是数据问题，不能让它变成查询噪声。
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(IEnumerable<string> lines)
    {
        var bySource = new Dictionary<string, List<(double Weight, string Phrase)>>(StringComparer.Ordinal);
        var lineNo = 0;
        var rejected = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            if (raw.Length == 0 || raw[0] == '#') continue;
            var cols = raw.Split('\t');
            if (cols.Length != 6)
            {
                rejected++;
                continue;
            }

            var sourceTerm = cols[0].Trim();
            var targetTerm = cols[1].Trim();
            var weightText = cols[2].Trim();
            var provenance = cols[3].Trim();
            var license = cols[4].Trim();

            if (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                || weight < MinWeight || weight > MaxWeight
                || provenance.Length == 0 || license.Length == 0
                || sourceTerm.Length == 0 || targetTerm.Length == 0
                || HasControl(sourceTerm) || HasControl(targetTerm))
            {
                rejected++;
                continue;
            }

            var sourceKey = Analyzer.Normalize(sourceTerm);
            if (sourceKey.Length == 0 || IsSingleHanChar(sourceKey))
            {
                rejected++;
                continue;
            }

            var phrase = ToTokenPhrase(targetTerm);
            if (phrase.Length == 0)
            {
                rejected++;
                continue;
            }

            if (!bySource.TryGetValue(sourceKey, out var list))
            {
                list = new List<(double, string)>(2);
                bySource[sourceKey] = list;
            }
            list.Add((weight, phrase));
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(bySource.Count, StringComparer.Ordinal);
        foreach (var (source, list) in bySource)
        {
            // 每源最多 4 个目标，按 weight 降序（同权重保持文件顺序），再按短语去重。
            var targets = list
                .OrderByDescending(x => x.Weight)
                .Select(x => x.Phrase)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxTargetsPerSource)
                .ToArray();
            if (targets.Length > 0) result[source] = targets;
        }

        if (rejected > 0)
        {
            Log.LogWarn("indexer",
                $"{FileName}: 拒绝 {rejected} 行非法条目（列数 / weight / 来源 / 许可证 / 单字源 / 控制字符）");
        }
        _ = lineNo;
        return result;
    }

    /// <summary>目标串 → 与倒排同构的 token 短语（CJK bigram；丢弃分析器补的首尾单字）。</summary>
    private static string ToTokenPhrase(string target)
    {
        var analyzed = Analyzer.Analyze(target);
        var tokens = analyzed.Tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return string.Empty;
        if (tokens.Length == 1) return tokens[0];
        var kept = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            // 单字符 token 是 CJK / 假名段的首尾 unigram 兜底，不属于短语本身。
            if (token.Length == 1 && !char.IsAsciiLetterOrDigit(token[0])) continue;
            kept.Add(token);
        }
        if (kept.Count == 0) kept.AddRange(tokens);
        return string.Join(' ', kept);
    }

    private static bool IsSingleHanChar(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x3400) return false;
            count++;
            if (count > 1) return false;
        }
        return count == 1;
    }

    private static bool HasControl(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) return true;
        }
        return false;
    }
}
