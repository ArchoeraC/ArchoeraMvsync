// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>命中字段（排序第 3 键；SearchSpec v1 §4.2，数字越小越靠前）。</summary>
internal enum FieldKind
{
    Title = 0,
    Artist = 1,
    Album = 2,
    Filename = 3,
    Lyrics = 4,
    LyricsTr = 5,
    LyricsRom = 6,
}

/// <summary>
/// 一条候选的匹配证据：**排序的唯一输入**（SearchSpec v1 §3.1 / §4.2）。
///
/// <para><see cref="Coverage"/> 是「命中 term 数 / 计划 term 数」；
/// <see cref="Exactness"/> 是 0 相等 / 1 前缀 / 2 包含 / 3 近似；
/// <see cref="Positioned"/> 是引擎给出的**真实**位置校验事实（短语 atom 被降级到纯交集时为
/// false，无短语时为 true，见 <see cref="EraStore.EraQueryHits.AnyPhraseDowngraded"/>）；
/// <see cref="Proximity"/> 是多 term 在同一字段内的最小跨度（单 term = 0，
/// 算不出时 = <see cref="SearchRanker.ProximityUnknown"/>）。</para>
///
/// <para>T3.11：字面精确度改由 <see cref="SearchExactness"/> 读 DocStore 归一列复核
/// （元数据步与 scope=all 的混合步、单步命中 ≤ <see cref="SearchExactness.DefaultCap"/>），
/// 邻近度由 <see cref="SearchRanker.FromStep"/> 按步骤形状给出。Q1：歌词字面步另有
/// <c>lyrics_digest</c> 整行相等复核（只给 <see cref="SearchExactness.Equal"/>，前缀 / 包含仍
/// <see cref="SearchExactness.None"/>）；读音 / 转写 / 模糊族保持 S2 的保守值。</para>
///
/// <para><see cref="ExactChecked"/> 是「该证据的字面精确度确实在 DocStore 上算过」的标志：
/// explain 用它决定 <c>resolvedField</c> 是否仍走逐列探测（见
/// <see cref="SearchExplainBuilder"/>）。宽命中步（&gt; cap）不读 DocStore，因此为 false；
/// 歌词整行复核（Q1）只改第 4 键，不单独置位（Mixed 步的元数据复核块仍会置位）；
/// 读音 / 拼音 / 首字母 / 转写 / 容错步不复核，同样为 false。</para>
/// </summary>
internal readonly record struct Evidence(
    MatchKind Kind,
    FieldKind Field,
    double Coverage,
    int Exactness,
    bool Positioned,
    int Proximity = SearchRanker.ProximityUnknown,
    bool ExactChecked = false);

/// <summary>一条候选来源（explain.provenance 的 S2 子集：趟次名 + 该趟证据）。</summary>
internal readonly record struct CandidateSource(string Pass, Evidence Evidence);

/// <summary>
/// 排序候选项（S2 / T2.1）。<see cref="DocId"/> 是 v7 rowid：只用于稳定键与合并，
/// 不对外暴露；对外稳定键是 <see cref="TrackId"/>。
/// </summary>
internal sealed class Candidate
{
    /// <summary>来源上限：单条查询的步骤数远小于该值，仅防御异常计划。</summary>
    private const int MaxSources = 16;

    // 多数候选只有单一来源；宽命中集（1 万条）下每个候选都建 List 会多出
    // 1 万个小对象（T3.14）。首条内联，只有真正合并多来源时才建 List。
    private CandidateSource _first;
    private List<CandidateSource>? _rest;
    private int _sourceCount;

    public Candidate(long docId, string trackId, Evidence evidence, double bm25, string pass)
    {
        DocId = docId;
        TrackId = trackId;
        Evidence = evidence;
        Bm25 = bm25;
        Pass = pass;
        _first = new CandidateSource(pass, evidence);
        _sourceCount = 1;
    }

    /// <summary>v7 rowid；合并时取更小值，保证稳定键只降不升。</summary>
    public long DocId { get; private set; }

    public string TrackId { get; }

    /// <summary>当前最高档证据（合并规则见 <see cref="SearchRanker.Merge"/>）。</summary>
    public Evidence Evidence { get; private set; }

    /// <summary>桶内细粒度分数（bm25，越小越相关）；只在前面全部键相等时生效。</summary>
    public double Bm25 { get; private set; }

    /// <summary>产生当前证据的步骤名（归因，兼容既有 <c>pass</c> 字段）。</summary>
    public string Pass { get; private set; }

    /// <summary>全部来源（合并时追加；只进 explain，不参与排序）。</summary>
    public IReadOnlyList<CandidateSource> Sources
    {
        get
        {
            if (_rest is null || _rest.Count == 0) return new[] { _first };
            var all = new List<CandidateSource>(_sourceCount) { _first };
            all.AddRange(_rest);
            return all;
        }
    }

    internal void AddSource(string pass, Evidence evidence)
    {
        if (_sourceCount >= MaxSources) return;
        _rest ??= new List<CandidateSource>(4);
        _rest.Add(new CandidateSource(pass, evidence));
        _sourceCount++;
    }

    internal void SetEvidence(Evidence evidence, string pass)
    {
        Evidence = evidence;
        Pass = pass;
    }

    internal void LowerDocId(long docId)
    {
        if (docId < DocId) DocId = docId;
    }

    internal void LowerBm25(double bm25)
    {
        if (bm25 < Bm25) Bm25 = bm25;
    }
}

/// <summary>
/// 桶序比较器（S2 / T2.1–T2.2 起；SearchSpec v1 §4.2）。
///
/// <para><b>唯一排序入口</b>：任何方案都只能用这一个比较器，不保留第二套 ranker；
/// 也不得改成加权求和（分桶是「序」的语义，加法权重存在「弱信号 × 高 TF」退化路径）。</para>
///
/// <para><b>生效的 7 键</b>：kind → coverage → field → exactness → proximity →
/// bm25 → docId。exactness / proximity 在 T3.11 起是真实值（字面步读 DocStore 归一列、
/// 按步骤形状判邻近度）；<b>field 也由复核决定</b>——命中 title / artist / album 归一列时
/// 写真实字段，复核为「近似」时保留字段族的保守映射（Mixed → Lyrics，宁可把歌词命中排低）。
/// 歌词字面步只在整行相等时记 <see cref="SearchExactness.Equal"/>（Q1）；其余读音 /
/// 拼音 / 首字母 / 转写 / 容错步的精确度仍记 <see cref="SearchExactness.None"/>。
/// 邻近度只在有字面字段集的族（元数据 / 歌词 / 混合 / 读音 / 模糊）里算，散落多 term
/// 由引擎读位置块填充；拼音 / 首字母 / 跨语言族保持 <see cref="ProximityUnknown"/>。</para>
/// </summary>
internal static class SearchRanker
{
    /// <summary>
    /// 邻近度「算不出」（不支持的族 / 引擎未填的散落多 term）。wire 上用 -1 表示（D.3.1）。
    /// </summary>
    public const int ProximityUnknown = int.MaxValue;

    /// <summary>kind 档位：枚举声明顺序即偏序（Exact 最好 … Typo 最差）。</summary>
    public static int KindRank(MatchKind kind) => (int)kind;

    /// <summary>字段档位：枚举声明顺序即偏序（Title 最好 … LyricsRom 最差）。</summary>
    public static int FieldRank(FieldKind field) => (int)field;

    /// <summary>
    /// 从计划步骤构造证据。覆盖度用「已覆盖 term 数 / 计划 term 数」；
    /// 字面精确度由调用方读 DocStore 后覆盖（未复核时是 <see cref="SearchExactness.None"/>）。
    ///
    /// <para><b>positioned</b>（T2.5）：本函数**不再**用「步骤里有短语 atom」推断，
    /// 而是默认 <c>true</c>；真正的降级事实来自引擎——<see cref="SearchSession"/> 用
    /// <see cref="EraStore.EraQueryHits.AnyPhraseDowngraded"/> 覆盖它。只有短语 atom 落到
    /// 无位置字段、被 <see cref="EraStore.Iterators.PhraseIter"/> 降级成纯交集时才为 false。</para>
    ///
    /// <para><b>邻近度</b>（§4.2 第 5 键）：单 term = 0（不需要位置）；有字面字段集的族
    /// （元数据 / 歌词 / 混合 / 读音 / 模糊）且表达式恰好是一个多 token 短语（位置连续性
    /// 已由 <c>PhraseIter</c> 证明）= 短语 token 数 - 1；其余记
    /// <see cref="ProximityUnknown"/>——散落多 term 的最小跨度由
    /// <see cref="EraStore.EraProximity"/> 在引擎里按真实位置计算后覆盖；
    /// 拼音 / 首字母 / 跨语言族的 term 不是其列的字面 token，按 spec 直接记算不出。</para>
    /// </summary>
    public static Evidence FromStep(Step step, int termCount, int exactness = SearchExactness.None)
    {
        var shape = StepShape.Of(step);
        return new(
            step.Kind,
            FieldOf(step.Family),
            termCount <= 0 ? 0 : Math.Min(1.0, (double)step.CoveredTerms / termCount),
            exactness,
            Positioned: true,
            Proximity: ProximityOf(step, termCount, shape));
    }

    /// <summary>
    /// 步骤邻近度的**兜底**值：单 term = 0；有字面字段集的族恰好一个多 token 短语 = token 数 - 1；
    /// 散落多 term / 不支持的族 = <see cref="ProximityUnknown"/>。
    ///
    /// <para>散落多 term 由 <see cref="EraStore.EraProximity.Fill"/> 在引擎里按真实位置覆盖
    /// （SearchSession 对有映射的族且 term ≥2 时调用），形状层不猜；拼音 / 首字母 / 跨语言族
    /// 没有映射，永远保持算不出。</para>
    /// </summary>
    public static int ProximityOf(Step step, int termCount, in StepShape shape)
    {
        if (termCount <= 1) return 0;
        // 拼音 / 首字母 / 跨语言族：step.Terms 不是这些列的字面 token，位置语义不同源 → 算不出。
        if (!EraStore.EraProximity.Supports(step.Family)) return ProximityUnknown;
        if (shape.Atoms == 1 && shape.PhraseAtoms == 1 && shape.MaxPhraseTokens > 1)
            return shape.MaxPhraseTokens - 1;
        return ProximityUnknown;
    }

    /// <summary>
    /// 步骤表达式的结构摘要（邻近度 / positioned 的唯一来源）。
    /// 解析失败（不该发生）按空形状处理：positioned=false、邻近度算不出。
    /// </summary>
    public readonly record struct StepShape(int Atoms, int PhraseAtoms, int MaxPhraseTokens)
    {
        public static StepShape Of(Step step)
        {
            if (string.IsNullOrEmpty(step.Expression)) return default;
            try
            {
                var atoms = 0;
                var phrases = 0;
                var maxTokens = 0;
                Count(EraStore.FtsExpression.Parse(step.Expression),
                    ref atoms, ref phrases, ref maxTokens);
                return new StepShape(atoms, phrases, maxTokens);
            }
            catch (FormatException)
            {
                return default;
            }
        }

        private static void Count(
            EraStore.FtsNode node, ref int atoms, ref int phrases, ref int maxTokens)
        {
            switch (node)
            {
                case EraStore.FtsScope scope:
                    Count(scope.Child, ref atoms, ref phrases, ref maxTokens);
                    return;
                case EraStore.FtsAnd and:
                    foreach (var child in and.Children)
                        Count(child, ref atoms, ref phrases, ref maxTokens);
                    return;
                case EraStore.FtsOr or:
                    foreach (var child in or.Children)
                        Count(child, ref atoms, ref phrases, ref maxTokens);
                    return;
                case EraStore.FtsPhrase phrase:
                    atoms++;
                    phrases++;
                    maxTokens = Math.Max(maxTokens, phrase.Tokens.Count);
                    return;
                case EraStore.FtsTerm:
                    atoms++;
                    return;
            }
        }
    }

    /// <summary>
    /// 字段族的保守映射（T3.11 起只作**复核前的默认值**）：v7 的表达式无法在结果行上区分
    /// title / artist / album，因此元数据族默认记 <see cref="FieldKind.Title"/>，歌词族记
    /// <see cref="FieldKind.Lyrics"/>；混合族（scope=all 的主趟 / 中文容错趟）**默认按歌词记**——
    /// 宁可把歌词命中排低，也不让它冒充标题命中（SearchSpec §3.6.3）。
    ///
    /// <para>复核生效时（单步命中 ≤ cap）由 <see cref="SearchExactness.RankBest"/> 给出真实列，
    /// 仅当查询确实命中 title_norm / artist_norm / album_norm 才改写本映射；复核为「近似」
    /// （doc 只命中歌词列）时保留这里的保守值。</para>
    /// </summary>
    public static FieldKind FieldOf(FieldFamily family) => family switch
    {
        FieldFamily.Meta => FieldKind.Title,
        FieldFamily.Pinyin => FieldKind.Title,
        FieldFamily.Initials => FieldKind.Title,
        FieldFamily.CrossLingual => FieldKind.Title,
        FieldFamily.Lyrics => FieldKind.Lyrics,
        // Reading 合并了 reading_meta_tok / lyrics_rom_tok，无法在 v7 上区分。
        FieldFamily.Reading => FieldKind.Lyrics,
        // Mixed / Fuzzy 可能落歌词列，按歌词保守处理。
        FieldFamily.Mixed => FieldKind.Lyrics,
        FieldFamily.Fuzzy => FieldKind.Lyrics,
        _ => FieldKind.Lyrics,
    };

    /// <summary>读音趟档位（3 前缀 / 2 包含 / 0 其它）映射到 exactness（0 相等 … 3 近似）。</summary>
    public static int ReadingExactness(int tier) => tier switch
    {
        ReadingRanker.PrefixTier => 1,
        ReadingRanker.ContainsTier => 2,
        _ => 3,
    };

    /// <summary>
    /// 合并同一条 track 的多步证据（SearchSpec v1 §3.6）：
    /// kind 取最高档；同档 coverage 取最大、field 取更小（**真实字段也按 FieldRank 比**：
    /// Title &lt; Artist &lt; Album &lt; … &lt; Lyrics）、exactness / proximity 取更小；
    /// bm25 取最小（仅桶内平局）；不累加分数、不跨 kind 拼 coverage。
    /// </summary>
    public static void Merge(Candidate target, Candidate incoming)
    {
        target.LowerDocId(incoming.DocId);
        target.LowerBm25(incoming.Bm25);
        target.AddSource(incoming.Pass, incoming.Evidence);

        var targetRank = KindRank(target.Evidence.Kind);
        var incomingRank = KindRank(incoming.Evidence.Kind);
        if (incomingRank < targetRank)
        {
            target.SetEvidence(incoming.Evidence, incoming.Pass);
            return;
        }
        if (incomingRank > targetRank) return;

        var better = IsBetterSameKind(incoming.Evidence, target.Evidence);
        target.SetEvidence(MergeSameKind(target.Evidence, incoming.Evidence),
            better ? incoming.Pass : target.Pass);
    }

    /// <summary>7 键字典序（见类注释；exactness / proximity 均为升序，越小越好）。</summary>
    public static int Compare(Candidate a, Candidate b)
    {
        var c = KindRank(a.Evidence.Kind).CompareTo(KindRank(b.Evidence.Kind));
        if (c != 0) return c;

        c = b.Evidence.Coverage.CompareTo(a.Evidence.Coverage);
        if (c != 0) return c;

        c = FieldRank(a.Evidence.Field).CompareTo(FieldRank(b.Evidence.Field));
        if (c != 0) return c;

        c = a.Evidence.Exactness.CompareTo(b.Evidence.Exactness);
        if (c != 0) return c;

        c = a.Evidence.Proximity.CompareTo(b.Evidence.Proximity);
        if (c != 0) return c;

        c = a.Bm25.CompareTo(b.Bm25);
        if (c != 0) return c;

        return a.DocId.CompareTo(b.DocId);
    }

    /// <summary>按 7 键字典序排序（唯一排序入口；切片由调用方负责）。</summary>
    public static void Sort(List<Candidate> candidates) => candidates.Sort(Compare);

    private static Evidence MergeSameKind(Evidence a, Evidence b)
        => new(
            a.Kind,
            FieldRank(a.Field) <= FieldRank(b.Field) ? a.Field : b.Field,
            Math.Max(a.Coverage, b.Coverage),
            Math.Min(a.Exactness, b.Exactness),
            a.Positioned && b.Positioned,
            Math.Min(a.Proximity, b.Proximity),
            // 任一段复核过就算「算过」：explain 的 resolvedField 因此不再走逐列探测，
            // 与合并后的 field（取自 FieldRank 更小者）保持一致。
            a.ExactChecked || b.ExactChecked);

    /// <summary>
    /// 同档时 incoming 是否严格更优（coverage 更大 / field 更前 / exactness 更小 / proximity 更小）。
    /// </summary>
    private static bool IsBetterSameKind(Evidence incoming, Evidence current)
    {
        if (incoming.Coverage != current.Coverage) return incoming.Coverage > current.Coverage;
        if (FieldRank(incoming.Field) != FieldRank(current.Field))
            return FieldRank(incoming.Field) < FieldRank(current.Field);
        if (incoming.Exactness != current.Exactness)
            return incoming.Exactness < current.Exactness;
        return incoming.Proximity < current.Proximity;
    }
}

// 说明：SearchExactness 与桶序放在同一文件，是为了不改 bench/qbench 的显式源码清单
// （bench/ 在本任务里只读）。逻辑上它是 ranker 的第 4 键复核，二者同源、同生命周期。

/// <summary>
/// 字面精确度复核（S3 / T3.11；SearchSpec v1 §4.2 的第 4 键）。
///
/// <para><b>输入</b>：查询归一形 <c>q</c>（<see cref="Analyzer.NormalizeMetadata"/> 后再去掉空白）
/// 与 DocStore v3 的 <c>title_norm / artist_norm / album_norm</c>（同一归一函数的产物）。
/// 比较是**空白不敏感**的：两侧都跳过 ASCII 空格再逐字节比，因此
/// <c>我爱你 中国</c> 与 <c>我爱你中国</c>、<c>hello  world</c> 与 <c>hello world</c> 等价。</para>
///
/// <para><b>为什么按字节</b>：UTF-8 的字节前缀等价于码点前缀（前导字节与续字节的取值区间不重叠），
/// 所以相等 / 前缀 / 包含都能直接在 <see cref="EraStore.DocNormSpans"/> 的 span 上判定，零分配、零解码。
/// CJK 里空格本来就不参与命中；拉丁里 0x20 是词边界，去空白比较顺带把「词间多空格」拉平。</para>
///
/// <para><b>成本护栏</b>：只在**单步命中集 ≤ <see cref="Cap"/>** 时复核（步骤命中总数取自
/// <see cref="EraStore.EraQueryPlan"/> 的未截断计数，与请求的 <c>limit</c> / 游标无关，翻页判定一致）。
/// 宽命中步一律记 <see cref="None"/>，不读 DocStore——p50 / p95 门禁靠这条守住（T3.14）。</para>
///
/// <para><b>Q1 歌词整行</b>：DocStore v5 的 <c>lyrics_digest</c> 存每行歌词的归一化 64 位哈希
/// （<see cref="EraStore.LyricDigest"/>）。字面主趟且 scope 覆盖歌词列、命中 ≤ cap 时，
/// 元数据三列都没命中才二分查找该列：整行相等记 <see cref="Equal"/>，**字段保持保守的
/// Lyrics**。哈希恢复不了原文，前缀 / 包含仍记 <see cref="None"/>。</para>
///
/// <para><b>适用步骤</b>：表达式可能命中 title / artist / album **字面**列（0x01–0x03）且
/// 搜索文本就是查询原文的步骤——<see cref="FieldFamily.Meta"/> 与 scope=all 的
/// <see cref="FieldFamily.Mixed"/>（主趟 + 中文容错趟）。scope=lyrics 收紧到歌词列、
/// 或搜索文本是转写 / 变体（拼音 / 首字母 / 读音 / 跨语言 / 容错）的步骤不做字节比较：
/// 它们与归一列不可能「相等」，复核只会白读 DocStore。</para>
/// </summary>
internal static class SearchExactness
{
    /// <summary>复核候选上限（SearchSpec §3.2 的「单步候选预算 ≈ 2k」）。</summary>
    public const int DefaultCap = 2048;

    /// <summary>整字段等于查询。</summary>
    public const int Equal = 0;

    /// <summary>字段以查询开头。</summary>
    public const int StartsWith = 1;

    /// <summary>字段包含查询。</summary>
    public const int Contains = 2;

    /// <summary>不满足以上三者（排序里「近似」档）。</summary>
    public const int None = 3;

    /// <summary>
    /// 三列复核的结果：档位 + **产生该档位的列**（T3.11 第二步）。
    ///
    /// <para><see cref="Field"/> 只在 <see cref="Matched"/> 为真时有意义——此时它是
    /// title / artist / album 之一，可直接作为排序第 3 键（<see cref="FieldKind"/>）；
    /// <see cref="Matched"/> 为假（三列都判不出）时调用方**必须**保留字段族的保守映射
    /// （Mixed → Lyrics），不能让空列 / 未命中冒充标题命中。</para>
    /// </summary>
    public readonly record struct ExactField(int Rank, FieldKind Field)
    {
        /// <summary>三列都没命中（<see cref="Rank"/> = <see cref="SearchExactness.None"/>）。</summary>
        public static ExactField None { get; } = new(SearchExactness.None, FieldKind.Title);

        /// <summary>是否真的命中了某一列（只有此时 <see cref="Field"/> 可写进排序键）。</summary>
        public bool Matched => Rank < SearchExactness.None;
    }

    /// <summary>测试接缝：临时覆盖候选上限（<c>null</c> = 用 <see cref="DefaultCap"/>）。</summary>
    internal static int? DebugCapOverride = null;

    /// <summary>生效的复核候选上限。</summary>
    public static int Cap => DebugCapOverride ?? DefaultCap;

    private static long _docReads;

    /// <summary>测试接缝：本进程累计的 DocStore 归一列读取次数。</summary>
    internal static long DebugDocReads => Interlocked.Read(ref _docReads);

    private static long _lyricDigestReads;

    /// <summary>测试接缝：本进程累计的 DocStore 歌词行摘要（v5 <c>lyrics_digest</c>）读取次数。</summary>
    internal static long DebugLyricDigestReads => Interlocked.Read(ref _lyricDigestReads);

    /// <summary>测试接缝：清零两类 DocStore 读取计数。</summary>
    internal static void DebugResetDocReads()
    {
        Interlocked.Exchange(ref _docReads, 0);
        Interlocked.Exchange(ref _lyricDigestReads, 0);
    }

    /// <summary>记一次 DocStore 归一列读取（<see cref="DocStoreReader.NormSpans"/>）。</summary>
    internal static void CountDocRead() => Interlocked.Increment(ref _docReads);

    /// <summary>记一次 DocStore 歌词行摘要读取（<see cref="DocStoreReader.ContainsLyricLine"/>）。</summary>
    internal static void CountLyricDigestRead() => Interlocked.Increment(ref _lyricDigestReads);

    /// <summary>
    /// 查询归一形对应的歌词行哈希（Q1）：与 <see cref="EraStore.LyricDigest.HashLine"/>
    /// 同一 FNV-1a 口径，因此可以用「整行相等」直接命中 DocStore v5 的 <c>lyrics_digest</c>。
    /// </summary>
    public static ulong? HashQuery(ReadOnlySpan<byte> normalizedQuery)
        => EraStore.LyricDigest.HashQuery(normalizedQuery);

    /// <summary>ASCII 空格（<see cref="Analyzer.NormalizeMetadata"/> 折叠后的唯一空白）。</summary>
    private const byte Space = 0x20;

    /// <summary>
    /// 查询归一形 <c>q</c>：<see cref="Analyzer.NormalizeMetadata"/> + 去掉全部 ASCII 空格，
    /// 输出 UTF-8（SearchSpec §3.6.3 的 <c>q = 归一形（去掉空白）</c>）。
    /// 空查询 / 纯空白返回空数组。
    /// </summary>
    public static byte[] NormalizeQuery(string? raw)
    {
        var normalized = Analyzer.NormalizeMetadata(raw);
        if (normalized.Length == 0) return Array.Empty<byte>();

        // NormalizeMetadata 已把任意空白折叠成单个 ASCII 空格并去掉首尾，
        // 所以这里只需要处理 0x20；快路径避免无空白时的二次分配。
        if (!normalized.Contains(' ')) return System.Text.Encoding.UTF8.GetBytes(normalized);
        return System.Text.Encoding.UTF8.GetBytes(normalized.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>
    /// 单个字段的精确度：<see cref="Equal"/> &lt; <see cref="StartsWith"/> &lt;
    /// <see cref="Contains"/> &lt; <see cref="None"/>（数字越小越靠前）。
    /// </summary>
    public static int Rank(ReadOnlySpan<byte> query, ReadOnlySpan<byte> field)
    {
        if (query.Length == 0) return None;
        if (field.Length == 0) return None;

        if (EqualsIgnoringSpaces(field, query)) return Equal;
        if (StartsWithIgnoringSpaces(field, query)) return StartsWith;
        if (ContainsIgnoringSpaces(field, query)) return Contains;
        return None;
    }

    /// <summary>
    /// 三列元数据取最优（最小）档，并回传产生该档位的列：相等 &lt; 前缀 &lt; 包含 &lt; 近似。
    /// 同档多列命中时按 <see cref="SearchRanker.FieldRank"/> 取更靠前的列（title → artist → album）。
    /// 空列（源字段为空）恒为 <see cref="None"/>，不会因为「空串包含空查询」误判。
    /// </summary>
    public static ExactField RankBest(ReadOnlySpan<byte> query, in EraStore.DocNormSpans spans)
    {
        var best = ExactField.None;
        Consider(ref best, Rank(query, spans.Title), FieldKind.Title);
        Consider(ref best, Rank(query, spans.Artist), FieldKind.Artist);
        Consider(ref best, Rank(query, spans.Album), FieldKind.Album);
        return best;
    }

    /// <summary>严格更优才替换（同档保留先出现的列，即 FieldRank 更小者）。</summary>
    private static void Consider(ref ExactField best, int rank, FieldKind field)
    {
        if (rank < best.Rank) best = new ExactField(rank, field);
    }

    /// <summary>整字段与查询（去空白后）逐字节相等。</summary>
    private static bool EqualsIgnoringSpaces(ReadOnlySpan<byte> field, ReadOnlySpan<byte> query)
    {
        var matched = 0;
        foreach (var b in field)
        {
            if (b == Space) continue;
            if (matched >= query.Length || b != query[matched]) return false;
            matched++;
        }
        return matched == query.Length;
    }

    /// <summary>字段（去空白后）以查询开头。</summary>
    private static bool StartsWithIgnoringSpaces(ReadOnlySpan<byte> field, ReadOnlySpan<byte> query)
    {
        var matched = 0;
        foreach (var b in field)
        {
            if (b == Space) continue;
            if (matched == query.Length) return true;
            if (b != query[matched]) return false;
            matched++;
        }
        return matched == query.Length;
    }

    /// <summary>查询（去空白后）出现在字段（去空白后）的任意位置。</summary>
    private static bool ContainsIgnoringSpaces(ReadOnlySpan<byte> field, ReadOnlySpan<byte> query)
    {
        for (var start = 0; start < field.Length; start++)
        {
            if (field[start] == Space) continue;
            if (MatchesAt(field, start, query)) return true;
        }
        return false;
    }

    /// <summary>从 <paramref name="start"/>（非空格字节）起跳过空格匹配整个查询。</summary>
    private static bool MatchesAt(ReadOnlySpan<byte> field, int start, ReadOnlySpan<byte> query)
    {
        var matched = 0;
        for (var i = start; i < field.Length; i++)
        {
            if (field[i] == Space) continue;
            if (matched == query.Length) return true;
            if (field[i] != query[matched]) return false;
            matched++;
        }
        return matched == query.Length;
    }
}
