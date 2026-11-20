"""``video_model`` 专项诊断：为什么只有这个端点返回空 body。

判据纪律（docs/03-开发要点.md §1.0）
------------------------------------
空 body **不是限流**。本脚本要回答的是「请求在哪儿被丢弃的」：

1. **路径还在吗** —— 同一个 vid，换路径变体各打一枪：
   - 返回 ``404``  ⇒ 路径不存在（端点迁移了）
   - 返回 ``200 + 0 字节`` ⇒ 路径存在，但请求被**静默丢弃**
2. **是业务前还是业务后被丢** —— 用**不存在的 vid** 打一枪：
   - 若仍空 body ⇒ 在业务校验**之前**就丢了（签名/策略层面）
   - 若返回 ``code≠0`` ⇒ 请求进到了业务层（那问题就在参数/内容）
3. **是不是这个设备的身份被针对** —— 换一套临时设备身份（不写盘）再打。

用法::

    python -m tools.playback_diag 7709143158319221785
    python -m tools.playback_diag 7709143158319221785 --vtype 1
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

from hgvd.api.detail import UA as APP_UA  # noqa: E402
from hgvd.api.detail import device_pairs  # noqa: E402
from hgvd.api.playback import build_playback_body  # noqa: E402
from hgvd.sign.core import sign_playback  # noqa: E402
from hgvd.service import HongguoService  # noqa: E402

HOSTS = ("reading.snssdk.com", "api5-normal-sinfonlinea.fqnovel.com")

PATH_VARIANTS = (
    "/novel/player/video_model/v1/",
    "/novel/player/video_model/v2/",
    "/novel/player/video_model/",
    "/novel/player/video_model/v1",
    "/novel/player/video_model/preload/v1",
    "/novel/player/video_model/v1/play",
)


def shoot(session, ident, cookie: str, body: bytes, host: str, path: str,
          timeout: int = 20):
    """打一枪，返回 (status, size, code_or_None, snippet)。"""
    now = int(time.time() * 1000)
    query, headers = sign_playback(
        path, device_pairs(ident), now, ident.device_id, ident.version_name,
        body, install_id=ident.iid, aid="8662")
    hdr = dict(headers)
    hdr.update({
        "User-Agent": APP_UA,
        "X-SS-DP": "8662",
        "x-xs-from-web": "0",
        "Content-Type": "application/x-protobuf; charset=UTF-8",
        "x-reading-request": f"{now}-0",
        "lc": "101",
        "Accept": "application/json; charset=utf-8,application/x-protobuf",
    })
    if cookie:
        hdr["Cookie"] = cookie
    try:
        r = session.post(f"https://{host}{path}?{query}", data=body,
                         headers=hdr, timeout=timeout)
    except Exception as e:  # noqa: BLE001
        return -1, 0, None, f"网络错误 {str(e)[:50]}"
    n = len(r.content)
    code = None
    snip = ""
    if n:
        try:
            j = json.loads(r.content)
            code = j.get("code")
            snip = str(j.get("message") or j.get("debug_info") or "")[:40]
        except Exception:  # noqa: BLE001
            snip = r.text[:40].replace("\n", " ")
    return r.status_code, n, code, snip


def main() -> int:
    ap = argparse.ArgumentParser(description="video_model 专项诊断")
    ap.add_argument("video_id")
    ap.add_argument("--vtype", type=int, default=0, choices=(0, 1))
    args = ap.parse_args()

    svc = HongguoService()
    if not svc.logged_in:
        print("未登录", file=sys.stderr)
        return 2
    ident, ck = svc.device_identity(), svc.cookie_dict()
    cookie = "; ".join(f"{k}={v}" for k, v in ck.items() if v)

    sess = svc.session

    body = build_playback_body(args.video_id, video_id_type=args.vtype)
    print(f"video_id={args.video_id} vtype={args.vtype} body={len(body)}B "
          f"device={ident.device_id}")
    print("=" * 86)
    print("① 路径变体（同一份 body，看 404 还是空 body）")
    for path in PATH_VARIANTS:
        status, n, code, snip = shoot(sess, ident, cookie, body, HOSTS[0], path)
        tag = ("EMPTY" if n == 0 else f"JSON code={code}")
        print(f"   {str(status):<4} {n:>5}B  {tag:<18} {path:<44} {snip}")

    print("=" * 86)
    print("② 不存在的 vid（判断是否在业务校验前就被丢）")
    bogus = build_playback_body("1234567890123456789", video_id_type=args.vtype)
    for host in HOSTS:
        status, n, code, snip = shoot(sess, ident, cookie, bogus, host, PATH_VARIANTS[0])
        tag = ("EMPTY" if n == 0 else f"JSON code={code}")
        print(f"   {str(status):<4} {n:>5}B  {tag:<18} {host:<40} {snip}")

    print("=" * 86)
    print("③ body 变体（当前 vid，看是否缺字段）")
    variants = {
        "当前": body,
        "加 scene=feed": build_playback_body(args.video_id, video_id_type=args.vtype),
        "video_platform=1": None,
    }
    # 手搓一个 video_platform=1 的 body
    variants["video_platform=1"] = json.dumps({
        "biz_param": {"need_all_video_definition": True,
                      "video_id_type": args.vtype, "video_platform": 1},
        "video_id": args.video_id,
    }, separators=(",", ":")).encode()
    variants["不带 biz_param"] = json.dumps(
        {"video_id": args.video_id}, separators=(",", ":")).encode()

    for name, b in variants.items():
        for host in HOSTS:
            status, n, code, snip = shoot(sess, ident, cookie, b, host, PATH_VARIANTS[0])
            tag = ("EMPTY" if n == 0 else f"JSON code={code}")
            print(f"   {str(status):<4} {n:>5}B  {tag:<18} {name:<18}@{host[:22]:<22} {snip}")
    print("=" * 86)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
