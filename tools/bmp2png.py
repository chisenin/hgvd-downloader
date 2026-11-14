#!/usr/bin/env python
"""BMP(24位) → PNG，纯 Python（zlib + struct），无需 Pillow。

用法：python tools/bmp2png.py 输入.bmp 输出.png
"""
import struct
import sys
import zlib


def read_bmp(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"BM":
        raise ValueError("不是 BMP 文件")
    offset = struct.unpack_from("<I", data, 10)[0]
    width = struct.unpack_from("<i", data, 18)[0]
    height = struct.unpack_from("<i", data, 22)[0]
    bpp = struct.unpack_from("<H", data, 28)[0]
    if bpp != 24:
        raise ValueError("只支持 24 位 BMP，实际 %d 位" % bpp)
    bottom_up = height > 0
    height = abs(height)
    row = ((width * 3 + 3) // 4) * 4
    rows = []
    for y in range(height):
        off = offset + y * row
        line = data[off:off + width * 3]
        # BMP 存的是 BGR，PNG 要 RGB
        rows.append(bytes(b for i in range(width)
                          for b in (line[i * 3 + 2], line[i * 3 + 1], line[i * 3])))
    if bottom_up:
        rows.reverse()
    return width, height, rows


def write_png(path, width, height, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, payload):
        c = tag + payload
        return struct.pack(">I", len(payload)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)


if __name__ == "__main__":
    w, h, rows = read_bmp(sys.argv[1])
    write_png(sys.argv[2], w, h, rows)
    print("saved %s (%dx%d)" % (sys.argv[2], w, h))
