#!/usr/bin/env bash
# ArchoeraMvsync 本地曲库搜索索引器构建脚本（幂等）。
#
# 产物：indexer/build/{indexer-ffi.{so,dll,dylib}, libe_sqlite3.{so,dylib}, e_sqlite3.dll}
#   - indexer-ffi  ：.NET 10 NativeAOT 共享库，导出 C ABI v2：
#                     era_search_api_version / era_search_open / era_search_run /
#                     era_search_fallback / era_search_decorate / era_search_free /
#                     era_search_close / era_search_health / era_index_run /
#                     era_index_cancel / era_analyze / era_set_log_sink
#                     （旧 indexer_* 符号已全部删除，同包升级不兼容；Dart 侧启动握手
#                     era_search_api_version()==2，不符直接拒绝而不是「猜着用」）
#                     **过渡形态**：构建 / 分析 / 日志目前仍是同步入口
#                     （era_index_run 阻塞到整库写完）。最终任务句柄
#                     era_index_start / era_index_wait / era_index_cancel / era_index_free
#                     由 S3 落地；在那之前调用方须在子 isolate / 后台线程里调 era_index_run。
#   - libe_sqlite3 ：SQLitePCLRaw 原生依赖（含 FTS5；FFI 库运行所需，随包分发）
#     Windows 上无 lib 前缀（e_sqlite3.dll），与其他平台命名不同
#
# 依赖：
#   - .NET 10 SDK（dotnet --version）
#   - NuGet 首次还原需网络（Microsoft.Data.Sqlite / SQLitePCLRaw，与 scanner 同版本）
#   - **不依赖 Python**：所有数据表由纯 .NET 的 tools/gen_tables 生成
#     （跨语言计划 §4.3 硬性门禁：构建期工具链收敛到 .NET 一套）
# 使用：./build.sh [RID]     # RID 默认 linux-x64；三端：linux-x64 / win-x64 / osx-x64（或 osx-arm64）
set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-linux-x64}"
case "$RID" in
  *win*) EXT="dll";   SQLITE_NAME="e_sqlite3.dll" ;;
  *osx*) EXT="dylib"; SQLITE_NAME="libe_sqlite3.dylib" ;;
  *)     EXT="so";    SQLITE_NAME="libe_sqlite3.so" ;;
esac

mkdir -p build

# 分析器数据表：**全部由纯 .NET 的 tools/gen_tables 生成**（无 Python 依赖）。
# 缺失任意一张即整批生成（幂等、确定性；Unihan 派生表需 reference/unihan/Unihan.zip）。
if [ ! -f data/nfkc.tsv ] || [ ! -f data/accent_fold.tsv ] || [ ! -f data/variant_fold.tsv ] \
   || [ ! -f data/ja_readings.tsv ] || [ ! -f data/def_en2hanzi.tsv ] || [ ! -f data/def_hanzi2en.tsv ] \
   || [ ! -f data/lexicon_cross.tsv ]; then
  echo "==> 生成分析器数据表（纯 .NET，无 Python）"
  dotnet run --project tools/gen_tables
fi

# 数据包清单 / Dart 同源常量必须与 data/ 一致（T4.3）。改了任一 tsv 却忘记重算
# data_pack_id 会让「索引与查询用的表不一致」——这是最难排查的一类错误，构建期直接拦住。
echo "==> 校验数据包清单（data_pack_id / Dart 常量）"
dotnet run --project tools/gen_tables -- --manifest-only --check

# 用 -o 固定 publish 输出目录（不依赖 bin/Release/<tfm>/<rid> 路径）
echo "==> 发布 indexer FFI (NativeAOT, $RID)"
APPVER="$(sed -n 's/^version:[[:space:]]*//p' "$(dirname "$0")/../../pubspec.yaml" | head -1)"
[ -n "$APPVER" ] || APPVER="0.0.0+0"
APPVER_NUM="${APPVER/+/.}"
dotnet publish indexer-ffi/indexer-ffi.csproj \
  -c Release \
  -r "$RID" \
  -p:Version="$APPVER_NUM" \
  -p:FileVersion="$APPVER_NUM" \
  -p:InformationalVersion="$APPVER" \
  -o build/publish

cp "build/publish/indexer-ffi.$EXT" build/
cp "build/publish/$SQLITE_NAME" "build/$SQLITE_NAME"

echo "indexer FFI 产物就绪:"
ls -lh "build/indexer-ffi.$EXT" "build/$SQLITE_NAME"
