// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG API 通用常量——对齐 apis/kugou/core/config.ts。
library;

/// KG 网关地址
const kgGatewayUrl = 'https://gateway.kugou.com';

/// 概念版（lite）客户端标识。
///
/// 本应用统一走 lite：登录、签名、取流与 KuGouMusicApi 的默认平台一致，
/// 不再保留标准版（appid 1005 / clientver 20489）分支，避免两套签名盐值
/// 与设备身份并存。
const kgLiteAppid = 3116;
const kgLiteClientver = 11440;

/// 扫码登录的来源 appid（KuGouMusicApi config.json `srcappid`）
const kgSrcAppid = 2919;

/// 扫码登录页（拼 `?qrcode=<key>` 后生成二维码图）
const kgQrLoginPage = 'https://h5.kugou.com/apps/loginQRCode/html/index.html';

/// Android 客户端 UA（对齐 KuGouMusicApi request.js）
const kgAndroidUa = 'Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi';

/// 登录接口 UA（对齐 module/login_cellphone.js）
const kgLoginUa = 'Android16-1070-11440-130-0-LOGIN-wifi';

/// 主搜索（带封面）：mobilecdn 的 /api/v3/search/song
const kgMobilecdnUrl = 'http://mobilecdn.kugou.com/api/v3/search/song';

/// 歌单信息（数字 specialid → global_specialid）：mobilecdn
const kgSpecialInfoUrl = 'http://mobilecdn.kugou.com/api/v3/special/info';

/// 专辑歌曲兜底：mobilecdn（网关 `/v1/album_audio/lite` 偶发 20010）
const kgAlbumSongUrl = 'http://mobilecdn.kugou.com/api/v3/album/song';

/// 兜底搜索：老的 songsearch（无封面）
const kgSearchUrl = 'https://songsearch.kugou.com/song_search_v2';

/// 歌词搜索/下载接口（lyrics.kugou.com）
const kgLyricSearchUrl = 'http://lyrics.kugou.com/search';
const kgLyricDownloadUrl = 'http://lyrics.kugou.com/download';

/// 歌词接口需要的伪装 headers（KuGou2012 PC 客户端）
final Map<String, String> kgLyricHeaders = {
  'KG-RC': '1',
  'KG-THash': 'expand_search_manager.cpp:852736169:451',
  'User-Agent': 'KuGou2012-9020-ExpandSearchManager',
};

const Map<String, String> _entityMap = {
  '&nbsp;': ' ',
  '&amp;': '&',
  '&lt;': '<',
  '&gt;': '>',
  '&quot;': '"',
  '&apos;': "'",
  '&#039;': "'",
};

/// HTML 实体反转义
String kgDecodeName(String? str) {
  if (str == null || str.isEmpty) return '';
  var out = str;
  _entityMap.forEach((k, v) => out = out.replaceAll(k, v));
  return out;
}

/// 安全升级 http 链接为 https
String kgSecureUrl(String? url) => url == null || url.isEmpty
    ? ''
    : url.replaceFirst(RegExp(r'^http://'), 'https://');

/// 填充并安全化KG 图片模板（如替换 `{size}` 占位符）
String? kgFillCover(String? url, [int size = 300]) {
  if (url == null || url.isEmpty) return null;
  return kgSecureUrl(url.replaceAll('{size}', '$size'));
}

/// 歌手数组 `[{name:'A'},{name:'B'}]` → `A / B`
String kgFormatSingerName(List<dynamic>? singers, [String join = ' / ']) {
  if (singers == null || singers.isEmpty) return '';
  return singers
      .map((s) => s is Map ? s['name'] : null)
      .whereType<String>()
      .map(kgDecodeName)
      .join(join);
}

/// `MM:SS` / `HH:MM:SS` 格式时长字符串 → 秒
int kgIntervalToSeconds(Object? interval) {
  if (interval is num) return interval.floor();
  if (interval == null) return 0;
  final parts = '$interval'.split(':').map(int.tryParse).toList();
  var seconds = 0;
  var unit = 1;
  while (parts.isNotEmpty) {
    final v = parts.removeLast();
    if (v != null) seconds += v * unit;
    unit *= 60;
  }
  return seconds;
}
