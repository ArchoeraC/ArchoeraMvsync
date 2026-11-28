// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 专辑（KG）——对齐 apis/kugou/modules/album.ts。
///
/// 详情走 `/kmr/v2/albums`，曲目走 `/v1/album_audio/lite` 全量分页。
/// 审计补充（2026-11 实测）：lite 网关在部分时段对匿名设备返回
/// `error_code=20010 invalid param`，此时回退 mobilecdn
/// `/api/v3/album/song`（无需登录、字段为小写下划线）。
library;

import '../core/config.dart';
import '../core/pagination.dart';
import '../core/request.dart';
import '../core/song.dart';
import '../core/types.dart';

const _pageSize = 50;

/// 把 mobilecdn 专辑曲目适配为 [kgNormalizeSong] 的输入结构。
Map<String, dynamic> _mobileSong(Map raw) {
  final filename = (raw['filename'] ?? raw['remark'] ?? '').toString();
  final sep = filename.indexOf(' - ');
  final title = sep >= 0 ? filename.substring(sep + 3) : filename;
  final singer = sep >= 0 ? filename.substring(0, sep) : '';
  final duration = raw['duration'] is num
      ? (raw['duration'] as num).toInt() * 1000
      : 0;
  return {
    'base': {
      'audio_id': raw['audio_id'],
      'album_audio_id': raw['album_audio_id'],
      'audio_name': title,
      'author_name': singer,
      'album_id': raw['album_id'],
    },
    'audio_info': {
      'hash': raw['hash'],
      'filesize': raw['filesize'],
      'timelength': duration,
      'hash_320': raw['320hash'],
      'filesize_320': raw['320filesize'],
      'hash_flac': raw['sqhash'],
      'filesize_flac': raw['sqfilesize'],
    },
    'trans_param': raw['trans_param'],
  };
}

/// 网关专辑曲目全量
Future<({List<Map> items, int total})> _gatewaySongs(String id) async {
  return kgFetchAllPages<Map>((page) async {
    final resp = await kgGatewayRequest(
      '/v1/album_audio/lite',
      method: 'POST',
      data: {'album_id': id, 'is_buy': '', 'page': page, 'pagesize': _pageSize},
      headers: {'x-router': 'openapi.kugou.com', 'kg-tid': '255'},
    );
    final data = resp is Map ? resp['data'] : null;
    final total = resp is Map ? resp['total'] : null;
    if (data is List) {
      return (
        items: data.whereType<Map>().map(Map<String, dynamic>.from).toList(),
        total: total is num ? total.toInt() : null,
      );
    }
    if (data is Map) {
      final songs = data['songs'];
      final t = data['total'] ?? total;
      return (
        items: songs is List
            ? songs.whereType<Map>().map(Map<String, dynamic>.from).toList()
            : const <Map>[],
        total: t is num ? t.toInt() : null,
      );
    }
    return (items: const <Map>[], total: 0);
  }, _pageSize);
}

/// mobilecdn 专辑曲目全量（兜底）
Future<List<Map>> _mobileSongs(String id) async {
  final items = <Map>[];
  for (var page = 1; page <= 200; page++) {
    final body = await kgRequest(
      Uri.parse(kgAlbumSongUrl)
          .replace(
            queryParameters: {
              'version': '9108',
              'albumid': id,
              'plat': '0',
              'pagesize': '100',
              'area_code': '1',
              'page': '$page',
              'with_res_tag': '0',
            },
          )
          .toString(),
    );
    final data = body['data'];
    final info = data is Map ? data['info'] : null;
    if (info is! List || info.isEmpty) break;
    items.addAll(info.whereType<Map>().map(_mobileSong));
    if (info.length < 100) break;
  }
  return items;
}

/// 专辑模块：`{id}`（数字 ID 或旧记录里的专辑名）
KgModule kgAlbum = (params) async {
  final rawId = '${params['id'] ?? params['album_id'] ?? ''}'.trim();
  if (rawId.isEmpty) return {'code': 400, 'message': 'id required'};

  final id = await _resolveAlbumId(rawId);
  final detailResp = await kgGatewayRequest(
    '/kmr/v2/albums',
    method: 'POST',
    data: {
      'data': [
        {'album_id': id},
      ],
      'fields': 'album_id,album_name,publish_date,sizable_cover,intro,authors,author_name',
    },
    headers: {'x-router': 'openapi.kugou.com', 'kg-tid': '255'},
  );

  final detailData = detailResp is Map ? detailResp['data'] : null;
  final detailRaw = detailData is List
      ? (detailData.isEmpty ? null : detailData.first)
      : detailData;
  if (detailRaw is! Map) throw KgApiException('KG 专辑不存在或不可访问');
  final detail = Map<String, dynamic>.from(detailRaw);

  var rawSongs = const <Map>[];
  var total = 0;
  try {
    final result = await _gatewaySongs(id);
    rawSongs = result.items;
    total = result.total;
  } on KgApiException {
    // 走 mobilecdn 兜底
  }
  if (rawSongs.isEmpty) {
    // 网关「成功但空」（20010 的另一种表现）也兜底。
    rawSongs = await _mobileSongs(id);
    total = rawSongs.length;
  }

  final songs = rawSongs
      .map(
        (s) => kgNormalizeSong(
          Map<String, dynamic>.from(s),
          albumId: id,
          albumName: detail['album_name']?.toString(),
          cover: detail['sizable_cover']?.toString(),
        ),
      )
      .toList();

  final authors = detail['authors'];
  return {
    'code': 200,
    'id': id,
    'name': kgDecodeName(detail['album_name']?.toString()),
    'cover': kgFillCover(detail['sizable_cover']?.toString(), 300),
    'coverOriginal': kgFillCover(detail['sizable_cover']?.toString(), 480),
    'artist': _firstNonEmpty([
      kgDecodeName(detail['author_name']?.toString()),
      if (songs.isNotEmpty) '${songs.first['artist'] ?? ''}',
    ]),
    'artists': authors is List
        ? [
            for (final a in authors.whereType<Map>())
              {
                'id': a['author_id']?.toString(),
                'name': kgDecodeName(a['author_name']?.toString()),
              },
          ]
        : const [],
    'publishTime': detail['publish_date'],
    'description': detail['intro'],
    'total': total,
    'songs': songs,
  };
};

/// 专辑 ID 归一：数字直接用；否则按名称搜索第一个结果
Future<String> _resolveAlbumId(String idOrName) async {
  if (RegExp(r'^[1-9]\d*$').hasMatch(idOrName)) return idOrName;
  final resp = await kgGatewayRequest(
    '/v1/search/album',
    params: {
      'keyword': idOrName,
      'page': 1,
      'pagesize': 1,
      'platform': 'AndroidFilter',
      'iscorrection': 1,
      'nocollect': 0,
    },
    headers: {'x-router': 'complexsearch.kugou.com'},
  );
  final data = resp is Map ? resp['data'] : null;
  final lists = data is Map ? data['lists'] : null;
  final first = lists is List && lists.isNotEmpty ? lists.first : null;
  final id = first is Map ? first['albumid']?.toString() ?? '' : '';
  if (id.isEmpty) throw KgApiException('KG 专辑不存在或不可访问');
  return id;
}

String _firstNonEmpty(List<String?> values) {
  for (final v in values) {
    if (v != null && v.trim().isNotEmpty) return v.trim();
  }
  return '';
}
