// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Tests;

/// <summary>
/// <see cref="LyricsStats"/>（S0 歌词分布统计）的回归断言：分层口径与索引器一致、
/// 分位数按 nearest-rank、护栏按层独立计数、旧库自动降级、输出不含绝对路径。
///
/// 全部用临时目录里的**合成** fixture（三首确定性曲目 + 护栏样本），不读真实用户库、不访问网络。
/// </summary>
internal static class LyricsStatsTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-lyrics-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            NewLibrary(root);
            LegacyLibrary(root);
            GuardTriggers(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 新库（含三列侧车）：分层口径 + 分位数 + 去重率 + 隐私              */
    /* ------------------------------------------------------------------ */

    private static void NewLibrary(string root)
    {
        var db = Path.Combine(root, "new.db");
        var outJson = Path.Combine(root, "new.json");
        var noLrcJson = Path.Combine(root, "new-nolrc.json");

        CreateLibrary(db, withLrc: true);
        using (var conn = Open(db))
        {
            // A：内嵌原文 5 行，其中 2 行重复（原始 5 / 去重 3）。
            InsertTrack(conn, "A", "/music/a.flac",
                "第一行\n第二行\n第三行\n第一行\n第三行");
            // B：只有侧车（原文 5 行 + 译文 2 行），验证 JoinLayers 用侧车层。
            InsertSidecarTrack(conn, "B", "/music/b.flac", null,
                "b-one\nb-two\nb-three\nb-four\nb-five", "译文一\n译文二", null);
            // C：无任何歌词（不计入任何层的 tracksWithLayer）。
            InsertTrack(conn, "C", "/music/c.flac", null);
        }

        var code = RunStats(db, outJson);
        Program.Check("lyrics stats 新库 exit 0", code == 0, code.ToString(CultureInfo.InvariantCulture));

        var json = Load(outJson);
        Program.Check("lyrics stats tracks == 3", IntAt(json, "library/tracks") == 3,
            Str(IntAt(json, "library/tracks")));
        Program.Check("lyrics stats lrcColumns == true", BoolAt(json, "library/lrcColumns"),
            BoolAt(json, "library/lrcColumns").ToString());
        Program.Check("lyrics stats includeSidecar == true", BoolAt(json, "library/includeSidecar"));

        // 护栏常量与 §8.1 V6 一致。
        Program.Check("lyrics stats guard.lines == 4096", IntAt(json, "library/guard/lines") == 4096,
            Str(IntAt(json, "library/guard/lines")));
        Program.Check("lyrics stats guard.bytes == 131072", IntAt(json, "library/guard/bytes") == 131072,
            Str(IntAt(json, "library/guard/bytes")));

        // generatedAt 是 UTC ISO（Replay 同格式）。
        Program.Check("lyrics stats generatedAt 可解析",
            DateTime.TryParse(StrAt(json, "generatedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _),
            StrAt(json, "generatedAt"));

        // ---- original：A(5/3) + B(5/5)，C 无层 ----
        Program.Check("original tracksWithLayer == 2",
            IntAt(json, "layers/original/tracksWithLayer") == 2,
            Str(IntAt(json, "layers/original/tracksWithLayer")));
        Program.Check("original rawTotal == 10", IntAt(json, "layers/original/rawTotal") == 10,
            Str(IntAt(json, "layers/original/rawTotal")));
        Program.Check("original uniqueTotal == 8", IntAt(json, "layers/original/uniqueTotal") == 8,
            Str(IntAt(json, "layers/original/uniqueTotal")));
        Program.Check("original dedupeRatio == 0.2000",
            Near(DoubleAt(json, "layers/original/dedupeRatio"), 0.2),
            StrDouble(DoubleAt(json, "layers/original/dedupeRatio")));
        Program.Check("original emptyRatio == 0.3333",
            Near(DoubleAt(json, "layers/original/emptyRatio"), 1.0 / 3.0),
            StrDouble(DoubleAt(json, "layers/original/emptyRatio")));
        Program.Check("original lines p50/p95/p99/max == 5",
            IntAt(json, "layers/original/lines/p50") == 5
                && IntAt(json, "layers/original/lines/p95") == 5
                && IntAt(json, "layers/original/lines/p99") == 5
                && IntAt(json, "layers/original/lines/max") == 5,
            Str(IntAt(json, "layers/original/lines/p95")));
        // nearest-rank：n=2，p50 → 第 1 个（3 行）；p95 → 第 2 个（5 行）。
        Program.Check("original uniqueLines p50 == 3", IntAt(json, "layers/original/uniqueLines/p50") == 3,
            Str(IntAt(json, "layers/original/uniqueLines/p50")));
        Program.Check("original uniqueLines p95/p99/max == 5",
            IntAt(json, "layers/original/uniqueLines/p95") == 5
                && IntAt(json, "layers/original/uniqueLines/p99") == 5
                && IntAt(json, "layers/original/uniqueLines/max") == 5,
            Str(IntAt(json, "layers/original/uniqueLines/p95")));
        // A = 15 汉字（45 B）+ 4 换行 = 49 B；B = 29 B + 4 换行 = 33 B。
        Program.Check("original bytes p50 == 33", IntAt(json, "layers/original/bytes/p50") == 33,
            Str(IntAt(json, "layers/original/bytes/p50")));
        Program.Check("original bytes p95/p99/max == 49",
            IntAt(json, "layers/original/bytes/p95") == 49
                && IntAt(json, "layers/original/bytes/p99") == 49
                && IntAt(json, "layers/original/bytes/max") == 49,
            Str(IntAt(json, "layers/original/bytes/p95")));
        // 护栏判定用**去重后**字节（A = 29 B，B = 33 B）。
        Program.Check("original dedupedBytes p50/p95 == 29/33",
            IntAt(json, "layers/original/dedupedBytes/p50") == 29
                && IntAt(json, "layers/original/dedupedBytes/p95") == 33,
            $"{IntAt(json, "layers/original/dedupedBytes/p50")}/"
                + $"{IntAt(json, "layers/original/dedupedBytes/p95")}");

        // ---- translation：只有 B ----
        Program.Check("translation tracksWithLayer == 1",
            IntAt(json, "layers/translation/tracksWithLayer") == 1,
            Str(IntAt(json, "layers/translation/tracksWithLayer")));
        Program.Check("translation rawTotal == 2", IntAt(json, "layers/translation/rawTotal") == 2,
            Str(IntAt(json, "layers/translation/rawTotal")));
        Program.Check("translation uniqueTotal == 2", IntAt(json, "layers/translation/uniqueTotal") == 2,
            Str(IntAt(json, "layers/translation/uniqueTotal")));
        Program.Check("translation dedupeRatio == 0.0000",
            Near(DoubleAt(json, "layers/translation/dedupeRatio"), 0.0),
            StrDouble(DoubleAt(json, "layers/translation/dedupeRatio")));
        Program.Check("translation emptyRatio == 0.6667",
            Near(DoubleAt(json, "layers/translation/emptyRatio"), 2.0 / 3.0),
            StrDouble(DoubleAt(json, "layers/translation/emptyRatio")));
        Program.Check("translation lines/unique/bytes == 2/2/19",
            IntAt(json, "layers/translation/lines/p50") == 2
                && IntAt(json, "layers/translation/uniqueLines/p50") == 2
                && IntAt(json, "layers/translation/bytes/p50") == 19,
            $"{IntAt(json, "layers/translation/lines/p50")}/"
                + $"{IntAt(json, "layers/translation/uniqueLines/p50")}/"
                + $"{IntAt(json, "layers/translation/bytes/p50")}");

        // ---- romaji：无该层（空样本，分位与比率不因 0 被压低） ----
        Program.Check("romaji tracksWithLayer == 0",
            IntAt(json, "layers/romaji/tracksWithLayer") == 0,
            Str(IntAt(json, "layers/romaji/tracksWithLayer")));
        Program.Check("romaji 空层分位为 0",
            IntAt(json, "layers/romaji/lines/p95") == 0
                && IntAt(json, "layers/romaji/bytes/p95") == 0,
            $"{IntAt(json, "layers/romaji/lines/p95")}/{IntAt(json, "layers/romaji/bytes/p95")}");
        Program.Check("romaji dedupeRatio == 0.0000（rawTotal 0）",
            Near(DoubleAt(json, "layers/romaji/dedupeRatio"), 0.0),
            StrDouble(DoubleAt(json, "layers/romaji/dedupeRatio")));
        Program.Check("romaji emptyRatio == 1.0000",
            Near(DoubleAt(json, "layers/romaji/emptyRatio"), 1.0),
            StrDouble(DoubleAt(json, "layers/romaji/emptyRatio")));

        // ---- 正常数据不触发 V6 ----
        Program.Check("正常数据 guardTriggers 全 0",
            IntAt(json, "layers/original/guardTriggers/lines") == 0
                && IntAt(json, "layers/original/guardTriggers/bytes") == 0
                && IntAt(json, "layers/original/guardTriggers/any") == 0
                && IntAt(json, "layers/translation/guardTriggers/any") == 0
                && IntAt(json, "layers/romaji/guardTriggers/any") == 0
                && IntAt(json, "layers/combined/guardTriggers/any") == 0,
            Str(IntAt(json, "layers/combined/guardTriggers/any")));

        // ---- 隐私：完整 JSON 只含文件名，不含绝对路径 ----
        var raw = File.ReadAllText(outJson);
        Program.Check("pathName 只写文件名", StrAt(json, "library/pathName") == "new.db",
            StrAt(json, "library/pathName"));
        Program.Check("输出 JSON 不含曲库绝对路径",
            !raw.Contains(Path.GetFullPath(db), StringComparison.Ordinal),
            raw.Contains(Path.GetFullPath(db), StringComparison.Ordinal) ? "含绝对路径" : raw.Length.ToString());

        // ---- --no-lrc：只看内嵌（A 原文 5/3；B 无内嵌 → 无层） ----
        var noLrcCode = RunStats(db, noLrcJson, noLrc: true);
        Program.Check("lyrics stats --no-lrc exit 0", noLrcCode == 0,
            noLrcCode.ToString(CultureInfo.InvariantCulture));
        var noLrc = Load(noLrcJson);
        Program.Check("--no-lrc includeSidecar == false", !BoolAt(noLrc, "library/includeSidecar"));
        Program.Check("--no-lrc lrcColumns == true（库仍有侧车列）", BoolAt(noLrc, "library/lrcColumns"));
        Program.Check("--no-lrc original tracksWithLayer == 1",
            IntAt(noLrc, "layers/original/tracksWithLayer") == 1,
            Str(IntAt(noLrc, "layers/original/tracksWithLayer")));
        Program.Check("--no-lrc original rawTotal/uniqueTotal == 5/3",
            IntAt(noLrc, "layers/original/rawTotal") == 5
                && IntAt(noLrc, "layers/original/uniqueTotal") == 3,
            $"{IntAt(noLrc, "layers/original/rawTotal")}/{IntAt(noLrc, "layers/original/uniqueTotal")}");
        Program.Check("--no-lrc translation tracksWithLayer == 0",
            IntAt(noLrc, "layers/translation/tracksWithLayer") == 0,
            Str(IntAt(noLrc, "layers/translation/tracksWithLayer")));
        Program.Check("--no-lrc romaji tracksWithLayer == 0",
            IntAt(noLrc, "layers/romaji/tracksWithLayer") == 0,
            Str(IntAt(noLrc, "layers/romaji/tracksWithLayer")));
    }

    /* ------------------------------------------------------------------ */
    /* 旧库（只有 lyrics 列）：降级为「无侧车」                              */
    /* ------------------------------------------------------------------ */

    private static void LegacyLibrary(string root)
    {
        var db = Path.Combine(root, "legacy.db");
        var outJson = Path.Combine(root, "legacy.json");

        CreateLibrary(db, withLrc: false);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "A", "/music/a.flac",
                "第一行\n第二行\n第三行\n第一行\n第三行");
            InsertTrack(conn, "C", "/music/c.flac", null);
        }

        var code = RunStats(db, outJson);
        Program.Check("旧库 stats exit 0", code == 0, code.ToString(CultureInfo.InvariantCulture));

        var json = Load(outJson);
        Program.Check("旧库 lrcColumns == false", !BoolAt(json, "library/lrcColumns"));
        Program.Check("旧库 includeSidecar == false", !BoolAt(json, "library/includeSidecar"));
        Program.Check("旧库只统计 original tracksWithLayer == 1",
            IntAt(json, "layers/original/tracksWithLayer") == 1,
            Str(IntAt(json, "layers/original/tracksWithLayer")));
        Program.Check("旧库 original rawTotal/uniqueTotal == 5/3",
            IntAt(json, "layers/original/rawTotal") == 5
                && IntAt(json, "layers/original/uniqueTotal") == 3,
            $"{IntAt(json, "layers/original/rawTotal")}/{IntAt(json, "layers/original/uniqueTotal")}");
        Program.Check("旧库 original dedupeRatio == 0.4000",
            Near(DoubleAt(json, "layers/original/dedupeRatio"), 0.4),
            StrDouble(DoubleAt(json, "layers/original/dedupeRatio")));
        Program.Check("旧库 translation 无样本",
            IntAt(json, "layers/translation/tracksWithLayer") == 0
                && IntAt(json, "layers/translation/rawTotal") == 0,
            Str(IntAt(json, "layers/translation/tracksWithLayer")));
        Program.Check("旧库 romaji 无样本",
            IntAt(json, "layers/romaji/tracksWithLayer") == 0
                && IntAt(json, "layers/romaji/rawTotal") == 0,
            Str(IntAt(json, "layers/romaji/tracksWithLayer")));
    }

    /* ------------------------------------------------------------------ */
    /* V6 护栏：超 4096 去重行 / 超 128 KB 字节                             */
    /* ------------------------------------------------------------------ */

    private static void GuardTriggers(string root)
    {
        var db = Path.Combine(root, "guard.db");
        var outJson = Path.Combine(root, "guard.json");

        // 4097 条唯一短行：只触发行数护栏（字节远小于 128 KB）。
        var manyLines = string.Join('\n', Enumerable.Range(0, 4097).Select(i => "L" + i));
        // 4096 行 × 34 B（10 汉字 + 4 位序号、每行唯一）：行数正好在护栏内，
        // 只有去重后字节 143,359 > 131,072，故只触发字节护栏。
        // 旧夹具用 100 条相同长行，Ordinal 去重后只剩 1 行；索引路径按归一化键去重，
        // 护栏判定的是去重后大小，故必须用唯一行才能覆盖「去重后仍超字节」的病态输入。
        var manyBytes = string.Join('\n', Enumerable.Range(0, 4096)
            .Select(i => new string('歌', 10) + i.ToString("D4")));

        CreateLibrary(db, withLrc: true);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "lines", "/music/lines.flac", manyLines);
            InsertTrack(conn, "bytes", "/music/bytes.flac", manyBytes);
        }

        var code = RunStats(db, outJson);
        Program.Check("护栏 stats exit 0", code == 0, code.ToString(CultureInfo.InvariantCulture));

        var json = Load(outJson);
        Program.Check("护栏 original lines 触发 1 首",
            IntAt(json, "layers/original/guardTriggers/lines") == 1,
            Str(IntAt(json, "layers/original/guardTriggers/lines")));
        Program.Check("护栏 original bytes 触发 1 首",
            IntAt(json, "layers/original/guardTriggers/bytes") == 1,
            Str(IntAt(json, "layers/original/guardTriggers/bytes")));
        Program.Check("护栏 original any 触发 2 首",
            IntAt(json, "layers/original/guardTriggers/any") == 2,
            Str(IntAt(json, "layers/original/guardTriggers/any")));
        Program.Check("护栏 combined 按曲目计（1/1/2）",
            IntAt(json, "layers/combined/guardTriggers/lines") == 1
                && IntAt(json, "layers/combined/guardTriggers/bytes") == 1
                && IntAt(json, "layers/combined/guardTriggers/any") == 2,
            $"{IntAt(json, "layers/combined/guardTriggers/lines")}/"
                + $"{IntAt(json, "layers/combined/guardTriggers/bytes")}/"
                + $"{IntAt(json, "layers/combined/guardTriggers/any")}");
        Program.Check("护栏 rawTotal/uniqueTotal == 8193/8193",
            IntAt(json, "layers/original/rawTotal") == 8193
                && IntAt(json, "layers/original/uniqueTotal") == 8193,
            $"{IntAt(json, "layers/original/rawTotal")}/{IntAt(json, "layers/original/uniqueTotal")}");
        Program.Check("护栏无重复行 → dedupeRatio == 0.0000",
            Near(DoubleAt(json, "layers/original/dedupeRatio"), 0.0),
            StrDouble(DoubleAt(json, "layers/original/dedupeRatio")));
        // 护栏判定在**去重后**：长行曲目 dedupedBytes == 143,359（> 131,072）。
        Program.Check("护栏 dedupedBytes max == 143359",
            IntAt(json, "layers/original/dedupedBytes/max") == 143359,
            Str(IntAt(json, "layers/original/dedupedBytes/max")));
        // nearest-rank：[4096, 4097] → p50 = 4096，p95 = 4097。
        Program.Check("护栏 lines p50 == 4096 / p95 == 4097",
            IntAt(json, "layers/original/lines/p50") == 4096
                && IntAt(json, "layers/original/lines/p95") == 4097,
            $"{IntAt(json, "layers/original/lines/p50")}/{IntAt(json, "layers/original/lines/p95")}");
        Program.Check("护栏 translation/romaji 不触发",
            IntAt(json, "layers/translation/guardTriggers/any") == 0
                && IntAt(json, "layers/romaji/guardTriggers/any") == 0,
            $"{IntAt(json, "layers/translation/guardTriggers/any")}/"
                + $"{IntAt(json, "layers/romaji/guardTriggers/any")}");
    }

    /* ------------------------------------------------------------------ */
    /* fixture / 断言小工具                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>与 scanner 同栈的 tracks 表（旧库版无侧车列，用于验证降级）。</summary>
    private static void CreateLibrary(string path, bool withLrc)
    {
        if (File.Exists(path)) File.Delete(path);
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = withLrc
            ? """
              CREATE TABLE tracks (
                  id TEXT PRIMARY KEY, path TEXT NOT NULL, title TEXT NOT NULL, lyrics TEXT,
                  lyrics_lrc TEXT, lyrics_lrc_trans TEXT, lyrics_lrc_romaji TEXT
              )
              """
            : """
              CREATE TABLE tracks (
                  id TEXT PRIMARY KEY, path TEXT NOT NULL, title TEXT NOT NULL, lyrics TEXT
              )
              """;
        cmd.ExecuteNonQuery();
    }

    private static void InsertTrack(SqliteConnection conn, string id, string path, string? lyrics)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tracks (id, path, title, lyrics) VALUES (@id, @path, @title, @lyrics)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@title", id);
        cmd.Parameters.AddWithValue("@lyrics", (object?)lyrics ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSidecarTrack(
        SqliteConnection conn, string id, string path, string? lyrics,
        string? lrc, string? trans, string? romaji)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tracks (id, path, title, lyrics, lyrics_lrc, lyrics_lrc_trans, lyrics_lrc_romaji)
            VALUES (@id, @path, @title, @lyrics, @lrc, @trans, @romaji)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@title", id);
        cmd.Parameters.AddWithValue("@lyrics", (object?)lyrics ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lrc", (object?)lrc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@trans", (object?)trans ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@romaji", (object?)romaji ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(LibraryDb.ConnectionString(path));
        conn.Open();
        return conn;
    }

    /// <summary>按 CLI 同构参数调用 <see cref="LyricsStats.Run"/>（stats 子命令）。</summary>
    private static int RunStats(string libraryPath, string? outPath = null, bool noLrc = false)
    {
        var args = new List<string> { "stats", "--library", libraryPath };
        if (outPath is not null)
        {
            args.Add("--out");
            args.Add(outPath);
        }
        if (noLrc) args.Add("--no-lrc");
        return LyricsStats.Run(args.ToArray());
    }

    private static JsonNode Load(string path)
        => JsonNode.Parse(File.ReadAllText(path))
           ?? throw new InvalidOperationException($"歌词统计 JSON 为空: {path}");

    private static JsonNode? At(JsonNode root, string pointer)
    {
        var current = root;
        foreach (var part in pointer.Split('/'))
        {
            if (current is null) return null;
            current = current[part];
        }
        return current;
    }

    private static int IntAt(JsonNode root, string pointer)
        => At(root, pointer)?.GetValue<int>() ?? int.MinValue;

    private static double DoubleAt(JsonNode root, string pointer)
        => At(root, pointer)?.GetValue<double>() ?? double.NaN;

    private static bool BoolAt(JsonNode root, string pointer)
        => At(root, pointer)?.GetValue<bool>() ?? false;

    private static string StrAt(JsonNode root, string pointer)
        => At(root, pointer)?.GetValue<string>() ?? "";

    /// <summary>4 位小数比较容差（0.00005 覆盖「F4 四舍五入」的最大偏差）。</summary>
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.00005;

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string StrDouble(double value)
        => value.ToString("F4", CultureInfo.InvariantCulture);

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
