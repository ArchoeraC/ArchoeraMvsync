// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Archoera.Text;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>高亮区间（半开 <c>[Start, End)</c>，UTF-16 code unit，与 Dart <c>String</c> 索引一致）。</summary>
internal readonly record struct HighlightRange(string Field, int Start, int End, string Mode);

/// <summary>单条命中的高亮集合（D.3.2）。</summary>
internal sealed class TrackHighlight
{
    /// <summary>按字段顺序排列的区间（每字段内按 start 升序）。</summary>
    public IReadOnlyList<HighlightRange> Ranges { get; init; } = Array.Empty<HighlightRange>();

    /// <summary>字段数或单字段区间数超过上限被截断。</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// 结果高亮（T2.7）：查询期只读 <c>library.db</c> 取原文，按**字面**优先、读音 / 首字母补充
/// 计算 UTF-16 区间。
///
/// <para>语义镜像 Dart <c>search_highlight.dart</c> + <c>search_highlight_pinyin.dart</c>：
/// 字面命中大小写不敏感、全角折叠、查询空白折叠（并尝试去空格版本）；
/// 字面为空时按连续 CJK 段重算拼音 / 首字母（音节数 ≠ 段长度则跳过该段，不猜）。</para>
///
/// <para>只对**前 <see cref="MaxRows"/> 条**（分页容量）生成；每字段最多
/// <see cref="MaxRangesPerField"/> 段、最多 <see cref="MaxFields"/> 个字段，超出置
/// <see cref="TrackHighlight.Truncated"/>。歌词只在 <c>snippet=true</c>
/// （<c>includeLyrics</c>）时读取原文，且同样只取前 <see cref="MaxRows"/> 条。</para>
/// 库不存在 / 不可读时**失败开放**：返回空字典，只记 warning。</para>
/// </summary>
internal static class SearchHighlight
{
    /// <summary>
    /// 只为前多少条命中生成高亮（D.3.1 / D.3.2）。
    /// 与分页容量同阶（每页 300）：UI 每页单独走 <c>era_search_decorate</c>。
    /// </summary>
    internal const int MaxRows = 300;

    /// <summary>单个字段最多的区间段数。</summary>
    internal const int MaxRangesPerField = 32;

    /// <summary>最多返回多少个字段的高亮。</summary>
    internal const int MaxFields = 4;

    private static readonly IReadOnlyDictionary<string, TrackHighlight> Empty =
        new Dictionary<string, TrackHighlight>(StringComparer.Ordinal);

    /// <summary>
    /// 为 <paramref name="items"/> 的前 50 条生成高亮（按 trackId 索引）。
    /// <paramref name="includeLyrics"/> 对应请求的 <c>snippet</c> 开关。
    /// </summary>
    public static IReadOnlyDictionary<string, TrackHighlight> Build(
        string libraryDbPath, IReadOnlyList<SearchHit> items, string query, bool includeLyrics)
    {
        var wanted = new List<string>(MaxRows);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in items)
        {
            if (wanted.Count >= MaxRows) break;
            if (seen.Add(hit.TrackId)) wanted.Add(hit.TrackId);
        }
        if (wanted.Count == 0 || string.IsNullOrEmpty(query) || !File.Exists(libraryDbPath))
            return Empty;

        try
        {
            var docs = LoadDocuments(libraryDbPath, wanted);
            var lyrics = includeLyrics
                ? SearchSnippet.LoadLayers(
                    libraryDbPath,
                    wanted.Take(Math.Min(SearchSnippet.MaxRows, wanted.Count)).ToList())
                : new Dictionary<string, (string Original, string Translation, string Romaji)>(
                    StringComparer.Ordinal);
            var tokens = Analyzer.AnalyzeQuery(query);

            var result = new Dictionary<string, TrackHighlight>(StringComparer.Ordinal);
            foreach (var id in wanted)
            {
                if (!docs.TryGetValue(id, out var doc)) continue;

                var ranges = new List<HighlightRange>();
                var fieldCount = 0;
                var truncated = false;

                void AddRanges(List<HighlightRange> fieldRanges)
                {
                    if (fieldRanges.Count == 0) return;
                    if (fieldCount >= MaxFields)
                    {
                        truncated = true;
                        return;
                    }
                    fieldCount++;
                    if (fieldRanges.Count > MaxRangesPerField)
                    {
                        fieldRanges.RemoveRange(
                            MaxRangesPerField, fieldRanges.Count - MaxRangesPerField);
                        truncated = true;
                    }
                    ranges.AddRange(fieldRanges);
                }

                void AddField(string field, string text)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    AddRanges(MatchField(text, query, tokens, field));
                }

                AddField("title", doc.Title);
                AddField("artist", doc.Artist);
                AddField("album", doc.Album);
                AddField("filename", doc.Filename);

                if (lyrics.TryGetValue(id, out var layer))
                {
                    // 与 snippet 一致：只取第一层命中的歌词层。
                    foreach (var (field, text) in new[]
                             {
                                 ("lyrics", layer.Original),
                                 ("lyricsTr", layer.Translation),
                                 ("lyricsRom", layer.Romaji),
                             })
                    {
                        var fieldRanges = string.IsNullOrEmpty(text)
                            ? new List<HighlightRange>()
                            : MatchField(text, query, tokens, field);
                        if (fieldRanges.Count == 0) continue;
                        AddRanges(fieldRanges);
                        break;
                    }
                }

                if (ranges.Count > 0)
                    result[id] = new TrackHighlight { Ranges = ranges, Truncated = truncated };
            }
            return result.Count == 0 ? Empty : result;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarn("indexer", $"结果高亮跳过（library.db 不可读）: {ex.Message}");
            return Empty;
        }
    }

    private readonly record struct HighlightDoc(
        string Title, string Artist, string Album, string Filename);

    /// <summary>按 id 批量取元数据原文（标题 / 歌手 / 专辑 / 文件名）。</summary>
    private static Dictionary<string, HighlightDoc> LoadDocuments(string libraryDbPath, List<string> ids)
    {
        var map = new Dictionary<string, HighlightDoc>(StringComparer.Ordinal);
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryDbPath, readOnly: true));
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 1000";
            pragma.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            names.Add("@p" + i);
            cmd.Parameters.AddWithValue("@p" + i, ids[i]);
        }
        cmd.CommandText =
            $"SELECT id, title, artists, album, path FROM tracks " +
            $"WHERE id IN ({string.Join(',', names)})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (id.Length == 0) continue;
            var path = reader.IsDBNull(4) ? "" : reader.GetString(4);
            map[id] = new HighlightDoc(
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                ParseArtistsDisplay(reader.IsDBNull(2) ? null : reader.GetString(2)),
                IndexerEngine.ParseAlbum(reader.IsDBNull(3) ? null : reader.GetString(3)),
                path.Length == 0 ? "" : Path.GetFileNameWithoutExtension(path));
        }
        return map;
    }

    /// <summary>
    /// 高亮用的歌手展示文本：与索引用的 <see cref="IndexerEngine.ParseArtists"/>（空格连接）不同，
    /// 这里按 <c>" / "</c> 连接，和 Dart <c>Track.artistNames</c> 的展示格式一致，保证区间偏移可直接复用。
    /// </summary>
    private static string ParseArtistsDisplay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
            var names = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    var value = name.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
                }
            }
            return string.Join(" / ", names);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /* ------------------------------------------------------------------ */
    /* 单字段匹配                                                          */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 单字段区间：字面优先；字面为空时按 CJK 段补充读音（<c>reading</c>）与首字母（<c>initial</c>）。
    /// </summary>
    private static List<HighlightRange> MatchField(
        string text, string query, QueryTokens tokens, string field)
    {
        var literal = SearchSnippet.MatchRanges(text, query);
        if (literal.Count > 0)
        {
            var mapped = new List<HighlightRange>(literal.Count);
            foreach (var range in literal)
                mapped.Add(new HighlightRange(field, range.Start, range.End, "literal"));
            return mapped;
        }

        var reading = new List<HighlightRange>();
        var initial = new List<HighlightRange>();
        foreach (var segment in CjkSegments(text))
        {
            var syllables = Pinyin.FromCjkRun(segment.Text);
            // 1 汉字 ↔ 1 音节；长度不符说明归一化改变了长度，放弃该段（D.3.2「不猜」）。
            if (syllables.Count == 0 || syllables.Count != segment.Text.Length) continue;
            reading.AddRange(MatchReading(tokens.Pinyin, syllables, segment.Start, field));
            initial.AddRange(MatchInitials(tokens, syllables, segment.Start, field));
        }

        reading = Merge(reading);
        initial = Merge(initial);
        if (reading.Count > 0 && initial.Count > 0)
            initial.RemoveAll(range => reading.Any(other => Overlaps(other, range)));

        if (reading.Count == 0) return initial;
        if (initial.Count == 0) return reading;
        var combined = new List<HighlightRange>(reading.Count + initial.Count);
        combined.AddRange(reading);
        combined.AddRange(initial);
        return combined;
    }

    /// <summary>文本内的连续 CJK 段（[Text] 与其在原文中的 UTF-16 起始下标）。</summary>
    private readonly record struct CjkSegment(string Text, int Start);

    private static List<CjkSegment> CjkSegments(string text)
    {
        var segments = new List<CjkSegment>();
        var i = 0;
        while (i < text.Length)
        {
            if (!ScriptRanges.IsCjk(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && ScriptRanges.IsCjk(text[i])) i++;
            segments.Add(new CjkSegment(text.Substring(start, i - start), start));
        }
        return segments;
    }

    /// <summary>查询音节在段内连续命中（单音节允许前缀，≥2 音节要求逐位相等）。</summary>
    private static List<HighlightRange> MatchReading(
        IReadOnlyList<string> querySyllables,
        List<string> segmentSyllables,
        int offset,
        string field)
    {
        var query = NormalizeTokens(querySyllables);
        if (query.Count == 0 || query.Count > segmentSyllables.Count)
            return new List<HighlightRange>();

        var prefixLast = query.Count == 1;
        var ranges = new List<HighlightRange>();
        for (var start = 0; start + query.Count <= segmentSyllables.Count; start++)
        {
            if (!RunMatches(segmentSyllables, start, query, prefixLast)) continue;
            ranges.Add(new HighlightRange(
                field, offset + start, offset + start + query.Count, "reading"));
        }
        return ranges;
    }

    private static bool RunMatches(
        List<string> syllables, int start, List<string> query, bool prefixLast)
    {
        for (var k = 0; k < query.Count; k++)
        {
            var segment = syllables[start + k].ToLowerInvariant();
            var token = query[k];
            if (k == query.Count - 1 && prefixLast)
            {
                if (!segment.StartsWith(token, StringComparison.Ordinal)) return false;
            }
            else if (!string.Equals(segment, token, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 首字母匹配：候选为查询 <c>initials</c> 各段、查询全拼串、查询词法串；
    /// 在段首字母串中做子串匹配，按「1 首字母 ↔ 1 汉字」映射回原文下标。
    /// </summary>
    private static List<HighlightRange> MatchInitials(
        QueryTokens tokens, List<string> segmentSyllables, int offset, string field)
    {
        var initials = BuildInitials(segmentSyllables);
        if (initials.Length == 0) return new List<HighlightRange>();

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in SplitLower(tokens.PinyinInitials))
            candidates.Add(part);
        var joinedPinyin = string.Concat(NormalizeTokens(tokens.Pinyin));
        if (joinedPinyin.Length > 0) candidates.Add(joinedPinyin);
        var joinedLexical = string.Concat(NormalizeTokens(tokens.Lexical));
        if (joinedLexical.Length > 0) candidates.Add(joinedLexical);
        candidates.RemoveWhere(candidate => candidate.Length == 0 || candidate.Length > initials.Length);
        if (candidates.Count == 0) return new List<HighlightRange>();

        var ranges = new List<HighlightRange>();
        foreach (var candidate in candidates)
        {
            var from = 0;
            while (from <= initials.Length - candidate.Length)
            {
                var index = initials.IndexOf(candidate, from, StringComparison.Ordinal);
                if (index < 0) break;
                ranges.Add(new HighlightRange(
                    field, offset + index, offset + index + candidate.Length, "initial"));
                from = index + 1;
            }
        }
        return ranges;
    }

    private static string BuildInitials(List<string> syllables)
    {
        var builder = new StringBuilder(syllables.Count);
        foreach (var syllable in syllables)
            if (syllable.Length > 0) builder.Append(syllable[0]);
        return builder.ToString().ToLowerInvariant();
    }

    /* ------------------------------------------------------------------ */
    /* 工具                                                                */
    /* ------------------------------------------------------------------ */

    private static bool Overlaps(HighlightRange a, HighlightRange b)
        => a.Start < b.End && b.Start < a.End;

    /// <summary>按 start / end 升序合并重叠或相邻区间（同字段、同 mode 调用）。</summary>
    private static List<HighlightRange> Merge(List<HighlightRange> ranges)
    {
        if (ranges.Count <= 1) return ranges;
        ranges.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : a.End.CompareTo(b.End));
        var merged = new List<HighlightRange>(ranges.Count) { ranges[0] };
        for (var i = 1; i < ranges.Count; i++)
        {
            var last = merged[merged.Count - 1];
            var next = ranges[i];
            if (next.Start <= last.End)
            {
                if (next.End > last.End)
                    merged[merged.Count - 1] = last with { End = next.End };
            }
            else
            {
                merged.Add(next);
            }
        }
        return merged;
    }

    private static List<string> NormalizeTokens(IReadOnlyList<string> tokens)
    {
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            var normalized = token.Trim().ToLowerInvariant();
            if (normalized.Length > 0) result.Add(normalized);
        }
        return result;
    }

    private static List<string> SplitLower(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].ToLowerInvariant();
        return parts.ToList();
    }
}
