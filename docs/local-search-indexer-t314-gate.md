# EraStore S3 实测：T3.10 / T3.13 / T3.14 / T3.15

> 日期：2026-11-24（第二轮）
> 代码：`main` 工作区（EraStore 增量 + 只读 mmap + 生产路径去 FTS）
> 关联：`docs/local-search-indexer-rewrite-plan.md` §5.4 / §8
>
> **最终状态（2026-11-25）**：S0–S4 已全部实施；本文件记录 T3.10 / T3.13 / T3.14 / T3.15
> 的门禁过程与后续各轮实测。最新数字见文末「第六轮」，剩余项见「下一步」。
>
> **结论先行**：T3.10 的功能已落地（增量 + 墓碑 + 分层合并），T3.13 的生产查询路径已
> 零 v7/FTS5，T3.15 的切换逻辑已可复现；**T3.14 七项门禁 7/7 通过**（30k，空闲
> 机器：p50 7.7–9.6 ms、p95 28.5–32.3 ms、匿名 RSS 56.5–58.7 MB）。按 §5.4.4
> 可以进入 S3 合并评审，合并前仍需 T3.15 的 Windows / macOS 实机验证与全平台 CI。

## 1. 环境与语料

| 项 | 值 |
|---|---|
| OS | Linux 7.3.5-zen1 x86_64 |
| CPU | 13th Gen Intel(R) Core(TM) i9-13980HX |
| 内存 | 31,141,704 kB（≈29.7 GiB） |
| .NET | 10.0.114（framework-dependent CLI；NativeAOT 产物另测） |
| 临时盘 | tmpfs 15 GB |
| 语料 | `bench/gen_corpus.py`，seed `20261013`，2–8 KB 中文歌词/曲，50% `.lrc` |
| 档位 | 5,000 / 30,000 / 48,700 曲（§8.6 允许以 48.7k 近似 50k） |
| 查询集 | `bench/qbench` 固定 14 条（`--limit 10000`，与 §8.0 基线同口径） |

构建命令：

```bash
python3 app/core/indexer/bench/gen_corpus.py --out /tmp/opencode/t314/corpus30k \
  --tracks 30000 --seed 20261013 --generation 1
dotnet app/core/indexer/bin/Release/net10.0/archoera-indexer.dll build \
  --full --db /tmp/opencode/t314/corpus30k/library.db \
  --index /tmp/opencode/t314/corpus30k/search.db
# 生产路径默认不写 search.db；--legacy-fts 仅用于构造 v7 回归夹具。
```

## 2. 全量构建（生产路径，只写 EraStore）

> 下表为**首轮（v13，2026-11-24）**实测快照；最终数字（DocStore v4 / S4，2026-11-25）
> 见本节末尾的演进注与「第六轮」。

| 档位 | 曲目 | 构建耗时 | 峰值 RSS | 索引体积 | B/曲 | 段数 |
|---|---|---|---|---|---|---|
| 5k | 5,000 | 7.7 s | 152.6 MB | 37.2 MB | 7,445 | 48 |
| 30k | 30,000 | 40.5 s | 160.0 MB | 229.0 MB | 7,633 | 286 |
| 48.7k | 48,700 | 59.1 s | 173.3 MB | 372.1 MB | 7,640 | 465 |
| **NativeAOT** 5k | 5,000 | 7.4 s | **142.3 MB** | — | — | 48 |
| **NativeAOT** 30k | 30,000 | 40.4 s | **142.0 MB** | — | — | 286 |
| **NativeAOT** 48.7k | 48,700 | 67.8 s | **147.1 MB** | — | — | 465 |

- 与 S0 基线（30k，FTS5 382.5 MB / 109.1 s / 181 MB）相比：体积 **0.60×**、
  构建耗时 **0.37×**、峰值 RSS **0.88×**。
- 体积已接近 EraStore 目标（≤0.60×）。
- **RSS 以真实 NativeAOT 产物为准**：30k 峰值 142.0 MB（含 Python ctypes 宿主），
  0.78× 基线 → **通过**；framework-dependent CLI 的 160 MB 是 JIT 高估。
  48.7k 为 147.1 MB（该档没有 S0 基线，按绝对值记录）。
**最终（S4 / DocStore v4，2026-11-25）**：同一 30k 语料 `liveBytes` **136.6 MB**
（`fileBytes` defrag 后同为 136.6 MB），`bytes_per_track` 7.34 → **4.45 KiB（0.35× 基线）**；
5k 为 22.7 MB。演进：v14 165.75 MB → T3.4 关歌词首字母 128.5 MiB → DocStore v3 归一列
+0.90% → v4 `layer_conf` +1 B/曲。opt-in 打开歌词首字母时回到 165.75 MB；
构建本体 29.1 s → 30.7 s（+5.6%，在 10% 内）；构建收尾自动压缩 221 → 1 段。
去重在合成语料上贡献为 0（`gen_corpus` 没有曲内重复行），全部收益来自「歌词字面列
不建首尾 unigram」；真实库的 ≥20% 去重率仍需真实 `library.db` 抽样验证。

- 段数由 `MaxSegmentBytes = 4 MB`（估算）决定；真实歌词下单段约 4k 曲。
  这是 **RSS 与查询延迟的核心矛盾**：段越大 RSS 越高，段越小查询越慢。

## 3. 查询（生产路径 `SearchSession → EraStore`）

`bench/qbench` 已从 FTS5 切到 `SearchSession.Run`（`Cache=false`），
测量完整查询路径（计划编译 + 召回 + bm25 + 桶序 + 分页）。

### 3.1 30k（压缩到 8 段后；接近稳态）

| 指标 | 基线（§8.0） | 目标 | 实测 | 比值 | 判定 |
|---|---|---|---|---|---|
| `query_p50` | 30.2 ms | ≤10.57 ms | 7.7–9.6 ms（5 次） | 0.26–0.32× | ✅ |
| `query_p95` | 69.7 ms | ≤34.85 ms | 28.5–32.3 ms（5 次） | 0.41–0.46× | ✅ |
| 单条最慢 | — | — | 107–116 ms（偶发系统抖动；p95 不受影响） | — | — |

**本轮查询引擎（从 0.55–0.60× 到 0.26–0.32×）**：

1. **bitset 候选评估**：每个 atom 先建段内文档位图（单词直接扫倒排，词组复用
   `PhraseIter`），df = popcount；布尔树用 AND/OR 位运算求值。一次倒排扫描同时得到
   df 与候选，不再「先 CountDocs 再逐 doc 迭代」。
2. **预计算 tf**：单词 atom 在扫倒排时累计加权 tf（`double[]`，按 atom × 段）；
   词组仍按位置用游标计算。评分退化为数组查表 + 布尔树遍历。
3. **固定大小 top-k**：每步上限 `limit + offset`，用最大堆只保留最终候选，
   不再物化 2.9 万条后全排序；堆缓冲走 `ArrayPool<ScoredHit>`（≈240 KB）。
   未触发截断时保持原有扫描顺序，触发时集合与「全排序后取前 k」完全一致。
4. 叠加 df 缓存、`OrIter` 堆化、延后 TrackId、惰性 AND、宽前缀 tf 归并、
   12 列长度一次切片、候选对象减配。

> **测量条件**：以上 5 次均在机器空闲（无其它常驻 App / 编译器）时测得。
> 同一二进制约有 1–2 个活跃 CPU 进程时，p50 会漂到 10.6–12.2 ms、p95 33–36 ms
> （仍接近目标）；正式基线重跑请关闭并发负载，避免把调度抖动当成回归。

### 3.2 5k / 48.7k

| 档位 | 段数 | pooled P50 | pooled P95 | 备注 |
|---|---|---|---|---|
| 5k | 48 | 4.8–6.2 ms | 35.5–36.3 ms | 段太小，`compact` 不合并（40 万 doc 预算） |
| 48.7k | 13 | 13.6–15.0 ms | 43.7–45.0 ms | `compact` 10 轮把 465 → 13 段后测得 |

- 48.7k 未压缩（465 段）时 P50/P95 = **46.7 / 225.2 ms**；`compact` 后降到
  13.6–15.0 / 43.7–45.0 ms，段数仍是主要放大器。
- 30k 的 p50/p95 已达 §8.0；48.7k 的绝对值更高（段更大 / 文档更多），但仍比
  优化前的 26.8–28.8 / 59.3–59.8 ms 快约 2×。
- 读音 / 模糊趟已降到 2–6 ms（`romaji_reading` 4.4 ms）。

### 3.3 查询期匿名 RSS（`rss_query`）

NativeAOT FFI 进程（Python `ctypes` 宿主 ≈4–5 MB 基线），`/proc/self/smaps_rollup`
的 `Anonymous:` 口径：

| 阶段 | 5k | 30k | 48.7k |
|---|---|---|---|
| 进程基线 | 4.0 MB | 5.4 MB | 5.4 MB |
| `era_search_open` 后 | 7.0 MB | 6.9 MB | 8.4 MB |
| 首条 CJK 查询后（回收后） | 56.5 MB | 56.5 MB | 56.5 MB |
| 重复 100 次轻查询（limit=200） | 56.6 MB | 56.6 MB | 56.6 MB |
| Latin 查询（加载释义表）后 | 57.9 MB | 57.9 MB | 58.6 MB |
| 重复 10 次宽查询（limit=10000） | 57.3 MB | 57.3 MB | 57.2 MB |

- **全部 ≤60 MB（含 ≈5.3 MB 的 Python ctypes 宿主）**；与索引体积基本无关，
  说明不是 mmap 文件页，也不是段数。
- 单独调用 `era_analyze` 可复现：`我爱你` / `周杰伦` / `愛してる` 一律 +93 MB；
  拉丁词（`love`）+30 MB。
- 归因（独立 .NET 进程实测 ToolGood.Words 3.1.0.3）：仅调用
  `ToSimplifiedChinese` 之后 13 MB，首次 `GetPinyin` 后 **92.9 MB**；强制压缩回收后
  活对象仍约 43 MB（拼音词库 + 自动机），其余约 8–10 MB 是 Unihan 释义表。
- **本轮的三个措施**：
  1. `MemoryTrim.cs`：惰性词库加载后置位，查询响应写完后做一次
     `GCCollectionMode.Aggressive` 压缩回收（首查 ≈8 ms，只在新词库加载时付）；
     另每 16 条查询兜底回收一次（≈0.5 ms/查），把堆高水位钉在基线附近。
  2. **JSON 直写**：`SearchApiJson` 新增 `sink` 路径，响应从线程内复用缓冲直接
     拷到非托管内存，去掉「JSON string + UTF-8 byte[]」两次 LOH 分配；
     `SetUtf8(string)` 也改为直接编码到非托管缓冲；>512 KB 的缓冲写出后缩回 64 KB。
     30k FFI 查询 P50 从 46.3 ms 降到 42.1 ms（未含宽查询回收）。
  3. **释义表内部化**：`def_hanzi2en` 的 8.3 万引用共享 1.1 万唯一 string，
     省约 2–3 MB；`LoadGlossCodes` 用 `int[]` 替代 `List<int>`。
- **代价**：宽响应（≥128 KB，≈1 万条命中）会在返回前触发一次压缩回收（≈9 ms）；
  常见 UI 页大小（≤200 条）不触发。qbench（门禁延迟口径）不走 FFI，不受影响。
- 复现：NativeAOT `indexer-ffi` + `ctypes` 宿主，`era_search_open` 后先读
  `/proc/self/smaps_rollup` 的 `Anonymous:`，再调 `era_search_run` 后重读。

### 3.4 存储层（引擎级基准，不含排序语义）

| 档位 | 体积 B/doc | open（冷/热） | 首查 | pooled P50/P95 |
|---|---|---|---|---|
| quick 5k | 1,264 | 6.06 / 0.05 ms | 1.42 ms | 0.013 / 0.112 ms |
| 30k | 1,227 | 3.94 / 0.05 ms | 1.39 ms | 0.017 / 0.259 ms |

> 存储层本身满足 §8.0 的 `open_ms` / `first_query_ms` / 延迟目标；未通过的是
> **查询计划 + 逐条 bm25 的托管开销**，不是 EraStore 的读写。

## 4. §8.0 七项门禁（30k，逐项）

| # | 指标 | 基线 | 目标 | 实测 | 判定 |
|---|---|---|---|---|---|
| 1 | `bytes_per_track` | 12.75 KB | ≤10.2 KB | **4.45 KB（0.35×）**（磁盘口径；DocStore v3 + v15 + T3.4 默认关） | ✅ |
| 2 | `open_ms` | 未单独统计 | ≤10 ms | 3.94 ms | ✅ |
| 3 | `first_query_ms` | ≈1.1 s | ≤100 ms | 1.39 ms | ✅ |
| 4 | `query_p50` | 30.2 ms | ≤10.57 ms | 7.3–9.6 ms（qbench；v14 复测 7.31–7.36） | ✅ |
| 5 | `query_p95` | 69.7 ms | ≤34.85 ms | 28.5–32.3 ms（qbench；v14 复测 29.6–32.2） | ✅ |
| 6 | `rss_peak_build` | 181 MB | ≤144.8 MB | 142.0 MB（NativeAOT） | ✅ |
| 7 | `rss_query` | 未单独统计 | ≤60 MB（匿名） | 56.5–58.7 MB（5k / 30k / 48.7k） | ✅ |

**判定：7/7 通过（空闲机器）。** 体积 / open / 首查 / p50 / p95 / 构建 RSS /
查询期匿名 RSS 全部达 §8.0；有并发负载时延迟会抖动到目标边缘，见 §3.1 的测量条件。按 §5.4.4，S3 可以进入合并评审；合并前仍需完成 **T3.15 的
Windows / macOS 实机验证**（§7.1）与全平台 CI 首次验证。

门禁之外的剩余项（不阻止 S3，但建议跟进）：

1. ~~全量构建收尾的自动 `compact`~~：已实现（行数见 §8）。
2. O8 位打包 / postings 紧凑化：可把段做得更大、进一步降低查询固定成本，
   并把 NativeAOT RSS 继续压低。
3. T3.4–T3.9 的体积 / 召回优化按实测收益排序再做。

## 5. T3.10 增量结果

以 30k 库、稳态 8 段为样本（`UPDATE file_mtime` 制造脏曲）：

| 场景 | 应用阶段 | 合并 | 总耗时 | 备注 |
|---|---|---|---|---|
| 1 首脏曲（稳态） | 72 ms | 0 ms | 1.3 s | 其余为进程 / 30k track 探测 / 状态扫描 |
| 100 首脏曲 | 147 ms | 2.7 s | 5.3 s | 当时 7 段触发合并 |
| 全量对照 | — | — | 40.5 s | 30k |

- 增量应用（追加新段 + 墓碑）**只与脏曲数相关**：1 首 72 ms、100 首 147 ms；
  固定成本来自「扫全部 track_id 建映射 / 读取 DocStore v2 状态」。
- 合并是唯一 O(库) 的部分，已按 2 s 预算 / 4k 源 doc 限界；从 465 段收敛到 13 段
  共 6 轮 `compact`（每轮 ≤2 s 目标，实测 1–3 s）。
- **合并策略校准**：设计稿的「段数 ≥8」在真实歌词（单段 ~4k 曲）下会让每次增量
  重写一个 4k 段（1.2–1.4 s）。已改为「段数 ≥24 + 只归并小段」；死行 ≥20% 仍立即
  回收。该偏离记录在 §3.8 / §5.4 与本文件。
- 峰值 RSS（增量）：1 首 ~231 MB、100 首 ~261 MB（含 mmap 文件页）；合并阶段是峰值
  主要来源，仍需按匿名 RSS 复核。

## 6. T3.13 审计：v7 / FTS5 边界

### 6.1 已迁移（生产查询路径零 FTS）

| 位置 | 处理 |
|---|---|
| `era_search_open` / `era_search_health` | 只读 `search.era/CURRENT` + Manifest；`metaVersion` 过旧返回 3 |
| `era_search_run` | `SearchSession` → `EraQueryPlan`；无 `search.db` 读取 |
| `era_search_fallback` / `decorate` | 只读 `library.db`（LIKE / 片段 / 高亮），不碰 v7 索引 |
| CLI `query` | 默认与 `--raw` 都走 `EraStore.EraQueryPlan` |
| CLI `compact` | EraStore 分层压缩 |
| 连接串 / ASCII 判定 | 抽到 `LibraryDb`，查询路径不再引用 `FtsStore` |
| Dart `SearchIndex.open` / `isAvailable` | 改判 `search.era/CURRENT`，不再用 SQLite `search_meta` 探针 |
| Dart `SearchService._needsFullBuild` | 只看 EraStore 是否就绪（`search.db` 不再是必需文件） |
| FFI `era_search_run` | 只检查 EraStore CURRENT，不再要求 `search.db` 存在 |

### 6.2 已删除（T3.13b，2026-11-25）

| 位置 | 处理 |
|---|---|
| `FtsStore.cs` | **整体删除**（`Put/Delete/Commit/Optimize/Search/ReadMeta/WriteMeta/LoadState/Seal/PeekMeta/EnsureSchema/fts5vocab` 等） |
| `IndexStore.cs` | 删 `IIndexStore` / `IIndexStoreAdmin` / `SchemaVersionValue` / `UpdatesSinceMerge` / `DocState`；保留 `DocTokens` / `StoreMeta`（查询元数据） |
| `IndexerEngine` | 删 `IIndexStore` 构造参数、`WriteLegacyFts` 分支与整个旧 `Run`；`RunEraOnly` 改名 `Run` |
| `Program.cs` | 删 `--legacy-fts` / `--merge-every`；`search.db` 仅作 `search.era/` 的目录锚点 |
| `Ffi.cs` / `IndexerJson.cs` | 删 `new FtsStore()`、`WriteLegacyFts`、`merge_every_updates`、`Merged` / `UpdatesSinceMerge`（ABI 与 JSON 字段名不变） |
| `Replay.cs` | 接受 EraStore-only 目录：`CURRENT` 判定、`.eraindex` 字节数、`scheme="erastore"` |
| 测试 | `EndToEnd` / `SearchLayer` / `SearchApi` / `SearchPlan` / `SearchConcurrency` 迁到 Manifest / `IndexStats` / `EraQueryPlan` / `DocStore`；删 `MergeThreshold`（测 FTS `optimize`）与 `RankingIntegrity`（测旧 bm25 缺陷） |

**边界定义（更新）**：仓库里已无 v7 存储 / 写入代码。查询 IR 仍用
`{title_tok ...} : "..."` 这套 MATCH 子集语法，但它现在是 `EraStore/Expression.cs`
的**内部语法**，不是 FTS5；T3.13 的验收口径按此理解。

- 健康检查的 `schemaVersion` 改为 `Manifest.CurrentMetaVersion`（1），`gen` 由 Manifest 实际值派生。
- 静态检索 `FtsStore|fts5vocab|search_meta|WriteLegacyFts|IIndexStore` 只剩历史注释
  与 `bench/` 旧报告；`app/lib`、`app/test` 无命中。

### 6.3 索引器路径

- v7 写入路径已整体删除（T3.13b）：构建只写 EraStore，不再产生 `search.db`。
- 脏集状态：`DocStore v2` 新增 `mtime / lrc_mtime / lrc_size` 三列；读取侧兼容 v1
  （缺失列按 0）。`StoreReader.LoadTrackState()` 提供 `track_id → 状态`。
- 版本锚：不再用 `_scanner_meta.generation` 与索引 `gen` 比较（二者语义不同）；
  只比较 Manifest 的 `analyzer` / `metaVersion` / 选项位。

## 7. T3.15 三端重建 / 占用场景

| 场景 | Linux | Windows | macOS |
|---|---|---|---|
| 全量重建 + `CURRENT` 原子切换 | ✅ 自动测试 | ⏳ 需真机 | ⏳ 需真机 |
| 旧文件被查询句柄占用（删除失败） | ✅ 逻辑测试（删除可成功，异常路径被吞） | ⏳ 需真机验证 `IOException` 分支 | ⏳ 需真机 |
| 未引用的旧世代（上次删除失败） | ✅ 无变更运行重试清理 | ⏳ 同上 | ⏳ 同上 |
| 构建中断（Manifest / 槽未提交） | ✅ `EraStoreFuzzTests` 槽前/槽后注入 | 同上（纯托管路径） | 同上 |
| 增量提交前异常不改旧世代 | ✅ `EraStoreIncrementalTests` | 同上 | 同上 |

实现要点：

- `CleanupEraFiles` 在**每次运行结束**都执行（不再只在全量构建后），保留 `CURRENT`
  指向的文件；删除失败只记录，下次重试。
- `StoreReader` / `Merger` 用 `FileStream(FileShare.ReadWrite)` + mmap；Windows 上
  允许索引写入句柄与查询句柄并存。
- 未引用的 `.eraindex`（中断残留 / 占用残留）不会影响 `CURRENT` 解析与查询。
- **待办**：在真实 Windows / macOS 上各跑一次「打开查询句柄 → 全量重建 → 删除旧文件
  失败 → 再运行一次」；复现命令与判定见 §7.1。

### 7.1 真机复现步骤（Windows / macOS）

以下步骤假定已用平台脚本产出 `archoera-indexer` CLI / `indexer-ffi` 动态库；
语料可用 `bench/gen_corpus.py` 生成的小库（≥200 曲即可触发多段）。

```bash
# 0) 准备：生成 200 曲语料
python3 app/core/indexer/bench/gen_corpus.py --out /tmp/t315 --tracks 200 --seed 7
cd /tmp/t315

# 1) 首次全量：记录 CURRENT 指向的 .eraindex
<indexer> build --full --db library.db --index search.db
cat search.era/CURRENT

# 2) 持有旧世代查询句柄（模拟 App 正在搜索）
#    Windows: 用 indexer-ffi 打开 era_search_open；macOS: 同上（.dylib）
#    保持进程不退出，句柄不 close。

# 3) 第二次全量：新世代原子切换，旧文件因句柄占用删除失败（应只告警）
<indexer> build --full --db library.db --index search.db
cat search.era/CURRENT          # 必须指向新文件
ls search.era/*.eraindex        # 旧世代可能仍在（预期）

# 4) 释放句柄后无变更再跑一次：清理未引用的旧世代
#    先关掉步骤 2 的进程，再执行：
<indexer> build --db library.db --index search.db
ls search.era/*.eraindex        # 只应剩 CURRENT 指向的文件

# 5) 构建中断：在步骤 3 进行到一半强制结束进程，重跑 build
#    断言：CURRENT 仍可解析、查询可用、无半写 Manifest 导致的启动失败。
```

判定标准：

- 步骤 3 的删除失败**不得**让 `build` 返回非 0，也不得让 `CURRENT` 回退；
- 步骤 4 之后不得残留未引用的 `.eraindex`（Windows 上若仍残留，说明
  `StoreReader` / `Merger` 的 `FileShare` 组合仍阻止删除）；
- 步骤 5 之后查询结果与中断前一致；不一致视为原子性缺陷。

## 8. 交付与后续

本轮新增 / 变更（代码）：

- `EraStore/Incremental.cs`：追加段 + 墓碑 + 有界分层合并 + `SelectMergeSet`。
- `EraStore/DocStore.cs` v2：`mtime / lrc_mtime / lrc_size`；`StoreReader.LoadTrackState`。
- `EraStore/StoreReader.cs`：只读 mmap（匿名 RSS 不随索引体积增长）、墓碑过滤、
  `TryReadCommitGen`、`LoadTrackState`。
- `EraStore/Merger.cs`：流式 k 路归并（不再物化全段 `term → postings`）、保留
  Manifest 查询元数据（修复合并后误判全量）。
- `EraStore/QueryEngine.cs`：**bitset 候选评估**（每 atom 位图 + 布尔树位运算，
  df = popcount）、单词 atom 预计算加权 tf、词组仍按位置计算、固定大小 top-k。
  同时保留 df 缓存、惰性 AND、延后 TrackId、12 列长度切片等优化。
  **top-k 输出顺序**：未截断时保持旧扫描顺序；截断时按
  `(Score, SegmentIndex, DocId)` 升序输出，与「全排序后取前 k」完全一致
  （堆推迟到第一次真正溢出才建，避免 `hits == k` 时被建堆打乱）。
- `IndexerEngine.RunEraOnly`：生产路径不写 `search.db`；全量流式分块分析
  （`EraBuildChunkSize=200`），峰值 RSS 从近 1 GB 降到 150–175 MB。
- `indexer-ffi/Ffi.cs`：open / health 改读 Manifest。
- `Program.cs`：`query` 走 EraStore、新增 `compact`、`--legacy-fts` 调试开关。
- `LibraryDb.cs`：连接串 / ASCII 判定中立化。
- `MemoryTrim.cs`：惰性词库加载后置位、响应 / 构建收尾各做一次压缩回收；配合
  `SearchApiJson` 的 `sink` 直写与 `SetUtf8(ReadOnlySpan<byte>)`，把首查匿名 RSS
  从 ≈105 MB 降到 ≈59 MB。
- `EraStore/BuildCompactor.cs` + `IndexerEngine`：全量构建提交后按预算自动压缩
  （段数 ≥24 才触发、最多 2 轮、失败只记 warn）；30k 286 → 1 段，p50 6.95–7.31 ms。
- `EraStore/Iterators/OrIter.cs`：`Advance` 改为只抬堆顶（原实现每次线性扫全部
  子迭代器并 Rebuild，前缀展开 1 万词条时单查 3 亿次循环）。
- `EraStore/StoreReader.cs`：按表达式缓存逐 atom df（同代内复用；翻页 / 重复查询
  不再重扫倒排）。
- `EraStore/QueryEngine.cs`：截断前只收集 `ScoredHit`（段索引 + doc + 分数），
  TrackId 字符串只为最终候选生成；`EraCandidate` 改 struct；惰性 AND 评分
  （第一个缺席子节点后不再算）；宽前缀 tf 用堆归并；12 列长度一次切片复用。
- `SearchRanker.cs`：`Candidate` 首来源内联，宽命中集不再为每个候选建 List。
- `lib/services/search/search_index.dart`：`isAvailable` 只看 `search.era/CURRENT`；
  `SearchService._needsFullBuild` 同步调整。Dart 侧不再用 SQLite `search_meta` 探针。
- 测试：`tests/EraStoreIncrementalTests.cs`（新增）；`EndToEnd.EraIncremental`
  覆盖生产路径、增量、合并、残留清理；`search_index_ffi_smoke_test` 改为断言
  「不写 `search.db` + 有 `CURRENT`」。回归 **C# PASS 3170 / FAIL 0**、
  **Flutter 1229 通过**、`dart analyze lib` 0 issue。
- **第三轮（2026-11-25）**：新增 `tests/EraStoreBitsetEquivalenceTests.cs`（339 项，
  以从旧程序集恢复的迭代器为参考：表达式矩阵 / 作用域 / 前缀截断 / 词组降级 /
  墓碑 / df / 加权 tf / AND-OR 归因 / top-k 边界）；新增 `SearchConcurrencyTests`
  （N20）、Dart 版本不符降级测试（N19）、`SearchPlanTests.LongQueryContraction`
  （N15）。回归 **C# PASS 3586 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**、
  **Flutter 1231 通过**、`dart analyze lib` 0 issue；FFI 重建后冒烟通过，
  `rss_query` 56.6 / 57.3 / 58.7 MB，30k 延迟复测 7.7–9.0 / 27.9–31.4 ms。
- **第四轮（2026-11-25）**：T3.13b 删除 v7/FTS5 写入器；构建收尾自动 compact；
  T3.5 / T3.7 / T3.8 V6 歌词预处理（analyzer v14）；>255B 词条崩溃修复；
  T3.4 歌词首字母改为可选（默认关）。回归 **C# PASS 3644 / FAIL 0 /
  KNOWN-GAP 0 / BLOCKED 3**、Flutter **1235 通过**、`dart analyze lib` 0 issue；
  30k live bytes 从 220.05 MB 降到 **136.6 MB（4.45 KiB/曲，0.35×）**、
  p50 8.6–12.8 ms、p95 26.7–33.5 ms、`rss_query` 56.7–59.0 MB；
  真实曲库（117 首）歌词行级去重率 **21.8%**；
  T3.11 精确度复核让 5k narrow 桶 P@1 0.333→0.667 / NDCG 0.710→0.833，30k 不降。
- **第六轮（2026-11-25，S4）**：T4.2 LRC 分层置信度（DocStore v4 + `layer_conf`）、
  T4.3 数据包 manifest / `data_pack_id` / 能力降级、T4.1 策展跨语言词表、T4.4 Dart 契约审计。
  **最终 30k 实测**：构建 33.5 s（compact 4.2 s + defrag 0.24 s）、`fileBytes 136.6 MB`
  （4.45 KiB/曲）、eval NDCG@10 **0.9827** / P@1 1.000 / P@10 0.9054（与基线逐项相同）、
  延迟 p50 **7.78 / 8.61 ms**、p95 **31.9 / 30.1 ms**、`rss_query` **56–58 MB**；
  回归 **C# PASS 3978 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**、Flutter **1243 通过**。

- **第五轮（2026-11-25）**：T3.11 DocStore v3 归一列 + 字面精确度 / 邻近度进入桶序；
  步骤 kind 全档可达、`positioned` 由 `PhraseIter` 透传、散落多 term 真实邻近度启用。
  回归 **C# PASS 3978 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**、Flutter **1243 通过**、
  `dart analyze lib` 0 issue；体积 +0.90%（DocStore 三列）；30k eval 逐项不变、
  5k narrow 桶 P@1 0.333→0.667 / MRR 0.611→0.778 / NDCG 0.710→0.833；
  `rss_query` 56.7–59.0 MB；延迟 5 批 p50 **8.03–8.74** / p95 **26.76–33.56 ms**
  （收口后复核扩到 `scope=all` 的 Mixed 主趟、字段桶取真实列）。
  构建收尾加 **defrag**：30k `fileBytes 279.1 → 136.6 MB`（回收 142.5 MB / 231 ms），
  磁盘每曲 9.09 → **4.45 KiB**，`overheadBytes` 只剩 Manifest。

**下一步（2026-11-25 收尾后的真实剩余）**：

1. Windows / macOS 实机跑 §7.1 的占用 / 中断场景（T3.15 收尾）——本机只能覆盖 Linux。
2. T4.1 词表的人工复核：§5.5.4 要求 ≥10% 抽样 + 第二名评审签字（当前标 `seed`）。
3. N10 / N11 / N18 三项 BLOCKED：per-track 语言画像 / 单字歌词召回语义 / 计划期 df 预算，
   均为产品决策。
4. 可选：O8 位打包 / postings 紧凑化；`bench/REPORT.md` 重跑（需 `ALLOW_LONG_BENCH=1`）。
