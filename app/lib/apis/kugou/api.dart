// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 主进程服务（Dart 移植）——对齐 apis/kugou/index.ts。
///
/// 统一入口：[kgCall]。搜索走 mobilecdn（含封面）、歌词走 hash+歌名+时长
/// 三元组；专辑/歌手/歌单/取流/资料/扫码登录见 `modules/`。
///
/// 缓存：默认进程内 LRU；登录态、取流直链、评论等实时接口不缓存。
library;

import '../lru_cache.dart';
import 'core/types.dart';
import 'modules/index.dart';

/// 不缓存的实时接口（token / 直链 / 一次性状态）
const _nonCacheable = <String>{
  'login_qr_key',
  'login_qr_check',
  'user_detail',
  'song_url',
  'comment',
};

bool _isEmptyResult(Object? value) {
  if (value is! Map) return false;
  for (final key in const ['songs', 'albums', 'artists', 'playlists']) {
    final list = value[key];
    if (list is List && list.isEmpty) return true;
  }
  return false;
}

final LruCache<Object> _apiCache = LruCache<Object>(
  shouldCache: (v) => !_isEmptyResult(v),
);

/// 清空 KG 接口缓存
void kgClearCache() => _apiCache.clear();

/// 调用任意 KG API
/// [name] 见 [kgModules] 的 key；实时接口（见 [_nonCacheable]）不吃缓存。
Future<Object?> kgCall(String name, [KgParams params = const {}]) async {
  final fn = kgModules[name];
  if (fn == null) throw StateError('unknown kg api: $name');

  if (_nonCacheable.contains(name)) return fn(params);

  final key = LruCache.key(name, params);
  final hit = _apiCache.get(key);
  if (hit != null) return hit;

  final value = await fn(params);
  if (value != null) _apiCache.set(key, value);
  return value;
}
