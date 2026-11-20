"""把抓到的请求原样重放（用来判断服务端到底在校验什么）。

用法::

    python tools/metasec/replay.py _ref/metasec/bodies/0053-xxx.txt            # 原样重放（旧签名）
    python tools/metasec/replay.py _ref/metasec/bodies/0053-xxx.txt --restub   # 重新压缩+重算 X-SS-STUB
    python tools/metasec/replay.py _ref/metasec/bodies/0053-xxx.txt --no-sig   # 去掉所有签名头
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
import sys
import time
from pathlib import Path

import requests

SKIP_REQ = {'content-length', 'content-encoding', 'transfer-encoding', 'connection', 'accept-encoding'}


def load(path: Path):
    text = path.read_text(encoding='utf-8')
    req_part, _, resp_part = text.partition('\nRESP ')
    lines = req_part.split('\n')
    first = lines[0]
    m = re.match(r'REQ (\S+) (\S+)', first)
    method, target = m.group(1), m.group(2)
    headers = []
    for ln in lines[1:]:
        if not ln or ln.startswith('{'):
            break
        if ':' in ln:
            headers.append(ln)
    body = req_part[req_part.rfind('\n\n') + 2:].strip()
    return method, target, headers, body


def build_headers(raw: list[str], *, restub: bool, no_sig: bool, body_gz: bytes):
    out = {}
    for h in raw:
        k, _, v = h.partition(':')
        k = k.strip()
        v = v.strip()
        kl = k.lower()
        if kl in SKIP_REQ:
            continue
        if no_sig and kl in ('x-gorgon', 'x-argus', 'x-ladon', 'x-khronos', 'x-medusa', 'x-helios',
                             'x-ss-stub', 'x-siren', 'x-tt-token'):
            continue
        out[k] = v
    if restub:
        out['X-SS-STUB'] = hashlib.md5(body_gz).hexdigest().upper()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('sample')
    ap.add_argument('--restub', action='store_true')
    ap.add_argument('--no-sig', action='store_true')
    ap.add_argument('--host', default='https://api5-normal-sinfonlineb.fqnovel.com')
    args = ap.parse_args()

    method, target, headers, body_txt = load(Path(args.sample))
    body_raw = body_txt.encode()
    body_gz = gzip.compress(body_raw, mtime=0)
    hdrs = build_headers(headers, restub=args.restub, no_sig=args.no_sig, body_gz=body_gz)
    hdrs['Content-Encoding'] = 'gzip'
    print(f'{method} {args.host}{target[:120]}…')
    print('body', len(body_raw), '→ gzip', len(body_gz))
    print('headers:', ', '.join(sorted(hdrs)))
    t0 = time.time()
    r = requests.request(method, args.host + target, headers=hdrs, data=body_gz, timeout=30)
    print(f'HTTP {r.status_code}  {len(r.content)} 字节  {time.time() - t0:.2f}s')
    print('resp headers:', {k: v for k, v in r.headers.items() if k.lower().startswith(('x-tt', 'content-type'))})
    try:
        j = r.json()
        print('JSON keys:', list(j.keys()))
        print(json.dumps(j, ensure_ascii=False)[:800])
        if isinstance(j.get('data'), dict):
            for vid, v in list(j['data'].items())[:2]:
                vm = v.get('video_model')
                if isinstance(vm, str):
                    vm = json.loads(vm)
                print(f'  {vid}: height={v.get("video_height")} urls={len((vm or {}).get("video_list") or [])}')
    except Exception as e:
        print('非 JSON:', e, r.text[:400])


if __name__ == '__main__':
    main()
