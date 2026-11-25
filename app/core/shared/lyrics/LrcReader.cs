// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Archoera.Text;

namespace Archoera.Lyrics;

/// <summary>
/// 歌词层置信度（§5.6.2 阈值）：<c>≥5 高</c>、<c>3–4 中</c>、<c>≤2 低</c>；
/// <see cref="None"/> 表示该层不存在（没有文本）。数值与 DocStore <c>layer_conf</c>
/// 的 2 bit 编码一一对应（0..3），**不得重排**。
/// </summary>
public enum LyricConfidence : byte
{
    /// <summary>该层不存在（空文本）。</summary>
    None = 0,

    /// <summary>低置信：≤2 分；只在 <c>scope=lyrics</c> 参与检索。</summary>
    Low = 1,

    /// <summary>中置信：3–4 分；参与 all / lyrics，不参与跨语言。</summary>
    Medium = 2,

    /// <summary>高置信：≥5 分；参与全部检索路径。</summary>
    High = 3,
}

/// <summary>
/// 歌词层来源（用于 <c>explain.layer</c> 与 DocStore <c>flags</c> 的来源位）。
///
/// <para>数值按「可信度递增」排列，选层打平时取大者：用户编辑 &gt; 显式侧车 &gt; 内嵌。
/// 注意：同一数值在不同层上的 wire 名不同（见 <see cref="LrcReader.LayerName"/>）——
/// 译文层的 <see cref="ExplicitPair"/> 表示 <c>.trans.lrc</c>，原文层则不会出现该值。</para>
/// </summary>
public enum LyricSource : byte
{
    /// <summary>该层不存在。</summary>
    None = 0,

    /// <summary>内嵌 <c>tracks.lyrics</c>（或主 <c>.lrc</c> 文件内的同时间戳分组）。</summary>
    Embedded = 1,

    /// <summary><c>.lrc</c> 侧车原文；译文层表示「主 <c>.lrc</c> 同时间戳结构」。</summary>
    Lrc = 2,

    /// <summary>用户编辑；译文 / 音译层表示显式命名侧车（<c>.trans.lrc</c> / <c>.romaji.lrc</c>）。</summary>
    ExplicitPair = 3,
}

/// <summary>单层打分结果（§5.6.2 的 0–8 分 + 阈值映射 + 来源）。</summary>
public readonly record struct LrcLayerScore(
    string Text,
    LyricConfidence Confidence,
    int Score,
    LyricSource Source)
{
    /// <summary>空层。</summary>
    public static readonly LrcLayerScore Empty = new("", LyricConfidence.None, 0, LyricSource.None);

    /// <summary>该层是否有可索引文本。</summary>
    public bool HasText => Text.Length > 0;
}

/// <summary>三层打分 / 选层结果（原文 / 译文 / 音译）。</summary>
public readonly record struct LrcScoredLayers(
    LrcLayerScore Original,
    LrcLayerScore Translation,
    LrcLayerScore Romaji)
{
    /// <summary>三空层。</summary>
    public static readonly LrcScoredLayers Empty = new(
        LrcLayerScore.Empty, LrcLayerScore.Empty, LrcLayerScore.Empty);
}

/// <summary>
/// <c>.lrc</c> 侧车读取结果（分层：原文 / 译文 / 音译）。
/// </summary>
public sealed class LrcContent
{
    /// <summary>是否存在同名侧车（主 <c>.lrc</c> 或 <c>.trans.lrc</c> / <c>.romaji.lrc</c>）。</summary>
    public bool Exists { get; init; }

    /// <summary>
    /// **原文层**：去时间轴 / 标签后的纯文本行（以 <c>\n</c> 连接）；无原文时为空串。
    /// 时间轴分组下取每组第 1 行，非 LRC 纯文本整体归此层。
    /// </summary>
    public string Original { get; init; } = "";

    /// <summary>**译文层**（以 <c>\n</c> 连接）；无译文时为空串。</summary>
    public string Translation { get; init; } = "";

    /// <summary>**音译 / 罗马音层**（以 <c>\n</c> 连接）；无音译时为空串。</summary>
    public string Romaji { get; init; } = "";

    /// <summary>原文层别名（等价 <see cref="Original"/>，保留既有字段语义）。</summary>
    public string Text => Original;

    /// <summary>侧车 mtime（Unix 毫秒，取各侧车最大）；不存在时为 0。</summary>
    public long Mtime { get; init; }

    /// <summary>侧车字节大小（各侧车求和）；不存在时为 0。</summary>
    public long Size { get; init; }

    /// <summary>原文层有效文本行数。</summary>
    public int LineCount { get; init; }
}

/// <summary>
/// <c>.lrc</c> 侧车读取与**三层拆分**（原文 / 译文 / 音译；设计文档 §4.3 / §4.4）。
///
/// 约定（大小写不敏感）：主侧车与音频**同目录、同 basename** 的 <c>.lrc</c>；
/// 另识别显式分层侧车 <c>&lt;name&gt;.trans.lrc</c>（译文）/ <c>&lt;name&gt;.romaji.lrc</c>（音译）。
///
/// 分层启发式（保守，确定性；不覆盖时对应层为空）：
/// <list type="bullet">
///   <item>解析主 <c>.lrc</c> 为有序 <c>(时间戳, 文本)</c> 条目；**相同时间戳的连续多行**
///         视为同一条目多层：第 1 行 → 原文，其后按位置 第 2 行 → 译文 / 第 3 行 → 音译；
///         若第 2 行纯拉丁而第 3 行非拉丁则对调；仅一行时按是否纯拉丁判为音译 / 译文；</item>
///   <item>显式侧车存在时其文本行**直接进入**对应层（替换内嵌推断）；</item>
///   <item>无时间轴的纯文本（非 LRC）整体作为原文；无译文 / 音译则对应层空串。</item>
/// </list>
/// 原文层**不回退**：只取每组第 1 行 + 无时间轴行，绝不把译文 / 音译混入原文
/// （避免检索语义混淆，见报告风险）。
///
/// 编码识别顺序：BOM（UTF-8 / UTF-16 LE / BE）→ 无 BOM 时严格 UTF-8 试解码 →
/// .NET 框架内置 CodePages 的 **GB18030** 解码（覆盖 GBK / GB18030，含 4 字节序列）；
/// 解码失败回退 UTF-8 替换（不崩溃）。
///
/// **归属（P5 实测修订）**：本实现由 **scanner 与 indexer 共用**（同一源文件，避免两套解码 /
/// 分层漂移）。**扫描器**遍历目录时解析并写入 <c>library.db</c>（`lyrics_lrc` /
/// `lyrics_lrc_trans` / `lyrics_lrc_romaji` + `lrc_mtime` / `lrc_size` / `lrc_checked`），
/// 索引器与播放侧都只读 DB——索引器因此**零文件系统访问**，且侧车与曲目按 <c>tracks.id</c>
/// 强绑定（旧实现在索引器里逐曲整目录枚举，成本 <c>O(曲目数 × 目录条目数)</c>：单目录 30k 曲实测
/// 全量 +lrc 393s、零脏曲增量 161s，见 indexer `bench/REPORT.md`）。
/// 本类保留目录探测 / 读取入口供扫描器使用（索引器不再调用）。
/// 日志经 <see cref="Warn"/> 回调由宿主接管（本文件不依赖任何模块的 Log）。
/// </summary>
public static class LrcReader
{
    /// <summary>告警回调（宿主注入：scanner / indexer 各自的 Log）。空则静默。</summary>
    public static Action<string>? Warn { get; set; }

    /// <summary>侧车探测结果（不读取内容；用于脏集判定；聚合主 / 译文 / 音译三层侧车）。</summary>
    public readonly record struct LrcProbe(bool Exists, string? Path, long Mtime, long Size)
    {
        /// <summary>无侧车。</summary>
        public static readonly LrcProbe None = new(false, null, 0, 0);
    }

    /// <summary>匹配 LRC 方括号标签（时间轴 / 元数据），如 <c>[00:12.34]</c>、<c>[by:foo]</c>。</summary>
    private static readonly Regex TagRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>匹配 LRC 时间轴标签，如 <c>[00:12.34]</c> / <c>[1:02]</c>（用于同时间戳分组）。</summary>
    private static readonly Regex TimeRegex =
        new(@"\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);

    /// <summary>解析后的单条 LRC 条目：时间戳标签原文（无时间轴时为 <c>null</c>）+ 文本。</summary>
    private readonly record struct LrcEntry(string? Timestamp, string Text);

    /// <summary>分层后的一行：文本 + 所属时间戳（毫秒；无时间轴为 <c>null</c>）。</summary>
    private readonly record struct LrcLine(string Text, long? Ms);

    /// <summary>分层明细（保留每行的时间戳，供 §5.6.2 打分与 §5.6.5 对齐判定）。</summary>
    private sealed class DetailedLayers
    {
        public List<LrcLine> Original { get; } = new();
        public List<LrcLine> Translation { get; } = new();
        public List<LrcLine> Romaji { get; } = new();
    }

    /// <summary>文本拆分后的三层（空层为空串）。供内嵌歌词与 <c>.lrc</c> 共用同一启发式。</summary>
    public readonly record struct LrcLayers(string Original, string Translation, string Romaji)
    {
        /// <summary>三空层。</summary>
        public static readonly LrcLayers Empty = new("", "", "");
    }

    /// <summary>
    /// 把**原始歌词文本**（内嵌 <c>tracks.lyrics</c> 或 <c>.lrc</c> 内容）按 §4.3 启发式
    /// 拆为原文 / 译文 / 音译三层；内嵌与侧车共用同一实现（保证语义一致）。
    /// 无时间轴的纯文本整体归原文；无法判定的层为空串。
    /// </summary>
    public static LrcLayers SplitText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return LrcLayers.Empty;
        var layers = SplitDetailed(raw);
        return new LrcLayers(
            JoinTexts(layers.Original),
            JoinTexts(layers.Translation),
            JoinTexts(layers.Romaji));
    }

    /// <summary>
    /// **按 §5.6.2 给单层打 0–8 分并映射置信度**（供单测 / 诊断直接调用）。
    /// <paramref name="explicitSidecar"/> 表示该文本来自显式命名侧车（+1）；
    /// 无时间轴的纯内嵌文本按 §5.6 打分口径得低置信。
    /// </summary>
    public static LrcLayerScore ScoreText(string? raw, bool explicitSidecar = false)
        => ScoreLayer(SplitDetailed(raw).Original, explicitSidecar ? LyricSource.Lrc : LyricSource.Embedded);

    /// <summary>
    /// **三层打分 + 选层**（T4.2 / §5.6.1–§5.6.5）。
    ///
    /// <para>输入是四个**原始文本**（保时间轴）：内嵌 <c>tracks.lyrics</c>、主 <c>.lrc</c>、
    /// 显式 <c>.trans.lrc</c>、显式 <c>.romaji.lrc</c>（后两者为 <c>null</c> 表示没有对应侧车）。
    /// 每层只保留一份：内嵌与侧车原文同时存在时按置信度取高者，**不拼接**。</para>
    ///
    /// <para><b>译文 / 音译配对</b>（§5.6.5）：显式侧车与原文时间戳完全一致 → 高；只对齐
    /// ≥80% → 中；时间戳双方都缺失但行数一致 → 中；时间戳存在但对不齐 → 低（仍写入索引，
    /// 只从 <c>scope=all</c> 退出）。同一文件内**同时间戳多行**是解析器的结构配对
    /// （§5.6.4），按对齐 100% 处理。</para>
    /// </summary>
    public static LrcScoredLayers ScoreLayers(
        string? embeddedRaw, string? mainRaw, string? transRaw, string? romajiRaw)
    {
        var embedded = SplitDetailed(embeddedRaw);
        var main = SplitDetailed(mainRaw);

        // ---- 原文：内嵌 vs 主 .lrc，取置信度高者；打平取显式侧车（来源枚举更大者）----
        var embeddedOriginal = ScoreLayer(embedded.Original, LyricSource.Embedded);
        var mainOriginal = ScoreLayer(main.Original, LyricSource.Lrc);
        var (original, originalLines) = Pick(embeddedOriginal, embedded.Original,
            mainOriginal, main.Original);

        // ---- 译文：内嵌结构 / 主 .lrc 结构 / 显式 .trans 侧车，取最高 ----
        var translation = PickPair(
            originalLines,
            (embedded.Translation, LyricSource.Embedded, ExplicitFile: false),
            (main.Translation, LyricSource.Lrc, ExplicitFile: false),
            (transRaw is null ? null : SplitDetailed(transRaw).Original,
                LyricSource.ExplicitPair, ExplicitFile: true));

        // ---- 音译：只认显式文件或同一文件内的时间戳结构；不从拼音反推 ----
        var romaji = PickPair(
            originalLines,
            (embedded.Romaji, LyricSource.Embedded, ExplicitFile: false),
            (main.Romaji, LyricSource.Lrc, ExplicitFile: false),
            (romajiRaw is null ? null : SplitDetailed(romajiRaw).Original,
                LyricSource.ExplicitPair, ExplicitFile: true));

        return new LrcScoredLayers(original, translation, romaji);
    }

    /// <summary><c>explain.layer</c> 的稳定 wire 名（源码内唯一映射，勿在别处重写）。</summary>
    public static string LayerName(int layer, LyricSource source) => layer switch
    {
        LayerOriginal => source switch
        {
            LyricSource.Embedded => "embedded",
            LyricSource.Lrc => "lrc",
            LyricSource.ExplicitPair => "user",
            _ => "embedded",
        },
        LayerTranslation => source switch
        {
            LyricSource.Embedded => "embedded",
            LyricSource.Lrc => "lrc",
            LyricSource.ExplicitPair => "lrc_trans",
            _ => "embedded",
        },
        LayerRomaji => source == LyricSource.None || source == LyricSource.Embedded
            ? "embedded"
            : "lrc_romaji",
        _ => "embedded",
    };

    /// <summary><c>explain.confidence</c> 的稳定 wire 名。</summary>
    public static string ConfidenceName(LyricConfidence confidence) => confidence switch
    {
        LyricConfidence.High => "high",
        LyricConfidence.Medium => "medium",
        LyricConfidence.Low => "low",
        _ => "none",
    };

    /// <summary>层下标：原文 0 / 译文 1 / 音译 2（DocStore <c>layer_conf</c> 的 2 bit 分组）。</summary>
    public const int LayerOriginal = 0;

    /// <inheritdoc cref="LayerOriginal"/>
    public const int LayerTranslation = 1;

    /// <inheritdoc cref="LayerOriginal"/>
    public const int LayerRomaji = 2;

    /* ------------------------------------------------------------------ */
    /* 打分（§5.6.2）                                                       */
    /* ------------------------------------------------------------------ */

    /// <summary>0–8 分 → 置信度阈值（≥5 高 / 3–4 中 / ≤2 低）。</summary>
    private static LyricConfidence ConfidenceOf(int score) => score switch
    {
        >= 5 => LyricConfidence.High,
        >= 3 => LyricConfidence.Medium,
        _ => LyricConfidence.Low,
    };

    /// <summary>
    /// 单层 0–8 分（§5.6.2）：
    /// <list type="bullet">
    ///   <item>≥80% 行有合法时间戳 +2；50–80% +1；</item>
    ///   <item>时间戳严格递增（≥2 个）+1；重复 / 乱序占比 ≥20% −1；</item>
    ///   <item>≥5 行 +1；显式侧车 +1；</item>
    ///   <item>U+FFFD &gt;1% −2 并**直接降为低置信**（§5.6.4）；</item>
    ///   <item>单行 &gt;500 字符 −1。</item>
    /// </list>
    /// 译文 / 音译的对齐信号（+2）在 <see cref="PickPair"/> 里追加。
    /// </summary>
    private static LrcLayerScore ScoreLayer(List<LrcLine> lines, LyricSource source)
    {
        if (lines.Count == 0)
            return LrcLayerScore.Empty;

        var text = JoinTexts(lines);
        var timed = 0;
        var strictIncreasing = true;
        var previous = long.MinValue;
        var duplicates = 0;
        var seen = new HashSet<long>();
        var outOfOrder = 0;
        var overlong = false;
        foreach (var line in lines)
        {
            if (line.Text.Length > 500) overlong = true;
            if (line.Ms is not { } ms)
            {
                strictIncreasing = false;
                continue;
            }
            if (timed > 0 && ms <= previous) strictIncreasing = false;
            if (timed > 0 && ms < previous) outOfOrder++;
            if (!seen.Add(ms)) duplicates++;
            previous = ms;
            timed++;
        }

        var score = 0;
        // 内嵌原文是**用户自带的唯一主来源**（tags 里的纯文本歌词，通常无时间轴）。
        // §5.6.2 的原始信号表只奖励“有时间轴 / 显式侧车”，若不给基线，绝大多数
        // 无 .lrc 的曲库会整库判为低置信、被排除出 scope=all——这违背 §5.6.3
        // “低置信只用于可疑层”的目标。故给内嵌原文 +3 基线（中置信）；U+FFFD 等
        // 强负信号仍会把它们压回低置信。
        if (source == LyricSource.Embedded) score += 3;
        if (timed > 0)
        {
            var ratio = (double)timed / lines.Count;
            if (ratio >= 0.8) score += 2;
            else if (ratio >= 0.5) score += 1;
        }
        if (strictIncreasing && timed >= 2) score += 1;
        if (lines.Count >= 5) score += 1;
        if (source is LyricSource.Lrc or LyricSource.ExplicitPair) score += 1;
        if (overlong) score -= 1;
        if (timed > 0 && (double)(duplicates + outOfOrder) / timed >= 0.2) score -= 1;

        // §5.6.4：替换字符 >1% 直接降为低置信（不是可被其它信号抵消的扣分）。
        var replaced = 0;
        foreach (var ch in text)
            if (ch == '\uFFFD') replaced++;
        var corrupt = text.Length > 0 && (double)replaced / text.Length > 0.01;
        if (corrupt) score -= 2;
        if (score < 0) score = 0;
        if (score > 8) score = 8;
        var confidence = corrupt ? LyricConfidence.Low : ConfidenceOf(score);
        return new LrcLayerScore(text, confidence, score, source);
    }

    /// <summary>
    /// 译文 / 音译的配对规则（§5.6.5）。返回选中的层（空文本返回空层）。
    ///
    /// <para>对齐信号 +2（≥80%）；「对齐」= 本层时间戳在原文时间戳集合里的占比。
    /// 双方都无时间轴时按行数判定：一致 → 中置信；否则低。</para>
    /// </summary>
    private static LrcLayerScore PickPair(
        List<LrcLine> originalLines,
        params (List<LrcLine>? Lines, LyricSource Source, bool ExplicitFile)[] candidates)
    {
        var best = LrcLayerScore.Empty;
        foreach (var (lines, source, explicitFile) in candidates)
        {
            if (lines is null || lines.Count == 0) continue;
            var score = ScoreLayer(lines, explicitFile ? LyricSource.ExplicitPair : source);
            var alignment = AlignmentPercent(lines, originalLines);
            if (alignment is >= 80) score = score with { Score = Math.Min(8, score.Score + 2) };

            // §5.6.5 配对结论覆盖 0–8 分的阈值：
            //   · 双方无时间轴 + 行数一致 → 中；
            //   · 有时间戳但对齐 <80%（含完全错位）→ 低；
            //   · 对齐 ≥80% → 保留 0–8 分结论。
            var originalTimed = originalLines.Exists(line => line.Ms is not null);
            var selfTimed = lines.Exists(line => line.Ms is not null);
            LyricConfidence confidence;
            if (!selfTimed && !originalTimed)
            {
                confidence = lines.Count == originalLines.Count
                    ? LyricConfidence.Medium
                    : LyricConfidence.Low;
                if (confidence == LyricConfidence.Medium && score.Confidence < LyricConfidence.Medium)
                    score = score with { Score = Math.Max(score.Score, 3) };
            }
            else if (alignment is null or < 80)
            {
                confidence = LyricConfidence.Low;
            }
            else
            {
                confidence = score.Confidence;
            }

            var candidate = score with { Confidence = confidence };
            // 打平取来源可信度更高者（显式侧车 > 主 .lrc > 内嵌）。
            if (candidate.Score > best.Score
                || (candidate.Score == best.Score && candidate.Source > best.Source))
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>本层时间戳落在原文时间戳集合里的占比（0–100）；任一方无时间轴返回 <c>null</c>。</summary>
    private static int? AlignmentPercent(List<LrcLine> layer, List<LrcLine> original)
    {
        var originalStamps = new HashSet<long>();
        foreach (var line in original)
            if (line.Ms is { } ms) originalStamps.Add(ms);
        if (originalStamps.Count == 0) return null;

        var timed = 0;
        var matched = 0;
        foreach (var line in layer)
        {
            if (line.Ms is not { } ms) continue;
            timed++;
            if (originalStamps.Contains(ms)) matched++;
        }
        return timed == 0 ? null : (int)(100.0 * matched / timed);
    }

    /// <summary>选原文层：分数高者胜；打平取来源可信度更高者。</summary>
    private static (LrcLayerScore Score, List<LrcLine> Lines) Pick(
        LrcLayerScore a, List<LrcLine> aLines, LrcLayerScore b, List<LrcLine> bLines)
    {
        if (b.Score > a.Score || (b.Score == a.Score && b.Source > a.Source)) return (b, bLines);
        return (a, aLines);
    }

    private static string JoinTexts(List<LrcLine> lines)
    {
        if (lines.Count == 0) return "";
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(lines[i].Text);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 探测音频对应的侧车（仅 stat mtime/size，不读内容）。聚合主 <c>.lrc</c> 与
    /// <c>.trans.lrc</c> / <c>.romaji.lrc</c>：<see cref="LrcProbe.Exists"/> 为「任一存在」，
    /// <c>Mtime</c> 取最大、<c>Size</c> 求和，使任一层侧车变动都能被脏集识别。
    /// 无任何侧车返回 <see cref="LrcProbe.None"/>。
    /// </summary>
    /// <summary>
    /// 侧车三层路径 + 聚合 mtime / size（**扫描器入口**；只 stat、不读内容）。
    /// 主 / 译文 / 音译任一层存在即 <see cref="Exists"/>；mtime 取各层最大、size 求和
    /// （任一层变动都能被增量识别）。
    /// </summary>
    public readonly record struct Sidecars(string? Main, string? Trans, string? Romaji, long Mtime, long Size)
    {
        /// <summary>无任何侧车。</summary>
        public static readonly Sidecars None = new(null, null, null, 0, 0);

        /// <summary>主 / 译文 / 音译任一层存在。</summary>
        public bool Exists => Main is not null || Trans is not null || Romaji is not null;
    }

    /// <summary>定位并 stat 三层侧车（扫描器入口；目录清单按目录缓存，见 <see cref="GetSidecarIndex"/>）。</summary>
    public static Sidecars Find(string audioPath)
    {
        var (main, trans, romaji) = FindSidecars(audioPath);
        if (main is null && trans is null && romaji is null) return Sidecars.None;

        long mtime = 0, size = 0;
        foreach (var path in new[] { main, trans, romaji })
        {
            if (path is null) continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var m = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (m > mtime) mtime = m;
                size += info.Length;
            }
            catch
            {
                // 单个 stat 失败不影响其它侧车的判定
            }
        }
        return new Sidecars(main, trans, romaji, mtime, size);
    }

    public static LrcProbe Probe(string audioPath)
    {
        var sidecars = Find(audioPath);
        return sidecars.Exists
            ? new LrcProbe(true, sidecars.Main ?? sidecars.Trans ?? sidecars.Romaji, sidecars.Mtime, sidecars.Size)
            : LrcProbe.None;
    }

    /// <summary>
    /// 读取音频路径对应的侧车歌词并**分三层**；不存在任何侧车时返回
    /// <see cref="LrcContent.Exists"/>=false（其余层为空串）。
    /// </summary>
    public static LrcContent Read(string audioPath)
    {
        var sidecars = Find(audioPath);
        if (!sidecars.Exists) return new LrcContent { Exists = false };

        var layers = FromRawTexts(
            sidecars.Main is null ? null : TryDecode(sidecars.Main),
            sidecars.Trans is null ? null : TryDecode(sidecars.Trans),
            sidecars.Romaji is null ? null : TryDecode(sidecars.Romaji));

        return new LrcContent
        {
            Exists = true,
            Original = layers.Original,
            Translation = layers.Translation,
            Romaji = layers.Romaji,
            Mtime = sidecars.Mtime,
            Size = sidecars.Size,
            LineCount = layers.Original.Length == 0 ? 0 : layers.Original.Split('\n').Length,
        };
    }

    /// <summary>
    /// **原始文本（未解码）→ 三层**——扫描器写库、索引器读库、<see cref="Read"/> 三者共用，
    /// 保证「写入时的层划分」与「索引时的层划分」逐字一致（不会因两处实现漂移）。
    ///
    /// 主文本按 <see cref="SplitText"/> 的同时间戳多层启发式拆分；
    /// 显式译文 / 音译文本存在时**直接替换**对应层（各自剥标签、trim、丢空行、以 <c>\n</c> 连接）。
    /// </summary>
    public static LrcLayers FromRawTexts(string? mainRaw, string? transRaw, string? romajiRaw)
    {
        var layers = string.IsNullOrEmpty(mainRaw) ? LrcLayers.Empty : SplitText(mainRaw);
        var original = layers.Original;
        var translation = transRaw is not null ? JoinLines(ExtractTextLines(transRaw)) : layers.Translation;
        var romanized = romajiRaw is not null ? JoinLines(ExtractTextLines(romajiRaw)) : layers.Romaji;

        return new LrcLayers(original, translation, romanized);
    }

    /// <summary>
    /// 定位主 <c>.lrc</c>（大小写不敏感）。为确定性，命中多个时取文件名序最小者。
    /// 目录不存在 / 无法枚举时返回 <c>null</c>。
    /// </summary>
    public static string? FindSidecar(string audioPath) => FindSidecars(audioPath).Main;

    /// <summary>
    /// 一次性枚举目录，返回（主 <c>&lt;stem&gt;.lrc</c> / 译文 <c>&lt;stem&gt;.trans.lrc</c> /
    /// 音译 <c>&lt;stem&gt;.romaji.lrc</c>），均大小写不敏感。每类命中多个时取文件名序最小者。
    /// 目录不存在 / 无法枚举时返回全 <c>null</c>。
    /// </summary>
    private static (string? Main, string? Trans, string? Romaji) FindSidecars(string audioPath)
    {
        var directory = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrEmpty(directory)) return (null, null, null);

        var stem = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrEmpty(stem)) return (null, null, null);

        var index = GetSidecarIndex(directory);
        if (index is null) return (null, null, null);

        return (index.Resolve(stem), index.Resolve(stem + ".trans"), index.Resolve(stem + ".romaji"));
    }

    /// <summary>
    /// 目录 → <c>.lrc</c> 清单（**每个目录只枚举一次**，进程内缓存）。
    ///
    /// 逐曲调用 <see cref="FindSidecars"/> 时若每次都整目录枚举，成本为
    /// <c>O(曲目数 × 目录条目数)</c>——单目录扁平大库（数万首）会退化成分钟级甚至小时级，
    /// 且每次增量（含零脏曲）都要重来一遍。缓存后为 <c>O(曲目数 + 目录条目数)</c>。
    /// 清单按「文件名序最小者优先」建表，与逐曲枚举的确定性取法一致。
    ///
    /// 新鲜度：缓存条目记录建表时的**目录 mtime**，每次命中先比对；目录内容变动
    /// （新增 / 删除 / 改名侧车）即重建——比每次整目录枚举只多一次 <c>stat</c>。
    /// 另见 <see cref="ResetDirectoryCache"/>（每次构建开始前清空）。
    /// 目录不存在返回 <c>null</c>；无法枚举只告警一次，不再逐曲重试。
    /// </summary>
    private static SidecarIndex? GetSidecarIndex(string directory)
    {
        // 命中且（信任本轮目录 mtime ｜ 目录 mtime 未变）→ 直接复用：**不产生任何 stat**。
        if (SidecarCache.TryGetValue(directory, out var cached) &&
            (TrustDirectoryStamp || cached.Stamp == DirectoryStamp(directory)))
        {
            return cached;
        }

        if (!Directory.Exists(directory)) return null;
        var stamp = Directory.GetLastWriteTimeUtc(directory).Ticks;
        return SidecarCache.AddOrUpdate(
            directory,
            _ => BuildSidecarIndex(directory, stamp),
            (_, existing) => existing.Stamp == stamp ? existing : BuildSidecarIndex(directory, stamp));
    }

    /// <summary>目录 mtime（ticks）；目录不存在 / 不可读返回 <c>-1</c>（必然不等 → 触发重建）。</summary>
    private static long DirectoryStamp(string directory)
    {
        try { return Directory.GetLastWriteTimeUtc(directory).Ticks; }
        catch { return -1; }
    }

    private static SidecarIndex BuildSidecarIndex(string directory, long stamp)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var files = Directory.EnumerateFiles(directory)
                .Where(f => string.Equals(Path.GetExtension(f), ".lrc", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
            foreach (var file in files)
                byName.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }
        catch (Exception ex)
        {
            // 枚举失败：告警一次并缓存空清单（旧实现是逐曲重试 + 逐曲告警）
            Warn?.Invoke($"枚举目录失败 {directory}: {ex.Message}");
        }
        return new SidecarIndex(byName, stamp);
    }

    /// <summary>
    /// **开始一次扫描**（扫描器在每轮扫描开始时调用）：清空目录清单缓存，并在**本轮扫描内**
    /// 信任首次读到的目录 mtime——之后每曲探测不再复核目录。
    ///
    /// 为什么安全：侧车**内容**变更靠 <c>FileInfo</c>（mtime/size）逐个识别，不依赖目录 mtime；
    /// 目录 mtime 只覆盖「新增 / 删除 / 改名」而扫描同时也在做这件事（媒体文件的发现同理）——
    /// 扫描**中途**落盘的侧车与媒体一样由下一轮扫描发现。这样把「每曲 2 次目录 stat」
    /// 降为「每目录 1 次」（30k 曲单目录实测：省掉约 6 万次 stat）。
    ///
    /// 长驻调用方若需要**严格**新鲜度（每次调用都复核目录），用 <see cref="ResetDirectoryCache"/>。
    /// 返回被清条目数。
    /// </summary>
    public static int BeginScan()
    {
        var cleared = SidecarCache.Count;
        SidecarCache.Clear();
        TrustDirectoryStamp = true;
        return cleared;
    }

    /// <summary>
    /// 清空目录清单缓存，返回被清条目数。**严格模式**：每次探测都复核目录 mtime
    /// （新增 / 删除 / 改名立即生效）。扫描器请改用 <see cref="BeginScan"/>（更省 stat）。
    /// </summary>
    public static int ResetDirectoryCache()
    {
        var cleared = SidecarCache.Count;
        SidecarCache.Clear();
        TrustDirectoryStamp = false;
        return cleared;
    }

    /// <summary>本轮是否信任目录 mtime（见 <see cref="BeginScan"/>）。</summary>
    private static volatile bool TrustDirectoryStamp;

    /// <summary>单个目录的 <c>.lrc</c> 清单：名称（去扩展名，大小写不敏感）→ 完整路径。</summary>
    private sealed class SidecarIndex
    {
        private readonly Dictionary<string, string> _byName;

        public SidecarIndex(Dictionary<string, string> byName, long stamp)
        {
            _byName = byName;
            Stamp = stamp;
        }

        /// <summary>建表时的目录 mtime（ticks），用于新鲜度比对。</summary>
        public long Stamp { get; }

        /// <summary>名称命中返回完整路径，否则 <c>null</c>。</summary>
        public string? Resolve(string name) => _byName.TryGetValue(name, out var path) ? path : null;
    }

    /// <summary>目录清单缓存（键为目录完整路径；值为不可变清单，构建后只读并发安全）。</summary>
    private static readonly ConcurrentDictionary<string, SidecarIndex> SidecarCache =
        new(StringComparer.Ordinal);

    /// <summary>剥离方括号标签与空行，返回纯文本行。</summary>
    private static List<string> ExtractTextLines(string raw)
    {
        var lines = new List<string>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = TagRegex.Replace(rawLine.TrimEnd('\r'), " ").Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>解析为有序条目：文本 = 剥离**所有**方括号标签后 trim；空文本行丢弃。</summary>
    private static List<LrcEntry> ExtractEntries(string raw)
    {
        var entries = new List<LrcEntry>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var text = TagRegex.Replace(line, " ").Trim();
            if (text.Length == 0) continue;
            var match = TimeRegex.Match(line);
            entries.Add(new LrcEntry(match.Success ? match.Value : null, text));
        }
        return entries;
    }

    /// <summary>
    /// 顺序条目 → 三层明细：**相同时间戳的连续行**归为同一条目多层；无时间戳行各自为原文。
    /// 原文只取每组第 1 行（不回退）。每行保留所属时间戳（毫秒）供置信度打分。
    /// </summary>
    private static DetailedLayers SplitDetailed(string? raw)
    {
        var layers = new DetailedLayers();
        if (string.IsNullOrEmpty(raw)) return layers;

        var entries = ExtractEntries(raw);
        var i = 0;
        while (i < entries.Count)
        {
            var entry = entries[i];
            if (entry.Timestamp is null)
            {
                layers.Original.Add(new LrcLine(entry.Text, null));
                i++;
                continue;
            }

            var ms = ParseTimestampMs(entry.Timestamp);
            var group = new List<string> { entry.Text };
            var j = i;
            while (j + 1 < entries.Count && entries[j + 1].Timestamp == entry.Timestamp)
            {
                j++;
                group.Add(entries[j].Text);
            }
            ClassifyGroup(group, ms, layers.Original, layers.Translation, layers.Romaji);
            i = j + 1;
        }
        return layers;
    }

    /// <summary>
    /// 单条时间戳分组 → 层：第 1 行原文；后两行按位置 第 2 行译文 / 第 3 行音译。
    /// 修正：若第 2 行为纯拉丁且第 3 行非拉丁，则二者对调（纯拉丁更可能是音译）。
    /// 组内仅 1 行时按纯拉丁否判为音译 / 译文。
    /// </summary>
    private static void ClassifyGroup(
        List<string> group, long? ms,
        List<LrcLine> originals, List<LrcLine> trans, List<LrcLine> romaji)
    {
        originals.Add(new LrcLine(group[0], ms));
        if (group.Count < 2) return;

        var rest = group.GetRange(1, group.Count - 1);
        if (rest.Count == 1)
        {
            if (IsLatinLine(rest[0])) romaji.Add(new LrcLine(rest[0], ms));
            else trans.Add(new LrcLine(rest[0], ms));
            return;
        }

        if (IsLatinLine(rest[0]) && !IsLatinLine(rest[1]))
        {
            romaji.Add(new LrcLine(rest[0], ms));
            trans.Add(new LrcLine(rest[1], ms));
        }
        else
        {
            trans.Add(new LrcLine(rest[0], ms));
            romaji.Add(new LrcLine(rest[1], ms));
        }
    }

    /// <summary>
    /// 时间标签 → 毫秒。<see cref="TimeRegex"/> 保证形态为 <c>[mm:ss]</c> /
    /// <c>[mm:ss.xx]</c> / <c>[mm:ss:xx]</c>，这里只做定点拆分（失败按 0 兜底）。
    /// </summary>
    private static long ParseTimestampMs(string tag)
    {
        if (tag.Length < 5) return 0;
        var inner = tag.AsSpan(1, tag.Length - 2);
        var colon = inner.IndexOf(':');
        if (colon <= 0) return 0;
        if (!long.TryParse(inner[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return 0;

        var tail = inner[(colon + 1)..];
        var separator = tail.IndexOfAny('.', ':');
        var secondsText = separator < 0 ? tail : tail[..separator];
        if (!long.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return 0;

        long fraction = 0;
        if (separator >= 0)
        {
            var digits = tail[(separator + 1)..];
            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                fraction = digits.Length switch
                {
                    1 => value * 100,
                    2 => value * 10,
                    _ => value,
                };
            }
        }
        return (minutes * 60 + seconds) * 1000 + fraction;
    }

    /// <summary>
    /// 纯拉丁行（含拉丁字母、且不含汉字 / 假名 / 谚文）——音译 / 罗马音的保守判定。
    /// 判定集中在 <see cref="ScriptRanges.IsLatinLine"/>（与索引器共用同一事实来源）：
    /// 旧实现「不含 CJK 即拉丁」会把**假名行误判成罗马音**（真实日文曲库实测）。
    /// </summary>
    private static bool IsLatinLine(string text) => ScriptRanges.IsLatinLine(text);

    /// <summary>
    /// 解码侧车或内嵌歌词文本（**公开**：扫描器写库前读原始文本用）。
    /// 解码失败返回 <c>null</c>（告警，不抛）。
    /// </summary>
    public static string? TryDecode(string path)
    {
        try { return Decode(path); }
        catch (Exception ex)
        {
            Warn?.Invoke($"读取 .lrc 失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>解码并取文本行；解码失败返回空列表（不抛）。</summary>
    private static List<string> TryDecodeLines(string path)
        => TryDecode(path) is { } raw ? ExtractTextLines(raw) : new List<string>();

    private static string JoinLines(List<string> lines) => string.Join('\n', lines);

    /// <summary>
    /// 解码：先按 BOM 识别 UTF-8 / UTF-16 LE|BE；无 BOM 时先做**严格 UTF-8 试解码**
    /// （含非法序列即失败），失败则走 .NET 框架内置 CodePages 的 GB18030 解码
    /// （覆盖 GBK / GB18030，含 4 字节序列），最终以 UTF-8 替换
    /// （非法字节替换为 <c>U+FFFD</c>，不抛异常）兜底。
    /// </summary>
    private static string Decode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return string.Empty;

        // 1) BOM：UTF-8 / UTF-16 LE / BE（剥掉 BOM 后按对应编码解码）。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 2) 无 BOM：严格 UTF-8 试解码（非法序列抛 DecoderFallbackException）。
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { /* 非 UTF-8，继续尝试 GB18030 */ }

        // 3) GB18030 / GBK 解码（框架 CodePages）。
        if (TryDecodeGb18030(bytes, out var gb18030)) return gb18030;

        // 4) 兜底：UTF-8 替换（合法字节照常、非法字节变 U+FFFD，不崩溃）。
        return ReplacingUtf8.GetString(bytes);
    }

    /// <summary>严格 UTF-8 解码器（禁用替换，非法序列即抛异常）。</summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>宽松 UTF-8 解码器（非法字节替换为 U+FFFD）。</summary>
    private static readonly UTF8Encoding ReplacingUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// GB18030 解码器（框架 CodePages）。首次访问时注册
    /// <see cref="CodePagesEncodingProvider"/>（幂等、进程级一次）；
    /// 非法字节以 <c>U+FFFD</c> 替换（与既有语义一致），4 字节序列由框架正确解码。
    /// </summary>
    private static readonly Encoding Gb18030 = CreateGb18030();

    private static Encoding CreateGb18030()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            "GB18030",
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    /// <summary>
    /// GB18030 / GBK 解码（框架 CodePages，按字节走，不抛异常）。空输入返回 <c>false</c>；
    /// 否则解码（非法字节 → <c>U+FFFD</c>）并返回 <c>true</c>。
    /// </summary>
    public static bool TryDecodeGb18030(ReadOnlySpan<byte> bytes, out string text)
    {
        if (bytes.Length == 0)
        {
            text = string.Empty;
            return false;
        }

        text = Gb18030.GetString(bytes);
        return true;
    }
}
