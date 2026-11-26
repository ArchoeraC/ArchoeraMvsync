// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Lyrics;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T4.2 LRC 分层置信度（§5.6）回归：
/// <list type="bullet">
///   <item>§5.6.2 0–8 分打分矩阵（时间戳比例 / 单调 / 行数 / 显式侧车 / U+FFFD / 乱序）；</item>
///   <item>§5.6.1 同层只保留一份（内嵌 vs 侧车按置信度取高，不拼接）；</item>
///   <item>§5.6.5 译文 / 音译配对（对齐 / 行数一致 / 不对齐）；</item>
///   <item>§5.6.3 查询过滤：低置信只在 <c>scope=lyrics</c>、中置信不参与跨语言；</item>
///   <item>§5.6.6 存储（layer_conf v4）在增量 / 合并 / 去碎片后保持，<c>rev</c> 随置信度变化；</item>
///   <item>explain.layer / explain.confidence / trace.confidence / 健康计数。</item>
/// </list>
/// 全部用临时目录的合成夹具，不访问网络。
/// </summary>
internal static class LayerConfidenceTests
{
    public static void Run()
    {
        ScoringMatrix();
        Packing();
        StorageLayout();
        QueryFiltering();
        SameLayerOnlyOnce();
        StorageSurvival();
    }

    /* ------------------------------------------------------------------ */
    /* 1. 打分矩阵（§5.6.2）                                               */
    /* ------------------------------------------------------------------ */

    private static void ScoringMatrix()
    {
        // 6 行全时间戳 + 显式侧车：2（比例）+1（单调）+1（行数）+1（显式）= 5 → 高。
        var full = TimedLines(6);
        var fullScore = LrcReader.ScoreText(full, explicitSidecar: true);
        Program.Check("T4.2 打分: 全时间戳侧车 → 高",
            fullScore.Confidence == LyricConfidence.High && fullScore.Score == 5,
            $"{fullScore.Score}/{fullScore.Confidence}");

        // 10 行里 6 行有时间戳（60%）：+1 比例 +1 行数 +1 显式 = 3 → 中。
        var partial = string.Join('\n',
            Enumerable.Range(1, 6).Select(i => $"[00:{i:00}.00]line{i}")
                .Concat(Enumerable.Range(7, 4).Select(i => $"plain{i}")));
        var partialScore = LrcReader.ScoreText(partial, explicitSidecar: true);
        Program.Check("T4.2 打分: 60% 行带时间戳 → 中",
            partialScore.Confidence == LyricConfidence.Medium,
            $"{partialScore.Score}/{partialScore.Confidence}");

        var noTags = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"plain{i}"));
        var noTagSidecar = LrcReader.ScoreText(noTags, explicitSidecar: true);
        Program.Check("T4.2 打分: 无时间戳的显式侧车 → 低",
            noTagSidecar.Confidence == LyricConfidence.Low,
            $"{noTagSidecar.Score}/{noTagSidecar.Confidence}");
        // 内嵌原文（tags 自带纯文本歌词）是用户主来源：+3 基线 → 中，不能整库判低。
        var noTagEmbedded = LrcReader.ScoreText(noTags, explicitSidecar: false);
        Program.Check("T4.2 打分: 无时间轴的内嵌原文 → 中（+3 基线）",
            noTagEmbedded.Confidence == LyricConfidence.Medium,
            $"{noTagEmbedded.Score}/{noTagEmbedded.Confidence}");

        // U+FFFD >1%：扣分之外直接降级（§5.6.4），仍必须是低。
        var corrupt = string.Join('\n',
            Enumerable.Range(1, 6).Select(i => $"[00:{i:00}.00]abc\uFFFDdef"));
        var corruptScore = LrcReader.ScoreText(corrupt, explicitSidecar: true);
        Program.Check("T4.2 打分: U+FFFD >1% → 低",
            corruptScore.Confidence == LyricConfidence.Low,
            $"{corruptScore.Score}/{corruptScore.Confidence}");

        // 单调 vs 乱序：乱序既拿不到 +1 还会吃 −1，必须低于单调。
        var shuffled = string.Join('\n',
            new[] { 6, 1, 5, 2, 4, 3 }.Select(i => $"[00:{i:00}.00]line{i}"));
        var shuffledScore = LrcReader.ScoreText(shuffled, explicitSidecar: true);
        Program.Check("T4.2 打分: 单调 > 乱序",
            shuffledScore.Score < fullScore.Score
            && shuffledScore.Confidence != LyricConfidence.High,
            $"{shuffledScore.Score}/{shuffledScore.Confidence} vs {fullScore.Score}");

        // ≥5 行门槛：4 行 → 中；5 行 → 高。
        var fourLines = LrcReader.ScoreText(TimedLines(4), explicitSidecar: true);
        var fiveLines = LrcReader.ScoreText(TimedLines(5), explicitSidecar: true);
        Program.Check("T4.2 打分: 4 行中 / 5 行高",
            fourLines.Confidence == LyricConfidence.Medium
            && fiveLines.Confidence == LyricConfidence.High,
            $"{fourLines.Score}/{fiveLines.Score}");

        // 单行 >500 字符（疑似正文）−1：5 行全时间戳本应高，含超长行降为中。
        var overlong = string.Join('\n', new[]
        {
            "[00:01.00]" + new string('x', 501),
            "[00:02.00]b", "[00:03.00]c", "[00:04.00]d", "[00:05.00]e",
        });
        var overlongScore = LrcReader.ScoreText(overlong, explicitSidecar: true);
        Program.Check("T4.2 打分: 单行 >500 字符 −1（高→中）",
            overlongScore.Confidence == LyricConfidence.Medium,
            $"{overlongScore.Score}/{overlongScore.Confidence}");

        // 译文对齐：同时间戳 → 高；错位 → 低（§5.6.5 配对覆盖阈值）。
        var original = TimedLines(6);
        var aligned = LrcReader.ScoreLayers(
            embeddedRaw: null, mainRaw: original, transRaw: TimedLines(6, "trans"), romajiRaw: null);
        Program.Check("T4.2 配对: 时间戳对齐的译文 → 高",
            aligned.Translation.Confidence == LyricConfidence.High,
            $"{aligned.Translation.Score}/{aligned.Translation.Confidence}");

        var misaligned = LrcReader.ScoreLayers(
            embeddedRaw: null, mainRaw: original,
            transRaw: TimedLines(6, "trans", hour: 3), romajiRaw: null);
        Program.Check("T4.2 配对: 时间戳错位的译文 → 低",
            misaligned.Translation.Confidence == LyricConfidence.Low,
            $"{misaligned.Translation.Score}/{misaligned.Translation.Confidence}");

        // 行数一致但双方都无时间轴 → 中（§5.6.5）。
        var plainLines = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"plain{i}"));
        var plainTrans = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"t{i}"));
        var lineCountOnly = LrcReader.ScoreLayers(
            embeddedRaw: null, mainRaw: plainLines, transRaw: plainTrans, romajiRaw: null);
        Program.Check("T4.2 配对: 行数一致但无时间轴 → 中",
            lineCountOnly.Translation.Confidence == LyricConfidence.Medium,
            $"{lineCountOnly.Translation.Score}/{lineCountOnly.Translation.Confidence}");

        var shortTrans = string.Join('\n', Enumerable.Range(1, 5).Select(i => $"t{i}"));
        var mismatched = LrcReader.ScoreLayers(
            embeddedRaw: null, mainRaw: plainLines, transRaw: shortTrans, romajiRaw: null);
        Program.Check("T4.2 配对: 行数不一致且无时间轴 → 低",
            mismatched.Translation.Confidence == LyricConfidence.Low,
            $"{mismatched.Translation.Score}/{mismatched.Translation.Confidence}");

        // 音译：显式文件 + 对齐 → 高；缺显式文件时不产生音译层。
        var romaji = LrcReader.ScoreLayers(
            embeddedRaw: null, mainRaw: original, transRaw: null,
            romajiRaw: TimedLines(6, "romaji"));
        Program.Check("T4.2 配对: 显式音译对齐 → 高",
            romaji.Romaji.Confidence == LyricConfidence.High,
            $"{romaji.Romaji.Score}/{romaji.Romaji.Confidence}");
        var noRomaji = LrcReader.ScoreLayers(
            embeddedRaw: original, mainRaw: null, transRaw: null, romajiRaw: null);
        Program.Check("T4.2 配对: 无显式音译 → 音译层为空",
            !noRomaji.Romaji.HasText && noRomaji.Romaji.Confidence == LyricConfidence.None,
            noRomaji.Romaji.Text);
    }

    /// <summary>纯假名低置信夹具：6 行、无时间戳；不产生拼音 / 首字母 / 读音词元。</summary>
    private static string KanaBlock() => string.Join('\n', new[]
    {
        "あいあい", "かくれたゆめ", "よるのかぜ", "ほしのひかり", "あさのそら", "とおいみち",
    });

    private static string TimedLines(int count, string prefix = "line", int hour = 0)
        => string.Join('\n', Enumerable.Range(1, count)
            .Select(i => $"[{(hour > 0 ? hour : 0):00}:{i:00}.00]{prefix}{i}"));

    /* ------------------------------------------------------------------ */
    /* 2. 2 bit 打包 / 位布局                                              */
    /* ------------------------------------------------------------------ */

    private static void Packing()
    {
        var packed = EraStore.DocStoreReader.PackLayerConf(
            LyricConfidence.High, LyricConfidence.Medium, LyricConfidence.Low);
        Program.Check("T4.2 存储: layer_conf 2bit×3 打包",
            packed == (3 | (2 << 2) | (1 << 4)),
            packed.ToString());

        var sources = EraStore.DocStoreReader.PackLayerSources(
            LyricSource.Lrc, LyricSource.ExplicitPair, LyricSource.Embedded);
        Program.Check("T4.2 存储: 来源位 flags bit3–7",
            EraStore.DocStoreReader.LayerSourceAt(sources, LrcReader.LayerOriginal) == LyricSource.Lrc
            && EraStore.DocStoreReader.LayerSourceAt(sources, LrcReader.LayerTranslation)
                == LyricSource.ExplicitPair,
            sources.ToString());

        Program.Check("T4.2 存储: 显式音译来源 1 bit",
            EraStore.DocStoreReader.LayerSourceAt(
                EraStore.DocStoreReader.PackLayerSources(
                    LyricSource.Embedded, LyricSource.Embedded, LyricSource.ExplicitPair),
                LrcReader.LayerRomaji) == LyricSource.ExplicitPair);
    }

    private static void StorageLayout()
    {
        Program.Check("T4.2 存储: DocStore ≥v4 / metaVersion ≥3",
            EraStore.DocStoreWriter.Version >= 4
            && EraStore.Manifest.CurrentMetaVersion >= 3,
            $"{EraStore.DocStoreWriter.Version}/{EraStore.Manifest.CurrentMetaVersion}");
        Program.Check("T4.2 存储: tokenizer 版本已递增（5）",
            new EraStore.ManifestAnchors().TokenizerVersion == 5,
            new EraStore.ManifestAnchors().TokenizerVersion.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 3. 查询过滤（§5.6.3）                                               */
    /* ------------------------------------------------------------------ */

    private static void QueryFiltering()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-layerconf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var dir = Path.Combine(root, "music");
            Directory.CreateDirectory(dir);

            // 跨语言候选：查两字词得到一个英文释义，再埋进高 / 中歌词里。
            var crossQuery = "爱情";
            var crossToken = Analyzer.AnalyzeQuery(crossQuery).CrossLingual
                .FirstOrDefault(word => word.Length > 2 && word.All(char.IsAsciiLetter));
            Program.Check("T4.2 夹具: 跨语言候选存在", crossToken is not null, crossQuery);

            EndToEnd.CreateLibrary(db, 1);
            using (var conn = EndToEnd.Open(db))
            {
                EndToEnd.InsertTrack(conn, "hi", Path.Combine(dir, "hi.mp3"),
                    "高置信曲", """[{"name":"合成"}]""", """{"name":"测试"}""", null, 1000);
                EndToEnd.InsertTrack(conn, "md", Path.Combine(dir, "md.mp3"),
                    "中置信曲", """[{"name":"合成"}]""", """{"name":"测试"}""", null, 2000);
                // 低置信夹具用假名：拼音 / 首字母 / 读音列都不产出，只能命中字面歌词列——
                // 这样「低置信不参与 scope=all」不会被派生列绕过（CJK / 拉丁会经拼音列召回）。
                // 低置信夹具必须只剩强负信号：内嵌原文 +3 基线后，普通无时间轴歌词已是中置信；
                // 这里注入 U+FFFD（>1% → 硬降为低），仍保留第 1 行供查询命中。
                EndToEnd.InsertTrack(conn, "lo", Path.Combine(dir, "lo.mp3"),
                    "低置信曲", """[{"name":"合成"}]""", """{"name":"测试"}""",
                    KanaBlock() + "\n\uFFFD\uFFFD\uFFFD\uFFFD", 3000);
                // 对照：普通无时间轴内嵌歌词（中置信）必须参与 scope=all。
                EndToEnd.InsertTrack(conn, "plain", Path.Combine(dir, "plain.mp3"),
                    "普通歌词", """[{"name":"合成"}]""", """{"name":"测试"}""",
                    "plainunique\n第二行", 3500);

                var high = AsciiBlock("highestunique", crossToken);
                var medium = AsciiBlock("mediumunique", crossToken, timedLines: 6, totalLines: 10);
                SetSidecar(conn, "hi", high);
                SetSidecar(conn, "md", medium);
            }

            EndToEnd.RunEngine(db, index, full: true, lrc: true);

            // 层置信度落盘：hi=高 / md=中 / lo=低。
            Program.Check("T4.2 查询: 层置信度落盘 hi=高",
                DocLayer(index, "hi").Confidence == LyricConfidence.High,
                DocLayer(index, "hi").Confidence.ToString());
            Program.Check("T4.2 查询: 层置信度落盘 md=中",
                DocLayer(index, "md").Confidence == LyricConfidence.Medium,
                DocLayer(index, "md").Confidence.ToString());
            Program.Check("T4.2 查询: 层置信度落盘 lo=低",
                DocLayer(index, "lo").Confidence == LyricConfidence.Low,
                DocLayer(index, "lo").Confidence.ToString());

            // 低置信：scope=all 不参与，scope=lyrics 仍可搜到。
            var lowAll = Query(index, "あいあい");
            Program.Check("T4.2 查询: 低置信不参与 scope=all",
                !lowAll.Items.Any(hit => hit.TrackId == "lo"), Hits(lowAll));
            var lowLyrics = Query(index, "あいあい", lyrics: true);
            Program.Check("T4.2 查询: 低置信仍参与 scope=lyrics",
                lowLyrics.Items.Any(hit => hit.TrackId == "lo"), Hits(lowLyrics));

            Program.Check("T4.2 查询: 普通内嵌歌词（中置信）参与 scope=all",
                Query(index, "plainunique").Items.Any(hit => hit.TrackId == "plain"),
                string.Join(",", Query(index, "plainunique").Items.Select(h => h.TrackId)));

            // 中 / 高：scope=all 都参与。
            var mediumAll = Query(index, "mediumunique");
            Program.Check("T4.2 查询: 中置信参与 scope=all",
                mediumAll.Items.Any(hit => hit.TrackId == "md"), Hits(mediumAll));
            var highAll = Query(index, "highestunique");
            Program.Check("T4.2 查询: 高置信参与 scope=all",
                highAll.Items.Any(hit => hit.TrackId == "hi"), Hits(highAll));

            // 跨语言：高置信参与、中置信不参与（与 scope 无关）。
            if (crossToken is not null)
            {
                var crossAll = Query(index, crossQuery);
                Program.Check("T4.2 查询: 跨语言命中高置信",
                    crossAll.Items.Any(hit => hit.TrackId == "hi"), Hits(crossAll));
                Program.Check("T4.2 查询: 中置信不参与跨语言（scope=all）",
                    !crossAll.Items.Any(hit => hit.TrackId == "md"), Hits(crossAll));
                var crossLyrics = Query(index, crossQuery, lyrics: true);
                Program.Check("T4.2 查询: 中置信不参与跨语言（scope=lyrics）",
                    !crossLyrics.Items.Any(hit => hit.TrackId == "md"), Hits(crossLyrics));
            }

            // explain：歌词命中的层 / 置信度。
            var explained = Query(index, "highestunique", explain: true);
            if (explained.Explanations.TryGetValue("hi", out var explanation))
            {
                Program.Check("T4.2 explain: 高置信侧车层 = lrc / high",
                    explanation.Layer == "lrc" && explanation.Confidence == "high",
                    $"{explanation.Layer}/{explanation.Confidence}");
            }
            else
            {
                Program.Check("T4.2 explain: 高置信命中存在解释", false, Hits(explained));
            }

            var explainedLow = Query(index, "あいあい", lyrics: true, explain: true);
            if (explainedLow.Explanations.TryGetValue("lo", out var lowExplanation))
            {
                Program.Check("T4.2 explain: 低置信内嵌层 = embedded / low",
                    lowExplanation.Layer == "embedded" && lowExplanation.Confidence == "low",
                    $"{lowExplanation.Layer}/{lowExplanation.Confidence}");
            }
            else
            {
                Program.Check("T4.2 explain: 低置信命中存在解释", false, Hits(explainedLow));
            }

            // trace：scope=all 记录被过滤的低置信；跨语言另记 medium。
            var traceAll = Query(index, "highestunique", explain: true).Trace;
            Program.Check("T4.2 trace: scope=all 记录 low_confidence",
                traceAll is not null
                && traceAll.Confidence.Any(entry =>
                    entry.Policy == "all" && entry.Layers.Contains("low")),
                DescribeTrace(traceAll));
            if (crossToken is not null)
            {
                var traceCross = Query(index, crossQuery, explain: true).Trace;
                Program.Check("T4.2 trace: 跨语言记录 low+medium",
                    traceCross is not null
                    && traceCross.Confidence.Any(entry =>
                        entry.Policy == "crossLingual"
                        && entry.Layers.Contains("low")
                        && entry.Layers.Contains("medium")),
                    DescribeTrace(traceCross));
            }

            // 健康计数（§5.6.6，additive）。
            var counts = EraStore.LayerConfidenceStats.Collect(
                Path.Combine(EndToEnd.EraDir(index), EraStore.CurrentPointer.Read(EndToEnd.EraDir(index))!));
            Program.Check("T4.2 健康: high/medium/low/none 计数",
                counts.High >= 1 && counts.Medium >= 1 && counts.Low >= 1 && counts.None >= 0
                && counts.Total == 4,
                $"{counts.High}/{counts.Medium}/{counts.Low}/{counts.None}");

            var health = SearchApiJson.HealthReady(
                SearchSession.MetaFromManifest(EndToEnd.ReadManifest(index)), counts);
            Program.Check("T4.2 健康: JSON 含 lyricsConfidence",
                health.Contains("\"lyricsConfidence\"", StringComparison.Ordinal)
                && health.Contains("\"high\":", StringComparison.Ordinal),
                health);
        }
        finally
        {
            SearchSession.CloseCached();
            EndToEnd.TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 4. 同层只保留一份（§5.6.1）                                         */
    /* ------------------------------------------------------------------ */

    private static void SameLayerOnlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-layerpick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var dir = Path.Combine(root, "music");
            Directory.CreateDirectory(dir);

            EndToEnd.CreateLibrary(db, 1);
            using (var conn = EndToEnd.Open(db))
            {
                // A：内嵌低（纯文本）+ 侧车高 → 只索引侧车原文。
                EndToEnd.InsertTrack(conn, "pickHi", Path.Combine(dir, "a.mp3"),
                    "侧车优先", """[{"name":"合成"}]""", """{"name":"测试"}""",
                    "embeddedonlytext\nembeddedsecondline", 1000);
                SetSidecar(conn, "pickHi", AsciiBlock("sidecarhigher"));

                // B：内嵌较高（时间戳结构）+ 侧车低（纯文本）→ 只索引内嵌原文。
                EndToEnd.InsertTrack(conn, "pickEmbedded", Path.Combine(dir, "b.mp3"),
                    "内嵌优先", """[{"name":"合成"}]""", """{"name":"测试"}""",
                    AsciiBlock("embeddedhigher"), 2000);
                SetSidecar(conn, "pickEmbedded", "sidecarlower\nsidecarlowers");
            }

            EndToEnd.RunEngine(db, index, full: true, lrc: true);

            Program.Check("T4.2 同层: 低内嵌 + 高侧车 → 侧车原文入列",
                MatchContains(index, "{lyrics_tok} : \"sidecar\"*", "pickHi"),
                DocLayer(index, "pickHi").Confidence.ToString());
            Program.Check("T4.2 同层: 低内嵌 + 高侧车 → 内嵌原文不入列（不拼接）",
                !MatchContains(index, "{lyrics_tok} : \"embedded\"*", "pickHi"));

            Program.Check("T4.2 同层: 较高内嵌 + 低侧车 → 内嵌原文入列",
                MatchContains(index, "{lyrics_tok} : \"embedded\"*", "pickEmbedded"),
                DocLayer(index, "pickEmbedded").Confidence.ToString());
            Program.Check("T4.2 同层: 较高内嵌 + 低侧车 → 侧车原文不入列",
                !MatchContains(index, "{lyrics_tok} : \"sidecar\"*", "pickEmbedded"));
        }
        finally
        {
            SearchSession.CloseCached();
            EndToEnd.TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 5. 存储：增量 / 合并 / 去碎片 + rev                                 */
    /* ------------------------------------------------------------------ */

    private static void StorageSurvival()
    {
        var root = Path.Combine(Path.GetTempPath(), "archoera-layerstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var dir = Path.Combine(root, "music");
            Directory.CreateDirectory(dir);

            EndToEnd.CreateLibrary(db, 1);
            using (var conn = EndToEnd.Open(db))
            {
                EndToEnd.InsertTrack(conn, "keep", Path.Combine(dir, "k.mp3"),
                    "存储保持", """[{"name":"合成"}]""", """{"name":"测试"}""", null, 1000);
                SetSidecar(conn, "keep", AsciiBlock("firstsidecar"));
            }

            EndToEnd.RunEngine(db, index, full: true, lrc: true);
            var before = DocLayer(index, "keep");
            Program.Check("T4.2 存储: 全量后高置信",
                before.Confidence == LyricConfidence.High, before.Confidence.ToString());

            // 改成低置信（纯文本侧车）并推进 mtime → 增量重写该曲。
            using (var conn = EndToEnd.Open(db))
            {
                SetSidecar(conn, "keep", "plainlowone\nplainlowtwo\nplainlowthree", mtime: 500);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE tracks SET file_mtime=2000 WHERE id='keep'";
                cmd.ExecuteNonQuery();
            }
            EndToEnd.RunEngine(db, index, full: false, lrc: true);

            var after = DocLayer(index, "keep");
            Program.Check("T4.2 存储: 增量后置信度更新为低",
                after.Confidence == LyricConfidence.Low, after.Confidence.ToString());
            Program.Check("T4.2 存储: 置信度变化 → rev 变化",
                !before.Rev.SequenceEqual(after.Rev),
                $"{Convert.ToHexString(before.Rev)} -> {Convert.ToHexString(after.Rev)}");

            // 合并全部段：置信度位原样搬运。
            var manifest = EndToEnd.ReadManifest(index);
            var segments = manifest.Sections
                .Where(section => section.Kind == EraStore.SectionKind.DocStore)
                .Select(section => (int)section.SegmentId)
                .Distinct()
                .ToList();
            EraStore.Merger.Merge(EraFile(index), segments);
            var merged = DocLayer(index, "keep");
            Program.Check("T4.2 存储: 合并后置信度保持",
                merged.Confidence == LyricConfidence.Low, merged.Confidence.ToString());

            // 去碎片：整段体复制，置信度同样保持。
            EraStore.Defragmenter.Defrag(EndToEnd.EraDir(index));
            var defragged = DocLayer(index, "keep");
            Program.Check("T4.2 存储: 去碎片后置信度保持",
                defragged.Confidence == LyricConfidence.Low, defragged.Confidence.ToString());
        }
        finally
        {
            SearchSession.CloseCached();
            EndToEnd.TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>生成 N 行 ASCII 歌词；前 <paramref name="timedLines"/> 行带时间戳（侧车用）。</summary>
    private static string AsciiBlock(
        string unique, string? crossToken = null, int timedLines = 6, int totalLines = 6)
    {
        var lines = new List<string>();
        for (var i = 1; i <= totalLines; i++)
        {
            var text = i == 1 ? unique : $"filler{i}";
            if (i == 2 && crossToken is not null) text += " " + crossToken;
            lines.Add(i <= timedLines ? $"[00:{i:00}.00]{text}" : text);
        }
        return string.Join('\n', lines);
    }

    private static void SetSidecar(
        SqliteConnection conn, string id, string? main, long mtime = 100)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE tracks SET lyrics_lrc=@m, lrc_mtime=@mt, lrc_size=@s, lrc_checked=1 WHERE id=@id";
        cmd.Parameters.AddWithValue("@m", (object?)main ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mt", main is null ? 0 : mtime);
        cmd.Parameters.AddWithValue("@s", main?.Length ?? 0);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static string EraFile(string index)
    {
        var eraDir = EndToEnd.EraDir(index);
        var name = EraStore.CurrentPointer.Read(eraDir)
            ?? throw new InvalidOperationException($"EraStore CURRENT 缺失: {eraDir}");
        return Path.Combine(eraDir, name);
    }

    private static (bool Found, LyricConfidence Confidence, byte Flags, byte[] Rev) DocLayer(
        string index, string trackId)
    {
        using var reader = new EraStore.StoreReader(EraFile(index));
        foreach (var segment in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < segment.Docs.Count; doc++)
            {
                if (!segment.IsLive(doc)) continue;
                if (!string.Equals(segment.Docs.TrackId(doc), trackId, StringComparison.Ordinal))
                    continue;
                return (true, segment.Docs.LayerConfidenceAt(doc, LrcReader.LayerOriginal),
                    segment.Docs.Flags(doc), segment.Docs.Rev(doc).ToArray());
            }
        }
        return (false, LyricConfidence.None, 0, Array.Empty<byte>());
    }

    private static bool MatchContains(string index, string expression, string trackId)
    {
        using var reader = new EraStore.StoreReader(EraFile(index));
        return EraStore.EraQueryPlan.Execute(reader, expression, -1)
            .Any(hit => string.Equals(hit.TrackIdHex, trackId, StringComparison.Ordinal));
    }

    private static SearchResult Query(
        string index, string query, bool lyrics = false, bool explain = false)
    {
        SearchSession.CloseCached();
        return SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Lyrics = lyrics,
            Explain = explain,
            Limit = 20,
            MinExact = 1,
        });
    }


    private static string Hits(SearchResult result)
        => string.Join(',', result.Items.Select(hit => hit.TrackId));

    private static string DescribeTrace(SearchTrace? trace)
        => trace is null
            ? "(null)"
            : string.Join(';', trace.Confidence.Select(entry =>
                $"{entry.Policy}:{string.Join('/', entry.Layers)}"));
}
