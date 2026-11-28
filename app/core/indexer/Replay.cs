// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archoera.Lyrics;
using Archoera.Text;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>
/// S0 回放 harness：把「查询集 + 语料」固化成可复现的基线，供 S1 行为等价重构做 before/after 对比
/// （设计文档 §6.7）。
///
/// 四个子命令：
/// <list type="bullet">
///   <item><c>run</c>：读 JSONL 查询集，逐条走生产查询路径 <see cref="SearchSession"/>，
///         输出 §6.7.4 结构的基线 JSON（每条只留前 100 个 trackId）。</item>
///   <item><c>corpus</c>：按 <c>sha256(track_id)</c> 升序从真实库切前 N 首，生成可复现的子集库
///         与 corpus hash（§6.7.3）。</item>
///   <item><c>sample</c>：从桌面端查询日志抽样回放集（§6.7.1 / §6.7.6）：
///         固定 14 条 + 按频次加权、无放回、确定性抽 N 条，输出可直接喂给 <c>run</c> 的 JSONL；
///         日志不足时可用 <c>--library</c> 从曲库派生标题 / 歌手 / 专辑（可选歌词行）补齐。</item>
///   <item><c>compare</c>：比较两份 §6.7.4 基线 JSON（S0/S1 固化基线 vs 新跑结果），
///         逐条给出 id 名次变化 / 空态 / overlap@10；完全一致才退出 0（§6.4 的 S1 门禁）。</item>
/// </list>
///
/// stdout：结果 JSON；stderr：人类日志与错误。exit code：0=成功（<c>compare</c> 还要求完全一致），
/// 1=参数 / 运行错误，或 <c>compare</c> 发现任何 ids / 顺序 / 空态差异。
/// </summary>
internal static class Replay
{
    /// <summary>基线里每条查询最多保留的 trackId 数（§6.7.4：够算 overlap@10 / NDCG@10）。</summary>
    private const int MaxIdsPerQuery = 100;

    /// <summary>查询文本长度上限（§6.7.1：超过即丢弃）。</summary>
    private const int MaxQueryLength = 200;

    /// <summary>corpus 子命令单批 IN(...) 的参数个数（SQLite 默认参数上限 999）。</summary>
    private const int InsertBatchSize = 500;

    private const string Usage = """
        用法: indexer replay run --index <search.db> --queries <queries.jsonl> --out <run.json>
                                 [--limit <n>] [--repeats <n>] [--commit <sha>] [--corpus-hash <hex>]
              indexer replay corpus --library <library.db> --tracks <n> --out <subset.db>
              indexer replay sample --log <queries-log.jsonl> --out <queries.jsonl>
                                    [--library <library.db> [--with-lyrics]]
                                    [--replay <n>] [--seed <int>] [--limit <n>]
              indexer replay compare --baseline <baseline.json> --run <run.json>
                                     [--out <diff.json>] [--allow-subset]
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "run" => RunQueries(args[1..]),
            "corpus" => BuildCorpus(args[1..]),
            "sample" => RunSample(args[1..]),
            "compare" => CompareRuns(args[1..]),
            "-h" or "--help" => PrintHelp(),
            _ => Fail($"未知 replay 子命令: {args[0]}"),
        };
    }

    private static int PrintHelp()
    {
        Console.Error.WriteLine(Usage);
        return 0;
    }

    /* ------------------------------------------------------------------ */
    /* replay run                                                          */
    /* ------------------------------------------------------------------ */

    /// <summary>查询集中的一条有效查询（id 一旦进入基线不可复用）。</summary>
    private sealed record QueryCase(string Id, string Query, bool Metadata, int Limit);

    private static int RunQueries(string[] args)
    {
        string? indexPath = null;
        string? queriesPath = null;
        string? outPath = null;
        string? commit = null;
        string? corpusHash = null;
        var limit = 20;
        var repeats = 3;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index" when i + 1 < args.Length: indexPath = args[++i]; break;
                case "--queries" when i + 1 < args.Length: queriesPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--commit" when i + 1 < args.Length: commit = args[++i]; break;
                case "--corpus-hash" when i + 1 < args.Length: corpusHash = args[++i]; break;
                case "--limit" when i + 1 < args.Length && int.TryParse(args[++i], out var l) && l > 0:
                    limit = l;
                    break;
                case "--repeats" when i + 1 < args.Length && int.TryParse(args[++i], out var r) && r > 0:
                    repeats = r;
                    break;
                case "-h" or "--help":
                    return PrintHelp();
            }
        }

        if (indexPath is null)
            return Fail($"索引不存在: {indexPath ?? "(未指定 --index)"}");
        var eraDir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? ".", IndexerEngine.EraStoreDirName);
        var eraName = EraStore.CurrentPointer.Read(eraDir);
        if (eraName is null)
            return Fail($"索引不存在（EraStore CURRENT 缺失）: {indexPath}");
        var eraPath = Path.Combine(eraDir, eraName);
        if (!File.Exists(eraPath))
            return Fail($"索引不存在: {indexPath}");
        if (queriesPath is null)
            return Fail("缺少 --queries <queries.jsonl>");
        if (!File.Exists(queriesPath))
            return Fail($"查询集不存在: {queriesPath}");
        if (outPath is null)
            return Fail("缺少 --out <run.json>");

        List<QueryCase> cases;
        try
        {
            cases = LoadQueries(queriesPath, limit);
        }
        catch (JsonException)
        {
            return Fail($"查询集不是合法的 JSONL: {queriesPath}");
        }
        catch (IOException ex)
        {
            return Fail($"读取查询集失败: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return Fail(ex.Message);
        }
        if (cases.Count == 0)
            return Fail($"查询集为空或无有效查询: {queriesPath}");

        var results = new List<(QueryCase Case, List<string> Ids, double P50Ms)>(cases.Count);
        try
        {
            foreach (var queryCase in cases)
            {
                var samples = new double[repeats];
                List<string> ids = new();

                for (var rep = 0; rep < repeats; rep++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var result = SearchSession.Run(indexPath, new SearchRequest
                    {
                        Query = queryCase.Query,
                        Limit = queryCase.Limit,
                        Metadata = queryCase.Metadata,
                        Fuzzy = true,
                        MinExact = 1,
                    });
                    var elapsed = Stopwatch.GetTimestamp();

                    samples[rep] = (elapsed - started) * 1000.0 / Stopwatch.Frequency;
                    // 结果只取首轮（确定性：同请求同 top-k），时间取 repeats 的中位数。
                    if (rep == 0) ids = result.Items.Take(MaxIdsPerQuery).Select(hit => hit.TrackId).ToList();
                }

                results.Add((queryCase, ids, Math.Round(Median(samples), 3, MidpointRounding.AwayFromZero)));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            return Fail($"回放失败: {ex.Message}");
        }

        var fullOut = Path.GetFullPath(outPath);
        var outDir = Path.GetDirectoryName(fullOut);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var queryHash = QueriesHash(cases);
        var json = WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("commit", commit ?? "");
            w.WriteString("builtAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            w.WritePropertyName("corpus");
            w.WriteStartObject();
            w.WriteString("kind", "replay");
            w.WriteNumber("tracks", 0);
            w.WriteString("hash", corpusHash ?? "");
            w.WriteEndObject();

            w.WritePropertyName("queries");
            w.WriteStartObject();
            w.WriteNumber("count", cases.Count);
            w.WriteString("hash", queryHash);
            w.WriteEndObject();

            w.WritePropertyName("options");
            w.WriteStartObject();
            w.WriteString("scheme", "erastore");
            w.WriteBoolean("lyricsPinyin", true);
            w.WriteBoolean("lyricsInitials", false);
            w.WriteEndObject();

            w.WritePropertyName("index");
            w.WriteStartObject();
            w.WriteNumber("fileBytes", new FileInfo(eraPath).Length);
            w.WriteNumber("liveBytes", 0);
            w.WriteNumber("buildMs", 0);
            w.WriteNumber("openMs", 0);
            w.WriteEndObject();

            w.WritePropertyName("results");
            w.WriteStartObject();
            foreach (var (queryCase, ids, p50) in results)
            {
                w.WritePropertyName(queryCase.Id);
                w.WriteStartObject();
                w.WritePropertyName("ids");
                w.WriteStartArray();
                foreach (var id in ids) w.WriteStringValue(id);
                w.WriteEndArray();
                w.WriteNumber("total", -1);
                // F3 保证固定 3 位小数（WriteNumber(double) 会省略末尾 0）。
                w.WritePropertyName("p50Ms");
                w.WriteRawValue(p50.ToString("F3", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteEndObject();
        });

        File.WriteAllBytes(fullOut, Encoding.UTF8.GetBytes(json));

        Console.WriteLine(WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteString("corpus", "replay");
            w.WriteNumber("queries", cases.Count);
            w.WriteString("hash", queryHash);
            w.WriteString("out", fullOut);
            w.WriteEndObject();
        }));
        return 0;
    }

    /// <summary>
    /// 读 JSONL 查询集：取 <c>query</c>（兼容旧日志键 <c>q</c>）与可选 <c>id</c> / <c>scope</c> / <c>limit</c>。
    /// 丢弃空串、纯空白、长度 &gt; 200 的查询；按 (query, scope) 去重；无 id 时按出现顺序编号 Q0001…
    /// </summary>
    private static List<QueryCase> LoadQueries(string path, int defaultLimit)
    {
        var cases = new List<QueryCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nextGeneratedId = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;

            var query = ReadString(root, "query") ?? ReadString(root, "q");
            if (query is null) continue;
            if (query.Length > MaxQueryLength) continue;
            if (query.Trim().Length == 0) continue;

            var metadata = string.Equals(ReadString(root, "scope"), "metadata", StringComparison.OrdinalIgnoreCase);
            var scopeKey = metadata ? "metadata" : "all";
            if (!seen.Add(scopeKey + "\u0001" + query)) continue;

            var queryLimit = root.TryGetProperty("limit", out var limitValue)
                && limitValue.ValueKind == JsonValueKind.Number
                && limitValue.TryGetInt32(out var parsedLimit)
                && parsedLimit > 0
                    ? parsedLimit
                    : defaultLimit;

            var givenId = ReadString(root, "id");
            string id;
            if (string.IsNullOrEmpty(givenId))
            {
                // 跳过已被显式 id 占用的编号，避免混合查询集里撞键。
                do { id = "Q" + (++nextGeneratedId).ToString("0000", CultureInfo.InvariantCulture); }
                while (ids.Contains(id));
            }
            else
            {
                id = givenId;
            }
            // id 是基线结果字典的键：重复会让 JSON 出现同名字段，必须显式失败。
            if (!ids.Add(id))
                throw new InvalidDataException($"查询集 id 重复: {id}（id 一旦进入基线不可复用）");
            cases.Add(new QueryCase(id, query, metadata, queryLimit));
        }

        return cases;
    }

    /// <summary>按 id 升序的 <c>id\tquery\tscope</c> 行以 <c>\n</c> 连接后的 SHA-256（小写 hex）。</summary>
    private static string QueriesHash(IReadOnlyList<QueryCase> cases)
        => QueriesHash(cases.Select(c => (c.Id, c.Query, c.Metadata ? "metadata" : "all")));

    /// <summary>
    /// 查询集 hash 的通用实现（<c>sample</c> 与 <c>run</c> 共用）：
    /// 按 id 序数排序后，把 <c>id\tquery\tscope</c> 行以 <c>\n</c> 连接，取 SHA-256 小写 hex。
    /// </summary>
    internal static string QueriesHash(IEnumerable<(string Id, string Query, string Scope)> rows)
    {
        var text = string.Join('\n', rows
            .OrderBy(row => row.Id, StringComparer.Ordinal)
            .Select(row => row.Id + "\t" + row.Query + "\t" + row.Scope));
        return Sha256Hex(text);
    }

    /* ------------------------------------------------------------------ */
    /* replay sample                                                       */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 固定 14 条代表查询（§6.7.1）：文本与顺序必须与 qbench 的 <c>QueryBench.Cases</c> 完全一致。
    ///
    /// 冻结后不得为了「结果好看」改动；新增查询必须用新 id，旧基线保留。
    /// </summary>
    private static readonly string[] FixedQueries =
    {
        "\u7231\u4F60",              // 爱你
        "\u6211\u597D\u7231\u4F60",  // 我好爱你
        "\u611B",                    // 愛（繁体，查询侧归一）
        "\u6211\u7231\u4F60",        // 我爱你（拼音支 + 首字母支 wan）
        "woaini",                    // 连写拼音
        "wo",                        // 单音节拼音前缀
        "wan",                       // 首字母
        "love",                      // 拉丁前缀
        "hello",                     // 拉丁整词
        "zongguo",                   // 平翘舌模糊音
        "kurarin",                   // 拉丁 → 日文读音
        "helo",                      // 漏字母
        "hlelo",                     // 相邻换位
        "\u4F26\u5468",              // 伦周（中文换位）
    };

    /// <summary>回放集里的一条查询（id 是基线结果字典的键）。</summary>
    private sealed record OutputQuery(
        string Id, string Query, string Scope, int Limit, string Source, int Weight, IReadOnlyList<string> Tags);

    /// <summary>日志聚合后的候选（<see cref="Weight"/> 为去重后的出现频次）。</summary>
    private sealed record SampleQuery(string Query, string Scope, int Weight, bool Derived = false);

    /// <summary>日志解析结果：候选集 + 被丢弃的行数（坏行 / 空串 / 纯空白 / 超长）。</summary>
    private sealed record SampleInput(List<SampleQuery> Candidates, int Dropped);

    private static int RunSample(string[] args)
    {
        string? logPath = null;
        string? outPath = null;
        var replayCount = 200;
        var limit = 20;
        long seed = 20261013;
        string? libraryPath = null;
        var withLyrics = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log":
                    if (i + 1 >= args.Length) return Fail("--log 缺少路径");
                    logPath = args[++i];
                    break;
                case "--library":
                    if (i + 1 >= args.Length) return Fail("--library 缺少路径");
                    libraryPath = args[++i];
                    break;
                case "--with-lyrics": withLyrics = true; break;
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out 缺少路径");
                    outPath = args[++i];
                    break;
                case "--replay":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var replayValue)
                        || replayValue <= 0)
                        return Fail("--replay 需正整数");
                    replayCount = replayValue;
                    break;
                case "--limit":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limitValue)
                        || limitValue <= 0)
                        return Fail("--limit 需正整数");
                    limit = limitValue;
                    break;
                case "--seed":
                    if (i + 1 >= args.Length
                        || !long.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seedValue))
                        return Fail("--seed 需整数");
                    seed = seedValue;
                    break;
                case "-h" or "--help":
                    return PrintHelp();
                default:
                    return Fail($"未知参数: {args[i]}");
            }
        }

        if (logPath is null) return Fail("缺少 --log <queries-log.jsonl>");
        if (!File.Exists(logPath)) return Fail($"查询日志不存在: {logPath}");
        if (outPath is null) return Fail("缺少 --out <queries.jsonl>");
        if (libraryPath is not null && !File.Exists(libraryPath))
            return Fail($"曲库不存在: {libraryPath}");

        SampleInput input;
        try
        {
            input = LoadQueryLog(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"读取查询日志失败: {ex.Message}");
        }

        // 固定条目永远在前，按固定 id（Q0001–Q0014）。
        var fixedEntries = new List<OutputQuery>(FixedQueries.Length);
        for (var i = 0; i < FixedQueries.Length; i++)
        {
            var query = FixedQueries[i].Trim();
            fixedEntries.Add(new OutputQuery(
                "Q" + (i + 1).ToString("0000", CultureInfo.InvariantCulture),
                query, "all", limit, "fixed", 1, TagsOf(query)));
        }

        // 候选里排除与固定 14 条（trim 后）完全相同的查询，避免同一文本出现两次。
        var fixedSet = new HashSet<string>(fixedEntries.Select(entry => entry.Query), StringComparer.Ordinal);
        var pool = input.Candidates
            .Where(candidate => !fixedSet.Contains(candidate.Query))
            .ToList();

        // §6.7.1「日志不足时用 bench 语料派生查询补齐」：日志候选不够时，从 library.db
        // 按 sha256(track_id) 顺序派生标题 / 文件名 / 歌手 / 专辑（可选歌词行）查询。
        var derivedCount = 0;
        if (pool.Count < replayCount && libraryPath is not null)
        {
            var seen = new HashSet<string>(
                pool.Select(candidate => candidate.Scope + "\u001f" + candidate.Query),
                StringComparer.Ordinal);
            try
            {
                foreach (var derived in DeriveQueries(libraryPath, includeLyrics: withLyrics))
                {
                    if (pool.Count >= replayCount) break;
                    if (fixedSet.Contains(derived)) continue;
                    var key = "all\u001f" + derived;
                    if (!seen.Add(key)) continue;
                    pool.Add(new SampleQuery(derived, "all", 1, Derived: true));
                    derivedCount++;
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                return Fail($"从曲库派生查询失败: {ex.Message}");
            }
        }

        if (pool.Count == 0)
        {
            return Fail(libraryPath is null
                ? $"查询日志没有有效查询: {logPath}"
                : $"查询日志与曲库都没有可派生查询: {logPath} / {libraryPath}");
        }

        // Efraimidis–Spirakis 加权无放回抽样：key = -ln(u)/weight，取最小的 n 条。
        // u 只由 (seed, scope, query) 的 SHA-256 派生，不用 System.Random（跨版本可能漂移）。
        var selected = pool
            .Select(candidate => (Candidate: candidate, Key: SampleKey(seed, candidate.Scope, candidate.Query, candidate.Weight)))
            .OrderBy(entry => entry.Key)
            .ThenBy(entry => entry.Candidate.Scope, StringComparer.Ordinal)
            .ThenBy(entry => entry.Candidate.Query, StringComparer.Ordinal)
            .Take(Math.Min(replayCount, pool.Count))
            .Select(entry => entry.Candidate)
            .ToList();

        // 输出顺序与抽取顺序解耦：回放按 (scope, query) 序数排序后再编号，保证 diff 稳定。
        selected.Sort((left, right) =>
        {
            var scope = string.CompareOrdinal(left.Scope, right.Scope);
            return scope != 0 ? scope : string.CompareOrdinal(left.Query, right.Query);
        });

        var replayEntries = new List<OutputQuery>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var candidate = selected[i];
            replayEntries.Add(new OutputQuery(
                "Q" + (i + 101).ToString("0000", CultureInfo.InvariantCulture),
                candidate.Query, candidate.Scope, limit, "replay", candidate.Weight,
                TagsOf(candidate.Query, candidate.Derived)));
        }

        var entries = fixedEntries.Concat(replayEntries).ToList();
        var queryHash = QueriesHash(entries.Select(entry => (entry.Id, entry.Query, entry.Scope)));

        var fullOut = Path.GetFullPath(outPath);
        var outDir = Path.GetDirectoryName(fullOut);
        try
        {
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            // 每行一条、行尾 LF、无 BOM：同输入 + 同 seed 必须字节级一致。
            var text = string.Join('\n', entries.Select(WriteQueryLine)) + "\n";
            File.WriteAllBytes(fullOut, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"写出查询集失败: {ex.Message}");
        }

        Console.WriteLine(WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("fixed", fixedEntries.Count);
            w.WriteNumber("replay", replayEntries.Count);
            w.WriteNumber("derived", derivedCount);
            w.WriteNumber("dropped", input.Dropped);
            w.WriteString("hash", queryHash);
            w.WriteString("out", fullOut);
            w.WriteEndObject();
        }));
        return 0;
    }

    /// <summary>
    /// 从曲库**确定性派生**回放查询（§6.7.1「日志不足时用 bench 语料派生查询补齐」）：
    /// 按 <c>sha256(track_id)</c> 升序挑曲目，依次产标题 / 文件名 / 歌手 / 专辑 / 标题前缀 /
    /// 首行歌词（可选）。只读 <c>library.db</c>；派生结果只写调用方的 <c>--out</c>（本地），
    /// 不进仓库，也不上传。
    /// </summary>
    private static IEnumerable<string> DeriveQueries(string libraryPath, bool includeLyrics)
    {
        var rows = new List<DerivedRow>();
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryPath, readOnly: true));
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, artists, album, path FROM tracks";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new DerivedRow(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4)));
            }
        }

        foreach (var row in rows.OrderBy(item => Sha256Hex(item.Id), StringComparer.Ordinal))
        {
            var title = NormalizeQuery(row.Title);
            if (title is not null) yield return title;

            var filename = NormalizeQuery(Path.GetFileNameWithoutExtension(row.Path));
            if (filename is not null && !string.Equals(filename, title, StringComparison.Ordinal))
                yield return filename;

            var artist = NormalizeQuery(ParseNameList(row.Artists));
            if (artist is not null) yield return artist;

            var album = NormalizeQuery(ParseNameList(row.Album));
            if (album is not null) yield return album;

            // 标题 ≥4 个 Unicode 标量时取前 3 个：模拟「边打边搜」的短前缀查询。
            if (title is not null && ScalarCount(title) >= 4)
            {
                var prefix = FirstScalars(title, 3);
                if (!string.Equals(prefix, title, StringComparison.Ordinal)) yield return prefix;
            }

            if (includeLyrics)
            {
                var line = FirstLyricLine(conn, row.Id);
                if (line is not null) yield return line;
            }
        }
    }

    /// <summary>派生用的一行元数据（不含歌词，避免把 50k 首歌词一次性读进内存）。</summary>
    private readonly record struct DerivedRow(
        string Id, string Title, string Artists, string Album, string Path);

    /// <summary>取该曲歌词三层里的原文首行（剥 LRC 标签、trim 后非空且 ≤200）。</summary>
    private static string? FirstLyricLine(SqliteConnection conn, string trackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT lyrics FROM tracks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", trackId);
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull) return null;

        var layered = LrcReader.SplitText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        foreach (var line in layered.Original.Split('\n'))
        {
            var candidate = NormalizeQuery(line);
            if (candidate is not null) return candidate;
        }
        return null;
    }

    /// <summary>trim 后 1..200 字符才可进查询集；空 / 超长返回 null。</summary>
    private static string? NormalizeQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.Length > MaxQueryLength ? null : trimmed;
    }

    /// <summary>曲目 artists 列是 JSON 数组（<c>[{"name":"…"}]</c>），album 是对象或字符串。</summary>
    private static string ParseNameList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadName(document.RootElement);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static string ReadName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Object => element.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? ""
                : "",
        JsonValueKind.Array => string.Join(' ', element.EnumerateArray()
            .Select(ReadName)
            .Where(value => value.Length > 0)),
        _ => "",
    };

    private static int ScalarCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>取前 <paramref name="count"/> 个 Unicode 标量（按码点切，不切碎代理对）。</summary>
    private static string FirstScalars(string text, int count)
    {
        var builder = new StringBuilder(text.Length);
        var taken = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken++ >= count) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>
    /// 读桌面端查询日志（<c>app/lib/services/search/query_log.dart</c>）：
    /// <c>q</c> / <c>query</c> 为查询串，<c>sc=m</c> 或 <c>scope=metadata</c> 为 metadata 范围；
    /// <c>ms</c> / <c>n</c> / <c>p</c> 只为兼容而容忍，不参与抽样。
    ///
    /// 逐行容错：坏行（JSON 解析失败 / 非对象 / 缺查询串）跳过并计入 dropped；
    /// 空串、纯空白、长度 &gt; 200 的查询丢弃；其余按 trim 后的 (query, scope) 去重并累计频次。
    /// </summary>
    private static SampleInput LoadQueryLog(string path)
    {
        var counts = new Dictionary<(string Query, string Scope), int>();
        var dropped = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                dropped++;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    dropped++;
                    continue;
                }

                var query = ReadString(root, "query") ?? ReadString(root, "q");
                if (query is null || query.Length > MaxQueryLength)
                {
                    dropped++;
                    continue;
                }

                var trimmed = query.Trim();
                if (trimmed.Length == 0)
                {
                    dropped++;
                    continue;
                }

                var metadata = string.Equals(ReadString(root, "scope"), "metadata", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadString(root, "sc"), "m", StringComparison.OrdinalIgnoreCase);
                var scope = metadata ? "metadata" : "all";

                counts.TryGetValue((trimmed, scope), out var weight);
                counts[(trimmed, scope)] = weight + 1;
            }
        }

        var candidates = counts
            .Select(pair => new SampleQuery(pair.Key.Query, pair.Key.Scope, pair.Value))
            .OrderBy(candidate => candidate.Scope, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Query, StringComparer.Ordinal)
            .ToList();
        return new SampleInput(candidates, dropped);
    }

    /// <summary>
    /// Efraimidis–Spirakis 加权抽样 key：<c>-ln(u)/weight</c>，越小越先被抽中。
    /// u 由 <c>sha256(seed \x1f scope \x1f query)</c> 的前 8 字节（大端 uint64 + 1）除以 2^64 得到，
    /// 恒在 (0, 1]，既避免 <c>ln(0)</c>，又保证同 seed 同输入跨进程 / 跨版本完全可复现。
    /// </summary>
    private static double SampleKey(long seed, string scope, string query, int weight)
    {
        var keyText = seed.ToString(CultureInfo.InvariantCulture) + "\u001f" + scope + "\u001f" + query;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(keyText));
        var raw = BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(0, 8));
        var u = ((double)raw + 1.0) / 18446744073709551616.0; // 2^64
        return -Math.Log(u) / weight;
    }

    /// <summary>
    /// 只从文本推导 tags：<c>cjk</c>（汉字 / 假名 / 谚文）、<c>latin</c>（拉丁字母）、
    /// <c>mixed</c>（两者都有）、<c>single-char</c>（trim 后 1 个 Unicode 标量）。
    /// <c>lyrics</c> / <c>typo</c> / <c>title</c> 无法从查询串可靠推导，不臆造；
    /// 从曲库派生的条目额外标 <c>derived</c>（§6.7.1「日志不足 → bench 语料补齐」）。
    /// </summary>
    private static List<string> TagsOf(string query, bool derived = false)
    {
        var hasCjk = false;
        var hasLatin = false;
        foreach (var ch in query)
        {
            if (ScriptRanges.IsCjk(ch) || ScriptRanges.IsSubstringScript(ch)) hasCjk = true;
            else if (ScriptRanges.IsLatinLetter(ch)) hasLatin = true;
        }

        var scalars = 0;
        foreach (var _ in query.EnumerateRunes()) scalars++;

        var tags = new List<string>(4);
        if (hasCjk) tags.Add("cjk");
        if (hasLatin) tags.Add("latin");
        if (hasCjk && hasLatin) tags.Add("mixed");
        if (scalars == 1) tags.Add("single-char");
        if (derived) tags.Add("derived");
        return tags;
    }

    /// <summary>把一条查询写成 §6.7.1 结构的一行 JSON（键顺序固定）。</summary>
    private static string WriteQueryLine(OutputQuery entry)
        => WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("id", entry.Id);
            w.WriteString("query", entry.Query);
            w.WriteString("scope", entry.Scope);
            w.WriteNumber("limit", entry.Limit);
            w.WriteString("total", "none");
            w.WriteString("source", entry.Source);
            w.WriteNumber("weight", entry.Weight);
            w.WritePropertyName("tags");
            w.WriteStartArray();
            foreach (var tag in entry.Tags) w.WriteStringValue(tag);
            w.WriteEndArray();
            w.WriteEndObject();
        });

    /* ------------------------------------------------------------------ */
    /* replay corpus                                                       */
    /* ------------------------------------------------------------------ */

    private static int BuildCorpus(string[] args)
    {
        string? libraryPath = null;
        string? outPath = null;
        int? trackCount = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--library" when i + 1 < args.Length: libraryPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--tracks" when i + 1 < args.Length && int.TryParse(args[++i], out var n) && n > 0:
                    trackCount = n;
                    break;
                case "-h" or "--help":
                    return PrintHelp();
            }
        }

        if (libraryPath is null || !File.Exists(libraryPath))
            return Fail($"曲库不存在: {libraryPath ?? "(未指定 --library)"}");
        if (trackCount is null)
            return Fail("缺少 / 非法的 --tracks <n>（需正整数）");
        if (outPath is null)
            return Fail("缺少 --out <subset.db>");

        try
        {
            var ids = ReadTrackIds(libraryPath);
            if (ids.Count == 0) return Fail($"曲库没有曲目: {libraryPath}");

            // 选片与排序都只用 sha256(track_id)：同一 id 集合在任何机器上都得到同一子集。
            var selected = ids
                .Select(id => (Id: id, Digest: Sha256Hex(id)))
                .OrderBy(entry => entry.Digest, StringComparer.Ordinal)
                .Take(trackCount.Value)
                .ToList();
            var corpusHash = Sha256Hex(string.Join('\n', selected.Select(entry => entry.Id)));

            var fullOut = Path.GetFullPath(outPath);
            var outDir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            // 覆盖写：连 WAL 侧车一起清掉，避免旧日志回放到新库。
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var candidate = fullOut + suffix;
                if (File.Exists(candidate)) File.Delete(candidate);
            }

            var ddl = ReadTracksDdl(libraryPath);
            var actual = WriteSubset(libraryPath, fullOut, ddl, selected.Select(entry => entry.Id).ToList());

            var json = WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("corpus", "subset");
                w.WriteNumber("tracks", actual);
                w.WriteString("hash", corpusHash);
                w.WriteString("out", fullOut);
                w.WriteEndObject();
            });
            Console.WriteLine(json);
            File.WriteAllText(fullOut + ".corpus.json", json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return 0;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail($"生成子集失败: {ex.Message}");
        }
    }

    /// <summary>只读打开曲库，读出全部非空 <c>tracks.id</c>。</summary>
    private static List<string> ReadTrackIds(string libraryPath)
    {
        var ids = new List<string>();
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryPath, readOnly: true));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM tracks";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var id = reader.GetString(0);
            if (id.Length > 0) ids.Add(id);
        }
        return ids;
    }

    /// <summary>取原库 <c>CREATE TABLE tracks</c> 的 DDL（不复制索引 / 触发器）。</summary>
    private static string ReadTracksDdl(string libraryPath)
    {
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryPath, readOnly: true));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'tracks'";
        var ddl = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(ddl))
            throw new InvalidOperationException("曲库缺少 tracks 表定义");
        return ddl;
    }

    /// <summary>
    /// 建 <paramref name="outPath"/> 的 tracks 表，并把选中的曲目行从只读源库逐行搬入；返回实际行数。
    ///
    /// 不走 <c>ATTACH</c>：SQLite 会把主库的只读标志**传播给附加库**，只读源库下的
    /// 附加库无法建表 / 写入（实测 `attempt to write a readonly database`）。因此保持
    /// 源库只读连接、目标库可写连接，按同一批 id 分块 <c>SELECT *</c> → 参数化
    /// <c>INSERT</c>；两张表 DDL 完全一致，列序相同。
    /// </summary>
    private static long WriteSubset(string libraryPath, string outPath, string sourceDdl, List<string> ids)
    {
        var openParen = sourceDdl.IndexOf('(');
        if (openParen < 0) throw new InvalidOperationException("tracks 表 DDL 缺少列定义");
        // 只保留列定义部分，表名统一为 tracks（新建的独立库，无需前缀）。
        var createSql = "CREATE TABLE tracks " + sourceDdl[openParen..];

        using var outConn = new SqliteConnection(LibraryDb.ConnectionString(outPath));
        outConn.Open();
        using (var create = outConn.CreateCommand())
        {
            create.CommandText = createSql;
            create.ExecuteNonQuery();
        }

        using var srcConn = new SqliteConnection(LibraryDb.ConnectionString(libraryPath, readOnly: true));
        srcConn.Open();

        // 列名取自源库 tracks；SELECT * 的列序与 pragma_table_info 的 cid 序一致。
        var columns = ReadColumnNames(srcConn);
        if (columns.Count == 0) throw new InvalidOperationException("tracks 表没有任何列");
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var valueList = string.Join(", ", Enumerable.Range(0, columns.Count)
            .Select(i => "@p" + i.ToString(CultureInfo.InvariantCulture)));

        using var insert = outConn.CreateCommand();
        insert.CommandText = $"INSERT INTO tracks ({columnList}) VALUES ({valueList})";
        for (var i = 0; i < columns.Count; i++)
            insert.Parameters.Add(new SqliteParameter("@p" + i.ToString(CultureInfo.InvariantCulture), null));

        long inserted = 0;
        using var tx = outConn.BeginTransaction();
        insert.Transaction = tx;

        for (var start = 0; start < ids.Count; start += InsertBatchSize)
        {
            var count = Math.Min(InsertBatchSize, ids.Count - start);
            using var select = srcConn.CreateCommand();
            var parameters = new string[count];
            for (var i = 0; i < count; i++)
            {
                parameters[i] = "@id" + i.ToString(CultureInfo.InvariantCulture);
                select.Parameters.AddWithValue(parameters[i], ids[start + i]);
            }
            select.CommandText = $"SELECT * FROM tracks WHERE id IN ({string.Join(',', parameters)})";

            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                for (var col = 0; col < insert.Parameters.Count; col++)
                    insert.Parameters[col].Value = reader.GetValue(col);
                insert.ExecuteNonQuery();
                inserted++;
            }
        }

        tx.Commit();
        return inserted;
    }

    private static List<string> ReadColumnNames(SqliteConnection conn)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('tracks') ORDER BY cid";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /* ------------------------------------------------------------------ */
    /* replay compare                                                      */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// overlap@10 的固定分母（§6.7.5：<c>|A₁₀ ∩ B₁₀| / 10</c>；列表不足 10 条时**仍除以 10**）。
    /// </summary>
    private const int OverlapDepth = 10;

    /// <summary>一份 §6.7.4 基线里的一条查询结果（只有 <c>ids</c> 参与比较）。</summary>
    private sealed record BaselineResult(string Id, string Query, IReadOnlyList<string> Ids);

    /// <summary>一份 §6.7.4 基线的只读视图；<see cref="Results"/> 保持文件里的顺序。</summary>
    private sealed record BaselineFile(string QueriesHash, IReadOnlyList<BaselineResult> Results);

    /// <summary>一条 id 的名次变化（1-based；0 = 不在该侧结果的 ids 里）。</summary>
    private sealed record RankChange(string Id, int OldRank, int NewRank);

    /// <summary>单条查询的 diff（§6.7.5 精简版：ids / 名次变化 / 空态 / overlap@10）。</summary>
    private sealed record QueryDiff(
        string Id,
        string Query,
        IReadOnlyList<string> OldTop,
        IReadOnlyList<string> NewTop,
        double Overlap,
        IReadOnlyList<RankChange> Changed,
        bool EmptyOld,
        bool EmptyNew);

    /// <summary>
    /// <c>replay compare</c>：比较两份 §6.7.4 基线 JSON，输出 diff（§6.7.5）并给出 S1 门禁退出码。
    ///
    /// 只读两份 JSON（不碰查询日志 / 曲库）；比较的是**完整 ids 数组**（不只是前 10），
    /// 前 10 只用于 <c>overlap@10</c> 与报告里的 <c>oldTop</c> / <c>newTop</c>。
    ///
    /// 退出码：完全一致 0；任何 ids / 顺序 / 空态差异 1（S1 要求 <c>changed</c> 为空）；
    /// 参数或数据错误也 1（stderr 说明原因，stdout 不输出假的 diff）。
    /// </summary>
    private static int CompareRuns(string[] args)
    {
        string? baselinePath = null;
        string? runPath = null;
        string? outPath = null;
        var allowSubset = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--baseline" when i + 1 < args.Length: baselinePath = args[++i]; break;
                case "--run" when i + 1 < args.Length: runPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--allow-subset": allowSubset = true; break;
                case "-h" or "--help": return PrintHelp();
                default: return Fail($"未知参数: {args[i]}");
            }
        }

        if (baselinePath is null) return Fail("缺少 --baseline <baseline.json>");
        if (runPath is null) return Fail("缺少 --run <run.json>");
        if (!File.Exists(baselinePath)) return Fail($"基线不存在: {baselinePath}");
        if (!File.Exists(runPath)) return Fail($"待比较结果不存在: {runPath}");

        BaselineFile baseline;
        BaselineFile run;
        try
        {
            baseline = LoadBaseline(baselinePath);
            run = LoadBaseline(runPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail($"读取基线失败: {ex.Message}");
        }

        var baselineById = new Dictionary<string, BaselineResult>(baseline.Results.Count, StringComparer.Ordinal);
        foreach (var result in baseline.Results) baselineById[result.Id] = result;
        var runById = new Dictionary<string, BaselineResult>(run.Results.Count, StringComparer.Ordinal);
        foreach (var result in run.Results) runById[result.Id] = result;

        var onlyBaseline = baseline.Results.Select(result => result.Id).Where(id => !runById.ContainsKey(id)).ToList();
        var onlyRun = run.Results.Select(result => result.Id).Where(id => !baselineById.ContainsKey(id)).ToList();
        var sameSet = onlyBaseline.Count == 0 && onlyRun.Count == 0;

        List<string> compareIds;
        if (sameSet)
        {
            // 同集时 hash 必须相等：hash 由 (id, query, scope) 决定，相等才能保证「同查询集」。
            if (!string.Equals(baseline.QueriesHash, run.QueriesHash, StringComparison.Ordinal))
                return Fail($"queries.hash 不同: {baseline.QueriesHash} vs {run.QueriesHash}（查询集不同不可比）");
            compareIds = baseline.Results.Select(result => result.Id).ToList();
        }
        else if (!allowSubset)
        {
            return Fail(
                $"查询集不同: 基线独有 {onlyBaseline.Count.ToString(CultureInfo.InvariantCulture)} 条、"
                + $"结果独有 {onlyRun.Count.ToString(CultureInfo.InvariantCulture)} 条"
                + $"{DescribeIds(onlyBaseline, onlyRun)}（查询集不同不可比；手工裁剪请加 --allow-subset）");
        }
        else
        {
            // --allow-subset 只给手工裁剪用：**只比较交集**，交集为空即报错。
            // 裁剪后 queries.hash 必然不同（hash 覆盖全部 id/query/scope），所以这里不要求
            // hash 相等，而是把两边 hash 写进日志，并把跳过条数写在汇总里，避免「悄悄少了查询」。
            compareIds = baseline.Results.Select(result => result.Id).Where(runById.ContainsKey).ToList();
            if (compareIds.Count == 0)
                return Fail("--allow-subset: 两份结果没有共同查询，无可比较内容");
            Log.LogWarn("replay",
                $"--allow-subset: 只比较交集 {compareIds.Count.ToString(CultureInfo.InvariantCulture)} 条"
                + $"（跳过基线独有 {onlyBaseline.Count.ToString(CultureInfo.InvariantCulture)} 条 / "
                + $"结果独有 {onlyRun.Count.ToString(CultureInfo.InvariantCulture)} 条）；"
                + $"hash {baseline.QueriesHash} vs {run.QueriesHash}");
        }

        var diffs = new List<QueryDiff>(compareIds.Count);
        foreach (var id in compareIds)
        {
            var oldResult = baselineById[id];
            var newResult = runById[id];
            // 同集时 hash 已经保证 (query, scope) 一致；子集模式下退化为逐条文本校验（有则校验）。
            if (oldResult.Query.Length > 0 && newResult.Query.Length > 0
                && !string.Equals(oldResult.Query, newResult.Query, StringComparison.Ordinal))
            {
                return Fail($"查询文本不同: {id} 「{oldResult.Query}」 vs 「{newResult.Query}」（查询集不可比）");
            }

            var query = oldResult.Query.Length > 0 ? oldResult.Query : newResult.Query;
            diffs.Add(BuildDiff(id, query, oldResult, newResult));
        }

        var fullOut = outPath is null ? null : Path.GetFullPath(outPath);
        var report = RenderDiff(diffs, baseline.QueriesHash, includeResults: true, outPath: fullOut);
        if (fullOut is null)
        {
            Console.WriteLine(report);
        }
        else
        {
            try
            {
                var outDir = Path.GetDirectoryName(fullOut);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                File.WriteAllBytes(fullOut, Encoding.UTF8.GetBytes(report));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Fail($"写出 diff 失败: {ex.Message}");
            }
            // stdout 始终有一条汇总 JSON（含 aggregate），便于 CI 直接抓取；完整报告落在 --out。
            Console.WriteLine(RenderDiff(diffs, baseline.QueriesHash, includeResults: false, outPath: fullOut));
        }

        return IsIdentical(diffs) ? 0 : 1;
    }

    /// <summary>读一份 §6.7.4 基线：校验 <c>schema == 1</c> / <c>results</c> / <c>queries.hash</c> 与每条 <c>ids</c>。</summary>
    private static BaselineFile LoadBaseline(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: 顶层必须是 JSON 对象（§6.7.4 基线）");
        if (!root.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var version)
            || version != 1)
        {
            throw new InvalidDataException($"{path}: schema 必须为 1（§6.7.4 基线）");
        }
        if (!root.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: 缺少 queries 对象");
        var hash = ReadString(queries, "hash");
        if (string.IsNullOrEmpty(hash))
            throw new InvalidDataException($"{path}: queries.hash 缺失（无法判定查询集是否可比）");
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: 缺少 results 对象");

        var list = new List<BaselineResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in results.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"{path}: results.{property.Name} 重复（基线 id 不可复用）");

            var entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{path}: results.{property.Name} 必须是对象");
            if (!entry.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path}: results.{property.Name} 缺少 ids 数组");

            var values = new List<string>();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"{path}: results.{property.Name}.ids 含非字符串元素");
                values.Add(id.GetString() ?? "");
            }
            list.Add(new BaselineResult(property.Name, ReadString(entry, "query") ?? "", values));
        }

        if (list.Count == 0)
            throw new InvalidDataException($"{path}: results 为空（没有可比较的查询）");
        return new BaselineFile(hash, list);
    }

    /// <summary>逐条比较：完整 ids 数组的名次变化 + 前 10 的 overlap + 空态。</summary>
    private static QueryDiff BuildDiff(string id, string query, BaselineResult oldResult, BaselineResult newResult)
    {
        var oldRanks = RankMap(oldResult.Ids);
        var newRanks = RankMap(newResult.Ids);

        // 并集按「旧序优先、新序补尾」展开，保证 changed 输出顺序稳定。
        var union = new List<string>(oldRanks.Count + newRanks.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trackId in oldResult.Ids) if (seen.Add(trackId)) union.Add(trackId);
        foreach (var trackId in newResult.Ids) if (seen.Add(trackId)) union.Add(trackId);

        var changed = new List<RankChange>();
        foreach (var trackId in union)
        {
            var oldRank = oldRanks.TryGetValue(trackId, out var parsedOld) ? parsedOld : 0;
            var newRank = newRanks.TryGetValue(trackId, out var parsedNew) ? parsedNew : 0;
            if (oldRank != newRank) changed.Add(new RankChange(trackId, oldRank, newRank));
        }
        changed.Sort(CompareRankChange);

        return new QueryDiff(
            id,
            query,
            oldResult.Ids.Take(OverlapDepth).ToList(),
            newResult.Ids.Take(OverlapDepth).ToList(),
            OverlapAt10(oldResult.Ids, newResult.Ids),
            changed,
            oldResult.Ids.Count == 0,
            newResult.Ids.Count == 0);
    }

    /// <summary>名次表：1-based，同一 id 重复出现时只认第一次（最靠前的名次）。</summary>
    private static Dictionary<string, int> RankMap(IReadOnlyList<string> ids)
    {
        var ranks = new Dictionary<string, int>(ids.Count, StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++) ranks.TryAdd(ids[i], i + 1);
        return ranks;
    }

    /// <summary>按 (oldRank, newRank, id) 排序；0（不在列表）排在最后。</summary>
    private static int CompareRankChange(RankChange left, RankChange right)
    {
        var byOld = RankKey(left.OldRank).CompareTo(RankKey(right.OldRank));
        if (byOld != 0) return byOld;
        var byNew = RankKey(left.NewRank).CompareTo(RankKey(right.NewRank));
        return byNew != 0 ? byNew : string.CompareOrdinal(left.Id, right.Id);
    }

    private static int RankKey(int rank) => rank == 0 ? int.MaxValue : rank;

    /// <summary>
    /// <c>|A₁₀ ∩ B₁₀| / 10</c>（§6.7.5；分母固定 10，列表不足 10 条也不缩放）。
    /// 两边都为空视作完全一致（返回 1.0）：空结果集合相等不应被判成「排序回退」。
    /// </summary>
    private static double OverlapAt10(IReadOnlyList<string> oldIds, IReadOnlyList<string> newIds)
    {
        if (oldIds.Count == 0 && newIds.Count == 0) return 1.0;

        var oldTop = new HashSet<string>(oldIds.Take(OverlapDepth), StringComparer.Ordinal);
        var shared = 0;
        foreach (var id in newIds.Take(OverlapDepth).Distinct(StringComparer.Ordinal))
        {
            if (oldTop.Contains(id)) shared++;
        }
        return shared / (double)OverlapDepth;
    }

    /// <summary>S1 等价门禁：没有任何名次变化，且空态一一对应。</summary>
    private static bool IsIdentical(IReadOnlyList<QueryDiff> diffs)
        => diffs.Sum(diff => diff.Changed.Count) == 0 && diffs.All(diff => diff.EmptyOld == diff.EmptyNew);

    /// <summary>
    /// 渲染 diff JSON。`includeResults=false` 时只出汇总（<c>schema</c> / <c>queries</c> / <c>aggregate</c>），
    /// 便于把完整报告写进 <c>--out</c> 时 stdout 只留一行汇总。
    /// </summary>
    private static string RenderDiff(
        IReadOnlyList<QueryDiff> diffs, string queriesHash, bool includeResults, string? outPath)
    {
        var changed = diffs.Sum(diff => diff.Changed.Count);
        var emptyOld = diffs.Count(diff => diff.EmptyOld);
        var emptyNew = diffs.Count(diff => diff.EmptyNew);
        var meanOverlap = diffs.Count == 0 ? 1.0 : diffs.Average(diff => diff.Overlap);
        var identical = IsIdentical(diffs);

        return WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);

            w.WritePropertyName("queries");
            w.WriteStartObject();
            w.WriteNumber("count", diffs.Count);
            w.WriteString("hash", queriesHash);
            w.WriteEndObject();

            w.WritePropertyName("aggregate");
            w.WriteStartObject();
            w.WriteNumber("changed", changed);
            w.WriteNumber("emptyOld", emptyOld);
            w.WriteNumber("emptyNew", emptyNew);
            WriteRatio(w, "overlap@10", meanOverlap);
            w.WriteBoolean("identical", identical);
            w.WriteEndObject();

            if (includeResults)
            {
                w.WritePropertyName("results");
                w.WriteStartObject();
                foreach (var diff in diffs)
                {
                    w.WritePropertyName(diff.Id);
                    w.WriteStartObject();
                    w.WriteString("query", diff.Query);
                    WriteStringArray(w, "oldTop", diff.OldTop);
                    WriteStringArray(w, "newTop", diff.NewTop);
                    WriteRatio(w, "overlap@10", diff.Overlap);

                    w.WritePropertyName("changed");
                    w.WriteStartArray();
                    foreach (var change in diff.Changed)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", change.Id);
                        w.WriteNumber("oldRank", change.OldRank);
                        w.WriteNumber("newRank", change.NewRank);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    w.WriteBoolean("emptyOld", diff.EmptyOld);
                    w.WriteBoolean("emptyNew", diff.EmptyNew);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }

            if (outPath is not null) w.WriteString("out", outPath);
            w.WriteEndObject();
        });
    }

    /// <summary>写 0..1 的比例：4 位小数、去掉尾随 0（<c>1</c> / <c>0.9</c> / <c>0.6667</c>）。</summary>
    private static void WriteRatio(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(Math.Round(value, 4, MidpointRounding.AwayFromZero)
            .ToString("0.####", CultureInfo.InvariantCulture));
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    /// <summary>错误信息里点名前几条不同集 id，避免刷屏。</summary>
    private static string DescribeIds(IReadOnlyList<string> baselineOnly, IReadOnlyList<string> runOnly)
    {
        const int SampleLimit = 5;
        var parts = new List<string>(2);
        if (baselineOnly.Count > 0)
            parts.Add("基线独有: " + string.Join(',', baselineOnly.Take(SampleLimit)));
        if (runOnly.Count > 0)
            parts.Add("结果独有: " + string.Join(',', runOnly.Take(SampleLimit)));
        return parts.Count == 0 ? "" : "（" + string.Join("；", parts) + "）";
    }

    /* ------------------------------------------------------------------ */
    /* 公共小工具                                                          */
    /* ------------------------------------------------------------------ */

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>中位数（偶数个取中间两者平均）。</summary>
    private static double Median(double[] samples)
    {
        if (samples.Length == 0) return 0.0;
        Array.Sort(samples);
        var mid = samples.Length / 2;
        return samples.Length % 2 == 1 ? samples[mid] : (samples[mid - 1] + samples[mid]) / 2.0;
    }

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string WriteJson(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            body(writer);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static int Fail(string message)
    {
        Log.LogError("replay", message);
        return 1;
    }
}
