"""扫 video_model 的 biz_param 组合，找出 content_type=1001 内容能用的那个。"""
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
SID = sys.argv[1] if len(sys.argv)>1 else "7662736695145548824"

COMBOS = []
for vit in (0,1):
    for vp in (0,1,2,3,4):
        COMBOS.append((f"vit={vit},vp={vp}",
                       {"video_id_type":vit,"need_all_video_definition":True,"video_platform":vp}))
COMBOS.append(("bare", {}))
COMBOS.append(("scene_play", {"caller_scene":"play","video_id_type":1}))
COMBOS.append(("scene_single_col", {"caller_scene":"single_col","video_id_type":1}))
COMBOS.append(("source4", {"source":4,"video_id_type":1,"need_all_video_definition":True}))
COMBOS.append(("source7", {"source":7,"video_id_type":1,"need_all_video_definition":True}))
COMBOS.append(("content1001", {"content_type":1001,"video_id_type":1,"need_all_video_definition":True}))

for name, biz in COMBOS:
    body = {"video_id": SID}
    if biz: body["biz_param"] = biz
    p = D.signed_post(s, ident, D.PATH_PLAYBACK, body, COOK, retries=1, quiet=True)
    if p:
        sts = pb.parse_streams(p)
        print(f"★ {name}: code={p.get('code')} 清晰度={len(sts)}", flush=True)
        for x in sts:
            print(f"    {x['definition']:8s} {x['codec']:12s} {x['width']}x{x['height']} size={x['size']:>9} enc={x['encrypted']}", flush=True)
        pathlib.Path("probe_album_play.json").write_text(json.dumps(p,ensure_ascii=False,indent=1),encoding="utf-8")
        break
    print(f"  {name}: 空", flush=True)
    time.sleep(6)
