"""本地网页版前端。

★ 为什么用网页而不是 Tkinter：
  - 浏览器界面**零依赖、跨平台**，不用为 GUI 框架的版本兼容操心。
  - 引入 PySide6 会让打包产物大幅膨胀（完整 PySide6 元包能把 exe 从
    96 MB 撑到 330 MB），对这种轻量工具不划算。
  - 桌面发行版另有 C# + Avalonia 实现，本模块只负责快速验证逻辑。

用法::

    python -m hgvd.web --port 8765
    # 浏览器打开 http://127.0.0.1:8765/
"""

from __future__ import annotations

import json
import mimetypes
import threading
import time
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from ..config import Config
from ..engine.downloader import CancelToken
from ..service import HongguoService

UI_PATH = Path(__file__).resolve().parent / "ui.html"


class AppState:
    """服务端的共享状态。"""

    def __init__(self):
        self.cfg = Config.load()
        self.svc = HongguoService(self.cfg)
        self.cancel = CancelToken()
        self.lock = threading.Lock()
        self.shelf: list[dict] = []
        self.episodes: dict[str, list[dict]] = {}
        self.jobs: dict[str, dict] = {}      # vid -> {state, down, total, speed, error, series, episode}
        self.running = False

    def snapshot(self) -> dict:
        with self.lock:
            return {
                "logged_in": self.svc.logged_in,
                "user_name": self.svc.account.user_name,
                "shelf": list(self.shelf),
                "jobs": list(self.jobs.values()),
                "running": self.running,
                "out_dir": str(self.cfg.ensure_out_dir()),
                "quality": self.cfg.quality,
            }


STATE = AppState()


class Handler(BaseHTTPRequestHandler):
    server_version = "hgvd"

    def log_message(self, fmt, *args):  # 静默访问日志
        pass

    # ---------------------------------------------------------- 工具

    def _json(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _html(self):
        try:
            body = UI_PATH.read_bytes()
        except OSError:
            self._json({"error": "找不到 ui.html"}, 500)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    # ---------------------------------------------------------- 路由

    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        if u.path in ("/", "/index.html"):
            self._html()
        elif u.path == "/api/state":
            self._json(STATE.snapshot())
        elif u.path == "/api/episodes":
            sid = (q.get("series_id") or [""])[0]
            if not sid:
                self._json({"error": "缺 series_id"}, 400)
                return
            try:
                # ★ 先试合集端点 —— book_type=3 的教学内容只有它能给出分集
                eps = STATE.svc.list_album_episodes(sid)
                if not eps:
                    eps = STATE.svc.list_episodes(sid)
            except Exception as e:  # noqa: BLE001
                self._json({"error": str(e)}, 500)
                return
            data = [
                {"vid": e.vid, "index": e.index, "title": e.title,
                 "duration": e.duration}
                for e in eps
            ]
            with STATE.lock:
                STATE.episodes[sid] = data
            self._json({"episodes": data})
        else:
            self._json({"error": "not found"}, 404)

    def do_POST(self):
        u = urlparse(self.path)
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b"{}"
        try:
            payload = json.loads(raw.decode("utf-8"))
        except Exception:  # noqa: BLE001
            self._json({"error": "请求体不是 JSON"}, 400)
            return

        if u.path == "/api/refresh":
            try:
                items = STATE.svc.list_shelf()
            except Exception as e:  # noqa: BLE001
                self._json({"error": str(e)}, 500)
                return
            with STATE.lock:
                STATE.shelf = [
                    {"series_id": i.series_id, "title": i.title,
                     "episode_cnt": i.episode_cnt, "cover": i.cover,
                     # ★ 类型标记：短剧 / 教学合集 / 单个视频
                     "type_label": i.type_label,
                     "is_drama": i.is_short_drama}
                    for i in items
                ]
            self._json({"ok": True, "count": len(items)})
        elif u.path == "/api/settings":
            if payload.get("out_dir"):
                STATE.cfg.out_dir = str(payload["out_dir"])
            if payload.get("quality"):
                STATE.cfg.quality = str(payload["quality"])
            try:
                STATE.cfg.workers = int(payload.get("workers") or STATE.cfg.workers)
            except (TypeError, ValueError):
                pass
            STATE.cfg.save()
            self._json({"ok": True})
        elif u.path == "/api/download":
            self._start_download(payload)
        elif u.path == "/api/stop":
            STATE.cancel.cancel()
            self._json({"ok": True})
        else:
            self._json({"error": "not found"}, 404)

    # ---------------------------------------------------------- 下载

    def _start_download(self, payload):
        series_id = str(payload.get("series_id") or "")
        vids = payload.get("vids") or []
        if not series_id or not vids:
            self._json({"error": "缺 series_id 或 vids"}, 400)
            return
        if STATE.running:
            self._json({"error": "已有任务在跑"}, 409)
            return

        with STATE.lock:
            eps = STATE.episodes.get(series_id) or []
            want = set(str(v) for v in vids)
            chosen = [e for e in eps if e["vid"] in want]
            if not chosen:
                self._json({"error": "没有匹配的集"}, 400)
                return
            title = next(
                (s["title"] for s in STATE.shelf if s["series_id"] == series_id),
                series_id,
            )
            for e in chosen:
                STATE.jobs[e["vid"]] = {
                    "vid": e["vid"], "index": e["index"], "episode": e["title"],
                    "series": title, "state": "排队中", "down": 0, "total": 0,
                    "speed": 0, "error": "",
                }
            STATE.running = True
            STATE.cancel = CancelToken()

        def work():
            from ..service import EpisodeItem

            items = [
                EpisodeItem(
                    vid=e["vid"], index=e["index"], title=e["episode"],
                    series_id=series_id, series_title=title,
                )
                for e in chosen
            ]

            def cb_for(ep):
                from ..engine.downloader import DownloadCallbacks

                cb = DownloadCallbacks()
                cb.on_phase = lambda p, v=ep.vid: _upd(v, {"state": p})
                cb.on_progress = lambda d, t, s, v=ep.vid: _upd(
                    v, {"state": "下载中", "down": d, "total": t, "speed": s}
                )
                return cb

            # 逐集下载（每集独立，便于显示每集状态）
            for ep in items:
                if STATE.cancel.cancelled:
                    break
                try:
                    res = STATE.svc.download_episode(
                        ep, cb=cb_for(ep), cancel=STATE.cancel
                    )
                    if res.ok:
                        _upd(ep.vid, {"state": "已完成", "down": res.size,
                                       "total": res.size, "speed": 0})
                    else:
                        _upd(ep.vid, {"state": "失败", "error": res.error})
                except Exception as e:  # noqa: BLE001
                    _upd(ep.vid, {"state": "失败", "error": str(e)[:200]})
            with STATE.lock:
                STATE.running = False

        threading.Thread(target=work, daemon=True).start()
        self._json({"ok": True, "count": len(chosen)})


def _upd(vid: str, patch: dict):
    with STATE.lock:
        j = STATE.jobs.get(vid)
        if j is not None:
            j.update(patch)


def main(argv=None):
    import argparse

    p = argparse.ArgumentParser(prog="hgvd.web", description="红果视频下载器 · 本地网页版")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--no-browser", action="store_true", help="不自动打开浏览器")
    args = p.parse_args(argv)

    srv = ThreadingHTTPServer((args.host, args.port), Handler)
    url = f"http://{args.host}:{args.port}/"
    print(f"红果视频下载器 · 本地网页版")
    print(f"打开：{url}")
    print("按 Ctrl+C 停止")
    if not args.no_browser:
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止")
    finally:
        srv.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
