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
from pathlib import Path

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
    """登录。

    ★ 默认从 MuMu 模拟器里的红果 App 读登录态 ——
      网页版二维码只有约 58 秒有效期，手机号登录又常触发滑块验证，
      这条路最快也最稳。
    """
    if args.qr:
        return _login_qr()
    if args.phone:
        return _login_phone()
    return _login_mumu()


def _login_mumu() -> int:
    """从 MuMu 里的红果 App 读取登录态。"""
    from .auth.mumu import login_from_mumu

    try:
        acc = login_from_mumu()
    except Exception as e:  # noqa: BLE001
        print(f"从模拟器读取登录态失败：{e}")
        print("其它方式：")
        print("  python -m hgvd login --phone   （手机号验证码，可能要过滑块）")
        print("  python -m hgvd login --qr       （扫码，二维码约 60 秒有效）")
        return 1
    print(f"已从模拟器读取登录态（cookie 字段：{', '.join(sorted(acc.cookies.keys()))}）")
    print("现在可以执行：python -m hgvd shelf")
    return 0


def _login_qr() -> int:
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
    return 0


def _login_phone() -> int:
    """手机号 + 短信验证码登录。"""
    from .auth.login import save_account
    from .auth.phone import PhoneLoginError, prepare, send_code, verify

    phone = args.phone or input("请输入手机号：").strip()
    sess = prepare()
    try:
        info = send_code(sess, phone)
    except PhoneLoginError as e:
        print(f"发送验证码失败：{e}")
        print("（红果有时会要求滑块验证 —— 那种情况请改用：python -m hgvd login --qr）")
        return 1
    print(f"验证码已发送到 {info['mobile']}")
    if info["retry_time"]:
        print(f"（{info['retry_time']} 秒内不要重复发送）")
    code = input("请输入验证码：").strip()
    try:
        acc = verify(sess, phone, code)
    except PhoneLoginError as e:
        print(f"登录失败：{e}")
        return 1
    save_account(acc)
    print(f"登录成功：{acc.user_name or '(未取到昵称)'}  uid={acc.user_id}")
    return 0


def save_qr_png(qr: dict, path: str) -> None:
    """存二维码 PNG 并尝试用系统默认程序打开。"""
    from .auth.login import save_qrcode_png

    if qr.get("qrcode_b64"):
        save_qrcode_png(qr["qrcode_b64"], path)
        print(f"\n二维码已保存：{path}")
        print("请用「抖音」App 扫描该二维码（红果 App 亦可），并在手机上确认。")
        print("⚠ 二维码只有约 60 秒有效期，请提前打开抖音的扫一扫。")
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
    videos = [i for i in items if i.is_video]
    books = [i for i in items if not i.is_video]
    print(f"收藏夹共 {len(items)} 项：短剧视频 {len(videos)} 部，"
          f"图书/听书 {len(books)} 部（无视频分集）")
    print()
    for i, it in enumerate(videos, 1):
        print(f"  [{i:2d}] {it.title}  ({it.episode_cnt} 集)  id={it.series_id}")
    if books:
        print("\n  —— 以下是图书/听书类，服务端没有视频分集 ——")
        for i, it in enumerate(books, 1):
            print(f"  [{i:2d}] {it.title}  (无视频)  id={it.series_id}")
    return 0


def cmd_list(args) -> int:
    """列出某部剧的分集。

    ★ 对 ``book_type=3`` 的教学合集，走 ``album_detail`` 端点
      （``preload`` 对这类永远返回 0 集）。
    """
    svc = _svc()
    eps: list = []
    if args.album:
        eps = svc.list_album_episodes(args.series_id)
    else:
        # ★ 先试合集端点 —— 教学类内容只有它能给出分集
        eps = svc.list_album_episodes(args.series_id)
        if not eps:
            try:
                eps = svc.list_episodes(args.series_id, max_count=args.limit or 0)
            except Exception as e:  # noqa: BLE001
                print(f"取分集失败：{e}")
                return 1
    if not eps:
        print("没有分集。")
        return 0
    title = eps[0].series_title if eps else ""
    print(f"共 {len(eps)} 集" + (f"（{title}）" if title else "") + "：")
    for e in eps:
        print(f"  {e.index:3d}. {e.title}  ({e.duration:.0f}s, vid={e.vid})")
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
        album_eps = {}
    else:
        if not args.series_id:
            print("请给 series_id，或用 --all 下载收藏夹全部。")
            return 2
        # ★ 先试合集端点（book_type=3 的教学内容只有它能给出分集），
        #   失败再回落到网页版详情。
        eps = svc.list_album_episodes(args.series_id)
        title = eps[0].series_title if eps else args.series_id
        album_eps = {args.series_id: eps} if eps else {}
        if not eps:
            try:
                s, _ = web_api.fetch_series_detail(
                    svc.session, args.series_id, cookies=svc.cookie_dict()
                )
                title = s.title
            except Exception as e:  # noqa: BLE001
                print(f"取剧详情失败：{e}")
                return 1
        targets = [
            type(
                "X", (), {"series_id": args.series_id, "title": title,
                          "episode_cnt": len(eps), "key": args.series_id}
            )()
        ]

    total_ok = total_fail = 0
    workers = args.workers or cfg.workers
    for t in targets:
        print(f"\n=== {t.title} ({t.series_id}) ===")
        # ★ 合集分集（已在上面取过）优先；否则走网页版
        eps = album_eps.get(t.series_id) or []
        if not eps:
            try:
                eps = svc.list_episodes(
                    t.series_id, max_count=args.limit or cfg.max_episodes
                )
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

        if not eps:
            print("  没有要下的集")
            continue

        print(f"  共 {len(eps)} 集，并发 {workers}")
        results = svc.download_series(
            t.series_id, eps, cb=_console_cb(), quality=cfg.quality, workers=workers
        )
        for ep, res in results:
            _print_result(ep, res)
            if res.ok:
                total_ok += 1
            else:
                total_fail += 1

    print(f"\n完成 {total_ok}，失败 {total_fail}")
    return 0 if total_fail == 0 else 1


def _console_cb() -> DownloadCallbacks:
    """控制台进度回调（多任务时按行刷新）。"""
    cb = DownloadCallbacks()
    state = {"t": 0.0}

    def _phase(p):
        print(f"  · {p}…", flush=True)

    def _progress(down, total, speed):
        now = time.time()
        if now - state["t"] < 0.5 and down != total:
            return
        state["t"] = now
        pct = int(down * 100 / total) if total else 0
        print(
            f"\r    {pct:3d}%  {fmt_size(down)}/{fmt_size(total)}  "
            f"{fmt_size(speed)}/s   ",
            end="", flush=True,
        )
        if total and down >= total:
            print(flush=True)

    cb.on_phase = _phase
    cb.on_progress = _progress
    return cb


def _print_result(ep, res):
    if res.ok:
        if res.skipped:
            print(f"  [{ep.index}] 已存在 {res.out_path}")
        else:
            print(
                f"  [{ep.index}] 完成 {fmt_size(res.size)} "
                f"{fmt_duration(res.duration)} → {res.out_path}"
            )
    else:
        print(f"  [{ep.index}] 失败：{res.error}")


def cmd_gui(args) -> int:
    """打开图形界面。"""
    from .gui.app import main as gui_main
    gui_main()
    return 0


def cmd_cache(args) -> int:
    """提取 MuMu 里红果 App 的视频缓存。

    ★ App 播放过的视频会缓存成**标准 MP4**（HEVC/AAC），
      这条路完全绕过服务端限流。
    """
    from .auth.cache_extract import extract_all, list_cache

    if args.list:
        entries = list_cache(args.instance)
        if not entries:
            print("没有缓存文件。")
            return 0
        print(f"缓存共 {len(entries)} 个视频：")
        for e in sorted(entries, key=lambda x: -x["size"]):
            print(f"  {e['size']:>10}  {e['mtime']}  {e['name']}")
        return 0

    out = args.out or str(Path.home() / "hgvd_cache")
    print(f"从 MuMu 实例 {args.instance} 提取缓存到 {out} …")
    res = extract_all(args.instance, out_dir=out, limit=args.limit or 0)
    print(f"\n共提取 {len(res)} 个视频。")
    return 0 if res else 1


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

    plogin = sub.add_parser("login", help="登录（默认手机号验证码）")
    plogin.add_argument("--qr", action="store_true", help="改用扫码登录（二维码约 60 秒有效）")
    plogin.add_argument("--phone", help="改用手机号验证码登录")
    plogin.add_argument("--list-mumu", action="store_true", help="列出所有 MuMu 实例")
    plogin.set_defaults(func=cmd_login)
    sub.add_parser("shelf", help="列出收藏夹").set_defaults(func=cmd_shelf)
    sub.add_parser("history", help="查看下载历史").set_defaults(func=cmd_history)
    sub.add_parser("logout", help="清除登录态").set_defaults(func=cmd_logout)
    sub.add_parser("gui", help="打开图形界面").set_defaults(func=cmd_gui)

    pc = sub.add_parser("cache", help="提取 App 视频缓存（绕过服务端限流）")
    pc.add_argument("--list", action="store_true", help="只列出缓存，不提取")
    pc.add_argument("--instance", type=int, default=1, help="MuMu 实例号")
    pc.add_argument("--out", help="输出目录")
    pc.add_argument("--limit", type=int, default=0, help="最多提取几个")
    pc.set_defaults(func=cmd_cache)

    pl = sub.add_parser("list", help="列出某部剧的分集")
    pl.add_argument("series_id")
    pl.add_argument("--limit", type=int, default=0)
    pl.add_argument("--album", action="store_true",
                    help="按「合集」查分集（book_type=3 的教学类内容用这个）")
    pl.set_defaults(func=cmd_list)

    pd = sub.add_parser("download", help="下载")
    pd.add_argument("series_id", nargs="?")
    pd.add_argument("--all", action="store_true", help="下载收藏夹里全部剧")
    pd.add_argument("--episodes", help="只下指定集，如 1-10 或 1,3,5")
    pd.add_argument("--limit", type=int, default=0, help="每部剧最多下几集")
    pd.add_argument("--out", help="下载目录")
    pd.add_argument("--quality", choices=["360p", "720p", "1080p"])
    pd.add_argument("--workers", type=int, default=0,
                    help="集级并发数（默认取配置）")
    pd.set_defaults(func=cmd_download)
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
