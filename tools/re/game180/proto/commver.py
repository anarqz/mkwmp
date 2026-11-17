"""Find where the online manager's u16 "communication version" (the N of the matchmaking attribute rom_id = "cv%d")
is written with a constant.

usage: python -I commver.py <dump>/main.bin [0xoffset=0x78]
The getter is the function referencing "cv%d": it reads ldrh [mgr, #0x78]. This scans .text for
`strh wT, [xN, #off]` whose wT was loaded by a `mov wT, #imm` within the previous 8 instructions and prints them
with the enclosing function, so the handful of candidates can be checked by hand (rx.py dis).
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from rx import Img  # noqa: E402


def main():
    im = Img(sys.argv[1])
    off = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x78
    want = 0x79000000 | ((off // 2) << 10)
    n = im.text_end // 4
    words = struct.unpack_from(f'<{n}I', im.b)
    for i, w in enumerate(words):
        if (w & 0xFFFFFC00) != want:
            continue
        rt = w & 31
        for k in range(1, 9):
            p = words[i - k]
            # MOVZ Wd,#imm16 (sf=0, opc=10, hw=0): 0x52800000 | imm16<<5 | Rd
            if (p & 0xFFE00000) == 0x52800000 and (p & 31) == rt:
                print(f'{i * 4:#010x}: strh w{rt}, [x{(w >> 5) & 31}, #{off:#x}]  <- mov w{rt}, #{(p >> 5) & 0xFFFF} '
                      f'at {(i - k) * 4:#x} (func {im.func_start(i * 4):#x})')
                break
            if (p & 31) == rt and (p >> 24) not in (0x79, 0x39, 0xB9, 0xF9):
                break  # register redefined by something else


if __name__ == '__main__':
    main()
