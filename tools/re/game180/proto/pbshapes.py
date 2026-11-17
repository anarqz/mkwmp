"""Protobuf-lite message shapes (field number : wire type, from the generated _InternalSerialize code) of a code
range, as an address-free list that can be diffed between two builds.

usage: python -I pbshapes.py <dump>/main.bin <0xstart> <0xend> > shapes.txt
       python -I pbshapes.py --diff shapes100.txt shapes180.txt
Same heuristic as resources/tools/re/pb_serializers.py (mov wN,#tag ; strb/strh wN) without the callee addresses.
Each output line: "<shape>\t<func addr>"; --diff compares the multisets of shapes and prints the shapes whose count
differs, with the addresses that carry them in each build ("-" 1.0.0 only, "+" 1.8.0 only).
"""
import collections
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..'))
sys.path.insert(0, os.path.join(REPO, '.deps'))

WIRE = {0: 'varint', 1: 'fixed64', 2: 'len', 5: 'fixed32'}


def decode_tag(value, two_byte):
    if not two_byte:
        return value >> 3, value & 7
    lo, hi = value & 0xFF, value >> 8
    if not lo & 0x80:
        return None
    tag = (lo & 0x7F) | (hi << 7)
    return tag >> 3, tag & 7


def shapes(path, start, end):
    from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs
    data = open(path, 'rb').read()
    words = struct.unpack_from(f'<{(end - start) // 4}I', data, start)
    starts = [start + i * 4 for i, w in enumerate(words) if w == 0xD503237F]
    md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
    md.skipdata = True
    for n, fn in enumerate(starts):
        stop = starts[n + 1] if n + 1 < len(starts) else end
        ins = list(md.disasm_lite(data[fn:stop], fn))
        fields = {}
        for k, (_, _, mn, ops) in enumerate(ins):
            if mn != 'mov' or '#' not in ops or not ops.startswith('w'):
                continue
            reg, imm = ops.split(', #')
            try:
                value = int(imm, 0)
            except ValueError:
                continue
            for (_, _, m2, o2) in ins[k + 1:k + 6]:
                if m2 in ('strb', 'strh') and o2.startswith(reg + ','):
                    dec = None
                    if m2 == 'strb' and 0 < value < 0x80 and (value & 7) in WIRE and value >> 3:
                        dec = decode_tag(value, False)
                    elif m2 == 'strh' and 0x80 <= value <= 0xFFFF:
                        dec = decode_tag(value, True)
                    if dec and dec[0] and dec[1] in WIRE:
                        fields.setdefault(dec[0], WIRE[dec[1]])
                    break
        if fields:
            print(', '.join(f'{f}:{w}' for f, w in sorted(fields.items())) + f'\t{fn:#x}')


def diff(a, b):
    def load(p):
        d = collections.defaultdict(list)
        for line in open(p):
            s, addr = line.rstrip('\n').split('\t')
            d[s].append(addr)
        return d
    da, db = load(a), load(b)
    for s in sorted(set(da) | set(db)):
        if len(da.get(s, [])) != len(db.get(s, [])):
            for x in da.get(s, []):
                print(f'- {x:>10}  {s}')
            for x in db.get(s, []):
                print(f'+ {x:>10}  {s}')
            print()
    print(f'# shapes: {sum(map(len, da.values()))} vs {sum(map(len, db.values()))}; distinct {len(da)} vs {len(db)}')


if __name__ == '__main__':
    if sys.argv[1] == '--diff':
        diff(sys.argv[2], sys.argv[3])
    else:
        shapes(sys.argv[1], int(sys.argv[2], 16), int(sys.argv[3], 16))
