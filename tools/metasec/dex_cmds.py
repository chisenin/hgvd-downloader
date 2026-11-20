"""从 dexdump 反汇编里抽 `Lms/bd/c/k3;.a(IIJLjava/lang/String;Ljava/lang/Object;)` 的调用表。

原生入口只有一个（`MSManager.a`），所有能力都靠第一个 int 参数（cmd）分派。
Java 侧 k3.a(cmd, arg2, long, str, obj) 是唯一桥，所以把所有调用点的常量读出来
= 直接得到 SDK 的命令表，并且能看到是谁在用（外层类#方法）。

用法::

    dexdump -d classes22.dex > dump.txt
    python tools/metasec/dex_cmds.py dump.txt > cmds.txt
"""
from __future__ import annotations

import re
import sys

CALL = 'Lms/bd/c/k3;.a:(IIJLjava/lang/String;Ljava/lang/Object;)'
# 形如: |0014: const v2, #float 2.35e-38 // #01000001
INSN = re.compile(r'^\s*[0-9a-f]+:\s+[0-9a-f ]+\|([0-9a-f]{4}):\s+(.*?)\s*(?://\s*(.*))?$')
HEXCOMMENT = re.compile(r'#([0-9a-fA-F]+)')
STR = re.compile(r'"(.*)"')


def main(path):
    cur_cls = cur_m = ''
    regs: dict[str, tuple[str, str]] = {}
    rows = []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            s = line.strip()
            if s.startswith('Class descriptor'):
                cur_cls = s.split("'")[1] if "'" in s else ''
                continue
            if s.startswith('name') and ':' in s and "'" in s:
                cur_m = s.split("'")[1]
                regs = {}
                continue
            if s.startswith('type') and "'" in s:
                cur_m += s.split("'")[1]
                continue
            m = INSN.match(line)
            if not m:
                continue
            body, comment = m.group(2), m.group(3) or ''
            if body.startswith('const'):
                rm = re.match(r'(const[^ ]*)\s+(v\d+),\s*(.*)', body)
                if not rm:
                    continue
                kind, reg, operand = rm.groups()
                if 'string' in kind:
                    sm = STR.search(operand)
                    regs[reg] = ('str', sm.group(1) if sm else '')
                else:
                    hm = HEXCOMMENT.search(comment)
                    if hm:
                        regs[reg] = ('int', hm.group(1))
                    else:
                        im = re.search(r'#int (-?\d+)', operand)
                        regs[reg] = ('int', im.group(1) if im else operand)
                continue
            if body.startswith('move') or body.startswith('iget') or body.startswith('sget') or body.startswith('new-'):
                rm = re.match(r'[^ ]+\s+(v\d+),', body)
                if rm:
                    regs.pop(rm.group(1), None)
                continue
            if CALL in body:
                rm = re.search(r'\{(v\d+)(?:, (v\d+))?(?:, (v\d+), (v\d+))?, (v\d+), (v\d+)\}', body)
                if not rm:
                    rm2 = re.search(r'\{(.*?)\}', body)
                    if not rm2:
                        continue
                    regs_in = [x.strip() for x in rm2.group(1).split(',')]
                else:
                    regs_in = [g for g in rm.groups() if g]
                vals = [regs.get(r, ('?', '?')) for r in regs_in]
                cmd = vals[0] if vals else ('?', '?')
                rows.append((cur_cls, cur_m, cmd[1], vals))
    print(f'# 共 {len(rows)} 个 k3.a 调用点')
    for cls, meth, cmd, vals in rows:
        desc = ' '.join(f'{v[0]}:{v[1]}' for v in vals[1:])
        print(f'{cmd:>10s}  {cls.split("/")[-1][:-1]:28s} {meth:34s} {desc}')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '_ref/metasec/dump22.txt')
