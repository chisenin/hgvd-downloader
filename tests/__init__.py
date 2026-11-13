"""离线回归测试。

★ 不发网络请求、不碰用户真实配置。
★ 用 ``HGV_TEST_HOME`` 隔离配置 / 历史 / 分片缓存。
★ 凭据文件**不隔离**（那会让真网络测试拿不到授权）。

运行::

    python -m tests.run
"""

from __future__ import annotations

import os
import pathlib
import sys

os.environ.setdefault(
    "HGV_TEST_HOME", str(pathlib.Path(__file__).resolve().parent.parent / ".testhome")
)
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
