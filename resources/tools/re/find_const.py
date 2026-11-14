#!/usr/bin/env python3
"""Find ARM64 code that materializes a 32-bit constant via MOVZ+MOVK (W regs).

    python -I tools/re/find_const.py scratch/main.bin 0x180141

Also reports single-instruction MOVZ hits when the high half is zero.
Addresses are image offsets (base 0); add 0x7000000000 for live guest addresses.
"""
import struct
import sys


def main():
    path, value = sys.argv[1], int(sys.argv[2], 0)
    lo, hi = value & 0xFFFF, (value >> 16) & 0xFFFF
    data = open(path, "rb").read()
    words = struct.unpack_from(f"<{len(data) // 4}I", data)
    movz = {}
    hits = []
    for idx, w in enumerate(words):
        # MOVZ Wd, #imm16 (hw=0): 0x52800000 | imm16<<5 | Rd
        w &= 0x7FFFFFFF  # treat X-register (sf=1) forms like W forms
        if (w & 0xFFE00000) == 0x52800000 and ((w >> 5) & 0xFFFF) == lo:
            rd = w & 31
            if hi == 0:
                hits.append(idx * 4)
            else:
                movz[rd] = idx
        # MOVK Wd, #imm16, LSL #16: 0x72A00000 | imm16<<5 | Rd
        if hi and (w & 0xFFE00000) == 0x72A00000 and ((w >> 5) & 0xFFFF) == hi:
            rd = w & 31
            start = movz.get(rd)
            if start is not None and idx - start <= 8:
                hits.append(start * 4)
        # forget stale MOVZ records
        if movz and idx % 64 == 0:
            movz = {r: i for r, i in movz.items() if idx - i <= 8}
    for h in hits:
        print(hex(h))
    print(f"{len(hits)} hits", file=sys.stderr)


if __name__ == "__main__":
    main()
