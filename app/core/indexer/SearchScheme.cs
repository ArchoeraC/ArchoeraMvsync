// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>方案 id 常量（SearchSpec v1 §2.2；已发布方案不可改名，只加版本）。</summary>
internal static class SchemeIds
{
    public const string Fast = "fast@1";
    public const string Default = "default@1";
    public const string Recall = "recall@1";
    public const string Minimal = "minimal@1";

    /// <summary>S1 过渡方案名（历史向量仍可能出现，注册表不提供该方案）。</summary>
    public const string S1Compat = "s1-compat@1";
}

/// <summary>matcher 名（SearchSpec v1 §2.2 的有序角色名，不是步骤 id）。</summary>
internal static class SchemeMatchers
{
    public const string Exact = "exact";
    public const string Prefix = "prefix";
    public const string Drop = "drop";
    public const string PhoneticMeta = "phonetic-meta";
    public const string Reading = "reading";
    public const string CrossLingual = "cross-lingual";
    public const string Typo1 = "typo1";
    public const string PhoneticLyrics = "phonetic-lyrics";
    public const string Typo2 = "typo2";
}

/// <summary>能力名（SearchSpec v1 §2.3）。S2 由 v7 的 <see cref="StoreMeta"/> 推导。</summary>
internal static class IndexCapabilities
{
    public const string SurfaceText = "surface_text";
    public const string LiteralLyrics = "literal_lyrics";
    public const string PhoneticMeta = "phonetic_meta";
    public const string PhoneticLyricsPinyin = "phonetic_lyrics_pinyin";
    public const string InitialsBigram = "initials_bigram";

    /// <summary>歌词首字母列可用（T3.4 V4，仅 <c>initialsLyrics=true</c> 的索引声明）。</summary>
    public const string InitialsLyrics = "initials_lyrics";
    public const string Readings = "readings";
    public const string LyricsLayers = "lyrics_layers";
    public const string CrossLingual = "cross_lingual";
    public const string TypoVocab = "typo_vocab";
    public const string HighlightOffsets = "highlight_offsets";

    /// <summary>
    /// v7 索引的能力集合。**保守超集**：v7 没有 Manifest 能力位，这里只按
    /// <see cref="StoreMeta"/> 的构建开关 / 语言画像 / 分析器版本推导；`df_snapshot`
    /// 不声明（EraStore 固有）。S3 接 Manifest 后改为「构建收尾写入、查询只读」。
    /// </summary>
    public static IReadOnlySet<string> FromMeta(StoreMeta meta)
    {
        var caps = new HashSet<string>(StringComparer.Ordinal)
        {
            SurfaceText,
            CrossLingual,
            TypoVocab,
            HighlightOffsets,
        };
        if (meta.IndexLrc || meta.RomajiLyrics) caps.Add(LyricsLayers);
        if (meta.IndexLrc) caps.Add(LiteralLyrics);
        if (meta.Pinyin)
        {
            caps.Add(PhoneticMeta);
            caps.Add(PhoneticLyricsPinyin);
            // v15 起首字母列才是 bigram。旧索引（analyzer < 15）首字母是整段一个 token，
            // 中间首字母查不到，不能声明该能力（T3.9：不谎报）。
            if (meta.AnalyzerVersion >= Analyzer.InitialsBigramMinVersion) caps.Add(InitialsBigram);
        }
        // 歌词首字母是独立能力（T3.4 V4）：关闭时列内容为空，不能宣称可用，
        // 否则方案协商会以为「歌词首字母」可召回。元数据首字母不受影响。
        if (meta.Pinyin && meta.InitialsLyrics) caps.Add(InitialsLyrics);
        if (meta.HasJapanese || meta.HasKorean) caps.Add(Readings);
        return caps;
    }
}

/// <summary>
/// 一个检索方案（SearchSpec v1 §2.1）：matchers 有序、ranker 唯一、policy 与能力依赖随方案冻结。
///
/// <para>方案只描述「生成哪些步骤」，不含任何存储方言；S2 的步骤生成仍在
/// <see cref="SearchPlanCompiler"/> 内完成，注册表本身**不引用旧实现**。</para>
/// </summary>
internal sealed record SearchScheme(
    string Id,
    IReadOnlyList<string> Matchers,
    IReadOnlySet<string> Requires,
    string Ranker,
    SearchPolicy Policy,
    string? DegradeTo)
{
    public bool Allows(string matcher) => Matchers.Contains(matcher, StringComparer.Ordinal);
}

/// <summary>
/// 方案注册表（S2 / T2.4）：四个内置方案 + 能力协商 + 环路检测。
///
/// <para>注册表只持有纯数据；`Resolve` 不查库、不做 IO。未知 id 与缺能力都回退到
/// 降级链，最终落到 <see cref="SchemeIds.Minimal"/>（链终点）。</para>
/// </summary>
internal static class SchemeRegistry
{
    public const string BucketRanker = "bucket@1";

    private static readonly Dictionary<string, SearchScheme> Schemes = Build();

    public static SearchScheme Default => Schemes[SchemeIds.Default];

    public static IReadOnlyCollection<SearchScheme> All => Schemes.Values;

    public static bool TryGet(string? id, out SearchScheme scheme)
    {
        if (!string.IsNullOrEmpty(id) && Schemes.TryGetValue(id, out var found))
        {
            scheme = found;
            return true;
        }
        scheme = Default;
        return false;
    }

    /// <summary>
    /// 按 SearchSpec §2.3 的伪代码解析：未知 id 取默认；缺能力沿 degradeTo 下降；
    /// 全链缺失时落到 `minimal@1` 并报告 `missing=["all"]`。
    /// </summary>
    public static (SearchScheme Scheme, IReadOnlyList<string> Missing) Resolve(
        string? requestedId, IReadOnlySet<string> capabilities)
    {
        var scheme = TryGet(requestedId, out var found) ? found : Default;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(scheme.Id))
                throw new InvalidOperationException($"scheme 降级链出现环: {scheme.Id}");

            var missing = scheme.Requires.Where(cap => !capabilities.Contains(cap)).ToArray();
            if (missing.Length == 0) return (scheme, Array.Empty<string>());

            if (scheme.DegradeTo is null)
                return (Schemes[SchemeIds.Minimal], new[] { "all" });
            if (!Schemes.TryGetValue(scheme.DegradeTo, out var next))
                return (Schemes[SchemeIds.Minimal], new[] { "all" });
            scheme = next;
        }
    }

    private static Dictionary<string, SearchScheme> Build()
    {
        var surfaceOnly = new HashSet<string>(StringComparer.Ordinal) { IndexCapabilities.SurfaceText };
        var withTypo = new HashSet<string>(StringComparer.Ordinal)
        {
            IndexCapabilities.SurfaceText,
            IndexCapabilities.TypoVocab,
        };

        var schemes = new[]
        {
            new SearchScheme(
                SchemeIds.Fast,
                new[] { SchemeMatchers.Exact, SchemeMatchers.Prefix },
                surfaceOnly,
                BucketRanker,
                SearchPolicy.StrongShortCircuit,
                SchemeIds.Minimal),
            new SearchScheme(
                SchemeIds.Default,
                new[]
                {
                    SchemeMatchers.Exact, SchemeMatchers.Prefix, SchemeMatchers.Drop,
                    SchemeMatchers.PhoneticMeta, SchemeMatchers.Reading,
                    SchemeMatchers.CrossLingual, SchemeMatchers.Typo1,
                },
                withTypo,
                BucketRanker,
                SearchPolicy.StrongShortCircuit,
                SchemeIds.Fast),
            new SearchScheme(
                SchemeIds.Recall,
                new[]
                {
                    SchemeMatchers.Exact, SchemeMatchers.Prefix, SchemeMatchers.Drop,
                    SchemeMatchers.PhoneticMeta, SchemeMatchers.Reading,
                    SchemeMatchers.CrossLingual, SchemeMatchers.Typo1,
                    SchemeMatchers.PhoneticLyrics, SchemeMatchers.Typo2,
                },
                withTypo,
                BucketRanker,
                SearchPolicy.Default,
                SchemeIds.Default),
            new SearchScheme(
                SchemeIds.Minimal,
                new[] { SchemeMatchers.Exact },
                surfaceOnly,
                BucketRanker,
                SearchPolicy.Default,
                null),
        };

        var map = new Dictionary<string, SearchScheme>(StringComparer.Ordinal);
        foreach (var scheme in schemes)
        {
            if (!map.TryAdd(scheme.Id, scheme))
                throw new InvalidOperationException($"scheme 重复: {scheme.Id}");
        }

        // 启动期环检测（SearchSpec §2.3）：任何 degradeTo 必须存在且无环。
        foreach (var scheme in schemes)
            ValidateChain(map, scheme);

        return map;
    }

    private static void ValidateChain(
        IReadOnlyDictionary<string, SearchScheme> map, SearchScheme scheme)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = scheme;
        while (current.DegradeTo is not null)
        {
            if (!seen.Add(current.Id))
                throw new InvalidOperationException($"scheme 降级链出现环: {current.Id}");
            if (!map.TryGetValue(current.DegradeTo, out var next))
                throw new InvalidOperationException(
                    $"scheme {current.Id} 的 degradeTo 不存在: {current.DegradeTo}");
            current = next;
        }
    }
}
