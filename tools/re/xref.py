"""Find ADRP(+ADD) sites in an NSO .text that compute a target image address (string xref).

usage: python -I xref.py <nso> <hex target addr> [window]
"""
import os
import struct
import sys

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs

sys.path.insert(0, os.path.dirname(__file__))
from nsodump import Nso  # noqa: E402

n = Nso(sys.argv[1])
target = int(sys.argv[2], 16)
img = bytes(n.image)
# text range: from MOD0/dynamic we don't have seg bounds here; scan whole image for ADRP.
# ADRP: bits 31=1? opcode 1xx10000. We'll decode with capstone over the whole image in chunks.
md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
# track adrp results per register then match following add/ldr
hits = []
# scan in a sliding manner over likely text (0..first 0x3400000)
end = min(len(img), 0x3600000)
pager = {}
for off in range(0, end - 4, 4):
    w = struct.unpack_from("<I", img, off)[0]
    if (w & 0x9F000000) == 0x90000000:  # ADRP
        immlo = (w >> 29) & 3
        immhi = (w >> 5) & 0x7FFFF
        imm = ((immhi << 2) | immlo) << 12
        if imm & (1 << 32):
            imm -= 1 << 33
        page = (off & ~0xFFF) + imm
        rd = w & 31
        pager[rd] = (page, off)
    elif (w & 0xFF800000) == 0x91000000:  # ADD (immediate, 64-bit)
        rn = (w >> 5) & 31
        rd = w & 31
        imm12 = (w >> 10) & 0xFFF
        sh = (w >> 22) & 1
        add = imm12 << (12 if sh else 0)
        if rn in pager:
            page, poff = pager[rn]
            if page + add == target:
                hits.append(off)
for h in hits:
    print(f"xref at {h:#010x}")
print(f"{len(hits)} xref(s) to {target:#x}")
