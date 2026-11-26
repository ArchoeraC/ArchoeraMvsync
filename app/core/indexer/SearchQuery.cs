// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Text;

namespace Archoera.Indexer;

/// <summary>
/// 一条计划步骤的**表达式形状**（T2.5 kind 分类的唯一来源）。
///
/// <para>值与 <see cref="MatchKind"/> 一一对应（不含 Reading / CrossLingual / Typo：
/// 那三档由「哪一趟」决定，不来自字面 / 拼音 / 首字母的表达式形状）。</para>
/// </summary>
internal enum QueryShapeKind
{
    Exact,
    Phrase,
    Prefix,
    Partial,
    PinyinExact,
    PinyinPhrase,
    Initials,
}

/// <summary>
/// 形状判定结果：<see cref="Kind"/> + 短语块词元数（诊断 / 邻近度用）。
///
/// <para><see cref="PhraseTokens"/> 为 0 表示该形状没有「位置连续的多词短语」——
/// 邻近度因此算不出（散落 AND / 前缀 / 拼音 / 首字母），由执行器另算。</para>
/// </summary>
internal readonly record struct QueryShape(
    QueryShapeKind Kind,
    int PhraseTokens = 0,
    int Terms = 0);

/// <summary>
/// FTS5 <c>MATCH</c> 表达式编译（**纯函数，无 IO，可单测**）。
///
/// 与旧 Dart 实现（<c>search_query.dart</c> / <c>search_cjk_fuzzy.dart</c>）逐字符等价：
/// 查询编译是搜索召回的核心规则，放在索引器内可让 Dart 只做 UI 调度与模型映射。
///
/// 语义：
/// <list type="bullet">
///   <item><b>词法支</b>：连续多个 CJK / 假名 / 谚文 bigram 合并为引号短语，其余逐个
///         <c>AND</c>；末词元为拉丁 / 数字且 ≥3 字符时加前缀 <c>*</c>。</item>
///   <item><b>拼音支</b>：单音节 ≥3 字符前缀；≥2 音节为相邻短语（位置无关的宽松形态与
///         模糊音扩展只在兜底趟 <see cref="CompilePinyinLoose"/> 中使用）。</item>
///   <item><b>首字母支</b>：CJK 段 / 用户键入的拉丁首字母串按相邻两两 bigram 匹配首字母列
///         （2 字符单词元 / ≥3 字符相邻短语）。</item>
///   <item><b>读音支</b>：拉丁 / 数字词元的字母 bigram 短语 <c>AND</c> 到读音列。</item>
///   <item><b>跨语言支</b>：候选逐个加引号后以 <c>OR</c> 组合（候选之间 OR）。</item>
///   <item><b>模糊支</b>：拉丁 / 数字词元展开编辑距离 ≤1 变体 OR 组，其余与主趟一致。</item>
/// </list>
/// 安全：所有词元都经 <see cref="Quote"/> 转义（内部 <c>"</c> 翻倍），杜绝 FTS5 语法错误与注入。
/// </summary>
internal static class SearchQuery
{
    /// <summary>元数据列过滤用的 FTS5 列集合（<c>scope=metadata</c>；不含歌词 / 译文 / 音译段）。</summary>
    public const string MetadataColumns =
        "title_tok artist_tok album_tok filename_tok pinyin_meta_tok initials_meta_tok reading_meta_tok";

    /// <summary>
    /// 歌词列过滤用的 FTS5 列集合（<c>scope=lyrics</c>）：原文 / 译文 / 音译 / 拼音 / 首字母。
    ///
    /// <para>FTS5 的 <c>{cols} : (expr)</c> 外过滤与表达式内部已有的列过滤**取交集**，
    /// 因此歌词趟可以复用元数据趟的编译结果，再统一收紧到歌词字段族。</para>
    /// </summary>
    public const string LyricsColumns =
        "lyrics_tok lyrics_tr_tok lyrics_rom_tok pinyin_lyrics_tok initials_lyrics_tok";

    /// <summary>索引里存放拼音的列集合。</summary>
    public const string PinyinColumns = "pinyin_meta_tok pinyin_lyrics_tok";

    /// <summary>索引里存放首字母的列集合。</summary>
    public const string InitialsColumns = "initials_meta_tok initials_lyrics_tok";

    /// <summary>
    /// 歌词首字母关闭（T3.4 V4 默认）时首字母支只扫的列集合。
    ///
    /// <para>此时 <c>initials_lyrics_tok</c> 在索引里是空列，把它留在列过滤里虽然
    /// 不改变结果（FTS5 对空列取并集），但会让 explain / 成本估算误以为歌词首字母
    /// 参与召回。按 Manifest 选项位收紧到元数据列，语义与「该能力不存在」一致。</para>
    /// </summary>
    public const string InitialsMetaColumns = "initials_meta_tok";

    /// <summary>
    /// 索引里存放读音的列集合：元数据读音（始终产出）+ 音译歌词列（LRC 音译整词，
    /// 或开启 T2-9「歌词罗马音生成」后的歌词读音 bigram）。
    ///
    /// 读音趟匹配的是**相邻 bigram 短语**，整词内容不会被 bigram 短语误命中；
    /// 反之用户键入整词时仍可命中整词。
    /// </summary>
    public const string ReadingColumns = "reading_meta_tok lyrics_rom_tok";

    /// <summary>用户可见文本列：歌名 / 歌手 / 专辑 / 文件名，不含派生转写列。</summary>
    public const string VisibleTextColumns = "title_tok artist_tok album_tok filename_tok";

    /// <summary>拉丁单字查询允许的列：可见文本 + 首字母（真实的首字母检索），不含拼音与歌词。</summary>
    public const string SingleCharLatinColumns =
        "title_tok artist_tok album_tok filename_tok initials_meta_tok";

    /// <summary>单次模糊编译的变体总数默认上限（防止长串 / 多词元生成巨型 MATCH）。</summary>
    public const int DefaultMaxFuzzyVariants = 500;

    /// <summary>单次中文容错的变体总数默认上限。</summary>
    public const int DefaultMaxCjkVariants = 200;

    /// <summary>CJK 子序列长度硬上限：超长（粘贴整段文本）放弃容错，避免 O(L²) 膨胀。</summary>
    public const int MaxCjkQueryLength = 64;

    /// <summary>
    /// 计划期收缩门槛（RP §3.5.7 / U4）：查询的**可见码点**数达到该值后只生成字面主趟，
    /// 不再生成丢词 / 拼音 / 读音 / 跨语言 / 容错 step——变体枚举与丢词子集都按长度组合增长，
    /// 粘贴整段歌词会把计划与表达式编译推到爆炸级。
    ///
    /// <para>这是**计划层**门槛，与请求层 200 码点截断（<c>SearchApiRequest.MaxQueryCodePoints</c>）
    /// 是两个独立约束，不得混用：请求层负责「载荷上限」，本门槛只决定「生成哪些趟」，
    /// 不改写查询文本。数值为初版策略上限，仍待回放校准。</para>
    /// </summary>
    public const int MaxPlanQueryCodePoints = 64;

    /// <summary>CJK 容错（删除 / 换位）的最小字数：两字删一字会退化成宽覆盖的单字查询。</summary>
    public const int MinCjkFuzzyLength = 3;

    /// <summary>拉丁模糊趟的最小词长：过短的词做编辑距离扩展只会引入高频噪声。</summary>
    public const int MinFuzzyWordLength = 3;

    /// <summary>typo2（Damerau ≤2，仅 recall@1）的最小词长（N22：3 字符词不启用）。</summary>
    public const int MinTypo2WordLength = 7;

    /// <summary>typo2 单次编译的变体上限（独立于 typo1，避免 recall@1 的表达式过大）。</summary>
    public const int DefaultMaxTypo2Variants = 200;

    /// <summary>首字母支的最小段长：单字母无法构成 bigram，且在真实曲库里过于稠密、不生成该支。</summary>
    public const int MinInitialsBranchLength = 2;

    /// <summary>跨语言释义支的最小 CJK 字数：单字释义极度发散，不生成「汉字 → 英文」候选。</summary>
    public const int MinCrossLingualCjkLength = 2;

    /// <summary>拼音宽松趟每次查询的变体总预算（跨音节累计）。</summary>
    public const int DefaultMaxPinyinVariants = 16;

    /// <summary>主趟读音支（拼音 / 首字母）的 CJK 字数下限。</summary>
    public const int ReadingBranchMinCjkLength = 2;

    /// <summary>末词前缀同时保留精确词，让 bm25 把字面命中排前（精确 ⊂ 前缀，不改变命中集合）。</summary>
    public static readonly bool PairExactWithPrefix = true;

    /// <summary>多词查询命中不足时允许丢词降级（仍是字面匹配，方向最安全）。</summary>
    public static readonly bool AllowDropTokens = true;

    /// <summary>单字查询限定在可见文本列，不扫歌词与派生转写列。</summary>
    public static readonly bool RequireMetadataForSingleChar = true;

    /// <summary>首字符不同的替换变体不生成（编辑距离只有 ≤1 一档，首错等价于 2 错）。</summary>
    public static readonly bool FirstCharTypoCostsTwo = true;

    /// <summary>拼音 / 拉丁词元加前缀 <c>*</c> 的最小长度。</summary>
    public const int MinPrefixTokenLength = 3;

    /// <summary>
    /// **单音节拼音前缀**（<see cref="PinyinBranch"/> 的 <c>*</c>）的最小长度（Q3）。
    ///
    /// <para>与 <see cref="MinPrefixTokenLength"/> 分开：后者是字面末词 / 模糊变体的前缀门槛，
    /// 本轮不动；本门槛只作用于「一个音节就是整个查询」的拼音支。3 字符完整音节
    /// （<c>wan</c> / <c>shi</c>）加前缀会扩到**另一个音节**（<c>wang</c> / <c>shine</c>），
    /// 5k 合成库实测 df 约翻倍（见 <c>docs/local-search-indexer-t314-gate.md</c> Q3）；
    /// 但同一个门槛也是「边打边搜」3 字符部分拼音（<c>zho</c> → <c>zhong</c>）的召回来源，
    /// 默认保持 3，抬高门槛属产品决策。</para>
    /// </summary>
    public const int MinPinyinPrefixTokenLength = MinPrefixTokenLength;

    /// <summary>
    /// 替换容错的受限字母表（编辑距离 ≤1 的替换集合，刻意不用完整 26 字母）。
    /// 依据「元音误拼 + 键盘邻键 + 视觉 / 语音混淆」，每个字母只给少量候选。
    /// </summary>
    private static readonly Dictionary<char, string> SubstitutionAlternatives = new()
    {
        ['a'] = "eiou",
        ['e'] = "aiou",
        ['i'] = "aeou",
        ['o'] = "aeiu",
        ['u'] = "aeio",
        ['b'] = "pv",
        ['p'] = "b",
        ['v'] = "bf",
        ['f'] = "v",
        ['d'] = "t",
        ['t'] = "d",
        ['c'] = "ksz",
        ['k'] = "csq",
        ['s'] = "cz",
        ['z'] = "s",
        ['g'] = "j",
        ['j'] = "g",
        ['m'] = "n",
        ['n'] = "m",
        ['l'] = "r",
        ['r'] = "l",
        ['q'] = "k",
        ['w'] = "v",
    };

    /// <summary>拼音兜底趟**每个音节**的变体上限（含原形；T1-6 模糊音扩展）。</summary>
    public const int MaxPinyinVariantsPerSyllable = 4;

    /// <summary>声母模糊音对（双向）：平翘舌、n/l、f/h（方言常见混淆）。</summary>
    private static readonly (string A, string B)[] PinyinInitialFuzz =
    {
        ("zh", "z"),
        ("ch", "c"),
        ("sh", "s"),
        ("n", "l"),
        ("f", "h"),
    };

    /// <summary>韵母模糊音对（双向，仅匹配词尾）：前后鼻音。</summary>
    private static readonly (string A, string B)[] PinyinFinalFuzz =
    {
        ("ang", "an"),
        ("eng", "en"),
        ("ing", "in"),
    };

    /// <summary>把单个词元转义为 FTS5 双引号字符串（内部 <c>"</c> 翻倍）。</summary>
    public static string Quote(string token) => "\"" + token.Replace("\"", "\"\"") + "\"";

    /// <summary>编译主趟表达式；无可匹配内容返回 <c>null</c>。</summary>
    /// <param name="lyricsInitials">
    /// 索引是否包含歌词首字母列（<see cref="StoreMeta.InitialsLyrics"/>）。默认
    /// <c>true</c> 保持纯编译 / 黄金向量调用方的旧行为；真实查询由计划编译器按
    /// Manifest 选项位传入。
    /// </param>
    /// <param name="includePrefix">
    /// 是否允许前缀 <c>*</c>（Q5）：由方案是否声明 <see cref="SchemeMatchers.Prefix"/>
    /// 决定。为 <c>false</c> 时主表达式**不含任何** <c>*</c>（末词前缀、单字前缀改写、
    /// 单音节拼音前缀全部关闭），让 <c>minimal@1</c> 真正只做精确匹配。
    /// 默认 <c>true</c> 保持既有调用方（黄金向量）字节不变。
    /// </param>
    /// <param name="minPinyinPrefixLength">
    /// 单音节拼音前缀的最小长度（Q3）；默认取 <see cref="MinPinyinPrefixTokenLength"/>。
    /// </param>
    public static string? CompileMatch(
        QueryTokens tokens, bool metadata, bool includeCrossLingual = false,
        bool lyricsInitials = true, bool includePrefix = true,
        int minPinyinPrefixLength = MinPinyinPrefixTokenLength)
    {
        var branches = new List<string>();
        Add(branches, LexicalBranch(tokens.Lexical, includePrefix));
        var singleChar = RestrictSingleCharToMetadata(tokens);
        if (!singleChar && AllowReadingBranch(tokens))
        {
            // 单字查询已由下面的列门控处理（拉丁单字经 initials_meta_tok 命中），
            // 这里只负责多字查询的拼音 / 首字母支。
            Add(branches, PinyinBranch(tokens, includePrefix, minPinyinPrefixLength));
            Add(branches, InitialsBranch(tokens, lyricsInitials));
            // 用户直接键入的首字母串（`wan` / `wanzg`）按 bigram 匹配首字母列（T3.9）。
            Add(branches, LatinInitialsBranch(tokens, lyricsInitials));
        }
        if (includeCrossLingual) Add(branches, CrossLingualBranch(tokens));
        if (singleChar)
        {
            if (branches.Count == 0) return null;
            var joined = string.Join(" OR ", branches);
            var columns = tokens.HasCjk ? VisibleTextColumns : SingleCharLatinColumns;
            return $"{{{columns}}} : ({joined})";
        }
        return JoinBranches(branches, metadata);
    }

    /// <summary>仅编译词法支（不含拼音 / 首字母），供字段亲和度探测等诊断用途。</summary>
    public static string? CompileLexical(QueryTokens tokens, bool includePrefix = true)
        => LexicalBranch(tokens.Lexical, includePrefix);

    /// <summary>跨语言候选分支（T1）：候选逐字加引号后 OR；无候选返回 <c>null</c>。</summary>
    public static string? CompileCrossLingual(QueryTokens tokens, bool metadata)
    {
        var branch = CrossLingualBranch(tokens);
        if (branch is null) return null;
        return metadata ? $"{{{MetadataColumns}}} : {branch}" : branch;
    }

    /// <summary>
    /// 拼音宽松兜底支：音节位置无关 <c>AND</c>，末音节 ≥3 字符前缀；&lt;2 音节返回 <c>null</c>。
    ///
    /// **模糊音扩展**（T1-6）：每个音节（仅限有效拼音音节，英文词不扩展）按声母
    /// <c>zh/z、ch/c、sh/s、n/l、f/h</c> 与韵母 <c>ang/an、eng/en、ing/in</c> 生成
    /// 至多 <see cref="MaxPinyinVariantsPerSyllable"/> 个变体（原形 + 声母 + 韵母 + 两者组合），
    /// 同一音节内 <c>OR</c>，音节之间仍 <c>AND</c>。只出现在兜底趟，不增加主趟成本。
    /// </summary>
    public static string? CompilePinyinLoose(
        QueryTokens tokens, bool metadata, bool includePrefix = true)
    {
        // 缺读音字的 CJK 查询不可做「音节位置无关 AND」：少一个音节就退化成高频单音节。
        if (tokens.HasCjk && !tokens.PinyinComplete) return null;
        var clean = Clean(tokens.Pinyin);
        if (clean.Count < 2) return null;

        // 总变体预算从尾部音节开始分配（尾部通常是查询里最不确定的部分），
        // 预算耗尽后剩余音节退回纯原形，避免超长输入的组合爆炸。
        var budget = Math.Max(1, DefaultMaxPinyinVariants);
        var variantsPerSyllable = new List<List<string>>(clean.Count);
        for (var i = 0; i < clean.Count; i++) variantsPerSyllable.Add(new List<string>());
        for (var i = clean.Count - 1; i >= 0; i--)
        {
            var take = Math.Min(MaxPinyinVariantsPerSyllable, budget);
            var variants = PinyinFuzzyVariants(clean[i], take);
            variantsPerSyllable[i] = variants;
            budget = Math.Max(0, budget - variants.Count);
        }

        var chunks = new List<string>(clean.Count);
        for (var i = 0; i < clean.Count; i++)
        {
            // 兜底趟的末音节前缀沿用字面门槛（MinPrefixTokenLength）：这是「多音节短语
            // 放宽」，不是 Q3 讨论的「单音节前缀扩到另一个音节」，右界必须更有判别力。
            var prefix = includePrefix && i == clean.Count - 1 && clean[i].Length >= MinPrefixTokenLength;
            var variants = variantsPerSyllable[i];
            if (variants.Count == 1)
            {
                chunks.Add(prefix ? Quote(variants[0]) + "*" : Quote(variants[0]));
                continue;
            }
            var group = string.Join(
                " OR ", variants.Select(v => prefix ? Quote(v) + "*" : Quote(v)));
            chunks.Add("(" + group + ")");
        }

        var body = string.Join(" AND ", chunks);
        return metadata
            ? $"{{{MetadataColumns}}} : ({body})"
            : $"{{{PinyinColumns}}} : ({body})";
    }

    /// <summary>
    /// 单个音节的模糊音变体（含原形，最多 <paramref name="maxVariants"/> 个）：
    /// 原形 → 声母替换 → 韵母替换 → 两者组合。非有效拼音音节（英文词）原样返回。
    /// </summary>
    private static List<string> PinyinFuzzyVariants(string syllable, int maxVariants)
    {
        var output = new List<string> { syllable };
        if (maxVariants <= 1 || !PinyinSegmenter.IsSyllable(syllable)) return output;

        var initial = SwapAffix(syllable, PinyinInitialFuzz, prefix: true);
        var final = SwapAffix(syllable, PinyinFinalFuzz, prefix: false);

        void Add(string? candidate)
        {
            if (candidate is null || candidate.Length == 0) return;
            if (output.Count >= maxVariants) return;
            if (!output.Contains(candidate, StringComparer.Ordinal)) output.Add(candidate);
        }

        Add(initial);
        Add(final);
        if (initial is not null) Add(SwapAffix(initial, PinyinFinalFuzz, prefix: false));
        return output;
    }

    /// <summary>
    /// 在词首（声母）或词尾（韵母）把模糊音对的一侧换成另一侧；无匹配返回 <c>null</c>。
    /// 两侧都试（双向），故「输入正确拼音」与「输入方言混淆拼音」都能召回。
    /// </summary>
    private static string? SwapAffix(
        string text, (string A, string B)[] pairs, bool prefix)
    {
        foreach (var (a, b) in pairs)
        {
            if (prefix)
            {
                if (text.StartsWith(a, StringComparison.Ordinal)) return b + text[a.Length..];
                if (text.StartsWith(b, StringComparison.Ordinal)) return a + text[b.Length..];
            }
            else
            {
                if (text.EndsWith(a, StringComparison.Ordinal)) return text[..^a.Length] + b;
                if (text.EndsWith(b, StringComparison.Ordinal)) return text[..^b.Length] + a;
            }
        }
        return null;
    }

    /// <summary>日文读音兜底支：拉丁 / 数字词元的字母 bigram 短语 AND 到读音列；无词元返回 <c>null</c>。</summary>
    public static string? CompileReading(QueryTokens tokens, bool metadata)
    {
        var parts = new List<string>();
        foreach (var token in tokens.Lexical)
        {
            // 罗马字规范形（T1-1）：Hepburn / Kunrei 折叠后与索引侧同构；
            // 「ん + 元音」额外给放宽变体，两候选 OR（原始形态始终保留）。
            var sequences = ReadingSequences(token);
            if (sequences.Count == 0) continue;

            // 单个引号短语 = 相邻 bigram 序列（FTS5 短语语义），等价于对该词元做子串检索。
            var phrases = sequences.Select(s => Quote(string.Join(' ', s))).ToList();
            parts.Add(phrases.Count == 1 ? phrases[0] : $"({string.Join(" OR ", phrases)})");
        }
        if (parts.Count == 0) return null;
        var body = string.Join(" AND ", parts);
        return metadata
            ? $"{{{MetadataColumns}}} : ({body})"
            : $"{{{ReadingColumns}}} : ({body})";
    }

    /// <summary>
    /// 查询中可走读音趟的词元的 <b>规范形 bigram 序列</b>（每个拼写变体一条）；
    /// 供 <see cref="ReadingRanker"/> 判定「前缀 / 包含」档位（T1-4）。
    /// </summary>
    public static List<string[]> ReadingSequences(QueryTokens tokens)
    {
        var output = new List<string[]>();
        foreach (var token in tokens.Lexical) AddReadingSequences(token, output);
        return output;
    }

    private static List<string[]> ReadingSequences(string token)
    {
        var output = new List<string[]>();
        AddReadingSequences(token, output);
        return output;
    }

    private static void AddReadingSequences(string token, List<string[]> output)
    {
        var lower = token.ToLowerInvariant();
        if (lower.Length < 2 || HasIndexedScript(lower)) return;

        foreach (var variant in RomajiCanonical.Variants(lower))
        {
            if (variant.Length < 2) continue;
            var bigrams = new List<string>(variant.Length - 1);
            for (var i = 0; i + 1 < variant.Length; i++) bigrams.Add(variant.Substring(i, 2));
            if (bigrams.Count > 0) output.Add(bigrams.ToArray());
        }
    }

    /// <summary>查询是否含可做编辑距离扩展的拉丁 / 数字词法词元。</summary>
    public static bool HasFuzzyEligible(QueryTokens tokens) => tokens.Lexical.Any(IsFuzzyEligible);

    /// <summary>查询是否含 typo2 可扩展词元（≥7 字符拉丁 / 数字，仅 recall@1 使用）。</summary>
    public static bool HasTypo2Eligible(QueryTokens tokens) => tokens.Lexical.Any(IsTypo2Eligible);

    /// <summary>编译模糊（容错）表达式：拉丁 / 数字词元换成编辑距离 ≤1 变体 OR 组。</summary>
    public static string? CompileFuzzy(
        QueryTokens tokens, bool metadata, int maxVariants = DefaultMaxFuzzyVariants,
        bool lyricsInitials = true, bool includePrefix = true)
        => CompileFuzzy(tokens, metadata, vocabulary: null, maxVariants, lyricsInitials, includePrefix);

    /// <summary>
    /// 同上，但用索引词表（T1-8）过滤候选：只保留索引里确实存在、且与查询词
    /// Damerau 距离 ≤1 的 term；词表为空（加载失败 / 空库）时退回无词典行为。
    /// </summary>
    public static string? CompileFuzzy(
        QueryTokens tokens, bool metadata, IReadOnlyList<string>? vocabulary,
        int maxVariants = DefaultMaxFuzzyVariants, bool lyricsInitials = true,
        bool includePrefix = true, int minPinyinPrefixLength = MinPinyinPrefixTokenLength)
    {
        var branches = new List<string>();
        Add(branches, FuzzyLexicalBranch(tokens.Lexical, maxVariants, vocabulary, includePrefix));
        Add(branches, PinyinBranch(tokens, includePrefix, minPinyinPrefixLength));
        Add(branches, InitialsBranch(tokens, lyricsInitials));
        Add(branches, LatinInitialsBranch(tokens, lyricsInitials));
        return JoinBranches(branches, metadata);
    }

    /// <summary>
    /// 编译 typo2 表达式（S2 / T2.6，仅 <c>recall@1</c>）：对长度 ≥7 的拉丁 / 数字词元，
    /// 取词表中 Damerau 距离**恰为 2**、首字符相同的真实词条；词表为空时返回 <c>null</c>
    /// （没有词表就无法枚举距离 2 的候选，不退回变体枚举以免组合爆炸）。
    /// </summary>
    public static string? CompileTypo2(
        QueryTokens tokens, bool metadata, IReadOnlyList<string>? vocabulary,
        int maxVariants = DefaultMaxTypo2Variants)
    {
        if (vocabulary is not { Count: > 0 } || maxVariants <= 0) return null;
        var branch = Typo2LexicalBranch(tokens.Lexical, maxVariants, vocabulary);
        if (branch is null) return null;
        var branches = new List<string>();
        Add(branches, branch);
        return JoinBranches(branches, metadata);
    }

    /// <summary>
    /// typo2 的纯词法支：只扩展 ≥7 字符词元，其余词元保持精确；没有任何扩展时返回 <c>null</c>
    /// （这一趟的语义是「多错一个字符」，不负责普通字面召回）。
    /// </summary>
    private static string? Typo2LexicalBranch(
        IReadOnlyList<string> terms, int maxVariants, IReadOnlyList<string> vocabulary)
    {
        var clean = Clean(terms);
        if (clean.Count == 0) return null;

        var chunks = new List<string>(clean.Count);
        var expanded = false;
        foreach (var word in clean)
        {
            if (IsTypo2Eligible(word))
            {
                var candidates = FuzzyVocabulary.DistanceTwoCandidates(vocabulary, word, maxVariants);
                if (candidates.Count > 0)
                {
                    expanded = true;
                    var quoted = new List<string>(candidates.Count);
                    foreach (var candidate in candidates) quoted.Add(Quote(candidate));
                    chunks.Add(quoted.Count == 1 ? quoted[0] : "(" + string.Join(" OR ", quoted) + ")");
                    continue;
                }
            }
            chunks.Add(Quote(word));
        }
        return expanded ? string.Join(" AND ", chunks) : null;
    }

    /// <summary>
    /// 生成 CJK 编辑距离 ≤1 变体（删除任一字符 / 相邻换位）；不含原串，去重保序。
    /// 子序列长度 &lt;<see cref="MinCjkFuzzyLength"/> 或 &gt;<see cref="MaxCjkQueryLength"/>
    /// 返回空；查询混入拉丁 / 数字时也返回空（容错只能覆盖索引脚本，丢掉它们会改变原查询）。
    /// </summary>
    public static List<string> CjkVariants(string query, int maxVariants = DefaultMaxCjkVariants)
    {
        if (HasNonIndexedLetterOrDigit(query)) return new List<string>();
        var sequence = CjkSubsequence(query);
        if (sequence.Length < MinCjkFuzzyLength || sequence.Length > MaxCjkQueryLength)
            return new List<string>();

        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { sequence };

        void Add(string candidate)
        {
            if (candidate.Length == 0) return;
            if (seen.Add(candidate)) output.Add(candidate);
        }

        // 删除：去掉任一字符（用户多打一字）。
        for (var i = 0; i < sequence.Length; i++) Add(sequence.Remove(i, 1));
        // 相邻换位（跳过相同字符对：换位无意义且与原串相同）。
        for (var i = 0; i + 1 < sequence.Length; i++)
        {
            if (sequence[i] == sequence[i + 1]) continue;
            var chars = sequence.ToCharArray();
            (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
            Add(new string(chars));
        }

        if (output.Count > maxVariants) return output.GetRange(0, maxVariants);
        return output;
    }

    /* ------------------------------------------------------------------ */
    /* 分支                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 字面支的一个块（<see cref="LexicalBranch"/> 与 <see cref="ShapeOf"/> 共用）：
    /// 连续 bigram 段是一个**短语块**（≥2 词时按位置连续引用），独立词元是一个词块；
    /// 末词 / 单字可被改写成前缀词块。
    /// </summary>
    private readonly record struct LexicalChunk(
        IReadOnlyList<string> Terms, bool IsPhrase, bool IsPrefix);

    /// <summary>
    /// 字面支的块划分（**唯一规则**）：连续 CJK bigram 合并为短语块；末词 ≥3 字符可加前缀；
    /// 独立单字 CJK 改前缀（索引里已无中间字 unigram，analyzer v13）。
    ///
    /// <para><paramref name="includePrefix"/> 为 <c>false</c> 时（Q5：
    /// <c>minimal@1</c> 只声明 exact）不做**任何**前缀改写：末词保持精确词元，
    /// 独立单字也不再写成 <c>字*</c>。</para>
    /// </summary>
    private static List<LexicalChunk> LexicalChunks(List<string> clean, bool includePrefix = true)
    {
        var chunks = new List<LexicalChunk>();
        if (clean.Count == 0) return chunks;

        var i = 0;
        while (i < clean.Count)
        {
            if (IsCjkBigram(clean[i]))
            {
                var j = i;
                while (j < clean.Count && IsCjkBigram(clean[j])) j++;
                var run = clean.GetRange(i, j - i);
                // 连续 ≥2 个 bigram → 引号短语；单个 bigram → 单个词元。
                chunks.Add(new LexicalChunk(run, run.Count >= 2, false));
                i = j;
            }
            else
            {
                chunks.Add(new LexicalChunk(new[] { clean[i] }, false, false));
                i++;
            }
        }

        var last = clean[^1];
        if (includePrefix && IsPrefixable(last) && last.Length >= MinPrefixTokenLength
            && !chunks[^1].IsPhrase)
        {
            chunks[^1] = chunks[^1] with { IsPrefix = true };
        }

        // 索引里已无「中间字」的 unigram（analyzer v13），单字必须改走前缀 `字*`，
        // 才能命中以该字开头的 bigram。只改独立成块的单字，短语块保持原样。
        // Q5：exact-only 方案（minimal@1）不做该改写——它只允许「索引里真实存在的词元」。
        if (!includePrefix) return chunks;
        for (var k = 0; k < chunks.Count; k++)
        {
            if (chunks[k].IsPhrase || chunks[k].Terms.Count != 1) continue;
            if (!IsSingleCjkChar(chunks[k].Terms[0])) continue;
            chunks[k] = chunks[k] with { IsPrefix = true };
        }
        return chunks;
    }

    private static string? LexicalBranch(IReadOnlyList<string> terms, bool includePrefix = true)
    {
        var chunks = LexicalChunks(Clean(terms), includePrefix);
        if (chunks.Count == 0) return null;

        var parts = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.IsPhrase) parts.Add(Quote(string.Join(' ', chunk.Terms)));
            else if (chunk.IsPrefix) parts.Add(PrefixTerm(chunk.Terms[0]));
            else parts.Add(Quote(chunk.Terms[0]));
        }
        return string.Join(" AND ", parts);
    }

    /* ------------------------------------------------------------------ */
    /* 表达式形状（T2.5 kind 分类；单一来源）                              */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 主字面步骤（<c>lexical@1</c>）的形状：**由词元与编译规则推出，不再解析表达式字符串**。
    ///
    /// <para>判定顺序（强语义优先）：</para>
    /// <list type="number">
    ///   <item><b>纯拼音输入</b>：查询不含汉字、每个词元都是 ASCII 小写、整串能完整切成音节
    ///         （<c>woaini</c> / <c>qing tian</c>）→ 1 音节 <c>PinyinExact</c>，
    ///         ≥2 音节 <c>PinyinPhrase</c>。字面支只是同一串的拉丁整词查找，几乎不命中，
    ///         真正的召回路径是拼音列。</item>
    ///   <item><b>首字母串</b>：单个 ASCII 小写词元、不是拼音、且辅音密度符合首字母
    ///         （元音 ≤1 个，如 <c>qt</c> / <c>wanzg</c>）→ <c>Initials</c>。
    ///         <c>hello</c> / <c>love</c> 这类词元元音多，保留字面档（<c>Prefix</c>）。</item>
    ///   <item><b>字面形状</b>：单个短语块 → <c>Phrase</c>；单个词块 → <c>Prefix</c>
    ///         （末词前缀 / 单字前缀改写）或 <c>Exact</c>；多个真实词块（含「歌手 + 歌名」）
    ///         是跨词元散落 AND → <c>Partial</c>。analyzer 追加的段首 / 段尾 unigram
    ///         兜底词元不算独立词（见 <see cref="IsBoundaryUnigram"/>）。</item>
    /// </list>
    ///
    /// <para><b>scope 不影响形状</b>：<c>metadata</c> / <c>lyrics</c> 只改列过滤
    /// （见 <see cref="CompileMatch"/> 的外层列集合），召回的「形状」仍是同一套词元规则，
    /// 因此本函数只吃 <see cref="QueryTokens"/>，保持纯函数。</para>
    /// </summary>
    public static QueryShape ShapeOf(QueryTokens tokens, bool includePrefix = true)
    {
        var clean = Clean(tokens.Lexical);

        // 1) 纯拼音输入（无汉字 + 全部可切分）。
        if (!tokens.HasCjk && clean.Count > 0 && TryPinyinInput(clean, out var syllables))
        {
            return new QueryShape(
                syllables <= 1 ? QueryShapeKind.PinyinExact : QueryShapeKind.PinyinPhrase,
                Terms: clean.Count);
        }

        // 2) 首字母串（单个、非拼音、辅音密度像首字母）。
        if (!tokens.HasCjk && clean.Count == 1 && IsInitialsShaped(clean[0]))
            return new QueryShape(QueryShapeKind.Initials, Terms: 1);

        // 3) 字面形状。
        var chunks = LexicalChunks(clean, includePrefix);
        if (chunks.Count == 0)
        {
            // 无字面词元（标点 / 纯空白）：退回拼音 / 首字母支的形状。
            var pinyin = Clean(tokens.Pinyin).Count;
            if (pinyin > 0)
            {
                return new QueryShape(pinyin <= 1
                    ? QueryShapeKind.PinyinExact
                    : QueryShapeKind.PinyinPhrase);
            }
            return new QueryShape(tokens.PinyinInitials is { Length: > 0 }
                ? QueryShapeKind.Initials
                : QueryShapeKind.Exact);
        }

        if (chunks.Count == 1)
        {
            var chunk = chunks[0];
            if (chunk.IsPhrase)
                return new QueryShape(QueryShapeKind.Phrase, chunk.Terms.Count, chunks.Count);
            return new QueryShape(
                chunk.IsPrefix ? QueryShapeKind.Prefix : QueryShapeKind.Exact,
                Terms: chunks.Count);
        }

        // analyzer 在每个 CJK 段后追加「首尾 unigram」兜底词元（v13）；它们不是独立词。
        // 形状只看**真实词块**：单字 unigram 若能由紧邻前面的 bigram 块解释，就从形状里剔除，
        // 否则「晴天」（[晴天, 晴, 天]）会被误判成散落多词。
        var words = 0;
        var single = default(LexicalChunk);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (IsBoundaryUnigram(chunks, i)) continue;
            words++;
            single = chunks[i];
        }

        // 多个真实词块 = 跨词元散落 AND（含「歌手 + 歌名」这类多词查询）→ Partial；
        // 单个词块才可能是 Exact / Prefix / Phrase（后者要求 ≥2 个 bigram 位置连续）。
        if (words != 1)
            return new QueryShape(QueryShapeKind.Partial, Terms: words);

        if (single.IsPhrase)
            return new QueryShape(QueryShapeKind.Phrase, single.Terms.Count, words);
        return new QueryShape(
            single.IsPrefix ? QueryShapeKind.Prefix : QueryShapeKind.Exact,
            Terms: words);
    }

    /// <summary>
    /// 该块是否是 analyzer 追加的「段首 / 段尾 unigram 兜底」：单字 CJK，且紧邻前面 1–2 个块里
    /// 有 bigram 块的首字 / 末字与之相同（analyzer 每段先写 bigram 再写 2 个 unigram）。
    /// </summary>
    private static bool IsBoundaryUnigram(List<LexicalChunk> chunks, int index)
    {
        var chunk = chunks[index];
        if (chunk.Terms.Count != 1 || !IsSingleCjkChar(chunk.Terms[0])) return false;
        var character = chunk.Terms[0][0];
        var start = Math.Max(0, index - 2);
        for (var back = index - 1; back >= start; back--)
        {
            var previous = chunks[back];
            if (previous.Terms.Count == 0 || !IsCjkBigram(previous.Terms[0])) continue;
            var first = previous.Terms[0];
            var last = previous.Terms[^1];
            if ((first.Length > 0 && first[0] == character)
                || (last.Length > 0 && last[^1] == character))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 邻近度用的**真实词**序列：剔除 analyzer 追加的段首 / 段尾 unigram 兜底词元
    /// （见 <see cref="IsBoundaryUnigram"/>）。短语块的词元全部保留（位置连续才有意义）。
    ///
    /// <para>例：<c>晴天</c> → <c>[晴天]</c>（单真实词），<c>我爱你</c> → <c>[我爱, 爱你]</c>，
    /// <c>hello world</c> → <c>[hello, world]</c>。单真实词的邻近度按 spec 记 0，
    /// 不按「词在字段里的位置」记惩罚。</para>
    /// </summary>
    public static IReadOnlyList<string> ProximityTerms(IReadOnlyList<string> terms)
    {
        var chunks = LexicalChunks(Clean(terms));
        if (chunks.Count == 0) return Array.Empty<string>();
        if (chunks.Count == 1) return chunks[0].Terms;

        var output = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (IsBoundaryUnigram(chunks, i)) continue;
            output.AddRange(chunks[i].Terms);
        }
        return output;
    }

    /// <summary>
    /// 拼音列步骤（<c>pinyin-loose@1</c>）的 kind：1 音节 <c>PinyinExact</c>，
    /// ≥2 音节 <c>PinyinPhrase</c>；无音节返回 <see cref="QueryShapeKind.Partial"/>（调用方应忽略）。
    /// </summary>
    public static QueryShapeKind PinyinKindOf(QueryTokens tokens)
    {
        var count = Clean(tokens.Pinyin).Count;
        if (count == 0) return QueryShapeKind.Partial;
        return count <= 1 ? QueryShapeKind.PinyinExact : QueryShapeKind.PinyinPhrase;
    }

    /// <summary>全部词元都是 ASCII 小写、且整串可完整切分为有效拼音音节。</summary>
    private static bool TryPinyinInput(List<string> tokens, out int syllables)
    {
        syllables = 0;
        if (tokens.Count == 0) return false;
        var buffer = new List<string>(4);
        foreach (var token in tokens)
        {
            if (!IsAsciiLowerWord(token)) return false;
            if (PinyinSegmenter.IsSyllable(token))
            {
                syllables++;
                continue;
            }
            if (!PinyinSegmenter.TrySegment(token, buffer)) return false;
            syllables += buffer.Count;
        }
        return syllables > 0;
    }

    /// <summary>
    /// 单个词元是否像**首字母串**：ASCII 小写、≥2 字符、有首字母 bigram，
    /// 且元音 ≤1（<c>qt</c> / <c>xzq</c> / <c>wanzg</c>）。拉丁整词（<c>hello</c> / <c>love</c>）
    /// 元音 ≥2，保留字面档——两者共用同一个表达式（首字母支是 OR 里的一项），
    /// 这里只决定 explain / 排序用的 kind 标签。
    /// </summary>
    private static bool IsInitialsShaped(string token)
    {
        if (!IsAsciiLowerWord(token) || token.Length < MinInitialsBranchLength) return false;
        if (Analyzer.InitialsToBigrams(token).Length == 0) return false;
        var vowels = 0;
        foreach (var ch in token)
        {
            if (ch is 'a' or 'e' or 'i' or 'o' or 'u' or 'y') vowels++;
            if (vowels > 1) return false;
        }
        return true;
    }

    /// <summary>单音节拼音支（Q3 的可测试入口：视图与 <see cref="CompileMatch"/> 同源）。</summary>
    internal static string? PinyinBranch(
        QueryTokens tokens, bool includePrefix = true,
        int minPinyinPrefixLength = MinPinyinPrefixTokenLength)
    {
        // 缺读音字的 CJK 查询：拼音整段串会少音节，退化成高频单音节宽命中。
        if (tokens.HasCjk && !tokens.PinyinComplete) return null;
        var clean = Clean(tokens.Pinyin);
        if (clean.Count == 0) return null;
        if (clean.Count == 1)
        {
            // Q3：单音节前缀门槛独立于字面门槛，3 字符完整音节（wan）加前缀会扩到
            // 另一个音节（wang）；同一门槛也保「边打边搜」的部分拼音（zho → zhong）。
            var prefix = includePrefix && clean[^1].Length >= minPinyinPrefixLength;
            return $"{{{PinyinColumns}}} : {QuotedAnd(clean, prefix)}";
        }
        return $"{{{PinyinColumns}}} : {Quote(string.Join(' ', clean))}";
    }

    private static string? InitialsBranch(QueryTokens tokens, bool lyricsInitials = true)
    {
        if (tokens.HasCjk && !tokens.PinyinComplete) return null;
        if (string.IsNullOrWhiteSpace(tokens.PinyinInitials)) return null;
        var runs = tokens.PinyinInitials!.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return InitialsExpression(runs, lyricsInitials);
    }

    /// <summary>
    /// 纯拉丁查询串的首字母支（T3.9）：用户直接键入 <c>wan</c> / <c>wanzg</c> 时，
    /// 按与索引侧相同的相邻 bigram 规则匹配首字母列。
    ///
    /// 旧实现靠「词法支整词 = 整段首字母 token」偶然命中；首字母列改成 bigram 后必须显式
    /// 分解，否则「用首字母搜歌词」这条产品能力（设置项文案里的 <c>wanzg</c>）会消失。
    /// 只接受纯 ASCII 小写字母词元（含数字 / 标点的词不可能是首字母串）；单字母无可匹配
    /// bigram，仍由 <see cref="MinInitialsBranchLength"/> 拦下。索引侧不再写拉丁首字母，
    /// 因此这条支只会命中 CJK 首字母。
    /// </summary>
    private static string? LatinInitialsBranch(QueryTokens tokens, bool lyricsInitials)
    {
        if (tokens.Lexical.Count == 0) return null;
        var runs = new List<string>(tokens.Lexical.Count);
        foreach (var token in tokens.Lexical)
        {
            if (token.Length < MinInitialsBranchLength || !IsAsciiLowerWord(token)) continue;
            runs.Add(token);
        }
        return InitialsExpression(runs, lyricsInitials);
    }

    /// <summary>
    /// 首字母 bigram 表达式（索引 / 查询同构）：
    ///   · 2 字符段 → 单个 bigram 词元（段中间也能命中）；
    ///   · ≥3 字符段 → bigram 短语（位置相邻），整段 / 段内子串都能命中，
    ///     且不会把同一批 bigram 的乱序排列当成命中；
    ///   · 1 字符段 / 无 bigram 段 → 跳过。
    /// 多个 CJK 段之间用 AND：索引列在段 / 字段之间插了边界词元，不能拼成一条跨段短语。
    /// </summary>
    private static string? InitialsExpression(IReadOnlyList<string> runs, bool lyricsInitials)
    {
        var phrases = new List<string>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Length < MinInitialsBranchLength) continue;
            var bigrams = Analyzer.InitialsToBigrams(run);
            if (bigrams.Length == 0) continue;
            phrases.Add(Quote(bigrams));
        }
        if (phrases.Count == 0) return null;
        var body = string.Join(" AND ", phrases);
        // 整体加括号：列过滤与 OR 混排时边界更明确（列过滤只作用于本支）。
        // 注意多列过滤必须用**花括号**：FTS5 的 `a b : expr` 是「列 a 后接词元 b」，
        // 不是列集合，会静默返回空集（本分支因此从不命中）。
        // 歌词首字母关闭时只扫元数据列（T3.4 V4）：结果与「空歌词列」一致，
        // 但 explain / 成本估算不再假装歌词首字母参与召回。
        var columns = lyricsInitials ? InitialsColumns : InitialsMetaColumns;
        return $"({{{columns}}} : {body})";
    }

    /// <summary>纯 ASCII 小写字母词元（归一化后文本；首字母串只可能是这种形态）。</summary>
    private static bool IsAsciiLowerWord(string token)
    {
        if (token.Length == 0) return false;
        foreach (var ch in token)
            if (ch < 'a' || ch > 'z') return false;
        return true;
    }

    /// <summary>
    /// 跨语言候选分支。查询只含一个汉字时，丢掉「汉字 → 英文释义」方向的候选：
    /// 单字释义极度发散，会把这个查询放大到近乎整库；「拉丁词 → 候选汉字」方向不受影响。
    /// </summary>
    private static string? CrossLingualBranch(QueryTokens tokens)
    {
        var clean = Clean(tokens.CrossLingual);
        if (clean.Count == 0) return null;
        if (tokens.CjkCharCount > 0 && tokens.CjkCharCount < MinCrossLingualCjkLength)
        {
            clean = clean.Where(IsCjkText).ToList();
            if (clean.Count == 0) return null;
        }
        return clean.Count == 1
            ? Quote(clean[0])
            : "(" + string.Join(" OR ", clean.Select(Quote)) + ")";
    }

    /// <summary>单个变体前缀下最多枚举多少个真实词条；超过则退回前缀匹配（避免枚举爆炸）。</summary>
    private const int MaxExactTermsPerVariant = 32;

    private static string? FuzzyLexicalBranch(
        IReadOnlyList<string> terms, int maxVariants, IReadOnlyList<string>? vocabulary,
        bool includePrefix = true)
    {
        var clean = Clean(terms);
        if (clean.Count == 0) return null;

        var useVocabulary = vocabulary is { Count: > 0 };
        var expandable = new List<int>();
        for (var i = 0; i < clean.Count; i++)
        {
            if (IsFuzzyEligible(clean[i])) expandable.Add(i);
        }

        // 每个可扩展词元 → 一批**已加引号**的 FTS 表达式（精确 term 或前缀形态）。
        var groups = new Dictionary<int, List<string>>();
        foreach (var i in expandable)
        {
            var prefix = includePrefix && i == clean.Count - 1
                && IsPrefixable(clean[i])
                && clean[i].Length >= MinPrefixTokenLength;
            if (!useVocabulary)
            {
                groups[i] = EditDistanceOneVariants(clean[i])
                    .Select(v => prefix ? Quote(v) + "*" : Quote(v))
                    .ToList();
                continue;
            }

            var limited = vocabulary!.Count > 0 && maxVariants > 0
                ? VocabularyExpressions(clean[i], vocabulary, maxVariants, includePrefix)
                : new List<string>();
            groups[i] = limited;
        }

        var total = groups.Values.Sum(list => list.Count);
        if (!useVocabulary && total > maxVariants && expandable.Count > 0)
        {
            // 预算超限：只扩展最后一个可扩展词元（并截断），其余退回精确。
            var last = expandable[^1];
            var lastPrefix = includePrefix && last == clean.Count - 1
                && IsPrefixable(clean[last])
                && clean[last].Length >= MinPrefixTokenLength;
            var full = EditDistanceOneVariants(clean[last])
                .Select(v => lastPrefix ? Quote(v) + "*" : Quote(v))
                .ToList();
            groups.Clear();
            groups[last] = full.Count > maxVariants ? full.GetRange(0, maxVariants) : full;
        }

        var chunks = new List<string>();
        var index = 0;
        while (index < clean.Count)
        {
            if (IsCjkBigram(clean[index]))
            {
                var j = index;
                while (j < clean.Count && IsCjkBigram(clean[j])) j++;
                var run = clean.GetRange(index, j - index);
                chunks.Add(Quote(run.Count >= 2 ? string.Join(' ', run) : run[0]));
                index = j;
                continue;
            }

            var prefix = includePrefix && index == clean.Count - 1
                && IsPrefixable(clean[index])
                && clean[index].Length >= MinPrefixTokenLength;
            if (groups.TryGetValue(index, out var list) && list.Count > 0)
            {
                chunks.Add(list.Count == 1 ? list[0] : "(" + string.Join(" OR ", list) + ")");
            }
            else if (useVocabulary && IsFuzzyEligible(clean[index]))
            {
                // 词表只覆盖文本列（见 SearchSession.LoadEraVocabulary）；拼音 / 首字母 /
                // 读音列里的词条不在其中，所以「词表无候选」≠「索引里不存在」。
                // 退回无词表的变体枚举，保住旧召回（如 "woo" 的删除变体 "wo" 只存在于拼音列）。
                var bare = EditDistanceOneVariants(clean[index])
                    .Select(v => prefix ? Quote(v) + "*" : Quote(v))
                    .ToList();
                if (bare.Count > maxVariants) bare = bare.GetRange(0, maxVariants);
                chunks.Add(bare.Count == 1 ? bare[0] : "(" + string.Join(" OR ", bare) + ")");
            }
            else
            {
                var quoted = Quote(clean[index]);
                chunks.Add(prefix ? quoted + "*" : quoted);
            }
            index++;
        }
        return string.Join(" AND ", chunks);
    }

    /// <summary>
    /// 词典化的模糊候选（T1-8）：对每个编辑距离 ≤1 变体，取词表中同前缀的真实词条，
    /// 再用 Myers 位并行复核 Damerau 距离 ≤1；前缀下词条过多时退回前缀匹配（保留旧召回）。
    /// </summary>
    private static List<string> VocabularyExpressions(
        string word, IReadOnlyList<string> vocabulary, int maxVariants, bool includePrefix = true)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var variant in EditDistanceOneVariants(word))
        {
            var (start, end) = FuzzyVocabulary.PrefixRange(vocabulary, variant);
            if (end <= start) continue;

            if (end - start <= MaxExactTermsPerVariant)
            {
                for (var i = start; i < end && output.Count < maxVariants; i++)
                {
                    var term = vocabulary[i];
                    if (!FuzzyVocabulary.WithinOne(word, term)) continue;
                    if (seen.Add(term)) output.Add(Quote(term));
                }
                continue;
            }

            // 变体过短 / 过于常见：前缀匹配保留旧召回（否则要枚举上千词条）。
            // Q5：exact-only 方案不生成前缀形态——宁可不扩这一条变体，也不引入 `*`。
            if (!includePrefix) continue;
            var expression = Quote(variant) + "*";
            if (seen.Add(expression) && output.Count < maxVariants) output.Add(expression);
        }

        return output;
    }

    /* ------------------------------------------------------------------ */
    /* 丢词降级                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 丢掉第 <paramref name="dropIndex"/> 个词后的 MATCH 表达式；不适用时返回 <c>null</c>。
    ///
    /// 只重新编译「词法支 + 读音支」，不含跨语言 / 容错：本趟的语义是
    /// 「字面少匹配一个词」，不是放宽匹配维度。
    /// </summary>
    public static string? CompileDropTokens(
        QueryTokens tokens, string query, bool metadata, int dropIndex,
        bool lyricsInitials = true, bool includePrefix = true)
    {
        if (!AllowDropTokens) return null;
        var words = SplitQueryWords(query);
        if (words.Count < 2 || dropIndex < 0 || dropIndex >= words.Count) return null;

        var keptWords = new List<string>(words.Count - 1);
        for (var i = 0; i < words.Count; i++)
        {
            if (i != dropIndex) keptWords.Add(words[i]);
        }
        if (keptWords.Count == 0) return null;

        var keptTokens = Analyzer.AnalyzeQuery(string.Join(' ', keptWords));
        if (keptTokens.Lexical.Count == 0) return null;

        var expression = CompileMatch(
            keptTokens, metadata, includeCrossLingual: false, lyricsInitials, includePrefix);
        if (expression is null) return null;
        var original = CompileMatch(
            tokens, metadata, includeCrossLingual: false, lyricsInitials, includePrefix);
        return string.Equals(expression, original, StringComparison.Ordinal) ? null : expression;
    }

    /// <summary>
    /// 丢词降级的候选丢法，按优先级排序。
    ///
    /// 中文音乐检索的主流写法是「歌手 + 歌名」，歌名在尾部且最该保留，
    /// 因此含 CJK 的查询先丢首位词；纯拉丁查询仍按尾部优先。
    /// </summary>
    public static List<int> DropTokenCandidates(QueryTokens tokens, string query)
    {
        var words = SplitQueryWords(query);
        if (words.Count < 2) return new List<int>();
        var indices = new List<int>(words.Count);
        if (tokens.HasCjk)
        {
            for (var i = 0; i < words.Count; i++) indices.Add(i);
        }
        else
        {
            for (var i = words.Count - 1; i >= 0; i--) indices.Add(i);
        }
        return indices;
    }

    /// <summary>
    /// 按「词在 title / artist 列的命中分布」重排丢词候选：
    /// 越像歌名的词越晚丢，越像歌手的词越早丢；信息不足时保持原顺序。
    /// </summary>
    public static List<int> OrderDropsByFieldAffinity(
        List<int> candidates, IReadOnlyList<int> titleCounts, IReadOnlyList<int> artistCounts)
    {
        if (candidates.Count <= 1) return candidates;
        var positions = new Dictionary<int, int>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++) positions[candidates[i]] = i;

        var ordered = new List<int>(candidates);
        ordered.Sort((a, b) =>
        {
            var cmp = Affinity(a, titleCounts, artistCounts)
                .CompareTo(Affinity(b, titleCounts, artistCounts));
            return cmp != 0 ? cmp : positions[a].CompareTo(positions[b]);
        });
        return ordered;
    }

    private static int Affinity(int index, IReadOnlyList<int> titleCounts, IReadOnlyList<int> artistCounts)
        => index < 0 || index >= titleCounts.Count || index >= artistCounts.Count
            ? 0
            : titleCounts[index] - artistCounts[index];

    private static List<string> SplitQueryWords(string? query)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return words;
        foreach (var part in query.Trim().Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(part);
        }
        return words;
    }

    /* ------------------------------------------------------------------ */
    /* 工具                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 读音支（拼音 / 首字母）是否允许进入主趟：CJK 输入产出的拼音是派生的，
    /// 单字查询没有必要再扩宽；只有拉丁输入或 CJK 字数达标时才允许。
    /// </summary>
    private static bool AllowReadingBranch(QueryTokens tokens)
        => !tokens.HasCjk || tokens.CjkCharCount >= ReadingBranchMinCjkLength;

    /// <summary>单字查询的列门控：只在查询恰好含一个可见字符时生效。</summary>
    private static bool RestrictSingleCharToMetadata(QueryTokens tokens)
        => RequireMetadataForSingleChar && tokens.VisibleCharCount == 1;

    /// <summary>词元是否为单个索引脚本字符（这类词元在索引里不一定有独立 unigram，须走前缀）。</summary>
    private static bool IsSingleCjkChar(string token)
    {
        var runes = token.EnumerateRunes().ToArray();
        return runes.Length == 1 && runes[0].Value <= char.MaxValue
            && IsIndexedScript((char)runes[0].Value);
    }

    /// <summary>末词编译为 <c>("tok" OR "tok"*)</c>，或纯前缀 <c>"tok"*</c>。</summary>
    private static string PrefixTerm(string token)
        => PairExactWithPrefix
            ? "(" + Quote(token) + " OR " + Quote(token) + "*)"
            : Quote(token) + "*";

    /// <summary>整串是否只由索引脚本（汉字 / 假名 / 谚文）构成（跨语言候选归属判定用）。</summary>
    private static bool IsCjkText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value > char.MaxValue || !IsIndexedScript((char)rune.Value)) return false;
        }
        return true;
    }

    /// <summary>查询是否含不属于索引脚本的字母或数字（拉丁词 / 数字；中文容错趟的门控）。</summary>
    private static bool HasNonIndexedLetterOrDigit(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)
                && (rune.Value > char.MaxValue || !IsIndexedScript((char)rune.Value)))
            {
                return true;
            }
        }
        return false;
    }

    private static string? JoinBranches(List<string> branches, bool metadata)
    {
        if (branches.Count == 0) return null;
        var joined = string.Join(" OR ", branches);
        if (metadata)
        {
            // 列过滤必须作用于「整个」表达式——FTS5 中 `col : a AND b` 只过滤 `a`。
            return $"{{{MetadataColumns}}} : ({joined})";
        }
        return branches.Count == 1 ? joined : $"({joined})";
    }

    private static void Add(List<string> branches, string? branch)
    {
        if (branch is not null) branches.Add(branch);
    }

    private static List<string> Clean(IReadOnlyList<string> terms)
    {
        var clean = new List<string>(terms.Count);
        foreach (var term in terms)
        {
            var trimmed = term.Trim();
            if (trimmed.Length > 0) clean.Add(trimmed);
        }
        return clean;
    }

    private static string QuotedAnd(IReadOnlyList<string> terms, bool prefixLast)
    {
        var parts = new List<string>(terms.Count);
        for (var i = 0; i < terms.Count; i++)
        {
            var prefixed = i == terms.Count - 1 && prefixLast;
            parts.Add(prefixed ? PrefixTerm(terms[i]) : Quote(terms[i]));
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>生成 <paramref name="word"/> 的编辑距离 ≤1 变体（含原词首位；去重保序）。</summary>
    private static List<string> EditDistanceOneVariants(string word)
    {
        var output = new List<string> { word };
        var seen = new HashSet<string>(StringComparer.Ordinal) { word };

        void Add(string candidate)
        {
            if (candidate.Length > 0 && seen.Add(candidate)) output.Add(candidate);
        }

        // 删除：去掉任一字符。
        for (var i = 0; i < word.Length; i++) Add(word.Remove(i, 1));
        // 相邻换位（跳过相同字符对）。
        for (var i = 0; i + 1 < word.Length; i++)
        {
            if (word[i] == word[i + 1]) continue;
            var chars = word.ToCharArray();
            (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
            Add(new string(chars));
        }
        // 替换：逐位用受限候选替换。首字符不生成：编辑距离只有 ≤1 一档，
        // 首字符不同往往意味着另一个词（首错等价于 2 错）。
        for (var i = FirstCharTypoCostsTwo ? 1 : 0; i < word.Length; i++)
        {
            if (!SubstitutionAlternatives.TryGetValue(word[i], out var alternatives)) continue;
            foreach (var ch in alternatives)
                Add(word.Substring(0, i) + ch + word.Substring(i + 1));
        }
        return output;
    }

    /// <summary>恰为两个子串脚本字符的词元（索引 / 查询侧 bigram 形态）。</summary>
    private static bool IsCjkBigram(string token)
    {
        var runes = token.EnumerateRunes().ToArray();
        return runes.Length == 2
            && runes.All(r => r.Value <= char.MaxValue && IsIndexedScript((char)r.Value));
    }

    /// <summary>拉丁 / 数字词元可加前缀：不含子串脚本字符即为真。</summary>
    private static bool IsPrefixable(string token)
        => !token.EnumerateRunes().Any(r => r.Value <= char.MaxValue && IsIndexedScript((char)r.Value));

    /// <summary>纯小写拉丁 / 数字词元（可做编辑距离扩展）；过短词不扩展，避免高频噪声。</summary>
    private static bool IsFuzzyEligible(string token)
    {
        if (token.Length < MinFuzzyWordLength) return false;
        foreach (var rune in token.EnumerateRunes())
        {
            var isLower = rune.Value >= 0x61 && rune.Value <= 0x7A;
            var isDigit = rune.Value >= 0x30 && rune.Value <= 0x39;
            if (!isLower && !isDigit) return false;
        }
        return true;
    }

    /// <summary>
    /// typo2 可扩展词元：纯小写拉丁 / 数字且长度 ≥ <see cref="MinTypo2WordLength"/>。
    /// 与 typo1 的差别只有词长门槛（N22：3 字符词不启用）。
    /// </summary>
    private static bool IsTypo2Eligible(string token) => IsFuzzyEligible(token)
        && token.Length >= MinTypo2WordLength;

    /// <summary>需要子串索引的脚本：汉字 + 假名 + 谚文（与 Dart <c>isIndexedScript</c> 一致）。</summary>
    private static bool IsIndexedScript(char c)
        => ScriptRanges.IsCjk(c) || ScriptRanges.IsSubstringScript(c);

    private static bool HasIndexedScript(string token)
        => token.EnumerateRunes().Any(r => r.Value <= char.MaxValue && IsIndexedScript((char)r.Value));

    /// <summary>取查询中的 CJK / 假名 / 谚文子序列（去其它字符，保序）。</summary>
    private static string CjkSubsequence(string query)
    {
        var buffer = new StringBuilder(query.Length);
        foreach (var ch in query)
        {
            if (IsIndexedScript(ch)) buffer.Append(ch);
        }
        return buffer.ToString();
    }
}
