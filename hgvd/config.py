"""配置与路径。

★ 所有可写状态（配置 / 历史 / 分片缓存）都落在 ``%USERPROFILE%\\.hgv_tool``，
与参考项目 ``.xqd_tool`` 隔离，避免两个工具互相覆盖。
"""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, asdict, field
from pathlib import Path

# ---------------------------------------------------------------- 环境隔离

# 测试用：设为 True 时配置/历史/分片缓存走临时目录，不碰用户真实数据
TEST_HOME = os.environ.get("HGV_TEST_HOME")

TOOL_DIR_NAME = ".hgv_tool"


def tool_dir() -> Path:
    """工具数据目录（配置 / 历史 / 分片缓存）。"""
    if TEST_HOME:
        return Path(TEST_HOME) / TOOL_DIR_NAME
    return Path.home() / TOOL_DIR_NAME


def config_path() -> Path:
    return tool_dir() / "config.json"


def history_path() -> Path:
    return tool_dir() / "history.json"


def creds_path() -> Path:
    """登录凭据。

    ★ 与配置/历史不同：凭据**不受** ``HGV_TEST_HOME`` 影响。
    测试隔离是为了不写坏用户真实数据，凭据隔离会让真网络测试拿不到授权。
    """
    return Path.home() / TOOL_DIR_NAME / "cookie.json"


def parts_root() -> Path:
    return tool_dir() / "parts"


def state_path() -> Path:
    return tool_dir() / "state.json"


# ---------------------------------------------------------------- 下载目录


def windows_download_dir() -> Path:
    """Windows「下载」目录。

    ★ 必须读注册表 Known Folder 的 GUID
    （``{374DE290-123F-4565-9164-39C4925E467B}``）——
    Windows 上「文档」目录常被重定向到其他盘，且目录名可能是小写 ``download``，
    ``expanduser('~') + '/Downloads'`` 会取错。
    """
    if sys.platform != "win32":
        return Path.home() / "Downloads"
    try:
        import winreg

        key = winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
        )
        try:
            val, _ = winreg.QueryValueEx(key, "{374DE290-123F-4565-9164-39C4925E467B}")
            return Path(val)
        finally:
            winreg.CloseKey(key)
    except Exception:
        return Path.home() / "Downloads"


# ---------------------------------------------------------------- 配置模型


@dataclass
class Config:
    """用户配置，落盘为 JSON。"""

    out_dir: str = ""          # 下载根目录
    quality: str = "1080p"     # 清晰度：360p / 720p / 1080p（默认最高）
    workers: int = 3           # 剧目级并发
    seg_workers: int = 8       # 单集内部分片并发
    max_episodes: int = 0      # 每部剧最多下几集（0 = 全部）
    skip_existing: bool = True  # 跳过已下载的

    def ensure_out_dir(self) -> Path:
        """返回有效的下载根目录。

        规则（与参考项目一致）：
        1. 用户改过 → 用 config 的 ``out_dir``
        2. 旧默认目录不存在 → 回落系统下载目录
        3. 用户自定目录盘没挂 → **仍记住，不偷改**
        """
        if self.out_dir:
            p = Path(self.out_dir)
            if p.exists():
                return p
            # 自定目录的盘可能没挂 —— 仍记住，不回落（回落会写到别处，用户找不到）
            if len(p.anchor) > 0 and p.anchor[0].isalpha():
                try:
                    import os as _os

                    if _os.path.exists(p.anchor + "\\"):
                        return p
                except Exception:
                    pass
            # 相对路径或已失效 → 回落
            return windows_download_dir()
        return windows_download_dir()

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def load(cls, path: Path | None = None) -> "Config":
        p = path or config_path()
        cfg = cls()
        if p.exists():
            try:
                raw = json.loads(p.read_text(encoding="utf-8"))
                for k, v in raw.items():
                    if hasattr(cfg, k):
                        setattr(cfg, k, v)
            except Exception:
                pass
        return cfg

    def save(self, path: Path | None = None) -> None:
        p = path or config_path()
        p.parent.mkdir(parents=True, exist_ok=True)
        tmp = p.with_suffix(p.suffix + ".tmp")
        tmp.write_text(
            json.dumps(self.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
        )
        tmp.replace(p)
