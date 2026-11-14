#!/bin/bash
# ★ 发版：打标签并推送 → 触发 GitHub Actions 的 release 工作流
#
# 用法：
#     bash tools/publish_release.sh v1.0.0
#     bash tools/publish_release.sh v1.0.1
#
# 前提：仓库已建好（https://github.com/chisenin/hgvd-downloader），
#       且 origin 指向 git@github.com:chisenin/hgvd-downloader.git
#
# 流程：
#   1. 同步 main
#   2. 打带注释的标签（版本号 = 标签去掉前缀 v）
#   3. 推 main + 标签
#   4. release.yml 被标签触发 ⇒ 构建自包含单文件 exe ⇒ 建 GitHub Release
#
# 结果看：https://github.com/chisenin/hgvd-downloader/releases
set -euo pipefail

TAG="${1:-}"
if [ -z "$TAG" ]; then
    echo "用法: bash tools/publish_release.sh v1.0.0" >&2
    exit 2
fi
case "$TAG" in
    v*) ;;
    *) echo "标签建议带 v 前缀（如 v1.0.0），当前是：$TAG" >&2; exit 2 ;;
esac

cd "$(dirname "$0")/.."

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
