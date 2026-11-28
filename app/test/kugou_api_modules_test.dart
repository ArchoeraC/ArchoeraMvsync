// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG API 移植回归（**不联网**）。
///
/// 1. crypto 与 `services/kugou` 的既有实现逐函数对齐（签名/盐值/设备 MID
///    一旦漂移会直接导致网关 401/签名错误）；
/// 2. 歌曲归一化（专辑/歌单/歌手共用的身份与音质字段）；
/// 3. 分页聚合的完整性校验（已知总数 / 未知总数两种路径）。
library;

import 'package:archoera_mvsync/apis/kugou/core/crypto.dart' as api;
import 'package:archoera_mvsync/apis/kugou/core/pagination.dart';
import 'package:archoera_mvsync/apis/kugou/core/song.dart';
import 'package:archoera_mvsync/services/kugou/kugou_crypto.dart' as svc;
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('crypto 与既有实现对齐', () {
    test('md5 / 设备 MID', () {
      expect(api.kgMd5('archoera'), svc.kgMd5('archoera'));
      expect(
        api.kgCalcMid('550e8400-e29b-41d4-a716-446655440000'),
        svc.kgCalcMid('550e8400-e29b-41d4-a716-446655440000'),
      );
    });

    test('android 签名（lite 盐）', () {
      final params = <String, dynamic>{
        'dfid': '-',
        'mid': '123',
        'appid': 3116,
        'clienttime': 1700000000,
        'keyword': '海阔天空',
      };
      expect(
        api.kgSignatureAndroidParams(params, data: '{"a":1}'),
        svc.kgSignature(params, data: '{"a":1}', salt: svc.kgLiteSignSalt),
      );
    });

    test('web 签名 / 参数 key / 取流 key', () {
      final params = <String, dynamic>{'appid': 1001, 'plat': 4, 'qrcode': 'k'};
      expect(api.kgSignatureWebParams(params), svc.kgSignatureWeb(params));
      expect(
        api.kgSignParamsKey('1700000000'),
        svc.kgSignParamsKey('1700000000'),
      );
      expect(
        api.kgSignKey('HASH', 'MID', 42),
        svc.kgSignKey(
          'HASH',
          'MID',
          42,
          svc.kgLiteAppid,
          salt: svc.kgLiteKeySalt,
        ),
      );
    });

    test('AES-128-CBC（设备信封）可逆', () {
      const key = 'abc123';
      const plain = '{"brand":"Redmi","imei":"x"}';
      final cipher = api.kgAesEncryptBase64(plain, key);
      expect(api.kgAesDecryptBase64(cipher, key), plain);
      expect(cipher, svc.kgAesEncryptBase64(plain, key));
    });

    test('RSA raw / PKCS1 输出 256 位 hex', () {
      final raw = api.kgRsaRawEncryptHex('{"token":"t"}');
      expect(raw.length, 256);
      expect(RegExp(r'^[0-9a-f]+$').hasMatch(raw), isTrue);

      final pkcs1 = api.kgRsaPkcs1EncryptHex('{"aes":"k"}');
      expect(pkcs1.length, 256);
    });
  });

  group('kgNormalizeSong', () {
    Map<String, dynamic> raw() => {
      'base': {
        'audio_id': 3261014,
        'album_audio_id': 32155307,
        'audio_name': '海阔天空',
        'author_name': 'BEYOND',
        'album_id': 973001,
      },
      'audio_info': {
        'hash': 'H128',
        'filesize': 1000,
        'timelength': 324000,
        'hash_320': 'H320',
        'filesize_320': 2000,
        'hash_flac': 'HF',
        'filesize_flac': 3000,
        'hash_high': 'HH',
        'filesize_high': 4000,
      },
      'authors': [
        {'author_id': 3520, 'author_name': 'BEYOND'},
      ],
      'album_info': {'album_name': '乐与怒', 'cover': 'http://x/{size}.jpg'},
      'deprecated': {'pay_type': 1, 'price': 2, 'feetype': 3},
    };

    test('身份 / 时长 / 音质档位', () {
      final song = kgNormalizeSong(raw());
      expect(song['id'], '3261014');
      expect(song['hash'], 'H128');
      expect(song['albumAudioId'], 32155307);
      expect(song['duration'], 324000);
      expect(song['interval'], 324.0);
      expect(song['hashes'], {
        '128k': 'H128',
        '320k': 'H320',
        'flac': 'HF',
        'flac24bit': 'HH',
      });
      expect(song['sizes'], {
        '128k': 1000,
        '320k': 2000,
        'flac': 3000,
        'flac24bit': 4000,
      });
      expect(song['qualities'], ['128k', '320k', 'flac', 'flac24bit']);
      expect((song['pay'] as Map)['payplay'], 1);
      expect((song['pay'] as Map)['feetype'], 3);
    });

    test('展示字段 / 封面填充 / 专辑兜底', () {
      final song = kgNormalizeSong(raw());
      expect(song['name'], '海阔天空');
      expect(song['artist'], 'BEYOND');
      expect(song['artistId'], '3520');
      expect(song['album'], '乐与怒');
      expect(song['albumId'], '973001');
      expect(song['cover'], 'https://x/300.jpg');
      expect(song['coverOriginal'], 'https://x/480.jpg');

      final noAlbum = kgNormalizeSong(
        {
          'base': {'hash': 'h', 'audio_name': 'x'},
        },
        albumId: '42',
        albumName: '兜底专辑',
      );
      expect(noAlbum['albumId'], '42');
      expect(noAlbum['album'], '兜底专辑');
      expect(noAlbum['hashes'], isEmpty);
    });

    test('缺 size 的档位不进入 hashes（避免取链拿到无长度档位）', () {
      final song = kgNormalizeSong({
        'audio_info': {'hash': 'h128', 'filesize': 100, 'hash_320': 'h320'},
      });
      expect(song['hashes'], {'128k': 'h128'});
      expect(song['sizes'], {'128k': 100});
    });
  });

  group('kgFetchAllPages', () {
    test('已知总数：并发分页、截断到 total', () async {
      const total = 250;
      final requested = <int>[];
      final result = await kgFetchAllPages<int>((page) async {
        requested.add(page);
        final start = (page - 1) * 100;
        final end = (start + 100).clamp(0, total);
        return (items: [for (var i = start; i < end; i++) i], total: total);
      }, 100);

      expect(result.total, total);
      expect(result.items.length, total);
      expect(result.items.first, 0);
      expect(result.items.last, total - 1);
      expect(requested.toSet(), {1, 2, 3});
    });

    test('未知总数：顺序拉取到短页', () async {
      final requested = <int>[];
      final result = await kgFetchAllPages<int>((page) async {
        requested.add(page);
        final count = page == 3 ? 5 : 10;
        return (
          items: [for (var i = 0; i < count; i++) page * 100 + i],
          total: null,
        );
      }, 10);

      expect(requested, [1, 2, 3]);
      expect(result.total, 25);
      expect(result.items.length, 25);
    });

    test('已知总数但数据不足 → 抛错（不静默截断）', () async {
      expect(
        () => kgFetchAllPages<int>(
          (page) async => (items: const [1, 2], total: 10),
          10,
        ),
        throwsStateError,
      );
    });
  });
}
