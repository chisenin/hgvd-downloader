"""合集（album）详情接口 —— ★ 拿到短剧/合集的完整分集列表。

★ 决定性发现（2026-11-13）：

    红果的收藏夹里 224 项 ``book_type=3`` 的内容，服务端用
    ``multi_video_detail/preload/v1`` 查时一律返回 ``episode_cnt=0``、
    ``video_list`` 为空 —— 看起来像「没有视频」。

    但它们其实是一个**「合集」**（album）。用正确的端点就能拿到全部��集：

        POST /novel/player/album_detail/v1/
        body = {"album_id": "<series_id>", "book_type": 3}

    → ``data.album_data``：
        - ``title``                合集名（如「颠覆你的动植物科普」）
        - ``episode_cnt``          集数（实测 16）
        - ``episode_entrance_text`` ["合集", "<名字>", "更新至16集"]
        - ``video_detail_list[]``  ★ 每项是一个**独立单集**，含
          ``series_id_str`` / ``series_title`` / ``duration`` / ``cover``

    ★ 参数名必须是 ``album_id``：传 ``series_id`` / ``book_id`` 会返回
      ``code=100001 series_id invalid, param:``。

    ★ ``video_detail_list[]`` 里的每集又有自己的 ``series_id``，
      拿它去调 ``preload/v1`` 能拿到该集的 ``video_list``（含 vid 与时长），
      再用 ``video_model`` 拿播放地址。
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field

from ..util import j_int, j_str


@dataclass
class AlbumEpisode:
    """合集里的一集。"""

    series_id: str
    title: str = ""
    duration: float = 0.0
    cover: str = ""
    content_type: int = 0
    has_played: bool = False
    raw: dict = field(default_factory=dict)


@dataclass
class AlbumDetail:
    """一个合集（短剧 / 教学合集）的详情。"""

    album_id: str
    title: str = ""
    intro: str = ""
    cover: str = ""
    episode_cnt: int = 0
    follow_cnt: int = 0
    entrance_text: list[str] = field(default_factory=list)
    episodes: list[AlbumEpisode] = field(default_factory=list)
    raw: dict = field(default_factory=dict)


def parse_album(raw: dict) -> AlbumDetail:
    """解析 ``album_detail`` 响应。"""
    data = raw.get("data") if isinstance(raw, dict) else None
    ad = (data or {}).get("album_data") if isinstance(data, dict) else None
    if not isinstance(ad, dict):
        raise RuntimeError("album_detail 响应缺少 album_data")

    eps: list[AlbumEpisode] = []
    for item in ad.get("video_detail_list") or []:
        if not isinstance(item, dict):
            continue
        sid = j_str(item, "series_id_str") or j_str(item, "series_id")
        if not sid:
            continue
        eps.append(
            AlbumEpisode(
                series_id=sid,
                title=j_str(item, "series_title"),
                duration=float(j_int(item, "duration")),
                cover=j_str(item, "series_cover"),
                content_type=j_int(item, "content_type"),
                has_played=bool(item.get("has_played")),
                raw=item,
            )
        )

    return AlbumDetail(
        album_id=j_str(ad, "album_id_str") or j_str(ad, "album_id"),
        title=j_str(ad, "title"),
        intro=j_str(ad, "intro"),
        cover=j_str(ad, "cover"),
        episode_cnt=j_int(ad, "episode_cnt"),
        follow_cnt=j_int(ad, "followed_cnt"),
        entrance_text=[str(x) for x in (ad.get("episode_entrance_text") or [])],
        episodes=eps,
        raw=ad,
    )


def fetch_album(
    signed_post,
    album_id: str,
    book_type: int = 3,
) -> AlbumDetail:
    """取合集详情。

    ``signed_post(path, body)`` 需返回 ``{"code":0,"data":{...}}`` 形态的 dict。
    """
    body = {"album_id": album_id, "book_type": book_type}
    raw = signed_post("/novel/player/album_detail/v1/", body)
    if not isinstance(raw, dict):
        raise RuntimeError("album_detail 无响应（可能被限流）")
    if raw.get("code") != 0:
        raise RuntimeError(
            f"album_detail 失败：code={raw.get('code')} "
            f"{raw.get('debug_info') or raw.get('message')}"
        )
    return parse_album(raw)
