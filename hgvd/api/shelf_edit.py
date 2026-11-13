"""收藏夹的增删改（带签名）。

★ 协议（参考 open-source 实现 charlie901208/HGTV 与
  beilunyang/hongguoTV 的 ``AccountParser``）：

    POST /reading/bookapi/bookshelf/video/update/v1/
    body = {
      "is_cancelled": false,
      "shark_extra": {                          # 仅添加时需要
        "enter_from": "MainFragmentActivity",
        "previous_page": "", "page_list": "MainFragmentActivity",
        "is_active_behavior": "true", "inactive_type": "0"
      },
      "update_bookshelf_video_list": [{
        "book_id": "<series_id>",
        "book_type": 2,
        "modify_time": <毫秒时间戳>,
        "video_shelf_operate_type": 0           # 0 = 加入，1 = 移除
      }]
    }

★ ``video_shelf_operate_type``：``0`` 加入、``1`` 移除。
★ 移除时还要带 ``"group_name": ""``。
★ ``book_type`` 要用**该条目自己的** book_type（2=短剧 / 3=合集 / 4=单个视频），
  用错会删不掉。
"""

from __future__ import annotations

import json
import time

from ..util import j_int

PATH_UPDATE = "/reading/bookapi/bookshelf/video/update/v1/"
PATH_UPDATE_LEGACY = "/reading/bookapi/bookshelf/video/update/v"


def build_update_body(
    series_id: str,
    book_type: int,
    remove: bool = True,
    now_ms: int | None = None,
) -> dict:
    """构造收藏夹更新请求体。"""
    entry: dict = {
        "book_id": series_id,
        "book_type": book_type,
        "modify_time": now_ms if now_ms is not None else int(time.time() * 1000),
        # 0 = 加入，1 = 移除
        "video_shelf_operate_type": 1 if remove else 0,
    }
    if remove:
        entry["group_name"] = ""

    body: dict = {
        "is_cancelled": False,
        "update_bookshelf_video_list": [entry],
    }
    if not remove:
        body["shark_extra"] = {
            "enter_from": "MainFragmentActivity",
            "previous_page": "",
            "page_list": "MainFragmentActivity",
            "is_active_behavior": "true",
            "inactive_type": "0",
        }
    return body


def update_shelf(
    signed_post,
    series_id: str,
    book_type: int,
    remove: bool = True,
) -> dict:
    """加入 / 移除收藏。

    ``signed_post(path, body)`` 需返回带 ``code`` 的 dict。
    """
    body = build_update_body(series_id, book_type, remove)
    raw = signed_post(PATH_UPDATE, body)
    if raw is None:
        raise RuntimeError("收藏夹更新无响应")
    code = j_int(raw, "code")
    if code != 0:
        raise RuntimeError(
            f"收藏夹更新失败：code={code} "
            f"{raw.get('debug_info') or raw.get('message')}"
        )
    return raw


def remove_many(
    signed_post,
    items: list[tuple[str, int]],
    pause: float = 0.8,
    progress=None,
) -> tuple[int, list[str]]:
    """批量移除收藏。

    ``items`` 是 ``[(series_id, book_type), ...]``。
    返回 ``(成功数, 失败原因列表)``。
    """
    ok = 0
    errors: list[str] = []
    for i, (sid, btype) in enumerate(items, 1):
        try:
            update_shelf(signed_post, sid, btype, remove=True)
            ok += 1
            if progress:
                progress(i, len(items), sid, True)
        except Exception as e:  # noqa: BLE001
            errors.append(f"{sid}: {e}")
            if progress:
                progress(i, len(items), sid, False)
        time.sleep(pause)
    return ok, errors
