#!/usr/bin/env bash
# =====================================================================
#  ArchoeraMvsync 平台桥接 —— Windows 端本地编译验证（Linux 主机）
#
#  编译并链接 Windows 桥接（core.cpp / apl.cpp / backend_windows.cpp →
#  archoera_platform.dll），再校验导出符号表。两种后端：
#
#    1) msvc-wine（默认，与 CI 同编译器）：MSVC cl.exe + Wine，能发现
#       LNK2019 之类的符号缺失与 MSVC 严格语义问题。
#    2) zig（msvc-wine 缺失时自动降级）：`zig c++ -target x86_64-windows-gnu`，
#       用 mingw-w64 头/库编译链接。它**不等于** MSVC 验收（/permissive-、
#       WinRT 分支都不覆盖），只保证「源码可编译、符号可链接、导出表齐全」。
#       WinRT/SMTC/Toast 分支无 cppwinrt 头时自动编译掉（见 backend_windows.cpp）。
#
#  CI 仍在 Windows runner 原生构建（MSVC）；本脚本只做提交前的本地拦截。
#
#  依赖：
#    - msvc-wine：默认 /opt/msvc（可用 MSVC_INSTALL_DIR 指定）+ wine
#      —— 或用 zig（>= 0.13）降级
#    - 导出表校验：llvm-readobj 或 objdump（二者任一）
#  可选：cppwinrt 头（存在则一并编译 WinRT/SMTC/Toast 分支；缺省自动降级）。
#
#  用法：
#    bash app/tool/verify_windows.sh
#    MSVC_INSTALL_DIR=/opt/msvc bash app/tool/verify_windows.sh
#    ARCHOERA_VERIFY_FORCE_ZIG=1 bash app/tool/verify_windows.sh   # 强制走 zig
#
#  退出码：0 = 编译（+ 导出校验）通过；非 0 = 失败。
# =====================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PLATFORM_DIR="$ROOT/app/native/platform"

MSVC_INSTALL_DIR="${MSVC_INSTALL_DIR:-/opt/msvc}"
CL_DIR="$MSVC_INSTALL_DIR/bin/x64"

BUILD_DIR="${ARCHOERA_VERIFY_BUILD:-$(mktemp -d /tmp/archoera-win-verify.XXXXXX)}"
mkdir -p "$BUILD_DIR"

# ── 选后端：msvc-wine 优先；缺依赖时降级 zig（显式 FORCE_ZIG 可跳过 MSVC）──
MODE="msvc"
if [ "${ARCHOERA_VERIFY_FORCE_ZIG:-0}" = "1" ] || [ ! -x "$CL_DIR/cl" ]; then
  if command -v zig >/dev/null 2>&1; then
    MODE="zig"
  else
    echo "找不到 msvc-wine cl：$CL_DIR/cl（设 MSVC_INSTALL_DIR），也没有 zig 可降级" >&2
    exit 1
  fi
elif ! command -v wine >/dev/null 2>&1; then
  echo "缺少 wine（msvc-wine 需要）；或设 ARCHOERA_VERIFY_FORCE_ZIG=1 走 zig" >&2
  exit 1
fi

# 导出表读取工具（llvm-readobj 输出干净，objdump 兜底）。
READ_EXPORTS=""
if command -v llvm-readobj >/dev/null 2>&1; then
  READ_EXPORTS="llvm-readobj"
elif command -v objdump >/dev/null 2>&1; then
  READ_EXPORTS="objdump"
fi

echo "模式      : $MODE"
echo "OUT       : $BUILD_DIR"
echo "导出校验  : ${READ_EXPORTS:-<无（跳过符号检查）>}"
echo

incs=(-I "$PLATFORM_DIR/include" -I "$PLATFORM_DIR/src")

if [ "$MODE" = "msvc" ]; then
  # cppwinrt 头目录：优先 CPPWINRT_INCLUDE，其次 msvc-wine SDK 内自带。
  CPPWINRT_INCLUDE="${CPPWINRT_INCLUDE:-}"
  if [ -z "$CPPWINRT_INCLUDE" ]; then
    CPPWINRT_INCLUDE="$(find "$MSVC_INSTALL_DIR" -maxdepth 6 -type d -path '*Include*' -name cppwinrt 2>/dev/null | sort | tail -1 || true)"
  fi
  if [ -n "$CPPWINRT_INCLUDE" ]; then incs+=("-I$CPPWINRT_INCLUDE"); fi

  export PATH="$CL_DIR:$PATH"
  export WINEDEBUG="${WINEDEBUG:--all}"

  echo "msvc-wine : $CL_DIR"
  echo "cppwinrt  : ${CPPWINRT_INCLUDE:-<无（WinRT 分支自动降级）>}"
  echo

  set +e
  # 项目同款标志（见 app/native/platform/CMakeLists.txt）：/utf-8 必需——
  # 否则 MSVC 在 936 代码页下会把 UTF-8 中文注释解析错乱（C4819）。
  # /Fo：中间 .obj 必须落 BUILD_DIR —— MSVC 默认把 .obj 写在**当前工作目录**，
  # 从仓库根调用本脚本会在仓库里留下 apl.obj / core.obj / backend_windows.obj
  #（2026-10-14 实录）。与 verify_macos.sh 显式 `-o $BUILD_DIR/...` 的做法对齐。
  cl /nologo /std:c++20 /EHsc /O2 /W3 /permissive- /Zc:__cplusplus /utf-8 \
     /DARCHOERA_PLATFORM_BUILD "${incs[@]}" \
     /LD \
     "/Fo$BUILD_DIR/" \
     "$PLATFORM_DIR/src/core.cpp" \
     "$PLATFORM_DIR/src/apl.cpp" \
     "$PLATFORM_DIR/src/backend_windows.cpp" \
     "/Fe$BUILD_DIR/archoera_platform.dll" \
     /link user32.lib powrprof.lib advapi32.lib dwmapi.lib windowsapp.lib \
           shell32.lib shlwapi.lib propsys.lib ole32.lib uuid.lib
  rc=$?
  set -e
else
  # zig 的 mingw-w64 目标：不需要 windowsapp.lib（WinRT 分支无头时自动关闭），
  # 也不重复定义 _WIN32_WINNT（zig 已内置 0x0A00，重复定义只会刷警告）。
  # 缓存落 BUILD_DIR，避免污染 ~/.cache/zig 也不受只读 HOME 影响。
  export ZIG_LOCAL_CACHE_DIR="$BUILD_DIR/zig-cache"
  export ZIG_GLOBAL_CACHE_DIR="$BUILD_DIR/zig-global-cache"
  echo "zig       : $(command -v zig)（x86_64-windows-gnu / mingw-w64，非 MSVC）"
  echo

  set +e
  zig c++ -target x86_64-windows-gnu -std=c++20 -O2 -shared \
     -DARCHOERA_PLATFORM_BUILD "${incs[@]}" \
     "$PLATFORM_DIR/src/core.cpp" \
     "$PLATFORM_DIR/src/apl.cpp" \
     "$PLATFORM_DIR/src/backend_windows.cpp" \
     -o "$BUILD_DIR/archoera_platform.dll" \
     -luser32 -lpowrprof -ladvapi32 -ldwmapi -lshell32 \
     -lshlwapi -lpropsys -lole32 -luuid -lgdi32
  rc=$?
  set -e
fi

# ── 导出符号校验：链接成功但漏导出（尤其 __declspec 宏失效）才是最隐蔽的回归 ──
if [ $rc -eq 0 ] && [ -n "$READ_EXPORTS" ]; then
  DLL="$BUILD_DIR/archoera_platform.dll"
  if [ "$READ_EXPORTS" = "llvm-readobj" ]; then
    llvm-readobj --coff-exports "$DLL" > "$BUILD_DIR/exports.txt"
  else
    objdump -p "$DLL" > "$BUILD_DIR/exports.txt"
  fi
  missing=0
  for sym in apl_abi_version apl_window_get_size apl_window_set_size \
             apl_window_set_decor apl_window_start_resize \
             apl_window_request_close apl_window_stop_events; do
    if ! grep -q "Name: ${sym}\$" "$BUILD_DIR/exports.txt" \
       && ! grep -q "[[:space:]]${sym}\$" "$BUILD_DIR/exports.txt"; then
      echo "❌ 缺少导出符号：$sym" >&2
      missing=1
    fi
  done
  if [ $missing -ne 0 ]; then rc=1; fi
fi

echo
if [ $rc -eq 0 ]; then
  if [ "$MODE" = "msvc" ]; then
    echo "✅ Windows 端编译+链接通过（MSVC，导出符号齐全）"
  else
    echo "✅ Windows 端编译+链接通过（zig/mingw-w64 降级校验，导出符号齐全）"
    echo "   注意：这不是 MSVC 验收——WinRT/SMTC 分支未编译，严格语义仍以 CI Windows runner 为准。"
  fi
  ls -l "$BUILD_DIR"/archoera_platform.*
else
  echo "❌ Windows 端编译/链接/导出校验失败" >&2
fi
exit $rc
