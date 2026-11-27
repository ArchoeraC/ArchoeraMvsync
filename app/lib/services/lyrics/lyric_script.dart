// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 歌词行归属判定：同一时间戳下多行文本，谁是原文 / 译文 / 音译。
///
/// 背景：LRC（主行 + 同时间戳译文交错）与 TTML（同一时间戳多个 `<p>`）都
/// 存在「一行原文 + 一行或多行译文 / 音译」的结构。旧实现固定「先出现的行
/// = 原文」，遇到译文排在前面（部分下载站 / 流媒体把译文也写进正文）时，
/// 中文译文会被当成主歌词。
///
/// 这里按**文字脚本**判定（保守、确定性）：
/// - 含假名 / 谚文的行优先当原文（日文 / 韩文歌）；
/// - 纯汉字行在存在假名 / 谚文对照时判为译文；
/// - 中文歌 + 英文译文仍按位置保留中文原文（脚本无法区分中英互译方向，
///   以文档顺序为准）。
library;

/// 一行文本的主脚本分类。
enum LyricScript {
  /// 空行 / 只有标点与数字。
  empty,

  /// 含假名（日文）。
  kana,

  /// 含谚文（韩文）。
  hangul,

  /// 纯汉字（无假名 / 谚文）。
  han,

  /// 纯拉丁（可含标点数字；不含汉字 / 假名 / 谚文）。
  latin,

  /// 其它脚本（希腊 / 西里尔 / 泰文等）。
  other,
}

bool _isKana(int r) =>
    (r >= 0x3040 && r <= 0x309F) ||
    (r >= 0x30A0 && r <= 0x30FF) ||
    (r >= 0x31F0 && r <= 0x31FF) ||
    (r >= 0xFF66 && r <= 0xFF9D);

bool _isHangul(int r) =>
    (r >= 0xAC00 && r <= 0xD7AF) ||
    (r >= 0x1100 && r <= 0x11FF) ||
    (r >= 0x3130 && r <= 0x318F);

bool _isHan(int r) =>
    (r >= 0x3400 && r <= 0x4DBF) ||
    (r >= 0x4E00 && r <= 0x9FFF) ||
    (r >= 0xF900 && r <= 0xFAFF);

bool _isLatinLetter(int r) =>
    (r >= 0x41 && r <= 0x5A) ||
    (r >= 0x61 && r <= 0x7A) ||
    (r >= 0xC0 && r <= 0x24F);

/// 一行文本的主脚本（假名 / 谚文优先于汉字，汉字优先于拉丁）。
LyricScript lyricScriptOf(String text) {
  var hasHan = false;
  var hasLatin = false;
  var hasOther = false;
  for (final r in text.runes) {
    if (_isKana(r)) return LyricScript.kana;
    if (_isHangul(r)) return LyricScript.hangul;
    if (_isHan(r)) {
      hasHan = true;
      continue;
    }
    if (_isLatinLetter(r)) {
      hasLatin = true;
      continue;
    }
    // 数字 / 标点 / 空白不算脚本；其它文字（希腊、西里尔、泰文…）记一笔。
    if (r > 0x7F && !_isLatinLetter(r) && (r < 0xC0 || r > 0x24F)) {
      hasOther = true;
    }
  }
  if (hasHan) return LyricScript.han;
  if (hasLatin) return LyricScript.latin;
  if (hasOther) return LyricScript.other;
  return LyricScript.empty;
}

/// 是否为日文 / 韩文原文（含假名或谚文）。
bool lyricHasKanaOrHangul(String text) {
  final script = lyricScriptOf(text);
  return script == LyricScript.kana || script == LyricScript.hangul;
}

/// 是否为纯拉丁行（含拉丁字母、且不含汉字 / 假名 / 谚文）。
bool lyricIsLatinOnly(String text) => lyricScriptOf(text) == LyricScript.latin;

/// 比较用的归一化文本（去首尾空白、折叠连续空白）。
String normalizeLyricText(String text) =>
    text.trim().replaceAll(RegExp(r'\s+'), ' ');

/// 同一时间戳下多行文本的归属结果。
///
/// [originalIndex] 指向 [texts] 中的原文行；其余行按脚本归入译文 / 音译。
/// [separateIndices] 是「同时间戳但应独立成主行」的下标（例如同一时刻的
/// 两句日文原文），调用方应按独立主行输出，不能当成译文。
class SameTimeLyricGroup {
  const SameTimeLyricGroup({
    required this.originalIndex,
    this.separateIndices = const [],
    this.translation,
    this.romaji,
  });

  final int originalIndex;
  final List<int> separateIndices;
  final String? translation;
  final String? romaji;
}

/// 把同一时间戳的多行文本分成「原文 / 译文 / 音译」。
///
/// [providedTranslation] / [providedRomaji] 是音源单独给出的译文 / 音译
/// （如流媒体服务器的 `translation` 字段）；它们优先于正文里的同时间戳行，
/// 正文中与它们重复的行会被丢弃（不再当成主歌词）。
SameTimeLyricGroup classifySameTimeLyricLines(
  List<String> texts, {
  String? providedTranslation,
  String? providedRomaji,
}) {
  if (texts.isEmpty) {
    return SameTimeLyricGroup(
      originalIndex: 0,
      translation: providedTranslation,
      romaji: providedRomaji,
    );
  }

  // 原文：优先含假名 / 谚文的行（日文 / 韩文原文），否则按文档顺序取第一行。
  var originalIndex = 0;
  for (var i = 0; i < texts.length; i++) {
    if (lyricHasKanaOrHangul(texts[i])) {
      originalIndex = i;
      break;
    }
  }
  final original = texts[originalIndex];
  final originalIsCjk = lyricHasKanaOrHangul(original);

  var translation = _nonEmpty(providedTranslation);
  var romaji = _nonEmpty(providedRomaji);
  final transKey = translation == null ? null : normalizeLyricText(translation);
  final romaKey = romaji == null ? null : normalizeLyricText(romaji);

  final extras = <int>[];
  for (var i = 0; i < texts.length; i++) {
    if (i == originalIndex) continue;
    final t = texts[i].trim();
    if (t.isEmpty) continue;
    final key = normalizeLyricText(t);
    // 正文里与单独给出的译文 / 音译重复的行 → 丢弃（避免重复展示、避免
    // 被当成主歌词）；相同文本只出现一次时也按同样的键去重。
    if (key == transKey || key == romaKey) continue;
    if (extras.any((e) => normalizeLyricText(texts[e]) == key)) continue;
    extras.add(i);
  }

  // 同一时刻的多个日文 / 韩文原文行（对唱、双声部）独立成主行，
  // 不能因为共用一个时间戳就被降级成译文。
  final separate = <int>[
    for (final i in extras)
      if (lyricHasKanaOrHangul(texts[i])) i,
  ];
  final candidates = [
    for (final i in extras)
      if (!separate.contains(i)) texts[i],
  ];

  // 先收非拉丁行（汉字 / 其它脚本）→ 译文；再收拉丁行 → 无译文时按位置当
  // 译文（旧行为），有译文且原文是日韩文时当罗马音（音译）。
  for (final t in candidates) {
    if (lyricIsLatinOnly(t)) continue;
    translation ??= t;
    break;
  }
  for (final t in candidates) {
    if (!lyricIsLatinOnly(t)) continue;
    if (translation == null) {
      translation = t;
    } else if (originalIsCjk) {
      romaji ??= t;
    }
  }

  return SameTimeLyricGroup(
    originalIndex: originalIndex,
    separateIndices: separate,
    translation: translation,
    romaji: romaji,
  );
}

String? _nonEmpty(String? s) {
  final t = s?.trim();
  return (t == null || t.isEmpty) ? null : t;
}
