// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>
/// S0 回放 harness：把「查询集 + 语料」固化成可复现的基线，供 S1 行为等价重构做 before/after 对比
/// （设计文档 §6.7）。
///
/// 两个子命令：
/// <list type="bullet">
///   <item><c>run</c>：读 JSONL 查询集，逐条走生产查询路径 <see cref="SearchSession"/>，
///         输出 §6.7.4 结构的基线 JSON（每条只留前 100 个 trackId）。</item>
///   <item><c>corpus</c>：按 <c>sha256(track_id)</c> 升序从真实库切前 N 首，生成可复现的子集库
///         与 corpus hash（§6.7.3）。</item>
/// </list>
///
/// stdout：结果 JSON；stderr：人类日志与错误。exit code：0=成功，1=参数 / 运行错误。
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

        if (indexPath is null || !File.Exists(indexPath))
            return Fail($"索引不存在: {indexPath ?? "(未指定 --index)"}");
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
            w.WriteString("scheme", "v7");
            w.WriteBoolean("lyricsPinyin", true);
            w.WriteBoolean("lyricsInitials", false);
            w.WriteEndObject();

            w.WritePropertyName("index");
            w.WriteStartObject();
            w.WriteNumber("fileBytes", new FileInfo(indexPath).Length);
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
    {
        var rows = cases
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id + "\t" + c.Query + "\t" + (c.Metadata ? "metadata" : "all"));
        return Sha256Hex(string.Join('\n', rows));
    }

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
        using var conn = new SqliteConnection(FtsStore.ConnectionString(libraryPath, readOnly: true));
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
        using var conn = new SqliteConnection(FtsStore.ConnectionString(libraryPath, readOnly: true));
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

        using var outConn = new SqliteConnection(FtsStore.ConnectionString(outPath));
        outConn.Open();
        using (var create = outConn.CreateCommand())
        {
            create.CommandText = createSql;
            create.ExecuteNonQuery();
        }

        using var srcConn = new SqliteConnection(FtsStore.ConnectionString(libraryPath, readOnly: true));
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
