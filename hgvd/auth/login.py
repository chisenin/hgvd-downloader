"""网页版扫码登录。

★ 走 ``hongguoduanju.com`` 的 passport 接口拿**明文** Cookie
（``sessionid`` / ``sessionid_ss``），不碰 App 内部存储、不解密
Chromium cookie 值。

流程（来自 hongguo-tv / hongguoTV 两个项目的独立实现，已实测）：

    1. GET /passport/web/get_qrcode/      → token + base64 PNG 二维码
    2. GET /passport/web/check_qrconnect/  → 轮询扫码状态
         status: 0/"" 待扫 · 1 已扫待确认 · 2/3 已确认 · 4 已取消 · 5 已过期
    3. GET data.redirect_url（同域，最多 5 跳）→ CookieJar 里出现 sessionid
    4. GET /passport/account/info/v2/      → data.user_id / data.name

★ 关键判定：``sessionid`` 或 ``sessionid_ss`` **任一非空**即登录成功。
"""

from __future__ import annotations

import base64
import json
import os
import time
from dataclasses import dataclass, asdict

import requests

from .. import net
from ..config import creds_path

BASE = "https://hongguoduanju.com"

# passport 公共参数
_COMMON = {
    "aid": "513755",
    "passport_jssdk_version": "3.4.10",
    "passport_jssdk_type": "normal",
    "account_sdk_source": "web",
    "is_from_ttaccountsdk": "1",
    "language": "zh",
    "next": BASE + "/",
}

PATH_QRCODE = "/passport/web/get_qrcode/"
PATH_CHECK = "/passport/web/check_qrconnect/"
PATH_INFO = "/passport/account/info/v2/"

# 决定性登录凭证
SESSION_KEYS = ("sessionid", "sessionid_ss")


@dataclass
class Account:
    """登录态。"""

    cookies: dict = None
    user_id: str = ""
    user_name: str = ""
    saved_at: float = 0.0

    def __post_init__(self):
        if self.cookies is None:
            self.cookies = {}

    @property
    def ok(self) -> bool:
        return any(self.cookies.get(k) for k in SESSION_KEYS)

    def cookie_header(self) -> str:
        return "; ".join(f"{k}={v}" for k, v in self.cookies.items() if v)

    def to_dict(self) -> dict:
        d = asdict(self)
        d["cookies"] = dict(self.cookies)
        return d

    @classmethod
    def from_dict(cls, d: dict) -> "Account":
        return cls(
            cookies=dict(d.get("cookies") or {}),
            user_id=str(d.get("user_id") or ""),
            user_name=str(d.get("user_name") or ""),
            saved_at=float(d.get("saved_at") or 0),
        )


# ---------------------------------------------------------------- 存取


def load_account(path=None) -> Account:
    """读已保存的登录态。没有或损坏就返回空 Account。"""
    p = path or creds_path()
    if not p.exists():
        return Account()
    try:
        return Account.from_dict(json.loads(p.read_text(encoding="utf-8")))
    except Exception:  # noqa: BLE001
        return Account()


def save_account(account: Account, path=None) -> bool:
    """保存登录态（原子写入）。

    ★ 凭据文件权限收到 0600（Unix）/ 尽力而为（Windows）。
    """
    p = path or creds_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    account.saved_at = time.time()
    tmp = p.with_suffix(p.suffix + ".tmp")
    tmp.write_text(
        json.dumps(account.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
    )
    tmp.replace(p)
    try:
        os.chmod(p, 0o600)
    except OSError:
        pass
    return True


def clear_account(path=None) -> None:
    p = path or creds_path()
    if p.exists():
        p.unlink()


# ---------------------------------------------------------------- 扫码


def make_login_session() -> requests.Session:
    """登录专用 session：带 CookieJar，不受系统代理影响。"""
    s = requests.Session()
    s.trust_env = False
    s.headers.update(
        {
            "User-Agent": net.APP_UA,
            "Referer": BASE + "/",
            "Origin": BASE,
            "Accept": "application/json, text/javascript, */*; q=0.01",
            "Accept-Language": "zh-CN,zh;q=0.9",
        }
    )
    return s


def _csrf(session) -> str:
    """取 ``passport_csrf_token``。

    ★ 每个请求都要把它同时作为 ``x-tt-passport-csrf-token`` 头发出去。
    """
    for c in session.cookies:
        if c.name == "passport_csrf_token":
            return c.value
    return ""


def _get(session, path: str, params: dict | None = None, timeout: int = 20) -> dict:
    p = dict(_COMMON)
    if params:
        p.update(params)
    csrf = _csrf(session)
    headers = {}
    if csrf:
        headers["x-tt-passport-csrf-token"] = csrf
    r = session.get(BASE + path, params=p, headers=headers, timeout=timeout)
    if r.status_code != 200:
        raise RuntimeError(f"{path} HTTP {r.status_code}")
    try:
        return r.json()
    except Exception as e:  # noqa: BLE001
        raise RuntimeError(f"{path} 返回非 JSON：{r.text[:150]}") from e


def fetch_qrcode(session) -> dict:
    """取二维码。返回 ``{token, qrcode_b64, expire_time}``。"""
    data = _get(session, PATH_QRCODE)
    if data.get("message") != "success":
        raise RuntimeError(f"取二维码失败：{data.get('message')} / {data.get('error_code')}")
    d = data.get("data") or {}
    qr = d.get("qrcode") or ""
    b64 = qr.split(",", 1)[1] if qr.startswith("data:") else qr
    return {
        "token": d.get("token") or "",
        "qrcode_b64": b64,
        "expire_time": int(d.get("expire_time") or 0),
    }


def save_qrcode_png(b64: str, path: str) -> str:
    """把 base64 二维码写成 PNG。"""
    data = base64.b64decode(b64)
    with open(path, "wb") as f:
        f.write(data)
    return path


def poll_qrconnect(session, token: str, interval: float = 1.8) -> dict:
    """轮询扫码状态，直到确认 / 取消 / 过期。

    返回服务端的状态 dict（含 ``status`` / ``redirect_url`` / ``message``）。

    ★ 服务端在二维码过期后会返回 **HTTP 502**（不是 JSON），
      所以这里把 5xx 当成「过期，需要重新取码」抛出去，
      由上层重新生成。
    """
    while True:
        try:
            data = _get(session, PATH_CHECK, {"token": token})
        except RuntimeError as e:
            if "HTTP 502" in str(e) or "HTTP 5" in str(e):
                raise RuntimeError("二维码已过期，请重新获取") from e
            raise
        d = data.get("data") or {}
        status = str(d.get("status") or "")
        if status in ("2", "3"):
            return d
        if status in ("4", "5"):
            raise RuntimeError(
                "扫码" + ("已取消" if status == "4" else "已过期，请重新获取二维码")
            )
        time.sleep(interval)


def follow_redirect(session, redirect_url: str, max_hops: int = 5) -> str:
    """跟随确认后的跳转，取到 sessionid。

    ★ 必须限定同域（``hongguoduanju.com``），防止被重定向到站外。
    """
    from urllib.parse import urlparse

    url = redirect_url
    for _ in range(max_hops):
        host = urlparse(url).netloc
        if not host.endswith("hongguoduanju.com"):
            raise RuntimeError(f"跳转到站外域名，拒绝：{host}")
        r = session.get(url, timeout=20, allow_redirects=False)
        if r.status_code in (301, 302, 303, 307, 308):
            loc = r.headers.get("Location")
            if not loc:
                break
            url = loc if loc.startswith("http") else BASE + loc
            continue
        break
    return url


def fetch_account_info(session) -> dict:
    """取账号信息（user_id / 昵称）。"""
    data = _get(session, PATH_INFO)
    d = data.get("data") or {}
    if d.get("error_code"):
        raise RuntimeError(
            f"账号信息获取失败：{d.get('description') or d.get('account_info_error')}"
        )
    return {
        "user_id": str(d.get("biz_user_id") or d.get("user_id") or ""),
        "user_name": str(d.get("name") or d.get("user_name") or ""),
    }


def login_by_qrcode(session=None, *, show=None) -> Account:
    """完整扫码登录流程。

    ``show`` 是可选回调，用来把二维码路径 / 状态提示给上层（GUI 用）。
    """
    session = session or make_login_session()
    qr = fetch_qrcode(session)
    if not qr["token"]:
        raise RuntimeError("服务端没有返回二维码 token")

    if show:
        show(qr)

    d = poll_qrconnect(session, qr["token"])

    redirect = d.get("redirect_url") or ""
    if redirect:
        follow_redirect(session, redirect)

    acc = Account(cookies={c.name: c.value for c in session.cookies})
    if not acc.ok:
        # 有些情况下 sessionid 在跳转后的 Set-Cookie 里，再查一次
        acc = Account(cookies={c.name: c.value for c in session.cookies})
    if not acc.ok:
        raise RuntimeError(
            "扫码确认未取得会话（sessionid 为空），请重试。"
            "服务端状态：" + str(d.get("message") or d.get("status"))
        )

    try:
        info = fetch_account_info(session)
        acc.user_id = info["user_id"]
        acc.user_name = info["user_name"]
    except Exception:
        # 账号信息拿不到不影响下载，收藏夹接口会自动按当前 cookie 判定
        pass
    return acc
