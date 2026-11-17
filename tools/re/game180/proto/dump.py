"""Decompress every NSO of an exefs and write, per module: <mod>.bin (flat image), <mod>.imports.txt,
<mod>.exports.txt, <mod>.strings.txt (offset<TAB>string, printable ASCII >= 5), plus info.txt (build ids, segments,
DT_NEEDED-less module name, SDK version strings).

usage: python -I dump.py <exefs dir> <out dir>
Reuses resources/tools/re/nsodump.py (Nso). Needs lz4 from .deps.
"""
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..'))
sys.path.insert(0, os.path.join(REPO, '.deps'))
sys.path.insert(0, os.path.join(REPO, 'resources', 'tools', 're'))
from nsodump import Nso  # noqa: E402

MODS = ('rtld', 'main', 'subsdk0', 'subsdk1', 'sdk')


def main():
    exefs, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    info = []
    for mod in MODS:
        p = os.path.join(exefs, mod)
        if not os.path.isfile(p):
            continue
        raw = open(p, 'rb').read()
        segs = [struct.unpack_from('<III', raw, 0x10 + i * 0x10) for i in range(3)]
        try:
            n = Nso(p)
        except Exception as e:  # rtld has no usable dynsym
            info.append(f'{mod}: build_id={raw[0x40:0x60].hex()} (symbols: {e})')
            continue
        img = bytes(n.image)
        open(os.path.join(out, mod + '.bin'), 'wb').write(img)
        imps = sorted({s[0] for s in n.syms if s[0] and s[1] == 0})
        exps = sorted({(s[0], s[2]) for s in n.syms if s[0] and s[1] != 0})
        open(os.path.join(out, mod + '.imports.txt'), 'w').write('\n'.join(imps) + '\n')
        open(os.path.join(out, mod + '.exports.txt'), 'w').write(''.join(f'{v:#010x} {s}\n' for s, v in exps))
        with open(os.path.join(out, mod + '.strings.txt'), 'w', encoding='utf-8') as f:
            for m in re.finditer(rb'[\x20-\x7e]{5,}', img):
                f.write(f'{m.start():#010x}\t{m.group().decode()}\n')
        # module name: after rodata start, u32 0, u32 len, name
        ro = segs[1][1]
        ln = struct.unpack_from('<I', img, ro + 4)[0]
        name = img[ro + 8:ro + 8 + ln].decode('latin1') if 0 < ln < 256 else ''
        sdkv = sorted({m.group().decode() for m in re.finditer(rb'SDK MW\+Nintendo\+[\x20-\x7e]+', img)})
        info.append(f'{mod}: build_id={raw[0x40:0x60].hex()} text={segs[0][2]:#x} ro={segs[1][2]:#x}@{segs[1][1]:#x} '
                    f'data={segs[2][2]:#x}@{segs[2][1]:#x} name={name!r} imports={len(imps)} exports={len(exps)}')
        for s in sdkv:
            info.append(f'    {s}')
    open(os.path.join(out, 'info.txt'), 'w').write('\n'.join(info) + '\n')
    print('\n'.join(info))


if __name__ == '__main__':
    main()
