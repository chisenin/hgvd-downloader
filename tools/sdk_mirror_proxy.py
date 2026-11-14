"""Android SDK 镜像代理：把 dl.google.com / dl-ssl.google.com 重定向到腾讯云镜像。

★ 背景：某些网络环境下 `dl.google.com` 直连不通，走常规代理也可能不通
  —— 代理只对国内源有效。而 sdkmanager 的仓库地址**硬编码在 jar 里**，无法通过
  配置文件改。所以用一个本地 HTTP 代理做 URL 重写。

用法：
    python tools/sdk_mirror_proxy.py --port 8899
    然后：sdkmanager --proxy=http --proxy_host=127.0.0.1 --proxy_port=8899 ...

原理：sdkmanager 以 HTTP 明文请求 dl.google.com/android/repository/xxx，
  代理收到后改成 mirrors.cloud.tencent.com/AndroidSDK/xxx 再取回。
"""
from __future__ import annotations

import argparse
import http.server
import socketserver
import urllib.request
import urllib.error

DEBUG = True
MIRROR = "https://mirrors.cloud.tencent.com/AndroidSDK"
UPSTREAM_HOSTS = {
    "dl.google.com",
    "dl-ssl.google.com",
    "storage.googleapis.com",
}
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0"


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 静默
        pass

    def _dbg(self, msg):
        if DEBUG:
            print(f"[proxy] {msg}", flush=True)

    def _rewrite(self, path: str) -> str:
        # ★ 用代理时 self.path 是**完整 URL**（http://host/path），
        #   不是普通路径 —— 必须先剥掉 scheme+host。
        if path.startswith("http://") or path.startswith("https://"):
            rest = path.split("://", 1)[1]
            slash = rest.find("/")
            path = rest[slash:] if slash >= 0 else "/"

        p = path
        for prefix in ("/android/repository/", "/repository/"):
            if p.startswith(prefix):
                p = "/" + p[len(prefix):]
                break
        else:
            p = "/" + p.lstrip("/")
        return MIRROR + p

    def do_GET(self):
        host = self.headers.get("Host", "")
        if host.split(":")[0] not in UPSTREAM_HOSTS:
            self.send_error(404, "only sdk hosts")
            return
        self._dbg(f"GET host={host} path={self.path[:120]}")
        url = self._rewrite(self.path)
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                data = resp.read()
                self.send_response(200)
                self.send_header("Content-Type", resp.headers.get("Content-Type", "application/octet-stream"))
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        except urllib.error.HTTPError as e:
            self.send_error(e.code, f"mirror fetch failed: {url}")
        except Exception as e:
            self.send_error(502, str(e))

    def do_HEAD(self):
        self.do_GET()

    def do_CONNECT(self):
        """处理 HTTPS CONNECT —— sdkmanager 有些仓库走 https。

        ★ 做法：**不真的做 TLS 隧道**，而是把 CONNECT 目标当作
          「要取的资源路径」，回一个 200 后用明文响应。
          ★★ 但客户端随后会发 TLS ClientHello —— 我们无法解密。
          ⇒ 所以这里改为：**直接失败**，并引导用 http 源。
          （实测：腾讯云镜像的 repository 索引与包用 http 就能取到，
            走 https 的只有少数仓库，不影响安装。）
        """
        self._dbg(f"CONNECT {self.path}")
        self.send_error(501, "https CONNECT not supported; use http mirror")


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8899)
    args = ap.parse_args()
    with Server(("127.0.0.1", args.port), Handler) as httpd:
        print(f"SDK 镜像代理已启动：http://127.0.0.1:{args.port} → {MIRROR}")
        print("用法：sdkmanager --proxy=http --proxy_host=127.0.0.1 --proxy_port=" + str(args.port))
        httpd.serve_forever()


if __name__ == "__main__":
    main()
