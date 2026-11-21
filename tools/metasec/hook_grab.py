#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
路线2：frida hook OkHttp + 深链 + 自动上滑，抓全剧直链并下载。

原理：不再伪造签名（已被证伪），让 App 自己在它的线程里请求播放接口
（签名由 App 算），我们 hook RealCall.getResponseWithInterceptorChain()
非破坏性读取响应，拿到 video_list[].main_url，然后复用 hgvd 的下载引擎落盘。

相比旧抓流方案：不用装 CA、不用设 http_proxy、不用重启 App 带代理，链路轻。

用法:
  python tools/metasec/hook_grab.py --series 7705413803088825369 --out D:\\movies\\hgvd
  python tools/metasec/hook_grab.py --series <id> --out <dir> --title "剧名" --max 100

前提: 雷电已开 root、frida-server 已启动、红果已登录。
      adb 路径/设备序列号/frida 地址都可自动探测，也可用 --adb/--serial/--frida-host 指定。
"""
from __future__ import annotations
import argparse, base64, gzip, zlib, json, re, time, subprocess, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

# ★ 一律不写死：main() 里按命令行参数 / 自动探测覆盖。
#   旧版写死 127.0.0.1:27042 + 系统 adb + 900x1600 坐标，换机器 / 换模拟器就废。
FRIDA_HOST = '127.0.0.1:27042'
ADB_EXE = 'adb'
ADB_SERIAL = ''
APP_SPLASH = 'com.phoenix.read/com.dragon.read.pages.splash.SplashActivity'

JS = r'''
var MAX = 8 * 1024 * 1024;
var KEYWORDS = ['multi_video_model/player', 'video_playback', 'series_detail'];
function isTarget(u){ for(var i=0;i<KEYWORDS.length;i++){ if(u.indexOf(KEYWORDS[i])>=0) return true; } return false; }
function b64(bytes){
  var C='ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  var out='',n=bytes.length,i=0;
  while(i<n){
    var a=bytes[i]&255,b=(i+1<n)?(bytes[i+1]&255):0,c=(i+2<n)?(bytes[i+2]&255):0;
    out+=C.charAt(a>>2)+C.charAt(((a&3)<<4)|(b>>4));
    out+=(i+1<n)?C.charAt(((b&15)<<2)|(c>>6)):'=';
    out+=(i+2<n)?C.charAt(c&63):'='; i+=3;
  }
  return out;
}
Java.perform(function(){
  var rc=Java.use('okhttp3.RealCall');
  rc.getResponseWithInterceptorChain.implementation=function(){
    var resp=this.getResponseWithInterceptorChain();
    try{
      var u=''+this.request().url();
      if(isTarget(u)){
        var raw=resp.peekBody(MAX).bytes();
        send({t:'r', u:u, enc:(''+resp.header('Content-Encoding')), b64:b64(raw)});
      }
    }catch(e){ send({t:'e', e:''+e}); }
    return resp;
  };
  send({t:'ready'});
});
'''


def adb(*args, timeout=20):
    """所有设备命令的唯一出口：adb 路径和序列号都来自参数/自动探测。"""
    cmd = [ADB_EXE]
    if ADB_SERIAL:
        cmd += ['-s', ADB_SERIAL]
    cmd += list(args)
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


def pick_serial() -> str:
    """没指定序列号时自动挑一台在线设备（优先 emulator-*，即模拟器）。"""
    try:
        r = adb('devices', timeout=15)
        rows = []
        for line in (r.stdout or '').splitlines()[1:]:
            parts = line.split()
            if len(parts) >= 2 and parts[1] == 'device':
                rows.append(parts[0])
        for s in rows:
            if s.startswith('emulator-'):
                return s
        return rows[0] if rows else ''
    except Exception:
        return ''


def screen_size() -> tuple:
    """读设备真实分辨率（不再写死 900x1600）；读不到才用旧值兜底。"""
    try:
        r = adb('shell', 'wm', 'size', timeout=15)
        m = re.search(r'(\d+)x(\d+)', r.stdout or '')
        if m:
            w, h = int(m.group(1)), int(m.group(2))
            if w >= 200 and h >= 200:
                return w, h
    except Exception:
        pass
    return 900, 1600


def ensure_forward(host: str):
    """frida 走本机回环时，自动补 adb forward（不再要求用户手动转 27042）。"""
    if not (host.startswith('127.0.0.1:') or host.startswith('localhost:')):
        return
    port = host.rsplit(':', 1)[-1]
    try:
        adb('forward', 'tcp:' + port, 'tcp:' + port, timeout=15)
    except Exception:
        pass


def decompress(raw: bytes, enc: str) -> str:
    if not raw: return ''
    enc = (enc or '').lower()
    try:
        if 'gzip' in enc or raw[:2] == b'\x1f\x8b':
            return gzip.decompress(raw).decode('utf-8', 'replace')
        if 'deflate' in enc:
            return zlib.decompress(raw).decode('utf-8', 'replace')
    except Exception:
        pass
    return raw.decode('utf-8', 'replace')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--series', required=True, help='剧集 series_id')
    ap.add_argument('--out', required=True, help='下载目录')
    ap.add_argument('--title', default='', help='剧名（用于文件夹/文件名），不填则从响应推断')
    ap.add_argument('--max', type=int, default=200, help='最多抓多少集')
    ap.add_argument('--swipes', type=int, default=300, help='最多上滑次数')
    ap.add_argument('--idle', type=int, default=12, help='连续多少次上滑无新集就停')
    ap.add_argument('--seconds', type=float, default=0, help='按时间采集（覆盖 idle 模式）')
    ap.add_argument('--pid', type=int, default=0)
    ap.add_argument('--dry', action='store_true', help='只抓直链清单不下载')
    ap.add_argument('--frida-host', default='', help='frida-server 地址（默认 127.0.0.1:27042）')
    ap.add_argument('--adb', default='', help='adb.exe 路径（默认用 PATH 里的 adb）')
    ap.add_argument('--serial', default='', help='adb 设备序列号（默认自动挑 emulator-*）')
    args = ap.parse_args()

    global FRIDA_HOST, ADB_EXE, ADB_SERIAL
    if args.adb:
        ADB_EXE = args.adb
    if args.frida_host:
        FRIDA_HOST = args.frida_host
    ADB_SERIAL = args.serial or pick_serial()
    ensure_forward(FRIDA_HOST)
    print(f'设备={ADB_SERIAL or "(未探测到)"}  adb={ADB_EXE}  frida={FRIDA_HOST}')

    import frida
    from hgvd.api import playback as pb
    from hgvd.engine.downloader import (
        CancelToken, DownloadCallbacks, DownloadRequest, download,
        episode_filename, series_folder,
    )

    # --- 连接 frida ---
    dev = frida.get_device_manager().add_remote_device(FRIDA_HOST)
    ses = None
    if args.pid:
        ses = dev.attach(args.pid)
    else:
        for p in dev.enumerate_processes():
            if 'phoenix' in (p.name or '') or '红果' in (p.name or ''):
                ses = dev.attach(p.pid); print(f'attach {p.pid} {p.name}'); break
    if ses is None:
        print('未找到红果进程'); return

    # --- 收集状态 ---
    episodes: dict[str, dict] = {}   # vid -> {model, title, idx}
    title = args.title
    raw_dir = Path('_ref/metasec/okhttp'); raw_dir.mkdir(parents=True, exist_ok=True)
    dump = raw_dir / (time.strftime('%Y%m%d-%H%M%S') + '-grab.jsonl')
    fh = open(dump, 'w', encoding='utf-8')

    def handle_resp(u: str, txt: str):
        nonlocal title
        try:
            obj = json.loads(txt)
        except Exception:
            return
        data = obj.get('data') or {}
        if not isinstance(data, dict):
            return
        # 播放模型
        for vid, info in data.items():
            if not isinstance(info, dict) or 'video_model' not in info:
                continue
            try:
                vm = json.loads(info['video_model'])
            except Exception:
                continue
            streams = pb.parse_streams({'data': {'video_model': info['video_model']}}) if isinstance(vm, dict) else []
            if streams and vid not in episodes:
                episodes[vid] = {
                    'vm': vm,
                    'streams': streams,
                    'duration': float(vm.get('video_duration') or 0),
                }
        # 详情里的剧名
        if not title:
            for v in data.values():
                if isinstance(v, dict):
                    for k in ('series_name', 'title', 'book_name'):
                        if v.get(k) and isinstance(v[k], str):
                            title = v[k][:60]; break
                if title: break

    def on_msg(m, data):
        if m['type'] == 'send':
            d = m.get('payload')
            if not isinstance(d, dict): return
            if d.get('t') == 'e':
                print('HOOK ERR:', d['e'][:150]); return
            if d.get('t') == 'r':
                txt = decompress(base64.b64decode(d['b64']), d.get('enc'))
                fh.write(json.dumps({'u': d['u'], 'body': txt[:500000]}, ensure_ascii=False) + '\n')
                fh.flush()
                before = len(episodes)
                handle_resp(d['u'], txt)
                if len(episodes) > before:
                    print(f'  +{len(episodes)-before} 集 → 累计 {len(episodes)} 集')

    sc = ses.create_script(JS)
    sc.on('message', on_msg)
    sc.load()
    time.sleep(1)

    # --- 深链进剧 ---
    print(f'深链进入 series={args.series}')
    adb('shell', 'input', 'keyevent', '3')  # home
    time.sleep(1)
    adb('shell', 'am', 'start', '-n', APP_SPLASH)
    time.sleep(3)
    url = f'dragon8662://detail?series_id={args.series}'
    adb('shell', 'am', 'start', '-a', 'android.intent.action.VIEW', '-d', url)
    time.sleep(4)
    print(f'  当前已抓 {len(episodes)} 集')

    # --- 自动上滑 ---
    sw, sh = screen_size()
    sx, y1, y2 = sw // 2, int(sh * 0.75), int(sh * 0.25)
    print(f'  分辨率 {sw}x{sh} → swipe {sx} {y1} {sx} {y2}')
    t0 = time.time()
    idle = 0
    swipes = 0
    while swipes < args.swipes and len(episodes) < args.max:
        if args.seconds and time.time() - t0 > args.seconds:
            break
        before = len(episodes)
        adb('shell', 'input', 'swipe', str(sx), str(y1), str(sx), str(y2), '280')
        swipes += 1
        time.sleep(1.3)
        if len(episodes) > before:
            idle = 0
        else:
            idle += 1
            if not args.seconds and idle >= args.idle:
                print(f'连续 {idle} 次无新集，停止')
                break
        if swipes % 10 == 0:
            print(f'  [上滑 {swipes}] 累计 {len(episodes)} 集')

    fh.close(); sc.unload(); ses.detach()
    print(f'\n采集完成：{len(episodes)} 集, 上滑 {swipes} 次, 原始响应 → {dump}')
    if not episodes:
        print('没抓到任何集，检查 App 是否在播放页、深链是否有效'); return

    book = title or f'短剧_{args.series}'
    print(f'剧名：{book}')

    if args.dry:
        manifest = Path(args.out) / f'{book}_直链清单.json'
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps(episodes, ensure_ascii=False, indent=2), encoding='utf-8')
        print(f'清单已存 → {manifest}'); return

    # --- 下载 ---
    out_root = Path(args.out)
    done = fail = 0
    for idx, (vid, ep) in enumerate(episodes.items(), 1):
        # 选最高码率
        st = max(ep['streams'], key=lambda s: s.get('bitrate') or 0)
        folder = out_root / series_folder(book)
        folder.mkdir(parents=True, exist_ok=True)
        name = episode_filename(book, idx, vid, '.mp4')
        content_key = pb.extract_content_key({'data': {'video_model': ep['vm']}}) or ''
        req = DownloadRequest(
            url=st['url'], out_path=str(folder / name),
            expect_duration=ep['duration'],
            content_key=content_key,
            backup_url=st.get('backup_url', ''),
        )
        cb = DownloadCallbacks(on_phase=lambda p: None, on_progress=None, on_log=None)
        res = download(req, cb, CancelToken())
        if res.ok:
            done += 1; print(f'✓ [{done}] {name} {res.size/1048576:.1f}MB')
        else:
            fail += 1; print(f'✗ {name}: {res.error}')
    print(f'\n完成：成功 {done}, 失败 {fail} → {out_root}')


if __name__ == '__main__':
    main()
