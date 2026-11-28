// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 加密与签名——对齐 apis/kugou/core/crypto.ts。
///
/// 只保留网关/概念版（lite）需要的子集：MD5、设备 MID、android/web 签名、
/// AES-128-CBC（register_dev 设备信封）与 RSA（register_dev 的 `p`、
/// user_detail 的 `p`）。SSA 行为指纹的 RSA-OAEP 在 `services/kugou`
/// 风控链路里，与本层无关。
library;

import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as crypto;
import 'package:pointycastle/export.dart';

import 'config.dart';

/// android 签名盐值（概念版 lite）
const kgLiteSignSalt = 'LnT6xpN3khm36zse0QzvmgTZ3waWdRSA';

/// `key` / `signKey` 签名盐值（概念版 lite）
const kgLiteKeySalt = '185672dd44712f60bb1736df5a377e82';

/// web 签名盐值（扫码登录等 web 接口）
const kgWebSignSalt = 'NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt';

/// 概念版 lite 的 RSA 公钥（1024bit，SPKI PEM）
const kgLitePublicKeyPem = '''-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDECi0Np2UR87scwrvTr72L6oO01rBbbBPriSDFPxr3Z5syug0O24QyQO8bg27+0+4kBzTBTBOZ/WWU0WryL1JSXRTXLgFVxtzIY41Pe7lPOgsfTCn5kZcvKhYKJesKnnJDNr5/abvTGf+rHG3YRwsCHcQ08/q6ifSioBszvb3QiwIDAQAB
-----END PUBLIC KEY-----''';

const _kgPool = '1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ';

/// 生成随机字符串（大写字母 + 数字，对齐 KuGouMusicApi `randomString`）
String kgRandomString(int len) {
  final rng = Random.secure();
  return String.fromCharCodes(
    List.generate(len, (_) => _kgPool.codeUnitAt(rng.nextInt(36))),
  );
}

/// MD5 hex（小写）
String kgMd5(String data) => crypto.md5.convert(utf8.encode(data)).toString();

/// 设备 MID：`md5(guid)` 的 hex 按大整数转十进制
String kgCalcMid(String guid) {
  final hex = crypto.md5.convert(utf8.encode(guid)).toString();
  return BigInt.parse('0x$hex').toString();
}

/// AES-128-CBC 加密：key = md5(secretKey) 前 16 字符，iv = 后 16 字符。
/// 返回 base64 密文（register_dev 设备信封）。
String kgAesEncryptBase64(String plain, String secretKey) {
  final (key, iv) = _aesKeyIv(secretKey);
  return base64.encode(_aesCipher(true, key, iv).process(utf8.encode(plain)));
}

/// AES-128-CBC 解密 base64 密文 → utf8 文本（register_dev 响应）。
String kgAesDecryptBase64(String cipherB64, String secretKey) {
  final (key, iv) = _aesKeyIv(secretKey);
  return utf8.decode(
    _aesCipher(false, key, iv).process(base64.decode(cipherB64)),
  );
}

(String, String) _aesKeyIv(String secretKey) {
  final digest = kgMd5(secretKey);
  return (digest.substring(0, 16), digest.substring(16, 32));
}

/// 构造一次性的 AES-128-CBC/PKCS7 处理器。
///
/// 返回的 [PaddedBlockCipherImpl] 只能 `process` 一次：每次加/解密都在
/// 内部保存链式状态。
PaddedBlockCipherImpl _aesCipher(bool forEncryption, String key, String iv) {
  final cipher = PaddedBlockCipherImpl(
    PKCS7Padding(),
    CBCBlockCipher(AESEngine()),
  );
  cipher.init(
    forEncryption,
    PaddedBlockCipherParameters<CipherParameters, CipherParameters>(
      ParametersWithIV<CipherParameters>(
        KeyParameter(utf8.encode(key)),
        utf8.encode(iv),
      ),
      null,
    ),
  );
  return cipher;
}

/// RSA-PKCS1v1.5 加密 → 小写 hex（register_dev 的 `p`）。
String kgRsaPkcs1EncryptHex(String jsonStr) {
  final (n, e) = _rsaPublicKey();
  final enc = PKCS1Encoding(RSAEngine());
  enc.init(true, PublicKeyParameter<RSAPublicKey>(RSAPublicKey(n, e)));
  return enc
      .process(utf8.encode(jsonStr))
      .map((b) => b.toRadixString(16).padLeft(2, '0'))
      .join();
}

/// raw RSA（零填充）加密 → 小写 hex（user_detail 的 `p`，调用方转大写）。
///
/// 数据左对齐补 0 到模长 128 字节后直接 modPow，与 PKCS1 完全不同，
/// 不可混用。
String kgRsaRawEncryptHex(String jsonStr) {
  final (n, e) = _rsaPublicKey();
  final data = utf8.encode(jsonStr);
  final padded = Uint8List(128)..setAll(0, data);
  final enc = RSAEngine();
  enc.init(true, PublicKeyParameter<RSAPublicKey>(RSAPublicKey(n, e)));
  return enc
      .process(padded)
      .map((b) => b.toRadixString(16).padLeft(2, '0'))
      .join();
}

/// android 签名：md5(salt + 排序后的 key=value 串 + body + salt)。
String kgSignatureAndroidParams(
  Map<String, dynamic> params, {
  String data = '',
}) {
  final keys = params.keys.toList()..sort();
  final buf = StringBuffer();
  for (final k in keys) {
    final v = params[k];
    buf.write('$k=${v is Map || v is List ? jsonEncode(v) : v}');
  }
  return kgMd5('$kgLiteSignSalt$buf$data$kgLiteSignSalt');
}

/// web 签名：md5(salt + 排序后的 key=value 串 + salt)。
String kgSignatureWebParams(Map<String, dynamic> params) {
  final keys = params.keys.toList()..sort();
  final buf = StringBuffer();
  for (final k in keys) {
    final v = params[k];
    buf.write('$k=${v is Map || v is List ? jsonEncode(v) : v}');
  }
  return kgMd5('$kgWebSignSalt$buf$kgWebSignSalt');
}

/// 参数密钥签名：md5(appid + 盐 + clientver + data)。
String kgSignParamsKey(String data) =>
    kgMd5('$kgLiteAppid$kgLiteSignSalt$kgLiteClientver$data');

/// 取流 `key` 签名：md5(hash + keySalt + appid + mid + userid)。
String kgSignKey(String hash, String mid, int userid) =>
    kgMd5('$hash$kgLiteKeySalt$kgLiteAppid$mid$userid');

/// 清洗 KG 响应：剥离 `<!--KG_TAG_RES_START-->` / `<!--KG_TAG_RES_END-->`。
String kgCleanResponse(String text) => text
    .trim()
    .replaceFirst(RegExp(r'^<!--KG_TAG_RES_START-->'), '')
    .replaceFirst(RegExp(r'<!--KG_TAG_RES_END-->$'), '');

// ─── RSA 公钥解析（SPKI PEM → n/e，结果缓存） ─────────────────────

(BigInt, BigInt)? _rsaKey;

(BigInt, BigInt) _rsaPublicKey() {
  final cached = _rsaKey;
  if (cached != null) return cached;
  final b64 = kgLitePublicKeyPem.replaceAll(
    RegExp(r'-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s'),
    '',
  );
  final outer = _DerReader(base64.decode(b64));
  final (_, body) = outer.read();
  final seq = _DerReader(body);
  seq.read(); // 算法 OID + NULL
  final (_, bitString) = seq.read();
  final inner = _DerReader(bitString.sublist(1)); // 跳过未使用位字节
  final (_, rsaSeq) = inner.read();
  final fields = _DerReader(rsaSeq);
  final (_, n) = fields.read();
  final (_, e) = fields.read();
  final key = (_bytesToBigInt(n), _bytesToBigInt(e));
  _rsaKey = key;
  return key;
}

BigInt _bytesToBigInt(List<int> bytes) {
  var result = BigInt.zero;
  for (final b in bytes) {
    result = (result << 8) | BigInt.from(b);
  }
  return result;
}

/// 最小 DER 读取器：只处理本文件用到的 SEQUENCE / INTEGER / BIT STRING。
class _DerReader {
  _DerReader(this._data);

  final List<int> _data;
  int _pos = 0;

  (int, List<int>) read() {
    final tag = _data[_pos++];
    var len = _data[_pos++];
    if (len & 0x80 != 0) {
      final n = len & 0x7f;
      len = 0;
      for (var i = 0; i < n; i++) {
        len = (len << 8) | _data[_pos++];
      }
    }
    final content = Uint8List.fromList(_data.sublist(_pos, _pos + len));
    _pos += len;
    return (tag, content);
  }
}
