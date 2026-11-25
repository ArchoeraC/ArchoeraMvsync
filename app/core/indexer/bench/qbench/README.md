# qbench —— 查询延迟 / 召回质量 harness

两个子命令，同一个项目（`QueryBench.csproj`）：

| 子命令 | 量什么 | 依赖 |
|---|---|---|
| `<无子命令>`（默认） | 查询**延迟**（P50 / P95，同进程循环） | `search.era/` |
| `eval` | 查询**质量**（P@1/P@10、R@10/R@50、MRR@10、NDCG@10） | `search.era/`（+ 可选 `library.db`） |

两者都直接复用生产 `SearchSession`（同源编译），不复制查询逻辑。
`--index` 只是 **EraStore 的目录锚**：读取 `<index 所在目录>/search.era/CURRENT`，
该 `.db` 文件本身可以不存在（T3.13 起生产已不再写 `search.db`）。

> `QueryEval.cs.txt` / `QueryBench.cs.txt` 用 `.txt` 后缀是有意的：父项目
> `app/core/indexer/indexer.csproj` 用 `**/*.cs` 通配，`.cs` 会被吸进去产生第二个
> `Main` / 重复类型；本项目的 `QueryBench.csproj` 关闭默认通配并显式 `Compile Include`。

## 质量评测（recall / NDCG）

```bash
cd app/core/indexer/bench/qbench

dotnet build -c Release QueryBench.csproj

dotnet run -c Release --no-build --project QueryBench.csproj -- eval \
  --index  ../.work/t314/corpus30k/search.db \
  --library ../.work/t314/corpus30k/library.db \
  --limit 50 \
  --out ../.work/t314/qeval-corpus30k-baseline.json
```

参数：

- `--index`（必填）EraStore 目录锚；`search.era/CURRENT` 缺失即报错退出 1。
- `--library`（可选）`library.db`，只用来读**歌词原文**（DocStore 只存标题/艺人/专辑）。
  缺失时自动退化为「元数据字面标注」，报告里 `truth.fields` 只有三列、`library_present=false`。
- `--spec`（可选）外部标注集，格式
  `{"queries":[{"name","text","targets":[],"metadata":false}]}`；
  不传时自动派生 + 内置分析器探针（见下）。
- `--limit` 默认 / 最小 50（否则 R@50 无意义）。
- `--out` 写 JSON；不传则只打印到 stdout。

**JSON 路径约定**（`.work/` 已 gitignore，基线留在本地工作目录）：

```
app/core/indexer/bench/.work/t314/qeval-<corpus>-<tag>.json
# 例如：qeval-corpus30k-baseline.json / qeval-corpus30k-<commit>.json
```

对比两轮时先比 `query_set.hash`：不同哈希说明评测集变了（语料 / `--spec` / 分析器探针改动），
数字不可直接比。`queries[].name` 稳定，可作为逐题 diff 的键。

### 已记录的基线（T3.14，2026-11-25）

复现命令（`corpus30k`；`corpus5k` 换目录名即可）：

```bash
cd app/core/indexer/bench/qbench
dotnet build -c Release QueryBench.csproj
dotnet run -c Release --no-build --project QueryBench.csproj -- eval \
  --index  ../.work/t314/corpus30k/search.db \
  --library ../.work/t314/corpus30k/library.db \
  --limit 50 --out ../.work/t314/qeval-corpus30k-baseline.json
```

> 下列数据为 **2026-11-25 复核基线**（analyzer v15 / DocStore v4 / 数据包 manifest、
> 构建后自动 compact + defrag）。旧基线（v13，FTS5 迁移初期）见 git 历史。

`corpus30k`（30000 曲，37 题，`query_set.hash = 68b4c4c3…`）：

| bucket | n | P@1 | P@10 | R@10 | R@50 | MRR@10 | NDCG@10 |
|---|---|---|---|---|---|---|---|
| narrow | 3 | 1.000 | 0.100 | 1.000 | 1.000 | 1.000 | 1.000 |
| broad | 34 | 1.000 | 0.977 | 0.002 | 0.012 | 1.000 | 0.981 |
| **ALL** | 37 | **1.000** | **0.905** | **0.083** | **0.092** | **1.000** | **0.983** |

`corpus5k`（5000 曲，37 题，`query_set.hash = 981bcfaa…`；窄 / 中 / 宽三档都有）：

| bucket | n | P@1 | P@10 | R@10 | R@50 | MRR@10 | NDCG@10 |
|---|---|---|---|---|---|---|---|
| narrow | 3 | 0.667 | 0.100 | 1.000 | 1.000 | 0.778 | 0.833 |
| medium | 12 | 1.000 | 1.000 | 0.031 | 0.153 | 1.000 | 1.000 |
| broad | 22 | 0.955 | 0.964 | 0.006 | 0.027 | 0.970 | 0.963 |
| **ALL** | 37 | **0.946** | **0.905** | **0.094** | **0.147** | **0.964** | **0.964** |

> 5k `narrow` 的 P@1 0.333 → 0.667 来自 T3.11（字面精确度 + 真实字段桶）：唯一歌词行题
> `line_mid` 从 #2 升到 #1；`line_tail` 只命中歌词列，按 spec 不做精确度，仍是 #2。

> 基线 `narrow` 的 P@10 = 0.100 是定义使然（`n_rel=1` → P@10 上限 0.1），
> 验收看 `P@1 / MRR@10 / NDCG@10`；宽命中题相反，`R@10/R@50` 天然低。
> **同一题集、同一语料**上的数值变化才是回归信号。

### 金标准（ground truth）口径

**字面包含**：曲库文本里出现目标字串即算相关（`Contains`，ASCII 忽略大小写）。
这是「子串搜索」的诚实定义，不依赖被测分析器（避免循环论证），但**不是人工相关性判定**：

- 不评价「哪首更该排第一」，因此 NDCG 只报二元相关（无分级标注）。
- 拼音 / 首字母 / 繁体查询的目标字串由评测集**显式给出**（`woaini` → `我爱你`），
  不由查询侧分析结果反推。
- 标注字段：`title` / `artist` / `album`（来自 EraStore DocStore）；
  非 `metadata` 查询再加上 `lyrics`、`lyrics_lrc`、`lyrics_lrc_trans`、`lyrics_lrc_romaji`
  （按 `library.db` 实际存在的列）。
- 标注只在 EraStore **活文档**（跳过墓碑）上生效。歌词命中里「库里有、索引里没有」的
  条数记进 `missing_from_index`；整库口径的差额记进 `library_only_tracks`——
  这两项用来区分「索引旧了」与「分析器不行」。

### 评测集怎么来（默认，不传 `--spec`）

全部**与查询结果无关**、确定性、按需自适应：

1. **标题频次分档**：最稀有 / 中位 / 最高频标题各 1 条（同名曲目必须都被找到）。
2. **标题前缀子串**：最长 3 个标题掐前 2 字（中文 bigram 子串召回）。
3. **高频艺人 / 专辑**各 2 条（`scope=metadata`）。
4. **歌词稀有二字词**：按 id 序前 2000 首采样统计纯汉字 bigram df，窄档 `2–10`、
   中档 `20–200` 各取 3 条（同 df 只取一条：同一注入行派生的 bigram 属重复题）；
   被既有 targets 覆盖的词跳过。需要 `library.db`，缺失则跳过。
5. **唯一歌词行**（窄档针尖）：取 id 序 1/6、1/2、5/6 处三首曲目的一条完整歌词行
   当查询，`n_rel` 通常为 1——检验「整行原文这种长查询能否精确找回且排第一」。
   跳过带空格的行（注入行都是 "A B" 形状，选到它们只是重复公共短语）。
6. **内置分析器探针**（21 条）：中文短语 / 子串 / 单字 / 繁体字·词·短语 /
   拉丁整词·前缀·短语·大小写 / 数字 / 拼音连写·前缀·首字母·词级前缀 /
   拉丁错拼·换位。目标字串取自 `gen_corpus.py` 的固定注入短语与词表；
   真实曲库里查不到的条目自动记为「无标注」并从汇总里排除。

`corpus30k` 上默认产出 **37 条**（3 条窄档唯一行 + 34 条中/宽档；本语料上 34 条全落在宽档），
`QueryBench` 会在 JSON 里写出 `query_set.hash`，跨轮对比先比哈希。

### 已知局限

- `corpus30k` / `corpus487` / `corpus5k` 用的是 `gen_corpus.py` 的**固定小词表**
  （约 90 个词组 + 20 个标题），单个词元几乎命中整库，`df ≤ 10` 的稀有词
  **不存在** → `term_narrow` 恒为空、`narrow` 档只由「唯一歌词行」填充，
  `medium` 档在 `corpus30k` 上也会因规模放大而消失（5k 上仍有 12 条）。
  要看三种分档都完整，用 `--vocab 4000` 生成大词表语料，或直接对真实曲库评测。
- `pinyin_initials`（`wan`）的 `found@50` 远小于 `n_rel`（30k: 12 / 4592）：
  `wan` 同时是 晚安 / 万千 / 我爱你 … 的首字母，引擎为它返回约 30000 条候选，
  top-50 里挤进多少「本意目标」（我爱你）取决于打分。这是**歧义查询的排序结果**，
  不是标注错误；验收看的是同题相对变化。
- 拉丁 / 拼音 / 首字母查询的 targets 是**标注者给出的本意**（`wan` → 我爱你）。
  其它同样命中该输入的字面（晚安…）既不算相关也不扣分（P@k 只数相关项），
  因此这类题的 R@k 天然低，不代表漏召回。
- 指标是**宏平均**（每题等权），不是按相关项数加权；宽命中题的 R@10/R@50 天然偏低
  （top-50 只覆盖全库 6% 的命中），窄命中题的 P@10 上限只有 `1/n_rel`。
  验收时看的是**同一题集上的相对变化**，不是绝对值高低。

## 延迟评测（不变）

```bash
dotnet run -c Release --no-build --project QueryBench.csproj -- \
  --index ../.work/t314/corpus30k/search.db --label A --pinyin on \
  --limit 10000 --repeats 3 --warmup 1 --out ../.work/t314/qb-latency.json
```

两条命令都不要在 CI 里跑（CI 只编译）；按 `AGENTS.md` 的要求，本地验证用
`PROFILE=quick`，不要开 `ALLOW_LONG_BENCH`。
