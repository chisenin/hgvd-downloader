"""CENC 内容密钥解包。

★ 服务端在 ``encrypt_info.spade_a`` 里下发一份被包裹的密钥材料，
播放器按固定规则解开，得到 AES 内容密钥（hex）。
★ 密钥是**本地计算**的，不需要额外请求。

★ 加密方式是 CENC AES-CTR，同一集的所有清晰度共用一个内容密钥，
   下载后用 ``ffmpeg -decryption_key <hex>`` 解密。

参考实现：openhongguo ``internal/drm/spade.go``（Apache-2.0）。
"""

from __future__ import annotations

import base64


class DrmError(Exception):
    """密钥材料无法解包。"""


def _bits8(v: int) -> int:
    n = 0
    while v:
        v &= v - 1
        n += 1
    return n


def _popcount(v: int) -> int:
    return (
        _bits8(v & 0xFF)
        + _bits8((v >> 8) & 0xFF)
        + _bits8((v >> 16) & 0xFF)
        + _bits8((v >> 24) & 0xFF)
    )


def _hex_digit(c: int) -> int:
    if 0x30 <= c <= 0x39:
        return c - 0x30
    if 0x61 <= c <= 0x66:
        return c - 0x61 + 10
    if 0x41 <= c <= 0x46:
        return c - 0x41 + 10
    return -1


def _scramble(buf: bytearray, v2: bool) -> None:
    """播放器的字节置换。

    ★ v2（``app_v2`` / ``web_v2`` 家族）先两两交换相邻字节，
      再做「减去前一个滚动常量」的变体。
    """
    if v2:
        last = len(buf) - 1
        i = 0
        while i + 1 < len(buf):
            buf[i], buf[i + 1] = buf[i + 1], buf[i]
            if i == last - 2:
                break
            i += 2
    prev, cur = 0x55, 0xFA
    for i in range(len(buf)):
        b = buf[i]
        term = (-(0x15 + _popcount(i))) & 0xFF
        if i % 2 == 0:
            if v2:
                buf[i] = ((cur ^ b) - prev + term) & 0xFF
            else:
                buf[i] = (term + (cur ^ b)) & 0xFF
            cur = b
        else:
            if v2:
                buf[i] = ((prev ^ b) - prev + term) & 0xFF
            else:
                buf[i] = (term + (prev ^ b)) & 0xFF
            prev = b


def derive_content_key(spade_a: str) -> str | None:
    """从 ``spade_a`` 解出内容密钥（hex）。失败返回 None。"""
    try:
        raw = base64.b64decode((spade_a or "").strip())
    except Exception:  # noqa: BLE001
        return None
    if len(raw) < 4:
        return None

    marker = raw[0] ^ raw[1] ^ raw[2]
    prefix_len = marker - 0x30
    if prefix_len < 1 or prefix_len > len(raw):
        return None
    out_len = len(raw) - marker + 0x2F
    if out_len < 2 or 1 + out_len > len(raw):
        return None
    buf = bytearray(raw[1:1 + out_len])

    tail = len(raw) - prefix_len
    if tail - 2 < 0 or tail > len(raw):
        return None
    b16, b14 = raw[tail - 2], raw[tail - 1]
    tag = bytearray(prefix_len)
    for i in range(prefix_len):
        if i + tail >= len(raw):
            return None
        tag[i] = b14 ^ b16 ^ raw[i + tail]
    tag_text = tag.decode("latin-1")
    _scramble(buf, tag_text.startswith("app_v2") or tag_text.startswith("web_v2"))

    version_len = _hex_digit(buf[0])
    if version_len < 1 or version_len >= out_len:
        return None
    key_len = out_len - version_len
    hex_key = bytes(buf[1:key_len]).decode("latin-1")
    if not hex_key or len(hex_key) % 2 != 0:
        return None
    for ch in hex_key:
        if _hex_digit(ord(ch)) < 0:
            return None
    return hex_key.lower()


def ffmpeg_decrypt_arg(key_hex: str) -> list[str]:
    """构造 ffmpeg 解密参数。

    ★ 内容密钥给 ffmpeg 用 ``-decryption_key``（十六进制，无 0x 前缀）。
    """
    return ["-decryption_key", key_hex]
