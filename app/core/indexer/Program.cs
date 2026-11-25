// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Text;
using System.Text.Json;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer;

/// <summary>
/// Archoera 本地曲库搜索索引器 CLI。
///
/// 用法：
///   archoera-indexer build [--full] [--index-lrc] [--no-pinyin] [--romaji-lyrics]
///                          [--initials-lyrics]
///                          [--db &lt;library.db&gt;] [--index &lt;search.db&gt;] [--batch &lt;n&gt;]
///   archoera-indexer query &lt;text&gt; [--index &lt;search.db&gt;] [--library &lt;library.db&gt;]
///                          [--limit &lt;n&gt;] [--raw] [--session] [--scheme &lt;id&gt;] [--explain]
///   archoera-indexer analyze &lt;field&gt; &lt;text&gt;
///   archoera-indexer dump   --index &lt;目录或.eraindex&gt; [--field &lt;1..12|0x01&gt;]
///                           [--prefix &lt;文本&gt;] [--limit &lt;n&gt;]
///   archoera-indexer verify --index &lt;目录或.eraindex&gt;
///   archoera-indexer stats  --index &lt;目录或.eraindex&gt;
///   archoera-indexer compact --index &lt;search.db|目录&gt; [--max-docs 4000] [--budget-seconds 2]
///       # 手动压缩；全量 build 结束后也会自动做一次有界压缩（见 BuildCompactor）
///       # 压缩后若留下可见垃圾（&gt;64 KiB 未引用字节）会再整理一次（见 Defragmenter）
///   archoera-indexer defrag --index &lt;search.db|目录&gt;
///       # 把 CURRENT 世代重写为只含活段体的新文件（崩溃安全，原子切换 CURRENT）
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
            "compact" => RunCompact(args[1..]),
            "defrag" => RunDefrag(args[1..]),
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
                case "--initials-lyrics": options.InitialsLyrics = true; break;
                case "--db": if (i + 1 < args.Length) dbPath = args[++i]; break;
                case "--index": if (i + 1 < args.Length) indexPath = args[++i]; break;
                case "--batch": if (i + 1 < args.Length && int.TryParse(args[++i], out var b)) options.Batch = b; break;
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
        Log.LogInfo("indexer", $"index={indexPath} full={full} index_lrc={options.IndexLrc} pinyin={options.Pinyin} romaji_lyrics={options.RomajiLyrics} initials_lyrics={options.InitialsLyrics}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var engine = new IndexerEngine(p => Console.WriteLine(IndexerJson.Progress(p)));
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
                "用法: indexer query <text> [--index <search.db>] [--library <library.db>] [--limit <n>] [--raw] [--session] [--scheme <id>] [--explain] [--snippet] [--highlight|--no-highlight] [--cursor <next>] [--total none|capped|exact]");
            return 1;
        }

        var text = args[0];
        var raw = false;
        var limit = 20;
        string? indexPath = null;
        // --session：走**生产查询路径**（SearchSession：六趟渐进 + 趟次归因 pass）。
        // 默认（无该开关）只做单条 MATCH 的裸排名，是更早的人工判读工具。
        var session = false;
        var explain = false;
        var snippet = false;
        var highlight = false;
        string? scheme = null;
        string? libraryDb = null;
        string? cursorRaw = null;
        var totalMode = SearchTotalMode.None;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--raw": raw = true; break;
                case "--session": session = true; break;
                case "--explain": explain = true; session = true; break;
                case "--snippet": snippet = true; session = true; break;
                case "--highlight": highlight = true; session = true; break;
                case "--no-highlight": highlight = false; session = true; break;
                case "--cursor":
                    if (i + 1 < args.Length) { cursorRaw = args[++i]; session = true; }
                    break;
                case "--total":
                    if (i + 1 < args.Length)
                    {
                        if (!SearchPaging.TryParseTotalMode(args[++i], out totalMode))
                        {
                            Console.Error.WriteLine("--total 只能是 none / capped / exact");
                            return 1;
                        }
                        session = true;
                    }
                    break;
                case "--scheme": if (i + 1 < args.Length) scheme = args[++i]; break;
                case "--library": if (i + 1 < args.Length) libraryDb = args[++i]; break;
                case "--index": if (i + 1 < args.Length) indexPath = args[++i]; break;
                case "--limit": if (i + 1 < args.Length && int.TryParse(args[++i], out var n)) limit = n; break;
            }
        }

        indexPath ??= Path.Combine(Directory.GetCurrentDirectory(), "search.db");
        var eraDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? ".", IndexerEngine.EraStoreDirName);
        if (!File.Exists(indexPath) && EraStore.CurrentPointer.Read(eraDir) is null)
        {
            Log.LogError("indexer", $"索引不存在: {indexPath}");
            return 1;
        }

        // §5.5.6：基础归一化表缺失时拒绝查询（不允许用不完整的表给出「看起来正常」的结果）。
        var pack = DataPack.Current;
        if (!pack.Present || !pack.FoundationOk)
        {
            Log.LogError("indexer", pack.FoundationError);
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
            SearchCursor? cursor = null;
            if (cursorRaw is not null)
            {
                if (!SearchPaging.TryDecode(cursorRaw, out var decodedCursor))
                {
                    Console.Error.WriteLine("--cursor 非法（不是 v1 的 32 字节游标）");
                    return 1;
                }
                cursor = decodedCursor;
            }

            var result = SearchSession.Run(indexPath, new SearchRequest
            {
                Query = text,
                Limit = limit,
                Fuzzy = true,
                Scheme = scheme,
                Explain = explain,
                Snippet = snippet,
                Highlight = highlight,
                Cursor = cursor,
                CursorRaw = cursorRaw,
                TotalMode = totalMode,
            }, libraryDb);
            Console.WriteLine(IndexerJson.QueryResult(result));
            return 0;
        }

        // 默认把输入当“人话”，经同一分析器归一为词元（AND 短语）；--raw 则原样作
        // EraStore 支持的 MATCH 子集（列作用域 / 引号 / 前缀 / AND / OR / 括号）。
        // T3.13：CLI 也走 EraStore，不再读 v7 的 search.db。
        var matchExpr = raw ? text : BuildMatchExpression(text);
        Log.LogInfo("indexer", $"MATCH: {matchExpr}");

        var eraName = EraStore.CurrentPointer.Read(eraDir);
        if (eraName is null)
        {
            Log.LogError("indexer", "EraStore CURRENT 缺失（索引未就绪）");
            return 1;
        }

        using var reader = new EraStore.StoreReader(Path.Combine(eraDir, eraName));
        // CLI 是人工判读工具：默认**不限候选**（-1），看真实全量排序（生产走 candidateCap）。
        var hits = EraStore.EraQueryPlan.Execute(reader, matchExpr, limit <= 0 ? -1 : limit);
        var rows = new List<(long DocId, double Score)>(hits.Count);
        var trackIds = new Dictionary<long, string>();
        foreach (var hit in hits)
        {
            var docId = (long)hit.SegmentId * 1_000_000L + hit.DocId;
            rows.Add((docId, hit.Score));
            trackIds[docId] = hit.TrackIdHex;
        }
        Console.WriteLine(IndexerJson.SearchResults(rows, trackIds));
        return 0;
    }

    /// <summary>把查询文本经分析器归一为 EraStore 支持的 MATCH 表达式（词元以空格连接）。</summary>
    private static string BuildMatchExpression(string text)
    {
        var query = Analyzer.AnalyzeQuery(text);
        var tokens = query.Lexical.Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return "\"\"";
        return string.Join(' ', tokens);
    }

    /* ------------------------------------------------------------------ */
    /* compact（EraStore 分层压缩，T3.10 / 运维用）                         */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 手动压缩：按 Tier 策略归并小段，直到段数低于触发线或时间预算耗尽。
    /// 可重复执行；每次只做有界工作，不阻塞查询。
    ///
    /// <para>合并是「向同一世代文件追加新段」，因此压缩后文件里仍留着旧段垃圾；
    /// 本命令在合并后若发现 <b>可见垃圾</b>（未引用字节 &gt; 64 KiB）会再做一次
    /// <see cref="EraStore.Defragmenter"/>，把文件缩回活数据大小。JSON 只在原有两个
    /// 键后追加字段（<c>merged</c> / <c>segments</c> 语义不变）。</para>
    /// </summary>
    private static int RunCompact(string[] args)
    {
        string? indexPath = null;
        var maxDocs = 4000;
        var budget = 2.0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index": if (i + 1 < args.Length) indexPath = args[++i]; break;
                case "--max-docs": if (i + 1 < args.Length && int.TryParse(args[++i], out var n) && n > 0)
                        maxDocs = n; break;
                case "--budget-seconds": if (i + 1 < args.Length
                        && double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0)
                        budget = b; break;
                case "--help": case "-h":
                    Console.Error.WriteLine("用法: indexer compact --index <search.db|目录> [--max-docs 4000] [--budget-seconds 2]");
                    Console.Error.WriteLine("  （压缩后若留下 >64 KiB 未引用字节，会再整理一次文件）");
                    return 0;
            }
        }

        indexPath ??= Path.Combine(Directory.GetCurrentDirectory(), "search.db");
        var eraDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? ".", IndexerEngine.EraStoreDirName);
        var name = EraStore.CurrentPointer.Read(eraDir);
        if (name is null)
        {
            Log.LogError("indexer", "EraStore CURRENT 缺失");
            return 1;
        }
        var eraPath = Path.Combine(eraDir, name);
        try
        {
            var fileBytesBefore = new FileInfo(eraPath).Length;
            var (merged, segments) = EraStore.IncrementalUpdater.Maintain(
                eraPath,
                new EraStore.IncrementalOptions
                {
                    MergeMaxDocs = maxDocs,
                    MergeBudgetSeconds = budget,
                });

            // 压缩后 BEST-EFFORT 去碎片：只处理「可见垃圾」，纯净索引不动。
            // 整理失败只 warn（压缩本身已成功，旧世代仍可查询）。
            var defrag = new EraStore.DefragResult
            {
                FileBytesBefore = fileBytesBefore,
                FileBytesAfter = new FileInfo(eraPath).Length,
            };
            try
            {
                defrag = EraStore.Defragmenter.DefragIfGarbage(
                    eraPath, minOverheadRatio: 0.0, minGarbageBytes: EraStore.Defragmenter.MinAutoGarbageBytes);
            }
            catch (Exception ex)
            {
                Log.LogWarn("indexer", $"压缩后去碎片失败（索引仍可用）: {ex.Message}");
            }

            Console.WriteLine(
                $"{{\"merged\":{(merged ? "true" : "false")},\"segments\":{segments}"
                + $",\"defragged\":{(defrag.Ran ? "true" : "false")}"
                + $",\"fileBytesBefore\":{defrag.FileBytesBefore}"
                + $",\"fileBytesAfter\":{defrag.FileBytesAfter}"
                + $",\"liveBytes\":{defrag.LiveBytes}"
                + $",\"overheadBytesAfter\":{defrag.OverheadBytesAfter}"
                + $",\"defragMs\":{defrag.ElapsedMs}}}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.LogError("indexer", $"压缩失败: {ex.Message}");
            return 1;
        }
    }

    /* ------------------------------------------------------------------ */
    /* defrag（EraStore 世代重写 / 空间回收，A2）                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 把 <c>CURRENT</c> 指向的世代重写成只含活段体的新文件并原子切换。
    /// 适合在多次增量 / 压缩后回收「追加式合并」留下的旧段垃圾。
    /// </summary>
    private static int RunDefrag(string[] args)
    {
        string? indexPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index": if (i + 1 < args.Length) indexPath = args[++i]; break;
                case "--help": case "-h":
                    Console.Error.WriteLine("用法: indexer defrag --index <search.db|目录>");
                    return 0;
            }
        }

        indexPath ??= Path.Combine(Directory.GetCurrentDirectory(), "search.db");
        var eraDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? ".", IndexerEngine.EraStoreDirName);
        var name = EraStore.CurrentPointer.Read(eraDir);
        if (name is null)
        {
            Log.LogError("indexer", "EraStore CURRENT 缺失");
            return 1;
        }
        var eraPath = Path.Combine(eraDir, name);

        try
        {
            var result = EraStore.Defragmenter.Defrag(eraPath);
            Console.WriteLine(
                $"{{\"defragged\":{(result.Ran ? "true" : "false")},\"gen\":{result.Gen}"
                + $",\"fileBytesBefore\":{result.FileBytesBefore}"
                + $",\"fileBytesAfter\":{result.FileBytesAfter}"
                + $",\"liveBytes\":{result.LiveBytes}"
                + $",\"overheadBytesBefore\":{result.OverheadBytesBefore}"
                + $",\"overheadBytesAfter\":{result.OverheadBytesAfter}"
                + $",\"defragMs\":{result.ElapsedMs}}}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.LogError("indexer", $"去碎片失败: {ex.Message}");
            return 1;
        }
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
        var verifyData = Array.IndexOf(args, "--data") >= 0;
        string? dataDir = null;
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--data-dir") dataDir = args[i + 1];

        if (indexPath is null && !verifyData)
            return WriteError("缺少 --index <目录或.eraindex>（或使用 --data 校验数据包）");

        try
        {
            if (indexPath is null)
            {
                var dir = dataDir ?? DataPack.FindDirectory();
                if (dir is null)
                    throw new InvalidDataException(
                        "找不到数据包目录（data/manifest.json）：请用 --data-dir <目录> 指定");
                var packOnly = IndexVerifier.VerifyDataPack(DataPack.FromDirectory(dir));
                WriteJson(packOnly.ToJson());
                return packOnly.Ok ? 0 : 1;
            }

            DataPackVerification? packReport = null;
            if (verifyData)
            {
                var dir = dataDir ?? DataPack.FindDirectory();
                if (dir is null)
                    throw new InvalidDataException(
                        "找不到数据包目录（data/manifest.json）：请用 --data-dir <目录> 指定");
                packReport = DataPack.FromDirectory(dir).VerifyAll(validateTables: true);
            }

            var report = IndexVerifier.Verify(indexPath, packReport);
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
                                     [--initials-lyrics]
                                     [--db <library.db>] [--index <search.db>] [--batch <n>]
                  构建 / 增量更新搜索索引（默认增量）。stdout 输出 JSON 进度与结果。
                  --initials-lyrics 额外为歌词生成首字母列（默认关；开启后索引变大）。
              archoera-indexer query <text> [--index <search.db>] [--library <library.db>]
                                     [--limit <n>] [--raw] [--session] [--scheme <id>] [--explain]
                  调试查询（bm25 排序）。默认经分析器归一；--raw 传入 EraStore 支持的 MATCH 子集。
                  --session 走生产查询路径（六趟渐进），每条命中附 pass 归因；
                  --library 指定源曲库，过滤已删除曲目（T2.9）；
                  --scheme 指定方案（fast@1 / default@1 / recall@1 / minimal@1）；
                  --explain 输出前 50 条 explain（kind / field / coverage / 来源）与 trace。
                  排障「这首歌为什么被搜出来」用这个。
              archoera-indexer analyze <field> <text>
                  分析文本，stdout 输出 {"tokens":"...","pinyin":"...","initials":"..."}
              archoera-indexer dump --index <目录或.eraindex> [--field <1..12|0x01>]
                                    [--prefix <文本>] [--limit <n>]
                  导出 EraStore 词典词条（stdout 单行 JSON）。默认最多 1000 条（上限 100000）。
              archoera-indexer verify --index <目录或.eraindex> [--data [--data-dir <目录>]]
                  全量校验 Header / 提交槽 / Manifest / 段体 CRC 与段结构；
                  --data 额外逐文件校验搜索数据包（sha256 + TSV 结构 + id 一致性）；
                  通过退出码 0，发现问题退出码 1（stdout JSON 列出 errors / dataPack）。
                  --data 可单独使用（不带 --index）只校验数据包。
              archoera-indexer stats --index <目录或.eraindex>
                  只读元信息统计（段数 / 文档数 / 活数据 / 文件体积 / 各段字节）。
              archoera-indexer compact --index <search.db|目录> [--max-docs 4000] [--budget-seconds 2]
                  有界分层压缩；合并后若留下 >64 KiB 未引用字节会再整理一次文件。
              archoera-indexer defrag --index <search.db|目录>
                  把 CURRENT 世代重写为只含活段体的新文件（崩溃安全、原子切换 CURRENT）。
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
