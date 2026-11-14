"""对比 WPF 版与 Avalonia 版的真实运行开销：启动耗时 + 内存（工作集）。

★ 数据来源：
  · 窗口创建耗时 = 进程启动 → 该 pid 出现可见主窗口
  · 工作集 = `tasklist /FO CSV` 的 Mem Usage（= 任务管理器里的"内存"）
  · 收藏夹读完 = gui-diag.log 里 "StartupAsync 开始" → "收藏夹读完" 的毫秒差

用法： python -m tools.compare_gui_runtime
"""

from __future__ import annotations

import csv
import ctypes
import ctypes.wintypes as wt
import io
import subprocess
import time
from pathlib import Path

user32 = ctypes.WinDLL("user32", use_last_error=True)

DIAG = Path.home() / ".hgv_tool" / "gui-diag.log"

TARGETS = [
    ("C#+WPF", Path(r"D:\.workbuddy\2026-11-13-17-51-19\hgvd-csharp\dist\hgvd-downloader-v1.0.0.exe")),
    ("C#+Avalonia", Path(r"D:\.workbuddy\2026-11-13-17-51-19\hgvd-avalonia\dist\hgvd-downloader-v1.0.0.exe")),
]


def has_window(pid: int) -> bool:
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return bool(found)


def tasklist_mem(pid: int) -> float:
    """用 tasklist 读该进程的工作集（MB）。"""
    out = subprocess.run(
        ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, errors="replace").stdout
    for row in csv.reader(io.StringIO(out)):
        if len(row) < 5 or row[1] != str(pid):
            continue
        raw = row[4].replace(",", "").replace(" K", "").replace(" M", "")
        unit = row[4].strip()[-1].upper()
        try:
            val = float(raw)
        except ValueError:
            return 0.0
        # ★ 单位换算：K → /1024 得 MB；M → 原值
        if unit == "M":
            return val
        if unit == "G":
            return val * 1024
        return val / 1024
    return 0.0


def last_shelf_span() -> float | None:
    """取 gui-diag.log 最后一段「启动 → 收藏夹读完」的毫秒数。"""
    if not DIAG.exists():
        return None
    lines = [ln.strip() for ln in
             DIAG.read_text(encoding="utf-8", errors="replace").splitlines() if ln.strip()]
    start = end = None
    for ln in lines:
        if "StartupAsync 开始" in ln:
            start = ln.split("]")[0].lstrip("[")
        if "收藏夹读完" in ln:
            end = ln.split("]")[0].lstrip("[")
    if not (start and end):
        return None
    try:
        fmt = "%H:%M:%S.%f"
        return (time.mktime(time.strptime(end, fmt))
                - time.mktime(time.strptime(start, fmt))) * 1000
    except Exception:  # noqa: BLE001
        return None


def measure(name: str, exe: Path) -> dict:
    t0 = time.perf_counter()
    proc = subprocess.Popen([str(exe)], cwd=str(exe.parent))
    t_window = None
    peak = 0.0
    try:
        deadline = time.perf_counter() + 12
        while time.perf_counter() < deadline:
            if t_window is None and has_window(proc.pid):
                t_window = (time.perf_counter() - t0) * 1000
            peak = max(peak, tasklist_mem(proc.pid))
            time.sleep(0.25)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
    return {"name": name, "window_ms": t_window,
            "shelf_ms": last_shelf_span(), "peak_mb": peak}


def main() -> int:
    print(f"{'版本':<14}{'窗口创建':>12}{'收藏夹读完':>14}{'工作集峰值':>14}")
    print("-" * 54)
    for name, exe in TARGETS:
        if not exe.exists():
            print(f"{name:<14} 产物不存在: {exe}")
            continue
        r = measure(name, exe)
        w = f"{r['window_ms']:.0f} ms" if r["window_ms"] else "-"
        s = f"{r['shelf_ms']:.0f} ms" if r["shelf_ms"] is not None else "-"
        m = f"{r['peak_mb']:.0f} MB" if r["peak_mb"] else "-"
        print(f"{r['name']:<14}{w:>12}{s:>14}{m:>14}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
