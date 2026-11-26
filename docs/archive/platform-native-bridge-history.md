# 平台原生桥接历史归档（2026-09-10 ～ 2026-09-12）

> 从 `docs/platform-native-bridge.md` 归档：Zig 期（P0–P5）的逐项实施与修复记录、
> macOS/Windows 窗口关闭崩溃的证据与 ABI v4 迁移草案。
> 现行实现（C++/ObjC++，ABI v9/v10）与验收状态以主文档为准；本文件只作历史证据。

---

## 1. Zig 期实施进度（P0–P5）与逐项修复

### 7.1 实施进度（回填）

| 阶段 | 状态 | 落地内容 / 验收 |
|---|---|---|
| **P0 骨架** | ✅ 2026-09-10 | `app/native/platform`（build.zig + `archoera_platform.h` + core 事件分发 + 三平台后端 stub）；Dart 绑定/工厂/Noop/失败类型/SystemWindow 契约；打包（linux/windows CMake + macOS pbxproj + build-linux.sh）；`zig build test` 5/5、三平台交叉编译、`flutter analyze` 全绿 |
| **P1 Linux Power** | ✅ 2026-09-10 | 自研 D-Bus（`dbus/message.zig` 编组↔解组往返 9 例 + `dbus/transport.zig` 地址解析/AUTH EXTERNAL/Hello/读取权认领）；`backend_linux.zig` ScreenSaver Inhibit/UnInhibit + ActiveChanged 订阅 + 泵线程 + BACKEND_STATE；`zig build test` **19/19**（含真实会话总线 Hello、两连接信号端到端）；Dart `SystemPower` 接线 + 失败 toast（l10n ×9）；工厂 FFI/兜底分流 |
| P2 Linux MPRIS | ✅ 2026-09-10 | `dbus/mpris.zig`：`org.mpris.MediaPlayer2[.archoera]` 名称申请 + Root/Player/Properties/Introspectable 方法分发 + 属性 Get/GetAll/Set + `PropertiesChanged` 推送 + 位置单调外推；`backend_linux` 能力位图扩 `MEDIA_SESSION\|MEDIA_SEEK\|MEDIA_ARTWORK`；Dart `MediaSessionHost` 接线（元数据/播放态同步、命令→`playbackNotifier`、断连 toast）。`zig build test` **22/22**（含真实总线 GetAll + Play 端到端）；`caps=31` |
| P3a Windows Power/窗口 | ✅ 2026-09-10（**WSL interop 真机验证**） | `backend_windows.zig`：SetThreadExecutionState（`CreateThread` 专用常驻线程抑制）+ PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_CALLBACK) 熄屏 + `FindWindowW` 发现 Flutter 顶层窗口 + WndProc 子类化窗口状态；`build.zig` 链 user32/powrprof。真机冒烟（`zig build win-smoke`，WSL 直跑 exe）：`caps=0x23`、抑制 on/off=OK、熄屏注册=OK、无 Flutter 窗口时 window_events 优雅返回 -2、shutdown 干净。Dart `power_saver` 按 caps 接入 `SystemWindow` |
| P3b Windows SMTC | ✅ 2026-09-10（**WSL interop 真机验证**；含封面缩略图 + 时间轴） | `win_smtc.zig`：WinRT COM vtable 直调。**权威来源**：本机 `Windows.Media.winmd`（ECMA-335）解析出方法声明顺序（vtable 槽位）+ `[Guid]`（IID `ISystemMediaTransportControls`=99FA3FF4-…），interop IID 取自 mingw 头。`RoInitialize/RoGetActivationFactory/WindowsCreateString` 经 `LoadLibrary("combase.dll")` 运行时解析（无 x86_64 导入库）；事件委托手写 COM（`add_ButtonPressed` → `get_Button` → apl 命令）。真机冒烟：`set_track/set_playback=0`、**读回 PlaybackStatus=Playing(3)/IsEnabled=1 校验槽位**、窗口事件触发。未做：时间轴（ISystemMediaTransportControls2）、缩略图 |
| P4 macOS | ✅ 2026-09-10（**交叉编译验证**，待真机；含封面 + changePlaybackPosition） | `backend_macos.zig`：无 SDK 头 → 全程 `dlopen`+`objc_msgSend` 运行时直调（不 `@cImport` 框架头、不链框架，仅 libSystem dlopen/dlsym）；观察者/远程命令目标用运行时 ObjC 类（`objc_allocateClassPair`+`class_addMethod`）。覆盖 NSProcessInfo beginActivity 抑制、NSWorkspace 熄屏通知、NSWindow 窗口状态通知、MPNowPlayingInfoCenter + MPRemoteCommandCenter（play/pause/toggle/next/prev/stop）。aarch64/x86_64-macos 交叉编译通过 |
| P5 Linux 窗口状态 | ✅ 2026-09-10（编译验证，待真机） | **会话检测 + GTK/GDK 统一模式**：`linux_window.zig` 经 `dlopen` libgtk-3/libgdk-3/libglib-2.0 取应用自身 GtkWindow，`g_main_context_invoke` 汇入 GTK 主线程 + `g_timeout_add(500ms)` 轻轮询 `gtk_window_is_active`/`gdk_window_get_state(ICONIFIED)`；**同时覆盖 X11 与 Wayland**（GDK 已抽象），消除 Wayland 回退 window_manager。`detectSession()`（WAYLAND_DISPLAY/DISPLAY）用于能力门控与日志 |

**过渡兜底（P3/P4 前不回归）**：桥接未置 Power 能力位时，工厂注入
`WakelockSystemPower`（wakelock_plus 插件）——Windows/macOS 休眠抑制维持现状；
P3/P4 原生落地后该实现与 `wakelock_plus` 依赖一并移除（§7 收尾）。

**验收环境说明**：本机开发会话无 `org.freedesktop.ScreenSaver` / `org.gnome.ScreenSaver`
服务（headless/受限会话），`Inhibit` 正确返回 `APL_ERR_BACKEND` → Dart 侧 toast；
真实 GNOME/KDE 会话下 `Inhibit` 返回 OK。反向事件链路已由「两连接信号端到端」测试覆盖
（无需真实 ScreenSaver 服务）。

### 7.2 SMTC 实现（P3b，已解阻）

原阻塞（缺 WinRT ABI 头/SDK）已通过**解析本机 `Windows.Media.winmd`** 解决：
ECMA-335 元数据的接口方法声明顺序即 WinRT vtable 槽位顺序；IID 由 winmd 内
`[Guid]` 校验（`ISystemMediaTransportControls` = `99FA3FF4-1742-42A6-902E-087D41F965EC`），
`ISystemMediaTransportControlsInterop` IID 取自 mingw 头。`combase.dll` 无 x86_64
导入库 → `LoadLibrary`+`GetProcAddress` 运行时解析。真机（WSL interop）验证
vtable 槽位正确（写读回一致）。

### 7.3 依赖与图标清理（2026-09-10）

- Dart 移除已无引用的 `dbus` 依赖（Linux D-Bus 已由原生桥接接管）；
  三平台原生抑制齐备后移除 `wakelock_plus` 与 `WakelockSystemPower` 兜底
  （桥接缺失时 `NoopSystemPower` 静默降级）。
- 图标库：删除 116 个未被 registry 引用的孤儿 source SVG + 3 个死常量
  （`borderRadius`/`dotCircleOutline`/`miniplayer`），`source = registry = dart = 引用 = 211`。
  字体暂含 3 个孤儿字形（无 node_modules 未重生成，无害；下次全量重生成自然收敛）。

### 7.4 Windows 平台修复（2026-09-11，待真机验证）

针对「Windows 下可多开实例」「蓝牙耳机 / 媒体面板按键全无响应（但面板能显示曲目）」
「系统主题色恒读不到」：

- **单实例（native）**：`backend_windows.zig` 用命名互斥体
  `CreateMutexW("Local\\ArchoeraMvsync.SingleInstance")` + `ERROR_ALREADY_EXISTS`
  实现（此前恒返回 1 放行）。`Local\` 会话命名空间 = 每登录会话一个实例，语义对齐
  Linux `XDG_RUNTIME_DIR` 文件锁 / macOS 文件锁；句柄持有至进程退出，`apl_shutdown` 关闭。
- **单实例（移除 Dart 兜底）**：`platform_capabilities.dart` 的
  `acquireSingleInstance()` 不再在桥接缺失时返回 `true`（旧兜底会掩盖桥接损坏并
  放任多开）；改为抛 `StateError`，`main.dart` 捕获后写 stderr 并 `exit(1)`——
  缺桥接时显式失败，绝不静默多开。
- **SMTC 按钮**：`win_smtc.zig` 事件委托 `QueryInterface` 修正——旧实现对所有 IID
  一律回 `S_OK`，会让 WinRT 误以为委托实现了 `IInspectable` / `IMarshal` 并按错位
  vtable 槽调用（`Invoke` 被当成 `GetIids` / `GetUnmarshalClass`），导致
  `add_ButtonPressed` 看似成功但 `ButtonPressed` 永不回调。现明确对
  `IInspectable` / `IMarshal` 返回 `E_NOINTERFACE`，其余（含 WinRT 运行期生成的
  委托 IID）应答并正确 `AddRef`。另注：Flutter 默认 UI 线程与平台线程合并，
  平台线程 `CoInitializeEx(APARTMENTTHREADED)` 为 STA → 委托须应答
  `IAgileObject`（QI 未拒绝即应答）。
- **媒体键兜底**：`backend_windows.zig` 子类化 WndProc 处理 `WM_APPCOMMAND`
  （`APPCOMMAND_MEDIA_*` → 统一命令），覆盖 SMTC 非当前会话时系统退化的旧通路。
- **系统主题色**：`backend_windows.zig` 预定义句柄 `HKEY_CURRENT_USER` 修正为 64 位
  符号扩展值 `0xFFFFFFFF80000001`（`(HKEY)(ULONG_PTR)((LONG)0x80000001)`；旧写
  `0x80000001` 在 64 位下非法）——此前 `RegGetValueW` / `RegOpenKeyExW` 全失败，
  `apl_system_accent` 恒返回 null，主题色与「颜色变更事件」双双失效。
- **诊断**：`apl/smtc:` 前缀经 `OutputDebugStringA` 输出 `RoInitialize` /
  `add_ButtonPressed` 的 HRESULT、QI 被拒 IID、`ButtonPressed` 到达；`win-smoke`
  增印 `instance_acquire` / `button_registered`。真机验收用 DebugView 观察。
- 附：修复 `win-smoke` 编译（补齐 `win_common.zig` 的 `WNDCLASSW` /
  `RegisterClassW` / `CreateWindowExW` / `DefWindowProcW` / `DestroyWindow` /
  `GetModuleHandleW` 声明，此前自 `@cImport` 移除后即失配）。

### 7.5 EraSync 内核 Windows 部署修复（2026-09-11，待真机验证）

现象：Windows 任意文件播放闪退；Linux 正常。根因是**打包缺陷而非内核运行时缺陷**：

- `app/core/audio-engine/build.zig` 同时安装静态库与动态库，Windows 下二者基名相同
  （`archoera_kernel.lib`）；动态库 install 在后，其 **DLL import lib 覆盖了静态库**。
- `build_windows.bat` 正是链接该 `zig-out\lib\archoera_kernel.lib`（import lib）→
  `archoera_mediaengine.dll` 产生对 `archoera_kernel.dll` 的**加载期硬依赖**。
- 而 Zig 把 DLL 装在 `zig-out\bin\archoera_kernel.dll`，scanner csproj / CMake 却按
  `lib/` 找 → DLL 从未进入 bundle → 播放首帧 `DynamicLibrary.open(mediaengine)`
  失败即闪退（与具体文件无关）。

修复：`build_windows.bat` 从 `zig-out\bin` 拷 DLL 到引擎 `build\`；`app/windows/
CMakeLists.txt` 将其装到 **exe 根**（Windows 加载器按 exe 目录解析依赖，scanner
`KernelMetadata` 亦按 `AppContext.BaseDirectory` 查找）；scanner 两个 csproj 的
Windows DLL 源路径 `lib`→`bin`；`KernelMetadata.LocateLibrary` 开发树回退路径按平台
取 `bin`/`lib`。注：Windows 静态库当前不含 compiler-rt（`___chkstk_ms` 等），故
Windows 走 DLL 而非静态链接。

### 7.6 SMTC 委托 QI 误应答致 combase 0xC0000005（2026-09-11，已复现验证）

现象：真 Windows 上 `combase.dll` 0xC0000005；Wine 不崩（SMTC 为 stub，不做
COM 封送）。上一轮「封面后台线程跨 apartment」修复（d6b9d68）后仍复现。

根因：SMTC 的 ButtonPressed 事件委托只实现 IUnknown + Invoke（WinRT 委托 ABI：
QueryInterface/AddRef/Release/Invoke 共 4 槽，**不**继承 IInspectable）。其
`QueryInterface` 旧策略为「除 IInspectable/IMarshal 外一律 S_OK」。真 Windows
标准封送时 combase 依次查询 `IMarshal`（拒绝）→ `IStdMarshalInfo`（旧策略误应答
S_OK）→ 调用 `GetClassForHandler`（槽 3）→ 实为委托的 `Invoke` → 把
`pvDestContext` 当事件参数解引用 → 访问冲突。Wine 的 `CoMarshalInterface` 亦
复现同一错位调用（`IStdMarshalInfo` / `IExternalConnection`）。

修复：`win_smtc.zig` 的 `handlerQI` 改为**白名单**——仅放行 `IUnknown`、
`IAgileObject` 与运行期生成的委托 IID（非 COM 保留段且非 IInspectable /
ICallFactory），其余一律 `E_NOINTERFACE`。`isReservedComIid` 识别保留段
`{000000xx-0000-0000-C000-000000000046}`。已用最小 C 复现程序在 Wine 验证：
旧逻辑 `CoMarshalInterface` 会错调 Invoke，新逻辑不再触发。

### 7.7 SMTC 委托封送必须应答 IMarshal（2026-09-11，7.6 的后续）

现象：7.6 的白名单（**拒绝** IMarshal）上线后，真 Windows 仍 `combase.dll`
0xC0000005（WER `61da1eb4-8474-4762-bce8-9b419dc24d46`）。

根因：combase 跨 apartment 封送 WinRT 委托时**必须**能 QI 到 `IMarshal`。7.6 把
IMarshal 也拒了 → combase 退回标准封送 → 仍按 `IMarshal` 的 9 槽布局调用本对象
（只有 4 槽）→ 错调 `Invoke` → 访问冲突。即「误应答 IMarshal」与「拒绝 IMarshal」
**都会崩**，正解是**返回一个真正的 IMarshal**。

修复（对齐 `windows-rs`：`crates/libs/core/src/imp/delegate_box.rs` 的
`QueryInterface` + `marshaler.rs`）：`handlerQI` 对 `IMarshal` 调
`CoCreateFreeThreadedMarshaler`（ole32）建自由线程封送器，再包一层 `MarshalerObj`
——`QI(IMarshal)` 返回包装自身、其余 IID 转发给委托对象；6 个 IMarshal 方法全部
转发给自由线程封送器。参考实现确认：`windows` crate 的委托对象也是
**4 槽（IUnknown + Invoke）**，与本实现一致。

验证：`x86_64-windows-gnu` 交叉编译通过；真机待 Windows 复测。

### 7.8 本地封面 refFromFile 致 combase 0xC0000005（2026-09-11）

现象：**仅本地音乐**（点曲即崩）触发 combase 0xC0000005；在线音乐正常。

根因：SMTC 封面有两条路径——在线 http → `RandomAccessStreamReference.CreateFromUri`
（正常）；**本地** → `refFromFile`（`StorageFile.GetFileFromPathAsync` + 轮询
`IAsyncOperation`）。后者两个问题：① Dart 侧 `local_track.dart` 拼出的是
`file://C:\...`（既非标准 URI 也非纯路径），被原样丢给 `GetFileFromPathAsync`；
② 在 **STA 平台线程**用 `Sleep(20)` 轮询异步操作，**阻塞公寓消息泵**，combase 无法
投递完成回调 → 崩溃/死锁。`CreateFromUri` 文档明确只支持 http/https/ms-appx/
ms-appdata，故本地必须走 `StorageFile`（不能改走 Uri）。

修复：`refFromFile` 剥离 `file://` 前缀（含 `file:///C:/` 的前导 `/`）得到纯路径；
等待改用 `CoWaitForMultipleHandles(COWAIT_DISPATCH_CALLS |
COWAIT_DISPATCH_WINDOW_MESSAGES)` 泵消息，不再 `Sleep`。交叉编译通过，真机待复测。

### 7.9 本地封面改内存流（7.8 的最终方案，2026-09-11）

7.8 的 `CoWaitForMultipleHandles` 仍属「用经典 COM 等待去套 WinRT 异步」，别扭且
不该依赖：**WinRT 惯例是事件驱动**（`put_Completed` 挂委托，完成时由消息泵回调），
不应在 STA 上同步阻塞等待异步操作。

最终方案：**Dart 读本地封面字节 → 原生按 Chromium 方案建内存流**（对齐
`components/system_media_controls/win/system_media_controls_win.cc` 的
`SetThumbnail`）：

- Dart（`platform_bindings.dart`）：`TrackMeta` 增 `art_bytes` 字段；本地封面
  （`file://`/本地路径）用 `readAsBytesSync` 读入，http(s) 仍只传 `art_url`。
- 原生（`win_smtc.zig`）：`RoActivateInstance("Windows.Storage.Streams.InMemoryRandomAccessStream")`
  → QI `IOutputStream` → `IDataWriterFactory.CreateDataWriter` → `WriteBytes`
  → `StoreAsync`（`put_Completed` 完成回调里 `CreateFromStream` → `put_Thumbnail`
  → `Update`）。流/writer/op 持有到回调完成（对齐 Chromium 成员变量，防异步
  完成前析构）。
- http 封面：`Windows.Foundation.Uri` → `CreateFromUri`（同步；文档只支持
  http/https/ms-appx/ms-appdata，故本地不能走 Uri）。
- `refFromFile`（`GetFileFromPathAsync` + 泵消息）已删除。

### 7.10 Windows SMTC 改 C++/WinRT 标准委托 + AppUserModelID（2026-09-12）

现象：Windows 媒体浮出面板能显示曲目/封面，但播放/上下首/停止与蓝牙耳机 AVRCP
按键全无响应。

根因（两条，均在 7.4/7.6/7.7 手写 COM 委托之外残留）：

- `apl_init()` **从未调用 `backend.init()`** → `SetCurrentProcessExplicitAppUserModelID`
  与 macOS `MPRemoteCommand` 注册实际从未执行；AUMID 缺失使媒体键路由/会话识别不稳。
- `ButtonPressed` 用 Zig 手写 4 槽 COM 委托 + 自研 `IMarshal` 包装。即便 QI 策略修对，
  真机上 `add_ButtonPressed` 返回 `S_OK` 但事件仍不回调（事件源跨 apartment 封送）。

修复（对齐 Chromium `components/system_media_controls/win/system_media_controls_win.cc`）：

- 新增 `app/native/platform/src/backend/win_smtc.cpp`：C++/WinRT 标准委托投影
  （`SystemMediaTransportControls.ButtonPressed(TypedEventHandler<…>)`）注册按钮事件，
  并实现 `GetForWindow` + `DisplayUpdater` + `ISystemMediaTransportControls2`
  时间轴/`PlaybackRate` + 内存流缩略图（对齐 Chromium `SetThumbnail`）。仅在
  `-Dcppwinrt-include` 提供时编译（与 `win_toast.cpp` 同一开关）。
- `win_smtc.zig` 改为**薄转发层**：正向 `init/setTrack/setPlayback/deinit` →
  `apl_smtc_win_*`；反向导出 `apl_smtc_on_button`，把 C++ 按钮回调经 `core.dispatch`
  转发 Dart（保持「Flutter 桥接层 + Zig 转发」架构）。未编译 C++ 时弱符号兜底
  （SMTC 静默禁用、不崩）。
- `apl_init()` 调 `backend.init()`：Windows 设 AUMID、macOS 注册远程命令。
- `windows/runner/main.cpp` 在建窗**之前**设 `SetCurrentProcessExplicitAppUserModelID`
  （任务栏分组/媒体键路由需早于首个窗口）。
- 按钮映射：Windows `Play/Pause/Stop/Next/Previous` → `core.CMD_*`；时间轴 +
  `PlaybackRate` 让系统把本会话视为**活动会话**（蓝牙 AVRCP/媒体键按活动会话路由）。
- 兜底保留：`backend_windows.zig` 的 `WM_APPCOMMAND` 子类化（SMTC 非活动会话时的
  前台退化通路）。



---

## 2. 窗口关闭崩溃（macOS / Windows）与迁入 C++ 的计划（草案）

## 附：窗口关闭崩溃（macOS / Windows）与窗口能力迁入 C++ 的计划

### 现象与证据

- macOS 关闭窗口 / 退出时 `SIGSEGV`，崩溃线程是主线程，栈顶为：
  `NSNotificationCenter postNotificationName:` → `_CFXRegistrationPost` →
  `__CFNOTIFICATIONCENTER_IS_CALLING_OUT_TO_AN_OBSERVER__` → **跳转到未映射地址**
  （`crash-849`：`0x119ef0ec8` 不在任何 VM region），调用链经
  `-[NSWindow resignKeyWindow]` / `_closeForTermination` / `NSApplication terminate`。
- Windows 侧同类现象：`window_manager`（0.6+ 依赖 `nativeapi`/`cnativeapi`）在
  注册/注销窗口监听、跨线程同步读窗口状态时卡死或崩溃（见
  `lib/stores/window_full_screen.dart` 的注释与规避）。

### 根因（第三方插件生命周期，不是 Flutter 本身）

`window_manager 0.7` 经 `nativeapi`/`cnativeapi 0.6.1` 实现窗口能力。其 macOS 后端
`cxx_impl/src/platform/macos/window_manager_macos.mm`：

- 在 `StartEventListening()` 里向 `NSNotificationCenter` 注册 9 个窗口通知
  （`windowDidResignKey:` 等），观察者是 `delegate_`，内部保存 **原始 `Impl*`** 指针；
- 只有 `Impl` 析构时 `StopEventListening()` 才 `removeObserver`；
- 应用终止 / 引擎关闭时，AppKit 关闭窗口 → 投递 `resignKey` 等通知 → 观察者仍指向
  已释放的 `Impl` / 引擎回调 → 跳转到被 unmap 的内存 → SIGSEGV。

这与「是不是 Flutter 跨平台 bug」无关；是插件在**进程退出时序**里没有先把原生观察者
摘干净。Flutter 官方 embedder 不控制插件的这些 observer。

### 已应用的止损（本次）

- `WindowControls` 的关闭按钮**不再调用 `window.close()`**（那是 cnativeapi 的直接
  关窗路径，必触发上述通知）。改为转交 `TrayIntegration` 注册的统一关闭策略
  （后台常驻 / 退出 / 询问），与系统关闭请求同一条路（`appWindowCloseHandler`）。
- 退出仍走 `quitApplication` → 落盘 → 平台桥接收尾 → `exit(0)`（不经过 AppKit 的
  窗口关闭链），与 Linux 的既有规避一致。

> 剩余风险：⌘Q / 系统注销仍可能由 AppKit 走 `terminate → closeForTermination`；
> 只要 `window_manager` 的原生观察者仍在，就有同一崩溃面。要彻底消除，需把窗口能力
> 迁入自研桥接（见下）。

### 迁移计划（符合「系统能力只走 C++ 桥接器」）

目标：删除 `window_manager` / `nativeapi` / `cnativeapi` 依赖，窗口生命周期由
`app/native/platform` 拥有（观察者的注册与注销都在我们的对象内，退出前可显式 stop）。

**新增 ABI（`archoera_platform.h`，ABI v4）**

```c
APL_API int32_t apl_window_set_size(int32_t w, int32_t h);
APL_API int32_t apl_window_set_min_size(int32_t w, int32_t h);
APL_API int32_t apl_window_center(void);
APL_API int32_t apl_window_show(void);
APL_API int32_t apl_window_hide(void);
APL_API int32_t apl_window_minimize(void);
APL_API int32_t apl_window_set_maximized(int32_t on);
APL_API int32_t apl_window_set_fullscreen(int32_t on);
APL_API int32_t apl_window_set_always_on_top(int32_t on);
APL_API int32_t apl_window_set_resizable(int32_t on);
APL_API int32_t apl_window_set_decor(int32_t mode);   /* system / caption / borderless */
APL_API int32_t apl_window_request_close(void);       /* 只发事件，不直接关窗 */
APL_API int32_t apl_window_stop_events(void);         /* 退出前显式摘观察者 */
```

事件扩为：`APL_EVENT_WINDOW_STATE`（minimized/focused/maximized/fullscreen 位域）+
`APL_EVENT_WINDOW_CLOSE_REQUEST`（Dart 决定隐藏/退出，桥接不自行关窗）。

**平台实现要点**

- macOS：桥接持有 `NSWindowDelegate` + `NSNotificationCenter` 观察者，**所有 observer
  句柄都有 owner 并在 stop/destroy 时 remove**；`applicationShouldTerminate` 可经桥接
  暴露的 native hook 转交 Dart 的 `quitApplication`（落盘后 `exit(0)`），不再让 AppKit
  走 `closeForTermination`。
- Windows：`SetWindowLongPtrW(GWLP_WNDPROC)` 子类化 + `WM_*` 处理；`WM_CLOSE` 只投递
  `WINDOW_CLOSE_REQUEST`，不直接 `DestroyWindow`；退出前恢复原 wndproc。
- Linux：沿用现有 X11/GTK 窗口状态后端，补 `_NET_WM_STATE_*`（最大化 / 全屏 /
  always-on-top）与 `WM_DELETE_WINDOW` 事件转发。

**Dart 侧**

- `lib/services/platform/window_controls.dart`（新）封装 `apl_window_*`，替换
  `window_manager` 的调用点（`main.dart`、`window_controls.dart`、
  `window_full_screen.dart`、`tray_integration_logic.dart`、`window_effects.dart`、
  `power_saver.dart`、`window_decor.dart`）。
- 关闭路径统一：`apl_window_request_close` → Dart 策略 → `hide()` 或 `quitApplication`。

**验收**

1. `rg "window_manager|nativeapi|cnativeapi" app/pubspec.* app/lib` 零命中；
2. macOS：⌘Q / 关闭按钮 / 托盘退出三条路径各 20 次无崩溃，且落盘完整；
3. Windows：关闭 + 快速重开 ×20 不卡死；4. 三端窗口状态（最大化/全屏/焦点）
   与现行为一致；5. `flutter test` 与 ABI 守卫（`native_platform_abi_test.dart`）全绿。

> 迁移前，任何**新功能**不得再新增 `window_manager` 调用；本节的止损（关闭按钮改道）
> 是所有现网版本的推荐修复。

