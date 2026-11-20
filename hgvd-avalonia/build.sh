#!/usr/bin/env bash
# ══════════════════════════════════════════════════════════════
#  红果视频下载器 —— C# / Avalonia UI 版 构建脚本（Git Bash / MSYS）
#
#  用法：./build.sh            构建 Release（输出到 ./dist 与 ./pkg）
#        ./build.sh verify     ★ 发版用：构建 + 界面自检 + 端到端真下载
#        ./build.sh selftest   只跑接口自检（Debug，不打包）
#
#  ★★ 发版形态：**B 案 —— 自包含目录版 + zip**（主推）
#      dist/hgvd-downloader-vX.Y.Z-win-x64.zip
#      解开就是一个文件夹：exe + 运行时 dll + ffmpeg/ + README.txt
#      同时仍产出单文件 exe 作备用（dist/hgvd-downloader-vX.Y.Z.exe）。
#      为什么主推 zip：单文件塞进 ffmpeg 后是"假单文件"（仍要解到
#      LOCALAPPDATA），且"exe 解出 exe"容易被杀软误判。
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
if ! command -v python >/dev/null 2>&1; then
    echo "[失败] 找不到 python —— 打 zip 需要它（tools/pack_zip.py）。" >&2
    exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

PROJ="src/Hgvd.Avalonia/Hgvd.Avalonia.csproj"
OUT="dist"
VER="1.3.0"
APP="hgvd-downloader-v$VER"
PKG="pkg/$APP"
ZIP="$OUT/$APP-win-x64.zip"
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

echo "[1/5] 准备内置 ffmpeg / ffprobe（已备好则秒过）"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "./prepare-ffmpeg.ps1"

echo "[2/5] 还原依赖（走 nuget.azure.cn 镜像）"
# ★ 必须带 -p:Configuration=Release —— PublishTrimmed 只在 Release 下开，
#   而 ILLink.Tasks 是 restore 阶段按 PublishTrimmed 决定下不下的。
#   restore 默认按 Debug 评估 ⇒ 漏了这个参数，publish 会静默不裁剪（89 MB vs 41 MB）。
"$DOTNET" restore "$PROJ" -p:Configuration=Release --configfile "./NuGet.config" -r win-x64

echo "[3/5] 发布目录版（B 案主推，输出到 $PKG）"
# ★ 目录版和单文件**必须分两次 publish**：PublishSingleFile 是 publish 期属性。
# ★ 先清空，否则上一次的残留文件会混进 zip（用 Python 删，绕开 bash 的 rm 钩子）。
python -c "import shutil,sys; shutil.rmtree(sys.argv[1], ignore_errors=True)" "$PKG" 2>/dev/null || true
"$DOTNET" publish "$PROJ" -c Release --no-restore -o "$PKG" -p:PublishSingleFile=false -p:PkgVersion="$VER"

echo "[4/5] 打 zip（顶层保留文件夹名 + 放入 README.txt）"
python ../tools/pack_zip.py "$PKG" "$ZIP" "./PACK-README.txt"

echo "[5/5] 发布单文件（备用形态，输出到 $OUT）"
"$DOTNET" publish "$PROJ" -c Release --no-restore -o "$OUT" -p:PkgVersion="$VER"

echo
echo "产物："
ls -la "$OUT"
echo
echo "  目录版：$PWD/$PKG/  （已打包成 $ZIP）"
echo "  单文件：$PWD/$OUT/$APP.exe"

if [ "$VERIFY" = "1" ]; then
    # ── 发版门禁：界面自检 + 端到端真下载 ──
    # ★ 验的是**目录版**（B 案主推的那个），不是单文件 ——
    #   两者是两次独立 publish，验主推形态才有意义。
    # ★ 两个开关都**不靠退出码**（WinExe 的 --uitest / --batchtest 恒返回 0），
    #   只能解析它们写出来的日志。先删旧日志，跑完再确认存在 ——
    #   否则会拿上一次的结果冒充通过。
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
    echo "[验证 2/2] 端到端真下载（1 部 · 最多 2 集）"
    rm -f "$LOGDIR/batchtest.log"
    "$EXE" --batchtest 1
    if [ ! -f "$LOGDIR/batchtest.log" ]; then
        echo "[失败] 批量测试没写出日志 —— 程序没起来，或启动就崩了。" >&2
        exit 1
    fi
    if grep -q "=== ok=0" "$LOGDIR/batchtest.log"; then
        echo "[失败] 一集都没下下来：" >&2
        cat "$LOGDIR/batchtest.log" >&2
        exit 1
    fi
    cat "$LOGDIR/batchtest.log"

    echo
    echo "═══ 验证通过：界面自检全绿 + 端到端下载成功 ═══"
    echo "    目录版：$PWD/$PKG/"
    echo "    压缩包：$PWD/$ZIP"
    echo "    可以发版了（bash tools/publish_release.sh v$VER）"
fi
