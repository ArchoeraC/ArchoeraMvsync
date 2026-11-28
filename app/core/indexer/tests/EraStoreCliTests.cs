// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P8：CLI <c>dump / verify / stats</c>（设计文档 §3.10 / §5.3）。
/// 覆盖正常校验、段体翻转 1 字节、目录 + CURRENT、截断、统计与词条导出、退出码。
/// </summary>
internal static class EraStoreCliTests
{
    public static void Run()
    {
        VerifyHealthy();
        VerifyDetectsFlippedByte();
        DirectoryAndCurrent();
        TruncatedFile();
        Stats();
        Dump();
        ExitCodes();
    }

    /// <summary>1. 正常索引：Ok、Gen/Docs/Segments/Sections 与 Manifest 一致。</summary>
    private static void VerifyHealthy()
    {
        var (dir, path, result) = BuildFixture();
        try
        {
            var report = IndexVerifier.Verify(path);
            Program.Check("verify: 正常索引 Ok", report.Ok,
                string.Join(" | ", report.Errors));
            Program.Check("verify: gen 一致", report.Gen == 1, report.Gen.ToString());
            Program.Check("verify: docs 一致", report.Docs == result.Manifest.DocCount,
                report.Docs.ToString());
            Program.Check("verify: segments=3", report.Segments == 3, report.Segments.ToString());
            Program.Check("verify: sections 与 Manifest 一致",
                report.Sections == result.Manifest.Sections.Count,
                $"{report.Sections}/{result.Manifest.Sections.Count}");

            using var doc = JsonDocument.Parse(report.ToJson());
            Program.Check("verify: JSON ok=true",
                doc.RootElement.GetProperty("ok").GetBoolean());
            Program.Check("verify: JSON errors 为空数组",
                doc.RootElement.GetProperty("errors").GetArrayLength() == 0);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>2. DocStore 段体翻转 1 字节 → verify 失败且 CLI 退出码 1。</summary>
    private static void VerifyDetectsFlippedByte()
    {
        var (dir, path, result) = BuildFixture();
        try
        {
            var docStore = result.Manifest.Sections.First(
                s => s.Kind == SectionKind.DocStore && s.SegmentId == 0);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                var at = (long)docStore.Offset + docStore.Length / 2;
                stream.Position = at;
                var value = stream.ReadByte();
                stream.Position = at;
                stream.WriteByte((byte)(value ^ 0xFF));
                stream.Flush(flushToDisk: true);
            }

            var report = IndexVerifier.Verify(path);
            Program.Check("verify 翻转字节: Ok=false", !report.Ok);
            Program.Check("verify 翻转字节: 错误含 crc 与段号",
                report.Errors.Any(e => e.Contains("crc", StringComparison.OrdinalIgnoreCase)
                    && e.Contains("seg=0", StringComparison.Ordinal)),
                string.Join(" | ", report.Errors));
            Program.Check("verify 翻转字节: CLI 退出码 1",
                RunCli("verify", "--index", path) == 1);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>3. 目录 + CURRENT：合法可校验；CURRENT 非法则报错。</summary>
    private static void DirectoryAndCurrent()
    {
        var (dir, path, _) = BuildFixture();
        try
        {
            Program.Check("verify 目录: CURRENT 解析成功", IndexVerifier.Verify(dir).Ok);
            Program.Check("verify 目录: CLI 退出码 0",
                RunCli("verify", "--index", dir) == 0);

            File.WriteAllText(Path.Combine(dir, CurrentPointer.FileName), "not-a-generation\n");
            Program.Check("verify 目录: 非法 CURRENT 抛 InvalidDataException",
                ThrowsInvalidData(() => IndexVerifier.Verify(dir)));
            Program.Check("verify 目录: 非法 CURRENT CLI 退出码 1",
                RunCli("verify", "--index", dir) == 1);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>4. 文件截断 → 报错且不崩溃。</summary>
    private static void TruncatedFile()
    {
        var (dir, path, _) = BuildFixture();
        try
        {
            var raw = File.ReadAllBytes(path);
            File.WriteAllBytes(path, raw.AsSpan(0, raw.Length / 2).ToArray());

            var report = IndexVerifier.Verify(path);
            Program.Check("verify 截断: Ok=false", !report.Ok);
            Program.Check("verify 截断: 有错误信息", report.Errors.Count > 0,
                report.Errors.Count.ToString());

            using var doc = JsonDocument.Parse(report.ToJson());
            Program.Check("verify 截断: JSON 可解析",
                doc.RootElement.GetProperty("ok").GetBoolean() == false);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>5. stats：文档 / 段数 / 体积与构建结果一致，JSON 可解析。</summary>
    private static void Stats()
    {
        var (dir, path, result) = BuildFixture();
        try
        {
            var stats = IndexStats.Collect(path);
            Program.Check("stats: docs 一致", stats.Docs == result.Manifest.DocCount,
                stats.Docs.ToString());
            Program.Check("stats: segments=3", stats.Segments == 3, stats.Segments.ToString());
            Program.Check("stats: liveBytes 一致", stats.LiveBytes == result.Manifest.LiveBytes,
                $"{stats.LiveBytes}/{result.Manifest.LiveBytes}");
            Program.Check("stats: fileBytes 与实际文件一致",
                stats.FileBytes == new FileInfo(path).Length,
                $"{stats.FileBytes}/{new FileInfo(path).Length}");
            Program.Check("stats: overheadBytes >= 0", stats.OverheadBytes >= 0,
                stats.OverheadBytes.ToString());

            var keys = stats.Sections.Select(s => (s.Segment, (int)s.Kind)).ToList();
            var sortedKeys = keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2).ToList();
            Program.Check("stats: sections 按 (seg, kind) 排序",
                keys.SequenceEqual(sortedKeys));

            using var doc = JsonDocument.Parse(stats.ToJson());
            Program.Check("stats: JSON sections 数量一致",
                doc.RootElement.GetProperty("sections").GetArrayLength() == stats.Sections.Count);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>6. dump：全量 / 字段 / 前缀 / 上限过滤，JSON 可解析。</summary>
    private static void Dump()
    {
        var (dir, path, _) = BuildFixture();
        try
        {
            var all = IndexDumper.Dump(path, null, null, 1000);
            Program.Check("dump: 全量 9 条", all.Terms.Count == 9, all.Terms.Count.ToString());
            Program.Check("dump: 段号非降",
                all.Terms.Zip(all.Terms.Skip(1)).All(p => p.First.Segment <= p.Second.Segment));

            var titles = IndexDumper.Dump(path, FieldId.Title, null, 1000);
            Program.Check("dump: --field Title 5 条", titles.Terms.Count == 5,
                titles.Terms.Count.ToString());

            var prefix = IndexDumper.Dump(path, FieldId.Title, "晴", 1000);
            Program.Check("dump: --prefix 晴 命中 1 条",
                prefix.Terms.Count == 1 && prefix.Terms[0].Term == "晴天",
                prefix.Terms.Count.ToString());

            var artists = IndexDumper.Dump(path, FieldId.Artist, "周", 1000);
            Program.Check("dump: artist 前缀 周 命中 3 条",
                artists.Terms.Count == 3 && artists.Terms.All(t => t.Field == FieldId.Artist),
                artists.Terms.Count.ToString());

            var limited = IndexDumper.Dump(path, null, null, 2);
            Program.Check("dump: --limit 截断到 2", limited.Terms.Count == 2,
                limited.Terms.Count.ToString());

            using var doc = JsonDocument.Parse(all.ToJson());
            Program.Check("dump: JSON terms 数量一致",
                doc.RootElement.GetProperty("terms").GetArrayLength() == 9);
            Program.Check("dump: CLI 退出码 0", RunCli("dump", "--index", path) == 0);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>7. CLI 退出码：stats 正常 0；缺 --index 为 1。</summary>
    private static void ExitCodes()
    {
        var (dir, path, _) = BuildFixture();
        try
        {
            Program.Check("CLI: stats 正常退出码 0",
                RunCli("stats", "--index", path) == 0);
            Program.Check("CLI: stats 缺 --index 退出码 1", RunCli("stats") == 1);
            Program.Check("CLI: verify 缺 --index 退出码 1", RunCli("verify") == 1);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    /// <summary>5 曲 / 每段 2 曲 → 3 段；词条合计 9 条。</summary>
    private static (string Dir, string Path, BuildResult Result) BuildFixture()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "search-1.eraindex");
        var docs = new List<AnalyzedDoc>
        {
            MakeDoc(1, "晴天", "周杰伦", (FieldId.Title, new[] { "晴天" }),
                (FieldId.Artist, new[] { "周杰伦" })),
            MakeDoc(2, "娃娃", "周杰伦", (FieldId.Title, new[] { "娃娃" }),
                (FieldId.Artist, new[] { "周杰伦" })),
            MakeDoc(3, "雨天", "孙燕姿", (FieldId.Title, new[] { "雨天" }),
                (FieldId.Artist, new[] { "孙燕姿" })),
            MakeDoc(4, "七里香", "周杰伦", (FieldId.Title, new[] { "七里香" }),
                (FieldId.Artist, new[] { "周杰伦" })),
            MakeDoc(5, "稻香", "周杰伦", (FieldId.Title, new[] { "稻香" }),
                (FieldId.Artist, new[] { "周杰伦" })),
        };

        var result = IndexBuilder.Build(path, docs, new BuildOptions
        {
            DocsPerSegment = 2,
            Gen = 1,
            FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
            CreatedMs = 1_700_000_000_000,
        });
        return (dir, path, result);
    }

    private static AnalyzedDoc MakeDoc(
        int seed, string title, string artist, params (FieldId Field, string[] Tokens)[] fields)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(seed + i);
            rev[i] = (byte)(seed * 7 + i);
        }

        var tokens = new Dictionary<FieldId, string[]>();
        foreach (var (field, terms) in fields) tokens[field] = terms;

        return new AnalyzedDoc(track, rev, (ushort)(seed & 0x7), (byte)(seed & 0x03), tokens,
            title, artist, "album" + seed);
    }

    /// <summary>调用 CLI Main 并吞掉其 stdout（返回退出码）。</summary>
    private static int RunCli(params string[] args)
    {
        var original = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            return Archoera.Indexer.Program.Main(args);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static bool ThrowsInvalidData(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
