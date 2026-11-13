"""手机号 + 短信验证码登录。

★ 为什么必须有这个：网页版二维码的**有效期只有约 58 秒**（实测 5 次都是
  58~59 秒）。在「打开抖音 → 找扫一扫 → 对准 → 识别 → 确认」这套动作下，
  一分钟非常紧张，**扫不出来是常态**。短信验证码没有这个时间压力。

协议（来自 ``hongguoTV`` 的 ``WebLogin.kt``，与 Go 版互为独立验证）：

    1. GET  /passport/web/get_qrcode/       ← 只为拿 passport_csrf_token
    2. POST /passport/web/send_code/        ← 发短信
         mobile = mix("+86 <手机号>")  type = mix("24")
         mix_mode = 1   fixed_mix_mode = 1
    3. POST /passport/web/sms_login/        ← 校验验证码
         mobile = mix("+86 <手机号>")  code = mix("<验证码>")
         login_only = true   mix_mode = 1   fixed_mix_mode = 1

★ ``mix`` 是每字节 XOR 5 后转两位小写十六进制。
  （Kotlin 版用 ``toString(16)`` 不补零，Go 版用 ``hex.EncodeToString`` 补零；
  但手机号/验证码字符 XOR 5 后都落在 0x25~0x3C，两种写法结果**完全相同**。）
"""

from __future__ import annotations

import json
import re
import time

from .login import (
    BASE,
    Account,
    _COMMON,
    _csrf,
    fetch_account_info,
    make_login_session,
)

PATH_SEND_CODE = "/passport/web/send_code/"
PATH_SMS_LOGIN = "/passport/web/sms_login/"

_PHONE_RE = re.compile(r"^\+?\d[\d\s\-]{5,19}$")


class PhoneLoginError(Exception):
    """手机号登录失败。"""

    def __init__(self, message: str, code: int = 0):
        super().__init__(message)
        self.code = code


def mix(value: str) -> str:
    """每字节 XOR 5 → 两位小写十六进制。

    ★ 补零是安全的：手机号与验证码的字符 XOR 5 后都 ≥ 0x25，
      本来就是两位。
    """
    return "".join(f"{(b ^ 5) & 0xFF:02x}" for b in value.encode("utf-8"))


def normalize_phone(phone: str) -> str:
    """把用户输入的号码规整成 ``+86 <号码>``。

    ★ 服务端要求这个格式（含空格）。
    """
    p = (phone or "").strip()
    if not _PHONE_RE.match(p):
        raise PhoneLoginError(f"手机号格式不对：{p!r}")
    digits = re.sub(r"[^\d]", "", p)
    if digits.startswith("86") and len(digits) > 11:
        digits = digits[2:]
    if len(digits) != 11:
        raise PhoneLoginError(f"请输入 11 位手机号（当前 {len(digits)} 位）")
    return f"+86 {digits}"


def _post(session, path: str, form: dict, timeout: int = 20) -> dict:
    """POST 表单（公共参数进 query，表单进 body）。"""
    params = dict(_COMMON)
    csrf = _csrf(session)
    headers = {
        "User-Agent": (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        ),
        "Referer": BASE + "/",
        "Origin": BASE,
        "Accept": "application/json, text/javascript",
    }
    if csrf:
        headers["x-tt-passport-csrf-token"] = csrf
    r = session.post(BASE + path, params=params, data=form, headers=headers,
                     timeout=timeout)
    if r.status_code != 200:
        raise PhoneLoginError(f"{path} HTTP {r.status_code}")
    try:
        return r.json()
    except Exception as e:  # noqa: BLE001
        raise PhoneLoginError(f"{path} 返回非 JSON：{r.text[:150]}") from e


def _checked(reply: dict) -> dict:
    """检查 passport 返回，失败时给出可读原因。"""
    data = reply.get("data")
    if not isinstance(data, dict):
        raise PhoneLoginError("登录接口返回格式异常")
    if reply.get("message") != "success":
        code = data.get("error_code", -1)
        if data.get("verify_center_decision_conf"):
            raise PhoneLoginError(
                "红果要求完成滑块验证 —— 这种情况请改用扫码登录", code
            )
        msg = (
            data.get("description")
            or data.get("display_error")
            or reply.get("message")
            or "登录失败"
        )
        raise PhoneLoginError(f"{msg}（错误码 {code}）", code)
    return data


def prepare(session=None):
    """准备会话：先取一次二维码，拿到 ``passport_csrf_token``。

    ★ 与参考实现一致 —— 手机号登录也需要 csrf token，
      而它的获取入口就是 ``get_qrcode``。
    """
    session = session or make_login_session()
    from .login import fetch_qrcode

    fetch_qrcode(session)   # 副作用：写入 passport_csrf_token cookie
    return session


def send_code(session, phone: str) -> dict:
    """发短信验证码。

    返回 ``{mobile, retry_time}``。
    """
    mobile = normalize_phone(phone)
    form = {
        "mobile": mix(mobile),
        "type": mix("24"),
        "mix_mode": "1",
        "fixed_mix_mode": "1",
    }
    reply = _post(session, PATH_SEND_CODE, form)
    data = _checked(reply)
    return {
        "mobile": data.get("mobile") or mobile,
        "retry_time": int(data.get("retry_time") or 60),
        "raw": data,
    }


def verify(session, phone: str, code: str) -> Account:
    """用验证码登录，返回带 cookie 的 Account。"""
    mobile = normalize_phone(phone)
    code = (code or "").strip()
    if not re.fullmatch(r"\d{4,8}", code):
        raise PhoneLoginError("验证码格式不对（4~8 位数字）")
    form = {
        "mobile": mix(mobile),
        "code": mix(code),
        "login_only": "true",
        "mix_mode": "1",
        "fixed_mix_mode": "1",
    }
    reply = _post(session, PATH_SMS_LOGIN, form)
    _checked(reply)

    acc = Account(cookies={c.name: c.value for c in session.cookies})
    if not acc.ok:
        raise PhoneLoginError("验证码校验通过，但没拿到 sessionid")
    try:
        info = fetch_account_info(session)
        acc.user_id = info["user_id"]
        acc.user_name = info["user_name"]
    except Exception:  # noqa: BLE001
        pass
    return acc


def login_by_phone(phone: str, code_provider=None) -> Account:
    """完整手机号登录。

    ``code_provider`` 是接收手机号、返回验证码的回调；
    不给就用 ``input()`` 交互式询问。
    """
    session = prepare()
    info = send_code(session, phone)
    print(f"验证码已发送到 {info['mobile']}"
          + (f"（{info['retry_time']} 秒后可重发）" if info["retry_time"] else ""))
    if code_provider is None:
        def code_provider(_phone):
            return input("请输入验证码：").strip()
    code = code_provider(info["mobile"])
    return verify(session, phone, code)
