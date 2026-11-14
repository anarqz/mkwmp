#!/usr/bin/env python3
"""List printable C strings referenced (ADRP+ADD) by code in [start, end).

    python -I tools/re/code_strings.py scratch/main.bin 0x1267000 0x1280000
"""
import struct
import sys


def main():
    path, start, end = sys.argv[1], int(sys.argv[2], 0), int(sys.argv[3], 0)
    data = open(path, "rb").read()
    words = struct.unpack_from(f"<{(end - start) // 4}I", data, start)
    for k, w in enumerate(words):
        if (w & 0x9F000000) != 0x90000000:
            continue
        pc = start + k * 4
        imm = (((w >> 5) & 0x7FFFF) << 2) | ((w >> 29) & 3)
        if imm & (1 << 20):
            imm -= 1 << 21
        page = (pc & ~0xFFF) + imm * 4096
        reg = w & 31
        for j in range(k + 1, min(k + 6, len(words))):
            op = words[j]
            if (op & 0xFF800000) == 0x91000000 and ((op >> 5) & 31) == reg:
                target = page + ((op >> 10) & 0xFFF)
                if 0 <= target < len(data):
                    e = data.find(b"\0", target, target + 300)
                    s = data[target:e] if e > target else b""
                    if len(s) >= 4 and all(32 <= c < 127 for c in s):
                        print(f"{pc:08x} {s.decode()[:160]}")
                break


if __name__ == "__main__":
    main()
