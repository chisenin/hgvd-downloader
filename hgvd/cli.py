"""命令行入口。

用法::

    python -m hgvd login                 # 扫码登录
    python -m hgvd shelf                 # 列出收藏夹
    python -m hgvd list <series_id>      # 列出某部剧的分集
    python -m hgvd download <series_id>  # 下载某部剧的全部集
    python -m hgvd download --all        # 下载收藏夹里全部剧
    python -m hgvd history               # 查看下载历史
"""

from __future__ import annotations

import argparse
import os
import sys
import time

from . import __version__
from .api import web as web_api
from .auth import (
    clear_account,
    load_account,
    login_by_qrcode,
    make_login_session,
    save_account,
)
from .config import Config
from .engine.downloader import CancelToken, DownloadCallbacks
from .service import HongguoService
from .util import fmt_duration, fmt_size


def _svc() -> HongguoService:
    return HongguoService(Config.load())


# ---------------------------------------------------------------- 子命令


def cmd_login(args) -> int:
    """扫码登录。二维码存到当前目录并自动打开。"""
    from .auth.login import login_by_qrcode, make_login_session, save_account

    print("正在获取二维码…")
    sess = make_login_session()
    qr_path = os.path.abspath("hongguo_qrcode.png")
    try:
        acc = login_by_qrcode(sess, show=lambda qr: save_qr_png(qr, qr_path))
    except Exception as e:  # noqa: BLE001
        print(f"登录失败：{e}")
        return 1
    save_account(acc)
    print(f"登录成功：{acc.user_name or '(未取到昵称)'}  uid={acc.user_id}")
    print(f"二维码已保存：{qr_path}（可删除）")
    return 0


def save_qr_png(qr: dict, path: str) -> None:
    """存二维码 PNG 并尝试用系统默认程序打开。"""
    from .auth.login import save_qrcode_png

    if qr.get("qrcode_b64"):
        save_qrcode_png(qr["qrcode_b64"], path)
        print(f"\n二维码已保存：{path}")
        print("请用「抖音」App 扫描该二维码（红果 App 亦可），并在手机上确认。")
        try:
            if sys.platform == "win32":
                os.startfile(path)  # noqa: S606
            elif sys.platform == "darwin":
                import subprocess

                subprocess.run(["open", path], check=False)
        except Exception:  # noqa: BLE001
            pass
    print("等待扫码…", flush=True)


def cmd_shelf(args) -> int:
    """列出收藏夹。"""
    svc = _svc()
    if not svc.logged_in:
        print("未登录。先执行：python -m hgvd login")
        print("（收藏夹接口本身免签名，但没有 Cookie 会返回空列表）")
        return 1
    try:
        items = svc.list_shelf()
    except Exception as e:  # noqa: BLE001
        print(f"读取收藏夹失败：{e}")
        return 1
    if not items:
        print("收藏夹为空。")
        return 0
    print(f"收藏夹（{len(items)} 部）：")
    for i, it in enumerate(items, 1):
        print(f"  [{i:2d}] {it.title}  (id={it.series_id}, {it.episode_cnt} 集)")
    return 0


def cmd_list(args) -> int:
    """列出某部剧的分集。"""
    svc = _svc()
    try:
        eps = svc.list_episodes(args.series_id, max_count=args.limit or 0)
    except Exception as e:  # noqa: BLE001
        print(f"取分集失败：{e}")
        return 1
    if not eps:
        print("没有分集。")
        return 0
    print(f"共 {len(eps)} 集：")
    for e in eps:
        print(f"  {e.index:3d}. {e.title}  (vid={e.vid})")
    return 0


def cmd_download(args) -> int:
    """下载。"""
    svc = _svc()
    cfg = svc.cfg
    if args.out:
        cfg.out_dir = args.out
        cfg.save()
    if args.quality:
        cfg.quality = args.quality

    if args.all:
        try:
            items = svc.list_shelf()
        except Exception as e:  # noqa: BLE001
            print(f"读取收藏夹失败：{e}")
            return 1
        if not items:
            print("收藏夹为空，没有可下载内容。")
            return 0
        targets = items
    else:
        if not args.series_id:
            print("请给 series_id，或用 --all 下载收藏夹全部。")
            return 2
        # 用网页版详情拿剧名
        try:
            s, _ = web_api.fetch_series_detail(
                svc.session, args.series_id, cookies=svc.cookie_dict()
            )
        except Exception as e:  # noqa: BLE001
            print(f"取剧详情失败：{e}")
            return 1
        targets = [
            type(
                "X", (), {"series_id": s.series_id, "title": s.title,
                          "episode_cnt": s.episode_cnt, "key": s.series_id}
            )()
        ]

    total_ok = total_fail = 0
    for t in targets:
        print(f"\n=== {t.title} ({t.series_id}) ===")
        try:
            eps = svc.list_episodes(t.series_id, max_count=args.limit or cfg.max_episodes)
        except Exception as e:  # noqa: BLE001
            print(f"  取分集失败：{e}")
            total_fail += 1
            continue

        if args.episodes:
            want = set()
            for part in args.episodes.split(","):
                part = part.strip()
                if "-" in part:
                    a, _, b = part.partition("-")
                    try:
                        want.update(range(int(a), int(b) + 1))
                    except ValueError:
                        pass
                elif part.isdigit():
                    want.add(int(part))
            eps = [e for e in eps if e.index in want]

        for e in eps:
            try:
                res = svc.download_episode(e, cb=_console_cb())
            except Exception as ex:  # noqa: BLE001
                print(f"  [{e.index}] 失败：{ex}")
                total_fail += 1
                continue
            if res.ok:
                if res.skipped:
                    print(f"  [{e.index}] 已存在 {res.out_path}")
                else:
                    print(
                        f"  [{e.index}] 完成 {fmt_size(res.size)} "
                        f"{fmt_duration(res.duration)} → {res.out_path}"
                    )
                total_ok += 1
            else:
                print(f"  [{e.index}] 失败：{res.error}")
                total_fail += 1

    print(f"\n完成 {total_ok}，失败 {total_fail}")
    return 0 if total_fail == 0 else 1


def _console_cb() -> DownloadCallbacks:
    """控制台进度回调。"""
    cb = DownloadCallbacks()
    state = {"t": 0.0}

    def _phase(p):
        print(f"  · {p}…")

    def _progress(down, total, speed):
        now = time.time()
        if now - state["t"] < 0.5 and down != total:
            return
        state["t"] = now
        pct = int(down * 100 / total) if total else 0
        print(f"\r    {pct:3d}%  {fmt_size(down)}/{fmt_size(total)}  "
              f"{fmt_size(speed)}/s   ", end="", flush=True)
        if total and down >= total:
            print()

    cb.on_phase = _phase
    cb.on_progress = _progress
    return cb


def cmd_gui(args) -> int:
    """打开图形界面。"""
    from .gui.app import main as gui_main
    gui_main()
    return 0


def cmd_history(args) -> int:
    """查看下载历史。"""
    svc = _svc()
    items = svc.history.sort(svc.history.load())
    if not items:
        print("没有历史记录。")
        return 0
    print(f"历史（{len(items)} 条）：")
    for r in items[:50]:
        print(f"  [{r.state:6s}] {r.title} / {r.episode}  "
              f"{fmt_size(r.size)}  {r.path}")
    return 0


def cmd_logout(args) -> int:
    """清除登录态。"""
    clear_account()
    print("已清除本机登录态。")
    return 0


# ---------------------------------------------------------------- main


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="hgvd", description="红果视频下载器 —— 从收藏夹批量下载剧目"
    )
    p.add_argument("--version", action="version", version=f"hgvd {__version__}")
    sub = p.add_subparsers(dest="cmd")

    sub.add_parser("login", help="扫码登录").set_defaults(func=cmd_login)
    sub.add_parser("shelf", help="列出收藏夹").set_defaults(func=cmd_shelf)
    sub.add_parser("history", help="查看下载历史").set_defaults(func=cmd_history)
    sub.add_parser("logout", help="清除登录态").set_defaults(func=cmd_logout)
    sub.add_parser("gui", help="打开图形界面").set_defaults(func=cmd_gui)

    pl = sub.add_parser("list", help="列出某部剧的分集")
    pl.add_argument("series_id")
    pl.add_argument("--limit", type=int, default=0)

    pd = sub.add_parser("download", help="下载")
    pd.add_argument("series_id", nargs="?")
    pd.add_argument("--all", action="store_true", help="下载收藏夹里全部剧")
    pd.add_argument("--episodes", help="只下指定集，如 1-10 或 1,3,5")
    pd.add_argument("--limit", type=int, default=0, help="每部剧最多下几集")
    pd.add_argument("--out", help="下载目录")
    pd.add_argument("--quality", choices=["360p", "720p", "1080p"])
    return p


def main(argv=None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "cmd", None):
        parser.print_help()
        return 0
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
