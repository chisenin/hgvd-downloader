"""扫码登录助手（自动续期）。

★ 服务端给的二维码只有约 5 分钟有效期，过期后 ``check_qrconnect`` 返回
  HTTP 502。这个脚本会：
    1. 取二维码 → 存 PNG → 用系统默认程序打开
    2. 持续轮询
    3. 二维码过期 → **自动换一张新的并重新打开**，无需重跑命令
    4. 扫码确认 → 保存登录态 → 退出

用法::

    python tools/login_watch.py
"""

from __future__ import annotations

import json
import os
import pathlib
import subprocess
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from hgvd.auth.login import (  # noqa: E402
    Account,
    fetch_account_info,
    fetch_qrcode,
    follow_redirect,
    make_login_session,
    poll_qrconnect,
    save_account,
    save_qrcode_png,
)

QR_PATH = pathlib.Path("hongguo_qrcode.png").resolve()
STATE_PATH = pathlib.Path(".login_state.json")


def open_image(path: pathlib.Path) -> None:
    """用系统默认程序打开图片（用户可直接扫）。"""
    try:
        if sys.platform == "win32":
            os.startfile(str(path))  # noqa: S606
        elif sys.platform == "darwin":
            subprocess.run(["open", str(path)], check=False)
        else:
            subprocess.run(["xdg-open", str(path)], check=False)
    except Exception:  # noqa: BLE001
        pass


def main() -> int:
    sess = make_login_session()
    attempts = 0
    print("=== 红果视频下载器 · 扫码登录 ===")
    print("请用「抖音」App 扫描弹出的二维码（红果 App 亦可），并在手机上点确认。")
    print("二维码约 5 分钟有效，过期会自动换新并重新弹出。\n")

    while True:
        attempts += 1
        try:
            qr = fetch_qrcode(sess)
        except Exception as e:  # noqa: BLE001
            print(f"取二维码失败（{e}），5 秒后重试…")
            time.sleep(5)
            continue

        save_qrcode_png(qr["qrcode_b64"], str(QR_PATH))
        # 供外部程序读取当前 token
        STATE_PATH.write_text(
            json.dumps(
                {
                    "token": qr["token"],
                    "qrcode_path": str(QR_PATH),
                    "cookies": {c.name: c.value for c in sess.cookies},
                    "issued_at": time.time(),
                    "expire_time": qr["expire_time"],
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        print(f"[{attempts}] 二维码已更新：{QR_PATH}")
        open_image(QR_PATH)

        try:
            d = poll_qrconnect(sess, qr["token"])
        except Exception as e:  # noqa: BLE001
            msg = str(e)
            if "过期" in msg:
                print("二维码已过期，自动换新…")
                time.sleep(1)
                continue
            print(f"扫码失败：{msg}")
            return 1

        print("已确认，正在取会话…")
        redirect = d.get("redirect_url") or ""
        if redirect:
            try:
                follow_redirect(sess, redirect)
            except Exception as e:  # noqa: BLE001
                print(f"跳转失败（不影响继续）：{e}")

        acc = Account(cookies={c.name: c.value for c in sess.cookies})
        if not acc.ok:
            print("未取得 sessionid。cookie 字段：", sorted(acc.cookies.keys()))
            return 1

        try:
            info = fetch_account_info(sess)
            acc.user_id = info["user_id"]
            acc.user_name = info["user_name"]
        except Exception as e:  # noqa: BLE001
            print(f"账号信息获取失败（不影响下载）：{e}")

        save_account(acc)
        print(f"\n登录成功：{acc.user_name or '(未取到昵称)'}  uid={acc.user_id}")
        print("cookie 字段：", sorted(acc.cookies.keys()))
        print("\n现在可以执行：python -m hgvd shelf")
        return 0


if __name__ == "__main__":
    sys.exit(main())
