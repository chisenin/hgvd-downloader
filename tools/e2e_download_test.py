"""端到端下载验证：解析播放地址 → 下载 → ffprobe 校验。

★ 用途：一次性回答「现在到底能不能下」，
  并把「解析失败 / 下载失败 / 拿到 10 秒片段」三种情况区分开。

用法：
    python -m tools.e2e_download_test --vid 7700851630874315800 --type 1
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

from hgvd.service import EpisodeItem, HongguoService

OUT_ROOT = Path(os.environ.get("HGVD_TEST_OUT", str(Path.home() / "Videos" / "_probe")))


def ffprobe(path: Path) -> str:
    try:
        r = subprocess.run(
            ["ffprobe", "-v", "quiet", "-print_format", "json",
             "-show_format", "-show_streams", str(path)],
            capture_output=True, text=True, timeout=60,
        )
        return r.stdout[:1200]
    except Exception as e:  # noqa: BLE001
        return f"ffprobe 失败：{e}"


def main(argv=None):
    p = argparse.ArgumentParser(description="端到端下载验证")
    p.add_argument("--vid", default="7700851630874315800")
    p.add_argument("--type", type=int, default=1)
    p.add_argument("--quality", default="720p")
    p.add_argument("--title", default="端到端验证")
    p.add_argument("--duration", type=float, default=265.0)
    args = p.parse_args(argv)

    svc = HongguoService()
    print(f"登录态: {svc.logged_in}  cookie: {sorted(svc.cookie_dict())}")
    ident = svc.device_identity()
    print(f"设备: device_id={ident.device_id} iid={ident.iid}")

    t0 = time.time()
    url, size, key, reason = svc.resolve_full_url(args.vid, args.quality, video_id_type=args.type)
    print(f"解析播放地址（type={args.type}）: {'成功' if url else '失败'}  "
          f"用时 {time.time()-t0:.2f}s")
    if not url:
        print(f"!! 没拿到地址 —— {reason or '播放接口不通'}")
        return 1
    print(f"  url    : {url[:120]}")
    print(f"  size   : {size}   content_key: {key or '(无，不加密)'}")

    ep = EpisodeItem(vid=args.vid, index=1, title=args.title,
                     duration=args.duration, series_id=args.vid,
                     video_id_type=args.type)
    out = OUT_ROOT / f"{args.vid}.mp4"
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()

    t0 = time.time()
    res = svc.download_episode(ep, quality=args.quality)
    dt = time.time() - t0
    print(f"\n下载: ok={res.ok} 用时 {dt:.1f}s 大小={res.size} 错误={res.error or '-'}")
    print(f"  输出: {res.out_path}")

    p2 = Path(res.out_path)
    if p2.exists():
        real = p2.stat().st_size
        print(f"  实际文件大小: {real} 字节 ({real/1024/1024:.1f} MB)")
        print("  ffprobe:")
        print(ffprobe(p2))
    return 0 if res.ok else 1


if __name__ == "__main__":
    sys.exit(main())
