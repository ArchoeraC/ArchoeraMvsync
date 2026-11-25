// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer;

/// <summary>单次查询请求（FFI JSON 反序列化后的强类型视图）。</summary>
internal sealed class SearchRequest
{
    public string Query { get; init; } = "";

    /// <summary>只搜元数据（标题 / 歌手 / 专辑 / 文件名），不触歌词 / 译文 / 音译。</summary>
    public bool Metadata { get; init; }

    /// <summary>
    /// 只搜歌词字段族（原文 / 译文 / 音译 / 歌词拼音 / 歌词首字母），不触元数据。
    /// 与 <see cref="Metadata"/> 互斥；同时为真时按元数据语义（调用方保证不出现）。
    /// </summary>
    public bool Lyrics { get; init; }

    /// <summary>请求的方案 id（null / 空 = 默认方案；未知值回退默认并保留在响应里）。</summary>
    public string? Scheme { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }

    /// <summary>启用模糊趟（拉丁编辑距离 + 中文删除 / 换位）；兜底趟不受此开关影响。</summary>
    public bool Fuzzy { get; init; }

    /// <summary>命中数达到该值即停止继续兜底（默认 1：主趟有命中就不再兜底）。</summary>
    public int MinExact { get; init; } = 1;

    /// <summary>
    /// 返回 <c>explain</c>（前 50 条命中的归因）与 <c>trace</c>（执行轨迹）。
    /// 默认关闭：字段归因要对每个命中逐列探测，属诊断开销。
    /// </summary>
    public bool Explain { get; init; }

    /// <summary>
    /// 返回前 300 条命中的歌词片段（T2.8）。查询期只读打开源曲库取原文，不写回索引。
    /// </summary>
    public bool Snippet { get; init; }

    /// <summary>
    /// 返回前 300 条命中的高亮区间（T2.7）。字面 / 读音 / 首字母均由本层计算；
    /// 歌词高亮只在 <see cref="Snippet"/> 为真时读取原文。
    /// </summary>
    public bool Highlight { get; init; }

    /// <summary>
    /// 上一次响应的 <c>next</c>（不透明 base64url 游标，T2.10）。
    /// 已在校验期解码；<c>null</c> 表示第一页。
    /// </summary>
    public SearchCursor? Cursor { get; init; }

    /// <summary>游标原串（仅进缓存键，保证每页独立缓存）。</summary>
    public string? CursorRaw { get; init; }

    /// <summary><c>total</c> 计数模式（默认 <see cref="SearchTotalMode.None"/>）。</summary>
    public SearchTotalMode TotalMode { get; init; }

    /// <summary>
    /// 候选集上限（**历史参数，已不再裁剪候选**）。
    ///
    /// 保留字段仅为请求 JSON 兼容；EraStore 查询路径已忽略它——
    /// 原 FTS 实现会让 bm25 静默返回 0、排序整体失效（见设计文档 §4.5）。
    /// </summary>
    public int CandidateCap { get; init; } = DefaultCandidateCap;

    /// <summary>历史候选集上限（请求兼容字段的默认值；查询路径已不再裁剪候选）。</summary>
    public const int DefaultCandidateCap = 5000;

    /// <summary>
    /// 是否使用结果 LRU 缓存。生产默认开；benchmark / 回放需要测真实执行时间时置 false，
    /// 否则重复同一查询会命中缓存（微秒级），测到的不是查询路径。
    /// </summary>
    public bool Cache { get; init; } = true;

    public static SearchRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("request_json 必须是 JSON 对象");

        var query = root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() ?? ""
            : "";
        var scope = root.TryGetProperty("scope", out var scopeElement)
            && scopeElement.ValueKind == JsonValueKind.String
            ? scopeElement.GetString()
            : null;
        var metadata = string.Equals(scope, "metadata", StringComparison.OrdinalIgnoreCase);
        var lyrics = string.Equals(scope, "lyrics", StringComparison.OrdinalIgnoreCase);
        return new SearchRequest
        {
            Query = query,
            Metadata = metadata,
            Lyrics = lyrics && !metadata,
            Scheme = root.TryGetProperty("scheme", out var schemeElement)
                && schemeElement.ValueKind == JsonValueKind.String
                ? schemeElement.GetString()
                : null,
            Limit = ReadInt(root, "limit", 200),
            Offset = ReadInt(root, "offset", 0),
            Fuzzy = ReadBool(root, "fuzzy", false),
            MinExact = ReadInt(root, "minExact", 1),
            CandidateCap = ReadInt(root, "candidateCap", DefaultCandidateCap),
            Explain = ReadBool(root, "explain", false),
            Snippet = ReadBool(root, "snippet", false),
            Highlight = ReadBool(root, "highlight", false),
        };
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}

/// <summary>
/// 单条命中（bm25 分数越小越相关）。
///
/// <paramref name="Pass"/> 是**命中原由归因**（精度审计 §6.3 / M-7）：产生当前证据的步骤名。
/// S2 起合并规则改为「同 track 取最高 kind 的证据」（见 <see cref="SearchRanker.Merge"/>），
/// 低优先趟不再覆盖高优先趟，但会在 <c>explain.provenance</c>（T2.7）保留来源。
/// </summary>
internal readonly record struct SearchHit(string TrackId, double Score, string Pass);

/// <summary>命中趟次名（与 <see cref="PassCounts"/> 的字段一一对应，供 UI / 日志归因）。</summary>
internal static class SearchPassNames
{
    public const string Lexical = "lexical";
    public const string PinyinLoose = "pinyinLoose";
    public const string Reading = "reading";
    public const string CrossLingual = "crossLingual";
    public const string Fuzzy = "fuzzy";
    public const string CjkFuzzy = "cjkFuzzy";

    /// <summary>typo2（Damerau ≤2，仅 recall@1）。计数并入 <see cref="PassCounts.Fuzzy"/>。</summary>
    public const string Typo2 = "typo2";

    /// <summary>
    /// **丢词降级趟**：多词查询命中不足时，从末尾开始逐个丢弃 query 词后重新召回。
    ///
    /// 与 <see cref="Lexical"/> 同属「字面匹配」，只是少了约束——**方向最安全**的放宽
    /// （指标见 <c>docs/local-search-indexer-references.md</c> §6.2）。
    /// </summary>
    public const string DropTokens = "dropTokens";
}

/// <summary>本次查询实际执行的兜底趟（0 / 1；用于诊断与回归计数）。</summary>
internal sealed class PassCounts
{
    public int DropTokens { get; set; }
    public int PinyinLoose { get; set; }
    public int Reading { get; set; }
    public int CrossLingual { get; set; }
    public int Fuzzy { get; set; }
    public int CjkFuzzy { get; set; }
}

/// <summary>查询结果（按桶序排序后的 top-k；S2 起合并规则见 <see cref="SearchRanker"/>）。</summary>
internal sealed class SearchResult
{
    public List<SearchHit> Items { get; } = new();
    public PassCounts Passes { get; } = new();

    /// <summary>实际生效的方案 id（能力协商 / 降级后的结果）。</summary>
    public string SchemeId { get; set; } = SearchPolicy.SchemeId;

    /// <summary>缺失能力（非空表示发生了降级；S2 由 v7 保守能力集推导，S3 接 Manifest）。</summary>
    public IReadOnlyList<string> MissingCaps { get; set; } = Array.Empty<string>();

    /// <summary>命中归因（仅 <c>explain=true</c>；键为 trackId，最多前 50 条）。</summary>
    public IReadOnlyDictionary<string, SearchExplanation> Explanations { get; set; }
        = new Dictionary<string, SearchExplanation>(StringComparer.Ordinal);

    /// <summary>歌词片段（仅 <c>snippet=true</c>；键为 trackId，最多前 300 条）。</summary>
    public IReadOnlyDictionary<string, LyricSnippet> Snippets { get; set; }
        = new Dictionary<string, LyricSnippet>(StringComparer.Ordinal);

    /// <summary>高亮区间（仅 <c>highlight=true</c>；键为 trackId，最多前 300 条）。</summary>
    public IReadOnlyDictionary<string, TrackHighlight> Highlights { get; set; }
        = new Dictionary<string, TrackHighlight>(StringComparer.Ordinal);

    /// <summary>
    /// 命中总数（T2.10）：<c>total=none</c> 时恒 -1；<c>capped</c> 最多
    /// <see cref="SearchPaging.CappedTotal"/>；<c>exact</c> 为合并去重后的候选数。
    /// </summary>
    public long Total { get; set; } = -1;

    /// <summary><c>total=exact</c> 且完整计数成功（S2 无超时，故请求 exact 即 true）。</summary>
    public bool TotalExact { get; set; }

    /// <summary>游标失效（世代 / 方案失配或位置已不存在），UI 应清空列表重查第一页。</summary>
    public bool Restart { get; set; }

    /// <summary>下一页游标（不透明）；<c>null</c> 表示没有更多结果。</summary>
    public string? Next { get; set; }

    /// <summary>执行轨迹（仅 <c>explain=true</c>）。</summary>
    public SearchTrace? Trace { get; set; }

    /// <summary>
    /// 只保留源曲库里仍存在的曲目（T2.9）。不改变缓存内容，每次返回时派生新结果。
    /// 过滤后可能少于 <c>limit</c>：陈旧曲目不做回填（与「先过滤再取 top-k」的差异）。
    /// </summary>
    internal SearchResult WithoutTracks(IReadOnlySet<string> alive)
    {
        var items = Items.Where(hit => alive.Contains(hit.TrackId)).ToList();
        var explanations = Explanations
            .Where(pair => alive.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var snippets = Snippets
            .Where(pair => alive.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var highlights = Highlights
            .Where(pair => alive.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var filtered = new SearchResult
        {
            SchemeId = SchemeId,
            MissingCaps = MissingCaps,
            Explanations = explanations,
            Snippets = snippets,
            Highlights = highlights,
            Trace = Trace,
            Total = Total,
            TotalExact = TotalExact,
            Restart = Restart,
            Next = Next,
        };
        filtered.Items.AddRange(items);
        filtered.Passes.DropTokens = Passes.DropTokens;
        filtered.Passes.PinyinLoose = Passes.PinyinLoose;
        filtered.Passes.Reading = Passes.Reading;
        filtered.Passes.CrossLingual = Passes.CrossLingual;
        filtered.Passes.Fuzzy = Passes.Fuzzy;
        filtered.Passes.CjkFuzzy = Passes.CjkFuzzy;
        return filtered;
    }
}

/// <summary>
/// 只读查询会话：缓存一个 EraStore 读取器（按 <c>search.era/</c> 路径复用），
/// 在库内执行「主趟 → 拼音宽松 → 日文读音 → 跨语言 → 模糊 → 中文容错」六段渐进查询。
/// S1 起这六趟由 <see cref="SearchPlanCompiler"/> 编译成有序 <see cref="Step"/>，
/// <see cref="ExecuteUncached"/> 只保留 Plan 一条执行路径。
///
/// <para><b>T3.0</b>：查询期不再打开 <c>search.db</c>；analyzer / 能力 / 选项全部来自
/// EraStore Manifest。源曲库 <c>library.db</c> 只做 snippet / highlight / 陈旧曲目过滤的
/// 只读访问。EraStore 缺失（旧索引）时查询返回空结果，索引器会在下一次运行强制全量重建。</para>
///
/// 所有调用在静态锁下串行——Dart 侧只在 UI isolate 查询，锁只用于防御。
/// </summary>
internal sealed class SearchSession : IDisposable
{
    private static readonly object Sync = new();
    private static SearchSession? _cached;

    private static long _cacheHits;
    private static long _cacheMisses;

    private readonly EraStore.StoreReader? _era;

    /// <summary>构造时 CURRENT 指向的文件名（null = 无 EraStore）；用于每次查询前检测重建。</summary>
    private readonly string? _eraName;

    /// <summary>构造时的提交世代（T3.10 增量写同一文件）；用于检测同文件增量更新。</summary>
    private readonly ulong _eraGen;

    private readonly string _path;
    private readonly string? _libraryDbPath;

    /// <summary>查询元数据（来自 EraStore Manifest；缺失时用保守默认值）。</summary>
    private readonly StoreMeta _meta;

    /// <summary>索引能力集合（Manifest 写入优先，旧文件按 <see cref="IndexCapabilities.FromMeta"/> 推导）。</summary>
    private readonly IReadOnlySet<string> _capabilities;

    /// <summary>模糊层词典（首次需要时从 EraStore 词典扫描并缓存）。</summary>
    private List<string>? _fuzzyVocabulary;

    /// <summary>
    /// 精确度复核用的段视图缓存（SegmentId → 段）。只在字面元数据步（Meta / scope=all 的
    /// Mixed）、单步命中 ≤ 2k 时读 DocStore 的归一列；与 <see cref="_fuzzyVocabulary"/>
    /// 一样随会话（提交世代）失效。
    /// </summary>
    private Dictionary<ushort, EraStore.SegmentReaders>? _normSegments;

    /// <summary>查询结果 LRU 容量（优化计划 §4-2：128 条 top-k）。</summary>
    internal const int CacheCapacity = 128;

    /// <summary>单条结果超过该条数时不入缓存（防大 limit 的宽命中把内存放大到百 MB 级）。</summary>
    internal const int MaxCachedItems = 500;

    /// <summary>EraStore 索引目录名（与 <see cref="IndexerEngine"/> 写入侧共用同一常量）。</summary>

    private readonly Dictionary<string, LinkedListNode<CachedQuery>> _cacheIndex =
        new(StringComparer.Ordinal);

    /// <summary>LRU 顺序：头部最新、尾部最旧。</summary>
    private readonly LinkedList<CachedQuery> _cacheOrder = new();

    private SearchSession(string path, string? libraryDbPath)
    {
        _path = Path.GetFullPath(path);
        _libraryDbPath = string.IsNullOrWhiteSpace(libraryDbPath)
            ? null
            : Path.GetFullPath(libraryDbPath);

        // T3.0: EraStore 是唯一查询路径（search.db 同目录下的 search.era/，由 CURRENT 指向最新段文件）。
        // 缺失 / 损坏时结果为空；索引器会在下一次运行强制全量重建（见 IndexerEngine）。
        var eraDir = Path.Combine(Path.GetDirectoryName(_path) ?? ".", IndexerEngine.EraStoreDirName);
        _eraName = EraStore.CurrentPointer.Read(eraDir);
        var eraFile = _eraName is { } eraName ? Path.Combine(eraDir, eraName) : null;
        if (eraFile is not null && File.Exists(eraFile))
        {
            try
            {
                _era = new EraStore.StoreReader(eraFile);
            }
            catch (Exception ex)
            {
                Log.LogWarn("indexer", $"EraStore 索引无法打开，查询返回空结果: {ex.Message}");
                _era = null;
            }
        }
        else
        {
            _era = null;
        }

        _meta = _era is null ? ConservativeMeta() : MetaFromManifest(_era.Manifest);
        // 能力 = Manifest 声明（旧文件按 meta 推导）− 数据包降级（§5.5.6）：
        // 缺 def_* 就不再走跨语言步，缺读音表就不再走读音步，方案协商会沿降级链下落。
        var capabilities = new HashSet<string>(
            _era is not null && _era.Manifest.Capabilities.Count > 0
                ? _era.Manifest.Capabilities
                : (IEnumerable<string>)IndexCapabilities.FromMeta(_meta),
            StringComparer.Ordinal);
        foreach (var degraded in DataPack.Current.DegradedCapabilities)
            capabilities.Remove(degraded);
        _capabilities = capabilities;
        _eraGen = _era?.Gen ?? 0;

    }

    /// <summary>EraStore 缺失时的保守元数据：不裁剪任何语言趟，analyzer / 世代按 0。</summary>
    private static StoreMeta ConservativeMeta() => new()
    {
        SchemaVersion = EraStore.Manifest.CurrentMetaVersion,
        AppliedGeneration = 0,
    };

    /// <summary>把 Manifest 的查询元数据还原成 <see cref="StoreMeta"/>（编译 / 能力协商共用）。</summary>
    internal static StoreMeta MetaFromManifest(EraStore.Manifest manifest)
    {
        var bits = manifest.OptionsBits;
        bool Has(int bit) => (bits & bit) != 0;
        return new StoreMeta
        {
            SchemaVersion = EraStore.Manifest.CurrentMetaVersion,
            AnalyzerVersion = manifest.Anchors.AnalyzerVersion,
            IndexLrc = Has(EraStore.ManifestOptionsBits.IndexLrc),
            Pinyin = Has(EraStore.ManifestOptionsBits.Pinyin),
            RomajiLyrics = Has(EraStore.ManifestOptionsBits.RomajiLyrics),
            InitialsLyrics = Has(EraStore.ManifestOptionsBits.InitialsLyrics),
            AppliedGeneration = (long)manifest.Gen,
            HasJapanese = Has(EraStore.ManifestOptionsBits.HasJapanese),
            HasKorean = Has(EraStore.ManifestOptionsBits.HasKorean),
            HasHan = Has(EraStore.ManifestOptionsBits.HasHan),
            HasLatin = Has(EraStore.ManifestOptionsBits.HasLatin),
        };
    }

    /// <summary>释放缓存会话（重建索引 / 删除索引 / 退出查询前调用）。</summary>
    public static void CloseCached()
    {
        lock (Sync)
        {
            _cached?.Dispose();
            _cached = null;
        }
    }

    /// <summary>测试接缝：本进程累计的缓存命中次数（<see cref="DebugResetCacheStats"/> 清零）。</summary>
    internal static long DebugCacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>测试接缝：本进程累计的缓存未命中次数。</summary>
    internal static long DebugCacheMisses => Interlocked.Read(ref _cacheMisses);

    /// <summary>测试接缝：当前缓存的条目数（无会话时为 0）。</summary>
    internal static int DebugCacheCount
    {
        get
        {
            lock (Sync) return _cached?._cacheIndex.Count ?? 0;
        }
    }

    /// <summary>测试接缝：清零命中 / 未命中计数（不清空缓存内容）。</summary>
    internal static void DebugResetCacheStats()
    {
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
    }

    /// <summary>执行一次查询；索引路径或源曲库路径变化时自动切换会话。</summary>
    public static SearchResult Run(string path, SearchRequest request, string? libraryDbPath = null)
    {
        // §5.5.6 基础归一化表缺失 → 拒绝查询（不是「悄悄少召回」）。查询入口也要拦，
        // 否则旧索引会继续被读，用户会看到与新数据包不一致的结果。
        DataPack.Current.ThrowIfFoundationMissing();
        lock (Sync)
        {
            var full = Path.GetFullPath(path);
            var library = string.IsNullOrWhiteSpace(libraryDbPath)
                ? null
                : Path.GetFullPath(libraryDbPath);
            // EraStore 每次全量重建都会换文件名（CURRENT 随之更新）；增量写入则复用同一文件、
            // 只推进提交世代。查询前同时比对指针与世代，避免继续读旧快照。
            var eraDir = Path.Combine(Path.GetDirectoryName(full) ?? ".", IndexerEngine.EraStoreDirName);
            var eraName = EraStore.CurrentPointer.Read(eraDir);
            var eraGen = eraName is not null
                && EraStore.StoreReader.TryReadCommitGen(Path.Combine(eraDir, eraName), out var gen)
                ? gen
                : 0UL;
            if (_cached is null
                || !string.Equals(_cached._path, full, StringComparison.Ordinal)
                || !string.Equals(_cached._libraryDbPath, library, StringComparison.Ordinal)
                || !string.Equals(_cached._eraName, eraName, StringComparison.Ordinal)
                || _cached._eraGen != eraGen)
            {
                _cached?.Dispose();
                _cached = new SearchSession(full, library);
            }
            return _cached.Execute(request);
        }
    }

    /// <summary>
    /// 执行一次查询（带结果 LRU 缓存，见优化计划 §4-2）。
    ///
    /// 输入去抖 250ms + 退格重放会产生同一查询的连续重复调用；命中直接返回上次的
    /// top-k（含 passes），不再走 FTS。缓存由会话实例持有，重建 / 删除 / 关闭索引时
    /// 都由 <see cref="CloseCached"/> 连同会话一起丢弃，无需额外失效逻辑。
    /// 返回的 <see cref="SearchResult"/> 视作只读：调用方（FFI 序列化 / 测试）不得修改。
    /// 线程安全：所有调用在 <see cref="Run"/> 的静态锁下串行，实例内无需再加锁。
    /// </summary>
    private SearchResult Execute(SearchRequest request)
    {
        var useCache = request.Cache;
        var key = BuildCacheKey(request);
        if (useCache && _cacheIndex.TryGetValue(key, out var node))
        {
            _cacheOrder.Remove(node);
            _cacheOrder.AddFirst(node);
            Interlocked.Increment(ref _cacheHits);
            return ApplyStaleFilter(node.Value.Result);
        }

        Interlocked.Increment(ref _cacheMisses);
        var result = ExecuteUncached(request);
        if (!useCache || result.Items.Count > MaxCachedItems) return ApplyStaleFilter(result);

        _cacheIndex[key] = _cacheOrder.AddFirst(new CachedQuery(key, result));
        if (_cacheIndex.Count > CacheCapacity)
        {
            var oldest = _cacheOrder.Last!;
            _cacheOrder.RemoveLast();
            _cacheIndex.Remove(oldest.Value.Key);
        }
        return ApplyStaleFilter(result);
    }

    /// <summary>
    /// 查询期过滤源曲库已删除的曲目（T2.9 / N12）：缓存里保留原始 top-k，
    /// 每次返回时按 <c>library.db</c> 当前内容派生，避免删除后仍吐幽灵结果。
    /// 库不存在 / 不可读 / 表缺失时**失败开放**（不过滤），只记 debug 日志。
    /// </summary>
    private SearchResult ApplyStaleFilter(SearchResult result)
    {
        if (_libraryDbPath is null || result.Items.Count == 0) return result;
        if (!File.Exists(_libraryDbPath)) return result;

        try
        {
            var wanted = result.Items
                .Select(hit => hit.TrackId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var alive = new HashSet<string>(StringComparer.Ordinal);
            using var conn = new SqliteConnection(
                LibraryDb.ConnectionString(_libraryDbPath, readOnly: true));
            conn.Open();

            // 分批 IN：SQLite 变量上限保守取 400，结果页通常一批就够。
            for (var start = 0; start < wanted.Count; start += 400)
            {
                var chunk = wanted.Skip(start).Take(400).ToList();
                using var cmd = conn.CreateCommand();
                var names = new List<string>(chunk.Count);
                for (var i = 0; i < chunk.Count; i++)
                {
                    names.Add("@p" + i);
                    cmd.Parameters.AddWithValue("@p" + i, chunk[i]);
                }
                cmd.CommandText = $"SELECT id FROM tracks WHERE id IN ({string.Join(',', names)})";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) alive.Add(reader.GetString(0));
            }

            return alive.Count == wanted.Count ? result : result.WithoutTracks(alive);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarn("indexer", $"陈旧曲目过滤跳过（library.db 不可读）: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// 缓存键：查询串（trim + 小写）+ 全部影响结果的请求参数 + 会话的 AnalyzerVersion。
    ///
    /// 不做 <see cref="Analyzer"/> 的完整归一化：中文容错趟的变体是先从**原串**取
    /// CJK 子序列再逐个分析的（见 <c>SearchQuery.CjkVariants</c>），深归一化会让两个
    /// 原串落到同一键却可能产出不同变体。trim + 小写只消除对结果无影响的差异
    /// （两侧编译都会归一化 / 去空白）。
    /// </summary>
    private string BuildCacheKey(SearchRequest request)
        => string.Join('\u0001',
            request.Query.Trim().ToLowerInvariant(),
            request.Metadata ? "meta" : request.Lyrics ? "lyrics" : "all",
            request.Scheme ?? "",
            request.Limit.ToString(CultureInfo.InvariantCulture),
            request.Offset.ToString(CultureInfo.InvariantCulture),
            request.Fuzzy ? "fuzzy" : "exact",
            request.MinExact.ToString(CultureInfo.InvariantCulture),
            request.CandidateCap.ToString(CultureInfo.InvariantCulture),
            request.Explain ? "explain" : "plain",
            request.Snippet ? "snippet" : "nosnippet",
            request.Highlight ? "highlight" : "nohighlight",
            request.CursorRaw ?? "",
            ((int)request.TotalMode).ToString(CultureInfo.InvariantCulture),
            _meta.AnalyzerVersion.ToString(CultureInfo.InvariantCulture));

    private SearchResult ExecuteUncached(SearchRequest request)
    {
        var result = new SearchResult();
        var tokens = Analyzer.AnalyzeQuery(request.Query);
        var resolved = SchemeRegistry.Resolve(request.Scheme, _capabilities);
        result.SchemeId = resolved.Scheme.Id;
        // explain / 健康页的 `missing`：方案协商缺的能力 + 数据包降级的能力（§5.5.6）。
        var missing = resolved.Missing;
        var degraded = DataPack.Current.DegradedCapabilities;
        if (degraded.Count > 0)
        {
            missing = missing.Concat(degraded)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        result.MissingCaps = missing;
        var plan = SearchPlanCompiler.Compile(request, tokens, _meta, resolved.Scheme);
        // N15 / RP §3.5.7：计划期收缩只在超长查询上触发，记一条 debug 便于回放核对。
        if (tokens.VisibleCharCount >= plan.Policy.MaxPlanQueryCodePoints)
        {
            Log.LogDebug("indexer",
                $"查询超长（{tokens.VisibleCharCount} 码点 ≥ {plan.Policy.MaxPlanQueryCodePoints}）：只跑字面主趟");
        }

        // S2 / T2.1：不再「按趟次先到者保留」，而是把每步命中摊成带证据的候选，
        // 同 track 合并取最高 kind，最后一次桶序排序取 top-k（唯一排序入口）。
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        // 候选召回上限（每步）：
        // - 分页 / 计数请求必须让**每一页看到同一候选集**，否则跨页排序会漂移；
        // - total=exact 要数全量 → 不截断；total=capped 只需数到 10001 → 每步取到该上限；
        // - 普通查询保持旧成本特征（limit + offset）。
        var take = request.Cursor is not null || request.TotalMode == SearchTotalMode.Exact
            ? -1
            : request.TotalMode == SearchTotalMode.Capped
                ? SearchPaging.CappedTotal + 1
                : request.Limit <= 0 ? -1 : (int)Math.Min(int.MaxValue,
                    (long)request.Limit + Math.Max(0, request.Offset));

        // explain 只在请求开启时收集：表达式按 pass 留档，供结果侧逐列归因（T2.3 / T2.7）。
        var explain = request.Explain;
        var probesByPass = explain
            ? new Dictionary<string, List<StepProbe>>(StringComparer.Ordinal)
            : null;
        var stepTraces = explain ? new List<StepTrace>() : null;
        var skippedTraces = explain ? new List<TraceSkip>() : null;
        var confidenceTraces = explain ? new List<ConfidenceFilterTrace>() : null;

        void Trace(Step traced, int returned, string state, string? reason, double ms)
        {
            if (stepTraces is null) return;
            stepTraces.Add(new StepTrace
            {
                I = stepTraces.Count,
                Step = traced.Id,
                Kind = SearchExplainBuilder.KindName(traced.Kind),
                FieldId = SearchExplainBuilder.FamilyId(traced.Family),
                Op = SearchExplainBuilder.OpName(traced.Op),
                Returned = returned,
                State = state,
                Reason = reason,
                Ms = ms,
            });
            if (reason is not null)
                skippedTraces!.Add(new TraceSkip { I = stepTraces.Count - 1, Reason = reason });
        }

        // 步骤门（RequiresMainMiss / RequiresFuzzy）保留：S2 只换排序与合并规则，
        // 不改召回范围（StepDominated 剪枝随方案注册表在 T2.4 再启用）。
        var steps = plan.Steps;
        var dropsOrdered = false;
        byte[]? exactQuery = null;
        var exactQueryComputed = false;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Gate.RequiresMainMiss && candidates.Count >= request.MinExact)
            {
                Trace(step, 0, "skipped", "min_exact", 0);
                continue;
            }
            if (step.Gate.RequiresFuzzy && !request.Fuzzy)
            {
                Trace(step, 0, "skipped", "fuzzy_off", 0);
                continue;
            }

            // 丢词候选按 title / artist 命中分布重排：只在门真正打开、且遇到第一个
            // drop step 时探测一次（保持旧实现的查询成本特征）。
            if (step.DropIndex >= 0 && !dropsOrdered)
            {
                dropsOrdered = true;
                var (titleCounts, artistCounts) =
                    ProbeFieldAffinity(request.Query, plan.DropCandidates.Count);
                steps = SearchPlanCompiler.OrderDropSteps(plan, titleCounts, artistCounts);
                step = steps[index];
            }

            var expression = SearchPlanCompiler.ResolveDeferredExpression(
                step, tokens, request.Metadata, request.Lyrics, _meta.InitialsLyrics,
                LoadEraVocabulary);

            // 旧 pass 计数：字面 / 丢词 / 拼音 / 读音 / 跨语言只在表达式非空时计；
            // 模糊趟只要执行就计（表达式由词表延迟生成）；中文容错按 step 声明计。
            if (step.NeedsFuzzyVocabulary || expression is not null || step.CountPassOnNullExpression)
                CountPass(result.Passes, step.Pass);
            if (expression is null)
            {
                Trace(step, 0, "skipped", "empty_expression", 0);
                continue;
            }

            if (probesByPass is not null)
            {
                if (!probesByPass.TryGetValue(step.Pass, out var list))
                    probesByPass[step.Pass] = list = new List<StepProbe>(1);
                list.Add(new StepProbe(step.Terms.ToArray(), expression));
            }

            var watch = explain ? Stopwatch.StartNew() : null;
            var evidence = SearchRanker.FromStep(step, plan.Terms.Count);
            var confidencePolicy = ConfidencePolicyFor(step, request);

            // 字面精确度复核（T3.11）：表达式可能命中 title / artist / album 字面列、
            // 且搜索文本就是查询原文的步骤（Meta / scope=all 的 Mixed）才复核；
            // 查询归一形每请求只算一次（惰性）。宽命中步由 ExecuteMatch 按 cap 跳过。
            // 歌词 / 读音 / 拼音 / 首字母 / 跨语言 / 容错步保持「歌词 + exactness=none」的保守口径。
            var checkExactness = MayMatchMetadataLiteral(step, request);
            if (checkExactness && !exactQueryComputed)
            {
                exactQueryComputed = true;
                exactQuery = SearchExactness.NormalizeQuery(request.Query);
            }

            var matched = ExecuteMatch(
                expression, take, step, evidence, step.ReadingSequences,
                checkExactness ? exactQuery : null, confidencePolicy);
            foreach (var candidate in matched)
            {
                if (candidates.TryGetValue(candidate.TrackId, out var existing))
                    SearchRanker.Merge(existing, candidate);
                else
                    candidates.Add(candidate.TrackId, candidate);
            }
            watch?.Stop();
            Trace(step, matched.Count, "ran", null, watch?.Elapsed.TotalMilliseconds ?? 0);
            if (confidenceTraces is not null && confidencePolicy != EraStore.LyricsConfidencePolicy.None)
            {
                confidenceTraces.Add(new ConfidenceFilterTrace
                {
                    I = stepTraces!.Count - 1,
                    Policy = confidencePolicy == EraStore.LyricsConfidencePolicy.HighOnly
                        ? "crossLingual"
                        : "all",
                    Layers = confidencePolicy == EraStore.LyricsConfidencePolicy.HighOnly
                        ? new[] { "low", "medium" }
                        : new[] { "low" },
                });
            }
        }

        var ordered = new List<Candidate>(candidates.Values);
        SearchRanker.Sort(ordered);

        // total（T2.10）：none → -1；capped → 最多 10001；exact → 合并去重后的全量。
        switch (request.TotalMode)
        {
            case SearchTotalMode.Capped:
                result.Total = Math.Min(ordered.Count, SearchPaging.CappedTotal);
                result.TotalExact = false;
                break;
            case SearchTotalMode.Exact:
                result.Total = ordered.Count;
                result.TotalExact = true;
                break;
            default:
                result.Total = -1;
                result.TotalExact = false;
                break;
        }

        var offset = Math.Max(0, request.Offset);
        var limit = request.Limit <= 0 ? int.MaxValue : request.Limit;
        var start = offset;
        var schemeHash = SearchPaging.SchemeHash(resolved.Scheme.Id);
        if (request.Cursor is { } cursor)
        {
            // 世代 / 方案失配 → 游标失效：清空本页，让 UI 从第一页重查（D.5）。
            if (cursor.Gen != _meta.AppliedGeneration || cursor.SchemeHash != schemeHash)
            {
                result.Restart = true;
                start = ordered.Count;
            }
            else
            {
                var position = ordered.FindIndex(candidate => candidate.DocId == cursor.DocId);
                if (position < 0)
                {
                    // 同一世代下位置消失（候选集被重建 / 过滤变化）：按失效处理，不回半页。
                    result.Restart = true;
                    start = ordered.Count;
                }
                else
                {
                    start = position + 1;
                }
            }
        }

        for (var i = start; i < ordered.Count && result.Items.Count < limit; i++)
            result.Items.Add(new SearchHit(ordered[i].TrackId, ordered[i].Bm25, ordered[i].Pass));

        // next 指向本页最后一条在全序里的位置；没有更多时为 null。
        if (!result.Restart && result.Items.Count > 0
            && SearchPaging.HasMore(start, result.Items.Count, ordered.Count))
        {
            var lastIndex = start + result.Items.Count - 1;
            result.Next = SearchPaging.Encode(ordered[lastIndex], _meta.AppliedGeneration, schemeHash);
        }

        if (explain)
        {
            var explanations = new Dictionary<string, SearchExplanation>(StringComparer.Ordinal);
            if (_era is not null)
            {
                var probe = new EraExplainProbe(_era);
                var explained = Math.Min(result.Items.Count, SearchExplainBuilder.MaxExplained);
                for (var i = 0; i < explained; i++)
                {
                    var candidate = ordered[start + i];
                    explanations[candidate.TrackId] = SearchExplainBuilder.Build(
                        candidate, plan.Terms.Count, probesByPass!, probe);
                }
            }
            result.Explanations = explanations;
            result.Trace = new SearchTrace
            {
                Steps = stepTraces!,
                Skipped = skippedTraces!,
                Confidence = confidenceTraces!,
            };
        }

        // 歌词片段只在缓存未命中时截取，随结果一起缓存（缓存键已含 snippet 开关）。
        if (request.Snippet && _libraryDbPath is not null)
            result.Snippets = SearchSnippet.Build(_libraryDbPath, result.Items, request.Query);

        // 高亮同样只在缓存未命中时计算；缓存键已含 highlight / snippet 开关。
        if (request.Highlight && _libraryDbPath is not null)
            result.Highlights = SearchHighlight.Build(
                _libraryDbPath, result.Items, request.Query, request.Snippet);
        return result;
    }

    /// <summary>把趟次名映射到 <see cref="PassCounts"/> 字段（未列出的趟不计）。</summary>
    private static void CountPass(PassCounts passes, string pass)
    {
        switch (pass)
        {
            case SearchPassNames.DropTokens: passes.DropTokens = 1; break;
            case SearchPassNames.PinyinLoose: passes.PinyinLoose = 1; break;
            case SearchPassNames.Reading: passes.Reading = 1; break;
            case SearchPassNames.CrossLingual: passes.CrossLingual = 1; break;
            case SearchPassNames.Fuzzy: passes.Fuzzy = 1; break;
            case SearchPassNames.Typo2: passes.Fuzzy = 1; break;
            case SearchPassNames.CjkFuzzy: passes.CjkFuzzy = 1; break;
        }
    }

    /// <summary>
    /// 探测每个查询词在 title / artist 列的命中数，用于判断「哪个词是歌名」。
    /// 只在主趟不足的多词查询上执行，每词两次计数查询。
    /// </summary>
    private (List<int> Title, List<int> Artist) ProbeFieldAffinity(string query, int wordCount)
    {
        var title = new List<int>(wordCount);
        var artist = new List<int>(wordCount);
        var words = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < wordCount; i++)
        {
            if (i >= words.Length)
            {
                title.Add(0);
                artist.Add(0);
                continue;
            }
            var lexical = SearchQuery.CompileLexical(Analyzer.AnalyzeQuery(words[i]));
            if (lexical is null)
            {
                title.Add(0);
                artist.Add(0);
                continue;
            }
            title.Add(CountEraMatches("title_tok", lexical));
            artist.Add(CountEraMatches("artist_tok", lexical));
        }
        return (title, artist);
    }

    /// <summary>
    /// 统计单列表达式命中的曲目数（丢词重排用，替代已删除的 FTS 计数实现）。
    /// 语言 / 语法异常时按 0 处理，不阻断查询。
    /// </summary>
    private int CountEraMatches(string column, string lexical)
    {
        if (_era is null) return 0;
        try
        {
            var candidates = EraStore.EraQueryPlan.Execute(
                _era, $"{{{column}}} : ({lexical})", -1);
            var tracks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates) tracks.Add(candidate.TrackIdHex);
            return tracks.Count;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            Log.LogWarn("indexer", $"丢词重排计数失败（按 0 处理）: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 加载模糊层词典（T1-8）：扫描 EraStore 词典里可检索的拉丁 / 数字词条，
    /// 首次需要模糊趟时加载并缓存。只收 ASCII 小写 / 数字（CJK 由
    /// <see cref="SearchQuery.CjkVariants"/> 负责），与已删除的 FTS 词表语义一致。
    /// </summary>
    private IReadOnlyList<string> LoadEraVocabulary()
    {
        if (_fuzzyVocabulary is { } cached) return cached;

        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (_era is not null)
        {
            ReadOnlySpan<EraStore.FieldId> wanted = stackalloc[]
            {
                EraStore.FieldId.Title,
                EraStore.FieldId.Artist,
                EraStore.FieldId.Album,
                EraStore.FieldId.Filename,
                EraStore.FieldId.Lyrics,
                EraStore.FieldId.LyricsTranslation,
            };
            try
            {
                foreach (var segment in _era.OpenPerSegment())
                {
                    foreach (var info in segment.Dict.ReadAll())
                    {
                        if (info.Key.Length == 0) continue;
                        var field = (EraStore.FieldId)info.Key[0];
                        if (!wanted.Contains(field)) continue;
                        var term = System.Text.Encoding.UTF8.GetString(
                            info.Key, 1, info.Key.Length - 1);
                        if (LibraryDb.IsAsciiWord(term)) terms.Add(term);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                Log.LogWarn("indexer", $"模糊词典加载失败，退回无词典过滤: {ex.Message}");
                terms.Clear();
            }
        }

        var list = terms.ToList();
        list.Sort(StringComparer.Ordinal);
        _fuzzyVocabulary = list;
        return _fuzzyVocabulary;
    }

    /// <summary>
    /// 该步是否值得做字面精确度复核（T3.11）：表达式必须**可能命中** 0x01–0x03 的
    /// title / artist / album 字面列，且步内搜索的文本就是查询原文。
    ///
    /// <list type="bullet">
    ///   <item>scope=lyrics 把整棵表达式收紧到歌词列 → 永不复核；</item>
    ///   <item><see cref="FieldFamily.Meta"/> 与 scope=all 的 <see cref="FieldFamily.Mixed"/>
    ///         （字面主趟 / 中文容错趟）的表达式覆盖字面元数据列 → 复核；</item>
    ///   <item>拼音 / 首字母 / 读音 / 跨语言 / 容错的搜索词是转写或变体，和
    ///         <c>title_norm</c> 等归一列做字节比较只会得到 none（白读 DocStore）→ 不复核。</item>
    /// </list>
    /// </summary>
    private static bool MayMatchMetadataLiteral(Step step, SearchRequest request)
        => !request.Lyrics && step.Family is FieldFamily.Meta or FieldFamily.Mixed;

    /// <summary>
    /// 该步的歌词层置信度过滤策略（T4.2 / §5.6.3）：
    /// <list type="bullet">
    ///   <item>跨语言步 → 只收高置信（中 / 低一律不参与，与 scope 无关）；</item>
    ///   <item><c>scope=all</c> 的混合 / 歌词 / 容错步 → 只收中置信及以上；</item>
    ///   <item><c>scope=lyrics</c> / <c>scope=metadata</c>、拼音 / 首字母 / 读音步 → 不过滤
    ///         （低置信层在 <c>scope=lyrics</c> 必须仍可搜到；读音列按 §5.6.3 各行都参与）。</item>
    /// </list>
    /// </summary>
    private static EraStore.LyricsConfidencePolicy ConfidencePolicyFor(
        Step step, SearchRequest request)
    {
        if (step.Family == FieldFamily.CrossLingual)
            return EraStore.LyricsConfidencePolicy.HighOnly;
        if (request.Metadata || request.Lyrics)
            return EraStore.LyricsConfidencePolicy.None;
        return step.Family is FieldFamily.Mixed or FieldFamily.Lyrics or FieldFamily.Fuzzy
            ? EraStore.LyricsConfidencePolicy.MediumOrBetter
            : EraStore.LyricsConfidencePolicy.None;
    }

    /// <summary>
    /// 执行一步表达式并产出带证据的候选；排序统一交给 <see cref="SearchRanker"/>。
    ///
    /// <para><paramref name="take"/> 是每步候选上限（<c>limit + offset</c>；&lt;=0 表示不截断），
    /// 从 0 开始取——offset 只在最终排序后切片，避免旧实现「每步先跳 offset」造成的漏召回。</para>
    ///
    /// <para>读音趟的档位（前缀 / 包含 / 其它）写进 <see cref="Evidence.Exactness"/>，
    /// 在同一 kind 内继续保持旧的档内优先级。</para>
    ///
    /// <para><paramref name="exactQuery"/> 非 null 时做字面精确度复核（T3.11）：
    /// 仅当该步**未截断命中数 ≤ <see cref="SearchExactness.Cap"/>** 才读 DocStore，
    /// 其余情况保持 <see cref="SearchExactness.None"/>——宽命中集不承担逐候选读取成本。
    /// 复核同时给出**产生最优档位的列**（title / artist / album），仅在真正命中某列时
    /// 覆盖字段族的保守映射；三列都不命中（doc 只命中歌词列）保留保守映射并记 none。</para>
    /// </summary>
    private List<Candidate> ExecuteMatch(
        string expression, int take, Step step, Evidence evidence,
        IReadOnlyList<string[]>? readingSequences = null, byte[]? exactQuery = null,
        EraStore.LyricsConfidencePolicy confidencePolicy = EraStore.LyricsConfidencePolicy.None)
    {
        // T3.0: 唯一召回路径是 EraStore；索引缺失时返回空（索引器会强制全量重建）。
        if (_era is null) return new List<Candidate>();

        var hits = EraStore.EraQueryPlan.ExecuteDetailed(
            _era, expression, take, readingSequences, confidencePolicy);
        var eraCandidates = hits.Candidates;
        if (eraCandidates.Count == 0) return new List<Candidate>();

        // 邻近度（§4.2 第 5 键）：散落多 term 的**元数据**步才按真实位置算最小跨度。
        // 歌词族不读原文（spec：算不出），拼音 / 首字母 / 读音族的位置语义不同源，均跳过。
        // 成本护栏与精确度复核同口径：单步候选 ≤ Cap 才读位置块，且游标在一次调用内复用。
        // 「真实词」剔除 analyzer 追加的段首 / 段尾 unigram：单真实词按 spec 记 0
        // （不按词在标题里的位置记惩罚）。
        var proximityTerms = step.Family == FieldFamily.Meta
            ? SearchQuery.ProximityTerms(step.Terms)
            : Array.Empty<string>();
        if (proximityTerms.Count > 1)
        {
            EraStore.EraProximity.Fill(
                _era, proximityTerms, eraCandidates, SearchExactness.Cap);
        }

        // 复核判定用「未截断命中数」而不是返回条数：翻页请求 take=-1、首页 take=limit+offset，
        // 用返回条数会让同一查询的不同页对同一步得出不同精确度，keyset 分页随之漂移。
        var exactness = exactQuery is { Length: > 0 } && hits.TotalHits <= SearchExactness.Cap
            ? exactQuery
            : null;
        var segments = exactness is null ? null : NormSegments();

        // positioned 的唯一事实来源是引擎：短语 atom 因字段缺位置被降级成纯交集时为 false，
        // 本步没有短语 atom（或字段带位置）时为 true。
        var positioned = !hits.AnyPhraseDowngraded;

        var result = new List<Candidate>(eraCandidates.Count);
        foreach (var ec in eraCandidates)
        {
            // bm25 分数（负值，与 FTS5 同号），Score 由 EraBm25 在 EraStore 分支内计算
            var virtualDocId = (long)ec.SegmentId * 1_000_000L + ec.DocId;
            // 读音趟：按读音串的前缀 / 包含档位给出 exactness；其余趟由复核结果决定。
            var stepEvidence = evidence with { Positioned = positioned };
            if (readingSequences is { Count: > 0 })
                stepEvidence = stepEvidence with { Exactness = SearchRanker.ReadingExactness(ec.Tier) };
            // 引擎算出真实跨度时才覆盖形状层的兜底值；元数据步的「单真实词」记 0，
            // 其余（歌词 / 转写族、算不出的散落窗口）保持 ProximityOf 的结论。
            if (ec.Proximity != SearchRanker.ProximityUnknown)
                stepEvidence = stepEvidence with { Proximity = ec.Proximity };
            else if (proximityTerms.Count == 1)
                stepEvidence = stepEvidence with { Proximity = 0 };
            if (exactness is not null
                && segments!.TryGetValue(ec.SegmentId, out var segment)
                && (uint)ec.DocId < (uint)segment.Docs.Count)
            {
                SearchExactness.CountDocRead();
                var exact = SearchExactness.RankBest(
                    exactness, segment.Docs.NormSpans(ec.DocId));
                stepEvidence = stepEvidence with
                {
                    Exactness = exact.Rank,
                    // 真实字段只在「查询确实命中某列」时生效；否则保留字段族的保守映射
                    // （Mixed → Lyrics），把只命中歌词列的 doc 排在标题 / 歌手 / 专辑后面。
                    Field = exact.Matched ? exact.Field : stepEvidence.Field,
                    ExactChecked = true,
                };
            }
            result.Add(new Candidate(virtualDocId, ec.TrackIdHex, stepEvidence, ec.Score, step.Pass));
        }
        return result;
    }

    /// <summary>精确度复核用的段视图（SegmentId → 段，首次需要时建立，随会话失效）。</summary>
    private Dictionary<ushort, EraStore.SegmentReaders> NormSegments()
    {
        if (_normSegments is { } cached) return cached;
        var map = new Dictionary<ushort, EraStore.SegmentReaders>();
        if (_era is not null)
        {
            foreach (var segment in _era.OpenPerSegment()) map[segment.SegmentId] = segment;
        }
        _normSegments = map;
        return map;
    }

    /// <summary>LRU 条目：键 + 已合并去重的 top-k 结果（只读共享）。</summary>
    private sealed record CachedQuery(string Key, SearchResult Result);

    public void Dispose()
    {
        _era?.Dispose();
    }
}
