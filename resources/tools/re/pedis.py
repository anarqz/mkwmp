"""x64 PE disassembler with COFF symbol names.
usage: python -I pedis.py <pe> at <hexrva|symbol> <maxinsns> [noret]
       python -I pedis.py <pe> callers <hexrva|symbol>     (call/jmp rel32 sites)
       python -I pedis.py <pe> syms <regex>
       python -I pedis.py <pe> str <hexrva>
"""
import bisect
import re
import struct
import sys

from capstone import CS_ARCH_X86, CS_MODE_64, Cs

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
d = open(sys.argv[1], 'rb').read()
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]
symptr, nsym = struct.unpack_from('<II', d, pe + 12)
osz = struct.unpack_from('<H', d, pe + 20)[0]
imagebase = struct.unpack_from('<Q', d, pe + 24 + 24)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + osz + 40 * i
    name = d[o:o + 8].rstrip(b'\0').decode()
    vsz, va, rsz, rptr = struct.unpack_from('<IIII', d, o + 8)
    secs.append((name, va, vsz, rptr, rsz))
strtab = symptr + nsym * 18
syms = {}
by_addr = {}
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
    if secno > 0 and secno <= nsec:
        rva = secs[secno - 1][1] + value
        syms.setdefault(name, rva)
        by_addr.setdefault(rva, name)
    i += 1 + naux
addrs = sorted(by_addr)

# imports
imports = {}
ddir = pe + 24 + 112
imp_rva = struct.unpack_from('<I', d, ddir + 8)[0]


def off(rva):
    for name, va, vsz, rptr, rsz in secs:
        if va <= rva < va + max(vsz, rsz):
            if rva - va >= rsz:
                return None
            return rptr + rva - va
    return None


def cstr(rva, maxlen=200):
    o = off(rva)
    if o is None:
        return None
    e = d.find(b'\0', o, o + maxlen)
    if e < 0:
        return None
    s = d[o:e]
    if len(s) >= 3 and all(32 <= c < 127 or c in (10, 9) or c >= 0x80 for c in s):
        return s.decode('utf-8', 'replace')
    return None


if imp_rva:
    o = off(imp_rva)
    while True:
        oft, _, _, nm, ft = struct.unpack_from('<IIIII', d, o)
        if not nm:
            break
        dll = cstr(nm) or '?'
        t = off(oft or ft)
        k = 0
        while True:
            v = struct.unpack_from('<Q', d, t + 8 * k)[0]
            if not v:
                break
            if v >> 63:
                n = f'#{v & 0xffff}'
            else:
                n = cstr(v + 2) or '?'
            imports[ft + 8 * k] = f'{dll}!{n}'
            k += 1
        o += 20


def symname(rva):
    if rva in imports:
        return '[' + imports[rva] + ']'
    k = bisect.bisect_right(addrs, rva) - 1
    if k < 0:
        return ''
    a = addrs[k]
    if rva - a > 0x4000:
        return ''
    return by_addr[a] + (f'+{rva - a:#x}' if rva != a else '')


def resolve(s):
    if s in syms:
        return syms[s]
    return int(s, 16)


md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
text = [s for s in secs if s[0] == '.text'][0]


def dis(rva, count, stop_ret=True):
    o = off(rva)
    n = 0
    for ins in md.disasm(d[o:o + count * 15], rva):
        extra = ''
        for op in ins.operands:
            if op.type == 2 and ins.mnemonic.startswith(('call', 'j')):
                extra = symname(op.imm)
            elif op.type == 3 and op.mem.base == 41:  # rip
                tgt = ins.address + ins.size + op.mem.disp
                extra = symname(tgt)
                s = cstr(tgt)
                if s and tgt not in imports:
                    extra += ' "' + s.replace('\n', '\\n')[:90] + '"'
        print(f'{ins.address:#010x} {ins.bytes.hex():<24} {ins.mnemonic} {ins.op_str}' + (f'   ; {extra}' if extra else ''))
        n += 1
        if n >= count or (stop_ret and ins.mnemonic in ('ret', 'int3')):
            break


cmd = sys.argv[2]
if cmd == 'at':
    dis(resolve(sys.argv[3]), int(sys.argv[4]), len(sys.argv) < 6)
elif cmd == 'syms':
    rx = re.compile(sys.argv[3])
    for a in addrs:
        pass
    for n_, a in sorted(syms.items(), key=lambda x: x[1]):
        if rx.search(n_):
            print(f'{a:#010x} {n_}')
elif cmd == 'callers':
    tgt = resolve(sys.argv[3])
    name, va, vsz, rptr, rsz = text
    blob = d[rptr:rptr + rsz]
    for m in re.finditer(rb'[\xe8\xe9]', blob):
        p = m.start()
        if p + 5 > len(blob):
            break
        rel = struct.unpack_from('<i', blob, p + 1)[0]
        if va + p + 5 + rel == tgt:
            print(f'{va + p:#010x} {"call" if blob[p] == 0xe8 else "jmp"}  in {symname(va + p)}')
    # also lea refs
    for m in re.finditer(rb'[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]', blob):
        p = m.start()
        rel = struct.unpack_from('<i', blob, p + 3)[0]
        if va + p + 7 + rel == tgt:
            print(f'{va + p:#010x} lea   in {symname(va + p)}')
elif cmd == 'str':
    print(cstr(resolve(sys.argv[3])))
elif cmd == 'secs':
    for s in secs:
        print(s)
    print(hex(imagebase))

