// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// GPU 读回兜底：核显与独显不是同一套 Metal 能力，`Image.toByteData` 在部分
/// 独显上存在「全 0 读回」的上游问题（flutter#196265：AMD 独显 Radeon Pro 560X；
/// flutter#195402：Intel Mac + AMD 独显缺 `synchronizeResource`）。
///
/// 该问题只在「设置 → 性能 / 渲染 → 独立显卡加速」开启、且机器带 AMD 独显时
/// 可能出现；表现为封面取色 / 亮度采样 / 流体背景拿到全 0 像素。这里给出与设备
/// 无关的判定，调用方据此跳过基于读回的结果（而不是把封面当成纯黑）。
library;

import 'dart:typed_data';

/// 读回的 RGBA 是否**全 0**（含 alpha）。
///
/// 全 0 代表读回失败 / 未同步，而不是「全黑图片」——真正的纯黑封面 alpha 仍是
/// 255，只有彩色通道为 0，不会被误判。
bool isBlankReadback(Uint8List rgba) {
  if (rgba.isEmpty) return false;
  for (var i = 0; i < rgba.length; i++) {
    if (rgba[i] != 0) return false;
  }
  return true;
}
