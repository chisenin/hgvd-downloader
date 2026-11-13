"""AES-V3 变体。

★ 这**不是**标准 AES —— S 盒、轮常量、行移位顺序都按字节重排，
且只有 2 轮混合（标准 AES 是 9/13/14 轮）。
Python 没有内置 AES，这是 medusa 头必需的。

参考实现：openhongguo ``internal/protocol/sign/aes.go``（Apache-2.0）。
"""

from __future__ import annotations

import base64
import struct

_SBOX_B64 = (
    "+n0Ia5xZs0sEXznQOEqRmQBnpiCf9U2Ccybu3xhmgzOAAxn72f6uqqmwUsYL83klTni0NqxdG"
    "ieeiNu9PGPsSRXBMB/cuFbUbM3KCUPINaPvHvSW0vwOcnuUhNHqRVpiAj/TEoE0K91+5ijypUYT"
    "ATsh9mE3KSoN7Yyvv51cuyR2D3XkU4nhmI2xmmVwT1RMWKtub4sjxAcRDLrPoKSO2AU9FLLadMPX5"
    "77Wf95IFj6FkKFVt3dCIsmGUC4X+WQxLJvxbRxEaOPpqJOXyzJX6+Vxaq3AzMfF/WAdoi1Hp+JRa"
    "V56zgpBtpWP97mH4DoGEIq1+FvV8LyS/3wvwugbQOwb2r26mJEMsiuDQTRn+wrYdrVGBVlhI3WQhyr"
    "jUBVMrLF5667llUcEaPCGPVGLD8qO5LlO8hKCvA7V9+8oJc9bXelqVQLhM76T5/WtnT45JKji+hdX0"
    "HoNCDDWuKON/QeaxB5uImSX0h2wv0VmP2zd2yeApxHcpsVS+MC2yFwAc2B7oBkTqsk1SEvTpM2fmfM"
    "QREBUfin0Bh+iq6EvPPavhWI2IX9e3yAas7Tm/3KEj2UmlFp36kN4x0rMLBRrxuh0U/zUHM4xcAMYjJ"
    "Y4MonxOl/X+alpt2M3WMI7w3HLnpIBigtNiJu7T21v4P6lSd5WFgntnMEt7oF92XzRLkJbTcGmXepE"
    "/UVOG6E/0YnhfS+q26utWcuxzpooyeD2cDlK1/8w9d28VzsRjbLuALbmGlp8+d7EzS6Au7lMpZ+ECM"
    "ZvQmzwJ+eLOpxR+2chdUExp8ogQyq3v9l68rX4jCwjg0+PYKAEEzcU4wHFY2ZcdIHfWL1okD3SszT0"
    "GZMyKdZJrg1L2AeerB4tC0C4crp2EHGo5FYdSP7lwkeR2ocmnR+Ia8CYviUJlzOjhRZef9xuVOn3"
    "qcjow3fQgivsAmKKkg4+sA8F8/GWeDiGNhg8JM8KtFPMYWWkx5TVFX5t73kiNRJqjlIGVXtGZFCV4g"
    "zt0xcDopuZ6xz8r9Rzafpf9ywev8jh8592gHFIqpStZPuJxmDDMrNN0uBE3V+oscdoIzTJbRJ/t+"
    "sVvqnReJOgDJKk10fjisJwqyZBmnmn2BSFj8BvVtCMEbkuPOKdzw7eA11GPs04Qw8zWtkaZWwiO/w"
    "wpojqN6K0jY5RnNZA7vn4hPSul+nKCkVnVwQvg1zVxcSCtqORmB9KrJaBbssbCQivGJVJfVTt+hYx"
    "Otq4ZvWl8f4QAQZ0zGPffCgl9s6yT4vlvIdpu4YhBwA25wtQWZsc6GJYGWHyvSdeuh3mmUI9DSq"
    "13Fsp8C1MU3tqc04/df9LoTUXVXI5INOw/e8C7Hd+5CvbkMEFnnrUUmskEw=="
)

_AES_CON2 = ((1, 0, 2, 3), (2, 0, 3, 1), (0, 1, 3, 2), (1, 0, 2, 3))
_AES_ORDER = (
    (0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3),
    (0, 9, 14, 15, 4, 13, 2, 7, 8, 1, 6, 3, 12, 5, 10, 11),
    (0, 9, 14, 7, 4, 13, 2, 11, 8, 1, 6, 3, 12, 5, 10, 15),
    (0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3),
)
_AES_INIT_WORD = (0xCA025DDC, 0x823DC546, 0xC9420583, 0xC298225F)


def _all_sbox() -> bytes:
    return base64.b64decode(_SBOX_B64)


def _xor_bytes(a: bytes, b: bytes) -> bytes:
    out = bytearray(a)
    for i in range(len(out)):
        out[i] ^= b[i % len(b)]
    return bytes(out)


def _expand_aes(key: bytes, word: int) -> list[bytes]:
    init = _AES_INIT_WORD[word]
    box = _all_sbox()[word * 256:word * 256 + 256]
    initial = struct.pack("<I", init) * 4
    expanded = bytearray(_xor_bytes(initial, key))
    expanded += bytes(32)
    rounds = 8
    for i in range(4, 12):
        at = 4 * (i - 1)
        a, b, c, d = expanded[at], expanded[at + 1], expanded[at + 2], expanded[at + 3]
        if i & 3 == 0:
            t = ((init >> (rounds & 24)) ^ box[b]) & 255
            b = box[c]
            c = box[d]
            d = box[a]
            a = t
        rounds += 2
        expanded[at + 4] = a ^ expanded[at - 12]
        expanded[at + 5] = b ^ expanded[at - 11]
        expanded[at + 6] = c ^ expanded[at - 10]
        expanded[at + 7] = d ^ expanded[at - 9]
    return [bytes(expanded[i * 4:i * 4 + 4]) for i in range(12)]


class AesV3:
    """AES-V3 变体（medusa 用）。"""

    def __init__(self, key: bytes, khronos: int):
        word = khronos & 3
        allbox = _all_sbox()
        self.box = allbox[word * 256:word * 256 + 256]
        self.con2 = _AES_CON2[word]
        self.order = _AES_ORDER[word]
        self.keys = _expand_aes(key, word)

    # ---------------------------------------------------------- 轮运算

    def _add_con(self, state, keys):
        for i in range(4):
            for j in range(4):
                state[i][j] ^= keys[i][self.con2[j]]

    def _sub(self, state):
        for i in range(4):
            for j in range(4):
                state[i][j] = self.box[state[i][j]]
        old = [row[:] for row in state]
        for i in range(4):
            state[i] = old[self.con2[i]]

    def _shift(self, state):
        flat = []
        for i in range(4):
            for j in range(4):
                flat.append(state[i][j])
        for i in range(16):
            state[i // 4][i % 4] = flat[self.order[i]]

    def _shift_con(self, state):
        old = [row[:] for row in state]
        for i in range(4):
            for j in range(4):
                state[i][j] = old[i][self.con2[j]]

    @staticmethod
    def _mix_columns(state):
        def xt(x):
            v = (x << 1) & 0xFF
            if x & 0x80:
                v ^= 0x1B
            return v & 0xFF

        for i in range(4):
            t = state[0][i] ^ state[1][i] ^ state[2][i] ^ state[3][i]
            u = state[0][i]
            state[0][i] ^= t ^ xt(state[0][i] ^ state[1][i])
            state[1][i] ^= t ^ xt(state[1][i] ^ state[2][i])
            state[2][i] ^= t ^ xt(state[2][i] ^ state[3][i])
            state[3][i] ^= t ^ xt(state[3][i] ^ u)

    def _block(self, value: bytes) -> bytes:
        state = [[value[i * 4 + j] for j in range(4)] for i in range(4)]
        keys = self.keys
        self._add_con(state, keys[0:4])
        for i in (1, 2):
            self._sub(state)
            self._shift(state)
            if i == 1:
                self._shift_con(state)
                self._mix_columns(state)
            self._add_con(state, keys[i * 4:i * 4 + 4])
        for i in range(4):
            for j in range(4):
                state[i][j] ^= keys[4 + i][j]
        out = bytearray()
        for i in range(4):
            out += bytes(state[i])
        return bytes(out)

    # ---------------------------------------------------------- 对外

    def encrypt(self, data: bytes, iv: bytes) -> bytes:
        source = bytearray(data)
        plaintext = bytearray(32)
        for i in range(31):
            at = i * 8
            n0 = (source[at] >> 4) & 2
            n1 = n0 | (source[at + 1] & 64)
            n2 = n1 | ((source[at + 2] >> 2) & 1)
            n3 = n2 | ((source[at + 3] << 3) & 128)
            n4 = n3 | ((source[at + 4] >> 1) & 4)
            n5 = n4 | ((source[at + 5] << 3) & 16)
            n6 = n5 | ((source[at + 6] << 5) & 32)
            plaintext[i] = n6 | ((source[at + 7] >> 4) & 8)
        plaintext[31] = 1

        blocks = bytearray()
        previous = bytes(iv)
        for off in range(0, 32, 16):
            value = self._block(_xor_bytes(bytes(plaintext[off:off + 16]), previous))
            blocks += value
            previous = value

        out = bytearray(source)
        for i in range(31):
            at = i * 8
            k = blocks[i]
            out[at] &= 0xDF
            out[at] |= (k << 4) & 0x20
            out[at + 1] &= 0xBF
            out[at + 1] |= k & 0x40
            out[at + 2] &= 0xFB
            out[at + 2] |= (k << 2) & 0x04
            out[at + 3] &= 0xEF
            out[at + 3] |= (k >> 3) & 0x10
            out[at + 4] &= 0xF7
            out[at + 4] |= (k + k) & 0x08
            out[at + 5] &= 0xFD
            out[at + 5] |= (k >> 3) & 0x02
            out[at + 6] &= 0xFE
            out[at + 6] |= (k >> 5) & 0x01
            out[at + 7] &= 0x7F
            out[at + 7] |= (k << 4) & 0x80
        return bytes([blocks[31]]) + bytes(out)
