#!/usr/bin/env python
"""按 hwnd 抓屏幕区域（BitBlt），存 BMP。用法：python shot_hwnd.py <hwnd> <out.bmp>"""
import ctypes
import ctypes.wintypes as w
import sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()


class BIH(ctypes.Structure):
    _fields_ = [("biSize", w.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                ("biPlanes", w.WORD), ("biBitCount", w.WORD), ("biCompression", w.DWORD),
                ("biSizeImage", w.DWORD), ("biX", ctypes.c_long), ("biY", ctypes.c_long),
                ("biClrUsed", w.DWORD), ("biClrImp", w.DWORD)]


class BI(ctypes.Structure):
    _fields_ = [("h", BIH), ("c", w.DWORD * 3)]


def grab(hwnd, out, pad=0, crop=None, scale=1, flip=True):
    r = w.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    left, top = max(0, r.left - pad), max(0, r.top - pad)
    width = min(1920 - left, r.right - r.left + pad * 2)
    height = min(1080 - top, r.bottom - r.top + pad * 2)
    if width <= 0 or height <= 0:
        raise RuntimeError("矩形非法 %s" % ((r.left, r.top, r.right, r.bottom),))

    src = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(src)
    bmp = gdi32.CreateCompatibleBitmap(src, width, height)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, width, height, src, left, top, 0x00CC0020)

    bi = BI()
    bi.h.biSize = ctypes.sizeof(BIH)
    bi.h.biWidth = width
    bi.h.biHeight = -height
    bi.h.biPlanes = 1
    bi.h.biBitCount = 32
    bi.h.biCompression = 0
    buf = ctypes.create_string_buffer(width * height * 4)
    gdi32.GetDIBits(mem, bmp, 0, height, buf, ctypes.byref(bi), 0)
    raw = bytes((ctypes.c_ubyte * (width * height * 4)).from_buffer(buf))

    # ★ Windows 上 GetDIBits 返回的是**自下而上**的行序 → 需要整体上下翻转
    if flip:
        raw = b"".join(raw[y * width * 4:(y + 1) * width * 4]
                       for y in range(height - 1, -1, -1))

    # 裁剪 + 放大
    if crop:
        cx, cy, cw, ch = crop
        cw = min(cw, width - cx)
        ch = min(ch, height - cy)
        rows = [raw[(cy + y) * width * 4:(cy + y) * width * 4 + cw * 4] for y in range(ch)]
        raw = b"".join(rows)
        width, height = cw, ch
    if scale > 1:
        rows = []
        for y in range(height):
            line = raw[y * width * 4:(y + 1) * width * 4]
            big = bytearray(width * scale * 4)
            for x in range(width):
                px4 = line[x * 4:(x + 1) * 4]
                big[x * scale * 4:(x + 1) * scale * 4] = px4
            for _ in range(scale):
                rows.append(bytes(big))
        raw = b"".join(rows)
        width *= scale
        height *= scale

    row = ((width * 3 + 3) // 4) * 4
    px = bytearray(row * height)
    for y in range(height):
        line = raw[y * width * 4:(y + 1) * width * 4]
        o = bytearray(width * 3)
        o[0::3] = line[0::4]
        o[1::3] = line[1::4]
        o[2::3] = line[2::4]
        px[y * row:y * row + width * 3] = o

    size = 14 + 40 + len(px)
    with open(out, "wb") as f:
        f.write(b"BM")
        f.write(int(size).to_bytes(4, "little"))
        f.write((0).to_bytes(4, "little"))
        f.write((54).to_bytes(4, "little"))
        f.write((40).to_bytes(4, "little"))
        f.write(width.to_bytes(4, "little"))
        f.write(height.to_bytes(4, "little"))
        f.write((1).to_bytes(2, "little"))
        f.write((24).to_bytes(2, "little"))
        f.write((0).to_bytes(4, "little"))
        f.write(len(px).to_bytes(4, "little"))
        f.write((2835).to_bytes(4, "little"))
        f.write((2835).to_bytes(4, "little"))
        f.write((0).to_bytes(4, "little"))
        f.write((0).to_bytes(4, "little"))
        f.write(bytes(px))
    print("saved %s (%dx%d) from hwnd=%s rect=(%d,%d)" % (out, width, height, hwnd, left, top))


if __name__ == "__main__":
    args = sys.argv[1:]
    hwnd = int(args[0]); out = args[1]
    crop = None; scale = 1
    for a in args[2:]:
        if a.startswith("--crop="):
            crop = tuple(int(v) for v in a.split("=", 1)[1].split(","))
        elif a.startswith("--scale="):
            scale = int(a.split("=", 1)[1])
    grab(hwnd, out, crop=crop, scale=scale)
