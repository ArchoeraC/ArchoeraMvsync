// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using ToolGood.Words;

using Archoera.Text;

namespace Archoera.Indexer;

/// <summary>
/// 可检索字段（枚举序号为 C ABI 契约，P2 定案后不得变更）。
/// </summary>
public enum Field
{
    Title = 0,
    Artist = 1,
    Album = 2,
    LyricsOriginal = 3,
    LyricsTranslation = 4,
    LyricsRomaji = 5,
    Filename = 6,
}

/// <summary>索引侧分析结果（词元 / 全拼 / 首字母 / 读音列，均为空格分隔串）。</summary>
public sealed class AnalysisResult
{
    public string Tokens { get; }

    public string Pinyin { get; }

    /// <summary>
    /// 日文读音列（字母 bigram；见 <see cref="Japanese.ReadingBigrams"/>）。
    /// 仅当该源含假名、或调用方标记整首为日文（<c>includeReadings</c>）时非空——
    /// 纯中文曲库不产出，既不增体积也无跨语言噪声。
    /// </summary>
    public string Readings { get; }

    /// <summary>
    /// **人类可读**的每段首字母串（空格分隔；拉丁 / 数字段不产出）。
    /// 查询侧与首字母高亮 / explain 使用这一列，永远不是 bigram。
    /// </summary>
    public string PinyinInitials { get; }

    /// <summary>
    /// 首字母倒排列的**索引词元**（T3.9 bigram）：每个 CJK 段内相邻首字母两两成词
    /// （<c>wanzg</c> → <c>wa an nz zg</c>），段与段之间用
    /// <see cref="Analyzer.InitialsBoundaryToken"/> 隔开，保证短语查询不会跨段命中。
    /// </summary>
    public string InitialsBigrams { get; }

    public AnalysisResult(
        string tokens, string pinyin, string pinyinInitials, string readings,
        string initialsBigrams = "")
    {
        Tokens = tokens;
        Pinyin = pinyin;
        PinyinInitials = pinyinInitials;
        Readings = readings;
        InitialsBigrams = initialsBigrams;
    }
}

/// <summary>查询侧结构化词元（供调用方按需拼 FTS5 表达式 / 前缀 / 短语）。</summary>
public sealed class QueryTokens
{
    public List<string> Lexical { get; } = new();

    public List<string> Pinyin { get; } = new();

    /// <summary>首字母串；文本不含 CJK 时为 <c>null</c>。</summary>
    public string? PinyinInitials { get; internal set; }

    /// <summary>
    /// 跨语言候选（**仅查询侧**，T1）：由 Unihan 释义扩展表（<c>def_en2hanzi</c> /
    /// <c>def_hanzi2en</c>）派生——拉丁/数字词元 → 候选汉字；CJK 段内的字 → 英文释义词。
    /// 合并去重后总预算 ≤ <see cref="Analyzer.CrossLingualBudget"/>（超出截断）。
    /// 索引侧不使用此列（T1 不重建索引）。
    /// </summary>
    public List<string> CrossLingual { get; } = new();

    /// <summary>查询是否含 CJK 段（汉字；假名 / 谚文不计）。</summary>
    public bool HasCjk { get; internal set; }

    /// <summary>
    /// CJK 段每个字都成功产出拼音音节。含无读音扩展汉字时为 <c>false</c>，
    /// 此时拼音 / 首字母支不可信（缺字会把短语退化成高频单音节），编译器应跳过它们。
    /// </summary>
    public bool PinyinComplete { get; internal set; } = true;

    /// <summary>
    /// 查询是否含不属于 CJK / 假名 / 谚文索引脚本的字母或数字（如拉丁字母、阿拉伯数字）。
    /// 中文容错趟只能覆盖索引脚本，混入其它字符时删字会改变原查询，故跳过该趟。
    /// </summary>
    public bool HasNonIndexedLetterOrDigit { get; internal set; }

    /// <summary>查询中非空白可见字符数（按码点计；单字判定用）。</summary>
    public int VisibleCharCount { get; internal set; }

    /// <summary>查询中 CJK 汉字数（按码点计；读音支 / 跨语言单字门控用）。</summary>
    public int CjkCharCount { get; internal set; }
}

/// <summary>
/// 本地曲库搜索分析器（核心，索引侧 / 查询侧同构）。
///
/// 链：NFKC（逐字符兼容折叠）→ 小写 → 繁转简 → 脚本切分 → 分词
/// （CJK bigram+unigram / 拉丁·数字整词）→ 派生列（全拼 / 首字母）。
/// 语义与设计文档 <c>docs/local-search-index-plan.md</c> §4 一致，
/// <see cref="AnalyzerVersion"/> 作为 schema/analyzer 版本锚点（变更即触发全量重建）。
///
/// 同步、无 IO、无可变静态状态（除惰性数据表），可安全并发调用。
/// </summary>
public static class Analyzer
{
    /// <summary>
    /// 分析器语义版本；任何影响词元输出的变更都必须递增。
    ///
    /// v2：归一化由手写全角折叠升级为**逐字符 NFKC 兼容折叠**（<c>data/nfkc.tsv</c>）——
    /// <c>ﬁ</c>→<c>fi</c>、<c>①</c>→<c>1</c>、兼容表意字折叠等改变词元输出，故递增触发全量重建。
    /// v3：NFKC 表扩展至**全范围**（新增非 BMP / 星平面兼容字符，如数学字母 <c>𝑨</c>→<c>A</c>、
    /// <c>𝟎</c>→<c>0</c>），折叠改为 **rune 遍历**；含非 BMP 字符时词元输出变化，故递增触发全量重建。
    /// v4：拼音 / 繁简 / 全角收敛到 <c>ToolGood.Words</c>（Apache-2.0），GB18030 解码改用框架
    /// <c>CodePagesEncodingProvider</c>，删除自带数据表与英文阻断表。拼音改为**按 CJK 段词级消歧**
    /// （<c>重庆→chong qing</c>、<c>银行→yin hang</c>），繁转简改用 ToolGood 词库，输出与旧表存在
    /// 可达差异，故递增触发全量重建。
    /// v5：**索引内容变化**——<c>LyricsTrTok</c> / <c>LyricsRomTok</c> 不再留空，改由
    /// <see cref="LrcReader"/> 从内嵌歌词 / <c>.lrc</c> 拆分出译文 / 音译层并分别分析入库
    /// （T0 多语言层入库）。译文 / 音译层此前恒空，现被填充，输出变化，故递增触发全量重建。
    /// （T1 的查询侧跨语言候选只改 <see cref="AnalyzeQuery"/>，不影响索引侧词元输出。）
    /// v7：**读音列变化**（T1-1 罗马音规范形）——读音 bigram 由 Hepburn 折叠为规范形
    /// （<c>sh→s</c> / <c>ch→t</c> / <c>ts→t</c> / <c>fu→hu</c> / <c>j→z</c>、促音与长音折叠、
    /// <c>ん</c>+元音→<c>nn</c>+元音），输出变化，故递增触发全量重建。
    /// v8：**助词双写**（T1-2）——<c>は</c> 增加 <c>wa</c>、<c>へ</c> 增加 <c>e</c> 备选拼写
    /// （<c>を</c> 原有 <c>o/wo</c>），修复局部窗口变体未生效的问题，读音列输出变化。
    /// v9：**韩文读音列**（T1-3）——谚文经国语罗马字写入 <c>reading_meta_tok</c>，
    /// 纯韩文曲目从「读音列为空」变为有值，输出变化，故递增触发全量重建。
    /// v10：**去音符号折叠**（T1-5）——<c>é / ü / ñ / ā</c> 等预组合拉丁字母折叠为 ASCII
    /// 基字母，组合音标丢弃；含重音字母的曲目（如 <c>Beyoncé</c>）词元由带音符号变为无音符号，
    /// 输出变化，故递增触发全量重建。
    /// v11：**异体字补齐**（T1-7）——Unihan <c>kSimplifiedVariant</c> 补齐 ToolGood 未覆盖的
    /// 日文新字体（鉄→铁、絵→绘、円→圆…）；含新字体的曲目词元变化，故递增触发全量重建。
    /// v12：**NFKC 表生成器迁移**——<c>data/nfkc.tsv</c> 的生成从 Python <c>unicodedata</c>
    /// （Unicode 16.0.0）改为纯 .NET <c>string.Normalize(FormKC)</c>（ICU / Unicode 17.0），
    /// 唯一内容差异是新增 <c>U+A7F1 → S</c>（Unicode 17 新增字符）。表内容变了，
    /// 按「表变即重建」的既定规则递增版本，避免新旧表混用产生难以复现的词元差异。
    /// v13：**CJK / 假名 / 谚文段只保留首尾 unigram**（中间字由 bigram 覆盖）——单字在
    /// 真实曲库里的文档频率极高，「歌词含某个字」几乎不构成检索意图，却是单字查询扫全库
    /// 的结构性根源。去掉中间字后，单字查询改走前缀 <c>字*</c>（命中以该字开头的 bigram）。
    /// 末尾字必须保留：它只作为某个 bigram 的第二个字符存在，不留 unigram 将彻底查不到。
    /// 索引内容变化，必须全量重建。
    /// v14：**字面歌词层不再产出 unigram**（T3.7）——歌词原文 / 译文 / 音译只入 bigram
    /// （拉丁 / 数字整词不变），单字不再进这三个列。单字歌词查询本就被查询侧列门控
    /// 排除（<c>RequireMetadataForSingleChar</c>），故召回不变、倒排显著变小。
    /// 同时歌词层在分析前经 <see cref="LyricsPreprocess"/> 归一化去重 + V6 护栏截断
    /// （T3.5 / T3.8）：歌词列内容变化，必须全量重建。
    /// v15：**首字母列改为 bigram**（T3.9）——每个 CJK 段的首字母串由「整段一个 token」
    /// 改为「相邻首字母两两成词」（<c>wanzg</c> → <c>wa an nz zg</c>），使段中间的首字母
    /// 也能命中；拉丁 / 数字段不再写入首字母列（设计文档 §4.2：只有汉字有拼音首字母）。
    /// 段 / 字段之间插入 <see cref="InitialsBoundaryToken"/>，短语查询不会跨段误命中。
    /// 首字母列内容变化，必须全量重建。
    /// </summary>
    public const int AnalyzerVersion = 15;

    /// <summary>
    /// 首字母 bigram 能力（<c>initials_bigram</c>）的最低分析器版本：v15 起首字母列才是
    /// bigram，之前是整段一个 token。能力推导据此声明，避免旧索引谎报可中间匹配。
    /// </summary>
    public const int InitialsBigramMinVersion = 15;

    /// <summary>
    /// 首字母倒排列的**段边界词元**（T3.9）。首字母 bigram 只在同一 CJK 段内生成；
    /// 段与段、字段与字段之间插入该词元占一个位置，于是相邻差 ≥2，短语查询
    /// （<c>"an zg"</c>）无法跨段 / 跨字段拼出「假相邻」命中。
    ///
    /// 选择 <c>#</c>：首字母 bigram 恒为两个 ASCII 小写字母，<c>#</c> 既不可能与真实词条
    /// 冲突，也只占 1 字节倒排键；查询侧永不产生该词元。
    /// </summary>
    public const string InitialsBoundaryToken = "#";

    /// <summary>查询侧跨语言候选（<see cref="QueryTokens.CrossLingual"/>）合并去重后的总预算。</summary>
    public const int CrossLingualBudget = 32;

    /// <summary>
    /// 单段首字母串 → 相邻两两 bigram（空格分隔）。长度 &lt;2 时无 bigram（返回空串）：
    /// 首字母单字母过于稠密，不产出 unigram。索引侧与查询侧共用这一份规则，
    /// 保证两侧的 bigram 空间逐字一致。
    /// </summary>
    public static string InitialsToBigrams(string initials)
    {
        if (initials.Length < 2) return string.Empty;
        var builder = new StringBuilder(initials.Length * 3 - 1);
        for (var i = 0; i + 1 < initials.Length; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(initials[i]);
            builder.Append(initials[i + 1]);
        }
        return builder.ToString();
    }

    /// <summary>把各段的 bigram 组用 <see cref="InitialsBoundaryToken"/> 连接成倒排列。</summary>
    private static string JoinInitialsBigrams(IReadOnlyList<string> groups)
    {
        if (groups.Count == 0) return string.Empty;
        return string.Join($" {InitialsBoundaryToken} ", groups);
    }

    private enum ScriptKind
    {
        None,
        Cjk,

        /// <summary>
        /// 需要子串索引的脚本（假名 / 谚文，见 <see cref="ScriptRanges.IsSubstringScript"/>）：
        /// 与 CJK 同样 <c>bigram + unigram</c> 入索引，但**不进拼音 / 首字母列**
        /// （假名 / 谚文没有拼音读音，镜像只会是噪声与体积；实测假名占真实歌词 9.6%）。
        /// </summary>
        Substring,
        Latin,
        Digit,
    }

    private readonly record struct Segment(ScriptKind Kind, string Text);

    /// <summary>
    /// 汉字判定：CJK 统一表意主区（U+4E00–U+9FFF）+ 扩展 A（U+3400–U+4DBF）
    /// + 兼容表意区（U+F900–U+FAFF）。
    /// 注意：BMP 之外（扩展 B 起）为代理对，本 API 基于 <see cref="char"/>，不覆盖（见报告风险项）。
    /// </summary>
    public static bool IsCjk(char c) => ScriptRanges.IsCjk(c);

    /// <summary>
    /// 索引侧分析：归一化 + 分词 + 拼音列 + 日文读音列。
    ///
    /// [includeReadings] 为真表示调用方判定**整首曲目是日文**（任一源含假名）——此时连
    /// 纯汉字段（如纯汉字歌手名 米津玄師）也查 Unihan 读音表；为假时只有含假名的源才产出读音。
    ///
    /// [includeUnigrams] 为假时**不产出 CJK / 假名 / 谚文的 unigram**（只留 bigram；拉丁 /
    /// 数字整词不受影响）。字面歌词层（原文 / 译文 / 音译）传 <c>false</c>：单字歌词查询
    /// 已被查询侧列门控排除，unigram 只占倒排、不带来召回（见 <see cref="AnalyzerVersion"/> v14）。
    /// 拼音 / 首字母 bigram / 读音列始终按**完整文本**生成，不受该开关影响。
    /// </summary>
    public static AnalysisResult Analyze(
        string text, bool includeReadings = false, bool includeUnigrams = true)
    {
        var parts = AnalyzeCore(text, includeUnigrams);
        return new AnalysisResult(
            string.Join(' ', parts.Tokens),
            string.Join(' ', parts.Pinyin),
            string.Join(' ', parts.Initials),
            Japanese.ReadingBigrams(text, includeReadings),
            JoinInitialsBigrams(parts.InitialsBigrams));
    }

    /// <summary>
    /// 查询侧分析：与索引侧同构，返回结构化词元列表。
    ///
    /// 与索引侧的唯一差异在 <see cref="QueryTokens.Pinyin"/>：对**每个**拼音词元尝试
    /// 连写全拼切分（P4，设计文档 §4.2）——<c>woaini</c> → <c>wo ai ni</c>、
    /// <c>woai</c> → <c>wo ai</c>；CJK 逐字主读音本身即单音节，切分为幂等；
    /// 英文词（<c>hello</c>）无法完整覆盖时保持原整词，即使可完整覆盖也受
    /// <see cref="PinyinSegmenter"/> 的英文过度切分门控约束（<c>banana</c> /
    /// <c>adele</c> 等不再切分）。
    /// </summary>
    public static QueryTokens AnalyzeQuery(string text)
    {
        // 查询侧恒产 unigram：单字 / 中间字要靠它改写为前缀 <c>字*</c>。
        var parts = AnalyzeCore(text, includeUnigrams: true);
        var query = new QueryTokens();
        query.Lexical.AddRange(parts.Tokens);

        var buffer = new List<string>();
        foreach (var token in parts.Pinyin)
        {
            if (PinyinSegmenter.TrySegment(token, buffer)) query.Pinyin.AddRange(buffer);
            else query.Pinyin.Add(token);
        }

        query.PinyinInitials = parts.HasCjk ? string.Join(' ', parts.Initials) : null;
        query.HasCjk = parts.HasCjk;
        query.CjkCharCount = parts.CjkCharCount;
        query.VisibleCharCount = parts.VisibleCharCount;
        query.HasNonIndexedLetterOrDigit = parts.HasNonIndexedLetterOrDigit;
        query.PinyinComplete = !parts.HasCjk || parts.PinyinSyllableCount >= parts.CjkCharCount;
        query.CrossLingual.AddRange(CollectCrossLingual(text));
        RemovePinyinCollisions(query);
        return query;
    }

    /// <summary>
    /// 去噪：Unihan 释义里的罗马化专名（如 <c>周</c> → “Zhou dynasty” 的 <c>zhou</c>）会与
    /// 查询自身拼音音节**同形**——该串已由拼音分支覆盖（且拉丁词元本就同时进入拼音列），
    /// 保留只会引入与拼音层重复的噪声命中（如 <c>周杰美</c> 误命中 <c>周杰伦</c> 的拼音）。
    /// 故丢弃与查询拼音音节逐字相同的候选（保序，仅删重复项）。
    /// </summary>
    private static void RemovePinyinCollisions(QueryTokens query)
    {
        if (query.CrossLingual.Count == 0 || query.Pinyin.Count == 0) return;
        var pinyin = new HashSet<string>(query.Pinyin, StringComparer.Ordinal);
        query.CrossLingual.RemoveAll(pinyin.Contains);
    }

    /// <summary>
    /// 查询侧跨语言候选（T1，Unihan 释义扩展；**不改索引侧**）。
    ///
    /// 规则（合并去重、保序、总预算 ≤ <see cref="CrossLingualBudget"/>，超出截断）：
    /// <list type="bullet">
    ///   <item>拉丁 / 数字段 → <c>def_en2hanzi</c> 查该整词，取候选汉字**码点**转为字符
    ///         （每词生成表侧已 ≤ 8）；</item>
    ///   <item>CJK 段内的每个字 → <c>def_hanzi2en</c> 查该字码点，取英文释义词。</item>
    ///   <item>T4.1：整个查询归一形命中策展词表 <c>lexicon_cross</c> 时，先加入其目标 token
    ///         短语（专名高置信，占预算最前）。</item>
    /// </list>
    /// 拉丁候选优先于 CJK 候选（按分段出现顺序），二者汇入同一预算池。
    /// 输入经 <see cref="Normalize"/>（NFKC + 小写 + 繁转简）后按脚本切分，与索引侧同源。
    /// </summary>
    private static List<string> CollectCrossLingual(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;

        void Add(string term)
        {
            if (result.Count >= CrossLingualBudget) return;
            if (term.Length == 0 || !seen.Add(term)) return;
            result.Add(term);
        }

        var normalized = Normalize(text);

        // T4.1：策展专名词表优先（高置信，按 weight 降序；目标已在加载期切成 token 短语）。
        foreach (var curated in LexiconCross.Expand(normalized)) Add(curated);

        foreach (var segment in SplitSegments(normalized))
        {
            if (segment.Kind == ScriptKind.Cjk)
            {
                foreach (var ch in segment.Text)
                {
                    if (!HanziData.TryGetGlossWords(ch, out var words)) continue;
                    foreach (var word in words) Add(word);
                }
            }
            else
            {
                if (!HanziData.TryGetGlossHanzi(segment.Text, out var codes)) continue;
                foreach (var cp in codes)
                {
                    if (cp >= 0 && cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF))
                        Add(char.ConvertFromUtf32(cp));
                }
            }
        }
        return result;
    }

    private sealed class Parts
    {
        public readonly List<string> Tokens = new();
        public readonly List<string> Pinyin = new();

        /// <summary>每段人类可读首字母串（查询侧 / 诊断用，非 bigram）。</summary>
        public readonly List<string> Initials = new();

        /// <summary>
        /// 每段首字母 bigram 组（<c>wa an nz zg</c>）。注意这里按段分组保存，
        /// 源内多段 / 多字段之间的边界由 <see cref="JoinInitialsBigrams"/> 与
        /// <c>IndexerEngine.CombineInitials</c> 插入 <see cref="InitialsBoundaryToken"/>。
        /// </summary>
        public readonly List<string> InitialsBigrams = new();
        public bool HasCjk;
        public int CjkCharCount;
        public int PinyinSyllableCount;
        public int VisibleCharCount;
        public bool HasNonIndexedLetterOrDigit;
    }

    private static Parts AnalyzeCore(string text, bool includeUnigrams)
    {
        var parts = new Parts();
        if (string.IsNullOrEmpty(text)) return parts;

        foreach (var segment in SplitSegments(Normalize(text)))
        {
            parts.VisibleCharCount += CountRunes(segment.Text);
            if (segment.Kind == ScriptKind.Cjk)
            {
                parts.HasCjk = true;
                AddCjkSegment(segment.Text, parts, includeUnigrams);
            }
            else if (segment.Kind == ScriptKind.Substring)
            {
                // 假名 / 谚文：只做子串索引（bigram + 首尾 unigram），不进拼音 / 首字母列。
                AddSubstringSegment(segment.Text, parts, includeUnigrams);
            }
            else
            {
                // 拉丁 / 数字段：整词进入词法 / 拼音列；**不进首字母列**（设计文档 §4.2：
                // 首字母是拼音派生列，只有汉字有拼音）。旧实现把整词塞进首字母列，
                // 令 ASCII 标题也能被首字母支命中（T3.9 修正）。
                parts.HasNonIndexedLetterOrDigit = true;
                parts.Tokens.Add(segment.Text);
                parts.Pinyin.Add(segment.Text);
            }
        }
        return parts;
    }

    /// <summary>段内码点数（非 BMP 按一个字符计，避免代理对重复计数）。</summary>
    private static int CountRunes(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>
    /// CJK 段分词：先按序输出 bigram（相邻二字，位置连续便于短语），再输出**首尾**
    /// unigram（中间字由 bigram 覆盖，见 <see cref="AnalyzerVersion"/> v13）；拼音列按
    /// **整段**词级消歧取音节；首字母列按段输出相邻两两 bigram（v15），不再产整段 token。
    ///
    /// 首尾 unigram 的取舍：末字只作为某个 bigram 的第二个字符存在，不留 unigram 会
    /// 彻底查不到；首字保证段首检索不受影响。中间字查询在编译器侧改写为前缀
    /// <c>字*</c>（命中以该字开头的 bigram），命中集合不缩小。
    ///
    /// <paramref name="includeUnigrams"/>=false（字面歌词层）时只输出 bigram；单字段
    /// 因此不产任何词元——查询侧单字不扫歌词列，这条路径不可达。
    /// 拼音 / 首字母 bigram 仍按完整文本生成，不受该开关影响。
    /// </summary>
    private static void AddCjkSegment(string segment, Parts parts, bool includeUnigrams)
    {
        var runes = segment.EnumerateRunes().ToArray();
        parts.CjkCharCount += runes.Length;
        if (runes.Length >= 2)
        {
            for (int i = 0; i + 1 < runes.Length; i++)
                parts.Tokens.Add(runes[i].ToString() + runes[i + 1].ToString());
            if (includeUnigrams)
            {
                parts.Tokens.Add(runes[0].ToString());
                parts.Tokens.Add(runes[^1].ToString());
            }
        }
        else if (runes.Length == 1 && includeUnigrams)
        {
            parts.Tokens.Add(runes[0].ToString());
        }

        // 整段一次性过 ToolGood：词级多音字消歧（重庆→chong qing）。
        var pinyin = Pinyin.FromCjkRun(segment);
        parts.Pinyin.AddRange(pinyin);
        parts.PinyinSyllableCount += pinyin.Count;

        // 首字母：人类可读的整段串 + 索引用的相邻 bigram（T3.9）。单字段（1 个首字母）
        // 没有 bigram，只保留人类可读串——单字母 token 过于稠密，不入倒排。
        var initials = Pinyin.Initials(segment);
        if (initials.Length > 0)
        {
            parts.Initials.Add(initials);
            var bigrams = InitialsToBigrams(initials);
            if (bigrams.Length > 0) parts.InitialsBigrams.Add(bigrams);
        }
    }

    /// <summary>
    /// 假名 / 谚文段分词：<c>bigram + 首尾 unigram</c>（与 CJK 同构，故子串 / 前缀 / 词组
    /// 查询都能命中），**不产出拼音 / 首字母**。
    ///
    /// 旧实现把假名 / 谚文归入「拉丁」分支 → 整段一个 token，子串查询必然落空
    /// （真实库实测：`くらりん` ⊂ `きゅうくらりん`，命中 0）。N-gram 取 2 与汉字一致，
    /// 查询侧同一套 <c>bigram</c> 组合即可（见 Dart <c>_isCjkBigram</c> 的对应实现）。
    /// 首尾 unigram 规则与 CJK 相同（v13）：单字查询走前缀，避免全库高频单字倒排。
    /// <paramref name="includeUnigrams"/>=false 时只输出 bigram（字面歌词层，v14）。
    /// </summary>
    private static void AddSubstringSegment(string segment, Parts parts, bool includeUnigrams)
    {
        var runes = segment.EnumerateRunes().ToArray();
        if (runes.Length >= 2)
        {
            for (int i = 0; i + 1 < runes.Length; i++)
                parts.Tokens.Add(runes[i].ToString() + runes[i + 1].ToString());
            if (includeUnigrams)
            {
                parts.Tokens.Add(runes[0].ToString());
                parts.Tokens.Add(runes[^1].ToString());
            }
        }
        else if (runes.Length == 1 && includeUnigrams)
        {
            parts.Tokens.Add(runes[0].ToString());
        }
    }

    /// <summary>
    /// 逐字符 NFKC 兼容折叠 → **拉丁去音符号折叠** → **异体字补齐（日文新字体 → 简体）**
    /// → 小写 → 繁转简（ToolGood.Words 词库）；
    /// 标点/空白留待 <see cref="SplitSegments"/> 作分隔符丢弃。
    ///
    /// 注意：本程序集以 <c>InvariantGlobalization=true</c> 构建（无 ICU），此时
    /// <c>string.Normalize(NormalizationForm.FormKC)</c> 为**空操作**（其表来自 ICU）。
    /// 故这里以离线表 <c>data/nfkc.tsv</c>（Python <c>unicodedata</c> NFKC）为**唯一事实来源**，
    /// 逐字符做兼容折叠（<c>ﬁ</c>→<c>fi</c>、<c>①</c>→<c>1</c>、全角→半角、兼容表意字→统一表意字、
    /// 数学字母 / 星平面兼容字符→ASCII 等）；未命中保持原字符。
    /// 结果与全球化是否可用无关，索引 / 查询两侧确定一致。
    ///
    /// 折叠以 <see cref="Rune"/>（码点）为单位遍历：非 BMP 码点直接查表替换，避免把代理对拆散；
    /// <see cref="string.EnumerateRunes"/> 遇非法 UTF-16（孤立代理）返回替换字符 <c>U+FFFD</c>，
    /// 不会产生非法代理对、不崩溃。
    ///
    /// 繁转简改用 <c>ToolGood.Words.ToSimplifiedChinese</c>（Apache-2.0）：一次对整串调用
    /// （词级、非逐字），取代原先的 <c>data/simp_trad.tsv</c> 查表。
    ///
    /// **去音符号折叠**（T1-5）：NFKC 只做兼容分解 + 规范合成，重音字母（<c>é</c> / <c>ü</c> /
    /// <c>ñ</c>）折叠后仍是原字符。这里在 NFKC 之后再过一遍 <c>data/accent_fold.tsv</c>
    /// （NFD 派生的「预组合字母 → ASCII 基字母」表），并丢弃组合音标码点，
    /// 使 <c>Beyoncé</c> 与 <c>Beyonce</c>（含分解形 <c>e</c>+U+0301）同形。索引 / 查询两侧
    /// 走同一函数，故不需要额外的查询侧变体。ł / ø / đ / ß 等无规范分解的字母保持原样。
    ///
    /// **异体字补齐**（T1-7）：<c>data/variant_fold.tsv</c> 由 Unihan <c>kSimplifiedVariant</c>
    /// 派生（日文新字体经 <c>kJapaneseOldVariant</c> 串联：鉄→鐵→铁、絵→繪→绘、円→圓→圆），
    /// 只补 ToolGood 繁简词库未覆盖的字符，故不改变既有中文繁简规则。
    /// 索引 / 查询同表，搜「银河铁道」可命中「銀河鉄道の夜」。
    ///
    /// 近似：NFKC 表**仅逐字符应用**——跨字符规范化组合（如 base + combining 合成）不做，
    /// 这与 NFKC 完整语义存在已知偏差（见 <c>tools/gen_tables</c> 与报告）。
    ///
    /// **internal 共享**：<see cref="LyricsPreprocess"/> 用它生成歌词行的去重键，保证
    /// 「去重判定」与「词元归一」是同一套语义（大小写 / 全半角 / 异体 / 繁简收敛一致），
    /// 不得另写第二份归一化。
    /// </summary>
    internal static string Normalize(string text)
    {
        if (text.Length == 0) return text;
        var compat = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (HanziData.TryGetCompatibilityFolded(rune.Value, out var replacement))
                compat.Append(replacement);
            else
                compat.Append(rune.ToString());
        }

        // 兼容折叠可能产出多个字符（如 Ⅻ → xii），故去重音 / 异体补齐需重新遍历一遍。
        var folded = new StringBuilder(compat.Length);
        foreach (var rune in compat.ToString().EnumerateRunes())
        {
            if (HanziData.IsCombiningDiacritic(rune.Value)) continue;
            if (HanziData.TryGetAccentFolded(rune.Value, out var baseLetter))
                folded.Append(baseLetter);
            else if (HanziData.TryGetVariantFolded(rune.Value, out var simplified))
                folded.Append(simplified);
            else
                folded.Append(rune.ToString());
        }

        return WordsHelper.ToSimplifiedChinese(folded.ToString().ToLowerInvariant(), 0);
    }

    /// <summary>
    /// 元数据归一形（DocStore v3 的 <c>title_norm</c> / <c>artist_norm</c> / <c>album_norm</c>），
    /// <b>唯一生成入口</b>（T3.11，RP §3.6.3 的 exactness 复核）。
    ///
    /// <para>规则 = <see cref="Normalize"/>（NFKC 兼容折叠 → 去音符号 → 异体补齐 → 小写 → 繁转简）
    /// → <b>空白折叠</b>：所有 Unicode 空白连续段压成一个 ASCII 空格，并去掉首尾空白。
    /// 不额外删标点 / 不做分词——归一语义只有 <see cref="Normalize"/> 一份。</para>
    ///
    /// <para>为什么保留单个空格：exactness 的拉丁多词字段需要<b>词级</b>覆盖
    /// （命中词数 / 查询词数），空格就是词边界；CJK 覆盖按码点计算，空格天然不参与
    /// （查询侧 <c>q</c> 去空白后做相等 / 前缀 / 包含判定）。因此列里只留「词间单空格」，
    /// 不保留源串的换行 / 制表 / 全角空格 / 连续空格。</para>
    ///
    /// <para>源字段为空（或全是空白）返回空串；调用方不需要再判空。</para>
    /// </summary>
    internal static string NormalizeMetadata(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalized = Normalize(text);
        // 快路径：无任何空白时直接返回（大多数 CJK / 单词元数据），免去两次缓冲分配。
        var hasWhitespace = false;
        foreach (var ch in normalized)
            if (char.IsWhiteSpace(ch)) { hasWhitespace = true; break; }
        if (!hasWhitespace) return normalized;

        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                // 首部空白直接丢弃；词间连续空白只记一个待写空格。
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>脚本切分：连续 CJK / 连续拉丁 / 连续数字各成段；其余（标点等）丢弃并断段。</summary>
    private static List<Segment> SplitSegments(string text)
    {
        var segments = new List<Segment>();
        var builder = new StringBuilder();
        var kind = ScriptKind.None;

        void Flush()
        {
            if (builder.Length == 0) return;
            segments.Add(new Segment(kind, builder.ToString()));
            builder.Clear();
            kind = ScriptKind.None;
        }

        foreach (var ch in text)
        {
            var current = Classify(ch);
            if (current == ScriptKind.None)
            {
                Flush();
                continue;
            }
            if (current != kind) Flush();
            kind = current;
            builder.Append(ch);
        }
        Flush();
        return segments;
    }

    private static ScriptKind Classify(char c)
    {
        if (IsCjk(c)) return ScriptKind.Cjk;
        if (ScriptRanges.IsSubstringScript(c)) return ScriptKind.Substring;
        if (char.IsDigit(c)) return ScriptKind.Digit;
        if (char.IsLetter(c)) return ScriptKind.Latin;
        return ScriptKind.None;
    }
}
