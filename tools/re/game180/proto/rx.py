"""String xrefs and annotated disassembly on a flat image produced by dump.py (<dump>/<mod>.bin).

usage: python -I rx.py <image.bin> refs <literal|0xaddr> [...]     xrefs (ADRP+ADD) with the enclosing function start
       python -I rx.py <image.bin> dis <0xaddr> <count>            disassemble, annotating ADRP+ADD strings and BLs
       python -I rx.py <image.bin> func <0xaddr> [max]             disassemble the enclosing function (pacibsp..next)
       python -I rx.py <image.bin> strs <0xfunc> [max]             only the strings / calls a function references
       python -I rx.py <image.bin> callers <0xaddr>                BL/B sites targeting addr
The xref index (every ADRP+ADD target) is cached next to the image as <image>.xref.pickle.
A literal is matched as an exact NUL-delimited string; every occurrence is looked up.
"""
import os
import pickle
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..'))
sys.path.insert(0, os.path.join(REPO, '.deps'))
from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs  # noqa: E402

PACIBSP = 0xD503237F


class Img:
    def __init__(self, path):
        self.path = path
        self.b = open(path, 'rb').read()
        self.text_end = self._text_end()
        self.md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
        self.md.skipdata = True
        self._idx = None

    def _text_end(self):
        # dump.py writes info.txt beside the image: "<mod>: ... text=0x... ro=..@0x..."
        info = os.path.join(os.path.dirname(self.path), 'info.txt')
        mod = os.path.basename(self.path)[:-4]
        if os.path.isfile(info):
            for line in open(info):
                if line.startswith(mod + ':') and 'text=' in line:
                    return int(line.split('text=')[1].split()[0], 16)
        return len(self.b) & ~3

    def index(self):
        if self._idx is not None:
            return self._idx
        cache = self.path + '.xref.pickle'
        if os.path.isfile(cache) and os.path.getmtime(cache) > os.path.getmtime(self.path):
            self._idx = pickle.load(open(cache, 'rb'))
            return self._idx
        idx = {}
        n = self.text_end // 4
        words = struct.unpack_from(f'<{n}I', self.b)
        reg = [None] * 32
        for i in range(n):
            w = words[i]
            if (w & 0x9F000000) == 0x90000000:
                imm = (((w >> 5) & 0x7FFFF) << 2) | ((w >> 29) & 3)
                if imm & (1 << 20):
                    imm -= 1 << 21
                reg[w & 31] = (((i * 4) & ~0xFFF) + (imm << 12), i)
            elif (w & 0xFF800000) == 0x91000000:
                r = reg[(w >> 5) & 31]
                if r is not None and i - r[1] < 16:
                    add = ((w >> 10) & 0xFFF) << (12 if (w >> 22) & 1 else 0)
                    idx.setdefault(r[0] + add, []).append(i * 4)
            elif w == PACIBSP:
                reg = [None] * 32
        pickle.dump(idx, open(cache, 'wb'))
        self._idx = idx
        return idx

    def func_start(self, pc):
        i = pc & ~3
        while i > 0 and struct.unpack_from('<I', self.b, i)[0] != PACIBSP:
            i -= 4
        return i

    def func_end(self, start):
        i = start + 4
        while i < self.text_end and struct.unpack_from('<I', self.b, i)[0] != PACIBSP:
            i += 4
        return i

    def cstr(self, a):
        if not 0 <= a < len(self.b):
            return None
        e = self.b.find(b'\0', a, a + 200)
        if e <= a:
            return None
        s = self.b[a:e]
        if len(s) >= 2 and all(0x20 <= c < 0x7f for c in s):
            return s.decode()
        return None

    def find(self, lit):
        e, out, i = lit.encode(), [], 0
        pat = b'\0' + e + b'\0'
        while True:
            i = self.b.find(pat, i)
            if i < 0:
                return out
            out.append(i + 1)
            i += 1

    def dis(self, start, end, only_notes=False, limit=100000):
        adrp, n = {}, 0
        for ins in self.md.disasm(self.b[start:end], start):
            note = ''
            m, ops = ins.mnemonic, ins.op_str
            if m == 'adrp':
                r, v = ops.split(', #')
                adrp[r] = int(v, 16)
            elif m == 'add' and ops.count(',') == 2 and '#' in ops:
                rd, rn, imm = [x.strip() for x in ops.split(',')]
                if rn in adrp and imm.startswith('#'):
                    try:
                        t = adrp[rn] + int(imm[1:], 16)
                        s = self.cstr(t)
                        note = f'  ; {t:#x}' + (f' "{s}"' if s else '')
                    except ValueError:
                        pass
            elif m in ('bl', 'b') and ops.startswith('#'):
                note = '  ; call' if m == 'bl' else ''
            elif m == 'mov' and ', #' in ops and ops[0] == 'w':
                try:
                    v = int(ops.split('#')[1], 16)
                    if v > 0xffff:
                        note = f'  ; {v:#x}'
                except ValueError:
                    pass
            if not only_notes or note:
                print(f'  {ins.address:#010x}: {m:7} {ops}{note}')
            n += 1
            if n >= limit:
                break


def main():
    im = Img(sys.argv[1])
    cmd = sys.argv[2]
    if cmd == 'refs':
        idx = im.index()
        for lit in sys.argv[3:]:
            addrs = [int(lit, 16)] if lit.startswith('0x') else im.find(lit)
            if not addrs:
                print(f'{lit}: no exact string')
            for a in addrs:
                refs = idx.get(a, [])
                print(f'{lit} @{a:#x}: {len(refs)} ref(s)')
                for pc in refs:
                    print(f'    {pc:#010x} in func {im.func_start(pc):#010x}')
    elif cmd == 'dis':
        a = int(sys.argv[3], 16)
        im.dis(a, a + int(sys.argv[4], 0) * 4)
    elif cmd in ('func', 'strs'):
        s = im.func_start(int(sys.argv[3], 16))
        e = im.func_end(s)
        mx = int(sys.argv[4], 0) if len(sys.argv) > 4 else 100000
        print(f'func {s:#x}..{e:#x} ({(e - s) // 4} insns)')
        im.dis(s, e, only_notes=(cmd == 'strs'), limit=mx)
    elif cmd == 'sym':
        # rx.py <dump>/sdk.bin sym <regex over exported names> [insns]   (uses <mod>.exports.txt beside the image)
        import re
        rx, n = re.compile(sys.argv[3]), int(sys.argv[4]) if len(sys.argv) > 4 else 24
        for line in open(im.path[:-4] + '.exports.txt'):
            a, name = line.split()
            if rx.search(name):
                print(f'{a} {name}')
                a = int(a, 16)
                for ins in im.md.disasm(im.b[a:a + n * 4], a):
                    print(f'  {ins.address:#010x}: {ins.mnemonic:7} {ins.op_str}')
                    if ins.mnemonic in ('ret', 'retab', 'retaa', 'br'):
                        break
    elif cmd == 'grep':
        # rx.py <img> grep <0xstart> <0xend> <regex over "mnemonic ops"> [context]
        import re
        s, e, rx = int(sys.argv[3], 16), int(sys.argv[4], 16), re.compile(sys.argv[5])
        ctx = int(sys.argv[6]) if len(sys.argv) > 6 else 0
        prev = []
        for ins in im.md.disasm(im.b[s:e], s):
            line = f'{ins.address:#010x}: {ins.mnemonic} {ins.op_str}'
            if rx.search(f'{ins.mnemonic} {ins.op_str}'):
                for p in prev[-ctx:] if ctx else []:
                    print('   ', p)
                print('>>>', line, f'(func {im.func_start(ins.address):#x})')
            prev.append(line)
            if len(prev) > 16:
                prev.pop(0)
    elif cmd == 'callers':
        t = int(sys.argv[3], 16)
        n = im.text_end // 4
        for i, w in enumerate(struct.unpack_from(f'<{n}I', im.b)):
            if (w >> 26) in (0b000101, 0b100101):
                d = ((w & 0x1ffffff) - (w & 0x2000000)) * 4
                if i * 4 + d == t:
                    print(f'{i * 4:#010x} {"bl" if w >> 31 else "b"} in func {im.func_start(i * 4):#010x}')


if __name__ == '__main__':
    main()
