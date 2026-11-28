// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'data_pack.g.dart';

/// 数据包与索引 / 原生库的一致性检查（T4.3 §5.5.3）。
///
/// **Dart 不重新计算哈希**：`dataPackId` 是由 `app/core/indexer/data/manifest.json`
/// 生成的编译期常量（生成命令见 `data_pack.g.dart` 文件头）。这里只把它与
/// `era_search_health` 返回的 `dataPack.id` 比对，避免两套哈希实现漂移。
///
/// 不一致时的语义（按 §5.5.3 / §5.5.6）：
/// - 原生库数据包与 Dart 常量不同 → 安装包内部版本不匹配（升级残留）→ 提示重装；
/// - 索引记录的 `dataPackId` 与当前包不同 → 索引作废，提示重建索引；
/// - 数据包缺失 / 基础表缺失 → 搜索降级（LIKE）或不可用，提示重装。
/// 这些情况都**不崩溃**：调用方保持「回退 LIKE / 索引不可用」的既有降级路径。

/// 数据包与索引不一致（安装包残留 / 升级未重建）时的可操作提示。
const String dataPackMismatchMessage = '数据包与索引不一致，请重建索引/重装应用';

/// 内置数据包整体缺失。
const String dataPackMissingMessage = '内置搜索数据包缺失，搜索已降级为基础匹配；请重装应用';

/// 基础归一化表（nfkc / accent_fold / variant_fold）缺失。
const String dataPackFoundationMessage = '搜索数据包基础表缺失，索引不可用；请重装应用';

/// 索引由旧数据包构建（storedId 与运行期 id 不同）。
const String dataPackStaleIndexMessage = '索引由旧数据包构建，请重建索引';

/// 校验 health 载荷里的数据包信息。
///
/// 返回 `null` 表示一致 / 载荷里没有该字段（旧原生库：无法校验，保持既有行为）；
/// 否则返回可直接展示给用户的可操作文案。
///
/// 纯函数，便于用假 health 载荷做单测。
String? checkDataPackHealth(
  Map<String, dynamic> health, {
  String expectedId = dataPackId,
}) {
  final dataPack = health['dataPack'];
  if (dataPack is! Map) return null;

  final id = dataPack['id'];
  final present = dataPack['present'];
  final foundationOk = dataPack['foundationOk'];
  final match = dataPack['match'];

  // 包缺失：原生侧已经按「未就绪」处理，这里只负责给出提示。
  if (present == false || id is! String || id.isEmpty) {
    return dataPackMissingMessage;
  }
  // 基础表缺失：索引无法保证归一化正确性，原生侧拒绝打开。
  if (foundationOk == false) return dataPackFoundationMessage;
  // 原生库内嵌包 ≠ Dart 常量（升级残留 / 生成器未同步）。
  if (id != expectedId) return dataPackMismatchMessage;
  // 索引记录的 id ≠ 当前包：索引作废（原生侧 Open 已返回版本不符）。
  if (match == false) return dataPackStaleIndexMessage;
  return null;
}
