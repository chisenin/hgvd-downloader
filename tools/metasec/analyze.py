"""libmetasec_ml.so 静态分析小工具（ARM64 / capstone）。

用法::

    python tools/metasec/analyze.py info
    python tools/metasec/analyze.py strings argus
    python tools/metasec/analyze.py xref 0x3ecd28
    python tools/metasec/analyze.py func 0x2a5194 60

★ 关键坑：capstone 的 Cs.disasm() 遇到无法解码的字节会**直接停止**，
   所以必须开 skipdata=True 才能线性扫完整段 .text（否则只扫到第一处数据就结束，
   表现为「明明有引用却 xref 不到」）。
"""
from __future__ import annotations

import re
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import capstone
from elftools_util import Blob

SO = Path(__file__).resolve().parents[2] / '_ref' / 'metasec' / 'libmetasec_ml.so'
MD = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_ARM)
MD.detail = True
MD.skipdata = True
A64_REG = capstone.arm64.ARM64_OP_REG
A64_IMM = capstone.arm64.ARM64_OP_IMM
A64_MEM = capstone.arm64.ARM64_OP_MEM


def text_range(blob):
    for name, addr, off, size, typ in blob.sections:
        if name == '.text':
            return addr, off, size
    raise SystemExit('no .text')


def iter_text(blob):
    addr, off, size = text_range(blob)
    return MD.disasm(blob.data[off:off + size], addr)


def describe(blob, value):
    """把一个地址尽量解释成人能看懂的东西。"""
    sec = blob.section_of(value)
    if sec == '.rodata':
        s = blob.str_at(value, 80)
        if s and all(32 <= ord(c) < 127 for c in s):
            return f'.rodata "{s}"'
        return f'.rodata {blob.read(value, 16).hex()}'
    if sec == '.text':
        return '.text'
    if sec in ('.data', '.data.rel.ro', '.bss'):
        k = blob.relocs.get(value)
        if k and k[0] == 'RELATIVE':
            return f'{sec} -> {k[1]:#x} ({blob.section_of(k[1])})'
        return f'{sec} {blob.read(value, 16).hex() if sec != ".bss" else ""}'
    if sec is None:
        if value in blob.relocs:
            return f'reloc {blob.relocs[value]}'
        return '外部/未映射'
    return sec


def page_tracking_xrefs(blob, wanted: set[int], *, include_mem=True):
    """线性扫描 .text，跟踪 adrp(+add) 建立起来的寄存器页基址，找出引用。

    返回 [(pc, kind, value, extra)]。会跟踪寄存器→常量地址的传播（add/ldr）；
    遇到 bl/blr/ret 清空（简单但够用的保守策略）。
    """
    pages: dict[str, tuple[int, int]] = {}
    hits = []
    for ins in iter_text(blob):
        if ins.id == 0:      # skipdata 伪指令（.byte）
            continue
        ops = ins.operands
        if ins.mnemonic == 'adrp':
            pages[ins.reg_name(ops[0].reg)] = (ins.address, ops[1].imm)
            continue
        if ins.mnemonic == 'add' and len(ops) == 3 and ops[1].type == A64_REG and ops[2].type == A64_IMM:
            rn = ins.reg_name(ops[1].reg)
            if rn in pages:
                val = pages[rn][1] + ops[2].imm
                pages[ins.reg_name(ops[0].reg)] = (ins.address, val)
                if val in wanted:
                    hits.append((ins.address, 'adrp+add', val, pages[rn][0]))
        elif include_mem and ins.mnemonic in ('ldr', 'ldur', 'str', 'ldrb', 'ldrh', 'ldrsw') and len(ops) >= 2:
            if ops[1].type == A64_MEM:
                m = ops[1].mem
                if m.base:
                    rn = ins.reg_name(m.base)
                    if rn in pages:
                        slot = pages[rn][1] + m.disp
                        if slot in wanted:
                            hits.append((ins.address, ins.mnemonic, slot, pages[rn][0]))
        if ins.mnemonic in ('bl', 'blr', 'ret'):
            pages.clear()
    return hits


def cmd_info(blob):
    print(f'{SO.name}: {len(blob.data):,} 字节')
    for name, addr, off, size, typ in blob.sections:
        if size:
            print(f'  {name:20s} va={addr:#010x} off={off:#09x} size={size:#09x}')
    print(f'重定位 {len(blob.relocs)} 条')


def cmd_strings(blob, pat):
    rx = re.compile(pat.encode(), re.I)
    n = 0
    for m in re.finditer(rb'[\x20-\x7e]{4,}', blob.data):
        s = m.group()
        if rx.search(s):
            va = blob.off2va(m.start())
            print(f'{va:#010x} (off {m.start():#09x}) {s[:160].decode()}')
            n += 1
    print(f'-- {n} 条')


def cmd_xref(blob, target):
    hits = page_tracking_xrefs(blob, {target})
    print(f'{target:#x} => {describe(blob, target)}')
    for pc, kind, val, src in hits:
        print(f'  {pc:#08x}  {kind:9s} base@{src:#x}')
    print(f'-- {len(hits)} 处')


def cmd_func(blob, start, count):
    addr, off, size = text_range(blob)
    pend = {}
    for i, ins in enumerate(MD.disasm(blob.data[blob.va2off(start):blob.va2off(start) + count * 4], start)):
        if ins.id == 0:
            print(f'{ins.address:#08x}  .byte     {ins.op_str}')
            continue
        note = ''
        ops = ins.operands
        if ins.mnemonic == 'adrp':
            pend[ins.reg_name(ops[0].reg)] = ops[1].imm
        elif ins.mnemonic == 'add' and len(ops) == 3 and ops[1].type == A64_REG and ops[2].type == A64_IMM:
            rn = ins.reg_name(ops[1].reg)
            if rn in pend:
                note = '  ; ' + describe(blob, pend[rn] + ops[2].imm)
        elif ins.mnemonic in ('ldr', 'ldur') and len(ops) >= 2 and ops[1].type == A64_MEM:
            m = ops[1].mem
            if m.base:
                rn = ins.reg_name(m.base)
                if rn in pend:
                    slot = pend[rn] + m.disp
                    r = blob.relocs.get(slot)
                    if r and r[0] == 'RELATIVE':
                        note = f'  ; [{slot:#x}] -> {r[1]:#x} ({blob.section_of(r[1])})'
                    elif r and r[0] == 'SYM':
                        note = f'  ; [{slot:#x}] -> import {r[1]}'
                    else:
                        note = f'  ; [{slot:#x}]'
        print(f'{ins.address:#08x}  {ins.mnemonic:9s} {ins.op_str}{note}')


def main():
    if not SO.exists():
        raise SystemExit(f'找不到 {SO}')
    blob = Blob(str(SO))
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    cmd = sys.argv[1]
    if cmd == 'info':
        cmd_info(blob)
    elif cmd == 'strings':
        cmd_strings(blob, sys.argv[2] if len(sys.argv) > 2 else '.')
    elif cmd == 'xref':
        cmd_xref(blob, int(sys.argv[2], 16))
    elif cmd == 'func':
        cmd_func(blob, int(sys.argv[2], 16), int(sys.argv[3]) if len(sys.argv) > 3 else 40)
    else:
        raise SystemExit(__doc__)


if __name__ == '__main__':
    main()
