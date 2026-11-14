"""签名头逐项替换 —— 把 C# 请求里的 4 个签名头逐个换成 Python 生成的版本。

★ 思路：抓到 C# 的原始请求（query / body / 头全部固定），
  然后**只**把某一个签名头替换成 Python 用**同一份 query** 算出来的值，
  再发出去：
      - 还是空 body → 这个头不是元凶
      - 变成正常响应 → **就是这个头算错了**

★ 前提：Python 用同一个 now_millis 走一遍签名，得到的 query 与 C# 一致
  （ticket 偏移是确定性的，与随机熵无关）。

用法：
    python -m tools.swap_sig --cap _cap2.json --index 0
"""

from __future__ import annotations

import argparse
import json
import sys
import time

from hgvd import net
from hgvd.api import playback as pb
from hgvd.auth import load_account
from hgvd.device import ensure_device

REAL_HOST = "api5-normal-sinfonlinea.fqnovel.com"
SIG_HEADERS = ["x-argus", "x-ladon", "x-gorgon", "x-helios"]


def py_sign_for(cap: dict):
    """用 Python 实现，对 C# 那份 query 重新签名。"""
    import urllib.parse
    q = urllib.parse.parse_qs(cap["query"])
    ticket = q["_rticket"][0]
    now_ms = int(ticket)
    ident = ensure_device()
    body = bytes.fromhex(cap["body_hex"])
    query, headers = pb.sign_playback(
        cap["path"], pb.device_pairs(ident), now_ms,
        ident.device_id, ident.version_name, body,
        install_id=ident.iid, aid="8662",
    )
    return query, headers


def send(cap: dict, override: dict, label: str) -> bool:
    hdrs = []
    skip = {k.lower() for k in override}
    for k, v in cap["headers"]:
        if k.lower() in ("host", "content-length"):
            continue
        if k.lower() in skip:
            continue
        hdrs.append((k, v))
    for k, v in override.items():
        hdrs.append((k, v))
    url = f"https://{REAL_HOST}{cap['path']}?{cap['query']}"
    s = net.make_session()
    s.trust_env = False
    s.headers.clear()
    for k, v in hdrs:
        s.headers[k] = v
    body = bytes.fromhex(cap["body_hex"])
    try:
        r = s.post(url, data=body, timeout=25)
    except Exception as e:  # noqa: BLE001
        print(f"  [{label}] 异常：{e}")
        return False
    ok = len(r.content) > 0
    extra = ""
    if ok:
        try:
            j = r.json()
            extra = f" code={j.get('code')} 档数={len(pb.parse_streams(j))}"
        except Exception:  # noqa: BLE001
            extra = f" {r.content[:60]!r}"
    print(f"  [{label}] HTTP {r.status_code} · {len(r.content)} 字节 → "
          f"{'成功 ✓' if ok else '空 body ✗'}{extra}")
    return ok


def main(argv=None):
    p = argparse.ArgumentParser(description="签名头逐项替换定位")
    p.add_argument("--cap", default="_cap2.json")
    p.add_argument("--index", type=int, default=0)
    p.add_argument("--pause", type=float, default=5.0)
    args = p.parse_args(argv)

    caps = json.load(open(args.cap, encoding="utf-8"))
    plays = [c for c in caps if "video_model" in c["path"]]
    cap = plays[args.index]

    query_py, headers_py = py_sign_for(cap)
    print(f"Python 复算的 query 与 C# 一致？ {query_py == cap['query']}")
    if query_py != cap["query"]:
        print("  C# :", cap["query"][:120])
        print("  Py :", query_py[:120])

    print("\n[0] 原样重放（C# 的头）")
    send(cap, {}, "原样")

    print("\n[1] 全部 4 个签名头换成 Python 的")
    allfour = {k: headers_py[k] for k in SIG_HEADERS}
    send(cap, allfour, "全换")
    time.sleep(args.pause)

    for k in SIG_HEADERS:
        print(f"\n[2] 只换 {k}")
        send(cap, {k: headers_py[k]}, k)
        time.sleep(args.pause)
    return 0


if __name__ == "__main__":
    sys.exit(main())
