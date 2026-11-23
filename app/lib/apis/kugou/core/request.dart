// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 请求层——对齐 apis/kugou/core/request.ts。
///
/// 两种入口：
/// - [kgRequest]：公开接口基础 GET（mobilecdn / songsearch / lyrics）；
/// - [kgGatewayRequest]：网关鉴权请求（自动注入设备标识、时间戳、android
///   签名与网关路由头）。
///
/// 成功码约定不统一：songsearch/mobilecdn/lyrics 用 `error_code` 0 或 200，
/// 网关在多数情况下用 `error_code`，部分模块（取流）用 `status` 表达业务
/// 结果。这里只按 `error_code/errcode/err_code` 判失败，`status` 交给模块
/// 自行解释（与 TS 侧一致）。
library;

import 'dart:convert';
import 'dart:io';

import 'config.dart';
import 'crypto.dart';
import 'session.dart';

/// KG 接口/传输错误
class KgApiException implements Exception {
  KgApiException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// 发一次公开 GET 请求 → JSON body
Future<Map<String, dynamic>> kgRequest(
  String url, {
  Map<String, String> headers = const {},
  Duration timeout = const Duration(seconds: 8),
}) async {
  final text = await kgHttpGet(url, headers: headers, timeout: timeout);
  final body = jsonDecode(kgCleanResponse(text));
  if (body is! Map<String, dynamic>) {
    throw KgApiException('KG 响应异常: ${body.runtimeType}');
  }
  _checkKgCode(body);
  return body;
}

/// 低层 GET → 原始文本（自动加 UA）
Future<String> kgHttpGet(
  String url, {
  Map<String, String> headers = const {},
  Duration timeout = const Duration(seconds: 8),
}) async {
  final bytes = await kgHttpGetBytes(url, headers: headers, timeout: timeout);
  return utf8.decode(bytes, allowMalformed: true);
}

/// 低层 GET → 原始字节（register_dev 等二进制响应）
Future<List<int>> kgHttpGetBytes(
  String url, {
  Map<String, String> headers = const {},
  Duration timeout = const Duration(seconds: 8),
}) async {
  final client = HttpClient()..connectionTimeout = timeout;
  try {
    final req = await client.getUrl(Uri.parse(url)).timeout(timeout);
    req.headers.set(HttpHeaders.userAgentHeader, kgAndroidUa);
    headers.forEach(req.headers.set);
    final res = await req.close().timeout(timeout);
    if (res.statusCode != 200) {
      throw KgApiException('KG HTTP ${res.statusCode}');
    }
    return await res.fold<List<int>>(<int>[], (a, b) => a..addAll(b));
  } finally {
    client.close(force: true);
  }
}

/// 低层 POST → 原始文本
Future<String> kgHttpPost(
  String url, {
  required String body,
  Map<String, String> headers = const {},
  Duration timeout = const Duration(seconds: 8),
}) async {
  final bytes = await kgHttpPostBytes(
    url,
    body: body,
    headers: headers,
    timeout: timeout,
  );
  return utf8.decode(bytes, allowMalformed: true);
}

/// 低层 POST → 原始字节（register_dev 的响应是 AES 二进制密文）
Future<List<int>> kgHttpPostBytes(
  String url, {
  required String body,
  Map<String, String> headers = const {},
  Duration timeout = const Duration(seconds: 8),
}) async {
  final client = HttpClient()..connectionTimeout = timeout;
  try {
    final req = await client.postUrl(Uri.parse(url)).timeout(timeout);
    req.headers.set(HttpHeaders.userAgentHeader, kgAndroidUa);
    headers.forEach(req.headers.set);
    req.write(body);
    final res = await req.close().timeout(timeout);
    if (res.statusCode != 200) {
      throw KgApiException('KG HTTP ${res.statusCode}');
    }
    return await res.fold<List<int>>(<int>[], (a, b) => a..addAll(b));
  } finally {
    client.close(force: true);
  }
}

/// 请求参数序列化：对象/数组按 JSON 编码，null/空值跳过（对齐
/// `URLSearchParams` 行为，服务端按解码后的原文验签）。
String kgQueryString(Map<String, dynamic> params) => params.entries
    .where((e) => e.value != null)
    .map(
      (e) =>
          '${e.key}=${Uri.encodeQueryComponent(switch (e.value) {
            final Map value => jsonEncode(value),
            final List value => jsonEncode(value),
            final value => '$value',
          })}',
    )
    .join('&');

/// 统一网关请求（对齐 apis/kugou/core/request.ts `kgGatewayRequest`）。
///
/// 1. 注入默认参数 `{dfid, mid, uuid:'-', appid, clientver, clienttime}` 与
///    登录态 `token` / `userid`（非空才注入）；
/// 2. [data] 序列化（Map/List → JSON 原文，String 原样）参与签名；
/// 3. android 签名（lite 盐）写入 `signature`；
/// 4. 拼接 `kg-*` 头与业务头后发送。
Future<dynamic> kgGatewayRequest(
  String path, {
  String method = 'GET',
  String baseUrl = kgGatewayUrl,
  Map<String, dynamic> params = const {},
  Object? data,
  Map<String, String> headers = const {},
  bool notSignature = false,
  Duration timeout = const Duration(seconds: 10),
}) async {
  final clienttime = DateTime.now().millisecondsSinceEpoch ~/ 1000;
  final mid = kgMid();
  final dfid = kgDfid().isEmpty ? '-' : kgDfid();
  final token = kgToken();
  final userid = kgUserId();

  final merged = <String, dynamic>{
    'dfid': dfid,
    'mid': mid,
    'uuid': '-',
    'appid': kgLiteAppid,
    'clientver': kgLiteClientver,
    'clienttime': clienttime,
    if (token.isNotEmpty) 'token': token,
    if (userid != 0) 'userid': userid,
    ...params,
  };

  final dataStr = data == null
      ? ''
      : data is String
      ? data
      : jsonEncode(data);

  if (!notSignature && merged['signature'] == null) {
    merged['signature'] = kgSignatureAndroidParams(merged, data: dataStr);
  }

  final url = '$baseUrl$path?${kgQueryString(merged)}';
  final reqHeaders = <String, String>{
    HttpHeaders.userAgentHeader: kgAndroidUa,
    'dfid': dfid,
    'clienttime': '$clienttime',
    'mid': mid,
    'kg-rc': '1',
    'kg-thash': '5d816a0',
    'kg-rec': '1',
    'kg-rf': 'B9EDA08A64250DEFFBCADDEE00F8F25F',
    ...headers,
    if (method == 'POST' && !headers.containsKey(HttpHeaders.contentTypeHeader))
      HttpHeaders.contentTypeHeader: 'application/json;charset=UTF-8',
  };

  final text = method == 'GET'
      ? await kgHttpGet(url, headers: reqHeaders, timeout: timeout)
      : await kgHttpPost(
          url,
          body: dataStr,
          headers: reqHeaders,
          timeout: timeout,
        );

  final dynamic body;
  try {
    body = jsonDecode(kgCleanResponse(text));
  } on FormatException {
    throw KgApiException('KG $path 响应不是 JSON: ${text.trim()}');
  }
  if (body is Map) _checkKgCode(body, path: path);
  return body;
}

/// `error_code/errcode/err_code` 非 0/200 视为失败
void _checkKgCode(Map body, {String? path}) {
  final dynamic code =
      body['error_code'] ?? body['errcode'] ?? body['err_code'] ?? 0;
  final numeric = code is num ? code.toInt() : int.tryParse('$code') ?? 0;
  if (numeric == 0 || numeric == 200) return;
  final label = path == null ? 'KG' : 'KG $path';
  final detail = body['error_msg'] ?? body['errmsg'] ?? body['msg'] ?? '$body';
  throw KgApiException('$label error_code=$numeric: $detail');
}
