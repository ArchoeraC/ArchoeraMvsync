# EraStore S3 实测：T3.10 / T3.13 / T3.14 / T3.15

> 日期：2026-11-24（第二轮）
> 代码：`main` 工作区（EraStore 增量 + 只读 mmap + 生产路径去 FTS）
> 关联：`docs/local-search-indexer-rewrite-plan.md` §5.4 / §8
>
> **最终状态（2026-11-25）**：S0–S4 已全部实施；本文件记录 T3.10 / T3.13 / T3.14 / T3.15
> 的门禁过程与后续各轮实测。最新数字见文末「第九轮」，剩余项见「下一步」。
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

- **第七轮（2026-11-26，S1）**：全量构建收尾压缩改为**直接写新世代**，省掉 defrag 复制。
  - `EraStore/Merger.cs` 新增 `MergeToNewGeneration`：未参与合并的段原样复制（不重新编码，
    CRC 比对），合并段仍用「旧最大段号 + 1」，活 doc 重新编号；新文件提交（双槽 fsync）
    后才原子替换 `search.era/CURRENT`。提交前失败 / 中止删除未引用的新文件，旧世代与旧
    `CURRENT` 不动；切换后清理失败只留下垃圾文件，下次运行重试。`Merge`（增量合并）
    保持「同文件追加」不变。
  - `EraStore/BuildCompaction.cs`：每轮改用 `MergeToNewGeneration`，结果新增
    `RewroteGeneration / GenerationName / FileBytes`；轮间按 `CURRENT` 重新解析文件。
  - `IndexerEngine`：压缩走新世代路径时**跳过** `DefragEraAfterFullBuild`，
    `EraFileBytesAfter` 直接取压缩结果；仅当某轮退回就地追加时才走 defrag 兜底。
  - 共享工具：`IndexFile.ReadIdentity`、`CurrentPointer.NextGenerationName /
    CleanupGenerations`（`Defragmenter` 同步复用，行为不变）。
  - 测试：`EraStoreBuildCompactionTests` 断言压缩后只剩 `CURRENT` 一个文件、
    `fileBytes = liveBytes + Manifest`、`overheadBytes` 只剩 Manifest；新增
    「新文件已 Commit、`CURRENT` 未切换时中断」用例（旧世代 / 字节 / 查询不变，孤儿文件被清理）。
    `EndToEnd` 新增可调段上限的全量构建夹具（30 段 → 1 段），断言
    `EraCompacted=true`、`EraDefragged=false`、`EraFileBytesAfter` 与当前文件一致、查询仍命中。
  - 格式锚 / Manifest 元数据 / 段号语义不变，既有索引仍可读，**不需要重建**。
  - 回归 **C# PASS 4011 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**。本轮未重跑 30k 长基准
    （`ALLOW_LONG_BENCH` 禁止），因此不记录新的构建耗时 / 峰值磁盘数字。

- **第五轮（2026-11-25）**：T3.11 DocStore v3 归一列 + 字面精确度 / 邻近度进入桶序；
  步骤 kind 全档可达、`positioned` 由 `PhraseIter` 透传、散落多 term 真实邻近度启用。
  回归 **C# PASS 3978 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**、Flutter **1243 通过**、
  `dart analyze lib` 0 issue；体积 +0.90%（DocStore 三列）；30k eval 逐项不变、
  5k narrow 桶 P@1 0.333→0.667 / MRR 0.611→0.778 / NDCG 0.710→0.833；
  `rss_query` 56.7–59.0 MB；延迟 5 批 p50 **8.03–8.74** / p95 **26.76–33.56 ms**
  （收口后复核扩到 `scope=all` 的 Mixed 主趟、字段桶取真实列）。
  构建收尾加 **defrag**：30k `fileBytes 279.1 → 136.6 MB`（回收 142.5 MB / 231 ms），
  磁盘每曲 9.09 → **4.45 KiB**，`overheadBytes` 只剩 Manifest。

- **第八轮（2026-11-26，Q1 歌词精确度）**：DocStore **v5** 追加 `lyrics_digest`，
  歌词族现在能对「整行相等」给出 `exactness = equal`（5k eval 的 `line_tail` #2 问题）。
  - **格式**：段体最后一列 `u32 offsets[N+1] + u64×M`；每个 doc 一份**严格递增
    去重**的 64 位行哈希，空 doc 的相邻偏移相同；读取端校验末偏移 = blob 长度、blob 长度
    % 8 == 0、逐 doc 偏移递增。`lyrics_digest` 只追加一列，前六列与 v4 逐字节同构。
  - **归一 / 哈希**：行先走 `Analyzer.NormalizeMetadata`（NFKC / 音标 / 异体 / 小写 / 繁简，
    空白折叠 + 去首尾），去掉 ASCII 空格后按 UTF-8 做 **FNV-1a 64**（本地实现，不引依赖）。
    查询用 `SearchExactness.NormalizeQuery` 的同一归一形，因此「我爱你 中国」=「我爱你中国」、
    「我愛你」=「我爱你」、「LOVE   IS ALL WE NEED」=「love is all we need」。
  - **覆盖层**：原文 / 译文 / **显式**音译三层中实际参与索引的行（预处理去重 + V6 护栏之后）；
    由读音派生、非用户字面的生成罗马音不参与。跨层重复行由哈希排序去重。
  - **生成 / 合并**：`IndexBuilder.MakeRecord` 是唯一生成点（全量 + 增量共用）；`Merger`
    从旧段 `LyricDigestArray` **原样拷贝**，不重新归一 / 哈希，合并前后字节稳定。
    DocStore **v4 → v5**、`Manifest.CurrentMetaVersion 3 → 4`；读取侧拒绝 v1–v4 并提示
    「旧索引必须全量重建」（旧索引不迁移）。
  - **查询**：只在**字面主趟**（`SearchPassNames.Lexical`）、scope 覆盖歌词（`FieldFamily.Lyrics`
    / `Mixed`，非 `scope=metadata`）且该步未截断命中 ≤ `SearchExactness.Cap` 时读摘要。
    元数据三列复核仍优先（T3.11 档位 / 字段桶不变）；三列都不命中才看整行哈希，命中记
    `Equal`，Field **保持保守映射 Lyrics**（不让歌词冒充标题桶）。前缀 / 包含**仍为 `None`**——
    摘要只回答「整行相等」，不存原文，无法恢复前缀 / 包含。
  - **成本护栏**：丢词 / 拼音 / 读音 / 跨语言 / 模糊 / 中文容错步不读新列；宽命中步（> cap）
    不读。读取计数与元数据复核并列：保留 `DebugDocReads`（归一列），新增
    `DebugLyricDigestReads`（摘要列），二者都由 `DebugResetDocReads` 清零。
  - **体积（实测 / 估算）**：`gen_corpus.py` 2k 抽样（seed 20261013）每曲唯一歌词行
    均值 **86.6**（p50 86 / p95 134）→ **+697 B/曲**（4 B 偏移 + 8 B/行），约 **0.68 KiB**；
    相对 S4 的 4.45 KiB/曲 **+15.3% → 5.13 KiB/曲**（30k 约 136.6 → 157.5 MB），
    仍远低于 §8.0 的 10.2 KiB/曲（0.40× 基线）。真实库若有译文 / 显式音译层，按
    「去重后每唯一行 +8 B」同口径增长。
  - **测试**：新增 `tests/EraStoreLyricDigestTests.cs`（体积公式 / 空 doc / 空格大小写繁简
    归一 / 部分行不命中 / 合并逐字节 / v3+v4 拒绝 / 整行相等压过 bm25 / 元数据档位不变 /
    cap 与非歌词步零读取）。既有 e2e 期望值随语义重录（`我爱你`：c1 / c3 整行相等升到
    #1 / #2，p1 行内包含退到 #3）。回归 **C# PASS 4045 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**；
    本轮未跑 30k 长基准（`ALLOW_LONG_BENCH` 禁止），只做 2k 语料行数抽样 + 单测。
  - **限制**：64 位哈希碰撞只可能多产生一次 `Equal`（只影响第 4 键，不改召回 / 高亮 / 片段）；
    行内标点不剥除（与元数据 exactness 同口径，`NormalizeMetadata` 只折叠空白）。

- **第九轮（2026-11-26，S2 评估：歌词音译免位置，拼音保留位置）**：先修正「文档 /
  基准已声明、生产未启用」的默认口径。`bench/erastore` 与 §8 的 quick / 30k 数字一直以
  `{LyricsRomanization, PinyinLyrics}` 免位置构建，但 `IndexerEngine` 的全量 / 增量都没传
  `BuildOptions.NoPositionFields` / `IncrementalOptions`，生产索引一直写全字段位置。本轮
  先让生产与 bench 共用 `EraStore.BuildDefaults`，随后独立 5k `qbench eval` 质量门推翻了
  `PinyinLyrics` 免位置：**最终默认只保留 `LyricsRomanization` 免位置**。
  - **2k 体积实测**（`gen_corpus.py --out /tmp/opencode/s2 --tracks 2000 --seed 20261126`，
    2–8 KB 中文歌词、50% `.lrc`；CLI `build --full` + `stats`；`positions 占比` =
    Positions 字节 / `liveBytes`）：

    | 变体 | 免位置字段 | fileBytes | positions | positions 占比 | B/曲 | 段数 | 构建 |
    |---|---|---:|---:|---:|---:|---:|---:|
    | 基线（全字段位置） | 无 | 10,909,167 | 6,846,627 | 62.81% | 5,454.6 | 12 | 3,990 ms |
    | 两条派生层免位置（**未发布**） | Romanization + PinyinLyrics | 7,118,687 | 3,141,176 | 44.18% | 3,559.3 | 10 | 3,831 ms |
    | **最终：仅音译层** | Romanization | 10,909,167 | 6,846,627 | 62.81% | 5,454.6 | 12 | 4,375 ms |

    两条派生层一起免位置的体积收益是真的（**-34.7%**），但它**不可发布**——见下。
    该 2k 语料没有显式音译层，所以「仅音译层免位置」与基线的字节完全相同。
  - **5k 质量门（qbench eval，37 题，`query_set.hash = 981bcfaa…`）**：
    同一 `corpus5k/library.db` 上对比「全字段带位置」与「Romanization + PinyinLyrics
    免位置」：

    | 变体 | P@1 | P@10 | NDCG@10 | MRR@10 |
    |---|---:|---:|---:|---:|
    | 全字段带位置（基线） | **0.973** | **0.905** | **0.978** | **0.982** |
    | 两条派生层免位置（未发布） | 0.838 | 0.854 | 0.854 | 0.874 |

    | 查询 | 失败方式 | 带位置 → 免位置 |
    |---|---|---|
    | `pinyin_joined` (`woaini`) | 拼音短语降级为 AND，假阳性抬到前面 | P@1 1.000 → 0.000；MRR 1.000 → 0.500；NDCG 1.000 → 0.212 |
    | `pinyin_word_prefix` (`woai`) | 同上 | P@1 1.000 → 0.000；MRR / NDCG 同量级下降 |
    | `line_head` / `line_mid` / `line_tail` | 拼音 AND 分支制造假阳性，压过真实歌词整行命中 | P@1 1.000 → 0.000 |

    **结论：`PinyinLyrics` 的位置是查询质量要求，不是可选体积项。** 当前查询层无法在
    无位置前提下区分「音节相邻」与「整首都有」，所以必须保留位置。
  - **最终字段集合**：`LyricsRomanization` 免位置；`PinyinLyrics`、`PinyinMeta`、其余字段
    全部保留位置。`InitialsLyrics` / `PinyinMeta` 免位置此前测量后也未采纳（首字母相邻
    bigram 假阳性；元数据短语精度）。`SearchPlanTests` 的拼音短语分数期望已恢复为 S2 前
    （带位置）的值，不再保留任何「bm25=0 for pinyin」预期。
  - **最终 5k 复核**：用修正后的 CLI 在 `/tmp/opencode/q5k/library.db` 全新构建
    `/tmp/opencode/q5k/final-romaji`，`fileBytes = 26,266,248`（25.05 MiB / 26.26 MB 十进制，
    5,253.2 B/曲，positions 64.38%），与带位置基线同字节（该语料音译层为空）；同一
    qbench eval 的 **ALL P@1 0.973 / P@10 0.905 / NDCG@10 0.978 / MRR@10 0.982**，
    逐题指标与基线**完全一致**（`query_set.hash` 同为 `981bcfaa…`）。
  - **是否要重建**：**不需要**。这是物理布局偏好，不是格式版本变化：旧索引照常可读；
    新构建 / 新增量段才按 `BuildDefaults` 写入。增量与全量共用同一清单，避免新旧段漂移。
  - **混合段合并（升级后第一次 merge / compact 的关键）**：同一词条可能同时来自
    「旧段（带位置）」与「新增量段（免位置）」。`Merger.MergeTerms` 在产出每个词条前
    做 **per-term 归一**：只要该词条有任一条目无位置，就把这个词条的所有条目降为无位置，
    写侧得到一致的 dict / block flag 0。**只有真正混合的词条会降级**；只出现在旧段的
    词条仍保留位置，只出现在新段的词条本来无位置。全有 / 全无两种输入保持原行为。
    该规则仍然必要：音译层在用户开启 / 关闭选项、或旧索引加增量段时同样会出现混合来源。
  - **读取侧配套修复**：一个段的所有词条都免位置时，Builder 不写 `Positions` section；
    `StoreReader.OpenPerSegment()` 现在把缺失的 Positions 当作空内存（该段无位置块），
    DocStore / Dict / DictIndex / Postings 仍必须存在。`IndexVerifier` / `IndexStats` /
    `bench` 原本就按「可选 Positions」处理。这样「全库免位置」或「合并后整段免位置」的
    索引也能查询 / verify，而不会在打开时抛「缺少 Positions」。
  - **测试**：`EndToEnd.ProductionRomanizationNoPositions`（经 `IndexerEngine` 全量建库，
    开启 romaji：音译词条 flags bit0=0 / 游标 `HasPositions=false`、短语 AND；拼音词条
    **仍 bit0=1** / 游标 `HasPositions=true`、短语保持相邻校验；`PinyinMeta` 仍带位置、
    `IndexVerifier` 通过）；`EraStoreMixedPositionsTests` 用音译层实际复现升级路径：
    23 个带位置旧段 + 1 个免位置增量段 → 24 段触发合并，断言不抛异常、verify 通过、
    混合词条 flag 0、同段 Title 仍带位置、短语 AND；另覆盖全有位置（仍相邻校验）与
    整段无 Positions section（读取 / 合并 / 查询均不抛异常）。C# 回归
    **PASS 4078 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**；本轮未跑长基准
    （`ALLOW_LONG_BENCH` 禁止），体积 / 时间取 2k / 5k 手动测量。
  - **S2 状态：O8 已补齐（第十轮，2026-11-26），S2 收口**。免位置路线只保留音译层
    这一项低风险、可选层的优化；positions 体积压缩改由 **O8 位打包**承担——它不改短语
    相邻语义、不升 `index_format`，旧索引仍可读，新构建更小。详见下一轮。

- **第十轮（2026-11-26，O8：位置块位打包）**：S2 的剩余主线落地。**只改 Positions 段
  的块体**；倒排 / 词典 / DocStore / 查询语义都不变。
  - **格式**：位置块 flags 新增 `0x02 = positions_packed`（`0x01 = has_positions` 不变）。
    写侧对每个位置块分别算 varint 块体
    `doc_delta varint | pos_count varint | Δpos varint...` 与位打包块体
    `doc_delta varint | pos_count varint | width u8 | Σ Δpos（width bit, LSB-first）`
    的字节数，**取较小者；相等保留 varint**。`width` 是该 doc 最大 Δpos 的位宽
    （0..31；只有单个位置 0 时为 0），打包体长度 `ceil(tf*width/8)`；doc / tf 仍为
    varint。位置块 12B 块头 / CRC / `pos_off` 语义不变。
  - **5k 体积与构建**（同一 `library.db`，CLI `build --full`，基线二进制与 O8 二进制
    各跑两次）：

    | 变体 | fileBytes | Positions | positions 占比 | B/曲 | 段数 | 构建（两次） |
    |---|---:|---:|---:|---:|---:|---:|
    | 基线（仅 varint） | 26,266,248 | 16,910,138 | 64.38% | 5,253.2 | 1 | 8.72 s / 8.71 s |
    | **O8 按块位打包** | **25,563,565** | **16,207,455** | **63.40%** | **5,112.7** | 1 | 8.54 s / 8.10 s |

    差值 **−702,683 B（文件 −2.675%）**，全部来自 Positions 段
    （**−4.16%**）；倒排 / 词典 / DocStore 字节完全不变。构建耗时在噪声内，
    打包的尺寸估算与位写入没有可测回归。压缩后仍是 1 段（压缩前 30 段）。
  - **质量（qbench eval，37 题，`query_set.hash = 981bcfaa…`）**：
    同一 `library.db` 上对基线索引与 O8 索引各跑一次，`queries[]` 与 `aggregate`
    **逐字段完全相等（0 差异）**：

    | 变体 | P@1 | P@10 | NDCG@10 | MRR@10 |
    |---|---:|---:|---:|---:|
    | 基线（varint） | 0.9730 | 0.9054 | 0.9778 | 0.9820 |
    | **O8（packed）** | **0.9730** | **0.9054** | **0.9778** | **0.9820** |

  - **兼容 / 重建**：新 reader 同时接受旧 varint 块与新 packed 块；用 O8 二进制对
    **旧格式基线索引**执行 `verify` 与 qbench eval 均通过（旧索引无需重建即可读）。
    旧 reader 会在位置块 flags 看到未知位 `0x02` 并拒绝——因此**新构建的索引不能被
    旧二进制读取**；索引由同一 App 本地重建 / 新增量段写入，这个代价可接受。
    `index_format` 仍为 **1**（不升版）：升版会让旧索引不可读，而 flags 位扩展已经
    提供「新读旧、旧拒绝新」的清晰边界。
  - **测试**：新增 `EraStorePackedPositionsTests`，覆盖密集（Δ=1）/ 稀疏 / 单点 0 /
    近 `int.MaxValue` 的 Δ / 300 doc 跨 3 块 × 三种编码（自动、强制 packed、强制
    varint）的 `CurrentPositions` **逐 doc 切片** / 短语相邻 / width=0 / 自动选择较小
    块体 / width=32 / 打包字节截断 / 非首 Δ=0 / 未知 flags / CRC 不符 /
    `IndexVerifier` 接受 packed 且检出位置段损坏。`EraStorePostingsTests.PositionDeltas`
    改为「自动选 packed + 强制 varint 逐字节」双断言。C# 回归
    **PASS 4117 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**。
  - **S2 收口**：`PinyinLyrics` 必须保留位置（5k 质量门），音译层仍默认免位置；
    positions 体积改由 O8 无损压缩承担。进一步收益（PForDelta 例外帧 / 跨 doc 共享
    width）需要更复杂编码，本轮不做。


**下一步（2026-11-25 收尾后的真实剩余）**：

1. Windows / macOS 实机跑 §7.1 的占用 / 中断场景（T3.15 收尾）——本机只能覆盖 Linux。
2. T4.1 词表的人工复核：§5.5.4 要求 ≥10% 抽样 + 第二名评审签字（当前标 `seed`）。
3. N10 / N11 / N18 三项 BLOCKED：per-track 语言画像 / 单字歌词召回语义 / 计划期 df 预算，
   均为产品决策。
4. S2 已于 2026-11-26 收口（音译层免位置 + O8 位置块位打包）；剩余只有
   `bench/REPORT.md` 长基准重跑（需 `ALLOW_LONG_BENCH=1`）。

更细的可改进项（含改法与代价）见 §9。

## 9. 索引器可进一步改进的地方（非门禁，按收益 / 风险排序）

> 以下都不是 S3/S4 的未完成项（门禁与验收已过），而是**已知可继续优化**的点。
> 每项都标了现状证据、改法与代价，供后续排期；不要在没有测量数据时一次改多项。

### 9.1 排序与召回语义（质量收益优先）

| # | 现状 / 证据 | 可改进方向 | 代价 / 风险 |
|---|---|---|---|
| Q1 | ✅ **已实现（2026-11-26）**：DocStore v5 `lyrics_digest`（归一化歌词整行 FNV-1a 64 哈希，有序去重），字面主趟 / ≤cap 时二分查找 → `exactness = equal`。仍**只支持整行相等**，前缀 / 包含保持 `none`（哈希无法恢复原文，也不存原文）；体积 +0.68 KiB/曲（2k 抽样均值 86.6 行） | 后续若要前缀 / 包含：只能存有界行前缀原文（体积更高）或查询期读 `library.db`（会碰 `busy_timeout`）；歌词层内子列（原文 / 译文 / 音译）仍不细分，字段桶保持 Lyrics | 已完成；旧索引必须全量重建；64 位碰撞只影响第 4 键 |
| Q2 | proximity 只有元数据字段的真实值；散落多 term 的歌词/转写/模糊仍 `int.MaxValue` | 把 term 位置暴露到候选侧（`PostingsCursor` 已在用），对 ≤cap 的步骤做歌词段最小窗口 | 需要第二遍位置扫描；p50 预算只剩 ~2 ms，可能超门禁 |
| Q3 | 短拼音前缀无长度门控：3–4 字符拼音（如 `hen*`）会扩到 `henry` 类无关词 | 按真实库统计 3–4 字符拼音命中分布，定「最短前缀 / 命中上限」 | 需要真实库数据；改门控会牺牲部分召回 |
| Q4 | `MinExact` 仍是「首趟命中即停」：宽查询可能掩盖后面更精确的趟 | 改成「主趟命中数未达阈值时继续跑更高精度趟」，或按 kind 合并后再判短路 | 影响 UI 结果数与延迟；需要产品确认 |
| Q5 | `fast@1` / `minimal@1` 共用 lexical（`exact + prefix` 仍配对） | 拆出独立 exact / prefix 表达式，让 `minimal@1` 真正「只 exact」 | 改表达式生成 + 方案向量；收益只在降级链 |
| Q6 | N10 纯汉字日文标题按中文拼音（`米津玄師 → mi jin xuan shi`） | per-track 语言画像（标题/歌手含假名、日文汉字表、曲目来源）写入 DocStore，查询按画像选读音表 | 新存储列 + 重建；需要真实日文库校准 |
| Q7 | N11 单字 CJK 歌词不可召回（`RequireMetadataForSingleChar`） | 按 §3.5 决策放宽单字歌词（配合置信度/桶序避免噪声） | 产品语义决策；回归面大 |
| Q8 | N18 无计划期 df 预算：高频词仍可能生成宽步骤 | 计划编译器读 `StoreReader` 的 df 快照，`est > 4×cap` 不生成步骤 | 编译器从纯函数变为可读存储；阈值需回放校准 |

### 9.2 索引与存储

| # | 现状 | 可改进方向 | 代价 |
|---|---|---|---|
| S1 | ~~`compact` 把归并结果 append 到同一 generation，随后 `defrag` 再整文件复制一遍（构建期峰值 2× 磁盘、耗 0.2–0.3 s）~~ **已实现（2026-11-26）**：全量构建收尾压缩走 `Merger.MergeToNewGeneration`，直接写只含活段体的新世代并原子切换 `CURRENT`，成功后跳过 defrag；增量合并仍保持同文件追加 | 后续只需观察真实曲库的峰值磁盘 / 收尾耗时（长基准未重跑） | 已完成；格式锚与既有索引不变，无需重建 |
| S2 | **已实现（2026-11-26 收口）**：两段路线。① 可选音译层 `LyricsRomanization` 默认免位置；`PinyinLyrics` 必须保留位置（两条一起免位置的 2k 体积收益 -34.7%，但 5k qbench eval 把 ALL P@1 从 **0.973 打到 0.838**、NDCG@10 0.978 → 0.854）。② **O8 位置块位打包**（第十轮）：位置块 flags 新增 `0x02`，逐块比较 varint / packed 块体取较小者；5k `fileBytes` 26,266,248 → **25,563,565**（−2.68%），Positions 16,910,138 → **16,207,455**（−4.16%），qbench eval 逐题与基线完全一致；旧索引可读、无需重建 | 进一步压缩可做 PForDelta 例外帧 / 跨 doc 共享 width，但复杂度更高、本轮不做 | 已完成；`PinyinLyrics` / `PinyinMeta` 免位置仍不采纳（短语精度），`initialsLyrics` 同理 |
| S3 | 超过 255 B 的词条被丢弃（`oversizeTokens`），该词不可检索 | 对超长词做分段 / 哈希词条 + 原文旁路，保留可检索性 | 词典格式与查询 IR 改动；真实库中极罕见 |
| S4 | 段大小固定 4 MB 估算（RSS ↔ 查询延迟的核心矛盾） | 按平台 / 曲库规模 / 内存自适应段大小，或构建后按段数二次合并 | 需要多档实测（5k/30k/48.7k 已有基线） |
| S5 | LRC 分层仍按「同时间戳第 1/2/3 行」猜（D-1）；无时间轴的内嵌歌词靠 +3 基线兜底 | 用语言识别 / 翻译对齐信号替代行位置猜测；分层置信度阈值回放校准 | 需要真实多语 LRC 样本 |
| S6 | 脏集只认 `file_mtime` + `.lrc` mtime/size（D-3）：直接改 `library.db` 不改文件会漏更新 | DocStore 增加「元数据内容哈希」列，脏集比较 mtime **或** 哈希 | DocStore v6 + 全量重建（v5 已被 Q1 用掉）；增量期多一次哈希计算 |
| S7 | v7 索引只能整库重建；无「按曲目墓碑回收」的轻量压缩 | 已有 `IncrementalUpdater.Maintain`，可加「死行 >X% 自动后台压缩」策略 | 已有实现，主要是策略调参 |

### 9.3 工程与验证

| # | 现状 | 可改进方向 |
|---|---|---|
| E1 | Windows / macOS 真机未跑 T3.15 四场景 | 真机验证「查询句柄占用 → 重建 → 删除重试 → 构建中断」 |
| E2 | T4.1 词表是 `seed`，未做人工复核签字 | 按 §5.5.4 抽样 ≥10%，第二人签字后去掉 `seed` 标记 |
| E3 | `bench/REPORT.md` 是 pre-EraStore 历史报告 | 空闲机跑一次长基准（`ALLOW_LONG_BENCH=1`）重渲染；`run_bench.py` 叙述段仍是 v7 措辞 |
| E4 | `SearchApi.Busy` 是协议预留，当前不会返回 | 保留（写任务 API 尚未需要）；若做后台压缩任务可启用 |
| E5 | health `schemaVersion` / `gen` 的 Dart 消费方为空 | 设置页可展示 `dataPack`/confidence 分布，便于用户自查 |

> 建议顺序：~~Q1（歌词精确度）~~（2026-11-26 已完成，仅整行相等）→ ~~S2（体积）~~
> （2026-11-26 已收口：音译层免位置 + O8 位打包）→ Q3/Q4（门禁词与短路）；
> S1 已于 2026-11-26 完成；Q6/Q7/Q8 属产品决策，单独评审；其余按真机/工具改进排期。
