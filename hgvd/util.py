"""通用工具：JSON 容错读取、时长/速度格式化、URL 处理。"""

from __future__ import annotations

import hashlib
import json
import os
import re
from typing import Any


# ---------------------------------------------------------------- JSON 容错

def j_str(obj: Any, key: str, default: str = "") -> str:
    """取字符串字段。

    ★ 真实网关返回的 JSON 字段类型不稳定（数字可能是字符串），
    所有读取都必须容错，不能直接 ``obj[key]``。
    """
    if not isinstance(obj, dict):
        return default
    v = obj.get(key)
    if v is None:
        return default
    if isinstance(v, str):
        return v
    if isinstance(v, (int, float)):
        return str(v)
    if isinstance(v, bool):
        return "true" if v else "false"
    return default


def j_int(obj: Any, key: str, default: int = 0) -> int:
    if not isinstance(obj, dict):
        return default
    v = obj.get(key)
    if v is None:
        return default
    try:
        return int(v)
    except (TypeError, ValueError):
        try:
            return int(float(v))
        except (TypeError, ValueError):
            return default


def j_float(obj: Any, key: str, default: float = 0.0) -> float:
    if not isinstance(obj, dict):
        return default
    v = obj.get(key)
    if v is None:
        return default
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def j_bool(obj: Any, key: str, default: bool = False) -> bool:
    if not isinstance(obj, dict):
        return default
    v = obj.get(key)
    if v is None:
        return default
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return bool(v)
    if isinstance(v, str):
        return v.lower() in ("1", "true", "yes")
    return default


def j_dict(obj: Any, key: str) -> dict:
    if not isinstance(obj, dict):
        return {}
    v = obj.get(key)
    return v if isinstance(v, dict) else {}


def j_list(obj: Any, key: str) -> list:
    if not isinstance(obj, dict):
        return []
    v = obj.get(key)
    if isinstance(v, list):
        return v
    if v is None:
        return []
    return [v]


def deep_find(obj: Any, keys: list[str], depth: int = 8) -> Any:
    """在嵌套结构里按优先级找第一个非空值。

    ``keys`` 是优先级列表（如 ``["playUrl", "url"]``）。
    广度优先 + 深度上限，避免在超大 JSON 里无限递归。
    """
    if depth <= 0:
        return None
    queue: list[Any] = [obj]
    while queue:
        cur = queue.pop(0)
        if isinstance(cur, dict):
            for k in keys:
                v = cur.get(k)
                if isinstance(v, str) and v:
                    return v
                if isinstance(v, (dict, list)) and v:
                    return v
            for v in cur.values():
                if isinstance(v, (dict, list)):
                    queue.append(v)
        elif isinstance(cur, list):
            queue.extend(cur)
        depth -= 1
    return None


# ---------------------------------------------------------------- 格式化

def fmt_duration(sec: float) -> str:
    """秒 → ``MM:SS`` 或 ``H:MM:SS``。"""
    if sec <= 0:
        return "--:--"
    sec = int(round(sec))
    h, rem = divmod(sec, 3600)
    m, s = divmod(rem, 60)
    if h:
        return f"{h}:{m:02d}:{s:02d}"
    return f"{m:02d}:{s:02d}"


def fmt_size(n: int) -> str:
    if n <= 0:
        return "--"
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{n:.1f} {unit}" if unit != "B" else f"{n} B"
        n /= 1024.0
    return f"{n:.1f} TB"


def fmt_speed(bps: float) -> str:
    if bps <= 0:
        return "--"
    for unit in ("B/s", "KB/s", "MB/s", "GB/s"):
        if bps < 1024 or unit == "GB/s":
            return f"{bps:.1f} {unit}"
        bps /= 1024.0
    return f"{bps:.1f} GB/s"


def fmt_eta(seconds: float | None) -> str:
    if seconds is None or seconds < 0:
        return "--"
    if seconds < 60:
        return f"{int(seconds)}秒"
    if seconds < 3600:
        return f"{int(seconds // 60)}分"
    return f"{int(seconds // 3600)}时{int((seconds % 3600) // 60)}分"


# ---------------------------------------------------------------- 文件名

_ILLEGAL = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}


def safe_name(name: str, max_len: int = 80) -> str:
    """文件名清洗。

    ★ Windows 保留名（CON/NUL/COM1…）即使带扩展名也会失败，必须单独处理。
    ★ 结尾的空格和点会被 Windows 静默去掉。
    """
    name = _ILLEGAL.sub("_", (name or "").strip())
    name = re.sub(r"\s+", " ", name).strip(" .")
    if not name:
        name = "未命名"
    stem = name.split(".")[0].upper()
    if stem in _RESERVED:
        name = "_" + name
    if len(name) > max_len:
        name = name[:max_len].rstrip(" .")
    return name


def md5_short(text: str, n: int = 16) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()[:n]


def parts_key(out_path: str) -> str:
    """成品路径 → 分片缓存目录名。

    ★ 必须与引擎侧 ``PersistentSegDir`` 的回退算法完全一致，
    否则 GUI 看到的进度和引擎实际用的目录对不上。
    """
    return md5_short(os.path.abspath(out_path))


def query_without(url: str, *drop: str) -> str:
    """去掉 URL 上的指定 query 参数。"""
    if "?" not in url:
        return url
    base, _, qs = url.partition("?")
    keep = [
        kv
        for kv in qs.split("&")
        if kv and kv.split("=", 1)[0] not in drop
    ]
    return base + ("?" + "&".join(keep) if keep else "")


def split_query(url: str) -> tuple[str, str]:
    base, _, qs = url.partition("?")
    return base, qs
