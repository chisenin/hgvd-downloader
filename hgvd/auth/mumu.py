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

import requests

from .login import Account, fetch_account_info, make_login_session

# ---------------------------------------------------------------- 定位

# ★★ MuMu 的安装目录**因机器而异**（版本名、盘符、用户改过的路径都不同）：
#   MuMu 6      → ``...\Netease\MuMu\nx_main\``
#   MuMu 12     → ``...\Netease\MuMuPlayer-12.0\nx_main\``（实测本机就是这种）
#   也有装在 ``Program Files\MuMu Player 12\shell\`` 或盘符根目录的情况。
#   旧代码只认第一个写死路径 → 本机「找不到 MuMuManager.exe」→ 拿不到授权。
MUMUMANAGER = "MuMuManager.exe"

# 相对「安装根目录」的候选位置
MUMU_RELATIVE = (
    os.path.join("nx_main", MUMUMANAGER),
    os.path.join("shell", MUMUMANAGER),
    os.path.join("nx_device", "shell", MUMUMANAGER),
    MUMUMANAGER,
)

# 显式覆盖：设了就优先用（装到非常规目录时最省事）
ENV_OVERRIDE = ("HGVD_MUMU_MANAGER", "HGV_MUMU_MANAGER", "MUMU_MANAGER")

PACKAGE = "com.phoenix.read"
DATA_ROOT = f"/data/data/{PACKAGE}"
PREFS = f"{DATA_ROOT}/shared_prefs"


class MumuError(Exception):
    """MuMu 相关错误。"""


def _cache_path() -> Path:
    from ..config import tool_dir

    return tool_dir() / "mumu_manager.txt"


def _remember(path: str) -> None:
    """把这次找到的路径记下来，下次直接命中（省掉全盘枚举）。"""
    try:
        p = _cache_path()
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(str(path), encoding="utf-8")
    except Exception:  # noqa: BLE001
        pass


def _from_cache() -> str:
    try:
        s = _cache_path().read_text(encoding="utf-8").strip()
    except Exception:  # noqa: BLE001
        return ""
    return s if s and os.path.isfile(s) else ""


def _clean_hint(raw: str) -> str:
    """把注册表里的路径/命令行清洗成纯路径。"""
    s = (raw or "").strip()
    if not s:
        return ""
    if s.startswith('"'):
        end = s.find('"', 1)
        if end > 0:
            s = s[1:end]
    if "," in s and not os.path.exists(s):
        # DisplayIcon 形如 ``D:\...\uninstall.exe,0``
        s = s.rsplit(",", 1)[0].strip().strip('"')
    if " -" in s and not os.path.exists(s):
        s = s.split(" -", 1)[0].strip().strip('"')
    return s


def _manager_in(root: str, levels: int = 3) -> str:
    """在给定目录（或它的上 N 层父目录）里找 MuMuManager.exe。

    ★ 注册表给的是安装根目录，进程给的是 ``nx_main/MuMuNxMain.exe``，
      两种都要能落到 ``nx_main/MuMuManager.exe``，所以统一「向上回溯 + 相对拼接」。
    """
    if not root:
        return ""
    try:
        cur = Path(root)
        if cur.is_file():
            if cur.name.lower() == MUMUMANAGER.lower():
                return str(cur)
            cur = cur.parent
    except Exception:  # noqa: BLE001
        return ""
    for _ in range(levels + 1):
        for rel in MUMU_RELATIVE:
            cand = cur / rel
            if cand.is_file():
                return str(cand)
        if cur.parent == cur:
            break
        cur = cur.parent
    return ""


def _decode_ps(raw: bytes) -> str:
    for enc in ("utf-8", "gbk", "utf-16"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", "replace")


# ★ 一次 PowerShell 调用同时取「运行中的 MuMu 进程路径」和「开始菜单/桌面快捷方式目标」，
#   不依赖具体安装目录（官方安装包必留这两样）。
_PS_HINTS = r"""
$ErrorActionPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
Get-Process MuMuNxMain, MuMuNxDevice, MuMuPlayer, MuMuMultiPlayer, MuMuManager, NemuPlayer |
    ForEach-Object { $_.Path }
$sh = New-Object -ComObject WScript.Shell
$dirs = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    (Join-Path $env:USERPROFILE 'Desktop'),
    (Join-Path $env:PUBLIC 'Desktop')
)
foreach ($d in $dirs) {
    Get-ChildItem -LiteralPath $d -Recurse -Filter *.lnk -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*MuMu*' -or $_.Name -like '*模拟器*' } |
        ForEach-Object { $sh.CreateShortcut($_.FullName).TargetPath }
}
"""


def _process_and_shortcut_hints() -> list[str]:
    """运行中的进程路径 + 快捷方式目标（最可靠的两条线索）。"""
    if os.name != "nt":
        return []
    try:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", _PS_HINTS],
            capture_output=True,
            timeout=25,
            creationflags=0x08000000,
        )
    except Exception:  # noqa: BLE001
        return []
    return [ln.strip() for ln in _decode_ps(r.stdout or b"").splitlines() if ln.strip()]


def _registry_hints() -> list[str]:
    """从卸载注册表里读 MuMu 的安装位置（跨盘、跨版本都靠它）。"""
    if os.name != "nt":
        return []
    try:
        import winreg
    except Exception:  # noqa: BLE001
        return []

    hives = (
        (winreg.HKEY_LOCAL_MACHINE,
         r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_LOCAL_MACHINE,
         r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_CURRENT_USER,
         r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Netease"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Netease"),
    )
    out: list[str] = []
    for hive, sub in hives:
        try:
            key = winreg.OpenKey(hive, sub)
        except OSError:
            continue
        try:
            count = winreg.QueryInfoKey(key)[0]
            for i in range(count):
                try:
                    name = winreg.EnumKey(key, i)
                    subkey = winreg.OpenKey(key, name)
                except OSError:
                    continue
                try:
                    try:
                        display = winreg.QueryValueEx(subkey, "DisplayName")[0]
                    except OSError:
                        display = ""
                    if not isinstance(display, str):
                        display = ""
                    # 没有 DisplayName 的分支（Netease）用子键名判断
                    if "mumu" not in f"{display} {name}".lower():
                        continue
                    for value_name in ("InstallLocation", "DisplayIcon",
                                       "UninstallString", "InstallPath", "Path"):
                        try:
                            val = winreg.QueryValueEx(subkey, value_name)[0]
                        except OSError:
                            continue
                        if isinstance(val, str) and val.strip():
                            out.append(_clean_hint(val))
                finally:
                    winreg.CloseKey(subkey)
        finally:
            winreg.CloseKey(key)
    return [x for x in out if x]


def _folder_hints() -> list[str]:
    """枚举各盘 ``Program Files`` / ``Netease`` 下名字含 MuMu 的目录。"""
    if os.name != "nt":
        return []
    parents: list[str] = []
    for env in ("ProgramFiles", "ProgramFiles(x86)"):
        p = os.environ.get(env)
        if p:
            parents.append(p)
    local = os.environ.get("LOCALAPPDATA")
    if local:
        parents.append(os.path.join(local, "Programs"))
    for letter in "CDEFGHIJK":
        root = f"{letter}:\\"
        if not os.path.exists(root):
            continue
        parents += [
            root,
            os.path.join(root, "Program Files"),
            os.path.join(root, "Program Files (x86)"),
            os.path.join(root, "Programs"),
        ]

    out: list[str] = []
    seen: set[str] = set()
    for parent in parents:
        key = parent.lower()
        if key in seen or not os.path.isdir(parent):
            continue
        seen.add(key)
        try:
            entries = os.listdir(parent)
        except OSError:
            continue
        for name in entries:
            low = name.lower()
            full = os.path.join(parent, name)
            if "mumu" in low or low == "nemu":
                out.append(full)
            elif low == "netease":
                try:
                    subs = os.listdir(full)
                except OSError:
                    continue
                for sub in subs:
                    if "mumu" in sub.lower():
                        out.append(os.path.join(full, sub))
    return out


def _which_hints() -> list[str]:
    found = shutil.which(MUMUMANAGER) or shutil.which("MuMuManager")
    return [found] if found else []


def find_mumu() -> str:
    """定位 MuMuManager.exe。

    ★ 面向公开发布：使用者多、安装位置千变万化，不能依赖用户设环境变量。
      官方安装包一定会留下这些痕迹，按可靠性排序：
      ① 上次找到的缓存 → ② 运行中的 MuMu 进程（授权时模拟器必然开着，最权威）
      → ③ 开始菜单/桌面快捷方式（安装包必建，且直接指向 nx_main）
      → ④ 卸载注册表 InstallLocation → ⑤ 各盘 Program Files / Netease / 盘根枚举
      → ⑥ PATH → ⑦ 环境变量（仅给售后/高级用户用）。
    """
    hints: list[str] = [_from_cache()]
    hints += _process_and_shortcut_hints()
    hints += _registry_hints()
    hints += _folder_hints()
    hints += _which_hints()

    for hint in hints:
        hit = _manager_in(hint)
        if hit:
            _remember(hit)
            return hit

    for env in ENV_OVERRIDE:
        val = os.environ.get(env)
        if val:
            hit = _manager_in(_clean_hint(val))
            if hit:
                _remember(hit)
                return hit

    raise MumuError(
        "没找到 MuMu 模拟器（MuMuManager.exe）。\n"
        "· 请确认已安装网易 MuMu，并且**至少启动过一次**"
        "（安装包会建快捷方式与注册表项，程序靠它们定位）；\n"
        "· 仍找不到时，可用环境变量指到 nx_main\\MuMuManager.exe，"
        "或改用其它方式登录：python -m hgvd login --qr"
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
        # ★ 不能因为 idx 0/1 没启动就 break —— 用户可能只开了实例 2，
        #   提前退出会把正在运行的实例漏掉（旧代码就有这个问题）。
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


def _latest_stamp(mumu: str, idx: int) -> str:
    """该实例里红果数据的最近修改时间（``YYYY-MM-DD HH:MM``）。

    ★ 必须用**绝对路径**（RPC 不以 /data/data 为 cwd，相对路径会静默返回空）。
    ★ 必须用 ``ls -lt``（按 mtime 排），不能用 ``ls -t``（按文件名排）。
    """
    for path in (f"{DATA_ROOT}/databases/", f"{DATA_ROOT}/files/disk_collect/",
                 f"{PREFS}/"):
        try:
            out = sh(
                mumu, idx,
                f"ls -lt {path} 2>/dev/null | awk 'NR==2{{print $6, $7}}'",
                timeout=20,
            ).strip()
        except MumuError:
            continue
        if out:
            return out
    return ""


def _probe_state(state: dict) -> tuple[bool, bool, dict]:
    """拿 cookie 去打一次 ``passport/account/info``，判断这份授权到底还能不能用。

    返回 ``(valid, unknown, info)``：
      - valid=True  → 服务端认这个 sessionid（info 里带 user_id / 昵称）
      - unknown=True → 网络/节点问题，**不能**据此判定授权失效
      - 都不是       → 授权确实失效（账号信息接口返回 error_code）
    """
    cookies = {
        k: state.get(k, "")
        for k in ("sessionid", "sid_tt", "install_id", "odin_tt", "csrf")
        if state.get(k)
    }
    try:
        sess = make_login_session()
        for k, v in cookies.items():
            sess.cookies.set(k, v)
        info = fetch_account_info(sess)
        if info.get("user_id"):
            return True, False, info
        return False, False, {"error": "账号信息为空"}
    except requests.RequestException as e:  # noqa: BLE001
        return False, True, {"error": f"网络异常：{str(e)[:120]}"}
    except Exception as e:  # noqa: BLE001
        return False, False, {"error": str(e)[:160]}


def collect_instances(mumu: str | None = None, verify: bool = True) -> list[dict]:
    """枚举所有正在运行、且 App 里有 sessionid 的实例。

    每项：``{index, state, stamp, valid, unknown, user_id, user_name, error}``，
    按「App 数据最近修改」倒序 —— 用户刚打开过的那台排前面。
    """
    mumu = mumu or find_mumu()
    instances = [i for i in list_instances(mumu) if i["started"]]
    if not instances:
        raise MumuError("没有正在运行的 MuMu 实例，请先启动模拟器并打开红果 App")

    rows: list[dict] = []
    for info in instances:
        idx = int(info.get("index", 0))
        try:
            state = read_app_state(idx, mumu)
        except MumuError as e:
            rows.append({"index": idx, "state": {}, "stamp": "",
                         "valid": False, "unknown": False, "user_id": "",
                         "user_name": "", "error": str(e)})
            continue
        if not state["sessionid"]:
            rows.append({"index": idx, "state": state,
                         "stamp": _latest_stamp(mumu, idx), "valid": False,
                         "unknown": False, "user_id": "", "user_name": "",
                         "error": "没有 sessionid（App 可能未登录）"})
            continue

        row = {"index": idx, "state": state, "stamp": _latest_stamp(mumu, idx),
               "valid": False, "unknown": False, "user_id": "",
               "user_name": "", "error": ""}
        if verify:
            valid, unknown, pinfo = _probe_state(state)
            row.update(valid=valid, unknown=unknown,
                       user_id=pinfo.get("user_id", ""),
                       user_name=pinfo.get("user_name", ""),
                       error="" if valid else pinfo.get("error", ""))
        rows.append(row)

    rows.sort(key=lambda r: r.get("stamp", ""), reverse=True)
    return rows


def find_usable_instance(mumu: str | None = None,
                         verify: bool = True) -> tuple[int, dict]:
    """找一台**真能拿到授权**的实例。

    ★★ 多开时每台都可能装着已登录的红果，不能只按 mtime 猜：
      逐台读 cookie → 用 ``passport/account/info`` 验证 → 取第一台真正有效的。
      这样即使账号在多台之间「互踢」（旧 session 失效），也能自动挑到还活着的那台。
    ★ 设备身份（device_id / install_id）始终跟**这台实例的 cookie 同源**返回，
      调用方不要拿另一台的设备档案来配。
    """
    mumu = mumu or find_mumu()
    rows = collect_instances(mumu, verify=verify)
    unknown: list[dict] = []
    errors: list[str] = []

    for row in rows:
        idx = row["index"]
        state = row["state"]
        if not state.get("sessionid"):
            errors.append(f"实例 {idx}: {row['error'] or '没有 sessionid'}")
            continue
        if not verify:
            return idx, state
        if row["valid"]:
            state = dict(state)
            state["user_id"] = row["user_id"]
            state["user_name"] = row["user_name"]
            return idx, state
        if row["unknown"]:
            unknown.append(row)
            continue
        errors.append(f"实例 {idx}: 授权已失效（{row['error']}）")

    if unknown:
        # 网络不通时不能把人挡在门外：先按「最近活动的」用，并带上警告
        row = unknown[0]
        state = dict(row["state"])
        state["user_id"] = row["user_id"]
        state["user_name"] = row["user_name"]
        state["verify_warning"] = row["error"]
        return row["index"], state

    raise MumuError(
        "找不到能用的红果 App 授权。\n"
        + ("\n".join(errors) if errors else "没有可读的实例")
        + "\n· 请在 App 里确认已登录；多开时每台都要单独登录红果。"
    )


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
    acc.user_id = state.get("user_id") or ""
    acc.user_name = state.get("user_name") or ""
    if not acc.ok:
        raise MumuError(f"实例 {idx} 的 App 没有登录态")
    if state.get("verify_warning"):
        print(f"⚠ 实例 {idx} 的授权未能在线验证（{state['verify_warning']}），先按最近活动的那台使用。")

    # 顺带把设备身份记下来（签名要用），并**按 install_id 归档**，多开互不覆盖
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
        rows = {}
        try:
            for r in collect_instances(mumu, verify=True):
                rows[r["index"]] = r
        except MumuError as e:
            print(f"（读取实例状态失败：{e}）")
        for i in list_instances(mumu):
            idx = int(i.get("index", 0))
            name = i.get("name", "")
            if not i["started"]:
                print(f"实例 {idx}: 未启动  {name}")
                continue
            r = rows.get(idx)
            if not r:
                print(f"实例 {idx}: 已启动，但读不到红果数据  {name}")
            elif r["valid"]:
                print(f"实例 {idx}: ✓ 授权有效  uid={r['user_id']}  "
                      f"{r['user_name']}  最近活动={r['stamp']}")
            elif r["unknown"]:
                print(f"实例 {idx}: ? 无法在线验证（{r['error']}）")
            else:
                print(f"实例 {idx}: ✗ {r['error']}  最近活动={r['stamp']}")
        return 0

    try:
        if args.index >= 0:
            state = read_app_state(args.index, mumu)
            if not state["sessionid"]:
                print(f"实例 {args.index} 的 App 未登录")
                return 1
            valid, unknown, info = _probe_state(state)
            if not valid and not unknown:
                print(f"实例 {args.index} 的授权已失效：{info.get('error')}")
                return 1
            state["user_id"] = info.get("user_id", "")
            state["user_name"] = info.get("user_name", "")
            from .login import Account, save_account

            acc = Account(cookies={k: v for k, v in state.items()
                                   if k in ("sessionid", "sid_tt", "install_id", "odin_tt") and v})
            save_account(acc)
            print(f"已从实例 {args.index} 读取登录态"
                  f"{'（uid=' + state['user_id'] + '）' if state.get('user_id') else ''}")
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
