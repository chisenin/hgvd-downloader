"""App 网关的批量详情（带签名）。

★ 收藏夹接口只给 ``series_id``，标题与分集列表要靠这里补。
★ 分两种内容：

  - ``book_type=2``（短剧视频）：``video_list`` 里有完整分集（vid / 标题 / 时长）
  - ``book_type=3``（番茄小说的「书」）：``video_list`` 为空、
    ``episode_cnt=0`` —— 服务端**确实没有视频分集**（不是接口没给全）

★ 空 body 表示服务端/网关把这个请求丢弃了 —— **不是限流**，等不会恢复
  （判据见 docs/03-开发要点.md §1.0）。所以先重试/换网关，别做长退避。
"""

from __future__ import annotations

import json
import time

from .. import net
from ..sign.core import DeviceIdentity, sign_playback, sign_request
from ..util import j_int, j_str

PATH_DETAIL = "/novel/player/multi_video_detail/preload/v1"
PATH_ALBUM = "/novel/player/album_detail/v1/"
PATH_PLAYBACK = "/novel/player/video_model/v1/"

HOST = "api5-normal-sinfonlinea.fqnovel.com"

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


def signed_post(
    session,
    ident: DeviceIdentity,
    path: str,
    body: dict,
    cookies: dict | None = None,
    *,
    retries: int = 4,
    base_pause: float = 2.0,
    max_pause: float = 20.0,
    quiet: bool = False,
    playback: bool = False,
) -> dict | None:
    """带签名的 POST，返回解析后的 JSON（失败返回 None）。

    ★ ``playback=True`` 时用**播放专用签名** ``sign_playback``：
      真实的 ``x-argus`` / ``x-ladon``，并且**去掉 ``x-medusa``**。
      带 medusa 时网关会直接返回空 body（历史上两次复现，原因见维护者本地留档）。

    ★ 空 body = 服务端/网关丢弃了这个请求（**不是限流**）；
      ``code=101000`` 是业务异常，换参数才有用。重试只是为了跨过偶发丢包。
    """
    raw = json.dumps(body, separators=(",", ":")).encode("utf-8")
    last = "未知"
    for attempt in range(retries):
        now = int(time.time() * 1000)
        if playback:
            query, headers = sign_playback(
                path, device_pairs(ident), now,
                ident.device_id, ident.version_name, raw,
                install_id=ident.iid, aid="8662",
            )
        else:
            query, headers = sign_request(
                path, device_pairs(ident), now,
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

        try:
            r = session.post(
                f"https://{HOST}{path}?{query}",
                data=raw, headers=hdr, timeout=30,
            )
        except Exception as e:  # noqa: BLE001
            last = f"网络错误 {str(e)[:80]}"
            time.sleep(min(base_pause * (attempt + 1), max_pause))
            continue

        if not r.content:
            last = f"空 body (HTTP {r.status_code})"
            time.sleep(min(base_pause * (attempt + 1), max_pause))
            continue
        try:
            payload = r.json()
        except Exception:  # noqa: BLE001
            last = f"非 JSON (HTTP {r.status_code})"
            time.sleep(min(base_pause * (attempt + 1), max_pause))
            continue

        code = payload.get("code")
        if code == 0:
            return payload
        last = f"code={code} {payload.get('message') or payload.get('debug_info')}"
        # 100001 是参数错（重试没用），101000 是服务端异常（可重试）
        if code == 100001:
            break
        time.sleep(min(base_pause * (attempt + 1), max_pause))

    if not quiet:
        print(f"  ! {path} 失败：{last}")
    return None


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

    ★ 每批之间有 ``pause`` —— 降低请求密度，减少撞上网关丢包。
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
        payload = signed_post(
            session, ident, PATH_DETAIL, body, cookies,
            retries=retries, quiet=True,
        )
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


def fetch_albums(
    session,
    ident: DeviceIdentity,
    album_ids: list[str],
    *,
    cookies: dict | None = None,
    book_type: int = 3,
    retries: int = 4,
    pause: float = 2.0,
    progress=None,
) -> dict:
    """批量取合集详情（含完整分集列表）。

    ★ 这是拿到 ``book_type=3`` 那批「教学合集」分集的**唯一可用端点**。
    ★ 每个合集一个请求，``pause`` 别设太小（密度高时更容易丢包）。
    """
    from .album import parse_album

    out: dict[str, dict] = {}
    total = len(album_ids)
    for i, aid in enumerate(album_ids, 1):
        body = {"album_id": aid, "book_type": book_type}
        payload = signed_post(
            session, ident, PATH_ALBUM, body, cookies,
            retries=retries, quiet=True,
        )
        if not payload:
            continue
        try:
            ad = parse_album(payload)
        except Exception:  # noqa: BLE001
            continue
        out[aid] = {
            "series_id": aid,
            "title": ad.title,
            "intro": ad.intro,
            "cover": ad.cover,
            "episode_cnt": ad.episode_cnt or len(ad.episodes),
            "follow_cnt": ad.follow_cnt,
            "entrance_text": ad.entrance_text,
            "is_album": True,
            "episodes": [
                {
                    "vid": "",  # ★ 合集的分集要再查 preload 才拿得到 vid
                    "series_id": ep.series_id,
                    "index": n,
                    "title": ep.title,
                    "duration": ep.duration,
                    "content_type": ep.content_type,
                }
                for n, ep in enumerate(ad.episodes, 1)
            ],
        }
        if progress:
            progress(i, total, ad.title, len(ad.episodes))
        if i < total:
            time.sleep(pause)
    return out
