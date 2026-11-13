"""从 MuMu 模拟器里提取红果 App 的**视频缓存**。

★ 这是绕过服务端限流的可靠路径（2026-11-13 实测）：

    App 播放过的视频会被 MediaDataLoader 缓存成**标准 MP4**：

        /sdcard/Android/data/com.phoenix.read/cache/short/<md5>.mdl
        /sdcard/Android/data/com.phoenix.read/cache/dataloader/<md5>.mdl

    ★ ``.mdl`` 的文件头是 ``ftypisom`` —— 标准 MP4 容器，
      里面是 **标准 HEVC (hvc1) + AAC**，ffmpeg 直接可播。

    ★ 每个 ``.mdl`` 有一个配对的 ``.mdlnodeconf``（136 字节），
      内容是 ``ttmd`` 索引（含 ``fkey`` = 缓存键），**不含明文 URL**。

★★ 二进制文件**不能**用 ``cat`` 经 RPC 传输 —— 会破坏字节
   （实测头部会变成 ``fiel`` 而不是 ``ftyp``）。
   必须走 ``base64``，再在本地解码。
"""

from __future__ import annotations

import base64
import binascii
import html
import json
import os
import re
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

MUMU_MANAGER = r"D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"

# App 数据目录（红果 = 番茄小说同包 com.phoenix.read）
CACHE_DIRS = [
    "/sdcard/Android/data/com.phoenix.read/cache/short",
    "/sdcard/Android/data/com.phoenix.read/cache/dataloader",
]


@dataclass
class CacheEntry:
    """一条缓存记录。"""

    name: str
    size: int = 0
    mtime: str = ""
    local_path: str = ""
    codec: str = ""
    width: int = 0
    height: int = 0
    duration: float = 0.0
    raw: dict = field(default_factory=dict)


def _mumu(instance: int, cmd: str, timeout: int = 300) -> str:
    """通过 MuMuManager 内部 RPC 执行命令，返回 stdout。"""
    exe = MUMU_MANAGER
    if not os.path.exists(exe):
        raise RuntimeError(f"找不到 MuMuManager：{exe}")
    proc = subprocess.run(
        [exe, "sh", "-v", str(instance), "-c", cmd],
        capture_output=True, timeout=timeout,
    )
    return proc.stdout.decode("utf-8", errors="replace")


def list_cache(instance: int = 1) -> list[dict]:
    """列出 App 的视频缓存文件（含所在目录）。"""
    out = _mumu(
        instance,
        "ls -l /sdcard/Android/data/com.phoenix.read/cache/short/ "
        "/sdcard/Android/data/com.phoenix.read/cache/dataloader/ 2>/dev/null",
    )
    entries: list[dict] = []
    cur_dir = ""
    for line in out.splitlines():
        m_dir = re.match(r"^/.*:\s*$", line.strip())
        if m_dir:
            cur_dir = line.strip().rstrip(":")
            continue
        m = re.match(
            r"^-(\S{9})\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s+(\S+\s+\S+)\s+(.+)$", line
        )
        if not m:
            continue
        name = m.group(7).strip()
        if not name.endswith(".mdl"):
            continue
        entries.append({
            "name": name,
            "size": int(m.group(5)),
            "mtime": m.group(6),
            "dir": cur_dir or CACHE_DIRS[0],
        })
    return entries


def pull(instance: int, remote_path: str, dest: str) -> str:
    """把模拟器里的文件拉到本机（走 base64，保证字节完整）。

    ★ 大文件（十几 MB）base64 会撑爆命令行输出，
      所以用 ``base64 <file>`` 让模拟器自己输出，再重定向。
    """
    exe = MUMU_MANAGER
    proc = subprocess.run(
        [exe, "sh", "-v", str(instance), "-c", f"base64 '{remote_path}'"],
        capture_output=True, timeout=1800,
    )
    raw = proc.stdout
    # 去掉所有空白（base64 输出可能被换行）
    compact = re.sub(rb"\s+", b"", raw)
    try:
        data = base64.b64decode(compact)
    except (binascii.Error, ValueError) as e:
        raise RuntimeError(f"base64 解码失败（传输可能被截断）：{e}") from e
    Path(dest).parent.mkdir(parents=True, exist_ok=True)
    Path(dest).write_bytes(data)
    return dest


def probe(path: str) -> dict:
    """用 ffprobe 读视频信息。

    ★ 用 JSON 输出，避免多条流时「同名 key 被后一条覆盖」
      （实测横屏+竖屏混合的缓存里，video 和 audio 都有 duration）。
    """
    try:
        p = subprocess.run(
            ["ffprobe", "-v", "error",
             "-show_streams", "-show_format",
             "-of", "json", path],
            capture_output=True, text=True, timeout=60,
        )
    except FileNotFoundError:
        return {}
    try:
        raw = json.loads(p.stdout or "{}")
    except json.JSONDecodeError:
        return {}

    info: dict = {}
    fmt = raw.get("format") or {}
    if fmt.get("duration"):
        info["duration"] = fmt["duration"]
    if fmt.get("size"):
        info["size"] = fmt["size"]

    for st in raw.get("streams") or []:
        kind = st.get("codec_type")
        if kind == "video" and "codec_name" not in info:
            info["codec_name"] = st.get("codec_name", "")
            info["codec_tag"] = st.get("codec_tag_string", "")
            info["width"] = st.get("width", 0)
            info["height"] = st.get("height", 0)
        elif kind == "audio" and "audio_codec" not in info:
            info["audio_codec"] = st.get("codec_name", "")
    return info


def extract_all(instance: int = 1, out_dir: str | None = None,
                min_size: int = 100_000, limit: int = 0) -> list[CacheEntry]:
    """把缓存里所有视频提取到本机并探测格式。"""
    out = Path(out_dir or (Path.home() / "hgvd_cache"))
    out.mkdir(parents=True, exist_ok=True)
    result: list[CacheEntry] = []
    entries = list_cache(instance)
    # 去掉 dataloader 里的重复（同名只取一份）
    seen: set[str] = set()
    uniq = []
    for e in entries:
        if e["name"] in seen:
            continue
        seen.add(e["name"])
        uniq.append(e)

    for e in uniq:
        if e["size"] < min_size:
            continue
        if limit and len(result) >= limit:
            break
        remote = f"{e.get('dir') or CACHE_DIRS[0]}/{e['name']}"
        dest = str(out / e["name"].replace(".mdl", ".mp4"))
        try:
            pull(instance, remote, dest)
        except Exception as ex:  # noqa: BLE001
            print(f"  ! 提取失败 {e['name']}: {ex}")
            continue
        if os.path.getsize(dest) == 0:
            print(f"  ! 提取为空 {e['name']}（RPC 传输可能截断）")
            continue
        info = probe(dest)
        ce = CacheEntry(
            name=e["name"],
            size=e["size"],
            mtime=e["mtime"],
            local_path=dest,
            codec=info.get("codec_name", ""),
            width=int(info.get("width") or 0),
            height=int(info.get("height") or 0),
            duration=float(info.get("duration") or 0),
            raw=info,
        )
        result.append(ce)
        print(f"  ✓ {e['name']} → {ce.size} 字节  "
              f"{ce.codec} {ce.width}x{ce.height} {ce.duration:.1f}s")
    return result
