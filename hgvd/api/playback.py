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
    """取一集的播放模型。

    返回原始 JSON（含各清晰度地址、加密信息）。

    ★ ``video_id_type``：``0`` = 传 vid（短剧），``1`` = 传 series_id（教学内容）。
    ★ 必须用播放专用签名（``sign_playback``：真 argus/ladon、**不带 medusa**）——
      用普通签名会被网关拦成 **HTTP 200 + 空 body**。

    ★★ **网关故障转移**：``fqnovel`` 那组网关会静默丢弃 ``video_model`` 请求
      （HTTP 200 + 空 body），``reading.snssdk.com`` 正常。
      ⇒ 依次试 ``API_HOSTS``，遇到**空 body 立刻换下一个网关**（不等退避），
      所有网关都空 body 才退避重试一轮。

    ★★★ **video_id_type 自动回退**：拿到 JSON 且 ``code=101000`` 说明
      **签名是通的**（否则会空 body），只是 id 类型对不上
      （短剧用 0、教学用 1，且两个网关的语义还不完全一样：
      snssdk 上短剧用 1 反而正常）。
      ⇒ 这时**换另一种 type 再打一枪**。
    """
    host_list = tuple(hosts) if hosts else API_HOSTS
    body = build_playback_body(video_id, definition, video_id_type=video_id_type)
    last = ""
    for attempt in range(retries):
        for host in host_list:
            payload, err = _post_playback(
                session, ident, body, cookies, host, timeout)
            if payload is None:
                last = err
                continue
            code = payload.get("code")
            if code == 101000 and video_id_type in (0, 1):
                # ★ id 类型对不上 —— 换另一种再试（只试一次）
                alt = build_playback_body(
                    video_id, definition, video_id_type=1 - video_id_type)
                alt_payload, alt_err = _post_playback(
                    session, ident, alt, cookies, host, timeout)
                if alt_payload is not None and alt_payload.get("code") in (0, None):
                    return alt_payload
                if alt_err:
                    last = alt_err
                elif alt_payload is not None:
                    last = f"{host}: code={alt_payload.get('code')}"
            return payload
        time.sleep(pause * (attempt + 1))
    raise RuntimeError(f"video_model 无响应：{last}")


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
