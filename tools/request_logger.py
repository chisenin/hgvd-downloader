"""本地抓包代理 —— 把客户端真实发出的请求字节记录下来。

★ 用途：Python 版能下载、C# 版返回空 body 时，用它把两边的请求
  **逐字节**抓下来对比，定位到底差在哪一个字段。

★ 它会把收到的请求**原样转发**到真实网关（query 与 body 不改），
  所以转发出去的请求依然是合法签名、依然会被服务端正常处理 ——
  只是中间留了一份副本。

用法：
    python -m tools.request_logger --port 8899 --out /tmp/cap_csharp.json
    # 然后让客户端把 HGVD_HOST 指向 http://127.0.0.1:8899
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import requests

REAL_HOST = "api5-normal-sinfonlinea.fqnovel.com"

# 这些头是「逐跳」或由代理自己生成的，转发时不该带
HOP_BY_HOP = {
    "host", "connection", "content-length", "accept-encoding",
    "transfer-encoding", "keep-alive", "proxy-connection",
}

_lock = threading.Lock()
_captures: list[dict] = []


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 静音
        pass

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b""

        path, _, query = self.path.partition("?")
        hdrs = []
        for k, v in self.headers.items():
            hdrs.append([k, v])

        cap = {
            "path": path,
            "query": query,
            "headers": hdrs,
            "body_text": body.decode("utf-8", "replace"),
            "body_hex": body.hex(),
            "body_len": len(body),
        }
        with _lock:
            _captures.append(cap)

        # 原样转发
        fwd = {}
        for k, v in self.headers.items():
            if k.lower() in HOP_BY_HOP:
                continue
            fwd[k] = v
        try:
            r = requests.post(
                f"https://{REAL_HOST}{self.path}",
                data=body, headers=fwd, timeout=30,
            )
            data = r.content
            code = r.status_code
            ctype = r.headers.get("Content-Type", "application/json")
        except Exception as e:  # noqa: BLE001
            data = json.dumps({"proxy_error": str(e)}).encode()
            code = 502
            ctype = "application/json"

        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def main(argv=None):
    p = argparse.ArgumentParser(description="请求抓包代理")
    p.add_argument("--port", type=int, default=8899)
    p.add_argument("--out", default="/tmp/capture.json")
    args = p.parse_args(argv)

    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"抓包代理已启动：http://127.0.0.1:{args.port} → https://{REAL_HOST}")

    def dump_loop():
        import time
        while True:
            time.sleep(2)
            with _lock:
                data = list(_captures)
            if data:
                with open(args.out, "w", encoding="utf-8") as f:
                    json.dump(data, f, ensure_ascii=False, indent=1)

    threading.Thread(target=dump_loop, daemon=True).start()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        with _lock:
            data = list(_captures)
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
        print(f"共抓到 {len(data)} 个请求 → {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
