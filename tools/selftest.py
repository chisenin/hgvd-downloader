"""一键自检：把整条链路的每一环跑一遍，打印结果。

用法::

    python tools/selftest.py

★ 这是验证「服务端是否还在用同一套协议」的快捷方式 ——
  服务端改版后，先跑它，看是哪一环开始不对。
"""

from __future__ import annotations

import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from hgvd import net  # noqa: E402
from hgvd.api import playback as pb  # noqa: E402
from hgvd.api import shelf as sh  # noqa: E402
from hgvd.api import web as web  # noqa: E402
from hgvd.auth import load_account  # noqa: E402
from hgvd.config import Config  # noqa: E402
from hgvd.device import ensure_device  # noqa: E402

TEST_SERIES = "7697459724635098174"   # 网页版首页上的真实剧目


def step(n, name):
    print(f"\n[{n}] {name}")


def main() -> int:
    ok = 0
    bad = 0
    sess = net.make_session()
    acc = load_account()
    ident = ensure_device()

    # 1
    step(1, "网页版详情（全集列表，免签名）")
    try:
        s, vids = web.fetch_series_detail(sess, TEST_SERIES)
        print(f"    ✓ {s.title} —— {len(vids)} 集")
        ok += 1
    except Exception as e:  # noqa: BLE001
        print(f"    ✗ {e}")
        bad += 1
        return report(ok, bad)

    # 2
    step(2, "网页版单集时长（免签名）")
    try:
        meta = web.fetch_episode_meta(sess, TEST_SERIES, vids[0])
        print(f"    ✓ {meta['duration']}s {meta['width']}x{meta['height']}")
        ok += 1
    except Exception as e:  # noqa: BLE001
        print(f"    ✗ {e}")
        bad += 1

    # 3
    step(3, "网页版免签名直链（★ 仅 10 秒试看片段）")
    try:
        url, size, ct = web.probe_video(sess, vids[0], "360p")
        if url:
            print(f"    ✓ {url}  ({size} bytes, {ct})")
            ok += 1
        else:
            print("    ✗ 没解析到直链")
            bad += 1
    except Exception as e:  # noqa: BLE001
        print(f"    ✗ {e}")
        bad += 1

    # 4
    step(4, "收藏夹接口（免签名，需要登录才有内容）")
    print(f"    登录状态：{'已登录 ' + (acc.user_name or '') if acc.ok else '未登录'}")
    try:
        rows = sh.list_shelf(sess, cookies=dict(acc.cookies) if acc.ok else None,
                             target_user_id=acc.user_id)
        print(f"    ✓ 接口通，收藏 {len(rows)} 部")
        for r in rows[:5]:
            print(f"      · {r['title']}（{r['episode_cnt']} 集） id={r['series_id']}")
        ok += 1
    except Exception as e:  # noqa: BLE001
        print(f"    ✗ {e}")
        bad += 1

    # 5
    step(5, "App 网关 video_model（需完整签名 + 登录）")
    if not acc.ok:
        print("    – 跳过（未登录）。先执行 python -m hgvd login")
    else:
        try:
            payload = pb.fetch_playback(
                sess, ident, vids[0], cookies=dict(acc.cookies)
            )
            streams = pb.parse_streams(payload)
            print(f"    ✓ 拿到 {len(streams)} 条清晰度")
            for st in streams[:4]:
                print(f"      · {st['definition']:8s} {st['url'][:90]}")
            key = pb.extract_content_key(payload)
            print(f"    内容密钥：{key or '（未加密）'}")
            ok += 1
        except Exception as e:  # noqa: BLE001
            print(f"    ✗ {e}")
            print("      （空 body = 匿名限流或签名被拒；先用参考实现交叉验证）")
            bad += 1

    return report(ok, bad)


def report(ok: int, bad: int) -> int:
    print(f"\n===== 通过 {ok} 项，失败 {bad} 项 =====")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
