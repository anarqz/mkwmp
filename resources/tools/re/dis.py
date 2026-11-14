"""Disassemble exported functions of an NSO module by name regex, or raw ranges.

usage: python -I dis.py <nso> sym <regex> [maxinsns]
       python -I dis.py <nso> at <hexaddr> <count>
       python -I dis.py <nso> callers <symbol-or-hexaddr>      (BL sites in .text targeting it or its PLT stub)
       python -I dis.py <nso> movz <hex32>                      (find mov/movk pairs building a 32-bit constant)
"""
import os
import re
import struct
import sys

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs

sys.path.insert(0, os.path.dirname(__file__))
from nsodump import Nso  # noqa: E402

n = Nso(sys.argv[1])
md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
img = bytes(n.image)
syms = {s[0]: s[2] for s in n.syms if s[0] and s[1] != 0}
by_addr = {}
for k, v in syms.items():
    by_addr.setdefault(v, k)


def dis(addr, count, stop_ret=True):
    for i in md.disasm(img[addr:addr + count * 4], addr):
        tgt = ''
        if i.mnemonic in ('bl', 'b') and i.op_str.startswith('#'):
            t = int(i.op_str[1:], 16)
            tgt = '  ; ' + by_addr.get(t, '')
        print(f'  {i.address:#010x}: {i.mnemonic:8} {i.op_str}{tgt}')
        if stop_ret and i.mnemonic == 'ret':
            break


mode = sys.argv[2]
if mode == 'sym':
    rx = re.compile(sys.argv[3])
    mx = int(sys.argv[4]) if len(sys.argv) > 4 else 40
    for name, a in sorted(syms.items(), key=lambda kv: kv[1]):
        if rx.search(name):
            print(f'{a:#010x} {name}')
            dis(a, mx)
elif mode == 'at':
    dis(int(sys.argv[3], 16), int(sys.argv[4]), stop_ret=False)
elif mode == 'callers':
    target = syms.get(sys.argv[3])
    if target is None:
        target = int(sys.argv[3], 16)
    for i, (word,) in enumerate(struct.iter_unpack('<I', img[:len(img) & ~3])):
        if word >> 26 not in (0b000101, 0b100101):
            continue
        delta = ((word & 0x1ffffff) - (word & 0x2000000)) * 4
        if i * 4 + delta == target:
            print(f'{i * 4:#010x} {"bl" if word >> 31 else "b"}')
elif mode == 'movz':
    want = int(sys.argv[3], 16)
    lo, hi = want & 0xffff, want >> 16
    for off in range(0, len(img) - 8, 4):
        w = struct.unpack_from('<I', img, off)[0]
        if (w & 0xFFFFFFE0) == (0x52800000 | (lo << 5)):
            rd = w & 31
            for k in range(1, 6):
                w2 = struct.unpack_from('<I', img, off + 4 * k)[0]
                if (w2 & 0xFFFFFFE0) == (0x72A00000 | (hi << 5)) and (w2 & 31) == rd:
                    print(f'{off:#010x} mov w{rd}, #{want:#x}')
                    break
