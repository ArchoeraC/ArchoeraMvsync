// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// 构建期 options_json 必须把派生列开关原样传给索引器（默认全关）。

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/search/search_service.dart';

void main() {
  test('默认选项：所有派生列关闭', () {
    final options = SearchService.buildOptionsMap(
      indexLrc: false,
      pinyin: false,
      lyricsPinyin: false,
      romajiLyrics: false,
      initialsLyrics: false,
    );
    expect(options['index_lrc'], isFalse);
    expect(options['pinyin'], isFalse);
    expect(options['lyrics_pinyin'], isFalse);
    expect(options['romaji_lyrics'], isFalse);
    expect(options['initialsLyrics'], isFalse);
  });

  test('显式开启：每个开关原样传给索引器', () {
    final options = SearchService.buildOptionsMap(
      indexLrc: true,
      pinyin: true,
      lyricsPinyin: true,
      romajiLyrics: true,
      initialsLyrics: true,
    );
    expect(options['index_lrc'], isTrue);
    expect(options['pinyin'], isTrue);
    expect(options['lyrics_pinyin'], isTrue);
    expect(options['romaji_lyrics'], isTrue);
    expect(options['initialsLyrics'], isTrue);
  });
}
