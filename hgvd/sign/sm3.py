"""SM3 哈希（GM/T 0004-2012）。

★ 抖音系签名链（x-gorgon / x-helios / x-medusa / x-argus）都依赖 SM3，
Python 标准库没有，必须自己实现。
★ ``hashlib.algorithms_available`` 里没有 sm3，gmssl 也没装 → 纯 Python 实现。

参考实现：openhongguo ``internal/protocol/sign/sm3.go``（Apache-2.0）。
"""

from __future__ import annotations

import struct

_MASK = 0xFFFFFFFF


def _rol32(x: int, n: int) -> int:
    n &= 31
    return ((x << n) | (x >> (32 - n))) & _MASK


def _p0(x: int) -> int:
    return x ^ _rol32(x, 9) ^ _rol32(x, 17)


def _p1(x: int) -> int:
    return x ^ _rol32(x, 15) ^ _rol32(x, 23)


def _ff(j: int, x: int, y: int, z: int) -> int:
    if j < 16:
        return x ^ y ^ z
    return (x & y) | (x & z) | (y & z)


def _gg(j: int, x: int, y: int, z: int) -> int:
    if j < 16:
        return x ^ y ^ z
    return (x & y) | ((~x & _MASK) & z)


def _pad(msg: bytes) -> bytes:
    bit_len = len(msg) * 8
    out = bytearray(msg)
    out.append(0x80)
    while (len(out) + 8) % 64 != 0:
        out.append(0)
    out += struct.pack(">Q", bit_len)
    return bytes(out)


def sm3_sum(msg: bytes) -> bytes:
    """SM3 摘要，32 字节。

    ★ ``msg`` 是 ``abc`` 时，结果应为
    ``66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0``。
    """
    h = [
        0x7380166F, 0x4914B2B9, 0x172442D7, 0xDA8A0600,
        0xA96F30BC, 0x163138AA, 0xE38DEE4D, 0xB0FB0E4E,
    ]
    data = _pad(msg)
    w = [0] * 68
    wp = [0] * 64

    for off in range(0, len(data), 64):
        block = data[off:off + 64]
        for i in range(16):
            w[i] = struct.unpack_from(">I", block, i * 4)[0]
        for j in range(16, 68):
            x = w[j - 16] ^ w[j - 9] ^ _rol32(w[j - 3], 15)
            w[j] = _p1(x) ^ _rol32(w[j - 13], 7) ^ w[j - 6]
        for j in range(64):
            wp[j] = w[j] ^ w[j + 4]

        a, b, c, d = h[0], h[1], h[2], h[3]
        e, f, g, hh = h[4], h[5], h[6], h[7]

        for j in range(64):
            tj = 0x79CC4519 if j < 16 else 0x7A879D8A
            ss1 = _rol32((_rol32(a, 12) + e + _rol32(tj, j)) & _MASK, 7)
            ss2 = ss1 ^ _rol32(a, 12)
            tt1 = (_ff(j, a, b, c) + d + ss2 + wp[j]) & _MASK
            tt2 = (_gg(j, e, f, g) + hh + ss1 + w[j]) & _MASK
            d = c
            c = _rol32(b, 9)
            b = a
            a = tt1
            hh = g
            g = _rol32(f, 19)
            f = e
            e = _p0(tt2)

        h[0] ^= a
        h[1] ^= b
        h[2] ^= c
        h[3] ^= d
        h[4] ^= e
        h[5] ^= f
        h[6] ^= g
        h[7] ^= hh

    return struct.pack(">8I", *[v & _MASK for v in h])


def sm3_hex(msg: bytes) -> str:
    return sm3_sum(msg).hex()
