"""等待扫码确认并保存登录态。

用法::

    python tools/wait_login.py <token> <cookie_json>

把二维码确认后拿到的 sessionid 落盘。
"""

from __future__ import annotations

import json
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from hgvd.auth.login import (
    fetch_account_info,
    make_login_session,
    poll_qrconnect,
    save_account,
    Account,
)
from hgvd.auth.login import follow_redirect


def main() -> int:
    token = sys.argv[1]
    cookie_file = sys.argv[2] if len(sys.argv) > 2 else None

    sess = make_login_session()
    if cookie_file and (import_path := __import__("pathlib").Path(cookie_file)).exists():
        data = json.loads(import_path.read_text(encoding="utf-8"))
        for k, v in (data.get("cookies") or {}).items():
            sess.cookies.set(k, v, domain="hongguoduanju.com", path="/")

    print("等待扫码…", flush=True)
    try:
        d = poll_qrconnect(sess, token)
    except Exception as e:  # noqa: BLE001
        print(f"扫码失败：{e}")
        return 1

    print("已确认，正在取会话…", flush=True)
    redirect = d.get("redirect_url") or ""
    if redirect:
        follow_redirect(sess, redirect)

    acc = Account(cookies={c.name: c.value for c in sess.cookies})
    if not acc.ok:
        print("未取得 sessionid。cookie:", sorted(acc.cookies.keys()))
        return 1
    try:
        info = fetch_account_info(sess)
        acc.user_id = info["user_id"]
        acc.user_name = info["user_name"]
    except Exception as e:  # noqa: BLE001
        print("账号信息获取失败（不影响下载）:", e)

    save_account(acc)
    print(f"登录成功：{acc.user_name} uid={acc.user_id}")
    print("cookie 字段:", sorted(acc.cookies.keys()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
