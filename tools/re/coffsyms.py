"""Print COFF symbols of a PE whose names match a regex (RVA = section va + value).

usage: python -I coffsyms.py <pe> <regex>
"""
import re
import struct
import sys

d = open(sys.argv[1], 'rb').read()
rx = re.compile(sys.argv[2])
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]
symptr, nsym = struct.unpack_from('<II', d, pe + 12)
osz = struct.unpack_from('<H', d, pe + 20)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + osz + 40 * i
    secs.append(struct.unpack_from('<I', d, o + 12)[0])
print(f'symtab at {symptr:#x}, {nsym} symbols', file=sys.stderr)
if not symptr:
    sys.exit(1)
strtab = symptr + nsym * 18
i = 0
while i < nsym:
    o = symptr + i * 18
    raw = d[o:o + 8]
    if raw[:4] == b'\0\0\0\0':
        off = struct.unpack_from('<I', raw, 4)[0]
        name = d[strtab + off:d.index(b'\0', strtab + off)].decode('utf-8', 'replace')
    else:
        name = raw.rstrip(b'\0').decode('utf-8', 'replace')
    value, secno, typ, cls, naux = struct.unpack_from('<IhHBB', d, o + 8)
    if rx.search(name) and secno > 0:
        print(f'{secs[secno - 1] + value:#010x} sec{secno} type={typ:#x} class={cls} {name}')
    i += 1 + naux
