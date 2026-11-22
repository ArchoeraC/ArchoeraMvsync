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
/// 改名兼容：旧版本使用 `ArchoeraMusic` 目录，启动时由
/// [migrateLegacyDataDir] 自动迁移；旧目录无法自动删除时保留并返回路径。
String resolveDataDir() {
  final override = Platform.environment['ARCHOERA_DATA_DIR'];
  if (override != null && override.isNotEmpty) return override;
  return '${_platformDataRoot()}/$_dataDirName';
}

const String _dataDirName = 'ArchoeraMvsumc';
const String _legacyDataDirName = 'ArchoeraMusic';

String _platformDataRoot() {
  final home =
      Platform.environment['HOME'] ?? Platform.environment['USERPROFILE'] ?? '.';
  if (Platform.isMacOS) {
    return '$home/Library/Application Support';
  }
  if (Platform.isWindows) {
    // LOCALAPPDATA 尊重用户重定向（如自定义用户目录 / 迁移盘符）
    return Platform.environment['LOCALAPPDATA'] ?? '$home/AppData/Local';
  }
  return '$home/.local/share';
}

/// 把旧品牌数据目录迁移到新目录。
///
/// - `ARCHOERA_DATA_DIR` 显式覆盖时不做迁移（用户自行管理路径）；
/// - 新目录已存在（曾启动过新版本）时不覆盖，旧目录保留并返回其路径；
/// - 同卷优先 `rename`；跨卷时先复制到临时目录、校验文件数/字节数，
///   成功后再替换并删除旧目录；
/// - 返回非 null 表示旧目录仍存在（自动清理失败），调用方应提示用户。
Future<String?> migrateLegacyDataDir({String? rootDir}) async {
  final override = Platform.environment['ARCHOERA_DATA_DIR'];
  if (override != null && override.isNotEmpty) return null;
  final root = rootDir ?? _platformDataRoot();
  final oldPath = '$root/$_legacyDataDirName';
  final newPath = '$root/$_dataDirName';
  final oldDir = Directory(oldPath);
  final newDir = Directory(newPath);
  if (!await oldDir.exists()) return null;
  if (await newDir.exists()) return oldPath;
  try {
    // 同一分区上 rename 是原子的，旧目录会直接消失。
    await oldDir.rename(newPath);
    return null;
  } catch (_) {
    // 跨卷 / 权限受限：走复制 + 校验 + 删除。
  }
  final temp = Directory('$newPath.migrating');
  try {
    if (await temp.exists()) await temp.delete(recursive: true);
    await _copyDirectory(oldDir, temp);
    final from = await _dirStats(oldDir);
    final to = await _dirStats(temp);
    if (from != to) {
      await temp.delete(recursive: true);
      return oldPath;
    }
    if (await newDir.exists()) {
      await temp.delete(recursive: true);
      return oldPath;
    }
    await temp.rename(newPath);
    await oldDir.delete(recursive: true);
    return null;
  } catch (_) {
    try {
      if (await temp.exists()) await temp.delete(recursive: true);
    } catch (_) {}
    return oldPath;
  }
}

Future<void> _copyDirectory(Directory from, Directory to) async {
  await to.create(recursive: true);
  await for (final entity in from.list(recursive: true, followLinks: false)) {
    final rel = entity.path.substring(from.path.length + 1);
    final target = '${to.path}/$rel';
    if (entity is Directory) {
      await Directory(target).create(recursive: true);
    } else if (entity is File) {
      await Directory(_parentOf(target)).create(recursive: true);
      await entity.copy(target);
    } else if (entity is Link) {
      await Directory(_parentOf(target)).create(recursive: true);
      await Link(target).create(await entity.target());
    }
  }
}

String _parentOf(String path) {
  final i = path.lastIndexOf(Platform.pathSeparator);
  return i <= 0 ? path : path.substring(0, i);
}

Future<(int, int)> _dirStats(Directory dir) async {
  var files = 0;
  var bytes = 0;
  await for (final entity in dir.list(recursive: true, followLinks: false)) {
    if (entity is File) {
      files++;
      try {
        bytes += await entity.length();
      } catch (_) {}
    }
  }
  return (files, bytes);
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
