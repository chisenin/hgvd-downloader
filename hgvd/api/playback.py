"""App 网关的播放接口。

★★★ **2026-11-20 协议变更（MITM 实测，结论已变，以本节为准）**

旧接口 ``/novel/player/video_model/v1/`` 已被服务端停用：任何请求都
**HTTP 200 + 0 字节**（匿名、错 vid、拆签名全一样）。

新接口::

    POST /novel/player/multi_video_model/preload/v1?<query>
    Content-Type: application/json; charset=utf-8
    Content-Encoding: gzip
    body = gzip({"biz_param":{…, "video_platform":1024},
                 "dr_scene":"preload",
                 "mixed_video_id_map":{"1004":[vid,…]}})

**这个接口不要求签名头** —— 签名头不参与成功与否的判定；query 只留 9 个参数也可以；
不带 Cookie（匿名）也可以。唯一硬要求是 **body 里必须有 ``biz_param``**
（只给 dr_scene + map 会 ``code=100001`` 参数错误）。

⇒ 之前「v04.10 改了 native 签名算法」的判断是**错的** —— 真实原因是
  接口换了、body 形状换了。判据与对照过程见维护者本地留档（不随仓库发布）。

响应仍是双层 JSON::

    {"code":0,"data":{"<vid>":{"video_model":"<JSON 字符串>", …}}}
"""

from __future__ import annotations

import gzip
import json
import struct
import time

from .. import net
from ..sign.core import DeviceIdentity, sign_playback
from ..util import j_int, j_str

API_HOST = "api5-normal-sinfonlinea.fqnovel.com"

# ★★★ 播放接口的**网关故障转移**（2026-11-14 17:0x 定位）
#
# 现象：``api5-normal-*.fqnovel.com`` 这组网关会把 ``video_model`` 请求
#       **静默丢弃**（HTTP 200 + 零字节 body），而**同一份签名**打到
#       ``reading.snssdk.com`` 却能正常返回（code=0 + 各清晰度直链）。
#
# ★ 关键鉴别实验（证明不是签名错、不是账号、不是内容）：
#   · 同一时刻同一签名换 host ⇒ fqnovel 空 body / snssdk code=0
#   · 连**不存在的 vid** 在 fqnovel 上也是空 body ⇒ 请求在业务逻辑之前
#     就被丢弃，不是「内容查不到」
#   · 同一 host 上的 ``video_detail`` 返回 JSON code=100001
#     ⇒ **签名被接受**（否则也会空 body）
#   · 两个网关返回的 video_model 结构**逐字段一致**（同清晰度阶梯 360~1080p）
#
# ⇒ 所以是**网关侧策略**，不是客户端实现问题。
#   修法：按顺序试多个网关，遇到空 body 立刻换下一个（不等退避）。
API_HOSTS: tuple[str, ...] = (
    "reading.snssdk.com",
    "api5-normal-sinfonlinea.fqnovel.com",
)


def playback_hosts() -> tuple[str, ...]:
    """本次要试的播放网关（按顺序）。

    ★ 可用环境变量 ``HGVD_PLAYBACK_HOST`` 覆盖（逗号分隔），例如::

        HGVD_PLAYBACK_HOST=api5-normal-sinfonlinea.fqnovel.com,reading.snssdk.com

    —— 故意把「会丢请求的网关」放第一位，**用来验证故障转移分支**：
    第一个网关空 body 时应当立刻换第二个并成功。
    """
    import os

    raw = (os.environ.get("HGVD_PLAYBACK_HOST") or "").strip()
    if raw:
        hosts = tuple(h.strip() for h in raw.split(",") if h.strip())
        if hosts:
            return hosts
    return API_HOSTS


PATH_VIDEO_MODEL = "/novel/player/video_model/v1/"
#: 2026-11-20 起真正可用的播放模型接口（旧的 video_model/v1 已停用）
PATH_PRELOAD = "/novel/player/multi_video_model/preload/v1"

#: preload 的网关候选（顺序无关紧要，第一个实测可用）
#: ``mixed_video_id_map`` 的键 = **内容类型**（实测）：
#: 1004 = 视频流/短剧；1000/1001 = 教学合集分集。顺序即尝试顺序。
PRELOAD_MAP_KEYS: tuple[str, ...] = ("1004", "1001", "1000")

PRELOAD_HOSTS: tuple[str, ...] = (
    "api5-normal-sinfonlineb.fqnovel.com",
    "api5-normal-lf.fqnovel.com",
    "reading.snssdk.com",
)

APP_UA_74132 = (
    "com.phoenix.read/74132 (Linux; U; Android 15; zh_CN_#Hans; 22041216C; "
    "Build/V417IR;tt-ok/3.12.13.20)"
)

APP_UA = (
    "com.phoenix.read/73932 (Linux; U; Android 14; zh_CN; 23127PN0CC; "
    "Build/UKQ1.230804.001; Cronet/TTNetVersion:8d40f833 2026-03-03 "
    "QuicVersion:21ac1950 2025-11-18)"
)


# ---------------------------------------------------------------- protobuf


def _varint(value: int) -> bytes:
    out = bytearray()
    value &= 0xFFFFFFFFFFFFFFFF
    while value > 127:
        out.append((value & 127) | 128)
        value >>= 7
    out.append(value)
    return bytes(out)


def _pb_key(tag: int, wire: int) -> bytes:
    return _varint((tag << 3) | wire)


def _pb_str(tag: int, s: str) -> bytes:
    b = s.encode("utf-8")
    return _pb_key(tag, 2) + _varint(len(b)) + b


def _pb_int(tag: int, v: int) -> bytes:
    return _pb_key(tag, 0) + _varint(v)


def build_playback_body(
    video_id: str,
    definition: str = "",
    *,
    scene: str = "play",
    video_id_type: int = 0,
    video_platform: int = 3,
    need_all: bool = True,
) -> bytes:
    """构造 video_model 的 request body。

    ★ body 是 **JSON**（实测 118 字节，虽然请求头写的是
      ``application/x-protobuf``，内容其实是 JSON）：

      {"biz_param":{"need_all_video_definition":true,"video_id_type":0,
                    "video_platform":3},"video_id":"<vid>"}

    ★ 字段与 Go 参考实现一致。**字节必须完全一致** ——
      ``x-gorgon`` 与 ``x-ss-stub`` 都覆盖 body，
      哪怕只差一个空格，服务端也会拒签。

    ★★★ ``video_id_type`` 是决定性的（2026-11-13 实测）：

      - ``0`` = 传 **vid**（短剧用）—— 教学类内容会返回 ``code=101000``
      - ``1`` = 传 **series_id** —— 教学类内容（``content_type=1001``）
        用这个才拿得到播放地址
    """
    import json as _json

    payload = {
        "biz_param": {
            "need_all_video_definition": need_all,
            "video_id_type": video_id_type,
            "video_platform": video_platform,
        },
        "video_id": video_id,
    }
    # ★ 分隔符与键序必须固定 —— 用紧凑分隔符，且保持 dict 插入序
    return _json.dumps(payload, separators=(",", ":"), sort_keys=False).encode("utf-8")


# ---------------------------------------------------------------- 查询串


def device_pairs(ident: DeviceIdentity) -> list[tuple[str, str]]:
    """设备参数（顺序与 App 一致）。

    ★ 顺序不影响服务端，但取值要和 App 一致，否则可能被判异常设备。
    """
    return [
        ("iid", ident.iid),
        ("device_id", ident.device_id),
        ("ac", "wifi"),
        ("channel", "xiaomi_8662_64"),
        ("aid", "8662"),
        ("app_name", "novelread"),
        ("cdid", ident.cdid),
        ("version_code", ident.version_code),
        ("version_name", ident.version_name),
        ("device_platform", "android"),
        ("os", "android"),
        ("ssmix", "a"),
        ("device_type", "Xiaomi 14"),
        ("device_brand", "Redmi"),
        ("language", "zh"),
        ("os_api", "34"),
        ("os_version", "14"),
        ("manifest_version_code", ident.version_code),
        ("resolution", "1080*2400"),
        ("dpi", "480"),
        ("update_version_code", ident.version_code),
        ("host_abi", "arm64-v8a"),
        ("dragon_device_type", "phone"),
        ("pv_player", ident.version_code),
        ("compliance_status", "0"),
        ("need_personal_recommend", "1"),
        ("player_so_load", "1"),
        ("is_android_pad_screen", "0"),
        ("openudid", ident.openudid),
    ]


# ---------------------------------------------------------------- 请求


def _post_playback(
    session,
    ident: DeviceIdentity,
    body: bytes,
    cookies: dict | None,
    host: str,
    timeout: int,
) -> tuple[dict | None, str]:
    """向**一个**网关打一枪播放请求。

    返回 ``(payload, last_error)``：拿到 JSON 就返回 ``(payload, "")``；
    空 body / 网络错 / 非 JSON 都返回 ``(None, 原因)``。
    """
    now_ms = int(time.time() * 1000)
    pairs = device_pairs(ident)
    query, headers = sign_playback(
        PATH_VIDEO_MODEL,
        pairs,
        now_ms,
        ident.device_id,
        ident.version_name,
        body,
        install_id=ident.iid,
        aid="8662",
    )

    hdr = dict(headers)
    hdr.update(
        {
            "User-Agent": APP_UA,
            "X-SS-DP": "8662",
            "x-xs-from-web": "0",
            "Content-Type": "application/x-protobuf; charset=UTF-8",
            "x-reading-request": f"{now_ms}-0",
            "lc": "101",
            "Accept": "application/json; charset=utf-8,application/x-protobuf",
        }
    )
    if cookies:
        hdr["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)

    url = f"https://{host}{PATH_VIDEO_MODEL}?{query}"
    try:
        r = session.post(url, data=body, headers=hdr, timeout=timeout)
    except Exception as e:  # noqa: BLE001
        return None, f"{host}: {str(e)[:120]}"
    if not r.content:
        # ★ 空 body = 这个网关把请求丢了 —— 立刻换下一个网关，不退避
        return None, f"{host}: 空 body (HTTP {r.status_code})"
    try:
        return r.json(), ""
    except Exception:  # noqa: BLE001
        return None, f"{host}: 非 JSON (HTTP {r.status_code})"


# ------------------------------------------------------- 新接口（preload）


def build_preload_body(
    vids,
    *,
    dr_scene: str = "preload",
    video_platform: int = 1024,
    map_key: str = "1004",
    detail_page_version: int = 0,
    device_level: int = 3,
    need_all_video_definition: bool = True,
    use_server_dns: bool = False,
) -> bytes:
    """构造 preload 的 body（**gzip 压缩的 JSON**）。

    ★ 字段与取值按协议下发形状对齐：
      缺 ``biz_param`` → ``code=100001`` 参数错误；
      ``mixed_video_id_map`` 的键 1004 / 1024 都接受。
    """
    payload = {
        "biz_param": {
            "detail_page_version": detail_page_version,
            "device_level": device_level,
            "disable_digg_stat": False,
            "disable_video_relate_book": False,
            "need_all_video_definition": need_all_video_definition,
            "need_mp4_align": False,
            "use_os_player": False,
            "use_server_dns": use_server_dns,
            "video_platform": video_platform,
        },
        "dr_scene": dr_scene,
        "mixed_video_id_map": {map_key: list(vids)},
    }
    raw = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return gzip.compress(raw, mtime=0)


def preload_query(ident: DeviceIdentity, *, now_ms: int | None = None) -> str:
    """preload 的 query（实测只留 9 个也能通，这里保持与 App 一致）。"""
    import urllib.parse

    pairs = list(device_pairs(ident))
    pairs.insert(3, ("_rticket", str(now_ms or int(time.time() * 1000))))
    return urllib.parse.urlencode(pairs)


def _preload_headers(cookies: dict | None) -> dict:
    """preload 的请求头。

    ★ **故意不带任何签名头**：实测全去掉仍然 code=0。
    """
    hdr = {
        "User-Agent": APP_UA_74132,
        "Accept": "application/json; charset=utf-8,application/x-protobuf",
        "Accept-Encoding": "gzip",
        "Content-Type": "application/json; charset=utf-8",
        "Content-Encoding": "gzip",
        "x-xs-from-web": "0",
        "lc": "101",
        "sdk-version": "2",
        "x-tt-store-region": "cn-gd",
        "x-tt-store-region-src": "uid",
    }
    if cookies:
        hdr["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)
    return hdr


def fetch_preload(
    session,
    ident: DeviceIdentity,
    vids,
    *,
    cookies: dict | None = None,
    timeout: int = 25,
    hosts: tuple[str, ...] | None = None,
    dr_scene: str = "preload",
    map_key: str = "1004",
) -> dict:
    """打一发 preload，返回原始 JSON（``data`` 里按 vid 分组）。"""
    body = build_preload_body(vids, dr_scene=dr_scene, map_key=map_key)
    last = ""
    for host in (hosts or PRELOAD_HOSTS):
        url = f"https://{host}{PATH_PRELOAD}?{preload_query(ident)}"
        try:
            r = session.post(url, data=body, headers=_preload_headers(cookies), timeout=timeout)
        except Exception as e:  # noqa: BLE001
            last = f"{host}: {str(e)[:120]}"
            continue
        if not r.content:
            last = f"{host}: 空 body (HTTP {r.status_code})"
            continue
        try:
            payload = r.json()
        except Exception:  # noqa: BLE001
            last = f"{host}: 非 JSON (HTTP {r.status_code})"
            continue
        payload["_host"] = host
        return payload
    raise RuntimeError(
        f"multi_video_model/preload 无响应：{last}"
        "\n★ 2026-11-21 起该端点校验 X-Helios / X-Medusa（与 query+body 绑定），"
        "空 body 即签名被拒")


def fetch_playback(
    session,
    ident: DeviceIdentity,
    video_id: str,
    *,
    cookies: dict | None = None,
    definition: str = "",
    timeout: int = 25,
    video_id_type: int = 0,
    retries: int = 3,
    pause: float = 2.0,
    hosts: tuple[str, ...] | None = None,
) -> dict:
    """取一集的播放模型（**新接口**，返回结构适配老的 ``data.video_model``）。

    ★ 2026-11-20 起走 ``/novel/player/multi_video_model/preload/v1``：
      不要求签名头、不要求登录，body 是 gzip JSON。
    ★ ``video_id_type`` 仅为兼容旧调用点保留 —— 新接口不需要它。
    """
    payload = None
    for key in PRELOAD_MAP_KEYS:
        payload = fetch_preload(
            session, ident, [video_id], cookies=cookies, timeout=timeout,
            hosts=hosts, map_key=key)
        code = payload.get("code")
        data = payload.get("data")
        if code not in (0, None) or not isinstance(data, dict) or not data:
            continue
        entry = data.get(str(video_id)) or next(iter(data.values()), None)
        if not isinstance(entry, dict):
            continue
        adapted = _adapt_preload(payload, entry)
        if parse_streams(adapted):
            adapted["_map_key"] = key
            return adapted
    return _adapt_preload(payload, None) if payload else {}


def _adapt_preload(payload: dict, entry: dict | None) -> dict:
    """把 preload 响应适配成老的 ``data.video_model`` 形状。"""
    if entry is None:
        return payload
    data = payload.get("data") or {}
    if entry is None:
        entry = next(iter(data.values()), None) or {}
    if not isinstance(entry, dict):
        return payload
    vm = entry.get("video_model")
    if isinstance(vm, str):
        try:
            vm = json.loads(vm)
        except Exception:  # noqa: BLE001
            vm = None
    return {
        "code": 0,
        "message": payload.get("message"),
        "_preload": True,
        "_host": payload.get("_host"),
        "_entry": entry,
        "data": {
            "video_model": vm,
            "expire_time": entry.get("expire_time"),
            "video_height": entry.get("video_height"),
            "video_width": entry.get("video_width"),
        },
    }


# ---------------------------------------------------------------- 解析


def _video_model(payload: dict) -> dict:
    """取出 ``data.video_model``（它本身是一个 **JSON 字符串**）。

    ★ 服务端的响应是双层 JSON：外层 ``data.video_model`` 是字符串，
      需要再 parse 一次才能拿到 ``video_list``。
    """
    data = payload.get("data") if isinstance(payload, dict) else None
    if not isinstance(data, dict):
        return {}
    vm = data.get("video_model")
    if isinstance(vm, str):
        try:
            vm = json.loads(vm)
        except Exception:  # noqa: BLE001
            return {}
    return vm if isinstance(vm, dict) else {}


def parse_streams(payload: dict) -> list[dict]:
    """从播放模型里抽出各清晰度的直链。

    ★ 真实结构（实测）：
        data.video_model            ← JSON 字符串
          .video_list[]             ← 每个清晰度一项
            .main_url / .backup_url ← CDN 直链
            .video_meta.definition  ← "360p" / "720p" / …
            .video_meta.vtype        ← "mp4"
            .video_meta.bitrate
            .encrypt_info.spade_a    ← CENC 密钥材料（加密时才有）
    """
    import json as _json

    vm = _video_model(payload)
    out: list[dict] = []
    for item in vm.get("video_list") or []:
        if not isinstance(item, dict):
            continue
        meta = item.get("video_meta")
        meta = meta if isinstance(meta, dict) else {}
        url = item.get("main_url") or item.get("backup_url") or ""
        if not url:
            continue
        enc = item.get("encrypt_info")
        enc = enc if isinstance(enc, dict) else {}
        out.append(
            {
                "url": url,
                "backup_url": item.get("backup_url") or "",
                "definition": j_str(meta, "definition"),
                "quality": j_str(meta, "quality"),
                "codec": j_str(meta, "codec_type"),
                "vtype": j_str(meta, "vtype"),
                "width": j_int(meta, "vwidth"),
                "height": j_int(meta, "vheight"),
                "bitrate": j_int(meta, "bitrate"),
                "size": j_int(meta, "size"),
                "duration": float(vm.get("video_duration") or 0),
                "encrypted": bool(enc.get("encrypt")),
                "spade_a": j_str(enc, "spade_a"),
                "kid": j_str(enc, "kid"),
            }
        )
    return out


def extract_content_key(payload: dict) -> str | None:
    """提取 CENC 内容密钥（hex）。

    ★ 加密分集在每条清晰度的 ``encrypt_info.spade_a`` 里携带被包裹的密钥材料，
    需要按播放器规则解包（见 ``hgvd.drm``）。
    ★ 同一集的所有清晰度共用一个内容密钥。
    """
    from ..drm import derive_content_key

    for st in parse_streams(payload):
        spade = st.get("spade_a") or ""
        if not spade:
            continue
        try:
            key = derive_content_key(spade)
        except Exception:  # noqa: BLE001
            continue
        if key:
            return key
    return None
