"""一键自检：把整条链路的每一环跑一遍，打印结果。

用法::

    python tools/selftest.py

★ 这是验证「服务端是否还在用同一套协议」的快捷方式 ——
  服务端改版后，先跑它，看是哪一环开始不对。
"""

from __future__ import annotations

import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from hgvd import net  # noqa: E402
from hgvd.api import detail as detail_api  # noqa: E402
from hgvd.api import playback as pb  # noqa: E402
from hgvd.api import shelf as sh  # noqa: E402
from hgvd.api import web as web  # noqa: E402
from hgvd.auth import load_account  # noqa: E402
from hgvd.device import ensure_device  # noqa: E402

TEST_SERIES = "7697459724635098174"   # 网页版首页上的真实剧目


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
        mark(True, f"接口通，收藏 {len(rows)} 项（短剧视频 {len(drama)} 部，"
                   f"图书/听书 {len(rows) - len(drama)} 部）")
    except Exception as e:  # noqa: BLE001
        mark(False, str(e))

    # 5 批量详情（带签名）
    head(5, "批量详情（带签名，补标题与分集）")
    details = {}
    if rows:
        try:
            details = detail_api.fetch_details(
                sess, ident, [r["series_id"] for r in rows[:40]], cookies=cookies
            )
            titled = [d for d in details.values() if d.get("title")]
            mark(bool(details), f"取到 {len(details)} 部详情（含标题 {len(titled)} 部）")
            for d in list(details.values())[:3]:
                if d.get("title"):
                    print(f"      · {d['title'][:26]:28s} {d.get('episode_cnt', 0)} 集 "
                          f"分集 {len(d.get('episodes') or [])}")
        except Exception as e:  # noqa: BLE001
            mark(False, str(e))

    # 6 播放地址
    head(6, "App 网关 video_model（需完整签名 + 登录）")
    sample = next((d for d in details.values() if d.get("episodes")), None)
    if not acc.ok:
        mark(False, "跳过（未登录）")
    elif not sample:
        mark(False, "跳过（收藏里没取到带分集的内容）")
    else:
        vid = sample["episodes"][0]["vid"]
        try:
            payload = pb.fetch_playback(sess, ident, vid, cookies=cookies)
            streams = pb.parse_streams(payload)
            key = pb.extract_content_key(payload)
            mark(bool(streams),
                 f"{len(streams)} 档清晰度，codec="
                 f"{streams[0]['codec'] if streams else '?'}，"
                 f"内容密钥={'有' if key else '无'}")
            if streams:
                print(f"      · {streams[0]['definition']} {streams[0]['size']} 字节 "
                      f"加密={streams[0]['encrypted']}")
        except Exception as e:  # noqa: BLE001
            mark(False, f"{e}（空 body = 限流或未登录）")

    print(f"\n===== 通过 {ok} 项，失败 {bad} 项 =====")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
