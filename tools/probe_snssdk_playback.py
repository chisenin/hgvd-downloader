"""验证 reading.snssdk.com 网关返回的播放模型是否真实可用（并尝试下载）。

★ 背景（2026-11-14 17:0x 定位）：
    ``api5-normal-sinfonlinea.fqnovel.com`` 等 fqnovel 网关会**静默丢弃**
    ``/novel/player/video_model/v1/`` 请求（HTTP 200 + 空 body），
    但 ``reading.snssdk.com`` 上同一个接口、同一个签名能正常返回
    （code=0，含各清晰度直链）。

用法：
    python -m tools.probe_snssdk_playback --vid 7700851630874315800 --type 1 --download
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

from hgvd import net
from hgvd.api import playback as pb
from hgvd.auth import load_account
from hgvd.device import ensure_device

HOST = "reading.snssdk.com"


def fetch(vid: str, vtype: int, host: str = HOST) -> dict | None:
    ident = ensure_device()
    acc = load_account()
    cookies = dict(acc.cookies) if acc.ok else {}
    session = net.make_session()

    body = pb.build_playback_body(vid, "", video_id_type=vtype)
    now_ms = int(time.time() * 1000)
    query, headers = pb.sign_playback(
        pb.PATH_VIDEO_MODEL, pb.device_pairs(ident), now_ms, ident.device_id,
        ident.version_name, body, install_id=ident.iid, aid="8662",
    )
    hdr = dict(headers)
    hdr.update(
        {
            "User-Agent": pb.APP_UA,
            "X-SS-DP": "8662",
            "x-xs-from-web": "0",
            "Content-Type": "application/x-protobuf; charset=UTF-8",
            "x-reading-request": f"{now_ms}-0",
            "lc": "101",
            "Accept": "application/json; charset=utf-8,application/x-protobuf",
        }
    )
    if cookies:
        hdr["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)

    r = session.post(f"https://{host}{pb.PATH_VIDEO_MODEL}?{query}",
                     data=body, headers=hdr, timeout=25)
    print(f"HTTP {r.status_code} len={len(r.content)}")
    if not r.content:
        return None
    j = r.json()
    print(f"code={j.get('code')} message={j.get('message')}")
    Path("probe_snssdk_playback.json").write_text(
        json.dumps(j, ensure_ascii=False, indent=1), encoding="utf-8")
    print("已存 probe_snssdk_playback.json")
    return j


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--vid", default="7700851630874315800")
    ap.add_argument("--type", type=int, default=1)
    ap.add_argument("--host", default=HOST)
    ap.add_argument("--download", action="store_true")
    ap.add_argument("--quality", default="1080p")
    ap.add_argument("--out", default="")
    args = ap.parse_args()

    j = fetch(args.vid, args.type, args.host)
    if not j:
        return 1
    streams = pb.parse_streams(j)
    print(f"\n清晰度 {len(streams)} 条：")
    for s in streams:
        print(f"  {s['definition']:>7} {s['codec']:>8} {s['width']}x{s['height']} "
              f"size={s['size']:>10} enc={s['encrypted']} dur={s['duration']}")
        print(f"          main   = {s['url'][:100]}")
        if s['backup_url']:
            print(f"          backup = {s['backup_url'][:100]}")

    if not args.download:
        return 0

    target = None
    for s in streams:
        if s["definition"] == args.quality:
            target = s
            break
    if target is None:
        target = streams[0] if streams else None
    if target is None:
        print("没有可用清晰度")
        return 1

    out = Path(args.out or f"probe_{args.vid}_{target['definition']}.mp4")
    session = net.make_session()
    print(f"\n下载 {target['definition']} → {out}")
    with session.get(target["url"], stream=True, timeout=60) as r:
        print(f"  HTTP {r.status_code}")
        r.raise_for_status()
        total = 0
        with open(out, "wb") as f:
            for chunk in r.iter_content(1 << 18):
                if chunk:
                    f.write(chunk)
                    total += len(chunk)
    print(f"  落盘 {total} 字节 → {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
