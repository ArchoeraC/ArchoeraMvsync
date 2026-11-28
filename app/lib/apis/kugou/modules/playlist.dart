// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌单（KG）——对齐 apis/kugou/modules/playlist.ts。
///
/// 公开歌单：数字 `specialid` 先经 mobilecdn `special/info` 换成
/// `global_specialid`，再分页拉 `/pubsongs/v2/get_other_list_file_nofilt`
/// （`begin_idx` 偏移，300/页）。
///
/// 审计补充（2026-11）：
/// - 数字 specialid 直接传给公开接口会得到 `error_code=20010 invalid param`；
/// - 私有歌单 /「我喜欢」需要用个人接口 `/v4/get_list_all_file`
///   （`listid` + 登录），公开接口对「我喜欢」只暴露前 ~600 首。
///   故模块支持可选 `listid`，走个人接口并统一成「最新在前」。
library;

import '../core/config.dart';
import '../core/pagination.dart';
import '../core/request.dart';
import '../core/session.dart';
import '../core/song.dart';
import '../core/types.dart';

const _pageSize = 300;

/// 歌单条目 → [kgNormalizeSong] 输入
///
/// 兼容公开接口（`name` = "歌手 - 歌名"、`singerinfo`、`mixsongid`、
/// `relate_goods` 档位 4/5/6）与个人接口（同一套字段，另带 `fileid`）。
Map<String, dynamic> _normalizePlaylistSong(Map raw) {
  final variants = <int, Map>{};
  final relate = raw['relate_goods'];
  if (relate is List) {
    for (final g in relate.whereType<Map>()) {
      final level = g['level'] is num
          ? (g['level'] as num).toInt()
          : int.tryParse('${g['level'] ?? ''}') ?? 0;
      variants[level] = g;
    }
  }
  final merged = '${raw['name'] ?? raw['filename'] ?? raw['songname'] ?? ''}';
  final sep = merged.indexOf(' - ');
  final albumInfo = raw['albuminfo'] is Map
      ? Map<String, dynamic>.from(raw['albuminfo'] as Map)
      : const <String, dynamic>{};
  final singerInfo = raw['singerinfo'];
  final authors = <Map>[
    if (singerInfo is List)
      for (final s in singerInfo.whereType<Map>())
        {'author_id': s['id'], 'author_name': s['name']},
  ];
  final downloads = raw['download'];
  Object? payType;
  if (downloads is List) {
    for (final d in downloads.whereType<Map>()) {
      if (d['hash'] == raw['hash']) {
        payType = d['pay_type'];
        break;
      }
    }
  }

  return {
    'base': {
      'audio_id': raw['audio_id'],
      'album_audio_id': raw['mixsongid'] ?? raw['album_audio_id'],
      'album_id': albumInfo['id'] ?? raw['album_id'],
      'audio_name': sep >= 0 ? merged.substring(sep + 3) : merged,
      'author_name': sep >= 0 ? merged.substring(0, sep) : raw['singername'],
    },
    'audio_info': {
      'hash': raw['hash'],
      'filesize': raw['size'] ?? raw['filesize'],
      'timelength': raw['timelen'] ?? raw['duration'],
      'hash_320': variants[4]?['hash'],
      'filesize_320': variants[4]?['size'],
      'hash_flac': variants[5]?['hash'],
      'filesize_flac': variants[5]?['size'],
      'hash_high': variants[6]?['hash'],
      'filesize_high': variants[6]?['size'],
    },
    'authors': authors,
    'album_info': {'album_name': albumInfo['name'], 'cover': raw['cover']},
    'copyright': {'privilege': raw['privilege']},
    'deprecated': {'pay_type': payType, 'feetype': raw['feetype']},
  };
}

Map<String, dynamic> _songOf(Map raw) => kgNormalizeSong(
  _normalizePlaylistSong(raw),
  cover: raw['cover']?.toString(),
  albumName: raw['albuminfo'] is Map
      ? (raw['albuminfo'] as Map)['name']?.toString()
      : null,
);

/// 歌单模块：`{id | specialid | special_id, listid?}`
KgModule kgPlaylist = (params) async {
  final rawId =
      '${params['id'] ?? params['specialid'] ?? params['special_id'] ?? ''}'
          .trim();
  final listid = '${params['listid'] ?? ''}'.trim();
  if (listid.isNotEmpty) return _personalPlaylist(listid, rawId);
  if (rawId.isEmpty) return {'code': 400, 'message': 'id required'};

  var globalId = rawId;
  if (RegExp(r'^\d+$').hasMatch(rawId)) {
    final body = await kgRequest(
      Uri.parse(kgSpecialInfoUrl)
          .replace(queryParameters: {'specialid': rawId, 'format': 'json'})
          .toString(),
    );
    final data = body['data'];
    globalId = data is Map ? (data['global_specialid'] ?? '').toString() : '';
    if (globalId.isEmpty) throw KgApiException('KG 歌单不存在或不可访问');
  }

  Map? listInfo;
  final result = await kgFetchAllPages<Map>((page) async {
    final resp = await kgGatewayRequest(
      '/pubsongs/v2/get_other_list_file_nofilt',
      params: {
        'global_collection_id': globalId,
        'begin_idx': (page - 1) * _pageSize,
        'pagesize': _pageSize,
        'area_code': 1,
        'plat': 1,
        'type': 1,
        'mode': 1,
        'personal_switch': 1,
      },
    );
    final data = resp is Map ? resp['data'] : null;
    final songs = data is Map ? data['songs'] : null;
    if (page == 1 && data is Map && data['list_info'] is Map) {
      listInfo = Map<String, dynamic>.from(data['list_info'] as Map);
    }
    final total = data is Map ? data['count'] : null;
    return (
      items: songs is List
          ? songs.whereType<Map>().map(Map<String, dynamic>.from).toList()
          : const <Map>[],
      total: total is num ? total.toInt() : null,
    );
  }, _pageSize);

  final info = listInfo ?? const <String, dynamic>{};
  return {
    'code': 200,
    'id': globalId,
    'name': kgDecodeName(info['name']?.toString()),
    'description': info['intro'],
    'creator': kgDecodeName(info['list_create_username']?.toString()),
    'cover': kgFillCover(info['pic']?.toString(), 300),
    'coverOriginal': kgFillCover(info['pic']?.toString(), 480),
    'total': result.total,
    'songs': result.items.map(_songOf).toList(),
  };
};

/// 个人歌单（含「我喜欢」）：`/v4/get_list_all_file`，需登录。
///
/// 返回顺序统一为「最新在前」（个人接口按收藏先后升序）。
Future<Map<String, dynamic>> _personalPlaylist(
  String listid,
  String gid,
) async {
  if (!kgLoggedIn) {
    return {'code': 301, 'loggedIn': false, 'message': '需要登录 KG 账号'};
  }
  final result = await kgFetchAllPages<Map>((page) async {
    final resp = await kgGatewayRequest(
      '/v4/get_list_all_file',
      method: 'POST',
      headers: {'x-router': 'cloudlist.service.kugou.com'},
      data: {
        'listid': listid,
        'userid': kgUserId(),
        'area_code': 1,
        'show_relate_goods': 0,
        'pagesize': _pageSize,
        'allplatform': 1,
        'show_cover': 1,
        'type': 0,
        'token': kgToken(),
        'page': page,
      },
    );
    final data = resp is Map ? resp['data'] : null;
    final songs = data is Map ? data['info'] : null;
    final total = data is Map ? data['count'] : null;
    return (
      items: songs is List
          ? songs.whereType<Map>().map(Map<String, dynamic>.from).toList()
          : const <Map>[],
      total: total is num ? total.toInt() : null,
    );
  }, _pageSize);

  final songs = result.items.reversed.map(_songOf).toList();
  return {
    'code': 200,
    'id': gid.isEmpty ? listid : gid,
    'listid': listid,
    'total': songs.length,
    'songs': songs,
  };
}
