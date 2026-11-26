// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Bench;

/// <summary>
/// EraStore 引擎级基准（设计文档 §3.3.0.16 / §5.3 P11）。
///
/// 只测存储层，不掺查询排序语义：固定合成语料上跑 build / open / query / merge / verify
/// 五个子命令，各写一份紧凑 JSON 便于逐版本对比。
///
/// 运行规则沿用仓库约定：默认 <c>quick</c>；<c>full</c> 必须显式 <c>ALLOW_LONG_BENCH=1</c>，
/// 否则直接拒绝（脚本与程序双重保护）。不上 CI。
/// </summary>
internal static class Program
{
    private const string Usage =
        """
        用法: EraStoreBench [--profile quick|30k|full] [--out <目录>] [--only build|open|query|merge|verify]
          --profile  语料规模（默认 quick：5,000 docs；30k：30,000 docs（约 1 分钟）；
                     full：50,000 docs，需 ALLOW_LONG_BENCH=1）
          --out      输出目录（默认当前目录下的 out/）
          --only     只跑一个子命令（仍会先构建索引作夹具）
        """;

    /// <summary>固定 file_uuid / created_ms：同输入必得同字节。</summary>
    private static readonly byte[] FixedFileUuid = MakeFixedBytes(0xA0);

    private const ulong FixedCreatedMs = 1_700_000_000_000UL;

    /// <summary>每类查询最多轮数（设计文档 §3.3.0.16）。</summary>
    private const int QueryRounds = 200;

    /// <summary>前缀查询单段最大展开词条数（控制 quick 耗时，不改变语义）。</summary>
    private const int MaxPrefixExpansions = 16;

    /// <summary>首字母查询样本（a–z 单字母 token）。</summary>
    private static readonly string[] InitialTokens =
        "abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()).ToArray();

    private static int Main(string[] args)
    {
        string profile = "quick";
        string? outDir = null;
        string? only = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile":
                    if (++i >= args.Length) return Fail("--profile 缺少取值");
                    profile = args[i];
                    break;
                case "--out":
                    if (++i >= args.Length) return Fail("--out 缺少取值");
                    outDir = args[i];
                    break;
                case "--only":
                    if (++i >= args.Length) return Fail("--only 缺少取值");
                    only = args[i];
                    break;
                case "-h":
                case "--help":
                    Console.Error.WriteLine(Usage);
                    return 0;
                default:
                    return Fail($"未知参数 {args[i]}");
            }
        }

        if (profile is not ("quick" or "30k" or "full"))
            return Fail($"--profile 只接受 quick / 30k / full（收到 {profile}）");

        if (profile == "full" && Environment.GetEnvironmentVariable("ALLOW_LONG_BENCH") != "1")
        {
            Console.Error.WriteLine(
                "PROFILE=full 是约 15 分钟的满负载基准；确需运行请显式设置 ALLOW_LONG_BENCH=1。");
            return 2;
        }

        if (only is not null && only is not ("build" or "open" or "query" or "merge" or "verify"))
            return Fail($"--only 只接受 build|open|query|merge|verify（收到 {only}）");

        var phases = new HashSet<string>(StringComparer.Ordinal);
        if (only is not null) phases.Add(only);
        bool Wants(string phase) => phases.Count == 0 || phases.Contains(phase);

        var docCount = profile switch { "quick" => 5_000, "30k" => 30_000, _ => 50_000 };
        // 12 字段族把每段驻留量放大约一个数量级：5k/段可把段内累加器压回百 MB 以内；
        // 50k → 10 段 × 5 条段引用 = 50 < Manifest 64 上限。
        var docsPerSegment = profile == "quick" ? 2_000 : 5_000;

        var resolvedOut = Path.GetFullPath(
            outDir ?? Path.Combine(Directory.GetCurrentDirectory(), "out"));
        Directory.CreateDirectory(resolvedOut);

        var work = Path.Combine(Path.GetTempPath(), "era-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var indexPath = Path.Combine(work, "search-1.eraindex");

        try
        {
            // 30k 用短歌词档：作为「中等语料 + 三层歌词」的常规回归；
            // full（50k）保留 20–40 行长歌词，是 12 层全开的最坏情况压测。
            var corpus = new Corpus(docCount, shortLyrics: profile != "full");
            var phraseSamples = new List<string[]>(20);
            var pinyinPhraseSamples = new List<string[]>(20);
            long tokenCount = 0;

            var buildWatch = Stopwatch.StartNew();
            var build = IndexBuilder.Build(
                indexPath,
                corpus.Docs(doc =>
                {
                    foreach (var field in doc.Tokens.Values) tokenCount += field.Length;
                    if (phraseSamples.Count < 20
                        && doc.Tokens.TryGetValue(FieldId.Title, out var title)
                        && title.Length >= 2)
                        phraseSamples.Add(new[] { title[0], title[1] });
                    if (pinyinPhraseSamples.Count < 20
                        && doc.Tokens.TryGetValue(FieldId.PinyinMeta, out var pinyin)
                        && pinyin.Length >= 2)
                        pinyinPhraseSamples.Add(new[] { pinyin[0], pinyin[1] });
                }),
                Options(docsPerSegment));
            buildWatch.Stop();

            if (Wants("build"))
            {
                var rssMb = PeakRssMb();
                var managedMb = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
                WriteBuildJson(
                    resolvedOut, profile, docCount, tokenCount, build,
                    buildWatch.Elapsed.TotalSeconds, rssMb, managedMb);
                var footprint = CollectFootprint(indexPath, build);
                WriteFootprintJson(resolvedOut, profile, footprint);
                Console.Error.WriteLine(
                    $"[build] {docCount} docs / {build.SegmentCount} segments in"
                    + $" {buildWatch.Elapsed.TotalSeconds:F2}s, {build.Manifest.FileBytes / 1024.0 / 1024.0:F2} MB"
                    + $", rss peak {rssMb:F1} MB (managed {managedMb:F1} MB) -> build.json / footprint.json"
                    + $"（{footprint.BytesPerTrack:F0} B/doc）");
            }

            if (Wants("open"))
            {
                var startup = RunStartup(indexPath, corpus, build.Manifest);
                WriteOpenJson(resolvedOut, startup.OpenColdMs, startup.OpenWarmMs, build.Manifest);
                WriteStartupJson(resolvedOut, startup);
                Console.Error.WriteLine(
                    $"[open] cold {startup.OpenColdMs:F2}ms, warm {startup.OpenWarmMs:F2}ms,"
                    + $" firstQuery {startup.FirstQueryMs:F3}ms, {build.SegmentCount} segments"
                    + $" -> open.json / startup.json");
            }

            if (Wants("query"))
            {
                var summary = RunQuery(indexPath, corpus, phraseSamples, pinyinPhraseSamples);
                WriteQueryJson(resolvedOut, summary);
                WriteLatencyJson(resolvedOut, summary);
                Console.Error.WriteLine(
                    $"[query] {summary.Queries} queries, p50 {summary.P50Ms:F3}ms,"
                    + $" p95 {summary.P95Ms:F3}ms, {summary.Qps:F0} qps -> query.json / latency.json");
            }

            if (Wants("merge"))
            {
                var merge = RunMerge(indexPath);
                WriteMergeJson(resolvedOut, merge);
                Console.Error.WriteLine(
                    $"[merge] {merge.InputSegments} segments, live {merge.LiveDocs}, dead {merge.DeadDocs},"
                    + $" ratio {merge.DeadRatioBefore:P2} -> {merge.DeadRatioAfter:P2},"
                    + $" {merge.Seconds:F3}s, {merge.BytesWritten} B -> merge.json");
            }

            if (Wants("verify"))
            {
                var report = IndexVerifier.Verify(indexPath);
                WriteVerifyJson(resolvedOut, report);
                Console.Error.WriteLine(
                    $"[verify] ok={report.Ok}, sections={report.Sections}, docs={report.Docs},"
                    + $" errors={report.Errors.Count} -> verify.json");
            }

            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[bench] 失败: {e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /* ------------------------------------------------------------------ */
    /* build                                                              */
    /* ------------------------------------------------------------------ */

    private static void WriteBuildJson(
        string outDir,
        string profile,
        int docCount,
        long tokenCount,
        BuildResult build,
        double seconds,
        double rssMb,
        double managedMb)
    {
        long dict = 0;
        long postings = 0;
        long positions = 0;
        long docstore = 0;
        foreach (var section in build.Manifest.Sections)
        {
            switch (section.Kind)
            {
                case SectionKind.Dict:
                case SectionKind.DictIndex:
                    dict += section.Length;
                    break;
                case SectionKind.Postings:
                    postings += section.Length;
                    break;
                case SectionKind.Positions:
                    positions += section.Length;
                    break;
                case SectionKind.DocStore:
                    docstore += section.Length;
                    break;
            }
        }

        var total = build.Manifest.FileBytes;
        var misc = total - dict - postings - positions - docstore;

        WriteJson(Path.Combine(outDir, "build.json"), writer =>
        {
            writer.WriteString("profile", profile);
            writer.WriteStartObject("corpus");
            writer.WriteNumber("docs", docCount);
            writer.WriteNumber("tokens", tokenCount);
            writer.WriteNumber("segments", build.SegmentCount);
            writer.WriteEndObject();
            writer.WriteStartObject("bytes");
            writer.WriteNumber("total", total);
            writer.WriteNumber("dict", dict);
            writer.WriteNumber("postings", postings);
            writer.WriteNumber("positions", positions);
            writer.WriteNumber("docstore", docstore);
            writer.WriteNumber("misc", misc < 0 ? 0 : misc);
            writer.WriteEndObject();
            writer.WriteStartObject("build");
            writer.WriteNumber("seconds", Round2(seconds));
            writer.WriteNumber("rssPeakMB", Round1(rssMb));
            writer.WriteNumber("managedHeapMB", Round1(managedMb));
            writer.WriteEndObject();
        });
    }

    /* ------------------------------------------------------------------ */
    /* footprint（T2.16 / §8.5）                                          */
    /* ------------------------------------------------------------------ */

    /// <summary>单个字段族的词典统计。</summary>
    private sealed class FieldBytes
    {
        /// <summary>各段词典条目数之和（跨段重复计入）。</summary>
        public int Terms;

        /// <summary>该族倒排字节数（位置块另计）。</summary>
        public long PostingsBytes;
    }

    private sealed class FootprintSummary
    {
        public int Docs;
        public int Segments;
        public long Total;
        public long FileBytes;
        public long LiveBytes;
        public long GarbageBytes;
        public double BytesPerTrack;
        public long Dict;
        public long DictIndex;
        public long Postings;
        public long Positions;
        public long DocStore;
        public long Tombs;
        public long Stats;
        public long Misc;
        public Dictionary<FieldId, FieldBytes> Fields = new();
    }

    /// <summary>字段族 → JSON 键（顺序与 <see cref="FieldId"/> 数值一致）。</summary>
    private static readonly (FieldId Field, string Name)[] FieldNames =
    {
        (FieldId.Title, "title"),
        (FieldId.Artist, "artist"),
        (FieldId.Album, "album"),
        (FieldId.Filename, "filename"),
        (FieldId.Lyrics, "lyrics"),
        (FieldId.LyricsTranslation, "lyricsTranslation"),
        (FieldId.LyricsRomanization, "lyricsRomanization"),
        (FieldId.PinyinMeta, "pinyinMeta"),
        (FieldId.PinyinLyrics, "pinyinLyrics"),
        (FieldId.InitialsMeta, "initialsMeta"),
        (FieldId.InitialsLyrics, "initialsLyrics"),
        (FieldId.ReadingMeta, "readingMeta"),
    };

    /// <summary>
    /// 在刚构建完的索引上采集体积口径（<b>不做合并</b>，否则旧段垃圾会污染 bytes_per_track）：
    /// 段体积来自 Manifest；各字段族倒排字节来自每段词典 <c>ReadAll()</c> 的 PostOffset 差值。
    /// </summary>
    private static FootprintSummary CollectFootprint(string indexPath, BuildResult build)
    {
        var raw = File.ReadAllBytes(indexPath);
        var manifest = build.Manifest;
        var segments = SegmentIds(manifest);

        var f = new FootprintSummary
        {
            Docs = (int)manifest.DocCount,
            Segments = segments.Length,
            Total = raw.LongLength,
            FileBytes = manifest.FileBytes,
            LiveBytes = manifest.LiveBytes,
        };
        f.GarbageBytes = Math.Max(0, f.Total - f.LiveBytes);
        f.BytesPerTrack = f.Docs <= 0 ? 0 : (double)f.Total / f.Docs;

        foreach (var section in manifest.Sections)
        {
            switch (section.Kind)
            {
                case SectionKind.Dict: f.Dict += section.Length; break;
                case SectionKind.DictIndex: f.DictIndex += section.Length; break;
                case SectionKind.Postings: f.Postings += section.Length; break;
                case SectionKind.Positions: f.Positions += section.Length; break;
                case SectionKind.DocStore: f.DocStore += section.Length; break;
                case SectionKind.Tombs: f.Tombs += section.Length; break;
                case SectionKind.Stats: f.Stats += section.Length; break;
            }
        }

        f.Misc = f.Total - f.Dict - f.DictIndex - f.Postings - f.Positions - f.DocStore - f.Tombs - f.Stats;
        if (f.Misc < 0) f.Misc = 0;

        foreach (var field in Enum.GetValues<FieldId>()) f.Fields[field] = new FieldBytes();

        foreach (var segmentId in segments)
        {
            var postingsLength = (long)Section(manifest, SectionKind.Postings, segmentId).Length;
            var terms = new TermDictReader(
                Body(raw, Section(manifest, SectionKind.Dict, segmentId)),
                Body(raw, Section(manifest, SectionKind.DictIndex, segmentId))).ReadAll();

            // 词典键按编码字节序排列，倒排按同一顺序写出，因此 PostOffset 必须严格递增。
            for (var i = 0; i < terms.Count; i++)
            {
                var offset = (long)terms[i].PostOffset;
                var next = i + 1 < terms.Count ? (long)terms[i + 1].PostOffset : postingsLength;
                if (next <= offset)
                    throw new InvalidDataException(
                        $"seg={segmentId} 词条 PostOffset 非严格递增（{offset} -> {next}），格式假设已被破坏");

                var target = f.Fields[terms[i].Field];
                target.Terms++;
                target.PostingsBytes += next - offset;
            }
        }

        return f;
    }

    private static void WriteFootprintJson(string outDir, string profile, FootprintSummary f)
    {
        WriteJson(Path.Combine(outDir, "footprint.json"), writer =>
        {
            writer.WriteString("profile", profile);
            writer.WriteStartObject("corpus");
            writer.WriteNumber("docs", f.Docs);
            writer.WriteNumber("segments", f.Segments);
            writer.WriteEndObject();
            writer.WriteStartObject("bytes");
            writer.WriteNumber("total", f.Total);
            writer.WriteNumber("fileBytes", f.FileBytes);
            writer.WriteNumber("liveBytes", f.LiveBytes);
            writer.WriteNumber("garbageBytes", f.GarbageBytes);
            writer.WriteNumber("bytesPerTrack", Round2(f.BytesPerTrack));
            writer.WriteNumber("dict", f.Dict);
            writer.WriteNumber("dictIndex", f.DictIndex);
            writer.WriteNumber("postings", f.Postings);
            writer.WriteNumber("positions", f.Positions);
            writer.WriteNumber("docstore", f.DocStore);
            writer.WriteNumber("tombs", f.Tombs);
            writer.WriteNumber("stats", f.Stats);
            writer.WriteNumber("misc", f.Misc);
            writer.WriteEndObject();
            writer.WriteStartObject("fields");
            foreach (var (field, name) in FieldNames)
            {
                var entry = f.Fields[field];
                writer.WriteStartObject(name);
                writer.WriteNumber("terms", entry.Terms);
                writer.WriteNumber("postingsBytes", entry.PostingsBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteStartObject("layers");
            WriteLayer(writer, "lyricsOriginalBytes", f.Fields[FieldId.Lyrics]);
            WriteLayer(writer, "lyricsTranslationBytes", f.Fields[FieldId.LyricsTranslation]);
            WriteLayer(writer, "lyricsRomanizationBytes", f.Fields[FieldId.LyricsRomanization]);
            WriteLayer(writer, "pinyinMetaBytes", f.Fields[FieldId.PinyinMeta]);
            WriteLayer(writer, "pinyinLyricsBytes", f.Fields[FieldId.PinyinLyrics]);
            WriteLayer(writer, "initialsMetaBytes", f.Fields[FieldId.InitialsMeta]);
            WriteLayer(writer, "initialsLyricsBytes", f.Fields[FieldId.InitialsLyrics]);
            WriteLayer(writer, "readingMetaBytes", f.Fields[FieldId.ReadingMeta]);
            writer.WriteEndObject();
            writer.WriteStartArray("noPositionFields");
            foreach (var (field, name) in FieldNames)
            {
                if (BuildDefaults.NoPositionFields.Contains(field))
                    writer.WriteStringValue(name);
            }
            writer.WriteEndArray();
        });
    }

    private static void WriteLayer(Utf8JsonWriter writer, string name, FieldBytes field) =>
        writer.WriteNumber(name, field.PostingsBytes);

    /* ------------------------------------------------------------------ */
    /* open                                                               */
    /* ------------------------------------------------------------------ */

    private sealed class StartupSummary
    {
        public double OpenColdMs;
        public double OpenWarmMs;
        public double FirstQueryMs;
        public int Docs;
        public int Segments;
    }

    /// <summary>
    /// 冷：本进程第一次打开并读出 Manifest；热：关闭后重复 5 次取中位数。
    /// 首条查询在同一进程内、新开 IndexFile + 构建 readers 后跑第一条 Title term 查询，
    /// <b>不是独立进程冷启动</b>，仅作相对参考（§8.6 的正式口径要求新进程第一次 open）。
    /// </summary>
    private static StartupSummary RunStartup(string indexPath, Corpus corpus, Manifest expected)
    {
        var (coldMs, warmMs) = RunOpen(indexPath, expected);

        var raw = File.ReadAllBytes(indexPath);
        Manifest manifest;
        using (var file = IndexFile.Open(indexPath))
            manifest = file.Current ?? throw new InvalidDataException("索引没有有效提交");
        var readers = OpenReaders(raw, manifest);
        var term = corpus.Vocabulary[0];
        var start = Stopwatch.GetTimestamp();
        foreach (var reader in readers)
        {
            using var iter = new TermIter(reader.Dict, reader.Postings, FieldId.Title, term);
            while (iter.Next()) { }
        }

        return new StartupSummary
        {
            OpenColdMs = coldMs,
            OpenWarmMs = warmMs,
            FirstQueryMs = ElapsedMs(start),
            Docs = (int)manifest.DocCount,
            Segments = SegmentIds(manifest).Length,
        };
    }

    private static void WriteStartupJson(string outDir, StartupSummary s)
    {
        WriteJson(Path.Combine(outDir, "startup.json"), writer =>
        {
            writer.WriteNumber("openColdMs", Round2(s.OpenColdMs));
            writer.WriteNumber("openWarmMs", Round2(s.OpenWarmMs));
            writer.WriteNumber("firstQueryMs", Round2(s.FirstQueryMs));
            writer.WriteNumber("docs", s.Docs);
            writer.WriteNumber("segments", s.Segments);
        });
    }

    /// <summary>冷：本进程第一次打开并读出 Manifest；热：关闭后重复 5 次取中位数。</summary>
    private static (double ColdMs, double WarmMs) RunOpen(string indexPath, Manifest expected)
    {
        long coldCount = 0;
        var coldWatch = Stopwatch.StartNew();
        using (var file = IndexFile.Open(indexPath))
            coldCount = file.Current?.DocCount ?? 0;
        coldWatch.Stop();
        if (coldCount != expected.DocCount)
            throw new InvalidDataException($"冷开文档数 {coldCount} != 构建结果 {expected.DocCount}");

        var warm = new List<double>(5);
        for (var i = 0; i < 5; i++)
        {
            var start = Stopwatch.GetTimestamp();
            using (var file = IndexFile.Open(indexPath))
                _ = file.Current?.DocCount;
            warm.Add(ElapsedMs(start));
        }
        warm.Sort();
        return (coldWatch.Elapsed.TotalMilliseconds, warm[warm.Count / 2]);
    }

    private static void WriteOpenJson(string outDir, double coldMs, double warmMs, Manifest manifest)
    {
        WriteJson(Path.Combine(outDir, "open.json"), writer =>
        {
            writer.WriteNumber("coldMs", Round2(coldMs));
            writer.WriteNumber("warmMs", Round2(warmMs));
            writer.WriteNumber("segments", SegmentIds(manifest).Length);
            writer.WriteNumber("docs", manifest.DocCount);
        });
    }

    /* ------------------------------------------------------------------ */
    /* query                                                              */
    /* ------------------------------------------------------------------ */

    private sealed class QuerySummary
    {
        public int Queries;
        public double P50Ms;
        public double P95Ms;
        public double Qps;
        public List<double> TermTimes { get; } = new();
        public List<double> PrefixTimes { get; } = new();
        public List<double> PhraseTimes { get; } = new();
        public List<double> MetaPinyinPhraseTimes { get; } = new();
        public List<double> LyricsPinyinTimes { get; } = new();
        public List<double> InitialsTimes { get; } = new();
        public List<double> TruncatedTimes { get; } = new();
    }

    /// <summary>
    /// 固定查询集：50 term + 20 prefix + 20 phrase，每类最多 200 轮；
    /// 另按 T2.14 的六类命名计时（literalExact / literalPrefix / metaPinyinPhrase /
    /// lyricsPinyin / initials / truncated），其中前两类复用 term / prefix 的采样。
    /// 单次查询 = 遍历所有段并耗尽结果，只计存储层耗时（无打分 / 排序）。
    /// </summary>
    private static QuerySummary RunQuery(
        string indexPath,
        Corpus corpus,
        IReadOnlyList<string[]> phrases,
        IReadOnlyList<string[]> pinyinPhrases)
    {
        var raw = File.ReadAllBytes(indexPath);
        Manifest manifest;
        using (var file = IndexFile.Open(indexPath))
            manifest = file.Current ?? throw new InvalidDataException("索引没有有效提交");

        var readers = OpenReaders(raw, manifest);

        var termQueries = EvenlyPick(corpus.Vocabulary, 50);
        var prefixQueries = PickPrefixes(corpus.Vocabulary, 20);
        var phraseQueries = phrases.Count == 0 ? new List<string[]> { new[] { corpus.Vocabulary[0], corpus.Vocabulary[1] } } : phrases;
        var metaPinyinQueries = pinyinPhrases.Count == 0
            ? new List<string[]> { new[] { corpus.PinyinVocabulary[0], corpus.PinyinVocabulary[1] } }
            : pinyinPhrases;
        var lyricsPinyinQueries = EvenlyPick(corpus.PinyinVocabulary, 20);
        var initialsQueries = EvenlyPick(InitialTokens, 13);
        var truncatedQueries = CjkPrefixes(corpus.Vocabulary, 20);

        var summary = new QuerySummary();

        for (var round = 0; round < QueryRounds; round++)
        {
            foreach (var term in termQueries)
            {
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    using var iter = new TermIter(reader.Dict, reader.Postings, FieldId.Title, term);
                    while (iter.Next()) { }
                }
                summary.TermTimes.Add(ElapsedMs(start));
            }

            foreach (var prefix in prefixQueries)
            {
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    using var iter = new PrefixIter(
                        reader.Dict, reader.Postings, FieldId.Title, prefix, MaxPrefixExpansions);
                    while (iter.Next()) { }
                }
                summary.PrefixTimes.Add(ElapsedMs(start));
            }

            foreach (var phrase in phraseQueries)
            {
                if (phrase.Length < 2) continue;
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    var first = new TermIter(reader.Dict, reader.Postings, FieldId.Title, phrase[0]);
                    var second = new TermIter(reader.Dict, reader.Postings, FieldId.Title, phrase[1]);
                    using var iter = new PhraseIter(new IDocIter[] { first, second }, positioned: true);
                    while (iter.Next()) { }
                }
                summary.PhraseTimes.Add(ElapsedMs(start));
            }

            foreach (var phrase in metaPinyinQueries)
            {
                if (phrase.Length < 2) continue;
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    var first = new TermIter(reader.Dict, reader.Postings, FieldId.PinyinMeta, phrase[0]);
                    var second = new TermIter(reader.Dict, reader.Postings, FieldId.PinyinMeta, phrase[1]);
                    using var iter = new PhraseIter(new IDocIter[] { first, second }, positioned: true);
                    while (iter.Next()) { }
                }
                summary.MetaPinyinPhraseTimes.Add(ElapsedMs(start));
            }

            foreach (var syllable in lyricsPinyinQueries)
            {
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    using var iter = new TermIter(reader.Dict, reader.Postings, FieldId.PinyinLyrics, syllable);
                    while (iter.Next()) { }
                }
                summary.LyricsPinyinTimes.Add(ElapsedMs(start));
            }

            foreach (var initial in initialsQueries)
            {
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    using var iter = new TermIter(reader.Dict, reader.Postings, FieldId.InitialsMeta, initial);
                    while (iter.Next()) { }
                }
                summary.InitialsTimes.Add(ElapsedMs(start));
            }

            foreach (var prefix in truncatedQueries)
            {
                var start = Stopwatch.GetTimestamp();
                foreach (var reader in readers)
                {
                    // MaxExpansions=2：CJK 单字前缀在 Title 下远多于 2 条，强制走截断分支。
                    using var iter = new PrefixIter(
                        reader.Dict, reader.Postings, FieldId.Title, prefix, maxExpansions: 2);
                    while (iter.Next()) { }
                }
                summary.TruncatedTimes.Add(ElapsedMs(start));
            }
        }

        var all = new List<double>(
            summary.TermTimes.Count + summary.PrefixTimes.Count + summary.PhraseTimes.Count);
        all.AddRange(summary.TermTimes);
        all.AddRange(summary.PrefixTimes);
        all.AddRange(summary.PhraseTimes);
        all.Sort();

        summary.Queries = all.Count;
        summary.P50Ms = Percentile(all, 0.50);
        summary.P95Ms = Percentile(all, 0.95);
        var seconds = 0.0;
        foreach (var time in all) seconds += time;
        summary.Qps = seconds <= 0 ? 0 : summary.Queries / (seconds / 1000.0);
        return summary;
    }

    private static void WriteQueryJson(string outDir, QuerySummary summary)
    {
        WriteJson(Path.Combine(outDir, "query.json"), writer =>
        {
            writer.WriteNumber("queries", summary.Queries);
            writer.WriteNumber("p50Ms", Round2(summary.P50Ms));
            writer.WriteNumber("p95Ms", Round2(summary.P95Ms));
            writer.WriteNumber("qps", Round1(summary.Qps));
            writer.WriteStartObject("kinds");
            WriteKind(writer, "term", summary.TermTimes);
            WriteKind(writer, "prefix", summary.PrefixTimes);
            WriteKind(writer, "phrase", summary.PhraseTimes);
            writer.WriteEndObject();
        });
    }

    /// <summary>六类固定查询的延迟分布（T2.14 命名）；pooled 只作参考。</summary>
    private static void WriteLatencyJson(string outDir, QuerySummary summary)
    {
        var kinds = new (string Name, List<double> Times)[]
        {
            ("literalExact", summary.TermTimes),
            ("literalPrefix", summary.PrefixTimes),
            ("metaPinyinPhrase", summary.MetaPinyinPhraseTimes),
            ("lyricsPinyin", summary.LyricsPinyinTimes),
            ("initials", summary.InitialsTimes),
            ("truncated", summary.TruncatedTimes),
        };

        var pooled = new List<double>();
        foreach (var (_, times) in kinds) pooled.AddRange(times);
        pooled.Sort();

        WriteJson(Path.Combine(outDir, "latency.json"), writer =>
        {
            writer.WriteNumber("queries", pooled.Count);
            writer.WriteStartObject("pooled");
            writer.WriteNumber("p50Ms", Round2(Percentile(pooled, 0.50)));
            writer.WriteNumber("p95Ms", Round2(Percentile(pooled, 0.95)));
            writer.WriteEndObject();
            writer.WriteStartObject("kinds");
            foreach (var (name, times) in kinds) WriteKind(writer, name, times);
            writer.WriteEndObject();
        });
    }

    private static void WriteKind(Utf8JsonWriter writer, string name, List<double> times)
    {
        var sorted = new List<double>(times);
        sorted.Sort();
        writer.WriteStartObject(name);
        writer.WriteNumber("queries", sorted.Count);
        writer.WriteNumber("p50Ms", Round2(Percentile(sorted, 0.50)));
        writer.WriteNumber("p95Ms", Round2(Percentile(sorted, 0.95)));
        writer.WriteEndObject();
    }

    /* ------------------------------------------------------------------ */
    /* merge                                                              */
    /* ------------------------------------------------------------------ */

    private sealed class MergeSummary
    {
        public int InputSegments;
        public int LiveDocs;
        public int DeadDocs;
        public double DeadRatioBefore;
        public double DeadRatioAfter;
        public double Seconds;
        public long BytesWritten;
    }

    /// <summary>给前两段各追加墓碑（约 10% 死行），再合并这两段。</summary>
    private static MergeSummary RunMerge(string indexPath)
    {
        Manifest manifest;
        using (var file = IndexFile.Open(indexPath))
            manifest = file.Current ?? throw new InvalidDataException("索引没有有效提交");

        var segments = SegmentIds(manifest);
        var inputs = segments.Take(2).ToArray();
        foreach (var segmentId in inputs)
            AppendTombs(indexPath, segmentId, (int)Section(manifest, SectionKind.DocStore, segmentId).Docs);

        var before = IndexStats.Collect(indexPath);
        var lengthBefore = new FileInfo(indexPath).Length;

        var watch = Stopwatch.StartNew();
        var result = Merger.Merge(indexPath, inputs.Select(id => (int)id).ToArray());
        watch.Stop();

        var after = IndexStats.Collect(indexPath);

        return new MergeSummary
        {
            InputSegments = inputs.Length,
            LiveDocs = result.LiveDocs,
            DeadDocs = result.DroppedDead,
            DeadRatioBefore = before.DeadRatio,
            DeadRatioAfter = after.DeadRatio,
            Seconds = watch.Elapsed.TotalSeconds,
            BytesWritten = new FileInfo(indexPath).Length - lengthBefore,
        };
    }

    private static void AppendTombs(string indexPath, ushort segmentId, int docCount)
    {
        var dead = new List<int>(docCount / 10 + 1);
        for (var doc = 0; doc < docCount; doc += 10) dead.Add(doc);
        var body = Tombstones.Write(dead);

        using var file = IndexFile.Open(indexPath);
        var manifest = file.Current ?? throw new InvalidDataException("索引没有有效提交");
        var appended = file.AppendSection(SectionKind.Tombs, body, (uint)dead.Count);
        manifest.Sections.Add(new SectionRef
        {
            Kind = appended.Kind,
            SegmentId = segmentId,
            Offset = appended.Offset,
            Length = appended.Length,
            Crc = appended.Crc,
            Docs = appended.Docs,
        });
        manifest.Gen = file.Gen + 1;
        manifest.LiveBytes += appended.Length;
        IndexBuilder.FillFileBytes(manifest, file.Length);
        file.Commit(manifest);
    }

    private static void WriteMergeJson(string outDir, MergeSummary merge)
    {
        WriteJson(Path.Combine(outDir, "merge.json"), writer =>
        {
            writer.WriteNumber("inputSegments", merge.InputSegments);
            writer.WriteNumber("liveDocs", merge.LiveDocs);
            writer.WriteNumber("deadDocs", merge.DeadDocs);
            writer.WriteNumber("deadRatioBefore", Round2(merge.DeadRatioBefore));
            writer.WriteNumber("deadRatioAfter", Round2(merge.DeadRatioAfter));
            writer.WriteNumber("seconds", Round2(merge.Seconds));
            writer.WriteNumber("bytesWritten", merge.BytesWritten);
        });
    }

    /* ------------------------------------------------------------------ */
    /* verify                                                             */
    /* ------------------------------------------------------------------ */

    private static void WriteVerifyJson(string outDir, VerifyReport report)
    {
        WriteJson(Path.Combine(outDir, "verify.json"), writer =>
        {
            writer.WriteBoolean("ok", report.Ok);
            writer.WriteNumber("sections", report.Sections);
            writer.WriteNumber("docs", report.Docs);
            writer.WriteStartArray("errors");
            foreach (var error in report.Errors) writer.WriteStringValue(error);
            writer.WriteEndArray();
        });
    }

    /* ------------------------------------------------------------------ */
    /* 共用工具                                                            */
    /* ------------------------------------------------------------------ */

    private sealed class SegmentReader
    {
        public SegmentReader(TermDictReader dict, PostingsReader postings)
        {
            Dict = dict;
            Postings = postings;
        }

        public TermDictReader Dict { get; }

        public PostingsReader Postings { get; }
    }

    /// <summary>为每个段建词典 / 倒排 reader（只读内存映射切片，计时不含这一步）。</summary>
    private static List<SegmentReader> OpenReaders(byte[] raw, Manifest manifest)
    {
        var readers = new List<SegmentReader>();
        foreach (var segmentId in SegmentIds(manifest))
        {
            var dict = new TermDictReader(
                Body(raw, Section(manifest, SectionKind.Dict, segmentId)),
                Body(raw, Section(manifest, SectionKind.DictIndex, segmentId)));
            var positions = FindSection(manifest, SectionKind.Positions, segmentId) is { } pos
                ? Body(raw, pos)
                : ReadOnlyMemory<byte>.Empty;
            readers.Add(new SegmentReader(
                dict,
                new PostingsReader(Body(raw, Section(manifest, SectionKind.Postings, segmentId)), positions)));
        }
        return readers;
    }

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = FixedFileUuid,
        CreatedMs = FixedCreatedMs,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
        // S2：与生产共用同一份默认清单，避免基准 / 生产漂移。
        NoPositionFields = BuildDefaults.CreateNoPositionFields(),
    };

    /// <summary>从 Manifest 取（排序后的）段号；只看 DocStore 段。</summary>
    private static ushort[] SegmentIds(Manifest manifest) =>
        manifest.Sections
            .Where(s => s.Kind == SectionKind.DocStore)
            .Select(s => s.SegmentId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    private static SectionRef Section(Manifest manifest, SectionKind kind, ushort segmentId) =>
        FindSection(manifest, kind, segmentId)
        ?? throw new InvalidDataException($"缺少 {kind} 段（seg={segmentId}）");

    private static SectionRef? FindSection(Manifest manifest, SectionKind kind, ushort segmentId)
    {
        foreach (var section in manifest.Sections)
            if (section.Kind == kind && section.SegmentId == segmentId) return section;
        return null;
    }

    private static ReadOnlyMemory<byte> Body(byte[] raw, SectionRef section) =>
        raw.AsMemory((int)section.Offset, (int)section.Length);

    /// <summary>按等间距从词表确定性地取 <paramref name="count"/> 条（去重）。</summary>
    private static List<string> EvenlyPick(IReadOnlyList<string> source, int count)
    {
        var picked = new List<string>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stride = Math.Max(1, source.Count / Math.Max(1, count));
        for (var i = 0; i < source.Count && picked.Count < count; i += stride)
            if (seen.Add(source[i])) picked.Add(source[i]);
        for (var i = 0; i < source.Count && picked.Count < count; i++)
            if (seen.Add(source[i])) picked.Add(source[i]);
        return picked;
    }

    /// <summary>从词表取 20 个有区分度的前缀（CJK 取 1 字，ASCII 取 2 字符）。</summary>
    private static List<string> PickPrefixes(IReadOnlyList<string> source, int count)
    {
        var picked = new List<string>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stride = Math.Max(1, source.Count / Math.Max(1, count));
        for (var i = 0; i < source.Count && picked.Count < count; i += stride)
        {
            var word = source[i];
            var length = word[0] > 0x7F ? 1 : Math.Min(2, word.Length);
            var prefix = word[..length];
            if (seen.Add(prefix)) picked.Add(prefix);
        }
        for (var i = 0; i < source.Count && picked.Count < count; i++)
        {
            var word = source[i];
            var length = word[0] > 0x7F ? 1 : Math.Min(2, word.Length);
            var prefix = word[..length];
            if (seen.Add(prefix)) picked.Add(prefix);
        }
        return picked;
    }

    /// <summary>从词表挑 CJK 单字前缀：确保 Title 下展开条数远多于 2，用于截断路径测量。</summary>
    private static List<string> CjkPrefixes(IReadOnlyList<string> source, int count)
    {
        var picked = new List<string>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stride = Math.Max(1, source.Count / Math.Max(1, count));
        for (var i = 0; i < source.Count && picked.Count < count; i += stride)
        {
            var word = source[i];
            if (word.Length == 0 || word[0] <= 0x7F) continue;
            var prefix = word[..1];
            if (seen.Add(prefix)) picked.Add(prefix);
        }
        for (var i = 0; i < source.Count && picked.Count < count; i++)
        {
            var word = source[i];
            if (word.Length == 0 || word[0] <= 0x7F) continue;
            var prefix = word[..1];
            if (seen.Add(prefix)) picked.Add(prefix);
        }
        return picked;
    }

    private static double ElapsedMs(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>已排序样本的分位值（最近秩）。</summary>
    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Round((sorted.Count - 1) * percentile, MidpointRounding.AwayFromZero);
        return sorted[index];
    }

    private static double PeakRssMb()
    {
        using var process = Process.GetCurrentProcess();
        return process.PeakWorkingSet64 / 1024.0 / 1024.0;
    }

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>紧凑 UTF-8 JSON，文件以换行结尾。</summary>
    private static void WriteJson(string path, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        var bytes = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] MakeFixedBytes(byte start)
    {
        var bytes = new byte[EraFormat.FileUuidBytes];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(start + i);
        return bytes;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响基准结论。
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}

/// <summary>
/// 确定性合成语料（xorshift64，不用 <c>Random</c>），覆盖全部 12 个字段族：
/// 元数据（Title 4–8 / Artist 2–3 / Album 1–2 / Filename 2–4）、歌词三层
/// （原文 CJK 双字、译文拉丁词、音译 ASCII 音节）、拼音 / 首字母 / 读音全开。
/// quick 缩到每首 10–20 行歌词以控构建耗时；full 用 20–40 行体现真实歌词规模。
/// </summary>
internal sealed class Corpus
{
    private static readonly string[] LatinStems =
    {
        "alpha", "alpine", "alto", "amber", "anchor", "angle",
        "beta", "better", "beyond", "bellow", "bronze", "breeze",
        "cobalt", "coast", "color", "coral", "crystal", "cinder",
        "delta", "dawn", "drift", "dusk", "dream", "dune",
        "ember", "echo", "emerald", "eagle", "evening", "estuary",
    };

    private static readonly string[] LatinSuffixes = { "", "s", "light", "fall" };

    /// <summary>歌词译文的拉丁词表（与元数据词表刻意分开，避免两族词典键重合）。</summary>
    private static readonly string[] TranslationWords =
    {
        "morning", "river", "shadow", "silver", "silent", "summer", "winter", "autumn",
        "spring", "distant", "gentle", "fading", "golden", "hollow", "island", "lantern",
        "meadow", "mirror", "ocean", "paper", "quiet", "rain", "season", "tender",
        "violet", "wander", "window", "yellow", "candle", "flight",
    };

    private const string Hanzi = "月光星海风雨雪云雾山林川河流影梦夜晨曦花火砂時空";

    /// <summary>拼音 / 音译音节（ASCII），歌词音译、拼音元数据、歌词拼音共用。</summary>
    private static readonly string[] Syllables =
    {
        "yue", "liang", "dai", "biao", "qing", "feng", "hua", "xue", "yun", "shan",
        "hai", "xing", "meng", "ye", "chen", "xi", "guang", "ying", "liu", "chuan",
        "he", "lin", "nian", "shao", "gu", "shi", "zhi", "xin", "yuan", "lai",
        "qu", "wang", "xiang", "kong", "sheng", "ming", "tian", "di", "ren", "xiao",
        "yao", "fei", "luo", "shui", "huo", "tu", "jin", "yu",
    };

    /// <summary>读音 bigram（取音节前两字母派生，形如 yu / li / da / bi）。</summary>
    private static readonly string[] ReadingBigrams = BuildReadingBigrams();

    private const string Letters = "abcdefghijklmnopqrstuvwxyz";

    private readonly int _docCount;
    private readonly int _minLyricLines;
    private readonly int _maxLyricLines;
    private readonly List<string> _vocabulary = new();
    private ulong _state = InitialState();

    public Corpus(int docCount, bool shortLyrics)
    {
        _docCount = docCount;
        _minLyricLines = shortLyrics ? 10 : 20;
        _maxLyricLines = shortLyrics ? 20 : 40;
        BuildVocabulary();
    }

    public IReadOnlyList<string> Vocabulary => _vocabulary;

    /// <summary>歌词拼音 / 元数据拼音的词表（音节级），供查询采样使用。</summary>
    public IReadOnlyList<string> PinyinVocabulary => Syllables;

    /// <summary>按固定顺序产出全部文档；<paramref name="onDoc"/> 用于统计 token 与采样短语。</summary>
    public IEnumerable<AnalyzedDoc> Docs(Action<AnalyzedDoc>? onDoc = null)
    {
        _state = InitialState();
        for (var index = 0; index < _docCount; index++)
        {
            var titleTokens = Pick(4 + Range(5));
            var artistTokens = Pick(2 + Range(2));
            var albumTokens = Pick(1 + Range(2));
            var filenameTokens = Pick(2 + Range(3));

            // 歌词三层：原文（CJK 双字）、译文（拉丁词）、音译（ASCII 音节），行数一致。
            var lines = _minLyricLines + Range(_maxLyricLines - _minLyricLines + 1);
            var lyrics = new List<string>(lines * 5);
            var translation = new List<string>(lines * 5);
            var romanization = new List<string>(lines * 6);
            for (var line = 0; line < lines; line++)
            {
                var words = 3 + Range(4);
                for (var i = 0; i < words; i++) lyrics.Add(_vocabulary[Range(_vocabulary.Count)]);
                for (var i = 0; i < words; i++) translation.Add(TranslationWords[Range(TranslationWords.Length)]);

                var syllables = 4 + Range(5);
                for (var i = 0; i < syllables; i++) romanization.Add(Syllables[Range(Syllables.Length)]);
            }

            // 拼音 / 首字母 / 读音：元数据层对齐 Title+Artist+Album，歌词层对齐歌词总词数。
            var metaCount = titleTokens.Length + artistTokens.Length + albumTokens.Length;
            var pinyinMeta = PickFrom(Syllables, metaCount);
            var pinyinLyrics = PickFrom(Syllables, lyrics.Count);
            var initialsMeta = PickLetters(metaCount);
            var initialsLyrics = PickLetters(lyrics.Count);
            var readingMeta = PickFrom(ReadingBigrams, metaCount);

            var indexBits = unchecked((ulong)index);
            var track = new byte[EraFormat.TrackIdBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(track, indexBits + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(
                track.AsSpan(8), 0xA5A5_5A5A_0000_0000UL | indexBits);

            var rev = new byte[EraFormat.TrackIdBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(rev, indexBits * InitialState() | 1UL);
            BinaryPrimitives.WriteUInt64LittleEndian(rev.AsSpan(8), 0x5555_AAAA_0000_0000UL ^ indexBits);

            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = titleTokens,
                [FieldId.Artist] = artistTokens,
                [FieldId.Album] = albumTokens,
                [FieldId.Filename] = filenameTokens,
                [FieldId.Lyrics] = lyrics.ToArray(),
                [FieldId.LyricsTranslation] = translation.ToArray(),
                [FieldId.LyricsRomanization] = romanization.ToArray(),
                [FieldId.PinyinMeta] = pinyinMeta,
                [FieldId.PinyinLyrics] = pinyinLyrics,
                [FieldId.InitialsMeta] = initialsMeta,
                [FieldId.InitialsLyrics] = initialsLyrics,
                [FieldId.ReadingMeta] = readingMeta,
            };

            var doc = new AnalyzedDoc(
                track,
                rev,
                (ushort)(index & 0x7),
                (byte)(index & 0x3),
                tokens,
                string.Join(' ', titleTokens),
                string.Join(" / ", artistTokens),
                string.Join(' ', albumTokens));

            onDoc?.Invoke(doc);
            yield return doc;
        }
    }

    private static ulong InitialState() => 0x9E37_79B9_7F4A_7C15UL;

    private void BuildVocabulary()
    {
        foreach (var stem in LatinStems)
            foreach (var suffix in LatinSuffixes)
                _vocabulary.Add(stem + suffix);

        for (var i = 0; i < Hanzi.Length; i++)
            for (var j = 0; j < Hanzi.Length; j++)
                if ((i * Hanzi.Length + j) % 3 == 0)
                    _vocabulary.Add(string.Concat(Hanzi[i], Hanzi[j]));
    }

    private static string[] BuildReadingBigrams()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var syllable in Syllables)
        {
            var bigram = syllable.Length >= 2 ? syllable[..2] : syllable + "u";
            if (seen.Add(bigram)) list.Add(bigram);
        }
        return list.ToArray();
    }

    private string[] Pick(int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++) result[i] = _vocabulary[Range(_vocabulary.Count)];
        return result;
    }

    private string[] PickFrom(IReadOnlyList<string> source, int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++) result[i] = source[Range(source.Count)];
        return result;
    }

    private string[] PickLetters(int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++) result[i] = Letters[Range(Letters.Length)].ToString();
        return result;
    }

    /// <summary>xorshift64；返回值恒非零。</summary>
    private ulong Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return _state;
    }

    private int Range(int exclusive) => (int)(Next() % (ulong)exclusive);
}
