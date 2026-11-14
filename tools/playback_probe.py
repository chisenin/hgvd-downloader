"""播放接口原始响应探针 —— 用来区分「服务端风控」和「客户端实现差异」。

★ 直接打 ``video_model``，把 HTTP 状态、body 长度、响应头、JSON 错误码
  全部原样打印出来，不做任何吞异常的处理。
  —— 之前「空 body = 风控」是**推断**，这里要的是**原始证据**。

用法：
    python -m tools.playback_probe                       # 用默认 vid 测
    python -m tools.playback_probe --vid 7702405382206917657 --type 0
"""

from __future__ import annotations

import argparse
import json
import sys
import time

from hgvd import net
from hgvd.api import playback as playback_api
from hgvd.auth import load_account
from hgvd.device import ensure_device


def probe(vid: str, video_id_type: int, label: str = "", *, pause: float = 2.0) -> dict:
    """打一枪播放接口，返回诊断字典。"""
    ident = ensure_device()
    acc = load_account()
    cookies = dict(acc.cookies) if acc.ok else {}
    session = net.make_session()

    body = playback_api.build_playback_body(vid, "", video_id_type=video_id_type)
    now_ms = int(time.time() * 1000)
    pairs = playback_api.device_pairs(ident)
    query, headers = playback_api.sign_playback(
        playback_api.PATH_VIDEO_MODEL,
        pairs,
        now_ms,
        ident.device_id,
        ident.version_name,
        body,
        install_id=ident.iid,
        aid="8662",
    )

    hdr = dict(headers)
    hdr.update(
        {
            "User-Agent": playback_api.APP_UA,
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

    url = f"https://{playback_api.API_HOST}{playback_api.PATH_VIDEO_MODEL}?{query}"
    print(f"\n=== {label or vid} · video_id_type={video_id_type} ===")
    print(f"  host   : {playback_api.API_HOST}")
    print(f"  body   : {body.decode('utf-8')}")
    print(f"  cookie : {sorted(cookies)}")
    print(f"  sign   : {sorted(h for h in headers if h.lower().startswith('x-'))}")
    print(f"  签名头长度: " + ", ".join(
        f"{k}={len(v)}" for k, v in sorted(headers.items())
        if k.lower().startswith("x-") and isinstance(v, str)
    ))

    out = {"vid": vid, "video_id_type": video_id_type}
    try:
        r = session.post(url, data=body, headers=hdr, timeout=25)
    except Exception as e:  # noqa: BLE001
        print(f"  请求异常: {type(e).__name__}: {e}")
        out["error"] = str(e)
        return out

    print(f"  HTTP   : {r.status_code} {r.reason}")
    print(f"  len    : {len(r.content)} 字节")
    interesting = {
        k: v for k, v in r.headers.items()
        if k.lower() in (
            "content-type", "content-length", "x-tt-logid", "x-tt-env",
            "server", "date", "x-passport-compliance-force-refresh",
            "x-tt-trace-id", "set-cookie", "x-ss-stub",
        )
    }
    for k, v in interesting.items():
        print(f"  hdr    : {k}: {v[:160]}")

    if not r.content:
        print("  >>> 空 body <<<")
        out["result"] = "empty"
        return out

    try:
        j = r.json()
    except Exception:  # noqa: BLE001
        print(f"  >>> 非 JSON: {r.content[:200]!r}")
        out["result"] = "non-json"
        return out

    code = j.get("code")
    msg = j.get("message") or j.get("msg")
    print(f"  code   : {code}  message={msg!r}")
    streams = playback_api.parse_streams(j)
    if streams:
        print(f"  >>> 成功：{len(streams)} 档清晰度")
        for s in streams:
            print(
                f"      {s['definition']:>6} {s['width']}x{s['height']} "
                f"{s['codec']} size={s['size']} dur={s['duration']}s"
            )
        out["result"] = "ok"
        out["streams"] = len(streams)
    else:
        print("  >>> 有 JSON 但没解析出流")
        out["result"] = "no-streams"
        out["code"] = code
    return out


def main(argv=None):
    p = argparse.ArgumentParser(description="播放接口原始响应探针")
    p.add_argument("--vid", default="7702405382206917657")
    p.add_argument("--type", type=int, default=0)
    p.add_argument("--both", action="store_true", help="两种 video_id_type 都试")
    p.add_argument("--pause", type=float, default=3.0)
    args = p.parse_args(argv)

    types = [0, 1] if args.both else [args.type]
    results = []
    for t in types:
        results.append(probe(args.vid, t, pause=args.pause))
        if args.both and t == 0:
            time.sleep(args.pause)
    print("\n=== 汇总 ===")
    for r in results:
        print(f"  {r.get('vid')} type={r.get('video_id_type')} → {r.get('result')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
