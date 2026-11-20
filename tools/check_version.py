#!/usr/bin/env python
"""校验版本号三处一致（build.cmd / build.sh / csproj），可再核对发布标签。

用法：
    python tools/check_version.py            # 只查三处是否一致
    python tools/check_version.py v1.4.0     # 再核对标签（去掉前缀 v）

退出码：0 = 一致；1 = 不一致（打印差异）。
被 publish_release.sh 与 release.yml 共用 —— 保证「标签 = 产物 = 界面显示」。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

CMD = "hgvd-avalonia/build.cmd"
SH = "hgvd-avalonia/build.sh"
CSPROJ = "hgvd-avalonia/src/Hgvd.Avalonia/Hgvd.Avalonia.csproj"

PATTERNS = {
    CMD: r'set "VER=([^"]+)"',
    SH: r'^VER="([^"]+)"',
    CSPROJ: r"""<PkgVersion Condition="'\$\(PkgVersion\)' == ''">([^<]+)</PkgVersion>""",
}


def read(path: str) -> str:
    # newline="" 保留原换行（build.cmd 必须是 CRLF）
    with open(ROOT / path, encoding="utf-8", newline="") as f:
        return f.read()


def main() -> int:
    found: dict[str, str] = {}
    for path, pat in PATTERNS.items():
        m = re.search(pat, read(path), re.M)
        if not m:
            print(f"[版本校验] 在 {path} 里找不到版本号（模式：{pat}）", file=sys.stderr)
            return 1
        found[path] = m.group(1).strip()

    values = set(found.values())
    if len(values) != 1:
        print("[版本校验] 三处版本号不一致：", file=sys.stderr)
        for path, ver in found.items():
            print(f"    {ver:<8} {path}", file=sys.stderr)
        print("  修：python tools/bump_version.py <x.y.z>", file=sys.stderr)
        return 1

    ver = values.pop()
    print(f"[版本校验] 仓库版本 {ver}（build.cmd / build.sh / csproj 一致）")

    if len(sys.argv) > 1:
        want = sys.argv[1].strip().lstrip("vV")
        if want != ver:
            def key(s: str) -> tuple:
                return tuple(int(x) for x in s.split("."))

            if key(want) > key(ver):
                print(
                    f"[版本校验] 标签 {sys.argv[1]} 比仓库版本 {ver} 新。\n"
                    f"  先把仓库升到该版本：python tools/bump_version.py {want}\n"
                    f"  提交改动后再发布。",
                    file=sys.stderr,
                )
            else:
                print(
                    f"[版本校验] 标签 {sys.argv[1]} 比仓库版本 {ver} 旧。\n"
                    f"  换一个新标签（已发布的版本号不能复用）；\n"
                    f"  若确定要回退仓库版本，请手工改三处并说明原因。",
                    file=sys.stderr,
                )
            return 1
        print(f"[版本校验] 标签 {sys.argv[1]} 与仓库版本一致")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
