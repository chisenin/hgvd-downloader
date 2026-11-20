#!/usr/bin/env python
"""升版本号（唯一来源的三处一起改），避免「exe 名新、界面旧」或「标签与仓库不一致」。

用法：
    python tools/bump_version.py 1.4.0

改动：
    hgvd-avalonia/build.cmd                  set "VER=x.y.z"
    hgvd-avalonia/build.sh                   VER="x.y.z"
    hgvd-avalonia/src/Hgvd.Avalonia/*.csproj <PkgVersion ...>x.y.z</PkgVersion>

★ 保留原换行：build.cmd 必须是 CRLF（cmd.exe 读 LF-only 会错行）。
★ 文档里的版本示例不会被自动改：历史叙述（如「v1.3.0 不含兴趣岛」）必须保留，
  改完自己 grep 一遍确认。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

TARGETS = {
    "hgvd-avalonia/build.cmd": r'set "VER=[^"]+"',
    "hgvd-avalonia/build.sh": r'^VER="[^"]+"',
    "hgvd-avalonia/src/Hgvd.Avalonia/Hgvd.Avalonia.csproj":
        r"""<PkgVersion Condition="'\$\(PkgVersion\)' == ''">[^<]+</PkgVersion>""",
}


def replacement(path: str, ver: str) -> str:
    if path.endswith(".cmd"):
        return f'set "VER={ver}"'
    if path.endswith(".sh"):
        return f'VER="{ver}"'
    return ("""<PkgVersion Condition="'$(PkgVersion)' == ''">"""
            + ver + "</PkgVersion>")


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__.strip().splitlines()[2].strip(), file=sys.stderr)
        return 2
    ver = sys.argv[1].strip().lstrip("vV")
    if not re.fullmatch(r"\d+\.\d+\.\d+", ver):
        print(f"[升版本] 版本号要写成 x.y.z（当前：{sys.argv[1]}）", file=sys.stderr)
        return 2

    old: str | None = None
    for path, pat in TARGETS.items():
        full = ROOT / path
        with open(full, encoding="utf-8", newline="") as f:
            text = f.read()
        m = re.search(pat, text, re.M)
        if not m:
            print(f"[升版本] 在 {path} 里找不到版本号，已中止（未改动任何文件）", file=sys.stderr)
            return 1
        if old is None:
            old = re.search(r"\d+\.\d+\.\d+", m.group(0)).group(0)
        if len(re.findall(pat, text, re.M)) != 1:
            print(f"[升版本] {path} 里版本号出现多次，已中止（请手工确认）", file=sys.stderr)
            return 1
        text = re.sub(pat, replacement(path, ver), text, count=1, flags=re.M)
        with open(full, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        print(f"  ✓ {path}")

    print(f"[升版本] {old} → {ver}")
    print("  下一步：")
    print("    1. 改 README / docs 里需要跟着变的版本示例（历史的别改）")
    print("    2. cd hgvd-avalonia && ./build.cmd verify")
    print(f"    3. bash tools/publish_release.sh v{ver}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
