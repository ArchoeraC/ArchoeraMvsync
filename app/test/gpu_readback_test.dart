// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// GPU 读回兜底（flutter#196265 / #195402）：
/// 部分独显上 `Image.toByteData` 全 0，必须与「真正的纯黑封面」区分开。
library;

import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';

import 'package:archoera_mvsync/services/render/gpu_readback.dart';

void main() {
  test('全 0（含 alpha）判定为读回失败', () {
    expect(isBlankReadback(Uint8List(64)), isTrue);
    expect(isBlankReadback(Uint8List.fromList(List.filled(16, 0))), isTrue);
  });

  test('纯黑封面（alpha=255）不算失败', () {
    expect(isBlankReadback(Uint8List.fromList([0, 0, 0, 255])), isFalse);
    expect(
      isBlankReadback(Uint8List.fromList([0, 0, 0, 255, 0, 0, 0, 255])),
      isFalse,
    );
  });

  test('任一非 0 字节即视为有效', () {
    expect(
      isBlankReadback(Uint8List.fromList([0, 0, 0, 0, 1, 0, 0, 0])),
      isFalse,
    );
  });

  test('空数组不判失败（交给调用方的 data == null 分支）', () {
    expect(isBlankReadback(Uint8List(0)), isFalse);
  });
}
