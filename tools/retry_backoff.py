"""长退避重试：等待限流窗口过去后重试 video_model。
⚠ 结论修正（2026-11-20）：本脚本是按「服务端限流」这个假设写的，
  **该假设已被证伪** —— 空 body 是服务端/网关丢弃请求，与请求频率无关
  （判据与实测见 docs/03-开发要点.md §1.0）。
  保留此脚本仅为复现历史实验；**不要**再照抄「限流窗口」「退避等待」这类做法，
  那个方向历史上白花过 19 分钟（7 轮退避全部未恢复）。

"""
import sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from hgvd.api import playback as pb
from hgvd.service import HongguoService

svc = HongguoService()
ident, cookies = svc.device_identity(), svc.cookie_dict()
VID = "7700851630874315800"
waits = [60, 90, 120, 150, 180, 240, 300]
for i, w in enumerate(waits):
    try:
        p = pb.fetch_playback(svc.session, ident, VID, cookies=cookies,
                              video_id_type=1, retries=1, pause=0.1)
        st = pb.parse_streams(p)
        if st:
            s = st[0]
            print(f"[{i+1}] wait={w}s → ✓ {len(st)}档 {s['definition']} {s['width']}x{s['height']} {s['size']}B", flush=True)
            print("URL:", s["url"][:120], flush=True)
            sys.exit(0)
        print(f"[{i+1}] wait={w}s → 空 streams", flush=True)
    except Exception as e:
        print(f"[{i+1}] wait={w}s → ✗ {str(e)[:70]}", flush=True)
    time.sleep(w)
print("全部退避后仍失败", flush=True)
sys.exit(1)
