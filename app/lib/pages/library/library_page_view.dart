// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

part of '../library_page.dart';

extension _LibraryPageView on _LibraryPageState {
  Widget _buildLibraryPage(BuildContext context) {
    final state = ref.watch(libraryStoreProvider);
    final notifier = ref.read(libraryStoreProvider.notifier);
    // 选择性订阅（播放位置/FFT 50ms 更新不重建列表）
    final playingId = ref.watch(playbackProvider.select((s) => s.trackId));
    final isPlaying = ref.watch(playbackProvider.select((s) => s.playing));
    final scheme = Theme.of(context).colorScheme;
    final l10n = context.l10n;

    // 搜索结果高亮 / 歌词片段：分页器在索引排行路径下用 C# `era_search_decorate`
    // 按页产出（关闭态 / 非搜索态为空，不额外调用、外观不变）。
    final searchActive = state.searchIndexActive &&
        SearchService.instance.enabled &&
        state.searchQuery.trim().isNotEmpty;

    return Scaffold(
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const LibraryHeader(),
          const Divider(height: 1),
          // ── 曲目列表 / 空状态 ─────────────────────────────────
          // 列表体四态（错误 / 未扫码空态 / 搜索无命中 / 列表）统一交叉淡化；
          // 每次切换只构建目标态，未展示的分支不触发取页/监听。
          Expanded(
            child: CrossFadeSwitcher(
              switchKey: _bodyPhase(state),
              child: switch (_bodyPhase(state)) {
                'error' => Center(
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(
                        EtaIcons.alertOutline,
                        size: 40,
                        color: scheme.error.withValues(alpha: 0.6),
                      ),
                      const SizedBox(height: 12),
                      Text(
                        state.error!,
                        style: TextStyle(
                          fontSize: 13,
                          color: scheme.onSurfaceVariant,
                        ),
                        textAlign: TextAlign.center,
                      ),
                    ],
                  ),
                ),
                'empty' => LibraryEmptyState(
                  hasDirs: state.scanDirs.isNotEmpty,
                  scanning: state.scanning,
                  onAddFolder: () => _handleEmptyAddFolder(state),
                ),
                'noMatch' => Center(
                  child: Text(
                    l10n.libraryNoMatch,
                    style: const TextStyle(fontSize: 13),
                  ),
                ),
                _ => SongList(
                  key: const PageStorageKey('page.library'),
                  // 窗口模式：items 传空，由 totalCount + itemAt 按全局 index
                  // 映射到有界页缓存；未驻留页渲染占位并触发异步取页。
                  items: const [],
                  totalCount: state.totalCount,
                  itemAt: (index) {
                    final row = notifier.rowAt(index);
                    return row == null ? null : trackFromRow(row);
                  },
                  onMissingIndex: notifier.ensureIndex,
                  playingIndexOverride: notifier.playingIndexOf(playingId),
                  playingId: playingId,
                  isPlaying: isPlaying,
                  showAlbum: true,
                  showDuration: true,
                  onPlay: _play,
                  onContextMenu: _onTrackMenu,
                  onBatchEditMetadata: _batchEditMetadata,
                  highlightOf: searchActive
                      ? (t) => state.searchHighlights[t.id]
                      : null,
                  // 歌词命中片段：仅搜索态透传查表回调；关闭态 / 非搜索态传 null，
                  // 行外观与现状逐像素一致（零成本）。
                  lyricSnippetOf: searchActive
                      ? (t) => state.lyricSnippets[t.id]
                      : null,
                  onReachBottom: () => notifier.loadMore(),
                  hasMore: state.hasMore,
                  loadingMore: state.loadingMore,
                  // 「全选」= 全库（当前搜索条件），批量操作按全量执行；
                  // UI 仍只渲染分页窗口，全量仅在批量模式期间驻留。
                  loadAllItems: () async {
                    final rows = await notifier.allTracks();
                    return rows.map(trackFromRow).toList();
                  },
                ),
              },
            ),
          ),
        ],
      ),
    );
  }
}
