#!/usr/bin/env bash
# ArchoeraMvsync
# Copyright (C) 2026 Archoera && BetaStudio2
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# EraStore 引擎级基准（设计文档 §3.3.0.16 / §5.3 P11）。
# 默认只跑 PROFILE=quick；30k 约 1 分钟（中间档，用来近似 50k 的构建 RSS）；
# full 约 15 分钟满负载，必须显式 ALLOW_LONG_BENCH=1。
# 不联网、无额外依赖，产物写入脚本同目录 out/。

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PROFILE="${PROFILE:-quick}"
OUT="${OUT:-$here/out}"

if [ "$PROFILE" = "full" ] && [ "${ALLOW_LONG_BENCH:-0}" != "1" ]; then
  echo "PROFILE=full 是约 15 分钟的满负载基准；确需运行请显式 ALLOW_LONG_BENCH=1。" >&2
  exit 2
fi

mkdir -p "$OUT"

exec dotnet run -c Release --project "$here/EraStoreBench.csproj" -- \
  --profile "$PROFILE" --out "$OUT" "$@"
