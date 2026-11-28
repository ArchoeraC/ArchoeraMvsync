// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌手（KG）——对齐 apis/kugou/modules/artist.ts。
///
/// 详情 `/kmr/v3/author` + 全量歌曲 `/kmr/v1/audio_group/author` +
/// 全量专辑 `/kmr/v1/author/albums`。三项并行请求，分页按总页数并发拉取。
///
/// 审计结论（2026-11 实测）：歌曲接口 `data` 为 List、总数在顶层 `total`；
/// 专辑接口同样是 List + `total`；`author_id` 传歌手名会返回
/// `error_code=20010 invalid param`，因此名称先经 `/v1/search/author` 解析。
library;

import '../core/config.dart';
import '../core/crypto.dart';
import '../core/pagination.dart';
import '../core/request.dart';
import '../core/song.dart';
import '../core/types.dart';

const _pageSize = 300;

/// 歌手歌曲条目 → [kgNormalizeSong] 输入
Map<String, dynamic> _normalizeArtistSong(Map raw, String currentArtistId) {
  final authors = raw['authors'];
  return {
    'base': {
      'audio_id': raw['audio_id'],
      'album_audio_id': raw['album_audio_id'],
      'audio_name': raw['audio_name'],
      'album_id': raw['album_id'],
      'author_name': raw['author_name'],
    },
    'audio_info': raw,
    'authors': authors is List && authors.isNotEmpty
        ? authors
        : [
            {'author_id': currentArtistId, 'author_name': raw['author_name']},
          ],
    'album_info': {'album_name': raw['album_name']},
    'trans_param': raw['trans_param'],
    'deprecated': {'pay_type': raw['pay_type']},
  };
}

/// 歌手模块：`{id}` / `{author_id}`（数字 ID 或歌手名）
KgModule kgArtist = (params) async {
  final rawId =
      '${params['id'] ?? params['author_id'] ?? params['artist_id'] ?? ''}'
          .trim();
  if (rawId.isEmpty) return {'code': 400, 'message': 'id required'};

  final id = await _resolveAuthorId(rawId);
  final clienttime = DateTime.now().millisecondsSinceEpoch ~/ 1000;

  final results = await Future.wait<dynamic>([
    kgGatewayRequest(
      '/kmr/v3/author',
      method: 'POST',
      data: {'author_id': id},
      headers: {'x-router': 'openapi.kugou.com', 'kg-tid': '36'},
    ),
    kgFetchAllPages<Map>((page) async {
      final resp = await kgGatewayRequest(
        '/kmr/v1/audio_group/author',
        method: 'POST',
        baseUrl: 'https://openapi.kugou.com',
        data: {
          'author_id': id,
          'pagesize': _pageSize,
          'page': page,
          'sort': 1,
          'area_code': 'all',
          'clienttime': clienttime,
          'key': kgSignParamsKey('$clienttime'),
        },
        headers: {'x-router': 'openapi.kugou.com', 'kg-tid': '220'},
      );
      final data = resp is Map ? resp['data'] : null;
      final total = resp is Map ? resp['total'] : null;
      return (
        items: data is List
            ? data.whereType<Map>().map(Map<String, dynamic>.from).toList()
            : const <Map>[],
        total: total is num ? total.toInt() : null,
      );
    }, _pageSize),
    kgFetchAllPages<Map>((page) async {
      final resp = await kgGatewayRequest(
        '/kmr/v1/author/albums',
        method: 'POST',
        data: {
          'author_id': id,
          'pagesize': _pageSize,
          'page': page,
          'sort': 3,
          'category': 1,
          'area_code': 'all',
        },
        headers: {'x-router': 'openapi.kugou.com', 'kg-tid': '36'},
      );
      final data = resp is Map ? resp['data'] : null;
      final total = resp is Map ? resp['total'] : null;
      return (
        items: data is List
            ? data.whereType<Map>().map(Map<String, dynamic>.from).toList()
            : const <Map>[],
        total: total is num ? total.toInt() : null,
      );
    }, _pageSize),
  ]);

  final detailResp = results[0] as Map;
  final songsPage = results[1] as ({List<Map> items, int total});
  final albumsPage = results[2] as ({List<Map> items, int total});

  final detail = detailResp['data'] is Map
      ? Map<String, dynamic>.from(detailResp['data'] as Map)
      : const <String, dynamic>{};
  final songs = songsPage.items
      .map((s) => kgNormalizeSong(_normalizeArtistSong(s, id)))
      .toList();
  final avatar = kgFillCover(detail['sizable_avatar']?.toString(), 300);

  final albums = [
    for (final a in albumsPage.items)
      {
        'id': '${a['album_id'] ?? ''}',
        'name': kgDecodeName(a['album_name']?.toString()),
        'cover': kgFillCover(a['sizable_cover']?.toString(), 300),
        'artist': kgDecodeName(
          (a['author_name'] ?? detail['author_name'])?.toString(),
        ),
        'artistId': id,
        'trackCount': a['song_count'] is num
            ? (a['song_count'] as num).toInt()
            : 0,
        'publishTime': a['publish_date'],
      },
  ];

  final longIntro = detail['long_intro'];
  final introText = _firstNonEmpty([
    detail['intro']?.toString(),
    if (longIntro is List)
      longIntro
          .whereType<Map>()
          .map((e) => e['content']?.toString() ?? '')
          .where((s) => s.isNotEmpty)
          .join('\n\n'),
  ]);

  return {
    'code': 200,
    'artist': {
      'id': '${detail['author_id'] ?? id}',
      'name': kgDecodeName((detail['author_name'] ?? rawId).toString()),
      'cover': avatar,
      'avatar': avatar,
      'songCount': songsPage.total,
      'albumCount': albumsPage.total,
      'fansCount': detail['fansnums'],
      'intro': introText,
    },
    'songs': songs,
    'albums': albums,
  };
};

/// 歌手 ID 归一：数字直接用；否则按名称搜索第一个结果
Future<String> _resolveAuthorId(String idOrName) async {
  if (RegExp(r'^\d+$').hasMatch(idOrName)) return idOrName;
  final resp = await kgGatewayRequest(
    '/v1/search/author',
    params: {
      'keyword': idOrName,
      'page': 1,
      'pagesize': 1,
      'platform': 'AndroidFilter',
      'iscorrection': 1,
    },
    headers: {'x-router': 'complexsearch.kugou.com'},
  );
  final data = resp is Map ? resp['data'] : null;
  final lists = data is Map ? data['lists'] : null;
  final first = lists is List && lists.isNotEmpty ? lists.first : null;
  final id = first is Map ? first['AuthorId']?.toString() ?? '' : '';
  if (id.isEmpty) throw KgApiException('KG 歌手不存在或不可访问');
  return id;
}

String _firstNonEmpty(List<String?> values) {
  for (final v in values) {
    if (v != null && v.trim().isNotEmpty) return v.trim();
  }
  return '';
}
