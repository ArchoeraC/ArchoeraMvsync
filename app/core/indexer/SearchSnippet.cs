// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Lyrics;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>片段内的一段命中区间（半开 <c>[Start, End)</c>，相对 <see cref="LyricSnippet.Text"/>）。</summary>
internal readonly record struct SnippetRange(int Start, int End);

/// <summary>
/// 歌词片段（D.3.2 的 <c>snippet</c> 对象）。
///
/// <para>原文不复制进索引（contentless FTS5），因此查询期以只读连接打开
/// <c>library.db</c> 现取现截；只对**前 <see cref="MaxRows"/> 条**（分页容量）命中生成，
/// 失败时整条缺省（不构造对象）。</para>
/// </summary>
internal sealed class LyricSnippet
{
    /// <summary>片段来自哪一列（<c>lyrics</c> / <c>lyricsTr</c> / <c>lyricsRom</c>）。</summary>
    public string Field { get; init; } = "lyrics";

    /// <summary>窗口文本（≤ <see cref="SearchSnippet.MaxTextLength"/> UTF-16 code unit）。</summary>
    public string Text { get; init; } = "";

    /// <summary>窗口内的命中区间（相对 <see cref="Text"/>，半开）。</summary>
    public IReadOnlyList<SnippetRange> Ranges { get; init; } = Array.Empty<SnippetRange>();

    /// <summary>窗口被截到长度上限（<see cref="Text"/> 不完整）。</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// 歌词片段截取（T2.8）：查询期只读 <c>library.db</c>，按**字面**命中截取窗口。
///
/// <para>语义镜像 Dart <c>search_highlight.dart</c> 的 <c>snippet()</c>：
/// 大小写不敏感、全角 ASCII → 半角、查询空白折叠（并额外尝试去空格版本）、
/// 首个命中前后各 24 个 UTF-16 code unit，被裁剪端加省略号「…」。</para>
///
/// <para>库不存在 / 不可读 / 锁冲突时**失败开放**：返回空字典（所有 <c>snippet</c> 为 null），
/// 只记一条 warning，不把查询整体判失败。</para>
/// </summary>
internal static class SearchSnippet
{
    /// <summary>只为前多少条命中生成片段（D.3.2）；与分页容量同阶（每页 300）。</summary>
    internal const int MaxRows = 300;

    /// <summary>片段文本的 UTF-16 code unit 上限（D.3.2）。</summary>
    internal const int MaxTextLength = 512;

    /// <summary>命中前后保留的上下文长度。</summary>
    internal const int Radius = 24;

    private static readonly IReadOnlyDictionary<string, LyricSnippet> Empty =
        new Dictionary<string, LyricSnippet>(StringComparer.Ordinal);

    /// <summary>为 <paramref name="items"/> 的前 <see cref="MaxRows"/> 条生成歌词片段（按 trackId 索引）。</summary>
    public static IReadOnlyDictionary<string, LyricSnippet> Build(
        string libraryDbPath, IReadOnlyList<SearchHit> items, string query)
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
            var layers = LoadLayers(libraryDbPath, wanted);
            var snippets = new Dictionary<string, LyricSnippet>(StringComparer.Ordinal);
            foreach (var id in wanted)
            {
                if (!layers.TryGetValue(id, out var text)) continue;
                var snippet = Extract(text.Original, query, "lyrics")
                    ?? Extract(text.Translation, query, "lyricsTr")
                    ?? Extract(text.Romaji, query, "lyricsRom");
                if (snippet is not null) snippets[id] = snippet;
            }
            return snippets.Count == 0 ? Empty : snippets;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarn("indexer", $"歌词片段跳过（library.db 不可读）: {ex.Message}");
            return Empty;
        }
    }

    /// <summary>按 id 批量取歌词三层（原文 / 译文 / 音译）；无歌词的 id 不出现在结果中。</summary>
    internal static Dictionary<string, (string Original, string Translation, string Romaji)> LoadLayers(
        string libraryDbPath, List<string> ids)
    {
        var map = new Dictionary<string, (string Original, string Translation, string Romaji)>(
            StringComparer.Ordinal);
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryDbPath, readOnly: true));
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            // 写库正在提交时最多等 1s，超时按「不可读」失败开放（D.3.2 / §3.11）。
            pragma.CommandText = "PRAGMA busy_timeout = 1000";
            pragma.ExecuteNonQuery();
        }

        var hasLrc = HasLrcColumns(conn);
        using var cmd = conn.CreateCommand();
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            names.Add("@p" + i);
            cmd.Parameters.AddWithValue("@p" + i, ids[i]);
        }
        cmd.CommandText = hasLrc
            ? $"SELECT id, lyrics, lyrics_lrc, lyrics_lrc_trans, lyrics_lrc_romaji " +
              $"FROM tracks WHERE id IN ({string.Join(',', names)})"
            : $"SELECT id, lyrics FROM tracks WHERE id IN ({string.Join(',', names)})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (id.Length == 0) continue;
            var embeddedText = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var lrc = hasLrc && !reader.IsDBNull(2) ? reader.GetString(2) : "";
            var lrcTrans = hasLrc && !reader.IsDBNull(3) ? reader.GetString(3) : "";
            var lrcRomaji = hasLrc && !reader.IsDBNull(4) ? reader.GetString(4) : "";

            var embedded = LrcReader.SplitText(embeddedText);
            var sidecar = hasLrc && (lrc.Length > 0 || lrcTrans.Length > 0 || lrcRomaji.Length > 0)
                ? LrcReader.FromRawTexts(
                    lrc, lrcTrans.Length > 0 ? lrcTrans : null, lrcRomaji.Length > 0 ? lrcRomaji : null)
                : LrcReader.LrcLayers.Empty;

            map[id] = (
                IndexerEngine.JoinLayers(embedded.Original, sidecar.Original),
                IndexerEngine.JoinLayers(embedded.Translation, sidecar.Translation),
                IndexerEngine.JoinLayers(embedded.Romaji, sidecar.Romaji));
        }
        return map;
    }

    /// <summary><c>tracks</c> 是否带 <c>.lrc</c> 侧车列（旧库降级为只读 <c>lyrics</c>）。</summary>
    private static bool HasLrcColumns(SqliteConnection conn)
    {
        try
        {
            using var probe = conn.CreateCommand();
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('tracks') " +
                "WHERE name IN ('lyrics_lrc','lyrics_lrc_trans','lyrics_lrc_romaji')";
            return Convert.ToInt64(probe.ExecuteScalar() ?? 0L) >= 3;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>在 <paramref name="text"/> 里截取首个字面命中的上下文窗口；无命中返回 null。</summary>
    private static LyricSnippet? Extract(string text, string query, string field)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var ranges = MatchRanges(text, query);
        if (ranges.Count == 0) return null;

        var first = ranges[0];
        var start = Math.Max(0, first.Start - Radius);
        var end = Math.Min(text.Length, first.End + Radius);
        var prefix = start > 0 ? "…" : "";
        var suffix = end < text.Length ? "…" : "";
        var snippetText = prefix + text.Substring(start, end - start) + suffix;

        var truncated = false;
        if (snippetText.Length > MaxTextLength)
        {
            snippetText = snippetText.Substring(0, MaxTextLength);
            truncated = true;
        }

        // 原文下标 i → 片段下标 i - start + prefix.Length；越过截断末端的区间被裁掉。
        var offset = prefix.Length - start;
        var local = new List<SnippetRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.Start < start || range.End > end) continue;
            var rangeStart = range.Start + offset;
            var rangeEnd = Math.Min(range.End + offset, snippetText.Length);
            if (rangeStart >= 0 && rangeEnd > rangeStart)
                local.Add(new SnippetRange(rangeStart, rangeEnd));
        }

        return new LyricSnippet
        {
            Field = field,
            Text = snippetText,
            Ranges = local,
            Truncated = truncated,
        };
    }

    /// <summary>全部非重叠字面命中（合并相邻 / 重叠），按 start 升序。</summary>
    internal static List<SnippetRange> MatchRanges(string text, string query)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0) return new List<SnippetRange>();

        var foldedText = Fold(text);
        var variants = new List<string>(2) { normalized };
        var noSpace = normalized.Replace(" ", "");
        if (noSpace.Length > 0 && !string.Equals(noSpace, normalized, StringComparison.Ordinal))
            variants.Add(noSpace);

        var ranges = new List<SnippetRange>();
        foreach (var variant in variants)
        {
            var needle = Fold(variant);
            if (needle.Length == 0) continue;
            var from = 0;
            while (from <= foldedText.Length - needle.Length)
            {
                var index = foldedText.IndexOf(needle, from, StringComparison.Ordinal);
                if (index < 0) break;
                ranges.Add(new SnippetRange(index, index + needle.Length));
                from = index + needle.Length; // 非重叠：命中段整体高亮即可
            }
        }
        return Merge(ranges);
    }

    /// <summary>合并重叠 / 相邻区间并按 start 升序（与 Dart <c>mergeMatchRanges</c> 同义）。</summary>
    private static List<SnippetRange> Merge(List<SnippetRange> ranges)
    {
        if (ranges.Count == 0) return ranges;
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<SnippetRange>(ranges.Count) { ranges[0] };
        for (var i = 1; i < ranges.Count; i++)
        {
            var last = merged[merged.Count - 1];
            var next = ranges[i];
            if (next.Start <= last.End)
            {
                if (next.End > last.End) merged[merged.Count - 1] = new SnippetRange(last.Start, next.End);
            }
            else
            {
                merged.Add(next);
            }
        }
        return merged;
    }

    /// <summary>查询归一化：全角折叠 → 逐码元小写 → 连续空白折叠为单空格 → trim。</summary>
    private static string NormalizeQuery(string query)
    {
        var folded = Fold(query);
        var builder = new StringBuilder(folded.Length);
        var inSpace = false;
        foreach (var ch in folded)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inSpace)
                {
                    builder.Append(' ');
                    inSpace = true;
                }
                continue;
            }
            inSpace = false;
            builder.Append(ch);
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// **保长**折叠：全角 ASCII → 半角、全角空格 → 半角空格、逐码元小写。
    /// 返回串与输入等长，折叠串上的下标可直接用于原文切片（与 Dart <c>_fold</c> 同义）。
    /// </summary>
    private static string Fold(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            var unit = (int)ch;
            if (unit >= 0xFF01 && unit <= 0xFF5E) unit -= 0xFEE0;
            else if (unit == 0x3000) unit = 0x20;
            builder.Append(char.ToLowerInvariant((char)unit));
        }
        return builder.ToString();
    }
}
