"""抓流模式：让 App 自己取一次播放地址，我们从 MITM 里把直链捞出来。

★ 为什么需要它（2026-11-21）：播放接口 ``multi_video_model/preload`` 现在校验
  ``X-Helios`` + ``X-Medusa``，且与 query+body **字节**绑定（见 ``docs/05`` §1.3）。
  这两个值由 ``libmetasec_ml.so`` 生成，宿主机算不出来；而 App 能算。
  于是改成「App 发请求 → 我们抓响应 → 宿主机只负责下载」。

用法::

    python -m hgvd grab --out "D:\\\\movies\\\\hgvd"
    python -m hgvd grab --out ... --no-restart      # App 已经带着代理启动过
    python -m hgvd grab --out ... --quality 720p

前提：
  1. MuMu 正在运行、红果已登录；
  2. 模拟器里装过本工具的 CA（``tools/metasec/mitm.py`` 的流程，见 docs/05 §2.1）；
  3. 本工具会把 ``http_proxy`` 指到宿主机并重启红果，**退出时自动清掉**。

★ 抓到的 ``video_model`` 与线上接口同构，所以下载/解密直接复用
  ``engine.downloader`` + ``api.playback.extract_content_key``，
  不另写一套 —— 两套实现迟早会不一致。
"""
from __future__ import annotations

import importlib.util
import json
import os
import socket
import ssl
import threading
import time
import queue
from dataclasses import dataclass, field
from pathlib import Path

from .api import playback as playback_api
from .auth import mumu as mumu_auth
from .engine.downloader import (
    CancelToken,
    DownloadCallbacks,
    DownloadRequest,
    download,
    episode_filename,
    series_folder,
)

APP_PACKAGE = "com.phoenix.read"
SPLASH = f"{APP_PACKAGE}/com.dragon.read.pages.splash.SplashActivity"
PRELOAD_HINTS = ("/multi_video_model/preload", "/multi_video_model/player/")
DEBUG = os.environ.get("HGVD_GRAB_DEBUG") == "1"
DETAIL_HINT = "/multi_video_detail/preload"
# ★ 2026-11-21 实盘发现：剧集页改用这批接口取流（老的 multi_video_model 仍在用）
WATCH_EXTRA = ("/video_playback/", "/player/video_detail/", "/series_detail_page/")

# 抓包工具的 CA / 叶证书逻辑（不重复实现一遍证书生成）
_MITM = None


def _load_mitm():
    global _MITM
    if _MITM is not None:
        return _MITM
    root = Path(__file__).resolve().parent.parent
    path = root / "tools" / "metasec" / "mitm.py"
    if not path.exists():
        raise RuntimeError(f"找不到抓包工具：{path}")
    spec = importlib.util.spec_from_file_location("hgvd_mitm", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    # 把 CA 目录固定到仓库里，避免受 cwd 影响
    mod.CA_DIR = root / "_ref" / "metasec" / "ca"
    mod.LOG = root / "_ref" / "metasec" / "mitm.log"
    _MITM = mod
    return mod


def _short_title(title: str, fallback: str) -> str:
    """集名截断：只留第一句、最多 32 字。"""
    text = " ".join((title or "").split())
    if not text:
        return fallback
    for mark in ("。", "！", "？", "，", "；"):
        cut = text.find(mark)
        if 0 < cut <= 32:
            return text[:cut]
    return text[:32]


def _as_dict(text: str) -> dict:
    """把 ``"{'definition': '360p'}"`` 这种伪 JSON（单引号）还原成 dict。"""
    import ast

    try:
        val = ast.literal_eval(text)
        return val if isinstance(val, dict) else {}
    except Exception:                       # noqa: BLE001
        return {}


# ---------------------------------------------------------------- 数据结构

@dataclass
class Clip:
    """一集的取流结果。"""

    vid: str
    url: str
    backup_url: str = ""
    duration: float = 0.0
    definition: str = ""
    height: int = 0
    content_key: str = ""
    series_id: str = ""
    index: int = 0
    title: str = ""
    book: str = ""


# ---------------------------------------------------------------- 代理

class CaptureProxy(threading.Thread):
    """最小 MITM：只解密、只旁观，不改一个字节。"""

    def __init__(self, port: int, on_clip, on_detail, log):
        super().__init__(daemon=True)
        self.port = port
        self.on_clip = on_clip
        self.on_detail = on_detail
        self.log = log
        self._srv: socket.socket | None = None
        self._halt = threading.Event()
        self._ctx_cache: dict[str, ssl.SSLContext] = {}

    # -- 生命周期
    def run(self):
        srv = socket.socket()
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", self.port))
        srv.listen(32)
        srv.settimeout(1.0)
        self._srv = srv
        self.log(f"代理已监听 0.0.0.0:{self.port}")
        while not self._halt.is_set():
            try:
                conn, addr = srv.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            threading.Thread(target=self._serve, args=(conn, addr), daemon=True).start()

    def stop(self):
        self._halt.set()
        try:
            if self._srv:
                self._srv.close()
        except OSError:
            pass

    # -- 单个连接
    def _serve(self, conn: socket.socket, addr):
        try:
            conn.settimeout(20)
            head = _read_head(conn)
            if not head:
                return
            first = head.split(b"\r\n", 1)[0].decode("latin1")
            if not first.startswith("CONNECT "):
                return                      # 只处理 HTTPS
            hostport = first.split()[1]
            host, _, port = hostport.partition(":")
            port = int(port or 443)

            conn.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
            ctx = self._ctx(host)
            tls = ctx.wrap_socket(conn, server_side=True)
            tls.settimeout(20)

            req = _read_head(tls)
            if not req:
                return
            req = _read_body(tls, req)

            if DEBUG:
                try:
                    self.log("→ " + req.split(b"\r\n", 1)[0].decode("latin1")[:150])
                except Exception:                   # noqa: BLE001
                    pass
            resp = _forward(host, port, req)
            if DEBUG:
                self.log(f"← {len(resp)} 字节")
            if not resp:
                return
            try:
                tls.sendall(resp)
            except OSError:
                pass
            self._inspect(req, resp)
        except Exception as exc:             # noqa: BLE001
            if DEBUG:
                try:
                    self.log(f"！连接异常 {type(exc).__name__}: {exc}")
                except Exception:               # noqa: BLE001
                    pass
        finally:
            try:
                conn.close()
            except OSError:
                pass

    def _ctx(self, host: str) -> ssl.SSLContext:
        hit = self._ctx_cache.get(host)
        if hit:
            return hit
        mitm = _load_mitm()
        mitm.ensure_ca()
        cert_pem, key_pem = mitm.leaf_for(host)
        chain = cert_pem + mitm._ca_cert.public_bytes(mitm.serialization.Encoding.PEM)
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.load_cert_chain(mitm._write_tmp(chain, ".crt"), mitm._write_tmp(key_pem, ".key"))
        ctx.set_alpn_protocols(["http/1.1"])
        self._ctx_cache[host] = ctx
        return ctx

    # -- 旁观
    def _inspect(self, req: bytes, resp: bytes):
        try:
            line = req.split(b"\r\n", 1)[0].decode("latin1")
            target = line.split(" ")[1] if " " in line else ""
        except Exception:                   # noqa: BLE001
            return
        watched = any(h in target for h in PRELOAD_HINTS + (DETAIL_HINT,) + WATCH_EXTRA)
        if not watched:
            return
        body = _response_json(resp)
        if DEBUG and body is not None:
            import pathlib

            pathlib.Path("_ref/grab").mkdir(parents=True, exist_ok=True)
            tag = target.split("?")[0].strip("/").replace("/", "_")
            (pathlib.Path("_ref/grab") / f"{tag}.req.bin").write_bytes(req)
            (pathlib.Path("_ref/grab") / f"{tag}.resp.json").write_text(
                json.dumps(body, ensure_ascii=False, indent=1), encoding="utf-8")
            self.log(f"⇢ 已记录 {tag}（{len(resp)} 字节）")
        if any(h in target for h in WATCH_EXTRA):
            return
        if not isinstance(body, dict):
            self.log(f"· 取流响应解析失败：{target.split('?')[0][-50:]} {len(resp)} 字节")
            return
        if body.get("code") not in (0, None):
            self.log(f"· 取流接口返回 code={body.get('code')}")
            return
        if DETAIL_HINT in target:
            self.on_detail(body)
        else:
            self.on_clip(body, _map_keys(req))


def _map_keys(req: bytes) -> tuple:
    """从请求体里取 mixed_video_id_map 的键（决定内容类型）。"""
    import gzip as _gzip

    head, _, body = req.partition(b"\r\n\r\n")
    if not body:
        return ("",)
    if b"content-encoding: gzip" in head.lower():
        try:
            body = _gzip.decompress(body)
        except Exception:                   # noqa: BLE001
            return ("",)
    try:
        payload = json.loads(body.decode("utf-8", "replace"))
        return tuple((payload.get("mixed_video_id_map") or {}).keys()) or ("",)
    except Exception:                       # noqa: BLE001
        return ("",)


def _read_head(sock: socket.socket) -> bytes:
    buf = b""
    while b"\r\n\r\n" not in buf:
        chunk = sock.recv(65536)
        if not chunk:
            return buf
        buf += chunk
        if len(buf) > 1 << 20:
            break
    return buf


def _read_body(sock: socket.socket, req: bytes) -> bytes:
    head, _, rest = req.partition(b"\r\n\r\n")
    clen = 0
    for ln in head.split(b"\r\n"):
        if ln.lower().startswith(b"content-length:"):
            try:
                clen = int(ln.split(b":", 1)[1].strip())
            except ValueError:
                clen = 0
    body = rest
    while len(body) < clen:
        chunk = sock.recv(65536)
        if not chunk:
            break
        body += chunk
    return head + b"\r\n\r\n" + body


def _forward(host: str, port: int, req: bytes) -> bytes:
    """把请求发给真服务器，读回完整响应（强制 close，省得等）。"""
    head, _, body = req.partition(b"\r\n\r\n")
    lines = head.split(b"\r\n")
    kept = [lines[0]]
    for ln in lines[1:]:
        low = ln.lower()
        if low.startswith((b"proxy-connection:", b"connection:")):
            continue
        kept.append(ln)
    kept.append(b"Connection: close")
    out = b"\r\n".join(kept) + b"\r\n\r\n" + body

    raw = socket.create_connection((host, port), timeout=20)
    if port == 443:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        ctx.set_alpn_protocols(["http/1.1"])
        raw = ctx.wrap_socket(raw, server_hostname=host)
    try:
        raw.sendall(out)
        chunks = []
        while True:
            try:
                chunk = raw.recv(65536)
            except socket.timeout:
                break
            if not chunk:
                break
            chunks.append(chunk)
        return b"".join(chunks)
    finally:
        try:
            raw.close()
        except OSError:
            pass


def _response_json(resp: bytes) -> dict | None:
    """从原始响应里解出 JSON（处理 chunked / gzip）。"""
    head, _, body = resp.partition(b"\r\n\r\n")
    if not body:
        return None
    hdrs = head.decode("latin1", "replace").lower()
    if "transfer-encoding: chunked" in hdrs:
        body = _dechunk(body)
    if "content-encoding: gzip" in hdrs or body[:2] == b"\x1f\x8b":
        try:
            import gzip

            body = gzip.decompress(body)
        except Exception:                   # noqa: BLE001
            pass
    try:
        return json.loads(body.decode("utf-8", "replace"))
    except Exception:                       # noqa: BLE001
        return None


def _dechunk(data: bytes) -> bytes:
    out = bytearray()
    pos = 0
    while pos < len(data):
        nl = data.find(b"\r\n", pos)
        if nl < 0:
            break
        try:
            size = int(data[pos:nl].split(b";")[0], 16)
        except ValueError:
            break
        if size == 0:
            break
        out += data[nl + 2:nl + 2 + size]
        pos = nl + 2 + size + 2
    return bytes(out)


# ---------------------------------------------------------------- 取流与下载

class Grabber:
    """把抓到的 video_model 变成磁盘上的成品。"""

    def __init__(self, out_dir: str, quality: str = "", include_all: bool = False,
                 log=print):
        self.out_dir = Path(out_dir)
        self.quality = quality
        self.include_all = include_all
        self.log = log
        self._lock = threading.Lock()
        self._seen: set[str] = set()
        self._titles: dict[str, tuple[str, int, str]] = {}   # vid -> (剧名, 序号, 集名)
        self._book_type: dict[str, int] = {}                 # vid -> book_type（3=教学合集）
        self._series_name: dict[str, str] = {}
        self._q: queue.Queue[Clip] = queue.Queue()
        self._done = 0
        self._fail = 0
        self._skipped_feed = 0
        self._worker = threading.Thread(target=self._loop, daemon=True)

    def start(self):
        self._worker.start()

    # -- 抓到的两个来源
    def feed_model(self, payload: dict, map_keys=("1004",)):
        data = payload.get("data")
        if not isinstance(data, dict):
            return
        for vid, entry in data.items():
            vm_raw = (entry or {}).get("video_model")
            if not isinstance(vm_raw, str):
                continue
            # ★ parse_streams 要的是 data.video_model（data 就是模型本身），
            #   不是按 vid 再套一层 —— 套错了会「有 response 但解析不出流」。
            try:
                vm = json.loads(vm_raw)
            except Exception:               # noqa: BLE001
                continue
            for item in (vm.get("video_list") or []):
                if not isinstance(item, dict):
                    continue
                for key in ("video_meta", "encrypt_info", "volume"):
                    val = item.get(key)
                    if isinstance(val, str):
                        item[key] = _as_dict(val)
            sub = {"data": {"video_model": json.dumps(vm, ensure_ascii=False)}}
            streams = playback_api.parse_streams(sub)
            if not streams:
                self.log(f"· {vid} 的 video_model 里没有可用直链（{len(vm_raw)} 字符）")
                continue
            pick = self._pick(streams)
            if not pick:
                continue
            key = ""
            try:
                key = playback_api.extract_content_key(sub) or ""
            except Exception:               # noqa: BLE001
                key = ""
            clip = Clip(
                vid=str(vid),
                url=pick["url"],
                backup_url=pick.get("backup_url") or "",
                duration=float(pick.get("duration") or 0),
                definition=pick.get("definition") or "",
                height=int(pick.get("height") or 0),
                content_key=key,
            )
            with self._lock:
                if clip.vid in self._seen:
                    return
                self._seen.add(clip.vid)
            self._q.put(clip)
            self.log(f"＋ 抓到 {clip.vid} {clip.definition or ''} "
                     f"{clip.duration:.0f}s{'（加密）' if key else ''}")

    def feed_detail(self, payload: dict):
        data = payload.get("data")
        if not isinstance(data, dict):
            return
        for series_id, book in data.items():
            if not isinstance(book, dict):
                continue
            rel = book.get("video_relate_book") or {}
            info = rel.get("book_info") or {}
            name = info.get("book_name") or ""
            if name:
                self._series_name[str(series_id)] = name
            try:
                bt = int(info.get("book_type") or 0)
            except Exception:               # noqa: BLE001
                bt = 0
            vd = book.get("video_data") or {}
            for item in (vd.get("video_list") or []):
                if not isinstance(item, dict):
                    continue
                vid = str(item.get("vid") or "")
                if not vid:
                    continue
                self._book_type[vid] = bt
                self._titles[vid] = (
                    name,
                    int(item.get("vid_index") or 0) + 1,
                    item.get("title") or "",
                )

    # -- 挑选清晰度
    def _pick(self, streams: list[dict]) -> dict | None:
        usable = [s for s in streams if s.get("url")]
        if not usable:
            return None
        if self.quality:
            want = self.quality.lower()
            exact = [s for s in usable if (s.get("definition") or "").lower() == want]
            if exact:
                return exact[0]
        return max(usable, key=lambda s: (int(s.get("height") or 0),
                                          int(s.get("bitrate") or 0)))

    # -- 下载线程
    def _loop(self):
        while True:
            clip = self._q.get()
            if clip is None:
                return
            try:
                self._save(clip)
            except Exception as exc:        # noqa: BLE001
                self._fail += 1
                self.log(f"✗ {clip.vid} 失败：{exc}")

    def _wait_book_type(self, vid: str, timeout: float = 5.0):
        """等详情响应把这一集的 book_type 带回来（详情往往比 preload 晚一点）。"""
        deadline = time.time() + timeout
        while time.time() < deadline:
            bt = self._book_type.get(vid)
            if bt is not None:
                return bt
            time.sleep(0.25)
        return None

    def _save(self, clip: Clip):
        # ★★ 判据是「内容类型」，不是「加不加密」——
        #   很多短剧本来就不加密，按加密筛会全放进来。
        #   book_type：3 = 教学合集（收藏夹里的课程），0/2 = 短剧/推荐流。
        #   类型要靠详情响应带回来，所以这里给 5 秒。
        bt = self._wait_book_type(clip.vid)
        if not self.include_all:
            if bt is None:
                self.log(f"· 跳过 {clip.vid}（拿不到内容类型，当作非教学）")
                return
            if bt != 3:
                self.log(f"· 跳过 {clip.vid}（book_type={bt}，非教学合集）")
                return

        # 标题可能晚到（detail 响应比 model 慢一点），给它 5 秒
        book, idx, title = self._titles.get(clip.vid, ("", 0, ""))
        if not title:
            for _ in range(10):
                time.sleep(0.5)
                book, idx, title = self._titles.get(clip.vid, ("", 0, ""))
                if title:
                    break
        if not book:
            book = self._series_name.get(clip.series_id, "")
        # ★ 收藏流里很多「集名」其实是整段剧情简介（几十上百字），
        #   直接拿来当文件名会超出 Windows 260 字符上限 —— 截断再用。
        title = _short_title(title, clip.vid)
        folder = self.out_dir / (series_folder(book) if book else "未知名")
        name = episode_filename(book or "未知名", idx or 1, title, ".mp4")
        out = folder / name

        cb = DownloadCallbacks(
            on_phase=lambda p: None,
            on_progress=None,
            on_log=None,
        )
        req = DownloadRequest(
            url=clip.url,
            out_path=str(out),
            expect_duration=clip.duration,
            content_key=clip.content_key,
            backup_url=clip.backup_url,
        )
        res = download(req, cb, CancelToken())
        if res.ok:
            self._done += 1
            tag = "已存在" if res.skipped else "完成"
            self.log(f"✓ [{self._done}] {name}  {res.size / 1048576:.1f} MB {tag}")
        else:
            self._fail += 1
            self.log(f"✗ {name} 失败：{res.error}")

    @property
    def stats(self) -> tuple[int, int, int]:
        return self._done, self._fail, self._q.qsize()


# ---------------------------------------------------------------- MuMu 控制

def _sh(mumu: str, index: int, cmd: str, timeout: int = 60) -> str:
    try:
        return mumu_auth.sh(mumu, index, cmd, timeout=timeout) or ""
    except Exception:                       # noqa: BLE001
        return ""


def pick_instance(mumu: str, wanted: int | None = None) -> int:
    if wanted is not None:
        return wanted
    try:
        items = mumu_auth.list_instances(mumu)
    except Exception:                       # noqa: BLE001
        items = []
    for it in items:
        if it.get("is_android_started"):
            return int(it.get("index", 0))
    raise RuntimeError("没有正在运行的 MuMu 实例：请先启动模拟器并打开红果。")


def app_pid(mumu: str, index: int) -> str:
    """★ pidof 可能返回多个 pid（主进程 + 子进程），只取第一个，
    否则拼出来的 ``/proc/1 2/root/...`` 一定是空。"""
    out = _sh(mumu, index, f"pidof {APP_PACKAGE}").strip()
    parts = out.split()
    return parts[0] if parts else ""


def ca_ok(mumu: str, index: int, retries: int = 3) -> bool | None:
    """检查 App 进程能不能看到自制 CA。True/False；读不到返回 None（当作未知）。

    ★ 只把「读到了但明显偏少（< 100）」判成 False ——
      刚启动时目录偶尔读不到，误报会让整条链路白跑。
    """
    for _ in range(retries):
        pid = app_pid(mumu, index)
        if pid:
            out = _sh(
                mumu, index,
                f"ls /proc/{pid}/root/apex/com.android.conscrypt/cacerts 2>/dev/null | wc -l")
            nums = [ln.strip() for ln in out.splitlines() if ln.strip().isdigit()]
            if nums:
                n = int(nums[-1])
                if n >= 146:
                    return True
                if n >= 100:
                    return False
        time.sleep(2)
    return None


def local_ips() -> list[str]:
    ips: list[str] = []
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ips.append(info[4][0])
    except Exception:                       # noqa: BLE001
        pass
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("223.5.5.5", 80))
        ips.append(s.getsockname()[0])
        s.close()
    except Exception:                       # noqa: BLE001
        pass
    seen, out = set(), []
    for ip in ips:
        if ip.startswith("127.") or ip in seen:
            continue
        seen.add(ip)
        out.append(ip)
    return out


def pick_host_ip(mumu: str, index: int, port: int) -> str:
    """挑一个模拟器真能连上的宿主 IPv4。"""
    for ip in local_ips():
        out = _sh(mumu, index,
                  f"echo > /dev/tcp/{ip}/{port} 2>/dev/null; echo rc=$?")
        if "rc=0" in out:
            return ip
    # toybox sh 可能没有 /dev/tcp，退回 nc
    for ip in local_ips():
        out = _sh(mumu, index, f"echo | nc -w 2 {ip} {port} >/dev/null 2>&1; echo rc=$?")
        if "rc=0" in out:
            return ip
    raise RuntimeError("模拟器连不上宿主机的代理端口：检查防火墙 / 网络模式。")


def set_proxy(mumu: str, index: int, addr: str):
    _sh(mumu, index, f"settings put global http_proxy {addr}")


def clear_proxy(mumu: str, index: int):
    _sh(mumu, index, "settings put global http_proxy :0")


def restart_app(mumu: str, index: int):
    _sh(mumu, index, f"am force-stop {APP_PACKAGE}")
    time.sleep(1)
    _sh(mumu, index, f"am start -n {SPLASH}")
