"""换 device_id 试：限流可能是按设备指纹分桶的。
⚠ 结论修正（2026-11-20）：本脚本是按「服务端限流」这个假设写的，
  **该假设已被证伪** —— 空 body 是服务端/网关丢弃请求，与请求频率无关
  （判据与实测见 docs/03-开发要点.md §1.0）。
  保留此脚本仅为复现历史实验；**不要**再照抄「限流窗口」「退避等待」这类做法，
  那个方向历史上白花过 19 分钟（7 轮退避全部未恢复）。

"""
from __future__ import annotations
import html, binascii, json, pathlib, re, sys, time
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from hgvd import net
from hgvd.api import detail as D
from hgvd.api import playback as pb
from hgvd.sign.core import DeviceIdentity

import tempfile
T = tempfile.gettempdir()          # ★ 不要写死本机路径
cs = html.unescape(pathlib.Path(T+"\\cookieStore_1.xml").read_text(encoding="utf-8",errors="replace"))
def grab(n):
    m = re.search(r'\|'+n+r'">(.*?)</string>', cs, re.S)
    if not m: return ""
    b = binascii.unhexlify(m.group(1).replace(" ",""))
    v = re.findall(rb'[0-9a-f]{12,}', b)
    return v[0].decode() if v else ""
COOK={"sessionid":grab("sessionid"),"sid_tt":grab("sid_tt"),"install_id":grab("install_id")}
s = net.make_session()
TEACH = "7662736695145548824"
SHORT = "7697465535398677529"

DEVICES = [
    ("App真机 744826091366314", dict(device_id="744826091366314", iid=COOK["install_id"], cdid="")),
    ("WASM设备 4351246100902096", dict(device_id="4351246100902096", iid=COOK["install_id"],
                                       cdid="e4dbc247-0c35-4c1c-ab28-8fa870ea6ce7")),
    ("WASM iid 4351246100332768", dict(device_id="4351246100902096", iid="4351246100332768",
                                        cdid="e4dbc247-0c35-4c1c-ab28-8fa870ea6ce7")),
]
for label, kw in DEVICES:
    ident = DeviceIdentity(**kw)
    for target, tag in ((TEACH,"教学合集"), (SHORT,"短剧对照")):
        body={"video_id":target,"biz_param":{"video_id_type":0,
                  "need_all_video_definition":True,"video_platform":3}}
        p = D.signed_post(s, ident, D.PATH_PLAYBACK, body, COOK, retries=1, quiet=True)
        if p:
            sts=pb.parse_streams(p)
            print(f"★ {label} / {tag}: code={p.get('code')} 清晰度={len(sts)}", flush=True)
            for x in sts:
                print(f"    {x['definition']:8s} {x['codec']:12s} {x['width']}x{x['height']} "
                      f"size={x['size']:>9} enc={x['encrypted']}", flush=True)
            pathlib.Path(f"probe_{'teach' if tag=='教学合集' else 'short'}.json").write_text(
                json.dumps(p,ensure_ascii=False,indent=1),encoding="utf-8")
        else:
            print(f"  {label} / {tag}: 空", flush=True)
        time.sleep(12)
