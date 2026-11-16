#!/usr/bin/env bash
# ══════════════════════════════════════════════════════════════
#  红果视频下载器 —— C# / Avalonia UI 版 构建脚本（Git Bash / MSYS）
#  用法：./build.sh            构建 Release（输出到 ./dist）
#        ./build.sh selftest   只构建并跑无头自检
# ══════════════════════════════════════════════════════════════
set -euo pipefail
cd "$(dirname "$0")"

# ★ 找 dotnet：DOTNET_ROOT → PATH → 常见安装位置
DOTNET=""
if [ -n "${DOTNET_ROOT:-}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
    DOTNET="$DOTNET_ROOT/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
elif [ -x "/c/Program Files/dotnet/dotnet" ]; then
    DOTNET="/c/Program Files/dotnet/dotnet"
fi
if [ -z "$DOTNET" ]; then
    echo "[失败] 找不到 dotnet —— 请安装 .NET 8 SDK，或设置 DOTNET_ROOT 后重试。" >&2
    exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

PROJ="src/Hgvd.Avalonia/Hgvd.Avalonia.csproj"
OUT="dist"
VER="1.1.0"

if [ "${1:-}" = "selftest" ]; then
    echo "[自检] 无头跑一遍收藏夹 / 分集 / 播放地址"
    "$DOTNET" run --project "$PROJ" -c Debug --no-restore -- --selftest
    exit 0
fi

echo "[1/3] 还原依赖（走 nuget.azure.cn 镜像）"
"$DOTNET" restore "$PROJ" --configfile "./NuGet.config" -r win-x64

echo "[2/3] 发布 Release"
"$DOTNET" publish "$PROJ" -c Release --no-restore -o "$OUT" -p:PkgVersion="$VER"

echo "[3/3] 产物"
ls -la "$OUT"
echo
echo "完成：$PWD/$OUT/hgvd-downloader-v$VER.exe"
