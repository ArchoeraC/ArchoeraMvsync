// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Text;

namespace Archoera.Indexer;

/// <summary>
/// 查询计划编译器（S1 / T1.3）：把 v7 <see cref="SearchSession.ExecuteUncached"/> 的 if 链
/// **逐项**翻译成有序 <see cref="Step"/> 列表，表达式与趟次顺序保持字节级一致。
///
/// <para>纯函数：不读存储、不加载词表（模糊词表由执行器经
/// <see cref="ResolveDeferredExpression"/> 延迟注入）。唯一的副作用是跨语言候选的脚本裁剪
/// （与旧代码相同：<c>tokens.CrossLingual</c> 原地替换为过滤后的候选）。</para>
/// </summary>
internal static class SearchPlanCompiler
{
    /// <summary>兼容重载：用默认方案的 matchers + 指定策略（S1 测试与诊断仍可用）。</summary>
    public static QueryPlan Compile(
        SearchRequest request, QueryTokens tokens, StoreMeta meta, SearchPolicy policy)
        => Compile(request, tokens, meta, SchemeRegistry.Default with { Policy = policy });

    /// <summary>
    /// 按方案生成计划（S2 / T2.4）：matchers 决定生成哪些步骤，顺序仍是
    /// lexical → drops → pinyinLoose → reading → crossLingual → fuzzy → cjkFuzzy。
    /// </summary>
    public static QueryPlan Compile(
        SearchRequest request, QueryTokens tokens, StoreMeta meta, SearchScheme scheme)
    {
        var policy = scheme.Policy;
        // Q5：只有声明 prefix matcher 的方案才生成 `*`（minimal@1 只声明 exact）。
        var includePrefix = scheme.Allows(SchemeMatchers.Prefix);
        var terms = new List<QueryTerm>(tokens.Lexical.Count);
        foreach (var token in tokens.Lexical)
        {
            terms.Add(new QueryTerm(
                token,
                ScriptOf(token),
                ReadingEligible(token),
                TypoEligible(token, policy)));
        }

        var family = request.Metadata
            ? FieldFamily.Meta
            : request.Lyrics ? FieldFamily.Lyrics : FieldFamily.Mixed;
        var steps = new List<Step>();

        // 单字 CJK 查询在歌词域的语义是「未知」：索引里的歌词单字只作为 bigram 前缀
        // 存在，扫歌词列会把整库召回。S1 直接取空计划（与 metadata 单字门控同源的理由），
        // 不做任何兜底（pass 计数因此全为 0）。
        if (request.Lyrics && tokens.HasCjk && tokens.Lexical.Count == 1)
        {
            return new QueryPlan(
                terms,
                Array.Empty<Step>(),
                Array.Empty<int>(),
                request.MinExact,
                scheme.Id,
                Array.Empty<string>(),
                policy,
                includePrefix);
        }

        // 1) 主趟：精确表达式（词法 / 拼音 / 首字母）。expression 为 null 也保留 step：
        //    旧代码 Merge(null) 之后仍会继续跑兜底，step 存在才不改变后续门的判定顺序。
        //    kind 由表达式形状决定（T2.5）：单字面 Exact / 短语 Phrase / 前缀 Prefix /
        //    散落 AND Partial / 纯拼音 Pinyin* / 首字母串 Initials。
        var shape = SearchQuery.ShapeOf(tokens, includePrefix);
        steps.Add(new Step(
            "lexical@1",
            KindOf(shape.Kind),
            family,
            MatchOp.And,
            tokens.Lexical.ToArray(),
            tokens.Lexical.Count,
            policy.LiteralCap,
            SearchPassNames.Lexical,
            StepGate.None,
            WrapLyrics(SearchQuery.CompileMatch(
                tokens, request.Metadata, lyricsInitials: meta.InitialsLyrics,
                includePrefix: includePrefix,
                minPinyinPrefixLength: policy.MinPinyinPrefixTokenLength), request.Lyrics)));

        // 1.5) 计划期收缩（RP §3.5.7 / N15）：查询可见码点数 ≥ 策略上限时只保留字面主趟。
        //      不生成丢词 / 拼音 / 读音 / 跨语言 / 容错 step——这些 step 的候选数随查询长度
        //      组合增长（变体枚举 / 丢词子集），粘贴整段歌词会让编译与召回同时失控。
        //      只裁剪「生成哪些趟」，不改写查询文本（请求层另有 200 码点截断）。
        if (tokens.VisibleCharCount >= policy.MaxPlanQueryCodePoints)
        {
            return new QueryPlan(
                terms,
                steps,
                Array.Empty<int>(),
                request.MinExact,
                scheme.Id,
                Array.Empty<string>(),
                policy,
                includePrefix);
        }

        // 2) 丢词降级：候选集含「编译为 null」的丢法（仍参与字段亲和度重排），
        //    但只有非空表达式才生成可执行 step（旧代码对 null 直接 continue）。
        var dropCandidates = new List<int>();
        if (policy.AllowDropTokens && scheme.Allows(SchemeMatchers.Drop))
        {
            dropCandidates = SearchQuery.DropTokenCandidates(tokens, request.Query);
            foreach (var dropIndex in dropCandidates)
            {
                var expression = SearchQuery.CompileDropTokens(
                    tokens, request.Query, request.Metadata, dropIndex,
                    meta.InitialsLyrics, includePrefix);
                if (expression is null) continue;
                expression = WrapLyrics(expression, request.Lyrics);

                // 与 CompileDropTokens 内部同源：保留词拼接后再分析。
                var kept = KeptWords(request.Query, dropIndex);
                var keptTokens = Analyzer.AnalyzeQuery(string.Join(' ', kept));
                steps.Add(new Step(
                    "drop@" + dropIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MatchKind.Partial,
                    family,
                    MatchOp.And,
                    keptTokens.Lexical.ToArray(),
                    keptTokens.Lexical.Count,
                    policy.DropCap,
                    SearchPassNames.DropTokens,
                    new StepGate(RequiresMainMiss: true),
                    expression,
                    DropIndex: dropIndex));
            }
        }

        // 3) 拼音宽松兜底（音节位置无关 AND + 模糊音扩展）。
        var pinyinLoose = scheme.Allows(SchemeMatchers.PhoneticMeta)
            ? WrapLyrics(SearchQuery.CompilePinyinLoose(
                tokens, request.Metadata, includePrefix), request.Lyrics)
            : null;
        if (pinyinLoose is not null)
        {
            steps.Add(new Step(
                "pinyin-loose@1",
                KindOf(SearchQuery.PinyinKindOf(tokens)),
                FieldFamily.Pinyin,
                MatchOp.And,
                tokens.Pinyin.ToArray(),
                tokens.Pinyin.Count,
                policy.PhoneticCap,
                SearchPassNames.PinyinLoose,
                new StepGate(RequiresMainMiss: true),
                pinyinLoose));
        }

        // 4) 读音趟：只在库画像含日 / 韩、且方案允许读音时生成（旧代码同画像条件）。
        if (scheme.Allows(SchemeMatchers.Reading) && (meta.HasJapanese || meta.HasKorean))
        {
            var reading = WrapLyrics(
                SearchQuery.CompileReading(tokens, request.Metadata), request.Lyrics);
            if (reading is not null)
            {
                steps.Add(new Step(
                    "reading@1",
                    MatchKind.Reading,
                    FieldFamily.Reading,
                    MatchOp.Phrase,
                    tokens.Lexical.ToArray(),
                    tokens.Lexical.Count,
                    policy.ReadingCap,
                    SearchPassNames.Reading,
                    new StepGate(RequiresMainMiss: true),
                    reading,
                    ReadingSequences: SearchQuery.ReadingSequences(tokens)));
            }
        }

        // 5) 跨语言趟：先按库画像裁剪（同 FilterCrossLingual），再编译候选 OR。
        //    这里会原地替换 tokens.CrossLingual（与旧代码一致）；后续步骤都不读该字段，
        //   executor 每次查询都持有新的 AnalyzeQuery 结果，副作用安全。
        var crossCandidates = scheme.Allows(SchemeMatchers.CrossLingual)
            ? FilterCrossLingual(tokens.CrossLingual, meta)
            : new List<string>();
        if (crossCandidates.Count > 0)
        {
            tokens.CrossLingual.Clear();
            tokens.CrossLingual.AddRange(crossCandidates);
            var crossLingual = WrapLyrics(
                SearchQuery.CompileCrossLingual(tokens, request.Metadata), request.Lyrics);
            if (crossLingual is not null)
            {
                steps.Add(new Step(
                    "cross-lingual@1",
                    MatchKind.CrossLingual,
                    FieldFamily.CrossLingual,
                    MatchOp.Or,
                    crossCandidates.ToArray(),
                    crossCandidates.Count,
                    policy.CrossLingualCap,
                    SearchPassNames.CrossLingual,
                    new StepGate(RequiresMainMiss: true),
                    crossLingual));
            }
        }

        // 6) 模糊趟：不在此加载词表（纯函数）；表达式由执行器延迟解析。
        var typoAllowed = scheme.Allows(SchemeMatchers.Typo1);
        if (typoAllowed && request.Fuzzy && SearchQuery.HasFuzzyEligible(tokens))
        {
            steps.Add(new Step(
                "fuzzy@1",
                MatchKind.Typo,
                FieldFamily.Fuzzy,
                MatchOp.Or,
                tokens.Lexical.ToArray(),
                tokens.Lexical.Count,
                policy.TypoCap,
                SearchPassNames.Fuzzy,
                new StepGate(RequiresMainMiss: true, RequiresFuzzy: true),
                Expression: null,
                NeedsFuzzyVocabulary: true));
        }

        // 6.5) typo2（仅 recall@1）：Damerau ≤2、首字符不变、词长 ≥7，必须靠索引词表枚举。
        //      门只要求 fuzzy=true，不参与 MinExact 短路——recall@1 的语义是「我确定库里有」。
        if (scheme.Allows(SchemeMatchers.Typo2) && request.Fuzzy
            && SearchQuery.HasTypo2Eligible(tokens))
        {
            steps.Add(new Step(
                "typo2@1",
                MatchKind.Typo,
                FieldFamily.Fuzzy,
                MatchOp.Or,
                tokens.Lexical.ToArray(),
                tokens.Lexical.Count,
                policy.TypoCap,
                SearchPassNames.Typo2,
                new StepGate(RequiresFuzzy: true),
                Expression: null,
                NeedsFuzzyVocabulary: true,
                Typo2: true));
        }

        // 7) 中文容错趟：仅 fuzzy=true 且方案允许 typo1 时生成（旧代码 !Fuzzy 已提前 return）。
        if (typoAllowed && request.Fuzzy)
        {
            var variants = SearchQuery.CjkVariants(request.Query);
            if (variants.Count > 0)
            {
                var parts = new List<string>(variants.Count);
                foreach (var variant in variants)
                {
                    var expression = SearchQuery.CompileMatch(
                        Analyzer.AnalyzeQuery(variant), metadata: false,
                        lyricsInitials: meta.InitialsLyrics, includePrefix: includePrefix,
                        minPinyinPrefixLength: policy.MinPinyinPrefixTokenLength);
                    if (expression is not null) parts.Add(expression);
                }

                string? cjkExpression = null;
                if (parts.Count > 0)
                {
                    var joined = parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
                    cjkExpression = request.Metadata
                        ? $"{{{SearchQuery.MetadataColumns}}} : ({joined})"
                        : WrapLyrics(joined, request.Lyrics);
                }

                steps.Add(new Step(
                    "cjk-fuzzy@1",
                    MatchKind.Typo,
                    FieldFamily.Mixed,
                    MatchOp.Or,
                    variants.ToArray(),
                    variants.Count,
                    policy.DefaultMaxCjkVariants,
                    SearchPassNames.CjkFuzzy,
                    new StepGate(RequiresMainMiss: true, RequiresFuzzy: true),
                    cjkExpression,
                    CountPassOnNullExpression: true));
            }
        }

        return new QueryPlan(
            terms,
            steps,
            dropCandidates,
            request.MinExact,
            scheme.Id,
            Array.Empty<string>(),
            policy,
            includePrefix);
    }

    /// <summary>
    /// 按 title / artist 命中分布重排 drop step；非 drop step 的位置不动。
    /// 与旧代码的 `candidates = OrderDropsByFieldAffinity(...)` 等价：只在 gate 打开时调用。
    /// </summary>
    public static IReadOnlyList<Step> OrderDropSteps(
        QueryPlan plan, IReadOnlyList<int> titleCounts, IReadOnlyList<int> artistCounts)
    {
        var ordered = SearchQuery.OrderDropsByFieldAffinity(
            plan.DropCandidates.ToList(), titleCounts, artistCounts);
        if (ordered.Count <= 1) return plan.Steps;

        var rank = new Dictionary<int, int>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++) rank[ordered[i]] = i;

        var dropSteps = plan.Steps
            .Where(step => step.DropIndex >= 0)
            .OrderBy(step => rank.TryGetValue(step.DropIndex, out var r) ? r : int.MaxValue)
            .ToList();
        if (dropSteps.Count <= 1) return plan.Steps;

        var result = new List<Step>(plan.Steps.Count);
        var next = 0;
        foreach (var step in plan.Steps)
            result.Add(step.DropIndex >= 0 ? dropSteps[next++] : step);
        return result;
    }

    /// <summary>
    /// 解析 step 的最终表达式：模糊趟需要索引词表（T1-8），其余直接返回编译期表达式。
    /// 词表加载失败时返回空列表，<see cref="SearchQuery.CompileFuzzy"/> 会自动退回变体枚举。
    /// </summary>
    public static string? ResolveDeferredExpression(
        Step step, QueryTokens tokens, bool metadata, bool lyrics, bool lyricsInitials,
        Func<IReadOnlyList<string>?> loadVocabulary,
        bool includePrefix = true,
        int minPinyinPrefixLength = SearchQuery.MinPinyinPrefixTokenLength)
    {
        if (step.NeedsFuzzyVocabulary)
        {
            var vocabulary = loadVocabulary();
            return WrapLyrics(
                step.Typo2
                    ? SearchQuery.CompileTypo2(tokens, metadata, vocabulary)
                    : SearchQuery.CompileFuzzy(
                        tokens, metadata, vocabulary, lyricsInitials: lyricsInitials,
                        includePrefix: includePrefix,
                        minPinyinPrefixLength: minPinyinPrefixLength),
                lyrics);
        }
        return step.Expression;
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 形状枚举 → 计划 kind 枚举（T2.5 一一对应）。形状本身由
    /// <see cref="SearchQuery.ShapeOf"/> / <see cref="SearchQuery.PinyinKindOf"/> 判定，
    /// 编译器只做枚举映射——**不再二次解析表达式字符串**。
    /// </summary>
    internal static MatchKind KindOf(QueryShapeKind kind) => kind switch
    {
        QueryShapeKind.Exact => MatchKind.Exact,
        QueryShapeKind.Phrase => MatchKind.Phrase,
        QueryShapeKind.Prefix => MatchKind.Prefix,
        QueryShapeKind.Partial => MatchKind.Partial,
        QueryShapeKind.PinyinExact => MatchKind.PinyinExact,
        QueryShapeKind.PinyinPhrase => MatchKind.PinyinPhrase,
        QueryShapeKind.Initials => MatchKind.Initials,
        _ => MatchKind.Exact,
    };

    /// <summary>
    /// 把表达式收紧到歌词字段族（<c>scope=lyrics</c>）：外层 <c>{cols} : (expr)</c> 与
    /// 表达式内部已有的列过滤取交集，因此拼音 / 读音 / 首字母支会自然退化成各自的歌词列。
    /// </summary>
    private static string? WrapLyrics(string? expression, bool lyrics)
        => !lyrics || expression is null
            ? expression
            : $"{{{SearchQuery.LyricsColumns}}} : ({expression})";

    /// <summary>
    /// 按曲库语言画像裁剪跨语言候选（v7）：汉字候选只可能在库内含汉字时命中，
    /// 拉丁释义候选只可能在库内含拉丁字母时命中。只裁「不可能命中」的方向，不裁整趟。
    /// </summary>
    private static List<string> FilterCrossLingual(List<string> candidates, StoreMeta meta)
    {
        var output = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var han = candidate.Any(ScriptRanges.IsCjk);
            if (han ? !meta.HasHan : !meta.HasLatin) continue;
            output.Add(candidate);
        }
        return output;
    }

    /// <summary>与 <see cref="SearchQuery.CompileDropTokens"/> 相同的词切分（空白分段）。</summary>
    private static List<string> KeptWords(string query, int dropIndex)
    {
        var words = string.IsNullOrWhiteSpace(query)
            ? new List<string>()
            : query.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var kept = new List<string>(Math.Max(0, words.Count - 1));
        for (var i = 0; i < words.Count; i++)
        {
            if (i != dropIndex) kept.Add(words[i]);
        }
        return kept;
    }

    /// <summary>词元脚本画像（诊断用；混合脚本记 <c>mixed</c>）。</summary>
    private static string ScriptOf(string surface)
    {
        var han = false;
        var kana = false;
        var hangul = false;
        var latin = false;
        var digit = false;
        var other = false;
        foreach (var rune in surface.EnumerateRunes())
        {
            if (rune.Value <= char.MaxValue)
            {
                var c = (char)rune.Value;
                if (ScriptRanges.IsCjk(c)) { han = true; continue; }
                if (ScriptRanges.IsKana(c)) { kana = true; continue; }
                if (ScriptRanges.IsHangul(c)) { hangul = true; continue; }
                if (c is >= '0' and <= '9') { digit = true; continue; }
                if (ScriptRanges.IsLatinLetter(c)) { latin = true; continue; }
            }
            if (Rune.IsLetterOrDigit(rune)) other = true;
        }

        var count = (han ? 1 : 0) + (kana ? 1 : 0) + (hangul ? 1 : 0)
            + (latin ? 1 : 0) + (digit ? 1 : 0) + (other ? 1 : 0);
        if (count > 1) return "mixed";
        if (han) return "han";
        if (kana) return "kana";
        if (hangul) return "hangul";
        if (latin) return "latin";
        if (digit) return "digit";
        return "other";
    }

    /// <summary>与 <see cref="SearchQuery.ReadingSequences(QueryTokens)"/> 的可读音门一致。</summary>
    private static bool ReadingEligible(string token)
    {
        var lower = token.ToLowerInvariant();
        if (lower.Length < 2) return false;
        foreach (var rune in lower.EnumerateRunes())
        {
            if (rune.Value <= char.MaxValue)
            {
                var c = (char)rune.Value;
                if (ScriptRanges.IsCjk(c) || ScriptRanges.IsSubstringScript(c)) return false;
            }
        }
        return true;
    }

    /// <summary>与 <see cref="SearchQuery"/> 的 <c>IsFuzzyEligible</c> 同口径。</summary>
    private static bool TypoEligible(string token, SearchPolicy policy)
    {
        if (token.Length < policy.MinFuzzyWordLength) return false;
        foreach (var rune in token.EnumerateRunes())
        {
            var isLower = rune.Value >= 0x61 && rune.Value <= 0x7A;
            var isDigit = rune.Value >= 0x30 && rune.Value <= 0x39;
            if (!isLower && !isDigit) return false;
        }
        return true;
    }
}
