// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 扫码登录（KG）——对齐 apis/kugou/modules/login_qr.ts。
///
/// 两步：`/v2/qrcode` 取 key → 轮询 `/v2/get_userinfo_qrcode`
/// （0=过期 / 1=等待 / 2=待确认 / 4=成功）。登录接口用 **web 签名**，
/// 且 `appid=1001` 固定（不是平台 appid）；`qrcode_txt` 里的 appid 才是
/// 当前平台（lite=3116）。
library;

import 'dart:convert';

import '../core/config.dart';
import '../core/crypto.dart';
import '../core/request.dart';
import '../core/session.dart';
import '../core/types.dart';

const _loginBaseUrl = 'https://login-user.kugou.com';

Future<Map<String, dynamic>> _request(
  String path,
  Map<String, dynamic> input,
) async {
  final params = <String, dynamic>{
    'dfid': '-',
    'mid': kgMid(),
    'uuid': '-',
    'appid': 1001,
    'clientver': kgLiteClientver,
    'clienttime': DateTime.now().millisecondsSinceEpoch ~/ 1000,
    ...input,
  };
  params['signature'] = kgSignatureWebParams(params);
  final text = await kgHttpGet('$_loginBaseUrl$path?${kgQueryString(params)}');
  final body = jsonDecode(kgCleanResponse(text));
  return body is Map ? Map<String, dynamic>.from(body) : const {};
}

/// 生成二维码 key：`{code:200, key, content}`
KgModule kgLoginQrKey = (params) async {
  final body = await _request('/v2/qrcode', {
    'type': 1,
    'plat': 4,
    'qrcode_txt': '$kgQrLoginPage?appid=$kgLiteAppid&',
    'srcappid': kgSrcAppid,
  });
  final data = body['data'];
  final key = data is Map ? (data['qrcode'] ?? '').toString() : '';
  if (key.isEmpty) throw KgApiException('KG 二维码 key 缺失');
  return {'code': 200, 'key': key, 'content': '$kgQrLoginPage?qrcode=$key'};
};

/// 轮询扫码状态：`{code:200, status, nickname, avatarUrl}`
KgModule kgLoginQrCheck = (params) async {
  final key = '${params['key'] ?? ''}'.trim();
  if (key.isEmpty) throw KgApiException('KG 二维码 key 缺失');

  final body = await _request('/v2/get_userinfo_qrcode', {
    'plat': 4,
    'appid': kgLiteAppid,
    'srcappid': kgSrcAppid,
    'qrcode': key,
  });
  final data = body['data'] is Map
      ? Map<String, dynamic>.from(body['data'] as Map)
      : const <String, dynamic>{};
  final status = data['status'] is num
      ? (data['status'] as num).toInt()
      : int.tryParse('${data['status'] ?? ''}') ?? 1;
  final nickname = _firstNonEmpty([
    data['username']?.toString(),
    data['nickname']?.toString(),
    data['nick_name']?.toString(),
    data['user_name']?.toString(),
  ]);
  final avatar = _firstNonEmpty([
    data['userpic']?.toString(),
    data['user_pic']?.toString(),
    data['avatar']?.toString(),
    data['avatar_url']?.toString(),
    data['pic']?.toString(),
    data['user_img']?.toString(),
  ]);

  if (status == 4) {
    kgSaveSession({
      'token': '${data['token'] ?? ''}',
      'userid': '${data['userid'] ?? ''}',
      if (nickname.isNotEmpty) 'nickname': nickname,
      if (avatar.isNotEmpty) 'avatarUrl': kgSecureUrl(avatar),
      if (data['vip_type'] != null) 'vip_type': '${data['vip_type']}',
      if (data['vip_token'] != null) 'vip_token': '${data['vip_token']}',
    });
  }

  return {
    'code': 200,
    'status': status,
    'nickname': nickname.isEmpty ? null : nickname,
    'avatarUrl': avatar.isEmpty ? null : kgSecureUrl(avatar),
  };
};

String _firstNonEmpty(List<String?> values) {
  for (final v in values) {
    if (v != null && v.trim().isNotEmpty) return v.trim();
  }
  return '';
}
