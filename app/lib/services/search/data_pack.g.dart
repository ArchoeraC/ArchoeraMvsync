// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 生成文件，勿手改（T4.3 §5.5.3 同源校验）。
// 生成命令（在 app/core/indexer 下）：
//   dotnet run --project tools/gen_tables -- --manifest-only
// 来源：app/core/indexer/data/manifest.json
//   data_pack_id = 'sha256:' + sha256(规范化 manifest，去掉 id)（规则见 DataPack.cs）
// 用途：与 era_search_health 的 dataPack.id 比对；不一致 → 设置页提示重建索引 / 重装。

/// 搜索数据包 id（编译期常量，必须与索引器内嵌的数据包一致）。
const String dataPackId = 'sha256:3583c58d4fc83729badd02d9cb5766e2f991f9b93b135d13fa09b6381a94f3ea';
