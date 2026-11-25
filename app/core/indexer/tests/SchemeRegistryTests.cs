// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 方案注册表测试（S2 / T2.4）：四个内置方案、能力协商 / 降级链、matcher 过滤，
/// 以及 <c>vectors/scheme/*.jsonl</c> 的「方案单调性」不变式。
///
/// <para>方案向量**刻意不写死每一步的 id**：真正要守的是
/// <c>minimal ⊆ fast ⊆ default ⊆ recall</c>，以及 fast / minimal 永不生成
/// 读音、跨语言、容错步骤。步骤 id 与 cap 的精确值另有计划向量把关。</para>
/// </summary>
internal static class SchemeRegistryTests
{
    public static void Run()
    {
        RegistryShape();
        CapabilityResolve();
        SchemePlanFiltering();
        SchemeVectors();
    }

    private static void RegistryShape()
    {
        Program.Check("scheme: 内置 4 个方案", SchemeRegistry.All.Count == 4,
            SchemeRegistry.All.Count.ToString());
        Program.Check("scheme: 全部使用 bucket@1",
            SchemeRegistry.All.All(s => s.Ranker == SchemeRegistry.BucketRanker));
        Program.Check("scheme: default 存在",
            SchemeRegistry.TryGet(SchemeIds.Default, out var def) && def.Id == SchemeIds.Default);
        Program.Check("scheme: fast 存在",
            SchemeRegistry.TryGet(SchemeIds.Fast, out var fast) && fast.Id == SchemeIds.Fast);
        Program.Check("scheme: recall 存在",
            SchemeRegistry.TryGet(SchemeIds.Recall, out var recall) && recall.Id == SchemeIds.Recall);
        Program.Check("scheme: minimal 存在",
            SchemeRegistry.TryGet(SchemeIds.Minimal, out var minimal) && minimal.Id == SchemeIds.Minimal);

        Program.Check("scheme: fast = exact + prefix",
            fast.Matchers.SequenceEqual(new[] { SchemeMatchers.Exact, SchemeMatchers.Prefix },
                StringComparer.Ordinal),
            string.Join(',', fast.Matchers));
        Program.Check("scheme: minimal = exact only",
            minimal.Matchers.Count == 1 && minimal.Matchers[0] == SchemeMatchers.Exact,
            string.Join(',', minimal.Matchers));
        Program.Check("scheme: default 无 typo2 / phonetic-lyrics",
            !def.Allows(SchemeMatchers.Typo2) && !def.Allows(SchemeMatchers.PhoneticLyrics));
        Program.Check("scheme: recall 含 typo2 / phonetic-lyrics",
            recall.Allows(SchemeMatchers.Typo2) && recall.Allows(SchemeMatchers.PhoneticLyrics));

        Program.Check("scheme: 未知 id 回退 default 且报告 false",
            !SchemeRegistry.TryGet("bogus@9", out var fallback) && fallback.Id == SchemeIds.Default);
        Program.Check("scheme: s1-compat@1 已退役",
            !SchemeRegistry.TryGet(SchemeIds.S1Compat, out _));
    }

    private static void CapabilityResolve()
    {
        var full = new HashSet<string>(StringComparer.Ordinal)
        {
            IndexCapabilities.SurfaceText, IndexCapabilities.LiteralLyrics,
            IndexCapabilities.PhoneticMeta, IndexCapabilities.PhoneticLyricsPinyin,
            IndexCapabilities.InitialsBigram, IndexCapabilities.Readings,
            IndexCapabilities.LyricsLayers, IndexCapabilities.CrossLingual,
            IndexCapabilities.TypoVocab, IndexCapabilities.HighlightOffsets,
        };

        Program.Check("scheme: 未知 id 解析为 default",
            SchemeRegistry.Resolve("bogus@9", full).Scheme.Id == SchemeIds.Default);
        Program.Check("scheme: 缺省解析为 default",
            SchemeRegistry.Resolve(null, full).Scheme.Id == SchemeIds.Default);
        Program.Check("scheme: recall 不缺能力时原样返回",
            SchemeRegistry.Resolve(SchemeIds.Recall, full).Scheme.Id == SchemeIds.Recall);

        var surfaceOnly = new HashSet<string>(StringComparer.Ordinal) { IndexCapabilities.SurfaceText };
        Program.Check("scheme: default 缺 typo_vocab 降级到 fast",
            SchemeRegistry.Resolve(SchemeIds.Default, surfaceOnly).Scheme.Id == SchemeIds.Fast);

        var empty = new HashSet<string>(StringComparer.Ordinal);
        var (minimal, missing) = SchemeRegistry.Resolve(SchemeIds.Default, empty);
        Program.Check("scheme: 全缺能力落到 minimal", minimal.Id == SchemeIds.Minimal, minimal.Id);
        Program.Check("scheme: 全缺能力报告 all",
            missing.Count == 1 && missing[0] == "all", string.Join(',', missing));

        // v7 能力推导：按 StoreMeta 的构建开关 / 语言画像 / 分析器版本。
        var meta = new StoreMeta
        {
            AnalyzerVersion = Analyzer.AnalyzerVersion,
            IndexLrc = true,
            Pinyin = true,
            HasJapanese = true,
            HasKorean = false,
            HasHan = true,
            HasLatin = true,
        };
        var caps = IndexCapabilities.FromMeta(meta);
        Program.Check("scheme: meta 推导 literal_lyrics", caps.Contains(IndexCapabilities.LiteralLyrics));
        Program.Check("scheme: meta 推导 phonetic_meta", caps.Contains(IndexCapabilities.PhoneticMeta));
        Program.Check("scheme: meta 推导 readings", caps.Contains(IndexCapabilities.Readings));
        Program.Check("scheme: meta 推导 initials_bigram", caps.Contains(IndexCapabilities.InitialsBigram));
        // T3.9：旧分析器（v14 首字母是整段 token）不得声明 bigram 能力。
        var legacy = new StoreMeta
        {
            AnalyzerVersion = Analyzer.InitialsBigramMinVersion - 1,
            Pinyin = true,
            HasHan = true,
            HasLatin = true,
        };
        Program.Check("scheme: v14 索引不宣称 initials_bigram",
            !IndexCapabilities.FromMeta(legacy).Contains(IndexCapabilities.InitialsBigram));
        Program.Check("scheme: 歌词首字母关闭不宣称 initials_lyrics",
            !caps.Contains(IndexCapabilities.InitialsLyrics));
        var withLyricsInitials = new StoreMeta
        {
            AnalyzerVersion = Analyzer.AnalyzerVersion,
            Pinyin = true,
            InitialsLyrics = true,
            HasHan = true,
            HasLatin = true,
        };
        Program.Check("scheme: 歌词首字母开启宣称 initials_lyrics",
            IndexCapabilities.FromMeta(withLyricsInitials).Contains(IndexCapabilities.InitialsLyrics));
        Program.Check("scheme: meta 不声明 df_snapshot",
            !caps.Contains("df_snapshot"));
    }

    private static void SchemePlanFiltering()
    {
        var meta = new StoreMeta
        {
            HasJapanese = true,
            HasKorean = false,
            HasHan = true,
            HasLatin = true,
            Pinyin = true,
            IndexLrc = true,
        };
        var request = new SearchRequest { Query = "我爱你", Fuzzy = true };
        var tokens = Analyzer.AnalyzeQuery(request.Query);

        var fast = SearchPlanCompiler.Compile(request, tokens, meta, Get(SchemeIds.Fast));
        var minimal = SearchPlanCompiler.Compile(request, tokens, meta, Get(SchemeIds.Minimal));
        var def = SearchPlanCompiler.Compile(request, tokens, meta, Get(SchemeIds.Default));
        var recall = SearchPlanCompiler.Compile(request, tokens, meta, Get(SchemeIds.Recall));

        Program.Check("scheme: fast 计划只有 lexical",
            fast.Steps.Count > 0 && fast.Steps.All(s => s.Pass == SearchPassNames.Lexical),
            string.Join(',', fast.Steps.Select(s => s.Pass)));
        Program.Check("scheme: minimal 计划只有 lexical",
            minimal.Steps.Count > 0 && minimal.Steps.All(s => s.Pass == SearchPassNames.Lexical),
            string.Join(',', minimal.Steps.Select(s => s.Pass)));
        Program.Check("scheme: default 含拼音兜底",
            def.Steps.Any(s => s.Pass == SearchPassNames.PinyinLoose),
            string.Join(',', def.Steps.Select(s => s.Pass)));

        var jaRequest = new SearchRequest { Query = "yoru", Fuzzy = true };
        var jaPlan = SearchPlanCompiler.Compile(
            jaRequest, Analyzer.AnalyzeQuery(jaRequest.Query), meta, Get(SchemeIds.Default));
        Program.Check("scheme: default 含读音趟（日文画像）",
            jaPlan.Steps.Any(s => s.Pass == SearchPassNames.Reading),
            string.Join(',', jaPlan.Steps.Select(s => s.Pass)));

        // typo2 只出现在 recall@1（S2 / T2.6）：用 ≥7 字符拉丁词验证。
        var typoRequest = new SearchRequest { Query = "beautiful", Fuzzy = true };
        var typoTokens = Analyzer.AnalyzeQuery(typoRequest.Query);
        var defTypo = SearchPlanCompiler.Compile(typoRequest, typoTokens, meta, Get(SchemeIds.Default));
        var recallTypo = SearchPlanCompiler.Compile(typoRequest, typoTokens, meta, Get(SchemeIds.Recall));
        Program.Check("scheme: default 不生成 typo2",
            defTypo.Steps.All(s => s.Pass != SearchPassNames.Typo2),
            string.Join(',', defTypo.Steps.Select(s => s.Pass)));
        Program.Check("scheme: recall 生成 typo2",
            recallTypo.Steps.Any(s => s.Pass == SearchPassNames.Typo2),
            string.Join(',', recallTypo.Steps.Select(s => s.Pass)));

        var fastPasses = fast.Steps.Select(s => s.Pass).ToHashSet(StringComparer.Ordinal);
        var defPasses = def.Steps.Select(s => s.Pass).ToHashSet(StringComparer.Ordinal);
        var recallPasses = recall.Steps.Select(s => s.Pass).ToHashSet(StringComparer.Ordinal);
        Program.Check("scheme: fast ⊆ default",
            fastPasses.IsSubsetOf(defPasses), string.Join(',', defPasses));
        Program.Check("scheme: default ⊆ recall",
            defPasses.IsSubsetOf(recallPasses), string.Join(',', recallPasses));

        Program.Check("scheme: plan.schemeId 随方案变化",
            fast.SchemeId == SchemeIds.Fast && def.SchemeId == SchemeIds.Default
            && recall.SchemeId == SchemeIds.Recall && minimal.SchemeId == SchemeIds.Minimal,
            $"{fast.SchemeId}/{def.SchemeId}/{recall.SchemeId}/{minimal.SchemeId}");

        // 同一步骤 id 在方案内唯一。
        foreach (var plan in new[] { fast, def, recall, minimal })
        {
            var ids = plan.Steps.Select(s => s.Id).ToArray();
            Program.Check($"scheme: {plan.SchemeId} 步骤 id 唯一",
                ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
                string.Join(',', ids));
        }
    }

    private static void SchemeVectors()
    {
        var dir = ResolveVectorDir();
        if (dir is null)
        {
            Program.Check("scheme vectors 目录存在", false, "找不到 tests/vectors/scheme");
            return;
        }

        var files = Directory.GetFiles(dir, "*.jsonl");
        Array.Sort(files, StringComparer.Ordinal);
        var cases = 0;
        foreach (var file in files)
        {
            Console.WriteLine($"-- scheme/{Path.GetFileName(file)}");
            foreach (var raw in File.ReadAllLines(file, System.Text.Encoding.UTF8))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                try
                {
                    RunVectorCase(line);
                    cases++;
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    Program.Check($"scheme vector {Path.GetFileName(file)}", false, ex.Message);
                }
            }
        }
        Program.Check("scheme vectors 含用例", cases > 0, cases.ToString());
    }

    private static void RunVectorCase(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? "(no id)";
        var query = root.GetProperty("query").GetString() ?? "";
        var scope = root.TryGetProperty("scope", out var scopeElement)
            ? scopeElement.GetString() ?? "all"
            : "all";
        var fuzzy = !root.TryGetProperty("fuzzy", out var fuzzyElement)
            || fuzzyElement.ValueKind != JsonValueKind.False;
        var profile = root.TryGetProperty("profile", out var profileElement)
            ? profileElement.GetString() ?? "cjk"
            : "cjk";
        var pinyin = !root.TryGetProperty("pinyin", out var pinyinElement)
            || pinyinElement.ValueKind != JsonValueKind.False;
        var lrc = !root.TryGetProperty("lrc", out var lrcElement)
            || lrcElement.ValueKind != JsonValueKind.False;

        var request = new SearchRequest
        {
            Query = query,
            Metadata = scope == "metadata",
            Lyrics = scope == "lyrics",
            Fuzzy = fuzzy,
        };
        var meta = new StoreMeta
        {
            HasHan = profile is "cjk" or "cjk+ja" or "cjk+ko",
            HasLatin = profile != "empty",
            HasJapanese = profile == "cjk+ja",
            HasKorean = profile == "cjk+ko",
            Pinyin = pinyin,
            IndexLrc = lrc,
        };

        var passes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var scheme in SchemeRegistry.All)
        {
            var plan = SearchPlanCompiler.Compile(
                request, Analyzer.AnalyzeQuery(query), meta, scheme);
            passes[scheme.Id] = plan.Steps.Select(s => s.Pass).ToHashSet(StringComparer.Ordinal);
        }

        Program.Check($"{id} minimal ⊆ fast",
            passes[SchemeIds.Minimal].IsSubsetOf(passes[SchemeIds.Fast]),
            string.Join(',', passes[SchemeIds.Minimal]));
        Program.Check($"{id} fast ⊆ default",
            passes[SchemeIds.Fast].IsSubsetOf(passes[SchemeIds.Default]),
            string.Join(',', passes[SchemeIds.Fast]));
        Program.Check($"{id} default ⊆ recall",
            passes[SchemeIds.Default].IsSubsetOf(passes[SchemeIds.Recall]),
            string.Join(',', passes[SchemeIds.Default]));

        foreach (var id2 in new[] { SchemeIds.Fast, SchemeIds.Minimal })
        {
            var forbidden = passes[id2].Where(p => p != SearchPassNames.Lexical).ToArray();
            Program.Check($"{id2} {id} 只含 lexical", forbidden.Length == 0,
                string.Join(',', forbidden));
        }
    }

    private static SearchScheme Get(string id)
        => SchemeRegistry.TryGet(id, out var scheme) ? scheme : SchemeRegistry.Default;

    private static string? ResolveVectorDir()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(SchemeRegistryTests).Assembly.Location);
        var dir = assemblyDir is null ? null : new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "tests", "vectors", "scheme"),
                         Path.Combine(dir.FullName, "vectors", "scheme"),
                     })
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
