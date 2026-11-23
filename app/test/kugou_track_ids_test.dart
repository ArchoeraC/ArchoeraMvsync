// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 曲目 album_id / mixSongId 解析回归（**不联网**）。
///
/// `/v5/url` 的 `album_audio_id` 是 **mixsongid**，不是 `audio_id`；传错会
/// 返回 `status=0 "Hash not found"`（实测）。搜索（mobilecdn / songsearch）
/// 与歌单条目的字段大小写不同，这里钉住两套字段的取值。
library;

import 'package:archoera_mvsync/services/kugou/kugou_parse.dart'
    show kgTrackFromKgPlain;
import 'package:archoera_mvsync/services/netease/track.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('mobilecdn 小写字段：album_id / album_audio_id', () {
    final t = Track.fromKugouSong({
      'hash': 'C41E80A18D1448FA47086372999C7F43',
      'songname': '海阔天空',
      'singername': 'BEYOND',
      'album_id': '973001',
      'album_audio_id': 32155307,
      'audio_id': 3261014,
    });
    expect(t.kugou?.albumId, 973001);
    expect(t.kugou?.mixSongId, 32155307);
    expect(t.kugou?.hash, 'C41E80A18D1448FA47086372999C7F43');
  });

  test('songsearch PascalCase：AlbumID / MixSongID', () {
    final t = Track.fromKugouSong({
      'FileHash': 'C41E80A18D1448FA47086372999C7F43',
      'SongName': '海阔天空',
      'SingerName': 'BEYOND',
      'AlbumID': '973001',
      'MixSongID': '32155307',
      'Audioid': 3261014,
    });
    expect(t.kugou?.albumId, 973001);
    expect(t.kugou?.mixSongId, 32155307);
  });

  test('歌单条目：mixsongid 缺失时用 album_audio_id 兜底', () {
    final t = kgTrackFromKgPlain({
      'hash': 'C41E80A18D1448FA47086372999C7F43',
      'name': 'BEYOND - 海阔天空',
      'album_id': '973001',
      'album_audio_id': 32155307,
    }, preferMergedName: true);
    expect(t.kugou?.albumId, 973001);
    expect(t.kugou?.mixSongId, 32155307);
    expect(t.title, '海阔天空');
    expect(t.artistNames, 'BEYOND');
  });

  test('不把 audio_id 当 mixSongId', () {
    final t = Track.fromKugouSong({
      'hash': 'H',
      'songname': 'x',
      'audio_id': 3261014,
    });
    expect(t.kugou?.mixSongId, isNull);
  });
}
