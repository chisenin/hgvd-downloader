"""最小 MITM 代理：抓 App 的真实请求头 + body（定位 x-argus 是否需要真值）。

设计取舍：
  · 对客户端只宣告 http/1.1（ALPN），省掉 h2 帧解析；
  · 每连接一轮请求（向两端都发 Connection: close），够抓样本；
  · 叶子证书按域名即时签发并缓存，全部由本地 CA 签；
  · body 默认只记关键接口（video_model / multi_video / album_detail / bookapi），
    可用 --body-all 全记。

用法::

    python tools/metasec/mitm.py --port 8899
    # CA 要装进 **Conscrypt APEX** 的 cacerts（见 docs/05 §2.1），不是 /system/etc
"""
from __future__ import annotations

import argparse
import base64
import itertools
import datetime as dt
import gzip
import ipaddress
import json
import socket
import ssl
import threading
import time
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

CA_DIR = Path('_ref/metasec/ca')
LOG = Path('_ref/metasec/mitm.log')
_lock = threading.Lock()
_leaf_cache: dict[str, tuple[bytes, bytes]] = {}
_ca_key = None
_ca_cert = None
BODY_ALL = False
RAW_DIR = Path('_ref/metasec/raw')   # --raw：把**未经改动的原始字节**落盘，供逐字节重放
BODY_KEYS = ('video_model', 'multi_video', 'album_detail', 'bookapi', 'video_detail')
_rid = itertools.count(1)
BODY_DIR = Path('_ref/metasec/bodies')


def log(msg: str):
    line = f'[{time.strftime("%H:%M:%S")}] {msg}'
    print(line, flush=True)
    with _lock:
        with LOG.open('a', encoding='utf-8') as f:
            f.write(line + '\n')


def ensure_ca():
    global _ca_key, _ca_cert
    CA_DIR.mkdir(parents=True, exist_ok=True)
    key_p = CA_DIR / 'ca.key'
    crt_p = CA_DIR / 'ca.crt'
    if key_p.exists() and crt_p.exists():
        _ca_key = serialization.load_pem_private_key(key_p.read_bytes(), None)
        _ca_cert = x509.load_pem_x509_certificate(crt_p.read_bytes())
        return
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'hgvd metasec lab CA'),
                      x509.NameAttribute(NameOID.ORGANIZATION_NAME, 'hgvd')])
    now = dt.datetime.now(dt.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
            .public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - dt.timedelta(days=1))
            .not_valid_after(now + dt.timedelta(days=3650))
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .sign(key, hashes.SHA256()))
    key_p.write_bytes(key.private_bytes(serialization.Encoding.PEM,
                                        serialization.PrivateFormat.TraditionalOpenSSL,
                                        serialization.NoEncryption()))
    crt_p.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    _ca_key, _ca_cert = key, cert


def subject_hash_old(cert: x509.Certificate) -> str:
    import hashlib
    return f'{int.from_bytes(hashlib.md5(cert.subject.public_bytes()).digest()[:4], "little"):08x}.0'


def leaf_for(host: str) -> tuple[bytes, bytes]:
    if host in _leaf_cache:
        return _leaf_cache[host]
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    try:
        san = x509.IPAddress(ipaddress.ip_address(host))
    except ValueError:
        san = x509.DNSName(host)
    now = dt.datetime.now(dt.timezone.utc)
    cert = (x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, host)]))
            .issuer_name(_ca_cert.subject).public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - dt.timedelta(days=2))
            .not_valid_after(now + dt.timedelta(days=365))
            .add_extension(x509.SubjectAlternativeName([san]), critical=False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .sign(_ca_key, hashes.SHA256()))
    out = (cert.public_bytes(serialization.Encoding.PEM),
           key.private_bytes(serialization.Encoding.PEM,
                             serialization.PrivateFormat.TraditionalOpenSSL,
                             serialization.NoEncryption()))
    _leaf_cache[host] = out
    return out


_TMP = Path('_ref/metasec/.tmp')
_TMP.mkdir(parents=True, exist_ok=True)


def _write_tmp(data: bytes, suffix: str) -> str:
    p = _TMP / f'tls{threading.get_ident()}{suffix}'
    p.write_bytes(data)
    return str(p)


def read_until(sock: socket.socket, marker: bytes, limit=0x80000) -> bytes:
    buf = b''
    while marker not in buf and len(buf) < limit:
        chunk = sock.recv(8192)
        if not chunk:
            break
        buf += chunk
    return buf


def show_body(tag: str, body: bytes, hdrs: list[str]):
    if not body:
        return
    enc = ''
    for h in hdrs:
        if h.lower().startswith('content-encoding:'):
            enc = h.split(':', 1)[1].strip().lower()
    raw = body
    if enc == 'gzip':
        try:
            raw = gzip.decompress(body)
        except Exception:
            raw = body
    text = ''
    try:
        text = json.dumps(json.loads(raw.decode('utf-8')), ensure_ascii=False)[:1200]
    except Exception:
        try:
            text = raw.decode('utf-8')[:600]
        except Exception:
            text = ''
    log(f'  {tag} body {len(body)}B (解压后 {len(raw)}B)')
    if text:
        log(f'    {text}')
    else:
        log('    b64:' + base64.b64encode(body[:768]).decode())


def dechunk(data: bytes) -> bytes:
    out = b''
    while data:
        i = data.find(b'\r\n')
        if i < 0:
            break
        try:
            n = int(data[:i].split(b';')[0], 16)
        except ValueError:
            break
        if n == 0:
            break
        out += data[i + 2:i + 2 + n]
        data = data[i + 2 + n + 2:]
    return out or data


def preview(body: bytes, hdrs: list[str]) -> str:
    if not body:
        return ''
    if any(h.lower().startswith('transfer-encoding:') and 'chunked' in h.lower() for h in hdrs):
        body = dechunk(body)
    enc = ''
    for h in hdrs:
        if h.lower().startswith('content-encoding:'):
            enc = h.split(':', 1)[1].strip().lower()
    raw = body
    if enc == 'gzip':
        try:
            raw = gzip.decompress(body)
        except Exception:
            pass
    if raw[:1] in (b'{', b'['):
        return raw.decode('utf-8', 'replace')
    return 'HEX ' + body[:64].hex()


def handle(conn: socket.socket, addr):
    try:
        conn.settimeout(25)
        raw = read_until(conn, b'\r\n\r\n')
        if not raw:
            return
        head, _, rest = raw.partition(b'\r\n\r\n')
        first = head.split(b'\r\n')[0].decode('latin1')
        if first.startswith('CONNECT'):
            hostport = first.split()[1]
            host, _, port = hostport.partition(':')
            port = int(port or 443)
            conn.sendall(b'HTTP/1.1 200 Connection established\r\n\r\n')
            cert_pem, key_pem = leaf_for(host)
            chain = cert_pem + _ca_cert.public_bytes(serialization.Encoding.PEM)
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ctx.load_cert_chain(_write_tmp(chain, '.crt'), _write_tmp(key_pem, '.key'))
            ctx.set_alpn_protocols(['http/1.1'])
            tls = ctx.wrap_socket(conn, server_side=True)
            req = rest + read_until(tls, b'\r\n\r\n')
            if not req:
                return
            do_request(tls, addr, host, port, req, tls=True)
        else:
            host = ''
            for ln in head.split(b'\r\n')[1:]:
                if ln.lower().startswith(b'host:'):
                    host = ln.split(b':', 1)[1].strip().decode()
            if host:
                do_request(conn, addr, host, 80, raw, tls=False)
    except Exception as e:
        log(f'!! {addr} {type(e).__name__}: {e}')
    finally:
        try:
            conn.close()
        except Exception:
            pass


def do_request(client: socket.socket, addr, host: str, port: int, req: bytes, tls=True):
    rid = next(_rid)
    dump: list[str] = []
    head, _, rest = req.partition(b'\r\n\r\n')
    lines = head.split(b'\r\n')
    first = lines[0].decode('latin1')
    hdrs = [ln.decode('latin1') for ln in lines[1:] if ln]
    drop = ('proxy-connection', 'proxy-authorization', 'connection:')
    hdrs = [h for h in hdrs if not h.lower().startswith(drop)]

    clen = 0
    for h in hdrs:
        if h.lower().startswith('content-length:'):
            try:
                clen = int(h.split(':', 1)[1].strip())
            except Exception:
                clen = 0
    body = rest
    while len(body) < clen:
        chunk = client.recv(65536)
        if not chunk:
            break
        body += chunk

    interesting = [h for h in hdrs if h.split(':')[0].lower().startswith(('x-', 'sdk-', 'lc'))]
    want_body = BODY_ALL or any(k in first for k in BODY_KEYS)
    if want_body or interesting:
        log('=' * 72 + f' #{rid}')
        log(f'{first}   [host={host}:{port}]'[:400])
        for h in hdrs:
            log(f'  {h}'[:400])
        if want_body and body:
            show_body('>>', body, hdrs)
            dump.append('REQ ' + first)
            dump.extend(hdrs)
            dump.append('')
            dump.append(preview(body, hdrs))

    if RAW_DIR is not None and want_body and body:
        try:
            RAW_DIR.mkdir(parents=True, exist_ok=True)
            safe = first.split(' ')[1][:90].replace('/', '_').replace('?', '_').replace('&', '_')
            (RAW_DIR / f'{rid:04d}-{host}{safe}.bin').write_bytes(req[:len(req) - len(rest)] + body)
        except Exception as e:
            log(f'raw dump failed: {e}')

    out = ('\r\n'.join([first] + hdrs + ['Connection: close']) + '\r\n\r\n').encode('latin1')
    up = socket.create_connection((host, port), timeout=20)
    if tls:
        cctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        cctx.check_hostname = False
        cctx.verify_mode = ssl.CERT_NONE
        cctx.set_alpn_protocols(['http/1.1'])
        up = cctx.wrap_socket(up, server_hostname=host)
    up.sendall(out + body)
    resp = b''
    while True:
        try:
            chunk = up.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        resp += chunk
        client.sendall(chunk)
    up.close()
    status = resp.split(b'\r\n', 1)[0].decode('latin1', 'replace') if resp else '(空响应)'
    if want_body:
        rh, _, rb = resp.partition(b'\r\n\r\n')
        rhdrs = [x.decode('latin1') for x in rh.split(b'\r\n')[1:] if x]
        log(f'<- {status}  {len(resp)} 字节')
        if rb:
            show_body('<<', rb, rhdrs)
        dump.append('')
        dump.append('RESP ' + status)
        dump.extend(rhdrs)
        dump.append('')
        dump.append(preview(rb, rhdrs))
        BODY_DIR.mkdir(parents=True, exist_ok=True)
        name = f'{rid:04d}-{host}-{first.split()[1][:40]}'.replace('/', '_').replace('?', '_')
        (BODY_DIR / (name + '.txt')).write_text('\n'.join(dump), encoding='utf-8')


def main():
    global BODY_ALL
    ap = argparse.ArgumentParser()
    ap.add_argument('--port', type=int, default=8899)
    ap.add_argument('--bind', default='0.0.0.0')
    ap.add_argument('--body-all', action='store_true')
    ap.add_argument('--raw', action='store_true', help='把原始请求字节落到 _ref/metasec/raw/')
    args = ap.parse_args()
    global RAW_DIR
    if not args.raw:
        RAW_DIR = None
    BODY_ALL = args.body_all
    ensure_ca()
    log(f'CA subject_hash_old = {subject_hash_old(_ca_cert)}')
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((args.bind, args.port))
    s.listen(50)
    log(f'监听 {args.bind}:{args.port}  body_all={BODY_ALL}')
    while True:
        c, a = s.accept()
        threading.Thread(target=handle, args=(c, a), daemon=True).start()


if __name__ == '__main__':
    main()
