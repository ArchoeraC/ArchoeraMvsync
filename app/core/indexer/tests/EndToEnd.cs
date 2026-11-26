// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore.Iterators;
using Archoera.Lyrics;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 端到端 round-trip：用 Microsoft.Data.Sqlite 建一个 <c>library.db</c> fixture，
/// 跑 <see cref="IndexerEngine"/> 全量 / 增量，直接对 EraStore（<c>search.era/</c>）断言：
/// <list type="bullet">
///   <item>全量：CURRENT 指向新快照、Manifest 元数据、活 doc 数、表达式命中
///         （中文子串 / 繁体经分析、拼音 / 首字母、拉丁前缀）；</item>
///   <item>增量：改 mtime 只重算该曲；删除曲目不再出现在活文档 / 查询结果；</item>
///   <item><c>.lrc</c> 子开关开 / 关各一次（并入 / 清理）；</item>
///   <item><c>_scanner_meta</c> 缺失时世代按 0、不报错（前向兼容）。</item>
/// </list>
/// </summary>
internal static class EndToEnd
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-indexer-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FullAndIncremental(root);
            MissingScannerMeta(root);
            LrcReading(root);
            SidecarDirectoryCache(root);
            MultilingualLayers(root);
            LyricsPreprocessIntegration(root);
            OversizeTermGuard(root);
            RomajiLyrics(root);
            InitialsLyricsOption(root);
            InitialsBigramSearch(root);
            ProductionRomanizationNoPositions(root);
            RepeatedFullBuild(root);
            EraIncremental(root);
            FullBuildCompactionWritesFreshGeneration(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 主流程：全量 → 增量 → 删除 → .lrc 开关                              */
    /* ------------------------------------------------------------------ */

    private static void FullAndIncremental(string root)
    {
        var db = Path.Combine(root, "library.db");
        var index = Path.Combine(root, "search.db");
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var song1 = Path.Combine(dirA, "song1.mp3");
        var song2 = Path.Combine(dirA, "song2.mp3");
        var song3 = Path.Combine(dirB, "song3.mp3");
        var song4 = Path.Combine(dirB, "delete-me.mp3");

        CreateLibrary(db, generation: 5);
        using (var conn = Open(db))
        {
            // t1：简体内嵌歌词 + LRC 时间轴标签
            InsertTrack(conn, "t1", song1, "晴天", """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""",
                "[00:12.34]我爱你中国\n[00:15.00]我爱你", 1000);
            // t2：繁体歌词（索引期归一为简体）
            InsertTrack(conn, "t2", song2, "爱", """[{"name":"邓丽君"}]""", """{"name":"经典"}""", "我愛你", 2000);
            InsertTrack(conn, "t3", song3, "Hello World", """[{"name":"ADELE"}]""", """{"name":"25"}""", null, 3000);
            InsertTrack(conn, "t4", song4, "Delete Me", "[]", null, "将被删除", 4000);
        }

        // 侧车歌词（t3）：由**扫描器写入 library.db**（索引器只读 DB）。
        // 含时间轴 / 元数据标签，索引器经共享 LrcReader.FromRawTexts 只取文本行。
        using (var conn = Open(db))
        {
            SetSidecar(conn, "t3",
                "[by:test]\n[00:01.00]蓝蓝的天\n[00:02.50]白云飘\n");
        }

        // ---- 全量构建 ----
        var full = RunEngine(db, index, full: true, lrc: false);
        Program.Check("全量: total==4", full.Total == 4, full.Total.ToString());
        Program.Check("全量: indexed==4", full.Indexed == 4, full.Indexed.ToString());
        Program.Check("全量: full==true", full.Full);
        Program.Check("全量: generation==5", full.Generation == 5, full.Generation.ToString());
        Program.Check("全量: 不写 v7 search.db（EraStore 唯一写入）", !File.Exists(index),
            "search.db exists");
        Program.Check("全量: CURRENT 指向 EraStore 快照",
            EraStore.CurrentPointer.Read(EraDir(index)) is not null);

        var manifest = ReadManifest(index);
        Program.Check("meta: analyzer 版本锚",
            manifest.Anchors.AnalyzerVersion == Analyzer.AnalyzerVersion,
            manifest.Anchors.AnalyzerVersion.ToString());
        Program.Check("meta: 元数据版本 >= CurrentMetaVersion",
            manifest.MetaVersion >= EraStore.Manifest.CurrentMetaVersion,
            manifest.MetaVersion.ToString());
        Program.Check("meta: index_lrc==0", !ManifestHas(manifest, EraStore.ManifestOptionsBits.IndexLrc));
        Program.Check("meta: pinyin==1", ManifestHas(manifest, EraStore.ManifestOptionsBits.Pinyin));
        // T3.9：v15 首字母列是 bigram，Manifest 必须如实声明该能力（旧索引不得声明）。
        Program.Check("meta: 宣称 initials_bigram（v15 分析器）",
            manifest.Capabilities.Contains(IndexCapabilities.InitialsBigram)
            && manifest.Anchors.AnalyzerVersion >= Analyzer.InitialsBigramMinVersion,
            string.Join(',', manifest.Capabilities));

        Program.Check("EraStore 活 doc==4", DocCount(index) == 4, DocCount(index).ToString());

        Program.Check("Era: 列过滤 {title_tok} : (\"晴天\") 命中 t1",
            MatchContainsTrack(index, "{title_tok} : (\"晴天\")", "t1"));
        Program.Check("Era: 短语 \"wo ai ni\" 命中 t1",
            MatchContainsTrack(index, "\"wo ai ni\"", "t1"));
        var helloHits = MatchHits(index, "hello");
        Program.Check("Era: bm25 打分可执行且非零",
            helloHits.Count == 1 && helloHits[0].Score != 0,
            helloHits.Count == 1 ? helloHits[0].Score.ToString("F4") : $"hits={helloHits.Count}");

        // 表达式：中文子串（原始已归一，无需分析器）
        Program.Check("匹配 '爱你' 命中 2（t1/t2）", MatchCount(index, "爱你") == 2,
            MatchCount(index, "爱你").ToString());
        // 繁体查询：原始 '愛' 命中 0（未归一），经分析器归一后命中 t2（及 t1）
        Program.Check("匹配 原始 '愛' ==0（需分析器）", MatchCount(index, "愛") == 0,
            MatchCount(index, "愛").ToString());
        Program.Check("匹配 分析('愛') 命中 t2", MatchCount(index, Analyzed("愛")) >= 1);
        Program.Check("分析('愛') 命中 t2 的 doc", MatchContainsTrack(index, Analyzed("愛"), "t2"));
        // 拼音（全拼相邻短语）与首字母（v4：元数据/歌词分列；v15：列内是相邻 bigram）
        Program.Check("匹配 pinyin 短语 'wo ai ni' 命中 t1", MatchContainsTrack(index, "\"wo ai ni\"", "t1"));
        Program.Check("v15 首字母 bigram 短语 'wa an nz zg' 命中 t1",
            MatchContainsTrack(index, "{initials_lyrics_tok} : \"wa an nz zg\"", "t1"));
        // v4 分列：**元数据**首字母（晴天 → qt）在 initials_meta_tok；
        // **歌词**首字母（我爱你中国 → wanzg）在 initials_lyrics_tok。
        Program.Check("首字母列过滤 {initials_meta_tok} : 'qt'（标题首字母）命中 t1",
            MatchContainsTrack(index, "{initials_meta_tok} : \"qt\"", "t1"));
        Program.Check("首字母列过滤 {initials_lyrics_tok} : 'wa an nz zg'（歌词首字母）命中 t1",
            MatchContainsTrack(index, "{initials_lyrics_tok} : \"wa an nz zg\"", "t1"));
        Program.Check("歌词首字母不在 initials_meta_tok（v4 分列）",
            !MatchContainsTrack(index, "{initials_meta_tok} : \"wa an nz zg\"", "t1"));
        // 首字母 bigram 不在拼音两列 → 拼音列过滤下不命中
        Program.Check("拼音两列 : 'wa an nz zg' 不命中（首字母已拆出）",
            !MatchContainsTrack(index, "{pinyin_meta_tok pinyin_lyrics_tok} : \"wa an nz zg\"", "t1"));
        // v4 分列：歌词拼音不在元数据拼音列 → 元数据范围的列过滤收得住（旧版混列时收不住）
        Program.Check("歌词拼音不在 pinyin_meta_tok（v4 分列）",
            MatchContainsTrack(index, "{pinyin_lyrics_tok} : \"wo ai ni\"", "t1")
            && !MatchContainsTrack(index, "{pinyin_meta_tok} : \"wo ai ni\"", "t1"));
        // 连写全拼（查询侧 woaini → wo ai ni，镜像 Dart compileMatch 的相邻短语）
        var woaini = Analyzer.AnalyzeQuery("woaini").Pinyin;
        Program.Check("E2E 连写全拼分析 == wo ai ni", string.Join(' ', woaini) == "wo ai ni",
            string.Join(' ', woaini));
        Program.Check("匹配 连写全拼短语 \"wo ai ni\" 命中 t1",
            MatchContainsTrack(index, "\"wo ai ni\"", "t1"));
        // 拉丁前缀 / 整词
        Program.Check("匹配 'hello' 命中 t3", MatchContainsTrack(index, "hello", "t3"));
        Program.Check("匹配 前缀 '\"hel\"*' 命中 t3", MatchContainsTrack(index, "\"hel\"*", "t3"));

        // ---- 增量：改 t1 的 file_mtime ----
        Exec(db, "UPDATE tracks SET file_mtime = 9999 WHERE id = 't1'");
        var (inc, incQueries) = RunEngineDetailed(db, index, full: false, lrc: false);
        Program.Check("增量(改 mtime): full==false", !inc.Full);
        Program.Check("增量(改 mtime): indexed==1", inc.Indexed == 1, inc.Indexed.ToString());
        Program.Check("增量(改 mtime): deleted==0", inc.Deleted == 0, inc.Deleted.ToString());
        Program.Check("增量(改 mtime): skipped==3", inc.Skipped == 3, inc.Skipped.ToString());
        Program.Check("增量(改 mtime): 仅 1 次内容查询（未全库读歌词）", incQueries == 1, incQueries.ToString());
        Program.Check("增量: t1 mtime 已更新", DocState(index, "t1").Mtime == 9999,
            DocState(index, "t1").Mtime.ToString());

        // ---- 增量：删除 t4（无脏曲 → 零内容读取）----
        Exec(db, "DELETE FROM tracks WHERE id = 't4'");
        var (del, delQueries) = RunEngineDetailed(db, index, full: false, lrc: false);
        Program.Check("删除: deleted==1", del.Deleted == 1, del.Deleted.ToString());
        Program.Check("删除: 仅删除、无内容查询", delQueries == 0, delQueries.ToString());
        Program.Check("删除: 活 doc==3", DocCount(index) == 3, DocCount(index).ToString());
        Program.Check("删除: t4 不再是活文档", !TrackExists(index, "t4"));
        Program.Check("删除: 歌词 '删除' 不再命中", MatchCount(index, Analyzed("删除")) == 0,
            MatchCount(index, Analyzed("删除")).ToString());

        // ---- .lrc 子开关：开（选项变化触发全量重建，侧车并入）----
        var lrcOn = RunEngine(db, index, full: false, lrc: true);
        Program.Check(".lrc 开: 构建选项变化触发全量重建", lrcOn.Full, lrcOn.Full.ToString());
        Program.Check(".lrc 开: 全量重算 t3 并入侧车", lrcOn.Indexed == 3, lrcOn.Indexed.ToString());
        Program.Check(".lrc 开: meta.index_lrc==1",
            ManifestHas(ReadManifest(index), EraStore.ManifestOptionsBits.IndexLrc));
        Program.Check(".lrc 开: indexed_lrc_size>0", DocState(index, "t3").LrcSize > 0,
            DocState(index, "t3").LrcSize.ToString());
        Program.Check(".lrc 开: 匹配 侧车文本 '蓝蓝的天' 命中 t3",
            MatchContainsTrack(index, "{lyrics_tok} : \"蓝蓝 蓝的 的天\"", "t3"));

        // ---- .lrc 子开关：关（选项变化触发全量重建，侧车清理）----
        var lrcOff = RunEngine(db, index, full: false, lrc: false);
        Program.Check(".lrc 关: 构建选项变化触发全量重建", lrcOff.Full, lrcOff.Full.ToString());
        Program.Check(".lrc 关: 全量重算清空侧车", lrcOff.Indexed == 3, lrcOff.Indexed.ToString());
        Program.Check(".lrc 关: meta.index_lrc==0",
            !ManifestHas(ReadManifest(index), EraStore.ManifestOptionsBits.IndexLrc));
        Program.Check(".lrc 关: indexed_lrc_size==0", DocState(index, "t3").LrcSize == 0,
            DocState(index, "t3").LrcSize.ToString());
        Program.Check(".lrc 关: 侧车文本不再命中",
            MatchCount(index, "{lyrics_tok} : \"蓝蓝 蓝的 的天\"") == 0,
            MatchCount(index, "{lyrics_tok} : \"蓝蓝 蓝的 的天\"").ToString());

        // ---- 无变更：全 skipped，且零内容查询（未读歌词）----
        var (idle, idleQueries) = RunEngineDetailed(db, index, full: false, lrc: false);
        Program.Check("无变更: indexed==0 && deleted==0", idle.Indexed == 0 && idle.Deleted == 0,
            $"indexed={idle.Indexed} deleted={idle.Deleted}");
        Program.Check("无变更: 零内容查询（未全库读歌词）", idleQueries == 0, idleQueries.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 世代化替换：同一进程内多次全量 + 替换后增量                         */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 回归：同一进程内**连续两次全量构建**都必须能完成，且全量替换后的增量收敛
    /// 必须真的落进**新**世代。
    ///
    /// EraStore 的全量构建先写新文件、提交后再原子切换 <c>CURRENT</c>；旧查询句柄
    /// 指向旧 inode 仍可读完整快照，但新会话必须看到新文件。本用例按
    /// 「全量 → 增量 → 全量 → 增量」覆盖两条路径。
    /// </summary>
    private static void RepeatedFullBuild(string root)
    {
        var work = Path.Combine(root, "repeated");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
            InsertTrack(conn, "r1", Path.Combine(dir, "r1.mp3"), "第一首", "[]", null, "最爱", 1000);

        // ① 全量：建立第 1 个世代
        var first = RunEngine(db, index, full: true, lrc: false);
        Program.Check("连续全量: 第 1 次 indexed==1", first.Indexed == 1, first.Indexed.ToString());
        Program.Check("连续全量: 第 1 次后活 doc==1", DocCount(index) == 1, DocCount(index).ToString());
        var firstCurrent = EraStore.CurrentPointer.Read(EraDir(index));
        Program.Check("连续全量: 第 1 次后 CURRENT 存在", firstCurrent is not null, firstCurrent ?? "(null)");

        // ② 增量：新增 r2。
        using (var conn = Open(db))
            InsertTrack(conn, "r2", Path.Combine(dir, "r2.mp3"), "第二首", "[]", null, "海边", 2000);
        var inc1 = RunEngine(db, index, full: false, lrc: false);
        Program.Check("连续全量: 替换前增量 indexed==1", inc1.Indexed == 1, inc1.Indexed.ToString());
        Program.Check("连续全量: 替换前增量可搜到 r2",
            MatchContainsTrack(index, Analyzed("第二首"), "r2"));

        // ③ 第二次全量：必须能完成并产出新快照。
        IndexerResult? second = null;
        string? error = null;
        try { second = RunEngine(db, index, full: true, lrc: false); }
        catch (Exception ex) { error = ex.Message; }
        Program.Check("连续全量: 同一进程内第 2 次全量可完成", error is null, error);
        if (second is null) return;

        Program.Check("连续全量: 第 2 次 indexed==2", second.Indexed == 2, second.Indexed.ToString());
        Program.Check("连续全量: 第 2 次后活 doc==2", DocCount(index) == 2, DocCount(index).ToString());
        Program.Check("连续全量: 第 2 次后 CURRENT 有效",
            EraStore.CurrentPointer.Read(EraDir(index)) is not null);

        // ④ 替换后的增量：必须写进**新**世代。
        using (var conn = Open(db))
            InsertTrack(conn, "r3", Path.Combine(dir, "r3.mp3"), "第三首", "[]", null, "星空", 3000);
        var inc2 = RunEngine(db, index, full: false, lrc: false);
        Program.Check("连续全量: 替换后增量 indexed==1", inc2.Indexed == 1, inc2.Indexed.ToString());
        Program.Check("连续全量: 替换后增量落库（r3 可搜到）",
            MatchContainsTrack(index, Analyzed("第三首"), "r3"));
        Program.Check("连续全量: 三首齐备", DocCount(index) == 3, DocCount(index).ToString());
    }

    /* ------------------------------------------------------------------ */
    /* EraStore 增量（T3.10）：脏曲只追加新段 / 墓碑，不再整库重写        */
    /* ------------------------------------------------------------------ */

    private static void EraIncremental(string root)
    {
        var work = Path.Combine(root, "era-incremental");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "e1", Path.Combine(dir, "e1.mp3"), "alpha", "[]", null, "first lyric", 1);
            InsertTrack(conn, "e2", Path.Combine(dir, "e2.mp3"), "bravo", "[]", null, "second lyric", 2);
            InsertTrack(conn, "e3", Path.Combine(dir, "e3.mp3"), "delta", "[]", null, "third lyric", 3);
        }

        var prodOptions = new IndexerOptions { IndexLrc = false };
        var full = RunEngine(db, index, full: true, options: prodOptions);
        Program.Check("Era 增量: 生产路径不写 search.db（T3.13）", !File.Exists(index),
            "search.db exists");
        Program.Check("Era 增量: 全量 written", full.EraWritten && !full.EraIncremental,
            $"written={full.EraWritten} inc={full.EraIncremental}");
        Program.Check("Era 增量: 全量 3 活 doc / 1 段",
            full.EraLiveDocs == 3 && full.EraSegments == 1, $"{full.EraLiveDocs}/{full.EraSegments}");
        // T3.15：小索引（<24 段）不做全量构建后的自动压缩；before/after 字段仍自洽。
        Program.Check("Era 增量: 小索引跳过自动压缩",
            !full.EraCompacted && full.EraSegmentsBeforeCompact == full.EraSegments,
            $"compacted={full.EraCompacted} before={full.EraSegmentsBeforeCompact}");

        // T3.15：上一次全量留下的「未被 CURRENT 引用」的旧世代（Windows 占用导致删除失败）
        // 必须在后续任意一次运行里被重试清理，且不影响当前索引可用性。
        var stale = Path.Combine(work, "search.era", "search-999999.eraindex");
        File.WriteAllText(stale, "incomplete-build");
        var idle = RunEngine(db, index, full: false, options: prodOptions);
        Program.Check("Era 切换: 无变更运行清理残留世代", !File.Exists(stale),
            File.Exists(stale) ? "stale exists" : "ok");
        Program.Check("Era 切换: 无变更运行不重建", idle.Indexed == 0 && !idle.EraIncremental,
            $"indexed={idle.Indexed} inc={idle.EraIncremental}");

        // 改一首标题 + 新增一首 + 删除一首。
        using (var conn = Open(db))
        {
            Exec(conn, "UPDATE tracks SET title='zulu', file_mtime=10 WHERE id='e1'");
            InsertTrack(conn, "e4", Path.Combine(dir, "e4.mp3"), "echo", "[]", null, "fourth lyric", 4);
            Exec(conn, "DELETE FROM tracks WHERE id='e3'");
        }

        var inc = RunEngine(db, index, full: false, options: prodOptions);
        Program.Check("Era 增量: 走增量路径", inc.EraIncremental,
            $"inc={inc.EraIncremental} written={inc.EraWritten}");
        Program.Check("Era 增量: 只处理 2 首（改 + 新）", inc.Indexed == 2, inc.Indexed.ToString());
        Program.Check("Era 增量: 删除 1 首", inc.Deleted == 1, inc.Deleted.ToString());
        Program.Check("Era 增量: 活 doc 3", inc.EraLiveDocs == 3, inc.EraLiveDocs.ToString());
        // 2/5 = 40% 死行，已过 20% 阈值 → 同一次增量内自动合并。
        Program.Check("Era 增量: 死行过阈值触发合并",
            inc.EraMerged && inc.EraSegments == 1, $"merged={inc.EraMerged} seg={inc.EraSegments}");
        Program.Check("Era 增量: 墓碑计数 2（旧版 + 删除）", inc.EraDeadDocs == 2, inc.EraDeadDocs.ToString());

        SearchSession.CloseCached();
        Program.Check("Era 增量: 新标题可搜到", EraHit(index, db, "zulu", "e1"), "zulu");
        Program.Check("Era 增量: 旧标题不再命中", !EraHit(index, db, "alpha", "e1"), "alpha");
        Program.Check("Era 增量: 新增曲可搜到", EraHit(index, db, "echo", "e4"), "echo");
        Program.Check("Era 增量: 删除曲不再命中", !EraHit(index, db, "delta", "e3"), "delta");
        Program.Check("Era 增量: 未变曲仍命中", EraHit(index, db, "bravo", "e2"), "bravo");

        // 连续改写同一首：达到段数 / 死行阈值时自动合并，段数不会无限增长。
        var merged = false;
        for (var i = 0; i < 10; i++)
        {
            using (var conn = Open(db))
                Exec(conn, $"UPDATE tracks SET file_mtime={100 + i} WHERE id='e2'");
            var step = RunEngine(db, index, full: false, options: prodOptions);
            merged |= step.EraMerged;
        }
        Program.Check("Era 增量: 连续改写触发合并", merged);
        var eraStats = EraStore.IndexStats.Collect(Path.Combine(work, "search.era"));
        Program.Check("Era 增量: 合并后段数 <8", eraStats.Segments < 8, eraStats.Segments.ToString());
        Program.Check("Era 增量: 合并后 dead_ratio <20%",
            eraStats.DeadRatio < 0.2, eraStats.DeadRatio.ToString("F4"));
        Program.Check("Era 增量: 活 doc 仍为 3", eraStats.Docs == 3, eraStats.Docs.ToString());
    }

    /// <summary>
    /// S1：全量构建收尾压缩直接写**新世代文件**并切换 CURRENT——
    /// <c>EraCompacted=true</c> / <c>EraDefragged=false</c>，目录只剩 CURRENT 指向的一个文件，
    /// <c>fileBytes = liveBytes + Manifest</c>，查询结果与构建内容一致。
    ///
    /// <para>用 <see cref="IndexerOptions.FullBuildMaxSegmentBytesOverride"/> 把单段估算上限
    /// 调到 256 KiB，30 首「每首 3000 个唯一拉丁词」的夹具即可稳定留下 30 个段，触发
    /// ≥24 段的自动压缩。</para>
    /// </summary>
    private static void FullBuildCompactionWritesFreshGeneration(string root)
    {
        var work = Path.Combine(root, "compact");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var music = Path.Combine(work, "m");
        Directory.CreateDirectory(music);

        const int tracks = 30;
        const int wordsPerTrack = 3000;
        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            for (var i = 0; i < tracks; i++)
            {
                var lyrics = string.Join(' ', Enumerable.Range(0, wordsPerTrack)
                    .Select(j => Word(1000 + i * wordsPerTrack + j)));
                InsertTrack(conn, "c" + i, Path.Combine(music, $"c{i}.mp3"), Word(i),
                    "[]", null, lyrics, 1000 + i);
            }
        }

        var result = RunEngine(db, index, full: true, new IndexerOptions
        {
            IndexLrc = false,
            Pinyin = false,
            FullBuildMaxSegmentBytesOverride = 256L * 1024,
        });

        Program.Check("S1 全量压缩: 触发自动压缩", result.EraCompacted,
            $"compacted={result.EraCompacted} seg={result.EraSegments}");
        Program.Check("S1 全量压缩: 夹具至少 24 段",
            result.EraSegmentsBeforeCompact >= EraStore.Merger.MergeTriggerSegments,
            result.EraSegmentsBeforeCompact.ToString());
        Program.Check("S1 全量压缩: 压回触发线以下",
            result.EraSegments < EraStore.Merger.MergeTriggerSegments,
            $"{result.EraSegmentsBeforeCompact}->{result.EraSegments}");
        Program.Check("S1 全量压缩: 不再需要 defrag（EraDefragged=false）",
            !result.EraDefragged, result.EraDefragged.ToString());

        var eraDir = EraDir(index);
        var current = EraStore.CurrentPointer.Read(eraDir);
        Program.Check("S1 全量压缩: CURRENT 指向新世代", current is not null, current ?? "(null)");
        Program.Check("S1 全量压缩: 目录只剩 CURRENT 指向的一个文件",
            EraGenerationFiles(eraDir).Length == 1, string.Join(',', EraGenerationFiles(eraDir)));

        var stats = EraStore.IndexStats.Collect(eraDir);
        var manifest = ReadManifest(index);
        var manifestBytes = manifest.ToJsonBytes().Length;
        Program.Check("S1 全量压缩: fileBytes = 活数据 + Manifest",
            stats.FileBytes == stats.LiveBytes + EraStore.EraFormat.DataStart + manifestBytes,
            $"file={stats.FileBytes} live={stats.LiveBytes} manifest={manifestBytes}");
        Program.Check("S1 全量压缩: overhead 只剩 Manifest",
            stats.OverheadBytes == manifestBytes, stats.OverheadBytes.ToString());
        Program.Check("S1 全量压缩: EraFileBytesAfter 与当前文件一致",
            result.EraFileBytesAfter == stats.FileBytes,
            $"{result.EraFileBytesAfter}/{stats.FileBytes}");
        Program.Check("S1 全量压缩: 活 doc 数不变", stats.Docs == tracks, stats.Docs.ToString());

        Program.Check("S1 全量压缩: 标题仍可命中",
            MatchContainsTrack(index, Analyzed(Word(0)), "c0"));
        Program.Check("S1 全量压缩: 歌词唯一词仍可命中",
            MatchContainsTrack(index, Analyzed(Word(1000)), "c0"));
        Program.Check("S1 全量压缩: 末曲仍可命中",
            MatchContainsTrack(index, Analyzed(Word(tracks - 1)), $"c{tracks - 1}"));
        Program.Check("S1 全量压缩: verify 通过",
            EraStore.IndexVerifier.Verify(eraDir).Ok,
            string.Join(" | ", EraStore.IndexVerifier.Verify(eraDir).Errors));
    }

    /// <summary>生成稳定唯一的纯小写拉丁词（5 字母，base-26）。</summary>
    private static string Word(int index)
    {
        var buffer = new char[5];
        var value = index;
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            buffer[i] = (char)('a' + value % 26);
            value /= 26;
        }
        return new string(buffer);
    }

    /// <summary>走生产查询路径（SearchSession → EraStore）判断某曲是否命中。</summary>
    private static bool EraHit(string index, string library, string query, string trackId)
    {
        try
        {
            var result = SearchSession.Run(index, new SearchRequest
            {
                Query = query,
                Limit = 20,
                Fuzzy = true,
            }, library);
            return result.Items.Any(hit => string.Equals(hit.TrackId, trackId, StringComparison.Ordinal));
        }
        finally
        {
            SearchSession.CloseCached();
        }
    }

    /// <summary>走生产查询路径（SearchSession → EraStore）判断某曲是否命中（不做容错）。</summary>
    private static bool SessionHit(string index, string library, string query, string trackId)
    {
        try
        {
            var result = SearchSession.Run(index, new SearchRequest
            {
                Query = query,
                Limit = 20,
            }, library);
            return result.Items.Any(hit => string.Equals(hit.TrackId, trackId, StringComparison.Ordinal));
        }
        finally
        {
            SearchSession.CloseCached();
        }
    }

    /* ------------------------------------------------------------------ */
    /* _scanner_meta 缺失：前向兼容                                        */
    /* ------------------------------------------------------------------ */

    private static void MissingScannerMeta(string root)
    {
        var db = Path.Combine(root, "lib-nometa.db");
        var index = Path.Combine(root, "idx-nometa.db");
        CreateLibrary(db, generation: null);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "m1", Path.Combine(root, "m1.mp3"), "歌", "[]", null, "测试", 10);
        }

        var result = RunEngine(db, index, full: true, lrc: false);
        Program.Check("无 _scanner_meta: 不报错且 generation==0", result.Generation == 0, result.Generation.ToString());
        Program.Check("无 _scanner_meta: 仍建索引", DocCount(index) == 1,
            DocCount(index).ToString());
    }

    /* ------------------------------------------------------------------ */
    /* .lrc 目录清单缓存（O(曲目 × 目录条目) → O(曲目 + 目录条目)）          */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 侧车目录清单缓存的语义：命中 / 大小写不敏感 / 确定性取文件名序最小者 /
    /// 三类侧车（主 · trans · romaji）/ 目录内容变动后立即可见 / 显式清空缓存。
    /// </summary>
    private static void SidecarDirectoryCache(string root)
    {
        var dir = Path.Combine(root, "sidecar-cache");
        Directory.CreateDirectory(dir);
        var audio = Path.Combine(dir, "song.mp3");

        LrcReader.ResetDirectoryCache();
        Program.Check("lrc 缓存: 空目录无侧车", LrcReader.FindSidecar(audio) is null,
            LrcReader.FindSidecar(audio) ?? "(null)");

        // 主侧车（小写）+ 显式分层侧车：一次目录枚举后应三类都能解析
        var main = Path.Combine(dir, "song.lrc");
        var trans = Path.Combine(dir, "song.TRANS.LRC"); // 大小写不敏感
        var romaji = Path.Combine(dir, "song.romaji.lrc");
        File.WriteAllText(main, "[00:01.00]原文\n", new UTF8Encoding(false));
        File.WriteAllText(trans, "译文\n", new UTF8Encoding(false));
        File.WriteAllText(romaji, "yi wen\n", new UTF8Encoding(false));
        LrcReader.ResetDirectoryCache();

        var layers = LrcReader.Read(audio);
        Program.Check("lrc 缓存: 主侧车命中", layers.Exists && layers.Text == "原文", layers.Text);
        Program.Check("lrc 缓存: 译文层命中", layers.Translation == "译文", layers.Translation);
        Program.Check("lrc 缓存: 音译层命中", layers.Romaji == "yi wen", layers.Romaji);

        // 同名多份（仅大小写 / 名字序不同）→ 取文件名序（Ordinal）最小者，确定性
        var dupUpper = Path.Combine(dir, "SONG.lrc");
        var dupLower = Path.Combine(dir, "Song.lrc");
        File.WriteAllText(dupUpper, "[00:01.00]大写\n", new UTF8Encoding(false));
        File.WriteAllText(dupLower, "[00:01.00]小写\n", new UTF8Encoding(false));
        LrcReader.ResetDirectoryCache();
        // Ordinal 序：'S'(0x53) < 's'(0x73)，且 'O'(0x4F) < 'o'(0x6F) → SONG.lrc 胜出
        Program.Check("lrc 缓存: 同名取 Ordinal 最小者", LrcReader.FindSidecar(audio) == dupUpper,
            Path.GetFileName(LrcReader.FindSidecar(audio) ?? "(null)"));

        // 新增侧车且**目录 mtime 前进**（显式推进，避免依赖 FS 时间粒度）→ 缓存失效、立即可见
        var added = Path.Combine(dir, "added.mp3");
        Program.Check("lrc 缓存: 新曲目初始无侧车", LrcReader.FindSidecar(added) is null);
        var addedLrc = Path.Combine(dir, "added.lrc");
        File.WriteAllText(addedLrc, "[00:01.00]新增\n", new UTF8Encoding(false));
        Directory.SetLastWriteTimeUtc(dir, Directory.GetLastWriteTimeUtc(dir).AddSeconds(2));
        Program.Check("lrc 缓存: 目录 mtime 变化后可见", LrcReader.FindSidecar(added) == addedLrc,
            Path.GetFileName(LrcReader.FindSidecar(added) ?? "(null)"));

        // 显式清空缓存（每次构建开始前调用）：清后仍能正确解析
        Program.Check("lrc 缓存: Reset 返回清理条目数", LrcReader.ResetDirectoryCache() >= 1);
        Program.Check("lrc 缓存: Reset 后主侧车仍命中", LrcReader.FindSidecar(audio) == dupUpper,
            Path.GetFileName(LrcReader.FindSidecar(audio) ?? "(null)"));

        // ---- BeginScan（扫描器入口）：本轮**信任**目录 mtime，但侧车**文件**仍逐个 stat ----
        var scanDir = Path.Combine(root, "sidecar-scan-scope");
        Directory.CreateDirectory(scanDir);
        var scanAudio = Path.Combine(scanDir, "s.mp3");
        var scanLrc = Path.Combine(scanDir, "s.lrc");
        File.WriteAllText(scanLrc, "[00:01.00]第一版\n", new UTF8Encoding(false));
        LrcReader.BeginScan();
        var first = LrcReader.Find(scanAudio);
        Program.Check("lrc 扫描域: 首轮可见", first.Exists && first.Main == scanLrc,
            Path.GetFileName(first.Main ?? "(null)"));

        // 侧车**内容**改写（同名）：仍必须看到新 mtime/size —— 这是「信任目录 mtime」安全的前提
        var beforeMtime = first.Mtime;
        File.WriteAllText(scanLrc, "[00:01.00]第二版内容更长一点\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(scanLrc, DateTime.UtcNow.AddSeconds(5));
        var rewritten = LrcReader.Find(scanAudio);
        Program.Check("lrc 扫描域: 同名改写仍逐个 stat（mtime 前进）",
            rewritten.Mtime > beforeMtime && rewritten.Size != first.Size,
            $"{rewritten.Mtime}>{beforeMtime} size={rewritten.Size}/{first.Size}");

        // 本轮**新增**侧车：信任目录 mtime → 本轮不发现（与 scanner 对媒体文件的语义一致：
        // 扫描中途落盘的由下一轮发现）；下一轮 BeginScan 即可见
        var lateAudio = Path.Combine(scanDir, "late.mp3");
        Program.Check("lrc 扫描域: 新曲目本轮无侧车", !LrcReader.Find(lateAudio).Exists);
        var lateLrc = Path.Combine(scanDir, "late.lrc");
        File.WriteAllText(lateLrc, "[00:01.00]迟到\n", new UTF8Encoding(false));
        Program.Check("lrc 扫描域: 本轮新增侧车不在本轮生效（信任目录语义）",
            !LrcReader.Find(lateAudio).Exists, "(应在下一轮可见)");
        LrcReader.BeginScan();
        Program.Check("lrc 扫描域: 下一轮可见新侧车", LrcReader.Find(lateAudio).Main == lateLrc,
            Path.GetFileName(LrcReader.Find(lateAudio).Main ?? "(null)"));
    }

    /* ------------------------------------------------------------------ */
    /* .lrc 编码 / 标签剥离                                                */
    /* ------------------------------------------------------------------ */

    private static void LrcReading(string root)
    {
        var dir = Path.Combine(root, "lrc");
        Directory.CreateDirectory(dir);
        var audio = Path.Combine(dir, "enc.mp3");
        var sidecar = Path.Combine(dir, "enc.LRC"); // 大小写不敏感
        File.WriteAllText(sidecar, "[00:01.00]第一行\n[ar:某人]\n\n[00:02.00]第二行\n", new UTF8Encoding(true));
        var read = LrcReader.Read(audio);
        Program.Check("lrc: 存在", read.Exists);
        Program.Check("lrc: 大小写不敏感命中", LrcReader.FindSidecar(audio) is not null);
        Program.Check("lrc: 标签已剥离", read.Text == "第一行\n第二行", read.Text.Replace("\n", "\\n"));
        Program.Check("lrc: 行数==2", read.LineCount == 2, read.LineCount.ToString());
        Program.Check("lrc: size>0", read.Size > 0, read.Size.ToString());

        // UTF-16 LE（BOM）
        var audio16 = Path.Combine(dir, "u16.mp3");
        var sidecar16 = Path.Combine(dir, "u16.lrc");
        File.WriteAllText(sidecar16, "[00:01.00]你好世界\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var read16 = LrcReader.Read(audio16);
        Program.Check("lrc: UTF-16LE 解码", read16.Exists && read16.Text == "你好世界", read16.Text);

        // GBK / GB18030 双字节（无 BOM）：严格 UTF-8 失败 → 表解码。
        var audioGb = Path.Combine(dir, "gbk.mp3");
        var sidecarGb = Path.Combine(dir, "gbk.lrc");
        byte[] gbk =
        {
            0x5B, 0x30, 0x30, 0x3A, 0x30, 0x31, 0x2E, 0x30, 0x30, 0x5D, // [00:01.00]
            0xC4, 0xE3, 0xBA, 0xC3, 0xCA, 0xC0, 0xBD, 0xE7,             // 你好世界（GBK）
            0x0A,
        };
        File.WriteAllBytes(sidecarGb, gbk);
        var readGb = LrcReader.Read(audioGb);
        Program.Check("lrc: GBK 解码", readGb.Exists && readGb.Text == "你好世界", readGb.Text);

        // GBK 混排 ASCII 标签与中文，多行；标签剥离后应得中文文本。
        var audioGb2 = Path.Combine(dir, "gbk2.mp3");
        var sidecarGb2 = Path.Combine(dir, "gbk2.lrc");
        var gbk2 = new List<byte>();
        gbk2.AddRange(Encoding.ASCII.GetBytes("[by:test]\n[00:02.00]"));
        gbk2.AddRange(new byte[] { 0xC0, 0xB6, 0xCC, 0xEC, 0xB0, 0xD7, 0xD4, 0xC6 }); // 蓝天白云（GBK）
        gbk2.Add(0x0A);
        File.WriteAllBytes(sidecarGb2, gbk2.ToArray());
        var readGb2 = LrcReader.Read(audioGb2);
        Program.Check("lrc: GBK 混排解码", readGb2.Exists && readGb2.Text == "蓝天白云", readGb2.Text);

        // 无侧车
        Program.Check("lrc: 无侧车 Exists=false", !LrcReader.Read(Path.Combine(dir, "none.mp3")).Exists);
    }

    /* ------------------------------------------------------------------ */
    /* 多语言层拆分（T0）：内嵌双语 / .trans.lrc / .romaji.lrc / 纯文本    */
    /* ------------------------------------------------------------------ */

    private static void MultilingualLayers(string root)
    {
        var db = Path.Combine(root, "layers.db");
        var index = Path.Combine(root, "layers-idx.db");
        var dir = Path.Combine(root, "layers");
        Directory.CreateDirectory(dir);

        var songB = Path.Combine(dir, "bilingual.mp3");   // 内嵌双语（同时间戳 3 行）
        var songC = Path.Combine(dir, "sidecar.mp3");      // 显式 .trans / .romaji 侧车
        var songD = Path.Combine(dir, "plain.mp3");        // 纯文本内嵌
        var songE = Path.Combine(dir, "notrans.mp3");      // 无译文
        var songH = Path.Combine(dir, "dual.mp3");         // 双语主 .lrc（同时间戳 3 行）
        var songDup = Path.Combine(dir, "dup.mp3");        // 内嵌 == 侧车（去重用例）
        var songOne = Path.Combine(dir, "one.mp3");        // 仅内嵌（对照组）

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "tB", songB, "双语", "[]", null,
                "[00:01.00]我爱你\n[00:01.00]I love you\n[00:01.00]wo ai ni\n"
                + "[00:02.00]再见\n[00:02.00]goodbye", 1);
            InsertTrack(conn, "tC", songC, "侧车", "[]", null, null, 2);
            InsertTrack(conn, "tD", songD, "纯文本", "[]", null, "只此一行原文", 3);
            InsertTrack(conn, "tE", songE, "无译文", "[]", null, "没有翻译", 4);
            InsertTrack(conn, "tH", songH, "双语LRC", "[]", null, null, 5);
            // 去重用例：tDUP 的内嵌与侧车**同一文本**；tONE 只有内嵌（同文本）——
            // 除侧车外两行完全一致，故 FTS 各列 token 数应逐列相同（去重生效）。
            InsertTrack(conn, "tDUP", songDup, "去重", "[]", null, "[00:02.00]相同歌词内容", 6);
            InsertTrack(conn, "tONE", songOne, "去重", "[]", null, "[00:02.00]相同歌词内容", 7);
        }

        File.WriteAllText(Path.Combine(dir, "dual.lrc"),
            "[00:01.00]你好世界\n[00:01.00]hello world\n[00:01.00]ni hao shi jie\n", new UTF8Encoding(false));

        // tC / tE / tH 的侧车文本由**扫描器写入 library.db**（索引器只读 DB）：
        // 原始文本保时间轴，索引器经共享 LrcReader.FromRawTexts 拆层。
        using (var conn = Open(db))
        {
            SetSidecar(conn, "tC",
                "[00:01.00]月亮代表我的心\n[00:02.00]轻轻地\n",
                "[00:01.00]The moon represents my heart\n[00:02.00]gently\n",
                "[00:01.00]yue liang dai biao wo de xin\n[00:02.00]qing qing de\n");
            SetSidecar(conn, "tE", "[00:01.00]没有翻译\n");
            SetSidecar(conn, "tH",
                "[00:01.00]你好世界\n[00:01.00]hello world\n[00:01.00]ni hao shi jie\n");
            // 与内嵌同文本（仅时间轴不同）→ 归一后相同 → 应只索引一份
            SetSidecar(conn, "tDUP", "[00:01.00]相同歌词内容\n");
        }

        var result = RunEngine(db, index, full: true, lrc: true);
        Program.Check("层拆分: indexed==7", result.Indexed == 7, result.Indexed.ToString());

        // 内嵌与侧车**完全相同** → 只索引一份（真实曲库 126/151 首属这种情形，否则同一段
        // 文本在倒排里存两遍）。判定用 EraStore DocStore 的逐列 token 数：两行除
        // 侧车外完全一致，故去重生效时 12 列长度逐列相同（未去重则歌词 / 拼音列翻倍）。
        var dupSize = DocLengths(index, "tDUP");
        var oneSize = DocLengths(index, "tONE");
        Program.Check("去重: 内嵌==侧车时逐列 token 数与仅内嵌完全一致",
            dupSize.Length == 12 && dupSize.SequenceEqual(oneSize),
            $"{string.Join(',', dupSize)} vs {string.Join(',', oneSize)}");

        // 内嵌双语（同时间戳 3 行）：原文 / 译文 / 音译分别入列，互不掺混。
        Program.Check("层拆分: 内嵌原文 '我爱你' 命中原文列 tB",
            MatchContainsTrack(index, "{lyrics_tok} : \"我爱 爱你\"", "tB"));
        Program.Check("层拆分: 内嵌英文译文 'love' 命中译文列 tB",
            MatchContainsTrack(index, "{lyrics_tr_tok} : \"love\"", "tB"));
        Program.Check("层拆分: 内嵌罗马音 'wo' 命中音译列 tB",
            MatchContainsTrack(index, "{lyrics_rom_tok} : \"wo\"", "tB"));
        Program.Check("层拆分: 原文列不含译文 'love'（不回退）",
            !MatchContainsTrack(index, "{lyrics_tok} : \"love\"", "tB"));
        Program.Check("层拆分: 译文列不含原文 '我'",
            MatchCount(index, "{lyrics_tr_tok} : \"我\"") == 0);

        // 显式侧车：主 .lrc 原文入原文列；.trans.lrc / .romaji.lrc 各入对应层。
        Program.Check("侧车: 主 .lrc 原文 '月亮' 命中原文列 tC",
            MatchContainsTrack(index, "{lyrics_tok} : \"月亮\"", "tC"));
        Program.Check("侧车: .trans.lrc 'moon' 命中译文列 tC",
            MatchContainsTrack(index, "{lyrics_tr_tok} : \"moon\"", "tC"));
        Program.Check("侧车: .romaji.lrc 'yue' 命中音译列 tC",
            MatchContainsTrack(index, "{lyrics_rom_tok} : \"yue\"", "tC"));

        // 双语主 .lrc（同时间戳 3 行）：英文译文可被英文查询命中，罗马音命中音译列。
        Program.Check("双语.lrc: 原文 '你好' 命中原文列 tH",
            MatchContainsTrack(index, "{lyrics_tok} : \"你好\"", "tH"));
        Program.Check("双语.lrc: 英文译文 'hello' 命中译文列 tH",
            MatchContainsTrack(index, "{lyrics_tr_tok} : \"hello\"", "tH"));
        Program.Check("双语.lrc: 罗马音 'hao' 命中音译列 tH",
            MatchContainsTrack(index, "{lyrics_rom_tok} : \"hao\"", "tH"));

        // 纯文本：整体入原文列；译文 / 音译层空。
        Program.Check("纯文本: '只此' 命中原文列 tD",
            MatchContainsTrack(index, "{lyrics_tok} : \"只此\"", "tD"));
        Program.Check("纯文本: 译文列为空",
            MatchCount(index, "{lyrics_tr_tok} : \"只此\"") == 0);

        // 无译文：原文命中；译文 / 音译层空。
        // 字面歌词层只入 bigram（T3.7），故表达式用 bigram 短语（不再含单字 unigram）。
        Program.Check("无译文: '没有翻译' 命中原文列 tE",
            MatchContainsTrack(index, "{lyrics_tok} : \"没有 有翻 翻译\"", "tE"));
        Program.Check("无译文: 译文列空（tE 不命中）",
            !MatchContainsTrack(index, "{lyrics_tr_tok} : \"没有 有翻 翻译\"", "tE"));

        // T1 素材就位（查询侧跨语言候选由 Dart 编译，这里验证索引内容可供召回）：
        // 英文词 love 的候选汉字含「爱」；英文查询经候选「爱」可命中含「爱」的中文内容（tB 原文）。
        Program.Check("T1 素材: AnalyzeQuery('love') 候选含 爱",
            Analyzer.AnalyzeQuery("love").CrossLingual.Contains("爱"));
        Program.Check("T1 素材: 候选 '爱' 命中 tB 原文",
            MatchContainsTrack(index, "{lyrics_tok} : \"爱\"*", "tB"));
        // 反向：汉字「爱」的英文释义词含 love；可命中含 love 的英文层（tB 译文）。
        Program.Check("T1 素材: AnalyzeQuery('爱') 候选含 love",
            Analyzer.AnalyzeQuery("爱").CrossLingual.Contains("love"));
        Program.Check("T1 素材: 候选 'love' 命中 tB 译文",
            MatchContainsTrack(index, "{lyrics_tr_tok} : \"love\"", "tB"));
    }

    /* ------------------------------------------------------------------ */
    /* 歌词层预处理（T3.5 去重 / T3.8 V6 护栏）与字面歌词去 unigram（T3.7） */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 索引路径上的预处理语义：
    /// <list type="bullet">
    ///   <item>重复副歌只索引一次（token 数 = 去重后行数 × 每行 bigram 数）；</item>
    ///   <item>短语仍按行内 bigram 连续匹配（去重不改行内位置）；</item>
    ///   <item>V6 护栏逐层截断，metric = (曲目, 层) 数、warn 每曲一次；</item>
    ///   <item>字面歌词列无 unigram（元数据列仍有）；</item>
    ///   <item><c>library.db</c> 原文不被改写（片段 / 高亮读库）。</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// 超长词条（&gt; <see cref="EraStore.EraFormat.MaxTermBytes"/>）不再丢弃，而是经
    /// <see cref="EraStore.TermCodec"/> 换成 <c>0x01 + MD5 hex</c> 的不透明词条后入库（S3）：
    /// 构建成功、计数为 0、同曲其余字段仍可检索，且**原长词本身仍能被查询命中**。
    ///
    /// v15（T3.9）：单行 300 字不再产出 300B 的首字母串——首字母列改为 2 字节 bigram，
    /// 超长只来自拉丁长词；用例同时锁住「bigram 化顺带消除了首字母超长风险」。
    /// </summary>
    private static void OversizeTermGuard(string root)
    {
        var work = Path.Combine(root, "oversize-term");
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        var longWord = new string('z', 300);   // 300B 拉丁词（> 255B）
        var longInitials = new string('爱', 300); // 单行 300 字 → v15 起是 2B bigram，不再超长

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "longWord", Path.Combine(dir, "lw.mp3"), "长词", "[]", null,
                longWord + "\n晴天", 1);
            InsertTrack(conn, "longInitials", Path.Combine(dir, "li.mp3"), "长首字母", "[]", null,
                longInitials, 2);
        }

        var result = RunEngine(db, index, full: true, lrc: false);
        var canonical = EraStore.TermCodec.Canonicalize(longWord);
        Program.Check("超长词条: 构建成功且两首全部索引", result.Indexed == 2, result.Indexed.ToString());
        Program.Check("超长词条: oversizeTokens == 0（改为哈希词条而非丢弃）",
            result.OversizeTokens == 0, result.OversizeTokens.ToString());
        Program.Check("超长词条: 规范化词条已入歌词词典",
            MatchContainsTrack(index, "{lyrics_tok} : " + SearchQuery.Quote(canonical), "longWord"),
            canonical);
        // 直接拼原始词条（绕过 Quote 的规范化）验证词典里**没有** 300B 原文键。
        Program.Check("超长词条: 词典里没有原始 300B 词条",
            !MatchContainsTrack(index, "{lyrics_tok} : \"" + longWord + "\"", "longWord"),
            "raw 300B term found");
        Program.Check("超长词条: 查询原始 300B 词命中该曲（生产查询路径）",
            SessionHit(index, db, longWord, "longWord"));
        Program.Check("超长词条: 长首字母曲的首字母 bigram 正常入库",
            MatchContainsTrack(index, "{initials_lyrics_tok} : \"aa\"", "longInitials"),
            "aa not found");
        Program.Check("超长词条: 同曲其余词仍可检索",
            MatchContainsTrack(index, "{lyrics_tok} : \"晴天\"", "longWord"),
            "晴天 not found");
        Program.Check("超长词条: 普通查询仍命中同曲（生产查询路径）",
            SessionHit(index, db, "晴天", "longWord"));
        Program.Check("超长词条: 结果 JSON additive 字段",
            IndexerJson.Result(result).Contains("\"oversizeTokens\":", StringComparison.Ordinal),
            IndexerJson.Result(result));
        var stats = EraStore.IndexStats.Collect(EraDir(index));
        Program.Check("超长词条: 索引仍可统计（2 曲 / 0 死行）",
            stats.Docs == 2 && stats.DeadDocs == 0, $"{stats.Docs}/{stats.DeadDocs}");
    }

    private static void LyricsPreprocessIntegration(string root)
    {
        var work = Path.Combine(root, "lyrics-preprocess");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        // 重复副歌（非相邻，验证「保序首现」与跨重复段的位置重排）。
        const string chorusLyrics = "副歌一句\n主歌一句\n副歌一句\n重复句\n副歌一句";
        var manyLines = string.Join('\n', Enumerable.Range(0, 4097).Select(i => "M" + i));
        var manyLineTranslations = string.Join('\n', Enumerable.Range(0, 4097).Select(i => "T" + i));
        var manyBytes = string.Join('\n', Enumerable.Range(0, 4096)
            .Select(i => new string('歌', 10) + i.ToString("D4")));

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "dup", Path.Combine(dir, "dup.mp3"), "副歌测试", "[]", null,
                chorusLyrics, 1);
            InsertTrack(conn, "normal", Path.Combine(dir, "normal.mp3"), "普通歌词", "[]", null,
                "晴天\n雨天", 2);
            InsertTrack(conn, "guardLines", Path.Combine(dir, "lines.mp3"), "行护栏", "[]", null,
                manyLines, 3);
            InsertTrack(conn, "guardBytes", Path.Combine(dir, "bytes.mp3"), "字节护栏", "[]", null,
                manyBytes, 4);
            // 同一曲的两层都超限：metric 记 2（层维度），warn 只记 1 次（曲目维度）。
            InsertTrack(conn, "multi", Path.Combine(dir, "multi.mp3"), "多层护栏", "[]", null,
                manyLines, 5);
            SetSidecar(conn, "multi", main: null, trans: manyLineTranslations);
        }

        // 捕获 stderr（无 sink 时的 Log 回退）验证「每曲最多 warn 一次」。
        var originalError = Console.Error;
        var log = new StringWriter();
        IndexerResult result;
        Console.SetError(log);
        try
        {
            result = RunEngine(db, index, full: true, options: new IndexerOptions { IndexLrc = true });
        }
        finally
        {
            Console.SetError(originalError);
        }

        Program.Check("预处理集成: 5 首全部索引", result.Indexed == 5, result.Indexed.ToString());

        // ---- 去重：副歌 5 行 → 3 行；3 行共 8 个 bigram（3+3+2）----
        var dupLengths = DocLengths(index, "dup");
        Program.Check("预处理集成: 重复副歌只索引一次（歌词 token = 8）",
            dupLengths[4] == 8, dupLengths[4].ToString());
        Program.Check("预处理集成: 去重后行内短语仍连续",
            MatchContainsTrack(index, "{lyrics_tok} : \"副歌 歌一 一句\"", "dup"));
        Program.Check("预处理集成: 首现顺序保留（主歌在重复句前）",
            MatchContainsTrack(index, "{lyrics_tok} : \"主歌 歌一 一句\"", "dup"));

        // ---- T3.7：字面歌词列无 unigram；元数据列仍有 ----
        Program.Check("预处理集成: 歌词列不再有单字 unigram",
            MatchCount(index, "{lyrics_tok} : \"句\"") == 0,
            MatchCount(index, "{lyrics_tok} : \"句\"").ToString());
        Program.Check("预处理集成: 元数据列仍有 unigram（标题「副」）",
            MatchContainsTrack(index, "{title_tok} : \"副\"", "dup"));
        Program.Check("预处理集成: 正常歌词 token 不变（晴天 + 雨天 = 2 bigram）",
            DocLengths(index, "normal")[4] == 2, DocLengths(index, "normal")[4].ToString());

        // ---- V6 护栏：逐层截断 + metric + 每曲一次 warn ----
        Program.Check("预处理集成: metric = (曲目, 层) 4 次",
            result.LyricsTruncatedLayers == 4, result.LyricsTruncatedLayers.ToString());
        Program.Check("预处理集成: metric = 受影响曲目 3 首",
            result.LyricsTruncatedTracks == 3, result.LyricsTruncatedTracks.ToString());
        Program.Check("预处理集成: 行护栏截到 4096 行（每行字母+数字=2 token）",
            DocLengths(index, "guardLines")[4] == 8192,
            DocLengths(index, "guardLines")[4].ToString());
        Program.Check("预处理集成: 同曲原文 + 译文两层独立截断",
            DocLengths(index, "multi")[4] == 8192 && DocLengths(index, "multi")[5] == 8192,
            $"{DocLengths(index, "multi")[4]}/{DocLengths(index, "multi")[5]}");

        var captured = log.ToString();
        Program.Check("预处理集成: 每曲最多 warn 一次（行护栏）",
            Occurrences(captured, "track=guardLines") == 1,
            Occurrences(captured, "track=guardLines").ToString());
        Program.Check("预处理集成: 每曲最多 warn 一次（字节护栏）",
            Occurrences(captured, "track=guardBytes") == 1,
            Occurrences(captured, "track=guardBytes").ToString());
        Program.Check("预处理集成: 多层超限只 warn 一次（multi）",
            Occurrences(captured, "track=multi") == 1,
            Occurrences(captured, "track=multi").ToString());
        Program.Check("预处理集成: 正常曲目不 warn",
            Occurrences(captured, "track=dup") == 0
            && Occurrences(captured, "track=normal") == 0,
            captured);

        // ---- 结果 JSON：新增字段 additive（不改既有键） ----
        var resultJson = IndexerJson.Result(result);
        Program.Check("预处理集成: 结果 JSON 含 lyricsTruncatedLayers/Tracks",
            resultJson.Contains("\"lyricsTruncatedLayers\":4", StringComparison.Ordinal)
            && resultJson.Contains("\"lyricsTruncatedTracks\":3", StringComparison.Ordinal)
            && resultJson.Contains("\"eraLiveDocs\":5", StringComparison.Ordinal),
            resultJson);
        var progressJson = IndexerJson.Progress(new IndexerProgress
        {
            LyricsTruncatedLayers = 4,
            LyricsTruncatedTracks = 3,
        });
        Program.Check("预处理集成: 进度 JSON 含截断计数",
            progressJson.Contains("\"lyricsTruncatedLayers\":4", StringComparison.Ordinal)
            && progressJson.Contains("\"lyricsTruncatedTracks\":3", StringComparison.Ordinal),
            progressJson);

        // ---- library.db 源文本不变（片段 / 高亮读库原文） ----
        using (var conn = Open(db))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT lyrics FROM tracks WHERE id='dup'";
            var stored = cmd.ExecuteScalar() as string;
            Program.Check("预处理集成: library.db 原文未被改写",
                string.Equals(stored, chorusLyrics, StringComparison.Ordinal), stored ?? "(null)");
        }
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /* ------------------------------------------------------------------ */
    /* T2-9 歌词罗马音生成（可选增强）                                     */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 开关关闭：<c>lyrics_rom_tok</c> 只含 LRC 音译层（此处 tK 的整词 sora）。
    /// 开关开启：无音译层的日语歌词自动生成读音 bigram（tJ 的 空 → so or ra），
    /// 已有用户音译不被覆盖；开关变化触发全量重建；查询侧读音趟能命中生成列。
    /// </summary>
    private static void RomajiLyrics(string root)
    {
        var work = Path.Combine(root, "romaji");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            // tJ：日语歌词（含假名、无音译层）→ 开启后应由「空」的读音 そら 生成 bigram。
            InsertTrack(conn, "tJ", Path.Combine(dir, "j.mp3"), "夜に駆ける", "[]", null, "空は青い", 1);
            // tK：带 LRC 音译层（整词 sora）→ 开启后也不得被生成内容覆盖。
            InsertTrack(conn, "tK", Path.Combine(dir, "k.mp3"), "Sidecar", "[]", null, null, 2);
            SetSidecar(conn, "tK", "[00:01.00]空\n", romaji: "[00:01.00]sora\n");
        }

        const string SoraPhrase = "{lyrics_rom_tok} : (\"so or ra\")";

        // ---- 默认关：不生成歌词读音 ----
        var off = RunEngine(db, index, full: true, options: new IndexerOptions { IndexLrc = true });
        Program.Check("歌词罗马音: 默认关 indexed==2", off.Indexed == 2, off.Indexed.ToString());
        Program.Check("歌词罗马音: 默认关 meta.romaji_lyrics==0",
            !ManifestHas(ReadManifest(index), EraStore.ManifestOptionsBits.RomajiLyrics));
        Program.Check("歌词罗马音: 默认关不生成 tJ 读音 bigram", MatchCount(index, SoraPhrase) == 0,
            MatchCount(index, SoraPhrase).ToString());
        Program.Check("歌词罗马音: 默认关 LRC 音译整词仍在（tK）",
            MatchContainsTrack(index, "{lyrics_rom_tok} : \"sora\"", "tK"));

        // ---- 开启（增量请求也会因选项变化升级为全量）----
        var on = RunEngine(db, index, full: false,
            options: new IndexerOptions { IndexLrc = true, RomajiLyrics = true });
        Program.Check("歌词罗马音: 开关变化触发全量", on.Full);
        Program.Check("歌词罗马音: 开启 meta.romaji_lyrics==1",
            ManifestHas(ReadManifest(index), EraStore.ManifestOptionsBits.RomajiLyrics));
        Program.Check("歌词罗马音: tJ 生成读音 bigram 可命中", MatchContainsTrack(index, SoraPhrase, "tJ"));
        Program.Check("歌词罗马音: 用户音译未被覆盖（tK 整词仍在）",
            MatchContainsTrack(index, "{lyrics_rom_tok} : \"sora\"", "tK"));
        var readingExpr = SearchQuery.CompileReading(Analyzer.AnalyzeQuery("sora"), metadata: false);
        Program.Check("歌词罗马音: 查询侧读音趟命中生成列",
            readingExpr is not null && MatchContainsTrack(index, readingExpr, "tJ"), readingExpr ?? "(null)");

        // ---- 再关闭：清理生成内容 ----
        var back = RunEngine(db, index, full: false, options: new IndexerOptions { IndexLrc = true });
        Program.Check("歌词罗马音: 关闭触发全量", back.Full);
        Program.Check("歌词罗马音: 关闭后 tJ 读音 bigram 归零", MatchCount(index, SoraPhrase) == 0,
            MatchCount(index, SoraPhrase).ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 生产路径派生转写层免位置（S2）                                      */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 生产路径只在**显式音译层**免位置（S2 修正，2026-11-26）：
    /// <see cref="IndexerEngine"/> 全量 / 增量使用 <see cref="EraStore.BuildDefaults"/>，
    /// 其中只有 <c>LyricsRomanization</c>；<c>PinyinLyrics</c> / <c>PinyinMeta</c> 必须
    /// 保留位置（5k qbench eval 证明拼音短语降级为 AND 会显著掉 P@1 / NDCG）。
    /// </summary>
    private static void ProductionRomanizationNoPositions(string root)
    {
        var work = Path.Combine(root, "production-no-pos");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        // t0 的音译 / 拼音里 qing 与 tian 相邻；t1 故意插两个音节，用来证明
        // 无位置音译的短语降级为 AND，而拼音短语仍按位置精确匹配。
        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "np0", Path.Combine(dir, "np0.mp3"), "晴天", "[]", null, "晴天", 1000);
            InsertTrack(conn, "np1", Path.Combine(dir, "np1.mp3"), "雨天", "[]", null, "晴爱你天", 2000);
        }
        using (var conn = Open(db))
        {
            SetSidecar(conn, "np0", "[00:01.00]晴天\n", romaji: "[00:01.00]qing tian\n");
            SetSidecar(conn, "np1", "[00:01.00]晴爱你天\n", romaji: "[00:01.00]qing ai ni tian\n");
        }

        var result = RunEngine(db, index, full: true,
            options: new IndexerOptions { IndexLrc = true, Pinyin = true, RomajiLyrics = true });
        Program.Check("生产免位置: indexed==2", result.Indexed == 2, result.Indexed.ToString());
        Program.Check("生产免位置: romaji 选项已开",
            ManifestHas(ReadManifest(index), EraStore.ManifestOptionsBits.RomajiLyrics));

        Program.Check("生产免位置: BuildDefaults 只含歌词音译层",
            EraStore.BuildDefaults.NoPositionFields.SequenceEqual(new[]
            {
                EraStore.FieldId.LyricsRomanization,
            }),
            string.Join(',', EraStore.BuildDefaults.NoPositionFields));

        var sawRoman = false;
        var sawPinyinLyrics = false;
        var sawPinyinMeta = false;
        var sawRomanPhrase = false;
        var sawPinyinPhrase = false;

        using var reader = new EraStore.StoreReader(EraFile(index));
        foreach (var seg in reader.OpenPerSegment())
        {
            if (seg.Dict.TryFind(EraStore.FieldId.LyricsRomanization, "qing", out var roman))
            {
                sawRoman = true;
                Program.Check("生产免位置: LyricsRomanization 词条 flags bit0=0",
                    !roman.HasPositions, $"flags=0x{roman.Flags:X2}");
                var cursor = seg.Postings.Open(roman.PostOffset, roman.Df);
                Program.Check("生产免位置: LyricsRomanization 游标 HasPositions=false",
                    cursor.Next() && !cursor.HasPositions,
                    $"hasPos={cursor.HasPositions} tf={cursor.CurrentTf}");
                if (PhraseDocs(seg, EraStore.FieldId.LyricsRomanization, "qing", "tian",
                        out var positioned) is { } docs)
                {
                    sawRomanPhrase = true;
                    Program.Check("生产免位置: 歌词音译短语 qing tian 降级 AND（doc0+doc1 / Positioned=false）",
                        docs == "0,1" && !positioned, $"{docs}/{positioned}");
                }
            }

            if (seg.Dict.TryFind(EraStore.FieldId.PinyinLyrics, "qing", out var pinyinLyrics))
            {
                sawPinyinLyrics = true;
                Program.Check("生产免位置: PinyinLyrics 仍带位置",
                    pinyinLyrics.HasPositions, $"flags=0x{pinyinLyrics.Flags:X2}");
                var cursor = seg.Postings.Open(pinyinLyrics.PostOffset, pinyinLyrics.Df);
                Program.Check("生产免位置: PinyinLyrics 游标 HasPositions=true",
                    cursor.Next() && cursor.HasPositions,
                    $"hasPos={cursor.HasPositions} tf={cursor.CurrentTf}");
                if (PhraseDocs(seg, EraStore.FieldId.PinyinLyrics, "qing", "tian",
                        out var positioned) is { } docs)
                {
                    sawPinyinPhrase = true;
                    Program.Check("生产免位置: 歌词拼音短语仍校验相邻（只有 doc0 / Positioned=true）",
                        docs == "0" && positioned, $"{docs}/{positioned}");
                }
            }

            if (seg.Dict.TryFind(EraStore.FieldId.PinyinMeta, "qing", out var pinyinMeta))
            {
                sawPinyinMeta = true;
                var cursor = seg.Postings.Open(pinyinMeta.PostOffset, pinyinMeta.Df);
                Program.Check("生产免位置: PinyinMeta 仍带位置（短语精度不回退）",
                    pinyinMeta.HasPositions && cursor.Next() && cursor.HasPositions,
                    $"flags=0x{pinyinMeta.Flags:X2} hasPos={cursor.HasPositions}");
            }
        }

        Program.Check("生产免位置: 两个字段都出现在词典", sawRoman && sawPinyinLyrics,
            $"roman={sawRoman} pinyinLyrics={sawPinyinLyrics}");
        Program.Check("生产免位置: 元数据拼音仍带位置", sawPinyinMeta, sawPinyinMeta.ToString());
        Program.Check("生产免位置: 音译降级 / 拼音保位置都已覆盖",
            sawRomanPhrase && sawPinyinPhrase, $"roman={sawRomanPhrase} pinyin={sawPinyinPhrase}");

        var verify = EraStore.IndexVerifier.Verify(EraFile(index));
        Program.Check("生产免位置: IndexVerifier.Verify 通过", verify.Ok,
            string.Join("; ", verify.Errors));
    }

    /// <summary>
    /// 在单段内跑 <c>qing tian</c> 短语：返回命中 doc 序列（找不到词条时 null）。
    /// <c>positioned</c> 是 <see cref="PhraseIter.Positioned"/> 的最终事实。
    /// </summary>
    private static string? PhraseDocs(
        EraStore.SegmentReaders seg, EraStore.FieldId field, string first, string second,
        out bool positioned)
    {
        positioned = true;
        if (!seg.Dict.TryFind(field, first, out _) || !seg.Dict.TryFind(field, second, out _))
            return null;

        var terms = new IDocIter[]
        {
            new TermIter(seg.Dict, seg.Postings, field, first),
            new TermIter(seg.Dict, seg.Postings, field, second),
        };
        using var phrase = new PhraseIter(terms, positioned: true);
        var docs = new List<int>();
        while (phrase.Next()) docs.Add(phrase.Current);
        positioned = phrase.Positioned;
        return string.Join(',', docs);
    }

    /// <summary>
    /// T3.4 V4：歌词首字母默认关。
    ///
    /// 默认：<c>initials_lyrics_tok</c> 无词条、Manifest 不写选项位 / 不宣称能力、
    /// 健康元数据报 false，但歌词**拼音**与元数据首字母照旧可召回；
    /// 开启：开关位翻转触发全量重建，列有词条、能力 / 健康元数据同步，查询命中；
    /// 再关闭：反向触发全量并清空歌词首字母。
    /// </summary>
    private static void InitialsLyricsOption(string root)
    {
        var work = Path.Combine(root, "initials-lyrics");
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
            InsertTrack(conn, "il1", Path.Combine(dir, "il1.mp3"), "测试", "[]", null, "我爱你中国", 1000);

        // v15：首字母列是相邻 bigram，整段查询写成短语。
        const string LyricsInitials = "{initials_lyrics_tok} : \"wa an nz zg\"";
        const string LyricsPinyin = "{pinyin_lyrics_tok} : \"wo ai ni\"";

        // ---- 默认关 ----
        var off = RunEngine(db, index, full: true, options: new IndexerOptions { Pinyin = true });
        Program.Check("歌词首字母: 默认关 indexed==1", off.Indexed == 1, off.Indexed.ToString());
        var offManifest = ReadManifest(index);
        Program.Check("歌词首字母: 默认关 meta.initials_lyrics==0",
            !ManifestHas(offManifest, EraStore.ManifestOptionsBits.InitialsLyrics));
        Program.Check("歌词首字母: 默认关不宣称 initials_lyrics 能力",
            !offManifest.Capabilities.Contains(IndexCapabilities.InitialsLyrics),
            string.Join(',', offManifest.Capabilities));
        Program.Check("歌词首字母: 默认关 initials_lyrics_tok 无词条",
            MatchCount(index, LyricsInitials) == 0, MatchCount(index, LyricsInitials).ToString());
        Program.Check("歌词首字母: 默认关歌词拼音仍可召回",
            MatchContainsTrack(index, LyricsPinyin, "il1"));
        Program.Check("歌词首字母: 默认关元数据首字母不受影响",
            MatchContainsTrack(index, "{initials_meta_tok} : \"cs\"", "il1"));
        Program.Check("歌词首字母: 默认关查询路径不命中歌词首字母串",
            !SessionHit(index, db, "wanzg", "il1"));
        var offHealth = SearchApiJson.HealthReady(SearchSession.MetaFromManifest(offManifest));
        Program.Check("歌词首字母: 默认关健康元数据报 false",
            offHealth.Contains("\"initialsLyrics\":false", StringComparison.Ordinal), offHealth);

        // ---- 开启（增量请求也会因选项变化升级为全量）----
        var on = RunEngine(db, index, full: false,
            options: new IndexerOptions { Pinyin = true, InitialsLyrics = true });
        Program.Check("歌词首字母: 开关翻转触发全量重建", on.Full, $"full={on.Full}");
        var onManifest = ReadManifest(index);
        Program.Check("歌词首字母: 开启 meta.initials_lyrics==1",
            ManifestHas(onManifest, EraStore.ManifestOptionsBits.InitialsLyrics));
        Program.Check("歌词首字母: 开启宣称 initials_lyrics 能力",
            onManifest.Capabilities.Contains(IndexCapabilities.InitialsLyrics),
            string.Join(',', onManifest.Capabilities));
        Program.Check("歌词首字母: 开启后列有词条",
            MatchContainsTrack(index, LyricsInitials, "il1"));
        Program.Check("歌词首字母: 开启后查询命中歌词首字母串",
            SessionHit(index, db, "wanzg", "il1"));
        var onHealth = SearchApiJson.HealthReady(SearchSession.MetaFromManifest(onManifest));
        Program.Check("歌词首字母: 开启健康元数据报 true",
            onHealth.Contains("\"initialsLyrics\":true", StringComparison.Ordinal), onHealth);

        // ---- 再关闭：反向翻转同样触发全量并清空 ----
        var back = RunEngine(db, index, full: false, options: new IndexerOptions { Pinyin = true });
        Program.Check("歌词首字母: 关闭触发全量重建", back.Full, $"full={back.Full}");
        Program.Check("歌词首字母: 关闭后列词条归零",
            MatchCount(index, LyricsInitials) == 0, MatchCount(index, LyricsInitials).ToString());
    }

    /// <summary>
    /// T3.9：首字母 bigram 召回（元数据列；歌词列由 <see cref="InitialsLyricsOption"/> 覆盖）。
    ///
    /// 用例矩阵：
    /// <list type="bullet">
    ///   <item>段中间两字（<c>nz</c>）可命中 —— 整段 token 时代只有前缀能命中；</item>
    ///   <item>整段（<c>wanzg</c>）/ 段首子串（<c>wan</c>）仍命中；</item>
    ///   <item>同一批 bigram 的乱序 / 非相邻排列不命中（短语而不是 AND）；</item>
    ///   <item>title 末尾 bigram 不能与 artist 开头 bigram 拼成假短语（边界词元）；</item>
    ///   <item>纯拉丁 / 数字标题不产生任何首字母词元。</item>
    /// </list>
    /// </summary>
    private static void InitialsBigramSearch(string root)
    {
        var work = Path.Combine(root, "initials-bigram");
        var db = Path.Combine(work, "library.db");
        var index = Path.Combine(work, "search.db");
        var dir = Path.Combine(work, "m");
        Directory.CreateDirectory(dir);

        CreateLibrary(db, generation: 1);
        using (var conn = Open(db))
        {
            InsertTrack(conn, "ib1", Path.Combine(dir, "ib1.mp3"), "我爱你中国",
                """[{"name":"李雷"}]""", null, null, 10);
            // nz 在段中间（an nz zb）——旧整段 token "anzb" 只能前缀命中。
            InsertTrack(conn, "ib2", Path.Combine(dir, "ib2.mp3"), "啊你中吧",
                """[{"name":"某某"}]""", null, null, 20);
            InsertTrack(conn, "ib3", Path.Combine(dir, "ib3.mp3"), "你中",
                """[{"name":"某"}]""", null, null, 30);
            // 与 ib3 的 bigram 相同、顺序相反：nz 不得命中。
            InsertTrack(conn, "ib4", Path.Combine(dir, "ib4.mp3"), "中你",
                """[{"name":"某"}]""", null, null, 40);
            // 纯拉丁 / 数字：不得产生首字母词元。
            InsertTrack(conn, "ib5", Path.Combine(dir, "ib5.mp3"), "Hello",
                """[{"name":"ADELE"}]""", """{"name":"25"}""", null, 50);
            // 跨边界：title "我爱你"（wa an）+ artist "中国"（zg）。
            InsertTrack(conn, "ib6", Path.Combine(dir, "ib6.mp3"), "我爱你",
                """[{"name":"中国"}]""", null, null, 60);
            // 歌手名的段中间 bigram（阿你中 → an nz）同样可命中。
            InsertTrack(conn, "ib7", Path.Combine(dir, "ib7.mp3"), "无关",
                """[{"name":"阿你中"}]""", null, null, 70);
        }

        var result = RunEngine(db, index, full: true, lrc: false);
        Program.Check("首字母 bigram: 全量 7 曲", result.Indexed == 7, result.Indexed.ToString());

        // 段中间两字：bigram 词元直接命中（旧整段 token 时代不可能）。
        Program.Check("首字母 bigram: 段中间 'nz' 命中 ib2",
            MatchContainsTrack(index, "{initials_meta_tok} : \"nz\"", "ib2"));
        Program.Check("首字母 bigram: 'nz' 命中段首 ib3",
            MatchContainsTrack(index, "{initials_meta_tok} : \"nz\"", "ib3"));
        Program.Check("首字母 bigram: 'nz' 命中歌手段中间 ib7",
            MatchContainsTrack(index, "{initials_meta_tok} : \"nz\"", "ib7"));
        Program.Check("首字母 bigram: 'nz' 不命中反序 ib4（zn）",
            !MatchContainsTrack(index, "{initials_meta_tok} : \"nz\"", "ib4"));
        Program.Check("首字母 bigram: 单字母词元不存在（'z' 命中 0）",
            MatchCount(index, "{initials_meta_tok} : \"z\"") == 0,
            MatchCount(index, "{initials_meta_tok} : \"z\"").ToString());

        // 整段 / 段首子串仍命中。
        Program.Check("首字母 bigram: 整段 'wa an nz zg' 命中 ib1",
            MatchContainsTrack(index, "{initials_meta_tok} : \"wa an nz zg\"", "ib1"));
        Program.Check("首字母 bigram: 段首子串 'wa an' 命中 ib1",
            MatchContainsTrack(index, "{initials_meta_tok} : \"wa an\"", "ib1"));
        Program.Check("首字母 bigram: 段中子串 'an nz' 命中 ib1",
            MatchContainsTrack(index, "{initials_meta_tok} : \"an nz\"", "ib1"));

        // 顺序 / 相邻性：同一批 bigram 反序或非相邻都不得命中。
        Program.Check("首字母 bigram: 反序短语 'nz wa' 命中 0",
            MatchCount(index, "{initials_meta_tok} : \"nz wa\"") == 0,
            MatchCount(index, "{initials_meta_tok} : \"nz wa\"").ToString());
        Program.Check("首字母 bigram: 非相邻短语 'wa nz' 命中 0",
            MatchCount(index, "{initials_meta_tok} : \"wa nz\"") == 0,
            MatchCount(index, "{initials_meta_tok} : \"wa nz\"").ToString());
        Program.Check("首字母 bigram: 'an nz' 命中 ib2（中间段）",
            MatchContainsTrack(index, "{initials_meta_tok} : \"an nz\"", "ib2"));

        // 跨字段边界：title 的 an 与 artist 的 zg 之间有边界词元，不能拼成短语。
        Program.Check("首字母 bigram: title/artist 边界阻断 'an zg'",
            MatchCount(index, "{initials_meta_tok} : \"an zg\"") == 0,
            MatchCount(index, "{initials_meta_tok} : \"an zg\"").ToString());
        Program.Check("首字母 bigram: ib6 自身整段不成立（wa an # zg）",
            !MatchContainsTrack(index, "{initials_meta_tok} : \"wa an zg\"", "ib6"));

        // 拉丁 / 数字不进首字母列：ib5 (Hello / ADELE / 25) 没有任何 bigram 词元。
        foreach (var bg in new[] { "he", "el", "ll", "lo", "ad", "de", "le" })
        {
            Program.Check($"首字母 bigram: 拉丁/数字不产 '{bg}'",
                !MatchContainsTrack(index, $"{{initials_meta_tok}} : \"{bg}\"", "ib5"),
                bg);
        }

        // 生产查询路径：直接键入的拉丁首字母串按 bigram 分解后仍可召回。
        Program.Check("首字母 bigram: 查询 'wanzg' 命中 ib1",
            SessionHit(index, db, "wanzg", "ib1"));
        Program.Check("首字母 bigram: 查询 'wan' 命中 ib1",
            SessionHit(index, db, "wan", "ib1"));
        Program.Check("首字母 bigram: 查询 'nz' 命中段中间 ib2",
            SessionHit(index, db, "nz", "ib2"));
        Program.Check("首字母 bigram: 查询 'nz' 不命中反序 ib4",
            !SessionHit(index, db, "nz", "ib4"));
    }

    /* ------------------------------------------------------------------ */
    /* 引擎 / fixture 辅助                                                 */
    /* ------------------------------------------------------------------ */

    internal static IndexerResult RunEngine(string db, string index, bool full, bool lrc)
        => RunEngineDetailed(db, index, full, lrc).Result;

    /// <summary>按显式选项运行引擎（T2-9 等开关需要）。</summary>
    private static IndexerResult RunEngine(string db, string index, bool full, IndexerOptions options)
        => RunEngineDetailed(db, index, full, options).Result;

    private static (IndexerResult Result, int ContentQueries) RunEngineDetailed(
        string db, string index, bool full, IndexerOptions options)
    {
        var engine = new IndexerEngine();
        var result = engine.Run(db, index, full, options);
        return (result, engine.ContentQueries);
    }

    /// <summary>运行引擎并回传阶段二「取内容」SQL 次数（用于断言未全库读歌词）。</summary>
    private static (IndexerResult Result, int ContentQueries) RunEngineDetailed(
        string db, string index, bool full, bool lrc)
    {
        // 主流程夹具沿用 v4 口径：歌词首字母开启，覆盖首字母分列断言；
        // 默认关闭（T3.4 V4）由 InitialsLyricsOption 单独覆盖。
        var options = new IndexerOptions { IndexLrc = lrc, Pinyin = true, InitialsLyrics = true };
        return RunEngineDetailed(db, index, full, options);
    }

    internal static void CreateLibrary(string path, long? generation)
    {
        if (File.Exists(path)) File.Delete(path);
        using var conn = Open(path);
        // 与 scanner 的 tracks 表同形（v3：含 .lrc 侧车列——**扫描器写入、索引器只读**）。
        Exec(conn, """
            CREATE TABLE tracks (
                id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE, title TEXT NOT NULL,
                track INTEGER, artists TEXT NOT NULL DEFAULT '[]', album TEXT,
                duration INTEGER NOT NULL, cover TEXT, codec TEXT, sample_rate INTEGER,
                bit_rate INTEGER, channels INTEGER, bits_per_sample INTEGER,
                file_size INTEGER NOT NULL, file_mtime INTEGER, file_ctime INTEGER,
                scanned_at INTEGER NOT NULL, lyrics TEXT, loudness_lufs REAL, loudness_peak REAL,
                lyrics_lrc TEXT, lyrics_lrc_trans TEXT, lyrics_lrc_romaji TEXT,
                lrc_mtime INTEGER, lrc_size INTEGER, lrc_checked INTEGER
            )
            """);
        if (generation.HasValue)
        {
            Exec(conn, "CREATE TABLE _scanner_meta (generation INTEGER)");
            Exec(conn, $"INSERT INTO _scanner_meta (generation) VALUES ({generation.Value})");
        }
    }

    /// <summary>
    /// 写入 scanner 负责的 <c>.lrc</c> 侧车列（**索引器只读 DB**，不再读文件系统）：
    /// 存**原始文本**（保时间轴，播放侧也用同一份），mtime/size 供脏集判定。
    /// </summary>
    private static void SetSidecar(
        SqliteConnection conn, string id, string? main, string? trans = null, string? romaji = null,
        long mtime = 100)
    {
        var exists = main is not null || trans is not null || romaji is not null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE tracks SET lyrics_lrc=@m, lyrics_lrc_trans=@t, lyrics_lrc_romaji=@r, "
            + "lrc_mtime=@mt, lrc_size=@s, lrc_checked=1 WHERE id=@id";
        cmd.Parameters.AddWithValue("@m", (object?)main ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@t", (object?)trans ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@r", (object?)romaji ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mt", exists ? mtime : 0);
        cmd.Parameters.AddWithValue("@s", (main?.Length ?? 0) + (trans?.Length ?? 0) + (romaji?.Length ?? 0));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    internal static void InsertTrack(
        SqliteConnection conn, string id, string path, string title,
        string artists, string? album, string? lyrics, long mtime)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tracks (id, path, title, artists, album, duration, file_size, file_mtime,
                                file_ctime, scanned_at, lyrics)
            VALUES (@id, @path, @title, @artists, @album, 0, 0, @mtime, 0, 0, @lyrics)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@artists", artists);
        cmd.Parameters.AddWithValue("@album", (object?)album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mtime", mtime);
        cmd.Parameters.AddWithValue("@lyrics", (object?)lyrics ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    internal static SqliteConnection Open(string path)
    {
        // 走 LibraryDb.ConnectionString（Pooling=False）：断言必须读**当前**文件。
        // 若沿用连接池，库被替换后池里仍留着指向旧 inode 的句柄，断言会静默读到旧快照。
        var conn = new SqliteConnection(LibraryDb.ConnectionString(path));
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Exec(string path, string sql)
    {
        using var conn = Open(path);
        Exec(conn, sql);
    }

    /* ------------------------------------------------------------------ */
    /* EraStore 断言辅助                                                   */
    /* ------------------------------------------------------------------ */

    /// <summary>与索引器同一目录约定：<c>&lt;index 目录&gt;/search.era</c>。</summary>
    internal static string EraDir(string index)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(index)) ?? ".", IndexerEngine.EraStoreDirName);

    private static string EraFile(string index)
    {
        var dir = EraDir(index);
        var name = EraStore.CurrentPointer.Read(dir)
            ?? throw new InvalidOperationException($"EraStore CURRENT 缺失: {dir}");
        return Path.Combine(dir, name);
    }

    /// <summary>索引目录里的世代文件名（升序），用于断言「只剩 CURRENT 一个文件」。</summary>
    private static string[] EraGenerationFiles(string dir) =>
        Directory.GetFiles(dir, "search-*.eraindex")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>读取当前快照的 Manifest（CURRENT + 提交槽恢复）。</summary>
    internal static EraStore.Manifest ReadManifest(string index)
        => EraStore.StoreReader.TryReadManifest(EraFile(index))
            ?? throw new InvalidOperationException($"EraStore Manifest 不可读: {index}");

    private static bool ManifestHas(EraStore.Manifest manifest, int bit) => (manifest.OptionsBits & bit) != 0;

    /// <summary>活 doc 数（Manifest 口径）。</summary>
    private static uint DocCount(string index)
        => EraStore.IndexStats.Collect(EraDir(index)).Docs;

    /// <summary>单曲 DocStore 状态；不存在（已删除 / 死行）时 Found=false。</summary>
    internal static (bool Found, long Mtime, long LrcMtime, long LrcSize, ushort[] Lengths)
        DocState(string index, string trackId)
    {
        using var reader = new EraStore.StoreReader(EraFile(index));
        foreach (var seg in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < seg.Docs.Count; doc++)
            {
                if (!seg.IsLive(doc)) continue;
                if (!string.Equals(seg.Docs.TrackId(doc), trackId, StringComparison.Ordinal)) continue;
                var lengths = new ushort[12];
                for (var i = 0; i < 12; i++)
                    lengths[i] = seg.Docs.DocLen(doc, (EraStore.FieldId)(i + 1));
                return (true, seg.Docs.Mtime(doc), seg.Docs.LrcMtime(doc), seg.Docs.LrcSize(doc), lengths);
            }
        }
        return (false, 0, 0, 0, Array.Empty<ushort>());
    }

    private static bool TrackExists(string index, string trackId) => DocState(index, trackId).Found;

    private static ushort[] DocLengths(string index, string trackId) => DocState(index, trackId).Lengths;

    private static List<EraStore.EraCandidate> MatchHits(string index, string expression)
    {
        using var reader = new EraStore.StoreReader(EraFile(index));
        return EraStore.EraQueryPlan.Execute(reader, expression, -1);
    }

    private static int MatchCount(string index, string expression)
        => MatchHits(index, expression)
            .Select(hit => hit.TrackIdHex)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static bool MatchContainsTrack(string index, string expression, string trackId)
        => MatchHits(index, expression).Any(hit =>
            string.Equals(hit.TrackIdHex, trackId, StringComparison.Ordinal));

    private static string Analyzed(string text)
    {
        var tokens = Analyzer.AnalyzeQuery(text).Lexical.Where(t => t.Length > 0).ToList();
        return tokens.Count == 0
            ? "\"\""
            : string.Join(" AND ", tokens.Select(SearchQuery.Quote));
    }

    internal static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结果 */ }
    }
}
