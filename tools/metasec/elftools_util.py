"""最小 ELF(ARM64) 分析工具：地址映射、重定位表、反汇编辅助。"""
from elftools.elf.elffile import ELFFile
from elftools.elf.relocation import RelocationSection
import capstone


class Blob:
    def __init__(self, path):
        self.path = path
        self.data = open(path, 'rb').read()
        self.elf = ELFFile(open(path, 'rb'))
        self.sections = []
        for s in self.elf.iter_sections():
            self.sections.append((s.name, s['sh_addr'], s['sh_offset'], s['sh_size'], s['sh_type']))
        # 用于区间查找的 LOAD 段
        self.loads = []
        for seg in self.elf.iter_segments():
            if seg['p_type'] == 'PT_LOAD':
                self.loads.append((seg['p_vaddr'], seg['p_vaddr'] + seg['p_filesz'], seg['p_offset']))
        self.relocs = self._load_relocs()

    def _load_relocs(self):
        """返回 {目标地址: ('RELATIVE', addend) 或 ('SYM', name)}"""
        out = {}
        for sec in self.elf.iter_sections():
            if not isinstance(sec, RelocationSection):
                continue
            symtab = self.elf.get_section(sec['sh_link'])
            for r in sec.iter_relocations():
                off = r['r_offset']
                t = r['r_info_type']
                sym = symtab.get_symbol(r['r_info_sym']) if r['r_info_sym'] else None
                name = sym.name if sym is not None and sym.name else None
                if t == 1027:   # R_AARCH64_RELATIVE
                    out[off] = ('RELATIVE', r['r_addend'], sec.name)
                elif t == 257:  # R_AARCH64_ABS64
                    out[off] = ('ABS64', r['r_addend'], name)
                elif name:
                    out[off] = ('SYM', name, sec.name)
                else:
                    out[off] = ('TYPE', t, sec.name)
        return out

    def off2va(self, off):
        for va, end, poff in self.loads:
            if poff <= off < poff + (end - va):
                return va + (off - poff)
        return None

    def va2off(self, va):
        for start, end, poff in self.loads:
            if start <= va < end:
                return poff + (va - start)
        return None

    def read(self, va, n):
        off = self.va2off(va)
        if off is None:
            return b''
        return self.data[off:off + n]

    def str_at(self, va, limit=400):
        b = self.read(va, limit)
        z = b.find(b'\0')
        if z >= 0:
            b = b[:z]
        return b.decode('utf-8', 'replace')

    def section_of(self, va):
        for name, addr, off, size, typ in self.sections:
            if size and addr <= va < addr + size:
                return name
        return None


def disasm(blob, start, size, count=None):
    return list(disasm_iter(blob, start, size, count))


def disasm_iter(blob, start, size, count=None):
    md = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_ARM)
    md.detail = True
    off = blob.va2off(start)
    if off is None:
        return
    code = blob.data[off:off + size]
    n = 0
    for ins in md.disasm(code, start):
        yield ins
        n += 1
        if count and n >= count:
            break
