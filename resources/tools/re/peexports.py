import struct
import sys

d = open(sys.argv[1], 'rb').read()
pe = struct.unpack_from('<I', d, 0x3c)[0]
opt = pe + 24
nsec = struct.unpack_from('<H', d, pe + 6)[0]
osz = struct.unpack_from('<H', d, pe + 20)[0]
secs = [struct.unpack_from('<IIII', d, opt + osz + 40 * i + 8) for i in range(nsec)]


def r2o(rva):
    for vs, va, rs, ra in secs:
        if va <= rva < va + max(vs, rs):
            return rva - va + ra
    return None


er = struct.unpack_from('<I', d, opt + 112)[0]
o = r2o(er)
nf, nn = struct.unpack_from('<II', d, o + 20)
npp = struct.unpack_from('<I', d, o + 32)[0]
names = []
for i in range(nn):
    nr = struct.unpack_from('<I', d, r2o(npp) + 4 * i)[0]
    names.append(d[r2o(nr):d.index(b'\0', r2o(nr))].decode())
print('nfuncs', nf, 'nnames', nn, 'exports', names)
