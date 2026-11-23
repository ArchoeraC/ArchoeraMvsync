# 本地曲库索引器与搜索层重写方案（提案，未实施）

> 状态：**方案文档；S2.7 EraStore 原型已落地，未接入发布路径**（v0.25）。本文只回答两个问题：
> ① 现有索引 / 搜索逻辑为什么"不够好"（结构性原因，不是 bug 清单）；
> ② 如果**直接重构**（不再对外移植），怎样组织成**可组合、可替换、可演进**的方案层，
> 并把索引器 / 搜索逻辑**尽可能全部收进 .NET**。
>
> **范围与前提**：
> - 只改 `ArchoeraMvsync` 本体，**不再做任何跨仓库移植**；
> - 因此不需要"绕开已有算法"——相反，**同一角色允许存在多个算法实现**，
>   由一个显式的「搜索方案（scheme）」挑选并组合；
> - Dart 只保留 UI、IO 与 FFI 组装，召回 / 匹配 / 排序 / 高亮 / 分页 / 归因全部在 .NET。
>
> **数字口径**：文中数字分三类——**实测**（标了语料规模，如 30k 曲 bench）、
> **外推**（按比例估算，明确写"外推"）、**待校准**（初版默认值，S1 回放后调整）。
> 任何"待校准"数字都不应被当成承诺或验收线。
>
> 关联文档（本文不重复其参数表，只引用结论）：
> - [local-search-index-plan.md](local-search-index-plan.md)：原始架构与验收预算（schema / ABI / 里程碑）
> - [local-search-recall-precision-audit.md](local-search-recall-precision-audit.md)：问题台账（§11–§23 是 23 批已落地补丁）
> - [local-search-indexer-references.md](local-search-indexer-references.md)：外部参照与方向性结论
> - [cross-lingual-search-upgrade-plan.md](cross-lingual-search-upgrade-plan.md)：跨语言灰度项（T1/T2）
>
> **范围：只改 `ArchoeraMvsync` 本体（`app/core/indexer/` + `app/lib/services/search/`）。**
>
> 代码对象：
> - `app/core/indexer/`（C# NativeAOT，本项目唯一实现，重构后承载全部搜索逻辑）
> - `app/lib/services/search/`（Dart：FFI 组装 + 结果渲染，重构后**不再包含**召回/排序/高亮算法）

---

## 0. 结论先行

现在的实现**不是设计坏了，而是长歪了**：一个正确的初始设计（FTS5 + 预分词 + 六趟兜底），
被 23 批补丁按"发现一个症状 → 加一个全局常量 → 全量重建"的方式打成了**按语料分布的补丁堆**：

- **12 个 FTS 列里有 5 个是纯派生列**（`lyrics_rom` 还是"半派生"），其中拼音列占了索引体积
  **36%**、让查询慢 **6.5–9.7×**；
- **查询层是字符串编译器 + 20 多个全局常量**，靠"命中数 < `MinExact`"决定语义，同一份代码
  在 400 首合成库和 48,759 首真实库上表现相反；
- **排序不是排序，是合并顺序 + 单池 bm25**，弱信号（首字母）和强信号（歌名精确）在同一池里竞争；
- **逻辑分居两层**：召回/排序在 C#，拼音区间高亮、脚本判定、歌词片段截取在 Dart，
  任何一处改口径都要两边同时改，且没有跨层契约测试。

重写的主张可以压成两句话：

> **第一句：把"匹配类型（match kind）"从隐含在列权重和趟次顺序里的东西，变成查询期一等公民的证据
> （evidence）；索引只负责候选召回，排序在索引之外按显式桶（bucket）决定。**
>
> **第二句：把"用哪些算法、按什么顺序组合"抽成可命名的搜索方案（scheme）；
> 每个算法角色（分词 / 读音 / 匹配 / 排序 / 后处理）都可以有多个实现，
> 方案层只做组合与能力协商——新增算法不改核心，降级方案只换名字。**

具体拆成九个可独立验收的改动：
1. 版本拆维（normalizer / tokenizer / schema / scheme 各自独立），不再"改个阈值也全量重建"；
2. 存储层换成自研 **EraStore**：字面 / 读音是不同字段族，元数据原文进 DocStore（可校验、可解释、可高亮）；
3. 查询层从"六趟 + 短路"改为**声明式查询计划**（编译期决定放宽路径，不再看命中数）；
4. 排序从"单池 bm25"改为**分桶字典序**（kind → 覆盖度 → 字段 → 精确度 → 邻近度 → bm25 → 稳定键）；
5. 新增**方案层（scheme）+ 算法注册表**：算法可替换、方案可并存、能力不满足可降级；
6. 每条命中带 `explain`（含高亮区间），让"为什么搜出这首"可回答，也让回归测试有断言对象；
7. **逻辑下沉 .NET**：召回 / 排序 / 高亮 / 片段 / 分页 / 陈旧 id 过滤 / 归因全部搬进索引器，
   Dart 只发请求、画界面；
8. **硬指标**：相对现状，**索引占用更低、启动更快、查询更快**（§8 给出逐项比值门禁）；
9. **存储层自研（已定）**：EraStore = 不可变段 + 前编码词典 + 块压缩倒排 + 提交槽；
   SQLite 只用于只读 `library.db`，不参与搜索（§3.3.0）。

迁移**分五步（S0–S4，含 S2.5 / S2.7 两个并行子阶段）**，其中 **S1 必须是行为完全等价的重构**（逐条 top-k 字节一致），
否则不进入 S2。理由很简单：在没有等价基线的状态下改语义，永远说不清"变好还是变坏"。

---

## 1. 现状盘点（本项目）

### 1.1 一份实现，但逻辑被切成两层

| 组件 | 现在负责什么 | 问题 |
|---|---|---|
| `app/core/indexer`（C#，NativeAOT） | 分析 / 索引 / 召回 / bm25 排序 / 多趟合并 | 23 批补丁全部堆在这里；排序只是"趟次拼接" |
| `app/lib/services/search/search_index.dart` | 组装请求、解析 JSON、schema 探针 | 持有 `kSearchIndexRankLimit` 等**语义参数** |
| `app/lib/services/search/search_highlight*.dart` | 拼音 / 首字母 / 原文的高亮区间计算 | 复刻了 C# 的拼音与脚本规则，口径必须手工同步 |
| `app/lib/services/search/search_lyric_snippet.dart` | 歌词片段截取 | 与排序无关却是"结果质量"的一部分，且只此一份 |
| `app/lib/services/search/script_ranges.dart` | 脚本判定（CJK / 假名 / 谚文） | 与 C# `ScriptRanges` 重复实现 |
| `app/lib/services/search/query_log.dart` | 查询归因计数 | 归因字段由 C# 产出，聚合却在 Dart |

结论：**不是"要移植"，而是本来就散**。重构的边界应当是——凡属于"搜索语义"的，全部下沉 .NET；
Dart 只保留"把用户意图变成请求、把结果画出来"。

> 这也顺带解释了为什么过去每个门控都要改两处：同一套规则在两层各写一遍，没有跨层契约测试。
> 下沉之后，只有一份实现，golden vectors 只需要约束一份代码。

### 1.2 索引：派生视图被当成独立字段

`FtsStore.FtsColumns` 的 12 列里：

| 列 | 性质 |
|---|---|
| `title_tok` / `artist_tok` / `album_tok` / `filename_tok` / `lyrics_tok` / `lyrics_tr_tok` / `lyrics_rom_tok` | 字面（原文） |
| `pinyin_meta_tok` / `pinyin_lyrics_tok` | 派生（汉字 → 拼音） |
| `initials_meta_tok` / `initials_lyrics_tok` | 派生（汉字 → 首字母） |
| `reading_meta_tok` | 派生（假名 / 谚文 → 罗马字 bigram） |

后果不是"列多了"，而是：

1. **体积**：30,000 曲实测 382.5 MiB，无拼音列时 243.5 MiB —— **拼音列 +139 MiB（36%）**；
2. **延迟**：pooled P50 从 4.7ms 涨到 30.2ms（**6.46×**），单条 `wan`（首字母前缀）P50 **70ms**；
3. **不可解释**：派生列与原文列**没有位置对齐**，所以"查询词命中了第几个词 / 哪一段"不可知，
   只能靠列权重 + 趟次顺序近似；
4. **演进成本**：每加一个召回维度 = 加一列 + 抬 `SchemaVersion` + 全量重建（v2→v7 已重建 5 次）。
   30k 曲全量 109s，按同比例外推 49k 曲约 180s；`optimize` 还要额外 4.5–13s 且**与脏曲数无关**。

### 1.3 查询：字符串编译器 + 全局常量

`SearchQuery.cs` 里能数出这些"策略开关"，全部是 `const` / `static readonly`：

```
MinInitialsBranchLength=2   MinInitialsPrefixLength=2   MinPrefixTokenLength=3
MinCjkFuzzyLength=3         MinFuzzyWordLength=3        MinCrossLingualCjkLength=2
DefaultMaxFuzzyVariants=500 DefaultMaxPinyinVariants=16 MaxPinyinVariantsPerSyllable=4
MaxExactTermsPerVariant=32  MaxCjkQueryLength=64        ReadingBranchMinCjkLength=2
PairExactWithPrefix=true    AllowDropTokens=true        RequireMetadataForSingleChar=true
FirstCharTypoCostsTwo=true
```

问题不在"有常量"，而在**这些常量是绝对数字而不是相对量**：`a` 在某个库里是 3% 文档，
在另一个库里是 0.01%；同一个 `MinExact=1` 在合成库表现为"一命中就停"、在真实库表现为
"零命中就无限放宽"。审计文档 §11 已经实测过同一机制在不同语料上的相反结果。

### 1.4 趟次短路 + 单池 bm25 ＝ 语义由"命中数"决定

`SearchSession.ExecuteUncached` 的执行顺序：

```
主趟(词法 OR 拼音 OR 首字母) → 丢词 → 拼音宽松 → 读音 → 跨语言 → 模糊 → 中文容错
                        ↑ 每步都以 result.Count < MinExact 为门
```

三个结构性缺陷：

1. **短路悖论**：主趟有 1 条弱命中（首字母）→ 后面的严格趟全不跑；主趟 0 条 → 一路放宽到跨语言。
   "有弱命中就停"和"无命中就放宽"同时存在，结果质量对语料分布极度敏感。
2. **跨趟不可比**：每一趟各自 `ORDER BY bm25 ... LIMIT`，然后**按趟次顺序合并去重**。
   于是词法第 100 名排在拼音第 1 名之前、读音第 1 名排在模糊第 1 名之前——这不是排序，是拼接。
3. **同池竞争**：`initials_meta=2.0` 与 `pinyin_meta=2.0` 与 `title=4.0` 在同一个 bm25 里算，
   而 bm25 的 TF/IDF 天然受语料影响，等于把"信号强度"交给了语料统计。

### 1.5 增量与并发口径不清

| 关注点 | 现状 | 问题 |
|---|---|---|
| 脏集判定 | `file_mtime` + 侧车 `(mtime,size)` | 内容变了但 mtime 没变 → 旧词元永久残留（审计 D-3） |
| 写连接 | 单写，全量走 tmp + rename | 基本正确 |
| 读连接 | 单只读会话 + 静态锁 | UI 串行够用，但没有世代（`gen`）失效 |
| 缓存失效 | `CloseCached()` 手动 | 重建 / 增量后容易忘，返回旧结果 |

两条对应的重构动作：用**内容指纹（`rev`）**替代 mtime（§3.8.1）；
缓存键纳入 `gen` + `scheme`，并给重建 / 增量一个**显式失效入口**（§3.8.5）。

### 1.6 评测只覆盖"该找到的"，不覆盖"不该找到的"

`qbench/QueryEval` 的金标准是"曲库文本字面包含查询串"，这对 bigram 方案是**循环论证**：
它奖励"多召回"，惩罚不了"乱召回"；也无法评测跨语言、容错、分层排序是否正确。
审计 §8.1 补了 8 条负例，但还没有"同一批真实查询在新旧实现上的 top-k 差异"这条主线。

---

## 2. 重写要守住的东西

### 2.1 红线（沿用现有约定，不做例外）

- **存储层自研且单一**：`IIndexStore` 接口下只有 **EraStore** 一个实现；
  不做运行时切换、不做第二后端（ADR-1 / ADR-13）；
  自研倒排是**已采用**的路线；WAND / 复杂打分不实现（桶序 + 固定堆足够），但迭代器接口允许将来加。
- **不引入外部服务 / 模型 / 向量**：桌面端不可能内嵌 Meilisearch；纯规则、离线表。
- **不靠调 `limit` 降噪**：`limit` 只影响取多少条，不改变命中集合与相对顺序。
- **不复制 GPL 代码**：Typesense / Navidrome / Groonga 只借参数与规则，独立实现。
- **索引只由 indexer 写**，查询侧只读；索引文件可随时删除重建。
- **资源只能更省**：同一语料下 `bytes_per_track` / `open` / `first_query` / `P50` / `P95` / 构建与查询 RSS
  必须**严格优于**现状（§8 的比值门禁，任一不达标不合并）。
- **搜索语义只允许存在一份**：召回 / 匹配 / 排序 / 高亮 / 片段 / 分页 / 陈旧过滤 / 归因
  全部在 `app/core/indexer`；Dart 不得再实现同类算法（含"为了省一次 FFI 而在 Dart 里算"）。

### 2.2 内部契约：可以整体换，但要成对升级

Dart 与 C# 不是两个产品，而是**同一个安装包里的两层**：没有第三方调用方，也没有跨团队排期。
所以 `indexer_*` 的签名、JSON 结构、甚至"查询请求应该长什么样"都可以在本次重构里**整体替换**——
不必为了兼容旧客户端而保留 `minExact` / `candidateCap` / `fuzzy` 这类语义已经过时的字段。

| 契约 | 现状 | 重写后的处理 |
|---|---|---|
| `indexer_*` C 符号 | 已稳定 | **直接删除**，只保留 `era_search_*`（见 2.2.1）；payload 换代 `api: 2` |
| 12 列列序 = bm25 权重序 | 注释里写"冻结" | 内部实现细节，不再是契约；EraStore 直接换格式 |
| `DocStore.track_id` = scanner `tracks.id` | 跨模块键 | **保留**（这是与 scanner 的唯一耦合点） |
| `passes` 诊断字段 | Dart 侧已消费 | v2 可删除，由 `explain` / `trace` 取代 |
| 版本锚 | 不等即全量重建 | 拆成 `index_format` / `normalizer` / `tokenizer` / `scheme` / `data_pack` 多锚 |

**成对升级规则**（替代"只追加"）：

1. 同一安装包里 Dart 与 native **必须同版本**；打包脚本把两者版本号绑在一起，
   启动时用 `era_search_api_version()` 握手，不一致直接降级 `LIKE` 并报错（不允许"猜着用"）。
2. **不复古**：不存在 `api:1` 兼容路径，不存在旧符号 wrapper，不存在"先双实现再删"。
   旧版本 App 用旧 native，新版本 App 用新 native；交错组合直接拒绝。
3. 载荷换代必须附**契约测试**：请求 / 响应 JSON Schema + 一组 golden 请求（含错误分支）。
4. 只有 `track_id` 语义与 `library.db` 的列是**真契约**；其余都是可以随重构消失的内部约定。

> **新增契约：能力声明（capabilities）**。索引写入时把"这份索引具备哪些能力"写进元数据
> （`surface_text` / `phonetic_meta` / `phonetic_lyrics` / `readings` / `initials_bigram` …）；
> 方案（scheme）声明"我要求哪些能力"。两者交集不足时，引擎**自动挑选降级方案**，
> 而不是运行到一半才发现某一列不存在。这是"多算法 + 多方案"能稳定共存的前提。

#### 2.2.1 C ABI v2 形态（建议，替换"每次传路径"的旧形态）

旧形态是 `indexer_query(index_path, request_json, ...)`：每次调用都传路径、依赖内部静态缓存、
生命周期靠 `indexer_query_close()` 隐式管理。v2 建议改成**显式句柄**：

```c
int32_t  era_search_api_version(void);                 // 2

int32_t  era_search_open(const char* index_path,
                         const char* options_json,     // scheme / readonly / cache 容量
                         era_search** out);           // 打开失败返回非 0

int32_t  era_search_run(era_search* h,
                        const char* request_json,
                        char** out_json, int32_t* out_len);

void     era_search_free(char* json);                  // 释放 run 的输出
void     era_search_close(era_search* h);              // 关闭句柄（内部负责 WAL / 缓存）

// ── 索引构建（与查询句柄分离；同一进程同一路径只允许一个写任务） ──
typedef void (*era_progress_fn)(const char* json, void* user);

int32_t  era_index_start(const char* library_path, const char* index_path,
                         const char* options_json,
                         era_progress_fn on_progress, void* user,
                         era_index_task** out);
int32_t  era_index_wait(era_index_task* t, int32_t timeout_ms);  // 0=完成 2=超时（任务继续）
int32_t  era_index_cancel(era_index_task* t);                    // 幂等；已提交批次保留
void     era_index_free(era_index_task* t);

// ── 无索引时的降级检索（仍是 .NET，不走搜索索引） ──
int32_t  era_search_fallback(const char* library_path, const char* request_json,
                             char** out_json, int32_t* out_len);

// ── 健康 / 诊断（只读；设置页与 CLI 共用） ──
int32_t  era_search_health(const char* index_dir, char** out_json);
```

`era_index_start` 的 `options_json`：

```jsonc
{ "full": false,             // true = 全量；false = 增量（版本不符自动升全量）
  "index_lrc": false,        // 索引 .lrc 侧车
  "pinyin": true,            // 元数据读音
  "lyrics_pinyin": true,     // 歌词拼音（默认开）
  "lyrics_initials": false,  // 歌词首字母（默认关）
  "romaji_lyrics": false,    // 歌词罗马音层
  "merge_pages": 2000 }      // 有界合并页数
```

进度回调 JSON（每次 50 曲或阶段切换时一次）：

```jsonc
{ "phase": "scan|analyze|write|merge",
  "done": 1200, "total": 30000,
  "docsPerSec": 410, "etaMs": 70200,
  "changed": 1200, "deleted": 3, "errors": 0,
  "truncatedLyrics": 0 }     // V6 护栏触发数
```

`era_search_open` 的 `options_json`：

```jsonc
{ "scheme": "default@1",     // 缺省用索引默认
  "cache_capacity": 128,     // 结果 LRU 条数
  "mmap_bytes": 268435456,   // 0 = 关闭 mmap
  "log_level": "warn" }      // debug | info | warn | error
```

未知字段忽略并记一条 warn（内部接口，不做严格 schema 拒绝）；`cache_capacity=0` 表示禁用结果缓存。

`era_search_fallback` 的响应与 `era_search_run` **同构**（`api:2`），区别只有：

- `scheme` 固定为 `fallback@1`（表示"无索引，SQL 降级"）；
- 不返回 `explain` / `trace`；`highlight` 仍返回原文区间；
- 排序用简化的字段权重（title > artist > album），不做桶序——因为它不是正式检索路径。

**返回值约定**（所有 `era_*` 统一）：

| 码 | 含义 | 调用方动作 |
|---|---|---|
| 0 | OK | — |
| 1 | 参数 / JSON 无效 | 修请求，不重试 |
| 2 | 索引不存在或未就绪 | 触发构建或降级 `LIKE` |
| 3 | 版本 / 能力不符 | 删除重建（见 §3.8.2.6） |
| 4 | 忙（写任务进行中 / 文件锁被占） | 重试或等 `era_index_wait` |
| 5 | IO / 磁盘满 / 只读 | 提示用户；保持 `LIKE` |
| 6 | 内存不足 | 降级 `LIKE`，记录 |
| 7 | 已取消 | 正常路径，不告警 |
| 8 | 内部错误（bug） | 记日志 + 降级 `LIKE` |

**线程与所有权**：

- `era_search_run` 可以在**不同句柄**上并发；同一句柄内部串行（或由调用方保证不并发）。
- 回调在**工作线程**上触发；回调里禁止再调用任何 `era_*` 接口（避免自锁）。
- `era_search_free` / `era_index_free` 只释放对应 API 返回的内存，不能混用。

好处：

- **生命周期显式**：重建索引前先 `close`，不再依赖"记得调 CloseCached"；
- **句柄可并发**：一个进程可同时持有"只读查询句柄"与"写句柄"，职责清晰；
- **路径只解析一次**：方案 / 能力协商在 `open` 时完成，`run` 只做纯查询；
- **测试更容易**：不需要全局静态状态，句柄可注入。

迁移方式：**一次性替换**——加 `era_search_*`，同时删除全部 `indexer_*` 符号；Dart 同 PR 切到新符号，
打包脚本 / 校验 / 文档中所有旧符号引用一并改掉。不留 wrapper，不留别名。

---

## 3. 目标架构

### 3.1 总览：六层 + 一个薄边界

```
┌─ 1 归一化层 ─────────────────────────────────────────────────┐
│ Normalize(text) → 归一形；纯函数，带 normalizer_version        │
└──────────────────────────────────────────────────────────────┘
┌─ 2 文档模型层 ───────────────────────────────────────────────┐
│ Doc{fields, layers} → Unit[field, seg] → Token{surface,        │
│ reading, initials, pos}；索引期一次性成本                      │
└──────────────────────────────────────────────────────────────┘
┌─ 3 物理索引层（可替换后端） ─────────────────────────────────┐
│ EraStore：单文件 + 不可变段 + 前编码词典 + 块压缩倒排          │
│ field_id 命名空间：字面 / 元数据读音 / 歌词读音 / 读音表        │
│ 接口 IIndexStore；唯一实现 = EraStore（无运行时切换）          │
└──────────────────────────────────────────────────────────────┘
┌─ 4 方案层（Scheme） ─────────────────────────────────────────┐
│ 角色 → 多个算法实现；scheme = 选哪些实现 + 顺序 + 策略 + 能力  │
│ 能力协商：索引声明的 capabilities ∩ scheme 要求的 capabilities │
│ 不允许 → 自动降级到 degradeTo 方案（不报错、不半途而废）      │
└──────────────────────────────────────────────────────────────┘
┌─ 5 查询计划层 ───────────────────────────────────────────────┐
│ Query → Term[] → Plan{steps, budget, minMatch}；纯函数         │
│ 放宽顺序在**编译期**决定，不看运行期命中数                     │
└──────────────────────────────────────────────────────────────┘
┌─ 6 决策层 ───────────────────────────────────────────────────┐
│ Evidence{kind, field, term, coverage} → 选定的 Ranker → top-k  │
│ 输出含 explain + highlight spans；同分按稳定键，分页不抖        │
└──────────────────────────────────────────────────────────────┘
        ▲
        │  era_search_run / era_search_open（C ABI）
        ▼
┌─ Dart（只做 UI / IO） ───────────────────────────────────────┐
│ 组装请求 → 调用 FFI → 渲染；不再计算高亮 / 片段 / 脚本 / 分页  │
└──────────────────────────────────────────────────────────────┘
```

**关键边界**：

- 第 4 层是"组合"，不含算法本体；算法都在注册表里，可以单独测、单独换。
- 第 5 层的 `Compile` 不接触存储：df / 预算由上一层以 `IndexStatsSnapshot` 注入（≤10 次词典二分），
  编译本身是纯函数；第 6 层不接触存储查询（只吃 evidence + 候选）。
  这样"查询为什么这么排"可以在单测里用假数据完整覆盖，不依赖真实库。
- Dart 与 .NET 之间只有 JSON + C ABI；**任何一层算法都不允许在 Dart 里复刻一份**。

### 3.2 第 1 层：归一化（保留，但收口）

现状已经做得不错（兼容折叠表 + 去音符号 + 异体字 + 繁简 + 脚本切分），**保留语义，改三件事**：

1. **单一来源**：`data/*.tsv`（`nfkc` / `accent_fold` / `variant_fold` / `kana_romaji` /
   `ja_readings` / `def_*`）打成一个**带校验和的版本化数据包**，由 C# 独占读取与解释。
   高亮所需的数据也由 C# 在查询结果里直接返回区间，Dart 不再读表、不再算拼音。
2. **版本拆分**：`normalizer_version`（只影响归一输出）与 `tokenizer_version`（只影响切分
   bigram/unigram 规则）分开。现状都挤在 `AnalyzerVersion` 里，改一个就全量重建。
3. **可回溯日志**：调试模式下记录"原串 → 归一串 → 词元"，用于排查 D-6 那类"折叠链改动原意"。

> 不建议动的地方：CJK **bigram + 首尾 unigram** 这套切分是真实的工程最优解
> （`fts5-cjk` 实测、我们的 bench 都验证过），重写不要"顺手换成分词器"。

#### 3.2.1 归一化管道规格（逐步骤、可单测）

顺序是契约，任何调整都算语义变更：

| # | 步骤 | 输入 → 输出 | 边界与失败 | 影响哪一维版本 |
|---|---|---|---|---|
| 1 | 逐码点兼容分解（`nfkc.tsv`，NFKD 语义） | `ﬁ`→`fi`、`①`→`1`、全角→半角 | 表未覆盖则原样保留；孤立代理→`U+FFFD`；**不重组** | `normalizer_version` |
| 2 | 小写 | `Locale.ROOT` / `ToLowerInvariant` | `İ` → `i` + `U+0307`，由下一步去掉 | `normalizer_version` |
| 3 | 去音符号折叠 | `é/ü/ñ/ā`→ASCII 基字母；组合音标（含 `U+0307`）丢弃 | `ł/ø/đ/ß` 无规范分解，保持原样 | `normalizer_version` |
| 4 | 异体字补齐 | 日文新字体→简体（`鉄→铁`） | 只补繁简词库未覆盖的字符 | `normalizer_version` |
| 5 | 繁转简 | ToolGood 词级转换 | 一次整串调用，不逐字 | `normalizer_version` |
| 6 | 脚本切分 | 连续同类码点成段：Han / Kana / Hangul / Latin / Digit | 标点/空白为分隔符，丢弃；混合脚本按段切开 | `tokenizer_version` |
| 7 | 段内切词 | Han/Kana/Hangul：bigram + 首尾 unigram；Latin/Digit：整词 | 单字段→单 unigram；空段丢弃 | `tokenizer_version` |
| 8 | 派生视图 | 拼音（逐字）/ 首字母（bigram）/ 罗马字（bigram） | 段内有不产读音的字→**整段不产读音**（不产错位数据） | `tokenizer_version` |

两条常被忽略的约束：

- **`Normalize` 必须是幂等的**：`Normalize(Normalize(x)) == Normalize(x)`。
  这条要进单测（现状没有覆盖，繁简 + 异体字串联存在理论上的非幂等点）。
- **索引侧与查询侧必须调用同一个函数**，不允许查询侧"少做一步为了快"。
  现状 `AnalyzeQuery` 与 `Analyze` 共用 `AnalyzeCore`，这一点要保留。

#### 3.2.2 数据包规格（C# 与 Dart 同一版）

```
app/core/indexer/data/            # 生成物，不入库源码，只入库生成脚本 + 校验和
  manifest.json                   # {name, version, sha256, source, license}
  nfkc.tsv  accent_fold.tsv  variant_fold.tsv
  kana_romaji.tsv  ja_readings.tsv
  def_hanzi2en.tsv  def_en2hanzi.tsv        # S4 前保留，S4 后由策展表替代
```

- 生成脚本在 `app/core/indexer/tools/gen_tables/`，产物带 `manifest.json`；
  **构建期**校验每个文件的 `sha256` 并把 `DATA_PACK_ID`（manifest 的哈希）写进索引。
- **运行期只比对 `DATA_PACK_ID`**（O(1)），不做全量哈希；全量校验只放在设置页的显式「校验数据」动作里。
  不一致时拒绝启用索引，而不是静默沿用旧数据表。
- **Dart 不再需要任何数据表**：高亮区间由 C# 计算并返回，拼音 / 罗马字 / 脚本判定只在 .NET 侧使用。
  这同时消掉了"两层读同一份表但版本不一致"的整类问题。
- 若未来 UI 需要离线预览（不经过查询），再复制 `data/` 进 Flutter 资源，并在启动时比对 `manifest.id`。
- 数据包版本变化属于 `data_pack_id` 变更 → 索引作废重建（与 §3.8.2.6 的一票否决一致）。

#### 3.2.3 表结构与冲突规则

数据包里的表不是"随便两列"，每一张都有固定语义、固定列数、固定校验：

| 文件 | 列 | 说明 |
|---|---|---|
| `nfkc.tsv` | `src \t dst_seq` | 兼容分解：`src` 单码点；`dst_seq` 1–3 个码点；语义为 **NFKD**（不重组） |
| `accent_fold.tsv` | `src \t dst` | 单码点 → 单码点；去掉组合音标（含 `U+0307` 等） |
| `variant_fold.tsv` | `src \t dst` | 单码点 → 单码点；异体 / 旧字体 → 目标形 |
| `kana_romaji.tsv` | `kana \t romaji` | 允许多字符键；运行时**最长匹配** |
| `ja_readings.tsv` | `kanji \t reading;reading` | 只给日文画像用；多读音用 `;` 分隔 |

码点统一写成 `U+XXXX` / `U+XXXXX`，避免不可见字符与制表符歧义；多码点输出用空格分隔。

**为什么用 NFKD 而不是完整 NFKC**：检索只需要"两侧同一个稳定形"，不需要组合回 NFC。
不做组合有两个好处：不依赖运行时的 Unicode 版本（否则 .NET/ICU 升级会悄悄改变归一结果），
且表可以完全由 `data_pack_id` 锁定。展示始终用原文，不用归一形。

#### 3.2.4 冲突、闭包与幂等

生成期就要把冲突解决掉，运行期只做**一次查表**（NFKD 允许最多 2 轮直到稳定）：

| 冲突 | 规则 |
|---|---|
| 同一码点同时出现在 `accent_fold` 与 `variant_fold` | **拒绝生成**；由人工决定归哪张表 |
| `variant_fold` 出现链（A→B、B→C） | 生成期压平成 A→C、B→C，运行期不再迭代 |
| `nfkc` 输出又以自身为源 | 生成期求闭包；运行期最多 2 轮，超过即判表损坏 |
| `dst` 为大写 | 拒绝（小写在第 2 步，表内保持小写目标） |
| `dst` 为空 / 含空白 / 含控制字符 | 拒绝 |
| 同一 `src` 重复 | 拒绝（后写不覆盖，直接失败） |

**幂等是硬门禁**：

```text
for all x: Normalize(Normalize(x)) == Normalize(x)
```

- 生成期对全表做闭包检查：`Normalize(dst) == dst`；
- 运行期单测覆盖 BMP 全量 + 10 万随机 astral 码点 + 组合音标串；
- 索引侧与查询侧必须调用**同一个** `Normalize`（同一二进制、同一数据包），
  否则会出现"索引里有、查询搜不到"的幽灵问题。

#### 3.2.5 运行时实现与性能

```text
Normalize(text):
    if all ASCII and no uppercase: return text        // 快路径，覆盖大多数拉丁查询
    chars = text
    for pass in 0..1:                                  // NFKD 最多两轮收敛
        out = mapCompat(chars)                         // 单码点查表；多码点输出展开
        if out == chars: break
        chars = out
    chars = toLowerInvariant(chars)
    chars = mapAccent(chars)
    chars = mapVariant(chars)
    return convertHans(chars)                          // 词级，整串一次
```

- 表加载成 `int → string` / `int → int` 的只读字典；BMP 用数组直查，astral 走字典；
- 不为每个字符分配 `string`：多码点输出写入 `Span<char>` 缓冲；
- 目标：50k 曲构建时归一化占比 <10%；单查询（≤200 码点）<20µs；
- 不做正则、不做逐字 `string.Replace`、不在热路径用 LINQ。

**语义边界**：

- 兼容折叠会合并一些视觉相近但语义不同的串（全角/半角、异体字）；这是**检索有意为之**，
  不是安全边界；展示与导出始终用原文；
- 不把 Cyrillic / Greek 折叠成拉丁（ homoglyph 攻击不在本层处理）；
- 繁转简是**词级**转换，不做逐字映射；词表未覆盖的保持原样。

### 3.3 第 2–3 层：文档模型与物理索引（**这是重写的核心**）

现状是"一个字段 → 一个列 → 一串空格分隔 token"，丢失了三样东西：
**段落在字段内的下标、派生视图与原文的对齐、以及可校验的原文**。

目标模型：

```
Doc {
  trackId, rev,                          // rev = 内容指纹
  fields: [ Field{kind, text} ],          // title/artist/album/filename/lyrics/tr/romaji
  layers: [ Layer{kind, confidence} ]     // 原文/译文/音译 + 置信度（S4 收口）
}

Unit  = (field_kind, seg_ord)             // 字段内的一个语义段（一句歌词 / 一个标题段）
Token = (surface, reading, initials, pos) // 同一位置的三视图
```

落地时**不必真的建 unit 表**，只需在**写入时保证三个视图同源同序**（同一段、同一 token 序号）：

- 字面：`title` 字段的 token 序列 = `晴天 / 晴 / 天`（bigram + 首尾 unigram）
- 读音：`pinyin = "qing tian"`（按**段内字符序**对齐：第 i 个音节 ↔ 第 i 个字）
- 首字母：`initials = "qt"`（≥2 字时改为字符 bigram，如 `xzq → "xz zq"`，不出 unigram）

对齐的强约束：**段内只要有一个字不产读音，整段就不产读音**（不做"发占位音节"——
分词器会把标点类占位符当分隔符丢掉，占位不可靠）。这正是现状
`QueryTokens.PinyinComplete` 在兜的事情，重写后应当从"查询期放弃"提前到"索引期就不产错位数据"，
并把该段的 `reading_complete=0` 记进 `Stats` 段供诊断。

这样"查询命中了第几个词"就退化成"在读音串里是第几个音节"，可以用 `PhraseIter`（同段内相邻）
精确表达，而不是现在的"整段一个 token / 整段短语"。

#### 3.3.0 存储引擎：EraStore（自研，已定）

**决定**：搜索索引不再使用 SQLite / FTS5，改为自研 **EraStore**（ADR-1）。
理由不是"想自己造"，而是三条硬约束只有自研能同时满足：

1. **体积上限**：FTS5 的 4KB 页、b-tree、freelist 决定了下限；同内容下 EraStore v1 预期 **0.55–0.75×**，编码优化后 **0.45–0.60×**。
2. **启动上限**：SQLite 打开要建连接 / 读 schema / 页缓存；EraStore 只读 Header + 提交槽 + Manifest，O(1)。
3. **能力可控**：`detail` / `contentless` / 列过滤语法把能力写死在引擎里；EraStore 的能力由字段族与词条内容显式决定。

`library.db` 仍由 scanner 用 SQLite 持有（跨模块契约），但**搜索索引与搜索查询都不经过 SQLite**。

**设计原则**（后面的每个细节都服务于这四条）：

| 原则 | 含义 |
|---|---|
| **只读路径零解析** | 打开只读固定 3 处；查询只做"词典二分 + 倒排解码"，没有通用解析器 |
| **写路径只追加** | 段不可变；更新 = 新 doc + tombstone；合并才重写 |
| **能力显式化** | `capabilities` 由段头 / 字段族 / 词条内容推导，不靠引擎隐式行为 |
| **每一字节可归因** | 体积报告能拆到"词典 / 倒排 / 位置 / DocStore / 统计 / 墓碑" |

**为什么不用现成引擎**：

| 候选 | 否决理由 |
|---|---|
| SQLite FTS5 | 体积 / 启动下限被页与 b-tree 锁死；`detail=none` 直接禁用短语，能力不可组合 |
| LMDB / RocksDB + 自研倒排 | 两套语义、两套故障模型；KV 层解决不了倒排压缩与块跳过 |
| 现成搜索引擎（Tantivy 等） | 引入另一套排序 / 格式 / 版本策略，与"逻辑收进 .NET、能力可解释"冲突 |

**不做的**：不做通用数据库、不支持 SQL、不支持任意 schema；它就是"音乐搜索专用存储"。
这既是可控性的来源，也是维护成本的边界。

#### 3.3.0.1 文件、段与提交

**目录结构**（每个世代一个文件，`CURRENT` 是唯一可替换的小指针）：

```text
search/
├─ CURRENT                 # "search-7.eraindex\n"；原子替换
├─ search-7.eraindex       # 当前世代（只追加）
├─ search-6.eraindex       # 上一世代（宽限期后删除）
└─ .write.lock             # 写者排它锁
```

**文件结构**（偏移单位：字节）：

| 区 | 偏移 | 大小 | 内容 |
|---|---|---|---|
| Header | 0 | 64 | magic `ERAIDX\x01\x00`、format、flags、header_size、file_uuid、created_ms、CRC32C |
| CommitSlot[0] | 64 | 32 | `{gen, manifest_off, manifest_len, manifest_crc, slot_seq, reserved}` |
| CommitSlot[1] | 96 | 32 | 同上；两槽交替写 |
| 数据区 | 128 | … | 段（DocStore / Dict / Postings / Positions / Stats / Tombs） |
| Manifest | 尾部 | ≤64 KB | UTF-8 JSON：段列表、版本锚、能力、`gen`、统计摘要 |

- **提交 = 写 Manifest → fsync → 覆盖较旧槽 → fsync**；槽里的 `slot_seq` 单调递增，
  打开时取"CRC 有效且 `slot_seq` 最大"的那个。
- **崩溃安全**：尾部未提交的字节没人指向，直接忽略；两槽都坏 → 尝试上一世代 → 都坏 → 重建。
- **增量不换文件**：同一世代文件内追加段；**全量重建**才写新世代文件并替换 `CURRENT`。
- **varint = 无符号 LEB128**；所有多字节整数显式小端；偏移一律 `long`；
- **所有偏移都是绝对偏移**；读取前校验 `off+len` 不越界、Section CRC 一致；
- **旧 Manifest / 被合并段算"垃圾字节"**：`era-index stats` 单独报告；只有世代切换（全量重建）
  才物理回收，合并只让它们不再被引用；不靠"每次打开重写文件"。

**格式演进规则**（避免"格式一升级就得重建历史"）：

| 变更 | 规则 |
|---|---|
| 新增可选段 / 可选字段 | 用 `section_type` 新值或 `flags` 位；旧读者跳过，不报错 |
| 改变现有段语义 | 必须升 `format`（major）；旧文件直接作废重建，**不做就地升级** |
| 预留字段 | 写 0，读时忽略；不得用"预留位"做实验特性 |
| 未知 required flag | 拒绝打开并降级 `LIKE`，不允许"猜着读" |
| 文件来自不可信来源 | 所有长度 / 计数先做上限校验（段 ≤ 文件大小、doc_count ≤ 2^32、词典块 ≤ 4KB），防止恶意文件触发巨量分配 |

#### 3.3.0.2 词典编码（体积与查询的共同核心）

词典是**一棵**按字节序排列的词典，键 = `field_id(1B) || term_utf8`，所以：

- 同一字段族的词条天然连续；
- 前缀查询 = 一段连续区间，不存在"跨字段误命中"；
- `df` 就在词条里，计划期不需要任何统计表。

**块结构**（每 64 个词条一块）：

| 区 | 大小 | 说明 |
|---|---|---|
| 块头 | 8B | `term_count u8`、`first_len u8`、`flags u8`、`reserved`、`crc32c u32` |
| 首词 | 1 + n | 块内第一个词条**完整存储**（便于块内定位与调试） |
| 词条 × n | 变长 | 前编码：`shared u8` + `suffix_len u8`（255 用 u16 转义）+ `suffix` + `df varint` + `post_off u32` + `flags u8` |

**块索引**（独立段）：每块一条 `{prefix8[8], block_off u32, block_len u32}`（16B）。

- 查找：先对 `prefix8` 二分块索引，再在块内线性扫（≤64 条）；`prefix8` 相同的块可能不止一个，需向相邻块扩展后再判定；
- 前缀：`lower_bound(field_id || prefix)` → 扫到不匹配为止；上界用 `prefix || 0xFF…`；
- **不做 DAWG / 双数组 trie**：前编码 + 块索引已把词典压到接近熵，Trie 的构建与更新成本不划算；
- 词条长度上限 255B（UTF-8），超长直接拒绝并记 warn（正常歌名/词元远小于此）。

预期：词典比"b-tree + 重复 term 指针"小 **35–50%**。

#### 3.3.0.3 倒排与位置编码

**倒排块**（每 128 doc 一块）：

| 区 | 大小 | 说明 |
|---|---|---|
| 块头 | 14B | `doc_count u16`、`last_doc u32`、`max_tf u16`、`flags u8`、`crc32c u32`、`tf_exc_count u8` |
| tf 位图 | 16B | 128 bit：`1` 表示该 doc 的 tf = 1（最常见） |
| doc 序列 | 变长 | `doc_delta varint`（<128 占 1B） |
| tf 例外表 | 变长 | 仅 `tf > 1` 的 doc：`doc_index u8` + `tf varint` |
| 位置块偏移 | 4B | 有位置时指向 Positions 段；无位置为 0 |

- **每块独立编码**：首个 `doc_delta` 相对 0（即首 doc 的局部 id），块内后续为与前一个的差值；
  这样跳块不必依赖上一块；块内严格递增；
- 跳块：`Advance(target)` 用 `last_doc` 跳过整块，再块内解码；
- **tf=1 不付字节**：位图 16B / 128 doc ≈ 0.125B/doc；只有重复词才进例外表。
  这比"每个 posting 一个 tf 字节"省 **70–90%** 的 tf 开销，且解码是位测试，比 varint 更快。

**位置块**（独立段，只有 `has_positions` 的词条才有）：

| 区 | 大小 | 说明 |
|---|---|---|
| 块头 | 8B | `doc_count u16`、`last_doc u32`、`flags u8`、`crc32c u32`（对齐后 12B） |
| doc 序列 | 变长 | `doc_delta varint` + `pos_count varint` + 逐位置 Δvarint |

- 位置按**段内 token 序号**记录；`seg_sep` 也占一个位置，所以跨段相邻天然被阻断；
- 位置块与倒排块**按相同 doc 顺序**排列，短语查询只对候选 doc 解码位置；
- 不做"全字段位置"以外的扩展：没有字符级偏移（高亮由 DocStore 原文重算）。

预期：倒排比固定 u32 小 **40–60%**；位置比"原始 int32 列表"小 **50–70%**。

#### 3.3.0.4 DocStore 与文档映射

DocStore 是**列式**的，只服务三件事：精确度复核、结果字段、歌词片段定位。

| 列 | 类型 | 说明 |
|---|---|---|
| `track` | 16B × N | MD5；结果唯一键 |
| `rev` | 16B × N | 内容指纹 |
| `script_bits` | u16 × N | 脚本画像 |
| `flags` | u8 × N | 是否有译文 / 音译 / 侧车 |
| `doc_len` | u16 × 6 | 每字段 token 数；溢出进侧表 |
| `title` | 偏移表 + UTF-8 | 歌名原文（归一后） |
| `artist_id` | u16/u32 × N | 歌名字典 id |
| `artist_dict` | 唯一串表 | 歌手重复度高，字典化收益大 |
| `album_id` / `album_dict` | 同上 | 专辑重复度更高 |
| `docmap` | 排序数组 | `hash(track) → local_doc`，只给工具与增量用，**查询不读** |

- **doc 引用**：`doc_ref = (segment_id << 32) | local_doc_id`（u64）；倒排只存 local id；
- 结果按 `track` 去重，保留"最新段 / 最新版本"；
- `docmap` 不参与查询：候选 ≤5k，用内存字典按 `track` 去重即可，避免打开时构建全局映射。

预期：DocStore 只占索引的 **5–10%**（50k 曲约 5–10 MB）。

#### 3.3.0.5 查询执行与剪枝（无 SQL）

**迭代器**（全部 `ref struct`，只读 `Span<byte>`）：

| 迭代器 | 行为 |
|---|---|
| `TermIter` | 词典命中 → 倒排块顺序解码；`Advance(doc)` 走块头跳过 |
| `PrefixIter` | 词典区间内多词倒排归并；用于前缀与纠错变体 |
| `AndIter` | 按 doc 求交，小步前进（leapfrog） |
| `OrIter` | 按 doc 归并（小顶堆） |
| `PhraseIter` | 位置块解码 + 同段相邻判定；无位置字段自动降级 `AndIter` 并置 `positioned=false` |
| `DocIter` | 顺序扫 DocStore（纯元数据短查询 / `min_match` 兜底） |

**执行顺序与剪枝**：

1. 步骤按 `kind_rank` 从高到低执行；
2. 堆（top-k）满了以后，若当前 step 的 `kind_rank` **严格劣于**堆内最差候选，直接跳过整个 step；
3. 单步有 `Cap`；合并后候选总量 ≤ `kMergeCap`；
4. `deadline` 每 4096 个 doc 检查一次，超时置 `partial`；
5. `total` 只在 `capped` / `exact` 模式计数：段内用 bitset 去重，数到 10001 即停。

> **不做 WAND / block-max 打分剪枝**：桶序的第一键是 `kind`，不是 tf；用 tf 上界剪枝会改变语义。
> 省钱靠"步骤顺序 + kind 剪枝 + Cap"，不靠近似分数。

**查询期内存预算**（`rss_query ≤ 60 MB` 的落点）：

| 结构 | 上限 | 复用方式 |
|---|---|---|
| 候选表 | 5k × 48B ≈ 240 KB | 池化 `Dictionary`，`Clear()` 后保留容量 |
| top-k 堆 | 10k × 32B ≈ 320 KB | 每查询一个池化数组 |
| 已访问 bitset | 每段 16k bit = 2 KB；池 64 个 ≈ 128 KB | 按段取用，查询结束归还 |
| 解码缓冲 | 每迭代器 2 × 4 KB | `ArrayPool<byte>` |
| DocStore 块 | 1 MB | 全局单块缓存 |
| **常态合计** | **< 2 MB** | 不含结果对象与 mmap 页 |

- 规则：**每查询不得产生大对象分配**（>85KB 进 LOH）；候选 / 堆 / bitset 全部池化；
- 结果 `Candidate` 用 `struct` + `List<Candidate>`（容量按 `limit` 预分配）；
- 嵌套查询（高亮 / 片段）不新开连接、不复用候选表，避免相互污染。

#### 3.3.0.6 构建流水线与内存控制

```text
读 library.db（512 曲/批）
  → 并行分析（N-1 worker，纯函数）
  → 单写者：term 内部化 + 倒排累积（内存段）
  → 64MB 或 16k doc 刷段（写段 + CRC）
  → 每 128 曲提交一次 gen（增量）；全量构建在收尾一次提交
```

- **term 内部化**：`Dictionary<byte[], int>`（自定义比较器）+ 单一 arena 存 UTF-8，避免每个 token 一个 string；
- **倒排累积**：每 term 一个 `List<int>`（doc）与 `List<int>`（pos），刷段时排序 + Δ编码；
- **确定性**：worker 输出按 doc_id 排序后交给写者；词典刷段前按字节序排序；同样的输入必得同样的段；
- **内存上限**：`rss_peak_build ≤ 0.8×` 靠"流式 + 及时刷段 + 复用缓冲"，不靠 GC 调参；
- **歌词去重**：每层维护行哈希集合（xxHash64），去重后再分词；三层独立计数；
- **进度回调**：每 50 曲或阶段切换一次，带 `changed / deleted / truncated / errors`。

#### 3.3.0.7 合并、回收与写放大

- **分层（tiered）**：段按 doc 数分桶（≤16k、≤64k、…）；同层 ≥4 段合并到下一层；
- **段数上限 64**：超过强制合并，保证 Manifest 始终 ≤64KB、`open` 不被段列表拖慢；
- **触发**：死行比例 ≥20% 或段数 ≥8；单次合并时间预算 2s；
- **合并只搬活 doc**：丢弃 tombstone；按 term 归并倒排，重算 `df` / `max_tf`；
- **原子性**：合并写新段 → 新 Manifest → 提交槽；中途失败不回滚已提交的增量；
- **写放大**：段大小每翻 4 倍合并一次，单 doc 生命周期内约 `log₄(N/16k)` 次重写（50k 曲≈1–2 次）；
- **旧段字节成为垃圾**：单文件不能原地删除；新 Manifest 不再引用它即可。
  垃圾比例 >30% 时排一次全量重建（新世代文件），顺带物理回收；
- `era-index stats` 同时报 `live_bytes` 与 `file_bytes`，两者差值就是垃圾。

#### 3.3.0.8 崩溃、并发与自愈

| 场景 | 规则 |
|---|---|
| 提交 | 数据段 + CRC → `fsync` → Manifest → `fsync` → 提交槽 → `fsync` |
| 掉电 / kill -9 | 提交槽只会指向旧或新；未提交尾部忽略；CRC 不符回退另一槽 |
| 并发读 | 读者持 `(gen, manifest)` 快照；mmap 段不可变；无锁 |
| 并发写 | 进程级排它锁（`flock` / `LockFileEx`）；5s 取不到 → 只读模式 |
| 全量切换 | 新世代文件 + `CURRENT` 原子替换；旧世代宽限期后删除 |
| 只读介质 / 网络盘 | mmap 或排它锁不可用 → 禁用索引，降级 `LIKE` |
| 文件损坏 | 打开只校验 Header + 槽 + Manifest；全量校验走 `era-index verify`；两槽都坏 → 重建 |
| 归档 | 坏文件最多留 1 份 `search.corrupt.<ts>`，**只归档不读取** |

#### 3.3.0.9 体积模型（50k 曲，估算）

| 组成 | 估算 | 说明 |
|---|---|---|
| 词典 | 25–45 MB | 唯一词 2–4M；前编码后平均 10–14B/词 |
| 倒排 | 100–135 MB | 约 70–90M postings；Δvarint（<128 占 1B）+ tf 位图 |
| 位置 | 70–95 MB | 约 60–80M 位置；Δvarint |
| DocStore | 5–10 MB | 列式 + 歌手/专辑字典 |
| 统计 / 墓碑 / 块索引 | 2–5 MB | 与文档数线性 |
| **合计** | **202–290 MB** | 现状 382.5 MB → **v1 0.53–0.76×**；位打包 / 例外优化后 0.45–0.65× |

- 加位打包（PForDelta）与更激进的 tf 编码：再降 **8–15%**；
- 关歌词拼音位置（V2，非默认）：再降 **5–8%**；
- 结论：**不关任何功能也能压到 0.80× 以内**；能否到 0.60× 取决于编码优化落地。

#### 3.3.0.10 性能模型（单查询）

| 操作 | 成本 | 说明 |
|---|---|---|
| 词典二分 | 2–4 次块索引 + 1 次块内扫 | 块索引顺序读，缓存友好 |
| 倒排解码 | ~50–100M doc/s | Δvarint + 单字节 tf；Span 顺序解码 |
| 位置解码 | ~30–60M pos/s | 只在短语 / 邻近度需要时 |
| DocStore 复核 | ~2k 次短串比较 / ms | 列式块读，顺序 |
| 堆排序 | O(n log k) | k = limit，n ≤ 5k |
| open | < 1 ms | 64B + 64B + Manifest |

目标：字面 P50 1–2 ms，含读音 3–6 ms；冷首查 ≤30 ms；open ≤10 ms。

#### 3.3.0.11 优化清单（按 ROI 排序，逐项实测）

| # | 优化 | 预期收益 | 风险 / 前提 |
|---|---|---|---|
| O1 | 词典前编码 + 前缀8 块索引 | 词典 −35–50%，查找 −30–50% | 已含在 v1 |
| O2 | Δvarint 倒排 + `last_doc` 跳块 | 倒排 −40–60%，选择性查询 −20–40% | 已含在 v1 |
| O3 | 位置 Δvarint + 独立位置块 | 位置 −50–70%；无位置查询不付解码 | 已含在 v1 |
| O4 | DocStore 列式 + 歌手/专辑字典 | 元数据 −30–40% | 已含在 v1 |
| O5 | tf=1 位图 + 例外表 | tf 开销 −70–90% | 已含在 v1；解码是位测试 |
| O6 | 结果 LRU（键含 `gen`/`scheme`） | 重复查询 P50 −30% | 内存 ≤128 条 |
| O7 | 候选 bitset 去重（段内） | `total` / 去重 −20–30% | 用池化 bitset |
| O8 | 位打包 / PForDelta | 倒排再 −8–15% | 复杂度高；仅在体积门禁不足时启用 |
| O9 | SIMD varint 解码 | 解码 ×1.5–2 | 先 .NET intrinsics，后考虑 Zig |
| O10 | 预取（`Prefetch` 词条块 / 倒排块） | 大库 P95 −10–20% | 跨平台差异大，需实测 |

**顺序**：O1–O7 是 v1 必须；O8–O10 只有在 §8.0 某项未达标时启用，并附前后对比。
**不做**：通用压缩（zstd / Brotli）作为默认——它会增加 CPU 与依赖，先证明"编码优化不够"再谈；
Bloom filter 也先不做，除非实测到明显的空查放大。

#### 3.3.0.12 代价、风险与边界

- 实现量约 **8–10 人周**：格式与读写 3–4 周、查询迭代器 2 周、合并与恢复 1–2 周、工具与模糊测试 2 周；
- **必须**做差分测试：同一语料、同一查询集，与朴素内存参考实现逐条比对；体积 / 耗时与 S0 基线对拍；
- **模糊测试**：随机字节喂 `verify` / 打开 / 查询，只能"报错或降级"，不能崩溃或越界读；
- **崩溃注入**：在"写段后 / 写 Manifest 后 / 写槽前"分别 kill，验证只会回到旧 `gen`；
- .NET GC：解码循环用 `Span<byte>` / `ArrayPool`，迭代器 `ref struct`，避免装箱与 LINQ；
- 热点确认后再考虑把解码下沉到 Zig 内核（**格式与 ABI 不变**，只是执行位置变化）；
- 没有 SQL 诊断：`era-index dump / verify / stats / facets / health` 必须覆盖 SQLite 时代能查的东西；
- 风险被"索引是派生数据"限制：损坏最多是重建，不会丢用户数据；
- **不做**通用数据库：不支持事务 SQL、不支持任意 schema、不支持二级索引；
- **所谓"查表翻译"的成本被压到 O(log n)**：一次词典二分 → 一段倒排顺序解码，
  没有 SQL 解析、没有通用查询计划器、没有 b-tree 页跳转。

#### 3.3.0.13 与"逻辑收进 .NET"的关系

EraStore 是存储层，方案 / 计划 / 排序仍在 C#；Dart 依旧只发请求、只渲染。
自研存储让能力变成**显式数据**：`capabilities` 由段头与词典实际内容推导，
不再依赖 FTS5 的隐式行为（`detail` / `contentless` / 列过滤语法）。

#### 3.3.0.14 编码示例（3 首小曲，逐字节）

语料（只取 `title` 字段，`field_id=0x01`）：

| doc | title | 生成的 title token（bigram + 首尾 unigram） |
|---|---|---|
| 0 | 晴天 | `晴天`(pos0)、`晴`(pos1)、`天`(pos2) |
| 1 | 晴天娃娃 | `晴天`(0)、`天娃`(1)、`娃娃`(2)、`晴`(3)、`娃`(4) |
| 2 | 雨天 | `雨天`(0)、`雨`(1)、`天`(2) |

**词典（排序后 + 前编码）**，键 = `01 + UTF-8`：

| # | 键（hex） | shared | suffix_len | suffix（hex） | df | 说明 |
|---|---|---|---|---|---|---|
| 1 | `01 E5 A4 A9` | 0 | 4 | `01 E5 A4 A9` | 2 | 天（doc0/2） |
| 2 | `01 E5 A4 A9 E5 A8 83` | 4 | 3 | `E5 A8 83` | 1 | 天娃 |
| 3 | `01 E5 A8 83` | 2 | 2 | `A8 83` | 1 | 娃 |
| 4 | `01 E5 A8 83 E5 A8 83` | 4 | 3 | `E5 A8 83` | 1 | 娃娃 |
| 5 | `01 E6 99 B4` | 1 | 3 | `E6 99 B4` | 1 | 晴 |
| 6 | `01 E6 99 B4 E5 A4 A9` | 4 | 3 | `E5 A4 A9` | 2 | 晴天（doc0/1） |
| 7 | `01 E9 9B A8` | 1 | 3 | `E9 9B A8` | 1 | 雨 |
| 8 | `01 E9 9B A8 E5 A4 A9` | 4 | 3 | `E5 A4 A9` | 1 | 雨天 |

单条词典条目 = `shared(1) + suffix_len(1) + suffix + df varint + post_off(4) + flags(1)`。
第 1 条占 `2+4+1+4+1 = 12B`，8 条合计约 88B（块头另计）。
块索引只需 1 条：`prefix8=01 E5 A4 A9 00 00 00 00`、`block_off`、`block_len`。

**倒排（以 `晴天` 为例，doc0 / doc1）**：

```text
块头 14B: doc_count=2, last_doc=1, max_tf=1, flags=0x01(has_pos), crc32c, tf_exc=0
tf 位图 16B: 03 00 00 ... 00          # bit0=doc0 tf=1, bit1=doc1 tf=1
doc 序列:   00 01                    # 首 doc 相对 0；第二个 delta=1
tf 例外:    （空）
pos_off:    4B → 指向下面的位置块
```

**位置块（`晴天`）**：

```text
块头 12B: doc_count=2, last_doc=1, flags, crc32c
doc0: 00        # delta = 0
      01        # pos_count = 1
      00        # position = 0
doc1: 01        # delta = 1
      01
      00
```

**tf > 1 的例子**：若 doc0 的 `晴天` 出现两次，tf 位图的 bit0 置 0，
例外表写 `doc_index=0, tf=2`；其余 doc 仍是位图命中。解码时"先查位图，再查例外表"。

**解码顺序**（`TermIter.Advance`）：

1. 块索引二分 → 词典块 → 块内线性扫 → 得到 `post_off / df / flags`；
2. 读倒排块头 → 若 `last_doc < target` 直接跳到下一块；
3. 块内解 doc delta，位图判 tf，必要时查例外表；
4. 需要短语时再按 `pos_off` 解码位置块。

> 这个例子故意很小，但覆盖了 v1 的全部编码路径：前编码、df、tf 位图、位置块、跳块。

#### 3.3.0.15 合并代价模型（50k / 48.7k / 200k）

符号：`N` 文档数，`S=16k` 目标段大小，`M=4` 层扇出，
`L = max(0, ceil(log_M(N/S)))` 层数，`A = 1 + L` 单 doc 生命周期重写次数（含首次写入）。

| N | 段数（S=16k） | L | A（重写次数） | 全生命周期写字节 ≈ `A × 索引体积` |
|---|---|---|---|---|
| 3k | 1 | 0 | 1 | 1.0× |
| 30k | 2 | 1 | 2 | 2.0× |
| 48.7k | 4 | 1 | 2 | 2.0× |
| 200k | 13 | 2 | 3 | 3.0× |

- 50k 曲、索引 250MB → 全生命周期写约 **500MB**；一次性构建 250MB；
- 死行触发的额外合并按 `d/(1-d)` 计：`d=10%` → +11%，可接受；
- **合并不重新分析文本**：只归并 postings / positions / DocStore，CPU 花在解码与编码，
  不花在分词 / 拼音；所以合并速度与"文本多复杂"无关，只与字节数有关；
- **2s 预算**：合并吞吐按 80–150 MB/s 估，单次输入上限取 `2×S`（约 160MB），
  超预算就选更小的段组合，不把一个巨型段硬塞进一次合并；
- 合并输出仍是不可变段；提交后才切 Manifest，旧段字节成为垃圾，待全量重建回收。

#### 3.3.0.16 引擎级基准与对拍（不进 CI）

`bench/erastore/` 只测存储层，不掺查询语义，避免"排序变了"污染"存储快了"：

| 子命令 | 测什么 | 输出 |
|---|---|---|
| `build` | 50k 合成语料写入：耗时 / 峰值 RSS / 各段体积 | `build.json` |
| `open` | 冷 / 热进程打开：`open_ms` | `open.json` |
| `query` | term / prefix / phrase / typo 四类固定查询：P50 / P95 / QPS | `query.json` |
| `merge` | N 段合并：耗时 / 写放大 / 合并后死行比例 | `merge.json` |
| `verify` | 全量 CRC + 结构校验 | `verify.json` |

- **对拍**：`NaiveIndex`（`Dictionary<term, List<(doc,pos)>>`）作为参考实现，
  同一语料 + 10k 随机查询，比较 **top-20 顺序**，不是只比集合；
- **模糊**：随机截断 / 翻转字节后打开与查询，只允许"报错或降级"，不允许崩溃 / 越界；
- **崩溃注入**：在"写段后 / 写 Manifest 后 / 写槽前"三个点 kill，重开只能看到旧 `gen` 或新 `gen`；
- **优化开关**：O1–O10 每项一个构建 flag；**任何 flag 组合下，查询结果必须逐条一致**，
  否则该优化不予采用；
- 运行规则沿用仓库约定：默认 `PROFILE=quick`，`PROFILE=full` 需 `ALLOW_LONG_BENCH=1`，不上 CI。

输出统一为一个 JSON 骨架，便于逐版本对比：

```jsonc
{ "commit": "…", "corpus": { "docs": 50000, "bytesPerDoc": 5120 },
  "bytes": { "total": 240000000, "dict": 35000000, "postings": 120000000,
             "positions": 78000000, "docstore": 8000000, "misc": 3000000 },
  "build": { "seconds": 95, "rssPeakMB": 110 },
  "open":  { "coldMs": 8.2, "warmMs": 2.1 },
  "query": { "p50Ms": 2.1, "p95Ms": 6.4, "qps": 420 },
  "merge": { "seconds": 1.8, "writeAmplification": 2.0, "deadRatioAfter": 0.03 } }
```

#### 3.3.1 逻辑模型与字段族（存储无关）

存储引擎已定为 **EraStore**（ADR-1）；这里只定义"要写什么"，不定义"用什么表"。
`library.db` 仍由 scanner 持有、查询期只读，但**搜索索引不依赖 SQLite**。

**Doc 记录（DocStore，列式块存储）**：

| 字段 | 类型 | 用途 |
|---|---|---|
| `doc_id` | u32 | 写入时分配；查询结果按 `track_id` 去重后取最新版本 |
| `track_id` | 16B（MD5） | 跨模块唯一键；UI 只见它 |
| `rev` | 16B | 内容指纹（§3.8.1） |
| `title_norm` / `artist_norm` / `album_norm` | UTF-8 | 精确度复核 / 高亮 / explain |
| `script_bits` | u16 | 库画像与降级判断 |
| `doc_len[field]` | u32 × N | bm25 与覆盖率 |
| `flags` | u8 | 是否有译文 / 音译 / 侧车 |

**字段族（`field_id`，同一棵词典内的命名空间）**：

| field_id | 内容 | 位置 | 说明 |
|---|---|---|---|
| `0x01–0x03` | title / artist / album 字面 | ✅ | bigram + 首尾 unigram |
| `0x04` | filename 字面 | ✅ | 日文文件名读音另计 |
| `0x05` | lyrics 原文 | ✅ | 行级去重后写 |
| `0x06` | lyrics 译文 | ✅ | 无译文层则不写 |
| `0x07` | lyrics 音译 | ✅ | 仅显式音译层 |
| `0x11–0x13` | title / artist / album 拼音 | ✅ | 段内逐字对齐 |
| `0x14–0x16` | title / artist / album 首字母 | ✅ | 只出 bigram |
| `0x17` | 日 / 韩读音 | ✅ | 仅库画像命中时写 |
| `0x21` | lyrics 拼音 | ✅（默认） | **保留短语能力**；"不存位置"是非默认优化（V2） |
| `0x22` | lyrics 首字母 | ✅ | 默认不写，设置项可开 |

> 位置（positions）默认**全字段都写**，这是行为等价的前提；EraStore 用 Δvarint 块压缩位置，
> 不是 SQLite 那种逐条存。只有 §8.1 的 V2（歌词拼音免位置）会在用户确认后关闭 `0x21` 的位置。

**版本锚（写进 Manifest，一票否决）**：

| 锚 | 初始值 | 说明 |
|---|---|---|
| `index_format` | **1**（EraStore） | 物理格式版本；与 v7 的 8 无关 |
| `normalizer_version` | 1 | 归一化管道 |
| `tokenizer_version` | 2 | bigram + 首字母 bigram；去重 / 截断规则变化也必须递增 |
| `scheme_version` | 1 | 查询 / 排序方案默认策略 |
| `data_pack_id` | manifest 哈希 | 数据表版本 |
| `options_bits` | bit 集合 | 见 §3.4.4 |
| `capabilities` | 能力集合 | 由段头与词典实际内容推导 |

**为什么 EraStore 能消解现状问题**：

| 现状问题 | EraStore 如何消解 |
|---|---|
| 拼音列让主路径慢 6.5–9.7× | 字面查询只读 `0x01–0x07` 的词典区间，完全不碰 `0x11–0x22` |
| 派生列体积 139 MiB | 读音是同一棵词典的独立命名空间；不查询就不解码，体积由压缩率决定 |
| 无法判断"命中字段 / 精确度" | DocStore 有归一原文，候选 ≤ 数千时可**精确复核**（equal / startsWith / contains / 覆盖度） |
| 首字母只能整段匹配（`xzq` 命中，`xz` 不命中） | 首字母改 **bigram + 位置**，部分首字母可命中且不会退回"单字母整库" |
| 每加维度就重建 | 维度是 `field_id` 命名空间；`data_pack_id` 变化才全量重建，方案变化不重建 |
| 统计要额外表（`hot_terms` / `vocab_meta`） | 词典条目自带 `df`；纠错扫词典前缀区间——**两张表都不存在** |

> **不做**：不把歌词原文塞进 DocStore（体积与"片段从 `library.db` 取"的初衷冲突）。
> 歌词的精确度复核只在"歌词档"做近似（命中段长度 / 覆盖度），因为歌词本来就是低置信信号。

#### 3.3.2 Token 生成规格（按脚本 × 字段）

| 脚本 | 字面 token | 读音 token | 首字母 token | 备注 |
|---|---|---|---|---|
| Han（连续段长 L） | `L-1` 个 bigram + 首字 + 末字（`L=1` 时 1 个 unigram） | 每字 1 音节（段级完整性前提） | `L=1`→1 字符；`L≥2`→`L-1` 个 bigram | 见下方"两种读音来源" |
| Kana | 同 Han | 罗马字（规范形）bigram | 无 | 只进读音列，不进拼音/首字母 |
| Hangul | 同 Han | 国语罗马字 bigram | 无 | 同上 |
| Latin / Digit | 整词小写（不做 n-gram） | 无 | 无 | 只进字面；查询侧的拉丁词会去匹配拼音/首字母列 |
| 其他（符号等） | 不产 token | — | — | 分隔符 |

> **两种读音来源互斥**：Han 段的读音视图要么是中文拼音（写 `pinyin_meta_tok`），
> 要么是日文音训读（写 `reading_meta_tok`，仅当整首被判定为日文），不重复写两列。
> 这与现状 `Analyzer.Analyze(..., includeReadings)` 的语义一致，重写不要改成"两列都写"。

字段级规则：

| 字段 | literal 列 | phon 列 | 说明 |
|---|---|---|---|
| title / artist / album | ✅ | ✅（拼音 + 首字母；日文时读音） | 元数据是最高置信信号 |
| filename | ✅ | 仅日文读音（可选） | 不产拼音 / 首字母（审计 D-4 的结论；日文文件名读音沿用现有行为） |
| lyrics（原文） | ✅ | ✅ 拼音默认写；**首字母默认关** | 单字查询仍不查歌词列 |
| lyrics_tr / lyrics_rom | ✅ | ❌ | 层本身已是派生 |
| 全部字段 | 段间插入 `seg_sep` 哨兵 | 同左 | 防止跨段短语假命中（见 3.3.3） |

> 首字母**只出 bigram、不出 unigram**，是"支持部分首字母"与"禁止单字母整库"两条约束的交点：
> `xzq → "xz zq"`，查询 `xz`（term）与 `xzq`（短语）都能命中，查询 `x` 不生成首字母步。

#### 3.3.3 写入不变量（段式）

一次写入 = 一个内存段批次；提交顺序见 §3.3.0.8。文档 / 词典 / 倒排 / 统计的一致性
由**同一次提交**保证，不靠"多张表同事务"：

1. **旧版本先 tombstone**：不原地改倒排；更新写新 `doc_id`，查询按 `track_id` 取最新非 tombstone 版本。
2. **词典与倒排同批**：某个 term 的 `df` 与 `postings_offset` 必须与倒排块一起写；
   不允许"先写倒排后补 df"。
3. **段哨兵 `seg_sep`**：每个字段 / 每段之间写入哨兵 token（建议字面量 `zzsepzz`）：
   - 阻断跨段短语（`title` 末字 + `artist` 首字被当成相邻）；
   - 代价：每段 1 个 token（30k 曲 × 平均 40 段 ≈ 120 万，可忽略）；
   - `seg_sep` 从 df 统计、纠错候选、模糊词表中排除，查询侧永不生成该词。
4. **空串不产词条**：空字段不写 token，词典里不出现空 term（与 NULL 无关，因为根本没有表）。
5. **`rev` 与内容同一批提交**：不允许"先写内容后写 rev"。
6. **`gen` 每批 +1**：一次提交推进一次 `Manifest.gen`；一批默认 128 曲。

读取不变量：查询只读 `(gen, manifest)` 快照；**绝不**在读路径合并段、回收 tombstone、改写文件。

#### 3.3.4 v7 列 → EraStore 字段族（仅供理解；不实现旧索引读取）

| v7 列 | EraStore 位置 | 语义说明 |
|---|---|---|
| `title_tok` / `artist_tok` / `album_tok` / `filename_tok` | `field 0x01–0x04` | ✅ 完全一致 |
| `lyrics_tok` / `lyrics_tr_tok` / `lyrics_rom_tok` | `field 0x05–0x07` | ✅ 完全一致（行级去重后） |
| `pinyin_meta_tok` / `initials_meta_tok` / `reading_meta_tok` | `field 0x11–0x17` | ⚠ 首字母改 bigram（`tokenizer_version+1`） |
| `pinyin_lyrics_tok` | `field 0x21`（默认写） | ✅ 保留现有召回语义；设置项可关 |
| `initials_lyrics_tok` | `field 0x22`（默认关） | ⚠ 默认不写；设置项可开 |
| `search_docs.reading_tok` | 删除 | DocStore 原文可复核，不需要副本 |
| `search_docs.indexed_mtime` | `DocStore.rev` | ⚠ 语义从"时间"变"指纹" |
| `search_meta.has_*` | `DocStore.script_bits` + `Stats` | 位域替代四个键 |

### 3.4 第 4 层：方案层（Scheme）与算法注册表

> 这一层是本次重构与"打补丁"最本质的区别：**不再用布尔开关组合算法，而是用可命名的方案组合实现**。
>
> **降级 ≠ 回退**：降级是同一代代码内换成更弱的方案（`default@1` → `fast@1`），
> 目的是「能力不足时仍可用」；本项目**不提供任何回到旧实现、旧符号、旧索引的路径**。

#### 3.4.1 三个概念：角色 / 实现 / 方案

| 概念 | 含义 | 例子 |
|---|---|---|
| **角色（Role）** | 一类可替换的算法职责 | `Tokenizer` / `ReadingProvider` / `Matcher` / `Ranker` / `PostProcessor` / `IndexStore` |
| **实现（Implementation）** | 某个角色的一个具体算法，自带 id 与能力声明 | `bigram@1`、`trigram@1`、`pinyin@1`、`ja-unihan@1`、`exact@1`、`prefix@1`、`phonetic@1`、`typo-damerau@1`、`bucket@1` |
| **方案（Scheme）** | 一次检索的完整组合：用哪些实现、按什么顺序、什么预算、需要哪些能力、降级到谁 | `fast@1`、`default@1`、`recall@1`、`minimal@1` |

关系：

```
Scheme = {
  id, version,
  requires:  [capability...],          // 对索引的要求
  matchers:  [matcherId...],           // 有序；决定步骤集合
  ranker:    rankerId,
  post:      [postProcessorId...],     // 高亮 / 片段 / 去重
  policy:    { minMatch, caps, thresholds... },
  degradeTo: schemeId | null
}
```

#### 3.4.2 接口（C#，名字可调整，职责固定）

```csharp
interface ITokenizer        { string Id { get; } Capabilities Produces { get; }
                              AnalysisResult Analyze(string text, FieldKind field); }
interface IReadingProvider  { string Id { get; } Capabilities Produces { get; }
                              bool TryReading(string run, out string reading); }
interface IMatcher          { string Id { get; } MatchKind Kind { get; }
                              bool Supports(QueryTerm term, Capabilities caps);
                              Step? Compile(QueryTerm term, CompileContext ctx); }
interface IRanker           { string Id { get; } int Compare(Candidate a, Candidate b); }
interface IPostProcessor    { string Id { get; } void Apply(Candidate c, PostContext ctx); }
interface IIndexStore       { /* Open/Put/Delete/Query/Merge；唯一实现 = EraStore */ }
interface ISchemeRegistry   { SearchScheme Resolve(string id, Capabilities caps); }
```

三条设计约束：

1. **角色之间不互相 `new`**：全部由注册表注入，便于替换与单测；
2. **实现声明能力，不声明"我比谁好"**：好坏由方案与指标决定，避免实现之间互相引用；
3. **`Compile` 是纯函数**：只吃 `QueryTerm + CompileContext`（其中含预算 / df 快照），不出 IO、不查库——
   这样"某个算法会生成什么步骤"可以完全离线测试。

#### 3.4.3 内置方案（初版四个，可并存）

| scheme | matchers（有序） | ranker | 典型用途 | 降级到 |
|---|---|---|---|---|
| `fast@1` | `exact` → `prefix` | `bucket@1` | 大库 / 低延迟；不做读音与容错 | `default@1` |
| `default@1` | `exact` → `prefix` → `drop` → `phonetic-meta` → `reading` → `cross-lingual` → `typo1` | `bucket@1` | 日常搜索（对齐现状能力） | `fast@1` |
| `recall@1` | `default@1` 的 matchers + `phonetic-lyrics` + `typo2`（**组合，不是继承**；`typo2` 与 `typo1` 是同一角色的两个实现） | `bucket@1` | "深度搜索 / 我确定库里有" | `default@1` |
| `minimal@1` | `exact`（按 scope 取列；有歌词能力时也含歌词） | `bucket@1` | 降级链终点 / 索引能力残缺 | — |

同一个算法可以服务多个方案（`exact` 同时出现在四个方案里），
同一个方案也可以换掉某个算法而不影响其他方案——这就是"多算法适配多方案"。

同一套引擎还能服务不同**入口**，入口只是 `scheme + scope + 字段族` 的组合：

| 入口 | scheme | scope | 说明 |
|---|---|---|---|
| 曲库全局搜索 | `default@1` | `all` | 当前搜索框 |
| 曲库元数据快搜 | `fast@1` | `metadata` | 大库 / 边输边搜 |
| 播放列表内搜索 | `default@1` | `metadata` + 列表 id 过滤 | 过滤条件由调用方给，语义不变 |
| 歌词搜索 | 推荐 `recall@1` | `lyrics`（**S1 起提供**） | 只查歌词族；`fast@1` / `default@1` 也能用，只是少了读音与容错 |
| 深度找回 | `recall@1` | `all` | 用户显式触发（"搜不到？试试深度搜索"） |

#### 3.4.4 能力协商（启动期一次，不在查询期试错）

```text
Resolve(requestedId, indexCaps):
    scheme = registry.Get(requestedId) ?? registry.Default
    while scheme != null:
        missing = scheme.Requires - indexCaps
        if missing is empty: return (scheme, missing: [])
        log.info("scheme {} 缺少能力 {}，降级到 {}", scheme.Id, missing, scheme.DegradeTo)
        scheme = scheme.DegradeTo
    return (registry.Get("minimal@1"), missing: ["all"])
```

能力清单（索引在构建收尾写入 `Manifest.capabilities`，方案只读不写）：

| capability | 含义 | 写入条件 |
|---|---|---|
| `surface_text` | DocStore 有归一后的标题 / 歌手 / 专辑 | EraStore 固有 |
| `literal_lyrics` | `field 0x05–0x07` 有词条 | 索引歌词开启（默认关） |
| `phonetic_meta` | `field 0x11–0x17` 有词条 | `pinyin=1` |
| `phonetic_lyrics_pinyin` | `field 0x21` 有词条 | `lyrics_pinyin=1`（默认开） |
| `phonetic_lyrics_initials` | `field 0x22` 有词条 | `lyrics_initials=1`（默认关） |
| `readings` | `field 0x17` 有词条 | 库画像含日 / 韩且生成成功 |
| `initials_bigram` | 首字母按 bigram 写入 | `tokenizer_version ≥ 2` |
| `lyrics_layers` | 至少存在一个译文 / 音译层 | 该曲有对应层 |
| `df_snapshot` | 词典条目含 `df` | EraStore 固有 |
| `typo_vocab` | 词典前缀区间可扫（含 ASCII 词） | EraStore 固有 |
| `highlight_offsets` | 支持 UTF-16 偏移高亮 | v2 固有 |
| `cross_lingual` | 有策展双语表（S4 起） | 数据包含 `lexicon_cross` |

`Manifest.options_bits`（影响索引内容，进 `rev` 与重建闸门）：

| bit | 开关 | 默认 |
|---|---|---|
| 0 | `pinyin`（元数据读音） | 1 |
| 1 | `lyrics_pinyin` | 1 |
| 2 | `lyrics_initials` | 0 |
| 3 | `lrc`（侧车歌词） | 0 |
| 4 | `romaji_lyrics` | 0 |

> 新增 capability 或 bit 必须同时更新：Manifest 写入、方案 `requires`、健康报告、
> golden vectors。漏一处就会出现"方案以为有、索引其实没有"的静默降级。

- 请求可以显式指定 `scheme`；不指定则用默认方案。
- 实际使用的方案与缺失能力要出现在 `explain` 与健康报告里，否则"为什么没搜到拼音"无从排查。
- **降级是静默可用的**：用户永远能拿到结果，只是档位低一档；日志里留证据。
- **兜底方案 `minimal@1`** 只依赖 `surface_text`，是降级链的终点；注册表启动时做
  **环检测**（`a.degradeTo=b` 且 `b.degradeTo=a` 直接拒绝启动），避免运行期无限循环。
- **scope 不参与方案选择**：scope 只负责裁剪列（搜哪里），所有 scheme 都支持三种 scope；
  能力不足（例如缺 `phonetic_lyrics`）仍按 `requires` 降级。

#### 3.4.5 稳定性规则（防止方案层自己变成补丁堆）

| 规则 | 说明 |
|---|---|
| **已发布 scheme 不可变** | `default@1` 的行为一旦有向量，就只能新增 `default@2`；不允许"悄悄调个权重" |
| **删除实现前先退役方案** | 注册表启动时做引用完整性检查：被任何 scheme 引用的实现不得删除 |
| **向量按方案分组** | 每个 scheme 有自己的 golden vectors；跨方案比较是另一套（回放 diff） |
| **新增算法不改旧向量** | 新算法只能通过新 scheme（或旧 scheme 升版本）生效 |
| **性能预算按方案设** | `fast@1` 的 P95 预算比 `recall@1` 严；不允许用一个全局预算评价所有方案 |
| **禁用开关在注册表层** | `disabledByDefault` 只影响"新请求默认选谁"，不影响已固定 scheme 的可用性 |

#### 3.4.6 为什么不用"布尔开关 + 条件编译"

现状 20 多个 `const` 实际构成 2ⁿ 种未测试组合，谁也说不清"关掉 A 打开 B"是什么语义。
方案层把组合**枚举化**：每个方案是一个有名字、有向量、有预算、有降级目标的整体，
评审时看的是"这个方案做什么"，而不是"这 20 个开关现在是什么值"。

### 3.5 第 5 层：查询计划（替代"六趟 + 短路"）

**先分类，再编译，最后定预算**——前两步是纯函数，预算阶段只允许点查（不允许扫描）：

```csharp
QueryPlan Plan(QueryTokens tokens, SearchScheme scheme, IndexStats stats)
{
    // 1) 分类：每个 term 的脚本 / 是否可读音化 / 是否可容错
    // 2) 生成步骤：由 scheme.Matchers 依次 Compile，显式、有序、有限
    // 3) 预算：按 stats 的 df 估计，给每步候选上限；超预算的步骤直接不生成
}
```

步骤声明（顺序即优先级，**不依赖运行期命中数**）：

| # | step | 何时生成 | 候选预算 | 备注 |
|---|---|---|---|---|
| 1 | `literal-exact` | 总有 | 2k | `n≤2` 时全 term 必须命中；`n≥3` 按 `minMatch` |
| 2 | `literal-prefix` | 末 term 可前缀（拉丁/拼音 ≥3，CJK 走 `字*`） | 2k | 与 #1 同一次执行，但 kind 降一档 |
| 3 | `literal-drop` | term 数 ≥3 | 1k/轮，≤2 轮 | **先减词**，按 info + 字段亲和度决定丢哪个 |
| 4 | `phonetic-exact` | 有可读音 term（单 CJK 字不生成） | 1k | 与 #1–#3 分开执行，单独产 evidence |
| 5 | `reading` | 库画像含日/韩 且 query 是拉丁 | 500 | 读音 bigram 短语 |
| 6 | `cross-lingual` | 有高置信候选（策展表，**非** Unihan 全量释义） | 300 | 最低档；单字禁止 |
| 7 | `typo` | 拉丁 term 长度 ≥4，且该 term 未超过热词阈值（附录 B） | 300 | 词长阶梯：≥4→1 错，≥7→2 错 |

三条硬规则（这是与现状最大的差别）：

1. **放宽方向只允许"减约束"，不允许"换维度"**：先丢词（仍是字面），再读音，最后容错。
   （沿用 `local-search-indexer-references.md` §6.2 的结论，但把它变成**计划**而不是 if 链。）
2. **最少命中数用分段曲线，不用 `MinExact` 标量**：
   `n=1 → 1`；`n=2 → 2`（禁止退化成单字）；`n≥3 → ceil(2n/3)`。
   曲线是 `SearchPolicy` 的字段，版控后可调。
3. **预算由统计决定**：`stats.df(term)/N` 超过阈值的 term 不生成前缀 / 读音 / 容错步骤
   （等价于 Lucene `maxQueryFrequency`，但用**累计候选估计**而不是单 term 频率）。

> 现状的 `MinExact` 是"结果数量开关"，新计划的 `budget` 是"候选规模开关"。
> 后者不改变语义（只是不生成注定爆量的步骤），可以放心地在不同规模的库上使用。

**执行层面的短路仍然保留，但语义不同**：执行器按预算顺序跑步骤，只有"更高 kind 的候选已经
填满 top-k"才跳过后续步骤（§3.5.5.2 的 `StepDominated`）——这是**省钱**，不是**定语义**。因为所有步骤产出的 evidence
都进同一个桶序，被跳过的步骤最多只能产出更低桶的候选，跳不跳都不改变已产出的 top-k 顺序
（S1 必须用回放证明这一点，否则说明桶序被跨桶污染了）。

#### 3.5.1 数据结构（单一实现，字段名即契约）

```csharp
enum MatchKind  { Exact, Phrase, Prefix, Partial,
                  PinyinExact, PinyinPhrase, Initials, Reading, CrossLingual, Typo }
enum FieldKind  { Title, Artist, Album, Filename, Lyrics, LyricsTr, LyricsRom }

sealed record QueryTerm(string Surface, Script Script, bool ReadingEligible, bool TypoEligible);

sealed record Step(
    MatchKind Kind,
    FieldFamily FieldFamily,      // 一个 Step 只覆盖一个字段族，field 归因由此确定
    FieldId   Field,              // 0x01–0x07 字面 / 0x11–0x22 读音（见 §3.3.1）
    MatchOp   Op,                 // Term | Prefix | Phrase | And | Or
    IReadOnlyList<string> Terms,  // 已归一化的词元（不是 SQL 片段）
    int       CoveredTerms,       // 本步覆盖的查询 term 数（变体展开仍算 1）
    int       Cap,                // 本步物理上限
    double    EstimatedCandidates // 计划期估计，用于决定"生不生成"
);

sealed record QueryPlan(
    IReadOnlyList<QueryTerm> Terms,
    IReadOnlyList<Step>      Steps,
    int                      MinMatch,
    string                   SchemeId,      // 例如 "default@1"
    IReadOnlyList<string>    MissingCaps); // 能力协商结果（空 = 完整方案）

sealed record Evidence(MatchKind Kind, FieldKind Field,
                       IReadOnlyList<string> MatchedTerms, double Coverage, bool Positioned);

readonly record struct DocRef(int Segment, int Local);
sealed record Candidate(DocRef Doc, string? TrackId, Evidence Evidence, double Bm25);
```

**为什么 `Step` 带 `FieldFamily`**：现状"三支平铺 OR"最大的损失是**不知道命中了哪个字段**，
只能靠 bm25 权重猜。把字段族放进 Step，等于在查询构造时就拿到了归因的一半
（`kind` 来自步骤、`field` 来自字段族），剩下一半（精确度 / 覆盖度）由 3.6.3 的复核补齐。

#### 3.5.2 步骤编译模板（EraStore 迭代器）

定义：`meta_fields = 0x01–0x04`（title / artist / album / filename 字面）。

| Step | 生成条件 | 迭代器示例 | 必须注意 |
|---|---|---|---|
| `literal-exact`（CJK 词） | term 是 Han/Kana/Hangul 段 | `TermIter(meta_fields, "晴天")` | 单字必须走 `PrefixIter`（前缀），否则中间字查不到 |
| `literal-exact`（多词） | term 数 ≥2 | `AndIter(TermIter(meta_fields, t) for t in terms)` | 每个词各自建迭代器，禁止拼字符串 |
| `literal-exact`（拉丁/数字） | Latin/Digit term | `TermIter(meta_fields, "love")` | 长度 <3 不加前缀，避免 2 字符前缀扫全库 |
| `literal-prefix` | 仅末 term 且长度 ≥3 | `PrefixIter(meta_fields, "lov")` | 前缀只作用于末词 |
| `literal-exact`（歌词族） | `scope=all` 或 `scope=lyrics` | `TermIter(0x05–0x07, t)` | 单字查询不进这一族 |
| `phonetic-exact`（拼音 / 元数据） | 有可读音 term | `PhraseIter(0x11–0x13, ["wo","ai","ni"])` | 多音节用短语（相邻），单音节 ≥3 才前缀 |
| `phonetic-exact`（拼音 / 歌词） | `scope=all` 或 `lyrics`，歌词拼音开启（默认开） | `PhraseIter(0x21, ["wo","ai","ni"])`；若 `0x21` 未存位置 → 自动降级 `AndIter` 并在 `explain` 标注 | 歌词首字母默认不生成步骤 |
| `phonetic-exact`（首字母） | CJK 段长度 ≥2 或拉丁输入 | `PhraseIter(0x14–0x16, ["xz","zq"])` | 单字母不生成；`xz` 用 Term、`xzq` 用 Phrase |
| `reading` | 库画像含日/韩且 term 是拉丁 | `PhraseIter(0x17, romaji_bigrams)` | 必须与索引侧**同一个** bigram 函数 |
| `cross-lingual` | 有策展候选 | `OrIter(TermIter(meta_fields, v) for v in variants)` | 最低桶；单字禁止 |
| `typo` | 拉丁 term 长度 ≥4 | 词典前缀桶内变体 → `OrIter(TermIter(...))` | 首字符替换不生成；变体 ≤32 |

> **不拼查询字符串**：`Step` 里存的是词元与操作符，执行器据此构造迭代器。
> 这样"查询语法"不再是攻击面，也不再需要转义规则。

EraStore 的六条执行不变量（写进实现注释与测试）：

1. **区间必须带 `field_id`**：`PrefixIter` 的上下界都包含同一个 `field_id`，禁止跨字段扫描；
2. **前缀上界**用 `prefix || 0xFF...`（UTF-8 字节序），并确认不越过 `field_id` 边界；
3. **位置只对存了位置的字段可用**：`PhraseIter` 遇到无位置字段自动降级为 `AndIter`，
   且必须在 `explain` 里标 `positioned=false`，不许静默改变语义；
4. **块跳过依赖 `last_doc_id` 单调**：块内 doc_id 必须严格递增，否则 `Advance` 会漏；
5. **`seg_sep` 不参与** df 统计、纠错候选、前缀展开；查询侧永不生成；
6. **同一 `(gen, scheme)` 下迭代顺序确定**：top-k 才可复现，分页才不漂移。

#### 3.5.2.1 `scope` 语义（S1 起就有三种，不是后续增强）

`scope` 是**请求约束**（搜哪里），`scheme` 是**检索策略**（怎么搜），两者正交：

| scope | 字段族（字面） | 字段族（读音） | 单字查询 | 说明 |
|---|---|---|---|---|
| `all` | `0x01–0x07` | `0x11–0x17` + `0x21`（默认开） | 只查元数据 | 默认范围 |
| `metadata` | `0x01–0x04` | `0x11–0x17` | 允许 | 快搜 / 播放列表内搜索 |
| `lyrics` | `0x05–0x07` | `0x21` / `0x22` | **禁止，返回空** | 单字在歌词里没有检索意图 |

实现约束：

- 生成步骤时按 scope 裁剪列集合，**不是查询后过滤**（后者会把 `total` 算错）；
- `scope=lyrics` 下 `field` 只能是 `lyrics / lyricsTr / lyricsRom`，
  `kind` 仍按 3.6.1 判定；
- 跨语言候选只在其候选能落进当前 scope 的列时才生成步骤；
- 所有 scheme 都支持三种 `scope`；scope 只裁剪列，不参与方案选择（能力不足仍按 §3.4.4 降级）；
- 缓存键、指标、回放 JSONL 都必须带 `scope`（已有）。

#### 3.5.3 预算估计与 df 快照

EraStore 的**词典条目自带 `df`**，所以不再需要"热词表"这种折中：
预算阶段直接对每个词做一次词典二分（`df` 就在条目里），成本 O(log V)，不做倒排解码。

```text
Stats 段（构建收尾写一次，查询只读）：
  n_docs                 = 50000
  doc_count[field]       = ...
  total_len[field]       = ...
  reading_complete_ratio = 0.87
  pending_writes         = 12
  pending_deletes        = 3
  last_merge_at          = 1767225600
```

df 快照按查询批量取：一次遍历所有 term，各自 `lower_bound(field_id || term)`；≤ 查询词数 × 3 次。

估计公式（计划期，零倒排解码）：

```
est        = N * Π(df(term_i) / N)   # 独立假设
est_prefix = Σ df(区间内的词)         # 前缀区间直接求和，比系数估计准
est_typo   = est * variants(term)     # 变体数
```

- `N=0`（空库）→ 直接返回空，不进入估计与查询；
- 任一步 `est > scheme.Policy.StepCandidateFactor × Cap`（默认 4×）→ **不生成该步**；
- 若删掉该步会导致"只有一个字面步"（例如单字查询），则不删步、改为**压缩 Cap**；
- 词典查不到该词 → `df = 0`，该步直接不生成（比"按最坏 N 处理"更准）。

> 计划期**只读词典，不读倒排**：这是 EraStore 相对"`hot_terms` 表 + 阈值登记"的直接收益——
> 不存在"没登记就误判成罕见词"的问题，也不需要维护两张统计表。

#### 3.5.4 多词最少命中与丢词算法（替代逐词 SQL 探测）

```text
terms = 从 AnalyzeQuery 的段序列展开（不是按空白切原串）
n = terms.Count

1) n == 1: minMatch = 1；只跑 literal；拉丁词且长度 ≥3 才追加 phonetic
   （单 CJK 字不追加 phonetic / reading / cross-lingual）
2) n == 2: minMatch = 2；不生成丢词步（两字退化成单字正是 M-3）
3) n >= 3: minMatch = ceil(2n/3)

丢词候选（只生成 ≤3 个子集，不逐词循环）：
  info(t)   = log(N / max(df(t), 1))          # 越罕见越重要
  role(t)   = title命中 ? 2 : artist命中 ? 1 : 0
  score(S)  = Σ info(t) + 0.5 * Σ role(t)     # 对子集 S（|S| = minMatch）
  枚举 |S| = minMatch 的子集，取 score 最高的 3 个；同分保持原词序
  子集数上限：n ≤ 8 时穷举；n > 8 时按 info 排序取前 minMatch 个 + 两个相邻替换变体
```

- `role(t)` 用**单 term** 在各字段族的 `df`（词典直接给）；
  为控制成本，n > 4 时只对 `info` 最高的 4 个 term 探测，其余记 0。
- 每一轮丢词命中数达到 `minMatch` 即停；最多 2 轮。
- 丢词步的 `kind` 记为 `Partial`，**永远低于**任何全 term 命中的 kind。

#### 3.5.5 计划 → 迭代器：完整映射

执行器**不拼查询字符串**，只把 `Step` 翻译成迭代器树：

| `Step.Op` | 迭代器 | 说明 |
|---|---|---|
| `Term` | `TermIter(field, terms[0])` | 词典精确命中 → 倒排块 |
| `Prefix` | `PrefixIter(field, terms[0], maxExp)` | 词典区间 → 多词倒排归并；超过 `maxExp` 截断 |
| `And` | `AndIter(children)` | 每个 term 一个 `TermIter` |
| `Or` | `OrIter(children)` | 变体 / 跨语言候选 |
| `Phrase` | `PhraseIter(AndIter(children), terms)` | 先求交，再按位置验相邻 |

```csharp
interface IDocIter : IDisposable {
    int  Current { get; }             // 段内局部 doc_id
    bool Next();                      // 前进一个 doc；false = 耗尽
    bool Advance(int target);         // 前进到 >= target；false = 耗尽
    ReadOnlySpan<int> Positions();    // 当前 doc 的位置；无位置时为空
}
```

**按段执行**（顺序固定：segment_id 升序，段内 doc_id 升序）：

```csharp
IEnumerable<DocRef> RunStep(Step s) {
    foreach (var seg in manifest.Segments) {
        using var it = BuildInSegment(s, seg);
        while (it.Next())
            yield return new DocRef(seg.Id, it.Current);
    }
}

IDocIter BuildInSegment(Step s, Segment seg) => s.Op switch {
    MatchOp.Term   => new TermIter(seg, s.Field, s.Terms[0]),
    MatchOp.Prefix => new PrefixIter(seg, s.Field, s.Terms[0], policy.MaxPrefixExpansions),
    MatchOp.And    => new AndIter(s.Terms.Select(t => new TermIter(seg, s.Field, t))),
    MatchOp.Or     => new OrIter(s.Terms.Select(t => new TermIter(seg, s.Field, t))),
    MatchOp.Phrase => new PhraseIter(
                          new AndIter(s.Terms.Select(t => new TermIter(seg, s.Field, t))),
                          s.Terms),
    _ => throw new InvalidOperationException()
};
```

`Advance` 的契约（所有迭代器一致）：

```text
Advance(target):
  若 Current >= target  -> 不变，返回 true
  否则前进到第一个 >= target 的 doc；没有 -> false
  Next() ≡ Advance(Current + 1)
```

- `TermIter`：块头 `last_doc < target` 时整块跳过（不解码），否则块内解 delta 到目标；
- `AndIter`：leapfrog——取子迭代器当前最大值 `m`，把所有 `< m` 的子迭代器 `Advance(m)`，
  全部相等才产出；任一耗尽则 false；
- `OrIter`：子迭代器小顶堆，弹出最小 doc 作为 `Current`，重复 doc 先各自前进再合并；
- `PrefixIter`：词典区间内逐个 term 建 `TermIter`，用 `OrIter` 归并；展开数超过 `maxExp`
  时置 `truncated=true`（进 `explain`），不静默少召回。

生命周期与线程：

- `Positions()` 只对 `Current` 有效；`Next()` / `Advance()` 之后立即失效；
- 迭代器绑定一个段读取器，**不跨线程**；可并行遍历不同段，但共享候选表不并行写；
- `Dispose()` 只归还池化缓冲，不关闭 mmap；mmap 生命周期归索引句柄。

#### 3.5.5.1 短语与位置

```text
PhraseIter.Current = 满足“所有 term 存在且位置相邻”的 doc
Check(doc, terms, posLists):
    for start in posLists[0]:
        cur = start
        ok = true
        for i in 1 .. n-1:
            p = lower_bound(posLists[i], cur + 1)   // 第一个 > cur
            if p != cur + 1: ok = false; break
            cur = p
        if ok: return true
    return false
```

- 位置按**段内 token 序号**递增；`seg_sep` 占一个位置，所以跨段相邻差 ≥2，天然不命中；
- 任一 term 没有位置（例如启用 V2 后的歌词拼音）→ `PhraseIter` **自动降级**为 `AndIter`，
  并在 `explain` 标 `positioned=false`；不允许悄悄改变语义；
- 同一 term 重复出现（`晴天 晴天`）时，位置列表复用但起点不同，按 `start` 枚举处理；
- 短语只允许在**同一字段族**内：跨字段（title 末字 + artist 首字）不构成短语。

#### 3.5.5.2 执行循环与 kind 剪枝

```text
Execute(plan, request):
    effectiveLimit = min(request.limit, 10000)
    deadline = now + 50ms
    heap = TopK(effectiveLimit)            // 最大堆：根 = 当前最差
    merged = pool.Dictionary<DocRef, Candidate>()

    foreach step in plan.Steps (按 kindRank 升序):
        if now > deadline: partial = true; break
        if StepDominated(step, heap):
            trace.skipped.Add(step, "kind_dominated"); break   // 后续 kind 只会更差
        stepDeadline = now + 150ms
        stepEmitted = 0
        foreach doc in RunStep(step):
            if (++scanned & 4095) == 0 && now > stepDeadline:
                trace.skipped.Add(step, "budget"); break
            ev = Evidence(step.Kind, step.FieldFamily, step.Terms,
                          coverage: (double)step.CoveredTerms / plan.Terms.Count,
                          positioned: step.Op != Phrase || FieldHasPositions(step.Field))
            merged[doc] = Merge(merged[doc], ev, bm25: Score(doc))
            if (++stepEmitted) >= step.Cap: break

    Rank(merged.Values, heap, effectiveLimit)
    ResolveTrackIds(heap)                  // 过滤已不在 library.db 的 track
    return heap.Ordered.Take(limit) + cursor
```

**`StepDominated` 的精确判据**：

```text
StepDominated(step, heap):
    if heap.Count < effectiveLimit: return false
    return RankOf(heap.Worst.Kind) < RankOf(step.Kind)   // 严格小于
```

- 只用**第一键 kind**，因为 kind 严格优于时，后续 coverage / field / bm25 都不可能翻盘；
- 相等不跳：同 kind 的步骤仍可能靠 coverage / field 胜出；
- `plan.Steps` 必须按 `kindRank` 非降序生成（debug 断言），否则剪枝不成立；
- 被跳过的步骤写入 `trace.skipped{reason=kind_dominated}`，`explain` 可见。

#### 3.5.5.3 分层排序（只算边界组）

7 个键不一次性算全，否则每候选都要读 DocStore / 位置：

```text
1) 先按 (kindRank, coverageLevel, fieldRank) 分组        // 全部是内存字段
2) 组间按字典序依次输出；之前的组不需要精确度 / 邻近度
3) 只有“最后一个可能被 limit 截断的组”计算 exactness     // 批量 DocStore 读取
4) 该组内仍并列时，再算 proximity（位置，仅在需要时）    // 只对边界候选
5) 最后 bm25、docId
```

- `coverageLevel` 按 `matched/total` 量化到 1/4 档，避免浮点比较；
- 非边界组不读 DocStore、不读位置，P50 主要花在倒排解码；
- `total` 与排序解耦：排序只保证前 `limit`；`total` 另算（见 §3.6.4）。

#### 3.5.5.4 `total` 的三种模式

| 模式 | 做法 | 代价 | 结果 |
|---|---|---|---|
| `none`（默认） | 不统计 | 0 | `total=-1` |
| `capped` | 段内 bitset 标记每个被任一步返回的 doc，数到 10001 即停 | O(候选) | `total` + `totalExact=false` |
| `exact` | **禁用 kind 剪枝与 Cap**，跑完所有步骤，bitset 去重计数 | O(全命中) | `totalExact=true`，可能 `partial` |

- `exact` 只用于诊断 / 分页边界；一旦超时 → 降级为 `capped` 并置 `partial=true`；
- `capped` 的 bitset 每段 16k bit（2KB），池化复用；不在堆里，不受 limit 影响。

#### 3.5.5.5 证据合并（同 doc 多步召回）

```text
Merge(old, ev, bm25):
    if old == null: return Candidate(ev, bm25)
    if KindRank(ev.Kind) < KindRank(old.Kind):
        old.Evidence = ev                       // 更好的 kind 直接覆盖
    else if KindRank == && ev.Coverage > old.Coverage:
        old.Evidence = ev                       // 同 kind 比覆盖度
    else if 同 coverage && FieldRank(ev.Field) < FieldRank(old.Field):
        old.Evidence = ev                       // 再比字段
    old.Bm25 = min(old.Bm25, bm25)              // 仅桶内平局
    old.Sources.Add(ev.SourceStep)              // 诊断保留全部来源
    return old
```

- **不累加分数**：合并只保留"最好的证据"，避免弱信号叠加把候选抬进高桶；
- `Sources` 只进 `explain`，不参与排序；
- `bm25` 取更小（更相关）是既有实现口径，只作为第 6 键。

#### 3.5.6 多条证据合并（同一 doc 被多步召回）

| 字段 | 合并规则 |
|---|---|
| `kind` | 取**最高档**（Exact > Phrase > Prefix > Partial > Pinyin…） |
| `coverage` | **跟随最高 kind 的那条证据**；同 kind 取最大值。**不跨 kind 求并集** |
| `matchedTerms` | 并集（保序），**只进 `explain`**，不参与排序 |
| `field` | 取最高 kind 对应的 field；同 kind 时取 FieldRank 更小者 |
| `bm25` | 取最小值（更相关）；**仅用于桶内平局**，不允许跨桶比较 |
| `provenance` | 保留全部来源 step（`explain` 里可展开，默认省略） |

> 反例：doc 用 `prefix` 命中 A、用 `exact` 命中 B，若把 coverage 并成 2/2，
> 就会得到"exact 且全覆盖"的假证据。覆盖度只在**同一 kind 的证据**内比较。

#### 3.5.7 边界用例（每条都要有向量）

| 输入 | 期望行为 | 禁止行为 |
|---|---|---|
| 空串 / 全空白 | 直接返回空，不打开索引 | 构造空迭代器 / 打开索引 |
| 单 CJK 字（`爱`） | 只查元数据字面族（`PrefixIter("爱")`）；不生成读音 / 跨语言 / 容错 | 查歌词族；生成单字母首字母步 |
| 两 CJK 字（`爱情`） | 两字短语；不生成删字变体 | 退化成 `爱` + `情` 两次单字 |
| ≥3 CJK 字 | 全 term 字面 + minMatch 曲线；不足时**先减词** | 把跨语言当作唯一兜底（跳过减词） |
| 纯数字（`2024`） | 字面整词；不生成拼音 / 首字母 | 把数字拆成单字符 |
| 1–2 字符拉丁（`ai`/`go`） | 精确 term；不做前缀、不做容错 | `a*` / `g*` 扫全库 |
| 中英混排（`晴天jay`） | 按脚本切成 `晴天` + `jay` 两个 term，各自编译 | 把 `晴天jay` 当一个词 |
| 纯 emoji / 符号 | 返回空 | 抛异常 |
| 超长（≥64 码点） | 截断到策略上限并记日志；只跑字面步 | 变体枚举爆炸 |
| 含查询元字符（`"` `*` `(` `:` `OR`） | 作为普通字符进入词元（不拼查询串） | 拼进表达式导致语法错误 / 语义注入 |
| 全角 / 带重音 / 繁体 / 假名 / 谚文 | 归一化后按对应脚本处理 | 归一到一半就切分 |

### 3.6 第 6 层：分桶排序（替代单池 bm25）

排序也是一个可替换角色：`bucket@1` 是默认实现；S1 的等价性验证只用临时比较代码，不保留第二套 ranker。
方案（scheme）决定用哪个，**但任何方案都只能有一个 ranker**——这是"单一排序入口"的落地方式。

**排序键是一个元组，不是分数**：

```
kind_rank          整字段精确 < 全 term 字面短语 < 全 term 字面前缀
                   < 部分字面 < 拼音全拼精确 < 拼音短语 < 首字母
                   < 读音 < 交叉语言 < 容错
-covered_terms     命中的查询 term 数（降序）
field_rank         title < artist < album < filename < lyrics   （数字越小越前）
exactness_rank     整字段相等 < 以查询开头 < 包含 < 近似
proximity          多 term 在字段内的最小跨度（越小越好）
bm25               仅在以上全部相同时使用，且**不跨桶**
doc_id             最终稳定键（保证分页 / 重复查询确定）
```

> `kind_rank` 是**整条命中的匹配类型**，不是某个分支的类型：查询 2 个 term 时，
> "两个 term 都以短语命中"高于"一个精确 + 一个前缀"，也高于"只命中一个 term"。
> 这正是现状"跨趟合并"做不到的事——它只能比较"来自哪一趟"。

实现上就是一次 `List.Sort`（候选 ≤ 数千），不需要在 SQL 里做复杂表达式。
`bm25` 只在**桶内**作为细粒度平局破解，不再承担"跨类型比较"的职责——
这一条就能消掉 `initials_meta=2.0` 与 `title=4.0` 同池竞争的问题。

不采用"加权求和"的理由：只要权重是加法的，就存在"弱信号 × 高 TF = 强信号"的退化路径；
分桶是**序**的语义，天然不可交换（与 Meilisearch `ranking_rules`、Typesense `_text_match`
位域分桶一致，见 `local-search-indexer-references.md` §3.1 / §4.5）。

#### 3.6.1 `kind_rank` 的判定规则

`kind` 不是"由哪一步召回"直接决定的，而是**该候选在本次查询下的最终匹配质量**：

```text
if 全 term 命中:
    if 任一字段整串 == 查询（归一后）           -> Exact
    elif 全 term 在同一字段内连续（短语）        -> Phrase
    elif 全 term 命中且末 term 是前缀            -> Prefix
    else                                        -> Partial
elif 命中来自拼音列:
    if 多音节短语                                -> PinyinPhrase
    else                                        -> PinyinExact
elif 命中来自首字母列                            -> Initials
elif 命中来自读音列                              -> Reading
elif 命中来自跨语言列                            -> CrossLingual
else                                             -> Typo
```

同一个 doc 可能被多个步骤召回（字面 + 拼音）。合并时**取最高 kind**，
但保留全部 `matched_terms` 与 `field`（用于展示与回归断言）。
现状"按趟次顺序，先到者保留"会丢掉更强的证据——这正是 S2 要改掉的行为之一。

#### 3.6.2 比较器（稳定、可复现、可分页）

```csharp
int Compare(Candidate a, Candidate b)
{
    cmp(KindRank(a), KindRank(b));                    // 1. 匹配类型
    cmp(b.Coverage,  a.Coverage);                     // 2. 覆盖度降序
    cmp(FieldRank(a), FieldRank(b));                  // 3. 字段
    cmp(ExactRank(a), ExactRank(b));                  // 4. 精确度
    cmp(a.Proximity,  b.Proximity);                   // 5. 邻近度
    cmp(a.Bm25,       b.Bm25);                        // 6. 桶内细粒度
    cmp(a.DocId,      b.DocId);                       // 7. 稳定键
}
```

- `FieldRank`：`Title=0, Artist=1, Album=2, Filename=3, Lyrics=4, LyricsTr=5, LyricsRom=6`。
- `Proximity`：多 term 在同一字段内的最小跨度（单 term = 0）；由 3.6.3 的复核得出，
  歌词族无法精确计算时统一记 `int.MaxValue`。
- 第 7 键保证**同一查询重复执行、进程重启、翻页**三者顺序一致（现状没有这一条）。
- `bm25` 只在 1–5 全部相等时生效，**永远不跨桶**。

#### 3.6.3 精确度复核（DocStore 的用途）

候选集 ≤ Cap（默认 2k/步，合并后 ≤ 数千）时，从 DocStore 一次块读取出 `title_norm/artist_norm/album_norm`：

```text
q = 查询的归一形（去掉空白）
for field in [title, artist, album]:
    v = norm(field)
    if v == q                      -> Exact,    rank 0
    elif v.startswith(q)           -> Prefix,   rank 1
    elif v.contains(q)             -> Contains, rank 2
    else                           -> None
```

- CJK 的"覆盖度"用**字符级**计算：`|q ∩ v| / |q|`（按码点，顺序敏感）。
- 拉丁多词用**词级**覆盖：命中的词数 / 查询词数。
- 歌词族没有原文，只能给近似：命中段长度 / 查询长度（由候选的 bm25 与 df 反推上限），
  且统一降一档——**宁可把歌词命中排低，也不让它冒充标题命中**。

成本：2k 候选 × 3 字段 ≈ 6k 次短字符串比较，< 1ms；不额外查库（DocStore 顺序块读）。

> **阶段约束**：S2 还在 v7 存储上（没有 DocStore 原文），`ExactRank` / `Proximity` 只能记常量，
> 排序实际只用到 kind / coverage / field / bm25 / docId；这两个键到 S3 才生效，
> 届时必须**再跑一轮回放评审**，不要和桶序首秀混在同一次上线里。

#### 3.6.4 top-k、分页与 `totalCount`

- **排序只保证前 `kSearchIndexRankLimit` 条**；超出部分不返回、也不保证顺序（候选上限 + 桶序，
  不再对全命中集排序）。
- `total` 默认**不计算**（`totalMode=none`，响应 `total=-1`），避免每次查询遍历全部倒排；
  需要显示"共 N 首"时才显式请求：
  - `capped`：最多累计到 `10001`，返回 `total` + `totalExact=false`（UI 显示「10000+」）；
  - `exact`：完整计数，仅用于分页 / 诊断，成本 O(命中集)。
- 分页必须用 **keyset**（`after=(bucket..., docId)`）：`next` 为空表示没有更多；不允许 `OFFSET`
  跨快照翻页（索引重建 / 增量后 `OFFSET` 会漂移）。
- `next` 是**不透明游标**：内部编码 `(gen, kind_rank, coverage, field_rank, exact_rank,
  proximity, bm25, doc_id)`；客户端只回传、不解析。
  翻页时 `gen` 不符 → 返回 `next=null` + `restart=true`，让 UI 从第一页重查
  （增量写入后不保证旧游标仍然对齐）。`limit` 超过 `10000` 时按 10000 处理并返回 `clamped=true`。
- 若某个调用方确实需要 `OFFSET`：响应附带 `gen`，翻页请求带上它；世代不符则重查第一页。
- `explain.bucket` 只序列化**前五个排序键**（kind / coverage / field / exact / proximity），
  `bm25` 与 `docId` 不放进数组，避免把浮点数与内部 id 带进 UI 契约。

### 3.7 归因与可解释（跨层）

每条命中都带（`explain` 是 v2 载荷的一部分，不是「兼容追加」）：

```jsonc
{
  "trackId": "…", "score": 12.3,
  "pass": "lexical",                 // 保留：首个召回的步骤
  "explain": {
    "kind": "literal-prefix",
    "field": "title",
    "terms": ["晴天"],
    "coverage": 1.0,
    "bucket": [1, 1, 0, 1, 0]
  }
}
```

用途：
- UI 可以做"命中在歌词"的角标（现在只能靠 `pass=crossLingual` 猜）；
- 回归测试可以断言"这条必须由 literal-exact 召回"，而不只是"出现在结果里"；
- 排查"爱 → 恨"这类问题时，直接看 `kind` 与 `field`，不用逐支手工构造 `--raw`。

#### 3.7.1 `explain` 契约（v1，随 `api` 换代调整）

```jsonc
"explain": {
  "version": 1,
  "kind": "literal-prefix",          // MatchKind 小写形式
  "field": "title",                  // FieldKind 小写形式；多字段命中取最高
  "terms": ["晴天"],                  // 实际命中的查询 term（归一后）
  "coverage": 1.0,                    // 0..1
  "exact": "prefix",                  // equal | prefix | contains | none | approx
  "proximity": 0,                     // 多 term 最小跨度；未知为 -1
  "bucket": [1, 1, 0, 1, 0],          // 排序键快照，便于回归比对
  "scheme": "default@1",              // 实际使用的方案（可能因能力不足而降级）
  "highlight": [                      // 由 .NET 计算，Dart 直接画
    { "field": "title", "start": 0, "end": 2, "mode": "literal" }
  ]
}
```

约束：

- `explain` **不参与**排序计算（只读派生），关掉它排序结果必须完全一致；
- `highlight` 的覆盖范围：标题 / 歌手 / 专辑 / 文件名总是可高亮；歌词高亮需要原文，
  因此只在 `snippet=true` 时返回（查询期读 `library.db`），否则只返回字段级说明；
- `highlight` 的 `start/end` 用 **UTF-16 code unit 偏移**（与 C# `string` 索引、Dart `String` 索引一致）；
  非 BMP 字符占 2 个偏移，两端都不需要额外换算；
- `explain` 默认只在 `debug` / 诊断请求里返回，避免给每次查询增加 JSON 体积；
- `highlight` 属于 UI 必需品，由请求的 `highlight: true` 控制（默认开），不与诊断绑定；
- 字段名与 `MatchKind` / `FieldKind` 枚举一一对应，**不允许**出现枚举外的字符串
  （测试用 JSON Schema 校验）。

#### 3.7.2 指标与日志（沿用 `query_log` 的接缝）

| 指标 | 类型 | 用途 |
|---|---|---|
| `search.query.ms{scope}` | 直方图 | P50/P95 回归 |
| `search.plan.steps` | 直方图 | 计划步数（变多说明门控失效） |
| `search.step.candidates{kind}` | 直方图 | 哪一步在爆量 |
| `search.step.skipped{reason}` | 计数器 | 预算裁剪是否过激 |
| `search.escalations{kinds}` | 计数器 | "放宽链"实际走到第几档 |
| `search.result.empty` | 计数器 | 空结果率（按查询长度分桶） |
| `search.explain.dropped` | 计数器 | 多字段命中被合并掉的次数 |
| `search.scheme{id}` | 计数器 | 实际使用的方案分布（含降级） |
| `search.capabilities.missing` | 计数器 | 能力不足导致的降级次数 |

日志只记**结构化字段**（query 的哈希、term 数、脚本分布、steps、耗时），不记原始查询串
（隐私）。诊断模式才落原文，且遵守 `query_log_file.dart` 的现有策略。

### 3.8 增量、并发、重建

#### 3.8.1 `rev` 计算规范（确定性、与算法解耦）

```
canonical = "rev1\n"
          + "n=" + normalizer_version + "\n"
          + "t=" + tokenizer_version + "\n"
          + "o=" + options_bits + "\n"          // 影响"写哪些列"的开关（含歌词读音开关）
          + "T=" + len(title_norm) +  ":" + title_norm + "\n"
          + "A=" + len(artist_norm) + ":" + artist_norm + "\n"
          + "L=" + len(album_norm) +  ":" + album_norm + "\n"
          + "F=" + len(filename)   +  ":" + filename + "\n"
          + "y=" + sha256(lyrics_original_utf8) + "\n"
          + "x=" + sha256(lyrics_translation_utf8) + "\n"
          + "r=" + sha256(lyrics_romaji_utf8) + "\n"
          + "c=" + lrc_mtime + ":" + lrc_size + "\n"

rev = hex(sha256(canonical))[0..31]             // 128 bit
```

- **长度前缀**防拼接歧义（`"ab"+"c"` 与 `"a"+"bc"`）；UTF-8 编码固定；
- `scheme_version` **不进** `rev`（它不影响索引内容，只影响查询）；
- **去重 / 截断 / token 预算**属于"切分产物"，任何改动必须递增 `tokenizer_version`；
  否则 `rev` 不变、旧词元残留，属于最难排查的一类脏数据；
- `rev` 不依赖 mtime：内容没变、mtime 变了 → rev 不变 → 不重建（现状会白重建）；
- 由 scanner 写 `tracks.content_rev`；无该列时 indexer 现场算（只读一次大列，记日志）；
- `rev` 一旦冻结，后续只允许**追加字段**（`rev2` 另起），不允许悄悄改 canonical 格式——
  否则所有曲目会在下一次启动时变成"全部脏"。

#### 3.8.2 构建与刷新状态机

状态分两个维度，必须分开看：

| 维度 | 取值 | 说明 |
|---|---|---|
| 磁盘索引 | `ABSENT` / `READY(gen, manifest)` / `CORRUPT` | 只有 `READY` 可查询；`CORRUPT` 只能重建 |
| 写任务 | `IDLE` / `BUILDING` / `REFRESHING` / `COMPACTING` / `CANCELLING` / `FAILED` | 同一索引同时最多一个写任务 |

```text
IDLE ──start(FULL)────▶ BUILDING ──commit + CURRENT swap──▶ IDLE（READY, 新世代）
IDLE ──start(REFRESH)─▶ REFRESHING ──commit（可多次）─────▶ IDLE（READY, gen+1）
IDLE ──start(COMPACT)─▶ COMPACTING ──commit──────────────▶ IDLE（READY, gen+1）
任意任务 ──cancel────▶ CANCELLING ──清理────▶ IDLE
任意任务 ──fatal─────▶ FAILED ──上报──────▶ IDLE（磁盘状态不变或停在最后提交点）
```

**写任务的三条硬约束**：

1. **查询永远可用**：刷新 / 压缩期间，读者继续用最后一次提交的 `(gen, manifest)`；
2. **未提交不可见**：段先写、后提交；没有 Manifest 指向的字节等于不存在；
3. **一个写者**：进程级锁拿不到就只读（返回码 4），不排队、不并发写。

#### 3.8.2.1 全量构建（BUILDING）

| 步骤 | 动作 | 失败/取消后 |
|---|---|---|
| 1 | 取写锁；版本锚与数据包自检 | 返回 4 / 3，状态不变 |
| 2 | 新建工作文件 `search-<N+1>.eraindex.tmp` | 删除工作文件，`CURRENT` 不变 |
| 3 | 流式读 `library.db`：512 曲/批 → 并行分析 → 单写者累积 | 同上；**不可断点续传** |
| 4 | 每 64MB / 16k doc 刷段（写段 + CRC） | 丢弃未提交段；已写字节成为垃圾，随文件删除 |
| 5 | 全部写完：Manifest → fsync → 提交槽 → fsync | 同上；旧世代仍可查 |
| 6 | 原子替换 `CURRENT` 指向新世代 | 替换失败：旧世代继续服务，工作文件保留待重试 |
| 7 | 旧世代进入删除队列（宽限期后删） | 删除失败只推迟 |

- 全量构建**只在收尾提交一次**（不是每 128 曲），所以中途取消 = 什么都没发生；
- 构建期间查询走旧索引（若版本锚不兼容则走 `LIKE`），不会看到半成品；
- 进度回调 `phase = scan | analyze | write`，每 50 曲或阶段切换一次。

#### 3.8.2.2 增量刷新（REFRESHING）

前置：`READY` 且版本锚一致；否则直接升级为全量构建。

| 步骤 | 动作 | 说明 |
|---|---|---|
| 1 | 打开当前 Manifest；从 DocStore `docmap` 建 `seen: track → rev` | 50k 曲约 1MB 内存 |
| 2 | 流式扫 `library.db`，逐批比较 `content_rev` | `rev` 相同 → 零内容读取 |
| 3 | 每 **128 曲一批**：分析 → 旧 doc tombstone → 追加新段 | 批内失败只丢该批 |
| 4 | 写 Manifest → fsync → 提交槽 → fsync，`gen + 1` | 提交后立即可查 |
| 5 | 扫描完整成功后，处理"索引有、库无"的删除 | **扫描失败/中断 → 一律不删** |
| 6 | 更新 `pending_writes / pending_deletes`，必要时触发压缩 | 统计在段属性里 |

- **删除安全**：只有在"库扫描完整成功 + `library.db` 曲目数 > 0"时才执行删除，
  防止磁盘瞬断 / 库被临时清空导致整库被 tombstone；
- 单曲分析异常：记录 `errors++` 并跳过该曲；错误率 >1% 或 >100 曲 → 中止任务；
- `content_rev` 缺失：应用侧现场哈希（正确但慢），记 `warn`，不阻塞；
- 取消：在批次边界生效，**已提交批次保留**，`gen` 不回退；下次刷新从 `seen` 重新对比。

单曲判定：

| `rev` 比较 | 在 `seen` | 动作 |
|---|---|---|
| 不存在 | — | 新增：分析 → 写新 doc |
| 相同 | 是 | 跳过（零内容读取） |
| 不同 | 是 | 旧 doc `tombstone` + 写新 doc |
| — | 否（索引有、库无） | `tombstone` + DocStore 标记删除（仅完整扫描后） |

#### 3.8.2.3 压缩（COMPACTING）

- 触发：死行比例 ≥20%、段数 ≥8、或后台空闲且距上次 ≥24h；
- 选段：同层最小的若干段，输入 ≤ `2×S`（约 160MB）；
- 产出：**向同一世代文件追加**合并后的新段；新 Manifest 只列活段；
- 旧段字节变成**垃圾**（单文件不可原地删除），由 `era-index stats` 报告；
- 提交后旧段不再被查询；不做"删除中间字节"；
- 垃圾比例 >30% → 排一次**全量重建**（新世代文件，顺便回收空间）；
- 取消 / 失败：不提交，旧 Manifest 不变；新段成为垃圾，下次重建清掉；
- **不可断点续传**：合并任务要么整体提交，要么整体丢弃。

> 这也是为什么 §8.0 的 `bytes_per_track` 必须按"全量重建后的文件"测：
> 两次重建之间允许有 ≤30% 的垃圾；报告里同时给 `live_bytes` 与 `file_bytes`。

#### 3.8.2.4 取消、超时与失败语义

`era_index_start` 返回任务句柄；`era_index_cancel` 是**协作式**取消：

| 规则 | 说明 |
|---|---|
| 检查点 | 每批 128 曲、每 4MB 写入、每 100ms 循环 |
| 提交临界区 | 写 Manifest / 提交槽的几毫秒内**不响应取消**，避免"取消把提交撕开" |
| 已提交批次 | 保留；索引停在第 `gen`，仍然一致可查 |
| 全量构建 | 删除工作文件；`CURRENT` 不变；等于没发生 |
| 返回值 | 任务码 7（已取消）；`era_index_wait` 返回 7 |
| 幂等 | 重复 cancel 无副作用；已完成的任务 cancel 返回 7 或 0（不改变结果） |

失败分类：

| 失败 | 处理 | 索引状态 |
|---|---|---|
| 读 `library.db` 失败 | 中止任务，**不做删除** | 不变 |
| 单曲分析异常 | 跳过该曲，累计错误；超阈值中止 | 已提交批次有效 |
| 磁盘满 / 只读 | 中止；未提交数据丢弃 | 停在最后 `gen` |
| Manifest / 槽写入失败 | 中止；旧槽继续有效 | 不变 |
| `CURRENT` 替换失败 | 旧世代继续服务；工作文件保留 | 不变 |
| 合并失败 | 中止；旧段继续服务 | 不变 |
| 打开时 CRC 失败 | 尝试另一槽 / 上一世代；都失败 → 重建 | `CORRUPT` → 重建 |

#### 3.8.2.5 恢复矩阵（按崩溃点）

| 崩溃点 | 持久状态 | 下次打开 |
|---|---|---|
| 写数据段中 | 没有 Manifest 指向 | 忽略尾部字节 |
| 写完 Manifest、未写槽 | 旧槽有效 | 回到旧 `gen`；新 Manifest 成垃圾 |
| 写槽中（撕裂） | 槽 CRC 失败 | 回退另一槽 |
| 槽已 fsync | 新 `gen` 有效 | 直接可用 |
| `CURRENT` 临时文件写入中 | 旧 `CURRENT` 有效 | 旧世代 |
| `CURRENT` 替换后 | 新世代 | 新世代 |
| 旧世代删除中 | 新 `CURRENT` 指向新文件 | 旧文件残留，后台清理 |
| 合并中 | 旧 Manifest | 旧段继续服务 |
| 全量构建中 | 工作文件 | 忽略；旧世代不变 |

#### 3.8.2.6 版本闸门

`index_format` / `normalizer_version` / `tokenizer_version` / `options_bits` /
`data_pack_id` / `capabilities` 任一与 Manifest 不符或无法解析 →
**旧索引立即作废（不读取）**：走全量构建，成功后替换 `CURRENT`；
失败则保持 `LIKE`，不尝试兼容、不回退旧文件。**`scheme` 不符不重建**（只影响查询）。

| 锚 | 初始值 | 说明 |
|---|---|---|
| `index_format` | **1** | EraStore 物理格式；与 v7 的 schema 8 无关 |
| `normalizer_version` | 1 | 归一化管道 |
| `tokenizer_version` | 2 | bigram + 首字母 bigram；去重 / 截断规则变化也必须递增 |
| `scheme_version` | 1 | 查询 / 排序方案默认策略 |
| `data_pack_id` | manifest 哈希 | 数据表版本 |

#### 3.8.3 崩溃恢复与世代切换

- **提交顺序不可交换**：写数据段 → `fsync` → 追加 Manifest → `fsync` →
  覆盖较旧的 CommitSlot（含 CRC）→ `fsync`；
- CommitSlot 是双槽：崩溃后只会指向"旧"或"新"，CRC 不符则回退另一槽；
- **增量不替换文件**：同一世代文件内追加段 + 提交槽；
- **全量重建**才产生新世代文件：`search-<N>.eraindex`，写完后原子更新 `CURRENT` 指针
  （POSIX `rename` / Windows `ReplaceFile`），旧世代在宽限期后删除；
  Windows 上若旧文件仍被 mmap，删除失败只推迟，不报错；
- 启动清理：未提交的尾部忽略；`CURRENT` 指向的文件头 / 提交槽损坏 → 尝试上一世代 →
  都不行 → 删除重建；坏文件可归档一份（`search.corrupt.<ts>`，最多 1 份，**只归档不读取**）；
- 不做"每次打开都全量校验"（大库成本高）；改为设置页的显式「校验索引」动作。

#### 3.8.4 合并与空间回收

- 触发：死行比例 ≥20% **或** 段数 ≥8；
- 分层合并：小段并入相近大小的段，单次时间预算 2s；
- **可中止、不可部分提交**：中途失败/取消则旧 Manifest 不变；重跑幂等，不续传；
- 合并产物是**新段 + 新 Manifest**；旧段字节保留为垃圾，由全量重建回收；
- 合并只做物理整理，不改变内容与排序；失败只记 warn，不回滚已提交的增量；
- 合并会重算每个 term 的 `df` 与 `max_tf`，所以统计不需要单独"重建"。

#### 3.8.5 并发与缓存键

| 角色 | 规则 | 现状问题 |
|---|---|---|
| 写 | 永远单写（进程级排它锁）；增量追加提交槽；全量写新世代 + 更新 `CURRENT` | 基本正确，但失效靠手工 |
| 读 | 持有 `(gen, manifest)` 快照；无锁；后台构建不阻塞读 | 正确 |
| 缓存键 | `hash(query, scope, limit, cursor, totalMode, explain, highlight, snippet, scheme, gen)` | 少一项就会串页 / 串诊断开关 |
| 失效 | `gen` 变化（每次提交 +1）→ 清结果缓存 + 清纠错候选缓存 | 现状是手动 `CloseCached()` |

> 没有 `VACUUM` / `checkpoint` / `optimize` 这类操作：合并只减少活段并产生垃圾，
> **物理空间回收只发生在全量重建**；两者都由写者独占执行。

#### 3.8.6 统计与词表的维护（EraStore）

EraStore 把"统计"变成**段的属性**，而不是一张需要后台刷新的表：

| 数据 | 存在哪 | 何时更新 | 过期会怎样 |
|---|---|---|---|
| `n_docs` / 各字段 doc_count / total_len | 每个段的 `Stats` | 段写入时 | 不存在过期：查询按活段求和 |
| 每词 `df` / `block_max` | 词典条目 | 段写入 / 合并重写 | 不存在过期 |
| 纠错候选 | 词典前缀区间 | 每次查询实时扫 | 无需缓存；`recall@1` 才走 |
| `reading_complete_ratio` | `Stats` | 段写入 | 只影响诊断显示 |

- **没有 `hot_terms` / `vocab_meta` / `stats_dirty`**：这些都是 SQLite 时代的折中，EraStore 不需要；
- 段自身的统计与段内容同生同死，合并时重算，所以不存在"统计落后于内容"的窗口；
- 查询期只读 Manifest 里的段列表与各段的常量，不扫描文档。

#### 3.8.7 多实例与文件锁

同一台机器可能同时跑两个 App 实例（多窗口 / 快速启动残留），必须有明确规则：

| 场景 | 规则 |
|---|---|
| 写任务 | 对 `<index-dir>/.write.lock` 取**进程级排它锁**（POSIX `flock` / Windows `LockFileEx`）；5s 取不到 → 只读模式，不构建 |
| 读任务 | 不需要锁；不可变段 + `(gen, manifest)` 快照；每个进程各自映射 |
| 全量切换 | 写新世代文件 → 原子替换 `CURRENT`（POSIX `rename` / Windows `ReplaceFile`） |
| Windows 文件占用 | `CURRENT` 替换失败 → 保留旧世代、下轮再试；旧世代删除失败只推迟，不报错 |
| 网络盘 / 只读介质 | mmap 或排它锁不可用 → 禁用索引、降级 `LIKE`，给出可操作提示 |
| 世代回收 | 只在新 `gen` 提交且宽限期（≥2 个 `gen`）后删除；读进程即使还开着旧文件也不受影响 |

> 与 SQLite 的关键差异：增量**从不替换文件**（只更新提交槽），只有全量重建才动 `CURRENT`；
> 因此不存在"rename 与打开会话互斥"的日常冲突。

#### 3.8.8 与 scanner（`library.db`）的契约

索引器**不访问音乐文件**；曲目数据只来自 `library.db`（索引文件由 EraStore 自己管理）。需要的列与语义：

| 列 | 用途 | 缺失时 |
|---|---|---|
| `tracks.id` | `track_id`（= md5(path)） | 无法工作，拒绝构建 |
| `title` / `artists` / `album` | 元数据 | 按空串处理 |
| `lyrics` | 内嵌歌词 | 按空处理 |
| `lyrics_lrc` / `lyrics_lrc_trans` / `lyrics_lrc_romaji` | 侧车三层 | 按"无侧车"构建 |
| `lrc_mtime` / `lrc_size` | 侧车锚点 | 同上 |
| **`content_rev`** | 内容指纹（新增） | 退回应用侧哈希（正确但慢），记 warn |

规则：

- `content_rev` 必须覆盖"会影响索引内容"的所有字段（标题 / 歌手 / 专辑 / 歌词三层 / 侧车锚点）；
  scanner 写库时更新，索引器只比较；
- 索引器**不**修改 `library.db`；查询期只在 `snippet=true` 时以只读连接取歌词片段；
- 列名 / 语义变更属于跨模块契约变更，必须先改 scanner 与本文档，再改索引器。

### 3.9 降级矩阵（任何一行都不能抛到 UI）

| 条件 | 行为 | 用户可见 | 日志 | 恢复 |
|---|---|---|---|---|
| 总开关关闭 | 不加载原生库、不查询 | 降级为 `LIKE` | debug | 设置页开启 |
| 索引缺失 | 后台构建；查询走 C# `era_search_fallback`（参数化 LIKE） | 搜索结果可用但较弱 | info | 构建完成自动切换 |
| `schema` 不符 | 旧索引作废（不读取）+ 后台全量重建；重建期走 `LIKE` | 短暂降级 | info | 重建完成 |
| `normalizer` / `tokenizer` 不符 | 后台全量重建 | 无感 | info | 同上 |
| `scheme` 不符（索引能力仍满足） | **不重建**，按新 scheme 查询 | 排序可能变化 | debug | 无需动作 |
| 文件损坏 | 隔离坏文件 → 全量重建 → 降级为 `LIKE` | 短暂降级 | error（一次性） | 自动 |
| 磁盘满 / 只读 FS | 中止构建、删除 tmp；查询走 `LIKE` | 短暂降级 | warn | 用户清理后重试 |
| 原生库缺失 / 加载失败 | 降级为 `LIKE` | 弱结果 | warn | 重装 / 修复包 |
| 构建进行中（版本已变） | 查询走 `LIKE`；**不读旧索引** | 短暂降级 | — | 构建完成自动切换 |
| 查询超时（> 预算） | 返回已完成的步骤结果 + 标记 `partial=true` | 结果略少 | warn | 下次查询 |
| 单步候选超预算 | 跳过该步（记 `skipped{reason=budget}`） | 可能少召回 | debug | 调 scheme |
| 迭代器构造错误（不应发生） | 该步返回空，不中断整个查询 | 无感 | error | 修编译器 |
| `track_id` 已不在 `library.db` | 结果侧过滤（不删索引，等增量回收） | 无感 | — | 增量 |
| 查询期间 `gen` 变化 | 本次结果照常返回，缓存不写 | 无感 | — | — |

> 桌面端特有的一条：**构建期间 UI 仍可查询**——版本一致的手动重建可读旧世代；
> 版本变更时旧索引作废，查询走 `LIKE`。两种情况都不允许「重建时搜索框不可用」。

### 3.10 可观测性与诊断

诊断命令（复用 `Program.cs` 的 CLI，新增子命令；只读，不改索引）：

```bash
era-index plan    '周杰伦 晴天' --index search/        # 打印 QueryPlan（steps/budget/est）
era-index query   '周杰伦 晴天' --index search/ --explain --limit 20
era-index stats   --index search/                     # n_docs / 各段体积 / 版本锚 / 死行比例
era-index facets  --index search/ --field 0x01 --top 50   # 某字段族的词条 + df
era-index revdiff --library library.db --index search/    # 列出 rev 不一致的曲目
era-index health  --index search/                     # 版本 / CRC / 段哨兵泄漏 / 段数
era-index verify  --index search/                     # 全量 CRC（设置页「校验索引」调用）
era-index schemes --index search/                     # 能力清单 / 可用方案 / 实际降级链
```

诊断输出（JSON / 表格，不是 SQL）：

```text
-- 段哨兵是否泄漏进查询可见结果（应为 0）
seg_sep_leaks = 0

-- 高频词分布（判断门控是否合理）
field=0x01 term='晴天' df=2143
field=0x21 term='ni'   df=802

-- 读音完备率（判断日文/生僻字是否让读音步失效）
reading_complete_ratio = 0.87

-- 增量健康状况
pending_writes = 12   pending_deletes = 3   dead_ratio = 0.04   segments = 3
```

健康报告（设置页「索引状态」）：全部版本锚（`index_format` / normalizer / tokenizer / scheme /
`data_pack_id` / `options_bits`）、曲目数、段数与死行比例、体积、上次构建/合并时间、
`pending_writes` / `pending_deletes`、失败步统计、最近 10 条空结果查询的 `explain` 摘要。

#### 3.10.1 指标目录（唯一定义）

命名：`search.<area>.<name>`；标签只允许低基数维度（`scheme` / `scope` / `reason` / `code`）。

| 指标 | 类型 | 来源 | 定义 |
|---|---|---|---|
| `search.build.docs` | counter | 构建 | 本次扫描曲目数 |
| `search.build.changed` | counter | 构建 | 内容变化 / 新增数 |
| `search.build.deleted` | counter | 构建 | tombstone 数（仅完整扫描后） |
| `search.build.errors` | counter | 构建 | 单曲分析失败数 |
| `search.build.truncated_lyrics` | counter | 构建 | 触发护栏的记录数（按层标签） |
| `search.build.duration_ms` | histogram | 构建 | 全量 / 增量 / 合并分开 |
| `search.build.rss_peak_mb` | gauge | 构建 | 峰值匿名 RSS |
| `search.build.segments` | gauge | 构建 | 提交后的活段数 |
| `search.index.live_bytes` | gauge | 健康 | 活段字节 |
| `search.index.file_bytes` | gauge | 健康 | 文件总字节 |
| `search.index.garbage_ratio` | gauge | 健康 | `1 - live/file` |
| `search.index.dead_ratio` | gauge | 健康 | tombstone 占文档比 |
| `search.index.dict_bytes` | gauge | 健康 | 词典段字节 |
| `search.index.postings_bytes` | gauge | 健康 | 倒排段字节 |
| `search.index.positions_bytes` | gauge | 健康 | 位置段字节 |
| `search.index.docstore_bytes` | gauge | 健康 | DocStore 字节 |
| `search.open_ms` | histogram | 查询 | `era_search_open` 耗时（冷 / 热标签） |
| `search.first_query_ms` | histogram | 查询 | open 后第一条查询 |
| `search.query.duration_us` | histogram | 查询 | 成功响应；不含错误 |
| `search.query.empty` | counter | 查询 | 空结果次数（按 scope） |
| `search.query.partial` | counter | 查询 | `partial=true` 次数 |
| `search.query.clamped` | counter | 查询 | limit 被压到 10000 的次数 |
| `search.query.error` | counter | 查询 | 按 `code` 标签（1–8） |
| `search.query.fallback` | counter | 查询 | `scheme=fallback@1` 次数 |
| `search.query.degraded` | counter | 查询 | 方案降级次数（`from` / `to` 标签） |
| `search.step.ran` | counter | 执行 | 按 `kind` 标签 |
| `search.step.skipped` | counter | 执行 | 按 `reason` 标签 |
| `search.step.candidates` | histogram | 执行 | 每步返回候选数 |
| `search.cache.hit` / `miss` / `evict` | counter | 查询 | 结果 LRU |
| `search.response.bytes` | histogram | 查询 | 单次响应 JSON 字节数 |

规则：

1. counter 只在进程内单调；进程重启归零，不做持久化累计；
2. histogram 单位统一：耗时为**微秒**（健康页再换算 ms），字节为 byte；
3. **禁止高基数标签**：不放查询文本、`track_id`、文件路径、用户名；
4. 延迟只统计成功响应；错误与超时单独计数；
5. 不允许用 pooled 指标对外报告（pooled 只进诊断 JSON，不进健康页）；
6. 导出：`era-index metrics --json` 输出本进程快照，供内测对比；不上传。

#### 3.10.2 日志规范与隐私

| 级别 | 用途 | 示例 |
|---|---|---|
| `error` | 索引损坏、数据包缺失、内部错误 | `index_corrupt{gen=7}` |
| `warn` | 降级、单曲分析失败、锁冲突 | `build_skip_track{code=analyze}` |
| `info` | 构建开始 / 结束、方案降级 | `build_done{docs=48759,ms=98000}` |
| `debug` | 计划、步骤、预算（仅诊断开关） | `step_skipped{reason=budget}` |

- 每条日志固定字段：`ts`、`level`、`event`、`gen`、`scheme`、`code?`、`ms?`；
- **禁止**写：查询文本、`track_id`、歌词、文件路径、用户名；
- 查询文本只进本地 `query_log`（用户可选、可清空、不随包分发）；
- 轮转：单文件 10MB × 3；超过丢弃最旧；
- `explain` 默认关闭；开启时也只写结构化字段，不写原文；
- 崩溃转储不包含查询与路径；只保留版本锚与错误码。

#### 3.10.3 健康页字段映射

| 页面区块 | 字段 |
|---|---|
| 状态 | `state`、`gen`、`scheme`、`capabilities` |
| 版本 | 全部锚 + `data_pack_id` |
| 规模 | `docs`、`segments`、`liveBytes`、`fileBytes`、`garbageBytes` |
| 增量 | `pendingWrites`、`pendingDeletes`、`deadRatio` |
| 时间 | `lastBuildMs`、`lastMergeMs`、`openMs`（最近一次） |
| 诊断 | 最近 10 条空结果查询的 `explain` 摘要（不含查询原文） |

### 3.11 逻辑下沉 .NET：职责边界与迁移清单

**原则**：Dart 只做三件事——把用户操作变成请求、调用 FFI、渲染结果。
凡涉及"搜索是什么"的判断，一律在 `app/core/indexer` 里。

| 现有 Dart 文件 | 现在做的事 | 去向 | 契约变化 |
|---|---|---|---|
| `search_index.dart` | 组装请求、解析命中、schema 探针、持有 `kSearchIndexRankLimit` | 保留组装 / 解析；**常量与 scope 决策移到 C#** | 请求加 `scheme` / `cursor`，响应加 `next` |
| `search_highlight.dart` | 原文命中区间 | 删除，改由 C# 返回 `highlight[]` | 响应新字段 |
| `search_highlight_pinyin.dart` | 拼音 / 首字母区间 | 删除，C# 用同一套读音表计算 | 同上 |
| `search_lyric_snippet.dart` | 歌词片段截取 | 移到 C#：查询期只读打开 `library.db` 取歌词，按证据截取 | 响应加 `snippet` |
| `script_ranges.dart` | 脚本判定 | 删除（只保留 C# `ScriptRanges`） | — |
| `query_log.dart` | 归因计数聚合 | **写文件仍在 Dart（IO/隐私）**；计数由 C# `explain` 提供，不在 Dart 做分类 | 字段来源改为 C# |
| `search_models.dart` | DTO 映射 | 保留（纯映射） | 加可选字段 |
| `library_store_pager.dart` | 翻页 / `totalCount` 展示 | 保留展示；翻页改用 `next` 游标；需要"共 N 首"时显式传 `total=capped` | 请求 / 响应 |
| 曲库的 `LIKE` 回退（Dart 拼 SQL） | 索引未就绪 / 降级时的检索 | 移到 C# `era_search_fallback`（读 `library.db`，参数化 LIKE + 字段权重） | Dart 不再拼 SQL |

实现约束：

- NativeAOT **不能用反射扫描**注册表：`ISchemeRegistry` 的实现在编译期显式列出
  （`Registry.BuiltInSchemes`），新增算法 = 加一行注册，不引入动态发现。
- 歌词片段需要的原文不复制进索引：查询期以**只读连接**打开 `library.db`，
  按 `track_id` 取一段（`busy_timeout=1s`；带缓存；失败/锁冲突时返回 `snippet=null`，不报错）。
- **读音高亮的算法**：C# 用与索引完全相同的分析器重新分析该字段文本（短文本，成本可忽略），
  得到「第 i 个音节 ↔ 字符区间」映射；命中 syllable 索引后映射回原文区间。
  若归一化改变了长度（NFKC 折叠 / 繁简），退化为「只高亮字面命中」，不猜。
- 下沉后 Dart 侧代码量应显著下降；把"删除的 Dart 文件"列为验收项，防止死灰复燃。

#### 3.11.1 Dart DTO 收敛清单（与附录 D 一一对应）

**目标**：Dart 侧只剩"组装 / 解析 / 渲染"，任何搜索语义都不在 Dart。

| 类（`search_models.dart`） | 字段 | 说明 |
|---|---|---|
| `SearchRequest` | `api=2`、`query`、`scope`、`limit`、`cursor?`、`total`、`explain`、`highlight`、`snippet`、`scheme?`、`filter?` | `toJson` 省略 `null`；布尔值显式写出，便于日志复现 |
| `SearchResponse` | `api`、`gen`、`scheme`、`schemeRequested`、`items`、`total`、`totalExact`、`partial`、`clamped`、`queryTruncated`、`restart`、`next?`、`trace?`、`timings?` | 未知字段忽略 |
| `SearchItem` | `trackId`、`score`、`explain?`、`highlight?`、`highlightTruncated`、`snippet?` | `score` 只展示在诊断页 |
| `Explain` | `version`、`kind`、`field`、`fieldId`、`matched`、`total`、`coverage`、`exact`、`proximity`、`positioned`、`bucket`、`bm25`、`lexicalSources` | `bucket` 固定 5 元 |
| `HighlightRange` | `field`、`start`、`end`、`mode` | UTF-16 偏移；`mode ∈ literal/reading/initial` |
| `Snippet` | `field`、`text`、`ranges`、`truncated` | `ranges` 相对 `text` |
| `Trace` | `steps`、`skipped`、`truncated`、`deadlineHit` | 仅 `explain=true` 时非空 |
| `SearchError` | `code`、`message`、`retryable`、`detail` | 不继承 `Exception` 直接抛给 UI |
| `IndexHealth` | 见 D.6 | 设置页「索引状态」直接渲染 |
| `IndexProgress` | 见 D.6 | 构建进度条消费 |

解析规则：

- 枚举用 `fromWire`，未知值 → `unknown` + 一条 warn，不崩溃；
- `explain` / `trace` / `snippet` 为空时**不构造对象**，避免无谓分配；
- `next` 是不透明字符串，Dart **不解析、不拼接**；
- `items` 顺序即展示顺序，不再本地排序。

**握手与版本绑定**：

```text
启动：era_search_api_version() == 2   且   payload.api == 2
不一致：不调用 run；直接走 era_search_fallback + 设置页提示
打包：构建脚本把 native 版本写进 app/lib/services/search/search_api.dart 的常量
```

- `kSearchApiVersion = 2` 必须与附录 D 一致；契约测试 C13 覆盖 `api=1`；
- Dart 与 native **同包升级**，不存在"旧 Dart 配新 native"的兼容承诺。

**错误 → 行为**（全部在服务层收敛，不把异常抛给 Widget）：

| 码 | Dart 行为 |
|---|---|
| 1 | 记日志（bug / 旧字段）；本次返回空并提示"搜索暂不可用" |
| 2 | 触发后台构建；本次用 `era_search_fallback` |
| 3 | 删除索引 → 重建；本次 `fallback` |
| 4 | 200ms 后重试一次；仍忙则 `fallback` |
| 5 / 6 | `fallback` + 一次性 toast；不阻塞播放 |
| 7 | 静默（用户主动取消 / 页面销毁） |
| 8 | 记日志 + `fallback`；不显示堆栈 |

**分页 / 状态字段的 UI 语义**：

| 字段 | 处理 |
|---|---|
| `restart=true` | 清空当前列表，按同一请求从第一页重查；**不追加** |
| `next=null` | 停止加载更多 |
| `partial=true` | 列表照常展示，底部显示"结果可能不完整"（不静默） |
| `clamped=true` | 提示"仅显示前 10000 条" |
| `queryTruncated=true` | 不提示；输入框计数器已限制 200 |
| `total=-1` | 不显示"共 N 首" |
| `totalExact=false` | 显示"10000+" |

**必须删除的代码**：

| 文件 / 模式 | 原因 |
|---|---|
| `search_highlight*.dart` | 高亮改由 C# 返回 |
| `search_lyric_snippet.dart` | 片段改为 `snippet` 字段 |
| `script_ranges.dart` | 脚本判定只在 C# |
| `kSearchIndexRankLimit` 等语义常量 | 上限 / Cap 归 C# |
| `minExact` / `candidateCap` / `fuzzy` / `ranking` / `engine` | v2 已删除 |
| Dart 侧 `LIKE` 拼 SQL | 改 `era_search_fallback` |

**验收命令**（本地跑，不进 CI）：

```bash
rg -n "minExact|candidateCap|fuzzy|ranking|engine=" app/lib
rg -n "pinyin|initials|script_ranges|search_highlight|lyric_snippet" app/lib/services/search
rg -n "LIKE .*tracks|SELECT .* FROM tracks" app/lib
```

- 只允许出现在 `search_models.dart` 的 `kind` / `mode` 枚举字符串与注释里；
- 其它命中一律视为"逻辑漏回 Dart"，PR 不合并。

**契约测试对接**：

- `tests/contract/response/*.json` 直接喂给 `SearchResponse.fromJson`，断言不抛异常且关键字段一致；
- 高亮用 C19 的非 BMP 用例验 `String.substring(start, end)`；
- 游标用 C08/C09 验"两页无重复"与"`restart` 清空重查"。

---

## 4. 重构纪律（十条硬规则）

> 这一节是本文的"回答"。前面是设计，这里是**纪律**。任何一条破了，重写就会变成第 24 批补丁。

1. **规格先行，代码第二**：先定 `SearchSpec v1`（归一化、切分、方案声明、计划、桶序、预算），
   再改代码。规格里每个数字都要能追到"证据"（bench / 真实库 / 外部参照），不能追到"感觉"。
2. **唯一实现，逻辑全部在 .NET**：召回 / 匹配 / 排序 / 高亮 / 片段 / 分页 / 陈旧过滤 / 归因
   只允许在 `app/core/indexer` 里存在一份；Dart 侧出现同类算法即视为架构违规。
3. **算法插件化，方案层组合**：新增算法 = 新增一个 `I*` 实现 + 可选注册进新 scheme；
   **不改动已有 scheme 的行为**。已有 scheme 的 golden vectors 是它的"行为合同"，
   改动必须显式升级 scheme 版本并附回放 diff。
4. **能力协商，不满足则降级**：索引声明 `capabilities`，scheme 声明 `requires`；
   交集不足时自动落到 `degradeTo` 方案，不允许"运行到一半发现列不存在"。
5. **不复古，成对升级**：Dart 与 native 同包同版本，靠 `era_search_api_version()` 握手；
   `api:2` 可以增删字段、改名、换语义，但**不保留 `api:1`、不保留旧符号、不读旧索引**。
   每次换代附 JSON Schema + 契约测试。
6. **排序只有一个入口**：`SearchRanker` 是唯一排序点，输入只有 `Evidence`；
   禁止"先排序、再后处理重排"的旁路。这条进 code review 清单。
7. **任何放宽都必须先有负例**：新增 step / 放宽门控 / 新增算法，
   必须先有"库里没有这个字 → 必须返回空 / 不得整库命中"的断言。
8. **阈值必须是相对量或可证明的绝对量**：新常量要么是 `df/N` 相对量，要么有真实库数据支撑
   （例如"≥4 字符才允许 1 错"有 Typesense / ES / Lucene 三方一致），不接受"先设 3 试试"。
9. **文档债不并存**：旧审计文档转为"问题台账（只记录已发生的事实）"，
   新文档是"目标与迁移（只记录要做什么）"；两者不各自维护一套"建议实施顺序"。
10. **不重复造轮子，也不引入服务**：成熟引擎只借**规则与参数**（分桶、词长阶梯、min-match、
    有界合并），不借代码；`local-search-indexer-references.md` 的结论继续有效，
    已评估否决的项（自研倒排 / WAND / 换打分器 / 全局 df 门控）不要重开。

---

## 5. 迁移路线（S0–S4，每步独立验收）

> **发布门**：S1 / S2 只进内测通道，**正式包从 S3（EraStore）起**。
> 若确实需要提前发正式版，S1/S2 只能沿用当时的 v7 索引（它是"当前"，不是"旧版兼容"），
> 且必须在下个版本完成 S3；不允许把 SQLite 搜索长期留在正式包里。

### S0 — 立规格与基线（不改行为）

产出：
- `SearchSpec v1`（归一化 / 切分 / 方案定义 / 计划 / 桶序 / 预算）+ `scheme` 注册表。
- **golden vectors**：`tests/vectors/` 扩到覆盖 `Analyze / Plan / Rank` 三层，
  每条给"输入 → 期望结构"，不是"期望字符串"（避免把存储细节固化进测试）。
- **歌词分布统计**：真实库每层的行数 p50/p95/p99/max + 行级去重率，用来校准 V6 护栏（不是拍 256）。
- **回放 harness**：从桌面端真实查询日志（`query_log_file.dart` 已在记录）生成
  `(query, top-k)`，作为新旧对比的唯一主线；日志不足时用 bench 语料派生查询补齐。

验收：规格评审通过；回放 harness 能在旧实现上跑出基线 JSON。
失败处置：无（纯文档 / 测试）。

> **落地进度（2026-11-23）**：`replay run` / `replay corpus` 已实现于
> `app/core/indexer/Replay.cs`（`Program.cs` 已接入 `replay` 分派），命令形态与 §6.7.6 一致。
> 已在真实库（117 首；查询日志 19 行按 `(query, scope)` 去重后 15 条）跑通：
> `replay run` 产出 §6.7.4 基线 JSON（重复运行 ids 与 `queries.hash` 完全一致）；
> `replay corpus` 按 `sha256(track_id)` 升序切子集并给出 corpus hash，子集库可正常
> `build` 后再回放。尚未实现：查询集抽样器（fixed 14 + replay 200 的加权抽样）、
> `SearchSpec v1` 文本、Plan/Rank 三层向量、真实库歌词分布统计（本机真实库仅 117 首、
> 且只有 `lyrics` 一层，样本不足以校准 V6 护栏）。

### S1 — 行为等价重构（**改动最大、风险最低**）

把现状的"六趟 + 短路 + 常量"重排成"Plan + Evidence"，**但保持输出 top-k 逐条字节一致**。
桶序在此阶段**不启用**（仍按趟次顺序合并），只把结构拆开。

| 任务 | 交付物 | 文件落点 | 验收 | 粗估 |
|---|---|---|---|---|
| T1.1 | `SearchPolicy`（承接全部现值，默认数字一个不改） | 新 `SearchPolicy.cs` | 单测：默认值快照 | 1–2 人日 |
| T1.2 | `QueryTerm / Step / Evidence / QueryPlan` 类型 | 新 `SearchPlan.cs` | 类型可序列化（诊断用） | 1 人日 |
| T1.3 | `PlanCompiler`：复刻六趟 + 丢词 + 容错 | 新 `SearchPlanCompiler.cs` | 向量：每个查询的 plan 稳定 | 3–5 人日 |
| T1.4 | `PlanExecutor`：按 plan 跑**当前 v7 存储**（S1 是唯一允许碰旧存储的阶段），保留旧合并顺序 | 改 `SearchSession.cs` | 与旧 top-k 逐条一致 | 3–5 人日 |
| T1.5 | **删除旧六趟直连代码**（直接替换，不保留双引擎、不留开关） | 改 `SearchSession.cs` | 代码里只有 `Plan` 一条路径 | 1–2 人日 |
| T1.6 | 等价性回放脚本（三语料） | `bench/qbench` 增模式 | 0 差异 | 2 人日 |
| T1.7 | **ABI v2**：`era_search_*` 句柄 + `api_version` + 请求/响应 JSON Schema（含 `scope=lyrics`）；**删除全部 `indexer_*` 符号** | `IndexerJson.cs` / `Ffi.cs` + `docs/` | Dart 与 native 握手一致；旧符号零残留 | 2–3 人日 |
| T1.8 | Dart FFI 适配 v2（同包升级，删除旧字段） | `indexer_ffi.dart` / `search_index.dart` | 无 `minExact` / `candidateCap` 残留 | 1–2 人日 |

验收：3 个语料（400 合成 / 3k quick / 真实库子集）× 全部固定查询 + 回放集，
**top-k 与 S0 固化的 baseline JSON 逐条相同**（含 `trackId` 顺序与内部计数）。
对照对象是**基线文件**，不是保留旧实现——旧代码在 T1.5 已删除。
`scope=lyrics` 与 `typo2` 没有旧基线，S1 只做"不崩溃 / 不越界"冒烟，正式判定在 S2 的负例。
失败处置：**不进入 S2**，修复后重跑等价性（不提供运行时回退开关）。

> 这一步是"重写"与"打补丁"的分水岭。跳过它直接改语义，就永远说不清"变好还是变坏"。
> 注意：等价性允许"排序相同但内部结构不同"，**不允许**"差一条然后解释为噪声"。

### S2 — 桶序上线（不重建索引）

| 任务 | 交付物 | 文件落点 | 验收 | 粗估 |
|---|---|---|---|---|
| T2.1 | `Ranker`：kind → coverage → field → bm25 → docId | 新 `SearchRanker.cs` | 单测：构造候选，断言顺序 | 2–3 人日 |
| T2.2 | `kind` 判定（全 term 命中 / 前缀 / 部分） | 同上 | 向量覆盖 10 个 kind | 2 人日 |
| T2.3 | 回放 diff 报告（每条查询 top-10 变化 + 原因） | `bench/qbench` | 报告可读、可筛选 | 1–2 人日 |
| T2.4 | `ISchemeRegistry` + `default@1` / `fast@1`（无 legacy 方案） | 新 `SearchScheme.cs` / `SchemeRegistry.cs` | 两个方案各自有向量；注册表无旧实现引用 | 2 人日 |
| T2.5 | 负例集扩到覆盖每个 kind | `tests/vectors/negative` | 全绿 | 1–2 人日 |
| T2.6 | **`recall@1`**（含 `typo2` / `scope=lyrics`），复用 T2.4 的注册表与 matcher | `SchemeRegistry` | 方案各自的向量；`typo2` 仅 `recall@1`；增删方案不改实现 | 2–3 人日 |

> ⚠ S2 仍在 v7 存储上（没有 DocStore 原文），`ExactRank` / `Proximity` 只能记常量；
> 这两个键要到 S3 才真正生效，届时排序会再变一次——必须再做一轮回放评审，不要一次放两个变量。

验收：NDCG@10 提升或持平；负例零回归；P95 不明显变差；`kind` 归因与人工抽样一致；
`scope=lyrics` 与 `typo2` 的负例（N21–N24）全绿；三个方案各自的向量全绿。
失败处置：不合并、不发布；修复后重跑回放与向量（不提供 `legacy` 回退方案）。

### S2.5 — 逻辑下沉 .NET（与 S2 并行，不依赖 EraStore）

| 任务 | 交付物 | 验收 | 粗估 |
|---|---|---|---|
| T2.7 | `era_search_run` 返回 `highlight[]`：原文 / 拼音 / 首字母区间由 C# 计算 | 与 Dart 现实现逐条对比一致（黄金集） | 2–3 人日 |
| T2.8 | 歌词片段下沉：查询期只读 `library.db` 截取 | 片段与 UI 期望一致；失败返回空片段 | 2 人日 |
| T2.9 | 陈旧 `track_id` 过滤下沉 | 结果里不出现已删除曲目 | 1 人日 |
| T2.10 | 分页 keyset 下沉（`next` 游标） | 翻页不漂移；与 `totalCount` 自洽 | 1–2 人日 |
| T2.11 | 删除 Dart `search_highlight*` / `script_ranges.dart` / `search_lyric_snippet.dart` | 静态检查无引用；行为不变 | 1 人日 |
| T2.12 | `LIKE` 回退下沉 `era_search_fallback`（参数化 SQL + 字段权重） | 索引关闭 / 未就绪时结果与旧 Dart 路径一致 | 1–2 人日 |

验收：Dart 侧不再出现任何"搜索语义"代码；`dart analyze` 0 issue；高亮 / 片段与旧实现一致率 100%
（先对比后删除，不允许"删了再说"）。

### S2.7 — EraStore 原型（2 周，前置验证）

> 存储已定 EraStore（ADR-1），S2.7 不再是"选型"，而是**提前把格式风险打掉**：
> 用 50k 合成语料跑通写 → 读 → 查询，拿到第一组真实数字。
> 原型不进发布包，但代码结构按正式模块写（后续直接演进，不重写）。

| # | 任务 | 验收 |
|---|---|---|
| T2.13 | 段写入 + 词典 + 倒排 + 提交槽；50k 合成文档（歌词三层 + 拼音全开） | 能建、能读、能查询；`open` 只读 Header + CommitSlot + Manifest |
| T2.14 | 查询覆盖：字面精确 / 前缀 / 元数据拼音短语 / 歌词拼音 / 首字母 / 截断 | 与朴素内存参考实现逐条一致 |
| T2.15 | 指标：体积 / `open` / P50 / 构建 RSS | 体积 ≤ 0.75×（目标 0.65×）、`open` ≤ 10 ms、P50 ≤ 3–6 ms、RSS ≤ 120 MB |
| T2.16 | 与 S0 基线 JSON 对拍（不是与 SQLite 现跑对比） | 产出 footprint / startup / latency 三份 JSON |
| T2.17 | 归档结论 + 已知限制清单（供 S3 排期） | 文档更新；原型代码作为 S3 骨架保留，不留一次性脚本 |

验收：三份 JSON + 一页结论。若任一指标不达，**先修格式/实现，不降级功能**（不关歌词拼音）。

### S3 — 索引落地：EraStore（需要重建，风险最高）

> S3 的合并条件不是"功能做完"，而是 **§8.0 的七个相对指标全部达标**。
> 每个体积相关子任务（V1–V7）必须单独给出"相对现状"的实测比值。
> 存储只有一个实现：**EraStore**；SQLite 仅保留"只读 `library.db`"这一项用途。

| # | 任务 | 失败处置 | 验收 |
|---|---|---|---|
| T3.0 | 移除搜索路径上的 SQLite / FTS5：`FtsStore.cs`、`SearchSession` 的 SQL、`fts5vocab`、PRAGMA 全部删除；`library.db` 只读访问保留 | 搜索路径无 SQL；`e_sqlite3` 只服务 `library.db` | 静态检查：搜索代码零 SQL 调用 |
| T3.1 | DocStore：`doc_id` / `track_id` / `rev` / `title_norm` / `artist_norm` / `album_norm` / `script_bits` / `doc_len` | — | 体积增量 ≤ 15 MB / 50k；`rev` 与源一致 |
| T3.2 | 段写入 + 词典（前编码）+ 倒排（Δvarint + 128 块）+ 提交槽；**只写 EraStore**；旧索引作废并覆盖 | 旧索引直接重建（无读取路径） | 不存在 v7 文件残留 |
| T3.3 | 切读路径到 EraStore | 修复后重建（索引可丢弃；不保留旧读取路径） | 回放集无回归 |
| T3.4 | **V4**：歌词拼音默认写、`initials_lyrics` 默认关 | 设置项（需重建） | 默认路径拼音召回无损；首字母歌词默认可关 |
| T3.5 | **V1**：歌词行级去重（原文 / 译文 / 拼音同源） | 修复后重建 | 去重率 ≥ 20%（真实语料抽样） |
| T3.6 | **V2（非默认）**：歌词拼音免位置（`positioned=false`），仅在其余无损手段仍不足时评估 | 保持默认带位置 | 启用时 `explain` 标注；歌词拼音召回不缩水（只放宽相邻） |
| T3.7 | **V3**：歌词字段不建 unigram（只 bigram） | 修复后重建 | 单字查询行为不变（本来不查歌词） |
| T3.8 | **V5/V6**：`lyrics_rom` 仅显式层写入 + 病态数据护栏（4096 唯一行 / 128 KB 每层） | 按真实分位上调护栏 | 真实库中**0 首正常曲目被截断**；截断仅发生在超护栏记录并打点 |
| T3.9 | 首字母 bigram（`tokenizer_version+1`） | 修复后重建 | 部分首字母召回提升；单字母仍未召回 |
| T3.10 | 分层段合并（死行 ≥20% / 段数 ≥8；单次 2s） | 修复后重建 | 增量耗时与脏曲数解耦；dead_ratio 可控 |
| T3.11 | 精确度 / 邻近度复核启用（读 DocStore + 位置） | 修复后重建（算法级开关仅调试，不进发布配置） | P@10 / NDCG 不降 |
| T3.12 | Manifest 写入 `capabilities` / `options_bits` / 版本锚 | — | `default@1` / `recall@1` 能正确降级 |
| T3.13 | 删除所有 v7 / FTS5 代码路径 / 常量 / 注释 | — | 代码里零 v7 / FTS5 引用；旧索引只会被删除重建 |
| T3.14 | **§8 实测门禁**：footprint / startup / latency 三份 JSON 与 S0 基线逐项对比 | 未达标不合并 | 七项比值全部满足 §8.0 |
| T3.15 | 全量重建路径：新世代文件 + `CURRENT` 原子切换；Windows 占用降级 | 修复后重建 | 三端都能在旧索引被占用时完成切换 |

验收（功能 + 资源，缺一不可）：

- `bytes_per_track ≤ 0.80×` 基线（EraStore 目标 ≤0.60×）；`open_ms ≤ 0.5×`（目标 ≤10 ms）；
  `first_query_ms ≤ 0.6×`；`query_p50 ≤ 0.35×`；`query_p95 ≤ 0.50×`；
  `rss_peak_build ≤ 0.80×`；`rss_query ≤ 60 MB`；
- 主路径（不落读音步）P50 ≤ 10 ms；带读音步的查询 P95 ≤ 35 ms；
- qbench 召回 / NDCG 不降；`rev` 增量在"只改标题不改 mtime"的构造用例生效；
- 旧版索引在启动时被删除并重建，**不存在读取旧索引的代码路径**；
- `lyrics_pinyin` 在默认配置下可用且音节级召回完整（V2 若启用，必须在 `explain` 标记 `positioned=false`）。

失败处置：停发已构建版本，修复后删除索引重建（索引是派生数据，允许丢弃；不保留旧路径）。
**不允许**用"关掉歌词拼音"来达标——V4 只关首字母，拼音歌词必须保持默认开。

### S4 — 跨语言与非核心能力收口

| 任务 | 交付物 | 验收 |
|---|---|---|
| T4.1 | 策展双语表（来源 / 许可证 / 权重），细则见 §5.5 | 负例集：无对应词必须空 |
| T4.2 | LRC 分层置信度 `layer_confidence`，细则见 §5.6 | 低置信层不参与 `scope=all`；`scope=lyrics` 仍可搜 |
| T4.3 | 数据包 manifest + C#/Dart 同源校验 | 校验和不一致时拒绝启用并有明确提示 |
| T4.4 | Dart 侧契约最终收敛（只剩请求 + 渲染） | `dart analyze` 干净；无搜索算法残留 |

验收：跨语言负例通过；Dart 侧搜索算法代码清零（只剩 FFI 组装与渲染）；
许可证登记进 `docs/acknowledgements.md` 与 `THIRD-PARTY-NOTICES.md`。

### 5.1 依赖关系与并行度

```
S0 ──▶ S1 ──▶ S2 ──┬──▶ S2.7（EraStore 原型，2 周；不阻塞 S2.5）
                   ├──▶ S3.1 ──▶ S3.2 ──▶ S3.3 ──▶ S3.4..S3.8
                   │        （S3 = EraStore 落地；无后端选择）
                   ├──▶ S2.5（逻辑下沉，可与 S2 并行）
                   └──▶ S4.2（LRC 置信度可独立于 EraStore 做）
S4.1（策展表）── 依赖数据/许可证，可与 S2/S3 并行
Dart 侧：S1 之后只改 FFI 契约与渲染，不参与召回 / 排序演进
```

### 5.2 分支与提交纪律（沿用现有约定）

- 每个 T 任务一个 PR；**语义变更与重构不混在同一个 PR**；
- 任何影响 `normalizer/tokenizer/schema` 的 PR 必须写明"触发重建"并附基准语料的
  体积 / 耗时 / 回放差异；
- 任何影响 `scheme` / `policy` 的 PR 必须附"回放 diff + 负例结果"，但不要求重建；
  新增算法不改任何已发布 scheme 的向量。
- **发布策略**：没有运行时回退开关，所以先在 beta / 内测通道跑满 §6.6 的矩阵与 §8 的门禁，
  再进正式包。正式包发现问题只能**发下一版修复**（索引可删除重建），不允许"线上切回旧实现"。 

### 5.3 S2.7 实施拆解（PR 序列，约 10 个工作日）

> 原型就是 S3 的正式模块骨架，**不是一次性脚本**；只是暂不接入发布路径。
> 每个 PR < 500 行、自带单测、不新增依赖；不能靠"最后一把跑通"。

| PR | 内容 | 主要文件 | 验收 | 人日 | 依赖 |
|---|---|---|---|---|---|
| P0 | varint（LEB128）、CRC32C、块编解码、端序 | `EraStore/Format.cs` | 1 万随机值往返；固定向量 | 0.5 | — |
| P1 | DocStore + 词典写入/读取 + `dump` | `DocStore.cs`、`TermDict.cs` | §3.3.0.14 的 3 首例子**逐字节一致** | 1.0 | P0 |
| P2 | 倒排 + 位置 + tf 位图 + 跳块 | `Postings*.cs`、`Positions*.cs` | tf=1 位图/例外表往返；位置 Δ 解码正确 | 1.0 | P1 |
| P3 | Manifest + 双提交槽 + open/恢复 | `Manifest.cs`、`IndexFile.cs` | 两槽轮换；"槽前/槽后"各 kill 一次 | 1.0 | P1 |
| P4 | `Term/Prefix/And/Or` 迭代器 + `Advance` | `Iterators/*` | `Advance` 单调性属性测试；与朴素实现结果一致 | 1.0 | P2 |
| P5 | `PhraseIter` + `positioned` + `seg_sep` | `PhraseIter.cs` | 短语向量；无位置字段自动降级 | 0.5 | P4 |
| P6 | 合成语料 + 流式构建 + 进度回调 | `bench/erastore/Corpus.cs`、`Builder.cs` | 50k 曲构建；RSS ≤120MB；字节确定 | 1.0 | P2、P3 |
| P7 | 合并 + tombstone + 垃圾统计 | `Merger.cs` | 死行比例下降；中止不半提交 | 1.0 | P6 |
| P8 | CLI `dump/verify/stats` | `Program.cs` | 翻转 1 字节被 `verify` 抓到；JSON 可机读 | 0.5 | P3 |
| P9 | `NaiveIndex` 差分（10k 随机查询） | `tests/NaiveIndex.cs` | top-20 顺序逐条一致 | 1.0 | P4、P5 |
| P10 | 模糊 + 三阶段崩溃注入 | `tests/Fuzz.cs` | 只允许报错/降级；只看到旧或新 `gen` | 0.5 | P3、P6 |
| P11 | 引擎基准 JSON | `bench/erastore/*` | build/open/query/merge 四份 JSON | 0.5 | P6、P7 |

**砍线规则**：若超出 10 个工作日，先砍 P7（合并）与 P11（完整基准），
**P9（差分）与 P10（崩溃）不能砍**——它们决定后面 S3 敢不敢切。

每个 PR 的完成定义（DoD）：

1. 能编译、单测全绿、`dart analyze` 不受影响（本阶段不动 Dart）；
2. 不新增 NuGet 依赖；所有整数显式端序；
3. 涉及格式的 PR 附"字节级"测试（不是只测对象相等）；
4. 涉及性能的 PR 附前后 JSON，不接受"感觉快了"；
5. 不改 §3.3、§3.5、§3.8 的语义；发现文档错误先改文档再改代码。

### 5.3.1 S2.7 归档结论（2026-11-23）

> T2.17 交付物：结论 + 已知限制清单（供 S3 排期）。原型代码即 S3 骨架，保留在
> `app/core/indexer/EraStore/`；bench / tests 是长期回归资产，没有一次性脚本。

**完成状态**

| # | 状态 | 证据 |
|---|---|---|
| T2.13 | 完成（quick + 30k 短歌词；full 未跑） | 12 字段族合成语料，5,000 doc / 3 段与 30,000 doc / 6 段均可建、可读、可查 |
| T2.14 | 完成 | `tests/EraStoreQueryCoverageTests.cs`：六类各 300 查询 × 4 段，完整序列逐条对拍；全量测试 PASS 956 / FAIL 0（含 `EraStoreNoPositionTests` 25 条） |
| T2.15 | 部分 | quick / 30k 指标见下；50k 长歌词档与「≤0.75×」比值待 S0 基线 |
| T2.16 | 完成 | `bench/erastore/out/` 八份 JSON：build / footprint / open / startup / query / latency / merge / verify |
| T2.17 | 完成 | 本节 |

**quick 实测**（5,000 doc / 3 段 / 12 字段族 / 派生层免位置，固定 `file_uuid` 与 `created_ms`）

| 项 | 实测 |
|---|---|
| 索引体积 | 6,200,917 B（1,240 B/轨）；dict 62,314 / dictIndex 1,392 / postings 2,531,783 / positions 2,864,405（46.2%）/ docstore 739,645 / 垃圾 1,378 |
| 歌词与读音层 | 歌词原文 466,631 / 译文 363,322 / 音译 522,400 / 元数据拼音 68,337 / 歌词拼音 425,088 / 元数据首字母 68,962 / 歌词首字母 340,609 / 读音 69,480 |
| 构建 | 0.49 s，峰值 RSS 97.2 MB，托管堆 0.1 MB |
| open | 冷 3.54 ms / 热 0.04 ms / 首查 1.23 ms |
| 查询（仅存储层） | 28,600 次；pooled P50 0.02 ms / P95 0.06 ms；literal-exact 0.01/0.01、literal-prefix 0.05/0.12、meta-pinyin-phrase 0.06/0.07、lyrics-pinyin 0.04/0.05、initials 0.02/0.02、truncated 0.01/0.02（P50/P95） |
| 合并 | 2 段 / 死行 8.00% → 0.00%，0.45 s，写出 4,364,749 B |
| verify | ok，10 段，4,600 doc（合并后；合并前 5,000） |

**30k 实测**（30,000 doc / 6 段 / 12 字段族 / 短歌词 10–20 行）

| 项 | 实测 |
|---|---|
| 索引体积 | 36,099,278 B（1,203 B/轨）；dict 124,574 / dictIndex 2,784 / postings 14,559,098 / positions 16,965,520（47.0%）/ docstore 4,444,824 / 垃圾 2,478 |
| 构建 | 1.52 s，峰值 RSS 118.7 MB（含 `ConserveMemory=9` + `Gen0Size=2 MB`） |
| open | 冷 4.00 ms / 热 0.06 ms / 首查 1.43 ms |
| 查询（仅存储层） | 28,600 次；pooled P50 0.08 ms / P95 0.37 ms；literal-exact 0.01/0.02、literal-prefix 0.23/0.61、meta-pinyin-phrase 0.35/0.44、lyrics-pinyin 0.22/0.28、initials 0.08/0.10、truncated 0.04/0.05 |
| 合并 | 2 段 / 死行 3.33% → 0.00%，0.92 s，写出 10,792,365 B |
| verify | ok，25 段，29,000 doc（合并后） |

> 与旧 quick 数字（7,788,704 B / 121.8 MB / positions 57.2%）相比：体积 −20%、RSS −20%。
> 主要来自三项改动：`PostingsWriter` 复用块缓冲 + 并行数组入口、`SegmentAccumulator`
> 紧凑累加器（直接摊进倒排，无 per-doc 分组字典）、V2 派生转写层免位置
> （`NoPositionFields = {lyricsRomanization, pinyinLyrics}`）。

**已知限制（S3 排期必须处理）**

1. **full（50k 长歌词）仍未跑**：30k 用的是 10–20 行短歌词；`PROFILE=full` 是 20–40 行 × 12 层全开的最坏情况，受仓库红线约束须分段执行、显式 `ALLOW_LONG_BENCH=1`，尚无官方数字。RSS/体积/段数都要按 full 重新校准。
2. **`Manifest.MaxSections = 64` 是硬顶**：每段写 5 个 section（DocStore / Dict / DictIndex / Postings / Positions）⇒ **最多 12 段**。写入侧不校验、读取侧 `Manifest.Parse` 超限抛 `InvalidDataException`，即**超限会写出打不开的索引**。`MaxSegmentBytes = 32 MB` 与默认 `DocsPerSegment = 16,000` 先到先得：长歌词 full 语料下每 doc 内存估算更大，段数可能逼近 12。S3 必须三选一：按已知总数自适应段大小（如 `min(DocsPerSegment, ceil(total/8))`）、把 Dict+DictIndex 合并为 4 段/段（⇒16 段），或 format v2 提高 `MaxSections`；并在 Builder 侧加**构建期硬校验**，宁可构建失败也不产出不可读索引。
3. **positions 仍占 46–47%**：已对两条派生转写层免位置，剩余位置块来自歌词原文 / 译文 / 首字母等。若要继续压体积，下一步是让 `initialsLyrics` / `pinyinMeta` 也免位置（代价同样是短语查询降级为 AND）。
4. **`NoPositionFields` 的语义必须写进 explain**：无位置字段上 `Positions()` 返回空，`PhraseIter` **降级为纯交集（AND）并置 `Positioned=false`**（不是"不匹配"）。查询层必须在 `explain` 中标注，否则用户会以为短语精确命中。
5. **GC 配置只在 bench 生效**：`System.GC.ConserveMemory=9` / `Gen0Size=2 MB` 写在本 bench 的 `RuntimeHostConfigurationOption`；生产 NativeAOT 产物是否等效需在 S3 用真实产物复核（不复核则 30k RSS 回落到约 154 MB）。
6. **体积比值尚不可判**：1,240 / 1,203 B/轨是合成语料数字，不能与现网 SQLite 382.5 MB / 30k（≈13.1 KB/轨）直接相除；≤0.75× 只能用 S0 的同语料同查询集基线判定。
7. **差分只到存储层**：P9 / T2.14 比的是段内文档序列；桶序、打分、top-k 游标属 S3 的 Ranker（§3.6.4），当前没有语义等价基线。
8. **合并是整段重写**：垃圾回收靠段级重写，尚无 T3.10 的分层合并（死行 ≥20% / 段数 ≥8 触发）。
9. **纯构建无 Stats / Tombs**：footprint 中 tombs / stats 为 0 属预期，只有合并 / 增量路径才写出。

**对 S3 的直接结论**

- 格式与核心读写路径风险已清零：写 → 读 → 查 → 合并 → 校验全链路可在 5k / 30k 语料上确定性复现（PASS 956，quick 97.2 MB、30k 118.7 MB）。
- 进入 S3 前必须补四件工程项：**段数/段字节硬校验**（`MaxSections=64` 不可写出超限索引）、**full 档分段实测**（长歌词最坏情况）、**NativeAOT 下 GC 配置复核**、**S0 同语料基线**（判比值）。
- 其余（方案层、排序、高亮下沉）按 §5.0 的 S1 / S2 / S2.5 顺序推进，不必回头改格式。

### 5.4 S3 切换与重建计划（v7 → EraStore）

> 原则：**只重建，不迁移**。v7 索引不读、不转换、不双写；切换当天的最坏情况是"索引不可用，
> 搜索退回 `LIKE`"，而不是"读到两代语义混在一起"。

#### 5.4.1 触发与前置

| 触发 | 行为 | 用户可见 |
|---|---|---|
| 首次启用索引 | 全量构建 | 进度条；构建期用 `fallback` |
| 版本锚变化（normalizer / tokenizer / options / data_pack / format） | 删除旧索引 + 全量构建 | 同左 |
| 打开时 CRC / Manifest 损坏 | 归档一份 + 全量构建 | 同左 |
| 垃圾比例 >30% | 后台全量重建（回收空间） | 无感，完成后切 `gen` |
| 用户点「重建索引」 | 全量构建 | 进度条 |
| 索引目录不存在 / 被删 | 全量构建 | 同左 |

前置（缺一不可）：

1. S2.7 的 P0–P11 完成，§8.0 指标在 5k / 30k / 48.7k 三档都达标；
2. 附录 D.8 契约用例 C01–C24 全绿；
3. 三档语料各有 `baseline-<commit>.json`，且 `queries_hash` 一致；
4. 打包产物包含 EraStore 代码，且 `era_search_api_version()==2`；
5. 设置页能展示锚、`gen`、`live/file/garbage` 字节。

#### 5.4.2 48.7k 首次构建的执行策略

| 项 | 策略 |
|---|---|
| 批大小 | 512 曲/批；4 个分析 worker，1 个写者 |
| 段大小 | 16k doc 或 64MB，先到为准 |
| 提交 | **只在收尾提交一次**（全量构建不逐批提交） |
| 进度 | 每 50 曲或阶段切换回调一次；`phase=scan/analyze/write/commit` |
| 优先级 | worker 用低优先级线程；每批 `yield`，不长时间占满 CPU |
| 中断 | 不续传；取消/退出即删工作文件，下次从头 |
| 时间目标 | ≤ **1.2×** 现状构建耗时（48.7k 参考 ≈109s → 目标 ≤130s） |
| 内存目标 | 峰值 RSS ≤ **120MB**；超了先降批大小，不降功能 |

构建期间：

- 搜索**照常可用**，走 `era_search_fallback`，UI 标"基础搜索"；
- 播放、扫描、刮削不受影响；写锁只阻止第二个索引写任务；
- 应用退出 → 工作文件删除；下次启动重新构建。

#### 5.4.3 磁盘、内存与电量

| 资源 | 规则 |
|---|---|
| 磁盘 | 启动前要求可用空间 ≥ **3× 预估索引体积**（工作文件 + 旧世代 + 临时）；不足 → 码 5，保持 `LIKE` |
| 索引体积预估 | 用 `docs × 6KB` 粗估（48.7k ≈ 300MB → 要求 ≥900MB） |
| 内存 | 构建 120MB、查询 60MB 为硬门禁；32 位进程另行降级块缓存 |
| 电量 | 笔记本电量 <20% 且未充电 → 建议延后（可设置覆盖）；不强制 |
| 温度 / 负载 | 连续构建超过 5 分钟且 CPU 100% → 暂停 30s 再继续（避免风扇狂转） |
| 网络盘 | 检测到 mmap/排它锁不可用 → 禁用索引，保持 `LIKE` |

#### 5.4.4 Go / No-Go 门禁

切换前必须在 **5k / 30k / 48.7k** 三档各跑一遍，全部满足才允许进正式包：

| 指标 | 门槛 |
|---|---|
| `bytes_per_track` | ≤0.80×（目标 ≤0.75×） |
| `open_ms` | ≤0.5× 且 ≤10ms |
| `first_query_ms` | ≤0.6× |
| `query_p50` | ≤0.35× |
| `query_p95` | ≤0.50× |
| `rss_peak_build` | ≤0.8× |
| `rss_query` | ≤60MB |
| 构建耗时 | ≤1.2× 现状 |
| 错误数 | 0（单曲分析错误按阈值） |
| 截断歌词 | 仅超护栏记录，且已打点 |
| 回放 | `overlap@10` / NDCG 不降；负例全绿 |
| 契约 | C01–C24 全绿 |
| 确定性 | 同输入连续构建两次，`verify` 通过且查询 top-20 一致 |

#### 5.4.5 失败与降级

| 失败 | 行为 | 恢复 |
|---|---|---|
| 构建中取消 / 退出 | 删工作文件；`CURRENT` 不变 | 下次启动重试 |
| 磁盘满 | 中止、删工作文件；码 5 | 用户清理后重试 |
| 单曲分析异常 | 跳过并计数；>1% 或 >100 曲中止 | 修数据后重试 |
| Manifest / 槽写失败 | 中止；旧槽有效 | 重试 |
| `CURRENT` 替换失败 | 旧世代继续服务 | 下次重试 |
| 打开时损坏 | 归档一份 + 重建 | 自动 |
| 两个实例 | 写锁 → 第二个只读 | 等第一个完成 |

**没有"切回 v7"这条路**：索引不可用时只降级到 `LIKE`；v7 代码在 S3 已删除。

#### 5.4.6 切换日清单

| 阶段 | 动作 |
|---|---|
| T-7d | 内测通道开索引；跑 5k/30k/48.7k；收集 `health` 与回放 |
| T-3d | 冻结查询集；生成三档 baseline；确认磁盘空间 |
| T-1d | 在 48.7k 库上连续构建 3 次；`verify` + top-20 一致 |
| T-0 | 合并 S3；正式包默认**不自动开启**，由用户在设置页开启 |
| T-0 | 开启后观察：构建成功、`open`、P50/P95、空结果率、`fallback` 比例 |
| T+1d | 内测扩大；检查错误码分布（1/3/5/8 应为 0） |
| T+7d | 无异常 → 保持默认；有异常 → 发修复版（不提供运行时回退） |

#### 5.4.7 切换后 7 天观察

本地记录（不上传）：

| 指标 | 观察点 |
|---|---|
| 构建成功率 | 应为 100%；失败要能复现 |
| `open_ms` / 首查 | 冷启动是否明显变慢 |
| P50 / P95 | 是否出现长尾（`partial` 比例） |
| 空结果率 | 分 tag 对比切换前 |
| `fallback` 比例 | 是否长期降级（说明构建/锁有问题） |
| 错误码 | 1/3/5/8 应接近 0；4 偶发可接受 |
| 索引体积 | `live` 与 `file` 差值（垃圾比例） |

#### 5.4.8 重建触发汇总

| 条件 | 范围 | 备注 |
|---|---|---|
| 锚变化 | 全量 | 不做局部迁移 |
| 格式升级 | 全量 | 旧文件直接作废 |
| CRC / Manifest 损坏 | 全量 | 归档一份 |
| 垃圾 >30% | 全量 | 回收空间 |
| 用户手动 | 全量 | 进度可见 |
| 段数 >64 | 强制合并或重建 | 优先合并 |

### 5.5 S4 数据包与跨语言治理

数据包（`data/`）不是"随手放的 tsv"，它是**索引内容的一部分**：包一变，`data_pack_id` 就变，
索引必须重建。所以先把治理规则定死，再谈词表。

#### 5.5.1 布局与 `data_pack_id`

```text
app/core/indexer/data/
├─ manifest.json
├─ nfkc.tsv
├─ accent_fold.tsv
├─ variant_fold.tsv
├─ kana_romaji.tsv
├─ ja_readings.tsv
└─ lexicon_cross.tsv        # S4 才启用
```

```jsonc
// manifest.json
{
  "schema": 1,
  "name": "archoera-search-data",
  "version": "1.0.0",
  "unicode": "16.0.0",
  "generator": "gen_tables@1",
  "files": [
    { "name": "nfkc.tsv", "sha256": "…", "bytes": 12345, "license": "Unicode" }
  ]
}
```

- `data_pack_id = "sha256:" + sha256(规范化后的 manifest，去掉 id 字段)`；
- 规范化：文件按 `name` 排序、字段顺序固定、UTF-8、LF、无尾随空白；
- 索引构建时把 `data_pack_id` 写进 Manifest；运行期只比对这个字符串；
- 全量校验（逐文件 sha256）只在设置页「校验数据」或 `era-index verify --data` 执行。

#### 5.5.2 版本与重建

| 变更 | 动作 |
|---|---|
| 任一 tsv 内容变化 | 生成新 manifest → 新 `data_pack_id` → 索引标记失效 → 全量重建 |
| 生成脚本变化但输出字节不变 | id 不变，不重建 |
| Unicode 版本升级 | 视为内容变化，必须重建；发布说明写明 |
| 新增文件但未使用 | 不影响 id 里的语义，但仍进 manifest；若影响能力则升 id |
| 用户手动替换数据包 | 不支持；启动校验失败 → 拒绝启用并提示重装 |

**不允许**"偷偷换词表但复用旧 id"——那会让索引与查询用的表不一致，属于最难排查的一类错误。

#### 5.5.3 构建期 / 运行期校验

构建期（生成脚本与索引器启动各一次）：

1. manifest 存在、schema 版本已知；
2. 每个文件存在、`bytes` / `sha256` 匹配；
3. TSV：UTF-8、无重复键、列数固定、单行 ≤4KB、总行数 ≤上限；
4. 不允许空 term、不允许控制字符、不允许 `\t` / `\n` 出现在字段内。

运行期：

- 只比对 `DATA_PACK_ID`（编译期常量）与 `Manifest.data_pack_id`；
- 不一致 → 索引作废重建；不尝试读旧数据；
- 数据包文件缺失 / 校验失败 → **不崩溃**：降级能力（见 5.5.6），并给出可操作提示。

#### 5.5.4 跨语言词表准入（`lexicon_cross.tsv`）

列：`source_term \t target_term \t weight \t source \t license \t note`。

准入规则：

| 规则 | 说明 |
|---|---|
| 只收高置信对 | 专名 / 名词优先；不收虚词、代词、泛义动词 |
| 单字禁止 | 单个汉字不做跨语言展开（噪声不可控） |
| 变体上限 | 每个 source 最多 4 个 target，按 weight 降序 |
| 权重 | 0.50–1.00；跨语言步永远落最低桶，权重只影响桶内 |
| 负例强制 | 每 100 条至少 2 条"必须搜不到"的负例进 `tests/vectors/negative` |
| 来源可追溯 | 每行必须有 `source` 与 `license`；无来源的行直接拒绝 |
| 人工复核 | 抽样 ≥10%，由第二名评审签字；争议条目退回 |

#### 5.5.5 许可证与评审

允许：**CC0 / CC-BY / Unicode License / Apache-2.0 / MIT / 自建**。
不允许：GPL 系数据、NC（禁商用）、ND（禁改）——与本仓库 AGPL 分发和"可修改"冲突。

- 每个数据文件在 manifest 里写 `license`，并在 `docs/acknowledgements.md`、`THIRD-PARTY-NOTICES.md` 登记；
- CC-BY 必须保留署名与链接；
- 自建词表需在 PR 里说明来源与抽取方法，不接受"网上找的"；
- 词表 PR 必须附：条数、去重后条数、抽样 20 条、负例、许可证。

#### 5.5.6 缺失与降级

| 缺失 | 影响 | 行为 |
|---|---|---|
| `lexicon_cross.tsv` | 跨语言步不可用 | `capabilities` 去掉 `cross_lingual`；`recall@1` 降级；不报错 |
| `ja_readings.tsv` | 日文读音步不可用 | 去掉 `readings`；日文歌仍可按假名 / 罗马字字面搜 |
| `nfkc` / `accent_fold` | 归一化不完整 | 拒绝构建（这是基础表，不允许缺） |
| 整个 `data/` 缺失 | 只能 ASCII 折叠 | 禁用索引，走 `LIKE`；提示重装 |

降级必须出现在 `explain` 与健康页（`missing` 列表），不能静默少召回。

### 5.6 LRC 分层置信度（T4.2）

歌词有三个来源：内嵌（`tracks.lyrics`）、LRC 侧车（原文 / 译文 / 音译）、用户编辑。
它们质量差异很大，不能一视同仁地进 `scope=all`。目标是**低置信层不污染全局搜索，但仍可在
`scope=lyrics` 里显式搜到**。

#### 5.6.1 层的定义

| 层 | 来源 | 说明 |
|---|---|---|
| `lyrics_embedded` | `tracks.lyrics` | 无时间轴；可能是纯文本或带结构的 LRC 字符串 |
| `lyrics_lrc` | `.lrc` 侧车 | 有时间轴；原文 |
| `lyrics_lrc_trans` | `.lrc` 译文侧车 | 与原文时间轴对齐才可信 |
| `lyrics_lrc_romaji` | `.lrc` 音译侧车 | **仅显式文件**；不自动生成 |
| `lyrics_user` | 用户编辑 | 置信度最高，但量少 |

**同一层只保留一份**：内嵌与 LRC 原文同时存在时，按置信度取高者；不双写、不拼接。

#### 5.6.2 置信度打分（0–8 分）

| 信号 | 分值 |
|---|---|
| ≥80% 行有合法时间戳 | +2 |
| 50–80% 行有合法时间戳 | +1 |
| 时间戳单调递增 | +1 |
| 原文 ≥5 行 | +1 |
| 译文与原文时间戳对齐 ≥80% | +2 |
| 侧车文件（显式命名） | +1 |
| 用户编辑 | +2 |
| 重复时间戳 / 乱序严重 | −1 |
| 替换字符 `U+FFFD` >1% | −2 |
| 单行 >500 字符（疑似正文） | −1 |

阈值：**≥5 高**、**3–4 中**、**≤2 低**。

#### 5.6.3 置信度如何影响检索

| 置信度 | `scope=all` | `scope=lyrics` | 读音（拼音） | 跨语言 |
|---|---|---|---|---|
| 高 | 参与，落歌词桶 | 参与 | 参与 | 参与 |
| 中 | 参与，落歌词桶 | 参与 | 参与 | **不参与** |
| 低 | **不参与** | 参与 | 参与 | 不参与 |

- 歌词永远不会产生 `title` / `exact` 桶；"低置信层不参与标题档"在这里落成"不参与 `scope=all`"；
- 低置信层仍写入索引（否则 `scope=lyrics` 搜不到），只是查询期按 `flags` 过滤；
- `flags` 每层 2 bit（高/中/低/无），DocStore 里只占 6 bit；
- `explain.layer` 与 `explain.confidence` 必须可见；`trace` 记录被过滤的层。

#### 5.6.4 LRC 解析规则

- 时间标签：`[mm:ss.xx]` / `[mm:ss.xxx]`；一行多标签 → 展开为多行；
- 元数据标签（`[ti:]` / `[ar:]` / `[al:]` / `[by:]` / `[offset:]`）忽略，不参与检索；
- enhanced LRC 的 `<mm:ss.xx>` 词级时间戳**忽略**（只用于播放，不用于搜索）；
- 空行、纯装饰行（`---`、`***`）丢弃；
- 编码：UTF-8 优先，失败尝试 UTF-8 with BOM / GB18030；替换字符 >1% 直接降为低置信；
- 行级去重与长度护栏仍按 §8.1.1 执行。

#### 5.6.5 译文与音译的配对

| 条件 | 结论 |
|---|---|
| 文件名后缀 `_trans` / `.trans.lrc` 且时间戳集合与原文一致 | 高置信译文 |
| 文件名后缀匹配但时间戳只对齐 ≥80% | 中置信译文 |
| 行数一致但时间戳缺失 | 中置信（无时间轴） |
| 无显式配对 | 不猜；不索引为译文 |
| 音译 | 只认显式文件；不从拼音反推 |

**绝不机器翻译**、绝不把原文复制成"译文"来凑层数。

#### 5.6.6 存储、版本与诊断

- `layer_confidence` 属于文档内容 → 进 `rev`；变化必须重建该曲；
- 解析规则变化 → 递增 `tokenizer_version`（属于"切分产物"）；
- 健康页显示各置信度的曲目数：`high / medium / low / none`；
- 回放对比时按置信度分组，避免"低置信层噪声"掩盖真实回归。

---

## 6. 评测与验收基线

### 6.1 golden vectors（三层，逐层断言）

现状 `tests/vectors` 只覆盖"分析器输出字符串"。重写后要覆盖**结构**，否则存储布局一变
测试就得重抄：

```jsonc
// vectors/plan/0001.json
{ "input": { "query": "周杰伦 晴天", "scope": "all" },
  "expect": {
    "terms": ["周杰伦", "晴天"],
    "minMatch": 2,
    "steps": [
      { "kind": "exact", "field": "meta", "fieldId": "0x01-0x04", "op": "term" },
      { "kind": "prefix", "field": "meta", "fieldId": "0x01-0x04", "op": "prefix" }
    ] } }

// vectors/rank/0001.json
{ "input": { "query": "晴天",
             "candidates": [ {"docId": 1, "kind": "prefix", "field": "title", "coverage": 1.0, "bm25": -3.2},
                             {"docId": 2, "kind": "exact",  "field": "lyrics","coverage": 1.0, "bm25": -9.9} ] },
  "expect": { "order": [2, 1] } }   // exact 跨字段压过 prefix
```

必覆盖的向量族：

| 族 | 条数下限 | 说明 |
|---|---|---|
| 归一化 | 40 | NFKC / 重音 / 繁简 / 异体 / 全角 / 孤立代理 / 幂等 |
| 切分 | 30 | 单字 / 两字 / 长 CJK / 假名 / 谚文 / 中英混排 / 数字 / 标点 |
| 计划 | 40 | 每类查询形状 × **三种 scope** × 库画像（含日韩 / 纯英文 / 纯中文） |
| 桶序 | 30 | 10 个 kind 的偏序关系 + 跨字段 + 覆盖度 + 平局 |
| 方案 | 20 | 每个 scheme 一份：`fast@1` / `default@1` / `recall@1` / `minimal@1` |
| 容错 | 20 | `typo1`（≥4 字）/ `typo2`（≥7 字）/ 首字符不纠错 / CJK 不纠错 |
| 负例 | 20 | 见 6.2 |

**唯一实现**（C#）跑全部向量；Dart 只消费结果，不再实现同类算法，
因此不存在"第二份实现改向量"的路径。

### 6.2 负例集（"不该找到的"必须找不到）

| # | 语料构造 | 查询 | 必须 |
|---|---|---|---|
| N1 | 全库无「痛」字 | `痛` | 返回空 |
| N2 | 有「恨」无「爱」 | `爱` | 不返回「恨」 |
| N3 | 仅有单字母标题 `A`/`I` | `爱` | 不返回它们 |
| N4 | 整行歌词只有「啊」 | `爱` | 不因首字母整个库命中 |
| N5 | 两字查询无命中 | `爱情` | 不返回只含「爱」或「情」的曲目 |
| N6 | 纯英文库 | 任意单汉字 | 空（除非显式开启跨语言且有策展命中） |
| N7 | 多词部分命中 | `夜 雨 恨` | 按 `minMatch` 返回，不整库放宽 |
| N8 | 标题 `Cover` 版本修饰 | `晴天` | 不把 `Cover` 当正向词 |
| N9 | 文件名含年份 `2024` | `爱` | 不因文件名数字命中 |
| N10 | 日文汉字标题（无假名） | 中文拼音 | 不误判为中文读音（或按策略显式标注） |
| N11 | 歌词仅译文含目标词 | `爱` | 可召回但必须 `field=lyricsTr` 且排在标题命中之后 |
| N12 | 索引里残留已删除曲目 | 任意 | 结果侧过滤，不出现"幽灵曲目" |
| N13 | 查询含查询元字符 | `a" OR b*` | 作为普通字符进入词元，不改变语义、不报错 |
| N14 | 空 / 纯空白 | `"   "` | 空结果，零查询 |
| N15 | 超长查询 | 200 码点 | 截断 + 只跑字面步，不超时 |
| N16 | 只有 emoji | `🎵🎵` | 空结果 |
| N17 | 单字母首字母输入 | `z` | 不展开首字母支（只按字面/拉丁处理） |
| N18 | 高频词 | `的` | 不因 prefix / phonetic 扫全库；可返回但受预算裁剪 |
| N19 | 索引版本不符 | 任意 | 降级 LIKE，不抛错 |
| N20 | 构建进行中 | 任意 | 读旧快照，结果完整 |
| N21 | `scope=lyrics` 但目标词只在标题 | 任意 | 返回空；不得因降级成 `all` 而返回 |
| N22 | 3 字符拉丁词 + `recall@1` | `cat` | **不**启用 `typo2`（词长 <7）；不得命中 `cart` / `cut` 之类 |
| N23 | 7+ 字符拉丁词 + `recall@1` | `beautiful` | 允许 2 错，但首字符错仍不生成变体 |
| N24 | `scope=lyrics` + 单 CJK 字 | `爱` | 空结果（不是"只查歌词"的整库命中） |

每条断言**同时**检查：结果集合（不得包含谁）与 `explain.kind`（不得来自哪一档）。

### 6.3 回放 harness 契约

输入（一行一条，脱敏）：

```jsonl
{"q":"周杰伦 晴天","scope":"all","src":"desktop","ts":"2026-11-01T12:00:00Z","count":128}
{ "q": "woaini", "scope": "all", "src": "desktop", "count": 4534 }
```

输出（每条查询一个对象）：

```jsonc
{"q":"周杰伦 晴天",
 "before":{"top":[{"id":"a1","kind":null,"pass":"lexical"}, …],"total":128},
 "after": {"top":[{"id":"a1","kind":"exact","field":"title"}, …],"total":128},
 "changed":[{"id":"x9","from":37,"to":4,"why":"kind=prefix→exact"}],
 "metrics":{"overlap@10":1.0,"ndcgDelta":0.012,"msBefore":31.2,"msAfter":8.4}}
```

> `before` 来自 **S0/S1 固化的 baseline JSON**，不是在同一个进程里并行跑旧实现
> （旧代码已在 S1 删除）。这也是"完全不回退"下仍能对比的原因：基线是数据，不是代码路径。

用途与门限：

| 阶段 | 门限 |
|---|---|
| S1 | `changed` 必须为空（等价） |
| S2 | `overlap@10 ≥ 0.7`；所有变化必须能归到 kind/field；`ndcgDelta ≥ -0.01` |
| S3 | `overlap@10 ≥ 0.8`；P50 改善 ≥ 30%；无负例回归 |

### 6.4 qbench 集成（不新增 CI 步骤）

- 复用 `app/core/indexer/bench/qbench`，新增 `--replay` 模式（读上节 JSONL）；
- 指标口径保持现状（P50/P95、P@k、R@k、MRR、NDCG、体积、构建耗时）；
- 新增 §8.5 的 `footprint.json` / `startup.json`，并输出与 S0 基线的逐项比值；
- **CI 只编译不跑测试**（仓库硬性约定），评测一律本地跑：
  日常 `PROFILE=quick`，正式基线需显式 `ALLOW_LONG_BENCH=1`，且不要连续满负载。

### 6.5 确定性

同一查询在同一 `(gen, scheme)` 下重复 10 次、跨进程、跨分页请求，top-k 必须完全一致。
不允许出现"靠 `bm25` 浮点差异 + 不稳定排序"导致的顺序抖动——第 7 键（`doc_id`）就是为此存在。

### 6.6 测试矩阵（本地跑，不进 CI）

| 层 | 用例 | 断言 |
|---|---|---|
| 单元 | `Normalize` 幂等、`rev` 确定性、元字符按字面处理、varint / CRC 往返、方案环检测 | 同输入同输出；不抛异常 |
| 属性 / 模糊 | 随机 Unicode（含非 BMP / 孤立代理）、200 码点查询、全角/组合音标 | 不崩溃；不误判脚本；偏移不越界 |
| 集成 | 构建 → 查询 → 增量 → 删除 → 全量重建；旧索引启动即删 | 每步结果符合 §6.2 负例 |
| 故障注入 | 构建中 `kill -9`、磁盘满、只读目录、损坏文件、两进程争锁、rename 被占用 | 不产生半写；降级 `LIKE`；下次能自愈 |
| 存储引擎 | 词典 / 倒排 / 位置编解码往返；截断与翻转字节；三阶段 kill；O1–O10 flag 两两组合 | 不崩溃、不越界；flag 组合下 top-20 逐条一致 |
| 跨平台 | Windows / macOS / Linux；路径含空格 / 中文 / emoji；网络盘 | 锁与 rename 行为符合 §3.8.7 |
| 契约 | JSON Schema（请求/响应/错误）；`highlight` UTF-16 偏移（含 emoji） | Schema 通过；Dart 端切片正确 |
| 资源 | §8 的 footprint / startup / latency 三份 JSON | 达 §8.0 比值 |
| 内存口径 | `rss_query` 必须报**匿名 RSS**（`/proc/self/smaps_rollup` 的 `Anonymous`，或 GetProcessMemoryInfo 的 `PrivateUsage`）；file-backed mmap 另报 `rss_file` | 不用包含 mmap 页的总 RSS 冒充“内存占用” |

> 仓库约定：测试只在本地跑，CI 只做编译与打包；但这些用例必须能在本地一条命令跑完，
> 否则"没有回退"的承诺就没有安全网。

指标口径要**相对化**：`df/N` 而不是绝对文档数；这样 400 首合成库与 48k 真实库的结论不再相反。

### 6.7 回放 harness 的数据格式（S0 固化，S1/S2 复用）

回放集有三个用途：**S0 基线**、**S1 等价性**、**S2 排序评审**。三者用同一份查询与同一份结果格式。

#### 6.7.1 查询集

```jsonc
// bench/.work/queries.jsonl
{ "schema": 1, "id": "Q0001", "query": "晴天", "scope": "all",
  "limit": 20, "total": "none", "source": "fixed", "weight": 1,
  "tags": ["cjk", "title"] }
```

| 字段 | 说明 |
|---|---|
| `id` | 稳定主键；一旦进入基线**不可复用** |
| `source` | `fixed`（14 条固定） / `replay`（日志抽样） / `synthetic`（负例） |
| `tags` | `cjk` / `latin` / `mixed` / `lyrics` / `single-char` / `typo` / `empty` |
| `weight` | 聚合指标用；默认 1 |

- 固定 14 条 + 回放 200 条，两组分开报；
- 回放集按 `(query, scope)` 去重，按频次加权抽 200 条；
- 丢弃空串、纯空白、长度 >200 的查询；
- **冻结后不许为了"结果好看"改查询**；新增查询必须新 id，旧基线保留。

#### 6.7.2 日志来源与脱敏

桌面端现有 `query_log.dart` 只写本地 JSONL：

```jsonc
{ "ts": 1767225600000, "query": "晴天", "scope": "all",
  "resultCount": 12, "clickedTrack": "…", "gen": 12 }
```

- **不写入**：文件路径、设备 id、用户名、IP、播放历史；
- 查询文本属于用户数据：只留在 `bench/.work/`，`.gitignore` 忽略，**绝不入库 / 上传**；
- 导出回放集必须是用户显式操作（设置页「导出搜索诊断」），默认关闭；
- 仓库里只放**合成样例**与 schema，不放真实查询。

#### 6.7.3 语料

- 真实库固定 5k 子集：按 `sha256(track_id)` 升序取前 5k，保证可复现；
- `corpus_hash = sha256(sorted(track_ids))`；hash 变化 → 必须重新基线；
- 另加 400 首合成语料（含 CJK / 拉丁 / 假名 / 谚文 / emoji 标题）；
- 语料元数据不入库，仓库只放**生成脚本**与 hash。

#### 6.7.4 基线文件

```jsonc
// bench/.work/baseline-<commit>.json
{
  "schema": 1,
  "commit": "…",
  "builtAt": "2026-11-23T12:00:00Z",
  "corpus": { "kind": "real-5k", "tracks": 5000, "hash": "…" },
  "queries": { "count": 214, "hash": "…" },
  "options": { "scheme": "default@1", "lyricsPinyin": true, "lyricsInitials": false },
  "index": { "fileBytes": 42000000, "liveBytes": 40000000,
             "buildMs": 9500, "openMs": 8.2 },
  "results": {
    "Q0001": { "ids": ["…", "…"], "total": -1, "p50Ms": 2.1 }
  }
}
```

- `results.ids` 只存**前 100**（足够算 overlap@10 / NDCG@10，也不至于文件过大）；
- 基线文件只在本机；PR 里贴**聚合指标**与 diff 摘要，不贴用户查询；
- 同一 `commit + corpus_hash + queries_hash + options` 必须复现同一份 `ids`。

#### 6.7.5 diff 报告

```jsonc
{
  "id": "Q0001",
  "query": "晴天",
  "oldTop": ["a", "b", "c"],
  "newTop": ["b", "a", "c"],
  "overlap@10": 0.9,
  "ndcg@10": 0.81,
  "emptyOld": false,
  "emptyNew": false,
  "p50Old": 30.2,
  "p50New": 2.1,
  "deltas": [ { "rank": 1, "old": "a", "new": "b", "why": "exact > prefix" } ]
}
```

聚合：

| 指标 | 定义 |
|---|---|
| `overlap@10` | `|A₁₀ ∩ B₁₀| / 10` |
| `NDCG@10` | 有点击标签时用点击做相关度；无标签时只报 overlap |
| `emptyRate` | 空结果查询占比（分 tag） |
| `P50 / P95` | 只统计成功响应；超时 / 错误单列 |

#### 6.7.6 运行与纪律

```bash
# 生成 5k 子集（本地；同时写出 <out>.corpus.json 供 --corpus-hash 使用）
dotnet run --project app/core/indexer -- replay corpus \
  --library library.db --tracks 5000 --out bench/.work/corpus-5k.db

# 回放（同语料、同查询集、同 options）
dotnet run --project app/core/indexer -- replay run \
  --index bench/.work/index --queries bench/.work/queries.jsonl \
  --out bench/.work/run-<commit>.json --corpus-hash <hex>
```

- **禁止**用回放集反向调 scheme 参数（过拟合）；它只用于评估；
- 每次 S3 子任务（V1–V7、O1–O10）都要给同一查询集的 before/after；
- 报告必须写 commit、corpus_hash、queries_hash、options，否则结果不可比。

---

## 7. 风险、检测与未决

| # | 风险 | 触发信号（怎么发现） | 缓解 / 处置 |
|---|---|---|---|
| R1 | 桶序改变用户熟悉的排序 | 回放 `overlap@10` 下跌；空结果率上升；用户反馈"搜歌名却排在后面" | **不设排序回退开关**；上线前用回放 + 负例 + 人工抽检定档；上线后只向前修 |
| R2 | 歌词拼音默认开后，读音步仍可能爆量 | `search.step.candidates{phonetic}` 超预算；P95 超标 | 受词典 df 预算 + Cap + 块跳过约束；歌词首字母默认已关（V4）；**不许关拼音** |
| R3 | DocStore 原文增大体积 | 体积增量 > 10 MB / 50k | 只存归一后的元数据（不存歌词）；列式 + 字典化压缩 |
| R4 | 首字母 bigram 增大倒排 | 段体积对比（`era-index stats`） | 只对元数据段生成；歌词段默认不生成 |
| R5 | 段哨兵泄漏进结果或词表 | `era-index health` 检查 `zzsepzz` 计数 / 词条 | 启动时断言；词典扫描跳过；查询编译器禁止生成 |
| R6 | EraStore 构建中断导致长时间无索引 | 构建失败日志；搜索长期走 LIKE | 未提交段自动忽略；自动重试；索引可丢弃，不阻塞播放 |
| R8 | 策展双语表数据/许可证未就绪 | S4 启动前检查清单 | 未就绪则 S4 不启动；跨语言能力保留在 T1 现状 |
| R9 | 增量 `rev` 在 scanner 侧未实现 | `rev` 列缺失 | 退回应用侧哈希（功能正确、成本高），记 warn；不阻塞 |
| R10 | 方案/参数调优又变成补丁堆 | `search.plan.steps` 持续上升；PR 只改常量不附回放；scheme 版本越来越多 | 已发布 scheme 不可变；改行为必须升 `@version` 并附回放 diff + 负例 |
| R11 | 搜索逻辑又漏回 Dart | PR 里出现 `pinyin` / `highlight` / `snippet` 等算法代码 | 代码评审清单 + 一条「Dart 不得 import 索引器数据表」的静态检查 |
| R12 | 全量重建把机器打满 | 构建耗时 / CPU 长时间 100% | 构建限速（分批 + 让出）；遵循仓库"禁止长时间满负载基准"红线 |

**已定，不再重开**（避免每次评审都翻一遍）：

- `passes` 在 v2 删除，诊断只保留 `explain` + `trace`；
- `scheme` 变更只影响缓存键，不重建索引；
- 首字母 bigram 与拼音用不同 `field_id`，必须有一条向量证明 `xz` 不会误命中拼音音节；
- 歌词拼音是独立字段族（`0x21`），与元数据读音分开；位置默认写，免位置是非默认优化。

**仍未决（不阻塞 S1）**：

1. `recall@1` 是否作为**正式 UI 入口**暴露（"深度搜索"），还是只在诊断 / 高级设置里；
2. 同曲多版本（remaster / live）是否做 `dedupe-versions@1`——需要曲目时长等额外字段；
3. S4 策展双语表的许可证与词表来源（未就绪则 S4 不启动）；
4. 若 §8 仍不达标，是否接受 V2（歌词拼音免位置）——需你确认，且默认不启用。

---

## 8. 硬指标：相对占用更低、启动与运行更快

> 这一节是**验收门禁**，不是"优化愿望"。所有数字都是**相对现状**的比值，
> 基线由 S0 在同一语料上测出（`PROFILE=quick` 用于日常，正式基线用 `ALLOW_LONG_BENCH=1` 分段跑）。

### 8.0 基线口径与总目标

| 指标 | 定义 | 现状（30k 基准语料） | 目标 |
|---|---|---|---|
| `bytes_per_track` | 全量重建后的索引文件字节 / 曲目数；同时报 `live_bytes` / `file_bytes` | ≈ 12.75 KB（382.5 MB） | **≤ 0.80×**（≈ ≤ 10.2 KB） |
| `open_ms` | `era_search_open` 返回耗时 | 未单独统计（含在首查里） | **≤ 0.5×**，且 ≤ 50 ms @50k |
| `first_query_ms` | open 之后第一条查询（冷缓存） | 含约 1.1 s 表懒加载 | **≤ 0.6×**，且 ≤ 100 ms @50k |
| `query_p50` | 常用查询池 P50 | 30.2 ms（拼音列开，LIMIT=10000） | **≤ 0.35×**（≈ ≤ 10 ms） |
| `query_p95` | 常用查询池 P95 | 69.7 ms | **≤ 0.5×**（≈ ≤ 35 ms） |
| `rss_peak_build` | 全量构建峰值 RSS | ≈ 181 MB @30k | **≤ 0.80×** |
| `rss_query` | 查询期**匿名 RSS**（堆 + JIT + 缓存；file-backed mmap 页单列 `rss_file`，不计入） | 未单独统计 | ≤ 60 MB |

> 附带约束：全量构建耗时不得比现状差 10% 以上（行/块去重的哈希成本应可忽略）；
> 若明显变慢，说明去重实现有问题，先修实现而不是放宽门禁。

**能力基线（先于数字）**：体积 / 延迟门禁一律按 `lyrics_pinyin=on` 且**音节级召回完整**计算；
"关掉歌词拼音"不是达标手段，也不作为默认值变更的借口。若用户主动关闭，只影响自身体验，
不影响门禁口径。

**三条不可谈判的约束**：

1. 体积只能**严格小于**现状；"持平"不算达标。
2. 启动与首查不得因为"多算法 / 多方案"变慢：`open` 阶段禁止任何全表扫描、全词表加载、全量哈希校验。
3. 延迟改善必须**逐条**成立，不允许用 pooled 摊平长尾（审计文档 §11 已证明 pooled 会掩盖 `wan` 这类 70 ms 的单条）。

### 8.1 体积削减手段（S3 必须落地，按收益排序）

> **功能不可用体积换**：`lyrics_pinyin`（歌词拼音）是**一等能力**，不许删除、不许默认关闭来达标。
> 下文 V1 / V3 / V5 / V6 / V7 严格无损；V2 是"词仍在、相邻要求放宽"的精度取舍，**非默认**，
> 只在其余无损手段仍不足时评估，并需用户确认。凡是要"少召回一部分歌词拼音"的方案，
> 一律不进默认配置。

| # | 手段 | 预计节省 | 召回影响 | 备注 |
|---|---|---|---|---|
| V1 | **歌词行级去重**（归一后相同的行只索引一次） | 歌词 token 的 20–40% | 无（重复行本来就不提供新词） | 副歌重复是主要来源；对原文 / 译文 / 拼音同时生效 |
| V2 | **歌词读音不存位置**（只 doc 级命中，只存 postings） | 歌词读音部分的 30–50% | **精度取舍**：多音节从短语（相邻）降为 AND（词仍在，只是不再要求相邻） | **非默认**：仅在其余无损手段仍不足时评估，需用户确认；元数据读音仍存位置 |
| V3 | **歌词列不建 unigram**（只 bigram） | ≈ 2 token/行 | 无（单字查询本来就不查歌词） | 与 `RequireMetadataForSingleChar` 一致 |
| V4 | **`initials_lyrics` 默认关**（`pinyin_lyrics` 始终保留且默认开） | 歌词派生列的 15–25% | 歌词**首字母**默认不可用（拼音不受影响） | 首字母是独立能力，不是拼音；设置项可随时开启 |
| V5 | `lyrics_rom` 仅在存在显式音译层时写入 | 小 | 无 | 现状已部分如此 |
| V6 | **病态数据护栏**（默认 4096 唯一行 / 128 KB **每层**；正常歌词不截断） | 仅挡异常数据 | 仅当真实分布超过护栏时才截断尾部 | 按层独立计数，译文 / 音译不挤占原文预算；触发必记指标 |
| V7 | DocStore 只存元数据原文，不存歌词 | 避免 +15 MB | 无 | 歌词片段查询期从 `library.db` 读 |

#### 8.1.1 歌词长度：按层独立、去重后计数、只挡病态数据

真实歌词的分布不是"几十行"：原文 p95 常在 150 行以上，叙事曲 / vocaloid / 长版可达 400–600 行；
带翻译时三层加起来轻松破千。**按总行数 256 截断会误伤正常曲目**，因此规则改成：

| 规则 | 说明 |
|---|---|
| **按层独立** | 原文 / 译文 / 音译各自计数，互不挤占；译文不会把原文的预算吃掉 |
| **去重后计数** | 先做 V1 行级去重，再判断是否超护栏；副歌重复不计入 |
| **默认不截断** | 正常歌词全量入索引；护栏只是"异常数据熔断" |
| **护栏默认值** | 4096 唯一行 / 128 KB **每层**（远高于真实 p99.9） |
| **触发可见** | 截断尾部 + `search.index.lyrics_truncated{layer}` 指标 + warn 日志；禁止静默 |
| **阈值可校准** | 用真实库的 p99.9 决定；若 p99.9 > 4096，**上调护栏**，而不是接受截断 |
| **译文独立** | 没有译文层就不写；不要用原文冒充译文，也不要因为「带翻译」而收紧原文 |
| **分层统计** | 各层去重率分别统计：译文的重复率通常低于原文，不共用 20–40% 这个比例 |

> 结论：体积不靠"砍歌词"，靠 V1 去重 + V3 去 unigram + V4 关首字母 + 存储编码；
> V2（歌词读音免位置）属精度取舍，非默认；V6 只在异常数据（例如整本小说被塞进歌词字段）时兜底。

预期合计（EraStore）：v1 编码 **0.55–0.75×**，加 O8（位打包）后 **0.45–0.60×**；
计入歌词拼音（默认开）后仍应 **≤0.75×**，门禁仍是 **≤0.80×**。
若只到 0.80×，优先加 V1 的实现质量（去重率）与 O1–O7，**不要**先关歌词拼音。

> 反过来说：**"拆表 / 换引擎"本身不省内容**（同一批 postings 换了个容器）。无损的体积来自去重、
> 编码与压缩；拆表 / 自研引擎买的是"主路径不扫读音列"的延迟与更紧凑的布局。两者都要，但不要混为一谈。

**若 V1–V7 仍不足以达到 0.80×，只在"无损"范围内加杠杆**：

| 顺序 | 杠杆 | 额外节省 | 是否无损 |
|---|---|---|---|
| L1 | `lyrics_rom_tok` 仅在存在显式音译层时写入 | 2–5% | ✅ 现状本就如此 |
| L2 | 词典前编码 + 块索引调优（块大小 / 前缀截断） | 3–8% | ✅ |
| L3 | 倒排块压缩（PForDelta / 位打包）替代纯 varint | 5–12% | ✅ |
| L4 | 元数据列式 + 字符串字典化（歌手 / 专辑重复度高） | 2–5% | ✅ |
| L5 | 更快 / 更频繁的合并，压低死行比例 | 2–5% | ✅ 只影响写放大 |

**明确拒绝**（会丢能力或明显降召回，不得作为达成手段）：

- 关闭 / 默认关闭歌词拼音；
- 只对副歌或前 N 行建歌词拼音；
- 把歌词拼音改成"整词合并"导致音节级召回消失；
- 对歌词拼音设行数上限（原文 / 译文 / 音译都不设业务上限）。

> 若上述无损手段仍达不到 0.80×，**由用户决定**是否接受某一项精度取舍；
> 实现方不得自行降级，也不得把"关功能"写进默认配置。

### 8.2 启动预算（open 阶段只做常数级工作）

| 阶段 | 允许做的事 | 禁止做的事 | 目标 |
|---|---|---|---|
| 动态库加载 | OS 映射 + NativeAOT 静态初始化 | 读段、建索引对象 | 由 OS 决定 |
| `era_search_open` | mmap Header + CommitSlot + Manifest；校验 CRC；读段列表与能力；分配句柄 | 遍历词典、解码倒排、全量哈希、扫描 DocStore | **≤ 10 ms @50k** |
| 数据包校验 | 比对二进制内嵌的 `DATA_PACK_ID` 与 `Manifest.data_pack_id` | 对每个 `.tsv` 做全量哈希 | O(1) |
| 方案解析 | 内存注册表查询 + 能力交集 | 动态反射 / 动态加载程序集 | < 1 ms |
| 首查 | 按需映射需要访问的段（mmap 页按需缺页） | 启动时预读整库 / 预加载读音表 | ≤ 30 ms @50k |

关键设计：

- **数据包身份而不是全量校验**：`manifest.json` 的 sha256 在**构建期**校验并写入索引；
  运行期只比对 `DATA_PACK_ID`。全量哈希放到设置页的显式「校验数据」动作。
- **懒加载按能力**：纯中文库永不加载日文读音表；`typo` 只在 `recall@1` 且首次使用时才扫词典。
- **`open` 只读一个 Manifest**：段列表、版本锚、能力、`gen` 都在里面；没有 schema 解析、没有连接池。

### 8.3 运行预算（逐条，不用 pooled 掩盖）

| 阶段 | 目标 P50 | 上限 P95 | 说明 |
|---|---|---|---|
| 计划编译（≤10 次词典二分） | 0.1 ms | 0.5 ms | `df` 在词典条目里，不读倒排 |
| `literal-exact`（1–2 步） | 1–2 ms | 6 ms | 块跳过 + 顺序解码 |
| `literal-prefix` | 1–2 ms | 5 ms | 仅末词；词典区间扫描 |
| `phonetic-*`（仅必要时） | 2–4 ms | 12 ms | 独立字段族 + Cap |
| DocStore 复核（≤2k 候选） | 0.2 ms | 1 ms | 列式块读，一次取回 |
| 排序（≤5k 候选） | 0.1 ms | 0.5 ms | 桶序 + 固定大小堆，不排全命中集 |
| **常见查询合计** | **≤ 5 ms** | **≤ 20 ms** | 不含首次 open |

三条运行期禁令：

1. **查询期不统计全库**：`df` 只从词典条目读；`total` 默认不计数。
2. **不对全命中集排序**：每步 Cap + 合并后总量上限，固定大小堆取 top-k。
3. **不加载全量词表**：纠错扫词典前缀区间，`rss_query` 与词表规模解耦。

只读配置（EraStore 自己控制，没有 SQL PRAGMA）：

- 整个索引文件 mmap；32 位 / 地址空间不足时退化为块读 + 64 MB 块缓存；
- 结果 LRU（默认 128 条）与纠错候选缓存分开，键都含 `gen`；
- 解码缓冲用 `ArrayPool<byte>`，迭代器是 `ref struct`，不产生每查询大对象；
- 实际 mmap / 缓存值写入健康报告，便于对照 §8 门禁。

### 8.4 模糊搜索的紧凑实现（同时解决体积、启动、内存）

现状 `LoadFuzzyVocabulary` 会把全量词表读进内存：50k 曲时可达数百万条，
启动慢、常驻内存大、且与"相对占用更低"直接冲突。

EraStore 不需要词表副本：**词典本身就是排好序的**。

- 查询 `helo` → 对每个变体前缀（`he` / `hl` / `el` / `eo` / `lo`）各做一次
  `lower_bound(field_id || prefix)`，扫 ≤2 个词典块（约 128 条），得到候选；
- 候选上跑有界 Damerau（长度差 ≤1，首字符替换不算），命中 ≤32 个变体；
- **不含歌词**：歌词拼写纠错是伪需求，去掉后体积与延迟都降一个量级；
- 只收 **ASCII 词**（`^[a-z0-9]+$`）：CJK 不做编辑距离纠错，扫进来只增延迟；
- 不需要 `vocab_meta` 表、不需要 `prefix2` 索引、启动零成本；
- 单次查询 O(log V + 桶大小)，桶大小由前缀长度决定（≥2 字符）。

### 8.5 验收方式

`bench/qbench` 增加三类输出（同一语料、同一查询集）：

| 输出 | 用途 |
|---|---|
| `footprint.json` | `bytes_per_track` / 各段体积（词典 / 倒排 / 位置 / DocStore / Stats / Tombstones）/ **垃圾字节（旧 Manifest / 待回收段）** / **各层字节** / 各字段族字节 |
| `startup.json` | `open_ms` / `first_query_ms` / 懒加载耗时（日文库 / 中文库各一组） |
| `latency.json` | 逐条 P50/P95 + pooled（只作参考，不作判据） |

门禁：

- S3 合并前必须给出与 S0 基线的**逐项比值**；任一项未达 §8.0 即不合并；
- 每次 S3 子任务（V1–V7）单独给出体积变化，避免"最后一并测发现没省"；
- 不允许用 `PROFILE=full` 之外的样本外推；正式基线必须同语料同查询集。

### 8.6 测量协议（防止"测出来更好"其实是口径变了）

| 项 | 规则 |
|---|---|
| 语料 | 30k 合成（`SEED=20261013`）+ 真实库子集（固定 5k，脱敏）；两组都要报 |
| 查询集 | 固定 14 条（`qbench` 现有）+ 回放集前 200 条；两组分开报 |
| 歌词分布 | 每层行数 p50/p95/p99/max + 去重率 + 护栏触发数；作为 V6 阈值依据 |
| 冷启动 | **新进程第一次** `era_search_open` / 第一条查询；不在同进程内重复测 |
| 热查询 | warmup 5 次、采样 60 次，取 P50/P95 |
| 体积 | 总文件大小（提交后）+ 各段字节（`era-index stats`） |
| 机器 | 同一台、同一电源模式、无并发构建；记录 CPU / 介质 |
| 基线 | S0 产出的 `baseline-<commit>.json` 留在本地 `bench/.work/`，报告里写 commit |
| 重复 | 每项 3 轮取中位数；单轮异常（>20% 偏差）必须复测 |

> 任何"优化"如果只改善了 pooled 而单条长尾没变好，按 §8.0 第 3 条判为**未达标**。

---

## 9. 配置项与设置页映射

| 配置键 | 默认 | 作用域 | 改它要重建吗 | 设置页位置 |
|---|---|---|---|---|
| `search.index.enabled` | 关 | 总开关 | 否 | 设置 → 搜索 → 本地曲库搜索索引 |
| `search.index.lrc` | 关 | 索引 `.lrc` 侧车 | 是（内容变） | 同上，「索引 .lrc 侧车歌词」 |
| `search.index.lyrics_pinyin` | **开** | 歌词拼音列 | 是（写列变） | 同上，「歌词拼音检索（关闭可省体积）」 |
| `search.index.lyrics_initials` | **关** | 歌词首字母列 | 是（写列变） | 同上，「歌词首字母检索（默认关）」 |
| `search.index.lyrics_guard_lines` | `4096` | 每层唯一行护栏（仅挡病态数据） | 否（下次构建生效） | 隐藏 |
| `search.index.lyrics_guard_bytes` | `131072` | 每层字节护栏 | 否（下次构建生效） | 隐藏 |
| `search.scheme` | `default@1` | 检索方案 | **否** | 隐藏（随版本） |
| `search.merge.interval_hours` | `24` | 合并最长间隔 | 否 | 隐藏 |
| `search.cache.capacity` | `128` | 结果缓存条数 | 否 | 隐藏 |

原则：**用户可见的设置项越少越好**。只暴露"是否启用 / 是否索引歌词 / 是否索引歌词拼音 / 歌词首字母"，
其余全部由 `scheme` 随版本走，避免用户调出"我自己也复现不了的搜索行为"。

以下**不是**配置项，不要做成设置：

- `explain` / `snippet` / `total`：属于**请求参数**，每次调用自己决定；
- `degradeTo`：属于方案定义（`SchemeRegistry`），用户不能改；
- 精确度 / 邻近度复核、单字只查元数据：属于方案策略，只能随版本调整；
- 候选预算：属于方案策略，不开放配置；
- `mmap` / 块缓存大小：默认按库大小与可用内存自适应（见附录 B），
  仅在诊断页显示实际值；内部 `options_json` 可覆盖，但**不作为用户设置暴露**。

**设置迁移**：旧键（如 `enableLocalSearchIndex`、`search.index.romaji_lyrics`）在升级时做一次性映射，
映射不到的键删除并记 info；不存在"读到旧键就按旧语义跑"的路径。

### 9.1 搜索 UI 状态机（构建 / 降级 / 分页 / 重启）

UI 只消费附录 D 的响应，不推断搜索语义；但必须把 `partial` / `restart` / `degraded` 表达清楚。

| 状态 | 触发 | 展示 |
|---|---|---|
| `idle` | 查询为空 | 最近搜索 / 提示；不发请求 |
| `debouncing` | 输入变化 | 保留旧结果，150ms 后发请求 |
| `loadingFirst` | 首次请求 | 骨架行；输入框仍可编辑 |
| `loadingMore` | 滚到底 + `next` 非空 | 列表尾部 loading；不阻塞其它交互 |
| `results` | 正常响应 | 列表 + 计数（按 `total` 语义） |
| `empty` | `items=[]` 且无错误 | "没有找到" + 清除筛选入口 |
| `degraded` | `scheme=fallback@1` | 列表 + "基础搜索（索引未就绪）" 标签 |
| `indexBuilding` | 健康状态 `absent/building` | 进度条 + 仍可用 `fallback` 搜索 |
| `partial` | `partial=true` | 结果保留 + 底部"结果可能不完整" |
| `error` | 错误码且 `fallback` 也失败 | 错误提示 + 重试按钮；不显示空列表冒充"无结果" |
| `restarting` | `restart=true` | 清空列表，按同一请求重查第一页 |

**转换规则**：

```text
输入变化 ──▶ debouncing ──150ms──▶ loadingFirst ──响应──▶ results / empty / degraded / partial
results ──滚动到底──▶ loadingMore ──响应──▶ results（追加）
results ──restart=true──▶ restarting ──▶ loadingFirst（同一查询，cursor=null）
任一状态 ──错误 2/3──▶ indexBuilding + degraded（trigger build）
indexBuilding ──构建完成回调──▶ 若查询非空，静默重跑第一页
```

**硬规则**：

1. **单请求**：只保留一个 in-flight；新输入使旧响应作废（按请求序号丢弃，不按耗时猜）；
2. **不改写查询**：不在 Dart 截断 / 去词 / 转拼音；查询原样送给 C#；
3. **中途不追加**：`restart=true` 必须清空并重查，不能把新第一页拼在旧列表后面；
4. **切换条件重置**：`scope` / `filter` / `total` 变化 → `cursor=null`、清列表、回到顶部；
5. **翻页不变参**：`loadingMore` 必须复用第一页的 `scope/filter/total/scheme`；
6. **加载中可编辑**：输入框永远可输入，不允许全屏 spinner 抢焦点；
7. **IME 保护**：输入法组合期间不触发搜索（`composing` 为 true 时跳过 debounce）；
8. **高亮防御**：`start/end` 必须 `0 ≤ start ≤ end ≤ text.length`，越界直接忽略该段（不抛异常）；
9. **无障碍**：结果变化时向 live region 播报"找到 N 条结果"；`partial` / `degraded` 也要播报；
10. **日志**：只记 `requestId / gen / scheme / partial / errorCode / ms`，不记结果内容。

**与构建任务的交互**：

- 设置页开启索引后，UI 订阅进度 JSON；`indexBuilding` 期间搜索走 `fallback`，不阻塞播放；
- 构建完成（`state=done`）→ 比较 `gen`；变化则静默重跑当前查询第一页（保持滚动位置在顶部）；
- 构建失败（`state=failed`）→ 回到 `degraded`，显示一次 toast，不自动重试轰炸。

**与 `total` 的展示**：

| 响应 | UI |
|---|---|
| `total=-1` | 不显示总数，只显示当前条数 |
| `totalExact=true` | "共 N 首" |
| `totalExact=false` | "10000+"（达到上限时） |
| `clamped=true` | 底部"仅显示前 10000 条" |

---

## 10. 术语表

| 术语 | 含义 |
|---|---|
| **term** | 查询经归一化 + 脚本切分后的一个检索单位（一个 CJK 段 / 一个拉丁词） |
| **kind** | 该候选的最终匹配类型（exact / phrase / prefix / partial / pinyin / initials / reading / crossLingual / typo） |
| **field** | 命中的字段族（title / artist / album / filename / lyrics / lyricsTr / lyricsRom） |
| **step** | 计划里的一次存储查询（一个 kind × 一个字段族 × 一个迭代器） |
| **evidence** | 候选的匹配证据（kind + field + matchedTerms + coverage），排序的唯一输入 |
| **bucket** | 排序键的层级；桶间字典序，桶内才允许 bm25 |
| **df** | 词的文档频率；EraStore 的词典条目直接携带，不需要额外统计表 |
| **seg_sep** | 段间哨兵 token，阻断跨段短语假命中 |
| **rev** | 曲目内容指纹（128 bit），增量的唯一判据 |
| **gen** | 索引世代号；**每次提交写事务 +1**（全量 / 增量 / 合并收尾都算），用于缓存与游标失效 |
| **EraStore** | 自研存储：单文件 + 不可变段 + 前编码词典 + 块压缩倒排 + 提交槽 |
| **段（segment）** | EraStore 的一次不可变写入单元；查询在段上并行/归并 |
| **提交槽（commit slot）** | 文件末尾 40 字节的双缓冲记录，指向当前 Manifest；原子提交点 |
| **tombstone** | 逻辑删除标记；合并时把活 doc 搬到新段，旧字节成为垃圾 |
| **前编码（front coding）** | 排序词典的公共前缀压缩：存"共享前缀长度 + 后缀" |
| **block_max** | 倒排块内的最大 tf / 最大分数，用于跳块与预算 |

---

## 附录 A：旧机制 → 新机制映射

| 现状机制 | 去向 | 说明 |
|---|---|---|
| `CompileMatch` 三支平铺 OR | 拆成 `literal-exact` / `literal-prefix` / `phonetic-exact` 三个 step | 不再共享一个表达式 |
| `MinExact` 短路 | `min_match` 曲线 + 计划期预算 | 运行期不再决定语义 |
| 六趟 + 丢词 + CJK 容错 | 计划里的 7 个 step | 顺序保留，语义边界显式化 |
| `bm25` 单池排序 | 桶序 → 桶内 bm25 | 跨桶不可比 |
| `initials_meta_tok` 整段 token | 首字母 bigram（无 unigram） | 支持部分首字母，仍禁止单字母 |
| `pinyin_lyrics_tok` / `initials_lyrics_tok` | 独立字段族 `0x21` / `0x22`：拼音默认写、首字母默认关 | 主路径不再扫读音字段；位置默认写 |
| `reading_meta_tok` + `reading_tok` 副本 | `field 0x17` + DocStore 原文 | 不需要"副本列"，DocStore 可复核 |
| `indexed_mtime` | `rev`（内容指纹） | 根治 D-3 |
| `optimize` | 分层段合并（死行 ≥20% / 段数 ≥8，单次 2s） | 成本与脏曲数解耦 |
| `AnalyzerVersion` 单锚 | `index_format` / normalizer / tokenizer / scheme / data_pack 多锚 | 只重建该重建的 |
| 20+ 个布尔开关 | 算法注册表 + 命名 scheme | 组合枚举化，可评审、可回放、可降级 |
| Dart 侧高亮 / 片段 / 脚本判定 | 全部下沉 .NET | 一份实现，一套向量 |
| Unihan 全量释义 | 策展表（最低桶） | 降低发散 |

## 附录 B：参数与默认值（初版，待 S1 回放校准）

| 参数 | 值 | 依据 |
|---|---|---|
| `min_match(n)` | `1→1, 2→2, n≥3→ceil(2n/3)` | Solr `mm` 分段 + 现有 M-3 结论 |
| 前缀条件 | 仅末 term；拉丁/拼音 ≥3；CJK 单字走 `字*` | 现状 + Meilisearch §3.5 |
| 容错词长 | ≥4→1 错；≥7→2 错；首字符替换**不生成变体**（等同算 2 错） | Typesense / ES / 审计 §21 |
| 读音步 | 单 CJK 字不生成；库画像无日/韩则跳过 | 审计 §13 |
| 跨语言步 | 单字禁止；仅策展候选；最低桶 | 审计 §14 + 本文 S4 |
| 候选预算 | literal 2k / drop 1k / phon 1k / reading 500 / cross 300 / typo 300 | 回放校准 |
| `maxQueryFrequency` | 拉丁/拼音词 `df/N > 1%` 不生成前缀/读音/容错；CJK bigram 一律不做容错 | Lucene 0.01；CJK bigram 的 df 分布更平，1% 会几乎全禁，故直接按脚本分治 |
| 合并 | 死行 ≥20% 或段数 ≥8；单次时间预算 2s | §3.3.0.7；可中断 |
| 歌词去重 | 行级（归一后相等） | §8.1 V1；目标去重率 ≥20% |
| 歌词 unigram | 不建（只 bigram） | §8.1 V3 |
| 歌词护栏 | 4096 唯一行 / 128 KB **每层**（去重后计数；正常歌词不截断） | §8.1 V6；阈值由真实库 p99.9 校准 |
| 歌词读音 | 字段族 `0x21`，默认开、位置默认写；`0x22` 首字母默认关 | §8.1 V4 + ADR-4；免位置（V2）非默认 |
| 模糊词表 | 词典前缀区间 + 有界编辑距离（**无额外表**） | §8.4；禁止全量载入词表 |
| `total` 默认 | `none`（响应 `total=-1`） | §3.6.4；避免每次查询遍历倒排 |
| `limit` 上限 | 10000（超出截断并返回 `clamped=true`） | UI 单屏用不到更多；保护长尾 |
| 查询预算 | 整体 50 ms；单步 deadline 150 ms | §3.6；超时置 `partial`，不 abort |
| 结果缓存 | LRU 128 条，键含 `gen` / `scheme` / 全部开关 | §3.8.5 |
| 只读映射 | 整文件 mmap；32 位 / 地址空间不足退化为块读 + 64MB 块缓存 | §8.3 |
| 写锁等待 | 5000 ms（读不需要锁） | §3.8.7 |
| EraStore 倒排块 | 128 doc/块 | §3.3.0.3；块内 Δvarint |
| EraStore 内存段 | 64 MB 或 16k doc 刷盘 | §3.3.0.6 |
| EraStore 合并 | 死行 ≥20% 或段数 ≥8；单次 2s | §3.3.0.7 |
| EraStore 词典块 | 每 64 条一个块索引 | §3.3.0.2 |
| EraStore 垃圾回收 | 垃圾 >30% 触发全量重建 | §3.3.0.7；单文件不原地删 |

## 附录 C：代码落点（供后续实施拆任务）

| 动作 | C#（唯一实现） | Dart（只保留 UI / IO） |
|---|---|---|
| 归一化版本拆分 | `Analyzer.cs` | 删除 `search_highlight*.dart` 的拼音/脚本逻辑 |
| 方案注册表 | 新 `SearchScheme.cs` / `SchemeRegistry.cs` | 无 |
| 计划数据结构 | 新 `SearchPlan.cs` | 无（只消费响应） |
| 算法角色与实现 | 新 `Searching/`（Tokenizer / Matcher / Ranker） | 无 |
| 计划编译 / 执行 | 新 `SearchPlanCompiler.cs`，改 `SearchSession.cs` | 无 |
| 桶序 | 新 `SearchRanker.cs` | 按 `items` 顺序展示 |
| 统计快照 | 新 `SearchStats.cs` | 无 |
| 热词 / 词表快照 | B：`HotTerms.cs` / `VocabMeta.cs`；A：**不需要**（词典自带 df） | 无 |
| EraStore 格式 | 新 `EraStore/Format.cs`（varint / CRC32C / 块编解码） | 无 |
| EraStore 写 | 新 `EraStore/SegmentWriter.cs` / `Manifest.cs` | 无 |
| EraStore 读 | 新 `EraStore/SegmentReader.cs` / `PostingsCursor.cs` / `DocStore.cs` | 无 |
| EraStore 合并 / 校验 | 新 `EraStore/Merger.cs` / `Verify.cs` | 无 |
| 查询迭代器 | 新 `EraStore/Iterators/`（Term / Prefix / And / Or / Phrase） | 无 |
| 存储抽象 | `IIndexStore` 只保留**一个实现**（S3 删除另一个） | 无 |
| 无索引兜底 | 新 `FallbackSearch.cs`（`era_search_fallback`） | 删除 Dart 里的 `LIKE` 拼装 |
| 构建任务 API | `Program.cs` / `indexer-ffi` 增加 `era_index_*` + 进度回调 | 构建进度条改为消费回调 JSON |
| EraStore 格式 | `EraStore/Format.cs` + `IIndexStore` | `search_index.dart` 版本探针 |
| `rev` | `IndexerEngine.cs` + scanner 写列 | 无 |
| `explain` / `highlight` / `snippet` | `IndexerJson.cs` | `search_models.dart` 解析并渲染 |
| 回放 | `bench/qbench/QueryEval.cs.txt` 增模式 | 无 |
| 版本 / 健康 | `Program.cs` 子命令 | 设置页「索引状态」 |

## 附录 D：`era_search_*` 内部契约 v2（可整体替换）

Dart 与 C# 是同一个安装包里的两层，没有第三方调用方；因此这里**只保证一份契约**，
不做 `api:1` 兼容。所有结构用 `api` 字段标注版本，当前为 **2**。

### D.1 ABI 与所有权

```c
int32_t era_search_api_version(void);                    // 返回 2

int32_t era_search_open(const char* index_dir, const char* options_json,
                        era_search** out);
int32_t era_search_run(era_search* h, const char* request_json,
                       char** out_json, int32_t* out_len);
void    era_search_free(char* json);                     // 成功 / 错误 JSON 都归它释放
void    era_search_close(era_search* h);

int32_t era_search_fallback(const char* library_path, const char* request_json,
                            char** out_json, int32_t* out_len);
int32_t era_search_health(const char* index_dir, char** out_json);
```

- 所有返回的 `char*`（`run` / `fallback` / `health`）都由 `era_search_free` 释放，
  **不能**混用 `free` / `CoTaskMemFree`；进度回调的 JSON 只在回调期间有效，需要保留就复制；
- `era_search_run` 可在**不同句柄**上并发；同一句柄内部串行；
- `out_json` 在失败时也返回**错误 JSON**（不是 NULL），方便调用方记日志；
- 句柄持有 mmap 与缓存；`close` 后所有迭代器 / 结果失效。

### D.2 请求

```jsonc
{
  "api": 2,
  "query": "周杰伦 晴天",
  "scope": "all",              // all | metadata | lyrics
  "limit": 50,                 // 1..10000；0 或缺省 = 20
  "cursor": null,              // 不透明游标（上一次响应的 next）
  "total": "none",             // none | capped | exact
  "explain": false,            // 只对前 50 条返回 explain
  "highlight": true,           // 对前 50 条返回 UTF-16 区间
  "snippet": false,            // 只对前 10 条返回歌词片段
  "scheme": "default@1",       // 缺省用索引默认；未知值回退默认并在响应里标注
  "filter": null               // { "trackIds": ["<32 hex>", ...] }，≤50k
}
```

| 字段 | 类型 | 默认 | 校验 / 截断 |
|---|---|---|---|
| `api` | int | — | 必须 = 2；缺失 / 1 → 码 1 |
| `query` | string | — | 去首尾空白后 0..200 码点；空串直接返回空结果（不报错）；超长截断并置 `queryTruncated` |
| `scope` | enum | `all` | 非法值 → 码 1 |
| `limit` | int | 20 | `<1` → 20；`>10000` → 10000 + `clamped=true` |
| `cursor` | string | null | ≤4KB；非法 → 码 1 |
| `total` | enum | `none` | 非法值 → 码 1 |
| `explain` / `highlight` / `snippet` | bool | false | — |
| `scheme` | string | 索引默认 | 未知 → 用默认，响应 `schemeRequested` 保留原值 |
| `filter.trackIds` | string[] | null | ≤50k，元素必须 32 hex；超出 → 码 1 |

**拒绝的旧字段**：`minExact` / `candidateCap` / `fuzzy` / `ranking` / `engine` 出现即 **码 1**，
`detail.reason = "deprecated_field"`。目的是让"忘了删旧字段"的调用方立刻暴露，而不是静默按新语义跑。

`filter.trackIds` 的语义：

- 先按库范围召回候选，再按 `trackId` 过滤；为保证填满 `limit`，召回上限按 `2×limit` 放宽；
- 过滤集合是查询期 `HashSet`（≤50k，约 3MB），池化复用；不参与排序键；
- 过滤后不足 `limit` 就返回实际数量，不置 `partial`（这是用户显式过滤，不是超时）；
- 列表内搜索优先用 `scope=metadata` + `filter`，避免把 `scope=all` 的歌词列也扫一遍。

### D.3 响应

```jsonc
{
  "api": 2,
  "gen": 12,
  "scheme": "default@1",
  "schemeRequested": "default@1",
  "items": [
    {
      "trackId": "0123456789abcdef0123456789abcdef",
      "score": -12.3,                    // 仅诊断；顺序才是契约
      "explain": { "...": "见 D.3.1" },   // explain=true 且前 50 条
      "highlight": [                     // highlight=true 且前 50 条
        { "field": "title", "start": 0, "end": 2, "mode": "literal" }
      ],
      "highlightTruncated": false,
      "snippet": {                       // snippet=true 时才有
        "field": "lyrics",
        "text": "…",
        "ranges": [ { "start": 3, "end": 7 } ],
        "truncated": false
      }
    }
  ],
  "total": -1,               // total=none → -1；否则为计数
  "totalExact": false,       // total=exact 且未超时 → true
  "partial": false,          // 超时 / 预算裁剪
  "clamped": false,          // limit 被压到 10000
  "queryTruncated": false,   // query 超过 200 码点
  "restart": false,          // 游标失效，需从第一页重查
  "next": null,              // 无更多为 null
  "trace": null,             // explain=true 时为 D.3.3 对象
  "timings": { "planUs": 120, "execUs": 2400, "rankUs": 90 }
}
```

- `items` 顺序就是结果顺序；`score` 不参与客户端排序；
- `explain` / `highlight` 只对**前 50 条**生成（`D.3.1`），其余为 `null`；
- `snippet` 只对前 10 条生成，文本 ≤512 UTF-16 code unit；
- `timings` 是诊断字段，允许缺失。

#### D.3.1 `explain`

```jsonc
{
  "version": 1,
  "kind": "exact",              // exact|phrase|prefix|partial|pinyin|initials|reading|crossLingual|typo
  "field": "title",             // title|artist|album|filename|lyrics|lyricsTr|lyricsRom
  "fieldId": "0x01",            // 实际字段族
  "matched": 2,                 // 命中的查询 term 数
  "total": 2,                   // 查询 term 总数
  "coverage": 1.0,              // matched / total（量化后）
  "exact": "equal",             // equal|startsWith|contains|none
  "proximity": 0,               // 0 = 相邻；无位置时 = -1
  "positioned": true,           // false = 该步无位置，短语降级为 AND
  "bucket": [0, 1, 0, 0, 0],    // 前五个排序键：kind / coverage / field / exact / proximity
  "bm25": -12.3,                // 第 6 键，仅桶内
  "lexicalSources": [           // 该候选被哪些 step 召回（诊断）
    { "step": 0, "kind": "exact", "field": "title", "matched": 2, "positioned": true }
  ]
}
```

- `bucket` 只放前五键，不放 `bm25` 与 `docId`，避免浮点与内部 id 进 UI 契约；
- `positioned=false` 表示该步没有位置信息（短语被降级为 AND），不是错误。

#### D.3.2 高亮（`highlight=true`）

```jsonc
"highlight": [
  { "field": "title",  "start": 0, "end": 2, "mode": "literal" },
  { "field": "artist", "start": 0, "end": 5, "mode": "reading" }
]
```

- `start/end` 是 **UTF-16 code unit 偏移**，与 Dart `String` 索引一致；`end` 不含；
- `mode`：`literal`（原文） / `reading`（拼音 / 罗马字） / `initial`（首字母）；
- 每个字段最多 32 段、最多 4 个字段；超出置 `highlightTruncated=true`；
- 读音高亮由 C# 用同一分析器重算；归一化改变长度时退化为只高亮字面，不猜；
- 歌词高亮需要原文，只在 `snippet=true` 时返回；否则只给 `field` 级说明。

#### D.3.3 `trace`（`explain=true` 时返回）

```jsonc
{
  "steps": [
    { "i": 0, "kind": "exact", "fieldId": "0x01-0x04", "op": "term",
      "est": 12, "returned": 5, "state": "ran", "ms": 0.8 },
    { "i": 3, "kind": "prefix", "fieldId": "0x01-0x04", "op": "prefix",
      "est": 400, "returned": 0, "state": "skipped", "reason": "kind_dominated" }
  ],
  "skipped": [
    { "i": 5, "reason": "budget" },
    { "i": 6, "reason": "df_gate" },
    { "i": 7, "reason": "capability", "missing": "phonetic_lyrics_initials" }
  ],
  "truncated": [
    { "i": 2, "what": "prefix_expansions", "limit": 128 }
  ],
  "deadlineHit": false
}
```

`reason` 取值固定：`kind_dominated` / `budget` / `df_gate` / `capability`。
`steps[].state` 取值：`ran` / `skipped` / `degraded`（无位置降级）。
`truncated[].what` 取值：`prefix_expansions` / `df_estimates` / `typo_variants`。

### D.4 错误

```jsonc
{
  "api": 2,
  "error": {
    "code": 3,
    "message": "index version mismatch",
    "retryable": false,
    "detail": { "anchor": "tokenizer_version", "expected": 2, "actual": 1 }
  }
}
```

| 码 | 含义 | `retryable` | 调用方动作 |
|---|---|---|---|
| 1 | 请求 / JSON 无效 | false | 修请求；旧字段出现也走这里 |
| 2 | 索引不存在 / 未就绪 | true | 触发构建 / 用 `era_search_fallback` |
| 3 | 版本 / 能力不符 | false | 删除重建 |
| 4 | 忙（写任务 / 锁被占） | true | 稍后重试或等待 |
| 5 | IO / 磁盘满 / 只读 | false | 提示用户；保持 `LIKE` |
| 6 | 内存不足 | true | 降级 `LIKE`，记录 |
| 7 | 已取消 | false | 正常路径，不告警 |
| 8 | 内部错误（bug） | true | 记日志 + `LIKE` |

- 失败**不通过空 `items` 表达**：语法 / 版本问题必须是错误码；
- `detail` 只放可安全展示 / 记录的结构化信息，不放路径与用户数据；
- `era_search_fallback` 的响应与正式查询同构，`scheme` 固定 `fallback@1`，
  `explain` / `trace` 为空，`highlight` 仍返回原文区间。

### D.5 游标

`next` 是 **base64url(32B)**，客户端只回传、不解析：

| 偏移 | 大小 | 字段 |
|---|---|---|
| 0 | 1 | `version=1` |
| 1 | 8 | `gen` |
| 9 | 4 | `scheme_hash`（FNV-1a） |
| 13 | 1 | `kind_rank` |
| 14 | 2 | `coverage`（0..10000） |
| 16 | 1 | `field_rank` |
| 17 | 1 | `exact_rank` |
| 18 | 2 | `proximity` |
| 20 | 4 | `bm25`（IEEE-754 f32） |
| 24 | 8 | `doc_ref = (segment<<32) \| local` |

- 翻页时 `gen` 或 `scheme_hash` 与当前不符 → `items=[]`、`next=null`、`restart=true`；
- 游标非法 / 越界 → 码 1，不返回半页结果；
- `limit` 跨页不变；改变 `scope` / `filter` / `total` 必须从第一页重查（服务端检测到差异 → `restart=true`）。

### D.6 健康与进度

`era_search_health(index_dir, char** out_json)` 供设置页：

```jsonc
{
  "api": 2,
  "state": "ready",            // absent|ready|building|corrupt
  "gen": 12,
  "scheme": "default@1",
  "anchors": { "index_format": 1, "normalizer": 1, "tokenizer": 2,
               "scheme": 1, "data_pack_id": "…" },
  "capabilities": ["surface_text", "phonetic_meta", "phonetic_lyrics_pinyin", "…"],
  "docs": 48759,
  "segments": 4,
  "liveBytes": 242000000,
  "fileBytes": 268000000,
  "garbageBytes": 26000000,
  "deadRatio": 0.04,
  "pendingWrites": 12,
  "pendingDeletes": 3,
  "lastBuildMs": 95000,
  "lastMergeMs": 1800
}
```

`era_index_start` 的进度回调 JSON：

```jsonc
{
  "api": 2,
  "task": "build",             // build|refresh|compact
  "phase": "analyze",          // scan|analyze|write|merge|commit
  "state": "running",          // running|done|cancelled|failed
  "done": 1200, "total": 30000,
  "docsPerSec": 410, "etaMs": 70200,
  "changed": 1200, "deleted": 3, "errors": 0,
  "truncatedLyrics": 0, "gen": 12,
  "error": null                // state=failed 时为 D.4 的 error 对象
}
```

### D.7 演进与校验

1. **`api` 必须 = 2**；缺失或 1 → 码 1，直接降级 `LIKE`，没有旧路径；
2. **只增不改**：新增字段允许，删除 / 改语义必须升 `api`；
3. 客户端必须忽略未知字段；服务端对未知**顶层**字段记 warn 并忽略；
4. 每次改请求 / 响应结构，同步更新：本附录、Dart DTO、golden 请求 / 响应、JSON Schema；
5. 本地契约测试（`tests/contract/`）：每个示例过 Schema；`highlight` 的 UTF-16 偏移用 emoji 用例验；
6. 不改 CI：Schema 校验属编译级静态检查，可在 `dart analyze` / 本地测试跑，不新增工作流步骤。

### D.8 契约 golden 集（本地跑，不进 CI）

目录：

```text
tests/contract/
├─ cases.json                 # id → request / expectations
├─ request/C01.json …         # 请求
├─ response/C01.json …        # 期望响应（可只断言子集）
├─ schema/request.schema.json
├─ schema/response.schema.json
└─ README.md                  # 如何加一条用例
```

**规则**：

1. 附录 D 的每个**字段**至少被一条用例覆盖；
2. 每个**错误码**（1–8）至少一条；
3. `trace.reason` 每个枚举值至少一条；
4. `explain.kind` 每个 kind 至少一条（主矩阵覆盖 exact / pinyin；其余 kind 放 `tests/contract/kinds/` 扩展集）；
5. 游标必须做"两页无重复 + `gen` 变化 → `restart`"两类断言；
6. 校验失败只报"哪条用例、哪个字段、期望 vs 实际"，不打印整份响应；
7. 运行：`dotnet test app/core/indexer/tests --filter Category=Contract`（本地，不进 CI）。

**用例矩阵**：

| id | 请求要点 | 必须断言 |
|---|---|---|
| C01 | 普通查询；`explain=false`、`highlight=true` | `api=2`；顺序稳定；`explain=null`；高亮区间合法 |
| C02 | 同上 + `explain=true` | `bucket` 长度 5；`trace.steps` 非空；`lexicalSources` 有来源 |
| C03 | `scope=lyrics`、拼音查询 | `field` 属于歌词族；`positioned=true`；不返回元数据命中 |
| C04 | 高频词 + `total=capped` | `total ≤ 10001`；`totalExact=false`；`partial=false` |
| C05 | 小语料 + `total=exact` | `totalExact=true`；`total` 与朴素实现一致 |
| C06 | `limit=20000` | `clamped=true`；`items ≤ 10000` |
| C07 | 250 码点查询 | `queryTruncated=true`；只跑字面步；不报错 |
| C08 | `limit=10` + 第二页 `cursor` | 两页无重复；`next` 可继续或为 null |
| C09 | `cursor` 后重建索引（`gen+1`） | `restart=true`、`items=[]`、`next=null` |
| C10 | `filter.trackIds` 子集 | 只返回子集；不足 `limit` 时 `partial=false` |
| C11 | `snippet=true` | 只有前 10 条有 `snippet`；`ranges` 在文本范围内 |
| C12 | `era_search_fallback` 同请求 | `scheme=fallback@1`；`explain/trace=null`；高亮仍有效 |
| C13 | `api=1` | 码 1；`detail.reason=api_mismatch` |
| C14 | 带 `minExact` | 码 1；`detail.reason=deprecated_field`、`field=minExact` |
| C15 | 索引不存在 | 码 2；`retryable=true` |
| C16 | 锚不符（`tokenizer_version`） | 码 3；`detail.anchor=tokenizer_version` |
| C17 | 写锁被占 | 码 4；`retryable=true` |
| C18 | 空串 / 全空白 | 成功；`items=[]`；`total` 按模式为 0 或 -1；不打开索引 |
| C19 | `晴天😀`（非 BMP） | 高亮 `start/end` 按 UTF-16；emoji 占 2 个偏移 |
| C20 | 未知 `scheme="x@9"` | 成功；`scheme=default@1`；`schemeRequested="x@9"` |
| C21 | 未知顶层字段 `"foo":1` | 成功；忽略并记 warn（不进响应） |
| C22 | 构建中 `cancel` | 进度 `state=cancelled`；`era_index_wait=7`；`gen` 不倒退 |
| C23 | 高频词 + 前缀 | `trace.skipped{reason=df_gate}`（或不生成该步） |
| C24 | 索引缺 `phonetic_lyrics_initials` + `recall@1` | `trace.skipped{reason=capability}`；`scheme` 降级可见 |

**C02 期望响应（节选）**：

```jsonc
{
  "api": 2, "gen": 12,
  "scheme": "default@1", "schemeRequested": "default@1",
  "items": [{
    "trackId": "0123456789abcdef0123456789abcdef",
    "score": -12.3,
    "explain": {
      "version": 1, "kind": "exact", "field": "title", "fieldId": "0x01",
      "matched": 2, "total": 2, "coverage": 1.0,
      "exact": "equal", "proximity": 0, "positioned": true,
      "bucket": [0, 1, 0, 0, 0], "bm25": -12.3,
      "lexicalSources": [
        { "step": 0, "kind": "exact", "field": "title", "matched": 2, "positioned": true }
      ]
    },
    "highlight": [{ "field": "title", "start": 0, "end": 2, "mode": "literal" }],
    "highlightTruncated": false,
    "snippet": null
  }],
  "total": -1, "totalExact": false, "partial": false,
  "clamped": false, "queryTruncated": false, "restart": false,
  "next": null,
  "trace": {
    "steps": [{ "i": 0, "kind": "exact", "fieldId": "0x01-0x04", "op": "term",
                "est": 12, "returned": 5, "state": "ran", "ms": 0.8 }],
    "skipped": [], "truncated": [], "deadlineHit": false
  },
  "timings": { "planUs": 120, "execUs": 2400, "rankUs": 90 }
}
```

**C13 错误响应**：

```jsonc
{ "api": 2,
  "error": { "code": 1, "message": "api mismatch", "retryable": false,
             "detail": { "reason": "api_mismatch", "expected": 2, "actual": 1 } } }
```

**C19 非 BMP 偏移**：

```text
title = "晴天😀"
UTF-16: 晴(0) 天(1) 😀(2,3)
查询 "晴天" 命中区间 = { start: 0, end: 2 }
查询 "😀"   命中区间 = { start: 2, end: 4 }
```

> 偏移错误是这类契约里最常见的"看起来对、切片错一位"，必须用非 BMP 用例钉死。

## 附录 E：与成熟引擎的"借 / 不借"边界

| 参照 | 借（规则 / 参数） | 不借（代码 / 服务） |
|---|---|---|
| Meilisearch | `ranking_rules` 的分桶思想、前缀只给末词 | `milli` 的 Rust 实现、内嵌服务 |
| Typesense | `min_len_1typo=4 / 2typo=7`、`drop_tokens_threshold`、`_text_match` 位域 | GPL 代码、C++ 索引结构 |
| Lucene | `maxQueryFrequency` 思路、`minQueryLength`、`prefix_length` | Lucene 全家桶 |
| Navidrome | `(token OR token*)` 双形态、退化查询判定、声明式列权重 | Go 实现（可作为对照测试） |
| Solr | `mm` 分段条件（`3<90%`） | 整个 Solr |
| Groonga | `match_escalation` 的分级思想 | LGPL 代码 |
| `fts5-cjk` | unigram 取舍、`cjk_query` 的"按命中数切分" | C 扩展（需 build 与逐连接注册） |

> 许可证提示：Typesense / Navidrome 是 GPL-3.0，Groonga 是 LGPL-2.1。我们只借"参数与规则"，
> 独立实现；若后续要移植代码片段，先走 `docs/licensing.md` 的评审流程。

## 附录 F：决策记录（ADR）

| ID | 决策 | 状态 | 替代方案（为何不选） |
|---|---|---|---|
| ADR-1 | **存储引擎自研：EraStore**；SQLite 只用于只读 `library.db` | **已定（v0.22）** | SQLite FTS5：体积 / 启动下限被页与 b-tree 锁死；现成搜索引擎：格式与排序不受控 |
| ADR-2 | 排序用分桶字典序，不用加权求和 | 已定 | 加法权重存在"弱信号 × 高 TF"退化路径 |
| ADR-3 | 字面索引与读音索引物理分离 | 已定 | 同表加列：主路径被迫扫读音列，6.5–9.7× 延迟 |
| ADR-4 | 歌词**拼音是一等能力**（永不删除，默认开）；**首字母**是独立可选能力（默认关） | 已定 | 关拼音换体积：直接违反"功能不可用体积换"；首字母默认开：噪声大、收益小 |
| ADR-5 | `rev` 由 scanner / 写库方生成 | 待落地 | indexer 自己算：要读全库大列，增量退化 |
| ADR-6 | 算法按角色注册、由 scheme 组合，不互斥分支 | 已定 | 布尔开关 + 条件编译：2ⁿ 组合无法测试与评审 |
| ADR-6b | 搜索逻辑全部下沉 .NET，Dart 不再实现同类算法 | 已定 | 两层各写一份：口径漂移、无跨层契约测试 |
| ADR-7 | 版本拆四维（schema/normalizer/tokenizer/scheme） | 已定 | 单 `AnalyzerVersion`：改阈值也全量重建 |
| ADR-7b | 能力协商 + `degradeTo` 方案 | 已定 | 运行期试错：列不存在才发现，用户看到异常 |
| ADR-11 | 体积只接受**无损优化**：去重、编码、压缩、合并；V2（歌词读音无位置）非默认且需用户确认 | 已定 | 关拼音 / 只索引副歌 / 词级合并：都是丢能力或明显降召回 |
| ADR-12 | 模糊词表直接扫词典前缀区间（**无额外表**） | 已定 | 复制一份词表：启动慢、常驻内存大，与 §8 冲突 |
| ADR-13 | **发布只带一个后端**（EraStore），无运行时切换、无双写、无引擎配置项 | **已定（v0.22）** | 双后端：两套故障模型、两倍测试面，且违反"不回退" |
| ADR-8 | 首字母改 bigram（不出 unigram） | 待 S3 实测 | 整段 token：部分首字母（`xz`）永远搜不到 |
| ADR-9 | 段间插入 `seg_sep` 哨兵 | 待 S3 实测 | 不插：跨段短语有极小概率假命中，难排查 |
| ADR-10 | 分页只用 keyset（不保留 offset） | 待 S2 落地 | 纯 offset：重建 / 增量后翻页漂移；内部接口无需兼容 |

## 附录 G：数据、许可证与隐私

| 资产 | 来源 | 许可证 | 落点 |
|---|---|---|---|
| `nfkc.tsv` | Unicode 兼容分解（NFKD 语义，由生成脚本产出） | Unicode License | `data/` + manifest |
| `accent_fold.tsv` | NFD 派生 | Unicode License | 同上 |
| `variant_fold.tsv` | Unihan `kSimplifiedVariant` / `kJapaneseOldVariant` | Unicode License | 同上 |
| `kana_romaji.tsv` / `ja_readings.tsv` | Unihan `kJapanese*` | Unicode License | 同上 |
| 韩国罗马字 | 规则实现（`Korean.cs`，无数据表） | 自建 | 代码 |
| 拼音 | `ToolGood.Words` | Apache-2.0 | 依赖，不复制表 |
| 策展双语表（S4） | 见 §5.5 | **需单独评审**（CC0 / CC-BY / 自建） | 未就绪不启动 S4 |
| 查询日志（回放） | 用户查询 | **隐私数据** | 只本地保存；导出必脱敏；不随仓库分发 |

隐私红线：查询日志**不得**进仓库、不得上传；回放 harness 只接受本地文件路径；
`explain` 默认关闭，避免把命中字段写进长期日志。

## 附录 H：开工前检查清单（S1 之前必须全部打勾）

- [ ] `SearchSpec v1` 评审通过（本文 §3 升格为正式规格，含所有数字的证据链接）。
- [ ] 回放语料就位：桌面端查询日志导出为 JSONL（脱敏、去重、去空）。
- [ ] 基准语料就位：400 合成 / 3k quick / 50k 真实子集三档可重复构建。
- [ ] `qbench` 能在当前实现上跑出基线（延迟 / 召回 / 体积 / **启动**四份 JSON）。
- [ ] 负例集 N1–N20 全部写成可执行断言（当前实现允许部分失败，作为对照基线）。
- [ ] golden vectors 目录与加载器就位（C# 全量跑；Dart 只做响应 schema 校验）。
- [ ] `rev` 的 canonical 格式冻结（C# 实现 + 向量，格式变更走 `rev2`）。
- [ ] 开关命名冻结：`scheme`、`explain`（**不存在** `engine` / `ranking` / `index.schema` 回退开关）。
- [ ] 清理演练一次：搜索代码里搜不到旧符号、旧索引格式、旧 ranker 的任何引用。

## 附录 I：算法候选矩阵（可插拔清单）

> 这张表是"延展性"的具体定义：**同一个角色可以有多份实现，方案挑一份或几份**。
> 加算法不等于改核心，只等于加一行注册 + 一组向量。

| 角色 | 现有实现 | 可替换 / 候选实现 | 采用条件 |
|---|---|---|---|
| **Tokenizer** | `bigram@1`（CJK bigram + 首尾 unigram） | `trigram@1`（<3 字符无效，仅对照）、`unigram@1`（噪声大）、`wordseg@1`（ToolGood 词级，需评估）、`kana-bigram@1` | 新分词必须给出体积 / 延迟 / 召回三项对比 |
| **ReadingProvider** | `pinyin-toolgood@1`、`ja-unihan@1`、`ko-romaja@1`、`romaji-canonical@1` | `pinyin-multi@1`（多音字全展开）、`ja-kana-only@1` | 只在索引侧生成；查询侧不新增转换 |
| **Matcher** | `exact@1`、`phrase@1`、`prefix@1`、`pinyin-joined@1`、`pinyin-phrase@1`、`initials-bigram@1`、`reading-bigram@1`、`cross-lingual@1`、`typo-damerau1@1`、**`typo-damerau2@1`**、`drop-term@1` | `abbrev@1`（英文缩写）、`numeric@1`（年份 / 音轨号）、`pinyin-fuzzy@1`（模糊音） | 每个 matcher 必须自带负例与预算估计；`typo2` 仅出现在 `recall@1` |
| **Ranker** | `bucket@1`（唯一） | `bm25-only@1`（对照）、`diversity@1`（同曲多版本去重）、`personalized@1`（需用户数据，暂不做） | 任何 ranker 都只能有一个入口；不得后处理重排；不保留旧 ranker |
| **PostProcessor** | `highlight-literal@1`、`highlight-reading@1`、`snippet@1`、`dedupe-versions@1` | `highlight-cross-lingual@1` | 纯展示，不参与排序 |
| **IndexStore** | `era-store@1`（唯一实现） | 无（不做第二后端） | 换存储必须整体替换，不做运行时切换 |
| **Scheme** | `fast@1`、`default@1`、`recall@1`、`minimal@1` | `strict@1`（精确优先）、`lyrics@1`（只搜歌词） | 新方案必须声明 requires / degradeTo / 预算；不提供 legacy 方案 |

新增一个实现的准入清单：

1. 声明 `Id` 与 `Capabilities`（产出或要求什么）；
2. 给出至少一组 golden vectors（含负例）；
3. 给出 P50/P95 与对索引体积的影响（索引侧实现）；
4. 指定它进入哪个 scheme（或作为 `disabledByDefault` 候选）；
5. 不得修改任何已发布 scheme 的向量。

## 附录 J：修订记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v0.1 | 2026-11-23 | 初稿：现状盘点、结构性诊断、目标五层、迁移 S0–S4、冲突规避十条 |
| v0.2 | 2026-11-23 | 细化：归一化 / token / SQL 不变量、计划数据结构与编译模板、预算与丢词算法、桶序判定与比较器、explain 契约、rev 规范与状态机、降级矩阵、可观测性、任务拆解、测试向量、性能与容量、配置项、JSON 契约、ADR、许可证与开工检查清单 |
| v0.3 | 2026-11-23 | 范围收敛：只考虑 ArchoeraMvsync 本体；歌词读音列改为**默认开**；删除服务端 / 移植相关任务与验收 |
| v0.4 | 2026-11-23 | 改为**直接重构**（不再移植）：新增方案层（Scheme）+ 算法注册表 + 能力协商；新增「逻辑下沉 .NET」的职责边界与迁移清单；版本锚 `policy` → `scheme`；附录新增算法候选矩阵 |
| v0.5 | 2026-11-23 | 内部契约解冻：`api:2` 可整体替换 + `era_search_api_version()` 握手；`scope=lyrics` 与 `typo2` **直接纳入 S1/S2**（不延后）；scope 只裁剪列、不参与方案选择；新增 N21–N24 负例 |
| v0.6 | 2026-11-23 | **完全不认可旧索引**：`search_meta` 一票否决锚（`index_format` / schema / normalizer / tokenizer / options / data_sha256）；删除旧符号 wrapper、`api:1` 兼容、v7 双写与旧索引读取；配置删除 `search.index.schema`；S3 删除全部 v7 代码路径 |
| v0.7 | 2026-11-23 | **完全不回退**：删除 `legacy@1` / `legacy-pass@1` / `engine=legacy|plan` / `ranking` 开关 / 所有「回退」步骤；S1 直接替换旧引擎（不保留双路径）；全流程改为「失败即停 + 只向前修」 |
| v0.8 | 2026-11-23 | 新增 §8 硬指标：占用 ≤0.80×、open ≤0.5×、首查 ≤0.6×、P50 ≤0.35×、P95 ≤0.50×、构建 RSS ≤0.80×；落地手段 V1–V7（歌词行/块去重、去 unigram、歌词首字母默认关、上限）与 `vocab_meta` 紧凑词表；S3 门禁改为逐项比值 |
| v0.9 | 2026-11-23 | 歌词读音拆为 `phon_lyrics_fts`（`detail=none`，不用 bm25）；V2 从「块级去重」改为「免位置存储」；版本锚统一 `data_pack_id` |
| v0.10 | 2026-11-23 | 取消「256 行截断」：改为**按层独立、去重后计数、只挡病态数据**的护栏（4096 唯一行 / 128 KB 每层），阈值由真实库 p99.9 校准；长歌词与三层翻译不再误伤 |
| v0.11 | 2026-11-23 | 完整性核查：`total` 默认不计数（`none/capped/exact`）+ `next` 不透明游标（含 `gen`）；索引构建 API `era_index_*` 与错误码/线程规则；`era_search_fallback` 兜底下沉重写；`capabilities`/`options_bits` 清单；SQLite ≥3.43 与 `detail=none` 实测结论；派生统计维护与多实例文件锁；scanner `content_rev` 契约；§6.6 测试矩阵；统一 `gen` 术语（去掉 `data_index`/`index_gen` 混用） |
| v0.12 | 2026-11-23 | 新增存储引擎选型（ADR-1 / ADR-13）：§3.3.0 EraStore 自研段式倒排（前编码词典、块压缩倒排、提交槽 MVCC、tombstone），与 SQLite v8 二选一；新增 S2.7 两周 spike 与决策门；**歌词拼音改为一等能力、永久保留**，体积只接受无损优化（V2 降为非默认、L2–L5 换成无损杠杆，删除"关拼音"备选）；体积预期按 A/B 分别给出 |
| v0.13 | 2026-11-23 | **存储定稿 EraStore**（ADR-1 由待决改为已定）：删除 A/B 二选一与选型决策门，§3.3 全量改写为字段族 / 段 / 词典 / 倒排模型；删除 SQLite schema、写入 SQL、PRAGMA、`hot_terms` / `vocab_meta` / `search_stats` 等表；`search_meta` → `Manifest`、`search_docs` → `DocStore`；S2.7 由"选型 spike"改为"原型前置验证"，S3 由"v8 索引"改为"EraStore 落地"；歌词拼音默认开、可手动关 |
| v0.14 | 2026-11-23 | **EraStore 专项细化**：字节级文件布局（Header 64B / 提交槽 2×32B / Manifest）、词典前编码与块索引、倒排与位置的块结构（tf=1 位图、Δvarint、独立位置块）、DocStore 列式与 doc_ref、查询迭代器与 kind 剪枝、流式构建与内存上限、分层合并与写放大、崩溃注入测试、体积模型（50k 曲 202–290 MB）、性能模型、O1–O10 优化清单、格式演进与不可信文件校验 |
| v0.15 | 2026-11-23 | 新增 §3.3.0.14 逐字节编码示例（3 首小曲：排序词典 / 前编码 / df / tf 位图 / 位置块 / 解码顺序）；§3.3.0.15 合并代价模型（3k/30k/48.7k/200k 的段数、重写次数、写放大与 2s 预算）；§3.3.0.16 引擎级基准与对拍（build/open/query/merge/verify、NaiveIndex 差分、模糊与崩溃注入、优化 flag 必须结果一致）；§3.3.0.5 查询期内存预算（候选表/堆/bitset/缓冲池化，常态 <2MB） |
| v0.16 | 2026-11-23 | 新增 §3.5.5.1–§3.5.5.5：`Step → 迭代器` 完整映射、`Advance` 契约、短语位置判定与降级、`Execute` 伪代码、`StepDominated` 精确判据、分层排序（只算边界组）、`total` 三模式、证据合并；新增 §3.8.2.1–§3.8.2.6：全量/增量/压缩三条路径的任务状态表、批次提交、取消语义、失败分类、按崩溃点的恢复矩阵；明确"单文件合并只产生垃圾、物理回收只在全量重建"；修正覆盖度合并（不跨 kind 求并集，避免假高覆盖） |
| v0.17 | 2026-11-23 | 附录 D 全量重写：请求/响应/`explain`/`trace`/错误/游标/健康与进度的字段表与示例；旧字段（`minExact` 等）显式拒绝；`filter.trackIds` 语义；**高亮从 `explain` 拆到 item 级**（`explain=false` 也能拿高亮）；游标 32B 二进制布局与 `restart` 规则；错误码补 8（内部错误）；只增不改的演进规则与本地契约测试 |
| v0.18 | 2026-11-23 | 新增附录 D.8 契约 golden 集（C01–C24 用例矩阵、C02/C13/C19 示例、字段/错误码/trace.reason/kind 覆盖规则）；新增 §3.11.1 Dart DTO 收敛清单（Request/Response/Item/Explain/Highlight/Snippet/Trace/Error/Health/Progress、握手、错误→行为、restart/partial/clamped UI 语义、删除清单与本地 grep 验收） |
| v0.19 | 2026-11-23 | 新增 §5.3 S2.7 PR 序列（P0–P11、9.5 人日、砍线规则与 DoD）；§6.7 回放 harness 数据格式（查询集/日志脱敏/5k 语料 hash/基线 JSON/diff 报告/运行纪律）；§9.1 搜索 UI 状态机（idle–restarting 十态、单请求、restart 清空、IME、构建交互、total 展示） |
| v0.20 | 2026-11-23 | 新增 §5.4 S3 切换与重建计划：触发与前置、48.7k 首建策略（批/段/提交/时间/内存）、磁盘/电量限制、三档 Go/No-Go 门禁、失败降级、切换日清单、7 天观察指标、重建触发汇总；明确"只重建不迁移、失败只退 LIKE" |
| v0.21 | 2026-11-23 | 新增 §5.5 S4 数据包治理（manifest 与 `data_pack_id`、版本/重建、构建期与运行期校验、跨语言词表准入、许可证白名单、缺失降级）；新增 §3.10.1–3.10.3 指标目录（命名/类型/定义/高基数禁令）、日志规范与隐私、健康页字段映射 |
| v0.22 | 2026-11-23 | 新增 §3.2.3–3.2.5 归一化表结构（NFKD 语义、列定义、冲突/闭包/幂等、运行时快路径与性能、语义边界），归一化步骤改为 NFKD→小写→去音→异体→繁简；新增 §5.6 LRC 分层置信度（层定义、0–8 打分、置信度对 `scope`/读音/跨语言的影响、解析规则、译文配对、存储与诊断） |
| v0.23 | 2026-11-23 | 新增 §5.3.1 S2.7 归档结论（T2.17）：P0–P11 与 T2.13–T2.16 完成状态、quick 实测（12 字段族语料、8 份 JSON、1,558 B/轨、open 3.64 ms、pooled P50 0.02 ms 等）、七条已知限制（构建 RSS 触线、positions 占 57.2%、体积比值待 S0、full 未跑、差分仅存储层、整段合并、纯构建无 Stats/Tombs）与 S3 前置项 |
| v0.24 | 2026-11-23 | 修正 §5.3.1 为构建内存修复后的最终数据：quick 6,200,917 B / 1,240 B/轨 / RSS 97.2 MB，30k 36,099,278 B / 1,203 B/轨 / RSS 118.7 MB；记录紧凑累加器 + 块缓冲复用 + V2 派生层免位置（`NoPositionFields`）三项改动；重写已知限制（新增 `MaxSections=64` 段数硬顶与写侧无校验风险、`NoPositionFields` 的 PhraseIter 降级语义、bench GC 配置需 NativeAOT 复核、full 仍缺）；T2.14 更新为 PASS 956 |
| v0.25 | 2026-11-23 | S0 落地 `replay run` / `replay corpus`（`app/core/indexer/Replay.cs`）：查询集 JSONL → §6.7.4 基线 JSON（前 100 个 trackId、中位耗时、queries hash），真实库 `sha256(track_id)` 子集与 corpus hash；并在 §S0 记录已实现 / 未实现范围 |

---

## 一句话总结

> 现有实现的问题不是"某个匹配规则不对"，而是**把召回、匹配语义、排序三件事塞进了同一个
> FTS5 `MATCH` 字符串和同一个 bm25 池**，于是每修一个症状就得加一个全局常量、抬一次版本、
> 重建一次索引。重写的正确姿势是：**索引只做候选召回，匹配类型变成显式证据，排序变成显式分桶；
> 算法按角色注册，由可命名的方案（scheme）组合**，新增算法不改核心、降级方案只换名字；
> 同时**把搜索逻辑全部收进 .NET**，Dart 只留 UI 与 IO；
> 存储层换成自研 **EraStore**，不再让 FTS5 的页开销与 `detail` / `contentless` 语义决定产品能力。
> 用"版本拆维 + 行为等价重构 + 只向前修"三条纪律保证不退回旧实现；
> 每步都有负例、有回放、有删除清单——这样它才是"重写"，而不是第 24 批补丁。
