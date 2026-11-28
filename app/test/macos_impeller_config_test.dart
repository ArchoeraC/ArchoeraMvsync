// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

// macOS GPU 渲染守卫：
// - Flutter 3.47+ 在 macOS 默认走 Impeller（Metal）；本项目显式写
//   FLTEnableImpeller=true，避免模板复制 / 未来默认值变化把 GPU 路径关掉。
// - 双显卡 Intel Mac 上「走 Metal」不等于「走独显」：引擎的 SelectMetalDevice()
//   硬编码「优先集显」（flutter#164569），必须由 Runner 的 GPUSelection.m 用
//   dyld interpose 改写；开关落在设置页「独立显卡加速」。
// 纯文本静态检查，Linux/Windows CI 也能跑。

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('macOS Info.plist 显式启用 Impeller（Metal）', () {
    final plist = File('macos/Runner/Info.plist').readAsStringSync();
    final enabled = RegExp(
      r'<key>\s*FLTEnableImpeller\s*</key>\s*<true\s*/>',
      multiLine: true,
    ).hasMatch(plist);
    expect(
      enabled,
      isTrue,
      reason: 'macOS Runner 必须显式 FLTEnableImpeller=true，禁止悄悄回退软件渲染',
    );
  });

  test('Info.plist 不把 app 钉死在独显（交给设置项 / 系统决定）', () {
    final plist = File('macos/Runner/Info.plist').readAsStringSync();
    final pinned = RegExp(
      r'<key>\s*NSSupportsAutomaticGraphicsSwitching\s*</key>\s*<false\s*/>',
      multiLine: true,
    ).hasMatch(plist);
    expect(
      pinned,
      isFalse,
      reason:
          'false 会让「独立显卡加速」关闭后系统仍保留独显驱动屏幕，'
          '省电形同虚设；设备选择交给 GPUSelection.m 的 interpose + 设置项',
    );
  });

  test('macOS Runner 使用 Metal API 探测 GPU 设备', () {
    final runner = File('macos/Runner/MainFlutterWindow.swift')
        .readAsStringSync();
    expect(runner, contains('import Metal'));
    expect(runner, contains('MTLCreateSystemDefaultDevice'));
    expect(runner, contains('MTLCopyAllDevices'));
    expect(runner, contains('recommendedMaxWorkingSetSize'));
    expect(runner, contains('isLowPower'), reason: '必须能区分「默认设备是集显」与「默认设备是独显」');
    expect(
      runner,
      contains('ArchoeraMacOSGPUModeDescription'),
      reason: '日志要带上「设置项是否开启」，否则无法判断 interpose 有没有生效',
    );
  });

  test('Runner 监听屏幕 / GPU 切换，运行期也能留下记录', () {
    final runner = File('macos/Runner/MainFlutterWindow.swift')
        .readAsStringSync();
    expect(
      runner,
      contains('didChangeScreenParametersNotification'),
      reason: '插拔外接屏会改变驱动屏幕的 GPU，需要重新记录',
    );
    expect(
      runner,
      contains('didChangeScreenNotification'),
      reason: '窗口被移到另一块屏幕时也应重新记录',
    );
  });

  test('引擎硬编码「优先集显」→ Runner 用 dyld interpose 改回高性能独显', () {
    final selection = File('macos/Runner/GPUSelection.m');
    expect(
      selection.existsSync(),
      isTrue,
      reason:
          'Flutter 的 SelectMetalDevice()（flutter#164569）固定挑 hasUnifiedMemory '
          '设备，双显卡 Intel Mac 上就是核显；必须有进程内替换才能拿到独显',
    );
    final source = selection.readAsStringSync();
    expect(source, contains('__DATA,__interpose'));
    expect(source, contains('MTLCopyAllDevices'));
    expect(source, contains('MTLCreateSystemDefaultDevice'));
    expect(
      source,
      contains('ARCHOERA_MACOS_GPU'),
      reason: '必须留一个现场 A/B 用的环境变量逃生口',
    );
    expect(
      source,
      contains('isRemovable'),
      reason: 'eGPU 不参与强制选择（避免复现 flutter#163218 拔插崩溃）',
    );
    expect(
      source,
      contains('GPU interpose active'),
      reason: '启动日志必须能证明 interpose 真的被 dyld 应用（否则等于没修）',
    );
    expect(source, contains('supportsFamily'));
    expect(
      source,
      contains('MTLGPUFamilyMac2'),
      reason: '只强选满足 Impeller 依赖 family 的设备，不假设「独显 ⊇ 集显」',
    );
    expect(
      source,
      contains('ArchoeraDescribeCapabilities'),
      reason: '首次选设备时要落 family / 缓冲区上限 / 读写纹理 tier 日志',
    );
    expect(source, contains('readWriteTextureSupport'));
    expect(source, contains('maxBufferLength'));
    expect(
      source,
      contains('ArchoeraSupportsAllFamilies'),
      reason: 'auto 档要求独显具备集显的全部 family，避免 Metal 3 → Metal 2 降级',
    );
    expect(source, contains('ArchoeraGPUModeAuto'));

    final pbxproj = File('macos/Runner.xcodeproj/project.pbxproj')
        .readAsStringSync();
    expect(
      pbxproj,
      contains('GPUSelection.m in Sources'),
      reason: '新增的 .m 必须加入 Runner target 的 Sources 阶段，否则不会编译',
    );
  });

  test('设置项与原生侧共用同一个偏好键', () {
    final prefs = File('lib/stores/prefs_render.dart').readAsStringSync();
    expect(
      prefs,
      contains("'render.macos_gpu'"),
      reason: 'Dart 侧的键必须与 GPUSelection.m 读取的 JSON 键完全一致',
    );
    expect(prefs, contains('defaultMacosGPU'));
    expect(
      prefs,
      contains('legacyMacosDiscreteGPUKey'),
      reason: '早期预览版的布尔键要能迁移到三档',
    );

    final native = File('macos/Runner/GPUSelection.m').readAsStringSync();
    expect(native, contains('render.macos_gpu'));
    expect(
      native,
      contains('render.macos_discrete_gpu'),
      reason: '旧布尔键要能被原生侧迁移读取',
    );
    expect(
      native,
      contains('prefs.json'),
      reason: '原生侧在 Flutter 初始化前就读偏好，只能直接读 prefs.json',
    );

    final bridging = File('macos/Runner/Runner-Bridging-Header.h')
        .readAsStringSync();
    expect(bridging, contains('ArchoeraMacOSGPUModeDescription'));
    expect(
      bridging,
      contains('const char *'),
      reason:
          '桥接头会被 Swift 预编译，不能依赖 Foundation'
          '（否则 `unknown type name NSString`，CI 已踩过）',
    );

    final runner = File('macos/Runner/MainFlutterWindow.swift')
        .readAsStringSync();
    expect(
      runner,
      contains('String(cString: ArchoeraMacOSGPUModeDescription())'),
    );
  });

  test('独显读回全 0（AMD 已知问题）有三处兜底', () {
    final helper = File('lib/services/render/gpu_readback.dart');
    expect(
      helper.existsSync(),
      isTrue,
      reason: '需要与设备无关的「全 0 读回」判定，不能把纯黑封面一起误杀',
    );
    expect(helper.readAsStringSync(), contains('isBlankReadback'));

    for (final path in [
      'lib/theme/cover_color.dart',
      'lib/services/render/image_luminance.dart',
      'lib/widgets/player/background/fluid_background.dart',
    ]) {
      expect(
        File(path).readAsStringSync(),
        contains('isBlankReadback'),
        reason: '$path 必须对独显全 0 读回做兜底（flutter#196265 / #195402）',
      );
    }
  });
}
