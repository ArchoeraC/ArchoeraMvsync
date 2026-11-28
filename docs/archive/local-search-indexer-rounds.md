# 索引器逐轮实测归档（2026-11-24 ～ 2026-11-27）

> 从 `docs/local-search-indexer-t314-gate.md` 归档：第一轮–第十轮的代码清单与逐轮实测数字，
> 以及第十二轮（O11/O12）的明细证据。
> 当前结论、门禁与 §9 待办以 gate 文档为准；本文件只作历史证据，不再更新。

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
    旧 reader 会在位置块 flags 看到未知位 `0x02` 并拒绝——实测旧二进制对 packed 索引
    `verify` 返回 `位置块 flags / reserved 非法`，因此**新构建的索引不能被旧二进制读取**；
    索引由同一 App 本地重建 / 新增量段写入，这个代价可接受。
    `index_format` 仍为 **1**（不升版）：升版会让旧索引不可读，而 flags 位扩展已经
    提供「新读旧、旧拒绝新」的清晰边界。
  - **测试**：新增 `EraStorePackedPositionsTests`，覆盖密集（Δ=1）/ 稀疏 / 单点 0 /
    近 `int.MaxValue` 的 Δ / 300 doc 跨 3 块 × 三种编码（自动、强制 packed、强制
    varint）的 `CurrentPositions` **逐 doc 切片** / 短语相邻 / width=0 / 自动选择较小
    块体 / width=32 / 打包字节截断 / 非首 Δ=0 / 未知 flags / CRC 不符 /
    `IndexVerifier` 接受 packed 且检出位置段损坏。`EraStorePostingsTests.PositionDeltas`
    改为「自动选 packed + 强制 varint 逐字节」双断言。C# 回归
    **PASS 4118 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**。
  - **S2 收口**：`PinyinLyrics` 必须保留位置（5k 质量门），音译层仍默认免位置；
    positions 体积改由 O8 无损压缩承担。进一步收益（PForDelta 例外帧 / 跨 doc 共享
    width）需要更复杂编码，本轮不做。



## 第十二轮（2026-11-27）：O11/O12 倒排块紧凑化（S8）

> 从 checkpoint 分支并入（gate 文档「第十二轮」）：只改 **Postings 段的块体 / 位图**，
> 词典、位置、DocStore、查询语义都不变。

- **O11 doc 序列位打包**：倒排块 flags 新增 `0x02 = docs_packed`。写侧逐块比较
  `doc_delta` 的 varint 块体与位打包块体（`width u8 + ceil(doc_count*width/8)`，
  Δ 为 LSB-first），**取较小者；相等保留 varint**。`width` 为该块最大 Δ 的位宽
  （0..31；单 doc 且 delta=0 时为 0），是 PForDelta 的退化形式：不设例外帧，
  离群 Δ 抬高 width 时整块自然回落 varint。
- **O12 tf 位图裁剪**：flags 新增 `0x04 = trimmed_bitmap`。`tf_exc_count` 已给出
  docs>1 的个数，位图只需 `ceil(doc_count/8)` 字节（旧格式固定 16B）；读侧按
  flags 选长度，尾部未用位仍要求为 0（规范形式）。
- **5k 体积与构建**（同一 `library.db`，`build --full`，基线二进制与 O11/O12 二进制
  各跑一次）：

  | 变体 | fileBytes | Postings | Positions | B/曲 | 构建 |
  |---|---:|---:|---:|---:|---:|
  | 基线（varint + 16B 位图） | 25,563,565 | 4,950,439 | 16,207,455 | 5,112.7 | 9.63 s |
  | O11 doc 位打包（仅） | 25,523,261 | 4,910,135 | 16,207,455 | 5,104.7 | — |
  | **O11 + O12 位图裁剪** | **25,441,844** | **4,828,719** | 16,207,455 | **5,088.4** | 9.91 s |

  合计 **−121,721 B（file −0.476%）**，全部来自 Postings（**−2.46%**）；
  Positions / DocStore / 词典字节完全不变。O11 单独让 Postings −0.81%，O12 再让
  file −0.32%；两者都是逐块无损选择，构建耗时在噪声内。
- **质量（qbench eval，37 题，`query_set.hash = 981bcfaa…`）**：同一 `library.db`
  上对基线索引与新索引各跑一次，`queries[]` 与 `aggregate` **逐字段完全相等
  （0 差异）**，ALL P@1 0.9730 / P@10 0.9054 / NDCG@10 0.9778 不变。
- **兼容 / 重建**：新 reader 同时接受旧 varint / 16B 位图块与 packed / 裁剪块；
  用新二进制对旧基线索引执行 `verify` 通过（旧索引无需重建即可读）。旧 reader 在
  倒排块 flags 看到未知位 `0x02` / `0x04` 后拒绝——实测旧二进制对新索引 `verify`
  返回 `倒排块 flags 非法`，与 O8 的边界一致；`index_format` 仍为 **1**。
- **测试**：`EraStorePostingsTests.DocsPacked` 覆盖密集（Δ=1）自动 packed 的
  逐字节断言、强制 varint 逐字节、稀疏大 Δ 自动回落、单 doc 0（width=0）、未知
  flags 位拒绝；`TfException` / `GoldenQingTian` 以测试接缝保留旧格式逐字节夹具；
  `EraStoreNoPositionTests` 的块头解析同步支持裁剪位图 / 位打包 doc。C# 回归
  **PASS 4131 / FAIL 0 / KNOWN-GAP 0 / BLOCKED 3**。
- **仍未做**：PForDelta 例外帧（把离群 Δ 抽到例外表，让 90% 分位宽覆盖多数 doc）
  与 tf 例外值的位打包；本轮只做「整块位打包 + 位图裁剪」，收益已落在 Postings
  段，进一步压缩需按块分布再评估。
- **同轮附带（E6）**：`StoreReader.Dispose` 显式关流 + 构建前后 compacting GC +
  会话释放断开模糊词表 / 段缓存 / 结果 LRU，修复索引更新后 RSS 逐次上涨；新增
  `ResourceTests`。
