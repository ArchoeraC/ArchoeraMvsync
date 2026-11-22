// ArchoeraMvsumc UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:convert';
import 'dart:io';

import '../utils/safe_file.dart';

/// 应用数据根目录（按平台约定）：
///   - Linux：`~/.local/share/ArchoeraMvsumc`（XDG）
///   - macOS：`~/Library/Application Support/ArchoeraMvsumc`
///   - Windows：`%LOCALAPPDATA%\ArchoeraMvsumc`
/// 可用环境变量 `ARCHOERA_DATA_DIR` 覆盖（与 scanner 侧一致）。
///
/// 改名后**不迁移**旧 `ArchoeraMusic` 目录：旧目录原样保留，新版本从新目录
/// 开始（避免跨盘复制/保险库文件锁导致迁移不完整）。
String resolveDataDir() {
  final override = Platform.environment['ARCHOERA_DATA_DIR'];
  if (override != null && override.isNotEmpty) return override;
  return '${_platformDataRoot()}/$_dataDirName';
}

/// 确保数据目录存在（首次启动/清空后）。失败时由各调用方自行降级。
Future<void> ensureDataDir() async {
  try {
    await Directory(resolveDataDir()).create(recursive: true);
  } catch (_) {
    // 权限/磁盘问题：后续读写会走各自的错误处理。
  }
}

const String _dataDirName = 'ArchoeraMvsumc';

String _platformDataRoot() {
  final home =
      Platform.environment['HOME'] ??
      Platform.environment['USERPROFILE'] ??
      '.';
  if (Platform.isMacOS) {
    return '$home/Library/Application Support';
  }
  if (Platform.isWindows) {
    // LOCALAPPDATA 尊重用户重定向（如自定义用户目录 / 迁移盘符）
    return Platform.environment['LOCALAPPDATA'] ?? '$home/AppData/Local';
  }
  return '$home/.local/share';
}

/// 应用「媒体库音乐目录」：读取扫描目录配置（scan_dirs.json，与
/// LibraryNotifier 持久化位置一致），有扫描目录时取第一个作为默认音乐目录
/// （媒体库即用户存放音乐的位置）；**无扫描目录时返回空串**，由调用方自行
/// 决定如何处理（不臆造平台路径——Windows/macOS/Linux 的家庭 Music 目录
/// 并不总存在，硬编码会造成跨平台问题）。
/// 「仅目录整理」等需要默认目标目录的功能使用本函数。
String defaultMusicDir() {
  try {
    final text = SafeFile.readString('${resolveDataDir()}/scan_dirs.json');
    if (text != null) {
      final dirs = (jsonDecode(text) as List<dynamic>)
          .whereType<String>()
          .where((d) => d.trim().isNotEmpty)
          .toList();
      if (dirs.isNotEmpty) return dirs.first.trim();
    }
  } catch (_) {
    // 配置损坏/不可读时回退默认，不阻断
  }
  return '';
}

/// 默认下载根目录：跟随媒体库（有扫描目录时取第一个）；无扫描目录时回退到
/// 应用数据目录下的 `downloads`（应用自己管理、三端均存在且可写，不依赖系统
/// “Music/Downloads”特殊目录是否存在）。用户可在设置页修改（保存后不再走默认值）。
String defaultDownloadRoot() {
  final music = defaultMusicDir();
  if (music.isNotEmpty) return music;
  return '${resolveDataDir()}/downloads';
}

/// 媒体库扫描目录（`scan_dirs.json`，与 LibraryNotifier 持久化位置一致）。
/// 文件缺失/损坏/无有效目录时返回空列表，不抛异常。
List<String> scanDirs() {
  try {
    final text = SafeFile.readString('${resolveDataDir()}/scan_dirs.json');
    if (text == null) return const [];
    return (jsonDecode(text) as List<dynamic>)
        .whereType<String>()
        .map((d) => d.trim())
        .where((d) => d.isNotEmpty)
        .toList();
  } catch (_) {
    return const [];
  }
}
