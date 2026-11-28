// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌曲评论（KG）——对应 apis/kugou/modules/comment.ts。
///
/// 审计结论（2026-11 实测）：
/// - TS 侧的网关 `/mcomment/v1/cmtlist` 在匿名设备上返回
///   `{status:1, data:null}`（登录后才可能有数据）；
/// - `mcomment.kugou.com/index.php?r=commentsv2/getCommentWithLike` 无需
///   登录即可拿到完整分页（`count` / `list`），与本应用 `services/kugou`
///   的评论链路一致。故本模块落在这条通道上。
library;

import 'dart:convert';

import '../core/crypto.dart';
import '../core/request.dart';
import '../core/types.dart';

const _commentUrl = 'https://mcomment.kugou.com/index.php';
const _commentUa = 'Android810-AndroidPhone-10659-14-0-COMMENT-wifi';

/// 评论固定 code（安卓客户端逆向值）
const _commentCode = 'fc4be23b4e972707f36b8a828a93ba8a';

/// 评论模块：`{hash, page?, limit?}`
KgModule kgComment = (params) async {
  final hash = '${params['hash'] ?? ''}'.trim();
  if (hash.isEmpty) return {'code': 400, 'message': 'hash required'};
  final page = _intOf(params['page'], fallback: 1);
  final limit = _intOf(params['limit'], fallback: 20);

  final query =
      'r=commentsv2/getCommentWithLike'
      '&code=$_commentCode'
      '&p=$page'
      '&pagesize=$limit'
      '&platform=android';
  final text = await kgHttpPost(
    '$_commentUrl?$query',
    body: 'extdata=$hash',
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
      'User-Agent': _commentUa,
    },
  );
  final body = jsonDecode(kgCleanResponse(text));
  if (body is! Map) throw KgApiException('KG 评论响应异常');

  final errCode = _intOf(body['err_code']);
  if (errCode != 0 && errCode != 200) {
    throw KgApiException('KG 评论 error_code=$errCode: ${body['msg'] ?? ''}');
  }

  final list = body['list'];
  return {
    'code': 200,
    'count': _intOf(body['count']),
    'page': page,
    'limit': limit,
    'list': list is List ? list : const [],
  };
};

int _intOf(Object? v, {int fallback = 0}) =>
    v is num ? v.toInt() : int.tryParse('${v ?? ''}') ?? fallback;
