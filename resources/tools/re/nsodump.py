"""Self-contained NSO dynsym dumper (no third-party code besides lz4).

usage: python -I nsodump.py <exefs dir> <regex> [--exports]
Prints imports (and optionally exports) of main/subsdk0/sdk whose names match <regex>.
"""
import re
import struct
import sys

import lz4.block

DT_NULL, DT_STRTAB, DT_SYMTAB, DT_HASH, DT_GNU_HASH = 0, 5, 6, 4, 0x6FFFFEF5


class Nso:
    def __init__(self, path):
        raw = open(path, 'rb').read()
        if raw[:4] != b'NSO0':
            raise ValueError(f'{path}: not an NSO')
        flags = struct.unpack_from('<I', raw, 0xC)[0]
        segs = []
        for i in range(3):
            file_off, mem_off, dec_size = struct.unpack_from('<III', raw, 0x10 + i * 0x10)
            comp_size = struct.unpack_from('<I', raw, 0x60 + i * 4)[0]
            data = raw[file_off:file_off + comp_size]
            if flags & (1 << i):
                data = lz4.block.decompress(data, uncompressed_size=dec_size)
            segs.append((mem_off, data))
        bss = struct.unpack_from('<I', raw, 0x3C)[0]
        size = (segs[2][0] + len(segs[2][1]) + bss + 0xFFF) & ~0xFFF
        self.image = bytearray(size)
        for off, d in segs:
            self.image[off:off + len(d)] = d
        self._dyn()

    def u32(self, o): return struct.unpack_from('<I', self.image, o)[0]
    def u64(self, o): return struct.unpack_from('<Q', self.image, o)[0]

    def cstr(self, o):
        return self.image[o:self.image.index(b'\0', o)].decode('utf-8', 'replace')

    def _dyn(self):
        mod0 = self.u32(4)
        dynamic = mod0 + struct.unpack_from('<i', self.image, mod0 + 4)[0]
        dyn, off = {}, dynamic
        while True:
            tag, val = self.u64(off), self.u64(off + 8)
            off += 16
            if tag == DT_NULL:
                break
            dyn.setdefault(tag, val)
        if DT_HASH in dyn:
            n = self.u32(dyn[DT_HASH] + 4)
        else:
            h = dyn[DT_GNU_HASH]
            nb, symoff, bloom = self.u32(h), self.u32(h + 4), self.u32(h + 8)
            buckets = h + 16 + bloom * 8
            last = max([self.u32(buckets + 4 * i) for i in range(nb)] + [0])
            if last < symoff:
                n = symoff
            else:
                chains = buckets + 4 * nb
                while not self.u32(chains + 4 * (last - symoff)) & 1:
                    last += 1
                n = last + 1
        st, strtab = dyn[DT_SYMTAB], dyn[DT_STRTAB]
        self.syms = []
        for i in range(n):
            name, info, other, shndx = struct.unpack_from('<IBBH', self.image, st + i * 24)
            value = self.u64(st + i * 24 + 8)
            self.syms.append((self.cstr(strtab + name), shndx, value))


def main():
    exefs, rx = sys.argv[1], re.compile(sys.argv[2])
    want_exp = '--exports' in sys.argv
    for mod in ('main', 'subsdk0', 'sdk'):
        n = Nso(f'{exefs}/{mod}')
        imps = sorted(s[0] for s in n.syms if s[0] and s[1] == 0)
        exps = sorted((s[0], s[2]) for s in n.syms if s[0] and s[1] != 0)
        print(f'===== {mod}: {len(imps)} imports, {len(exps)} exports')
        for name in imps:
            if rx.search(name):
                print('  IMP', name)
        if want_exp:
            for name, val in exps:
                if rx.search(name):
                    print(f'  EXP {val:#010x} {name}')


if __name__ == '__main__':
    main()
