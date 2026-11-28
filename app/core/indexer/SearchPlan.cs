// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>匹配类型（设计文档 §3.5.1 / SearchSpec v1；S1 只用于计划诊断与归因）。</summary>
internal enum MatchKind
{
    Exact,
    Phrase,
    Prefix,
    Partial,
    PinyinExact,
    PinyinPhrase,
    Initials,
    Reading,
    CrossLingual,
    Typo,
}

/// <summary>一个步骤覆盖的字段族（归因用；S1 仍按趟次合并，不参与排序）。</summary>
internal enum FieldFamily
{
    Meta,
    Lyrics,
    Mixed,
    Pinyin,
    Initials,
    Reading,
    CrossLingual,
    Fuzzy,
}

/// <summary>步骤的迭代器 / 表达式操作形态（与设计文档 §3.5.5 的映射表一致）。</summary>
internal enum MatchOp
{
    Term,
    Prefix,
    Phrase,
    And,
    Or,
}

/// <summary>归一化后的查询词元 + 它的脚本 / 可否读音 / 可否容错画像。</summary>
internal sealed record QueryTerm(
    string Surface,
    string Script,
    bool ReadingEligible,
    bool TypoEligible);

/// <summary>
/// 步骤的执行门（S1 为复刻 v7 短路语义而存在）：
/// <see cref="RequiresMainMiss"/> = 已有命中数 &lt; <c>request.MinExact</c> 才执行；
/// <see cref="RequiresFuzzy"/> = 请求显式启用 <c>fuzzy</c> 才执行。
/// </summary>
internal sealed record StepGate(
    bool RequiresMainMiss = false,
    bool RequiresFuzzy = false)
{
    /// <summary>无条件执行（主趟）。</summary>
    public static StepGate None { get; } = new();
}

/// <summary>
/// 查询计划的一步：语义（kind / family / op / terms）与 v7 执行所需的表达式并列。
///
/// <para><see cref="Expression"/> 是 v7 FTS5 <c>MATCH</c> 表达式（S1 唯一允许的存储方言），
/// <c>null</c> 表示尚未解析（如模糊趟要先读索引词表）或本步没有表达式。</para>
///
/// <para><see cref="Gate"/> 复刻旧 `if (result.Count &lt; MinExact)` 短路；S2 换成桶序后
/// 这组门会退役，届时只保留「省钱」语义的步骤剪枝。</para>
/// </summary>
internal sealed record Step(
    string Id,
    MatchKind Kind,
    FieldFamily Family,
    MatchOp Op,
    IReadOnlyList<string> Terms,
    int CoveredTerms,
    int Cap,
    string Pass,
    StepGate Gate,
    string? Expression,
    bool NeedsFuzzyVocabulary = false,
    bool CountPassOnNullExpression = false,
    IReadOnlyList<string[]>? ReadingSequences = null,
    int DropIndex = -1,
    bool Typo2 = false);

/// <summary>
/// 一次查询的完整计划（纯数据，可 <c>System.Text.Json</c> 序列化用于诊断 / 向量）。
///
/// <para><see cref="MinMatch"/> 在 S1 直接取 <c>request.MinExact</c>（v7 兼容字段）；
/// 设计文档 §3.5 的分段曲线（n=1→1 / n=2→2 / n≥3→ceil(2n/3)）要等 S2 才改变行为。</para>
///
/// <para><c>IncludePrefix</c>（Q5）由方案是否声明 <see cref="SchemeMatchers.Prefix"/> 决定，
/// 执行器解析延迟表达式（模糊趟）时据此关闭 <c>*</c>；<c>minimal@1</c> 因此是纯精确方案。</para>
/// </summary>
internal sealed record QueryPlan(
    IReadOnlyList<QueryTerm> Terms,
    IReadOnlyList<Step> Steps,
    IReadOnlyList<int> DropCandidates,
    int MinMatch,
    string SchemeId,
    IReadOnlyList<string> MissingCaps,
    SearchPolicy Policy,
    bool IncludePrefix = true);
