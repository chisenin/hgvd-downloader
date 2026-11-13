"""HTTP 客户端。

★ 业务请求必须**禁用系统代理**（``trust_env=False``）——
系统默认代理会把请求吞成 HTML 首页，而不是 JSON。
下载 CDN 走直连即可，不需要代理。
"""

from __future__ import annotations

import json
import time
from typing import Any

import requests

# 模拟 App 的 UA，让 CDN 不做奇怪的降级
APP_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
)

WEB_REFERER = "https://hongguoduanju.com/"


def make_session() -> requests.Session:
    """建一个不受系统代理影响的 session。

    ★ ``trust_env=False`` 同时禁用 ``HTTP_PROXY``/``HTTPS_PROXY`` 环境变量
    和 Windows 的系统代理设置。
    """
    s = requests.Session()
    s.trust_env = False
    s.headers.update(
        {
            "User-Agent": APP_UA,
            "Accept-Language": "zh-CN,zh;q=0.9",
        }
    )
    return s


def get_json(
    session: requests.Session,
    url: str,
    *,
    headers: dict | None = None,
    cookies: dict | None = None,
    timeout: int = 25,
    retries: int = 2,
) -> dict:
    """GET 并解析 JSON。失败重试。"""
    last = ""
    for attempt in range(retries + 1):
        try:
            r = session.get(
                url, headers=headers, cookies=cookies, timeout=timeout
            )
            if r.status_code != 200:
                last = f"HTTP {r.status_code}"
                raise RuntimeError(last)
            text = r.text.strip()
            if not text:
                last = "响应为空"
                raise RuntimeError(last)
            if text[0] not in "{[":
                # 被代理吞成 HTML
                last = f"返回非 JSON（可能被代理拦截），首字节 {text[0]!r}"
                raise RuntimeError(last)
            return json.loads(text)
        except Exception as e:  # noqa: BLE001
            last = str(e)[:200]
            if attempt < retries:
                time.sleep(0.8 * (attempt + 1))
    raise RuntimeError(f"GET {url} 失败：{last}")


def get_text(
    session: requests.Session,
    url: str,
    *,
    headers: dict | None = None,
    cookies: dict | None = None,
    timeout: int = 25,
    retries: int = 2,
) -> str:
    """GET 文本（用于网页版 SSR 抓取）。"""
    last = ""
    for attempt in range(retries + 1):
        try:
            r = session.get(
                url, headers=headers, cookies=cookies, timeout=timeout
            )
            if r.status_code != 200:
                last = f"HTTP {r.status_code}"
                raise RuntimeError(last)
            return r.text
        except Exception as e:  # noqa: BLE001
            last = str(e)[:200]
            if attempt < retries:
                time.sleep(0.8 * (attempt + 1))
    raise RuntimeError(f"GET {url} 失败：{last}")
