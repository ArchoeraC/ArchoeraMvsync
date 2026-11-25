// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>无索引时的降级检索结果（与 <see cref="SearchResult"/> 同形的子集）。</summary>
internal sealed class FallbackResult
{
    /// <summary>本页命中（已按字段权重排序）。</summary>
    public List<SearchHit> Items { get; } = new();

    /// <summary>全量匹配数（等价旧 Dart <c>countTracks</c>）。</summary>
    public long Total { get; set; }

    /// <summary>高亮区间（仅 <c>highlight=true</c>；键为 trackId，最多前 300 条）。</summary>
    public IReadOnlyDictionary<string, TrackHighlight> Highlights { get; set; }
        = new Dictionary<string, TrackHighlight>(StringComparer.Ordinal);

    /// <summary>歌词片段（仅 <c>snippet=true</c>；键为 trackId，最多前 300 条）。</summary>
    public IReadOnlyDictionary<string, LyricSnippet> Snippets { get; set; }
        = new Dictionary<string, LyricSnippet>(StringComparer.Ordinal);
}

/// <summary>
/// 索引关闭 / 未就绪时的降级检索（T2.12，ABI <c>era_search_fallback</c>）：
/// 只读打开 <c>library.db</c>，按**参数化 LIKE** 匹配元数据，不走搜索索引。
///
/// <para><b>匹配语义与旧 Dart 路径一致</b>（<c>tracks_db.dart</c> 的
/// <c>title LIKE ? OR artists LIKE ? OR album LIKE ?</c>）：直接对原始列做
/// <c>%query%</c> 匹配，不解析 artists / album 的 JSON；查询为空则返回整库。</para>
///
/// <para><b>排序</b>用简化的字段权重 <c>title(3) &gt; artist(2) &gt; album(1)</c>，
/// 同权重按 <c>title</c> / <c>id</c> 升序（全部为 title 命中时与旧
/// <c>ORDER BY title</c> 一致）。<c>score</c> 即该权重（fallback 方案下越大越相关）。</para>
///
/// <para>不产出 <c>explain</c> / <c>trace</c>；<c>highlight</c> / <c>snippet</c>
/// 复用正式查询路径的 <see cref="SearchHighlight"/> / <see cref="SearchSnippet"/>。</para>
/// </summary>
internal static class SearchFallback
{
    /// <summary>降级方案 id（响应里的 <c>scheme</c>）。</summary>
    public const string SchemeId = "fallback@1";

    /// <summary>命中归因名（Dart 未知值回落 lexical，不影响解码）。</summary>
    public const string PassName = "fallback";

    private const double TitleWeight = 3;
    private const double ArtistWeight = 2;
    private const double AlbumWeight = 1;

    /// <summary>
    /// 执行一次降级检索。库不存在 / 不可读由调用方（FFI 层）判定为 2 / 5；
    /// 这里只负责查询与高亮 / 片段派生。
    /// </summary>
    public static FallbackResult Execute(string libraryDbPath, SearchRequest request)
    {
        var query = request.Query;
        var hasQuery = query.Length > 0;
        var pattern = "%" + query + "%";

        var result = new FallbackResult();
        using var conn = new SqliteConnection(LibraryDb.ConnectionString(libraryDbPath, readOnly: true));
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            // 写库正在提交时最多等 1s（与高亮 / 片段同口径），超时按 IO 失败上报。
            pragma.CommandText = "PRAGMA busy_timeout = 1000";
            pragma.ExecuteNonQuery();
        }

        result.Total = Count(conn, hasQuery, pattern);

        using (var cmd = conn.CreateCommand())
        {
            if (hasQuery)
            {
                cmd.CommandText =
                    "SELECT id, " +
                    "CASE WHEN title LIKE @p THEN 3 WHEN artists LIKE @p THEN 2 ELSE 1 END AS weight " +
                    "FROM tracks " +
                    "WHERE title LIKE @p OR artists LIKE @p OR album LIKE @p " +
                    "ORDER BY weight DESC, title, id LIMIT @limit OFFSET @offset";
                cmd.Parameters.AddWithValue("@p", pattern);
            }
            else
            {
                cmd.CommandText =
                    "SELECT id, 0 AS weight FROM tracks " +
                    "ORDER BY title, id LIMIT @limit OFFSET @offset";
            }
            cmd.Parameters.AddWithValue("@limit", request.Limit);
            cmd.Parameters.AddWithValue("@offset", request.Offset);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var id = reader.GetString(0);
                if (id.Length == 0) continue;
                var weight = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                result.Items.Add(new SearchHit(id, weight, PassName));
            }
        }

        if (result.Items.Count == 0) return result;
        result.Highlights = SearchHighlight.Build(
            libraryDbPath, result.Items, query, includeLyrics: request.Snippet);
        if (request.Snippet)
            result.Snippets = SearchSnippet.Build(libraryDbPath, result.Items, query);
        return result;
    }

    /// <summary>全量匹配数（空查询为整库），不受 <c>limit</c> / <c>offset</c> 影响。</summary>
    private static long Count(SqliteConnection conn, bool hasQuery, string pattern)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasQuery
            ? "SELECT COUNT(*) FROM tracks " +
              "WHERE title LIKE @p OR artists LIKE @p OR album LIKE @p"
            : "SELECT COUNT(*) FROM tracks";
        if (hasQuery) cmd.Parameters.AddWithValue("@p", pattern);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}

/// <summary>一页曲目的高亮 / 片段集合（T2.11 的 <c>era_search_decorate</c> 结果）。</summary>
internal sealed class DecorationResult
{
    public IReadOnlyDictionary<string, TrackHighlight> Highlights { get; init; }
        = new Dictionary<string, TrackHighlight>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LyricSnippet> Snippets { get; init; }
        = new Dictionary<string, LyricSnippet>(StringComparer.Ordinal);
}

/// <summary>
/// 按 trackId 批量计算高亮 / 片段（T2.11，ABI <c>era_search_decorate</c>）：
/// 只读 <c>library.db</c>，不重跑排名、不改召回。供 UI 分页时对当前页（≤300 行）补充
/// 高亮与歌词片段；结果与正式查询路径的 <see cref="SearchHighlight"/> /
/// <see cref="SearchSnippet"/> 完全一致（两者本身失败开放）。
/// </summary>
internal static class SearchDecorations
{
    /// <summary>单次最多装饰多少条（与分页容量 / <see cref="SearchHighlight.MaxRows"/> 同阶）。</summary>
    public const int MaxIds = SearchHighlight.MaxRows;

    public static DecorationResult Build(
        string libraryDbPath, string query, IReadOnlyList<string> trackIds, bool includeLyrics)
    {
        if (trackIds.Count == 0 || string.IsNullOrEmpty(query) || !File.Exists(libraryDbPath))
            return new DecorationResult();

        var items = new List<SearchHit>(trackIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in trackIds)
        {
            if (seen.Add(id)) items.Add(new SearchHit(id, 0, "decorate"));
        }
        if (items.Count == 0) return new DecorationResult();

        return new DecorationResult
        {
            Highlights = SearchHighlight.Build(libraryDbPath, items, query, includeLyrics),
            Snippets = includeLyrics
                ? SearchSnippet.Build(libraryDbPath, items, query)
                : new Dictionary<string, LyricSnippet>(StringComparer.Ordinal),
        };
    }
}
