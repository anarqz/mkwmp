"""Callers of imported SDK functions in an NSO (through the PLT stubs), several symbols in one pass.

usage: python -I plt.py <exefs>/<module> <dump>/<module>.bin <symbol-regex> [--sites]
Prints, per matching import: its PLT stub and the functions (pacibsp starts) that BL / B to it, with the count of
sites; --sites lists every call site. Needs the raw NSO (dynamic section: JMPREL) and the flat image of dump.py.
"""
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from rx import Img, REPO  # noqa: E402

sys.path.insert(0, os.path.join(REPO, 'resources', 'tools', 're'))
from nsodump import Nso  # noqa: E402

DT_JMPREL, DT_PLTRELSZ = 23, 2


def main():
    n = Nso(sys.argv[1])
    im = Img(sys.argv[2])
    rx = re.compile(sys.argv[3])
    img = im.b
    mod0 = n.u32(4)
    o = mod0 + struct.unpack_from('<i', img, mod0 + 4)[0]
    dyn = {}
    while True:
        t, v = n.u64(o), n.u64(o + 8)
        o += 16
        if t == 0:
            break
        dyn.setdefault(t, v)
    slots = {}
    for i in range(dyn[DT_PLTRELSZ] // 24):
        r_off, r_info, _ = struct.unpack_from('<QQq', img, dyn[DT_JMPREL] + i * 24)
        name = n.syms[r_info >> 32][0]
        if rx.search(name):
            slots[r_off] = name
    cnt = im.text_end // 4
    words = struct.unpack_from(f'<{cnt}I', img)
    stubs = {}
    for i in range(cnt - 3):
        w0 = words[i]
        if (w0 & 0x9F00001F) == 0x90000010 and words[i + 3] == 0xD61F0220:
            imm = (((w0 >> 5) & 0x7FFFF) << 2) | ((w0 >> 29) & 3)
            if imm & (1 << 20):
                imm -= 1 << 21
            slot = ((i * 4) & ~0xFFF) + (imm << 12) + ((words[i + 1] >> 10) & 0xFFF) * 8
            if slot in slots:
                stubs[i * 4] = slots[slot]
    sites = {}
    for i, w in enumerate(words):
        if (w >> 26) in (0b000101, 0b100101):
            t = i * 4 + ((w & 0x1ffffff) - (w & 0x2000000)) * 4
            if t in stubs:
                sites.setdefault(stubs[t], []).append(i * 4)
    for stub, name in sorted(stubs.items(), key=lambda kv: kv[1]):
        ss = sites.get(name, [])
        funcs = {}
        for s in ss:
            funcs.setdefault(im.func_start(s), []).append(s)
        print(f'{name}: stub {stub:#x}, {len(ss)} site(s) in {len(funcs)} function(s)')
        for f, lst in sorted(funcs.items()):
            extra = ' ' + ','.join(f'{x:#x}' for x in lst) if '--sites' in sys.argv else ''
            print(f'    func {f:#010x} x{len(lst)}{extra}')
    missing = set(slots.values()) - set(stubs.values())
    for m in sorted(missing):
        print(f'{m}: JUMP_SLOT but no PLT stub found')


if __name__ == '__main__':
    main()
