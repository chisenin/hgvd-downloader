"""空 body 的**范围**判据：同一时刻对多个带签名端点各打一枪，打印原始响应。

为什么需要它
------------
「HTTP 200 + 零字节」这个现象至少有三类成因，靠猜没有用，只能靠**同刻对照**：

1. **签名整体未被接受** —— 所有带签名端点一起空 body，
   而免签名的收藏夹接口照常返回（cookie 有效）。
2. **单个网关丢请求** —— 同一份签名同刻换 host 就通（见 ``playback_probe.py``）。
3. **单个端点/单组参数不对** —— 别的带签名端点返回 JSON（哪怕 code≠0）。

这三种的正确处置完全不同，所以**不要**把空 body 写成「限流」——
历史上三次误判（L/N/O/Q 章、X6 章、AC2 章）都是从这句推断开始的。

用法::

    python -m tools.emptybody_probe 7709143946680601624
    python -m tools.emptybody_probe 7709143946680601624 --vid 7662736695145548824
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

from hgvd.api import detail, shelf  # noqa: E402
from hgvd.api.detail import UA, device_pairs  # noqa: E402
from hgvd.sign.core import sign_playback, sign_request  # noqa: E402
from hgvd.service import HongguoService  # noqa: E402

HOSTS = ("reading.snssdk.com", "api5-normal-sinfonlinea.fqnovel.com")


def _post(session, ident, path, body: dict, cookies, host, playback: bool, timeout=20):
    raw = json.dumps(body, separators=(",", ":")).encode("utf-8")
    now = int(time.time() * 1000)
    pairs = device_pairs(ident)
    if playback:
        query, headers = sign_playback(
            path, pairs, now, ident.device_id, ident.version_name, raw,
            install_id=ident.iid, aid="8662")
    else:
        query, headers = sign_request(
            path, pairs, now, ident.device_id, ident.version_name, raw)
    hdr = {
        "User-Agent": UA,
        "Accept": "application/json",
        "X-SS-DP": "8662",
        "Content-Type": "application/json; charset=UTF-8",
        **headers,
    }
    if cookies:
        hdr["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)
    try:
        r = session.post(f"https://{host}{path}?{query}", data=raw,
                         headers=hdr, timeout=timeout)
    except Exception as e:  # noqa: BLE001
        return None, f"网络错误 {str(e)[:70]}"
    return r, ""


def main() -> int:
    ap = argparse.ArgumentParser(description="带签名端点空 body 范围判据")
    ap.add_argument("series_id", help="合集/短剧 series_id")
    ap.add_argument("--vid", default="", help="可选：单独一集的 vid（打 video_model）")
    ap.add_argument("--vtype", type=int, default=0, choices=(0, 1))
    args = ap.parse_args()

    svc = HongguoService()
    if not svc.logged_in:
        print("未登录：先 `python -m hgvd login`", file=sys.stderr)
        return 2
    ident, ck = svc.device_identity(), svc.cookie_dict()

    print("=" * 84)
    print("① 基线：免签名接口（验证 cookie / 账号本身是否有效）")
    try:
        raw = shelf.list_shelf_raw(svc.session, ck)
        items = (raw.get("data") or {}).get("video_shelf_info") or []
        print(f"   bookshelf/video/list/v  → code={raw.get('code')} "
              f"items={len(items)}  "
              f"{'✔ cookie 有效' if raw.get('code') == 0 else '✘ 登录态可疑'}")
    except Exception as e:  # noqa: BLE001
        print(f"   bookshelf 失败：{str(e)[:70]}")

    print("=" * 84)
    print("② 带签名端点（同刻、同凭据、同设备，逐 host 各打一枪）")
    cases = [
        (detail.PATH_DETAIL, {"series_id": args.series_id, "video_id_type": 0}, False),
        (detail.PATH_ALBUM, {"series_id": args.series_id, "video_id_type": 0}, False),
    ]
    if args.vid:
        cases.append((detail.PATH_PLAYBACK,
                      {"video_id": args.vid, "video_id_type": args.vtype}, True))

    verdict = []
    for host in HOSTS:
        for path, body, pb in cases:
            t0 = time.perf_counter()
            r, err = _post(svc.session, ident, path, body, ck, host, pb)
            ms = (time.perf_counter() - t0) * 1000
            tag = "playback" if pb else "normal"
            if r is None:
                print(f"   {host:<40} {path:<44} ERR  {err} {ms:.0f}ms")
                verdict.append(("err", path, host))
                continue
            n = len(r.content)
            if n == 0:
                print(f"   {host:<40} {path:<44} EMPTY 0B "
                      f"HTTP {r.status_code} {ms:.0f}ms")
                verdict.append(("empty", path, host))
                continue
            try:
                code = json.loads(r.content).get("code")
            except Exception:  # noqa: BLE001
                code = "非JSON"
            print(f"   {host:<40} {path:<44} JSON {n}B "
                  f"HTTP {r.status_code} code={code} {ms:.0f}ms  [{tag}]")
            verdict.append(("json", path, host))

    print("=" * 84)
    empties = [v for v in verdict if v[0] == "empty"]
    jsons = [v for v in verdict if v[0] == "json"]
    if not empties:
        print("结论：没有空 body。若播放仍失败，看 JSON 里的 code（参数问题）。")
    elif len(empties) == len(verdict):
        print("结论：**所有**带签名端点全空 body，而免签名接口正常")
        print("      ⇒ 签名整体未被接受（不是某个端点、不是频率、不是限流）")
        print("      下一步：核对签名参数（版本/时间戳/argus 版本），别再等退避。")
    elif jsons:
        print(f"结论：空 body 只出现在 {len(empties)}/{len(verdict)} 个组合上，"
              f"其余端点返回 JSON ⇒ 签名被接受")
        print("      ⇒ 是**特定网关**或**特定端点/参数**的问题，"
              "不是账号、不是频率。")
    else:
        print("结论：全部失败（含网络错），先看网络。")
    print("=" * 84)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
