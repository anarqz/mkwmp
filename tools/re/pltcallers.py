"""Find BL call sites of an imported symbol in an NSO (via its PLT stub) and disassemble after each.

usage: python -I pltcallers.py <nso> <symbol> [after_insns] [max_sites]
"""
import os
import struct
import sys

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs

sys.path.insert(0, os.path.dirname(__file__))
from nsodump import Nso  # noqa: E402

DT_JMPREL, DT_PLTRELSZ = 23, 2
n = Nso(sys.argv[1])
want = sys.argv[2]
after = int(sys.argv[3]) if len(sys.argv) > 3 else 12
maxs = int(sys.argv[4]) if len(sys.argv) > 4 else 6
img = bytes(n.image)

# re-read dynamic for JMPREL
mod0 = n.u32(4)
dyn_off = mod0 + struct.unpack_from('<i', img, mod0 + 4)[0]
dyn = {}
o = dyn_off
while True:
    t, v = n.u64(o), n.u64(o + 8)
    o += 16
    if t == 0:
        break
    dyn.setdefault(t, v)
slot = None
for i in range(dyn[DT_PLTRELSZ] // 24):
    r_off, r_info, _ = struct.unpack_from('<QQq', img, dyn[DT_JMPREL] + i * 24)
    if n.syms[r_info >> 32][0] == want:
        slot = r_off
if slot is None:
    sys.exit(f'{want}: no JUMP_SLOT')
# PLT stub: adrp x16, P; ldr x17, [x16, #lo]; add x16, x16, #lo; br x17
stub = None
for off in range(0, len(img) - 16, 4):
    w0, w1, w3 = struct.unpack_from('<III', img, off)[0], struct.unpack_from('<I', img, off + 4)[0], \
        struct.unpack_from('<I', img, off + 12)[0]
    if (w0 & 0x9F00001F) == 0x90000010 and w3 == 0xD61F0220:
        immlo, immhi = (w0 >> 29) & 3, (w0 >> 5) & 0x7FFFF
        imm = ((immhi << 2) | immlo) << 12
        if imm & (1 << 32):
            imm -= 1 << 33
        page = (off & ~0xFFF) + imm
        lo = ((w1 >> 10) & 0xFFF) * 8
        if page + lo == slot:
            stub = off
            break
print(f'{want}: GOT slot {slot:#x}, PLT stub {stub:#x}')
md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
sites = []
for off in range(0, n.image.index(b'\0' * 64) if False else len(img) - 4, 4):
    w = struct.unpack_from('<I', img, off)[0]
    if (w & 0xFC000000) == 0x94000000:
        imm = w & 0x3FFFFFF
        if imm & (1 << 25):
            imm -= 1 << 26
        if off + imm * 4 == stub:
            sites.append(off)
print(f'{len(sites)} call sites')
for s in sites[:maxs]:
    print(f'--- site {s:#x}')
    for i in md.disasm(img[s - 16:s + after * 4], s - 16):
        print(f'  {i.address:#010x}: {i.mnemonic:8} {i.op_str}')
