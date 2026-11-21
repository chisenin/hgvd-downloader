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
  3. 本工具会把 ``http_proxy`` 指到宿主机并重启红果，**拿到直链后自动清掉并关 MITM**
     （下载继续跑完，命令行不用手动 Ctrl-C）。

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
from .config import Config
from .engine.downloader import (
    CancelToken,
    DownloadCallbacks,
    DownloadRequest,
    download,
    episode_filename,
    series_folder,
)
from .service import HongguoService

APP_PACKAGE = "com.phoenix.read"
SPLASH = f"{APP_PACKAGE}/com.dragon.read.pages.splash.SplashActivity"
PRELOAD_HINTS = ("/multi_video_model/preload", "/multi_video_model/player/")
DEBUG = os.environ.get("HGVD_GRAB_DEBUG") == "1"
DETAIL_HINT = "/multi_video_detail/preload"
# ★ 单条详情：请求体带 video_id_type（教学用 1、短剧用 0），
#   响应是 {video_data, video_relate_book}，外面没有 series_id 那一层。
#   教学合集的详情多半走这里 —— 之前它被 WATCH_EXTRA 吞掉，只是落盘不解析。
VIDEO_DETAIL_HINT = "/player/video_detail/"
# ★ 2026-11-21 实盘发现：剧集页改用这批接口取流（老的 multi_video_model 仍在用）
WATCH_EXTRA = ("/video_playback/", "/series_detail_page/")

# ★★ 内容类型（video_data.content_type）：1003 = 教学/知识合集
#   （老胡说热点、老胡说时局、奇趣百科、深空观测局…），1004 = 短剧/小说视频。
#   注意这跟 mixed_video_id_map 的键不是一回事：那里的 1001 只表示「这是合集 ID」，
#   推荐流里一堆宣发短片也是 1001 —— 早期就是把它当内容类型，才误收一片。
TEACHING_CONTENT_TYPES = (1003,)

# 白名单缓存：vid -> (剧名, 序号, 集名, 时长)
# 收藏夹 242 部教学合集约 1.2 万集，拉一次要几分钟，所以落盘复用。
WHITELIST_CACHE = Path.home() / ".hgv_tool" / "grab_whitelist.json"
WHITELIST_TTL = 12 * 3600


def load_whitelist() -> tuple[dict, float]:
    """读白名单缓存 -> (vid 映射, 抓取时间戳)。坏了就当没有。"""
    try:
        raw = json.loads(WHITELIST_CACHE.read_text("utf-8"))
    except Exception:                       # noqa: BLE001
        return {}, 0.0
    if not isinstance(raw, dict):
        return {}, 0.0
    ts = float(raw.get("_ts") or 0.0)
    out = {}
    for vid, v in (raw.get("vids") or {}).items():
        if not isinstance(v, (list, tuple)) or len(v) < 3:
            continue
        try:
            out[str(vid)] = (str(v[0]), int(v[1] or 0), str(v[2]),
                             float(v[3]) if len(v) > 3 and v[3] else 0.0)
        except (TypeError, ValueError):
            continue
    return out, ts


def save_whitelist(album: dict):
    try:
        WHITELIST_CACHE.parent.mkdir(parents=True, exist_ok=True)
        WHITELIST_CACHE.write_text(
            json.dumps({"_ts": time.time(),
                        "vids": {k: list(v) for k, v in album.items()}},
                       ensure_ascii=False),
            encoding="utf-8")
    except Exception:                       # noqa: BLE001
        pass

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
    # ★ preload 请求里 mixed_video_id_map 的键 = 内容类型
    #   （1004 短视频/短剧，1000/1001 教学合集分集）。详情响应常常缺这一集，
    #   所以它才是「默认只收教学」最可靠、最早拿到的判据。
    key: str = ""


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
        watched = any(h in target for h in
                      PRELOAD_HINTS + (DETAIL_HINT, VIDEO_DETAIL_HINT) + WATCH_EXTRA)
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
        if DETAIL_HINT in target or VIDEO_DETAIL_HINT in target:
            self.on_detail(body)
        else:
            self.on_clip(body, _map_keys(req))


def _map_keys(req: bytes) -> dict:
    """从请求体 mixed_video_id_map 里取「vid -> 内容类型键」。

    ★ 一次 preload 可能同时带多个键（App 会把首页推荐与合集分集凑成一发），
      所以必须映射到每个 vid，不能只记「这次请求用了哪些键」。
    """
    import gzip as _gzip

    head, _, body = req.partition(b"\r\n\r\n")
    if not body:
        return {}
    if b"content-encoding: gzip" in head.lower():
        try:
            body = _gzip.decompress(body)
        except Exception:                   # noqa: BLE001
            return {}
    try:
        payload = json.loads(body.decode("utf-8", "replace"))
    except Exception:                       # noqa: BLE001
        return {}
    out: dict[str, str] = {}
    for k, vids in (payload.get("mixed_video_id_map") or {}).items():
        if isinstance(vids, (list, tuple)):
            for v in vids:
                out[str(v)] = str(k)
        else:
            out[str(vids)] = str(k)
    return out


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
                 take_unknown: bool = False, log=print):
        self.out_dir = Path(out_dir)
        self.quality = quality
        self.include_all = include_all
        self.take_unknown = take_unknown
        self.log = log
        self._lock = threading.Lock()
        self._seen: set[str] = set()
        self._titles: dict[str, tuple[str, int, str]] = {}   # vid -> (剧名, 序号, 集名)
        self._book_type: dict[str, int] = {}                 # vid -> book_type（3=教学合集）
        self._series_name: dict[str, str] = {}
        self._content_type: dict[str, int] = {}               # series_id -> content_type
        # ★ 教学合集白名单：详情响应认出 content_type=1003 的系列后，
        #   后台用 album_detail（免签名）把它的全部分集 vid 拉下来，抓流只认白名单。
        #   preload 响应里既没有 series_id 也没有类型，这是唯一能把 vid 归类的路。
        self._album: dict[str, tuple[str, int, str, float]] = {}
        self._album_lock = threading.Lock()
        self._album_seen: set[str] = set()
        self._album_busy = 0
        self._album_q: queue.Queue = queue.Queue()
        self._svc = None
        cached, self._whitelist_ts = load_whitelist()
        if cached:
            self._album.update(cached)
            self.log(f"· 白名单缓存 {len(cached)} 集")
        self._q: queue.Queue[Clip] = queue.Queue()
        # ★ 抓流的目的就是拿直链：第一条「白名单命中、可下载」的直链到手即置位，
        #   命令行据此停代理/关 MITM；下载线程继续把它和同批抓到的其余集下完。
        self.captured = threading.Event()
        self._done = 0
        self._fail = 0
        self._skipped_feed = 0
        self._worker = threading.Thread(target=self._loop, daemon=True)
        self._album_worker = threading.Thread(target=self._album_loop, daemon=True)
        self._shelf_worker = threading.Thread(target=self._shelf_loop, daemon=True)

    def start(self):
        self._worker.start()
        self._album_worker.start()
        self._shelf_worker.start()

    # -- 教学合集白名单
    def _service(self):
        if self._svc is None:
            self._svc = HongguoService(Config.load())
        return self._svc

    def _album_loop(self):
        """把 content_type=1003 的系列展开成 vid 白名单（album_detail 免签名）。"""
        while True:
            sid = self._album_q.get()
            if sid is None:
                return
            self._album_busy += 1
            try:
                eps = self._service().list_album_episodes(sid)
            except Exception as exc:        # noqa: BLE001
                self.log(f"· 拉分集失败 {sid}：{exc}")
                continue
            finally:
                self._album_busy -= 1
            if not eps:
                self.log(f"· 教学合集 {sid} 没有分集（album_detail 返回空）")
                continue
            add = {}
            for e in eps:
                add[str(e.vid)] = (e.series_title or "", int(e.index or 0),
                                   e.title or "", float(e.duration or 0))
            with self._album_lock:
                self._album.update(add)
            self.log(f"＋ 教学合集《{eps[0].series_title or sid}》白名单 +{len(add)} 集")

    def _shelf_loop(self):
        """把收藏夹里所有教学合集展开成 vid 白名单。

        ★ 抓流从 preload 只能拿到 vid，而 preload 响应没有 series_id；
          没有白名单就只能靠「用户先打开课程页」碰运气。
          收藏夹接口与 album_detail 都免签名，一次拉全最省事，
          结果落盘缓存，重启秒开（12 小时内不重拉）。
        """
        if self._album and time.time() - self._whitelist_ts < WHITELIST_TTL:
            return
        try:
            svc = self._service()
            items = svc.list_shelf(with_albums=False, album_limit=0)
        except Exception as exc:            # noqa: BLE001
            self.log(f"· 读收藏夹失败（不影响抓流）：{exc}")
            return
        albums = [it for it in items if getattr(it, "book_type", 0) == 3]
        if not albums:
            return
        self.log(f"· 收藏夹 {len(items)} 部，其中教学合集 {len(albums)} 部，开始建白名单…")
        for n, it in enumerate(albums, 1):
            try:
                eps = svc.list_album_episodes(it.series_id)
            except Exception:               # noqa: BLE001
                continue
            if not eps:
                continue
            add = {str(e.vid): (e.series_title or it.title, int(e.index or 0),
                                e.title or "", float(e.duration or 0)) for e in eps}
            with self._album_lock:
                self._album.update(add)
            if n % 20 == 0 or n == len(albums):
                self.log(f"· 白名单 {n}/{len(albums)} 部，累计 {len(self._album)} 集")
        save_whitelist(self._album)
        self.log(f"＋ 收藏夹白名单就绪：{len(self._album)} 集（已缓存，下次秒开）")

    def _wait_album(self, vid: str, timeout: float = 8.0):
        """等白名单认出这一集（album_detail 是 1~2 秒的网络请求）。

        ★ 白名单是逐系列拉取的：推荐流那一大堆 vid 永远等不到，
          所以「没有在途请求」时只给 1.5 秒，避免队列被 8 秒×十几条拖死。
        """
        start = time.time()
        while True:
            with self._album_lock:
                hit = self._album.get(vid)
            if hit is not None:
                return hit
            busy = self._album_busy > 0 or not self._album_q.empty()
            if time.time() - start >= (timeout if busy else 1.5):
                return None
            time.sleep(0.2)

    # -- 抓到的两个来源
    def feed_model(self, payload: dict, keymap: dict | None = None):
        keymap = keymap or {}
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
                key=str(keymap.get(str(vid)) or ""),
            )
            with self._lock:
                if clip.vid in self._seen:
                    # ★ 必须是 continue 不是 return：同一批 preload 里
                    #   重复出现一个已抓过的 vid，用 return 会把后面全部吞掉。
                    continue
                self._seen.add(clip.vid)
            self._q.put(clip)
            self.log(f"＋ 抓到 {clip.vid} {clip.definition or ''} "
                     f"{clip.duration:.0f}s{'（加密）' if key else ''}"
                     f" key={clip.key or '?'}")

    def feed_detail(self, payload: dict):
        data = payload.get("data")
        if not isinstance(data, dict):
            return
        # 形态一：data[series_id] = {video_data, video_relate_book}  （multi_video_detail）
        # 形态二：data 本身就是一条 {video_data, video_relate_book} （video_detail/v1）
        if "video_data" in data or "video_relate_book" in data:
            items = [("", data)]
        else:
            items = list(data.items())
        for series_id, book in items:
            if not isinstance(book, dict):
                continue
            vd = book.get("video_data") or {}
            vl = [it for it in (vd.get("video_list") or []) if isinstance(it, dict)]
            sid = str(series_id or "")
            if not sid:
                for it in vl:
                    if it.get("series_id"):
                        sid = str(it["series_id"])
                        break
            if not sid:
                # ★ 教学合集的 video_list 是空的，series_id 只在这里有
                sid = str(vd.get("series_id") or vd.get("series_id_str") or "")
            rel = book.get("video_relate_book") or {}
            info = rel.get("book_info") or {}
            name = info.get("book_name") or vd.get("series_title") or ""
            if name and sid:
                self._series_name[sid] = name
            # ★ 内容类型只在 video_data 上（1003=教学/知识合集）。
            #   教学类的 video_relate_book 整个缺失，book_type 永远拿不到，
            #   所以必须靠它 —— 这也是之前「拿不到内容类型」的根因。
            ct = None
            if vd.get("content_type") is not None:
                try:
                    ct = int(vd["content_type"])
                except (TypeError, ValueError):
                    ct = None
            if ct is not None and sid:
                self._content_type[sid] = ct
                if ct in TEACHING_CONTENT_TYPES and sid not in self._album_seen:
                    self._album_seen.add(sid)
                    self._album_q.put(sid)
                    self.log(f"· 认出教学合集《{name or sid}》，拉白名单…")
            # ★★ 关键：拿不到就不要写。原来「缺字段 → 0」把未知一律当短剧，
            #     教学合集整批被误判跳过（2026-11-21 抓流实测）。
            bt = None
            if info.get("book_type") is not None:
                try:
                    bt = int(info["book_type"])
                except (TypeError, ValueError):
                    bt = None
            for item in vl:
                vid = str(item.get("vid") or "")
                if not vid:
                    continue
                if bt is not None:
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
            finally:
                self._q.task_done()

    def wait_idle(self, timeout: float | None = None) -> bool:
        """等下载队列清空（命令行 Ctrl-C 收尾时用）。

        返回 True 表示队列已空；超时返回 False。代理停掉后不会再有新任务，
        但同一批 preload 可能还在往队列里塞，所以这里看的是未完成数而不是瞬时 qsize。
        """
        deadline = None if timeout is None else time.time() + timeout
        while self._q.unfinished_tasks:
            if deadline is not None and time.time() >= deadline:
                return False
            time.sleep(0.2)
        return True

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
        # ★★ 判据（2026-11-21 抓流实测定案）：
        #   ① 白名单：详情响应认出 content_type=1003 的教学系列，再用 album_detail
        #      把该系列的分集 vid 拉下来。命中才是教学课 —— preload 响应里
        #      既没有 series_id 也没有类型，这是唯一能把 vid 归类的路；
        #   ② book_type=3 也算（部分合集走 multi_video_detail）；
        #   ③ 都没有 = 未知，**不当短剧**（原来写死 0，整批误杀）。
        #   ⚠ mixed_video_id_map 的 1001 不能当判据：它只表示「这是合集 ID」，
        #     推荐流里的宣发短片同样是 1001。
        album_ep = self._wait_album(clip.vid, timeout=8.0)
        teaching = album_ep is not None
        if not teaching and not self.include_all:
            if self._wait_book_type(clip.vid, timeout=0.5) == 3:
                teaching = True
        if not teaching and not self.include_all:
            if self.take_unknown and not clip.content_key and clip.duration >= 100:
                self.log(f"· 白名单未命中，按教学特征收下（key={clip.key or '?'} "
                         f"{clip.duration:.0f}s 未加密）")
            else:
                self.log(f"· 跳过 {clip.vid}（不在教学白名单 key={clip.key or '?'} "
                         f"{clip.duration:.0f}s{' 加密' if clip.content_key else ''}）")
                return

        # ★ 直链到手且确认是我们要下的内容 → 置「已抓到」信号（供自检/进度提示）。
        #   注意：抓流是常驻循环，抓到直链**不会**停代理、不会收网；
        #   相邻几集 / 换集还要靠同一个 MITM 继续探测，只有 Ctrl-C 才结束。
        #   放在白名单判定之后，避免推荐流的直链误置信号。
        self.captured.set()

        # 标题：白名单最准（真正的剧名 + 集序号 + 集名），其次详情响应
        if album_ep is not None:
            book, idx, title = album_ep[0], album_ep[1], album_ep[2]
        else:
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
