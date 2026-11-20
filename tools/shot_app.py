#!/usr/bin/env python
"""还原 Avalonia 主窗口并截图（可裁剪放大）。

用法：
  python tools/shot_app.py <title关键字> <out.bmp> [--crop=x,y,w,h] [--scale=N]
"""
import ctypes
import ctypes.wintypes as w
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
from shot_hwnd import grab  # noqa: E402

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

TARGET_TITLE = "红果视频下载器"


def find_main_window():
    """Avalonia 的窗口句柄不唯一（还有辅助窗/弹出窗），取标题匹配且面积最大的可见窗口。"""
    best = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if TARGET_TITLE not in buf.value:
            return True
        r = w.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        best.append((hwnd, (r.right - r.left) * (r.bottom - r.top)))
        return True

    user32.EnumWindows(cb, 0)
    if not best:
        raise SystemExit("没找到标题含 %r 的窗口" % TARGET_TITLE)
    best.sort(key=lambda x: x[1], reverse=True)
    return best[0][0]


def restore(hwnd, x=40, y=30, w_=1500, h_=860):
    user32.ShowWindow(hwnd, 9)                     # SW_RESTORE
    user32.SetWindowPos(hwnd, -1, x, y, w_, h_, 0x0004)   # HWND_TOPMOST + SWP_SHOWWINDOW
    user32.SetForegroundWindow(hwnd)
    time.sleep(1.5)
    r = w.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print("窗口位置: (%d,%d)-(%d,%d)  iconic=%s" % (r.left, r.top, r.right, r.bottom,
                                                     user32.IsIconic(hwnd)))


if __name__ == "__main__":
    out = sys.argv[1]
    crop = None
    scale = 1
    w_ = 1500          # ★ 可用 --w= / --h= 指定窗口尺寸（验证「最小窗口」布局时用）
    h_ = 860
    for a in sys.argv[2:]:
        if a.startswith("--crop="):
            crop = tuple(int(v) for v in a.split("=", 1)[1].split(","))
        elif a.startswith("--scale="):
            scale = int(a.split("=", 1)[1])
        elif a.startswith("--w="):
            w_ = int(a.split("=", 1)[1])
        elif a.startswith("--h="):
            h_ = int(a.split("=", 1)[1])
    hwnd = find_main_window()
    print("hwnd=%s" % hwnd)
    restore(hwnd, w_=w_, h_=h_)
    grab(hwnd, out, crop=crop, scale=scale)
