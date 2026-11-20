"""从 MuMu 模拟器里提取红果 App 的**视频缓存**。

★ 这条路径**完全不经过** ``video_model``，因此不受空 body 影响
  （2026-11-13 实测）：

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


def _manager() -> str:
    """定位 MuMuManager.exe（可移植：不写死本机路径）。"""
    env = os.environ.get("HGVD_MUMUMANAGER", "")
    if env and os.path.exists(env):
        return env
    try:
        from .mumu import find_mumu

        return find_mumu()
    except Exception:  # noqa: BLE001
        import shutil as _shutil

        found = _shutil.which("MuMuManager")
        if found:
            return found
        raise RuntimeError(
            "找不到 MuMuManager.exe。可用环境变量 HGVD_MUMUMANAGER 指向它。")


def _mumu(instance: int, cmd: str, timeout: int = 300) -> str:
    """通过 MuMuManager 内部 RPC 执行命令，返回 stdout。"""
    exe = _manager()
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
    exe = _manager()
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
    # ★ 大文件优先：播放器的缓存里既有完整正片也有几十 KB 的预览分片，
    #   按体积降序能先拿到最可能是正片的那些。
    uniq.sort(key=lambda x: -x["size"])

    for e in uniq:
        if e["size"] < min_size:
            continue
        if limit and len(result) >= limit:
            break
        short = e["name"][:8]
        # ★ 增量：已经提取过（名字里带短 md5）就跳过
        done = list(out.glob(f"cache_*_{short}.mp4"))
        if done:
            info = probe(str(done[0]))
            result.append(CacheEntry(
                name=e["name"], size=e["size"], mtime=e["mtime"],
                local_path=str(done[0]),
                codec=info.get("codec_name", ""),
                width=int(info.get("width") or 0),
                height=int(info.get("height") or 0),
                duration=float(info.get("duration") or 0),
                raw=info,
            ))
            print(f"  = 已存在 {done[0].name}")
            continue

        remote = f"{e.get('dir') or CACHE_DIRS[0]}/{e['name']}"
        dest = str(out / f".tmp_{short}.mp4")
        try:
            pull(instance, remote, dest)
        except Exception as ex:  # noqa: BLE001
            print(f"  ! 提取失败 {e['name']}: {ex}")
            continue
        if os.path.getsize(dest) == 0:
            print(f"  ! 提取为空 {e['name']}（RPC 传输可能截断）")
            continue
        info = probe(dest)
        dur = float(info.get("duration") or 0)
        w = int(info.get("width") or 0)
        h = int(info.get("height") or 0)
        # ★ 用「时长_宽x高_短md5」命名：md5 本身看不出是哪一集，
        #   加上时长后可以和各接口拿到的分集时长对上号。
        final = out / f"cache_{dur:.0f}s_{w}x{h}_{short}.mp4"
        try:
            os.replace(dest, final)
            dest = str(final)
        except OSError:
            pass
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


def guess_episodes(out_dir: str, episodes, tol: int = 2):
    """按时长猜测每个缓存可能是哪一集 —— **只返回候选，不改名**。

    ★ 为什么不敢改名：实测过一次，``218s`` 在《奇趣百科》里"唯一匹配"到
      第 3 集，但抽帧一看画面是另一部剧（讲软件工程师的）。缓存目录里
      混着多部剧，按时长跨剧撞车很正常。
    ⇒ 只给候选，让人自己看缩略图确认。

    返回 ``[(文件名, [候选说明, ...]), ...]``。
    """
    import re as _re
    from pathlib import Path as _Path

    out = _Path(out_dir)
    by_dur: dict[int, list] = {}
    for ep in episodes:
        d = int(round(getattr(ep, "duration", 0) or 0))
        if d:
            by_dur.setdefault(d, []).append(ep)

    rows = []
    for f in sorted(out.glob("cache_*s_*x*_*.mp4")):
        m = _re.match(r"cache_(\d+)s_(\d+)x(\d+)_([0-9a-f]{8})\.mp4$", f.name)
        if not m:
            continue
        dur = int(m.group(1))
        cands = []
        for k in range(dur - tol, dur + tol + 1):
            cands.extend(by_dur.get(k, []))
        if not cands:
            continue
        desc = []
        for ep in cands[:4]:
            idx = int(getattr(ep, "index", 0) or 0)
            title = str(getattr(ep, "title", ""))[:26]
            desc.append(f"[{idx:03d}] {title}")
        rows.append((f.name, desc))
    return rows


def make_thumbs(out_dir: str, at: float = 5.0, width: int = 360) -> int:
    """给每个 mp4 抽一帧当缩略图（``<名字>.jpg``）。返回生成数量。

    ★ 这是用来「看画面认剧」的：缓存文件名只有 md5，时长又不可靠，
      真正能确认内容的只有画面本身。
    """
    out = Path(out_dir)
    made = 0
    for mp4 in sorted(out.glob("*.mp4")):
        jpg = mp4.with_suffix(".jpg")
        if jpg.exists():
            continue
        try:
            subprocess.run(
                ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                 "-ss", str(at), "-i", str(mp4), "-frames:v", "1",
                 "-vf", f"scale={width}:-2", str(jpg)],
                capture_output=True, timeout=120,
            )
            if jpg.exists():
                made += 1
        except (FileNotFoundError, subprocess.SubprocessError):
            break
    return made


def write_index_html(out_dir: str) -> str:
    """生成缩略图索引页（浏览器直接打开，看画面挑片子）。"""
    out = Path(out_dir)
    items = []
    for mp4 in sorted(out.glob("*.mp4"), key=lambda p: -p.stat().st_size):
        jpg = mp4.with_suffix(".jpg")
        has_img = jpg.exists()
        img = f'<img src="{jpg.name}" loading="lazy">' if has_img else ""
        bad = "" if has_img else '<div class="warn">⚠ 抽不出画面：缓存可能只写了一半（播放器边播边存，退出早了就截断）</div>' 
        mb = mp4.stat().st_size / 1048576
        info = probe(str(mp4))
        dur = float(info.get("duration") or 0)
        res = f'{info.get("width", 0)}x{info.get("height", 0)}'
        items.append(
            f'<div class="card">{img}'
            f'<div class="meta"><div class="name">{mp4.name}</div>'
            f'<div class="sub">{dur:.0f}s · {res} · {mb:.1f} MB</div>{bad}</div></div>')

    html = """<!doctype html><meta charset="utf-8">
<title>红果缓存 · 缩略图索引</title>
<style>
body{background:#111;color:#ddd;font:14px/1.5 system-ui,"Microsoft YaHei";margin:24px}
h1{font-size:18px;font-weight:600;margin:0 0 16px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:16px}
.card{background:#1b1b1b;border:1px solid #2a2a2a;border-radius:8px;overflow:hidden}
.card img{width:100%;display:block;background:#000}
.meta{padding:8px 10px}
.name{font-size:12px;color:#8ab4f8;word-break:break-all}
.sub{font-size:12px;color:#888;margin-top:2px}
.warn{font-size:12px;color:#e0a030;margin-top:6px}
</style>
<h1>红果缓存 · __COUNT__ 个（点开视频看画面确认是哪一集）</h1>
<div class="grid">__ITEMS__</div>
"""
    # ★ 不能用 % 格式化：上面的 CSS 里有 ``width:100%``，会被当成格式符。
    html = html.replace("__COUNT__", str(len(items))).replace("__ITEMS__", "".join(items))
    p = out / "_index.html"
    p.write_text(html, encoding="utf-8")
    return str(p)
