#ifndef RUNNER_BRIDGING_HEADER_H_
#define RUNNER_BRIDGING_HEADER_H_

// 原生 `archoerashell` 入口（Rust staticlib，见 app/core/shell）。main.swift 在
// Flutter 初始化前调用；返回进程退出码。
int archoera_shell_main(int argc, char **argv);

// macOS 显卡档位（`prefs.json` 的 `render.macos_gpu`：auto / discrete /
// integrated，默认 auto）。与 GPUSelection.m 的 interpose 共用同一份读取逻辑，
// 供 MainFlutterWindow 打日志 / 自检（选 discrete 却仍拿到集显 = interpose 未生效）。
//
// 返回**静态 C 字符串**而不是 NSString：桥接头会被 Swift 预编译，不能假设
// 调用方已经引入 Foundation（否则 `unknown type name 'NSString'`）。
const char *ArchoeraMacOSGPUModeDescription(void);

#endif  // RUNNER_BRIDGING_HEADER_H_
