#!/usr/bin/env python
"""文档链自检：检查仓库里所有 Markdown 的相对链接是否指向存在的文件。

为什么要有：
    2026-11-22 收回逆向留档时，公开文档里有 9 处指向已移除的 docs/05 / 06 / 07，
    README、NOTICE、docs/01、docs/03、代码注释各挂一份 —— 链接坏了但没人发现，
    因为 C 端的渲染不会报错。这个脚本把「链接纪律」变成可执行的检查。

用法::
    python tools/check_docs_links.py           # 只列错误；有错返回 1
    python tools/check_docs_links.py -v        # 连通过的链接也打出来

检查范围：
    README.md、NOTICE.md、docs/*.md（含本地留档文档，它们也在本机存在）

不查：
    外链（http/https）、锚点（#section）、mailto、图片外链。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# [文字](目标) —— 目标里可能有空格与尖括号
LINK = re.compile(r"\[[^\]]*\]\(<?([^)>\s]+)>?\)?")


def md_files() -> list[Path]:
    out = [ROOT / "README.md", ROOT / "NOTICE.md"]
    out += sorted((ROOT / "docs").glob("*.md"))
    return [p for p in out if p.is_file()]


def check(verbose: bool) -> int:
    bad = 0
    total = 0
    for f in md_files():
        text = f.read_text(encoding="utf-8")
        for target in LINK.findall(text):
            if re.match(r"^(https?|mailto|#)", target):
                continue
            if target.startswith("mailto"):
                continue
            rel = target.split("#")[0]
            if not rel:
                continue
            total += 1
            dest = (f.parent / rel).resolve()
            if dest.exists():
                if verbose:
                    print(f"  ok   {f.relative_to(ROOT)} -> {rel}")
            else:
                bad += 1
                print(f"  ✗    {f.relative_to(ROOT)} -> {rel}")
    print(f"\n链接 {total} 条，失效 {bad} 条")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(check("-v" in sys.argv or "--verbose" in sys.argv))
