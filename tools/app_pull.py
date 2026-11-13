"""「借 App 的播放，拿 App 的缓存」—— 半自动下载流水线。

★ 背景（文档 Y 章实测结论）：
  · 红果**没有**导出「下载指定剧集」的接口（downloader.db 97 条记录里
    只有 1 条 video/mp4，还是登录页背景素材）—— 官方不提供下载正片。
  · 深链 `dragon8662://` 能调起 App，但**跳不到指定剧集详情页**。
  · ★ 但 App 播放的内容会落成**标准 MP4** 缓存：
      /sdcard/Android/data/com.phoenix.read/cache/short/<md5>.mdl
    权限 `u0_a51:ext_data_rw` ⇒ 独立 App 可直接读（文档 Y11）。

★ 所以唯一能自动化的路径是：
    1. 深链调起红果
    2. **用户**在 App 里点开目标剧集（深链做不到这一步）
    3. App 播放 → 内容进缓存
    4. 我们把新增的 .mdl 提取出来
  本脚本负责 1、3（监视）、4，2 需要人工。

用法：
    python tools/app_pull.py --series 7662736695145548824 --watch 300
    python tools/app_pull.py --pull-only        # 只提取现有缓存
"""
from __future__ import annotations

import argparse
import base64
import re
import subprocess
import sys
import time
from pathlib import Path

MUMU = r"D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"
CACHE_DIRS = [
    "/sdcard/Android/data/com.phoenix.read/cache/short",
    "/sdcard/Android/data/com.phoenix.read/cache/dataloader",
]


def sh(instance: int, cmd: str, timeout: int = 300) -> str:
    p = subprocess.run([MUMU, "sh", "-v", str(instance), "-c", cmd],
                       capture_output=True, timeout=timeout)
    return p.stdout.decode("utf-8", errors="replace")


def list_cache(instance: int) -> dict[str, tuple[str, int]]:
    """返回 {文件名: (目录, 大小)}。"""
    out = sh(instance, "ls -l " + " ".join(CACHE_DIRS) + " 2>/dev/null")
    result: dict[str, tuple[str, int]] = {}
    cur = ""
    for line in out.splitlines():
        line = line.rstrip()
        if not line:
            continue
        if line.endswith(":") and line.startswith("/"):
            cur = line[:-1]
            continue
        m = re.match(r"^-(\S{9})\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s+(\S+\s+\S+)\s+(.+)$", line)
        if not m:
            continue
        name = m.group(7).strip()
        if name.endswith(".mdl"):
            result[name] = (cur or CACHE_DIRS[0], int(m.group(5)))
    return result


def pull(instance: int, remote: str, dest: Path) -> bool:
    p = subprocess.run([MUMU, "sh", "-v", str(instance), "-c", f"base64 '{remote}'"],
                       capture_output=True, timeout=1800)
    try:
        data = base64.b64decode(re.sub(rb"\s+", b"", p.stdout))
    except Exception as e:
        print(f"  ! 解码失败 {remote}: {e}")
        return False
    if not data:
        return False
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(data)
    return True


def probe(path: Path) -> str:
    try:
        p = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries",
             "stream=codec_type,codec_name,codec_tag_string,width,height",
             "-show_entries", "format=duration,size", "-of", "default=noprint_wrappers=1",
             str(path)],
            capture_output=True, text=True, timeout=60)
        return (p.stdout or "").strip()
    except FileNotFoundError:
        return "(无 ffprobe)"


def is_complete(path: Path, info: str = "") -> bool:
    """判断缓存文件是否**下载完成**。

    ★★★ 精确判据（实测推导）：App 是**流式边下边存**，
      MP4 的结构是 `ftyp` + `moov`（先写，含完整时长/轨道信息）
      + `free` + `mdat`（**边播边追加**）。
      ⇒ **mdat box 头里声明的总大小 > 实际剩余字节 ⇒ 文件没写完。**
      这个判据 100% 可靠，优于「有没有音视频轨」的启发式
      （因为 moov 里轨道信息是全的，未完成的文件同样能解析出音视频轨）。

    实测样例：
      131072 B 的文件 → mdat 声明 6450727，实际只剩 23864  ⇒ 未完成
    """
    try:
        import struct
        data = path.read_bytes()
    except OSError:
        return False

    i = 0
    n = len(data)
    while i + 8 <= n:
        size = struct.unpack_from(">I", data, i)[0]
        typ = data[i + 4:i + 8]
        if size == 0:
            size = n - i
        elif size == 1:
            if i + 16 > n:
                return False
            size = struct.unpack_from(">Q", data, i + 8)[0]
        if size < 8:
            return False
        if typ == b"mdat":
            # 声明的大小必须 <= 剩余字节数，才是完整的
            return size <= (n - i)
        i += size
    # 没有 mdat（纯音频/异常）→ 视为不完整
    return False


def mp4_expected_size(path: Path) -> int:
    """从 mdat 头读出完整文件的应有大小（用于报告还差多少）。"""
    try:
        import struct
        data = path.read_bytes()
        i = 0
        n = len(data)
        while i + 8 <= n:
            size = struct.unpack_from(">I", data, i)[0]
            typ = data[i + 4:i + 8]
            if size == 0:
                size = n - i
            elif size == 1:
                if i + 16 > n:
                    break
                size = struct.unpack_from(">Q", data, i + 8)[0]
            if size < 8:
                break
            if typ == b"mdat":
                return i + size
            i += size
    except OSError:
        pass
    return 0


def wait_stable(instance: int, name: str, d: str, tries: int = 6,
                interval: float = 3.0) -> tuple[str, int] | None:
    """等缓存文件停止增长后再提取。"""
    last = -1
    stable = 0
    for _ in range(tries):
        cache = list_cache(instance)
        if name not in cache:
            return None
        _, size = cache[name]
        if size == last and size > 0:
            stable += 1
            if stable >= 2:
                return d, size
        else:
            stable = 0
        last = size
        time.sleep(interval)
    return (d, last) if last > 0 else None


def main():
    ap = argparse.ArgumentParser(description="借 App 的播放 + 拿 App 的缓存")
    ap.add_argument("--instance", type=int, default=1)
    ap.add_argument("--out", default=str(Path.home() / "Downloads" / "hgvd_appcache"))
    ap.add_argument("--series", default="", help="剧集 id（用于深链调起 App）")
    ap.add_argument("--watch", type=int, default=0, help="监视秒数（期间新增缓存会被提取）")
    ap.add_argument("--pull-only", action="store_true", help="只提取现有缓存，不监视")
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    if args.series:
        url = f"dragon8662://detail?series_id={args.series}"
        print(f"→ 调起红果：{url}")
        r = sh(args.instance,
               f"am start -a android.intent.action.VIEW -c android.intent.category.BROWSABLE "
               f"-d '{url}' 2>&1 | head -3")
        print("  " + r.strip().replace("\n", "\n  "))
        print("★ 请在 App 里点开目标剧集并播放（深链跳不到指定剧集）。")

    before = set(list_cache(args.instance))
    print(f"当前缓存 {len(before)} 个 .mdl")

    deadline = time.time() + args.watch if args.watch > 0 else 0
    while True:
        if deadline and time.time() > deadline:
            break
        cache = list_cache(args.instance)
        new = [n for n in (set(cache) - before)]
        for name in sorted(new):
            d, size = cache[name]
            dest = out / name.replace(".mdl", ".mp4")
            # ★ 等文件写完（流式缓存会先写 128KB 分片）
            got = wait_stable(args.instance, name, d) if args.watch else (d, size)
            if got is None:
                continue
            d, size = got
            if pull(args.instance, f"{d}/{name}", dest):
                info = probe(dest)
                ok = is_complete(dest)
                codec = "?"
                m = re.search(r"codec_name=(\w+)", info)
                if m:
                    codec = m.group(1)
                dur = "?"
                m2 = re.search(r"duration=([\d.]+)", info)
                if m2:
                    dur = m2.group(1)
                if ok:
                    mark = "✓"
                    note = ""
                    before.add(name)
                else:
                    exp = mp4_expected_size(dest)
                    have = dest.stat().st_size
                    mark = "…"
                    note = f"  未写完（{have}/{exp} 字节，差 {exp-have}）"
                print(f"  {mark} {name}  {size}B  {codec}  {dur}s{note}")
        if deadline:
            time.sleep(4)
        else:
            break

    if args.pull_only or not args.watch:
        print("\n=== 提取全部现有缓存（只取已写完的） ===")
        cache = list_cache(args.instance)
        ok = skip = 0
        for name, (d, size) in sorted(cache.items()):
            dest = out / name.replace(".mdl", ".mp4")
            if dest.exists() and dest.stat().st_size > 0:
                skip += 1
                continue
            if pull(args.instance, f"{d}/{name}", dest):
                info = probe(dest)
                if is_complete(dest):
                    ok += 1
                    m = re.search(r"codec_name=(\w+)", info)
                    m2 = re.search(r"duration=([\d.]+)", info)
                    print(f"  ✓ {name}  {size}B  {m.group(1) if m else '?'}  "
                          f"{m2.group(1) if m2 else '?'}s")
                else:
                    # 不完整的删掉，避免误用
                    exp = mp4_expected_size(dest)
                    have = dest.stat().st_size
                    dest.unlink(missing_ok=True)
                    print(f"  … {name}  {have}/{exp} 字节  未写完（跳过）")
        print(f"提取 {ok} 个，跳过 {skip} 个（已存在）")

    print(f"\n完成，输出目录：{out}")


if __name__ == "__main__":
    main()
