#!/bin/bash
# ★ 发版：打标签并推送 → 触发 GitHub Actions 的 release 工作流
#
# 用法：
#     python tools/bump_version.py 1.4.0      # ★ 先升版本（改 build.cmd/build.sh/csproj）
#     bash tools/publish_release.sh v1.4.0
#     bash tools/publish_release.sh v1.4.1
#
# 前提：仓库已建好（https://github.com/chisenin/hgvd-downloader），
#       且 origin 指向 git@github.com:chisenin/hgvd-downloader.git
#       ★ 并且最近 24 小时内跑过 `cd hgvd-avalonia && ./build.sh verify`
#         （界面自检 + 端到端真下载，见下面的门禁）
#
# 流程：
#   0. 门禁：版本号三处一致 + 标签对得上（不同步就拒绝）；最近跑过 verify
#   1. 同步 main
#   2. 打带注释的标签（版本号 = 标签去掉前缀 v）
#   3. 推 main + 标签
#   4. release.yml 被标签触发 ⇒ 构建自包含单文件 exe ⇒ 建 GitHub Release
#
# 结果看：https://github.com/chisenin/hgvd-downloader/releases
set -euo pipefail

TAG="${1:-}"
if [ -z "$TAG" ]; then
    echo "用法: bash tools/publish_release.sh v1.4.0" >&2
    exit 2
fi
case "$TAG" in
    v*) ;;
    *) echo "标签建议带 v 前缀（如 v1.4.0），当前是：$TAG" >&2; exit 2 ;;
esac

cd "$(dirname "$0")/.."

# ══════════════════════════════════════════════════════════════
#  ★★ 版本门禁：标签必须与仓库版本一致
#
#  版本号有三处副本（build.cmd / build.sh / csproj）。只改一处、
#  或者忘了升版本就打标签，会出现「exe 名是新版、界面显示旧版」
#  「同一个标签两次构建产物不同」这类无法回溯的问题。
#  ⇒ 不一致直接拒绝，先跑 python tools/bump_version.py <x.y.z>
# ══════════════════════════════════════════════════════════════
echo ">>> 版本门禁"
if ! python tools/check_version.py "$TAG"; then
    echo "[拒绝发版] 标签与仓库版本不一致。" >&2
    echo "           要发新版本：python tools/bump_version.py ${TAG#v}，提交后再发布；" >&2
    echo "           标签填错了：换一个与仓库版本一致的标签。" >&2
    exit 4
fi
echo

# ══════════════════════════════════════════════════════════════
#  ★★ 发版门禁：最近 24 小时内必须跑过「界面自检 + 端到端真下载」
#
#  为什么卡在这里：发行版开了 IL 裁剪（PublishTrimmed），它的破坏是**静默**的
#  —— 编译、启动、开窗、读收藏夹全都正常，只有点到某个功能才炸。
#  GitHub Actions 跑不了这两步（需要真实登录态和收藏夹），所以只能本地卡。
#  跳过门禁的办法只有两个，都不建议：
#    HGVD_SKIP_VERIFY=1 bash tools/publish_release.sh vX.Y.Z
#    或者手工 git tag + git push
# ══════════════════════════════════════════════════════════════
if [ "${HGVD_SKIP_VERIFY:-0}" != "1" ]; then
    echo ">>> 发版门禁：界面自检 + 端到端下载"
    LOG_DIR="${HOME}/.hgv_tool"

    check_log () {
        local file="$1" must="$2" mustnot="$3" label="$4"
        local path="${LOG_DIR}/${file}"
        if [ ! -f "$path" ]; then
            echo "[拒绝发版] 找不到「${label}」的日志：$path" >&2
            echo "           先跑：cd hgvd-avalonia && ./build.sh verify" >&2
            return 1
        fi
        if [ -z "$(find "$path" -newermt "-24 hours" -print -quit 2>/dev/null)" ]; then
            echo "[拒绝发版] 「${label}」的日志超过 24 小时了：$path" >&2
            echo "           先跑：cd hgvd-avalonia && ./build.sh verify" >&2
            return 1
        fi
        if ! grep -q "$must" "$path"; then
            echo "[拒绝发版] 「${label}」没通过（日志里找不到「${must}」）：$path" >&2
            return 1
        fi
        if [ -n "$mustnot" ] && grep -q "$mustnot" "$path"; then
            echo "[拒绝发版] 「${label}」没通过（日志里出现「${mustnot}」）：$path" >&2
            return 1
        fi
        echo "  ✓ ${label} — $(tail -n 1 "$path")"
    }

    check_log "uitest.log"    "=== fail=0 ===" ""          "界面自检"
    check_log "batchtest.log" "=== ok="      "=== ok=0"   "端到端下载"
    echo
else
    echo "！ 已跳过发版门禁（HGVD_SKIP_VERIFY=1）—— 出事别怪脚本没提醒。" >&2
fi

echo ">>> 当前分支与工作区"
git status --short
git rev-parse --abbrev-ref HEAD

echo ">>> 同步 main"
git checkout main
git pull --ff-only origin main

echo ">>> 打标签 $TAG"
if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "标签 $TAG 已存在（$(git rev-parse --short "$TAG")）—— 若要重打先删：git tag -d $TAG" >&2
    exit 3
fi
git tag -a "$TAG" -m "发布 $TAG"

echo ">>> 推送"
git push origin main
git push origin "$TAG"

echo
echo "✓ 已推送标签 $TAG"
echo "  工作流：https://github.com/chisenin/hgvd-downloader/actions"
echo "  Release：https://github.com/chisenin/hgvd-downloader/releases"
