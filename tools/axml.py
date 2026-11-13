"""解析 Android 二进制 AXML（AndroidManifest.xml），提取 Intent 声明。

★★ 这个 APK 的 AXML 变体（实测踩坑记录）：
  · chunk 头是 **8 字节**（type(4)+size(4)），不是 AOSP 的 10 字节
    → 按 10 字节解析会得到 count=0 / hdr=54856 的荒谬值
  · 字符串池：chunk(8) + count(4) style(4) flags(4) stringsStart(4) stylesStart(4)
    偏移表起点 = pos + 28
  · 属性结构（20 字节/个）：ns(4) nameIdx(4) rawValue(4) type(4) data(4)
    ★ **属性名 = nameIdx 指向字符串池**（如 26='scheme'、27='host'、3='name'）
    ★ **属性值 = typedValue.data**（type=0x03 时是字符串池索引）
    ★ rawValue 指向 android: 命名空间 URI，不是属性名
"""
from __future__ import annotations

import struct
import sys
import zipfile

CHUNK_STRING_POOL = 0x001C0001
CHUNK_XML_START = 0x00100102
CHUNK_XML_END = 0x00100103
CHUNK_RESOURCE_MAP = 0x00080180

# 常见 android: 属性 → 资源 id（备用；优先用 nameIdx 查字符串池）
ATTR_IDS = {
    0x01010003: "name", 0x01010000: "label", 0x0101001E: "exported",
    0x01010028: "authorities", 0x01010027: "permission",
    0x01010024: "launchMode", 0x0101002D: "scheme", 0x0101002E: "host",
    0x01010030: "path", 0x01010031: "pathPrefix", 0x01010032: "pathPattern",
    0x0101002C: "mimeType",
}


def parse_string_pool(d: bytes, pos: int):
    count, _style, flags, str_start, _sty = struct.unpack_from("<IIIII", d, pos + 8)
    is_utf8 = bool(flags & 0x100)
    off_base = pos + 28
    data_base = pos + str_start

    strings = []
    for i in range(count):
        off = struct.unpack_from("<I", d, off_base + 4 * i)[0]
        p = data_base + off
        if is_utf8:
            bl = struct.unpack_from("<H", d, p + 2)[0]
            strings.append(d[p + 4:p + 4 + bl].decode("utf-8", errors="replace"))
        else:
            ln = struct.unpack_from("<H", d, p)[0]
            strings.append(d[p + 2:p + 2 + ln * 2].decode("utf-16-le", errors="replace"))
    return strings, pos + struct.unpack_from("<I", d, pos + 4)[0]


def parse(apk_path: str):
    z = zipfile.ZipFile(apk_path)
    d = z.read("AndroidManifest.xml")

    pos = 8
    strings, pos = parse_string_pool(d, pos)
    while pos < len(d):
        ctype, csize = struct.unpack_from("<II", d, pos)
        if ctype == CHUNK_RESOURCE_MAP:
            pos += csize
            continue
        break

    events = []
    while pos + 8 <= len(d):
        ctype, csize = struct.unpack_from("<II", d, pos)
        if csize <= 0:
            break

        if ctype in (CHUNK_XML_START, CHUNK_XML_END):
            name_idx = struct.unpack_from("<I", d, pos + 20)[0]
            attrs = {}
            if ctype == CHUNK_XML_START:
                attr_start, attr_size, attr_count = struct.unpack_from("<HHH", d, pos + 24)
                base = pos + 8 + attr_start
                for i in range(attr_count):
                    off = base + i * attr_size
                    name_i = struct.unpack_from("<I", d, off + 4)[0]
                    raw = struct.unpack_from("<I", d, off + 8)[0]
                    dtype = struct.unpack_from("<I", d, off + 12)[0]
                    dval = struct.unpack_from("<I", d, off + 16)[0]

                    # ★ 属性名 = nameIdx 指向的字符串
                    akey = None
                    if 0 <= name_i < len(strings):
                        cand = strings[name_i]
                        if cand and not cand.startswith("android:"):
                            akey = cand
                    if akey is None:
                        akey = ATTR_IDS.get(name_i)
                    if akey is None:
                        continue

                    # ★ 属性值
                    if dtype == 0x03:
                        val = strings[dval] if dval < len(strings) else ""
                    elif dtype == 0x1A:
                        val = strings[raw] if 0 <= raw < len(strings) else f"@0x{dval:08x}"
                    elif dtype == 0x10:
                        val = dval
                    elif dtype == 0x11:
                        val = f"0x{dval:x}"
                    elif dtype == 0x12:
                        val = bool(dval)
                    else:
                        val = None
                    attrs[akey] = val

            tag = strings[name_idx] if name_idx < len(strings) else f"?{name_idx}"
            events.append(("start" if ctype == CHUNK_XML_START else "end", tag, attrs))
        pos += csize
    return events


def build_tree(events):
    comps, comp_stack, filter_stack = [], [], []
    for kind, tag, attrs in events:
        if kind == "start":
            if tag in ("activity", "service", "receiver", "provider"):
                c = {"kind": tag, "attrs": attrs, "filters": []}
                comp_stack.append(c)
                comps.append(c)
            elif tag == "intent-filter":
                f = {"attrs": {}}
                filter_stack.append(f)
                if comp_stack:
                    comp_stack[-1]["filters"].append(f)
            elif tag in ("action", "category", "data"):
                if filter_stack:
                    filter_stack[-1]["attrs"][tag] = attrs
        else:
            if tag == "intent-filter" and filter_stack:
                filter_stack.pop()
            elif tag in ("activity", "service", "receiver", "provider") and comp_stack:
                comp_stack.pop()
    return comps


def main(apk_path: str):
    events = parse(apk_path)
    comps = build_tree(events)
    kinds = {}
    for c in comps:
        kinds[c["kind"]] = kinds.get(c["kind"], 0) + 1
    print(f"组件总数: {len(comps)}  {kinds}")

    print("\n=== ★ 可外部调起的入口（带 scheme / host） ===")
    seen = set()
    for c in comps:
        for f in c["filters"]:
            a = f["attrs"]
            data = a.get("data", {})
            schemes = list(data.get("scheme", {}).values()) if "scheme" in data else []
            hosts = list(data.get("host", {}).values()) if "host" in data else []
            paths = list(data.get("path", {}).values()) if "path" in data else []
            paths += list(data.get("pathPrefix", {}).values()) if "pathPrefix" in data else []
            if not schemes and not hosts:
                continue
            key = (c["attrs"].get("name"), tuple(schemes), tuple(hosts))
            if key in seen:
                continue
            seen.add(key)
            acts = list(a.get("action", {}).values()) if "action" in a else []
            cats = list(a.get("category", {}).values()) if "category" in a else []
            print(f"\n{c['kind']}: {c['attrs'].get('name')}  exported={c['attrs'].get('exported')}")
            if acts: print(f"    action : {acts}")
            if cats: print(f"    categ  : {cats}")
            if schemes: print(f"    scheme : {schemes}")
            if hosts: print(f"    host   : {hosts}")
            if paths: print(f"    path   : {paths}")


if __name__ == "__main__":
    main(sys.argv[1])
