"""Side-by-side disassembly of a code range in two builds (to check a ported address by eye).

usage: python -I fdiff.py <old exefs|main> <new exefs|main> <old hex addr> <new hex addr> [count=40] [--cache DIR]
       python -I fdiff.py <exefs|main> - <hex addr> - [count]        (one build only)
Lines whose L1-normalised words differ are marked '|', L2-different ones '#'.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gamebin import load  # noqa: E402

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs  # noqa: E402

md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)


def lines(g, addr, count):
    out = []
    code = g.image[addr:addr + count * 4]
    for k in range(count):
        a = addr + k * 4
        ins = next(md.disasm(code[k * 4:k * 4 + 4], a), None)
        s = f'{ins.mnemonic} {ins.op_str}' if ins else f'.word {g.u32(a):#x}'
        mark = '>' if a in g.funcset else ' '
        out.append(f'{mark}{a:08x} {s}')
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    cache = sys.argv[sys.argv.index('--cache') + 1] if '--cache' in sys.argv else None
    if cache:
        args.remove(cache)
    count = int(args[4]) if len(args) > 4 else 40
    old = load(args[0], cache)
    old.funcset = set(old.funcs)
    oa = int(args[2], 16)
    if args[1] == '-':
        print('\n'.join(lines(old, oa, count)))
        return
    new = load(args[1], cache)
    new.funcset = set(new.funcs)
    na = int(args[3], 16)
    lo, ln = lines(old, oa, count), lines(new, na, count)
    for k in range(count):
        i, j = old.idx(oa) + k, new.idx(na) + k
        m = ' ' if old.l1[i] == new.l1[j] else ('|' if old.l2[i] == new.l2[j] else '#')
        print(f'{lo[k]:<58}{m} {ln[k]}')


if __name__ == '__main__':
    main()
