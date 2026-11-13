"""收藏夹（云端书架）接口。

★ 关键实测结论（2026-11-13）：红果的收藏夹接口**不需要 App 端签名链**。
用普通的设备参数 + Cookie 就能拿到 ``code:0 SUCCESS``，收藏为空只是因为
cookie 是假的。

    GET /reading/bookapi/bookshelf/video/list/v?target_user_id=<uid>
    → data.video_shelf_info[]  每项含 series_id / series_title / collect_time

★ 端点在 5 个 host 上都可用（实测全部 ``code:0``）：
    api5-normal-sinfonlinea.fqnovel.com
    api5-normal-lq.fqnovel.com
    api5-normal-sinfonlineb.fqnovel.com
    reading.snssdk.com
    fanqienovel.com
"""

from __future__ import annotations

import time

from .. import net
from ..util import j_int, j_str

# 优先用 sinfonlinea（openhongguo 记录的官方主 host），失败自动换
HOSTS = [
    "api5-normal-sinfonlinea.fqnovel.com",
    "api5-normal-lq.fqnovel.com",
    "reading.snssdk.com",
    "api5-normal-sinfonlineb.fqnovel.com",
    "fanqienovel.com",
]

PATH_SHELF_VIDEO = "/reading/bookapi/bookshelf/video/list/v"
PATH_SHELF_INFO = "/reading/bookapi/bookshelf/video/info/v1/"

AID = "8662"
APP_NAME = "novelread"
VERSION_CODE = "73532"
VERSION_NAME = "7.3.5.32"

# 匿名设备参数。★ 顺序不重要（不走签名），但值要和 App 一致，
# 否则服务端可能判定为异常设备。
_DEVICE_FIELDS = [
    ("iid", "0"),
    ("device_id", "0"),
    ("ac", "wifi"),
    ("channel", "update_64"),
    ("aid", AID),
    ("app_name", APP_NAME),
    ("cdid", "0"),
    ("version_code", VERSION_CODE),
    ("version_name", VERSION_NAME),
    ("device_platform", "android"),
    ("os", "android"),
    ("ssmix", "a"),
    ("device_type", "Xiaomi 14"),
    ("device_brand", "Redmi"),
    ("language", "zh"),
    ("os_api", "34"),
    ("os_version", "15"),
    ("manifest_version_code", VERSION_CODE),
    ("resolution", "1080*2400"),
    ("dpi", "480"),
    ("update_version_code", VERSION_CODE),
    ("host_abi", "arm64-v8a"),
    ("dragon_device_type", "phone"),
    ("pv_player", VERSION_CODE),
    ("compliance_status", "0"),
    ("need_personal_recommend", "1"),
    ("player_so_load", "1"),
    ("is_android_pad_screen", "0"),
    ("openudid", "0"),
]

# 冒充 App 的 UA
APP_UA = (
    "com.ss.android.ugc.aweme/90020 (Linux; U; Android 15; zh_CN; Redmi; Build; "
    "tt-ok/3.12.13.1-tiktok)"
)


def _base_params(extra: list[tuple[str, str]] | None = None) -> dict:
    p = dict(_DEVICE_FIELDS)
    if extra:
        p.update(dict(extra))
    ts = int(time.time())
    p["ts"] = str(ts)
    p["_rticket"] = str(ts * 1000)
    return p


def _headers(cookies: dict | None) -> dict:
    h = {
        "User-Agent": APP_UA,
        "Accept": "application/json",
        "X-SS-DP": AID,
        "X-XS-From-Web": "0",
    }
    if cookies:
        # 手动拼 Cookie 头，避免 requests 的 cookiejar 作用域问题
        h["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items() if v)
    return h


def list_shelf_raw(
    session,
    cookies: dict | None = None,
    target_user_id: str = "",
) -> dict:
    """调收藏夹接口，返回原始 JSON。"""
    extra = [("target_user_id", target_user_id)] if target_user_id else []
    params = _base_params(extra)
    headers = _headers(cookies)
    last = ""
    for host in HOSTS:
        url = f"https://{host}{PATH_SHELF_VIDEO}"
        try:
            r = session.get(url, params=params, headers=headers, timeout=20)
            if r.status_code != 200:
                last = f"{host}: HTTP {r.status_code}"
                continue
            data = r.json()
            code = data.get("code")
            if code == 0:
                return data
            last = f"{host}: code={code} message={data.get('message')}"
        except Exception as e:  # noqa: BLE001
            last = f"{host}: {str(e)[:120]}"
            continue
    raise RuntimeError(f"收藏夹接口全部 host 失败：{last}")


def list_shelf(
    session,
    cookies: dict | None = None,
    target_user_id: str = "",
) -> list[dict]:
    """取收藏夹条目列表（已归一化的 dict）。

    每项：``{series_id, title, cover, episode_cnt, collect_time}``
    """
    data = list_shelf_raw(session, cookies, target_user_id)
    payload = data.get("data")
    rows = []
    if isinstance(payload, dict):
        rows = payload.get("video_shelf_info") or []
    out = []
    for r in rows:
        if not isinstance(r, dict):
            continue
        vid = r.get("video_data") if isinstance(r.get("video_data"), dict) else {}
        sid = (
            j_str(r, "series_id")
            or j_str(vid, "series_id")
            or j_str(r, "book_id")
        )
        if not sid:
            continue
        out.append(
            {
                "series_id": sid,
                "title": (
                    j_str(r, "series_title")
                    or j_str(vid, "title")
                    or j_str(r, "book_name")
                    or sid
                ),
                "cover": j_str(r, "series_cover_url") or j_str(vid, "cover"),
                "episode_cnt": j_int(vid, "episode_cnt") or j_int(r, "episode_cnt"),
                "collect_time": j_int(r, "collect_time"),
                "raw": r,
            }
        )
    return out
