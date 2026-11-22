#!/usr/bin/env bash
# ══════════════════════════════════════════════════════════════
#  红果视频下载器 —— C# / Avalonia UI 版 构建脚本（Git Bash / MSYS）
#
#  用法：./build.sh            构建 Release（输出到 ./pkg 目录版）
#        ./build.sh verify     ★ 发版用：构建 + 界面自检 + 端到端真下载
#        ./build.sh selftest   只跑接口自检（Debug，不打包）
#
#  ★★ 发版形态（2026-11-21 用户拍板）：**本地只出目录版**
#      pkg/hgvd-downloader-vX.Y.Z/
#      解开就是一个文件夹：exe + 运行时 dll + ffmpeg/ + README.txt
#
#      ★ **本地不再打 zip、不再出单文件 exe**（用户 2026-11-21 决定）：
#        目录版启动快、ffmpeg 可替换、出问题能直接看目录里有什么；
#        单文件塞进 ffmpeg 后是"假单文件"（首次运行仍要解到 LOCALAPPDATA），
#        且"exe 解出 exe"容易被杀软误判。
#      ⇒ 本地自用直接跑 pkg/ 里的 exe 即可，不必再打包。
#      ★ **GitHub Release 的产物不受此影响**：CI 的 release.yml 仍照旧产出
#        zip + 单文件 exe（远端给公众下载的形态不变）。
#
#  ★★ 发版前必须跑 verify。IL 裁剪（PublishTrimmed）的破坏是**静默**的 ——
#     编译、启动、开窗、读收藏夹全都正常，只有点到某个功能才炸。
#     光"能构建"不算通过，必须界面自检全绿 + 真下一集。
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
VER="1.9.0"
APP="hgvd-downloader-v$VER"
PKG="pkg/$APP"
LOGDIR="$HOME/.hgv_tool"

MODE="${1:-build}"
VERIFY=0
case "$MODE" in
    selftest)
        echo "[自检] 无头跑一遍收藏夹 / 分集 / 播放地址"
        "$DOTNET" run --project "$PROJ" -c Debug --no-restore -- --selftest
        exit 0
        ;;
    verify) VERIFY=1 ;;
    build|"") VERIFY=0 ;;
    *) echo "用法: ./build.sh [verify|selftest]" >&2; exit 2 ;;
esac

echo "[1/3] 准备内置 ffmpeg / ffprobe（已备好则秒过）"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "./prepare-ffmpeg.ps1"

echo "[2/3] 还原依赖（走 nuget.azure.cn 镜像）"
# ★ 必须带 -p:Configuration=Release —— PublishTrimmed 只在 Release 下开，
#   而 ILLink.Tasks 是 restore 阶段按 PublishTrimmed 决定下不下的。
#   restore 默认按 Debug 评估 ⇒ 漏了这个参数，publish 会静默不裁剪（89 MB vs 41 MB）。
"$DOTNET" restore "$PROJ" -p:Configuration=Release --configfile "./NuGet.config" -r win-x64

echo "[3/3] 发布目录版（本地唯一产物形态，输出到 $PKG）"
# ★ 目录版用 PublishSingleFile=false —— 运行时 dll 摊在目录里，
#   启动快、出问题能直接看目录里有什么。
# ★★ EmbedFfmpeg=false：目录版不内嵌 ffmpeg，下面复制进 pkg 根目录；
#   内嵌只留给 CI 的单文件版。
# ★ 先清空，否则上一次的残留文件会混进产物（用 Python 删，绕开 bash 的 rm 钩子）。
python -c "import shutil,sys; shutil.rmtree(sys.argv[1], ignore_errors=True)" "$PKG" 2>/dev/null || true
"$DOTNET" publish "$PROJ" -c Release --no-restore -o "$PKG" -p:PublishSingleFile=false -p:EmbedFfmpeg=false -p:PkgVersion="$VER"

# ★ 把 ffmpeg/ffprobe 摊进目录版
for tool in ffmpeg ffprobe; do
    src="../.cache/ffmpeg/$tool.exe"
    if [ ! -f "$src" ]; then
        echo "[FAIL] $src not found -- run prepare-ffmpeg.ps1 first" >&2
        exit 1
    fi
    cp -f "$src" "$PKG/" || exit 1
done
echo "  ffmpeg/ffprobe copied into $PKG"

echo
echo "产物（本地唯一形态：目录版）："
echo "  $PWD/$PKG/"
echo
echo "  ★ 本地不再打 zip、不再出单文件 exe（2026-11-21 起）。"
echo "    直接跑上面目录里的 exe 即可。"
echo "    GitHub Release 仍照旧提供 zip + 单文件 exe（CI 构建，不受本地影响）。"

if [ "$VERIFY" = "1" ]; then
    # ── 发版门禁：界面自检 + 端到端真下载 ──
    # ★ 验的是**目录版**（本地唯一产物形态）。
    # ★ 自检开关都**不靠退出码**（WinExe 恒返回 0），只能解析它们写出来的日志。
    #   先删旧日志，跑完再确认存在 —— 否则会拿上一次的结果冒充通过。
    #
    # ★★ 2026-11-21 门禁改造：播放接口加固后，无人值守拿不到有效直链
    #    （直链带 100 秒时间戳，且必须有人在 App 里点播才产生 preload）。
    #    原 --batchtest 走老直连路径必然失败，已不适合作门禁。
    #    拆成两道本地可控的验证：
    #      1. --uitest    界面交互
    #      2. --dlprobe   下载引擎 + 命名 + ffprobe 校验（本地 http 小文件）
    #    抓流能力已于 2026-11-22 裁掉，原 TLS 证书链门禁一并撤销。
    EXE="$PKG/$APP.exe"

    echo
    echo "[验证 1/2] 界面自检"
    rm -f "$LOGDIR/uitest.log"
    "$EXE" --uitest
    if [ ! -f "$LOGDIR/uitest.log" ]; then
        echo "[失败] 自检没写出日志 —— 程序没起来，或启动就崩了。" >&2
        exit 1
    fi
    if ! grep -q "=== fail=0 ===" "$LOGDIR/uitest.log"; then
        echo "[失败] 界面自检有失败项：" >&2
        cat "$LOGDIR/uitest.log" >&2
        exit 1
    fi


    echo
    echo "[验证 2/2] 下载链路（本地小文件 → 下载 → ffprobe）"
    rm -f "$LOGDIR/dlprobe.log"
    "$EXE" --dlprobe
    if [ ! -f "$LOGDIR/dlprobe.log" ] || ! grep -q "=== ok=1 fail=0 ===" "$LOGDIR/dlprobe.log"; then
        echo "[失败] 下载链路门禁没过（或没写日志）：" >&2
        cat "$LOGDIR/dlprobe.log" >&2
        exit 1
    fi
    cat "$LOGDIR/dlprobe.log"

    echo
    echo "═══ 验证通过：界面自检 + 下载链路全绿 ═══"
    echo "    目录版：$PWD/$PKG/"
    echo "    可以发版了（bash tools/publish_release.sh v$VER）"
fi
