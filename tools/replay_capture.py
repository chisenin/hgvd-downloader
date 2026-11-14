"""重放抓到的请求 —— 区分「头部差异」与「HTTP/TLS 栈差异」。

★ 用**同一份 query + body + 头部**，分别用不同的 HTTP 栈发出去：
    - curl（另一套 TLS 栈）
    - Python requests（第三套）
  如果同一份请求换个栈就成功 → 问题在 **HTTP/TLS 栈**（.NET 的指纹）；
  如果都失败 → 问题在 **请求内容**。

用法：
    python -m tools.replay_capture --cap _cap.json --index 0
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys

from hgvd import net

REAL_HOST = "api5-normal-sinfonlinea.fqnovel.com"


def replay_curl(cap: dict) -> tuple[str, int, int]:
    exe = shutil.which("curl") or shutil.which("curl.exe")
    url = f"https://{REAL_HOST}{cap['path']}?{cap['query']}"
    cmd = [exe, "-s", "-o", "-", "-w", "\n__HTTP__%{http_code}", "--max-time", "25",
           "-X", "POST", url]
    for k, v in cap["headers"]:
        cmd += ["-H", f"{k}: {v}"]
    cmd += ["--data-binary", "@-"]
    p = subprocess.run(cmd, input=bytes.fromhex(cap["body_hex"]),
                       capture_output=True, timeout=40)
    out = p.stdout
    m = out.rfind(b"__HTTP__")
    if m < 0:
        return "curl", 0, 0
    return "curl", int(out[m + 8:].strip() or 0), m


def replay_requests(cap: dict) -> tuple[str, int, int]:
    url = f"https://{REAL_HOST}{cap['path']}?{cap['query']}"
    hdrs = [(k, v) for k, v in cap["headers"]
            if k.lower() not in ("host", "content-length", "accept-encoding",
                                 "connection", "transfer-encoding")]
    s = net.make_session()
    s.trust_env = False
    # requests 会自动加 Accept-Encoding / Connection，这里刻意复刻原始顺序
    s.headers.clear()
    for k, v in hdrs:
        s.headers[k] = v
    body = bytes.fromhex(cap["body_hex"])
    try:
        r = s.post(url, data=body, timeout=25)
    except Exception as e:  # noqa: BLE001
        return "requests", 0, 0
    return "requests", r.status_code, len(r.content)


def main(argv=None):
    p = argparse.ArgumentParser(description="重放抓到的请求")
    p.add_argument("--cap", default="_cap.json")
    p.add_argument("--index", type=int, default=0)
    args = p.parse_args(argv)

    caps = json.load(open(args.cap, encoding="utf-8"))
    plays = [c for c in caps if "video_model" in c["path"]]
    if not plays:
        print("没有 video_model 请求")
        return 1
    cap = plays[args.index]
    print(f"重放 #{args.index}：body={cap['body_len']} 字节  query 长度={len(cap['query'])}")
    print("头部顺序：")
    for k, v in cap["headers"]:
        print(f"  {k}: {v[:70]}")

    for name, fn in (("curl", replay_curl), ("requests", replay_requests)):
        tag, status, n = fn(cap)
        if name == "curl":
            print(f"\n[{tag}] HTTP {status} · {n} 字节 → "
                  f"{'★ 空 body' if n == 0 else '有响应'}")
        else:
            print(f"[{tag}] HTTP {status} · {n} 字节 → "
                  f"{'★ 空 body' if n == 0 else '有响应'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
