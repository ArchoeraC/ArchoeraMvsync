# SearchSpec v1：本地曲库搜索规格

> 版本：**v1** ｜ 日期：**2026-11-24** ｜ 状态：**S0 产出，S1 起为唯一规格来源**
> 适用：`app/core/indexer`（C#，唯一实现）与 Dart FFI 消费方；只覆盖本地曲库搜索。
> 引用：`RP §x` = [rewrite-plan](local-search-indexer-rewrite-plan.md)，`RP 附录 D.x` = 其
> `era_search_*` 契约；[recall-precision-audit](local-search-recall-precision-audit.md) 只作背景，
> **本文不从审计文档引入新规范**。
> 用语：**MUST / MUST NOT** 为硬约束，**SHOULD** 为默认；标「目标」的条目在 S1/S2/S3 逐步生效。
> 数字：只复述 RP 已写下的值并标出处；RP 没有的一律写「待校准（S1 回放后定）」。
> **迁移顺序、阶段划分、验收门限只认 RP §5，本文不另立。**

## 0. 范围与版本锚

覆盖：归一化、切分、字段族、方案层、查询计划、桶序、预算、`api:2` 契约、向量与回放；不覆盖升级顺序（RP §5）、EraStore 物理字节布局（RP §3.3.0.x）、scanner / `library.db` schema、策展表内容与许可证（RP §5.5）。

| 锚 | v1 初始值 | 说明 | 出处 |
|---|---|---|---|
| `index_format` | **1** | EraStore 物理格式，与旧 schema 无关 | RP §3.3.1 / §3.8.2.6 |
| `normalizer_version` | **1** | 只影响归一输出 | RP §3.2 / §3.3.1 |
| `tokenizer_version` | **2** | bigram + 首字母 bigram；去重 / 截断规则变化也 MUST 递增 | RP §3.3.1 / §3.8.1 |
| `scheme_version` | **1** | 查询 / 排序默认策略 | RP §3.3.1 |
| `data_pack_id` | manifest 哈希 | 数据表版本，运行期 O(1) 比对 | RP §3.2.2 |
| `options_bits` | 默认 `pinyin=1, lyrics_pinyin=1, lyrics_initials=0, lrc=0, romaji_lyrics=0`（bit 0–4） | 影响索引内容，进 `rev` 与重建闸门 | RP §3.4.4 |
| `capabilities` | 由构建收尾写入 `Manifest`（从段头 / 字段族 / 词典内容推导） | 方案只读不写 | RP §3.3.1 / §3.4.4 |

> **门禁**：上表锚任一不符或无法解析 → **旧索引作废、不读取**，走全量构建；构建期查询走
> `LIKE`。`scheme` 不符**不重建**（只影响查询）。（RP §3.8.2.6）

契约与握手（RP §2.2 / §3.11.1 / 附录 D）：`api` MUST 为 **2**，缺失或为 1 → 错误码 1；
启动要求 `era_search_api_version() == 2` **且** `payload.api == 2`，不一致不调用 `run`，
直接 `era_search_fallback` + 设置页提示。**不存在** `api:1` 兼容、旧符号 wrapper、旧索引读取、
运行时回退旧实现；S1 删除 `indexer_*`，只留 `era_search_*`（2026-11-24 已落地），旧字段
`minExact` / `candidateCap` / `fuzzy` / `ranking` / `engine` 出现即错误码 1
（`detail.reason="deprecated_field"`，RP 附录 D.2）。

## 1. 归一化与切分

### 1.1 归一化管道（顺序即契约）

任何顺序调整都算语义变更；前半（兼容分解→繁简）递增 `normalizer_version`，后半（脚本切分→派生）递增 `tokenizer_version`（RP §3.2.1）。

| # | 步骤 | 语义 / 边界 |
|---|---|---|
| 1 | 兼容分解（`nfkc.tsv`，**NFKD 语义**） | `ﬁ→fi`、`①→1`、全角→半角；表未覆盖原样保留；孤立代理→`U+FFFD`；**不重组** |
| 2 | 小写 + 去音符号 | `ToLowerInvariant`；`İ`→`i`+`U+0307` 后丢弃；`é/ü/ñ/ā`→ASCII 基字母；`ł/ø/đ/ß` 保持原样 |
| 3 | 异体字补齐 | 日文新字体→简体（`鉄→铁`）；只补繁简词库未覆盖的字符 |
| 4 | 繁转简 | ToolGood 词级转换；整串一次调用，不逐字 |
| 5 | 脚本切分 | Han / Kana / Hangul / Latin / Digit 成段；标点 / 空白丢弃；混合脚本按段切开 |
| 6 | 段内切词 | 见 §1.2；空段丢弃 |
| 7 | 派生视图 | 拼音 / 首字母 / 罗马字；段内有一个字不产读音 → **整段不产读音** |

> ⚠ **幂等是硬门禁**：`Normalize(Normalize(x)) == Normalize(x)` MUST 进单测；生成期校验
> `Normalize(dst) == dst`，运行期覆盖 BMP 全量 + 随机 astral + 组合音标（RP §3.2.4）。
> 索引侧与查询侧 MUST 调用**同一个** `Normalize`。数据包只比对 `DATA_PACK_ID`，不一致
> MUST 拒绝启用索引、不得静默沿用旧表；展示始终用原文（RP §3.2.2）。

### 1.2 切分规则（RP §3.3.2）

| 脚本 | 字面 token（段长 L） | 读音 token | 首字母 token |
|---|---|---|---|
| Han | `L-1` bigram + 首字 + 末字；`L=1` 时 1 个 unigram | 每字 1 音节（段级完整） | `L=1`→1 字符；`L≥2`→`L-1` bigram |
| Kana | 同 Han | 罗马字（规范形）bigram | 无 |
| Hangul | 同 Han | 国语罗马字 bigram | 无 |
| Latin / Digit | 整词小写，**不做 n-gram** | 无 | 无 |
| 其他（符号等） | 不产 token | — | — |

- CJK bigram + 首尾 unigram 是**保留项**，重写 MUST NOT 换成分词器（RP §3.2）。
- 首字母只出 bigram；`L=1` 的单字符 token 仅作兼容写入，查询侧 MUST NOT 生成单字母步（RP §3.3.2 / N17）。
- 读音来源互斥（中文拼音**或**日文音训读）；文件名不产拼音 / 首字母，日文读音沿用现有行为。

### 1.3 字段族与 `field_id`（RP §3.3.1）

| field_id | 内容 | 位置 | 备注 |
|---|---|---|---|
| `0x01–0x03` | title / artist / album 字面 | ✅ | bigram + 首尾 unigram |
| `0x04` | filename 字面 | ✅ | 日文文件名读音另计 |
| `0x05` | lyrics 原文 | ✅ | 行级去重后写 |
| `0x06` | lyrics 译文 | ✅ | 无译文层则不写 |
| `0x07` | lyrics 音译 | ❌（S2 保留项） | 仅显式音译层 |
| `0x11–0x13` | title / artist / album 拼音 | ✅ | 段内逐字对齐 |
| `0x14–0x16` | title / artist / album 首字母 | ✅ | 只出 bigram |
| `0x17` | 日 / 韩读音 | ✅ | 仅库画像命中时写 |
| `0x21` | lyrics 拼音 | ✅ | **必须保留位置**：5k eval 免位置 P@1 0.973→0.838 |
| `0x22` | lyrics 首字母 | ✅ | 默认不写，设置项可开 |

位置默认除 `0x07` 外全字段写（EraStore 逐块选择 Δvarint 或 O8 位打包，RP §3.3.0.3；
S2 2026-11-26 只把可选音译层设为生产全量 / 增量默认免位置，歌词拼音保留位置，旧索引
仍可照常读取。O8 只改位置块物理编码，解码后的位置数组与短语语义不变；新 reader 读旧
varint 块，旧 reader 会拒绝带 `0x02` 标志的新 packed 块）。
**`seg_sep` 哨兵**：每字段 / 每段
之间写一个哨兵（建议 `zzsepzz`），阻断跨段短语；它 MUST 从 df 统计、纠错候选、模糊词表、
前缀展开中排除，查询侧 MUST NOT 生成；位置差因此 ≥2（RP §3.3.2 / §3.3.3）。

### 1.4 查询门控

| 输入形态 | 允许 | 禁止 | 出处 |
|---|---|---|---|
| 空串 / 全空白 | 直接返回空，不打开索引 | 构造空迭代器 | RP §3.5.7 / N14 |
| 单 CJK 字 | 只查元数据字面族，`PrefixIter("字")` | 查歌词族；生成读音 / 跨语言 / 容错 / 单字母首字母步 | RP §3.5.2.1 / §3.5.7 |
| 两 CJK 字 | 两字短语 | 退化成两次单字；生成删字变体 | RP §3.5.4 / §3.5.7 |
| ≥3 CJK 字 | 全 term 字面 + min-match 曲线；不足先减词 | 把跨语言当唯一兜底 | RP §3.5.4 / §3.5.7 |
| 1–2 字符拉丁 / 数字 | 精确 term | 前缀、容错、`a*` 扫全库 | RP §3.5.2 / N17 |
| 纯数字 | 字面整词 | 拆单字符；产拼音 / 首字母 | RP §3.5.7 / N9 |
| 纯 emoji / 符号 | 返回空 | 抛异常 | RP §3.5.7 / N16 |
| 含查询元字符 | 作为普通字符进入词元 | 拼进查询串 / 表达式 | RP §3.5.2 / N13 |
| ≥64 码点 | 只跑字面步并记日志 | 变体枚举爆炸 | RP §3.5.7（策略上限待校准） |

请求层 `query` 上限 **200 码点**（超出截断并置 `queryTruncated`，RP 附录 D.2）；与 64 码点的
**计划期**收缩门槛是两层约束，不得混用。

## 2. 方案层（Scheme）

### 2.1 角色 / 实现 / 方案

| 概念 | 含义 | 例子 |
|---|---|---|
| 角色（Role） | 可替换的算法职责 | `Tokenizer` / `ReadingProvider` / `Matcher` / `Ranker` / `PostProcessor` / `IndexStore` |
| 实现（Implementation） | 具体算法，自带 id 与能力声明 | `bigram@1`、`pinyin@1`、`exact@1`、`prefix@1`、`bucket@1` |
| 方案（Scheme） | 实现 + 顺序 + 预算 + requires + degradeTo | `fast@1` / `default@1` / `recall@1` / `minimal@1` |

`Scheme = { id, version, requires[], matchers[]（有序）, ranker, post[], policy{minMatch,caps,thresholds…}, degradeTo }`。
角色之间 MUST NOT 互相 `new`；`Compile` MUST 是纯函数，只吃 `QueryTerm + CompileContext`，
不查库、不出 IO（RP §3.4.1 / §3.4.2）。

### 2.2 内置方案（照 RP §3.4.3）

| scheme | matchers（有序） | ranker | 典型用途 | 降级到 |
|---|---|---|---|---|
| `fast@1` | `exact` → `prefix` | `bucket@1` | 大库 / 低延迟；不做读音与容错 | `default@1` |
| `default@1` | `exact` → `prefix` → `drop` → `phonetic-meta` → `reading` → `cross-lingual` → `typo1` | `bucket@1` | 日常搜索 | `fast@1` |
| `recall@1` | `default@1` 的 matchers + `phonetic-lyrics` + `typo2`（**组合，不是继承**） | `bucket@1` | 深度搜索 / 「我确定库里有」 | `default@1` |
| `minimal@1` | `exact`（按 scope 取列；有歌词能力时也含歌词） | `bucket@1` | 降级链终点 / 索引能力残缺 | — |

> ⚠ RP §3.4.3 **未逐方案列出 `requires` 与预算数值**。S1 注册表 MUST 显式写死两者：
> `requires` 由 matchers 所需能力推导（§2.3）；预算按方案设（`fast@1` 的 P95 严于 `recall@1`），
> 具体值「待校准（S1 回放后定）」，本文不替它拍数。
>
> **已发布 scheme 不可变**：行为一旦有向量，只能新增 `default@2`；新增算法只能通过新 scheme
> 或旧 scheme 升版本生效，且 MUST 附回放 diff。向量按方案分组，跨方案比较走回放 diff
> （RP §3.4.5 / §4 第 3 条）。

### 2.3 能力与协商（RP §3.4.4）

能力清单（RP §3.4.4）：`surface_text`（DocStore 有归一标题 / 歌手 / 专辑，EraStore 固有）、
`literal_lyrics`（`0x05–0x07`，索引歌词开启才写）、`phonetic_meta`（`0x11–0x17`）、
`phonetic_lyrics_pinyin` / `phonetic_lyrics_initials`（`0x21` 默认开 / `0x22` 默认关）、
`readings`（`0x17`，库画像含日 / 韩且生成成功）、`initials_bigram`、`lyrics_layers`、
`df_snapshot`、`typo_vocab`（后两者 EraStore 固有）、`highlight_offsets`（v2 固有）、
`cross_lingual`（S4 策展表）。索引构建收尾写入 `Manifest.capabilities`，方案只读不写。

```text
Resolve(requestedId, indexCaps):
  scheme = registry.Get(requestedId) ?? registry.Default
  while scheme != null:
    missing = scheme.Requires - indexCaps
    if missing is empty: return (scheme, missing: [])
    scheme = scheme.DegradeTo
  return (registry.Get("minimal@1"), missing: ["all"])
```

- `minimal@1` 只依赖 `surface_text`，是链终点；注册表启动时 MUST 做环检测
  （`a.degradeTo=b` 且 `b.degradeTo=a` 直接拒绝启动）。
- 实际方案与 `missingCaps` MUST 出现在 `explain` 与健康报告；降级对用户静默可用，日志留证据。
- `scope` **不参与**方案选择，只裁剪列；所有 scheme 都支持三种 scope。
- **门禁**：新增 capability / bit MUST 同步更新 Manifest 写入、scheme `requires`、健康报告、
  golden vectors 四处，否则会出现「方案以为有、索引其实没有」的静默降级。

## 3. 查询计划

### 3.1 数据结构（字段名即契约，RP §3.5.1）

| 类型 | 字段与语义 |
|---|---|
| `QueryTerm` | `Surface` / `Script` / `ReadingEligible` / `TypoEligible`（后两者是门控结果） |
| `Step` | `Kind` / `FieldFamily` / `Field` / `Op` / `Terms` / `CoveredTerms` / `Cap` / `EstimatedCandidates`；一个 step 只覆盖一个字段族，`Terms` 是词元不是 SQL |
| `QueryPlan` | `Terms` / `Steps` / `MinMatch` / `SchemeId` / `MissingCaps` |
| `Evidence` | `Kind` / `Field` / `MatchedTerms` / `Coverage` / `Positioned`（排序唯一输入） |
| `Candidate` | `Doc`(`Segment`,`Local`) / `TrackId` / `Evidence` / `Bm25` |

`MatchKind`（10 个）：`Exact`、`Phrase`、`Prefix`、`Partial`、`PinyinExact`、`PinyinPhrase`、
`Initials`、`Reading`、`CrossLingual`、`Typo`；`FieldKind`：`Title`、`Artist`、`Album`、`Filename`、
`Lyrics`、`LyricsTr`、`LyricsRom`；`MatchOp`：`Term | Prefix | Phrase | And | Or`。`Step.FieldFamily`
提供归因的前一半（kind 来自步骤、field 来自字段族），另一半由精确度复核补齐（RP §3.6.3）。

### 3.2 步骤生成（顺序即优先级，RP §3.5 / §3.5.2）

| # | step | 何时生成 | 候选预算 | 备注 |
|---|---|---|---|---|
| 1 | `literal-exact` | 总有 | 2k | `n≤2` 全 term 必须命中；`n≥3` 按 `minMatch` |
| 2 | `literal-prefix` | 末 term 可前缀（拉丁 / 拼音 ≥3，CJK 走 `字*`） | 2k | 与 #1 同一次执行，kind 降一档 |
| 3 | `literal-drop` | term 数 ≥3 | 1k/轮，≤2 轮 | 先减词，按 info + 字段亲和度 |
| 4 | `phonetic-exact` | 有可读音 term（单 CJK 字不生成） | 1k | 与 #1–#3 分开执行，单独产 evidence |
| 5 | `reading` | 库画像含日 / 韩且 query 是拉丁 | 500 | 读音 bigram 短语 |
| 6 | `cross-lingual` | 有高置信策展候选（非全量释义） | 300 | 最低档；单字禁止 |
| 7 | `typo` | 拉丁 term 长度 ≥4 且未超热词阈值 | 300 | 词长阶梯：≥4→1 错，≥7→2 错 |

**三条硬规则**：① 放宽只允许减约束、不允许换维度（先丢词 → 读音 → 容错）；
② 最少命中数用分段曲线（§3.3），不用 `MinExact` 标量；③ 预算由统计决定：
`stats.df(term)/N` 超阈值则不生成前缀 / 读音 / 容错步。`plan.Steps` MUST 按 `kindRank`
非降序生成（debug 断言），否则 §4.4 的剪枝不成立。

### 3.3 min-match 与丢词（RP §3.5.4 / 附录 B）

```text
n==1 : minMatch=1；只跑 literal；拉丁词且长度 ≥3 才追加 phonetic
       （单 CJK 字不追加 phonetic / reading / cross-lingual）
n==2 : minMatch=2；不生成丢词步（两字退化成单字正是 M-3）
n>=3 : minMatch=ceil(2n/3)
```

丢词候选只生成 ≤3 个子集，不逐词循环：`info(t)=log(N/max(df(t),1))`，
`role(t)=title命中?2:artist命中?1:0`，`score(S)=Σinfo+0.5×Σrole`（`|S|=minMatch`）。

- 取 `score` 最高的 3 个；同分保持原词序；`n≤8` 穷举，`n>8` 按 info 取前 `minMatch` 个 + 两个相邻替换变体。
- `n>4` 时只对 info 最高的 4 个 term 探测 `role`，其余记 0；每轮命中达 `minMatch` 即停，最多 2 轮。
- 丢词步 `kind = Partial`，**永远低于**任何全 term 命中的 kind。

### 3.4 预算估计与 df 快照（RP §3.5.3）

计划期**只读词典，不读倒排**；df 就在词典条目里。

```text
df 快照：每个 term 一次 lower_bound(field_id || term)，≤ 查询词数 × 3 次
est        = N * Π(df(term_i) / N)    # 独立假设
est_prefix = Σ df(区间内的词)
est_typo   = est * variants(term)
```

- `N=0` → 直接返回空；任一步 `est > scheme.Policy.StepCandidateFactor × Cap`（默认 **4×**）→ 不生成该步。
- 若删步会导致"只剩一个字面步"（如单字查询）→ 不删步，改为**压缩 Cap**。
- 词典查不到 → `df=0` → 该步不生成。候选总量 ≤ `kMergeCap`，其数值待校准（RP §3.3.0.5）。

### 3.5 `scope` 三种语义（RP §3.5.2.1）

`scope` 是请求约束（搜哪里），`scheme` 是检索策略（怎么搜），两者正交。

| scope | 字面字段族 | 读音字段族 | 单字查询 | 说明 |
|---|---|---|---|---|
| `all` | `0x01–0x07` | `0x11–0x17` + `0x21`（默认开） | 只查元数据 | 默认 |
| `metadata` | `0x01–0x04` | `0x11–0x17` | 允许 | 快搜 / 播放列表内 |
| `lyrics` | `0x05–0x07` | `0x21` / `0x22` | **禁止，返回空** | 单字无检索意图 |

生成步骤时按 scope **裁剪列集合**，不是查询后过滤（后者会把 `total` 算错）；`scope=lyrics`
下 `field` 只能是 `lyrics / lyricsTr / lyricsRom`；跨语言候选只有能落进当前 scope 列时才生成步骤；
缓存键、指标、回放 JSONL MUST 带 `scope`。

### 3.6 执行与证据合并

- 步骤按 `kindRank` 升序执行；`StepDominated` 仅在 `heap.Count == effectiveLimit` 且
  `RankOf(worst.Kind) < RankOf(step.Kind)` 时成立，**相等不跳**（RP §3.5.5.2）。跳步是**省钱**
  不是定语义，S1 MUST 用回放证明跳步不改变 top-k。
- 短语 MUST 在同一字段族内；无位置字段自动降级 `AndIter` 并标 `positioned=false`。
- 同 doc 多步召回（RP §3.5.6）：`kind` 取最高档；`coverage` **跟随最高 kind 的那条证据**、
  同 kind 取最大、**不跨 kind 求并集**；`matchedTerms` 并集只进 `explain`；`field` 取最高 kind
  对应者，同 kind 取 `FieldRank` 更小者；`bm25` 取最小值（仅桶内平局）；`provenance` 保留全部
  来源。合并 MUST NOT 累加分数，也不得把 prefix 与 exact 的 coverage 并成假证据（RP §3.5.6 反例）。

## 4. 桶序与排序

### 4.1 唯一入口

`SearchRanker` 是**唯一排序点**，输入只有 `Evidence`；`bucket@1` 是 v1 唯一 ranker，任何方案
都只能有一个 ranker（RP §3.6 / §4 第 6 条）。`highlight` / `snippet` / `dedupe` 属展示层，
不参与排序；改用加权求和同样禁止——分桶是**序**的语义，加法权重存在「弱信号 × 高 TF」的
退化路径（RP §3.6 / 附录 I）。

### 4.2 比较键（7 键字典序，RP §3.6.2）

| # | 键 | 方向 | 说明 |
|---|---|---|---|
| 1 | `kind` | 升序（好在前） | 见 §4.3 的 10 档 |
| 2 | `coverage` | 降序 | 命中 term 数 / 总 term 数 |
| 3 | `field` | 升序 | `Title=0, Artist=1, Album=2, Filename=3, Lyrics=4, LyricsTr=5, LyricsRom=6` |
| 4 | `exactness` | 升序 | 整字段相等 < 以查询开头 < 包含 < 近似 |
| 5 | `proximity` | 升序 | 多 term 最小跨度；单 term=0；歌词算不出时 = `int.MaxValue` |
| 6 | `bm25` | 升序 | **仅 1–5 全等时**；永不跨桶 |
| 7 | `docId` | 升序 | 稳定键，保证重复查询 / 重启 / 翻页一致 |

> 任务口径简称的「kind → coverage → field → bm25 → docId」是这 7 键的子集，实际以本表为准。
> **S3 状态（2026-11-25）**：`exactness` / `proximity` 已按本表真实生效——
> 精确度来自 DocStore v3 的 `title_norm/artist_norm/album_norm`（Mixed/Meta 步、单步命中
> ≤2048 才复核，空白不敏感、三列取最优并回传命中列作为 `field`）；邻近度对
> 单 term=0、整短语=`token-1`、散落多 term 用元数据字段内的位置最小窗口，歌词 / 无位置 /
> 超 cap 记 `int.MaxValue`（wire `-1`）。**Q1（2026-11-26）补齐歌词整行相等**：DocStore v5
> 的 `lyrics_digest` 在字面主趟 / 歌词 scope / ≤cap 时二分查找，整行相等记 `equal`（字段仍是
> 保守的 `Lyrics*`）；前缀 / 包含仍记 `none`（不存歌词原文）。回放评审已随 T3.11 完成
> （30k / 5k 逐项对比）。

`explain.bucket` 只序列化**前五键**（kind / coverage / field / exactness / proximity）；
`bm25` 与 `docId` 不进数组（RP §3.6.4 / D.3.1）。

### 4.3 `kind` 判定（10 档）

偏序（RP §3.6）：Exact < Phrase < Prefix < Partial < PinyinExact < PinyinPhrase < Initials <
Reading < CrossLingual < Typo。判定（RP §3.6.1）：

```text
全 term 命中: 任一字段整串 == 查询 -> Exact；同字段内连续 -> Phrase
             末 term 是前缀 -> Prefix；否则 -> Partial
拼音列: 多音节短语 -> PinyinPhrase，否则 PinyinExact；首字母 -> Initials
读音 -> Reading；跨语言 -> CrossLingual；其余 -> Typo
```

`kind` 是**整条命中的匹配质量**，不是"由哪一步召回"；同 doc 多步召回取最高档。

> ⚠ **命名待统一（S1 必办）**：内部枚举是 `PinyinExact/PinyinPhrase`，RP §3.7.1 示例用
> `literal-prefix`、附录 D.3.1 用 `pinyin`。本文不擅自裁定；S1 MUST 定一份 wire 枚举并同步
> JSON Schema、向量与 Dart DTO。在此之前内部类型以 §3.1 为准，JSON 契约以附录 D 为准。

### 4.4 剪枝、`total` 与分页

top-k 用固定大小堆，`effectiveLimit = min(limit, 10000)`，不排全命中集（RP §3.5.5.2）。

| `total` 模式 | 做法 | 结果 |
|---|---|---|
| `none`（默认） | 不统计 | `total = -1` |
| `capped` | 段内 bitset 去重，数到 10001 即停 | `total` + `totalExact=false` |
| `exact` | 禁用 kind 剪枝与 Cap，跑完所有步骤 | `totalExact=true`，可能 `partial` |

分页用 keyset：`next` 不透明；`gen` 或 `scheme_hash` 不符 → `items=[]`、`next=null`、
`restart=true`；不允许 `OFFSET` 跨快照翻页（RP §3.6.4 / D.5）。同一 `(gen, scheme)` 下重复
10 次、跨进程、跨分页的 top-k MUST 完全一致；第 7 键就是为此存在（RP §6.5）。

## 5. 预算与护栏

只列查询 / 索引侧相关默认值；未给值的一律「待校准（S1 回放后定）」。

| 参数 | v1 值 | 出处 | 状态 |
|---|---|---|---|
| 单步候选预算 | literal 2k / drop 1k / phon 1k / reading 500 / cross 300 / typo 300 | RP §3.5 / 附录 B | 待校准 |
| 单步超预算系数 / 候选总量 | `StepCandidateFactor = 4×` Cap / `kMergeCap` 未给数值（候选表按 5k 设计） | RP §3.5.3 / §3.3.0.5 | 待校准 |
| `maxQueryFrequency` | 拉丁 / 拼音词 `df/N > 1%` 不生成前缀 / 读音 / 容错；CJK bigram 一律不做容错 | RP 附录 B | 已定（外部参照 Lucene 0.01） |
| 前缀 / 变体上限 | 仅末 term；拉丁 / 拼音 ≥3；CJK 单字走 `字*`；`maxPrefixExpansions` 与拼音变体预算未给值 | RP 附录 B / §3.5.5 | 已定 / 待校准 |
| 容错词长阶梯 / 变体上限 | ≥4→1 错、≥7→2 错、首字符替换不生成变体 / ≤32 个、只收 ASCII、不含歌词 | RP 附录 B / §3.5.2 / §8.4 | 已定（外部参照）/ 待校准 |
| 拼音变体预算 | RP **未定义**该参数（仅 phon 1k 总预算） | — | 待校准 |
| min-match 曲线 / 中文单字 | `1→1, 2→2, n≥3→ceil(2n/3)` / 单字只查元数据字面族，不进歌词 | RP §3.5.4 / §3.5.2.1 / 附录 B | 已定（Solr `mm` 分段） |
| 丢词子集 | ≤3 个；`n≤8` 穷举；`n>8` 取 info 前 `minMatch` + 2 个相邻变体；≤2 轮 | RP §3.5.4 | 待校准 |
| 歌词护栏 | **4096 唯一行 / 128 KB 每层**（去重后计数，按层独立） | RP §8.1 / §8.1.1 / §9 | 待校准（S0 已补 `lyrics stats`） |
| 歌词去重 / unigram | 行级（归一后相等），目标去重率 ≥20% / 不建 unigram（只 bigram） | RP 附录 B / §8.1 V1、V3 | 目标 / 已定 |
| 查询预算 / 内存 | 整体 50 ms、单步 150 ms、超时 `partial` / `rss_query ≤ 60 MB`、常态 <2 MB | RP §3.5.5.2 / §8.0 / §8.3 | 待校准 / 门禁目标 |
| `limit` / `total` 默认 | 默认 20，上限 10000，超出 `clamped` / `total=none`（`total=-1`） | RP 附录 B / D.2 | 已定 |
| 结果缓存 / 游标 | LRU 128 条，键含 `gen`/`scheme`/全部开关 / base64url 32B 不透明 | RP 附录 B / D.5 | 已定 |

> ⚠ 「已定」指 RP 已给明确规则，不等于已用真实库校准。除 `limit` / `total` / 游标 / `min-match`
> 外，其余阈值在 S1 回放后 MUST 重新登记一次证据，否则不得升格为验收线（RP §4 第 8 条 / 附录 B）。

## 6. 契约摘要（细节只认 RP 附录 D）

**请求**（D.2）：`api` / `query` / `scope` / `limit` / `cursor` / `total` / `explain` /
`highlight` / `snippet` / `scheme` / `filter`。`api` MUST = 2；`query` 去首尾空白后 0..200 码点，
空串返回空结果不报错，超长截断并置 `queryTruncated`；`scope ∈ {all, metadata, lyrics}`；`limit`
缺省 20、`>10000` 压到 10000 并置 `clamped`；`cursor` ≤4KB；`explain` 只对**前
50 条**生成，`highlight` / `snippet` 对**前 300 条**（一页容量）生成；`scheme` 未知值回退默认并保留 `schemeRequested`；
`filter.trackIds` ≤50k、元素 32 hex，只过滤、不参与排序键。

**响应**（D.3）：`api` / `gen` / `scheme` / `schemeRequested` / `items` / `total` / `totalExact` /
`partial` / `clamped` / `queryTruncated` / `restart` / `next` / `trace` / `timings`；`items`
顺序即契约，`score` 只作诊断。

- `explain`：`version` / `kind` / `field` / `fieldId` / `matched` / `total` / `coverage` /
  `exact` / `proximity` / `positioned` / `bucket`(5 元) / `bm25` / `lexicalSources`；**不参与
  排序**，关掉它排序结果 MUST 完全一致（RP §3.7.1 / D.3.1）。
  `resolvedField` / `resolvedFieldId`：**保留字段**。精确度复核生效时 `field == resolvedField`
  （排序与归因同源，取真实命中列 title/artist/album）；只有未复核步（宽命中 / 转写 / 歌词族）
  才用 explain 期逐列探测的保守值。
- `trace`：`steps[]`（`state ∈ ran/skipped/degraded`）、`skipped[]`（`reason` 取
  `kind_dominated`/`budget`/`df_gate`/`capability`）、`truncated[]`（`what` 取
  `prefix_expansions`/`df_estimates`/`typo_variants`）、`deadlineHit`（D.3.3）。
  **S2 过渡**：`StepDominated` 未启用，`reason` 额外出现 `min_exact` / `fuzzy_off` /
  `empty_expression`，`est` 缺失（无 df 快照），`truncated` 恒空。
- `highlight`：UTF-16 偏移（`end` 不含）；`mode ∈ literal/reading/initial`；每字段 ≤32 段、最多
  4 字段，超出置 `highlightTruncated`；歌词高亮只在 `snippet=true` 时返回（D.3.2）。
- `snippet`：≤512 UTF-16 code unit；查询期只读 `library.db`，失败返回 `null` 不报错。

**错误**（D.4）：码 1–8 = 请求无效 / 索引未就绪 / 版本或能力不符 / 忙 / IO / 内存不足 / 已取消 /
内部错误；失败 MUST NOT 用空 `items` 表达。`fallback` 与正式查询同构，`scheme` 固定
`fallback@1`，`explain` / `trace` 为空，`highlight` 仍返回原文区间。
`era_search_decorate` 是独立的**按 trackId 装饰入口**（只读 `library.db`，返回
`highlights` / `snippets`，不含 `items` / `total`；`trackIds` ≤300）。

**S1 必须交付**（RP 修订 v0.5 / §5）：`scope=lyrics` 全链路（请求 → 列裁剪 → 空结果语义 →
`explain.field` 只落在歌词族）与 `typo2`（`recall@1` 的 matcher，`typo1`/`typo2` 是同一角色的
两个实现）。S1 只做「不崩溃 / 不越界」冒烟（无旧基线）；正式门槛与 N21–N24 判定在 S2。

> **S1 实况（2026-11-24）**：`scope=lyrics` 的请求解析、歌词列外过滤
> （`{lyrics_tok lyrics_tr_tok lyrics_rom_tok pinyin_lyrics_tok initials_lyrics_tok} : (expr)`）
> 与单 CJK 字空计划已落地，N21 / N24 已转绿；`explain` / `trace` 已随 S2 落地（T2.7 部分），
> 字段归因见 §9.3。`snippet` 已随 S2 / T2.8 落地（查询期只读 `library.db`）。
> `typo2` 随 S2 / T2.6 落地（N22 / N23 已转绿，仅 `recall@1` 启用）。

## 7. golden vectors 与负例

### 7.1 三层与数量下限（RP §6.1）

数量下限（RP §6.1）：归一化 40 / 切分 30 / 计划 40 / 桶序 30 / 方案 20 / 容错 20 / 负例 20。
计划族覆盖"查询形状 × 三种 scope × 库画像（日韩 / 纯英文 / 纯中文）"；桶序族覆盖 10 个 kind 的
偏序、跨字段、覆盖度与平局；容错族覆盖 `typo1`（≥4）、`typo2`（≥7）、首字符不纠错、CJK 不纠错。

### 7.2 现有格式与新增格式

- 现有 `tests/vectors/analyzer.tsv` / `query.tsv` 是**四列 TSV**（输入 / tokens / 拼音 / 首字母），
  `*` 表示该列不校验；运行器按文件名前缀 `query` 分派查询侧，其余走索引侧。逐字读取逻辑见
  [tests/vectors/README.md](../app/core/indexer/tests/vectors/README.md)。
- S1/S2 新增 **Plan / Rank / Negative** 三层，MUST 断言**期望结构**（term 列表、step kind /
  fieldId / op、minMatch、排序 id 顺序、bucket 元组），**不是**期望字符串或整份响应 JSON。
  其中 Plan 已落地（`plan/*.jsonl` + `tests/PlanVectorTests.cs`，2026-11-24）。
- 结构断言 MUST NOT 依赖存储细节（段号、文件偏移、物理 doc_id、内部计数）；稳定键用
  `trackId` 或向量内自造的 `docId`。

### 7.3 负例规则

每条负例 MUST 同时断言**结果集合**（不得包含谁）与**归因档位**（`explain.kind` / `field`
不得来自哪一档）；空结果要显式断言"零命中"，不能只断言"没崩"。

> **门禁**：每 100 条向量至少 2 条负例（本文约定的工程下限，非 RP 数字）。仓库只放**合成样例**；
> 真实查询只留在 `bench/.work/`（`.gitignore`），绝不入库（RP §6.7.2）。N1–N20 是当前基线，N21–N24 属 `scope=lyrics` / `typo2`（RP §6.2）。

## 8. 证据与未决

### 8.1 数字出处

| 数字 / 规则 | 出处 | 状态 |
|---|---|---|
| 版本锚 1 / 1 / 2 / 1、`options_bits`、`field_id`、min-match 曲线、前缀 ≥3 且仅末词 | RP §3.3.1 / §3.8.2.6 / §3.4.4 / §3.5.4 | 已定 |
| 容错 ≥4→1、≥7→2、首字符不纠错、`df/N > 1%` | RP 附录 B | 已定（外部参照） |
| 单步预算 2k/1k/1k/500/300/300、`4×`、容错变体 ≤32、丢词 ≤3 子集、`kMergeCap`/`maxExp`/拼音变体预算 | RP §3.5 / §3.5.3 / §8.4（后三项未给值） | 待校准 |
| 歌词护栏 4096 行 / 128 KB 每层、去重目标 ≥20% | RP §8.1 / §9 | 待校准 / 目标 |
| 查询 50 ms、单步 150 ms、`rss_query ≤ 60 MB`、常态 <2 MB | RP §3.5.5.2 / §8.0 / §8.3 | 待校准 / 门禁 / 外推 |
| `limit` 10000、`total` 10001、缓存 128、向量下限 40/30/40/30/20/20/20、每 100 条 ≥2 负例 | RP 附录 B / §3.6.4 / §6.1 + 本文约定 | 已定 |

> 状态取值：**已定**（RP 已冻结、不待回放）/ **待校准**（初版默认，S1 回放后定）/ **外推**（比例估算）；
> 本文无「已实测」数字——S0 回放只验证了同语料重复运行 `ids` 稳定（RP §5 落地进度），不能当成延迟 / 体积实测。

### 8.2 仍未决（不阻塞 S1）

RP §7 原列：① `recall@1` 是否作为正式 UI 入口；② 同曲多版本（remaster / live）是否做
`dedupe-versions@1`；③ S4 策展双语表许可证与词表来源；④ ~~是否接受 V2（歌词拼音免位置）~~
——**已决（2026-11-26）否**：5k qbench eval 证明歌词拼音免位置会让 ALL P@1 0.973→0.838、
`woaini` / `woai` / 歌词行题 P@1 1→0；只保留可选音译层免位置，歌词拼音与元数据拼音 /
首字母仍带位置。positions 体积已由 **O8 位置块位打包**压缩（5k Positions −4.16%，
qbench eval 逐题与基线完全一致），查询语义不变。

S0 复核另发现以下**规格内部待统一项**，同样不阻塞 S1，但 S1 MUST 定案：

| # | 待统一项 | 现状 / 处理 |
|---|---|---|
| U1 | `explain.kind` wire 字符串 | ✅ S2 定稿 10 档 lowerCamel：`exact` / `phrase` / `prefix` / `partial` / `pinyinExact` / `pinyinPhrase` / `initials` / `reading` / `crossLingual` / `typo`（不把读音精确 / 短语合并成 `pinyin`）；由 `SearchExplainBuilder.KindName` 输出，`vectors/rank` 覆盖 10 档偏序 |
| U2 | 计划期词典二分次数 | §3.1 写 ≤10 次，§3.5.3 写 ≤3×词数 → S1 统一上限并写进 policy |
| U3 | `kMergeCap` / `maxPrefixExpansions` / 拼音变体预算 | 无值 → S1 回放后定 |
| U4 | 请求 200 码点 vs 计划期 64 码点收缩 | ✅ S1 定案（2026-11-25）：请求层仍按 200 码点截断（`queryTruncated`）；计划层按**可见码点** ≥ `SearchQuery.MaxPlanQueryCodePoints`（64，`SearchPolicy` 可覆盖）**只生成字面主趟**，不生成丢词 / 拼音 / 读音 / 跨语言 / 容错 step。两层门槛独立、不改写查询文本；64 仍是初版策略上限（待回放校准），N15 已转绿 |
| U5 | `scheme` 的 `requires` / 预算 / 向量归属 | §3.4.3 未逐方案列 → S1 注册表显式写死 |

## 9. S0 → S1 交接清单

### 9.1 S0 已落地

- `replay run` / `replay corpus`（`Replay.cs` + `Program.cs`）：命令同 RP §6.7.6，基线 JSON 同 §6.7.4。
- `replay sample`（同日 S0 补齐）：固定 14 条 + 加权回放，默认 200（RP §6.7.1）；
  日志不足时 `--library <library.db>` 按 `sha256(track_id)` 派生标题 / 歌手 / 专辑 / 歌词首行补齐。
- `lyrics stats`（同日 S0 补齐）：三层行数分位 / 去重率 / 护栏触发，用于校准 V6。
- 负例对照基线 `tests/NegativeBaselineTests.cs`：N1–N17 已按 RP §6.2 断言（13 绿 / N15 known-gap；
  N8、N10、N11、N18–N24 blocked，等 S1/S2 的 plan / explain / `scope=lyrics`）。
  **2026-11-25 现状**：N15 已转绿（计划期收缩）；N19 由
  `app/test/search_version_mismatch_fallback_test.dart` + `SearchApiTests` 覆盖，
  N20 由 `SearchConcurrencyTests` 覆盖；仍 blocked 的只有 N10、N11、N18。
- 本文与 `tests/vectors/README.md`；Plan 向量已接线（`plan/*.jsonl` + `PlanVectorTests`）；
  Rank 向量已随 S2 接线（`vectors/rank`）并在 S3 保持；Scheme 向量见 `vectors/scheme`。

> 同日补齐项的**实现文件以合并结果为准**；未合并前不得在验收里声称已具备。

### 9.2 S1 进度（2026-11-24）

| 项 | 状态 |
|---|---|
| Plan 向量 | ✅ 已接线（`vectors/plan/*.jsonl` + `PlanVectorTests`，40 条）；Rank / Scheme 向量已随 S2 接线（`vectors/rank` 30 条、`vectors/scheme` 20 条） |
| `SearchPolicy` / 计划类型与编译器 | ✅ `SearchPolicy.cs`、`SearchPlan.cs`、`SearchPlanCompiler.cs`；默认值快照、plan 稳定 |
| 执行器替换 | ✅ `SearchSession.cs` 只剩 Plan 一条路径，旧六趟直连已删除；三语料 `replay compare` changed=0 |
| scheme registry | ✅ S2 / T2.4 完成：`SearchScheme.cs` 四方案 + 能力协商 + 环检测，`s1-compat@1` 退役 |
| ABI v2 / Dart FFI v2 | ✅ S1 收尾完成；`era_search_api_version()==2`、旧符号清理、Dart DTO 收敛 |
| `scope=lyrics` / `typo2` | ✅ `scope=lyrics` 已落地（N21/N24 转绿）；`typo2` 随 S2/T2.6 落地（N22/N23 转绿），仅 `recall@1` 启用 |

> ⚠ 完整四方案注册与方案向量按 RP T2.4 归 S2；本文要求的「最小 registry」只为让 `scheme` 字段与降级链在 S1 可工作，不等于提前做 S2 的方案评审。

### 9.3 S2 → S3 进度（2026-11-24 起，2026-11-25 更新）

| 项 | 状态 |
|---|---|
| 桶序唯一入口 | ✅ `SearchRanker.Compare`：kind → coverage → field → exactness → proximity → bm25 → docId；`Merge` 同 track 取最高 kind，不累加分数 |
| 证据缺口（v7，已由 S3 补齐） | 原：field 只到字段族、exactness 仅读音趟、proximity 恒常量。现：字面步按 DocStore 归一列取真实 field、exactness（equal/prefix/contains/none）与 proximity 均生效；转写族仍按字段族保守归因；歌词族自 Q1（DocStore v5 `lyrics_digest`）起支持**整行相等**=equal，前缀 / 包含仍 none |
| 方案注册表 | ✅ fast/default/recall/minimal；未知 id → default；缺能力沿 degradeTo 下降；`minimal ⊆ fast ⊆ default ⊆ recall` |
| 方案差异 | fast/minimal 共用 lexical（v7 的 exact+prefix 配对未拆）；recall 额外执行 `typo2@1`（Damerau ≤2、首字符不变、词长 ≥7、依赖词表） |
| typo2 | `FuzzyVocabulary.WithinTwo` + `DistanceTwoCandidates`（首字符前缀区间、严格距离 2、扫描上限 20000）；真实库 `pikasonax` → default 0 / recall 3 |
| explain / trace | ✅ 前 50 条 `explain`（kind / field / resolvedField / matched / coverage / exact / bucket / bm25 / lexicalSources）+ `trace`（steps / skipped）；关闭 explain 时 items 逐条一致（`SearchExplainTests`） |
| 负例（N8） | ✅ 标题「(Cover)」修饰不进 `matched`，真实字段探测为 `title`（保守排序字段仍是 `lyrics`） |
| 陈旧曲目（N12） | ✅ `options_json.libraryDb` → 返回时按 `library.db.tracks` 回查；缓存保留原始 top-k、每次派生过滤；库不可读失败开放 |
| 歌词片段（T2.8） | ✅ `snippet=true` 时前 300 条返回 `{field,text,ranges,truncated}`；查询期只读 `library.db`（`busy_timeout=1000`），原文 / 译文 / 音译按首个字面命中截取 ≤512 UTF-16 窗口；库缺失 / 锁冲突返回 null 不报错；缓存键含 `snippet` |
| 结果高亮（T2.7） | ✅ `highlight=true` 时前 300 条返回 `{field,start,end,mode}`（UTF-16 半开区间；`literal` / `reading` / `initial`）；歌词仅 `snippet=true` 时取原文；每字段 ≤32 段、最多 4 字段，超出 `highlightTruncated=true`；库缺失失败开放；缓存键含 `highlight` |
| 按页装饰（T2.11） | ✅ `era_search_decorate(library_path, request_json) → {api,highlights,snippets}`：请求 `{api:2,query,trackIds[≤300],snippet}`，按 trackId 只读 `library.db` 复用 T2.7/T2.8，不重跑排名；`trackIds` 非 32 hex / 超 300 → 码 1，库缺失 → 码 2；artist 展示文本按 `" / "` 连接（对齐 Dart `artistNames`）；Dart 删除 `search_highlight*` / `script_ranges.dart` / `search_lyric_snippet.dart` |
| 分页与 total（T2.10） | ✅ `cursor` 为不透明 `base64url(32B)`（v1），`next` 由本页最后一条的全序位置生成；`restart=true` 仅当 gen / scheme 失配或位置消失（items=[] / next=null）；`total=none` → -1、`capped` → ≤10001、`exact` → 全量且 `totalExact=true`；分页 / 计数请求每步召回同一候选集（exact 不截断、capped 取 10002）以避免跨页漂移；缓存键含 `cursor` 与 `total` |
| 降级检索（T2.12） | ✅ `era_search_fallback`：只读 `library.db` 参数化 `title/artists/album LIKE '%query%'`，匹配集合与旧 Dart 一致；排序 title(3) > artist(2) > album(1) 再 title/id；`scheme="fallback@1"`、`total` 全量、`next=null`、无 explain/trace；`highlight` / `snippet` 与正式查询同实现；Dart 仅在索引未就绪时调用，失败（码 2/5）回退既有 LIKE |
| 回放评审 | ✅ 真实库 15 条 `changed=0 / identical=true`（本轮不改召回范围）；NDCG / 负例按 kind 扩展待 T2.5/T2.6 |
| 键序 | kind → coverage → field → exactness → proximity → bm25 → docId（S3 起 7 键全部生效；proximity 对歌词 / 算不出时为哨兵） |

**一句话**：查询期只做三件事——把归一化词元编译成显式步骤、按预算给每步上限、用唯一 ranker 按 7 键字典序取 top-k；放宽与排序理由必须来自计划与证据，不藏在常量、趟次或后处理里。
