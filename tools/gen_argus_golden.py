"""生成 x-argus / x-ladon 的**字节级**黄金向量（供 C# 回归测试用）。

★ 为什么必须有这个文件：
  2026-11-14 实测发现 C# 版 ``x-argus`` 有两处静默错误 ——
  ① PKCS#7 填充值用了 0（应为填充长度）
  ② ``ts << 1`` 在 int32 内溢出（3400000000 → -894967296）
  两者都**不会报错**，只是让签名对不上 —— 服务端收到后
  **不返回任何错误码、直接回一个空 body**，表现得像「被风控」，
  极难定位。而当时的黄金向量里 ``x-argus`` 只有 4 字节桩，
  压根没覆盖真签名 ⇒ 漏网。

★ 本文件由 **Python 版**（已对过 Go 参考实现）算出，
  C# 必须逐字节一致。

用法：
    python tools/gen_argus_golden.py            # 写到 hgvd-csharp/tools/golden_argus.json
"""

from __future__ import annotations

import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from hgvd.sign import core as sc  # noqa: E402

# 固定的测试输入（ts 取 3 个不同年代，覆盖 int32 边界）
CASES = [
    {
        "name": "基础用例",
        "query": (
            "iid=4351246100332768&device_id=4351246100902096&ac=wifi&aid=8662"
            "&app_name=novelread&version_code=73932&version_name=7.3.9.32"
            "&channel=xiaomi_8662_64&device_platform=android&os=android"
            "&ts={ts}&_rticket={rt}"
        ),
        "body": (
            b'{"biz_param":{"need_all_video_definition":true,"video_id_type":1,'
            b'"video_platform":3},"video_id":"7700851630874315800"}'
        ),
        "ts": 1700000000,
        "nonce": 123456789,
        "aid": "8662",
        "device_id": "4351246100902096",
        "install_id": "4351246100332768",
    },
    {
        # ★ ts*2 = 3583… > int32.MaxValue —— 曾经的溢出点
        "name": "ts<<1 超过 int32",
        "query": (
            "iid=744826091370410&device_id=744826091366314&ac=wifi&aid=8662"
            "&app_name=novelread&version_code=73932&version_name=7.3.9.32"
            "&channel=xiaomi_8662_64&device_platform=android&os=android"
            "&ts={ts}&_rticket={rt}"
        ),
        "body": (
            b'{"biz_param":{"need_all_video_definition":true,"video_id_type":0,'
            b'"video_platform":3},"video_id":"7702405382206917657"}'
        ),
        "ts": 1794614047,
        "nonce": 2701414485,
        "aid": "8662",
        "device_id": "744826091366314",
        "install_id": "744826091370410",
    },
    {
        "name": "nonce 取满 32 位",
        "query": (
            "iid=4351246100332768&device_id=4351246100902096&ac=wifi&aid=8662"
            "&app_name=novelread&version_code=73932&version_name=7.3.9.32"
            "&channel=xiaomi_8662_64&device_platform=android&os=android"
            "&ts={ts}&_rticket={rt}"
        ),
        "body": (
            b'{"biz_param":{"need_all_video_definition":true,"video_id_type":1,'
            b'"video_platform":3},"video_id":"7700851630874315800"}'
        ),
        "ts": 2000000000,
        "nonce": 4294967295,
        "aid": "8662",
        "device_id": "4351246100902096",
        "install_id": "4351246100332768",
    },
]


def build() -> dict:
    cases = []
    for c in CASES:
        query = c["query"].format(ts=c["ts"], rt=c["ts"] * 1000)
        body_md5 = hashlib.md5(c["body"]).digest()
        argus = sc._argus_header(
            query, body_md5, c["ts"], c["nonce"], c["aid"],
            c["device_id"], c["install_id"],
        )
        ladon = sc._ladon_header(c["ts"], c["nonce"], c["aid"])
        cases.append({
            "name": c["name"],
            "query": query,
            "body_md5_hex": body_md5.hex(),
            "ts": c["ts"],
            "nonce": c["nonce"],
            "aid": c["aid"],
            "device_id": c["device_id"],
            "install_id": c["install_id"],
            "x_argus": argus,
            "x_ladon": ladon,
        })
    return {"cases": cases}


def main() -> int:
    root = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    out = os.path.join(root, "hgvd-csharp", "tools", "golden_argus.json")
    data = build()
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    print(f"已生成 {len(data['cases'])} 组向量 → {out}")
    for c in data["cases"]:
        print(f"  · {c['name']}: x-argus {len(c['x_argus'])} 字符 / x-ladon {len(c['x_ladon'])} 字符")
    return 0


if __name__ == "__main__":
    sys.exit(main())
