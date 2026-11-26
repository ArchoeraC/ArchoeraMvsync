// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:async';
import 'dart:io';

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../l10n/l10n.dart';
import '../../services/search/search_models.dart';
import '../../services/search/search_service.dart';
import '../../stores/app_prefs.dart';
import '../widgets/common/toast.dart';
import 'settings_widgets.dart';

import 'package:archoera_mvsync/eta/icon/eta_icons.dart';

/// 搜索分类：本地曲库搜索索引（**默认关闭**，设计文档 §2.4 / §9.3）。
///
/// - 总开关 `启用本地曲库搜索索引`（默认关）：开启后在后台按需构建 `search.db`，
///   搜索切换到按相关度（BM25）排序的排行；关闭即回退现有 `LIKE`，索引默认保留。
/// - 子开关 `索引 .lrc 侧车歌词` 与派生列开关（元数据拼音 / 歌词拼音 / 歌词首字母 /
///   歌词罗马音，全部默认关）：只写偏好、**不自动重建**；现有索引保留，下次重建生效。
/// - 动作：重建索引 / 删除索引（二次确认）；展示构建进度、状态与上次结果。
///
/// 索引是**派生数据**：默认关闭零成本；构建失败不阻断搜索（自动回退 `LIKE`）。
class SearchSection extends ConsumerStatefulWidget {
  const SearchSection({super.key});

  @override
  ConsumerState<SearchSection> createState() => _SearchSectionState();
}

class _SearchSectionState extends ConsumerState<SearchSection> {
  /// 总开关切换：写偏好（同步刷新快照）+ 触发/停止索引服务。
  void _onToggleEnable(bool value) {
    ref.read(appPrefsProvider.notifier).setSearchIndex(enabled: value);
    if (value) {
      // 空闲时按需构建（后台 isolate，不阻塞 UI；失败静默回退 LIKE）。
      unawaited(_ensureIndexThenRefresh());
    } else {
      // 停止接管、回退 LIKE；索引默认保留（删索引走独立动作）。
      SearchService.instance.deactivate();
    }
    setState(() {});
  }

  /// 触发按需构建并在结束后刷新状态行（模块缺失等失败的 status 变更不经
  /// [SearchService.progress]，需显式刷新）。
  Future<void> _ensureIndexThenRefresh() async {
    await SearchService.instance.ensureIndex();
    if (mounted) setState(() {});
  }

  /// 派生列子开关：只写偏好，**不自动重建**。
  ///
  /// 现有索引仍可继续查询（关闭后旧列只是不再被新构建写入）；新选项在用户下次
  /// 「重建索引」或索引因其它原因全量重建时生效。这样用户可以在不等待重建的
  /// 情况下先调整设置。
  void _setIndexOption({
    bool? indexLrc,
    bool? pinyin,
    bool? lyricsPinyin,
    bool? romajiLyrics,
    bool? initialsLyrics,
  }) {
    ref
        .read(appPrefsProvider.notifier)
        .setSearchIndex(
          indexLrc: indexLrc,
          pinyin: pinyin,
          lyricsPinyin: lyricsPinyin,
          romajiLyrics: romajiLyrics,
          initialsLyrics: initialsLyrics,
        );
    setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final l10n = context.l10n;
    final prefs = ref.watch(appPrefsProvider);
    final enabled = prefs.enableLocalSearchIndex;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SettingSection(
          title: l10n.settingsSectionSearchIndex,
          note: l10n.settingsSearchIndexNote,
          children: [
            SettingSwitchTile(
              icon: EtaIcons.search3,
              title: l10n.settingsSearchIndexEnable,
              subtitle: enabled
                  ? l10n.settingsSearchIndexEnableOn
                  : l10n.settingsSearchIndexEnableOff,
              value: enabled,
              onChanged: _onToggleEnable,
            ),
            SettingSwitchTile(
              icon: EtaIcons.fileMusicOutline,
              title: l10n.settingsSearchIndexLrc,
              subtitle: l10n.settingsSearchIndexLrcDesc,
              value: prefs.indexLrc,
              enabled: enabled,
              onChanged: enabled
                  ? (value) => _setIndexOption(indexLrc: value)
                  : null,
            ),
            SettingSwitchTile(
              icon: EtaIcons.search3,
              title: l10n.settingsSearchIndexPinyin,
              subtitle: l10n.settingsSearchIndexPinyinDesc,
              value: prefs.pinyin,
              enabled: enabled,
              onChanged: enabled
                  ? (value) => _setIndexOption(pinyin: value)
                  : null,
            ),
            SettingSwitchTile(
              icon: EtaIcons.music2Outline,
              title: l10n.settingsSearchIndexLyricsPinyin,
              subtitle: l10n.settingsSearchIndexLyricsPinyinDesc,
              value: prefs.lyricsPinyin,
              enabled: enabled,
              onChanged: enabled
                  ? (value) => _setIndexOption(lyricsPinyin: value)
                  : null,
            ),
            SettingSwitchTile(
              icon: EtaIcons.abcOutline,
              title: l10n.settingsSearchIndexInitials,
              subtitle: l10n.settingsSearchIndexInitialsDesc,
              value: prefs.initialsLyrics,
              enabled: enabled,
              onChanged: enabled
                  ? (value) => _setIndexOption(initialsLyrics: value)
                  : null,
            ),
            SettingSwitchTile(
              icon: EtaIcons.translateOutline,
              title: l10n.settingsSearchIndexRomaji,
              subtitle: l10n.settingsSearchIndexRomajiDesc,
              value: prefs.romajiLyrics,
              enabled: enabled,
              onChanged: enabled
                  ? (value) => _setIndexOption(romajiLyrics: value)
                  : null,
            ),
          ],
        ),
        const SizedBox(height: 20),
        // 关闭时**不触碰** SearchService（不构造 / 不读本地索引）。
        if (enabled)
          ValueListenableBuilder<IndexerProgress?>(
            valueListenable: SearchService.instance.progress,
            builder: (context, progress, _) => _buildStatusSection(
              l10n,
              scheme,
              service: SearchService.instance,
              progress: progress,
            ),
          )
        else
          _buildStatusSection(l10n, scheme, service: null, progress: null),
      ],
    );
  }

  /// 状态与维护：状态行（+ 构建进度）+ 重建 / 删除动作。
  Widget _buildStatusSection(
    AppLocalizations l10n,
    ColorScheme scheme, {
    required SearchService? service,
    required IndexerProgress? progress,
  }) {
    final status = service?.status;
    final statusText = status == null
        ? l10n.settingsSearchIndexStatusDisabled
        : switch (status.state) {
            LocalSearchState.ready => l10n.settingsSearchIndexStatusReady,
            LocalSearchState.building =>
              l10n.settingsSearchIndexStatusBuilding,
            LocalSearchState.error =>
              l10n.settingsSearchIndexStatusError(reason: status.message),
            LocalSearchState.disabled =>
              l10n.settingsSearchIndexStatusPending,
          };
    final detailParts = <String>[];
    if (service != null) {
      final size = _indexSizeText();
      if (size != null) {
        detailParts.add(l10n.settingsSearchIndexSize(size: size));
      }
    }
    final last = service?.lastResult;
    if (last != null) {
      detailParts.add(l10n.settingsSearchIndexLastResult(
        indexed: last.indexed,
        duration: (last.durationMs / 1000).round(),
      ));
    }
    return SettingSection(
      title: l10n.settingsSectionSearchIndexStatus,
      children: [
        SettingTile(
          icon: EtaIcons.storageOutline,
          title: statusText,
          subtitle: detailParts.join(' · '),
          enabled: service != null,
          trailing: const SizedBox(width: 8),
        ),
        if (progress != null)
          SettingTile(
            icon: EtaIcons.refreshOutline,
            title: l10n.settingsSearchIndexStatusBuilding,
            subtitle: l10n.settingsSearchIndexProgress(
              phase: progress.phase,
              processed: progress.processed,
              total: progress.total,
            ),
            trailing: SizedBox(
              width: 120,
              child: LinearProgressIndicator(
                value: progress.total > 0 ? progress.fraction : null,
                minHeight: 4,
              ),
            ),
          ),
        const Divider(height: 6),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
          child: Row(
            children: [
              const Spacer(),
              SizedBox(
                height: 28,
                child: TextButton.icon(
                  style: TextButton.styleFrom(
                    visualDensity: VisualDensity.compact,
                  ),
                  onPressed: service == null ? null : _rebuild,
                  icon: const Icon(EtaIcons.refreshOutline, size: 15),
                  label: Text(
                    l10n.settingsSearchIndexRebuild,
                    style: const TextStyle(fontSize: 12),
                  ),
                ),
              ),
              const SizedBox(width: 8),
              SizedBox(
                height: 28,
                child: TextButton.icon(
                  style: TextButton.styleFrom(
                    visualDensity: VisualDensity.compact,
                    foregroundColor: scheme.error,
                  ),
                  onPressed: service == null ? null : _confirmDelete,
                  icon: const Icon(EtaIcons.wastebasketOutline, size: 15),
                  label: Text(
                    l10n.settingsSearchIndexDelete,
                    style: const TextStyle(fontSize: 12),
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  /// 强制全量重建（后台 isolate；进度经 [SearchService.progress] 回传）。
  Future<void> _rebuild() async {
    final l10n = context.l10n;
    toast(l10n.settingsSearchIndexRebuildStarted);
    await SearchService.instance.rebuild();
    if (mounted) setState(() {});
  }

  /// 删除派生索引（二次确认；不影响曲库与源文件）。
  Future<void> _confirmDelete() async {
    final l10n = context.l10n;
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.settingsSearchIndexDeleteConfirmTitle),
        content: Text(l10n.settingsSearchIndexDeleteConfirmBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.commonConfirm),
          ),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    await SearchService.instance.deleteIndex();
    if (!mounted) return;
    toast(l10n.settingsSearchIndexDeleted, type: ToastType.success);
    setState(() {});
  }

  /// 当前索引体积（EraStore 世代文件；不存在 / 读取失败返回 null）。
  ///
  /// T3.13 起索引本体是 `search.era/<CURRENT 指向的 .eraindex>`；旧的
  /// `search.db` 仅作历史残留，不能再用来显示体积。
  String? _indexSizeText() {
    try {
      final base = File(SearchService.defaultSearchDbPath());
      final eraDir = Directory(
        '${base.parent.path}${Platform.pathSeparator}search.era',
      );
      if (!eraDir.existsSync()) return null;

      final current = File('${eraDir.path}${Platform.pathSeparator}CURRENT');
      if (current.existsSync()) {
        final name = current.readAsStringSync().trim();
        if (name.startsWith('search-') && name.endsWith('.eraindex')) {
          final f = File('${eraDir.path}${Platform.pathSeparator}$name');
          if (f.existsSync()) return _fmtSize(f.lengthSync());
        }
      }

      // CURRENT 缺失 / 损坏：退化为整个目录的可见体积（诊断用）。
      var total = 0;
      for (final entity in eraDir.listSync()) {
        if (entity is File) total += entity.lengthSync();
      }
      return total > 0 ? _fmtSize(total) : null;
    } catch (_) {
      return null;
    }
  }

  String _fmtSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    final kb = bytes / 1024;
    if (kb < 1024) return '${kb.toStringAsFixed(1)} KB';
    return '${(kb / 1024).toStringAsFixed(1)} MB';
  }
}
