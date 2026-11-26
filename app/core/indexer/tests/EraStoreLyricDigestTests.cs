// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// Q1 歌词精确度：DocStore v5 的 <c>lyrics_digest</c>（归一化歌词整行 64 位哈希）。
///
/// <list type="number">
///   <item><b>格式</b>：有序去重 / 空 doc / 偏移表与段尾校验 / 体积公式（4 B/doc + 8 B/行）；</item>
///   <item><b>相等语义</b>：空格 / 大小写 / 繁简归一后整行相等才命中，部分行不命中；</item>
///   <item><b>排序</b>：歌词整行相等拿 exact=equal，压过 bm25 更好的部分行命中；
///         元数据精确度（title / artist / album）逐项不变；</item>
///   <item><b>合并</b>：从旧段原样拷贝，逐字节稳定；</item>
///   <item><b>成本</b>：非歌词步不读摘要列；宽命中（&gt; cap）不读。</item>
/// </list>
/// </summary>
internal static class EraStoreLyricDigestTests
{
    public static void Run()
    {
        FormatAndSize();
        MergeCarriesDigests();
        SearchQuality();
    }

    /* ------------------------------------------------------------------ */
    /* 1. 格式 / 归一 / 体积                                               */
    /* ------------------------------------------------------------------ */

    private static void FormatAndSize()
    {
        var exactLines = new[]
        {
            "夜空中 最亮的星",
            "请指引我靠近你",
            "  夜空中最亮的星  ", // 与第一行归一后重复 → 只在摘要里出现一次
        };
        var emptyLines = Array.Empty<string>();

        var withDigest = new[]
        {
            MakeRecord(1, exactLines),
            MakeRecord(2, emptyLines),
            MakeRecord(3, new[] { "LOVE   IS ALL WE NEED", "我愛你 時光不老" }),
        };
        var withoutDigest = new[]
        {
            MakeRecord(1, emptyLines),
            MakeRecord(2, emptyLines),
            MakeRecord(3, emptyLines),
        };

        var body = DocStoreWriter.Write(withDigest);
        var plain = DocStoreWriter.Write(withoutDigest);
        var totalHashes = 2UL + 0 + 2;
        // v5 段体恒有 4 B/doc 的偏移表（空摘要也一样）；这里对比同一版本、
        // 只差哈希 blob 的两份段体，所以差值应恰好是 8 B/行。
        var expectedOverhead = (uint)(8 * totalHashes);
        Program.Check("Q1 格式: 体积 = 8B/行哈希（偏移表 4B/doc 恒定存在）",
            body.Length == plain.Length + expectedOverhead,
            $"{body.Length} vs {plain.Length}+{expectedOverhead}");

        var reader = new DocStoreReader(body);
        Program.Check("Q1 格式: 文档数", reader.Count == 3, reader.Count.ToString());
        Program.Check("Q1 格式: 空歌词 doc 摘要为空", reader.LyricDigestCount(1) == 0,
            reader.LyricDigestCount(1).ToString());

        var digests = reader.LyricDigestArray(0);
        Program.Check("Q1 格式: 归一化重复行只存一个哈希", digests.Length == 2, digests.Length.ToString());
        Program.Check("Q1 格式: 哈希严格递增", LyricDigest.IsSortedUnique(digests));

        var lineHash = LyricDigest.HashLine("夜空中 最亮的星")!.Value;
        var queryHash = SearchExactness.HashQuery(SearchExactness.NormalizeQuery("  夜空  中最亮的星 "))!.Value;
        Program.Check("Q1 相等: 空格不敏感（行 / 查询同哈希）",
            lineHash == queryHash, $"{lineHash}/{queryHash}");
        Program.Check("Q1 相等: 整行命中", reader.ContainsLyricLine(0, lineHash));

        var latinLine = LyricDigest.HashLine("love is all we need")!.Value;
        Program.Check("Q1 相等: 大小写 / 多空格不敏感（拉丁）",
            latinLine == LyricDigest.HashLine("LOVE   IS ALL WE NEED")!.Value
            && reader.ContainsLyricLine(2, latinLine), latinLine.ToString());

        var traditional = LyricDigest.HashLine("我愛你 時光不老")!.Value;
        Program.Check("Q1 相等: 繁简归一（繁体行 / 简体查询）",
            traditional == LyricDigest.HashLine("我爱你 时光不老")!.Value
            && reader.ContainsLyricLine(2, traditional),
            traditional.ToString());

        Program.Check("Q1 相等: 部分行不命中",
            !reader.ContainsLyricLine(0, LyricDigest.HashLine("夜空中最亮的星 请指引我")!.Value)
            && !reader.ContainsLyricLine(0, LyricDigest.HashLine("夜空中")!.Value));

        // 旧 v5 / v4 / v3 段体拒绝并提示重建（写入侧版本 6）。
        Program.Check("Q1 版本: 当前 DocStore v6 / 六列",
            DocStoreWriter.Version == 6 && DocStoreWriter.TextColumns == 6,
            $"{DocStoreWriter.Version}/{DocStoreWriter.TextColumns}");
        foreach (var old in new ushort[] { 3, 4, 5 })
        {
            var patched = body.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(4), old);
            var message = ThrowsInvalidDataWithMessage(() => new DocStoreReader(patched));
            Program.Check($"Q1 版本: v{old} 段体拒绝且提示全量重建",
                message is not null && message.Contains("重建", StringComparison.Ordinal),
                message ?? "(no throw)");
        }
    }

    /* ------------------------------------------------------------------ */
    /* 2. 合并原样搬运                                                      */
    /* ------------------------------------------------------------------ */

    private static void MergeCarriesDigests()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, new[] { "第一行", "重复行", " 重复行 " }),
                MakeDoc(2, new[] { "Beyoncé", "第二首 尾行" }),
                MakeDoc(3, new[] { "第三首" }),
                MakeDoc(4, Array.Empty<string>()),
            };
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
            var before = LiveDigests(path);

            var result = Merger.Merge(path, new[] { 0, 1 });
            var after = LiveDigests(path);

            Program.Check("Q1 合并: 活 doc 数不变", result.LiveDocs == 4 && after.Count == 4,
                $"{result.LiveDocs}/{after.Count}");

            var same = before.Count == after.Count;
            foreach (var (track, digests) in before)
            {
                if (!after.TryGetValue(track, out var merged)
                    || !digests.AsSpan().SequenceEqual(merged))
                {
                    same = false;
                }
            }
            Program.Check("Q1 合并: 摘要列逐字节一致（不重新哈希）", same,
                string.Join(',', before.Keys));

            Program.Check("Q1 合并: 夹具覆盖「非空 / 空」两类 doc",
                before.Values.Any(d => d.Length > 0) && before.Values.Any(d => d.Length == 0));
            Program.Check("Q1 合并: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 3. 查询：整行相等压过 bm25；元数据精确度不变                         */
    /* ------------------------------------------------------------------ */

    private static void SearchQuality()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-lyric-digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            BuildLyricsFixture(root);
            ExactLineBeatsBm25(root);
            MetadataExactnessUnchanged(root);
            CostsAndCap(root);
        }
        finally
        {
            SearchExactness.DebugCapOverride = null;
            SearchSession.CloseCached();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响断言。
            }
        }
    }

    /// <summary>
    /// x1：整行相等一次 + 大量无关行（bm25 差）；x2：多条「以查询为前缀」的不同行（bm25 好）。
    /// 复核生效时 x1 必须靠 exact=equal 排到 #1；关闭复核（cap=0）退回 x2 优先。
    /// </summary>
    private static void ExactLineBeatsBm25(string root)
    {
        var index = Path.Combine(root, "search.db");
        var query = "夜空中最亮的星";

        var ordered = Query(index, query, metadata: false, explain: true);
        var ids = Ids(ordered);
        Program.Check("Q1 排序: 整行相等排到 #1", ids.StartsWith("x1", StringComparison.Ordinal), ids);
        Program.Check("Q1 排序: 整行等价的 track 记 exact=equal",
            Explain(ordered, "x1") is { } x1
            && x1.Exact == "equal" && x1.Bucket[3] == SearchExactness.Equal
            && x1.Field == "lyrics",
            BucketOf(ordered, "x1"));
        Program.Check("Q1 排序: 部分行命中仍是 exact=none",
            Explain(ordered, "x2") is { } x2
            && x2.Exact == "none" && x2.Bucket[3] == SearchExactness.None,
            BucketOf(ordered, "x2"));
        Program.Check("Q1 排序: 部分行 bm25 确实更好（不是靠分数取胜）",
            Explain(ordered, "x2") is { } x2b && Explain(ordered, "x1") is { } x1b
            && x2b.Bm25 < x1b.Bm25,
            $"{BucketOf(ordered, "x1")} vs {BucketOf(ordered, "x2")}");

        // scope=lyrics（FieldFamily.Lyrics）走同一条复核：整行相等同样生效。
        var lyricsOnly = SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Lyrics = true,
            Limit = 50,
            Fuzzy = true,
            MinExact = 1,
            Explain = true,
            Cache = false,
        });
        Program.Check("Q1 排序: scope=lyrics 也用整行相等",
            Ids(lyricsOnly).StartsWith("x1", StringComparison.Ordinal)
            && Explain(lyricsOnly, "x1") is { } lx1
            && lx1.Exact == "equal" && lx1.Field == "lyrics",
            BucketOf(lyricsOnly, "x1"));

        // 关掉所有 DocStore 复核：顺序退回 bm25（x2 在前），说明 #1 来自整行相等。
        SearchExactness.DebugCapOverride = 0;
        var bm25Order = Ids(Query(index, query, metadata: false, explain: false));
        SearchExactness.DebugCapOverride = null;
        Program.Check("Q1 排序: 关闭复核后 x2 凭 bm25 领先", bm25Order.StartsWith("x2", StringComparison.Ordinal),
            bm25Order);
    }

    /// <summary>元数据三列复核不受 Q1 影响：title 相等 / 前缀仍按原档位，歌词 equal 不抢字段桶。</summary>
    private static void MetadataExactnessUnchanged(string root)
    {
        var dir = Path.Combine(root, "meta");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "library.db");
        var index = Path.Combine(dir, "search.db");
        var audio = Path.Combine(dir, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            Insert(conn, audio, "m1", "晴天", "周杰伦", lyrics: "无关的歌词");
            Insert(conn, audio, "m2", "晴天娃娃", "合辑一");
            Insert(conn, audio, "m3", "无关一", "合辑二", lyrics: "晴天");
            Insert(conn, audio, "m4", "无关二", "合辑三", lyrics: "晴天 雨天");
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        SearchExactness.DebugResetDocReads();
        var metadata = Query(index, "晴天", metadata: true, explain: true);
        Program.Check("Q1 元数据: title 相等仍 exact=equal",
            Explain(metadata, "m1") is { } m1
            && m1.Exact == "equal" && m1.Bucket[3] == SearchExactness.Equal
            && m1.Field == "title",
            BucketOf(metadata, "m1"));
        Program.Check("Q1 元数据: title 前缀仍 exact=startsWith",
            Explain(metadata, "m2") is { } m2
            && m2.Exact == "startsWith" && m2.Bucket[3] == SearchExactness.StartsWith,
            BucketOf(metadata, "m2"));
        Program.Check("Q1 元数据: scope=metadata 不读歌词摘要",
            SearchExactness.DebugLyricDigestReads == 0,
            SearchExactness.DebugLyricDigestReads.ToString());

        var all = Query(index, "晴天", metadata: false, explain: true);
        Program.Check("Q1 元数据: 歌词整行相等拿 equal + 保守 field=lyrics",
            Explain(all, "m3") is { } m3
            && m3.Exact == "equal" && m3.Bucket[3] == SearchExactness.Equal
            && m3.Field == "lyrics",
            BucketOf(all, "m3"));
        Program.Check("Q1 元数据: 歌词部分行仍是 none",
            Explain(all, "m4") is { } m4
            && m4.Exact == "none" && m4.Bucket[3] == SearchExactness.None,
            BucketOf(all, "m4"));
        Program.Check("Q1 元数据: 字段桶仍 title → artist → album → lyrics",
            Ids(all) == "m1,m2,m3,m4", Ids(all));
    }

    /// <summary>成本护栏：宽命中步不读摘要；预算内只对字面主趟按候选读取。</summary>
    private static void CostsAndCap(string root)
    {
        var index = Path.Combine(root, "search.db");
        SearchExactness.DebugResetDocReads();
        SearchExactness.DebugCapOverride = 0;
        try
        {
            Query(index, "夜空中最亮的星", metadata: false, explain: false);
        }
        finally
        {
            SearchExactness.DebugCapOverride = null;
        }
        Program.Check("Q1 成本: 宽命中步不读摘要", SearchExactness.DebugLyricDigestReads == 0,
            SearchExactness.DebugLyricDigestReads.ToString());

        SearchExactness.DebugResetDocReads();
        Query(index, "夜空中最亮的星", metadata: false, explain: false);
        Program.Check("Q1 成本: 预算内按候选读摘要（2 条）",
            SearchExactness.DebugLyricDigestReads == 2,
            SearchExactness.DebugLyricDigestReads.ToString());
        Program.Check("Q1 成本: 元数据复核只跑字面主趟（2 条）",
            SearchExactness.DebugDocReads == 2,
            SearchExactness.DebugDocReads.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* fixture                                                             */
    /* ------------------------------------------------------------------ */

    private static void BuildLyricsFixture(string root)
    {
        var db = Path.Combine(root, "library.db");
        var index = Path.Combine(root, "search.db");
        var audio = Path.Combine(root, "audio");
        Directory.CreateDirectory(audio);

        var query = "夜空中最亮的星";
        // x1：整行相等一次，其余 40 行无关长句（拉长 doc 长度 / 稀释 tf → bm25 差）。
        var filler = new List<string> { query };
        for (var i = 0; i < 40; i++) filler.Add("无关的歌词第" + i + "句在风里飘荡，");
        // x2：30 条不同的「查询前缀」行（每条整行都不等于查询 → exactness=none）。
        var prefixLines = new List<string>();
        for (var i = 0; i < 30; i++) prefixLines.Add(query + " 请指引我靠近你" + i);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            Insert(conn, audio, "x1", "测试甲", "合辑", lyrics: string.Join('\n', filler));
            Insert(conn, audio, "x2", "测试乙", "合辑", lyrics: string.Join('\n', prefixLines));
        }
        EndToEnd.RunEngine(db, index, full: true, lrc: false);
        SearchSession.CloseCached();

        // 夹具自检：两首都能被主趟召回，且标题不含查询（否则字段桶会掩盖 exactness 的效果）。
        var probe = Query(index, query, metadata: false, explain: true);
        Program.Check("Q1 夹具: 两条命中都在 lyrics 桶",
            Explain(probe, "x1") is { Field: "lyrics" }
            && Explain(probe, "x2") is { Field: "lyrics" }
            && Ids(probe) == "x1,x2",
            Ids(probe));
    }

    private static SearchResult Query(string index, string text, bool metadata, bool explain)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = text,
            Metadata = metadata,
            Limit = 50,
            Fuzzy = true,
            MinExact = 1,
            Explain = explain,
            Cache = false,
        });

    private static void Insert(
        Microsoft.Data.Sqlite.SqliteConnection conn, string audio, string id,
        string title, string artist, string? album = null, string? lyrics = null)
        => EndToEnd.InsertTrack(
            conn, id, Path.Combine(audio, id + ".mp3"), title,
            "[{\"name\":\"" + artist + "\"}]", album, lyrics, 1000);

    private static AnalyzedDoc MakeDoc(int seed, IReadOnlyList<string> lines) =>
        new(TrackBytes(seed), RevBytes(seed), (ushort)(seed & 0x7), (byte)(seed & 0x03),
            new Dictionary<FieldId, string[]> { [FieldId.Title] = new[] { "t" + seed } },
            "t" + seed, "a" + seed, string.Empty,
            LyricLines: lines);

    private static DocRecord MakeRecord(int seed, IReadOnlyList<string> lines) => new()
    {
        Track = TrackBytes(seed),
        Rev = RevBytes(seed),
        Title = "t" + seed,
        Artist = "a",
        Album = "b",
        TitleNorm = Analyzer.NormalizeMetadata("t" + seed),
        ArtistNorm = "a",
        AlbumNorm = "b",
        LyricDigests = LyricDigest.Build(lines),
    };

    private static byte[] TrackBytes(int seed)
    {
        var track = new byte[16];
        for (var i = 0; i < 16; i++) track[i] = (byte)(seed + i);
        return track;
    }

    private static byte[] RevBytes(int seed)
    {
        var rev = new byte[16];
        for (var i = 0; i < 16; i++) rev[i] = (byte)(seed * 7 + i);
        return rev;
    }

    private static string TrackHex(int seed) => Convert.ToHexString(TrackBytes(seed)).ToLowerInvariant();

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
        CreatedMs = 1_700_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    /// <summary>活 doc 的摘要列（track hex → ulong[]）。</summary>
    private static Dictionary<string, ulong[]> LiveDigests(string path)
    {
        using var reader = new StoreReader(path);
        var map = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
        foreach (var segment in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < segment.Docs.Count; doc++)
            {
                if (!segment.IsLive(doc)) continue;
                map[segment.Docs.TrackId(doc)] = segment.Docs.LyricDigestArray(doc);
            }
        }
        return map;
    }

    private static string Ids(SearchResult result)
        => string.Join(',', result.Items.Select(hit => hit.TrackId));

    private static SearchExplanation? Explain(SearchResult result, string trackId)
        => result.Explanations.TryGetValue(trackId, out var explanation) ? explanation : null;

    private static string BucketOf(SearchResult result, string trackId)
        => Explain(result, trackId) is { } explanation
            ? string.Join('/', explanation.Bucket.Select(
                value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            + $" [{explanation.Exact}/{explanation.Field}]"
            : "missing";

    private static string? ThrowsInvalidDataWithMessage(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-lyric-digest-fmt-" + Guid.NewGuid().ToString("N"));
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
