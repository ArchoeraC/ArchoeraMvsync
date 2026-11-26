// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Archoera.Indexer;

/// <summary>索引器运行选项（对齐 FFI <c>options_json</c>）。</summary>
public sealed class IndexerOptions
{
    /// <summary>是否索引 <c>.lrc</c> 侧车歌词（默认 false，设计文档 §4.4）。</summary>
    public bool IndexLrc { get; set; }

    /// <summary>
    /// 是否生成**元数据**拼音列（歌名 / 歌手 / 专辑的拼音与首字母，默认 false）。
    ///
    /// 由设置页「元数据拼音检索」控制；默认关闭时中文歌名只能用字面 / bigram 命中，
    /// 拼音输入不再召回。切换需重建索引（Manifest 选项位参与版本判定）。
    /// </summary>
    public bool Pinyin { get; set; }

    /// <summary>
    /// 是否生成**歌词**拼音列（<c>pinyin_lyrics_tok</c>，默认 false）。
    ///
    /// 与 <see cref="Pinyin"/> 独立：只影响歌词列的拼音召回，不改元数据拼音。
    /// 歌词拼音是索引体积的主要派生列，故默认关闭、由设置页显式开启。
    /// </summary>
    public bool LyricsPinyin { get; set; }

    /// <summary>
    /// 是否为日语歌词生成罗马音（T2-9 可选增强，默认 false）。
    ///
    /// 仅在**没有 LRC 音译层**时生效：把已有分析结果里的歌词读音 bigram 落进
    /// <c>lyrics_rom_tok</c>，使罗马字查询可命中歌词。歌词读音体积约为原文的 2×，
    /// 故默认关闭、由用户在设置里显式开启。
    /// </summary>
    public bool RomajiLyrics { get; set; }

    /// <summary>
    /// 是否为歌词生成首字母列（T3.4 V4，**默认 false**）。
    ///
    /// 关闭时 <c>initials_lyrics_tok</c> 留空，元数据首字母（<c>initials_meta_tok</c>）
    /// 不受影响；与歌词拼音列相互独立（不需要同时开 <see cref="LyricsPinyin"/>）。
    /// 开启后可用「wanzg」这类歌词首字母串检索，代价是歌词派生列体积增加。
    /// 切换会改变 Manifest 选项位；现有索引可保留，重建后生效。
    /// </summary>
    public bool InitialsLyrics { get; set; }

    /// <summary>内部批量事务条数（0 = 使用存储默认）。</summary>
    public int Batch { get; set; }

    /// <summary>
    /// 测试用：覆盖全量构建的单段估算内存上限（0 = 生产默认 4 MB）。
    /// 只影响段切分，不改变格式 / 查询语义；用于在小型夹具上触发「多段 → 自动压缩」。
    /// </summary>
    internal long FullBuildMaxSegmentBytesOverride { get; set; }
}

/// <summary>索引进度（stdout JSON / FFI 回调共用）。</summary>
public sealed class IndexerProgress
{
    public string Type { get; set; } = "progress";

    /// <summary>阶段：<c>collect</c> | <c>index</c> | <c>delete</c> | <c>commit</c>。</summary>
    public string Phase { get; set; } = "index";

    public int Total { get; set; }
    public int Processed { get; set; }
    public int Indexed { get; set; }
    public int Deleted { get; set; }
    public string Current { get; set; } = "";

    /// <summary>本次运行截至当前被 V6 护栏截断的（曲目, 歌词层）数。</summary>
    public int LyricsTruncatedLayers { get; set; }

    /// <summary>本次运行截至当前受影响曲目数（同一曲多层只计一次）。</summary>
    public int LyricsTruncatedTracks { get; set; }
}

/// <summary>索引结果摘要（stdout JSON / FFI 回参共用）。</summary>
public sealed class IndexerResult
{
    public string Type { get; set; } = "done";
    public int Total { get; set; }
    public int Indexed { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public bool Full { get; set; }
    public long Generation { get; set; }
    public long DurationMs { get; set; }

    /// <summary>本次是否写过 EraStore（全量或增量）。</summary>
    public bool EraWritten { get; set; }

    /// <summary>EraStore 是否按 T3.10 增量路径更新（false = 全量构建 / 未写）。</summary>
    public bool EraIncremental { get; set; }

    /// <summary>EraStore 是否在本次执行了段合并。</summary>
    public bool EraMerged { get; set; }

    /// <summary>提交后的 EraStore 物理段数。</summary>
    public int EraSegments { get; set; }

    /// <summary>
    /// 全量构建后的有界自动压缩是否执行过（增量构建恒为 false）。
    /// 见 <see cref="EraStore.BuildCompactor"/>。
    /// </summary>
    public bool EraCompacted { get; set; }

    /// <summary>
    /// 自动压缩前的物理段数（未压缩时等于 <see cref="EraSegments"/>）；
    /// 与 <see cref="EraSegments"/> 一起给出 before → after。
    /// </summary>
    public int EraSegmentsBeforeCompact { get; set; }

    /// <summary>自动压缩耗时（毫秒）；未压缩为 0。<c>durationMs - eraCompactMs</c> 即构建本体耗时。</summary>
    public long EraCompactMs { get; set; }

    /// <summary>
    /// 全量构建后是否执行过空间整理（<see cref="EraStore.Defragmenter"/>，A2）：
    /// 只在自动压缩真的留下 >20% 活数据的垃圾时发生；增量构建恒为 false。
    /// </summary>
    public bool EraDefragged { get; set; }

    /// <summary>空间整理耗时（毫秒）；未整理为 0。整理后 <c>eraFileBytesAfter</c> 即 CURRENT 文件大小。</summary>
    public long EraDefragMs { get; set; }

    /// <summary>整理后的 EraStore CURRENT 文件字节数；未整理为 0（Dart 目前不消费，仅诊断）。</summary>
    public long EraFileBytesAfter { get; set; }

    /// <summary>提交后的 EraStore 活文档数。</summary>
    public int EraLiveDocs { get; set; }

    /// <summary>本次增量标记为死亡的旧文档数（改写 + 删除）。</summary>
    public int EraDeadDocs { get; set; }

    /// <summary>
    /// 被 V6 护栏截断的（曲目, 歌词层）数：<see cref="LyricsPreprocess.MaxUniqueLines"/> /
    /// <see cref="LyricsPreprocess.MaxUtf8Bytes"/> 任一触发即计一次（原文 / 译文 / 音译独立）。
    /// 0 表示全部歌词层都在护栏内（真实曲库的常态）。
    /// </summary>
    public int LyricsTruncatedLayers { get; set; }

    /// <summary>被截断影响的曲目数（同一曲多层只计一次）；Dart 目前不消费，仅诊断。</summary>
    public int LyricsTruncatedTracks { get; set; }

    /// <summary>
    /// 因超过 EraFormat.MaxTermBytes（255B）被跳过的词条数（超长无分隔拉丁词 / 首字母串）。
    /// 只跳过该词条，不影响其余字段与整库构建；Dart 目前不消费，仅诊断。
    /// </summary>
    public int OversizeTokens { get; set; }
}

/// <summary>
/// 手写 JSON 序列化（AOT 安全、无源生成依赖），风格与 scanner 的 <c>ScannerJson</c> 一致。
/// </summary>
internal static class IndexerJson
{
    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        body(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Analysis(AnalysisResult result) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("tokens", result.Tokens);
        w.WriteString("pinyin", result.Pinyin);
        w.WriteString("initials", result.PinyinInitials);
        w.WriteEndObject();
    });

    /// <summary>
    /// 查询侧分析结果（<see cref="Analyzer.AnalyzeQuery"/>）序列化：与索引侧同形
    /// （空格分隔的 <c>tokens</c> / <c>pinyin</c> / <c>initials</c>），供 Dart 的
    /// <c>era_analyze</c> 取得含连写全拼切分的词元；另附 <c>defs</c> = 空格连接的
    /// 跨语言候选（<see cref="QueryTokens.CrossLingual"/>，T1，Unihan 释义扩展）。
    /// </summary>
    public static string Analysis(QueryTokens query) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("tokens", string.Join(' ', query.Lexical));
        w.WriteString("pinyin", string.Join(' ', query.Pinyin));
        w.WriteString("initials", query.PinyinInitials ?? string.Empty);
        w.WriteString("defs", string.Join(' ', query.CrossLingual));
        w.WriteEndObject();
    });

    public static string Version(int analyzerVersion) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("analyzerVersion", analyzerVersion);
        w.WriteEndObject();
    });

    public static string Progress(IndexerProgress p) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", p.Type);
        w.WriteString("phase", p.Phase);
        w.WriteNumber("total", p.Total);
        w.WriteNumber("processed", p.Processed);
        w.WriteNumber("indexed", p.Indexed);
        w.WriteNumber("deleted", p.Deleted);
        w.WriteString("current", p.Current);
        w.WriteNumber("lyricsTruncatedLayers", p.LyricsTruncatedLayers);
        w.WriteNumber("lyricsTruncatedTracks", p.LyricsTruncatedTracks);
        w.WriteEndObject();
    });

    public static string Result(IndexerResult r) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", r.Type);
        w.WriteNumber("total", r.Total);
        w.WriteNumber("indexed", r.Indexed);
        w.WriteNumber("deleted", r.Deleted);
        w.WriteNumber("skipped", r.Skipped);
        w.WriteBoolean("full", r.Full);
        w.WriteNumber("generation", r.Generation);
        w.WriteNumber("durationMs", r.DurationMs);
        w.WriteBoolean("eraWritten", r.EraWritten);
        w.WriteBoolean("eraIncremental", r.EraIncremental);
        w.WriteBoolean("eraMerged", r.EraMerged);
        w.WriteNumber("eraSegments", r.EraSegments);
        w.WriteBoolean("eraCompacted", r.EraCompacted);
        w.WriteNumber("eraSegmentsBeforeCompact", r.EraSegmentsBeforeCompact);
        w.WriteNumber("eraCompactMs", r.EraCompactMs);
        w.WriteBoolean("eraDefragged", r.EraDefragged);
        w.WriteNumber("eraDefragMs", r.EraDefragMs);
        w.WriteNumber("eraFileBytesAfter", r.EraFileBytesAfter);
        w.WriteNumber("eraLiveDocs", r.EraLiveDocs);
        w.WriteNumber("eraDeadDocs", r.EraDeadDocs);
        w.WriteNumber("lyricsTruncatedLayers", r.LyricsTruncatedLayers);
        w.WriteNumber("lyricsTruncatedTracks", r.LyricsTruncatedTracks);
        w.WriteNumber("oversizeTokens", r.OversizeTokens);
        w.WriteEndObject();
    });

    /// <summary>
    /// 查询层（<see cref="SearchSession"/>）结果序列化：<c>items</c> 为按趟次合并去重后的
    /// top-k（含 <c>trackId</c> / bm25 <c>score</c>），<c>passes</c> 记录本次实际执行的兜底趟。
    /// </summary>
    public static string QueryResult(SearchResult result) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "result");
        w.WriteNumber("count", result.Items.Count);
        w.WriteString("scheme", result.SchemeId);
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var hit in result.Items)
        {
            w.WriteStartObject();
            w.WriteString("trackId", hit.TrackId);
            w.WriteNumber("score", hit.Score);
            // 命中原由归因（精度审计 §6.3）：Dart 侧据此区分「字面命中」与「兜底命中」。
            w.WriteString("pass", hit.Pass);
            if (result.Explanations.TryGetValue(hit.TrackId, out var explanation))
            {
                w.WritePropertyName("explain");
                SearchApiJson.WriteExplain(w, explanation);
            }
            if (result.Highlights.TryGetValue(hit.TrackId, out var highlight))
            {
                w.WritePropertyName("highlight");
                SearchApiJson.WriteHighlight(w, highlight);
                w.WriteBoolean("highlightTruncated", highlight.Truncated);
            }
            if (result.Snippets.TryGetValue(hit.TrackId, out var snippet))
            {
                w.WritePropertyName("snippet");
                SearchApiJson.WriteSnippet(w, snippet);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("passes");
        w.WriteStartObject();
        w.WriteNumber("pinyinLoose", result.Passes.PinyinLoose);
        w.WriteNumber("dropTokens", result.Passes.DropTokens);
        w.WriteNumber("reading", result.Passes.Reading);
        w.WriteNumber("crossLingual", result.Passes.CrossLingual);
        w.WriteNumber("fuzzy", result.Passes.Fuzzy);
        w.WriteNumber("cjkFuzzy", result.Passes.CjkFuzzy);
        w.WriteEndObject();
        if (result.Trace is not null)
        {
            w.WritePropertyName("trace");
            SearchApiJson.WriteTrace(w, result.Trace);
        }
        w.WriteNumber("total", result.Total);
        w.WriteBoolean("totalExact", result.TotalExact);
        w.WriteBoolean("restart", result.Restart);
        if (result.Next is null)
        {
            w.WriteNull("next");
        }
        else
        {
            w.WriteString("next", result.Next);
        }
        w.WriteEndObject();
    });

    public static string SearchResults(
        IReadOnlyList<(long DocId, double Score)> rows, IReadOnlyDictionary<long, string> trackIds) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "result");
        w.WriteNumber("count", rows.Count);
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var (docId, score) in rows)
        {
            w.WriteStartObject();
            w.WriteNumber("docId", docId);
            if (trackIds.TryGetValue(docId, out var trackId)) w.WriteString("trackId", trackId);
            w.WriteNumber("score", score);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });
}
