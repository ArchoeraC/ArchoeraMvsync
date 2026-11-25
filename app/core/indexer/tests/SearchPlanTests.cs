// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S1 / T1.1–T1.5：搜索策略快照 + 查询计划编译 + v7 执行器**行为等价**回归。
///
/// <para>端到端用例的期望值是在**改动前**的 v7 实现上、用同一份 fixture 录制的
/// （top-k trackId 顺序 / pass 名 / pass 计数 / bm25 分数），旧代码删除后仍以它为合同。</para>
/// </summary>
internal static class SearchPlanTests
{
    public static void Run()
    {
        DefaultsSnapshot();
        PlanStructure();
        LyricsScope();
        ReadingProfile();
        SingleAndMultiWord();
        LongQueryContraction();
        DropOrdering();
        EmptyDropExpression();
        FuzzyPlan();
        CjkFuzzyPlan();
        JsonShape();
        EndToEndEquivalence();
        LyricsEndToEnd();
    }

    /* ------------------------------------------------------------------ */
    /* T1.1 默认值快照                                                     */
    /* ------------------------------------------------------------------ */

    private static void DefaultsSnapshot()
    {
        var policy = SearchPolicy.Default;
        Program.Check("policy: DefaultLimit==200", policy.DefaultLimit == 200, policy.DefaultLimit.ToString());
        Program.Check("policy: DefaultMinExact==1", policy.DefaultMinExact == 1, policy.DefaultMinExact.ToString());
        Program.Check("policy: AllowDropTokens 同源", policy.AllowDropTokens == SearchQuery.AllowDropTokens);
        Program.Check("policy: LiteralCap==2000", policy.LiteralCap == 2000, policy.LiteralCap.ToString());
        Program.Check("policy: DropCap==1000", policy.DropCap == 1000, policy.DropCap.ToString());
        Program.Check("policy: PhoneticCap==1000", policy.PhoneticCap == 1000, policy.PhoneticCap.ToString());
        Program.Check("policy: ReadingCap==500", policy.ReadingCap == 500, policy.ReadingCap.ToString());
        Program.Check("policy: CrossLingualCap==300", policy.CrossLingualCap == 300, policy.CrossLingualCap.ToString());
        Program.Check("policy: TypoCap==300", policy.TypoCap == 300, policy.TypoCap.ToString());

        Program.Check("policy: SchemeId", SearchPolicy.SchemeId == SchemeIds.Default, SearchPolicy.SchemeId);
        Program.Check("policy: 常量快照 == SearchQuery",
            policy.DefaultMaxFuzzyVariants == SearchQuery.DefaultMaxFuzzyVariants
            && policy.DefaultMaxCjkVariants == SearchQuery.DefaultMaxCjkVariants
            && policy.MaxCjkQueryLength == SearchQuery.MaxCjkQueryLength
            && policy.MaxPlanQueryCodePoints == SearchQuery.MaxPlanQueryCodePoints
            && policy.MinCjkFuzzyLength == SearchQuery.MinCjkFuzzyLength
            && policy.MinFuzzyWordLength == SearchQuery.MinFuzzyWordLength
            && policy.MinInitialsBranchLength == SearchQuery.MinInitialsBranchLength
            && policy.MinCrossLingualCjkLength == SearchQuery.MinCrossLingualCjkLength
            && policy.DefaultMaxPinyinVariants == SearchQuery.DefaultMaxPinyinVariants
            && policy.MaxPinyinVariantsPerSyllable == SearchQuery.MaxPinyinVariantsPerSyllable
            && policy.ReadingBranchMinCjkLength == SearchQuery.ReadingBranchMinCjkLength
            && policy.MinPrefixTokenLength == SearchQuery.MinPrefixTokenLength
            && policy.PairExactWithPrefix == SearchQuery.PairExactWithPrefix
            && policy.RequireMetadataForSingleChar == SearchQuery.RequireMetadataForSingleChar
            && policy.FirstCharTypoCostsTwo == SearchQuery.FirstCharTypoCostsTwo);
    }

    /* ------------------------------------------------------------------ */
    /* T1.3 计划结构                                                       */
    /* ------------------------------------------------------------------ */

    private static void PlanStructure()
    {
        var meta = Profile(hasJapanese: false, hasKorean: false);
        var request = new SearchRequest { Query = "我爱你" };
        var tokens = Analyzer.AnalyzeQuery(request.Query);
        var plan = SearchPlanCompiler.Compile(request, tokens, meta, SearchPolicy.Default);

        Program.Check("plan: scheme", plan.SchemeId == SearchPolicy.SchemeId, plan.SchemeId);
        Program.Check("plan: minMatch 沿用 MinExact", plan.MinMatch == request.MinExact);
        Program.Check("plan: missingCaps 为空", plan.MissingCaps.Count == 0);
        Program.Check("plan: terms 保序", string.Join(' ', plan.Terms.Select(t => t.Surface)) == "我爱 爱你 我 你",
            string.Join(' ', plan.Terms.Select(t => t.Surface)));
        Program.Check("plan: 首步是 lexical",
            plan.Steps.Count > 0 && plan.Steps[0].Id == "lexical@1" && plan.Steps[0].Pass == SearchPassNames.Lexical,
            plan.Steps.Count == 0 ? "(empty)" : plan.Steps[0].Id);

        var lexical = plan.Steps.First(step => step.Pass == SearchPassNames.Lexical);
        var fresh = Analyzer.AnalyzeQuery("我爱你");
        Program.Check("plan: lexical 表达式同源",
            lexical.Expression == SearchQuery.CompileMatch(fresh, metadata: false));
        // T2.5：连续 bigram 段 = 位置连续的短语（我爱你 → "我爱 爱你"）→ Phrase。
        Program.Check("plan: lexical kind/family/op",
            lexical.Kind == MatchKind.Phrase && lexical.Family == FieldFamily.Mixed && lexical.Op == MatchOp.And);
        Program.Check("plan: lexical gate 无条件", !lexical.Gate.RequiresMainMiss && !lexical.Gate.RequiresFuzzy);

        var pinyin = plan.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.PinyinLoose);
        Program.Check("plan: 我爱你 有拼音宽松 step", pinyin is not null);
        Program.Check("plan: 拼音宽松表达式同源",
            pinyin?.Expression == SearchQuery.CompilePinyinLoose(fresh, metadata: false));
        Program.Check("plan: 拼音宽松 kind/family/gate",
            pinyin is not null
            && pinyin.Kind == MatchKind.PinyinPhrase
            && pinyin.Family == FieldFamily.Pinyin
            && pinyin.Op == MatchOp.And
            && pinyin.Gate.RequiresMainMiss
            && !pinyin.Gate.RequiresFuzzy);

        // 跨语言趟按实际候选是否存在决定是否生成（与旧代码「有候选才编译」一致）。
        var crossTokens = Analyzer.AnalyzeQuery("我爱你");
        var filtered = crossTokens.CrossLingual
            .Where(candidate => candidate.Any(Archoera.Text.ScriptRanges.IsCjk) ? meta.HasHan : meta.HasLatin)
            .ToList();
        crossTokens.CrossLingual.Clear();
        crossTokens.CrossLingual.AddRange(filtered);
        var expectedCross = filtered.Count > 0
            ? SearchQuery.CompileCrossLingual(crossTokens, metadata: false)
            : null;
        var cross = plan.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.CrossLingual);
        Program.Check("plan: 跨语言 step 存在性与表达式同源",
            (cross is null) == (expectedCross is null) && cross?.Expression == expectedCross,
            expectedCross is null ? "(none)" : expectedCross);

        Program.Check("plan: 无日韩画像不生成 reading",
            plan.Steps.All(step => step.Pass != SearchPassNames.Reading));
        Program.Check("plan: 非 fuzzy 不生成 fuzzy/cjk",
            plan.Steps.All(step => step.Pass != SearchPassNames.Fuzzy && step.Pass != SearchPassNames.CjkFuzzy));

        // 顺序：lexical → pinyinLoose → cross（若存在）。
        var order = plan.Steps.Select(step => step.Id).ToList();
        Program.Check("plan: 步骤顺序 lexical 最先", order[0] == "lexical@1", string.Join(',', order));
        if (pinyin is not null && cross is not null)
            Program.Check("plan: pinyinLoose 在 crossLingual 之前",
                order.IndexOf("pinyin-loose@1") < order.IndexOf("cross-lingual@1"));

        // metadata scope：字面族记 Meta，表达式带元数据列过滤。
        var metaRequest = new SearchRequest { Query = "我爱你", Metadata = true };
        var metaPlan = SearchPlanCompiler.Compile(
            metaRequest, Analyzer.AnalyzeQuery(metaRequest.Query), meta, SearchPolicy.Default);
        Program.Check("plan: metadata 字面族 == Meta",
            metaPlan.Steps[0].Family == FieldFamily.Meta);
        Program.Check("plan: metadata 表达式带列过滤",
            metaPlan.Steps[0].Expression?.StartsWith("{" + SearchQuery.MetadataColumns + "}", StringComparison.Ordinal) == true);
    }

    /* ------------------------------------------------------------------ */
    /* T1.7 / T1.8 前置：scope=lyrics 的计划语义                          */
    /* ------------------------------------------------------------------ */

    /// <summary>歌词列外过滤前缀：`{lyrics_tok ...} : (`。</summary>
    private static string LyricsPrefix => "{" + SearchQuery.LyricsColumns + "} : (";

    private static void LyricsScope()
    {
        var meta = Profile(false, false);

        // 多字：每一趟都保留 v7 的步骤形状，只把最终表达式包进歌词列外过滤。
        var multiRequest = new SearchRequest { Query = "我爱你", Lyrics = true };
        var multi = SearchPlanCompiler.Compile(
            multiRequest, Analyzer.AnalyzeQuery(multiRequest.Query), meta, SearchPolicy.Default);
        Program.Check("plan: lyrics 多字有步骤", multi.Steps.Count > 0, multi.Steps.Count.ToString());
        Program.Check("plan: lyrics 多字每步都带歌词列外过滤",
            multi.Steps.All(step => step.Expression?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true),
            string.Join(" | ", multi.Steps.Select(step => step.Expression ?? "(null)")));

        var lyricsLexical = multi.Steps.First(step => step.Pass == SearchPassNames.Lexical);
        Program.Check("plan: lyrics lexical kind/family/op",
            lyricsLexical.Kind == MatchKind.Phrase
            && lyricsLexical.Family == FieldFamily.Lyrics
            && lyricsLexical.Op == MatchOp.And);
        Program.Check("plan: lyrics lexical 表达式 == 主趟 + 外过滤",
            lyricsLexical.Expression == "{" + SearchQuery.LyricsColumns + "} : ("
                + SearchQuery.CompileMatch(Analyzer.AnalyzeQuery("我爱你"), metadata: false) + ")");

        var lyricsPinyin = multi.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.PinyinLoose);
        Program.Check("plan: lyrics 拼音宽松保留 step 且带外过滤",
            lyricsPinyin?.Expression?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true,
            lyricsPinyin?.Expression ?? "(none)");

        // 单 CJK 字：歌词列的单字只会大范围命中，S1 直接给空计划（零兜底趟）。
        var singleRequest = new SearchRequest { Query = "爱", Lyrics = true };
        var single = SearchPlanCompiler.Compile(
            singleRequest, Analyzer.AnalyzeQuery(singleRequest.Query), meta, SearchPolicy.Default);
        Program.Check("plan: lyrics 单 CJK 字 → 无步骤",
            single.Steps.Count == 0, single.Steps.Count.ToString());
        Program.Check("plan: lyrics 单 CJK 字 → 无丢词候选",
            single.DropCandidates.Count == 0, string.Join(',', single.DropCandidates));

        // 多词：丢词 / 跨语言支同样收紧到歌词列。
        var wordsRequest = new SearchRequest { Query = "周杰伦 晴天", Lyrics = true };
        var words = SearchPlanCompiler.Compile(
            wordsRequest, Analyzer.AnalyzeQuery(wordsRequest.Query), meta, SearchPolicy.Default);
        var lyricsDrops = words.Steps.Where(step => step.DropIndex >= 0).ToList();
        Program.Check("plan: lyrics 多词有 drop step", lyricsDrops.Count > 0, lyricsDrops.Count.ToString());
        Program.Check("plan: lyrics drop 表达式都带外过滤",
            lyricsDrops.All(step => step.Expression?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true),
            string.Join(" | ", lyricsDrops.Select(step => step.Expression ?? "(null)")));
        Program.Check("plan: lyrics drop 字面族 == Lyrics",
            lyricsDrops.All(step => step.Family == FieldFamily.Lyrics));
        var cross = words.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.CrossLingual);
        Program.Check("plan: lyrics 跨语言支存在且带外过滤",
            cross?.Expression?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true,
            cross?.Expression ?? "(none)");

        // 读音趟只在日 / 韩画像下生成：拉丁词元走读音列，歌词域下同样收窄。
        var readingRequest = new SearchRequest { Query = "kurarin", Lyrics = true };
        var reading = SearchPlanCompiler.Compile(
            readingRequest, Analyzer.AnalyzeQuery(readingRequest.Query),
            Profile(hasJapanese: true, hasKorean: false), SearchPolicy.Default);
        var readingStep = reading.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.Reading);
        Program.Check("plan: lyrics reading 表达式带外过滤",
            readingStep?.Expression?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true,
            readingStep?.Expression ?? "(none)");

        // 模糊趟延迟解析：解析时带 lyrics 才包外过滤。
        var fuzzyRequest = new SearchRequest { Query = "helo", Fuzzy = true, Lyrics = true };
        var fuzzy = SearchPlanCompiler.Compile(
            fuzzyRequest, Analyzer.AnalyzeQuery(fuzzyRequest.Query), meta, SearchPolicy.Default);
        var fuzzyStep = fuzzy.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.Fuzzy);
        Program.Check("plan: lyrics fuzzy step 存在", fuzzyStep is not null);
        var resolved = fuzzyStep is null
            ? null
            : SearchPlanCompiler.ResolveDeferredExpression(
                fuzzyStep, Analyzer.AnalyzeQuery("helo"), metadata: false, lyrics: true,
                lyricsInitials: meta.InitialsLyrics,
                loadVocabulary: () => Array.Empty<string>());
        Program.Check("plan: lyrics fuzzy 解析后带外过滤",
            resolved?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true
            && resolved.Contains("helo", StringComparison.Ordinal),
            resolved ?? "(null)");

        // 中文容错：变体先各自编译（metadata:false），拼接后统一包外过滤。
        var cjkRequest = new SearchRequest { Query = "周杰伦啊", Fuzzy = true, Lyrics = true };
        var cjkPlan = SearchPlanCompiler.Compile(
            cjkRequest, Analyzer.AnalyzeQuery(cjkRequest.Query), meta, SearchPolicy.Default);
        var cjkStep = cjkPlan.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.CjkFuzzy);
        Program.Check("plan: lyrics cjk 容错表达式带外过滤",
            cjkStep?.Expression?.StartsWith(LyricsPrefix, StringComparison.Ordinal) == true,
            cjkStep?.Expression ?? "(null)");

        // 对照：metadata / all 的表达式不能被外过滤污染。
        var plainRequest = new SearchRequest { Query = "我爱你" };
        var plain = SearchPlanCompiler.Compile(
            plainRequest, Analyzer.AnalyzeQuery(plainRequest.Query), meta, SearchPolicy.Default);
        Program.Check("plan: all 主趟表达式不带歌词列",
            plain.Steps[0].Expression?.Contains(SearchQuery.LyricsColumns, StringComparison.Ordinal) != true,
            plain.Steps[0].Expression ?? "(null)");
        Program.Check("plan: all 字面族仍为 Mixed", plain.Steps[0].Family == FieldFamily.Mixed);
    }

    private static void ReadingProfile()
    {
        var request = new SearchRequest { Query = "kurarin" };
        var noJa = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(request.Query), Profile(false, false), SearchPolicy.Default);
        Program.Check("plan: 无日韩 profile 不生成 kurarin reading",
            noJa.Steps.All(step => step.Pass != SearchPassNames.Reading));

        var ja = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(request.Query), Profile(true, false), SearchPolicy.Default);
        var reading = ja.Steps.FirstOrDefault(step => step.Pass == SearchPassNames.Reading);
        Program.Check("plan: HasJapanese 生成 reading", reading is not null);
        Program.Check("plan: reading 表达式/序列同源",
            reading is not null
            && reading.Expression == SearchQuery.CompileReading(Analyzer.AnalyzeQuery("kurarin"), metadata: false)
            && reading.ReadingSequences is not null
            && reading.Kind == MatchKind.Reading
            && reading.Family == FieldFamily.Reading
            && reading.Op == MatchOp.Phrase
            && reading.Gate.RequiresMainMiss);

        var korean = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(request.Query), Profile(false, true), SearchPolicy.Default);
        Program.Check("plan: HasKorean 也生成 reading",
            korean.Steps.Any(step => step.Pass == SearchPassNames.Reading));
    }

    private static void SingleAndMultiWord()
    {
        var meta = Profile(false, false);

        var single = SearchPlanCompiler.Compile(
            new SearchRequest { Query = "爱" }, Analyzer.AnalyzeQuery("爱"), meta, SearchPolicy.Default);
        Program.Check("plan: 单字无丢词候选", single.DropCandidates.Count == 0);
        Program.Check("plan: 单字无 drop step", single.Steps.All(step => step.DropIndex < 0));

        var multiRequest = new SearchRequest { Query = "周杰伦 晴天" };
        var multi = SearchPlanCompiler.Compile(
            multiRequest, Analyzer.AnalyzeQuery(multiRequest.Query), meta, SearchPolicy.Default);
        Program.Check("plan: 两词候选完整 [0,1]",
            multi.DropCandidates.Count == 2 && multi.DropCandidates[0] == 0 && multi.DropCandidates[1] == 1,
            string.Join(',', multi.DropCandidates));
        var dropIds = multi.Steps.Where(step => step.DropIndex >= 0).Select(step => step.DropIndex).ToList();
        Program.Check("plan: 两词生成 drop step", dropIds.Count == 2, string.Join(',', dropIds));
        Program.Check("plan: drop step 表达式同源",
            multi.Steps.Where(step => step.DropIndex >= 0).All(step =>
                step.Expression == SearchQuery.CompileDropTokens(
                    Analyzer.AnalyzeQuery(multiRequest.Query), multiRequest.Query, false, step.DropIndex)));
        Program.Check("plan: drop step kind/family/gate",
            multi.Steps.Where(step => step.DropIndex >= 0).All(step =>
                step.Kind == MatchKind.Partial
                && step.Family == FieldFamily.Mixed
                && step.Op == MatchOp.And
                && step.Gate.RequiresMainMiss
                && !step.Gate.RequiresFuzzy));
    }

    /// <summary>
    /// N15 / U4：计划期收缩门槛（RP §3.5.7）。查询可见码点数 ≥
    /// <see cref="SearchQuery.MaxPlanQueryCodePoints"/> 时只生成字面主趟——
    /// 不生成丢词 / 拼音 / 读音 / 跨语言 / 容错 step，且不改写查询文本。
    /// </summary>
    private static void LongQueryContraction()
    {
        var meta = Profile(false, false);
        var threshold = SearchQuery.MaxPlanQueryCodePoints;

        var latin = new string('a', 200);
        var longLatin = SearchPlanCompiler.Compile(
            new SearchRequest { Query = latin, Fuzzy = true },
            Analyzer.AnalyzeQuery(latin), meta, SearchPolicy.Default);
        Program.Check("plan: 200 码点只生成字面主趟",
            longLatin.Steps.Count == 1 && longLatin.Steps[0].Id == "lexical@1",
            string.Join(',', longLatin.Steps.Select(step => step.Id)));
        Program.Check("plan: 200 码点无丢词候选", longLatin.DropCandidates.Count == 0,
            longLatin.DropCandidates.Count.ToString());

        // 门槛是「≥」：threshold-1 个可见码点仍走常规计划（可能生成模糊趟）。
        var below = new string('a', threshold - 1);
        var belowPlan = SearchPlanCompiler.Compile(
            new SearchRequest { Query = below, Fuzzy = true },
            Analyzer.AnalyzeQuery(below), meta, SearchPolicy.Default);
        Program.Check($"plan: {threshold - 1} 码点仍生成模糊趟",
            belowPlan.Steps.Any(step => step.Pass == SearchPassNames.Fuzzy),
            string.Join(',', belowPlan.Steps.Select(step => step.Id)));

        // 多词 / CJK 超长：同样只保留字面主趟（丢词子集与 CJK 变体都按长度增长）。
        var words = string.Join(' ', Enumerable.Repeat("晴天", 40));
        var longWords = SearchPlanCompiler.Compile(
            new SearchRequest { Query = words, Fuzzy = true },
            Analyzer.AnalyzeQuery(words), meta, SearchPolicy.Default);
        Program.Check("plan: 多词超长只生成字面主趟",
            longWords.Steps.Count == 1 && longWords.DropCandidates.Count == 0,
            string.Join(',', longWords.Steps.Select(step => step.Id)));

        var cjk = new string('爱', threshold);
        var longCjk = SearchPlanCompiler.Compile(
            new SearchRequest { Query = cjk, Fuzzy = true },
            Analyzer.AnalyzeQuery(cjk), meta, SearchPolicy.Default);
        Program.Check($"plan: {threshold} 个 CJK 字只生成字面主趟",
            longCjk.Steps.Count == 1 && longCjk.Steps[0].Pass == SearchPassNames.Lexical,
            string.Join(',', longCjk.Steps.Select(step => step.Id)));
    }

    private static void DropOrdering()
    {
        var meta = Profile(false, false);
        var request = new SearchRequest { Query = "周杰伦 晴天" };
        var plan = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(request.Query), meta, SearchPolicy.Default);
        var initial = plan.Steps.Where(step => step.DropIndex >= 0).Select(step => step.DropIndex).ToList();
        Program.Check("plan: 初始丢词顺序 [0,1]",
            initial.SequenceEqual(new[] { 0, 1 }), string.Join(',', initial));

        // 让「周杰伦」（index 0）只出现在 title 上：亲和度更高 → 应该更晚丢。
        var ordered = SearchPlanCompiler.OrderDropSteps(
            plan, titleCounts: new[] { 5, 0 }, artistCounts: new[] { 0, 0 });
        var after = ordered.Where(step => step.DropIndex >= 0).Select(step => step.DropIndex).ToList();
        Program.Check("plan: 按字段亲和度重排 drop", after.SequenceEqual(new[] { 1, 0 }), string.Join(',', after));
        Program.Check("plan: 重排不动非 drop 步位置",
            ordered.Where(step => step.DropIndex < 0).Select(step => step.Id)
                .SequenceEqual(plan.Steps.Where(step => step.DropIndex < 0).Select(step => step.Id)));
        Program.Check("plan: 重排 step 内容不变",
            ordered.Where(step => step.DropIndex >= 0).Select(step => step.Expression)
                .OrderBy(expr => expr, StringComparer.Ordinal)
                .SequenceEqual(plan.Steps.Where(step => step.DropIndex >= 0)
                    .Select(step => step.Expression).OrderBy(expr => expr, StringComparer.Ordinal)));
    }

    private static void EmptyDropExpression()
    {
        var meta = Profile(false, false);
        var request = new SearchRequest { Query = "!!! 晴天 爱" };
        var tokens = Analyzer.AnalyzeQuery(request.Query);
        var plan = SearchPlanCompiler.Compile(request, tokens, meta, SearchPolicy.Default);

        Program.Check("plan: 空表达式丢法仍在 DropCandidates",
            plan.DropCandidates.SequenceEqual(new[] { 0, 1, 2 }), string.Join(',', plan.DropCandidates));
        Program.Check("plan: drop 0 编译为 null",
            SearchQuery.CompileDropTokens(tokens, request.Query, false, 0) is null);
        Program.Check("plan: drop 1/2 编译非 null",
            SearchQuery.CompileDropTokens(tokens, request.Query, false, 1) is not null
            && SearchQuery.CompileDropTokens(tokens, request.Query, false, 2) is not null);
        var ids = plan.Steps.Where(step => step.DropIndex >= 0).Select(step => step.Id).ToList();
        Program.Check("plan: 空表达式不生成 step",
            ids.SequenceEqual(new[] { "drop@1", "drop@2" }), string.Join(',', ids));
    }

    private static void FuzzyPlan()
    {
        var meta = Profile(false, false);
        var tokens = Analyzer.AnalyzeQuery("helo");

        var fuzzy = SearchPlanCompiler.Compile(
            new SearchRequest { Query = "helo", Fuzzy = true }, tokens, meta, SearchPolicy.Default);
        var step = fuzzy.Steps.FirstOrDefault(s => s.Pass == SearchPassNames.Fuzzy);
        Program.Check("plan: fuzzy step 存在", step is not null);
        Program.Check("plan: fuzzy 表达式延迟（null）", step?.Expression is null);
        Program.Check("plan: fuzzy 需要词表", step?.NeedsFuzzyVocabulary == true);
        Program.Check("plan: fuzzy gate 双门",
            step is not null && step.Gate.RequiresMainMiss && step.Gate.RequiresFuzzy);
        Program.Check("plan: fuzzy kind/family/op/cap",
            step is not null
            && step.Kind == MatchKind.Typo
            && step.Family == FieldFamily.Fuzzy
            && step.Op == MatchOp.Or
            && step.Cap == SearchPolicy.Default.TypoCap);

        var resolved = step is null
            ? null
            : SearchPlanCompiler.ResolveDeferredExpression(
                step, Analyzer.AnalyzeQuery("helo"), metadata: false, lyrics: false,
                lyricsInitials: meta.InitialsLyrics,
                loadVocabulary: () => Array.Empty<string>());
        Program.Check("plan: 空词表仍能解析 fuzzy 表达式",
            resolved is not null && resolved.Contains("helo", StringComparison.Ordinal), resolved ?? "(null)");

        var noFuzzy = SearchPlanCompiler.Compile(
            new SearchRequest { Query = "helo", Fuzzy = false }, Analyzer.AnalyzeQuery("helo"), meta,
            SearchPolicy.Default);
        Program.Check("plan: fuzzy=false 无 fuzzy step",
            noFuzzy.Steps.All(s => s.Pass != SearchPassNames.Fuzzy));
        Program.Check("plan: fuzzy=false 无 cjk step",
            noFuzzy.Steps.All(s => s.Pass != SearchPassNames.CjkFuzzy));
    }

    private static void CjkFuzzyPlan()
    {
        var meta = Profile(false, false);

        var twoChar = SearchPlanCompiler.Compile(
            new SearchRequest { Query = "伦周", Fuzzy = true },
            Analyzer.AnalyzeQuery("伦周"), meta, SearchPolicy.Default);
        Program.Check("plan: 两字 CJK 无容错变体",
            SearchQuery.CjkVariants("伦周").Count == 0
            && twoChar.Steps.All(s => s.Pass != SearchPassNames.CjkFuzzy));

        var request = new SearchRequest { Query = "周杰伦啊", Fuzzy = true };
        var plan = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(request.Query), meta, SearchPolicy.Default);
        var cjk = plan.Steps.FirstOrDefault(s => s.Pass == SearchPassNames.CjkFuzzy);
        Program.Check("plan: 多字 CJK 生成容错 step", cjk is not null);
        Program.Check("plan: cjk 容错表达式非空", cjk?.Expression is not null);
        Program.Check("plan: cjk 容错空表达式计数/双门",
            cjk is not null
            && cjk.CountPassOnNullExpression
            && cjk.Gate.RequiresMainMiss
            && cjk.Gate.RequiresFuzzy
            && cjk.Kind == MatchKind.Typo
            && cjk.Family == FieldFamily.Mixed
            && cjk.Op == MatchOp.Or
            && cjk.Cap == SearchQuery.DefaultMaxCjkVariants);
        Program.Check("plan: cjk 容错在计划末尾",
            plan.Steps[plan.Steps.Count - 1].Pass == SearchPassNames.CjkFuzzy);

        var metaRequest = new SearchRequest { Query = "周杰伦啊", Fuzzy = true, Metadata = true };
        var metaPlan = SearchPlanCompiler.Compile(
            metaRequest, Analyzer.AnalyzeQuery(metaRequest.Query), meta, SearchPolicy.Default);
        var metaCjk = metaPlan.Steps.First(s => s.Pass == SearchPassNames.CjkFuzzy);
        Program.Check("plan: metadata cjk 容错带列过滤",
            metaCjk.Expression?.StartsWith("{" + SearchQuery.MetadataColumns + "} : (", StringComparison.Ordinal) == true,
            metaCjk.Expression ?? "(null)");
    }

    private static void JsonShape()
    {
        var request = new SearchRequest { Query = "我爱你" };
        var plan = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(request.Query), Profile(false, false), SearchPolicy.Default);
        var json = JsonSerializer.Serialize(plan);
        Program.Check("plan: JSON 非空", json.Length > 0);
        Program.Check("plan: JSON 含 Steps", json.Contains("\"Steps\"", StringComparison.Ordinal));
        Program.Check("plan: JSON 含 SchemeId", json.Contains("\"SchemeId\"", StringComparison.Ordinal));
        Program.Check("plan: JSON 含 scheme 值",
            json.Contains(SchemeIds.Default, StringComparison.Ordinal));
    }

    /* ------------------------------------------------------------------ */
    /* T1.4 端到端等价（期望值随 analyzer v15 重录：首字母 bigram 改变字段长度）  */
    /* ------------------------------------------------------------------ */

    private static void EndToEndEquivalence()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-searchplan-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (db, index) = BuildFixture(root);
            foreach (var expected in ExpectedCases)
            {
                SearchSession.CloseCached();
                var result = SearchSession.Run(index, new SearchRequest
                {
                    Query = expected.Query,
                    Metadata = expected.Metadata,
                    Fuzzy = expected.Fuzzy,
                    Limit = 20,
                    MinExact = 1,
                });

                var label = $"{expected.Query} [{(expected.Metadata ? "meta" : "all")}/{(expected.Fuzzy ? "fuzzy" : "exact")}]";
                Program.Check($"e2e: {label} 条数", result.Items.Count == expected.Items.Length,
                    $"{result.Items.Count} != {expected.Items.Length}");
                for (var i = 0; i < expected.Items.Length && i < result.Items.Count; i++)
                {
                    var actual = result.Items[i];
                    var want = expected.Items[i];
                    Program.Check($"e2e: {label} #{i} trackId", actual.TrackId == want.TrackId,
                        $"{actual.TrackId} != {want.TrackId}");
                    Program.Check($"e2e: {label} #{i} pass", actual.Pass == want.Pass,
                        $"{actual.Pass} != {want.Pass}");
                    Program.Check($"e2e: {label} #{i} score", Math.Abs(actual.Score - want.Score) <= 1e-6,
                        $"{actual.Score} != {want.Score}");
                }

                var p = result.Passes;
                var wantPass = expected.Passes;
                Program.Check($"e2e: {label} passes",
                    p.DropTokens == wantPass[0]
                    && p.PinyinLoose == wantPass[1]
                    && p.Reading == wantPass[2]
                    && p.CrossLingual == wantPass[3]
                    && p.Fuzzy == wantPass[4]
                    && p.CjkFuzzy == wantPass[5],
                    $"{p.DropTokens},{p.PinyinLoose},{p.Reading},{p.CrossLingual},{p.Fuzzy},{p.CjkFuzzy}");
            }
        }
        finally
        {
            SearchSession.CloseCached();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /* ------------------------------------------------------------------ */
    /* T1.7 / T1.8 端到端：scope=lyrics 只看歌词字段族                     */
    /* ------------------------------------------------------------------ */

    private static void LyricsEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-searchplan-lyrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (_, index) = BuildLyricsFixture(root);

            // y1：标题不含「夜色」，歌词含；y2：标题「夜色」，无歌词。
            SearchSession.CloseCached();
            var lyricsOnly = SearchSession.Run(index, new SearchRequest
            {
                Query = "夜色",
                Lyrics = true,
                Limit = 20,
                MinExact = 1,
            });
            Program.Check("e2e lyrics: 只命中歌词含目标词的曲目",
                lyricsOnly.Items.Count == 1 && lyricsOnly.Items[0].TrackId == "y1",
                DescribeHits(lyricsOnly));

            SearchSession.CloseCached();
            var all = SearchSession.Run(index, new SearchRequest
            {
                Query = "夜色",
                Limit = 20,
                MinExact = 1,
            });
            Program.Check("e2e all: 仍命中标题曲目",
                all.Items.Any(hit => hit.TrackId == "y2"), DescribeHits(all));
            Program.Check("e2e all: 同时命中歌词曲目",
                all.Items.Any(hit => hit.TrackId == "y1"), DescribeHits(all));

            SearchSession.CloseCached();
            var metadata = SearchSession.Run(index, new SearchRequest
            {
                Query = "夜色",
                Metadata = true,
                Limit = 20,
                MinExact = 1,
            });
            Program.Check("e2e metadata: 不命中歌词曲目",
                !metadata.Items.Any(hit => hit.TrackId == "y1"), DescribeHits(metadata));
            Program.Check("e2e metadata: 命中标题曲目",
                metadata.Items.Any(hit => hit.TrackId == "y2"), DescribeHits(metadata));

            SearchSession.CloseCached();
            var single = SearchSession.Run(index, new SearchRequest
            {
                Query = "夜",
                Lyrics = true,
                Fuzzy = true,
                Limit = 20,
                MinExact = 1,
            });
            Program.Check("e2e lyrics: 单 CJK 字 → 空结果",
                single.Items.Count == 0, DescribeHits(single));
            Program.Check("e2e lyrics: 单 CJK 字 → 零兜底趟",
                NoPasses(single), PassSummary(single));
        }
        finally
        {
            SearchSession.CloseCached();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static (string Db, string Index) BuildLyricsFixture(string root)
    {
        var db = Path.Combine(root, "lyrics-library.db");
        var index = Path.Combine(root, "lyrics-search.db");
        EndToEnd.CreateLibrary(db, 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "y1", Path.Combine(root, "y1.mp3"),
                "无关曲目", """[{"name":"合成"}]""", """{"name":"测试"}""",
                "夜色温柔\n星光点点", 1000);
            EndToEnd.InsertTrack(conn, "y2", Path.Combine(root, "y2.mp3"),
                "夜色", """[{"name":"合成"}]""", """{"name":"测试"}""",
                null, 2000);
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        return (db, index);
    }

    private static string DescribeHits(SearchResult result)
        => string.Join(',', result.Items.Select(hit => hit.TrackId));

    private static string PassSummary(SearchResult result)
    {
        var passes = result.Passes;
        return $"drop={passes.DropTokens} pinyin={passes.PinyinLoose} reading={passes.Reading} "
            + $"cross={passes.CrossLingual} fuzzy={passes.Fuzzy} cjk={passes.CjkFuzzy}";
    }

    private static bool NoPasses(SearchResult result)
        => result.Passes.DropTokens == 0
        && result.Passes.PinyinLoose == 0
        && result.Passes.Reading == 0
        && result.Passes.CrossLingual == 0
        && result.Passes.Fuzzy == 0
        && result.Passes.CjkFuzzy == 0;

    private static (string Db, string Index) BuildFixture(string root)
    {
        var db = Path.Combine(root, "library.db");
        var index = Path.Combine(root, "search.db");
        EndToEnd.CreateLibrary(db, 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "j1", Path.Combine(root, "j1.mp3"),
                "きゅうくらりん", """[{"name":"花譜"}]""", """{"name":"J"}""",
                "きゅうくらりん", 1000);
            EndToEnd.InsertTrack(conn, "k1", Path.Combine(root, "k1.mp3"),
                "사랑해", """[{"name":"아이유"}]""", """{"name":"K"}""",
                "사랑해", 2000);
            EndToEnd.InsertTrack(conn, "c1", Path.Combine(root, "c1.mp3"),
                "晴天", """[{"name":"王菲"}]""", """{"name":"叶惠美"}""",
                "我爱你中国\n我爱你", 3000);
            EndToEnd.InsertTrack(conn, "c2", Path.Combine(root, "c2.mp3"),
                "夜曲", """[{"name":"周杰伦"}]""", """{"name":"十一月的萧邦"}""",
                null, 4000);
            EndToEnd.InsertTrack(conn, "c3", Path.Combine(root, "c3.mp3"),
                "爱", """[{"name":"邓丽君"}]""", """{"name":"经典"}""",
                "我愛你", 5000);
            EndToEnd.InsertTrack(conn, "e1", Path.Combine(root, "e1.mp3"),
                "Hello World", """[{"name":"ADELE"}]""", """{"name":"25"}""",
                "蓝蓝的天\n白云飘", 6000);
            EndToEnd.InsertTrack(conn, "p1", Path.Combine(root, "p1.mp3"),
                "月亮代表我的心", """[{"name":"邓丽君"}]""", """{"name":"岛国之情歌"}""",
                "你问我爱你有多深", 7000);
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        return (db, index);
    }

    private static StoreMeta Profile(bool hasJapanese, bool hasKorean)
        => new()
        {
            SchemaVersion = EraStore.Manifest.CurrentMetaVersion,
            AnalyzerVersion = Analyzer.AnalyzerVersion,
            // 这些计划结构断言沿用「v14 full」口径（歌词首字母可用），
            // 默认关闭路径由 EndToEnd 的 InitialsLyricsOption 覆盖。
            InitialsLyrics = true,
            HasJapanese = hasJapanese,
            HasKorean = hasKorean,
            HasHan = true,
            HasLatin = true,
        };

    private sealed record ExpectedHit(string TrackId, string Pass, double Score);

    /// <summary>Passes 顺序：drop / pinyinLoose / reading / crossLingual / fuzzy / cjkFuzzy。</summary>
    private sealed record ExpectedCase(
        string Query, bool Metadata, bool Fuzzy, ExpectedHit[] Items, int[] Passes);

    private static readonly ExpectedCase[] ExpectedCases =
    {
        new("我爱你", false, false, new[]
        {
            // T4.2：内嵌原文带 +3 基线后为「中置信」，仍参与 scope=all；
            // 低置信（损坏 / 可疑）才被歌词列掩码排除。bm25 因 layer_conf 参与 rev 与
            // df 口径变化而重排（重录于 v4 DocStore + 中置信基线的最终实现）。
            new ExpectedHit("c1", "lexical", -1.5194613539697288),
            new ExpectedHit("p1", "lexical", -1.4121169382316834),
            new ExpectedHit("c3", "lexical", -0.3891403477208586),
        }, new[] { 0, 0, 0, 0, 0, 0 }),
        new("晴天", false, false, new[]
        {
            new ExpectedHit("c1", "lexical", -16.234855922155567),
        }, new[] { 0, 0, 0, 0, 0, 0 }),
        new("周杰伦 晴天", false, false, new[]
        {
            new ExpectedHit("c1", "dropTokens", -16.234855922155567),
        }, new[] { 1, 0, 0, 0, 0, 0 }),
        new("woaini", false, false, new[]
        {
            new ExpectedHit("c1", "lexical", -0.23450826217914902),
            new ExpectedHit("c3", "lexical", -0.1945701738604293),
            new ExpectedHit("p1", "lexical", -0.11364766494203321),
        }, new[] { 0, 0, 0, 0, 0, 0 }),
        new("helo", false, true, new[]
        {
            new ExpectedHit("e1", "fuzzy", -2.787473935883563),
        }, new[] { 0, 1, 1, 0, 1, 0 }),
        new("伦周杰", false, true, new[]
        {
            new ExpectedHit("c2", "pinyinLoose", -6.143227047853662),
        }, new[] { 0, 1, 0, 0, 0, 0 }),
        new("周杰伦啊", false, true, new[]
        {
            new ExpectedHit("c2", "cjkFuzzy", -16.12867556856548),
        }, new[] { 0, 1, 0, 1, 0, 1 }),
        new("love", false, false, new[]
        {
            new ExpectedHit("c3", "crossLingual", -2.622288763832019),
        }, new[] { 0, 0, 1, 1, 0, 0 }),
        new("zongguo", false, false, new[]
        {
            new ExpectedHit("c1", "pinyinLoose", -1.3351572513715717),
        }, new[] { 0, 1, 0, 0, 0, 0 }),
        new("kurarin", false, false, new[]
        {
            new ExpectedHit("j1", "reading", -2.0038721039870278),
        }, new[] { 0, 0, 1, 0, 0, 0 }),
        new("晴天", true, false, new[]
        {
            new ExpectedHit("c1", "lexical", -16.234855922155567),
        }, new[] { 0, 0, 0, 0, 0, 0 }),
        new("사랑해", false, false, new[]
        {
            new ExpectedHit("k1", "lexical", -13.579634685672177),
        }, new[] { 0, 0, 0, 0, 0, 0 }),
    };
}
