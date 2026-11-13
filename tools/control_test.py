"""对照实验：短剧 vid（已知成功）vs 教学内容 vid，同时刻各发一次。

判据：
  - 两者都空 → 服务端限流（当前状态），与内容类型无关
  - 短剧成功、教学空 → content_type=1001 走的是另一个播放端点
"""
from __future__ import annotations
import html, binascii, json, pathlib, re, sys, time
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from hgvd import net
from hgvd.api import detail as D
from hgvd.api import playback as pb
from hgvd.sign.core import DeviceIdentity

import tempfile
T = tempfile.gettempdir()
cs = html.unescape(pathlib.Path(T+"\\cookieStore_1.xml").read_text(encoding="utf-8",errors="replace"))
def grab(n):
    m = re.search(r'\|'+n+r'">(.*?)</string>', cs, re.S)
    if not m: return ""
    b = binascii.unhexlify(m.group(1).replace(" ",""))
    v = re.findall(rb'[0-9a-f]{12,}', b)
    return v[0].decode() if v else ""
COOK={"sessionid":grab("sessionid"),"sid_tt":grab("sid_tt"),"install_id":grab("install_id")}
s = net.make_session()
ident = DeviceIdentity(device_id="744826091366314", iid=COOK["install_id"], cdid="")

# 短剧对照组：网页版那部剧的第一集 vid（此前 video_model 成功过）
SHORT = "7697465535398677529"
# 教学合集的第一集
TEACH = "7662736695145548824"

for label, vid in (("短剧(1004)对照", SHORT), ("教学合集(1001)", TEACH)):
    body={"video_id":vid,"biz_param":{"video_id_type":0,"need_all_video_definition":True,"video_platform":3}}
    p = D.signed_post(s, ident, D.PATH_PLAYBACK, body, COOK, retries=1, quiet=True)
    if p:
        sts = pb.parse_streams(p)
        print(f"★ {label}: code={p.get('code')} 清晰度={len(sts)}", flush=True)
        for x in sts:
            print(f"    {x['definition']:8s} {x['codec']:12s} {x['width']}x{x['height']} size={x['size']:>9} enc={x['encrypted']}", flush=True)
        if label.startswith("教学"):
            pathlib.Path("probe_album_play.json").write_text(json.dumps(p,ensure_ascii=False,indent=1),encoding="utf-8")
    else:
        print(f"  {label}: 空 body", flush=True)
    time.sleep(10)
