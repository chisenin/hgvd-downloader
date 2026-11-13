"""Tkinter 图形界面。

★ 用标准库 Tkinter，**不引入 PySide6** —— 参考项目用 PySide6 时打包后
exe 膨胀到 330 MB（完整 PySide6 元包带上 QtWebEngine 等 Addons）。
Tkinter 是 Python 自带，打包后几乎不增加体积。

界面：
    左栏  收藏夹列表（多选）
    右栏  选中剧目的分集列表（多选）
    底部  下载任务表 + 日志
"""

from __future__ import annotations

import threading
import tkinter as tk
from tkinter import messagebox, ttk

from ..config import Config
from ..engine.downloader import CancelToken, DownloadCallbacks
from ..service import HongguoService


class DownloaderApp:
    def __init__(self):
        self.cfg = Config.load()
        self.svc = HongguoService(self.cfg)
        self.cancel = CancelToken()
        self.worker = None
        self.shelf_items = []
        self.episodes = {}

        self.root = tk.Tk()
        self.root.title("红果视频下载器")
        self.root.geometry("1180x760")
        self.root.minsize(900, 600)

        self._build()
        self.root.after(100, self._tick)

    # ---------------------------------------------------------- 界面

    def _build(self):
        top = ttk.Frame(self.root, padding=(8, 6))
        top.pack(fill="x")
        ttk.Label(top, text="红果视频下载器", font=("Microsoft YaHei UI", 13, "bold")).pack(
            side="left"
        )
        self.auth_label = ttk.Label(top, text="未登录")
        self.auth_label.pack(side="left", padx=14)
        ttk.Button(top, text="重新登录", command=self.on_login).pack(side="right", padx=4)
        ttk.Button(top, text="刷新收藏夹", command=self.on_refresh).pack(side="right", padx=4)

        # 参数条
        opts = ttk.Frame(self.root, padding=(8, 0))
        opts.pack(fill="x")
        ttk.Label(opts, text="下载目录：").pack(side="left")
        self.out_var = tk.StringVar(value=str(self.cfg.ensure_out_dir()))
        ttk.Entry(opts, textvariable=self.out_var, width=42).pack(side="left")
        ttk.Label(opts, text="  清晰度：").pack(side="left")
        self.q_var = tk.StringVar(value=self.cfg.quality)
        ttk.Combobox(
            opts, textvariable=self.q_var, width=7,
            values=["360p", "720p", "1080p"], state="readonly",
        ).pack(side="left")
        ttk.Label(opts, text="  每部最多：").pack(side="left")
        self.max_var = tk.StringVar(value=str(self.cfg.max_episodes))
        ttk.Spinbox(opts, from_=0, to=9999, textvariable=self.max_var, width=6).pack(
            side="left"
        )
        ttk.Label(opts, text="集").pack(side="left", padx=(2, 12))
        ttk.Button(opts, text="开始下载", command=self.on_download).pack(side="left", padx=4)
        ttk.Button(opts, text="停止", command=self.on_stop).pack(side="left", padx=4)

        # 主区
        paned = ttk.PanedWindow(self.root, orient="horizontal")
        paned.pack(fill="both", expand=True, padx=8, pady=6)

        left = ttk.Frame(paned)
        paned.add(left, weight=1)
        ttk.Label(left, text="收藏夹").pack(anchor="w")
        self.shelf_list = tk.Listbox(
            left, selectmode="extended", exportselection=False, height=20
        )
        self.shelf_list.pack(fill="both", expand=True, pady=(2, 4))
        self.shelf_list.bind("<<ListboxSelect>>", self.on_shelf_select)
        ttk.Label(left, text="提示：按住 Ctrl / Shift 多选").pack(anchor="w")

        right = ttk.Frame(paned)
        paned.add(right, weight=2)
        ttk.Label(right, text="分集").pack(anchor="w")
        self.ep_list = tk.Listbox(
            right, selectmode="extended", exportselection=False, height=20
        )
        self.ep_list.pack(fill="both", expand=True, pady=(2, 4))
        self.ep_list.bind("<<ListboxSelect>>", self.on_ep_select)

        # 任务表
        task_frame = ttk.LabelFrame(self.root, text="下载任务", padding=4)
        task_frame.pack(fill="both", expand=False, padx=8, pady=(0, 4))
        cols = ("state", "series", "episode", "size", "speed", "percent")
        self.task_tree = ttk.Treeview(task_frame, columns=cols, show="headings", height=9)
        for c, t, w in (
            ("state", "状态", 90), ("series", "剧名", 220),
            ("episode", "集", 150), ("size", "大小", 100),
            ("speed", "速度", 100), ("percent", "进度", 160),
        ):
            self.task_tree.heading(c, text=t)
            self.task_tree.column(c, width=w, anchor="w")
        self.task_tree.pack(fill="both", expand=True)

        # 日志
        log_frame = ttk.LabelFrame(self.root, text="日志", padding=4)
        log_frame.pack(fill="both", expand=True, padx=8, pady=(0, 6))
        self.log_text = tk.Text(log_frame, height=6, state="disabled")
        self.log_text.pack(fill="both", expand=True)

        self._update_auth()

    # ---------------------------------------------------------- 状态

    def _update_auth(self):
        if self.svc.logged_in:
            name = self.svc.account.user_name or "已登录"
            self.auth_label.config(text=f"已登录：{name}")
        else:
            self.auth_label.config(text="未登录")

    def log(self, msg: str):
        self.root.after(0, self._append_log, msg)

    def _append_log(self, msg: str):
        self.log_text.config(state="normal")
        self.log_text.insert("end", msg + "\n")
        self.log_text.see("end")
        self.log_text.config(state="disabled")

    # ---------------------------------------------------------- 动作

    def on_login(self):
        def work():
            from ..auth.login import login_by_qrcode, save_account

            try:
                self.log("正在获取二维码…")
                acc = login_by_qrcode(show=lambda qr: self.log("二维码已生成，请在浏览器/终端查看"))
                save_account(acc)
                self.svc.set_account(acc)
                self.log(f"登录成功：{acc.user_name}")
                self.root.after(0, self._update_auth)
            except Exception as e:  # noqa: BLE001
                self.log(f"登录失败：{e}")

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def on_refresh(self):
        def work():
            try:
                items = self.svc.list_shelf()
                self.shelf_items = items
                self.root.after(0, self._fill_shelf, items)
                self.log(f"收藏夹共 {len(items)} 部")
            except Exception as e:  # noqa: BLE001
                self.log(f"读取收藏夹失败：{e}")
                self.root.after(
                    0, lambda: messagebox.showerror("错误", f"读取收藏夹失败：{e}")
                )

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def _fill_shelf(self, items):
        self.shelf_list.delete(0, "end")
        for it in items:
            self.shelf_list.insert("end", f"{it.title}（{it.episode_cnt} 集）")

    def on_shelf_select(self, _evt):
        sel = self.shelf_list.curselection()
        if not sel:
            return

        def work():
            for idx in sel:
                item = self.shelf_items[idx]
                try:
                    eps = self.svc.list_episodes(item.series_id)
                    self.episodes[item.series_id] = eps
                    self.log(f"{item.title}：{len(eps)} 集")
                except Exception as e:  # noqa: BLE001
                    self.log(f"{item.title} 取分集失败：{e}")
            self.root.after(0, self._fill_eps)

        threading.Thread(target=work, daemon=True).start()

    def _fill_eps(self):
        self.ep_list.delete(0, "end")
        for idx in self.shelf_list.curselection():
            item = self.shelf_items[idx]
            eps = self.episodes.get(item.series_id) or []
            for e in eps:
                self.ep_list.insert("end", f"{e.index}. {e.title}")

    def on_ep_select(self, _evt):
        pass

    def on_download(self):
        if self.worker and self.worker.is_alive():
            messagebox.showinfo("提示", "上一个任务还在跑")
            return
        sel = self.shelf_list.curselection()
        if not sel:
            messagebox.showinfo("提示", "请先在左边选剧")
            return
        ep_sel = set(self.ep_list.curselection())
        if not ep_sel:
            messagebox.showinfo("提示", "请在右边选集（不选 = 全部）")
            return

        self.cfg.out_dir = self.out_var.get().strip()
        self.cfg.quality = self.q_var.get()
        try:
            self.cfg.max_episodes = int(self.max_var.get() or 0)
        except ValueError:
            self.cfg.max_episodes = 0
        self.cfg.save()

        self.cancel = CancelToken()
        task_rows = []

        def work():
            for idx in sel:
                item = self.shelf_items[idx]
                eps = self.episodes.get(item.series_id) or []
                chosen = [e for i, e in enumerate(eps) if i in ep_sel] if ep_sel else eps
                if self.cfg.max_episodes > 0:
                    chosen = chosen[: self.cfg.max_episodes]
                for e in chosen:
                    if self.cancel.cancelled:
                        return
                    row = (item.title, e.title)
                    task_rows.append(row)
                    self.root.after(0, self._add_task, row)
                    cb = DownloadCallbacks()
                    cb.on_phase = lambda p, r=row: self.root.after(
                        0, self._set_task, r, p, "", 0, 0
                    )
                    cb.on_progress = lambda d, t, s, r=row: self.root.after(
                        0, self._set_task, r, "下载中", d, t, s
                    )
                    try:
                        res = self.svc.download_episode(e, cb=cb, cancel=self.cancel)
                        if res.ok:
                            self.root.after(
                                0, self._set_task, row, "已完成", res.size, res.size, 0
                            )
                            self.log(f"✓ {item.title} {e.title} → {res.out_path}")
                        else:
                            self.root.after(
                                0, self._set_task, row, "失败", 0, 0, 0, res.error
                            )
                            self.log(f"✗ {item.title} {e.title}：{res.error}")
                    except Exception as ex:  # noqa: BLE001
                        self.root.after(0, self._set_task, row, "失败", 0, 0, 0, str(ex))
                        self.log(f"✗ {item.title} {e.title}：{ex}")

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def on_stop(self):
        self.cancel.cancel()
        self.log("已请求停止")

    # ---------------------------------------------------------- 任务表

    def _add_task(self, row):
        self.task_tree.insert("", "end", iid=f"{row[0]}||{row[1]}",
                              values=("排队中", row[0], row[1], "-", "-", "0%"))

    def _set_task(self, row, state, down, total, speed, error=""):
        iid = f"{row[0]}||{row[1]}"
        if not self.task_tree.exists(iid):
            self._add_task(row)
        pct = int(down * 100 / total) if total else 0
        size = f"{down/1048576:.1f} MB" if down else "-"
        spd = f"{speed/1048576:.1f} MB/s" if speed else "-"
        text = error if error else f"{pct}%"
        self.task_tree.item(
            iid, values=(state, row[0], row[1], size, spd, text)
        )

    def _tick(self):
        self.root.after(500, self._tick)

    def run(self):
        self.root.mainloop()


def main():
    DownloaderApp().run()


if __name__ == "__main__":
    main()
