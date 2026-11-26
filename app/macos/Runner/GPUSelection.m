// ArchoeraMvsync macOS Runner
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

//! 进程级显卡选择：把 Flutter 引擎硬编码的「优先集成显卡」改成可配置的三档。
//!
//! 背景：Flutter 的 macOS 嵌入层（`shell/platform/darwin/macos/framework/
//! Source/FlutterRenderer.mm` 里的 `SelectMetalDevice()`，随 flutter#164569 引入）
//! 固定返回「第一个 `hasUnifiedMemory` 的设备」——在双显卡 Intel Mac 上就是
//! **Intel 核显**；只有列表里找不到统一内存设备时才回退
//! `MTLCreateSystemDefaultDevice()`。`Info.plist` 的
//! `NSSupportsAutomaticGraphicsSwitching` 只影响 WindowServer 用哪块 GPU 驱动屏幕，
//! 管不到引擎内部的设备选择，于是出现「明明要求高性能 / 独显，实际仍跑在集显上」。
//! 上游动机是省电（flutter#47822）与绕开 eGPU 拔插崩溃（flutter#163218）。
//!
//! 做法：用 dyld 的 `__DATA,__interpose` 段在进程内替换两个 C 符号——
//! - `MTLCopyAllDevices()`：只保留高性能设备（非 lowPower、非统一内存、非可移除、
//!   支持 `MTLGPUFamilyMac2`），让引擎的 `for` 循环找不到集显；
//! - `MTLCreateSystemDefaultDevice()`：系统默认设备不是高性能设备时，直接返回
//!   高性能设备。
//!
//! 三档偏好（设置页「性能 / 渲染 → 独立显卡加速」，存 `prefs.json` 的
//! `render.macos_gpu`，**启动时读取，改完需重启**）：
//! - `auto`（默认）：独显的 Metal 能力**不低于**集显时才用独显。核显 / 独显不是
//!   同一套能力，例如 2018 款 MacBook Pro 15"：Intel UHD 630 支持 Metal 3，
//!   Radeon Pro 555X 只有 Metal 2 —— 这种机器上「自动」保持集显，避免降级；
//! - `discrete`：强制独显（即使只有 Metal 2，且 AMD 独显存在 `Image.toByteData`
//!   全 0 的上游 bug，见下），用于现场 A/B 与明确想要独显算力的场景；
//! - `integrated`：始终集显（省电）。
//!
//! 环境变量 `ARCHOERA_MACOS_GPU=auto|discrete|integrated` 优先级最高（仅终端 /
//! `flutter run` 启动时继承），也兼容旧布尔键 `render.macos_discrete_gpu`。
//!
//! 保持 Flutter 原行为（直接透传）的情况：机器上没有「非低功耗且非统一内存且
//! 非可移除」的设备（Apple Silicon、单核显 Intel、只有 eGPU 的机器）。
//!
//! ⚠ 核显与独显**不是同一套 Metal 能力**：GPU family（Mac2 / Metal3）、
//! `maxBufferLength` / `maxThreadgroupMemoryLength`、读写纹理 tier、argument
//! buffer tier、是否统一内存都可能不同。因此这里：
//! - `auto` 档要求独显支持集显具备的全部 family，不假设「独显 ⊇ 集显」；
//! - 首次选设备时把每块 GPU 的 family / 上限打进日志，并在「独显缺少集显具备的
//!   Metal3」时给出 WARNING；
//! - 引擎侧 shader / 管线按设备编译，切换设备后必须重新验证特效；`Image.toByteData`
//!   在部分 AMD 独显上存在全 0 读回的上游 bug（flutter#196265 / #195402），Dart 侧
//!   对读回结果另有兜底（封面取色 / 亮度采样 / 流体背景）。

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <dlfcn.h>

/// 供 Swift Runner 读取当前档位（`auto` / `discrete` / `integrated`）。
NSString *ArchoeraMacOSGPUModeDescription(void);

typedef NSArray<id<MTLDevice>> *_Nonnull (*ArchoeraCopyAllDevicesFn)(void);
typedef id<MTLDevice> _Nullable (*ArchoeraCreateDefaultDeviceFn)(void);

/// dyld 的 interpose 段（与 Apple dyld 源码里的 `DYLD_INTERPOSE` 等价）。
#define ARCHOERA_INTERPOSE(replacement, replacee)                                    \
  __attribute__((used)) static struct {                                              \
    const void *replacement;                                                         \
    const void *replacee;                                                            \
  } _archoera_interpose_##replacee __attribute__((section("__DATA,__interpose"))) = { \
      (const void *)(unsigned long)&replacement,                                     \
      (const void *)(unsigned long)&replacee,                                        \
  };

typedef NS_ENUM(NSInteger, ArchoeraGPUMode) {
  ArchoeraGPUModeAuto = 0,
  ArchoeraGPUModeDiscrete,
  ArchoeraGPUModeIntegrated,
};

static ArchoeraCopyAllDevicesFn ArchoeraOriginalCopyAllDevices(void) {
  static ArchoeraCopyAllDevicesFn function;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    function = (ArchoeraCopyAllDevicesFn)dlsym(RTLD_NEXT, "MTLCopyAllDevices");
  });
  return function;
}

static ArchoeraCreateDefaultDeviceFn ArchoeraOriginalCreateDefaultDevice(void) {
  static ArchoeraCreateDefaultDeviceFn function;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    function = (ArchoeraCreateDefaultDeviceFn)dlsym(RTLD_NEXT, "MTLCreateSystemDefaultDevice");
  });
  return function;
}

/// 确认 interpose 真的生效：只要两个替换函数被调用过，就说明 dyld 应用了
/// `__DATA,__interpose`。启动日志里没有这行 = 本机/本签名方式下 interpose 未生效，
/// 需要另找方案（例如自编译引擎），不要以为「已经在独显上跑」。
static void ArchoeraLogInterposition(void) {
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    NSLog(@"[archoera] GPU interpose active (MTLCopyAllDevices / MTLCreateSystemDefaultDevice)");
  });
}

/// 高性能独显：非低功耗、非统一内存、非可移除，且支持 Impeller 依赖的
/// `MTLGPUFamilyMac2`。macOS 14 能跑的 Intel / AMD GPU 都满足这一条；不满足
/// 就不强选——核显与独显的 Metal 能力集并不相同，不能假设「独显 ⊇ 集显」。
static BOOL ArchoeraIsHighPerformanceDevice(id<MTLDevice> _Nullable device) {
  if (device == nil) {
    return NO;
  }
  return !device.isLowPower && !device.hasUnifiedMemory && !device.isRemovable &&
         [device supportsFamily:MTLGPUFamilyMac2];
}

/// 设备能力摘要（日志用）。
///
/// 核显 / 独显不是同一套 Metal 能力：GPU family、缓冲区 / 线程组内存上限、
/// 读写纹理 tier、argument buffer tier 都可能不同。排查「换到独显后特效 /
/// 封面取色异常」时，先对比这行与集显那行。
static NSString *ArchoeraDescribeCapabilities(id<MTLDevice> device) {
  NSMutableArray<NSString *> *families = [NSMutableArray array];
  if ([device supportsFamily:MTLGPUFamilyMac2]) {
    [families addObject:@"Mac2"];
  }
  if ([device supportsFamily:MTLGPUFamilyCommon3]) {
    [families addObject:@"Common3"];
  }
  if ([device supportsFamily:MTLGPUFamilyMetal3]) {
    [families addObject:@"Metal3"];
  }
  NSString *familyText =
      families.count > 0 ? [families componentsJoinedByString:@","] : @"none";

  NSString *readWrite = @"none";
  switch (device.readWriteTextureSupport) {
    case MTLReadWriteTextureTier1:
      readWrite = @"tier1";
      break;
    case MTLReadWriteTextureTier2:
      readWrite = @"tier2";
      break;
    default:
      break;
  }
  NSString *argumentBuffers =
      device.argumentBuffersSupport == MTLArgumentBuffersTier2 ? @"tier2" : @"tier1";

  return [NSString
      stringWithFormat:@"families=%@ rwTex=%@ argBuf=%@ buf=%luMB tgMem=%luKB",
                       familyText, readWrite, argumentBuffers,
                       (unsigned long)(device.maxBufferLength / (1024 * 1024)),
                       (unsigned long)(device.maxThreadgroupMemoryLength / 1024)];
}

/// 首次选设备时打印全部设备的能力，并检查「独显是否缺少集显具备的 family」。
static void ArchoeraLogDeviceCapabilities(NSArray<id<MTLDevice>> *devices) {
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    id<MTLDevice> integrated = nil;
    id<MTLDevice> discrete = nil;
    for (id<MTLDevice> device in devices) {
      NSLog(@"[archoera] GPU device: %@ (%@, %@, %@) %@", device.name,
            device.isRemovable ? @"eGPU" : (device.isLowPower ? @"integrated" : @"discrete"),
            device.hasUnifiedMemory ? @"unified" : @"dedicated",
            ArchoeraIsHighPerformanceDevice(device) ? @"eligible" : @"skipped",
            ArchoeraDescribeCapabilities(device));
      if (device.isLowPower) {
        integrated = device;
      }
      if (ArchoeraIsHighPerformanceDevice(device)) {
        discrete = device;
      }
    }
    if (integrated != nil && discrete != nil) {
      if ([integrated supportsFamily:MTLGPUFamilyMetal3] &&
          ![discrete supportsFamily:MTLGPUFamilyMetal3]) {
        NSLog(@"[archoera] GPU WARNING: 独显 %@ 只支持 Metal 2（集显支持 Metal 3）；"
              @"「自动」档会保持集显，强制独显会降级",
              discrete.name);
      }
      if ([discrete maxBufferLength] < [integrated maxBufferLength]) {
        NSLog(@"[archoera] GPU note: 独显 maxBufferLength 小于集显（%luMB < %luMB）",
              (unsigned long)([discrete maxBufferLength] / (1024 * 1024)),
              (unsigned long)([integrated maxBufferLength] / (1024 * 1024)));
      }
    }
  });
}

/// Dart 侧偏好文件（与 `app/lib/stores/data_dir.dart` 的 `resolveDataDir()` 对齐）：
/// `$ARCHOERA_DATA_DIR/prefs.json`，否则
/// `~/Library/Application Support/ArchoeraMvsync/prefs.json`（沙盒内 HOME 已指向容器）。
static NSString *ArchoeraPrefsFilePath(void) {
  NSString *override = NSProcessInfo.processInfo.environment[@"ARCHOERA_DATA_DIR"];
  if (override.length > 0) {
    return [override stringByAppendingPathComponent:@"prefs.json"];
  }
  NSString *base =
      [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support"];
  return [[base stringByAppendingPathComponent:@"ArchoeraMvsync"]
      stringByAppendingPathComponent:@"prefs.json"];
}

static ArchoeraGPUMode ArchoeraParseGPUMode(NSString *_Nullable raw) {
  NSString *value = [raw lowercaseString];
  if ([value isEqualToString:@"discrete"] || [value isEqualToString:@"high-performance"]) {
    return ArchoeraGPUModeDiscrete;
  }
  if ([value isEqualToString:@"integrated"] || [value isEqualToString:@"default"]) {
    return ArchoeraGPUModeIntegrated;
  }
  return ArchoeraGPUModeAuto;
}

static NSString *ArchoeraGPUModeName(ArchoeraGPUMode mode) {
  switch (mode) {
    case ArchoeraGPUModeDiscrete:
      return @"discrete";
    case ArchoeraGPUModeIntegrated:
      return @"integrated";
    case ArchoeraGPUModeAuto:
    default:
      return @"auto";
  }
}

/// 读取档位（环境变量 > `prefs.json` > 旧布尔键迁移 > 默认 `auto`）。
static ArchoeraGPUMode ArchoeraReadGPUMode(void) {
  NSString *override = NSProcessInfo.processInfo.environment[@"ARCHOERA_MACOS_GPU"];
  if (override.length > 0) {
    return ArchoeraParseGPUMode(override);
  }

  NSData *data = [NSData dataWithContentsOfFile:ArchoeraPrefsFilePath()];
  if (data == nil) {
    return ArchoeraGPUModeAuto;
  }
  id json = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
  if (![json isKindOfClass:NSDictionary.class]) {
    return ArchoeraGPUModeAuto;
  }
  id value = ((NSDictionary *)json)[@"render.macos_gpu"];
  if ([value isKindOfClass:NSString.class]) {
    return ArchoeraParseGPUMode((NSString *)value);
  }
  // 早期预览版布尔键：true = 强制独显，false = 集显。
  id legacy = ((NSDictionary *)json)[@"render.macos_discrete_gpu"];
  if ([legacy isKindOfClass:NSNumber.class]) {
    return [legacy boolValue] ? ArchoeraGPUModeDiscrete : ArchoeraGPUModeIntegrated;
  }
  return ArchoeraGPUModeAuto;
}

static ArchoeraGPUMode ArchoeraGPUModeCached(void) {
  static ArchoeraGPUMode mode;
  static dispatch_once_t once;
  dispatch_once(&once, ^{
    mode = ArchoeraReadGPUMode();
    NSLog(@"[archoera] macOS GPU preference: %@ (prefs: %@)",
          ArchoeraGPUModeName(mode), ArchoeraPrefsFilePath());
  });
  return mode;
}

/// 供 Swift Runner 读取当前档位（日志 / 自检）。
NSString *ArchoeraMacOSGPUModeDescription(void) {
  return ArchoeraGPUModeName(ArchoeraGPUModeCached());
}

/// 独显是否具备集显的**全部** Metal family（`auto` 档的门槛）。
static BOOL ArchoeraSupportsAllFamilies(id<MTLDevice> candidate, id<MTLDevice> reference) {
  const MTLGPUFamily families[] = {MTLGPUFamilyMac2, MTLGPUFamilyCommon3,
                                    MTLGPUFamilyMetal3};
  for (size_t i = 0; i < sizeof(families) / sizeof(families[0]); i++) {
    if ([reference supportsFamily:families[i]] && ![candidate supportsFamily:families[i]]) {
      return NO;
    }
  }
  return YES;
}

/// 在设备列表里挑一块高性能独显；工作集更大的优先（多独显 / Mac Pro）。
static id<MTLDevice> _Nullable ArchoeraPickHighPerformanceDevice(
    NSArray<id<MTLDevice>> *_Nullable devices) {
  id<MTLDevice> best = nil;
  for (id<MTLDevice> device in devices) {
    if (!ArchoeraIsHighPerformanceDevice(device)) {
      continue;
    }
    if (best == nil ||
        device.recommendedMaxWorkingSetSize > best.recommendedMaxWorkingSetSize) {
      best = device;
    }
  }
  return best;
}

static id<MTLDevice> _Nullable ArchoeraFindIntegratedDevice(
    NSArray<id<MTLDevice>> *_Nullable devices) {
  for (id<MTLDevice> device in devices) {
    if (device.isLowPower) {
      return device;
    }
  }
  return nil;
}

/// 是否把引擎的 device 换成独显。
///
/// `auto` 档要求独显支持集显具备的全部 family（例如集显 Metal 3 / 独显
/// Metal 2 的机器保持集显），否则不做降级。
static BOOL ArchoeraShouldForceDiscrete(NSArray<id<MTLDevice>> *devices) {
  ArchoeraGPUMode mode = ArchoeraGPUModeCached();
  if (mode == ArchoeraGPUModeIntegrated) {
    return NO;
  }
  id<MTLDevice> discrete = ArchoeraPickHighPerformanceDevice(devices);
  if (discrete == nil) {
    return NO;
  }
  if (mode == ArchoeraGPUModeDiscrete) {
    return YES;
  }
  id<MTLDevice> integrated = ArchoeraFindIntegratedDevice(devices);
  if (integrated == nil) {
    return YES;
  }
  if (!ArchoeraSupportsAllFamilies(discrete, integrated)) {
    NSLog(@"[archoera] GPU auto: 独显 %@ 缺少集显 %@ 的部分 Metal family，保持集显"
          @"（需要强制可在设置里选「优先独立显卡」）",
          discrete.name, integrated.name);
    return NO;
  }
  return YES;
}

static NSArray<id<MTLDevice>> *ArchoeraMTLCopyAllDevices(void) {
  ArchoeraLogInterposition();
  ArchoeraCopyAllDevicesFn original = ArchoeraOriginalCopyAllDevices();
  NSArray<id<MTLDevice>> *devices = original != NULL ? original() : @[];
  ArchoeraLogDeviceCapabilities(devices);
  if (!ArchoeraShouldForceDiscrete(devices)) {
    return devices;
  }

  NSMutableArray<id<MTLDevice>> *filtered = [NSMutableArray arrayWithCapacity:devices.count];
  for (id<MTLDevice> device in devices) {
    if (ArchoeraIsHighPerformanceDevice(device)) {
      [filtered addObject:device];
    }
  }
  return filtered.count > 0 ? filtered : devices;
}

static id<MTLDevice> _Nullable ArchoeraMTLCreateSystemDefaultDevice(void) {
  ArchoeraLogInterposition();
  ArchoeraCreateDefaultDeviceFn original = ArchoeraOriginalCreateDefaultDevice();
  id<MTLDevice> device = original != NULL ? original() : nil;

  ArchoeraCopyAllDevicesFn copyAll = ArchoeraOriginalCopyAllDevices();
  NSArray<id<MTLDevice>> *devices = copyAll != NULL ? copyAll() : @[];
  if (!ArchoeraShouldForceDiscrete(devices) || ArchoeraIsHighPerformanceDevice(device)) {
    return device;
  }
  id<MTLDevice> discrete = ArchoeraPickHighPerformanceDevice(devices);
  return discrete != nil ? discrete : device;
}

ARCHOERA_INTERPOSE(ArchoeraMTLCopyAllDevices, MTLCopyAllDevices)
ARCHOERA_INTERPOSE(ArchoeraMTLCreateSystemDefaultDevice, MTLCreateSystemDefaultDevice)
