"""判定 video_model 当前空 body 是「限流窗口」还是「端点/参数失效」。

对照实验：
  A 组：同一 vid 连续 4 次、间隔 5s
  B 组：不同 vid，间隔 15s
  C 组：完全免签名的收藏夹接口（基线，验证账号/cookie 仍有效）
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from hgvd.api import playback as pb
from hgvd.api import shelf
from hgvd.service import HongguoService

svc = HongguoService()
ident = svc.device_identity()
cookies = svc.cookie_dict()
print(f"device_id={ident.device_id} iid={ident.iid}")
print(f"cookie: sessionid={'有' if cookies.get('sessionid') else '无'} "
      f"install_id={cookies.get('install_id')}")

# ---------- C 组：免签名收藏夹（基线） ----------
try:
    raw = shelf.list_shelf_raw(svc.session, cookies)
    items = raw.get("data", {}).get("video_shelf_info", []) or []
    print(f"[C 基线] 收藏夹 code={raw.get('code')} items={len(items)}")
except Exception as e:
    print(f"[C 基线] 失败 {str(e)[:120]}")
    items = []

# ---------- 候选 vid ----------
CANDS = [
    ("植物科普#1", "7662736695145548824"),
    ("植物科普#2", "7700851630874315800"),
]
for it in items[:4]:
    CANDS.append((str(it.get("series_title", "?"))[:18], it.get("series_id")))


def try_one(vid, vtype, tag):
    try:
        payload = pb.fetch_playback(
            svc.session, ident, vid, cookies=cookies,
            video_id_type=vtype, retries=1, pause=0.1,
        )
        streams = pb.parse_streams(payload)
        if not streams:
            print(f"  [{tag}] {vid} → 空 streams")
            return False
        s = streams[0]
        print(f"  [{tag}] {vid} → ✓ {len(streams)} 档, "
              f"{s['definition']} {s['width']}x{s['height']} {s['size']}B enc={s['encrypted']}")
        return True
    except Exception as e:
        print(f"  [{tag}] {vid} → ✗ {str(e)[:90]}")
        return False


print("\n[A 组] 同一 vid 连续 4 次，间隔 5s")
ok = 0
for i in range(4):
    ok += 1 if try_one("7700851630874315800", 1, f"A{i+1}") else 0
    if i < 3:
        time.sleep(5)

print("\n[B 组] 换 vid，间隔 15s")
for name, vid in CANDS:
    if not vid:
        continue
    print(f"  —— {name}")
    try_one(vid, 1, "B")
    time.sleep(15)

print(f"\n结论：A 组成功 {ok}/4")
