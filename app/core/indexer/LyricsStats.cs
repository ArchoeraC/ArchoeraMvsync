// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Archoera.Lyrics;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>
/// S0 歌词分布统计（设计文档 §S0「歌词分布统计」/ §8.1 V6 / §5.3.1 已知限制）：
/// 只读 <c>library.db</c> 的 <c>tracks</c>，按**与索引器完全相同的分层口径**统计
/// 原文 / 译文 / 音译三层，产出真实库的行数分位、去重率与护栏触发数，
/// 作为 V6 阈值（4096 唯一行 / 128 KB 每层）的校准依据。
///
/// 分层口径与 <see cref="IndexerEngine"/> 的 <c>BuildTokens</c> 逐字一致：
/// <list type="bullet">
///   <item>内嵌 <c>tracks.lyrics</c> → <see cref="LrcReader.SplitText"/>；</item>
///   <item>侧车 <c>lyrics_lrc</c> / <c>lyrics_lrc_trans</c> / <c>lyrics_lrc_romaji</c>
///         → <see cref="LrcReader.FromRawTexts"/>（旧库无侧车列 / <c>--no-lrc</c> 时为空层）；</item>
///   <item>每层最终文本 = <see cref="Archoera.Lyrics.LrcReader.ScoreLayers"/>（T4.2：
///         同层按置信度取一份、不拼接；与索引器同一函数）。</item>
/// </list>
///
/// 统计项（逐曲逐层）：原始行数（按 <c>\n</c> 切、trim 后非空）、**归一化键去重**后的唯一行数、
/// 原始 / 去重后 UTF-8 字节数。分位数**只在有该层的曲目上**按 nearest-rank 计算
/// （0 值不参与，避免把 p50 压成 0）。
///
/// <para>去重键与护栏口径**直接调用 <see cref="LyricsPreprocess"/>**（与索引路径同源）：
/// 键 = <see cref="Analyzer.Normalize"/>（大小写 / 全半角 / 异体 / 繁简收敛），护栏按
/// **去重后**的行数 / 字节数判定。故 <c>rawTotal - uniqueTotal</c> 与
/// <c>guardTriggers</c> 和真实索引的压缩 / 截断一一对应。</para>
///
/// stdout：一条简短汇总 JSON（含 <c>--out</c> 路径）；stderr：仅错误与告警。
/// exit code：0=成功，1=参数 / 运行错误。输出 JSON 只写库文件名，**不含绝对路径**（隐私）。
/// </summary>
internal static class LyricsStats
{
    /// <summary>V6 护栏：每层去重行数上限（§8.1 V6；与索引路径同源）。</summary>
    private const int GuardLineLimit = LyricsPreprocess.MaxUniqueLines;

    /// <summary>V6 护栏：每层去重后 UTF-8 字节上限（§8.1 V6；与索引路径同源）。</summary>
    private const int GuardByteLimit = LyricsPreprocess.MaxUtf8Bytes;

    private const string Usage = """
        用法: indexer lyrics stats --library <library.db> [--out <json>] [--no-lrc]
          --library   只读打开的真实曲库（scanner 写入的 library.db）
          --out       可选：把完整统计写入该 JSON 文件（缺省只打 stdout 汇总）
          --no-lrc    只统计内嵌 lyrics（默认包含 .lrc 侧车层）
          --help      打印本段用法
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(Usage);
            return 0;
        }

        if (!string.Equals(args[0], "stats", StringComparison.OrdinalIgnoreCase))
            return Fail($"未知 lyrics 子命令: {args[0]}");

        string? libraryPath = null;
        string? outPath = null;
        var noLrc = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--library" when i + 1 < args.Length: libraryPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--no-lrc": noLrc = true; break;
                case "-h" or "--help":
                    Console.Error.WriteLine(Usage);
                    return 0;
            }
        }

        if (libraryPath is null)
            return Fail("缺少 --library <library.db>");
        if (!File.Exists(libraryPath))
            return Fail($"曲库不存在: {libraryPath}");

        return RunStats(libraryPath, outPath, includeSidecar: !noLrc);
    }

    private static int RunStats(string libraryPath, string? outPath, bool includeSidecar)
    {
        try
        {
            var counters = Count(libraryPath, includeSidecar, out var lrcColumns);
            var effectiveSidecar = includeSidecar && lrcColumns;
            var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var json = WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("schema", 1);
                w.WriteString("generatedAt", generatedAt);

                w.WritePropertyName("library");
                w.WriteStartObject();
                w.WriteString("pathName", Path.GetFileName(libraryPath));
                w.WriteNumber("tracks", counters.Tracks);
                w.WriteBoolean("lrcColumns", lrcColumns);
                w.WriteBoolean("includeSidecar", effectiveSidecar);
                w.WritePropertyName("guard");
                w.WriteStartObject();
                w.WriteNumber("lines", GuardLineLimit);
                w.WriteNumber("bytes", GuardByteLimit);
                w.WriteEndObject();
                w.WriteEndObject();

                w.WritePropertyName("layers");
                w.WriteStartObject();
                WriteLayer(w, "original", counters.Original, counters.Tracks);
                WriteLayer(w, "translation", counters.Translation, counters.Tracks);
                WriteLayer(w, "romaji", counters.Romaji, counters.Tracks);
                w.WritePropertyName("combined");
                w.WriteStartObject();
                WriteGuards(w, counters.CombinedLines, counters.CombinedBytes, counters.CombinedAny);
                w.WriteEndObject();
                w.WriteEndObject();

                w.WriteEndObject();
            });

            var fullOut = "";
            if (outPath is not null)
            {
                fullOut = Path.GetFullPath(outPath);
                var outDir = Path.GetDirectoryName(fullOut);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                File.WriteAllBytes(fullOut, Encoding.UTF8.GetBytes(json));
            }

            Console.WriteLine(WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("schema", 1);
                w.WriteNumber("tracks", counters.Tracks);
                w.WriteBoolean("lrcColumns", lrcColumns);
                w.WriteBoolean("includeSidecar", effectiveSidecar);
                w.WritePropertyName("layers");
                w.WriteStartObject();
                WriteSummaryLayer(w, "original", counters.Original);
                WriteSummaryLayer(w, "translation", counters.Translation);
                WriteSummaryLayer(w, "romaji", counters.Romaji);
                w.WriteEndObject();
                WriteGuards(w, counters.CombinedLines, counters.CombinedBytes, counters.CombinedAny);
                w.WriteString("out", fullOut);
                w.WriteEndObject();
            }));
            return 0;
        }
        catch (Exception ex) when (ex is SqliteException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Fail($"统计歌词分布失败: {ex.Message}");
        }
    }

    /* ------------------------------------------------------------------ */
    /* 统计                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>逐曲逐层统计的累加器（只保留「有该层」曲目的样本，供热路径外的分位数使用）。</summary>
    private sealed class LayerAccumulator
    {
        private readonly List<int> _lineSamples = new();
        private readonly List<int> _uniqueSamples = new();
        private readonly List<int> _byteSamples = new();
        private readonly List<int> _dedupedByteSamples = new();

        /// <summary>该层非空的曲目数（分位数 / emptyRatio 的分母）。</summary>
        public int TracksWithLayer { get; private set; }

        /// <summary>原始行数总和（含重复行）。</summary>
        public long RawTotal { get; private set; }

        /// <summary>归一化键去重后的行数总和。</summary>
        public long UniqueTotal { get; private set; }

        /// <summary>去重后行数超护栏的曲目数。</summary>
        public int GuardLines { get; private set; }

        /// <summary>去重后 UTF-8 字节超护栏的曲目数。</summary>
        public int GuardBytes { get; private set; }

        /// <summary>任一项超护栏的曲目数。</summary>
        public int GuardAny { get; private set; }

        public Distribution Lines => Distribution.Of(_lineSamples);
        public Distribution Unique => Distribution.Of(_uniqueSamples);
        public Distribution Bytes => Distribution.Of(_byteSamples);
        public Distribution DedupedBytes => Distribution.Of(_dedupedByteSamples);

        /// <summary>累加一首曲目的一层最终文本；返回该曲该层是否触发护栏（供 combined 去重计数）。</summary>
        public (bool Lines, bool Bytes) Add(string text)
        {
            var measurement = Measure(text);
            var layer = measurement.Layer;
            RawTotal += layer.RawLines;
            UniqueTotal += layer.UniqueLines;

            // 空层（无歌词 / 只有空白）不进入分位样本，避免 0 把 p50 压成 0。
            if (layer.RawLines == 0) return (false, false);

            TracksWithLayer++;
            _lineSamples.Add(layer.RawLines);
            _uniqueSamples.Add(layer.UniqueLines);
            _byteSamples.Add(measurement.RawBytes);
            _dedupedByteSamples.Add(layer.DedupedBytes);

            // 护栏口径 = LyricsPreprocess（去重后判行数 / 字节），与索引路径逐字一致。
            var lineHit = layer.TruncatedByLines;
            var byteHit = layer.TruncatedByBytes;
            if (lineHit) GuardLines++;
            if (byteHit) GuardBytes++;
            if (lineHit || byteHit) GuardAny++;
            return (lineHit, byteHit);
        }
    }

    /// <summary>三层累加结果 + 曲目数 + combined 护栏计数（同一首曲只计一次）。</summary>
    private sealed class StatsCounters
    {
        public int Tracks { get; init; }
        public LayerAccumulator Original { get; } = new();
        public LayerAccumulator Translation { get; } = new();
        public LayerAccumulator Romaji { get; } = new();
        public int CombinedLines { get; set; }
        public int CombinedBytes { get; set; }
        public int CombinedAny { get; set; }
    }

    /// <summary>
    /// 只读遍历 <c>tracks</c>：按索引器同一口径分层，逐曲累加三层统计。
    /// 旧库无侧车列时只读 <c>lyrics</c>（侧车层恒为空），并把 <paramref name="lrcColumns"/> 置 false。
    /// </summary>
    private static StatsCounters Count(string libraryPath, bool includeSidecar, out bool lrcColumns)
    {
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryPath, readOnly: true));
        conn.Open();

        // 三列全在才算「新库」：缺任意一列即按旧库口径（只有 tracks.lyrics）。
        lrcColumns = HasLrcColumns(conn);
        var readSidecar = includeSidecar && lrcColumns;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = readSidecar
            ? "SELECT lyrics, lyrics_lrc, lyrics_lrc_trans, lyrics_lrc_romaji FROM tracks"
            : "SELECT lyrics FROM tracks";

        var counters = new StatsCounters
        {
            // 曲目数先由独立 COUNT 求得，避免把「读歌词」与「计数」混在一个游标里（流式读取不缓存行）。
            Tracks = CountTracks(conn),
        };

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var lyrics = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var lrc = "";
            var trans = "";
            var romaji = "";
            if (readSidecar)
            {
                lrc = reader.IsDBNull(1) ? "" : reader.GetString(1);
                trans = reader.IsDBNull(2) ? "" : reader.GetString(2);
                romaji = reader.IsDBNull(3) ? "" : reader.GetString(3);
            }

            // 与 IndexerEngine.BuildTokens 同源（T4.2）：分层 + 置信度选层后取每层最终文本。
            var scored = LrcReader.ScoreLayers(
                lyrics,
                readSidecar ? lrc : null,
                readSidecar && trans.Length > 0 ? trans : null,
                readSidecar && romaji.Length > 0 ? romaji : null);

            var original = scored.Original.Text;
            var translation = scored.Translation.Text;
            var romanized = scored.Romaji.Text;

            var (originalLines, originalBytes) = counters.Original.Add(original);
            var (translationLines, translationBytes) = counters.Translation.Add(translation);
            var (romajiLines, romajiBytes) = counters.Romaji.Add(romanized);

            // combined 是「任意层触发」的**曲目数**（不在层间累加同一首曲）。
            if (originalLines || translationLines || romajiLines) counters.CombinedLines++;
            if (originalBytes || translationBytes || romajiBytes) counters.CombinedBytes++;
            if (originalLines || originalBytes
                || translationLines || translationBytes
                || romajiLines || romajiBytes)
            {
                counters.CombinedAny++;
            }
        }

        return counters;
    }

    /// <summary>打开的只读连接上判断 <c>tracks</c> 是否带三列侧车（缺一即旧库）。</summary>
    private static bool HasLrcColumns(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('tracks') "
            + "WHERE name IN ('lyrics_lrc','lyrics_lrc_trans','lyrics_lrc_romaji')";
        return Convert.ToInt64(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) >= 3;
    }

    private static int CountTracks(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks";
        var value = cmd.ExecuteScalar();
        return value is null || value is DBNull
            ? 0
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 行数 / 字节测量：直接复用 <see cref="LyricsPreprocess"/>（trim / 丢空行 → 归一化键去重
    /// → V6 护栏），故 <c>Unique</c> 与 <c>Truncated*</c> 和索引路径逐字一致。
    /// <c>RawBytes</c> 保留「去重前整层 UTF-8 字节」作为描述性分布；护栏用的是去重后字节。
    /// </summary>
    private static (LyricLayerText Layer, int RawBytes) Measure(string text)
    {
        if (text.Length == 0) return (new LyricLayerText { Text = "" }, 0);
        return (LyricsPreprocess.Apply(text), Encoding.UTF8.GetByteCount(text));
    }

    /* ------------------------------------------------------------------ */
    /* 分位数                                                              */
    /* ------------------------------------------------------------------ */

    /// <summary>nearest-rank 分位（rank = ceil(p/100*n)，索引 min(n-1, rank-1)）；空样本为 0。</summary>
    private readonly record struct Distribution(int P50, int P95, int P99, int Max)
    {
        public static Distribution Of(List<int> samples)
        {
            if (samples.Count == 0) return default;
            var sorted = samples.ToArray();
            Array.Sort(sorted);
            return new Distribution(
                NearestRank(sorted, 50),
                NearestRank(sorted, 95),
                NearestRank(sorted, 99),
                sorted[^1]);
        }

        private static int NearestRank(int[] sorted, int percentile)
        {
            var rank = ((long)percentile * sorted.Length + 99) / 100; // ceil(p/100 * n)
            if (rank < 1) rank = 1;
            var index = (int)Math.Min(sorted.Length - 1L, rank - 1L);
            return sorted[index];
        }
    }

    /* ------------------------------------------------------------------ */
    /* JSON                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>单层完整统计（键名与设计文档 §S0 固定口径一致）。</summary>
    private static void WriteLayer(Utf8JsonWriter w, string name, LayerAccumulator layer, int tracks)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WriteNumber("tracksWithLayer", layer.TracksWithLayer);
        // 无该层的曲目占比；空库按 0（没有可空的分母）。
        WriteRatio(w, "emptyRatio", tracks == 0 ? 0.0 : 1.0 - (double)layer.TracksWithLayer / tracks);

        WriteDistribution(w, "lines", layer.Lines);
        WriteDistribution(w, "uniqueLines", layer.Unique);
        // bytes = 去重前整层字节（描述性）；dedupedBytes = 护栏实际判定的去重后字节。
        WriteDistribution(w, "bytes", layer.Bytes);
        WriteDistribution(w, "dedupedBytes", layer.DedupedBytes);

        w.WriteNumber("rawTotal", layer.RawTotal);
        w.WriteNumber("uniqueTotal", layer.UniqueTotal);
        WriteRatio(w, "dedupeRatio",
            layer.RawTotal == 0 ? 0.0 : 1.0 - (double)layer.UniqueTotal / layer.RawTotal);

        WriteGuards(w, layer.GuardLines, layer.GuardBytes, layer.GuardAny);
        w.WriteEndObject();
    }

    private static void WriteDistribution(Utf8JsonWriter w, string name, Distribution distribution)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WriteNumber("p50", distribution.P50);
        w.WriteNumber("p95", distribution.P95);
        w.WriteNumber("p99", distribution.P99);
        w.WriteNumber("max", distribution.Max);
        w.WriteEndObject();
    }

    private static void WriteGuards(Utf8JsonWriter w, int lines, int bytes, int any)
    {
        w.WritePropertyName("guardTriggers");
        w.WriteStartObject();
        w.WriteNumber("lines", lines);
        w.WriteNumber("bytes", bytes);
        w.WriteNumber("any", any);
        w.WriteEndObject();
    }

    /// <summary>stdout 汇总的单层：只留 tracksWithLayer / p95 行数 / 去重率。</summary>
    private static void WriteSummaryLayer(Utf8JsonWriter w, string name, LayerAccumulator layer)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WriteNumber("tracksWithLayer", layer.TracksWithLayer);
        w.WriteNumber("p95Lines", layer.Lines.P95);
        WriteRatio(w, "dedupeRatio",
            layer.RawTotal == 0 ? 0.0 : 1.0 - (double)layer.UniqueTotal / layer.RawTotal);
        w.WriteEndObject();
    }

    /// <summary>比率统一 4 位小数（WriteNumber(double) 会省略末尾 0，故写原始字面量）。</summary>
    private static void WriteRatio(Utf8JsonWriter w, string name, double value)
    {
        w.WritePropertyName(name);
        w.WriteRawValue(value.ToString("F4", CultureInfo.InvariantCulture));
    }

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
        Log.LogError("lyrics-stats", message);
        return 1;
    }
}
