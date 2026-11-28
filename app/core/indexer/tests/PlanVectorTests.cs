// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 查询计划向量（T1.3）：读取 <c>tests/vectors/plan/*.jsonl</c>，逐条编译
/// <see cref="SearchPlanCompiler"/> 并断言期望的**结构**（terms / 有序 steps /
/// notSteps / minMatch / 确定性）。
///
/// <para>只断言计划结构，不断言 <see cref="Step.Expression"/> 字符串（存储方言细节）；
/// 表达式延迟解析 / null 属于执行器契约，向量不碰。</para>
///
/// <para>接线入口：<see cref="Run"/>；由 <c>tests/Program.cs</c> 的 Main 调用，
/// 复用 <see cref="Program.Check"/> 的 PASS / FAIL 计数。</para>
/// </summary>
internal static class PlanVectorTests
{
    /// <summary>向量目录内可能出现的文件名；按序加载便于定位。</summary>
    public static void Run()
    {
        var dir = ResolvePlanDir();
        if (dir is null)
        {
            Program.Check("plan vectors 目录存在", false, "找不到 tests/vectors/plan");
            return;
        }

        var files = Directory.GetFiles(dir, "*.jsonl");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Program.Check("plan vectors 非空", false, dir);
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var caseCount = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            Console.WriteLine($"-- plan/{fileName}");
            var lineNumber = 0;
            foreach (var raw in File.ReadAllLines(file, System.Text.Encoding.UTF8))
            {
                lineNumber++;
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.Trim().Length == 0) continue;

                PlanCase vector;
                try
                {
                    vector = ParseLine(line, $"{fileName}:{lineNumber}");
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                {
                    Program.Check($"plan {fileName}:{lineNumber} 行格式", false, ex.Message);
                    continue;
                }

                if (!seenIds.Add(vector.Id))
                {
                    Program.Check($"plan {fileName}:{lineNumber} id 唯一", false,
                        $"重复 id {vector.Id}");
                    continue;
                }

                caseCount++;
                RunCase(vector);
            }
        }

        if (caseCount == 0)
            Program.Check("plan vectors 含用例", false, $"{dir} 中没有可解析的用例");
        else
            Console.WriteLine($"plan vectors: {caseCount} cases / {files.Length} file(s)");
    }

    /* ------------------------------------------------------------------ */
    /* 断言                                                                */
    /* ------------------------------------------------------------------ */

    private static void RunCase(PlanCase c)
    {
        var request = new SearchRequest
        {
            Query = c.Query,
            Metadata = c.Scope == "metadata",
            Lyrics = c.Scope == "lyrics",
            Limit = c.Limit,
            Fuzzy = c.Fuzzy,
            MinExact = c.MinExact,
        };
        var meta = new StoreMeta
        {
            HasJapanese = c.HasJapanese,
            HasKorean = c.HasKorean,
            HasHan = c.HasHan,
            HasLatin = c.HasLatin,
        };

        var plan = SearchPlanCompiler.Compile(request, Analyzer.AnalyzeQuery(c.Query), meta, SearchPolicy.Default);

        // 1) 方案 / 能力 / 最小匹配数。
        Program.Check($"{c.Id} schemeId", plan.SchemeId == SearchPolicy.SchemeId,
            $"{plan.SchemeId} != {SearchPolicy.SchemeId}");
        Program.Check($"{c.Id} missingCaps", plan.MissingCaps.Count == 0,
            string.Join(",", plan.MissingCaps));
        Program.Check($"{c.Id} minMatch", plan.MinMatch == c.MinMatch,
            $"expected {c.MinMatch} / actual {plan.MinMatch}");

        // 2) terms 序列（逐项相等）。
        var actualTerms = plan.Terms.Select(t => t.Surface).ToArray();
        Program.Check($"{c.Id} terms 数量", actualTerms.Length == c.Terms.Length,
            $"expected {c.Terms.Length} / actual {actualTerms.Length}");
        var termCount = Math.Min(actualTerms.Length, c.Terms.Length);
        for (var i = 0; i < termCount; i++)
        {
            Program.Check($"{c.Id} terms[{i}]", actualTerms[i] == c.Terms[i],
                $"expected [{c.Terms[i]}] / actual [{actualTerms[i]}]");
        }

        // 3) steps：数量 + 顺序 + 每步结构字段。
        Program.Check($"{c.Id} steps 数量", plan.Steps.Count == c.Steps.Count,
            $"expected {c.Steps.Count} / actual {plan.Steps.Count}");
        var stepCount = Math.Min(plan.Steps.Count, c.Steps.Count);
        for (var i = 0; i < stepCount; i++)
        {
            var actual = plan.Steps[i];
            var expect = c.Steps[i];
            Program.Check($"{c.Id} steps[{i}].id", actual.Id == expect.Id,
                $"expected [{expect.Id}] / actual [{actual.Id}]");
            Program.Check($"{c.Id} steps[{i}].pass", actual.Pass == expect.Pass,
                $"expected [{expect.Pass}] / actual [{actual.Pass}]");
            Program.Check($"{c.Id} steps[{i}].kind", actual.Kind.ToString() == expect.Kind,
                $"expected [{expect.Kind}] / actual [{actual.Kind}]");
            Program.Check($"{c.Id} steps[{i}].family", actual.Family.ToString() == expect.Family,
                $"expected [{expect.Family}] / actual [{actual.Family}]");
            Program.Check($"{c.Id} steps[{i}].op", actual.Op.ToString() == expect.Op,
                $"expected [{expect.Op}] / actual [{actual.Op}]");
            Program.Check($"{c.Id} steps[{i}].covered", actual.CoveredTerms == expect.Covered,
                $"expected {expect.Covered} / actual {actual.CoveredTerms}");

            if (expect.Id.StartsWith("drop@", StringComparison.Ordinal))
            {
                Program.Check($"{c.Id} steps[{i}].dropIndex", actual.DropIndex == expect.DropIndex,
                    $"expected {expect.DropIndex} / actual {actual.DropIndex}");
            }
        }

        // 4) notSteps：这些 step id 必须不存在。
        var actualIds = new HashSet<string>(plan.Steps.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var forbidden in c.NotSteps)
        {
            Program.Check($"{c.Id} notSteps {forbidden}", !actualIds.Contains(forbidden),
                "step 存在");
        }

        // 5) 确定性：同输入再编译一次，序列化字节级一致。
        var again = SearchPlanCompiler.Compile(
            request, Analyzer.AnalyzeQuery(c.Query), meta, SearchPolicy.Default);
        var first = JsonSerializer.Serialize(plan);
        var second = JsonSerializer.Serialize(again);
        Program.Check($"{c.Id} deterministic", string.Equals(first, second, StringComparison.Ordinal),
            $"len {first.Length} / {second.Length}");
    }

    /* ------------------------------------------------------------------ */
    /* 解析（手写 schema 校验；缺字段即失败，不做默认值填充）               */
    /* ------------------------------------------------------------------ */

    private static PlanCase ParseLine(string line, string context)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{context}: 顶层必须是 JSON 对象");

        var id = RequireString(root, "id", context);
        if (id.Length == 0) throw new FormatException($"{context}: id 不能为空");

        var input = RequireObject(root, "input", context);
        var query = RequireString(input, "query", context + ".input.query");
        var scope = RequireString(input, "scope", context + ".input.scope");
        if (scope is not ("all" or "metadata" or "lyrics"))
            throw new FormatException($"{context}: 不支持的 scope [{scope}]（只接受 all / metadata / lyrics）");

        var profile = RequireObject(input, "profile", context + ".input.profile");

        var expect = RequireObject(root, "expect", context + ".expect");
        var terms = RequireStringArray(expect, "terms", context + ".expect.terms");
        var minMatch = RequireInt(expect, "minMatch", context + ".expect.minMatch");
        var missingCaps = RequireStringArray(expect, "missingCaps", context + ".expect.missingCaps");
        if (missingCaps.Length != 0)
            throw new FormatException($"{context}: expect.missingCaps 必须为空数组");

        var stepsElement = RequireArray(expect, "steps", context + ".expect.steps");
        var steps = new List<StepExpect>(stepsElement.GetArrayLength());
        var stepIndex = 0;
        foreach (var stepElement in stepsElement.EnumerateArray())
        {
            var stepContext = $"{context}.expect.steps[{stepIndex++}]";
            if (stepElement.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{stepContext}: step 必须是 JSON 对象");

            var stepId = RequireString(stepElement, "id", stepContext);
            var isDrop = stepId.StartsWith("drop@", StringComparison.Ordinal);
            var skipDropIndex = !isDrop && !stepElement.TryGetProperty("dropIndex", out _);
            steps.Add(new StepExpect(
                Id: stepId,
                Pass: RequireString(stepElement, "pass", stepContext),
                Kind: RequireString(stepElement, "kind", stepContext),
                Family: RequireString(stepElement, "family", stepContext),
                Op: RequireString(stepElement, "op", stepContext),
                Covered: RequireInt(stepElement, "covered", stepContext),
                DropIndex: skipDropIndex ? -1 : RequireInt(stepElement, "dropIndex", stepContext)));
        }

        var notSteps = expect.TryGetProperty("notSteps", out var notStepsElement)
            ? ReadStringArray(notStepsElement, context + ".expect.notSteps")
            : Array.Empty<string>();

        return new PlanCase
        {
            Id = id,
            Query = query,
            Scope = scope,
            Fuzzy = RequireBool(input, "fuzzy", context + ".input.fuzzy"),
            MinExact = RequireInt(input, "minExact", context + ".input.minExact"),
            Limit = RequireInt(input, "limit", context + ".input.limit"),
            HasJapanese = RequireBool(profile, "japanese", context + ".input.profile.japanese"),
            HasKorean = RequireBool(profile, "korean", context + ".input.profile.korean"),
            HasHan = RequireBool(profile, "han", context + ".input.profile.han"),
            HasLatin = RequireBool(profile, "latin", context + ".input.profile.latin"),
            Terms = terms,
            MinMatch = minMatch,
            Steps = steps,
            NotSteps = notSteps,
        };
    }

    private static string RequireString(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"{context}: 缺少字符串字段 {name}");
        return value.GetString()!;
    }

    private static bool RequireBool(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"{context}: 缺少布尔字段 {name}");
        return value.GetBoolean();
    }

    private static int RequireInt(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
            throw new FormatException($"{context}: 缺少整数字段 {name}");
        return result;
    }

    private static JsonElement RequireObject(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{context}: 缺少对象字段 {name}");
        return value;
    }

    private static JsonElement RequireArray(JsonElement obj, string name, string context)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{context}: 缺少数组字段 {name}");
        return value;
    }

    private static string[] RequireStringArray(JsonElement obj, string name, string context)
    {
        var element = RequireArray(obj, name, context);
        return ReadStringArray(element, context);
    }

    private static string[] ReadStringArray(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{context}: 必须是字符串数组");
        var output = new string[element.GetArrayLength()];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new FormatException($"{context}[{i}]: 必须是字符串");
            output[i++] = item.GetString()!;
        }
        return output;
    }

    /* ------------------------------------------------------------------ */
    /* 目录 / 数据模型                                                     */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 定位 <c>tests/vectors/plan</c>：输出目录旁 → 从输出目录向上找
    /// <c>tests/vectors/plan</c> / <c>vectors/plan</c>（与 tests/Program.cs 的
    /// ResolveVectorsDir 同思路；不依赖 csproj 复制规则）。
    /// </summary>
    private static string? ResolvePlanDir()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(PlanVectorTests).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            var beside = Path.Combine(assemblyDir, "vectors", "plan");
            if (Directory.Exists(beside)) return beside;

            var found = WalkUp(new DirectoryInfo(assemblyDir));
            if (found is not null) return found;
        }

        return WalkUp(new DirectoryInfo(AppContext.BaseDirectory));
    }

    private static string? WalkUp(DirectoryInfo? start)
    {
        var dir = start;
        while (dir is not null)
        {
            var fromTests = Path.Combine(dir.FullName, "tests", "vectors", "plan");
            if (Directory.Exists(fromTests)) return fromTests;
            var fromVectors = Path.Combine(dir.FullName, "vectors", "plan");
            if (Directory.Exists(fromVectors)) return fromVectors;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class PlanCase
    {
        public string Id { get; init; } = "";
        public string Query { get; init; } = "";
        public string Scope { get; init; } = "all";
        public bool Fuzzy { get; init; }
        public int MinExact { get; init; } = 1;
        public int Limit { get; init; } = 20;
        public bool HasJapanese { get; init; }
        public bool HasKorean { get; init; }
        public bool HasHan { get; init; }
        public bool HasLatin { get; init; }
        public string[] Terms { get; init; } = Array.Empty<string>();
        public int MinMatch { get; init; }
        public IReadOnlyList<StepExpect> Steps { get; init; } = Array.Empty<StepExpect>();
        public string[] NotSteps { get; init; } = Array.Empty<string>();
    }

    private sealed record StepExpect(
        string Id,
        string Pass,
        string Kind,
        string Family,
        string Op,
        int Covered,
        int DropIndex);
}
