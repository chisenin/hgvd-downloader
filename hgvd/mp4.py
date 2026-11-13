"""最小 MP4 box 解析 + CENC AES-CTR 解密。

★ 为什么需要自己写：字节的短剧视频用 ``bvc2`` 这个私有 tag 标记加密的
  H.264（``encv`` + ``sinf``/``schm=cenc`` + ``senc``/``saiz``），
  而且 **avcC（SPS/PPS）被抹掉了** —— SPS/PPS 藏在加密的样本里。
  ffmpeg 因此既认不出 codec，也解不出内容。

★ 流程：
    1. 解析 box 树，拿到每个 trak 的样本表（stsz/stsc/stco）
    2. 读对应 trak 的 ``senc``，拿到每个样本的 IV
    3. 按 CENC AES-CTR 解密样本数据
    4. 拼成 Annex-B（``00 00 00 01`` + 样本）→ 完整 h264（含内嵌 SPS/PPS）

★ CENC AES-CTR：counter block = IV(8 字节) + 4 字节大端计数器；
  计数器随每个 16 字节块递增。
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field

try:
    from Crypto.Cipher import AES
except ImportError:  # pragma: no cover
    AES = None


# ---------------------------------------------------------------- box 解析


@dataclass
class Box:
    """一个 box。"""

    type: str
    start: int          # 内容起点
    size: int           # 内容长度
    children: list = field(default_factory=list)


def _parse(data: bytes, start: int, end: int, depth: int = 0) -> list[Box]:
    """解析 ``[start, end)`` 区间的 box 列表。"""
    out: list[Box] = []
    i = start
    while i + 8 <= end:
        size = struct.unpack_from(">I", data, i)[0]
        btype = data[i + 4:i + 8].decode("latin-1")
        hdr = 8
        if size == 1:
            if i + 16 > end:
                break
            size = struct.unpack_from(">Q", data, i + 8)[0]
            hdr = 16
        elif size == 0:
            size = end - i
        if size < hdr or i + size > end:
            break
        box = Box(btype, i + hdr, size - hdr)
        if btype in ("moov", "trak", "mdia", "minf", "stbl", "moof", "traf"):
            box.children = _parse(data, i + hdr, i + size, depth + 1)
        out.append(box)
        i += size
    return out


def find_box(boxes: list[Box], path: str) -> Box | None:
    """按 ``"a/b/c"`` 路径找第一个匹配的 box。"""
    parts = path.split("/")
    cur = boxes
    found = None
    for p in parts:
        found = None
        for b in cur:
            if b.type == p:
                found = b
                break
        if found is None:
            return None
        cur = found.children
    return found


def find_all(boxes: list[Box], btype: str) -> list[Box]:
    out = []
    for b in boxes:
        if b.type == btype:
            out.append(b)
        out.extend(find_all(b.children, btype))
    return out


# ---------------------------------------------------------------- 样本表


@dataclass
class Track:
    """一条轨道。"""

    track_id: int = 0
    handler: str = ""          # vide / soun
    codec_tag: str = ""        # 原始 sample entry tag
    encrypted: bool = False
    iv_size: int = 0
    kid: bytes = b""
    sizes: list[int] = field(default_factory=list)
    offsets: list[int] = field(default_factory=list)
    ivs: list[bytes] = field(default_factory=list)
    duration: float = 0.0
    timescale: int = 0


def _parse_stbl(data: bytes, trak: Box, out: Track) -> None:
    """解析一条 trak 的样本表。"""
    stbl = find_box(trak.children, "mdia/minf/stbl")
    if stbl is None:
        return

    # hdlr → handler type
    hdlr = find_box(trak.children, "mdia/hdlr")
    if hdlr is not None:
        out.handler = data[hdlr.start + 8:hdlr.start + 12].decode("latin-1", "replace")

    # mdhd → timescale / duration
    mdhd = find_box(trak.children, "mdia/mdhd")
    if mdhd is not None:
        ver = data[mdhd.start]
        if ver == 1:
            out.timescale = struct.unpack_from(">I", data, mdhd.start + 20)[0]
            dur = struct.unpack_from(">Q", data, mdhd.start + 24)[0]
        else:
            out.timescale = struct.unpack_from(">I", data, mdhd.start + 12)[0]
            dur = struct.unpack_from(">I", data, mdhd.start + 16)[0]
        if out.timescale:
            out.duration = dur / out.timescale

    # stsd → codec tag + sinf/tenc
    stsd = find_box(stbl.children, "stsd")
    if stsd is not None:
        # stsd: version/flags(4) entry_count(4) 然后是 entries
        p = stsd.start + 8
        # 跳过 sample entry 的固定头（6 保留 + 2 dref index）
        p += 8
        out.codec_tag = data[p:p + 4].decode("latin-1", "replace")
        # tenc（version 1 布局：reserved / crypt+skip / isProtected / IV_Size / KID）
        tenc = find_box(find_all([stsd], "sinf"), "sinf/tenc")
        if tenc is not None:
            out.iv_size = data[tenc.start + 7]
            out.kid = data[tenc.start + 8:tenc.start + 24]
            out.encrypted = data[tenc.start + 6] != 0

    # stsz → 样本大小
    stsz = find_box(stbl.children, "stsz")
    if stsz is not None:
        uniform = struct.unpack_from(">I", data, stsz.start + 4)[0]
        count = struct.unpack_from(">I", data, stsz.start + 8)[0]
        if uniform:
            out.sizes = [uniform] * count
        else:
            out.sizes = list(
                struct.unpack_from(f">{count}I", data, stsz.start + 12)
            ) if count else []

    # stsc + stco/co64 → 样本偏移
    stsc = find_box(stbl.children, "stsc")
    stco = find_box(stbl.children, "stco")
    co64 = find_box(stbl.children, "co64")
    if stsc is None or (stco is None and co64 is None):
        return
    sc = stsc.start
    n_entries = struct.unpack_from(">I", data, sc + 4)[0]
    sc_entries = []
    for k in range(n_entries):
        first_chunk, samples_per_chunk = struct.unpack_from(">II", data, sc + 8 + k * 12)
        sc_entries.append((first_chunk, samples_per_chunk))

    chunk_offsets = []
    if stco is not None:
        c = struct.unpack_from(">I", data, stco.start + 4)[0]
        chunk_offsets = list(struct.unpack_from(f">{c}I", data, stco.start + 8))
    else:
        c = struct.unpack_from(">I", data, co64.start + 4)[0]
        chunk_offsets = list(struct.unpack_from(f">{c}Q", data, co64.start + 8))

    # 展开成每样本偏移
    sample_idx = 0
    ci = 0
    while ci < len(chunk_offsets) and sample_idx < len(out.sizes):
        entry = None
        for first_chunk, spc in sc_entries:
            if first_chunk - 1 <= ci:
                entry = spc
            else:
                break
        if entry is None:
            entry = sc_entries[0][1] if sc_entries else 1
        off = chunk_offsets[ci]
        for _ in range(entry):
            if sample_idx >= len(out.sizes):
                break
            out.offsets.append(off)
            off += out.sizes[sample_idx]
            sample_idx += 1
        ci += 1

    # senc → 每样本 IV
    senc = find_box(find_all(stbl.children, "senc"), "senc")
    if senc is not None:
        ver_flags = struct.unpack_from(">I", data, senc.start)[0]
        ver = ver_flags >> 24
        flags = ver_flags & 0xFFFFFF
        count = struct.unpack_from(">I", data, senc.start + 4)[0]
        iv_len = out.iv_size if out.iv_size in (8, 16) else 8
        p = senc.start + 8
        for _ in range(count):
            if p + iv_len > len(data):
                break
            out.ivs.append(data[p:p + iv_len])
            p += iv_len
            if flags & 0x000002:      # subsample 信息
                n_sub = struct.unpack_from(">H", data, p)[0]
                p += 2 + n_sub * 6
        out.encrypted = out.encrypted or bool(out.ivs)


def parse_tracks(data: bytes) -> list[Track]:
    """解析文件里的所有轨道。"""
    boxes = _parse(data, 0, len(data))
    moov = find_box(boxes, "moov")
    if moov is None:
        return []
    tracks = []
    for trak in moov.children:
        if trak.type != "trak":
            continue
        t = Track()
        tkhd = find_box(trak.children, "tkhd")
        if tkhd is not None:
            ver = data[tkhd.start]
            t.track_id = struct.unpack_from(">I", data, tkhd.start + (20 if ver == 1 else 12))[0]
        _parse_stbl(data, trak, t)
        tracks.append(t)
    return tracks


# ---------------------------------------------------------------- 解密


def ctr_crypt(key: bytes, iv: bytes, data: bytes) -> bytes:
    """CENC AES-CTR 解密一段数据。

    ★ counter block = IV + 4 字节大端计数器；每 16 字节块计数器 +1。
    ★ IV 长度可以是 8 或 16 字节。
    """
    if AES is None:
        raise RuntimeError("需要 pycryptodome 或 cryptography 来解密")
    if len(iv) == 8:
        base = iv + b"\x00\x00\x00\x00"
    elif len(iv) == 16:
        base = iv
    else:
        base = iv[:16].ljust(16, b"\x00")
    out = bytearray()
    ctr = 0
    aes = AES.new(key, AES.MODE_ECB)
    for off in range(0, len(data), 16):
        ks = aes.encrypt(base[:12] + struct.pack(">I", ctr))
        chunk = data[off:off + 16]
        out += bytes(a ^ b for a, b in zip(chunk, ks))
        ctr += 1
    return bytes(out)


def decrypt_track(data: bytes, track: Track, key: bytes) -> bytes:
    """解密一条轨道，输出 Annex-B 字节流。

    ★ 每个样本前面加 4 字节起始码 ``00 00 00 01``。
    """
    out = bytearray()
    n = min(len(track.sizes), len(track.offsets), len(track.ivs))
    for i in range(n):
        size = track.sizes[i]
        off = track.offsets[i]
        raw = data[off:off + size]
        if not raw:
            continue
        if track.encrypted and track.ivs:
            raw = ctr_crypt(key, track.ivs[i], raw)
        out += b"\x00\x00\x00\x01" + raw
    return bytes(out)


def decrypt_audio_adts(data: bytes, track: Track, key: bytes) -> bytes:
    """解密音频轨，输出 ADTS 裸流（带 7 字节 ADTS 头）。

    ★ mp4 里的 AAC 样本没有 ADTS 头，要自己加才能被 ffmpeg 识别。
    """
    out = bytearray()
    n = min(len(track.sizes), len(track.offsets), len(track.ivs))
    for i in range(n):
        size = track.sizes[i]
        off = track.offsets[i]
        raw = data[off:off + size]
        if not raw:
            continue
        if track.encrypted and track.ivs:
            raw = ctr_crypt(key, track.ivs[i], raw)
        out += _adts_header(size) + raw
    return bytes(out)


def _adts_header(payload: int) -> bytes:
    """7 字节 ADTS 头（AAC-LC, 44.1kHz, 立体声）。"""
    fl = 0x0F          # MPEG-4, layer 00, protection off
    profile = 0x01     # AAC LC
    sf = 0x04          # 采样率索引 4 = 44100
    ch = 0x01          # 声道配置 1 = 单声道（保守值，ffmpeg 会自适应）
    # 帧长 = 7 + payload
    total = 7 + payload
    b = bytearray(7)
    b[0] = 0xFF
    b[1] = 0xF1 | (profile << 6)
    b[2] = (sf << 2) | (ch >> 2)
    b[3] = ((ch & 3) << 6) | ((total >> 11) & 3)
    b[4] = (total >> 3) & 0xFF
    b[5] = ((total & 7) << 5) | 0x1F
    b[6] = 0xFC
    return bytes(b)
