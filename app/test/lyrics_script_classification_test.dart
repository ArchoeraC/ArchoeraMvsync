// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/lyrics/lyric_line.dart';
import 'package:archoera_mvsync/services/lyrics/lyric_script.dart';
import 'package:archoera_mvsync/services/lyrics/ttml_parser.dart';

void main() {
  group('lyricScriptOf', () {
    test('假名 / 谚文 / 汉字 / 拉丁', () {
      expect(lyricScriptOf('僕は強く'), LyricScript.kana);
      expect(lyricScriptOf('東京'), LyricScript.han);
      expect(lyricScriptOf('我要变强'), LyricScript.han);
      expect(lyricScriptOf('안녕하세요'), LyricScript.hangul);
      expect(lyricScriptOf('hello world'), LyricScript.latin);
      expect(lyricScriptOf('123 !?'), LyricScript.empty);
    });
  });

  group('parseLyricGroups 同时间戳归位', () {
    test('原文在前：第二行当译文', () {
      final groups = parseLyricGroups(
        content:
            '[00:01.000]僕は強くならなきゃいけない\n'
            '[00:01.000]必须要让自己变得强大起来\n',
        format: 'lrc',
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強くならなきゃいけない');
      expect(groups.single.translation, '必须要让自己变得强大起来');
    });

    test('译文在前：含假名的行仍当原文，中文归译文', () {
      final groups = parseLyricGroups(
        content:
            '[00:01.000]必须要让自己变得强大起来\n'
            '[00:01.000]僕は強くならなきゃいけない\n',
        format: 'lrc',
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強くならなきゃいけない');
      expect(groups.single.translation, '必须要让自己变得强大起来');
    });

    test('原文 + 译文 + 罗马音：三行全部归位，不再多出一条主歌词', () {
      final groups = parseLyricGroups(
        content:
            '[00:01.000]僕は強くならなきゃいけない\n'
            '[00:01.000]I must become stronger\n'
            '[00:01.000]boku wa tsuyoku naranakya ikenai\n',
        format: 'lrc',
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強くならなきゃいけない');
      expect(groups.single.translation, 'I must become stronger');
      expect(groups.single.romaji, 'boku wa tsuyoku naranakya ikenai');
    });

    test('中文歌 + 英文译文：中文原文不被英文顶掉', () {
      final groups = parseLyricGroups(
        content: '[00:01.000]你好世界\n[00:01.000]hello world\n',
        format: 'lrc',
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '你好世界');
      expect(groups.single.translation, 'hello world');
    });

    test('单独给出 translation 时，正文里重复的译文行不再当主歌词', () {
      final groups = parseLyricGroups(
        content:
            '[00:01.000]僕は強くならなきゃいけない\n'
            '[00:01.000]必须要让自己变得强大起来\n'
            '[00:05.000]空を見上げた\n',
        format: 'lrc',
        translation: '[00:01.000]必须要让自己变得强大起来\n',
      );
      expect(groups, hasLength(2));
      expect(groups[0].original.text, '僕は強くならなきゃいけない');
      expect(groups[0].translation, '必须要让自己变得强大起来');
    });

    test('单独给出 translation 且正文含罗马音：拉丁行归音译而不是顶替译文', () {
      final groups = parseLyricGroups(
        content:
            '[00:01.000]僕は強くならなきゃいけない\n'
            '[00:01.000]boku wa tsuyoku naranakya ikenai\n',
        format: 'lrc',
        translation: '[00:01.000]必须要让自己变得强大起来\n',
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強くならなきゃいけない');
      expect(groups.single.translation, '必须要让自己变得强大起来');
      expect(groups.single.romaji, 'boku wa tsuyoku naranakya ikenai');
    });

    test('同一时刻的两句日文原文（对唱）都保留为主行', () {
      final groups = parseLyricGroups(
        content: '[00:01.000]僕は強く\n[00:01.000]君を守る\n',
        format: 'lrc',
      );
      expect(groups, hasLength(2));
      expect(groups[0].original.text, '僕は強く');
      expect(groups[0].translation, isNull);
      expect(groups[1].original.text, '君を守る');
      expect(groups[1].translation, isNull);
    });

    test('背景人声行不参与归位', () {
      final groups = parseLyricGroups(
        content:
            '[00:01.000]僕は強く（和声）\n'
            '[00:01.000]必须要让自己变得强大起来\n',
        format: 'lrc',
      );
      expect(groups, hasLength(2));
      expect(groups[0].original.text, '僕は強く');
      expect(groups[0].translation, '必须要让自己变得强大起来');
      expect(groups[1].isBG, isTrue);
      expect(groups[1].original.text, '和声');
    });
  });

  group('parseTtmlLyrics 同一时间戳多段落归位', () {
    String wrap(String body) =>
        '<tt xmlns="http://www.w3.org/ns/ttml"><body><div>$body</div></body></tt>';

    test('段落级 x-translation（排在正文前面）挂到正文行', () {
      final groups = parseTtmlLyrics(
        wrap(
          '<p begin="1s" end="3s" ttm:role="x-translation">我要变强</p>'
          '<p begin="1s" end="3s">僕は強く</p>',
        ),
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強く');
      expect(groups.single.translation, '我要变强');
    });

    test('两个无 role 的 <p>（中文在前）：含假名的仍当原文', () {
      final groups = parseTtmlLyrics(
        wrap(
          '<p begin="1s" end="3s">我要变强</p>'
          '<p begin="1s" end="3s">僕は強く</p>',
        ),
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強く');
      expect(groups.single.translation, '我要变强');
    });

    test('同一时刻两句日文 <p> 都保留为主行', () {
      final groups = parseTtmlLyrics(
        wrap(
          '<p begin="1s" end="3s">僕は強く</p>'
          '<p begin="1s" end="3s">君を守る</p>',
        ),
      );
      expect(groups, hasLength(2));
      expect(groups[0].original.text, '僕は強く');
      expect(groups[1].original.text, '君を守る');
    });

    test('只含 x-translation 子 span 的 <p> 不产生空主行', () {
      final groups = parseTtmlLyrics(
        wrap(
          '<p begin="1s" end="3s">'
          '<span ttm:role="x-translation">我要变强</span></p>'
          '<p begin="1s" end="3s">僕は強く</p>',
        ),
      );
      expect(groups, hasLength(1));
      expect(groups.single.original.text, '僕は強く');
      expect(groups.single.translation, '我要变强');
    });

    test('段落级 x-bg 作为背景行且不挂译文', () {
      final groups = parseTtmlLyrics(
        wrap(
          '<p begin="1s" end="3s">僕は強く</p>'
          '<p begin="1s" end="3s" ttm:role="x-bg">（和声）</p>',
        ),
      );
      expect(groups, hasLength(2));
      expect(groups[0].isBG, isFalse);
      expect(groups[1].isBG, isTrue);
      expect(groups[1].translation, isNull);
    });
  });
}
