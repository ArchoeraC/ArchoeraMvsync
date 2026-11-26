// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// 自绘边缘缩放的纯几何（不依赖 Flutter / FFI，便于单测）。
///
/// 原生 `startResize` 在 Windows / Linux 可用；macOS 返回不支持
/// （`APL_ERR_UNSUPPORTED`，系统边缘负责）。此处的回退路径把指针位移折算成
/// 新的窗口矩形，再用 `SystemWindow.setSize/setPosition` 应用——三端同一套
/// 兜底语义，也便于在真机验证 macOS 无边框窗口。
library;

import 'system_window.dart';

/// 自绘缩放的最小窗口尺寸（逻辑像素）。原生路径经 `setMinSize` 同步到系统，
/// 回退路径用它做几何钳制，两者保持一致。
const int kWindowMinWidth = 640;
const int kWindowMinHeight = 420;

/// 窗口矩形（逻辑像素，左上角原点）。
class WindowRect {
  const WindowRect({
    required this.x,
    required this.y,
    required this.width,
    required this.height,
  });

  final int x;
  final int y;
  final int width;
  final int height;

  @override
  bool operator ==(Object other) =>
      other is WindowRect &&
      other.x == x &&
      other.y == y &&
      other.width == width &&
      other.height == height;

  @override
  int get hashCode => Object.hash(x, y, width, height);

  @override
  String toString() => 'WindowRect($x,$y $width×$height)';
}

/// 按拖动边缘与累计位移计算新矩形；尺寸不小于 [minWidth] × [minHeight]，
/// 触底时对侧坐标保持不动（不会把窗口「推走」）。
WindowRect resizeWindowRect({
  required WindowResizeEdge edge,
  required WindowRect start,
  required int dx,
  required int dy,
  required int minWidth,
  required int minHeight,
}) {
  final left =
      edge == WindowResizeEdge.topLeft ||
      edge == WindowResizeEdge.left ||
      edge == WindowResizeEdge.bottomLeft;
  final right =
      edge == WindowResizeEdge.topRight ||
      edge == WindowResizeEdge.right ||
      edge == WindowResizeEdge.bottomRight;
  final top =
      edge == WindowResizeEdge.topLeft ||
      edge == WindowResizeEdge.top ||
      edge == WindowResizeEdge.topRight;
  final bottom =
      edge == WindowResizeEdge.bottomLeft ||
      edge == WindowResizeEdge.bottom ||
      edge == WindowResizeEdge.bottomRight;

  var x = start.x;
  var y = start.y;
  var width = start.width;
  var height = start.height;

  if (left) {
    final proposed = start.width - dx;
    if (proposed < minWidth) {
      width = minWidth;
      x = start.x + start.width - minWidth;
    } else {
      width = proposed;
      x = start.x + dx;
    }
  } else if (right) {
    width = start.width + dx;
    if (width < minWidth) width = minWidth;
  }

  if (top) {
    final proposed = start.height - dy;
    if (proposed < minHeight) {
      height = minHeight;
      y = start.y + start.height - minHeight;
    } else {
      height = proposed;
      y = start.y + dy;
    }
  } else if (bottom) {
    height = start.height + dy;
    if (height < minHeight) height = minHeight;
  }

  return WindowRect(x: x, y: y, width: width, height: height);
}
