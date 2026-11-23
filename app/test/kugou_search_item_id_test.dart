// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 搜索结果 id 回归（**不联网**）。
///
/// `/v1/search/special` 同时返回数字 `specialid` 与公开歌单 `gid`
/// （`collection_...`）。若优先取 `specialid`，点开歌单会用数字 id 调
/// `get_other_list_file_nofilt`，服务端返回 `error_code=20010 invalid param`
/// （表现为列表弹窗「加载失败」）。这里把「按分类取 id」钉成确定性单测。
library;

import 'package:archoera_mvsync/services/kugou/kugou_parse.dart'
    show kgSearchItemId;
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('歌单搜索结果优先 gid（global_collection_id）而非数字 specialid', () {
    final id = kgSearchItemId({
      'specialid': 6409645,
      'gid': 'collection_3_2132029040_287_0',
      'specialname': '周杰伦出道26周年',
    }, type: 'special');
    expect(id, 'collection_3_2132029040_287_0');
  });

  test('无 gid 时退回数字 specialid（由请求层换 global_specialid）', () {
    expect(kgSearchItemId({'specialid': 6409645}, type: 'special'), '6409645');
    expect(kgSearchItemId({'SpecialID': 4184876}, type: 'special'), '4184876');
  });

  test('global_collection_id / global_specialid 字段别名可直接使用', () {
    expect(
      kgSearchItemId({
        'global_collection_id': 'collection_1_1_2_0',
      }, type: 'special'),
      'collection_1_1_2_0',
    );
    expect(
      kgSearchItemId({
        'global_specialid': 'collection_3_1_2_0',
      }, type: 'special'),
      'collection_3_1_2_0',
    );
  });

  test('专辑 / 歌手按分类取字段，不被其它分类字段干扰', () {
    expect(
      kgSearchItemId({
        'albumid': 1234,
        'albumname': '专辑',
        'gid': 'stray-gid',
      }, type: 'album'),
      '1234',
    );
    expect(kgSearchItemId({'AlbumID': 5678}, type: 'album'), '5678');
    expect(
      kgSearchItemId({
        'AuthorId': 6539,
        'AuthorName': '歌手',
        'gid': 'stray-gid',
      }, type: 'author'),
      '6539',
    );
    expect(kgSearchItemId({'authorid': 42}, type: 'author'), '42');
  });

  test('未知分类回退到通用字段顺序', () {
    expect(kgSearchItemId({'albumid': 1234}), '1234');
    expect(kgSearchItemId({'AuthorId': 6539}), '6539');
  });

  test('缺 id 字段或空串返回空串（调用方跳过该条目）', () {
    expect(kgSearchItemId({'specialname': '无 id'}, type: 'special'), '');
    expect(kgSearchItemId({'albumid': ''}, type: 'album'), '');
  });
}
