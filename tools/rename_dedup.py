"""把「剧名重复两遍」的旧文件名改成去重后的名字。

★ 背景（2026-11-14）：成品文件名规则是
  ``<剧名>.<序号>.<分集标题>.mp4``，
  而很多教学合集的**分集标题本身就带剧名**
  （剧名《生物史前》、分集标题「《生物史前》 P1」），
  于是拼出来是「《生物史前》.001.《生物史前》 P1.mp4」—— 同一段文字重复两遍。
  新版下载器已经去掉重复前缀，这里把**已下载的旧文件**也一并改名。

★ 只改名，不删任何文件；重名冲突时**跳过**并报告。

用法：
    python -m tools.rename_dedup                    # 干跑，只列清单
    python -m tools.rename_dedup --apply            # 实际改名
    python -m tools.rename_dedup --dir D:\\某目录
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from hgvd.util import dedup_title, safe_name  # noqa: E402


def default_root() -> Path:
    """默认用配置里的下载目录。"""
    try:
        from hgvd.config import Config

        return Path(Config().resolve_out_dir())
    except Exception:
        return Path.home() / "Videos"


def plan_for(root: Path):
    """找出需要改名的文件，返回 [(旧路径, 新路径)]。"""
    changes = []
    if not root.exists():
        return changes
    for folder in sorted(p for p in root.iterdir() if p.is_dir()):
        series = folder.name
        for f in sorted(folder.glob("*.mp4")):
            stem = f.stem
            # 成品名格式：<剧名>.<NNN>.<集名>
            parts = stem.split(".")
            if len(parts) < 3:
                continue
            prefix, num, title = parts[0], parts[1], ".".join(parts[2:])
            # 只处理「前缀 == 剧名」且「集名又以剧名开头」的
            if prefix != series:
                continue
            new_title = dedup_title(series, title)
            if new_title == title:
                continue
            # ★ 去重后为空（标题就是剧名本身）→ 成品名不带集名部分
            new_name = (f"{prefix}.{num}.mp4" if not new_title
                        else f"{prefix}.{num}.{safe_name(new_title, 80)}.mp4")
            target = folder / new_name
            if target.exists() and target != f:
                changes.append((f, target, "目标已存在，跳过"))
                continue
            changes.append((f, target, "改名"))
    return changes


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description="去掉文件名里重复的剧名前缀")
    p.add_argument("--dir", default="", help="下载根目录（默认取配置里的）")
    p.add_argument("--apply", action="store_true", help="真正执行改名（默认只列清单）")
    args = p.parse_args(argv)

    root = Path(args.dir) if args.dir else default_root()
    print(f"扫描目录：{root}")
    changes = plan_for(root)
    if not changes:
        print("没有需要改名的文件。")
        return 0

    print(f"\n共 {len(changes)} 个文件：")
    for old, new, note in changes:
        print(f"  {old.name}")
        print(f"    → {new.name}   [{note}]")

    if not args.apply:
        print("\n（干跑，未改动任何文件；加 --apply 执行）")
        return 0

    done = skip = 0
    for old, new, note in changes:
        if note != "改名":
            skip += 1
            continue
        try:
            os.rename(old, new)
            done += 1
        except OSError as e:
            print(f"  改名失败：{old.name} —— {e}")
            skip += 1
    print(f"\n完成：改名 {done} 个，跳过 {skip} 个。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
