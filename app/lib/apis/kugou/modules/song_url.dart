// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 取流（KG）——对齐 apis/kugou/modules/song_url.ts。
///
/// 概念版（lite）协议：优先用 `privilege/lite` 换取登录账号在各档位的
/// 变体 hash，再按「偏好档位 → 逐档下降」请求 `/v5/url`；未登录或换取
/// 失败时直接用原始 hash 降档尝试。
///
/// 审计结论（2026-11 实测）：
/// - `album_id` / `album_audio_id` 必须带真实值，恒为 0 时部分曲目返回
///   `status=0 "Hash not found"`；
/// - `clientver` 固定 11430（与 `version` 一致，比 lite 默认 11440 旧）；
/// - `status=2` 表示当前账号对该曲目**没有全曲权益**（需登录 / VIP /
///   已购），不是设备失效：同一设备把 `IsFreePart` 置 1 会立刻返回
///   status=1 + 128k 试听直链。因此不做 dfid 重注册（反复注册反而是
///   风控信号），需要试听时由调用方传 `freePart: true`。
library;

import '../core/config.dart';
import '../core/crypto.dart';
import '../core/device.dart';
import '../core/request.dart';
import '../core/session.dart';
import '../core/types.dart';

/// 音质档位（低 → 高）
const _qualityLevels = ['128', '320', 'flac', 'high'];

/// 用户档位 → KG 品质参数
const _qualityMap = <String, String>{
  'hi-res': 'high',
  'lossless': 'flac',
  'hq': '320',
  'sq': '128',
  'lq': '128',
};

/// `/v5/url` 固定参数（对齐 KuGouMusicApi module/song_url.js，概念版）
const _urlBase = <String, dynamic>{
  'area_code': 1,
  'ssa_flag': 'is_fromtrack',
  'version': 11430,
  'page_id': 967177915,
  'behavior': 'play',
  'pid': 411,
  'cmd': 26,
  'pidversion': 3001,
  'IsFreePart': 0,
  'ppage_id': '356753938,823673182,967485191',
  'cdnBackup': 1,
  'module': '',
  'clientver': 11430,
};

/// 取流响应里的直链（`url` / `backup_url` 可能是字符串或数组）
List<String> _extractUrls(Map response) => [
  for (final key in const ['url', 'backup_url'])
    if (response[key] is List)
      for (final u in response[key] as List)
        if (u is String && u.isNotEmpty)
          u
        else if (response[key] is String &&
            (response[key] as String).isNotEmpty)
          response[key] as String,
];

/// 查询登录账号在 `privilege/lite` 各档位可用的变体 hash
Future<Map<String, String>> _fetchLiteQualityOptions(
  String hash,
  int albumId,
) async {
  final resp = await kgGatewayRequest(
    '/v2/get_res_privilege/lite',
    method: 'POST',
    headers: {'x-router': 'media.store.kugou.com'},
    data: {
      'appid': kgLiteAppid,
      'clientver': kgLiteClientver,
      'area_code': 1,
      'behavior': 'play',
      'need_hash_offset': 1,
      'relate': 1,
      'support_verify': 1,
      'resource': [
        {'type': 'audio', 'page_id': 0, 'hash': hash, 'album_id': albumId},
      ],
      'qualities': _qualityLevels,
    },
  );
  final options = <String, String>{};
  final data = resp is Map ? resp['data'] : null;
  if (data is! List) return options;
  for (final item in data.whereType<Map>()) {
    final variants = <Map>[
      item,
      if (item['relate_goods'] is List)
        ...(item['relate_goods'] as List).whereType<Map>(),
    ];
    for (final variant in variants) {
      final quality = variant['quality']?.toString() ?? '';
      final variantHash = variant['hash']?.toString() ?? '';
      final level = variant['level'] is num
          ? (variant['level'] as num).toInt()
          : int.tryParse('${variant['level'] ?? ''}') ?? 0;
      if (variantHash.isEmpty || level == 0) continue;
      if (!_qualityLevels.contains(quality) || options.containsKey(quality)) {
        continue;
      }
      options[quality] = variantHash;
    }
  }
  return options;
}

/// 请求单档直链
Future<Map> _requestUrl(
  String hash,
  String quality,
  int albumId,
  int audioId,
  int userid,
  bool freePart,
) async {
  final resp = await kgGatewayRequest(
    '/v5/url',
    params: {
      ..._urlBase,
      'album_id': albumId,
      'album_audio_id': audioId,
      'hash': hash,
      'quality': quality,
      'key': kgSignKey(hash, kgMid(), userid),
      'IsFreePart': freePart ? 1 : 0,
    },
    headers: {'x-router': 'trackercdn.kugou.com'},
  );
  return resp is Map ? resp : const {};
}

/// 取流模块：`{hash, albumId?, audioId?, level?, freePart?}`
///
/// 返回 `{code: 200, data: {url}}` 或 `{code, message}`（与 TS 模块一致，
/// 不抛错给调用方判断）。
KgModule kgSongUrl = (params) async {
  final hash = (params['hash'] ?? '').toString().toLowerCase().trim();
  if (hash.isEmpty) return {'code': 400, 'message': 'hash required'};

  final albumId = _intOf(params['albumId']);
  final audioId = _intOf(params['audioId']);
  final userid = kgUserId();
  final preferred = _qualityMap['${params['level'] ?? 'hq'}'] ?? '320';

  // 首次取流前注册设备 dfid；失败回退 '-'（匿名通道仍可返回试听/免费档）
  try {
    await kgEnsureDfid();
  } catch (_) {
    // 注册失败不阻断取流，网关按匿名设备处理
  }

  final preferredIndex = _qualityLevels.indexOf(preferred);
  final fallbackChain = _qualityLevels
      .sublist(
        0,
        preferredIndex < 0 ? _qualityLevels.length : preferredIndex + 1,
      )
      .reversed
      .toList();

  var candidates = [
    for (final quality in fallbackChain) (hash: hash, quality: quality),
  ];

  // 登录账号：用 privilege/lite 换取各档位变体 hash
  if (kgLoggedIn) {
    try {
      final options = await _fetchLiteQualityOptions(hash, albumId);
      final privileged = [
        for (final quality in fallbackChain)
          if (options[quality] != null)
            (hash: options[quality]!, quality: quality),
      ];
      if (privileged.isNotEmpty) candidates = privileged;
    } catch (_) {
      // 换取失败 → 用原始 hash 继续
    }
  }

  Map last = const {};
  String lastError = '';
  for (final candidate in candidates) {
    try {
      final response = await _requestUrl(
        candidate.hash,
        candidate.quality,
        albumId,
        audioId,
        userid,
        params['freePart'] == true,
      );
      last = response;
      final urls = _extractUrls(response);
      if (urls.isNotEmpty && response['extName'] != 'mp4') {
        return {
          'code': 200,
          'quality': candidate.quality,
          'data': {'url': urls.first},
        };
      }
      final err = response['error'] ?? response['errmsg'] ?? response['msg'];
      if (err != null) lastError = '$err';
    } on KgApiException catch (e) {
      lastError = e.message;
    }
  }

  final code = last['errcode'] ?? last['error_code'] ?? 500;
  return {
    'code': code is num ? code.toInt() : int.tryParse('$code') ?? 500,
    'message': lastError.isNotEmpty ? lastError : '${last['error'] ?? ''}',
  };
};

int _intOf(Object? v) => v is num ? v.toInt() : int.tryParse('${v ?? ''}') ?? 0;
