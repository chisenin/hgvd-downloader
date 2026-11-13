"""一键自检：把整条链路的每一环跑一遍，打印结果。

用法::

    python tools/selftest.py

★ 这是验证「服务端是否还在用同一套协议」的快捷方式 ——
  服务端改版后，先跑它，看是哪一环开始不对。

★ 六环对应：
  1 登录态（MuMu 读 App）
  2 网页版详情（免签名）
  3 网页版直链（★ 仅 10 秒试看片段）
  4 收藏夹（免签名，需要 Cookie）
  5 合集分集（★ album_detail，book_type=3 的唯一路径）
  6 播放地址（★ 播放专用签名 + video_id_type）
"""

from __future__ import annotations

import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from hgvd import net  # noqa: E402
from hgvd.api import album as album_api  # noqa: E402
from hgvd.api import detail as detail_api  # noqa: E402
from hgvd.api import playback as pb  # noqa: E402
from hgvd.api import shelf as sh  # noqa: E402
from hgvd.api import web as web  # noqa: E402
from hgvd.auth import load_account  # noqa: E402
from hgvd.device import ensure_device  # noqa: E402

TEST_SERIES = "7697459724635098174"   # 网页版首页上的真实短剧
TEST_ALBUM = "7662738851323989016"   # 教学合集「颠覆你的动植物科普」


def main() -> int:
    ok = 0
    bad = 0
    sess = net.make_session()
    acc = load_account()
    ident = ensure_device()
    cookies = dict(acc.cookies) if acc.ok else None

    def head(n, name):
        print(f"\n[{n}] {name}")

    def mark(good, msg=""):
        nonlocal ok, bad
        if good:
            ok += 1
            print(f"    ✓ {msg}")
        else:
            bad += 1
            print(f"    ✗ {msg}")

    # 1 登录态
    head(1, "登录态")
    if acc.ok:
        mark(True, f"已登录（cookie: {', '.join(sorted(acc.cookies.keys()))}）")
    else:
        mark(False, "未登录 —— 先执行 python -m hgvd login（默认从 MuMu 读）")

    # 2 网页版详情
    head(2, "网页版详情（全集列表，免签名）")
    vids = []
    try:
        s, vids = web.fetch_series_detail(sess, TEST_SERIES)
        mark(True, f"{s.title} —— {len(vids)} 集")
    except Exception as e:  # noqa: BLE001
        mark(False, str(e))

    # 3 网页版直链
    head(3, "网页版免签名直链（★ 仅 10 秒试看片段）")
    try:
        url, size, ct = web.probe_video(sess, vids[0] if vids else "0", "360p")
        mark(bool(url), f"{url} ({size} bytes, {ct})" if url else "没解析到直链")
    except Exception as e:  # noqa: BLE001
        mark(False, str(e))

    # 4 收藏夹
    head(4, "收藏夹（免签名，需要登录才有内容）")
    print(f"    登录状态：{'已登录' if acc.ok else '未登录'}")
    rows = []
    try:
        rows = sh.list_shelf(sess, cookies=cookies, target_user_id=acc.user_id)
        drama = [r for r in rows if r.get("book_type") == 2]
        album = [r for r in rows if r.get("book_type") == 3]
        mark(True, f"接口通，收藏 {len(rows)} 项（短剧 {len(drama)} 部，"
                   f"教学合集 {len(album)} 部）")
    except Exception as e:  # noqa: BLE001
        mark(False, str(e))

    # 5 合集分集（★ album_detail）
    head(5, "合集分集（★ album_detail，book_type=3 的唯一路径）")
    eps: list = []
    if not acc.ok:
        mark(False, "跳过（未登录）")
    else:
        try:
            raw = detail_api.signed_post(
                sess, ident, detail_api.PATH_ALBUM,
                {"album_id": TEST_ALBUM, "book_type": 3}, cookies,
                quiet=True,
            )
            if raw:
                ad = album_api.parse_album(raw)
                eps = ad.episodes
                mark(bool(eps), f"「{ad.title}」{ad.episode_cnt} 集，取到 {len(eps)} 项")
                for e in eps[:3]:
                    print(f"      · {e.index if hasattr(e, 'index') else ''}"
                          f"{e.title[:30]:32s} {e.duration:.0f}s  id={e.series_id}")
            else:
                mark(False, "album_detail 无响应")
        except Exception as e:  # noqa: BLE001
            mark(False, str(e))

    # 6 播放地址（★ 播放专用签名 + video_id_type=1）
    head(6, "App 网关 video_model（★ 播放专用签名 + video_id_type）")
    if not acc.ok:
        mark(False, "跳过（未登录）")
    elif not eps:
        mark(False, "跳过（没取到合集分集）")
    else:
        target = eps[0].series_id
        try:
            payload = pb.fetch_playback(
                sess, ident, target, cookies=cookies, video_id_type=1
            )
            streams = pb.parse_streams(payload)
            key = pb.extract_content_key(payload)
            mark(bool(streams),
                 f"{len(streams)} 档清晰度，codec="
                 f"{streams[0]['codec'] if streams else '?'}，"
                 f"加密={'是' if streams and streams[0]['encrypted'] else '否'}，"
                 f"内容密钥={'有' if key else '无'}")
            if streams:
                for x in streams:
                    print(f"      · {x['definition']:8s} {x['codec']:10s} "
                          f"{x['width']}x{x['height']} {x['size']} 字节 "
                          f"加密={x['encrypted']}")
        except Exception as e:  # noqa: BLE001
            mark(False, f"{e}（★ 确认用的是 sign_playback 而不是 sign_request）")

    print(f"\n===== 通过 {ok} 项，失败 {bad} 项 =====")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
