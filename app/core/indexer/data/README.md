# 搜索数据包（`app/core/indexer/data/`）

这个目录不是「随手放的 tsv」：它是**索引内容的一部分**（跨语言计划 §5.5）。
包内容变化 → `data_pack_id` 变化 → 已建索引作废并全量重建。

## 文件与来源

| 文件 | 内容 | 来源 | 许可证 |
|---|---|---|---|
| `nfkc.tsv` | 逐字符 NFKC 兼容折叠 | .NET 运行时 `string.Normalize(FormKC)`（生成器 `tools/gen_tables`） | Unicode License |
| `accent_fold.tsv` | 拉丁去音符号折叠 | Unicode NFD 规范分解（生成器） | Unicode License |
| `variant_fold.tsv` | 异体字 → 简体补齐 | Unihan `kSimplifiedVariant` + `kJapaneseOldVariant` | Unicode License |
| `ja_readings.tsv` | 汉字 → 音读 / 训读假名 | Unihan `kJapanese` | Unicode License |
| `def_en2hanzi.tsv` | 英文词 → 候选汉字码点 | Unihan `kDefinition` | Unicode License |
| `def_hanzi2en.tsv` | 汉字码点 → 英文释义词 | Unihan `kDefinition` | Unicode License |
| `kana_romaji.tsv` | 假名 → 罗马字（Hepburn 主拼 + Kunrei 变体） | **Archoera 自研**（标准罗马字事实，非第三方数据） | AGPL-3.0-or-later |
| `lexicon_cross.tsv` | 策展跨语言专名对照（艺人 / 乐队，双向） | **Archoera 自建**（公开名称的事实性对照；seed 待人工复核） | CC0-1.0 |
| `manifest.json` | 数据包清单（**生成，勿手改**） | 由本目录实际文件计算 | — |

生成 / 校验（在 `app/core/indexer` 下）：

```bash
dotnet run --project tools/gen_tables -- --manifest-only          # 重算 manifest + Dart 常量
dotnet run --project tools/gen_tables -- --manifest-only --check  # 只校验；不一致退出码 1
```

`build.sh` 在发布前会跑 `--check`：改了任一 tsv 却忘记重算 `data_pack_id` 会直接构建失败，
避免「索引与查询用的表不一致」。

许可证口径（§5.5.5 白名单）：Unicode 数据一律按
[Unicode License](https://www.unicode.org/license.txt)；`kana_romaji.tsv`
是本项目自建表，随本仓库按 AGPL-3.0-or-later 分发。逐项登记另见
`app/core/indexer/THIRD-PARTY-LICENSES.md`。

### Unicode 版本口径

`manifest.json` 的顶层 `unicode` 记录 **Unihan 派生的 UCD 版本**
（`reference/unihan/Unihan.zip`，文件头 `Unicode Version 18.0.0`）：
它决定 `ja_readings` / `def_*` / `variant_fold` 的内容。

`nfkc.tsv` / `accent_fold.tsv` 由生成工具链的 .NET 归一化实现产出，跟随运行时
自带的 ICU 数据（当前 .NET 10 / ICU 78 = Unicode 17.0）。这是**保守子集**：
Unicode 18 新增的兼容映射暂不折叠（字符保持原样，不会产生错误结果）。
升级 .NET / ICU 后重跑生成器即可补齐，此时 `unicode` 与 `data_pack_id` 都会变化。

## 重新生成

```bash
cd app/core/indexer
dotnet run --project tools/gen_tables                    # 重建全部表 + manifest + Dart 常量
dotnet run --project tools/gen_tables -- --manifest-only # 只重算 manifest / id / Dart 常量
```

Unihan 派生表需要 `reference/unihan/Unihan.zip`（仓库级 `.gitignore` 忽略，仅生成期需要）。
生成器同时写出：

- `data/manifest.json`（缩进 2 空格、LF、`files` 按 `name` 排序、无尾随空白 / 换行）；
- `app/lib/services/search/data_pack.g.dart`（`const String dataPackId = 'sha256:…'`）。

**生成是确定性的**：文件不变时重跑，两者字节完全一致。不要手改 `manifest.json`
或 `data_pack.g.dart`。

## `data_pack_id` 的规范化规则

> 唯一实现在 `app/core/indexer/DataPack.cs`（`DataPackId`），生成器与运行时
> **Link 同一份源码**，禁止两份实现。

`data_pack_id = "sha256:" + lowerhex(sha256(canonical_manifest))`，其中
`canonical_manifest` 为：

1. 顶层键顺序固定：`schema`、`name`、`version`、`unicode`、`generator`、`files`；
2. `files` 按 `name` 的序数（UTF-8 字节序）升序；
3. 每个文件对象的键顺序固定：`name`、`sha256`、`bytes`、`license`；
4. 紧凑 JSON（无缩进 / 无空格）、UTF-8 无 BOM、LF、无尾随空白、无尾随换行；
5. 未知字段与 `id` 字段不参与规范化（因此「manifest 去掉 id」天然成立）。

推论：只改 `manifest.json` 的排版 / 文件顺序 → id 不变；改任一 `sha256` / `bytes` /
`version` / `unicode` / `generator` → id 变化。

## 校验

- **构建期**：生成器写 manifest 前检查每个文件（UTF-8、两列、键非空且不重复、
  单行 ≤ 4096 B、无控制字符）。
- **运行期启动**：只做便宜检查（manifest / 文件存在 + 字节数）；索引记录
  `anchors.dataPackId`，查询打开时只比对这个字符串，不一致 → 索引作废（全量重建）。
- **全量校验**（逐文件 sha256，按需）：

  ```bash
  cd app/core/indexer
  dotnet run -c Release -- verify --data --data-dir data
  ```

  同时检查磁盘包 id 与索引器内嵌包 id 是否一致（防止「改了 tsv 没重新生成 manifest」）。
  设置页「校验数据」走同一个 `DataPack.VerifyAll()`。

## 缺失 / 损坏时的降级

| 缺失文件 | 影响 | 行为 |
|---|---|---|
| `nfkc` / `accent_fold` / `variant_fold` | 归一化正确性 | **拒绝构建 / 查询**，提示重装（不允许静默降级） |
| `def_en2hanzi` / `def_hanzi2en` | 跨语言步不可用 | 去掉能力 `cross_lingual`；健康页 / explain `missing` 报告 |
| `ja_readings` / `kana_romaji` | 日文读音步不可用 | 去掉能力 `readings`；日文仍可按假名 / 罗马音字面搜 |
| 整个 `data/`（manifest 缺失） | 只能字面匹配 | 停用索引，搜索回退 `LIKE`；提示重装 |

降级只影响**对应能力**，不崩溃、不冒充完整召回。健康 JSON 的
`dataPack.{present,missing[],corrupt[],degradedCapabilities[]}` 与顶层 `missing[]`
由 `era_search_health` 提供。
