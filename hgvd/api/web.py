"""网页版（hongguoduanju.com）免签名接口。

★ 核心发现（2026-11-13 实测）：红果的网页版 SSR 把整份数据内嵌在
``_ROUTER_DATA`` 里，**完全不需要 App 端签名链**：

    详情页  /detail?series_id=<id>   → loaderData.detail_page.seriesDetail
                                      → vid_list（全集 ID 数组）
    播放页  /player/<sid>/<vid>      → loaderData["player_(series_id)/(vid)/page"]
                                      → publicVideoSeo（时长/分辨率）
    直链    /video/public/<vid>/<q>.mp4  → 免签名直链，206 Partial Content

这让整个工具**不需要移植** x-gorgon / x-helios / x-medusa / x-argus
那套约 1700 行的签名实现。
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field

from .. import net
from ..util import j_int, j_str

BASE = "https://hongguoduanju.com"

_UA = net.APP_UA


# ---------------------------------------------------------------- SSR 解析

def extract_router_data(html: str) -> dict:
    """从 HTML 里抠出 ``_ROUTER_DATA`` 的 JSON。

    ★ 必须用 ``json.JSONDecoder().raw_decode`` 精确读取，
    不能用正则 ``\\{.*\\}`` 贪婪匹配 —— 后面还有别的脚本块，
    贪婪匹配会把 JSON 截断成一个非法片段（实测报
    ``Extra data: line 1 column 39440``）。
    """
    idx = html.find("_ROUTER_DATA")
    if idx < 0:
        raise RuntimeError("页面里没有 _ROUTER_DATA（结构可能已改版）")
    start = html.find("{", idx)
    if start < 0:
        raise RuntimeError("_ROUTER_DATA 后找不到 JSON 起点")
    try:
        data, _ = json.JSONDecoder().raw_decode(html[start:])
    except json.JSONDecodeError as e:
        raise RuntimeError(f"_ROUTER_DATA 解析失败：{e}") from e
    if not isinstance(data, dict):
        raise RuntimeError("_ROUTER_DATA 不是对象")
    return data


def _loader(data: dict, key: str) -> dict:
    ld = data.get("loaderData")
    if not isinstance(ld, dict):
        raise RuntimeError("缺少 loaderData")
    v = ld.get(key)
    if not isinstance(v, dict):
        raise RuntimeError(f"缺少 loaderData.{key}")
    return v


# ---------------------------------------------------------------- 数据模型


@dataclass
class Series:
    """一部剧（短剧）。"""

    series_id: str
    title: str = ""
    cover: str = ""
    intro: str = ""
    episode_cnt: int = 0
    accessible_cnt: int = 0
    tags: list[str] = field(default_factory=list)
    collect_time: int = 0
    source: str = ""       # 来源：shelf（收藏）/ web（网页版）

    @property
    def key(self) -> str:
        return self.series_id


@dataclass
class Episode:
    """一集。"""

    vid: str
    index: int = 0            # 序号（从 1 起）
    title: str = ""
    duration: float = 0.0     # 秒
    width: int = 0
    height: int = 0
    series_id: str = ""
    series_title: str = ""

    @property
    def key(self) -> str:
        return self.vid


# ---------------------------------------------------------------- 详情


def fetch_series_detail(
    session, series_id: str, cookies: dict | None = None
) -> tuple[Series, list[str]]:
    """抓详情页，返回（剧信息，全集 vid 列表）。

    ★ 详情页是 SSR 出来的，``vid_list`` 就是全集 ID 数组（实测一部剧 786 集）。
    """
    url = f"{BASE}/detail?series_id={series_id}"
    html = net.get_text(
        session,
        url,
        headers={"Accept": "text/html,application/xhtml+xml,*/*",
                 "Referer": BASE + "/"},
        cookies=cookies,
    )
    data = extract_router_data(html)
    page = _loader(data, "detail_page")
    sd = page.get("seriesDetail")
    if not isinstance(sd, dict):
        raise RuntimeError(f"剧 {series_id} 没有 seriesDetail")

    vid_list = [str(v) for v in (sd.get("vid_list") or []) if v]
    tags = []
    for t in (sd.get("tags") or []):
        if isinstance(t, dict):
            name = j_str(t, "tag_name") or j_str(t, "name") or j_str(t, "text")
            if name:
                tags.append(name)
        elif isinstance(t, str):
            tags.append(t)

    s = Series(
        series_id=j_str(sd, "series_id") or series_id,
        title=j_str(sd, "series_name"),
        cover=j_str(sd, "series_cover"),
        intro=j_str(sd, "series_intro"),
        episode_cnt=j_int(sd, "episode_cnt"),
        accessible_cnt=j_int(sd, "accessible_episode_cnt"),
        tags=tags,
        source="web",
    )
    return s, vid_list


def fetch_episodes(
    session,
    series_id: str,
    *,
    cookies: dict | None = None,
    max_count: int = 0,
) -> tuple[Series, list[Episode]]:
    """取一部剧的集列表（含每集时长）。

    ★ 时长要逐集抓播放页（每集一个请求），所以是「按需」而非全量预取：
    只在真正要下载时才去查，避免几百集的剧发几百个请求。
    """
    s, vids = fetch_series_detail(session, series_id, cookies=cookies)
    eps: list[Episode] = []
    for i, v in enumerate(vids):
        eps.append(
            Episode(
                vid=v,
                index=i + 1,
                title=f"第 {i + 1} 集",
                series_id=s.series_id,
                series_title=s.title,
            )
        )
        if max_count and len(eps) >= max_count:
            break
    return s, eps


def fetch_episode_meta(
    session,
    series_id: str,
    vid: str,
    *,
    cookies: dict | None = None,
) -> dict:
    """抓单集的播放元信息（时长 / 分辨率 / 是否可播）。"""
    url = f"{BASE}/player/{series_id}/{vid}"
    html = net.get_text(
        session,
        url,
        headers={"Accept": "text/html,application/xhtml+xml,*/*",
                 "Referer": BASE + "/"},
        cookies=cookies,
    )
    data = extract_router_data(html)
    ld = data.get("loaderData") or {}
    # 播放器页的 key 带路由参数，形如 player_(series_id)/(vid)/page
    page = None
    for k, v in ld.items():
        if isinstance(v, dict) and "publicVideoSeo" in v:
            page = v
            break
    if page is None:
        raise RuntimeError(f"播放页 {vid} 里没有 publicVideoSeo")
    seo = page.get("publicVideoSeo") or {}
    return {
        "available": bool(seo.get("available")),
        "series_id": j_str(seo, "seriesId"),
        "vid": j_str(seo, "itemId"),
        "poster": j_str(seo, "posterUrl"),
        "duration": float(seo.get("durationSeconds") or 0),
        "width": int(seo.get("width") or 0),
        "height": int(seo.get("height") or 0),
        "protected": bool(page.get("protectedPlayback")),
    }


# ---------------------------------------------------------------- 播放直链

_MP4_RE = re.compile(r"https://hongguoduanju\.com/video/public/[\w/\.\-]+")


def guess_video_url(vid: str, quality: str = "360p") -> str:
    """构造免签名直链。

    ★ 路径是 ``/video/public/<vid>/<quality>.mp4``，
    实测 360p 直链可直接 206 下载（``video/mp4`` + 合法 ftyp 头）。
    其它清晰度是否真实存在取决于服务端；拿不到时回落到 360p。
    """
    return f"{BASE}/video/public/{vid}/{quality}.mp4"


def probe_video(
    session,
    vid: str,
    quality: str = "360p",
    *,
    cookies: dict | None = None,
) -> tuple[str, int | None, str | None]:
    """探测可用直链。

    返回（url, content_length, content_type）。找不到就返回空。
    """
    for q in _quality_candidates(quality):
        url = guess_video_url(vid, q)
        try:
            r = session.get(
                url,
                headers={"Referer": BASE + "/", "Accept": "*/*",
                         "Range": "bytes=0-1"},
                cookies=cookies,
                timeout=20,
                stream=True,
            )
            ct = r.headers.get("Content-Type")
            cr = r.headers.get("Content-Range")
            r.close()
            if r.status_code in (200, 206) and (ct or "").startswith("video/"):
                length = None
                if cr and "/" in cr:
                    try:
                        length = int(cr.rsplit("/", 1)[1])
                    except ValueError:
                        length = None
                if length is None:
                    try:
                        length = int(r.headers.get("Content-Length") or 0) or None
                    except ValueError:
                        length = None
                return url, length, ct
        except Exception:  # noqa: BLE001
            continue
    return "", None, None


def _quality_candidates(quality: str) -> list[str]:
    """清晰度候选，从请求的往下降级。"""
    order = {
        "1080p": ["1080p", "720p", "360p"],
        "720p": ["720p", "360p"],
        "360p": ["360p"],
    }
    return order.get((quality or "360p").lower(), ["360p"])


def extract_video_urls(html: str) -> list[str]:
    """从播放页 HTML 里抽出所有 mp4 直链（兜底手段）。"""
    return sorted(set(_MP4_RE.findall(html)))
