# `tests/vectors`：搜索向量目录

> 状态：**S0 + S1 进行中**。分析器两层（`analyzer.tsv` / `query.tsv`）已接线；
> **Plan** 层已落地（`plan/*.jsonl` + `PlanVectorTests`）；Rank / Negative 仍是建议格式，
> 加载器分别等 S2 / S1 归因接线。
> 另有一份**代码级负例对照基线**（`tests/NegativeBaselineTests.cs`，见 §2.3.1），
> 不属于本目录的向量文件。
> 规格正文见 [SearchSpec v1](../../../../../docs/search-spec-v1.md)；
> 阶段与验收只认 [rewrite-plan §5](../../../../../docs/local-search-indexer-rewrite-plan.md)。

---

## 1. 现有文件

| 文件 | 分派 | 断言对象 | 说明 |
|---|---|---|---|
| `analyzer.tsv` | 索引侧 | `Analyzer.Analyze(input)` | 索引期词元 / 拼音 / 首字母 |
| `query.tsv` | 查询侧 | `Analyzer.AnalyzeQuery(input)` | 含连写全拼切分；纯拉丁 `initials` 为 `null` |

> **v15（T3.9）口径**：`initials` 列仍是**人类可读**的每段首字母串，但拉丁 / 数字段不再
> 进入该列（行尾空列即断言空串）。索引列真正写入的是相邻两两 bigram
> （`wanzg` → `wa an nz zg`），由 `Program.cs` 直接断言 `AnalysisResult.InitialsBigrams`，
> 不放进 tsv（避免把「可读列」与「倒排列」混为一谈）。

分派规则不是后缀，而是**文件名前缀**：`query*.tsv` 走查询侧，其余 `*.tsv` 走索引侧
（`tests/Program.cs` 的 `RunVectorFile`）。

### 1.1 当前读取流程

```text
vectors 目录解析（ResolveVectorsDir）：
  1) 命令行第一个参数（若为存在的目录）
  2) <输出目录>/vectors
  3) 从输出目录向上逐级找 vectors/
读取（Main）：
  Directory.GetFiles(vectorsDir, "*.tsv")   // 仅当前目录，不递归
  Array.Sort(..., StringComparer.Ordinal)   // 文件名序
  逐文件 RunVectorFile
```

`RunVectorFile` 的逐行规则（与代码一致）：

- 以 UTF-8 读取；每行先 `TrimEnd('\r')`，兼容 CRLF。
- **空行**与**首字符是 `#`** 的行跳过（`#` 只有在行首才算注释）。
- 按 `\t` 切列；`cols[0]` 是输入，`cols[1..3]` 是期望值；**缺列按 `*` 处理**。
- `*` 表示该列不校验；其它值走**逐字符相等**比较，不做 trim、不做大小写折叠。
- 输出值：词元与拼音用单空格连接；索引侧首字母原样比较，查询侧用 `?? string.Empty`。
- 失败打印 `FAIL <文件> [<输入>] <列>` 与 expected / actual，不打印整份输出。

`IndexerTests.csproj` 目前只复制根目录的 `vectors/*.tsv` 到输出目录
（`<None Include="vectors/*.tsv" CopyToOutputDirectory="PreserveNewest" />`），
**不递归**；新增子目录时必须在工程里补一条复制规则，否则运行期找不到。

### 1.2 格式边界

| 约束 | 结果 |
|---|---|
| 列分隔 | 制表符；**没有转义机制**，输入里不能出现 `\t` |
| 引号 | 无特殊语义，`"` 是普通字符 |
| 行首 `#` | 注释；要测 `#` 开头的查询只能放 JSON 格式（S1 起） |
| 空输入 | 行首直接是 `\t`（如 `analyzer.tsv` 的空串用例） |
| 空列 / 缺列 | 视为 `*`（不校验）；不能用 `*` 断言字面 `*` 输出 |
| 前后空格 | 有意义，不做 trim |
| 多行文本 | 不支持；向量只覆盖单行输入 |

> ⚠ **不要**把新格式的 `plan.tsv` / `rank.tsv` / `negative.tsv` 直接放进本目录根下：
> 现有 `Directory.GetFiles("*.tsv")` 会把它们当作分析器向量逐行解析并产生假失败。
> 新格式一律放子目录，并由 S1 的加载器**显式**按目录分派。

---

## 2. 新增向量（Plan / Rank 已接线）

三层都断言**期望结构**（term、step、排序 id、bucket 元组），
**不是**期望字符串，也**不是**整份响应 JSON。禁止把段号、文件偏移、物理 `doc_id`、
内部计数写进期望值——存储布局一变测试就得重抄，那说明断言层次错了。

### 2.1 `plan`：查询计划（加载器已实现）

实际落地在 `plan/` 子目录 + **JSONL**（steps 是嵌套结构，TSV 不适合），
当前共 45 条：`plan-core.jsonl`（28 条查询形状 / scope / fuzzy）、
`plan-profiles.jsonl`（12 条曲库语言画像）与 `plan-lyrics.jsonl`（5 条 `scope=lyrics`）。

一行一个对象（**每行必须自带完整 `profile`，不继承默认值**）：

```json
{"id":"P0007","input":{"query":"周杰伦 晴天","scope":"metadata","fuzzy":false,"minExact":1,"limit":20,"profile":{"japanese":true,"korean":true,"han":true,"latin":true}},"expect":{"terms":["周杰","杰伦","周","伦","晴天","晴","天"],"minMatch":1,"missingCaps":[],"steps":[{"id":"lexical@1","pass":"lexical","kind":"Exact","family":"Meta","op":"And","covered":7},{"id":"drop@0","pass":"dropTokens","kind":"Partial","family":"Meta","op":"And","covered":3,"dropIndex":0},{"id":"drop@1","pass":"dropTokens","kind":"Partial","family":"Meta","op":"And","covered":4,"dropIndex":1},{"id":"pinyin-loose@1","pass":"pinyinLoose","kind":"PinyinExact","family":"Pinyin","op":"And","covered":5},{"id":"cross-lingual@1","pass":"crossLingual","kind":"CrossLingual","family":"CrossLingual","op":"Or","covered":15}],"notSteps":["reading@1","fuzzy@1","cjk-fuzzy@1","drop@2"]}}
```

| 键 | 必填 | 说明 |
|---|---|---|
| `id` | ✅ | 稳定主键（`P*`），文件内唯一；一旦入库不复用、不重排 |
| `input.query` | ✅ | 合成查询，禁止真实用户查询；允许空串 / 纯空白 / emoji |
| `input.scope` | ✅ | `all` / `metadata` / `lyrics`（分别映射 `SearchRequest` 的默认 / `Metadata=true` / `Lyrics=true`，后两者互斥） |
| `input.fuzzy` | ✅ | 映射 `SearchRequest.Fuzzy` |
| `input.minExact` | ✅ | 映射 `SearchRequest.MinExact`（在 S1 等于 `plan.MinMatch`） |
| `input.limit` | ✅ | 映射 `SearchRequest.Limit`；计划本身不暴露该字段 |
| `input.profile` | ✅ | 四个布尔 `japanese` / `korean` / `han` / `latin` → `StoreMeta.HasJapanese` 等；缺项即向量错误 |
| `expect.terms` | ✅ | `QueryPlan.Terms.Select(t => t.Surface)`，**保序**逐项相等 |
| `expect.minMatch` | ✅ | `QueryPlan.MinMatch` |
| `expect.missingCaps` | ✅ | 必须是空数组（S1 无能力缺口；非空视为向量错误） |
| `expect.steps` | ✅ | **有序且完整**（与 `QueryPlan.Steps` 一一对应）；每项只断 `id` / `pass` / `kind` / `family` / `op` / `covered`；`drop@*` 额外断 `dropIndex` |
| `expect.notSteps` | 可选 | 不得出现的 `Step.Id` 列表（只写关键缺席步骤，不必罗列全部） |

**不断言** `Expression` 字符串（v7 FTS5 存储方言细节；模糊趟为 `null`、中文容错趟可能非 `null`，
交给执行器契约）。同理不断言 `Cap` / `Gate` / `ReadingSequences` / `Policy` 快照，
这些随预算校准或内部重构漂移。

加载器 `tests/PlanVectorTests.cs`（入口 `PlanVectorTests.Run()`）：

- 逐文件、逐行读取；**空行**与**首字符 `#`** 跳过（与 tsv 规则一致）。
- JSON 坏行、schema 缺字段、`id` 重复、未知 `scope`、`missingCaps` 非空 → `Program.Check` 失败并带
  文件名 + 行号；**不静默跳过**。
- 目录定位：测试程序集旁的 `vectors/plan` → 从程序集目录向上逐级找
  `tests/vectors/plan` / `vectors/plan`（目录不存在或没有 `*.jsonl` 直接失败）。
- 每条 case 断言：① `SchemeId` / 空 `MissingCaps` / `MinMatch`；② `terms` 序列；③ `steps` 数量、
  顺序与每个结构字段（枚举按 `ToString()` 名比较，drop step 的 `dropIndex` 单独比较）；
  ④ `notSteps` 不存在；⑤ 同输入再 `Compile` 一次，`JsonSerializer.Serialize(plan)` **字节级一致**。
- 失败信息带 `id` 与下标（如 `P0007 steps[2].covered`），便于定位。

### 2.2 `rank`：桶序

建议 `rank/` 子目录 + TSV，**一行一个候选**，同一 `id` 的多行构成一组：

```text
# id	query	docId	kind	field	coverage	exact	proximity	bm25	expectRank
R0001	qingtian	d1	prefix	title	1.0	startsWith	0	-3.2	2
R0001	qingtian	d2	exact	title	1.0	equal	0	-1.0	1
```

| 列 | 说明 |
|---|---|
| `id` | 组主键（`R0001` 起）；组内行序不重要，`expectRank` 才是断言 |
| `query` | 合成查询，仅用于诊断输出 |
| `docId` | 向量内自造稳定键（`d1` / `d2`…），**不是**物理 doc id |
| `kind` / `field` | 用规格里的 wire 名；跨方案比较放在 `scheme` 列（可选） |
| `coverage` / `exact` / `proximity` / `bm25` | 构造候选的输入，按规格键序参与比较 |
| `expectRank` | 1 起；同一 `id` 下必须构成 1..N 的排列，重复或缺口即向量错误 |

排序断言只到**顺序 + 前五键 bucket**；`bm25` 只用于桶内平局，不跨桶。
需要验证"相同 kind 下 coverage 胜出"时，`expectRank` 已足够，不要再加一份解释文本。

### 2.3 `negative`：负例

建议 `negative/` 子目录 + TSV：

```text
# id	corpus	query	scope	scheme	mustNotContain	kindNotIn	mustBeEmpty
N-SYN-001	synthetic/no-pain	痛	all	default@1	*	*	true
N-SYN-002	synthetic/love-hate	爱	all	default@1	恨	*	false
N-SYN-003	synthetic/title-only	晴天	lyrics	default@1	*	*	true
```

| 列 | 说明 |
|---|---|
| `id` | `N-SYN-*`；仓库内只用合成负例，编号不与 RP §6.2 的 N1–N24 混用 |
| `corpus` | 指向 `negative/<name>.jsonl` 的合成曲目夹具（标题 / 歌手 / 歌词，可为空） |
| `query` / `scope` / `scheme` | 请求三要素 |
| `mustNotContain` | 逗号分隔的 `trackId` 或标题；`*` = 不额外限制（空结果由下一列表达） |
| `kindNotIn` | 逗号分隔的 kind；断言**没有任何**命中来自这些档位，`*` = 不断言 |
| `mustBeEmpty` | `true` 时必须零命中；`false` 时允许有命中但受前两列约束 |

负例 MUST 同时覆盖**结果集合**与**归因档位**：只断言"没崩"或只断言"结果里没有 X"
都不算通过（RP §6.2 末段）。

### 2.3.1 S0 执行现状（2026-11-24，代码级对照基线）

`tests/NegativeBaselineTests.cs` 已把能独立执行的负例写成断言（临时合成库 + 生产
`SearchSession`），结果分三档：

| 档 | 含义 | 条目 |
|---|---|---|
| **绿** | 要求当前已成立；行为变化会失败 | N1–N9、N12–N17、N21–N24（N6 用纯英文库；N12 含缓存路径） |
| **KNOWN-GAP** | 要求尚未成立；不增 FAIL，修好后自动变 PASS | **0 条**（N12 / N15 已转绿） |
| **BLOCKED** | 语义缺口尚未实现，无法断言 | N10（per-track 语言画像）、N11（单字歌词召回语义）、N18（计划期 df 预算） |

- `KNOWN-GAP` / `BLOCKED` 只打印，不计入 `FAIL`；测试运行器摘要会单列这两个计数。
- S1 接线 plan / explain 后，必须把 N8 / N10 / N11 / N18 转成正式断言；
  `scope=lyrics` 已落地（N21 / N24 转正式断言），`typo2` 落地后处理 N22 / N23；
  N19 / N20 属 FFI / 并发集成层。
- 本目录仍**不放**空向量文件；上表是执行状态，不是已覆盖的 tsv。

### 2.4 合成夹具

`negative/<name>.jsonl` 每行一首合成曲目，最少字段：

```json
{"trackId":"00000000000000000000000000000001","title":"晴天","artist":"合成歌手","album":"合成专辑","lyrics":null}
```

夹具只放**合成标题 / 歌手 / 歌词**；真实曲库、真实查询、真实 `track_id` 一律不进仓库
（真实数据只留在 `bench/.work/`，见 RP §6.7.2）。

---

## 3. 维护规则

1. **新增查询必须新 id，旧向量不改**。已有 id 的输入、期望值、行序都视为冻结；
   行为变化只能新增 id，或升 scheme 版本（RP §3.4.5 / §6.7.1）。
2. **断言结构，不断言存储**：不写段号、偏移、物理 doc id、内部计数；
   稳定键用 `trackId` 或向量自造的 `docId`。
3. **负例必须带归因**：`mustNotContain` 与 `kindNotIn` 至少一个非 `*`；
   空结果用 `mustBeEmpty=true` 显式表达。
4. **仓库只放合成样例**：文件名、id、夹具都不得来自真实用户数据。
5. **每组都要能单独定位失败**：失败信息报 `id` + 列名 + 期望/实际；
   `plan` 报第几个 step，`rank` 报期望名次与实际名次。
6. **不写空文件充数**：格式未接线前，只保留本文；不要创建空的
   `plan.tsv` / `rank.tsv` / `negative.tsv` 假装已覆盖。

---

## 4. 接线状态（S1）

- **已实现**：`plan` 层加载器 `tests/PlanVectorTests.cs`（入口 `PlanVectorTests.Run()`），
  已接入 `tests/Program.cs`；读取 `plan/*.jsonl`；当前 40 条向量（28 条查询形状 + 12 条曲库画像）。
- **已接线**：`tests/Program.cs` 的 `Main` 调用 `PlanVectorTests.Run()`，PASS / FAIL 并入现有计数。
- **复制规则**：`IndexerTests.csproj` 增加 `vectors/plan/*.jsonl` 的
  `CopyToOutputDirectory`；加载器仍优先按「程序集目录 → 向上逐级找 `tests/vectors/plan`」定位。
- `rank` 向量已由 `tests/SearchRankerTests.cs` 接线（读 `vectors/rank/*.jsonl`，覆盖 7 键比较器与证据合并）。
- `negative` 不走向量加载器：`tests/NegativeBaselineTests.cs` 用临时合成库 + 生产 `SearchSession` 直接断言（绿 / KNOWN-GAP / BLOCKED 三档）。
- 运行方式（本地，不进 CI）：

  ```bash
  cd app/core/indexer/tests
  dotnet run            # 默认向上查找 tests/vectors
  dotnet run -- <dir>   # 显式指定向量目录
  ```
