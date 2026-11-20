"""用 Unicorn 在 x86 上跑起 libmetasec_ml.so（Android arm64）。

目标：不反编译也能观察真实行为 —— 先跑 init_array，再跑 JNI_OnLoad，
把所有 JNI 调用、所有 import 调用打印出来，最终拿到 RegisterNatives 的原生方法表。

用法::

    python tools/metasec/emu.py                 # 跑 init_array + JNI_OnLoad
    python tools/metasec/emu.py --trace         # additionally 打印每条 import 调用

★ 设计要点
  · ELF 按 vaddr 映射到 0 基址（Unicorn 不要求真实随机基址）；
  · 所有 import 写进 GOT，指向自己的 stub 页，hook 后由 Python 实现；
  · JNIEnv / JavaVM 用内存里的函数表 + stub 页伪造，表项索引即 JNI 函数号；
  · 调用约定：hook 命中即视为「函数被调用」，处理后 PC = x30 返回。
"""
from __future__ import annotations

import argparse
import struct
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

from unicorn import Uc, UC_ARCH_ARM64, UC_MODE_ARM, UC_HOOK_CODE, UC_HOOK_MEM_UNMAPPED, UC_PROT_ALL
from unicorn import arm64_const as a64

sys.path.insert(0, str(Path(__file__).parent))
from elftools_util import Blob

SO = Path(__file__).resolve().parents[2] / '_ref' / 'metasec' / 'libmetasec_ml.so'

PAGE = 0x1000
STACK = (0x1000_0000, 0x10_0000)
TLS = (0x1100_0000, 0x1000)
HEAP = (0x2000_0000, 0x40_0000)
STUB = (0x3000_0000, 0x1_0000)
ENV_TABLE = (0x3100_0000, 0x2000)
VM_TABLE = (0x3110_0000, 0x2000)
ENV_PTR = 0x3120_0000
VM_PTR = 0x3121_0000
SENTINEL = 0x3FFF_0000

# JNI 函数表里我们关心的索引（JNINativeInterface 顺序）
JNI_INDEX = {
    # JNINativeInterface 真实顺序（0~3 是 4 个 reserved 槽）
    4: 'GetVersion', 5: 'DefineClass', 6: 'FindClass', 7: 'FromReflectedMethod',
    8: 'FromReflectedField', 9: 'ToReflectedMethod', 10: 'GetSuperclass',
    11: 'IsAssignableFrom', 12: 'ToReflectedField', 13: 'Throw', 14: 'ThrowNew',
    15: 'ExceptionOccurred', 16: 'ExceptionDescribe', 17: 'ExceptionClear', 18: 'FatalError',
    19: 'PushLocalFrame', 20: 'PopLocalFrame', 21: 'NewGlobalRef', 22: 'DeleteGlobalRef',
    23: 'DeleteLocalRef', 24: 'IsSameObject', 25: 'NewLocalRef', 26: 'EnsureLocalCapacity',
    27: 'AllocObject', 28: 'NewObject', 29: 'NewObjectV', 30: 'NewObjectA',
    31: 'GetObjectClass', 32: 'IsInstanceOf', 33: 'GetMethodID',
    34: 'CallObjectMethod', 35: 'CallObjectMethodV', 36: 'CallObjectMethodA',
    37: 'CallBooleanMethod', 38: 'CallBooleanMethodV', 39: 'CallBooleanMethodA',
    40: 'CallByteMethod', 41: 'CallByteMethodV', 42: 'CallByteMethodA',
    43: 'CallCharMethod', 44: 'CallCharMethodV', 45: 'CallCharMethodA',
    46: 'CallShortMethod', 47: 'CallShortMethodV', 48: 'CallShortMethodA',
    49: 'CallIntMethod', 50: 'CallIntMethodV', 51: 'CallIntMethodA',
    52: 'CallLongMethod', 53: 'CallLongMethodV', 54: 'CallLongMethodA',
    55: 'CallFloatMethod', 56: 'CallFloatMethodV', 57: 'CallFloatMethodA',
    58: 'CallDoubleMethod', 59: 'CallDoubleMethodV', 60: 'CallDoubleMethodA',
    61: 'CallVoidMethod', 62: 'CallVoidMethodV', 63: 'CallVoidMethodA',
    64: 'CallNonvirtualObjectMethod', 65: 'CallNonvirtualObjectMethodV', 66: 'CallNonvirtualObjectMethodA',
    112: 'GetFieldID', 113: 'GetObjectField', 115: 'GetBooleanField', 116: 'GetByteField',
    117: 'GetCharField', 118: 'GetShortField', 119: 'GetIntField', 120: 'GetLongField',
    121: 'GetFloatField', 122: 'GetDoubleField', 131: 'SetIntField', 132: 'SetLongField',
    143: 'GetStaticMethodID', 144: 'CallStaticObjectMethod', 145: 'CallStaticObjectMethodV',
    146: 'CallStaticObjectMethodA', 147: 'CallStaticBooleanMethod', 149: 'CallStaticIntMethod',
    151: 'CallStaticLongMethod', 154: 'CallStaticVoidMethod',
    162: 'GetStaticFieldID', 166: 'GetStaticObjectField', 167: 'GetStaticBooleanField',
    168: 'GetStaticByteField', 170: 'GetStaticShortField', 172: 'GetStaticIntField',
    173: 'GetStaticLongField',
    183: 'NewString', 184: 'GetStringLength', 185: 'GetStringChars', 186: 'ReleaseStringChars',
    187: 'NewStringUTF', 188: 'GetStringUTFLength', 189: 'GetStringUTFChars',
    190: 'ReleaseStringUTFChars', 191: 'GetArrayLength',
    192: 'NewObjectArray', 193: 'GetObjectArrayElement', 194: 'SetObjectArrayElement',
    195: 'NewBooleanArray', 196: 'NewByteArray', 197: 'NewCharArray', 198: 'NewShortArray',
    199: 'NewIntArray', 200: 'NewLongArray', 201: 'NewFloatArray', 202: 'NewDoubleArray',
    203: 'GetBooleanArrayElements', 204: 'GetByteArrayElements', 205: 'GetCharArrayElements',
    206: 'GetShortArrayElements', 207: 'GetIntArrayElements', 208: 'GetLongArrayElements',
    209: 'GetFloatArrayElements', 210: 'GetDoubleArrayElements',
    211: 'ReleaseBooleanArrayElements', 212: 'ReleaseByteArrayElements',
    219: 'GetPrimitiveArrayCritical', 220: 'ReleasePrimitiveArrayCritical',
    221: 'GetStringCritical', 222: 'ReleaseStringCritical',
    223: 'NewWeakGlobalRef', 224: 'DeleteWeakGlobalRef',
    225: 'ExceptionCheck',
    226: 'NewDirectByteBuffer', 227: 'GetDirectBufferAddress', 228: 'GetDirectBufferCapacity',
    229: 'GetObjectRefType',
    230: 'GetModule', 231: 'IsVirtualThread',
    215: 'RegisterNatives', 216: 'UnregisterNatives',
    218: 'MonitorEnter', 219 + 1: 'MonitorExit',
    227 + 1: 'GetJavaVM',
}


VM_INDEX = {3: 'DestroyJavaVM', 4: 'AttachCurrentThread', 5: 'DetachCurrentThread',
            6: 'GetEnv', 7: 'AttachCurrentThreadAsDaemon'}


@dataclass
class Call:
    seq: int
    kind: str       # import | jni | vm
    name: str
    args: list = field(default_factory=list)
    ret: int = 0


class Emu:
    def __init__(self, so: Path, trace=False, verbose=False):
        self.blob = Blob(str(so))
        self.trace = trace
        self.verbose = verbose
        self.uc = Uc(UC_ARCH_ARM64, UC_MODE_ARM)
        self.calls: list[Call] = []
        self.seq = 0
        self.log: list[str] = []
        self.import_names: dict[int, str] = {}
        self.jni_stub_index: dict[int, int] = {}
        self.stub_handlers: dict[int, object] = {}
        self.vm_stub_index: dict[int, int] = {}
        self.stub_cursor = STUB[0]
        self.heap_cursor = HEAP[0]
        self.jni_return_override: dict[int, int] = {}
        self._map_all()
        self._apply_relocs()
        self._setup_jni()
        self._hook_stubs()

    # ---------------------------------------------------------------- 内存
    def _map_all(self):
        uc = self.uc
        # ELF 映像
        lo, hi = 0, 0
        for name, addr, off, size, typ in self.blob.sections:
            if size and typ != 'SHT_NOBITS':
                hi = max(hi, addr + size)
        for seg in self.blob.elf.iter_segments():
            if seg['p_type'] == 'PT_LOAD':
                hi = max(hi, seg['p_vaddr'] + seg['p_memsz'])
        hi = (hi + PAGE - 1) & ~(PAGE - 1)
        uc.mem_map(0, hi, UC_PROT_ALL)
        for seg in self.blob.elf.iter_segments():
            if seg['p_type'] != 'PT_LOAD':
                continue
            data = self.blob.data[seg['p_offset']:seg['p_offset'] + seg['p_filesz']]
            uc.mem_write(seg['p_vaddr'], data)
        # 其它区域
        for (base, size) in (STACK, TLS, HEAP, STUB, ENV_TABLE, VM_TABLE, (ENV_PTR, 0x20000)):
            uc.mem_map(base, size, UC_PROT_ALL)
        uc.mem_map(SENTINEL & ~(PAGE - 1), PAGE, UC_PROT_ALL)
        # 栈
        self.sp = STACK[0] + STACK[1] - 0x100
        uc.reg_write(a64.UC_ARM64_REG_SP, self.sp)
        # TLS：tpidr_el0，[+0x28] 放栈保护哨兵指针
        uc.reg_write(a64.UC_ARM64_REG_TPIDR_EL0, TLS[0])
        uc.mem_write(TLS[0] + 0x28, struct.pack('<Q', TLS[0] + 0x800))
        uc.mem_write(TLS[0] + 0x800, struct.pack('<Q', 0x1111222233334444))
        self._hook_faults()

    def _hook_faults(self):
        def on_unmapped(uc, access, address, size, value, ud):
            pc = uc.reg_read(a64.UC_ARM64_REG_PC)
            lr = uc.reg_read(a64.UC_ARM64_REG_LR)
            sp = uc.reg_read(a64.UC_ARM64_REG_SP)
            self.emit(f'!! 未映射访问 addr={address:#x} size={size} pc={pc:#x} lr={lr:#x} sp={sp:#x}')
            # 反汇编故障点前后
            start = pc - 4 * 6
            for ins in self.disasm(start, 14):
                mark = ' <<< FAULT' if ins.address == pc else ''
                self.emit(f'     {ins.address:#08x}  {ins.mnemonic:9s} {ins.op_str}{mark}')
            # 栈回溯（粗）
            words = self.rd(sp, 0x200)
            cand = [struct.unpack('<Q', words[i:i + 8])[0] for i in range(0, len(words) - 8, 8)]
            cand = [c for c in cand if self.blob.section_of(c) == '.text']
            self.emit('     栈上 .text 返回地址: ' + ', '.join(f'{c:#x}' for c in cand[:8]))
            uc.emu_stop()
            return False
        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED, on_unmapped)

    def _apply_relocs(self):
        uc = self.uc
        for off, info in self.blob.relocs.items():
            kind = info[0]
            if kind == 'RELATIVE':
                uc.mem_write(off, struct.pack('<Q', info[1] & 0xFFFF_FFFF_FFFF_FFFF))
            elif kind == 'ABS64':
                uc.mem_write(off, struct.pack('<Q', info[1] & 0xFFFF_FFFF_FFFF_FFFF))
        # import 槽 → stub
        for off, info in self.blob.relocs.items():
            if info[0] == 'SYM':
                name = info[1]
                stub = self.new_stub()
                self.import_names[stub] = name
                uc.mem_write(off, struct.pack('<Q', stub))

    # ---------------------------------------------------------------- stub
    def new_stub(self):
        addr = self.stub_cursor
        self.stub_cursor += 8
        self.uc.mem_write(addr, b'\xc0\x03\x5f\xd6' * 2)   # ret; ret
        return addr

    def _setup_jni(self):
        uc = self.uc
        self.new_stub()   # 0 号占位
        env_table = ENV_TABLE[0]
        for idx in range(240):
            stub = self.new_stub()
            self.jni_stub_index[stub] = idx
            uc.mem_write(env_table + idx * 8, struct.pack('<Q', stub))
        uc.mem_write(ENV_PTR, struct.pack('<Q', env_table))
        vm_table = VM_TABLE[0]
        for idx in range(8):
            stub = self.new_stub()
            self.vm_stub_index[stub] = idx
            uc.mem_write(vm_table + idx * 8, struct.pack('<Q', stub))
        uc.mem_write(VM_PTR, struct.pack('<Q', vm_table))

    def _hook_stubs(self):
        def on_code(uc, address, size, ud):
            if address in self.stub_handlers:
                self.stub_handlers.pop(address)()
            elif address in self.import_names:
                self.on_import(self.import_names[address])
            elif address in self.jni_stub_index:
                idx = self.jni_stub_index[address]
                self.on_jni(idx, JNI_INDEX.get(idx, f'#{idx}'))
            elif address in self.vm_stub_index:
                idx = self.vm_stub_index[address]
                self.on_vm(idx, VM_INDEX.get(idx, f'#{idx}'))
        self.uc.hook_add(UC_HOOK_CODE, on_code, begin=STUB[0], end=STUB[0] + STUB[1])

    # ---------------------------------------------------------------- 工具
    def disasm(self, start, count):
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_ARM)
        md.skipdata = True
        off = self.blob.va2off(start)
        if off is None:
            return []
        return list(md.disasm(self.blob.data[off:off + count * 4], start))[:count]

    def emit(self, msg):
        self.log.append(msg)
        if self.verbose or self.trace:
            print(msg, flush=True)

    def rd(self, addr, n):
        try:
            return bytes(self.uc.mem_read(addr, n))
        except Exception:
            return b''

    def cstr(self, addr, limit=256):
        b = self.rd(addr, limit)
        z = b.find(b'\0')
        return (b if z < 0 else b[:z]).decode('utf-8', 'replace')

    def alloc(self, n, align=16):
        self.heap_cursor = (self.heap_cursor + align - 1) & ~(align - 1)
        addr = self.heap_cursor
        self.heap_cursor += max(n, 16)
        if self.heap_cursor > HEAP[0] + HEAP[1]:
            raise RuntimeError('堆耗尽')
        return addr

    def write_cstr(self, s):
        b = s.encode() + b'\0'
        addr = self.alloc(len(b))
        self.uc.mem_write(addr, b)
        return addr

    def args(self, n):
        regs = [a64.UC_ARM64_REG_X0, a64.UC_ARM64_REG_X1, a64.UC_ARM64_REG_X2,
                a64.UC_ARM64_REG_X3, a64.UC_ARM64_REG_X4, a64.UC_ARM64_REG_X5,
                a64.UC_ARM64_REG_X6, a64.UC_ARM64_REG_X7]
        return [self.uc.reg_read(r) for r in regs[:n]]

    def ret_to_lr(self, value=0):
        self.uc.reg_write(a64.UC_ARM64_REG_X0, value)
        self.uc.reg_write(a64.UC_ARM64_REG_PC, self.uc.reg_read(a64.UC_ARM64_REG_LR))

    def tail_call(self, fn, value=0):
        """不嵌套 emu_start 的「调用」：把 LR 指到临时 stub，PC 跳 fn；
        fn 返回时落到 stub，我们在 stub 里恢复 PC/LR 并给返回值。

        ★ Unicorn 不支持在 hook 里再次 emu_start（会崩），所以必须这么绕。
        """
        uc = self.uc
        ret_addr = uc.reg_read(a64.UC_ARM64_REG_LR)
        stub = self.new_stub()

        def resume():
            uc.reg_write(a64.UC_ARM64_REG_X0, value)
            uc.reg_write(a64.UC_ARM64_REG_LR, ret_addr)
            uc.reg_write(a64.UC_ARM64_REG_PC, ret_addr)

        self.stub_handlers[stub] = resume
        uc.reg_write(a64.UC_ARM64_REG_LR, stub)
        uc.reg_write(a64.UC_ARM64_REG_PC, fn)

    def record(self, kind, name, args, ret):
        self.seq += 1
        c = Call(self.seq, kind, name, args, ret)
        self.calls.append(c)
        return c

    # ---------------------------------------------------------------- 调用
    def call(self, addr, args=(), timeout=2_000_000, tag=''):
        """调用一个模拟函数；返回 x0。用哨兵地址作为返回点。"""
        uc = self.uc
        saved = [uc.reg_read(r) for r in (a64.UC_ARM64_REG_SP, a64.UC_ARM64_REG_LR,
                                          a64.UC_ARM64_REG_X29, a64.UC_ARM64_REG_PC)]
        sp = uc.reg_read(a64.UC_ARM64_REG_SP) - 0x10
        uc.mem_write(sp, struct.pack('<Q', SENTINEL))
        uc.reg_write(a64.UC_ARM64_REG_SP, sp)
        uc.reg_write(a64.UC_ARM64_REG_LR, SENTINEL)
        regs = [a64.UC_ARM64_REG_X0, a64.UC_ARM64_REG_X1, a64.UC_ARM64_REG_X2,
                a64.UC_ARM64_REG_X3, a64.UC_ARM64_REG_X4, a64.UC_ARM64_REG_X5,
                a64.UC_ARM64_REG_X6, a64.UC_ARM64_REG_X7]
        for r, v in zip(regs, args):
            uc.reg_write(r, v)
        self.emit(f'--> call {tag or hex(addr)} args={[hex(a) for a in args]}')
        try:
            uc.emu_start(addr, SENTINEL, timeout=timeout, count=200_000_000)
        except Exception as e:
            pc = uc.reg_read(a64.UC_ARM64_REG_PC)
            lr = uc.reg_read(a64.UC_ARM64_REG_LR)
            spx = uc.reg_read(a64.UC_ARM64_REG_SP)
            self.emit(f'!! call 异常 {tag}: {e}  pc={pc:#x} lr={lr:#x} sp={spx:#x}')
            for ins in self.disasm(pc - 24, 12):
                mark = ' <<<' if ins.address == pc else ''
                self.emit(f'     {ins.address:#08x}  {ins.mnemonic:9s} {ins.op_str}{mark}')
            for i in range(8):
                self.emit(f'     x{i}={uc.reg_read(getattr(a64, f"UC_ARM64_REG_X{i}")):#x}')
        ret = uc.reg_read(a64.UC_ARM64_REG_X0)
        return ret

    # ---------------------------------------------------------------- 处理器
    def on_import(self, name):
        a = self.args(4)
        ret = 0
        if name in ('malloc', 'calloc', 'realloc', 'memalign', 'posix_memalign', 'strdup', 'strndup', 'asprintf', 'vasprintf'):
            if name == 'malloc':
                ret = self.alloc(a[0])
            elif name in ('calloc',):
                ret = self.alloc(max(a[0] * a[1], 1))
                self.uc.mem_write(ret, b'\0' * max(a[0] * a[1], 1))
            elif name == 'realloc':
                ret = self.alloc(max(a[1], 1))
                if a[0]:
                    old = self.rd(a[0], 4096)
                    self.uc.mem_write(ret, old)
            elif name == 'strdup':
                s = self.cstr(a[0])
                ret = self.write_cstr(s)
            elif name == 'strndup':
                b = self.rd(a[0], a[1])
                ret = self.write_cstr(b.split(b'\0')[0].decode('utf-8', 'replace'))
            elif name == 'memalign' or name == 'posix_memalign':
                ret = self.alloc(max(a[1] if name == 'memalign' else a[2], 1))
        elif name in ('free', 'pthread_mutexattr_init', 'pthread_mutexattr_settype',
                      'pthread_mutex_init', 'pthread_mutexattr_destroy', 'pthread_mutex_lock',
                      'pthread_mutex_unlock', 'pthread_mutex_destroy', 'pthread_mutex_trylock',
                      'pthread_detach', 'pthread_join', 'pthread_key_delete', 'closelog',
                      'sigemptyset', 'sigaddset', 'sigprocmask', 'sync', 'free'):
            ret = 0
        elif name == 'strlen':
            ret = len(self.cstr(a[0], 4096))
        elif name in ('strcmp', 'strncmp', 'memcmp'):
            n = a[2] if name != 'strcmp' else 4096
            b1 = self.rd(a[0], n)
            b2 = self.rd(a[1], n)
            if name == 'strcmp':
                b1 = b1.split(b'\0')[0]
                b2 = b2.split(b'\0')[0]
                ret = (b1 > b2) - (b1 < b2)
            else:
                ret = (b1 > b2) - (b1 < b2)
        elif name in ('memcpy', 'memmove', '__memcpy_chk', '__memmove_chk'):
            data = self.rd(a[1], a[2])
            self.uc.mem_write(a[0], data)
            ret = a[0]
        elif name in ('memset', '__memset_chk'):
            self.uc.mem_write(a[0], bytes([a[1] & 0xFF]) * a[2])
            ret = a[0]
        elif name == 'memchr':
            data = self.rd(a[0], a[2])
            i = data.find(bytes([a[1] & 0xFF]))
            ret = a[0] + i if i >= 0 else 0
        elif name in ('strcpy', 'strncpy'):
            s = self.cstr(a[1])
            self.uc.mem_write(a[0], s.encode() + b'\0')
            ret = a[0]
        elif name in ('strncmp',):
            ret = 0
        elif name == 'strstr':
            h = self.cstr(a[0], 8192)
            n = self.cstr(a[1], 256)
            i = h.find(n)
            ret = a[0] + i if i >= 0 else 0
        elif name == 'strrchr':
            h = self.cstr(a[0], 4096)
            i = h.rfind(chr(a[1] & 0xFF))
            ret = a[0] + i if i >= 0 else 0
        elif name in ('strtoll', 'atoi', 'atoll', 'strtol', 'strtoull', 'strtoimax'):
            s = self.cstr(a[0], 64).strip()
            try:
                ret = int(s, 0) & 0xFFFF_FFFF_FFFF_FFFF
            except Exception:
                try:
                    ret = int(s) & 0xFFFF_FFFF_FFFF_FFFF
                except Exception:
                    ret = 0
        elif name == 'strtod':
            try:
                ret = struct.unpack('<Q', struct.pack('<d', float(self.cstr(a[0], 32))))[0]
            except Exception:
                ret = 0
        elif name == '__system_property_get':
            key = self.cstr(a[0], 128)
            val = PROPS.get(key, '')
            b = val.encode() + b'\0'
            self.uc.mem_write(a[1], b)
            ret = len(val)
        elif name in ('__system_property_find', '__system_property_read'):
            ret = 0
        elif name == 'clock_gettime':
            now = time.time()
            self.uc.mem_write(a[1], struct.pack('<qq', int(now), int((now % 1) * 1e9)))
            ret = 0
        elif name in ('time',):
            ret = int(time.time())
            if a[0]:
                self.uc.mem_write(a[0], struct.pack('<q', ret))
        elif name in ('getpid', 'gettid'):
            ret = 4242
        elif name == 'getuid':
            ret = 0
        elif name == 'getppid':
            ret = 1
        elif name == 'pthread_self':
            ret = 0x1234
        elif name == 'pthread_once':
            # a0=once_control, a1=init_routine
            flag = self.rd(a[0], 4)
            val = struct.unpack('<i', flag)[0] if len(flag) == 4 else 0
            if val == 0:
                self.uc.mem_write(a[0], struct.pack('<i', 2))
                self.emit(f'    [pthread_once] 跳到 init {a[1]:#x}')
                self.tail_call(a[1], 0)
                return
            ret = 0
        elif name == 'dlopen':
            ret = 0x1
        elif name == 'dlsym':
            ret = 0
        elif name in ('dlclose', 'dl_iterate_phdr'):
            ret = 0
        elif name in ('open', '__open_2', 'openat'):
            ret = -1
        elif name == 'read':
            ret = 0
        elif name in ('close', 'unlink', 'remove', 'mkdir', 'mkdirat', 'access', 'fstatat', 'fstat'):
            ret = 0 if name != 'access' else -1
        elif name == 'readlink':
            ret = -1
        elif name in ('opendir',):
            ret = 0
        elif name in ('readdir', 'closedir'):
            ret = 0
        elif name == 'sysconf':
            ret = 4096
        elif name == 'getpagesize':
            ret = 4096
        elif name in ('mmap',):
            ret = self.alloc(0x10000)
        elif name in ('munmap', 'mprotect'):
            ret = 0
        elif name == 'getauxval':
            ret = 0
        elif name == 'uname':
            self.uc.mem_write(a[0], b'Linux\0' + b'\0' * 64)
            ret = 0
        elif name == 'sysinfo':
            ret = 0
        elif name == 'syscall':
            ret = 0
        elif name == 'getenv':
            ret = 0
        elif name == 'abort' or name == '__stack_chk_fail':
            self.emit(f'!! {name}() 被调用')
            self.uc.emu_stop()
            return
        elif name in ('printf', 'fprintf', 'vfprintf', 'puts', 'fputc', 'fwrite', 'fflush', 'fopen', 'fread', 'fclose'):
            ret = 0
        elif name in ('vsnprintf', '__vsnprintf_chk', '__vsprintf_chk'):
            # a0=dst, a1=size, a2=fmt
            fmt = self.cstr(a[2], 256)
            self.emit(f'    [fmt] {fmt!r}')
            ret = 0
        elif name == 'sscanf':
            ret = 0
        elif name == 'android_set_abort_message':
            self.emit(f'!! abort message: {self.cstr(a[0], 200)!r}')
            ret = 0
        elif name in ('openlog', 'syslog'):
            ret = 0
        elif name == 'pthread_create':
            self.emit(f'    [pthread_create] start={a[2]:#x} arg={a[3]:#x}')
            ret = 0
        elif name in ('sigaction', 'fork', 'kill', 'prctl'):
            ret = 0
        elif name == 'popen':
            ret = 0
        elif name == 'system':
            ret = 0
        elif name == 'socket':
            ret = -1
        elif name in ('bind', 'connect', 'setsockopt', 'sendto', 'recvfrom', 'ioctl', 'inet_addr'):
            ret = 0
        elif name == '__cxa_atexit':
            ret = 0
        elif name == '__cxa_finalize':
            ret = 0
        elif name == '__cxa_thread_atexit_impl':
            ret = 0
        elif name == 'environ':
            ret = 0
        elif name in ('strtok', 'strtok_r', 'strcspn', 'strcasestr', 'strerror_r'):
            ret = 0
        elif name == 'srand' or name == 'rand':
            ret = 4
        elif name == 'pthread_key_create':
            ret = 0
        elif name in ('pthread_getspecific',):
            ret = 0
        elif name in ('pthread_setspecific',):
            ret = 0
        else:
            self.emit(f'    [未实现 import] {name}({[hex(x) for x in a]}) -> 0')
        c = self.record('import', name, a, ret)
        if self.trace:
            self.emit(f'  #{c.seq} {name}({[hex(x) for x in a[:3]]}) = {ret:#x}')
        self.ret_to_lr(ret)

    def on_jni(self, idx, name):
        a = self.args(4)
        ret = 0
        lr = self.uc.reg_read(a64.UC_ARM64_REG_LR)
        extra = ''
        if idx in (5, 6):
            extra = f' str(x1)={self.cstr(a[1], 128)!r} x3data={self.rd(a[3] or 0, 16).hex()} x4={self.uc.reg_read(a64.UC_ARM64_REG_X4):#x}'
        self.emit(f'    [JNI #{idx} {name}] lr={lr:#x} args={[hex(x) for x in a]}{extra}')
        if name == 'FindClass':
            cls = self.cstr(a[1], 200)
            self.emit(f'    [JNI] FindClass({cls!r})')
            ret = 0x1000 + len(self.log)
        elif name in ('GetMethodID', 'GetStaticMethodID', 'GetFieldID', 'GetStaticFieldID'):
            cls, n, sig = a[1], self.cstr(a[2], 128), self.cstr(a[3], 128)
            self.emit(f'    [JNI] {name} cls={cls:#x} {n}{sig}')
            ret = 0x2000 + len(self.log)
        elif name in ('NewStringUTF',):
            s = self.cstr(a[1], 512)
            self.emit(f'    [JNI] NewStringUTF({s!r})')
            ret = 0x3000 + len(self.log)
        elif name in ('GetStringUTFChars',):
            ret = self.write_cstr('DUMMY')
        elif name in ('GetStringUTFLength', 'GetStringLength', 'GetArrayLength'):
            ret = 0
        elif name in ('GetJavaVM',):
            self.uc.mem_write(a[1], struct.pack('<Q', VM_PTR))
            ret = 0
        elif idx == 215 or name == 'RegisterNatives':
            cls, methods, n = a[1], a[2], a[3]
            self.emit(f'    [JNI] RegisterNatives cls={cls:#x} methods={methods:#x} n={n}')
            for i in range(n):
                base = methods + i * 24
                nm, sig, fn = struct.unpack('<QQQ', self.rd(base, 24))
                self.emit(f'        [{i}] {self.cstr(nm, 96)!r} {self.cstr(sig, 128)!r} fn={fn:#x}')
            ret = 0
        elif name in ('ExceptionOccurred',):
            ret = 0
        elif name in ('GetObjectClass',):
            ret = 0x1000
        elif name in ('GetSuperclass',):
            # 返回非 0：libmetasec 用它遍历类层次做校验，返回 0 会走失败分支
            ret = a[1] + 0x100 if a[1] else 0x2001
        elif name in ('NewGlobalRef', 'NewLocalRef', 'NewWeakGlobalRef'):
            ret = a[1]
        elif name in ('GetStaticObjectField',):
            ret = 0
        elif name in ('GetStaticIntField',):
            ret = 0
        elif name in ('GetStaticLongField',):
            ret = 0
        elif name in ('GetIntField',):
            ret = 0
        elif name in ('GetLongField',):
            ret = 0
        elif name in ('GetBooleanField',):
            ret = 0
        else:
            self.emit(f'    [JNI 未实现] #{idx} {name}({[hex(x) for x in a]}) -> 0')
        c = self.record('jni', name, a, ret)
        if self.trace:
            self.emit(f'  #{c.seq} JNI {name}({[hex(x) for x in a[:3]]}) = {ret:#x}')
        self.ret_to_lr(ret)

    def on_vm(self, idx, name):
        a = self.args(3)
        ret = 0
        if name == 'GetEnv':
            self.uc.mem_write(a[1], struct.pack('<Q', ENV_PTR))
            self.emit('    [VM] GetEnv -> ENV')
            ret = 0
        elif name == 'AttachCurrentThread':
            self.uc.mem_write(a[1], struct.pack('<Q', ENV_PTR))
            ret = 0
        else:
            self.emit(f'    [VM] {name}({[hex(x) for x in a]}) -> 0')
        self.record('vm', name, a, ret)
        self.ret_to_lr(ret)

    # ---------------------------------------------------------------- 入口
    def run_init_array(self):
        tg = []
        for off, info in self.blob.relocs.items():
            if info[0] == 'RELATIVE' and 0x3e80c0 <= off < 0x3e8140:
                tg.append((off, info[1]))
        self.emit(f'init_array 共 {len(tg)} 项')
        for off, fn in sorted(tg):
            self.emit(f'\n=== init_array[{hex(off)}] -> {fn:#x}')
            self.call(fn, (), tag=f'init@{fn:#x}')

    def run_jni_onload(self):
        base = self.blob.elf.get_section_by_name('.dynsym').get_symbol(0)
        self.emit('\n=== JNI_OnLoad')
        ret = self.call(0x2a5194, (VM_PTR,), tag='JNI_OnLoad')
        self.emit(f'JNI_OnLoad 返回 {ret:#x}')
        return ret


PROPS = {
    'ro.build.version.sdk': '35',
    'ro.build.version.release': '15',
    'ro.build.version.preview_sdk': '0',
    'ro.product.model': '22041216C',
    'ro.product.brand': 'Redmi',
    'ro.product.manufacturer': 'Xiaomi',
    'ro.product.cpu.abi': 'arm64-v8a',
    'ro.build.fingerprint': 'Redmi/Note 11T Pro:15/AP3A/1:user/release-keys',
    'ro.debuggable': '0',
    'ro.secure': '1',
    'ro.kernel.qemu': '0',
    'ro.boot.qemu': '0',
    'ro.hardware': 'qcom',
    'ro.serialno': 'unknown',
    'ro.build.type': 'user',
    'ro.build.tags': 'release-keys',
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--trace', action='store_true')
    ap.add_argument('--verbose', action='store_true')
    ap.add_argument('--only', choices=['init', 'onload'], default=None)
    ap.add_argument('--call', default=None, help='JNI_OnLoad 之后调用 MSManager.a，格式 cmd[,arg2[,long]]')
    ap.add_argument('--fn', default='0x297890')
    ap.add_argument('--null-findclass', action='store_true', help='模拟真实环境：FindClass 返回 0')
    ap.add_argument('--scan', default='x-argus,argus,ladon,x-ladon,khronos,MSManager,metasec')
    args = ap.parse_args()
    t0 = time.time()
    emu = Emu(SO, trace=args.trace, verbose=args.verbose)
    if args.null_findclass:
        emu.jni_return_override[6] = 0
    emu.emit(f'加载完成，段/重定位就绪，用时 {time.time() - t0:.2f}s')
    if args.only != 'onload':
        emu.run_init_array()
    if args.only != 'init':
        emu.run_jni_onload()
    if args.call:
        cmd, arg2, lng = (int(x, 0) for x in (args.call.split(',') + ['0', '0'])[:3])
        print(f'\n=== 调 MSManager.a(cmd={cmd}, arg2={arg2}, long={lng})')
        before = len(emu.calls)
        r = emu.call(int(args.fn, 16), (ENV_PTR, cmd, arg2, lng, 0, 0), tag=f'MSManager.a({cmd})')
        print(f'返回 {r:#x}；新增调用 {len(emu.calls) - before} 条')
    if args.scan:
        kws = [k.encode() for k in args.scan.split(',')]
        print('\n=== 扫描已解密内存中的关键字 ===')
        for (base, size, label) in ((HEAP[0], 0x400000, 'heap'), (0x4527b0, 0x15000, 'bss'),
                                    (0x3ec9f0, 0x66000, 'data')):
            try:
                blob = bytes(emu.uc.mem_read(base, size))
            except Exception:
                continue
            for kw in kws:
                start = 0
                n = 0
                while n < 6:
                    i = blob.find(kw, start)
                    if i < 0:
                        break
                    ctx = blob[max(0, i - 24):i + 48]
                    printable = ''.join(chr(c) if 32 <= c < 127 else '.' for c in ctx)
                    print(f'  [{label}] {base + i:#x} {kw.decode()!r} :: {printable}')
                    start = i + 1
                    n += 1
    print('\n--- 调用统计 ---')
    from collections import Counter
    for (k, n), c in Counter((c.kind, c.name) for c in emu.calls).most_common(40):
        print(f'  {k:7s} {n:32s} x{c}')
    print(f'总调用 {len(emu.calls)}，用时 {time.time() - t0:.2f}s')


if __name__ == '__main__':
    main()
