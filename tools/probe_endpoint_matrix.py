"""播放端点矩阵探针 —— 定位「video_model 被丢弃」的边界。

★ 用途：把同一份签名请求打到
    - 不同 host（网关集群的不同入口）
    - 不同 path（video_model / multi_video_model / video_detail）
    - 不同 biz_param 组合
  看**哪一种组合还能拿到 JSON**。
  —— 拿到 JSON 就说明签名是通的（哪怕 code≠0），
     全是空 body 就说明请求在业务逻辑之前就被丢弃。

用法：
    python -m tools.probe_endpoint_matrix --vid 7700851630874315800 --type 1
"""

from __future__ import annotations

import argparse
import json
import time

from hgvd import net
from hgvd.api import playback as pb
from hgvd.auth import load_account
from hgvd.device import ensure_device

HOSTS = [
    "api5-normal-sinfonlinea.fqnovel.com",
    "api5-normal-sinfonlineb.fqnovel.com",
    "api5-normal-lq.fqnovel.com",
    "api5-normal-fqj.fqnovel.com",
    "reading.snssdk.com",
]

PATHS = [
    "/novel/player/video_model/v1/",
    "/novel/player/multi_video_model/player/v1/",
    "/novel/player/video_detail/v1/",
]


def _headers(headers: dict, now_ms: int, cookies: dict) -> dict:
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
    return hdr


def shot(session, ident, cookies, host, path, vid, vtype, label):
    body = pb.build_playback_body(vid, "", video_id_type=vtype)
    now_ms = int(time.time() * 1000)
    query, headers = pb.sign_playback(
        path, pb.device_pairs(ident), now_ms, ident.device_id,
        ident.version_name, body, install_id=ident.iid, aid="8662",
    )
    url = f"https://{host}{path}?{query}"
    hdr = _headers(headers, now_ms, cookies)
    try:
        r = session.post(url, data=body, headers=hdr, timeout=25)
    except Exception as e:  # noqa: BLE001
        print(f"  ✗ {label:52s} 异常 {type(e).__name__}: {str(e)[:60]}")
        return None
    n = len(r.content)
    if n:
        try:
            j = r.json()
            code = j.get("code")
            msg = str(j.get("message"))[:40]
            nstreams = len(pb.parse_streams(j))
            print(f"  ★ {label:52s} HTTP {r.status_code} len={n} code={code} 清晰度={nstreams} {msg}")
            return j
        except Exception:  # noqa: BLE001
            print(f"  ? {label:52s} HTTP {r.status_code} len={n} 非JSON {r.content[:50]!r}")
            return None
    print(f"  ✗ {label:52s} HTTP {r.status_code} len={n} 空 body")
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--vid", default="7700851630874315800")
    ap.add_argument("--type", type=int, default=1)
    ap.add_argument("--hosts", default="")
    ap.add_argument("--paths", default="")
    ap.add_argument("--pause", type=float, default=2.0)
    args = ap.parse_args()

    ident = ensure_device()
    acc = load_account()
    cookies = dict(acc.cookies) if acc.ok else {}
    session = net.make_session()
    hosts = args.hosts.split(",") if args.hosts else HOSTS
    paths = args.paths.split(",") if args.paths else PATHS

    print(f"vid={args.vid} type={args.type} cookie={sorted(cookies)}")
    print("\n--- 同 host 多 path ---")
    for path in paths:
        shot(session, ident, cookies, hosts[0], path, args.vid, args.type,
             f"{hosts[0]}{path}")
        time.sleep(args.pause)

    print("\n--- 同 path 多 host ---")
    for host in hosts:
        shot(session, ident, cookies, host, paths[0], args.vid, args.type,
             f"{host}{paths[0]}")
        time.sleep(args.pause)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
