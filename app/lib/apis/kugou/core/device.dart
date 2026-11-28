// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 安装级设备注册——对齐 apis/kugou/core/device.ts。
///
/// `register_dev` 返回的 dfid 会持久化到 `kugou_device`，与 `services/kugou`
/// 共用同一份设备记录：同一安装跨进程/跨登录复用同一 GUID / MID / dfid，
/// 不随机换设备（风控要点）。
library;

import 'dart:convert';
import 'dart:math';

import 'config.dart';
import 'crypto.dart';
import 'request.dart';
import 'session.dart';

const _kgRegisterUrl = 'https://userservice.kugou.com/risk/v2/r_register_dev';

/// register_dev 的设备信息（对齐 module/register_dev.js 默认值）
const _kgDeviceInfo = <String, dynamic>{
  'availableRamSize': 4983533568,
  'availableRomSize': 48114719,
  'availableSDSize': 48114717,
  'basebandVer': '',
  'batteryLevel': 100,
  'batteryStatus': 3,
  'brand': 'Redmi',
  'buildSerial': 'unknown',
  'device': 'marble',
  'imei': 'unknown',
  'imsi': '',
  'manufacturer': 'Xiaomi',
  'uuid': 'unknown',
  'accelerometer': false,
  'accelerometerValue': '',
  'gravity': false,
  'gravityValue': '',
  'gyroscope': false,
  'gyroscopeValue': '',
  'light': false,
  'lightValue': '',
  'magnetic': false,
  'magneticValue': '',
  'orientation': false,
  'orientationValue': '',
  'pressure': false,
  'pressureValue': '',
  'step_counter': false,
  'step_counterValue': '',
  'temperature': false,
  'temperatureValue': '',
};

Future<String>? _registering;

/// 确保有可用的 dfid（首次调用注册设备；并发调用共享同一请求）。
Future<String> kgEnsureDfid() {
  final current = kgDfid();
  if (current.isNotEmpty) return Future.value(current);
  return _registering ??= _registerDevice().whenComplete(() {
    _registering = null;
  });
}

/// 重新注册 dfid（同一 GUID，不换设备）。
///
/// 取流返回 `status=2` 时服务端要求重新验证设备指纹；这里清掉旧 dfid 后
/// 重新走一次 register_dev，与 `services/kugou` 的重试策略一致。
Future<String> kgRefreshDfid() {
  kgSaveDevice({'dfid': ''});
  _registering = null;
  return kgEnsureDfid();
}

Future<String> _registerDevice() async {
  final device = kgDevice();
  final guid = (device['guid'] ?? '').trim().isEmpty
      ? _uuidV4()
      : device['guid']!.trim();
  final mid = (device['mid'] ?? '').trim().isEmpty
      ? kgCalcMid(guid)
      : device['mid']!.trim();
  final ts = DateTime.now().millisecondsSinceEpoch ~/ 1000;

  final secretKey = kgRandomString(6).toLowerCase();
  final cipher = kgAesEncryptBase64(
    jsonEncode({..._kgDeviceInfo, 'imei': guid, 'uuid': guid}),
    secretKey,
  );
  final p = kgRsaPkcs1EncryptHex(
    jsonEncode({'aes': secretKey, 'uid': kgUserId(), 'token': kgToken()}),
  );

  final params = <String, dynamic>{
    'dfid': '-',
    'mid': mid,
    'uuid': '-',
    'appid': kgLiteAppid,
    'clientver': kgLiteClientver,
    'clienttime': ts,
    'part': 1,
    'platid': 1,
    'p': p,
  };
  params['signature'] = kgSignatureAndroidParams(params, data: cipher);

  final bytes = await kgHttpPostBytes(
    '$_kgRegisterUrl?${kgQueryString(params)}',
    body: cipher,
    headers: {
      'dfid': '-',
      'clienttime': '$ts',
      'mid': mid,
      'kg-rc': '1',
      'kg-thash': '5d816a0',
      'kg-rec': '1',
      'kg-rf': 'B9EDA08A64250DEFFBCADDEE00F8F25F',
      'Content-Type': 'text/plain;charset=utf-8',
    },
  );

  final Map decoded;
  try {
    final value = jsonDecode(
      kgAesDecryptBase64(base64.encode(bytes), secretKey),
    );
    if (value is! Map) throw const FormatException('not a map');
    decoded = value;
  } catch (_) {
    throw KgApiException('KG register_dev 响应异常（${bytes.length} bytes）');
  }
  final data = decoded['data'];
  final dfid = data is Map ? (data['dfid'] ?? '').toString() : '';
  final status = (decoded['status'] as num?)?.toInt();
  if (status != 1 || dfid.isEmpty) {
    throw KgApiException('KG register_dev 失败: $decoded');
  }

  kgSaveDevice({'guid': guid, 'mid': mid, 'dfid': dfid});
  return dfid;
}

/// 生成 UUID v4（RFC 4122），用于设备 GUID。
String _uuidV4() {
  final rng = Random.secure();
  final bytes = List<int>.generate(16, (_) => rng.nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}
