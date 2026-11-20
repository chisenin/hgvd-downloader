"""探测教学合集分集的播放地址与编码（带长退避，规避服务端风控）。
⚠ 结论修正（2026-11-20）：本脚本是按「服务端限流」这个假设写的，
  **该假设已被证伪** —— 空 body 是服务端/网关丢弃请求，与请求频率无关
  （判据与实测见 docs/03-开发要点.md §1.0）。
  保留此脚本仅为复现历史实验；**不要**再照抄「限流窗口」「退避等待」这类做法，
  那个方向历史上白花过 19 分钟（7 轮退避全部未恢复）。

用法：
    python tools/probe_album_play.py <album_series_id> [集序号]
"""
from __future__ import annotations

import html
import binascii
import json
import pathlib
import re
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

from hgvd import net
from hgvd.api import album as album_api
from hgvd.api import detail as D
from hgvd.api import playback as pb
from hgvd.sign.core import DeviceIdentity

T = r"C:\Users\chise\AppData\Local\Temp"


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


def main():
    album_id = sys.argv[1] if len(sys.argv) > 1 else "7662738851323989016"
    which = int(sys.argv[2]) if len(sys.argv) > 2 else 1

    cookies = load_cookies()
    session = net.make_session()
    ident = DeviceIdentity(
        device_id="744826091366314", iid=cookies["install_id"], cdid="")

    print(f"[1] album_detail {album_id}", flush=True)
    raw = D.signed_post(session, ident, D.PATH_ALBUM,
                        {"album_id": album_id, "book_type": 3}, cookies,
                        retries=5, quiet=True)
    if not raw:
        print("  album 失败（限流）")
        return 1
    ad = album_api.parse_album(raw)
    print(f"  {ad.title} | {ad.episode_cnt} 集 | {len(ad.episodes)} 项", flush=True)
    if not ad.episodes:
        return 1

    ep = ad.episodes[min(which, len(ad.episodes)) - 1]
    print(f"[2] 第{which}集 {ep.series_id} {ep.title[:36]} ({ep.duration}s)", flush=True)

    print("[3] video_detail 拿 vid", flush=True)
    dv = D.signed_post(session, ident, "/novel/player/video_detail/v1/",
                       {"series_id": ep.series_id,
                        "biz_param": {"caller_scene": "single_col",
                                      "detail_page_version": 0,
                                      "from_video_id": "",
                                      "need_all_video_definition": True,
                                      "need_mp4_align": False,
                                      "source": 4, "video_id_type": 1}},
                       cookies, retries=4, quiet=True)
    if not dv:
        print("  video_detail 失败（限流）")
        return 1
    vdl = ((dv.get("data") or {}).get("video_data") or {}).get("video_list") or []
    vid = ""
    if vdl:
        vid = vdl[0].get("vid") or ""
    print(f"  vid={vid}  content_type={vdl[0].get('content_type') if vdl else '?'}",
          flush=True)
    vid = vid or ep.series_id

    print("[4] video_model（长退避）", flush=True)
    body = {"video_id": vid,
            "biz_param": {"video_id_type": 0,
                          "need_all_video_definition": True,
                          "video_platform": 3}}
    p = None
    for attempt in range(8):
        p = D.signed_post(session, ident, D.PATH_PLAYBACK, body, cookies,
                          retries=1, quiet=True)
        if p:
            break
        wait = min(10 * (attempt + 1), 60)
        print(f"  试{attempt+1}: 空 body，等待 {wait}s", flush=True)
        time.sleep(wait)
    if not p:
        print("  video_model 始终失败")
        return 1

    sts = pb.parse_streams(p)
    print(f"  code={p.get('code')} 清晰度 {len(sts)} 条", flush=True)
    for x in sts:
        print(f"    {x['definition']:8s} codec={x['codec']:12s} "
              f"{x['width']}x{x['height']} size={x['size']:>9} "
              f"enc={x['encrypted']} vtype={x.get('vtype')} tag={x.get('codec_tag')}")
    print("  内容密钥:", pb.extract_content_key(p), flush=True)
    pathlib.Path("probe_album_play.json").write_text(
        json.dumps(p, ensure_ascii=False, indent=1), encoding="utf-8")
    print("  已存 probe_album_play.json", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
