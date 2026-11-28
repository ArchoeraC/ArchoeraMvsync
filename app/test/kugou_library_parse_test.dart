// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 用户曲库条目解析回归（**不联网**）。
///
/// `/v7/get_all_list` 的字段语义容易混：
/// - `global_collection_id`：本人副本的公开 id（读歌曲优先）
/// - `list_create_gid`：收藏来源（原作者歌单）的公开 id
/// - `listid`：用户本地 id（个人增删接口用）
/// - `is_def`：1=默认收藏，2=我喜欢
/// 收藏页打开 / 「我喜欢」定位都依赖这套解析，这里钉成确定性单测。
library;

import 'package:archoera_mvsync/services/kugou/kugou_parse.dart'
    show kgLibraryItemFromKg;
import 'package:archoera_mvsync/services/kugou/kugou_types.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('收藏歌单：优先本人副本 global_collection_id，保留 listid', () {
    final item = kgLibraryItemFromKg({
      'listid': 3,
      'global_collection_id': 'collection_3_10001_3_0',
      'name': '示例收藏歌单',
      'count': 93,
      'type': 1,
      'list_create_userid': 20002,
      'list_create_listid': 11,
      'list_create_gid': 'collection_3_20002_11_0',
      'pic': 'http://example.invalid/pic.jpg',
    }, myUserid: '10001');
    expect(item, isNotNull);
    expect(item!.id, 'collection_3_10001_3_0');
    expect(item.listid, '3');
    expect(item.type, KugouLibraryType.collectedPlaylist);
    expect(item.isLiked, isFalse);
    expect(item.title, '示例收藏歌单');
    expect(item.trackCount, 93);
  });

  test('无 global_collection_id 时回落原作者 list_create_gid', () {
    final item = kgLibraryItemFromKg({
      'listid': 3,
      'name': '旧数据收藏歌单',
      'list_create_userid': 20002,
      'list_create_gid': 'collection_3_20002_11_0',
    }, myUserid: '10001');
    expect(item!.id, 'collection_3_20002_11_0');
    expect(item.type, KugouLibraryType.collectedPlaylist);
  });

  test('is_def=2 识别「我喜欢」（名称不是关键）', () {
    final item = kgLibraryItemFromKg({
      'listid': 2,
      'name': '我的红心',
      'is_def': 2,
      'global_collection_id': 'collection_3_10001_2_0',
    }, myUserid: '10001');
    expect(item!.isLiked, isTrue);
    expect(item.type, KugouLibraryType.createdPlaylist);
    expect(item.id, 'collection_3_10001_2_0');
    expect(item.listid, '2');
  });

  test('「我喜欢」缺公开 id 时用 listid 兜底', () {
    final item = kgLibraryItemFromKg({
      'listid': 2,
      'name': '我喜欢',
      'is_def': 2,
    }, myUserid: '10001');
    expect(item!.isLiked, isTrue);
    expect(item.id, '2');
  });

  test('authors 存在 → 收藏的专辑', () {
    final item = kgLibraryItemFromKg({
      'listid': 7,
      'name': '某专辑',
      'list_create_userid': 20002,
      'global_collection_id': 'collection_3_10001_7_0',
      'authors': [
        {'author_id': 1, 'author_name': 'A'},
      ],
    }, myUserid: '10001');
    expect(item!.type, KugouLibraryType.collectedAlbum);
  });

  test('字段别名 globalCollectionId / gid 兼容', () {
    final a = kgLibraryItemFromKg({
      'listid': 5,
      'name': '别名',
      'list_create_userid': 20002,
      'globalCollectionId': 'collection_3_10001_5_0',
    }, myUserid: '10001');
    expect(a!.id, 'collection_3_10001_5_0');
    final b = kgLibraryItemFromKg({
      'listid': 6,
      'name': '别名2',
      'list_create_userid': 20002,
      'gid': 'collection_3_10001_6_0',
    }, myUserid: '10001');
    expect(b!.id, 'collection_3_10001_6_0');
  });

  test('缺名称或缺 id 返回 null（调用方跳过）', () {
    expect(kgLibraryItemFromKg({'listid': 9}, myUserid: '1'), isNull);
    expect(
      kgLibraryItemFromKg({
        'name': '没有 id',
        'list_create_userid': 2,
      }, myUserid: '1'),
      isNull,
    );
  });
}
