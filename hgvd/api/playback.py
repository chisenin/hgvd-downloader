"""App 网关的播放接口（``/novel/player/video_model/v1/``）。

★ 这是下载**完整正片**的唯一路径。网页版只给 10 秒试看片段
（``accessible_episode_cnt`` 实测 = 5，即前 5 集有 SEO 数据）。

★ body 是 **protobuf 二进制**（实测 118 字节），不是 JSON。
签名与 body 强绑定（``x-gorgon``/``x-ss-stub`` 都覆盖 body），
所以必须**先造出完全相同的 body 字节**，再拿去签名。

★ 从 2026-11-10 起，该接口校验真实的 ``x-argus`` / ``x-ladon``；
搜索、详情等其它接口仍接受 4 字节时间戳桩。
"""

from __future__ import annotations

import json
import struct
import time

from .. import net
from ..sign.core import DeviceIdentity, sign_playback
from ..util import j_int, j_str

API_HOST = "api5-normal-sinfonlinea.fqnovel.com"
PATH_VIDEO_MODEL = "/novel/player/video_model/v1/"

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
) -> bytes:
    """构造 video_model 的 request body。

    ★ body 是 **JSON**（实测 118 字节，虽然请求头写的是
      ``application/x-protobuf``，内容其实是 JSON）：

      {"biz_param":{"need_all_video_definition":true,"video_id_type":0,
                    "video_platform":3},"video_id":"<vid>"}

    ★ 字段与 Go 参考实现一致。**字节必须完全一致** ——
      ``x-gorgon`` 与 ``x-ss-stub`` 都覆盖 body，
      哪怕只差一个空格，服务端也会拒签。
    """
    import json as _json

    payload = {
        "biz_param": {
            "need_all_video_definition": True,
            "video_id_type": 0,
            "video_platform": 3,
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


def fetch_playback(
    session,
    ident: DeviceIdentity,
    video_id: str,
    *,
    cookies: dict | None = None,
    definition: str = "",
    timeout: int = 25,
) -> dict:
    """取一集的播放模型。

    返回原始 JSON（含各清晰度地址、加密信息）。
    """
    body = build_playback_body(video_id, definition)
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

    url = f"https://{API_HOST}{PATH_VIDEO_MODEL}?{query}"
    r = session.post(url, data=body, headers=hdr, timeout=timeout)
    if r.status_code != 200:
        raise RuntimeError(f"video_model HTTP {r.status_code}")
    if not r.content:
        raise RuntimeError(
            "video_model 返回空 body —— 签名被拒或设备被限流"
            "（常见于未登录；请先执行 login）"
        )
    import json

    try:
        return json.loads(r.text)
    except Exception as e:  # noqa: BLE001
        raise RuntimeError(f"video_model 返回非 JSON：{r.text[:200]}") from e


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
