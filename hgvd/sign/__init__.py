"""抖音系签名链（x-gorgon / x-helios / x-medusa / x-argus / x-ladon）。

这是从 Go 参考实现（openhongguo，Apache-2.0）移植的等价 Python 实现。

★ 为什么需要它：网页版只提供 10 秒试看片段，
完整正片必须走 App 网关的 ``/novel/player/video_model/v1/``，
而该接口从 2026-11-10 起校验真实 ``x-argus`` / ``x-ladon``
（搜索、详情等其它接口仍接受 4 字节时间戳桩）。

模块划分：
    sm3.py      SM3 哈希（标准 GB/T 32905-2016）
    aesv3.py    AES-V3 变体（medusa 用的自定义分组密码）
    core.py     签名主流程
"""

from .core import (  # noqa: F401
    DeviceIdentity,
    Entropy,
    sign_playback,
    sign_request,
    sign_video_model,
)

__all__ = [
    "DeviceIdentity",
    "Entropy",
    "sign_playback",
    "sign_request",
    "sign_video_model",
]
