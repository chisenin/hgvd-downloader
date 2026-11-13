"""干净的单请求探测：长等待后只发一次 video_model。"""
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

WAIT = int(sys.argv[1]) if len(sys.argv)>1 else 300
TARGETS = [("教学合集第1集","7662736695145548824"), ("短剧对照","7697465535398677529")]
print(f"等待 {WAIT}s 让限流窗口过去…", flush=True)
time.sleep(WAIT)
for label, vid in TARGETS:
    body={"video_id":vid,"biz_param":{"video_id_type":0,"need_all_video_definition":True,"video_platform":3}}
    p = D.signed_post(s, ident, D.PATH_PLAYBACK, body, COOK, retries=1, quiet=True)
    if p:
        sts=pb.parse_streams(p)
        print(f"★ {label}: code={p.get('code')} 清晰度={len(sts)}", flush=True)
        for x in sts:
            print(f"    {x['definition']:8s} codec={x['codec']:12s} {x['width']}x{x['height']} "
                  f"size={x['size']:>9} dur={x.get('duration')} enc={x['encrypted']}", flush=True)
        pathlib.Path(f"clean_{'teach' if '教学' in label else 'short'}.json").write_text(
            json.dumps(p,ensure_ascii=False,indent=1),encoding="utf-8")
    else:
        print(f"  {label}: 空 body", flush=True)
    time.sleep(15)
