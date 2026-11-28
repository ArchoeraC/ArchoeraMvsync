// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 响应解析（KRC 歌词 + 歌单/榜单/搜索通用条目 → [Track]）。
///
/// 从 `kugou_api.dart` 拆出：纯解析函数，与网络请求/会话解耦，便于
/// 单测与复用。
library;

import 'dart:convert';
import 'dart:io';

import '../netease/track.dart';
import 'kugou_types.dart';

/// KRC 歌词密钥（lx-music kg.js）
const _krcKey = <int>[
  0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, //
  0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69,
];

/// 毫秒 → `MM:SS.xxx`（对齐 krc.ts msToTimeTag）。
String kgMsToTimeTag(int ms) {
  final m = (ms ~/ 60000).toString().padLeft(2, '0');
  final s = ((ms % 60000) ~/ 1000).toString().padLeft(2, '0');
  final x = (ms % 1000).toString().padLeft(3, '0');
  return '$m:$s.$x';
}

/// 解析解密后的 KRC 文本 → 四种歌词（对齐 krc.ts parseKrc）。
KugouLyric kgParseKrc(String raw) {
  var text = raw.replaceAll('\r', '');
  // KRC 头部元数据行（[id:$]/[ar:]/[ti:]/[al:]/[by:]/[hash:]/[sign:]/
  // [qq:]/[total:]/[offset:]）整体移除——此前仅移除 [id:$] 行，其余
  // 残留进 lrc 文本污染行级解析。注意 [id:$xxxx] 值后直接 ']'（无冒号），
  // 故冒号部分可选。
  text = text.replaceAll(
    RegExp(
      r'^\[(?:id:\$\w+|ar|ti|al|by|hash|sign|qq|total|offset)(?::[^\]]*)?\](?:\r?\n)?',
      multiLine: true,
    ),
    '',
  );

  // 翻译 & 罗马音以 [language:base64(json)] 整体嵌入
  List<String>? transLines;
  List<String>? romaLines;
  final langMatch = RegExp(r'\[language:([\w=\\/+]+)\]').firstMatch(text);
  if (langMatch != null) {
    text = text.replaceAll(RegExp(r'\[language:[\w=\\/+]+\]\n'), '');
    try {
      final json = jsonDecode(utf8.decode(base64.decode(langMatch.group(1)!)));
      final content = json is Map ? json['content'] : null;
      if (content is List) {
        for (final item in content) {
          if (item is! Map) continue;
          final type = item['type'];
          final lc = item['lyricContent'];
          if (lc is! List) continue;
          final lines = lc
              .map((arr) => arr is List ? arr.join('') : arr.toString())
              .toList();
          if (type == 0) {
            romaLines = lines;
          } else if (type == 1) {
            transLines = lines;
          }
        }
      }
    } catch (_) {
      // 译文解析失败不影响主歌词
    }
  }

  // 逐行替换行首时间标签，并同步给翻译/罗马音补时间头
  var idx = 0;
  final krcBody = text.replaceAllMapped(RegExp(r'\[((\d+),\d+)\].*'), (m) {
    final startMs = int.parse(m.group(2)!);
    final tag = kgMsToTimeTag(startMs);
    if (romaLines != null && idx < romaLines.length) {
      romaLines[idx] = '[$tag]${romaLines[idx]}';
    }
    if (transLines != null && idx < transLines.length) {
      transLines[idx] = '[$tag]${transLines[idx]}';
    }
    idx++;
    return m.group(0)!.replaceFirst(m.group(1)!, tag);
  });

  // 字级时间标签 <offset,dur,0> → <offset,dur>
  final krc = kgDecodeName(
    krcBody.replaceAllMapped(
      RegExp(r'<(\d+,\d+),\d+>'),
      (m) => '<${m.group(1)}>',
    ),
  );
  final lrc = krc.replaceAll(RegExp(r'<\d+,\d+>'), '');

  return KugouLyric(
    lrc: lrc,
    krc: krc,
    trans: kgDecodeName(transLines?.join('\n') ?? ''),
    roma: kgDecodeName(romaLines?.join('\n') ?? ''),
  );
}

/// 解密 KRC base64 内容（base64 → 去头 4 字节 → XOR → zlib inflate → utf8）。
KugouLyric decodeKrc(String base64Content) {
  if (base64Content.isEmpty) throw const FormatException('empty krc content');
  final buf = base64.decode(base64Content).sublist(4);
  for (var i = 0; i < buf.length; i++) {
    buf[i] ^= _krcKey[i % 16];
  }
  final inflated = ZLibCodec().decode(buf);
  return kgParseKrc(utf8.decode(inflated, allowMalformed: true));
}

/// 秒/毫秒双兼容时长：KG 各接口字段单位不统一（time_length/timelen/
/// timelength 有 s 也有 ms），值 < 1000 按秒处理。返回毫秒。
int kgDurationMs(Object? raw) {
  if (raw == null) return 0;
  final v = raw is num ? raw.toInt() : int.tryParse(raw.toString()) ?? 0;
  return v < 1000 ? v * 1000 : v;
}

/// 歌单条目 `name`（形如 "歌手 - 歌名.mp3"）需剥离的音频扩展名。
const _kgAudioExts = <String>{
  'mp3',
  'flac',
  'm4a',
  'aac',
  'ogg',
  'ape',
  'wav',
  'wma',
  'ac3',
  'aiff',
  'alac',
  'opus',
};

/// 歌单条目 id：hash（音频文件级唯一键）优先，audio_id 仅作兜底，
/// 保证每首歌 id 唯一（历史去重/红心匹配依赖；与 Track.fromKugouSong 一致）。
String kgTrackId(Map<String, dynamic> item, String hash) {
  if (hash.isNotEmpty) return hash;
  final audioId = item['audio_id'];
  if (audioId is num && audioId > 0) return audioId.toString();
  return '';
}

/// 搜索结果条目（歌单 / 专辑 / 歌手）的详情 id。
///
/// 歌单**优先 `gid`（即 `global_collection_id`）**：`/v1/search/special`
/// 同时返回数字 `specialid` 与 `gid`，把数字 id 当 `global_collection_id`
/// 请求歌单歌曲会得到 `error_code=20010 invalid param`（数字 id 需先经
/// mobilecdn `special/info` 换成 `global_specialid`）。其余字段别名对齐
/// KuGouMusicApi 与 SPlayer Desktop-only `apis/kugou/modules/search.ts`。
///
/// [type] 为 `special` / `album` / `author` 时按该类型优先取字段，避免
/// 不同分类里的同名字段（如零散 `gid`）盖过真正的专辑/歌手 id。
String kgSearchItemId(Map<String, dynamic> item, {String? type}) {
  final keys = switch (type) {
    'special' => const [
      'gid',
      'Gid',
      'global_collection_id',
      'GlobalCollectionID',
      'global_specialid',
      'specialid',
      'SpecialID',
    ],
    'album' => const ['albumid', 'AlbumID', 'album_id', 'AlbumId'],
    'author' => const ['AuthorId', 'AuthorID', 'authorid', 'author_id'],
    _ => const [
      'gid',
      'Gid',
      'global_collection_id',
      'GlobalCollectionID',
      'global_specialid',
      'specialid',
      'SpecialID',
      'albumid',
      'AlbumID',
      'AuthorId',
      'AuthorID',
      'authorid',
    ],
  };
  for (final k in keys) {
    final v = item[k]?.toString().trim() ?? '';
    if (v.isNotEmpty) return v;
  }
  return '';
}

/// `/v7/get_all_list` 单条 → [KugouLibraryItem]（纯解析，便于单测）。
///
/// 字段语义对齐 kugou_sdk `UserPlaylist`：
/// - `global_collection_id` 是**本人副本**的公开 id（读歌曲/详情优先）；
/// - `list_create_gid` 是收藏来源（原作者歌单）的公开 id（兜底）；
/// - `listid` 是用户本地歌单 id（个人增删接口用）；
/// - `is_def`：1=默认收藏，2=我喜欢（红心写入目标）；
/// - `authors` 存在表示「收藏的专辑」。
///
/// 返回 `null` 表示条目缺名称或缺少可用 id（调用方跳过）。
KugouLibraryItem? kgLibraryItemFromKg(
  Map<String, dynamic> p, {
  required String myUserid,
}) {
  final name = p['name']?.toString() ?? '';
  if (name.isEmpty) return null;
  final isDef = int.tryParse('${p['is_def']}');
  final isLiked = isDef == 2 || name == '我喜欢';
  final isMine = p['list_create_userid']?.toString() == myUserid || isLiked;
  final isAlbum = p['authors'] != null;
  final gid = (p['global_collection_id'] ?? p['globalCollectionId'] ?? p['gid'])
      ?.toString()
      .trim();
  final createGid = (p['list_create_gid'] ?? p['create_gid'])
      ?.toString()
      .trim();
  final listid = p['listid']?.toString();
  final id =
      (gid != null && gid.isNotEmpty ? gid : null) ??
      (createGid != null && createGid.isNotEmpty ? createGid : null) ??
      (isLiked ? listid ?? '' : '');
  if (id.isEmpty) return null;
  return KugouLibraryItem(
    type: isMine
        ? KugouLibraryType.createdPlaylist
        : isAlbum
        ? KugouLibraryType.collectedAlbum
        : KugouLibraryType.collectedPlaylist,
    id: id,
    title: name,
    cover: kgFillCover(p['pic']?.toString(), 300),
    trackCount: int.tryParse('${p['count']}') ?? 0,
    listid: listid,
    isLiked: isLiked,
  );
}

/// `/v2/getSearchTip` 的 `data` 分组 → 提示文本（`(label, text)`）。
///
/// 实测新结构为 List：`[{LableName, RecordCount, RecordDatas:[{HintInfo,…}]}]`
/// （label 为空 = 关键词，另有 `MV` / `专辑` 分组），条目只有 `HintInfo`
/// **没有 songid/albumid**；旧结构才是 Map（`music_tip` / `album_tip`）。
/// 去重并保持服务端顺序。
List<({String label, String text})> kgSuggestHints(dynamic data) {
  final out = <({String label, String text})>[];
  if (data is! List) return out;
  final seen = <String>{};
  for (final g in data.whereType<Map>()) {
    final label = g['LableName']?.toString().trim() ?? '';
    final rows = g['RecordDatas'];
    if (rows is! List) continue;
    for (final e in rows.whereType<Map>()) {
      final text = (e['HintInfo'] ?? e['name'] ?? '').toString().trim();
      if (text.isEmpty || !seen.add(text)) continue;
      out.add((label: label, text: text));
    }
  }
  return out;
}

/// `/v2/getSearchTip` 里可作为「歌曲关键词」的提示文本。
///
/// 只有**空标签**分组是歌曲/关键词建议；`MV` 分组是 MV 标题、`专辑` 分组
/// 是专辑名（实测 2026-11），把它们当歌名搜索会播到无关曲目，故跳过。
List<String> kgSuggestSongKeywords(dynamic data) => [
  for (final h in kgSuggestHints(data))
    if (h.label.isEmpty) h.text,
];

/// 从「歌单/榜单/推荐/搜索」通用条目结构构建 [Track]（source == 'kugou'）。
///
/// 兼容字段别名（对齐接口实际字段）：
/// - 名称：`songname` / `ori_audio_name` / `audio_name` / `name`（"歌手 - 歌名"合并名拆分）
/// - 歌手：`singerinfo` / `author_name` / `singername`，或合并名前半
/// - 封面：`trans_param.union_cover` / `sizable_cover` / `cover`（含 {size} 占位）
/// - 品质：`hash` + `320hash`/`sqhash`/`hires_hash`，或 `hash_320`/`hash_flac`/`hash_flac_24bit`，
///   以及公开歌单接口的 `relate_goods` 档位列表（level 0 不可用跳过）
///
/// [preferMergedName]：公开歌单接口（get_other_list_file_nofilt）的 `name`
/// 恒为 "歌手 - 歌名" 合并名且无独立歌手字段，传 true 时强制以合并名拆分
/// 覆盖歌名/歌手（接口把两者合并在 `name` 里）。
Track kgTrackFromKgPlain(
  Map<String, dynamic> item, {
  bool preferMergedName = false,
}) {
  final hash = item['hash']?.toString() ?? '';
  var name =
      item['songname']?.toString() ??
      item['ori_audio_name']?.toString() ??
      item['audio_name']?.toString() ??
      '';
  // 歌手：优先 singerinfo 列表（歌单/榜单条目格式，如「我喜欢」歌单）
  var author = '';
  final singerInfo = item['singerinfo'];
  if (singerInfo is List && singerInfo.isNotEmpty) {
    author = singerInfo
        .whereType<Map<String, dynamic>>()
        .map((s) => s['name']?.toString() ?? '')
        .where((s) => s.isNotEmpty)
        .join(' / ');
  }
  if (author.isEmpty) {
    author =
        item['author_name']?.toString() ?? item['singername']?.toString() ?? '';
  }
  final merged = item['name']?.toString() ?? '';
  if (merged.isNotEmpty && (preferMergedName || name.isEmpty)) {
    // 合并名形如 "歌手 - 歌名.mp3"：剥离音频扩展名后按 " - " 拆分
    var m = merged;
    final dot = m.lastIndexOf('.');
    if (dot > 0 && dot > m.lastIndexOf(' ')) {
      final ext = m.substring(dot + 1).toLowerCase();
      if (_kgAudioExts.contains(ext)) m = m.substring(0, dot);
    }
    final idx = m.indexOf(' - ');
    if (idx > 0) {
      if (preferMergedName || name.isEmpty) name = m.substring(idx + 3);
      if (author.isEmpty) author = m.substring(0, idx);
    } else if (preferMergedName || name.isEmpty) {
      name = m;
    }
  }
  String? coverTpl;
  final tp = item['trans_param'];
  if (tp is Map) coverTpl = tp['union_cover']?.toString();
  coverTpl ??= item['sizable_cover']?.toString() ?? item['cover']?.toString();

  final hashes = <String, String>{};
  final sizes = <String, int>{};
  if (hash.isNotEmpty) hashes['128k'] = hash;
  void add(String key, Object? h, Object? s) {
    final hs = h?.toString() ?? '';
    if (hs.isEmpty) return;
    hashes[key] = hs;
    final sz = s is num
        ? s.toInt()
        : (s != null ? int.tryParse(s.toString()) : null);
    if (sz != null && sz > 0) sizes[key] = sz;
  }

  add('320k', item['320hash'] ?? item['hash_320'], item['320filesize']);
  add('flac', item['sqhash'] ?? item['hash_flac'], item['sqfilesize']);
  add(
    'flac24bit',
    item['hires_hash'] ?? item['hash_flac_24bit'] ?? item['hash_hires'],
    item['hires_filesize'],
  );

  // 公开歌单接口（get_other_list_file_nofilt）的音质档位列表：relate_goods
  // 与主条目同结构（quality/hash/size/level），level 0 不可用跳过，
  // 与公开接口 relate_goods 的档位语义一致。
  final relateGoods = item['relate_goods'];
  if (relateGoods is List && relateGoods.isNotEmpty) {
    for (final g in relateGoods.whereType<Map<String, dynamic>>()) {
      if ((g['level'] as num?)?.toInt() == 0) continue;
      final h = g['hash']?.toString() ?? '';
      if (h.isEmpty) continue;
      final key = switch (g['quality']?.toString()) {
        '320' => '320k',
        'flac' => 'flac',
        'high' => 'flac24bit',
        _ => null,
      };
      if (key != null) add(key, h, g['size']);
    }
  }

  String? albumName;
  final ai = item['albuminfo'];
  if (ai is Map) albumName = ai['name']?.toString();
  albumName ??= item['album_name']?.toString();

  // 私有歌单条目附加信息（「我喜欢」移除需要 fileid；添加需要 album_id/mixsongid；
  // sort 为收藏序号（部分条目缺失/不稳定）——列表顺序由接口页序保证，
  // 此处仅保留元数据供移除/去重等场景使用）。
  final fileid = item['fileid'];
  final albumId = item['album_id'];
  final mixSongId = item['mixsongid'] ?? item['album_audio_id'];
  final sort = item['sort'];

  return Track(
    id: kgTrackId(item, hash),
    title: kgDecodeName(name),
    artists: author.isEmpty ? const [] : kugouArtists(author),
    album: albumName == null || albumName.isEmpty
        ? null
        : TrackAlbum(
            name: kgDecodeName(albumName),
            cover: kgFillCover(coverTpl, 300),
          ),
    duration: kgDurationMs(
      item['time_length'] ??
          item['timelen'] ??
          item['timelength'] ??
          item['duration'],
    ),
    cover: kgFillCover(coverTpl, 300),
    source: 'kugou',
    // 原唱标识（`IsOriginal == 1`）+ 版权
    // privilege == 10 → VIP（接口约定）
    isOriginal: _kgIsOriginal(item['IsOriginal'] ?? item['isOriginal']),
    fee: _kgFee(item['privilege']),
    kugou: hashes.isEmpty
        ? null
        : KugouTrackInfo(
            hash: hash,
            hashes: hashes,
            sizes: sizes,
            fileid: fileid is num
                ? fileid.toInt()
                : (fileid != null ? int.tryParse('$fileid') : null),
            albumId: albumId is num
                ? albumId.toInt()
                : (albumId != null ? int.tryParse('$albumId') : null),
            mixSongId: mixSongId is num
                ? mixSongId.toInt()
                : (mixSongId != null ? int.tryParse('$mixSongId') : null),
            sort: sort is num
                ? sort.toInt()
                : (sort != null ? int.tryParse('$sort') : null),
          ),
  );
}

/// KG `IsOriginal`（1=原唱）→ bool；兼容数字与字符串。
bool _kgIsOriginal(Object? raw) {
  if (raw == null) return false;
  if (raw is num) return raw.toInt() == 1;
  return raw.toString() == '1' || raw.toString().toLowerCase() == 'true';
}

/// KG 版权 `privilege` → TrackFee（10=VIP 需会员；其余视作免费）。
int _kgFee(Object? raw) {
  if (raw == null) return 0;
  final v = raw is num ? raw.toInt() : int.tryParse(raw.toString()) ?? 0;
  return v == 10 ? 1 : 0;
}
