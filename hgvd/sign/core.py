"""签名核心：x-gorgon / x-helios / x-medusa / x-argus / x-ladon。

★ 这是从 Go 参考实现（openhongguo，Apache-2.0）移植的等价 Python 实现，
已用 WASM 参考实现产出的真实请求头逐项校验。

用法::

    ident = DeviceIdentity(device_id="...", iid="...")
    query, headers = sign_request(path, pairs, now_ms, ident)
"""

from __future__ import annotations

import hashlib
import math
import os
import random
import struct
import urllib.parse
from dataclasses import dataclass, field

from .aesv3 import AesV3
from .sm3 import sm3_sum

# ---------------------------------------------------------------- 常量

AID = "8662"
APP_ID = "8662"
CHANNEL_ID = "1588093228"
GORGON_SDK_VERSION = 67503104

MEDUSA_SIGN_KEY = bytes.fromhex(
    "8ebdfa3806ecc5cee79423e6029ed82540bc2218bb7eaef71cb691f7aa8aa2f5"
)
MEDUSA_AES_KEY = bytes.fromhex("f1593376766ea98d34f31b057a9d5be4")
MEDUSA_AES_IV = bytes.fromhex("1fe109a4125283f418de9e051a969e12")

ARGUS_SIGN_KEY = bytes.fromhex(
    "fc78e0a9657a0c748ce51559903ccf03510e51d3cff232d71343e88a321c5304"
)
ARGUS_AES_KEY = bytes.fromhex("d31e3718288a1027baab59f146a09a9c")
ARGUS_AES_IV = bytes.fromhex("ea180a0336ed352fcd24e4d50018ae54")
ARGUS_MASK = bytes((0xF2, 0xF7, 0xFC, 0xFF, 0xF2, 0xF7, 0xFC, 0xFF))
ARGUS_TAIL = bytes.fromhex("eafb2cfe8568519154")
PLAYBACK_LICENSE_ID = "1611921764"

_BRANCH2_ORDERS = bytes.fromhex(
    "0f0704000908030a060b050d0e010c020f05080c0009020103070e060b0a0d04060500070c000a04080f"
    "010b0d09020e060b02050403080101070a000d0c090e0d070e0f0b0208030c0509010004060a0d090206"
    "0f0b0a040807000c0503010e0c090f07060f030e020d0405010b0a000c050a090e0802040407030f0106"
    "0b000b0f0408020a0700090d06010e03050c0b060a0508020c03070f0e090d0001040906080f050800040a"
    "0b030d01020c0e090d0c0604070a03030f00080105020e01000d0f090a0b0e0402080703060c0501080a0c"
    "0f0905060b000304020e070d04080f000c0f0e0d0a01060207090503040205080d0b0a0606000e0f070c0903"
    "0a08040f000b01060d0c0709030e05020a070b050f00020e0108030d0c0609040d070f08050f06040b0a0e"
    "0c090002030d0c020704010b0e0e08060f09050003"
)

_BRANCH2_SV = (
    0xA7AEFE20, 0x7149F1D6, 0x47E4CA07, 0xE9B58F67, 0x93B924DE, 0xC614D0F5,
    0x38AFE0EF, 0xB2BBAD73, 0xE24444C3, 0x9D3AEC9B, 0xDF7B37E4, 0xD8B16D40,
    0xF8AC31B8, 0x76B9A90B, 0x31D833EE, 0x953FCE64, 0x353595A4, 0x4609C13B,
    0x36925008, 0x8C6D0925, 0x5DF5C177, 0x1CFBF52B, 0x8A4FA7F0, 0x114CA35E,
    0x8193F984, 0x7A7A8733, 0x316AB4D5, 0x3C20CFC9, 0xA6D84453, 0x3A18500C,
    0x798EC47A, 0x97A76B28, 0x66C4FF96, 0x51716443, 0x0DD2FC3B, 0xB5696DA7,
    0xBBEB3AC5, 0x5C53D204, 0xD32608CE, 0x7279B9EC, 0xF4188ECF, 0xF7D793DB,
    0x332CC491, 0xAB76AE15, 0x9BEBE727, 0x18A01384, 0x5BE9F8A7, 0x5F90A754,
    0x39B663C0, 0x36673C83, 0x7C92F514, 0x9D7D94D7, 0xE2E8D9AA, 0x5F7E9EA9,
    0x7ABD4551, 0x569E05DA, 0x40A25632, 0x3DF5A9A5, 0xBAB37D80, 0x454286DC,
    0x3F5D4E78, 0x03D7B75D, 0xB1FE4AF7, 0xA5AB26A3,
)

_BRANCH0_IV = (0xC4A78580, 0xB3C0FD39, 0xC58C5686, 0xC9AA3BA7, 0xF5A7ADF2, 0x963C2ED1)
_BRANCH0_TT = (
    0xEBB64FAF, 0x07AADCC2, 0xCF3187BF, 0xE01138FF, 0x6D0BFCFF, 0x5A30A3BE,
    0xB41AD638, 0x34180EB8, 0xF233EB6F, 0xB1A584CC, 0xCCC30DC7, 0x47D1DB51,
    0xD55653DE, 0x70A84FA1, 0x57473C12, 0xF76F0288, 0x2C077F0A, 0xDA0DCAD0,
    0xFBB86F6C, 0xFDC4CF00, 0x688A020D, 0xE676C6A6, 0x8CD6338B, 0x1A3C8D0E,
    0xCCE8B06B, 0x6AD0ED0B, 0xA0522717, 0xDC71AC83, 0x2285DB71, 0xD5B4DDA6,
    0x736F8650, 0x6560306C, 0x617CE2A6, 0xE423417E, 0x0A40E143, 0x544E4032,
    0x88DFFB2A, 0x716C1AE0, 0x4C467A88, 0x05B23BB3, 0xE1D0B866, 0xBAA3DCB8,
    0xAE3374D3, 0xC3381A50, 0x1702F75B, 0xFE6DA368, 0xF0B4CF48, 0x4E0FFBB8,
    0x72AAD10D, 0x26C53A3D, 0xF2BCE0F6, 0xB4557581, 0x4A257FDD, 0x8C3182A2,
    0xAB0B3B86, 0x3D5DFB14, 0x4F103634, 0xD37B52D7, 0x444EFF16, 0xEB0A33D1,
    0x6CA86F6E, 0x00284BA7, 0x08387CFA, 0x5FB37586,
)
_BRANCH0_INIT = (
    0x7ABA4FC8, 0x67166507, 0x6403FA00, 0x340F512F,
    984304912, 3005047866, 2874125293, 2152413264,
)
_BRANCH0_ROUNDS = (
    (101, 5, 7, 6, 3, 2, 1, 0, 5, 4, 3),
    (96, 0, 6, 7, 5, 3, 2, 1, 5, 4, 4),
    (96, 7, 6, 2, 1, 4, 0, 5, 4, 3, 5),
    (99, 3, 6, 2, 4, 5, 1, 0, 0, 7, 6),
    (96, 0, 5, 6, 7, 3, 1, 2, 5, 4, 4),
    (100, 2, 0, 3, 5, 4, 6, 7, 2, 1, 5),
)


# ---------------------------------------------------------------- 基础工具


def _rol32(v: int, c: int) -> int:
    c &= 31
    if c == 0:
        return v & 0xFFFFFFFF
    return ((v << c) | (v >> (32 - c))) & 0xFFFFFFFF


def _ror32(v: int, c: int) -> int:
    c &= 31
    if c == 0:
        return v & 0xFFFFFFFF
    return ((v >> c) | (v << (32 - c))) & 0xFFFFFFFF


def _ror64(v: int, c: int) -> int:
    c &= 63
    if c == 0:
        return v & 0xFFFFFFFFFFFFFFFF
    return ((v >> c) | (v << (64 - c))) & 0xFFFFFFFFFFFFFFFF


def _rotl64(v: int, c: int) -> int:
    c &= 63
    if c == 0:
        return v & 0xFFFFFFFFFFFFFFFF
    return ((v << c) | (v >> (64 - c))) & 0xFFFFFFFFFFFFFFFF


def _reverse_bits(v: int) -> int:
    r = 0
    for i in range(8):
        r = (r << 1) | ((v >> i) & 1)
    return r


def _le32(v: int) -> bytes:
    return struct.pack("<I", v & 0xFFFFFFFF)


def _be32(v: int) -> bytes:
    return struct.pack(">I", v & 0xFFFFFFFF)


def _md5(data: bytes) -> bytes:
    return hashlib.md5(data).digest()


def _zigzag(v: int) -> int:
    return ((v << 1) ^ (v >> 63)) & 0xFFFFFFFFFFFFFFFF


def _varint(value: int) -> bytes:
    out = bytearray()
    value &= 0xFFFFFFFFFFFFFFFF
    while value > 127:
        out.append((value & 127) | 128)
        value >>= 7
    out.append(value)
    return bytes(out)


def _proto_key(tag: int, wire: int) -> bytes:
    return _varint((tag << 3) | wire)


def _proto_bytes(tag: int, body: bytes) -> bytes:
    if not body:
        return b""
    return _proto_key(tag, 2) + _varint(len(body)) + body


def _proto_string(tag: int, s: str) -> bytes:
    return _proto_bytes(tag, s.encode("utf-8"))


def _proto_sint(tag: int, value: int) -> bytes:
    return _proto_key(tag, 0) + _varint(_zigzag(value))


def _proto_float(tag: int, value: float) -> bytes:
    return _proto_key(tag, 5) + struct.pack("<f", value)


def _pb_varint(tag: int, value: int) -> bytes:
    """Argus 用的 protobuf varint（**不做** zigzag）。"""
    return _proto_key(tag, 0) + _varint(value)


def _pb_bytes(tag: int, body: bytes) -> bytes:
    """Argus 用的长度分隔字段 —— 空 body 也必须输出。"""
    return _proto_key(tag, 2) + _varint(len(body)) + body


def _pb_string(tag: int, s: str) -> bytes:
    return _pb_bytes(tag, s.encode("utf-8"))


def _pkcs7(data: bytes) -> bytes:
    pad = 16 - len(data) % 16
    return data + bytes([pad]) * pad


def _get_iv(iv: int, data: bytes) -> int:
    value = iv
    for i, b in enumerate(data):
        if i & 1 == 0:
            value = ((value >> 4) ^ value ^ (value << 6) ^ b) & 0xFFFFFFFF
            continue
        inner = ((value >> 7) ^ value ^ ((b | (value << 12)) & 0xFFFFFFFF)) & 0xFFFFFFFF
        value = (~inner) & 0xFFFFFFFF
    return value


def _sum_md5(data: bytes) -> int:
    check = 0x20220420
    for i in range(12):
        if i & 1 == 0:
            temp = ((check >> 3) ^ check) & 0xFFFFFFFF
            check = (data[i] ^ (check << 7)) & 0xFFFFFFFF
        else:
            temp = ((check >> 5) ^ check) & 0xFFFFFFFF
            check = (data[i] | (check << 11)) & 0xFFFFFFFF
            check = (~check) & 0xFFFFFFFF
        check ^= temp
    return ((check | 4) ^ 0x01000000) & 0xFFFFFFFF


# ---------------------------------------------------------------- F13


def _branch_of(query: bytes, body_md5: bytes, ts: bytes) -> int:
    iv = _get_iv(_get_iv(_get_iv(0x20230928, query), body_md5), ts)
    low = iv & 15
    return low - ((low * 171) >> 9) * 3


def _expand_words(data: bytes) -> list[int]:
    di = []
    for off in range(0, len(data) - 3, 4):
        di.append(struct.unpack_from(">I", data, off)[0])
    di0 = di[0]
    for i in range(112):
        di1 = di[i + 1]
        di14 = di[i + 14]
        r1 = _rol32(di1, 14) ^ _rol32(di1, 25) ^ (di1 >> 3)
        r2 = _rol32(di14, 13) ^ _rol32(di14, 15) ^ (di14 >> 10)
        di.append((di0 + di[i + 9] + r1 + r2) & 0xFFFFFFFF)
        di0 = di1
    return di


def _mix_rounds(state: list[int], rounds, di, tt, iv_v1: int) -> list[int]:
    state = list(state)
    for i in range(rounds[0]):
        base = iv_v1 + i
        w = di[base & 127]
        idx = lambda k: rounds[k]  # noqa: E731
        n1 = (((state[idx(3)] ^ state[idx(4)]) & state[idx(1)]) ^ state[idx(3)])
        n2 = _rol32(state[idx(1)], 26) ^ _rol32(state[idx(1)], 21) ^ _rol32(state[idx(1)], 7)
        n4 = (w + n1 + n2 + tt[base & 63] + state[idx(5)]) & 0xFFFFFFFF
        n5 = _rol32(state[idx(2)], 30) ^ _rol32(state[idx(2)], 19) ^ _rol32(state[idx(2)], 10)
        n6 = (state[idx(2)] & state[idx(6)]) | ((state[idx(2)] | state[idx(6)]) & state[idx(7)])
        n7 = (n5 + n6) & 0xFFFFFFFF
        old = state[idx(9)]
        last = state[-1]
        state = [last] + state[:-1]
        state[idx(10)] = (n7 + n4) & 0xFFFFFFFF
        state[idx(8)] = (old + n4) & 0xFFFFFFFF
    return state


def _branch0_f13(query_sm3, body_md5, ts, khronos: int, iv_v0: int) -> bytes:
    iv_v1 = _BRANCH0_IV[min(iv_v0, len(_BRANCH0_IV) - 1)]
    count_v1 = (iv_v1 + khronos + 1) & 255
    count_v2 = (iv_v1 + khronos) & 0xFFFFFFFF
    tt = [_ror32(v, count_v1) for v in _BRANCH0_TT]
    n0 = (count_v2 + 2) & 7
    seed = bytes((0xFA, 0x45, 0x61, 0xD7))
    pad = bytes((((v | (v << 8)) >> n0) & 255) for v in seed)
    data = query_sm3 + body_md5 + ts + pad + bytes.fromhex("00000000000001a0")
    di = _expand_words(data)
    init = [_ror32(v, count_v2 & 31) for v in _BRANCH0_INIT]
    d = _mix_rounds(init, _BRANCH0_ROUNDS[iv_v0], di, tt, iv_v1)
    ret = bytearray()
    for i in range(8):
        ret += _be32((d[i] + init[i]) & 0xFFFFFFFF)
    for i in range(16):
        ret[i] ^= ret[16 + i]
    out = bytes(ret[:16])
    return out + _le32(_sum_md5(ret[:16]))


def _md5_v3_step(kind: int, a, b, c, d, m, shift, constant) -> int:
    if kind == 0:
        f = (b & c) | (~b & d)
    elif kind == 1:
        f = (b & d) | (c & ~d)
    elif kind == 2:
        f = b ^ c ^ d
    else:
        f = c ^ (b | ~d)
    return (b + _rol32((a + f + m + constant) & 0xFFFFFFFF, shift)) & 0xFFFFFFFF


def _md5_sum_v3(message: bytes, count_v2: int, orders: bytes, count_v1: int) -> bytes:
    sv = [_ror32(v, count_v1) for v in _BRANCH2_SV]
    start = (
        _ror32(0x79E0F2FB, count_v2),
        _ror32(0xC8B52570, count_v2),
        _ror32(0xEBC2F8CD, count_v2),
        _ror32(0x7C104D93, count_v2),
    )
    end_count = (count_v2 + 6) & 255
    end = (
        _ror32(0x19BE4866, end_count),
        _ror32(0xE85986B4, end_count),
        _ror32(0xE19B326E, end_count),
        _ror32(0x71D1D7D4, end_count),
    )
    m = list(struct.unpack("<16I", message[:64]))
    a, b, c, d = start
    shifts = (
        (7, 12, 17, 22), (5, 9, 14, 20), (4, 11, 16, 23), (6, 10, 15, 21),
    )

    def run(kind: int, offset: int):
        nonlocal a, b, c, d
        sh = shifts[kind] * 4
        for i in range(16):
            phase = i & 3
            w = m[orders[offset + i]]
            k = sv[offset + i]
            if phase == 0:
                a = _md5_v3_step(kind, a, b, c, d, w, sh[i], k)
            elif phase == 1:
                d = _md5_v3_step(kind, d, a, b, c, w, sh[i], k)
            elif phase == 2:
                c = _md5_v3_step(kind, c, d, a, b, w, sh[i], k)
            else:
                b = _md5_v3_step(kind, b, c, d, a, w, sh[i], k)

    run(0, 0)
    run(1, 16)
    run(2, 32)
    run(3, 48)
    ret = bytearray()
    ret += _le32((start[0] + a) ^ end[0])
    ret += _le32((start[1] + b) ^ end[1])
    ret += _le32((start[2] + c) ^ end[2])
    ret += _le32((start[3] + d) ^ end[3])
    return bytes(ret) + _le32(_sum_md5(bytes(ret)))


def _branch2_f13(iv: int, query_sm3, body_md5, ts, khronos: int) -> bytes:
    v = (iv & 13) * 86
    iv_v0 = ((v >> 15) & 255) + ((v >> 8) & 255)
    n1s = (0x8980F29B, 0xEB549C7F, 0xB08726DB, 0xD40CB5E6, 0xE8F559E4)
    n1 = n1s[min(iv_v0, len(n1s) - 1)]
    count_v1 = (n1 + khronos + 1) & 255
    count_v2 = (n1 + khronos) & 0xFFFFFFFF
    shift = (count_v2 + 5) & 7
    seed = bytes((0x84, 0x96, 0x77, 0x9D, 0xD4, 0x15, 0x0B, 0xF8))
    pad = bytes((((v | (v << 8)) >> shift) & 255) for v in seed)
    data = query_sm3 + body_md5 + ts + pad + bytes.fromhex("a0010000")
    begin = iv_v0 << 6
    return _md5_sum_v3(data, count_v2, _BRANCH2_ORDERS[begin:begin + 64], count_v1)


def _hash_f13(query_sm3: bytes, body_md5: bytes, ts: bytes, khronos: int) -> bytes:
    iv = _get_iv(_get_iv(_get_iv(0x20230928, query_sm3), body_md5), ts)
    low = iv & 15
    iv_v0 = (low * 171) >> 9
    branch = low - iv_v0 * 3
    if branch == 2:
        return _branch2_f13(iv, query_sm3, body_md5, ts, khronos)
    if branch != 0:
        out = bytearray(query_sm3[:16])
        out += body_md5
        summed = query_sm3 + body_md5
        out += _le32(_sum_md5(summed))
        return bytes(out)
    return _branch0_f13(query_sm3, body_md5, ts, khronos, iv_v0)


# ---------------------------------------------------------------- Gorgon


def _rc4_gorgon(data: bytes, key: bytes) -> bytearray:
    s = bytearray(range(256))
    j = 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 255
        # ★ 注意：这里是**单向赋值** s[i] = s[j]，不是标准 RC4 的 s[i],s[j] 交换。
        # 写成交换会与 Go 参考实现不一致（x-gorgon 校验失败）。
        s[i] = s[j]
    out = bytearray()
    i = j = 0
    for value in data:
        i += 1
        x = s[i & 255]
        j = (j + x) & 255
        y = s[j & 255]
        s[i & 255] = y
        out.append(value ^ s[(y + y) & 255])
    return out


def _x_gorgon(query: str, body: bytes | None, khronos: int, random_v: int) -> str:
    query_md5 = _md5(query.encode("utf-8"))
    body_md5 = _md5(body) if body else b"\x00" * 16
    inp = bytearray()
    inp += query_md5[:4]
    inp += body_md5[:4]
    inp += b"\x00\x00\x00\x00"
    inp += _le32(GORGON_SDK_VERSION)
    inp += _be32(khronos)
    key = bytes((0x4A, 0x40, 0x16, (random_v >> 8) & 255, 0x47, 0x6C, 0x01, random_v & 255))
    out = _rc4_gorgon(bytes(inp), key)
    n = len(out)
    for i in range(n):
        value = out[i]
        swapped = ((value >> 4) | (value << 4)) & 255
        nxt = out[0] if i + 1 >= n else out[i + 1]
        out[i] = ((~(_reverse_bits(nxt ^ swapped) ^ 20)) & 255)
    result = bytes((0x84, 0x04, random_v & 255, (random_v >> 8) & 255, 0x40, 0x01)) + bytes(out)
    return result.hex()


# ---------------------------------------------------------------- Helios


def _assemble_helios(khronos: int, random_v: int) -> str:
    import base64

    seed = _le32(random_v) + APP_ID.encode("utf-8")
    digest = _md5(seed)
    ascii_hex = digest.hex().encode("ascii")
    words = [
        struct.unpack_from("<Q", ascii_hex, i * 8)[0] for i in range(4)
    ]
    table = _diffuse(words)
    text = f"{khronos}-{CHANNEL_ID}-{APP_ID}".encode("utf-8")
    fill = 16 - (len(text) % 16)
    size = ((len(text) + 1 + 15) // 16) * 16
    padded = bytearray([fill] * size)
    padded[: len(text)] = text
    output = bytearray()
    for off in range(0, len(padded), 16):
        chunk = padded[off:off + 16]
        a = struct.unpack_from("<Q", chunk, 0)[0]
        b = struct.unpack_from("<Q", chunk, 8)[0]
        for t in table[:34]:
            b = t ^ ((a + _ror64(b, 8)) & 0xFFFFFFFFFFFFFFFF)
            a = b ^ _ror64(a, 61)
        ab = struct.pack("<Q", a) + struct.pack("<Q", b)
        output += ab
    result = _le32(random_v) + bytes(output)
    return base64.b64encode(result).decode("ascii")


def _diffuse(words: list[int]) -> list[int]:
    table = [words[0]]
    b0 = words[0]
    b8 = words[1]
    queue = list(words[2:])
    for i in range(34):
        x8 = (_ror64(b8, 8) + b0) & 0xFFFFFFFFFFFFFFFF
        x8 ^= i
        queue.append(x8)
        x8 ^= _ror64(b0, 61)
        table.append(x8)
        b0 = x8
        b8 = queue.pop(0)
    return table


# ---------------------------------------------------------------- Medusa


def _xmxor(data: bytes, key: bytes) -> bytearray:
    n = len(data)
    encoded = bytearray(n)
    for i in range(n):
        at = (i * 4) & 28
        d0 = key[at]
        d1 = key[at + 1]
        d2 = (((data[i] << 4) | (data[i] >> 4)) + d0) & 0xFF
        d2 = ((~d2 & 0xFF) ^ d1) & 0xFF
        d2 = ((d2 << 3) | (d2 >> 5)) & 0xFF
        d2 = (d2 + d1) & 0xFF
        d2 ^= d0
        encoded[n - i - 1] = (~d2) & 0xFF
    last = encoded[n - 1] ^ encoded[n - 2]
    first = encoded[0]
    encoded[0] = ((~last & 0xFF) + first) & 0xFF
    encoded[1] = ((encoded[0] ^ encoded[n - 1] ^ 254) + encoded[1]) & 0xFF
    shifted = ((encoded[1] << 3) | (encoded[1] >> 5)) & 0xFF
    fix2 = ((last - first) ^ (shifted ^ 2)) & 0xFF
    encoded[2] = (encoded[2] + fix2) & 0xFF
    for i in range(n - 4):
        sh = ((encoded[i + 2] << 3) | (encoded[i + 2] >> 5)) & 0xFF
        temp = (sh ^ encoded[i + 1] ^ (i + 3)) & 0xFF
        encoded[i + 3] = (((~temp & 0xFF)) + encoded[i + 3]) & 0xFF
    encoded[n - 1] ^= encoded[n - 2]
    total = sum(encoded[1:]) & 0xFFFFFFFF
    encoded[0] = ((encoded[0] ^ encoded[1]) + (total & 0xFF)) & 0xFF
    return encoded


def _key_hash(sign_key: bytes, random_v: int) -> tuple[bytes, bytes]:
    buf = sign_key + _le32(random_v) + sign_key
    hash_v = sm3_sum(buf)
    d1 = (random_v >> 16) & 255
    d2 = ((d1 << 11) | (random_v >> 24)) & 0xFFFFFFFF
    d2 ^= d1 >> 5
    d2 ^= d1
    d2 = (~d2) & 0xFFFFFFFF
    seed = _le32(d2)
    return hash_v, seed


def _device_proto(device_id: str) -> bytes:
    out = bytearray()
    out += _proto_sint(1, 1)
    out += _proto_sint(2, 2)
    out += _proto_string(3, APP_ID)
    out += _proto_string(4, device_id)
    out += _proto_string(5, "Ai6svO3PyrwDOUSmO6ZcResxu")
    out += _proto_string(6, "!noperm!")
    out += _proto_sint(7, -888888)
    out += _proto_sint(8, -888888)
    out += _proto_sint(9, 3)
    out += _proto_sint(10, -888888)
    out += _proto_string(11, "!notset!")
    out += _proto_string(12, "Asia/Shanghai,8")
    out += _proto_string(13, "zh_CN")
    out += _proto_sint(14, 4)
    out += _proto_float(16, 255.24993896484375)
    out += _proto_float(17, 35.58599090576172)
    out += _proto_float(18, 3.467449188232422)
    out += _proto_float(19, 3.467449188232422)
    out += _proto_float(20, 255.1754913330078)
    out += _proto_float(21, 42.17544174194336)
    out += _proto_string(22, "16")
    out += _proto_sint(23, 41)
    out += _proto_sint(24, 36)
    out += _proto_sint(25, 1728388016635)
    out += _proto_sint(26, 1728388016635)
    out += _proto_sint(27, 1728388016635)
    out += _proto_sint(28, 1728388016637)
    out += _proto_sint(29, -1)
    out += _proto_string(30, "25053RT47C")
    out += _proto_string(31, "Redmi")
    out += _proto_string(32, "25053RT47C")
    out += _proto_string(33, "25053RT47C")
    out += _proto_string(34, "Xiaomi")
    out += _proto_string(35, "Redmi")
    out += _proto_string(36, "Redmi")
    out += _proto_sint(38, 31)
    return bytes(out)


def _assemble_medusa(
    raw_url: str, body: bytes | None, khronos: int, device_id: str,
    version_name: str, ent: "Entropy",
) -> str:
    import base64

    body_md5 = _md5(body) if body else b"\x00" * 16
    query_raw = raw_url.split("?", 1)[1] if "?" in raw_url else ""
    query_sm3 = sm3_sum(query_raw.encode("utf-8"))
    ts = _le32(khronos)
    query_body_ts = _hash_f13(query_sm3, body_md5, ts, khronos)
    check = ((query_sm3[0] & 63) << 14) | 0x18000001 | ((query_body_ts[0] & 63) << 8)

    nested = bytearray()
    nested += _proto_sint(1, 111)
    nested += _proto_sint(2, 10)
    nested += _proto_sint(3, 694367)
    nested += _proto_sint(5, 586952199)
    nested13 = bytearray()
    nested13 += _proto_sint(1, ent.now_secs)
    nested13 += _proto_sint(2, -2)
    nested13 += _proto_sint(4, 200)

    env = bytearray()
    env += _proto_sint(1, ent.env_launch)
    env += _proto_sint(2, 146331399)
    env += _proto_sint(3, 146331396)
    env += _proto_sint(5, 7)
    env += _proto_string(6, "v04.06.04.03-bugfix")
    env += _proto_sint(7, ent.env_pid)
    env += _proto_bytes(12, _device_proto(device_id))
    env += _proto_bytes(13, bytes(nested13))
    env += _proto_string(14, version_name)

    qh = query_raw.encode("utf-8") + body_md5 + b"none"
    query_hash = sm3_sum(qh)

    fixed1 = bytes.fromhex("f7e85ffad7d7dc3bd62ac87057cf6118")
    fixed2 = bytes.fromhex("4001000000000000")

    message = bytearray()
    message += _proto_bytes(1, fixed1)
    message += _proto_sint(2, 3)
    message += _proto_sint(3, ent.message_random)
    message += _proto_string(4, APP_ID)
    message += _proto_string(5, device_id)
    message += _proto_string(6, CHANNEL_ID)
    message += _proto_string(7, version_name)
    message += _proto_string(8, "v04.06.04-ml-android")
    message += _proto_sint(9, 67503104)
    message += _proto_bytes(10, fixed2)
    message += _proto_sint(12, khronos)
    message += _proto_bytes(13, query_body_ts)
    message += _proto_bytes(14, query_sm3[:6])
    message += _proto_bytes(15, bytes(nested))
    message += _proto_string(16, "AXYQOS6n2m60x1fVZHIrH3iol")
    message += _proto_sint(17, khronos)
    message += _proto_bytes(19, query_hash)
    message += _proto_string(20, "none")
    message += _proto_sint(21, 312)
    message += _proto_bytes(23, bytes(env))
    message += _proto_string(
        24,
        '{"cmr":16777216,"cmr2":16777216,"un_h":1879194040,"vpn":0,"kd":0,'
        '"fkd":3672518972,"pd":-1872573247,"dyn":"","do":0,"tk":true}',
    )

    hash_v, seed = _key_hash(MEDUSA_SIGN_KEY, ent.random)
    transformed = _xmxor(bytes(message), hash_v)
    prefix_bytes = fixed2 + bytes(transformed)
    transformed = bytearray(prefix_bytes)
    transformed.reverse()
    for i in range(len(transformed)):
        transformed[i] ^= seed[(~i) & 3]

    packed = bytearray()
    packed.append(0x35)
    packed += _le32(ent.packed_random)
    packed += _le32(check)
    packed += bytes(transformed)
    packed.append((ent.random >> 16) & 255)
    packed.append((ent.random >> 24) & 255)

    encrypted = AesV3(MEDUSA_AES_KEY, khronos).encrypt(bytes(packed), MEDUSA_AES_IV)
    version = bytes.fromhex("03000000f7e85ffad7d7dc3bd62ac87057cf6118")
    prefix = bytearray()
    for i in range(5):
        v = struct.unpack_from("<I", version, i * 4)[0]
        prefix += _le32(v ^ khronos)
    out = bytes(prefix) + bytes((ent.random & 255, (ent.random >> 8) & 255, 0, 1)) + encrypted
    return base64.b64encode(out).decode("ascii")


# ---------------------------------------------------------------- Argus / Ladon


def _simon_key_schedule() -> list[int]:
    k = [0] * 72
    for i in range(4):
        k[i] = struct.unpack_from("<Q", ARGUS_SIGN_KEY, i * 8)[0]
    z = 0x3DC94C3A046D678B
    for i in range(4, 72):
        x = _ror64(k[i - 1], 3) ^ k[i - 3]
        x ^= _ror64(x, 1)
        k[i] = (
            (~k[i - 4] & 0xFFFFFFFFFFFFFFFF)
            ^ x
            ^ ((z >> ((i - 4) % 62)) & 1)
            ^ 3
        )
    return k


def _simon_encrypt(plain: bytearray) -> None:
    keys = _simon_key_schedule()
    for at in range(0, len(plain), 16):
        a = struct.unpack_from("<Q", plain, at)[0]
        b = struct.unpack_from("<Q", plain, at + 8)[0]
        for rk in keys:
            a, b = (
                b,
                (a ^ (_rotl64(b, 1) & _rotl64(b, 8)) ^ _rotl64(b, 2) ^ rk)
                & 0xFFFFFFFFFFFFFFFF,
            )
        struct.pack_into("<Q", plain, at, a)
        struct.pack_into("<Q", plain, at + 8, b)


def _argus_header(
    query: str, body_md5: bytes, ts: int, nonce: int, aid: str,
    device_id: str, install_id: str,
) -> str:
    import base64

    body_hash = sm3_sum(body_md5)
    query_bytes = query.encode("utf-8") if query else bytes(16)
    query_hash = sm3_sum(query_bytes)

    counters = (
        _pb_varint(1, 1)
        + _pb_varint(2, 1)
        + _pb_varint(3, 1)
        + _pb_varint(7, 0xC792ECCC)
    )
    device = _pb_string(1, "NX551J") + _pb_varint(2, 8196) + _pb_varint(4, 0x80E0D800)

    bean = bytearray()
    bean += _pb_varint(1, 1077940818)
    bean += _pb_varint(2, 2)
    bean += _pb_varint(3, nonce)
    bean += _pb_string(4, aid)
    bean += _pb_string(5, device_id)
    bean += _pb_string(6, PLAYBACK_LICENSE_ID)
    bean += _pb_string(7, _query_value(query, "version_name"))
    bean += _pb_string(8, "v04.04.05-ov-android")
    bean += _pb_varint(9, 134744640)
    bean += _pb_bytes(10, bytes(8))
    bean += _pb_varint(11, 0)
    bean += _pb_varint(12, ts << 1)
    bean += _pb_bytes(13, body_hash[:6])
    bean += _pb_bytes(14, query_hash[:6])
    bean += _pb_bytes(15, counters)
    bean += _pb_string(16, install_id)
    bean += _pb_string(20, "none")
    bean += _pb_varint(21, 738)
    bean += _pb_bytes(23, device)
    bean += _pb_varint(25, 2)

    plain = bytearray(_pkcs7(bytes(bean)))
    _simon_encrypt(plain)

    frame = bytearray(ARGUS_MASK + bytes(plain) + ARGUS_TAIL)
    for i in range(len(ARGUS_MASK), len(frame)):
        frame[i] ^= ARGUS_MASK[i % len(ARGUS_MASK)]
    frame.reverse()
    frame = bytearray(_pkcs7(bytes(frame) + b"ao"))

    from .aes_cbc import aes_cbc_encrypt

    enc = aes_cbc_encrypt(bytes(frame), ARGUS_AES_KEY, ARGUS_AES_IV)
    return base64.b64encode(bytes((0xF2, 0x81)) + enc).decode("ascii")


def _ladon_header(ts: int, nonce: int, aid: str) -> str:
    import base64

    seed = _le32(nonce)
    digest = _md5(seed + aid.encode("utf-8"))
    hex_key = digest.hex().encode("ascii")

    k = struct.unpack_from("<Q", hex_key, 0)[0]
    pending = [
        struct.unpack_from("<Q", hex_key, 8)[0],
        struct.unpack_from("<Q", hex_key, 16)[0],
        struct.unpack_from("<Q", hex_key, 24)[0],
    ]
    keys = []
    for i in range(34):
        keys.append(k)
        x = (_ror64(pending[0], 8) + k) & 0xFFFFFFFFFFFFFFFF
        x ^= i
        pending = [pending[1], pending[2], x]
        k = x ^ _rotl64(k, 3)

    plain = bytearray(
        _pkcs7(f"{ts}-{PLAYBACK_LICENSE_ID}-{aid}".encode("utf-8"))
    )
    for at in range(0, len(plain), 16):
        a = struct.unpack_from("<Q", plain, at)[0]
        b = struct.unpack_from("<Q", plain, at + 8)[0]
        for rk in keys:
            b = rk ^ ((a + _ror64(b, 8)) & 0xFFFFFFFFFFFFFFFF)
            a = b ^ _rotl64(a, 3)
        struct.pack_into("<Q", plain, at, a)
        struct.pack_into("<Q", plain, at + 8, b)
    return base64.b64encode(seed + bytes(plain)).decode("ascii")


def _query_value(query: str, name: str) -> str:
    values = urllib.parse.parse_qs(query)
    got = values.get(name)
    return got[0] if got else ""


def _playback_aid(query: str) -> str:
    return _query_value(query, "aid") or "1967"


# ---------------------------------------------------------------- 熵


@dataclass
class Entropy:
    """签名用的随机熵。

    ★ 每次请求都要重新抽（服务端会校验新鲜度）。
    """

    env_launch: int = 0
    env_pid: int = 0
    now_secs: int = 0
    message_random: int = 0
    random: int = 0
    packed_random: int = 0
    gorgon_random: int = 0
    helios_random: int = 0

    @classmethod
    def draw(cls, now_millis: int) -> "Entropy":
        buf = os.urandom(16)
        n = struct.unpack_from("<Q", buf, 0)[0]
        return cls(
            env_launch=100 + (n % 21),
            env_pid=10001 + (struct.unpack_from("<H", buf, 8)[0] % 2000),
            now_secs=now_millis // 1000,
            message_random=struct.unpack_from("<I", buf, 0)[0],
            random=struct.unpack_from("<I", buf, 4)[0],
            packed_random=struct.unpack_from("<I", buf, 8)[0],
            gorgon_random=struct.unpack_from("<H", buf, 12)[0],
            helios_random=struct.unpack_from("<I", buf, 12)[0],
        )


@dataclass
class DeviceIdentity:
    """设备身份。"""

    device_id: str = ""
    iid: str = ""
    cdid: str = ""
    openudid: str = ""
    version_name: str = "7.3.9.32"
    version_code: str = "73932"


# ---------------------------------------------------------------- 主流程

_UNRESERVED = "-_.!~*'()"


def _encode_component(s: str) -> str:
    out = []
    for ch in s.encode("utf-8"):
        c = chr(ch)
        if (
            ("A" <= c <= "Z") or ("a" <= c <= "z") or ("0" <= c <= "9")
            or c in _UNRESERVED
        ):
            out.append(c)
        else:
            out.append("%%%02X" % ch)
    return "".join(out)


def _encode_query(pairs) -> str:
    return "&".join(
        f"{_encode_component(k)}={_encode_component(str(v))}" for k, v in pairs
    )


def sign_request(
    path: str,
    pairs,
    now_millis: int,
    device_id: str,
    version_name: str,
    body: bytes | None = None,
    entropy: Entropy | None = None,
) -> tuple[str, dict]:
    """给一个请求签名。

    返回 ``(query_string, headers)``。
    """
    ent = entropy or Entropy.draw(now_millis)
    has_body = body is not None
    body_md5 = _md5(body) if has_body else bytes(16)

    base = now_millis
    ticket = 0
    khronos = 0
    query = ""
    for offset in range(32):
        ticket = base + offset
        khronos = ticket // 1000
        with_ticket = list(pairs) + [
            ("ts", khronos),
            ("_rticket", ticket),
        ]
        query = _encode_query(with_ticket)
        sum_v = sm3_sum(query.encode("utf-8"))
        if _branch_of(sum_v, body_md5, _le32(khronos)) != 1:
            break
    if ent.now_secs == 0:
        ent.now_secs = khronos

    raw_url = "https://reading.snssdk.com" + path + "?" + query
    headers = {
        "Accept": "application/json; charset=utf-8,application/x-protobuf",
        "x-xs-from-web": "0",
        "x-ss-req-ticket": str(ticket),
        "x-tt-request-tag": "t=0;n=0",
        "sdk-version": "2",
        "passport-sdk-version": "50561",
        "x-vc-bdturing-sdk-version": "3.7.2.cn",
        "x-khronos": str(khronos),
        "x-ladon": _base64(_le32(khronos)),
        "x-argus": _base64(_le32(khronos)),
        "x-gorgon": _x_gorgon(query, body, khronos, ent.gorgon_random),
        "x-helios": _assemble_helios(khronos, ent.helios_random),
        "x-medusa": _assemble_medusa(
            raw_url, body, khronos, device_id, version_name, ent
        ),
        "x-tt-dt": "",
    }
    if has_body:
        headers["Content-Type"] = "application/json; charset=UTF-8"
        headers["x-ss-stub"] = _md5(body).hex().upper()
    return query, headers


def sign_playback(
    path: str,
    pairs,
    now_millis: int,
    device_id: str,
    version_name: str,
    body: bytes | None = None,
    entropy: Entropy | None = None,
    install_id: str = "",
    aid: str = "",
) -> tuple[str, dict]:
    """给 ``video_model`` 播放接口签名。

    ★ 与普通请求的差别：
      1. ``x-argus`` / ``x-ladon`` 换成**真实**签名（不是 4 字节时间戳桩）
      2. **去掉** ``x-medusa`` —— 带 Medusa 时服务端会返回空 body（2026-11-10 实测）
    """
    import base64

    query, headers = sign_request(
        path, pairs, now_millis, device_id, version_name, body, entropy
    )
    khronos = int(headers["x-khronos"])
    body_md5 = _md5(body) if body else bytes(16)
    real_aid = aid or _playback_aid(query)
    headers["x-argus"] = _argus_header(
        query, body_md5, khronos, random.randint(0, 0xFFFFFFFF), real_aid,
        device_id, install_id,
    )
    headers["x-ladon"] = _ladon_header(
        khronos, random.randint(0, 0xFFFFFFFF), real_aid
    )
    headers.pop("x-medusa", None)
    return query, headers


# video_model 是唯一需要真实 argus/ladon 的接口
sign_video_model = sign_playback


def _base64(data: bytes) -> str:
    import base64

    return base64.b64encode(data).decode("ascii")
