#!/usr/bin/env python3
"""Find ADRP + ADD/LDR pairs resolving into [start, end) in a base-0 ARM64 image.

    python -I tools/re/adrp_refs.py scratch/main.bin 0x292f9c0 0x292fa00
"""
import struct
import sys


def main():
    path, start, end = sys.argv[1], int(sys.argv[2], 0), int(sys.argv[3], 0)
    data = open(path, "rb").read()
    words = struct.unpack_from(f"<{len(data) // 4}I", data)
    for i, w in enumerate(words):
        if (w & 0x9F000000) != 0x90000000:
            continue
        pc = i * 4
        imm = (((w >> 5) & 0x7FFFF) << 2) | ((w >> 29) & 3)
        if imm & (1 << 20):
            imm -= 1 << 21
        page = (pc & ~0xFFF) + imm * 4096
        reg = w & 31
        for j in range(i + 1, min(i + 9, len(words))):
            op = words[j]
            if ((op >> 5) & 31) != reg:
                if (op & 31) == reg:
                    break
                continue
            scaled = (op >> 10) & 0xFFF
            if (op & 0xFF800000) == 0x91000000:
                off = scaled << (12 if (op >> 22) & 1 else 0)
            elif (op & 0xFFC00000) == 0xF9400000:
                off = scaled * 8
            elif (op & 0xFFC00000) == 0xB9400000:
                off = scaled * 4
            elif (op & 0xFFC00000) == 0x3DC00000:  # LDR Qt, [Xn, #imm]
                off = scaled * 16
            elif (op & 0xFFC00000) == 0xAD400000:  # LDP Qt1, Qt2, [Xn, #imm]
                imm7 = (op >> 15) & 0x7F
                off = (imm7 - 128 if imm7 & 64 else imm7) * 16
            else:
                continue
            target = page + off
            if start <= target < end:
                print(f"{j * 4:08x} -> {target:08x} (ADRP {pc:08x})")
            break


if __name__ == "__main__":
    main()
