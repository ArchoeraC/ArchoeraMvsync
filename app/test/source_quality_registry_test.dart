// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// 音质选择注册表：各源可选档位裁剪（`SourcePlatform.availableQualities`）。

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/netease/track.dart';
import 'package:archoera_mvsync/services/source/source_platform.dart';

void main() {
  group('音质选择注册表', () {
    test('默认源（NT / 未知）为全档位', () {
      expect(sourcePlatform('netease').supportedQualities, audioQualityLevels);
      expect(
        sourcePlatform('netease')
            .availableQualities(const Track(id: '1', title: 'x')),
        audioQualityLevels,
      );
      expect(
        sourcePlatform('__unknown__')
            .availableQualities(const Track(id: '1', title: 'x')),
        audioQualityLevels,
      );
      expect(
        sourcePlatform('netease')
            .maxQualityProvider(const Track(id: '1', title: 'x')),
        isNull,
      );
    });

    test('KG：覆写裁剪（无品质 hash → 空档位）', () {
      const noHash = Track(
        id: '1',
        title: 'x',
        source: 'kugou',
        kugou: KugouTrackInfo(hash: 'h'),
      );
      expect(sourcePlatform('kugou').availableQualities(noHash), isEmpty);
      expect(
        sourcePlatform('kugou')
            .availableQualities(const Track(id: '1', title: 'x')),
        audioQualityLevels,
      );
    });

    test('KG：档位按精确 hash 映射（128k=SQ，320k=HQ，不回退冒充 HQ）', () {
      const t128 = Track(
        id: '1',
        title: 'x',
        source: 'kugou',
        kugou: KugouTrackInfo(hash: 'h', hashes: {'128k': 'h'}),
      );
      expect(t128.kugou!.hashFor('sq'), 'h');
      expect(t128.kugou!.hashFor('hq'), isNull);
      expect(t128.kugou!.hashFor('lossless'), isNull);
      expect(sourcePlatform('kugou').availableQualities(t128), ['sq']);

      const t320 = Track(
        id: '2',
        title: 'y',
        source: 'kugou',
        kugou: KugouTrackInfo(hash: 'h', hashes: {'128k': 'h', '320k': 'h320'}),
      );
      expect(t320.kugou!.hashFor('hq'), 'h320');
      expect(sourcePlatform('kugou').availableQualities(t320), ['sq', 'hq']);

      const tFlac = Track(
        id: '3',
        title: 'z',
        source: 'kugou',
        kugou: KugouTrackInfo(
          hash: 'h',
          hashes: {
            '128k': 'h',
            '320k': 'h320',
            'flac': 'hf',
            'flac24bit': 'hr',
          },
        ),
      );
      expect(sourcePlatform('kugou').availableQualities(tFlac), [
        'sq',
        'hq',
        'lossless',
        'hi-res',
      ]);
    });
  });
}
