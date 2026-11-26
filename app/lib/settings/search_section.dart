// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'dart:async';
import 'dart:io';

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../l10n/l10n.dart';
import '../../services/search/search_index_diagnostics.dart';
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
/// - 子开关 `索引 .lrc 侧车歌词`（默认关；总开关关闭时禁用）；切换后**增量重建**
///   索引（仅重算受影响曲目，异步、失败降级），无需手动「重建索引」。
/// - 子开关 `生成歌词罗马音索引`（默认关；T2-9 可选增强）：无音译层的日语歌词
///   自动生成罗马音（索引体积约增加一倍），切换后触发全量重建。
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
    // 索引打开时服务内部已缓存健康快照；这里再按需补一次（后台 isolate，不阻塞 UI）。
    await SearchService.instance.refreshHealth();
    if (mounted) setState(() {});
  }

  /// `.lrc` 子开关切换：写偏好后触发**增量**重建（仅重算受影响曲目），
  /// 异步、不阻塞 UI；失败在服务内降级 / 吞异常，不抛到 UI。
  void _onToggleLrc(bool value) {
    ref.read(appPrefsProvider.notifier).setSearchIndex(indexLrc: value);
    unawaited(_reindexThenRefresh());
  }

  /// 歌词罗马音子开关切换：写偏好后触发重建（选项变化 → 索引器自动升级为全量）。
  void _onToggleRomajiLyrics(bool value) {
    ref.read(appPrefsProvider.notifier).setSearchIndex(romajiLyrics: value);
    unawaited(_reindexThenRefresh());
  }

  /// 歌词首字母子开关切换（T3.4 V4）：写偏好后触发重建（选项位变化 → 全量）。
  void _onToggleInitialsLyrics(bool value) {
    ref.read(appPrefsProvider.notifier).setSearchIndex(initialsLyrics: value);
    unawaited(_reindexThenRefresh());
  }

  Future<void> _reindexThenRefresh() async {
    await SearchService.instance.reindex();
    await SearchService.instance.refreshHealth();
    if (mounted) setState(() {});
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
              onChanged: enabled ? _onToggleLrc : null,
            ),
            SettingSwitchTile(
              icon: EtaIcons.translateOutline,
              title: l10n.settingsSearchIndexRomaji,
              subtitle: l10n.settingsSearchIndexRomajiDesc,
              value: prefs.romajiLyrics,
              enabled: enabled,
              onChanged: enabled ? _onToggleRomajiLyrics : null,
            ),
            SettingSwitchTile(
              icon: EtaIcons.abcOutline,
              title: l10n.settingsSearchIndexInitials,
              subtitle: l10n.settingsSearchIndexInitialsDesc,
              value: prefs.initialsLyrics,
              enabled: enabled,
              onChanged: enabled ? _onToggleInitialsLyrics : null,
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
        ..._buildDiagnostics(l10n, service),
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

  /// 诊断行：索引世代 / 版本、数据包状态、歌词置信度分布（E5）。
  ///
  /// 仅在索引**就绪**且健康快照可解析时显示；缺失字段对应的片段（乃至整行）
  /// 直接省略，旧原生库 / 未就绪载荷不会显示空值或占位符。数据全部来自
  /// [SearchService.healthSnapshot] 缓存，这里不做 FFI / 文件 IO。
  List<Widget> _buildDiagnostics(
    AppLocalizations l10n,
    SearchService? service,
  ) {
    final status = service?.status;
    if (service == null || status == null) return const [];
    if (status.state != LocalSearchState.ready) return const [];
    final diagnostics = SearchIndexDiagnostics.fromHealth(
      service.healthSnapshot,
    );
    if (diagnostics == null) return const [];

    final rows = <Widget>[];

    // 世代 / 格式 / 分析器版本：有哪个显示哪个。
    final versionParts = <String>[
      if (diagnostics.generation != null)
        l10n.settingsSearchIndexDiagGen(gen: diagnostics.generation!),
      if (diagnostics.schemaVersion != null)
        l10n.settingsSearchIndexDiagSchema(version: diagnostics.schemaVersion!),
      if (diagnostics.analyzerVersion != null)
        l10n.settingsSearchIndexDiagAnalyzer(
          version: diagnostics.analyzerVersion!,
        ),
    ];
    if (versionParts.isNotEmpty) {
      rows.add(
        SettingTile(
          icon: EtaIcons.informationOutline,
          title: l10n.settingsSearchIndexDiagVersions,
          subtitle: versionParts.join(' · '),
          trailing: const SizedBox(width: 8),
        ),
      );
    }

    // 数据包：短 id + 同源 / 存在 / 基础表状态；降级能力非零时追加计数。
    final packParts = <String>[
      if (diagnostics.shortDataPackId != null)
        l10n.settingsSearchIndexDiagPackId(id: diagnostics.shortDataPackId!),
      if (diagnostics.dataPackMatch != null)
        diagnostics.dataPackMatch!
            ? l10n.settingsSearchIndexDiagPackMatch
            : l10n.settingsSearchIndexDiagPackMismatch,
      if (diagnostics.dataPackPresent != null)
        diagnostics.dataPackPresent!
            ? l10n.settingsSearchIndexDiagPackPresent
            : l10n.settingsSearchIndexDiagPackMissing,
      if (diagnostics.dataPackFoundationOk != null)
        diagnostics.dataPackFoundationOk!
            ? l10n.settingsSearchIndexDiagPackFoundation
            : l10n.settingsSearchIndexDiagPackFoundationBad,
      if (diagnostics.capabilityIssueCount > 0)
        l10n.settingsSearchIndexDiagDegraded(
          count: diagnostics.capabilityIssueCount,
        ),
    ];
    if (packParts.isNotEmpty) {
      rows.add(
        SettingTile(
          icon: EtaIcons.foldersOutline,
          title: l10n.settingsSearchIndexDiagDataPack,
          subtitle: packParts.join(' · '),
          trailing: const SizedBox(width: 8),
        ),
      );
    }

    // 歌词分层置信度：全 0（无歌词索引）时不显示，避免噪声行。
    if (diagnostics.confidenceTotal > 0) {
      rows.add(
        SettingTile(
          icon: EtaIcons.chartBarOutline,
          title: l10n.settingsSearchIndexDiagLyrics,
          subtitle: l10n.settingsSearchIndexDiagConfidence(
            high: diagnostics.confidenceHigh,
            medium: diagnostics.confidenceMedium,
            low: diagnostics.confidenceLow,
            none: diagnostics.confidenceNone,
          ),
          trailing: const SizedBox(width: 8),
        ),
      );
    }
    return rows;
  }

  /// 强制全量重建（后台 isolate；进度经 [SearchService.progress] 回传）。
  Future<void> _rebuild() async {
    final l10n = context.l10n;
    toast(l10n.settingsSearchIndexRebuildStarted);
    await SearchService.instance.rebuild();
    await SearchService.instance.refreshHealth();
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
