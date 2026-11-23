// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 歌曲归一化——对齐 apis/kugou/core/song.ts。
///
/// 歌单 / 专辑 / 歌手三个接口的歌曲结构不同，这里统一为同一份 Map：
/// 身份（id / hash / albumAudioId）、音质（hashes / sizes）、展示字段
/// （name / artist / album / cover）与付费信息。
library;

import 'config.dart';

bool _truthy(Object? v) => switch (v) {
  null => false,
  false => false,
  num n => n != 0,
  String s => s.isNotEmpty,
  _ => true,
};

String _firstTruthyString(List<Object?> values) {
  for (final v in values) {
    final s = v?.toString() ?? '';
    if (s.isNotEmpty && s != '0') return s;
  }
  return '';
}

int _firstPositiveInt(List<Object?> values) {
  for (final v in values) {
    final n = v is num ? v.toInt() : int.tryParse('${v ?? ''}') ?? 0;
    if (n > 0) return n;
  }
  return 0;
}

/// 把接口资源条目归一化为公共歌曲结构（时长单位毫秒）。
///
/// [albumId] / [albumName] / [cover] 为集合已知的专辑信息（条目本身缺省时
/// 兜底，如专辑详情页的 `album_id` 与封面）。
Map<String, dynamic> kgNormalizeSong(
  Map<String, dynamic> raw, {
  String? albumId,
  String? albumName,
  String? cover,
}) {
  final base = raw['base'] is Map
      ? Map<String, dynamic>.from(raw['base'] as Map)
      : const <String, dynamic>{};
  final audio = raw['audio_info'] is Map
      ? Map<String, dynamic>.from(raw['audio_info'] as Map)
      : const <String, dynamic>{};
  final albumInfo = raw['album_info'] is Map
      ? Map<String, dynamic>.from(raw['album_info'] as Map)
      : const <String, dynamic>{};
  final transParam = raw['trans_param'] is Map
      ? Map<String, dynamic>.from(raw['trans_param'] as Map)
      : const <String, dynamic>{};
  final deprecated = raw['deprecated'] is Map
      ? Map<String, dynamic>.from(raw['deprecated'] as Map)
      : const <String, dynamic>{};
  final copyright = raw['copyright'] is Map
      ? Map<String, dynamic>.from(raw['copyright'] as Map)
      : const <String, dynamic>{};

  final coverTpl = _firstTruthyString([
    albumInfo['cover'],
    transParam['union_cover'],
    cover,
  ]);

  final duration = _firstPositiveInt([
    audio['timelength_128'],
    audio['timelength'],
    audio['timelength_320'],
    audio['timelength_flac'],
    audio['timelength_high'],
    audio['duration_128'],
    audio['duration'],
    audio['duration_320'],
    audio['duration_flac'],
    audio['duration_high'],
  ]);

  const qualities = ['128k', '320k', 'flac', 'flac24bit'];
  final hashes = <String, String>{};
  final sizes = <String, int>{};
  final variants = <(String, Object?, Object?)>[
    (
      '128k',
      _truthy(audio['hash_128']) ? audio['hash_128'] : audio['hash'],
      _truthy(audio['filesize_128'])
          ? audio['filesize_128']
          : audio['filesize'],
    ),
    ('320k', audio['hash_320'], audio['filesize_320']),
    ('flac', audio['hash_flac'], audio['filesize_flac']),
    ('flac24bit', audio['hash_high'], audio['filesize_high']),
  ];
  for (final (quality, hash, size) in variants) {
    if (!_truthy(hash) || !_truthy(size)) continue;
    hashes[quality] = '$hash';
    sizes[quality] = size is num ? size.toInt() : int.tryParse('$size') ?? 0;
  }

  final rawAuthors = raw['authors'];
  final authorList = <Map<String, dynamic>>[
    if (rawAuthors is List)
      for (final a in rawAuthors.whereType<Map>())
        if (_truthy(a['author_name'])) Map<String, dynamic>.from(a),
  ];
  final artists = authorList.isNotEmpty
      ? [
          for (final a in authorList)
            {
              'id': _truthy(a['author_id']) ? '${a['author_id']}' : null,
              'name': kgDecodeName('${a['author_name']}'),
            },
        ]
      : '${base['author_name'] ?? ''}'
            .split('、')
            .where((s) => s.isNotEmpty)
            .map((s) => {'id': null, 'name': kgDecodeName(s.trim())})
            .toList();

  final hash = _firstTruthyString([audio['hash_128'], audio['hash']]);
  final audioId = base['audio_id'];
  final rawAlbumId = base['album_id'];
  final parsedAlbumId = rawAlbumId is num
      ? rawAlbumId.toInt()
      : int.tryParse('${rawAlbumId ?? ''}') ?? 0;
  final orderedHashes = [
    for (final q in qualities)
      if (hashes.containsKey(q)) q,
  ];

  return {
    'id': _truthy(audioId) ? '$audioId' : hash,
    'audioId': audioId is num
        ? audioId.toInt()
        : int.tryParse('${audioId ?? ''}') ?? 0,
    'albumAudioId': base['album_audio_id'],
    'hash': hash,
    'name': kgDecodeName('${base['audio_name'] ?? ''}'),
    'artist': artists.map((a) => a['name']).join(' / '),
    'artistId': artists.isEmpty ? null : artists.first['id'],
    'artists': artists,
    'album': kgDecodeName(
      _firstTruthyString([albumInfo['album_name'], albumName]),
    ),
    'albumId': parsedAlbumId > 0 ? '$parsedAlbumId' : (albumId ?? ''),
    'cover': kgFillCover(coverTpl, 300),
    'coverOriginal': kgFillCover(coverTpl, 480),
    'interval': duration / 1000,
    'duration': duration,
    'qualities': orderedHashes,
    'hashes': hashes,
    'sizes': sizes,
    'pay': {
      'payplay': deprecated['pay_type'] ?? 0,
      'privilege': copyright['privilege'],
      'pkg_price': deprecated['pkg_price'],
      'price': deprecated['price'],
      'feetype': deprecated['feetype'],
    },
  };
}
