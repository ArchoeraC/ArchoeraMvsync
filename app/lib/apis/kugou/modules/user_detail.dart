// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 用户资料（KG）——对齐 apis/kugou/modules/user_detail.ts。
///
/// 未登录返回 `{code:200, loggedIn:false}`；登录后调 `/v3/get_my_info`
/// （`p` 为 raw RSA 密文），`vip_type` 只反映标准会员，概念版会员需再查
/// `/v1/get_union_vip`。资料写回 `kugou` 会话。
library;

import 'dart:convert';

import '../core/config.dart';
import '../core/crypto.dart';
import '../core/request.dart';
import '../core/session.dart';
import '../core/types.dart';

KgModule kgUserDetail = (params) async {
  if (!kgLoggedIn) return {'code': 200, 'loggedIn': false};

  final clienttime = DateTime.now().millisecondsSinceEpoch ~/ 1000;
  final resp = await kgGatewayRequest(
    '/v3/get_my_info',
    method: 'POST',
    params: {'plat': 1},
    data: {
      'visit_time': clienttime,
      'usertype': 1,
      'p': kgRsaRawEncryptHex(
        jsonEncode({'token': kgToken(), 'clienttime': clienttime}),
      ).toUpperCase(),
      'userid': kgUserId(),
    },
    headers: {'x-router': 'usercenter.kugou.com'},
  );

  final data = resp is Map && resp['data'] is Map
      ? Map<String, dynamic>.from(resp['data'] as Map)
      : const <String, dynamic>{};
  final session = kgSession();
  final nickname = _firstNonEmpty([
    data['nickname']?.toString(),
    data['username']?.toString(),
    data['user_name']?.toString(),
    session['nickname'],
  ]);
  final avatar = _firstNonEmpty([
    data['pic']?.toString(),
    data['userpic']?.toString(),
    data['user_pic']?.toString(),
    data['avatar']?.toString(),
    session['avatarUrl'],
  ]);

  var isVip = _intOf(data['vip_type']) > 0;
  if (!isVip) {
    try {
      final union = await kgGatewayRequest(
        '/v1/get_union_vip',
        baseUrl: 'https://kugouvip.kugou.com',
        params: {'busi_type': 'concept'},
      );
      final unionData = union is Map ? union['data'] : null;
      final busiVip = unionData is Map ? unionData['busi_vip'] : null;
      if (busiVip is List && busiVip.isNotEmpty) {
        isVip = _intOf((busiVip.first as Map)['is_vip']) == 1;
      }
    } catch (_) {
      // 会员信息失败不影响资料展示
    }
  }

  final updates = <String, String>{
    if (nickname.isNotEmpty) 'nickname': nickname,
    if (avatar.isNotEmpty) 'avatarUrl': kgSecureUrl(avatar),
    if (data['vip_type'] != null) 'vip_type': '${data['vip_type']}',
  };
  if (updates.isNotEmpty) kgSaveSession(updates);

  return {
    'code': 200,
    'loggedIn': true,
    'profile': {
      'userId': session['userid'] ?? '',
      'nickname': nickname.isNotEmpty
          ? nickname
          : 'KG ${session['userid'] ?? ''}',
      'avatarUrl': avatar.isNotEmpty ? kgSecureUrl(avatar) : '',
      'isVip': isVip,
      'vipLevel': _intOf(data['vip_type']),
    },
  };
};

String _firstNonEmpty(List<String?> values) {
  for (final v in values) {
    if (v != null && v.trim().isNotEmpty) return v.trim();
  }
  return '';
}

int _intOf(Object? v) => v is num ? v.toInt() : int.tryParse('${v ?? ''}') ?? 0;
