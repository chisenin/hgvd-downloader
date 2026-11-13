"""设备档案。

★ App 网关的签名把 ``device_id`` / ``iid`` 绑进签名里，
所以必须**稳定持久化** —— 每次请求都换设备身份会被判异常。

★ 设备注册（``/service/2/device_register/``）用 TTEncrypt V5 编码，
是一块独立的硬骨头。当前实现走「借用已有设备身份」的路线：
  1. 优先读本地 ``device.json``
  2. 没有则用固定种子生成一个稳定的匿名身份
  3. 服务端若拒绝（返回空 body），提示用户重新登录

匿名设备身份对「已登录 + 带 cookie」的请求够用：
服务端校验的是**账号**，设备身份只参与签名计算。
"""

from __future__ import annotations

import hashlib
import json
import uuid
from pathlib import Path

from .config import tool_dir
from .sign.core import DeviceIdentity


def device_path() -> Path:
    return tool_dir() / "device.json"


def load_device() -> DeviceIdentity | None:
    p = device_path()
    if not p.exists():
        return None
    try:
        d = json.loads(p.read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        return None
    if not d.get("device_id") or not d.get("iid"):
        return None
    return DeviceIdentity(
        device_id=str(d["device_id"]),
        iid=str(d["iid"]),
        cdid=str(d.get("cdid") or ""),
        openudid=str(d.get("openudid") or ""),
        version_name=str(d.get("version_name") or "7.3.9.32"),
        version_code=str(d.get("version_code") or "73932"),
    )


def save_device(ident: DeviceIdentity) -> None:
    p = device_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(
        json.dumps(
            {
                "device_id": ident.device_id,
                "iid": ident.iid,
                "cdid": ident.cdid,
                "openudid": ident.openudid,
                "version_name": ident.version_name,
                "version_code": ident.version_code,
            },
            ensure_ascii=False,
            indent=1,
        ),
        encoding="utf-8",
    )


def _seeded_int(seed: str, digits: int = 16) -> str:
    """从种子生成稳定的数字串（模拟 App 的 device_id / iid 形态）。"""
    h = hashlib.sha256(seed.encode("utf-8")).hexdigest()
    n = int(h[:16], 16)
    return str(n)[:digits].ljust(digits, "0")


def ensure_device() -> DeviceIdentity:
    """拿设备身份；没有就生成一个并持久化。

    ★ 种子取自机器的稳定信息 + 随机 UUID 的持久化结果，
      保证同一台机器每次拿到同一个身份。
    """
    ident = load_device()
    if ident:
        return ident

    import socket
    import uuid as _uuid

    host = socket.gethostname()
    seed = f"{host}|{_uuid.getnode()}"
    ident = DeviceIdentity(
        device_id=_seeded_int(seed + "|d"),
        iid=_seeded_int(seed + "|i"),
        cdid=str(_uuid.uuid4()),
        openudid=hashlib.md5(seed.encode()).hexdigest()[:16],
        version_name="7.3.9.32",
        version_code="73932",
    )
    save_device(ident)
    return ident
