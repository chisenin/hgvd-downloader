"""从 MuMu 模拟器里读红果 App 的登录态。

★ **这是本项目解决「扫码太慢」的根本办法**：
  网页版二维码的**有效期只有约 58 秒**（实测 5 次都是 58~59 秒），
  在「打开抖音 → 找扫一扫 → 对准 → 识别 → 确认」的动作链下几乎必失败；
  而手机号登录又会触发滑块验证（实测 ``code=1105`` +
  ``verify_center_decision_conf``）。

★ 既然用户本来就在 App 里收藏剧，**App 一定是登录状态**，
  直接把 App 的登录态读出来最稳 —— 这也是上一项目（兴趣岛下载器）
  用过的成熟思路。

★ 读取链路：
    MuMuManager.exe sh -v <实例> -c "<命令>"     ← 模拟器内部 RPC（不是 adbd）
      → /data/data/com.phoenix.read/shared_prefs/cookieStore.xml
        （Java 序列化的 Cookie，**明文**存在十六进制串里）
      → /data/data/com.phoenix.read/shared_prefs/token_shared_preference.xml
        （``X-Tt-Token``，明文）
      → /data/data/com.phoenix.read/shared_prefs/applog_stats.xml
        （``device_id``，明文）

★ 关键：``device_id`` / ``iid`` / ``install_id`` 必须来自**同一台设备**，
  否则签名与 cookie 对不上，服务端会返回空 body。
"""

from __future__ import annotations

import binascii
import html
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

from .login import Account

# ---------------------------------------------------------------- 定位

MUMU_PATHS = [
    r"D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe",
    r"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe",
    r"D:\Program Files\Netease\MuMu\nx_device\MuMuManager.exe",
]

PACKAGE = "com.phoenix.read"
PREFS = "/data/data/com.phoenix.read/shared_prefs"


class MumuError(Exception):
    """MuMu 相关错误。"""


def find_mumu() -> str:
    """定位 MuMuManager.exe。"""
    for p in MUMU_PATHS:
        if os.path.exists(p):
            return p
    found = shutil.which("MuMuManager.exe") or shutil.which("MuMuManager")
    if found:
        return found
    raise MumuError(
        "找不到 MuMuManager.exe。请确认已安装网易 MuMu 模拟器，"
        "或用其它方式登录（python -m hgvd login）。"
    )


def _run(mumu: str, args: list[str], timeout: int = 30) -> str:
    """调 MuMuManager 并返回 stdout。

    ★ 用 ``creationflags=CREATE_NO_WINDOW`` 避免弹黑框。
    """
    try:
        r = subprocess.run(
            [mumu] + args,
            capture_output=True,
            timeout=timeout,
            creationflags=0x08000000 if os.name == "nt" else 0,
        )
    except subprocess.TimeoutExpired as e:
        raise MumuError(f"MuMuManager 超时：{' '.join(args[:3])}") from e
    return (r.stdout or b"").decode("utf-8", "replace")


def list_instances(mumu: str | None = None) -> list[dict]:
    """列出所有 MuMu 实例。"""
    mumu = mumu or find_mumu()
    out = []
    for idx in range(0, 8):
        txt = _run(mumu, ["info", "-v", str(idx)], timeout=20)
        if '"index"' not in txt:
            continue
        try:
            info = json.loads(txt[txt.index("{"):txt.rindex("}") + 1])
        except Exception:  # noqa: BLE001
            continue
        info["started"] = bool(info.get("is_android_started"))
        out.append(info)
        if not info.get("is_process_started") and idx > 1:
            # 多开器通常最多 3 个实例，后面不会再有
            break
    return out


def sh(mumu: str, index: int, command: str, timeout: int = 30) -> str:
    """在指定实例里执行 shell 命令（内部 RPC）。"""
    txt = _run(mumu, ["sh", "-v", str(index), "-c", command], timeout=timeout)
    # 设备没启动时，RPC 会返回 {"errcode":-201,...}（不是空输出）
    if '"errcode"' in txt and '"vm not running"' in txt:
        raise MumuError("该 MuMu 实例未启动")
    return txt


# ---------------------------------------------------------------- 解析


def _extract_xml(text: str) -> str:
    """XML 实体解码（ prefs 里 &quot; &amp; 之类）。"""
    return html.unescape(text)


def _java_cookie_value(xml: str, name: str) -> str:
    """从 Java 序列化的 CookieStore 里取某个 cookie 的明文值。

    ★ CookieStore 的结构是
      ``<string name="http://fqnovel.com/|sessionid">ACED0005…</string>``
      十六进制串里能直接看到明文（值本身不是加密的，只是被 Java 序列化包了一层）。
    """
    m = re.search(
        r'\|' + re.escape(name) + r'">(.*?)</string>', xml, re.S
    )
    if not m:
        return ""
    try:
        raw = binascii.unhexlify(m.group(1).replace(" ", ""))
    except (binascii.Error, ValueError):
        return ""
    # 明文值是连续的十六进制/数字串（长度 >= 8）
    vals = re.findall(rb"[0-9a-f]{8,}", raw)
    return vals[0].decode("latin-1") if vals else ""


def _xml_text_value(xml: str, key: str) -> str:
    """从 prefs XML 里取 ``<string name="KEY">value</string>``。"""
    m = re.search(r'name="' + re.escape(key) + r'">([^<]*)<', xml)
    return m.group(1) if m else ""


# ---------------------------------------------------------------- 主流程


def read_app_state(index: int, mumu: str | None = None) -> dict:
    """读一个实例里的红果登录态。

    返回 ``{sessionid, sid_tt, install_id, device_id, odin_tt, x_tt_token, csrf}``。
    """
    mumu = mumu or find_mumu()
    store = sh(mumu, index, f"cat {PREFS}/cookieStore.xml")
    if not store.strip():
        raise MumuError(f"实例 {index} 里读不到 cookieStore.xml")
    store = _extract_xml(store)

    token_xml = _extract_xml(sh(mumu, index, f"cat {PREFS}/token_shared_preference.xml"))
    stats_xml = _extract_xml(sh(mumu, index, f"cat {PREFS}/applog_stats.xml"))

    sid = _java_cookie_value(store, "sessionid")
    if not sid:
        sid = _java_cookie_value(store, "sid_tt")
    return {
        "sessionid": sid,
        "sid_tt": _java_cookie_value(store, "sid_tt") or sid,
        "install_id": _java_cookie_value(store, "install_id"),
        "device_id": _xml_text_value(stats_xml, "device_id"),
        "odin_tt": _java_cookie_value(store, "odin_tt"),
        "x_tt_token": _xml_text_value(token_xml, "X-Tt-Token"),
        "csrf": _java_cookie_value(store, "passport_csrf_token")
        or _xml_text_value(
            _extract_xml(sh(mumu, index, f"cat {PREFS}/CsrfTokenManager_sp.xml")),
            "csrf_token",
        ),
    }


def find_usable_instance(mumu: str | None = None) -> tuple[int, dict]:
    """找一个能用（有登录态）的实例。

    ★ 优先选**最近活动过**的实例 —— 用户刚在 App 里收藏完剧的那个。
      判断依据是 App 的**数据库文件 mtime**。

    ★★ 必须用 ``ls -lt``（按 mtime 排），**不能用 ``ls -t``** ——
      后者按**文件名**排序，数据库文件名里没有时间信息，
      结果会随机挑一个实例（实测挑到了很久没启动过的实例 0）。
    """
    mumu = mumu or find_mumu()
    instances = [i for i in list_instances(mumu) if i["started"]]
    if not instances:
        raise MumuError("没有正在运行的 MuMu 实例，请先启动模拟器并打开红果 App")

    best = None
    for info in instances:
        idx = int(info.get("index", 0))
        stamp = ""
        for path in (f"{PACKAGE}/databases/", f"{PACKAGE}/files/disk_collect/",
                     f"{PACKAGE}/shared_prefs/"):
            try:
                out = sh(
                    mumu, idx,
                    f"ls -lt {path} 2>/dev/null | awk 'NR==2{{print $6, $7}}'",
                    timeout=20,
                ).strip()
            except MumuError:
                continue
            if out:
                stamp = out
                break
        if not best or (stamp and stamp > best[1]):
            best = (idx, stamp)

    order = [best[0]] if best else []
    order += [i["index"] for i in instances if i["index"] not in order]

    errors = []
    for idx in order:
        try:
            state = read_app_state(idx, mumu)
        except MumuError as e:
            errors.append(f"实例 {idx}: {e}")
            continue
        if state["sessionid"]:
            return idx, state
        errors.append(f"实例 {idx}: 没有 sessionid（App 可能未登录）")
    raise MumuError("找不到已登录的红果 App。\n" + "\n".join(errors))


def login_from_mumu(save: bool = True) -> Account:
    """从 MuMu 里的红果 App 读登录态并（可选）保存。

    ★ 这比扫码快得多，也不用过滑块。
    """
    idx, state = find_usable_instance()
    cookies = {
        "sessionid": state["sessionid"],
        "sid_tt": state["sid_tt"] or state["sessionid"],
        "install_id": state["install_id"],
        "odin_tt": state["odin_tt"],
    }
    cookies = {k: v for k, v in cookies.items() if v}
    if state.get("csrf"):
        cookies["passport_csrf_token"] = state["csrf"]

    acc = Account(cookies=cookies)
    acc.user_id = ""
    acc.user_name = ""
    if not acc.ok:
        raise MumuError(f"实例 {idx} 的 App 没有登录态")

    # 顺带把设备身份记下来（签名要用）
    try:
        from ..device import save_device
        from ..sign.core import DeviceIdentity

        save_device(
            DeviceIdentity(
                device_id=state.get("device_id") or "",
                iid=state.get("install_id") or "",
                cdid="",
                openudid="",
            )
        )
    except Exception:  # noqa: BLE001
        pass

    if save:
        from .login import save_account

        save_account(acc)
    return acc


def main(argv=None):
    import argparse

    p = argparse.ArgumentParser(description="从 MuMu 里的红果 App 读取登录态")
    p.add_argument("--index", type=int, default=-1, help="指定实例号；默认自动选最近活动的")
    p.add_argument("--list", action="store_true", help="列出所有实例")
    args = p.parse_args(argv)

    mumu = find_mumu()
    if args.list:
        for i in list_instances(mumu):
            print(f"  实例 {i['index']}: 已启动={i['started']}  {i.get('name','')}")
        return 0

    try:
        if args.index >= 0:
            state = read_app_state(args.index, mumu)
            if not state["sessionid"]:
                print(f"实例 {args.index} 的 App 未登录")
                return 1
            from .login import Account, save_account

            acc = Account(cookies={k: v for k, v in state.items()
                                   if k in ("sessionid", "sid_tt", "install_id", "odin_tt") and v})
            save_account(acc)
            print(f"已从实例 {args.index} 读取登录态")
        else:
            acc = login_from_mumu()
            print(f"已读取登录态（实例 {find_usable_instance()[0]}）")
        print("cookie 字段:", sorted(acc.cookies.keys()))
        return 0
    except MumuError as e:
        print(f"失败：{e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
