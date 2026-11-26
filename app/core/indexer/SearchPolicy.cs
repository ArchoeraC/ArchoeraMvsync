// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>
/// 查询侧策略快照（S1 / T1.1）：把 <see cref="SearchQuery"/> 里的全部默认值收拢成一个
/// 可序列化、可版本化的记录，供 <see cref="SearchPlanCompiler"/> 生成查询计划。
///
/// <para><b>默认数字一个不改</b>：S1 是行为等价重构，本记录的每个默认值都必须与
/// <see cref="SearchQuery"/> / <see cref="SearchRequest"/> 的现值逐项相等（见
/// <c>tests/SearchPlanTests.cs</c> 的默认值快照）。</para>
///
/// <para><b>Cap 只做计划诊断</b>：<see cref="LiteralCap"/> 等是计划期预算 / 诊断字段，
/// EraStore 执行器**不用它们裁剪候选**——候选上限仍由请求参数决定，语义未变。</para>
/// </summary>
internal sealed record SearchPolicy
{
    /// <summary>默认方案 id（S2 / T2.4 起为 <see cref="SchemeIds.Default"/>；S1 的过渡名已退役）。</summary>
    public const string SchemeId = SchemeIds.Default;

    /// <summary>默认结果条数；对应 <see cref="SearchRequest.Limit"/> 的默认值 <c>200</c>。</summary>
    public int DefaultLimit { get; init; } = 200;

    /// <summary>默认「主趟命中达到该值即停止兜底」；对应 <see cref="SearchRequest.MinExact"/> 的默认值 <c>1</c>。</summary>
    public int DefaultMinExact { get; init; } = 1;

    /// <summary>多词查询命中不足时允许丢词降级；对应 <see cref="SearchQuery.AllowDropTokens"/>。</summary>
    public bool AllowDropTokens { get; init; } = SearchQuery.AllowDropTokens;

    /// <summary>字面主趟的计划预算（诊断用，不裁剪候选）；对应设计文档 §3.5 的 <c>literal-exact</c> 2k。</summary>
    public int LiteralCap { get; init; } = 2000;

    /// <summary>单轮丢词趟的计划预算（诊断用）；对应设计文档 §3.5 的 <c>1k/轮</c>。</summary>
    public int DropCap { get; init; } = 1000;

    /// <summary>拼音宽松趟的计划预算（诊断用）；对应设计文档 §3.5 的 <c>phonetic-exact</c> 1k。</summary>
    public int PhoneticCap { get; init; } = 1000;

    /// <summary>读音趟的计划预算（诊断用）；对应设计文档 §3.5 的 <c>reading</c> 500。</summary>
    public int ReadingCap { get; init; } = 500;

    /// <summary>跨语言趟的计划预算（诊断用）；对应设计文档 §3.5 的 <c>cross-lingual</c> 300。</summary>
    public int CrossLingualCap { get; init; } = 300;

    /// <summary>模糊 / 容错趟的计划预算（诊断用）；对应设计文档 §3.5 的 <c>typo</c> 300。</summary>
    public int TypoCap { get; init; } = 300;

    /* ------------------------------------------------------------------ */
    /* 常量快照：值与 SearchQuery 公开常量一一对应（S1 不改数字）          */
    /* ------------------------------------------------------------------ */

    /// <summary>对应 <see cref="SearchQuery.DefaultMaxFuzzyVariants"/>。</summary>
    public int DefaultMaxFuzzyVariants { get; init; } = SearchQuery.DefaultMaxFuzzyVariants;

    /// <summary>对应 <see cref="SearchQuery.DefaultMaxCjkVariants"/>。</summary>
    public int DefaultMaxCjkVariants { get; init; } = SearchQuery.DefaultMaxCjkVariants;

    /// <summary>对应 <see cref="SearchQuery.MaxCjkQueryLength"/>。</summary>
    public int MaxCjkQueryLength { get; init; } = SearchQuery.MaxCjkQueryLength;

    /// <summary>
    /// 对应 <see cref="SearchQuery.MaxPlanQueryCodePoints"/>（计划期收缩门槛）：
    /// 查询可见码点数 ≥ 该值时只生成字面主趟（RP §3.5.7 / N15）。
    /// </summary>
    public int MaxPlanQueryCodePoints { get; init; } = SearchQuery.MaxPlanQueryCodePoints;

    /// <summary>对应 <see cref="SearchQuery.MinCjkFuzzyLength"/>。</summary>
    public int MinCjkFuzzyLength { get; init; } = SearchQuery.MinCjkFuzzyLength;

    /// <summary>对应 <see cref="SearchQuery.MinFuzzyWordLength"/>。</summary>
    public int MinFuzzyWordLength { get; init; } = SearchQuery.MinFuzzyWordLength;

    /// <summary>对应 <see cref="SearchQuery.MinInitialsBranchLength"/>。</summary>
    public int MinInitialsBranchLength { get; init; } = SearchQuery.MinInitialsBranchLength;

    /// <summary>对应 <see cref="SearchQuery.MinCrossLingualCjkLength"/>。</summary>
    public int MinCrossLingualCjkLength { get; init; } = SearchQuery.MinCrossLingualCjkLength;

    /// <summary>对应 <see cref="SearchQuery.DefaultMaxPinyinVariants"/>。</summary>
    public int DefaultMaxPinyinVariants { get; init; } = SearchQuery.DefaultMaxPinyinVariants;

    /// <summary>对应 <see cref="SearchQuery.MaxPinyinVariantsPerSyllable"/>。</summary>
    public int MaxPinyinVariantsPerSyllable { get; init; } = SearchQuery.MaxPinyinVariantsPerSyllable;

    /// <summary>对应 <see cref="SearchQuery.ReadingBranchMinCjkLength"/>。</summary>
    public int ReadingBranchMinCjkLength { get; init; } = SearchQuery.ReadingBranchMinCjkLength;

    /// <summary>对应 <see cref="SearchQuery.MinPrefixTokenLength"/>。</summary>
    public int MinPrefixTokenLength { get; init; } = SearchQuery.MinPrefixTokenLength;

    /// <summary>
    /// 单音节拼音前缀的最小长度（Q3）：对应 <see cref="SearchQuery.MinPinyinPrefixTokenLength"/>。
    /// 只作用于拼音支，不动字面 / 模糊前缀门槛（<see cref="MinPrefixTokenLength"/>）。
    /// </summary>
    public int MinPinyinPrefixTokenLength { get; init; } = SearchQuery.MinPinyinPrefixTokenLength;

    /// <summary>
    /// 主趟短路是否要求「强证据」（Q4）：为 <c>true</c> 时，主趟命中数达到
    /// <c>MinExact</c> 但只有 <see cref="MatchKind.Partial"/>（散落 AND）这类弱证据时，
    /// 仍继续执行拼音宽松 / 丢词 / 读音等兜底趟；强证据（Exact / Phrase / Prefix /
    /// PinyinExact / PinyinPhrase / Initials）一旦出现就照旧短路。<c>MinExact</c> 语义不变。
    /// </summary>
    public bool RequireStrongKindForShortCircuit { get; init; } = false;

    /// <summary>
    /// 步骤 df 预算（Q8）：&gt; 0 时，执行前按表达式估算 df，超过
    /// <c>DfBudgetFactor × step.Cap × DfBudgetCapMultiplier</c> 的步骤跳过并记
    /// <c>df_budget</c>；默认 0 关闭，召回不变。估算为墓碑无关的近似（见
    /// <see cref="EraStore.StoreReader.EstimateDf"/>）。
    /// </summary>
    public double DfBudgetFactor { get; init; } = 0;

    /// <summary>
    /// 步骤 df 预算的 cap 倍率（Q8）：与 <see cref="DfBudgetFactor"/> 相乘，默认 4 即
    /// 「估算 df &gt; 4×cap 才跳过」（对应设计文档 Q8 的 <c>est &gt; 4×cap</c>）。
    /// </summary>
    public int DfBudgetCapMultiplier { get; init; } = 4;

    /// <summary>对应 <see cref="SearchQuery.PairExactWithPrefix"/>。</summary>
    public bool PairExactWithPrefix { get; init; } = SearchQuery.PairExactWithPrefix;

    /// <summary>对应 <see cref="SearchQuery.RequireMetadataForSingleChar"/>。</summary>
    public bool RequireMetadataForSingleChar { get; init; } = SearchQuery.RequireMetadataForSingleChar;

    /// <summary>对应 <see cref="SearchQuery.FirstCharTypoCostsTwo"/>。</summary>
    public bool FirstCharTypoCostsTwo { get; init; } = SearchQuery.FirstCharTypoCostsTwo;

    /// <summary>v7 行为等价方案（唯一默认策略）。</summary>
    public static SearchPolicy Default { get; } = new();

    /// <summary>
    /// Q4 生效策略：与 <see cref="Default"/> 只差「短路要求强证据」。5k qbench eval 实测
    /// P@1 / NDCG@10 / R@50 全部不变（只有兜底趟计数增加），因此给 <c>default@1</c> /
    /// <c>fast@1</c> 启用；<c>recall@1</c> / <c>minimal@1</c> 保持 <see cref="Default"/>。
    /// </summary>
    public static SearchPolicy StrongShortCircuit { get; }
        = Default with { RequireStrongKindForShortCircuit = true };
}
