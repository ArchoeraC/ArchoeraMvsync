// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 本地搜索索引的**健康诊断视图**（纯函数，无 FFI / 无 IO）。
///
/// 数据来源是 `era_search_health` 的 JSON（见 `app/core/indexer/SearchApi.cs`
/// 的 `HealthReady` / `HealthNotReady`）。设置页只消费这里的结构化字段，
/// 不直接读原始 Map，也**不在 UI 线程**触发健康检查——缓存由
/// `SearchService.healthSnapshot` / `refreshHealth()` 负责。
///
/// 缺失键一律解析为 `null`（设置页按「行内无数据就不显示该片段」处理），
/// 因此旧原生库 / 未就绪载荷都能安全降级，不需要版本判断。
class SearchIndexDiagnostics {
  const SearchIndexDiagnostics({
    this.generation,
    this.schemaVersion,
    this.analyzerVersion,
    this.dataPackId,
    this.dataPackMatch,
    this.dataPackPresent,
    this.dataPackFoundationOk,
    this.degradedCapabilities = 0,
    this.missingCapabilities = 0,
    this.confidenceHigh = 0,
    this.confidenceMedium = 0,
    this.confidenceLow = 0,
    this.confidenceNone = 0,
  });

  /// 已应用的索引世代（`gen`；Manifest 的实际值，0 也是合法世代）。
  final int? generation;

  /// 索引格式 / 元数据版本（`schemaVersion`）。
  final int? schemaVersion;

  /// 分析器版本（`analyzerVersion`）。
  final int? analyzerVersion;

  /// 数据包完整 id（可能带 `sha256:` 前缀；展示用 [shortDataPackId]）。
  final String? dataPackId;

  /// 索引是否由当前数据包构建（`dataPack.match`）。
  final bool? dataPackMatch;

  /// 数据包文件是否存在（`dataPack.present`）。
  final bool? dataPackPresent;

  /// 归一化基础表是否齐全（`dataPack.foundationOk`）。
  final bool? dataPackFoundationOk;

  /// 降级能力数量（`dataPack.degradedCapabilities` 去重后计数）。
  final int degradedCapabilities;

  /// 顶层 `missing` 能力数量（与降级能力同源，保留以便诊断对照）。
  final int missingCapabilities;

  final int confidenceHigh;
  final int confidenceMedium;
  final int confidenceLow;
  final int confidenceNone;

  /// 解析健康载荷；**未就绪（`ready:false`）返回 null**，设置页不显示诊断块。
  ///
  /// 容错：任何字段类型不符都按缺失处理，绝不抛异常。
  static SearchIndexDiagnostics? fromHealth(Map<String, dynamic>? health) {
    if (health == null || health['ready'] != true) return null;

    final rawPack = health['dataPack'];
    final pack = rawPack is Map ? rawPack : null;
    final id = pack?['id'];
    final rawConfidence = health['lyricsConfidence'];
    final confidence = rawConfidence is Map ? rawConfidence : null;

    return SearchIndexDiagnostics(
      generation: _asInt(health['gen']),
      schemaVersion: _asInt(health['schemaVersion']),
      analyzerVersion: _asInt(health['analyzerVersion']),
      dataPackId: id is String && id.isNotEmpty ? id : null,
      dataPackMatch: pack?['match'] is bool ? pack!['match'] as bool : null,
      dataPackPresent: pack?['present'] is bool
          ? pack!['present'] as bool
          : null,
      dataPackFoundationOk: pack?['foundationOk'] is bool
          ? pack!['foundationOk'] as bool
          : null,
      degradedCapabilities: _countList(pack?['degradedCapabilities']),
      missingCapabilities: _countList(health['missing']),
      confidenceHigh: _asInt(confidence?['high']) ?? 0,
      confidenceMedium: _asInt(confidence?['medium']) ?? 0,
      confidenceLow: _asInt(confidence?['low']) ?? 0,
      confidenceNone: _asInt(confidence?['none']) ?? 0,
    );
  }

  /// 数据包 id 的短形式：去掉 `sha256:` 之类前缀后取**前 12 个十六进制字符**。
  ///
  /// 非十六进制 id（未来可能换哈希）退化为前 12 个字符；不足 12 位原样返回。
  String? get shortDataPackId {
    final id = dataPackId;
    if (id == null) return null;
    final colon = id.indexOf(':');
    final body = colon >= 0 ? id.substring(colon + 1) : id;
    final hex = RegExp(r'^[0-9a-fA-F]+').firstMatch(body)?.group(0);
    final text = hex ?? body;
    return text.length > 12 ? text.substring(0, 12) : text;
  }

  /// 是否需要展示降级能力数量（取两处计数的较大者）。
  int get capabilityIssueCount => degradedCapabilities > missingCapabilities
      ? degradedCapabilities
      : missingCapabilities;

  /// 歌词置信度总数；为 0 时设置页不显示分布行（全 0 只是噪声）。
  int get confidenceTotal =>
      confidenceHigh + confidenceMedium + confidenceLow + confidenceNone;

  static int? _asInt(Object? value) {
    if (value is int) return value;
    if (value is num) return value.toInt();
    return null;
  }

  static int _countList(Object? value) {
    if (value is! List) return 0;
    return value.whereType<String>().where((e) => e.isNotEmpty).toSet().length;
  }
}
