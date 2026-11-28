// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'app_prefs.dart';

// ── 搜索键（search. 前缀）──────────────────────────────────────
const searchHistoryKey = 'search.history';

/// 本地曲库搜索索引（总开关）键：**默认关**。
const enableLocalSearchIndexKey = 'search.index.enabled';

/// `.lrc` 侧车歌词索引键：**默认关**（仅总开关开启时才有意义）。
const indexLrcKey = 'search.index.lrc';

/// 元数据拼音列键：**默认关**（歌名 / 歌手 / 专辑的拼音与首字母）。
const pinyinKey = 'search.index.pinyin';

/// 歌词拼音列键：**默认关**（索引体积的主要派生列，独立于元数据拼音）。
const lyricsPinyinKey = 'search.index.lyrics_pinyin';

/// 歌词罗马音生成键：**默认关**（T2-9 可选增强，仅总开关开启时才有意义）。
const romajiLyricsKey = 'search.index.romaji_lyrics';

/// 歌词首字母列键：**默认关**（T3.4 V4；仅总开关开启时才有意义）。
const initialsLyricsKey = 'search.index.initials_lyrics';

/// 搜索历史条数上限（对齐原项目 MAX_SEARCH_HISTORY=20，最新在前）。
const int maxSearchHistory = 20;

/// 搜索偏好：搜索历史（关键词列表，最新在前、去重）+ 本地曲库搜索索引开关。
extension SearchPrefs on AppPrefs {
  /// 搜索历史（最新在前；非法值回退空）。
  List<String> get searchHistory {
    final v = data[searchHistoryKey];
    if (v is List) return v.whereType<String>().toList();
    return const [];
  }

  AppPrefs copyWithSearchHistory(List<String> history) =>
      AppPrefs(initialData: {...data, searchHistoryKey: history});

  /// 本地曲库搜索索引**总开关**（默认关）：关闭时不加载索引器 /
  /// 不建库 / 不接管搜索，回退现有 `LIKE`（设计文档 §2.4）。
  /// 非法值（非布尔）一律回退默认关。
  bool get enableLocalSearchIndex => data[enableLocalSearchIndexKey] == true;

  /// 是否索引 `.lrc` 侧车歌词（默认关；仅总开关开启时生效，设计文档 §4.4）。
  /// 非法值（非布尔）一律回退默认关。
  bool get indexLrc => data[indexLrcKey] == true;

  /// 是否生成元数据拼音列（默认关；歌名 / 歌手 / 专辑的拼音与首字母）。
  bool get pinyin => data[pinyinKey] == true;

  /// 是否生成歌词拼音列（默认关；独立于元数据拼音）。
  bool get lyricsPinyin => data[lyricsPinyinKey] == true;

  /// 是否为无音译层的日语歌词自动生成罗马音（默认关；T2-9 可选增强）。
  /// 非法值（非布尔）一律回退默认关。
  bool get romajiLyrics => data[romajiLyricsKey] == true;

  /// 是否为歌词生成首字母列（默认关；T3.4 V4 可选增强）。
  /// 非法值（非布尔）一律回退默认关。
  bool get initialsLyrics => data[initialsLyricsKey] == true;

  /// 写入本地搜索索引开关（总开关 / `.lrc` / 元数据拼音 / 歌词拼音 /
  /// 歌词罗马音 / 歌词首字母子开关）。
  AppPrefs copyWithSearchIndex({
    bool? enabled,
    bool? indexLrc,
    bool? pinyin,
    bool? lyricsPinyin,
    bool? romajiLyrics,
    bool? initialsLyrics,
  }) => AppPrefs(
    initialData: {
      ...data,
      enableLocalSearchIndexKey: ?enabled,
      indexLrcKey: ?indexLrc,
      pinyinKey: ?pinyin,
      lyricsPinyinKey: ?lyricsPinyin,
      romajiLyricsKey: ?romajiLyrics,
      initialsLyricsKey: ?initialsLyrics,
    },
  );
}
