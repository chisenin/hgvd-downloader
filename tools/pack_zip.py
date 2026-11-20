#!/usr/bin/env python
"""把发布目录打成 zip（B 案：自包含目录版）。

用法：
    python tools/pack_zip.py <源目录> <输出.zip> [README 模板]

要点（每条都是踩过的坑）：
  1. **顶层必须保留文件夹名**。如果直接把目录里的文件平铺进 zip，用户
     「解压到当前目录」会把两百个 dll 撒到桌面 —— zip 分发的经典灾难。
  2. **包内文件名全用英文**。中文名在某些解压工具下会乱码；README 内容
     用中文没问题，但文件名必须是 README.txt 而不是「使用说明.txt」。
  3. **README.txt 写成 UTF-8 with BOM**。Windows 记事本对无 BOM 的 UTF-8
     在旧系统上会按 GBK 解释，中文全变乱码。
  4. deflate 压缩级别用 6：再高对 .NET 的 dll 收益极小，纯浪费时间。
"""
from __future__ import annotations

import os
import shutil
import sys
import zipfile

# 包内 README 的文件名（**必须英文**，见上面第 2 条）
README_NAME = "README.txt"


def build(src: str, out: str, readme: str | None) -> int:
    src = os.path.abspath(src)
    out = os.path.abspath(out)
    if not os.path.isdir(src):
        print(f"[zip] 源目录不存在：{src}", file=sys.stderr)
        return 1

    # ★ 顶层目录名 = 源目录名（用户解压后得到一个干净的文件夹）
    root = os.path.dirname(src)
    base = os.path.basename(src)

    # ★ 把 README 模板放进包里（转成 UTF-8 with BOM）
    if readme and os.path.isfile(readme):
        dst = os.path.join(src, README_NAME)
        with open(readme, "r", encoding="utf-8-sig") as f:
            text = f.read()
        with open(dst, "w", encoding="utf-8-sig", newline="\r\n") as f:
            f.write(text)
        print(f"[zip] 已放入 {README_NAME}（UTF-8 with BOM，CRLF）")

    if os.path.exists(out):
        os.remove(out)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    count = 0
    total = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for dirpath, _dirs, files in os.walk(src):
            for name in sorted(files):
                full = os.path.join(dirpath, name)
                arc = os.path.relpath(full, root).replace(os.sep, "/")
                z.write(full, arc)
                count += 1
                total += os.path.getsize(full)

    size = os.path.getsize(out) / 1048576
    print(f"[zip] {out}")
    print(f"[zip] {count} 个文件 · 原始 {total / 1048576:.1f} MB → 压缩后 {size:.1f} MB"
          f"（{total / 1048576 / size:.2f}x）")

    # 自检：顶层目录名必须在包里
    with zipfile.ZipFile(out) as z:
        names = z.namelist()
        if not any(n.startswith(base + "/") for n in names):
            print(f"[zip] ★ 警告：包内没有顶层目录 {base}/ —— 用户解压会撒一地", file=sys.stderr)
            return 1
        if README_NAME in [n.split("/", 1)[-1] for n in names if n.count("/") == 1]:
            print(f"[zip] ✓ 顶层目录 {base}/ 与 {README_NAME} 都在")
    return 0


def main() -> int:
    if len(sys.argv) < 3:
        print("用法: python tools/pack_zip.py <源目录> <输出.zip> [README 模板]",
              file=sys.stderr)
        return 2
    readme = sys.argv[3] if len(sys.argv) > 3 else None
    return build(sys.argv[1], sys.argv[2], readme)


if __name__ == "__main__":
    sys.exit(main())
