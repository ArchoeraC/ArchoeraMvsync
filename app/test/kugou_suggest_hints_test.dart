// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 搜索建议解析回归（**不联网**）。
///
/// `/v2/getSearchTip` 实测返回 `data` 为 List 分组：
/// `[{LableName, RecordDatas:[{HintInfo}]}]`，条目只有 `HintInfo`。
/// 旧代码只认 Map（`music_tip` / `album_tip`），导致 KG 搜索建议恒为空。
library;

import 'package:archoera_mvsync/services/kugou/kugou_parse.dart'
    show kgSuggestHints, kgSuggestSongKeywords;
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('List 分组结构解析并按顺序去重', () {
    final hints = kgSuggestHints([
      {
        'LableName': '',
        'RecordCount': 2,
        'RecordDatas': [
          {'HintInfo': '周杰伦'},
          {'HintInfo': '周杰伦 告白气球'},
        ],
      },
      {
        'LableName': 'MV',
        'RecordDatas': [
          {'HintInfo': '周杰伦 - 晴天'},
          {'HintInfo': '周杰伦'},
        ],
      },
      {
        'LableName': '专辑',
        'RecordDatas': [
          {'HintInfo': '周杰伦 - 魔杰座'},
        ],
      },
    ]);
    expect(hints.map((h) => h.text).toList(), [
      '周杰伦',
      '周杰伦 告白气球',
      '周杰伦 - 晴天',
      '周杰伦 - 魔杰座',
    ]);
    expect(hints.map((h) => h.label).toList(), ['', '', 'MV', '专辑']);
    // 只有空标签分组可作为歌曲关键词：MV / 专辑分组是其它实体。
    expect(
      kgSuggestSongKeywords([
        {
          'LableName': '',
          'RecordDatas': [
            {'HintInfo': '周杰伦'},
          ],
        },
        {
          'LableName': 'MV',
          'RecordDatas': [
            {'HintInfo': '周杰伦 - 晴天'},
          ],
        },
        {
          'LableName': '专辑',
          'RecordDatas': [
            {'HintInfo': '周杰伦 - 魔杰座'},
          ],
        },
      ]),
      ['周杰伦'],
    );
  });

  test('空文本 / 缺 RecordDatas 的组被忽略', () {
    final hints = kgSuggestHints([
      {'LableName': 'x', 'RecordDatas': null},
      {
        'LableName': '',
        'RecordDatas': [
          {'HintInfo': '  '},
          {'nope': 1},
          {'HintInfo': '有效'},
        ],
      },
    ]);
    expect(hints.map((h) => h.text).toList(), ['有效']);
  });

  test('非 List（旧 Map / null）返回空', () {
    expect(kgSuggestHints(null), isEmpty);
    expect(kgSuggestHints({'music_tip': <dynamic>[]}), isEmpty);
  });
}
