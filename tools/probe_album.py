"""端到端探测：合集 → 分集 → 播放地址 → 编码 → 解密 → 可播放文件。
⚠ 结论修正（2026-11-20）：本脚本是按「服务端限流」这个假设写的，
  **该假设已被证伪** —— 空 body 是服务端/网关丢弃请求，与请求频率无关
  （判据与实测见 docs/03-开发要点.md §1.0）。
  保留此脚本仅为复现历史实验；**不要**再照抄「限流窗口」「退避等待」这类做法，
  那个方向历史上白花过 19 分钟（7 轮退避全部未恢复）。

带重试与限流（服务端对密集请求返回 101000 / 空 body）。
"""
from __future__ import annotations

import html
import binascii
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

from hgvd import net
from hgvd.api import playback as pb
from hgvd.sign.core import DeviceIdentity, sign_request

T = r"C:\Users\chise\AppData\Local\Temp"
UA = ("com.phoenix.read/73932 (Linux; U; Android 14; zh_CN; 23127PN0CC; "
      "Build/UKQ1.230804.001; Cronet/TTNetVersion:8d40f833)")


def load_cookies(idx: int = 1) -> dict:
    cs = html.unescape(
        pathlib.Path(T + f"\\cookieStore_{idx}.xml").read_text(
            encoding="utf-8", errors="replace")
    )

    def grab(n):
        m = re.search(r"\|" + n + r'">(.*?)</string>', cs, re.S)
        if not m:
            return ""
        b = binascii.unhexlify(m.group(1).replace(" ", ""))
        v = re.findall(rb"[0-9a-f]{12,}", b)
        return v[0].decode() if v else ""

    return {
        "sessionid": grab("sessionid"),
        "sid_tt": grab("sid_tt"),
        "install_id": grab("install_id"),
    }


COOK = load_cookies()
SESSION = net.make_session()
IDENT = DeviceIdentity(
    device_id="744826091366314", iid=COOK["install_id"], cdid=""
)


def pairs():
    return [
        ("iid", IDENT.iid), ("device_id", IDENT.device_id), ("ac", "wifi"),
        ("channel", "xiaomi_8662_64"), ("aid", "8662"), ("app_name", "novelread"),
        ("cdid", IDENT.cdid), ("version_code", "73932"),
        ("version_name", "7.3.9.32"), ("device_platform", "android"),
        ("os", "android"), ("ssmix", "a"), ("device_type", "Xiaomi 14"),
        ("device_brand", "Redmi"), ("language", "zh"), ("os_api", "34"),
        ("os_version", "14"), ("manifest_version_code", "73932"),
        ("resolution", "1080*2400"), ("dpi", "480"),
        ("update_version_code", "73932"), ("host_abi", "arm64-v8a"),
        ("dragon_device_type", "phone"), ("pv_player", "73932"),
        ("compliance_status", "0"), ("need_personal_recommend", "1"),
        ("player_so_load", "1"), ("is_android_pad_screen", "0"), ("openudid", ""),
    ]


def signed_post(path: str, body: dict, tries: int = 5, gap: float = 2.5):
    """带重试的签名 POST。返回 (status, json_or_None)。"""
    rb = json.dumps(body, separators=(",", ":")).encode()
    last = ""
    for i in range(tries):
        now = int(time.time() * 1000)
        q, h = sign_request(path, pairs(), now, IDENT.device_id,
                             IDENT.version_name, rb)
        hd = {
            "User-Agent": UA, "Accept": "application/json", "X-SS-DP": "8662",
            "Content-Type": "application/json; charset=UTF-8",
            "Cookie": "; ".join(f"{k}={v}" for k, v in COOK.items()),
            **h,
        }
        try:
            r = SESSION.post(
                f"https://api5-normal-sinfonlinea.fqnovel.com{path}?{q}",
                data=rb, headers=hd, timeout=30)
        except Exception as e:  # noqa: BLE001
            last = f"net {e}"
            time.sleep(gap)
            continue
        if not r.content:
            last = f"空 body (HTTP {r.status_code})"
            time.sleep(gap * (i + 1))
            continue
        try:
            j = r.json()
        except Exception:
            last = f"非 JSON (HTTP {r.status_code})"
            time.sleep(gap)
            continue
        if j.get("code") == 0:
            return j, ""
        last = f"code={j.get('code')} {j.get('message')}"
        time.sleep(gap * (i + 1))
    return None, last


def album_detail(album_id: str, book_type: int = 3):
    j, err = signed_post("/novel/player/album_detail/v1/",
                          {"album_id": album_id, "book_type": book_type})
    if j is None:
        return None, err
    return (j.get("data") or {}).get("album_data"), ""


def video_model(vid: str):
    j, err = signed_post("/novel/player/video_model/v1/",
                          {"video_id": vid,
                           "biz_param": {"video_id_type": 0,
                                         "need_all_video_definition": True,
                                         "video_platform": 3}})
    if j is None:
        return None, err
    return j, ""


def main():
    sid = sys.argv[1] if len(sys.argv) > 1 else "7662738851323989016"
    print(f"=== 合集 {sid} ===")
    ad, err = album_detail(sid)
    if ad is None:
        print("album_detail 失败:", err)
        return 1
    vdl = ad.get("video_detail_list") or []
    print(f"标题: {ad.get('title')}  集数: {ad.get('episode_cnt')}  "
          f"列表: {len(vdl)}  合集文案: {ad.get('episode_entrance_text')}")
    if not vdl:
        return 1

    for idx, item in enumerate(vdl[:3], 1):
        esid = item["series_id_str"]
        print(f"\n--- 第 {idx} 集: {item.get('series_title','')[:40]} "
              f"(id={esid}, {item.get('duration')}s) ---")
        p, err = video_model(esid)
        if p is None:
            print("   video_model 失败:", err)
            continue
        sts = pb.parse_streams(p)
        print(f"   清晰度 {len(sts)} 条:")
        for st in sts:
            print(f"     {st['definition']:8s} codec={st['codec']:12s} "
                  f"{st['width']}x{st['height']} size={st['size']:>9} "
                  f"enc={st['encrypted']} vtype={st.get('vtype')} "
                  f"tag={st.get('codec_tag')}")
        key = pb.extract_content_key(p)
        print("   内容密钥:", key)
        if sts:
            out = f"probe_{idx}.json"
            pathlib.Path(out).write_text(
                json.dumps(p, ensure_ascii=False, indent=1), encoding="utf-8")
            print("   已存", out)
        time.sleep(3)
    return 0


if __name__ == "__main__":
    sys.exit(main())
