"""证伪「服务端限流」：不做任何间隔、不做退避，背靠背连打 ``video_model``。

背景（重要）
------------
历史上至少**三次**把「HTTP 200 + 空 body」归因为「服务端限流 / 账号级风控」：

* 2026-11-13 上午：L/N/O/Q 章 → 判为限流，实际是**用错签名函数**
  （``sign_request`` 带 ``x-medusa`` 打播放接口，服务端直接丢）；R 章推翻。
* 2026-11-13 晚：X6 章又判为「账号级风控」，做了 7 轮退避（累计约 19 分钟）
  **全部未恢复**；Z 章查明是 C# 的 ``x-argus`` 移植 bug。
* 2026-11-14：AC2 章再次确认「空 body 是症状，不是根因」，并明确
  **不要**写成「服务端限流」。

每次走错路的代价都是几十分钟的退避实验 + 把排查引向服务端状态。
本脚本给出**可复现的密度实验**，用来一次性回答「到底有没有限流」：

* 全部成功  ⇒ 该密度下不存在所谓限流；
* 出现空 body 且**与间隔无关**（间隔 0 与间隔 60s 表现一样）
  ⇒ 这是**网关丢请求**（fqnovel 那组网关的固有行为），不是频率限制；
* 只有「密度相关、间隔拉长就恢复」才叫限流 —— 历史上**从未观察到**这种模式。

用法::

    python -m tools.rate_probe 7662736695145548824 --vtype 0 -n 30
    python -m tools.rate_probe 7700851630874315800 --vtype 1 -n 30 --gap 0
    python -m tools.rate_probe 7700851630874315800 --vtype 1 -n 5  --gap 60

``--gap`` 是两次请求之间的等待秒数（默认 0 = 背靠背）。
想验证「网关属性」时加 ``--all-hosts``：每一发都同时打所有网关，
若某个网关恒空、另一个恒通，就证明空 body 是**网关**的属性，与频率无关。
"""

from __future__ import annotations

import argparse
import statistics
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

from hgvd.api.playback import (  # noqa: E402
    _post_playback,
    build_playback_body,
    parse_streams,
    playback_hosts,
)
from hgvd.service import HongguoService  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description="video_model 密度实验（证伪限流）")
    ap.add_argument("video_id", help="短剧 vid 或教学 series_id")
    ap.add_argument("--vtype", type=int, default=0, choices=(0, 1),
                    help="0=vid（短剧） 1=series_id（教学）")
    ap.add_argument("-n", "--count", type=int, default=30, help="请求次数")
    ap.add_argument("--gap", type=float, default=0.0, help="两次请求间隔秒数")
    ap.add_argument("--all-hosts", action="store_true",
                    help="每一发都打所有网关，用于验证「空 body 是网关属性」")
    ap.add_argument("--timeout", type=int, default=25)
    args = ap.parse_args()

    svc = HongguoService()
    if not svc.logged_in:
        print("未登录：先 `python -m hgvd login`", file=sys.stderr)
        return 2
    ident = svc.device_identity()
    cookies = svc.cookie_dict()
    hosts = playback_hosts()

    print(f"video_id={args.video_id}  vtype={args.vtype}  "
          f"n={args.count}  gap={args.gap}s")
    print(f"device_id={ident.device_id}  iid={ident.iid}  "
          f"sessionid={'有' if cookies.get('sessionid') else '无'}")
    print(f"hosts={', '.join(hosts)}")
    print("-" * 78)

    body = build_playback_body(args.video_id, video_id_type=args.vtype)
    ok = empty = other = 0
    codes: dict[str, int] = {}
    per_host: dict[str, dict[str, int]] = {h: {"ok": 0, "empty": 0, "other": 0}
                                           for h in hosts}
    latencies: list[float] = []
    first_empty_at: int | None = None

    for i in range(1, args.count + 1):
        for host in hosts if args.all_hosts else hosts[:1]:
            t0 = time.perf_counter()
            payload, err = _post_playback(
                svc.session, ident, body, cookies, host, args.timeout)
            ms = (time.perf_counter() - t0) * 1000
            latencies.append(ms)

            if payload is not None:
                code = payload.get("code")
                codes[str(code)] = codes.get(str(code), 0) + 1
                streams = parse_streams(payload)
                per_host[host]["ok"] += 1
                if code in (0, None) and streams:
                    ok += 1
                    print(f"[{i:03d}] {host:<40} OK    "
                          f"code={code} streams={len(streams)} {ms:.0f}ms")
                else:
                    other += 1
                    print(f"[{i:03d}] {host:<40} JSON  "
                          f"code={code} streams={len(streams)} {ms:.0f}ms")
            elif "空 body" in err:
                empty += 1
                per_host[host]["empty"] += 1
                first_empty_at = first_empty_at or i
                print(f"[{i:03d}] {host:<40} EMPTY {ms:.0f}ms")
            else:
                other += 1
                per_host[host]["other"] += 1
                print(f"[{i:03d}] {host:<40} ERR   {err[:70]} {ms:.0f}ms")

        if args.gap and i < args.count:
            time.sleep(args.gap)

    print("-" * 78)
    print(f"成功 {ok} / 空 body {empty} / 其他 {other}（共 {args.count} 发）")
    if codes:
        print("业务码分布：" + "  ".join(f"{k}×{v}" for k, v in sorted(codes.items())))
    if latencies:
        print(f"耗时 中位 {statistics.median(latencies):.0f}ms  "
              f"最大 {max(latencies):.0f}ms")
    if args.all_hosts:
        for host, c in per_host.items():
            print(f"  {host:<40} ok={c['ok']} empty={c['empty']} other={c['other']}")

    print("-" * 78)
    if empty == 0:
        print("结论：该密度下**没有**任何限流迹象（服务端全程正常受理）。")
        return 0
    if args.all_hosts and any(c["empty"] == args.count for c in per_host.values()) \
            and any(c["ok"] == args.count for c in per_host.values()):
        print("结论：空 body 只出现在**特定网关**上，同刻另一网关全通")
        print("      ⇒ 是**网关丢请求**，与请求频率无关，不是限流。")
        return 0
    print(f"结论：出现 {empty} 次空 body（首发于第 {first_empty_at} 发）。")
    print("      若把 --gap 拉到 60s 重跑仍然空，则**不是限流**（限流必然随间隔恢复）。")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
