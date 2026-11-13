"""App 网关的批量详情（带签名）。

★ 收藏夹接口只给 ``series_id``，标题与分集列表要靠这里补。
★ 分两种内容：

  - ``book_type=2``（短剧视频）：``video_list`` 里有完整分集（vid / 标题 / 时长）
  - ``book_type=3``（番茄小说的「书」）：``video_list`` 为空、
    ``episode_cnt=0`` —— 服务端**确实没有视频分集**（不是接口没给全）

★ 接口会**间歇性限流**（连续请求时返回 HTTP 200 + 空 body），
  所以必须有重试 + 间隔。
"""

from __future__ import annotations

import json
import time

from .. import net
from ..sign.core import DeviceIdentity, sign_request
from ..util import j_int, j_str

PATH_DETAIL = "/novel/player/multi_video_detail/preload/v1"

UA = (
    "com.phoenix.read/73932 (Linux; U; Android 14; zh_CN; 23127PN0CC; "
    "Build/UKQ1.230804.001; Cronet/TTNetVersion:8d40f833 2026-03-03)"
)


def device_pairs(ident: DeviceIdentity) -> list[tuple[str, str]]:
    """设备参数（顺序与 App 一致）。"""
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


def fetch_details(
    session,
    ident: DeviceIdentity,
    series_ids: list[str],
    *,
    cookies: dict | None = None,
    chunk_size: int = 20,
    retries: int = 3,
    pause: float = 0.4,
) -> dict:
    """批量取详情。返回 ``{series_id: {title, episode_cnt, episodes:[...], ...}}``。

    ★ 每批之间有 ``pause`` —— 实测连续请求会被限流（空 body）。
    """
    out: dict[str, dict] = {}
    for i in range(0, len(series_ids), chunk_size):
        chunk = series_ids[i:i + chunk_size]
        body = {
            "dr_scene": "preload",
            "series_id": ",".join(chunk),
            "biz_param": {
                "detail_page_version": 0,
                "disable_digg_stat": False,
                "source": 7,
                "need_all_video_definition": False,
            },
        }
        raw = json.dumps(body, separators=(",", ":")).encode("utf-8")
        now = int(time.time() * 1000)
        query, headers = sign_request(
            PATH_DETAIL, device_pairs(ident), now,
            ident.device_id, ident.version_name, raw,
        )
        hdr = {
            "User-Agent": UA,
            "Accept": "application/json",
            "X-SS-DP": "8662",
            "Content-Type": "application/json; charset=UTF-8",
            **headers,
        }
        if cookies:
            hdr["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)

        payload = None
        for attempt in range(retries):
            try:
                r = session.post(
                    f"https://api5-normal-sinfonlinea.fqnovel.com{PATH_DETAIL}?{query}",
                    data=raw, headers=hdr, timeout=25,
                )
                if r.content:
                    payload = r.json()
                    break
            except Exception:  # noqa: BLE001
                pass
            time.sleep(1.2 * (attempt + 1))
        if not payload:
            continue

        for sid, item in (payload.get("data") or {}).items():
            if not isinstance(item, dict):
                continue
            vd = item.get("video_data")
            vd = vd if isinstance(vd, dict) else {}
            episodes = []
            for e in vd.get("video_list") or []:
                if not isinstance(e, dict):
                    continue
                vid = j_str(e, "vid") or j_str(e, "video_id")
                if not vid:
                    continue
                episodes.append({
                    "vid": vid,
                    "index": j_int(e, "vid_index") or j_int(e, "index") or len(episodes) + 1,
                    "title": j_str(e, "title") or j_str(e, "video_title"),
                    "duration": float(e.get("duration") or 0),
                    "series_id": sid,
                })
            out[sid] = {
                "series_id": sid,
                "title": j_str(vd, "series_title") or sid,
                "cover": j_str(vd, "series_cover"),
                "intro": j_str(vd, "series_intro"),
                "episode_cnt": j_int(vd, "episode_cnt"),
                "content_type": j_int(vd, "content_type"),
                "episodes": episodes,
            }
        if i + chunk_size < len(series_ids):
            time.sleep(pause)
    return out
