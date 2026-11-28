// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Lyrics;

namespace Archoera.Indexer;

/// <summary>
/// <c>explain.lexicalSources</c> 的一条来源（D.3.1）：产生当前候选的步骤 + 该步的证据。
/// S2 仍按趟次名归因；<see cref="Step"/> 兼作 pass 名的可读别名。
/// </summary>
internal sealed class ExplainSource
{
    public string Step { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Field { get; init; } = "";
    public int Matched { get; init; }
    public bool Positioned { get; init; }
}

/// <summary>
/// 单条命中的 <c>explain</c>（D.3.1）。
///
/// <para><b>T3.11 第二步（两者合一）</b>：<see cref="Field"/> 是**排序实际使用的字段**；
/// 字面精确度复核生效时（<see cref="Evidence.ExactChecked"/> = true）它已经由
/// <see cref="SearchExactness.RankBest"/> 给出 title / artist / album，<see cref="ResolvedField"/>
/// 直接取同一个值——不再走 explain 期的逐列探测（否则排序键与解释会互相矛盾）。</para>
///
/// <para><see cref="ResolvedField"/> 只在**精确度未复核**的步骤上仍作诊断探测的别名
/// （宽命中步、读音 / 拼音 / 首字母 / 跨语言 / 容错步），那时它可以比
/// <see cref="Field"/> 更细（例如 typo 命中真实落在 title、或译文命中落在 lyricsTr）。
/// Q1 歌词整行复核命中后字段仍是保守的 Lyrics，<see cref="ResolvedField"/> 随复核标志取同一值。</para>
///
/// <para><b>T3.11</b>：<see cref="Exact"/> / <see cref="Proximity"/> 与
/// <see cref="Bucket"/> 的第 4 / 5 元现在是排序实际用的值（不再是常量与 -1）。
/// 未复核的步（读音 / 转写 / 容错、或单步命中超预算）写
/// <c>exact="none"</c> / <c>proximity=-1</c>；歌词整行复核（Q1）只可能写 <c>equal</c>。</para>
/// </summary>
internal sealed class SearchExplanation
{
    public int Version { get; init; } = 1;
    public string Kind { get; init; } = "";
    public string Field { get; init; } = "";
    public string FieldId { get; init; } = "";
    public string ResolvedField { get; init; } = "";
    public string ResolvedFieldId { get; init; } = "";
    public int Matched { get; init; }
    public int Total { get; init; }
    public double Coverage { get; init; }
    public string Exact { get; init; } = "none";
    public int Proximity { get; init; } = -1;
    public bool Positioned { get; init; }
    public double[] Bucket { get; init; } = Array.Empty<double>();
    public double Bm25 { get; init; }
    public IReadOnlyList<ExplainSource> LexicalSources { get; init; } = Array.Empty<ExplainSource>();

    /// <summary>
    /// 歌词命中的层名（<c>embedded</c> / <c>lrc</c> / <c>lrc_trans</c> / <c>lrc_romaji</c> /
    /// <c>user</c>）；非歌词命中为空串。名称由 <see cref="Archoera.Lyrics.LrcReader.LayerName"/>
    /// 单一映射（T4.2 / §5.6.3）。
    /// </summary>
    public string Layer { get; init; } = "";

    /// <summary>歌词命中的层置信度（<c>high</c> / <c>medium</c> / <c>low</c> / <c>none</c>）。</summary>
    public string Confidence { get; init; } = "none";
}

/// <summary>执行轨迹的一步（D.3.3 的 <c>trace.steps[]</c>）。</summary>
internal sealed class StepTrace
{
    public int I { get; init; }
    public string Step { get; init; } = "";
    public string Kind { get; init; } = "";
    public string FieldId { get; init; } = "";
    public string Op { get; init; } = "";
    public int Returned { get; init; }
    public string State { get; init; } = "ran";
    public string? Reason { get; init; }
    public double Ms { get; init; }
}

/// <summary><c>trace.skipped[]</c> 的一条。</summary>
internal sealed class TraceSkip
{
    public int I { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// <c>trace.confidence[]</c> 的一条（T4.2，additive）：该步按层置信度过滤掉了哪些层。
/// <see cref="Policy"/> = <c>all</c>（只保留中 / 高）或 <c>crossLingual</c>（只保留高）。
/// </summary>
internal sealed class ConfidenceFilterTrace
{
    /// <summary>对应 <c>trace.steps[]</c> 的下标。</summary>
    public int I { get; init; }

    /// <summary><c>all</c> / <c>crossLingual</c>。</summary>
    public string Policy { get; init; } = "";

    /// <summary>被过滤掉的层置信度（<c>low</c> / <c>medium</c>）。</summary>
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 查询执行轨迹（D.3.3）。S2 只输出 <c>steps</c> / <c>skipped</c>：
/// <c>est</c> 需要 df 快照（v7 没有），<c>truncated</c> 与 deadline 归 S3。
/// </summary>
internal sealed class SearchTrace
{
    public IReadOnlyList<StepTrace> Steps { get; init; } = Array.Empty<StepTrace>();
    public IReadOnlyList<TraceSkip> Skipped { get; init; } = Array.Empty<TraceSkip>();

    /// <summary>被层置信度过滤的步骤（T4.2；additive 字段，旧的 <c>steps</c> / <c>skipped</c> 语义不变）。</summary>
    public IReadOnlyList<ConfidenceFilterTrace> Confidence { get; init; }
        = Array.Empty<ConfidenceFilterTrace>();

    public bool DeadlineHit => false;
}

/// <summary>一步的探测材料：词元（字面 / 拼音 / 首字母）与已解析表达式（模糊变体）。</summary>
internal readonly record struct StepProbe(string[] Terms, string Expression);

/// <summary>
/// <c>explain</c> / <c>trace</c> 的构造与 wire 名称（S2 / T2.3、T2.5、T2.7 的一部分）。
///
/// <para><b>只读派生</b>：关闭 explain 时排序结果必须逐条一致（见 <c>SearchExplainTests</c>）。
/// 字段探测走 <see cref="EraExplainProbe"/>（EraStore 词典 + 倒排），按 kind 选择探测字段，
/// 最多解释前 50 条；查询期不再接触 <c>search.db</c>。</para>
///
/// <para><b>为什么按 kind 而不是整条表达式探测</b>：v7 主趟表达式是
/// 「字面 OR 拼音 OR 首字母 OR 读音」的复合式，内部自带列过滤；把整条式子再套一层
/// <c>{col} : (...)</c> 会让所有分支都被外层列约束卡死，必然探测失败。
/// 因此按步骤声明的 kind，用该步词元重建单列探测式。</para>
/// </summary>
internal static class SearchExplainBuilder
{
    /// <summary>D.3.1：explain 只对前 50 条生成。</summary>
    public const int MaxExplained = 50;

    private static readonly (FieldKind Field, EraStore.FieldId FieldId)[] MetaLiteralColumns =
    {
        (FieldKind.Title, EraStore.FieldId.Title),
        (FieldKind.Artist, EraStore.FieldId.Artist),
        (FieldKind.Album, EraStore.FieldId.Album),
        (FieldKind.Filename, EraStore.FieldId.Filename),
    };

    private static readonly (FieldKind Field, EraStore.FieldId FieldId)[] LyricsLiteralColumns =
    {
        (FieldKind.Lyrics, EraStore.FieldId.Lyrics),
        (FieldKind.LyricsTr, EraStore.FieldId.LyricsTranslation),
        (FieldKind.LyricsRom, EraStore.FieldId.LyricsRomanization),
    };

    private static readonly (FieldKind Field, EraStore.FieldId FieldId)[] PinyinColumns =
    {
        (FieldKind.Title, EraStore.FieldId.PinyinMeta),
        (FieldKind.Lyrics, EraStore.FieldId.PinyinLyrics),
    };

    private static readonly (FieldKind Field, EraStore.FieldId FieldId)[] InitialsColumns =
    {
        (FieldKind.Title, EraStore.FieldId.InitialsMeta),
        (FieldKind.Lyrics, EraStore.FieldId.InitialsLyrics),
    };

    private static readonly (FieldKind Field, EraStore.FieldId FieldId)[] ReadingColumns =
    {
        (FieldKind.Title, EraStore.FieldId.ReadingMeta),
        (FieldKind.LyricsRom, EraStore.FieldId.LyricsRomanization),
    };

    public static SearchExplanation Build(
        Candidate candidate,
        int totalTerms,
        IReadOnlyDictionary<string, List<StepProbe>> probesByPass,
        EraExplainProbe probe)
    {
        var evidence = candidate.Evidence;
        var total = Math.Max(1, totalTerms);
        var matched = Math.Clamp((int)Math.Round(evidence.Coverage * total), 0, total);
        // 复核过的证据：field 就是真实字段，resolvedField 必须与排序键一致（不再探测）。
        // 未复核（宽命中 / 转写 / 读音 / 拼音 / 首字母 / 容错）：保留逐列探测作为诊断补充。
        // 歌词整行复核（Q1）命中时 ExactChecked=true，resolvedField 与 field 同值（Lyrics）。
        var resolved = evidence.ExactChecked
            ? evidence.Field
            : ResolveField(candidate, probesByPass, probe);
        var attribution = probe.LyricsAttribution(candidate.DocId, resolved);

        return new SearchExplanation
        {
            Kind = KindName(evidence.Kind),
            Field = FieldName(evidence.Field),
            FieldId = FieldId(evidence.Field),
            ResolvedField = FieldName(resolved),
            ResolvedFieldId = FieldId(resolved),
            Layer = attribution.Layer,
            Confidence = attribution.Confidence,
            Matched = matched,
            Total = total,
            Coverage = evidence.Coverage,
            Exact = ExactName(evidence.Exactness),
            Proximity = WireProximity(evidence.Proximity),
            Positioned = evidence.Positioned,
            Bucket = new[]
            {
                SearchRanker.KindRank(evidence.Kind),
                evidence.Coverage,
                SearchRanker.FieldRank(evidence.Field),
                evidence.Exactness,
                WireProximity(evidence.Proximity),
            },
            Bm25 = candidate.Bm25,
            LexicalSources = candidate.Sources
                .Select(source => new ExplainSource
                {
                    Step = source.Pass,
                    Kind = KindName(source.Evidence.Kind),
                    Field = FieldName(source.Evidence.Field),
                    Matched = Math.Clamp(
                        (int)Math.Round(source.Evidence.Coverage * total), 0, total),
                    Positioned = source.Evidence.Positioned,
                })
                .ToArray(),
        };
    }

    /// <summary>10 档 kind 的 wire 名（lowerCamel，S2 定稿；见 search-spec §4.3 / D.3.1）。</summary>
    public static string KindName(MatchKind kind) => kind switch
    {
        MatchKind.Exact => "exact",
        MatchKind.Phrase => "phrase",
        MatchKind.Prefix => "prefix",
        MatchKind.Partial => "partial",
        MatchKind.PinyinExact => "pinyinExact",
        MatchKind.PinyinPhrase => "pinyinPhrase",
        MatchKind.Initials => "initials",
        MatchKind.Reading => "reading",
        MatchKind.CrossLingual => "crossLingual",
        MatchKind.Typo => "typo",
        _ => "typo",
    };

    public static string FieldName(FieldKind field) => field switch
    {
        FieldKind.Title => "title",
        FieldKind.Artist => "artist",
        FieldKind.Album => "album",
        FieldKind.Filename => "filename",
        FieldKind.Lyrics => "lyrics",
        FieldKind.LyricsTr => "lyricsTr",
        FieldKind.LyricsRom => "lyricsRom",
        _ => "lyrics",
    };

    /// <summary>字段族 id（§3.3.1）：元数据 0x01–0x04 / 歌词 0x05–0x07。</summary>
    public static string FieldId(FieldKind field) => field switch
    {
        FieldKind.Title => "0x01",
        FieldKind.Artist => "0x02",
        FieldKind.Album => "0x03",
        FieldKind.Filename => "0x04",
        FieldKind.Lyrics => "0x05",
        FieldKind.LyricsTr => "0x06",
        FieldKind.LyricsRom => "0x07",
        _ => "0x05",
    };

    /// <summary>步骤的字段族范围（<c>trace.fieldId</c>；§3.3.1）。</summary>
    public static string FamilyId(FieldFamily family) => family switch
    {
        FieldFamily.Meta => "0x01-0x04",
        FieldFamily.Lyrics => "0x05-0x07",
        FieldFamily.Mixed => "0x01-0x07",
        FieldFamily.Pinyin => "0x11-0x13",
        FieldFamily.Initials => "0x14-0x16",
        FieldFamily.Reading => "0x17",
        FieldFamily.CrossLingual => "0x01-0x07",
        FieldFamily.Fuzzy => "0x01-0x07",
        _ => "0x01-0x07",
    };

    /// <summary>步骤操作符的 wire 名（lowercase）。</summary>
    public static string OpName(MatchOp op) => op switch
    {
        MatchOp.Term => "term",
        MatchOp.Prefix => "prefix",
        MatchOp.Phrase => "phrase",
        MatchOp.And => "and",
        MatchOp.Or => "or",
        _ => "and",
    };

    private static string ExactName(int exactness) => exactness switch
    {
        SearchExactness.Equal => "equal",
        SearchExactness.StartsWith => "startsWith",
        SearchExactness.Contains => "contains",
        _ => "none",
    };

    /// <summary>
    /// 排序键里的邻近度 → wire 值：算不出（歌词族 / 散落多 term / 单步超预算）写 <c>-1</c>，
    /// 与 D.3.1 既有的「不可用」口径一致（不新增 wire 值）。
    /// </summary>
    public static int WireProximity(int proximity)
        => proximity >= SearchRanker.ProximityUnknown ? -1 : proximity;

    /// <summary>
    /// 逐列探测真实字段（**只用于精确度未复核的步骤**）：同一个 pass 可能有多个步骤
    /// （丢词步共享 pass），取第一个能在该文档上命中的。探测失败退回排序用的保守字段。
    /// 复核过的证据不进这里（见 <see cref="Build"/>）。
    /// </summary>
    private static FieldKind ResolveField(
        Candidate candidate,
        IReadOnlyDictionary<string, List<StepProbe>> probesByPass,
        EraExplainProbe probe)
    {
        if (!probesByPass.TryGetValue(candidate.Pass, out var probes)) return candidate.Evidence.Field;
        foreach (var step in probes)
        {
            var resolved = Probe(candidate, step, probe);
            if (resolved.HasValue) return resolved.Value;
        }
        return candidate.Evidence.Field;
    }

    private static FieldKind? Probe(Candidate candidate, StepProbe step, EraExplainProbe probe)
    {
        switch (candidate.Evidence.Kind)
        {
            case MatchKind.PinyinExact:
            case MatchKind.PinyinPhrase:
                return probe.ProbeColumns(candidate.DocId, step.Terms, PinyinColumns);
            case MatchKind.Initials:
                return probe.ProbeColumns(candidate.DocId, step.Terms, InitialsColumns);
            case MatchKind.Reading:
                return probe.ProbeColumns(candidate.DocId, step.Terms, ReadingColumns);
            case MatchKind.Typo:
                // fuzzy / typo2 的表达式是纯变体 OR（没有内部列过滤）；
                // cjk-fuzzy 是复合式，退回字面词元探测（可能探不到，最终走保守字段）。
                if (candidate.Pass is SearchPassNames.Fuzzy or SearchPassNames.Typo2)
                {
                    var variants = EraExplainProbe.QuotedTerms(step.Expression);
                    var hit = probe.ProbeColumns(candidate.DocId, variants, MetaLiteralColumns)
                        ?? probe.ProbeColumns(candidate.DocId, variants, LyricsLiteralColumns);
                    if (hit.HasValue) return hit;
                }
                return ProbeLiteral(candidate.DocId, step.Terms, probe);
            default:
                return ProbeLiteral(candidate.DocId, step.Terms, probe);
        }
    }

    private static FieldKind? ProbeLiteral(
        long docId, string[] terms, EraExplainProbe probe)
        => probe.ProbeColumns(docId, terms, MetaLiteralColumns)
        ?? probe.ProbeColumns(docId, terms, LyricsLiteralColumns);
}

/// <summary>
/// explain 期的字段探针（T3.12）：只读 EraStore 的词典 + 倒排，判断某个词元是否真的出现在
/// 文档的某个字段里。候选的 <c>DocId</c> 是 EraStore 的虚拟 doc id
/// （<c>SegmentId * 1_000_000 + 段内 doc</c>，见 <c>SearchSession</c> 的候选构造）。
/// </summary>
internal sealed class EraExplainProbe
{
    private const long DocIdStride = 1_000_000L;

    private readonly Dictionary<ushort, EraStore.SegmentReaders> _segments;

    public EraExplainProbe(EraStore.StoreReader reader)
    {
        _segments = new Dictionary<ushort, EraStore.SegmentReaders>();
        foreach (var segment in reader.OpenPerSegment())
            _segments[segment.SegmentId] = segment;
    }

    /// <summary>任一 <paramref name="terms"/> 命中 <paramref name="columns"/> 中的字段即返回该字段。</summary>
    public FieldKind? ProbeColumns(
        long virtualDocId,
        IReadOnlyList<string> terms,
        (FieldKind Field, EraStore.FieldId FieldId)[] columns)
    {
        if (terms.Count == 0) return null;

        var segmentId = (ushort)(virtualDocId / DocIdStride);
        var doc = (int)(virtualDocId % DocIdStride);
        if (!_segments.TryGetValue(segmentId, out var segment)) return null;
        if ((uint)doc >= (uint)segment.Docs.Count) return null;

        foreach (var (field, fieldId) in columns)
        {
            foreach (var term in terms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                if (!segment.Dict.TryFind(fieldId, term, out var info) || info.Df == 0) continue;
                var cursor = segment.Postings.Open(info.PostOffset, info.Df);
                if (cursor.Advance(doc) && cursor.CurrentDoc == doc) return field;
            }
        }
        return null;
    }

    /// <summary>
    /// 歌词命中的层 / 置信度归因（T4.2，§5.6.3）：直接读该 doc 的 <c>layer_conf</c> 与
    /// 来源位。非歌词字段返回 <c>("", "none")</c>。
    /// </summary>
    public (string Layer, string Confidence) LyricsAttribution(long virtualDocId, FieldKind field)
    {
        var layer = field switch
        {
            FieldKind.Lyrics => LrcReader.LayerOriginal,
            FieldKind.LyricsTr => LrcReader.LayerTranslation,
            FieldKind.LyricsRom => LrcReader.LayerRomaji,
            _ => -1,
        };
        if (layer < 0) return ("", "none");

        var segmentId = (ushort)(virtualDocId / DocIdStride);
        var doc = (int)(virtualDocId % DocIdStride);
        if (!_segments.TryGetValue(segmentId, out var segment)) return ("", "none");
        if ((uint)doc >= (uint)segment.Docs.Count) return ("", "none");

        var flags = segment.Docs.Flags(doc);
        var source = EraStore.DocStoreReader.LayerSourceAt(flags, layer);
        // 音译来源只有 1 bit（0 = 非显式 / 1 = 显式），LayerName 会映射成 embedded / lrc_romaji。
        var confidence = segment.Docs.LayerConfidenceAt(doc, layer);
        return (LrcReader.LayerName(layer, source), LrcReader.ConfidenceName(confidence));
    }

    /// <summary>
    /// 从生成的变体表达式里提取双引号词条（处理 <c>""</c> 转义）。
    /// 仅用于 fuzzy / typo2 的纯 OR 变体式；其它复合式仍按字面词元探测。
    /// </summary>
    public static List<string> QuotedTerms(string expression)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(expression)) return terms;

        var i = 0;
        while (i < expression.Length)
        {
            if (expression[i] != '"') { i++; continue; }
            i++;
            var start = i;
            var builder = new System.Text.StringBuilder();
            while (i < expression.Length)
            {
                if (expression[i] != '"') { builder.Append(expression[i++]); continue; }
                if (i + 1 < expression.Length && expression[i + 1] == '"')
                {
                    builder.Append('"');
                    i += 2;
                    continue;
                }
                i++;
                break;
            }
            if (i > start) terms.Add(builder.ToString());
        }

        return terms.Distinct(StringComparer.Ordinal).ToList();
    }
}
