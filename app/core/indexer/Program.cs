// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer;

/// <summary>
/// Archoera 本地曲库搜索索引器 CLI。
///
/// 用法：
///   archoera-indexer build [--full] [--index-lrc] [--no-pinyin] [--romaji-lyrics]
///                          [--db &lt;library.db&gt;] [--index &lt;search.db&gt;] [--batch &lt;n&gt;]
///   archoera-indexer query &lt;text&gt; [--index &lt;search.db&gt;] [--limit &lt;n&gt;] [--raw] [--session]
///   archoera-indexer analyze &lt;field&gt; &lt;text&gt;
///   archoera-indexer dump   --index &lt;目录或.eraindex&gt; [--field &lt;1..12|0x01&gt;]
///                           [--prefix &lt;文本&gt;] [--limit &lt;n&gt;]
///   archoera-indexer verify --index &lt;目录或.eraindex&gt;
///   archoera-indexer stats  --index &lt;目录或.eraindex&gt;
///   archoera-indexer replay run    --index &lt;search.db&gt; --queries &lt;queries.jsonl&gt; --out &lt;run.json&gt;
///   archoera-indexer replay corpus --library &lt;library.db&gt; --tracks &lt;n&gt; --out &lt;subset.db&gt;
///   archoera-indexer replay sample --log &lt;查询日志.jsonl&gt; --out &lt;queries.jsonl&gt;
///                                   [--library &lt;library.db&gt; [--with-lyrics]]
///                                   [--replay &lt;n&gt;] [--seed &lt;int&gt;] [--limit &lt;n&gt;]
///   archoera-indexer replay compare --baseline &lt;baseline.json&gt; --run &lt;run.json&gt; [--out &lt;diff.json&gt;]
///   archoera-indexer lyrics stats   --library &lt;library.db&gt; [--out &lt;json&gt;] [--no-lrc]
///   archoera-indexer version
///
/// stdout：JSON 进度 / 结果（TS / Dart 风格，与 scanner 一致）；stderr：人类日志。
/// exit code：0=成功，1=参数 / 运行错误，2=取消。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "build" => RunBuild(args[1..]),
            "query" => RunQuery(args[1..]),
            "analyze" => RunAnalyze(args[1..]),
            "dump" => RunDump(args[1..]),
            "verify" => RunVerify(args[1..]),
            "stats" => RunStats(args[1..]),
            "replay" => Replay.Run(args[1..]),
            "lyrics" => LyricsStats.Run(args[1..]),
            "version" => RunVersion(),
            "-h" or "--help" or "help" => PrintUsage(),
            _ => UnknownCommand(args[0]),
        };
    }

    /* ------------------------------------------------------------------ */
    /* build                                                               */
    /* ------------------------------------------------------------------ */

    private static int RunBuild(string[] args)
    {
        var full = false;
        var options = new IndexerOptions();
        string? dbPath = null;
        string? indexPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--full": case "-f": full = true; break;
                case "--index-lrc": options.IndexLrc = true; break;
                case "--no-pinyin": options.Pinyin = false; break;
                case "--romaji-lyrics": options.RomajiLyrics = true; break;
                case "--db": if (i + 1 < args.Length) dbPath = args[++i]; break;
                case "--index": if (i + 1 < args.Length) indexPath = args[++i]; break;
                case "--batch": if (i + 1 < args.Length && int.TryParse(args[++i], out var b)) options.Batch = b; break;
                case "--merge-every": if (i + 1 < args.Length && int.TryParse(args[++i], out var me) && me > 0)
                        options.MergeEveryUpdates = me; break;
                case "--help": case "-h": PrintUsage(); return 0;
            }
        }

        dbPath ??= Environment.GetEnvironmentVariable("ARCHOERA_DB_PATH");
        if (string.IsNullOrEmpty(dbPath))
        {
            Log.LogError("indexer", "缺少源曲库路径：请设置 ARCHOERA_DB_PATH 或使用 --db <library.db>。");
            return 1;
        }
        if (!File.Exists(dbPath))
        {
            Log.LogError("indexer", $"源曲库不存在: {dbPath}");
            return 1;
        }
        indexPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "search.db");

        Log.LogInfo("indexer", $"library={dbPath}");
        Log.LogInfo("indexer", $"index={indexPath} full={full} index_lrc={options.IndexLrc} pinyin={options.Pinyin} romaji_lyrics={options.RomajiLyrics}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var store = new FtsStore();
        var engine = new IndexerEngine(store, p => Console.WriteLine(IndexerJson.Progress(p)));
        try
        {
            var result = engine.Run(dbPath, indexPath, full, options, cts.Token);
            Console.WriteLine(IndexerJson.Result(result));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log.LogWarn("indexer", "已取消");
            return 2;
        }
        catch (Exception ex)
        {
            Log.LogFatal("indexer", $"FATAL: {ex}");
            return 1;
        }
    }

    /* ------------------------------------------------------------------ */
    /* query（调试：跑 bm25 排序）                                          */
    /* ------------------------------------------------------------------ */

    private static int RunQuery(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine(
                "用法: indexer query <text> [--index <search.db>] [--limit <n>] [--raw] [--session]");
            return 1;
        }

        var text = args[0];
        var raw = false;
        var limit = 20;
        string? indexPath = null;
        // --session：走**生产查询路径**（SearchSession：六趟渐进 + 趟次归因 pass）。
        // 默认（无该开关）只做单条 MATCH 的裸排名，是更早的人工判读工具。
        var session = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--raw": raw = true; break;
                case "--session": session = true; break;
                case "--index": if (i + 1 < args.Length) indexPath = args[++i]; break;
                case "--limit": if (i + 1 < args.Length && int.TryParse(args[++i], out var n)) limit = n; break;
            }
        }

        indexPath ??= Path.Combine(Directory.GetCurrentDirectory(), "search.db");
        if (!File.Exists(indexPath))
        {
            Log.LogError("indexer", $"索引不存在: {indexPath}");
            return 1;
        }

        // 生产查询路径：逐趟渐进 + 每条命中带 pass 归因（精度审计 §6.3 / §7.3）。
        // 排障「这首歌为什么被搜出来」应当用这个，而不是默认的裸 MATCH。
        if (session)
        {
            if (raw)
            {
                Console.Error.WriteLine("--session 与 --raw 互斥（--session 自己编译表达式）。");
                return 1;
            }
            var result = SearchSession.Run(indexPath, new SearchRequest
            {
                Query = text,
                Limit = limit,
                Fuzzy = true,
            });
            Console.WriteLine(IndexerJson.QueryResult(result));
            return 0;
        }

        // 默认把输入当“人话”，经同一分析器归一为词元（AND 短语）；--raw 则原样作 FTS5 表达式。
        var matchExpr = raw ? text : BuildMatchExpression(text);
        Log.LogInfo("indexer", $"MATCH: {matchExpr}");

        using var store = new FtsStore();
        store.Open(indexPath, build: false);
        // CLI 是人工判读工具：默认**不限候选**（0），看真实全量排序（生产走 candidateCap）。
        var rows = store.Search(matchExpr, limit, 0, 0);

        // 附加 track_id 便于人工判读（只读连接，WAL 下可并发）
        var trackIds = ResolveTrackIds(indexPath, rows.Select(r => r.DocId).ToList());
        Console.WriteLine(IndexerJson.SearchResults(rows, trackIds));
        return 0;
    }

    /// <summary>把查询文本经分析器归一为 FTS5 MATCH 表达式（词元以空格 = 隐式 AND）。</summary>
    private static string BuildMatchExpression(string text)
    {
        var query = Analyzer.AnalyzeQuery(text);
        var tokens = query.Lexical.Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return "\"\"";
        return string.Join(' ', tokens);
    }

    private static Dictionary<long, string> ResolveTrackIds(string indexPath, List<long> docIds)
    {
        var map = new Dictionary<long, string>();
        if (docIds.Count == 0) return map;
        try
        {
            using var conn = new SqliteConnection(FtsStore.ConnectionString(indexPath, readOnly: true));
            conn.Open();
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (int i = 0; i < docIds.Count; i++)
            {
                names.Add("@p" + i);
                cmd.Parameters.AddWithValue("@p" + i, docIds[i]);
            }
            cmd.CommandText = $"SELECT doc_id, track_id FROM search_docs WHERE doc_id IN ({string.Join(',', names)})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) map[reader.GetInt64(0)] = reader.GetString(1);
        }
        catch (Exception ex)
        {
            Log.LogWarn("indexer", $"解析 track_id 失败: {ex.Message}");
        }
        return map;
    }

    /* ------------------------------------------------------------------ */
    /* analyze / version（P1 保留）                                         */
    /* ------------------------------------------------------------------ */

    private static int RunAnalyze(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: indexer analyze <field> <text>");
            return 1;
        }
        if (!TryParseField(args[0], out _))
        {
            Console.Error.WriteLine($"未知字段: {args[0]}（可用: title/artist/album/lyrics/lyrics_tr/lyrics_rom/filename 或 0..6）");
            return 1;
        }

        Console.WriteLine(IndexerJson.Analysis(Analyzer.Analyze(args[1])));
        return 0;
    }

    private static int RunVersion()
    {
        Console.WriteLine(IndexerJson.Version(Analyzer.AnalyzerVersion));
        return 0;
    }

    /* ------------------------------------------------------------------ */
    /* EraStore 诊断：dump / verify / stats（只读，stdout 输出 JSON）        */
    /* ------------------------------------------------------------------ */

    private static int RunVerify(string[] args)
    {
        if (HasHelp(args)) return PrintUsage();
        var indexPath = ReadIndexArg(args);
        if (indexPath is null) return WriteError("缺少 --index <目录或.eraindex>");

        try
        {
            var report = IndexVerifier.Verify(indexPath);
            WriteJson(report.ToJson());
            return report.Ok ? 0 : 1;
        }
        catch (Exception ex) when (IsDiagnosticError(ex))
        {
            return WriteError(ex.Message);
        }
    }

    private static int RunStats(string[] args)
    {
        if (HasHelp(args)) return PrintUsage();
        var indexPath = ReadIndexArg(args);
        if (indexPath is null) return WriteError("缺少 --index <目录或.eraindex>");

        try
        {
            WriteJson(IndexStats.Collect(indexPath).ToJson());
            return 0;
        }
        catch (Exception ex) when (IsDiagnosticError(ex))
        {
            return WriteError(ex.Message);
        }
    }

    private static int RunDump(string[] args)
    {
        if (HasHelp(args)) return PrintUsage();

        string? indexPath = null;
        string? prefix = null;
        FieldId? field = null;
        var limit = IndexDumper.DefaultLimit;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index":
                    if (i + 1 < args.Length) indexPath = args[++i];
                    break;
                case "--prefix":
                    if (i + 1 < args.Length) prefix = args[++i];
                    break;
                case "--limit":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out limit) || limit <= 0)
                        return WriteError("--limit 需要正整数");
                    break;
                case "--field":
                    if (i + 1 >= args.Length) return WriteError("--field 需要字段编号");
                    if (!TryParseFieldId(args[++i], out var parsed))
                        return WriteError($"--field 非法: {args[i]}（1..12 或 0x01..0x0c）");
                    field = parsed;
                    break;
            }
        }

        if (indexPath is null) return WriteError("缺少 --index <目录或.eraindex>");

        try
        {
            WriteJson(IndexDumper.Dump(indexPath, field, prefix, limit).ToJson());
            return 0;
        }
        catch (Exception ex) when (IsDiagnosticError(ex))
        {
            return WriteError(ex.Message);
        }
    }

    private static bool HasHelp(string[] args) =>
        Array.IndexOf(args, "-h") >= 0 || Array.IndexOf(args, "--help") >= 0;

    private static string? ReadIndexArg(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--index") return args[i + 1];
        return null;
    }

    /// <summary>解析 <c>--field</c>：十进制或 <c>0x</c> 十六进制，范围 1..12。</summary>
    private static bool TryParseFieldId(string value, out FieldId field)
    {
        field = default;
        int parsed;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                return false;
        }
        else if (!int.TryParse(value, out parsed))
        {
            return false;
        }

        if (parsed < (int)FieldId.Title || parsed > (int)FieldId.ReadingMeta) return false;
        field = (FieldId)parsed;
        return true;
    }

    /// <summary>诊断命令可预期的失败（结构 / 文件 / 参数），转成 JSON 错误并返回 1。</summary>
    private static bool IsDiagnosticError(Exception ex) =>
        ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException;

    private static void WriteJson(byte[] json) =>
        Console.Out.WriteLine(Encoding.UTF8.GetString(json));

    private static int WriteError(string message)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", false);
            w.WriteString("error", message);
            w.WriteEndObject();
        }
        WriteJson(buffer.WrittenSpan.ToArray());
        return 1;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("""
            Archoera 本地曲库搜索索引器

            用法:
              archoera-indexer build [--full] [--index-lrc] [--no-pinyin] [--romaji-lyrics]
                                     [--db <library.db>] [--index <search.db>] [--batch <n>]
                                     [--merge-every <n>]
                  构建 / 增量更新搜索索引（默认增量）。stdout 输出 JSON 进度与结果。
                  --merge-every 累计改写多少首后做一次 FTS5 全段合并（默认 30）。
              archoera-indexer query <text> [--index <search.db>] [--limit <n>] [--raw] [--session]
                  调试查询（bm25 排序）。默认经分析器归一；--raw 传入原始 FTS5 MATCH 表达式。
                  --session 走生产查询路径（六趟渐进），每条命中附 pass 归因——
                  排障「这首歌为什么被搜出来」用这个。
              archoera-indexer analyze <field> <text>
                  分析文本，stdout 输出 {"tokens":"...","pinyin":"...","initials":"..."}
              archoera-indexer dump --index <目录或.eraindex> [--field <1..12|0x01>]
                                    [--prefix <文本>] [--limit <n>]
                  导出 EraStore 词典词条（stdout 单行 JSON）。默认最多 1000 条（上限 100000）。
              archoera-indexer verify --index <目录或.eraindex>
                  全量校验 Header / 提交槽 / Manifest / 段体 CRC 与段结构；
                  通过退出码 0，发现问题退出码 1（stdout JSON 列出 errors）。
              archoera-indexer stats --index <目录或.eraindex>
                  只读元信息统计（段数 / 文档数 / 活数据 / 文件体积 / 各段字节）。
              archoera-indexer replay run --index <search.db> --queries <queries.jsonl> --out <run.json>
                                          [--limit <n>] [--repeats <n>] [--commit <sha>] [--corpus-hash <hex>]
                  回放查询集，输出 §6.7.4 基线 JSON（每条留前 100 个 trackId，耗时取中位数）。
              archoera-indexer replay corpus --library <library.db> --tracks <n> --out <subset.db>
                  按 sha256(track_id) 升序取前 n 首，生成可复现子集库与 <out>.corpus.json。
              archoera-indexer replay sample --log <查询日志.jsonl> --out <queries.jsonl>
                                          [--library <library.db> [--with-lyrics]]
                                          [--replay <n>] [--seed <int>] [--limit <n>]
                  把桌面端查询日志抽样成 §6.7.1 查询集（固定 14 条 + 加权回放，默认 200）；
                  日志不足时可从 library.db 按 sha256(track_id) 派生查询补齐。
              archoera-indexer replay compare --baseline <baseline.json> --run <run.json>
                                             [--out <diff.json>] [--allow-subset]
                  逐条比较两份 §6.7.4 基线；ids 顺序 / 空态完全一致退出 0，否则退出 1。
              archoera-indexer lyrics stats --library <library.db> [--out <json>] [--no-lrc]
                  统计歌词三层行数分位 / 去重率 / 护栏触发（V6 阈值校准，只读 library.db）。
              archoera-indexer version
                  输出版本锚点 {"analyzerVersion":1}
              archoera-indexer -h | --help
                  打印本用法

            环境变量:
              ARCHOERA_DB_PATH  源曲库路径（build 的默认 --db）

            字段（<field>，大小写不敏感，或 0..6）:
              title | artist | album | lyrics | lyrics_tr | lyrics_rom | filename
            """);
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令: {command}");
        PrintUsage();
        return 1;
    }

    private static bool TryParseField(string value, out Field field)
    {
        switch (value.ToLowerInvariant())
        {
            case "title": field = Field.Title; return true;
            case "artist" or "artists": field = Field.Artist; return true;
            case "album": field = Field.Album; return true;
            case "lyrics" or "lyrics_original": field = Field.LyricsOriginal; return true;
            case "lyrics_tr" or "lyricstr" or "translation": field = Field.LyricsTranslation; return true;
            case "lyrics_rom" or "lyricsrom" or "romaji": field = Field.LyricsRomaji; return true;
            case "filename": field = Field.Filename; return true;
        }

        if (int.TryParse(value, out var index) && index >= 0 && index <= (int)Field.Filename)
        {
            field = (Field)index;
            return true;
        }

        field = default;
        return false;
    }
}
