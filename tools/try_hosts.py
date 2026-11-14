"""同一请求打不同 host，看 content_type=1001 的播放地址是否走别的网关。"""
from __future__ import annotations
import html, binascii, json, pathlib, re, sys, time
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from hgvd import net
from hgvd.api import detail as D
from hgvd.api import playback as pb
from hgvd.sign.core import DeviceIdentity, sign_request

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
ident = DeviceIdentity(device_id="744826091366314", iid=COOK["install_id"], cdid="")
SID = sys.argv[1] if len(sys.argv)>1 else "7662736695145548824"

HOSTS = ["api5-normal-sinfonlinea.fqnovel.com","api5-normal-lq.fqnovel.com",
         "reading.snssdk.com","api5-normal-sinfonlineb.fqnovel.com","fanqienovel.com"]
body={"video_id":SID,"biz_param":{"video_id_type":0,"need_all_video_definition":True,"video_platform":3}}
rb=json.dumps(body,separators=(",",":")).encode()
now=int(time.time()*1000)
q,h = sign_request(D.PATH_PLAYBACK, D.device_pairs(ident), now, ident.device_id, ident.version_name, rb)
hdr={"User-Agent":D.UA,"Accept":"application/json","X-SS-DP":"8662",
     "Content-Type":"application/json; charset=UTF-8",
     "Cookie":"; ".join(f"{k}={v}" for k,v in COOK.items()), **h}
for host in HOSTS:
    try:
        r = s.post(f"https://{host}{D.PATH_PLAYBACK}?{q}", data=rb, headers=hdr, timeout=30)
        n = len(r.content)
        ok = "?"
        if n:
            try:
                j=r.json(); sts=pb.parse_streams(j)
                ok = f"code={j.get('code')} 清晰度={len(sts)}"
                if sts:
                    for x in sts:
                        print(f"     {x['definition']} {x['codec']} {x['width']}x{x['height']} size={x['size']} enc={x['encrypted']}", flush=True)
                    pathlib.Path("probe_album_play.json").write_text(json.dumps(j,ensure_ascii=False,indent=1),encoding="utf-8")
            except Exception as e:
                ok = f"非JSON {r.content[:60]}"
        print(f"[{r.status_code}] {host:42s} len={n:>7} {ok}", flush=True)
    except Exception as e:
        print(f"[err] {host:42s} {str(e)[:70]}", flush=True)
    time.sleep(8)
