// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// T3.4 V4：构建期 options_json 必须把「歌词首字母」开关传给索引器（默认 false），
// 且不影响既有 index_lrc / pinyin / romaji_lyrics 键。

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/search/search_service.dart';

void main() {
  test('默认选项：lyrics initials 关闭、pinyin 恒开', () {
    final options = SearchService.buildOptionsMap(
      indexLrc: false,
      romajiLyrics: false,
      initialsLyrics: false,
    );
    expect(options['index_lrc'], isFalse);
    expect(options['pinyin'], isTrue, reason: '歌词拼音默认写，不受首字母开关影响');
    expect(options['romaji_lyrics'], isFalse);
    expect(options['initialsLyrics'], isFalse);
  });

  test('显式开启：initialsLyrics=true 传给索引器', () {
    final options = SearchService.buildOptionsMap(
      indexLrc: true,
      romajiLyrics: true,
      initialsLyrics: true,
    );
    expect(options['index_lrc'], isTrue);
    expect(options['romaji_lyrics'], isTrue);
    expect(options['initialsLyrics'], isTrue);
  });
}
