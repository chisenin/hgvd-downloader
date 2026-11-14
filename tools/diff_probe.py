"""请求差分探针 —— 定位 C# 版「空 body」的真正差异。

★ 背景（2026-11-14 07:37~07:45 实测）：
    同一账号、同一设备、同一集 ``7700851630874315800``
      - Python 版（requests + urllib3/OpenSSL）→ **成功**，下载 29.1 MB
      - C# 版（HttpClient + SslStream）        → **空 body (HTTP 200)**
    ⇒ 这不是「账号级风控」，是**客户端请求本身**的问题。

★ 本探针用「同一份签名结果」，分别换 HTTP 栈 / 换头部形态发出去，
  用二分法定位到底是**哪一处差异**让服务端把响应变成空 body。

变体：
    A  Python 栈 + Python 头        （基线，预期成功）
    B  Python 栈 + C# 形态头         （只改头）
    C  curl 栈 + Python 头           （只改 HTTP/TLS 栈）
    D  curl 栈 + C# 形态头           （两者都改）

用法：
    python -m tools.diff_probe --vid 7700851630874315800 --type 1
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time

from hgvd import net
from hgvd.api import playback as playback_api
from hgvd.auth import load_account
from hgvd.device import ensure_device

CSHARP_UA = (
    "com.phoenix.read/73932 (Linux; U; Android 14; zh_CN; 23127PN0CC; "
    "Build/UKQ1.230804.001; Cronet/TTNetVersion:8d40f833)"
)


def build(vid: str, video_id_type: int):
    """造一份签名请求（Python 与 C# 共用同一套签名实现）。"""
    ident = ensure_device()
    acc = load_account()
    cookies = dict(acc.cookies) if acc.ok else {}
    body = playback_api.build_playback_body(vid, "", video_id_type=video_id_type)
    now_ms = int(time.time() * 1000)
    query, headers = playback_api.sign_playback(
        playback_api.PATH_VIDEO_MODEL,
        playback_api.device_pairs(ident),
        now_ms,
        ident.device_id,
        ident.version_name,
        body,
        install_id=ident.iid,
        aid="8662",
    )
    url = f"https://{playback_api.API_HOST}{playback_api.PATH_VIDEO_MODEL}?{query}"
    return url, body, headers, cookies, now_ms


def python_headers(headers: dict, cookies: dict, now_ms: int) -> dict:
    """Python 版实际发出的头。"""
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
    return hdr


def csharp_headers(headers: dict, cookies: dict, now_ms: int) -> dict:
    """C# 版实际发出的头（照 SignedClient.PostAsync 复刻）。"""
    hdr = dict(headers)
    hdr["User-Agent"] = CSHARP_UA
    hdr["X-SS-DP"] = "8662"
    hdr["Content-Type"] = "application/json; charset=UTF-8"
    hdr.pop("x-reading-request", None)
    hdr.pop("lc", None)
    if cookies:
        hdr["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)
    return hdr


def report(tag: str, status, length: int, body: bytes, extra: str = ""):
    verdict = "空 body ★★" if length == 0 else "有响应"
    print(f"  [{tag}] HTTP {status} · {length} 字节 → {verdict} {extra}")
    if length:
        try:
            j = json.loads(body.decode("utf-8"))
            code = j.get("code")
            msg = j.get("message")
            n = len(playback_api.parse_streams(j))
            print(f"         code={code} message={msg!r} 清晰度档数={n}")
            return code == 0 and n > 0
        except Exception:  # noqa: BLE001
            print(f"         非 JSON：{body[:120]!r}")
    return False


def send_requests(url: str, body: bytes, hdr: dict):
    session = net.make_session()
    r = session.post(url, data=body, headers=hdr, timeout=25)
    return r.status_code, r.content


def send_curl(url: str, body: bytes, hdr: dict):
    """换一套 TLS/HTTP 栈（curl + OpenSSL，另一套 TLS 指纹）。"""
    exe = shutil.which("curl") or shutil.which("curl.exe")
    if not exe:
        return None, None
    cmd = [exe, "-s", "-o", "-", "-w", "\n__HTTP__%{http_code}", "--compressed",
           "--max-time", "25", "-X", "POST", url]
    for k, v in hdr.items():
        cmd += ["-H", f"{k}: {v}"]
    cmd += ["--data-binary", "@-"]
    p = subprocess.run(cmd, input=body, capture_output=True, timeout=40)
    out = p.stdout
    marker = out.rfind(b"__HTTP__")
    if marker < 0:
        return None, out
    status = int(out[marker + 8:].strip() or 0)
    return status, out[:marker]


def main(argv=None):
    p = argparse.ArgumentParser(description="请求差分探针")
    p.add_argument("--vid", default="7700851630874315800")
    p.add_argument("--type", type=int, default=1)
    p.add_argument("--pause", type=float, default=5.0)
    args = p.parse_args(argv)

    url, body, headers, cookies, now_ms = build(args.vid, args.type)
    py_hdr = python_headers(headers, cookies, now_ms)
    cs_hdr = csharp_headers(headers, cookies, now_ms)

    print(f"vid={args.vid}  video_id_type={args.type}")
    print(f"body: {body.decode('utf-8')}")
    diff = [
        (k, py_hdr.get(k), cs_hdr.get(k))
        for k in sorted(set(py_hdr) | set(cs_hdr))
        if py_hdr.get(k) != cs_hdr.get(k)
    ]
    print("头差异（Python → C#）：")
    for k, a, b in diff:
        print(f"  {k}: {a!r} → {b!r}")

    results = {}

    print("\n[A] Python 栈 + Python 头（基线）")
    st, content = send_requests(url, body, py_hdr)
    results["A"] = report("A", st, len(content), content)
    time.sleep(args.pause)

    print("\n[B] Python 栈 + C# 形态头（只改头）")
    st, content = send_requests(url, body, cs_hdr)
    results["B"] = report("B", st, len(content), content)
    time.sleep(args.pause)

    print("\n[C] curl 栈 + Python 头（只换 HTTP/TLS 栈）")
    st, content = send_curl(url, body, py_hdr)
    if st is None:
        print("  curl 不可用，跳过")
        results["C"] = None
    else:
        results["C"] = report("C", st, len(content), content)
    time.sleep(args.pause)

    print("\n[D] curl 栈 + C# 形态头")
    st, content = send_curl(url, body, cs_hdr)
    if st is None:
        print("  curl 不可用，跳过")
        results["D"] = None
    else:
        results["D"] = report("D", st, len(content), content)

    print("\n=== 判定 ===")
    if results.get("A") and not results.get("B"):
        print("  → 差异在**请求头**（不是 HTTP/TLS 栈）")
    elif results.get("A") and not results.get("C"):
        print("  → 差异在**HTTP/TLS 栈**（不是请求头）")
    elif results.get("A") and not results.get("B") and not results.get("C"):
        print("  → 两者都有影响")
    else:
        print("  → 见上面各变体结果")
    return 0


if __name__ == "__main__":
    sys.exit(main())
