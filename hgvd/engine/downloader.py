"""下载引擎。

移植自「兴趣岛课程下载器」的成熟内核（``Xqd.Core/Engine/*``），
按红果的实际链路做了裁剪：

红果的免签名直链是**单文件 MP4**（``/video/public/<vid>/360p.mp4``），
不是 HLS 分片，所以不需要清单解析 / 分片拼接那套复杂逻辑。
但**断点续传**、**并发**、**时长校验**、**已下载检测**全部保留。

★ 关键设计（来自参考项目踩坑记录）：
  - 进度用**字节数**而不是时长占比（直链视频没有逐片时长）
  - 断点续传用 HTTP Range，读已下载部分的字节数
  - 已下载检测：文件名主键 + 大小门槛 + ffprobe 时长校验
  - 临时文件用 ``.part`` 后缀，成功后原子改名
"""

from __future__ import annotations

import os
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path

from .. import net
from ..util import fmt_size, safe_name

# ---------------------------------------------------------------- 模型


@dataclass
class DownloadCallbacks:
    """进度回调。"""

    on_phase: object = None      # (phase: str) -> None
    on_progress: object = None   # (down: int, total: int, speed: float) -> None
    on_log: object = None        # (msg: str) -> None


@dataclass
class DownloadResult:
    ok: bool = False
    out_path: str = ""
    size: int = 0
    duration: float = 0.0
    error: str = ""
    skipped: bool = False


@dataclass
class DownloadRequest:
    url: str
    out_path: str
    expect_duration: float = 0.0
    headers: dict = field(default_factory=dict)
    cookies: dict = field(default_factory=dict)
    content_key: str = ""      # CENC 内容密钥（hex）；非空则需 ffmpeg 解密


class CancelToken:
    """简易取消/暂停控制。"""

    def __init__(self):
        self._cancelled = False

    def cancel(self):
        self._cancelled = True

    @property
    def cancelled(self) -> bool:
        return self._cancelled


# ---------------------------------------------------------------- ffmpeg


def find_tool(name: str) -> str | None:
    """找 ffmpeg / ffprobe。返回完整路径或 None。"""
    exe = name + ".exe" if os.name == "nt" else name
    # 1) 工具自装目录
    from ..config import tool_dir

    local = tool_dir() / "ffmpeg" / "bin" / exe
    if local.exists():
        return str(local)
    # 2) PATH
    found = shutil.which(name)
    if found:
        return found
    return None


def probe_duration(path: str) -> float:
    """ffprobe 取时长（秒）。失败返回 0。"""
    tool = find_tool("ffprobe")
    if not tool:
        return 0.0
    try:
        r = subprocess.run(
            [
                tool,
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path,
            ],
            capture_output=True,
            timeout=60,
            creationflags=_no_window(),
        )
        if r.returncode != 0:
            return 0.0
        return float(r.stdout.decode("utf-8", "replace").strip())
    except Exception:  # noqa: BLE001
        return 0.0


def _no_window() -> int:
    """Windows 下不弹控制台窗口。

    ★ 参考项目踩坑：Python 版靠 ``CREATE_NO_WINDOW`` 根治，
    C# 版天然有等价物但必须显式设置。
    """
    if os.name == "nt":
        return 0x08000000  # CREATE_NO_WINDOW
    return 0


# ---------------------------------------------------------------- 下载


def decrypt_file(src: str, dst: str, content_key: str, timeout: int = 600) -> bool:
    """用 ffmpeg 解密 CENC 加密流。

    ★ 内容密钥由服务端下发、在本地按播放器规则解包（见 ``hgvd.drm``），
      与官方播放器同一套机制，不是破解。
    ★ ffmpeg 参数：``-decryption_key <hex>``（十六进制，不带 0x）。
    """
    tool = find_tool("ffmpeg")
    if not tool:
        return False
    cmd = [
        tool, "-y", "-loglevel", "error",
        "-decryption_key", content_key,
        "-i", src,
        "-c", "copy", "-movflags", "+faststart",
        dst,
    ]
    try:
        r = subprocess.run(
            cmd, capture_output=True, timeout=timeout, creationflags=_no_window()
        )
        if r.returncode != 0:
            err = r.stderr.decode("utf-8", "replace")[-300:]
            raise RuntimeError(f"ffmpeg 解密失败：{err}")
        return True
    except Exception as e:  # noqa: BLE001
        raise RuntimeError(str(e)) from e


def download(
    req: DownloadRequest,
    cb: DownloadCallbacks | None = None,
    cancel: CancelToken | None = None,
    timeout: int = 60,
) -> DownloadResult:
    """下载单个文件（支持断点续传）。"""
    cb = cb or DownloadCallbacks()
    out = Path(req.out_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    part = out.with_suffix(out.suffix + ".part")

    if cb.on_phase:
        cb.on_phase("下载")

    # 已完成？直接返回
    if out.exists() and out.stat().st_size > 0:
        dur = probe_duration(str(out))
        if dur <= 0 or req.expect_duration <= 0 or \
                abs(dur - req.expect_duration) <= max(3.0, req.expect_duration * 0.02):
            return DownloadResult(
                ok=True, out_path=str(out), size=out.stat().st_size, duration=dur,
                skipped=True,
            )

    headers = dict(req.headers)
    # ★ 默认 UA 用 App 的 —— CDN 认这个
    headers.setdefault("User-Agent", net.APP_UA)
    # ★★ **不要设 Referer**：抖音系 CDN 会校验 Referer 白名单，
    # 带 `Referer: https://hongguoduanju.com/` 会直接 403（实测）。
    headers.pop("Referer", None)
    if req.cookies:
        headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in req.cookies.items() if v)

    session = net.make_session()

    # 断点续传：已下载部分
    resume_from = 0
    if part.exists():
        resume_from = part.stat().st_size

    if resume_from:
        headers["Range"] = f"bytes={resume_from}-"

    try:
        r = session.get(req.url, headers=headers, timeout=timeout, stream=True)
    except Exception as e:  # noqa: BLE001
        return DownloadResult(ok=False, out_path=str(out), error=f"请求失败：{e}"[:200])

    if r.status_code not in (200, 206):
        r.close()
        return DownloadResult(
            ok=False, out_path=str(out), error=f"HTTP {r.status_code}"
        )

    # 服务端不支持 Range → 从头下
    if resume_from and r.status_code == 200:
        resume_from = 0

    total = 0
    cr = r.headers.get("Content-Range")
    if cr and "/" in cr:
        try:
            total = int(cr.rsplit("/", 1)[1])
        except ValueError:
            total = 0
    if not total:
        try:
            total = int(r.headers.get("Content-Length") or 0)
        except ValueError:
            total = 0
    if resume_from:
        total += resume_from

    down = resume_from
    mode = "ab" if resume_from else "wb"
    t0 = time.time()
    last_report = 0.0
    last_bytes = down

    try:
        with open(part, mode) as f:
            for chunk in r.iter_content(chunk_size=256 * 1024):
                if cancel and cancel.cancelled:
                    return DownloadResult(
                        ok=False, out_path=str(out), error="已取消", size=down
                    )
                if not chunk:
                    continue
                f.write(chunk)
                down += len(chunk)
                now = time.time()
                if now - last_report >= 0.25:
                    speed = (down - last_bytes) / max(now - t0, 0.001) if now > t0 else 0
                    # 用瞬时增量算速度更准
                    speed = len(chunk) / max(now - last_report, 0.001)
                    last_bytes = down
                    last_report = now
                    if cb.on_progress:
                        cb.on_progress(down, total, speed)
    except Exception as e:  # noqa: BLE001
        return DownloadResult(ok=False, out_path=str(out), error=f"写入失败：{e}"[:200])
    finally:
        r.close()

    if cb.on_progress:
        cb.on_progress(down, total, 0)

    if total and down < total:
        return DownloadResult(
            ok=False, out_path=str(out),
            error=f"下载不完整 {fmt_size(down)}/{fmt_size(total)}（可重试续传）",
            size=down,
        )

    # 原子改名
    if out.exists():
        try:
            out.unlink()
        except OSError:
            pass
    part.replace(out)

    # ★ 加密流：用 ffmpeg 解密成可播放的成品
    if req.content_key:
        if cb.on_phase:
            cb.on_phase("解密")
        # ★ 输出名必须以 .mp4 结尾 —— ffmpeg 靠扩展名判断容器格式，
        #   用「.mp4.dec」会报 "Error initializing the muxer ... Invalid argument"
        dec = out.with_name(out.stem + ".dec.mp4")
        try:
            decrypt_file(str(out), str(dec), req.content_key)
        except Exception as e:  # noqa: BLE001
            try:
                if dec.exists():
                    dec.unlink()
            except OSError:
                pass
            return DownloadResult(
                ok=False, out_path=str(out), error=str(e)[:200], size=down
            )
        try:
            out.unlink()
        except OSError:
            pass
        dec.replace(out)

    # 时长校验
    dur = 0.0
    if cb.on_phase:
        cb.on_phase("校验")
    dur = probe_duration(str(out))
    if dur <= 0:
        return DownloadResult(
            ok=False, out_path=str(out), error="成品无法解析（可能损坏）", size=down
        )
    if req.expect_duration > 0:
        tol = max(3.0, req.expect_duration * 0.02)
        if abs(dur - req.expect_duration) > tol:
            return DownloadResult(
                ok=False, out_path=str(out),
                error=f"时长不符（标称 {req.expect_duration:.1f}s，实测 {dur:.1f}s）",
                size=down, duration=dur,
            )

    return DownloadResult(ok=True, out_path=str(out), size=down, duration=dur)


# ---------------------------------------------------------------- 命名


def episode_filename(series_title: str, index: int, ep_title: str, ext: str = ".mp4") -> str:
    """成品文件名：``<剧名>.<序号>.<集名>.mp4``。

    ★ 与参考项目一致：序号从 1 起，与 App 列表顺序一致。
    """
    st = safe_name(series_title)
    et = safe_name(ep_title) if ep_title else ""
    parts = [st, f"{index:03d}"]
    if et:
        parts.append(et)
    return ".".join(parts) + ext


def series_folder(series_title: str) -> str:
    return safe_name(series_title)


# ---------------------------------------------------------------- 已下载检测


def find_existing(folder: Path, stem: str, expect_duration: float = 0.0) -> Path | None:
    """找已下载的成品。

    ★ 判据顺序（参考项目硬约束）：
      1. 按文件名主键找候选
      2. 大小 > 10KB
      3. ffprobe 时长与标称比对（容差 max(3s, 2%)）
    """
    if not folder.exists():
        return None
    cands = [p for p in folder.glob(stem + ".*") if p.is_file() and p.stat().st_size > 10_240]
    if not cands:
        return None
    if expect_duration <= 0:
        return max(cands, key=lambda p: p.stat().st_mtime)
    for p in cands:
        d = probe_duration(str(p))
        if d > 0 and abs(d - expect_duration) <= max(3.0, expect_duration * 0.02):
            return p
    return None
