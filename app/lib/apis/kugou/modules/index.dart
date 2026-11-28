// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 模块注册表——对齐 apis/kugou/modules/index.ts。
library;

import '../core/types.dart';

import 'album.dart';
import 'artist.dart';
import 'comment.dart';
import 'login_qr.dart';
import 'lyric.dart';
import 'playlist.dart';
import 'search.dart';
import 'song_url.dart';
import 'user_detail.dart';

/// 模块注册表：key 与 TS index.ts 完全一致
final Map<String, KgModule> kgModules = {
  'search': kgSearch,
  'lyric': kgLyric,
  'album': kgAlbum,
  'artist': kgArtist,
  'playlist': kgPlaylist,
  // 兼容旧调用名（TS 侧 `song_list` 指向 playlist）
  'song_list': kgPlaylist,
  'user_detail': kgUserDetail,
  'login_qr_key': kgLoginQrKey,
  'login_qr_check': kgLoginQrCheck,
  'song_url': kgSongUrl,
  'comment': kgComment,
};
