"""Compare the immediates (struct offsets, sizes, constants) of matched functions in two builds.

usage: python -I fnconsts.py <old exefs|main> <new exefs|main> --cache DIR [--min HEX] <old fn addr>[:<new fn addr>] ...
Without ':<new>' the new function comes from fmatch. Prints the immediates only in old, only in new, and (with
--all) the common ones. An offset that the probe reads through a guest pointer is unchanged when it is "common"
in the functions that access the field, and has moved when it shows up as an old-only / new-only pair.
"""
import collections
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gamebin import load  # noqa: E402
from fmatch import Matcher  # noqa: E402

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs  # noqa: E402
from capstone.arm64_const import ARM64_OP_IMM, ARM64_OP_MEM  # noqa: E402

md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
md.detail = True
SKIP = {'b', 'bl', 'cbz', 'cbnz', 'tbz', 'tbnz', 'adrp', 'adr'}


def consts(g, start, n):
    out = collections.Counter()
    pending = {}
    for ins in md.disasm(g.image[start:start + n * 4], start):
        m = ins.mnemonic
        if m in SKIP or m.startswith('b.'):
            continue
        for op in ins.operands:
            if op.type == ARM64_OP_IMM:
                v = op.imm & 0xFFFFFFFFFFFFFFFF
                if m == 'movk':
                    sh = op.shift.value
                    r = ins.reg_name(ins.operands[0].reg)
                    if r in pending:
                        out[pending[r]] -= 1
                        v = pending[r] | (v << sh)
                        pending[r] = v
                elif m in ('mov', 'movz'):
                    pending[ins.reg_name(ins.operands[0].reg)] = v
                out[v] += 1
            elif op.type == ARM64_OP_MEM and op.mem.disp and ins.reg_name(op.mem.base) not in ('sp', 'x29'):
                out[op.mem.disp & 0xFFFFFFFFFFFFFFFF] += 1
    return +out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    cache = sys.argv[sys.argv.index('--cache') + 1] if '--cache' in sys.argv else None
    lo = int(sys.argv[sys.argv.index('--min') + 1], 16) if '--min' in sys.argv else 0x20
    for x in (cache, sys.argv[sys.argv.index('--min') + 1] if '--min' in sys.argv else None):
        if x in args:
            args.remove(x)
    old, new = load(args[0], cache), load(args[1], cache)
    m = Matcher(old, new, cache)
    for tok in args[2:]:
        a, _, b = tok.partition(':')
        a = int(a, 16)
        if b:
            b, how = int(b, 16), 'given'
        else:
            r = m.m.get(a)
            if not r:
                print(f'{a:#x}: unmatched')
                continue
            b, how = r
        co, cn = consts(old, a, m.fo[a].n), consts(new, b, m.fn_[b].n)
        fmt = lambda c, keys: ' '.join(f'{k:#x}' + (f'x{c[k]}' if c[k] > 1 else '') for k in sorted(keys) if k >= lo)
        print(f'{a:#x} ({m.fo[a].n} insns) -> {b:#x} ({m.fn_[b].n} insns) [{how}]')
        print('   old only:', fmt(co, [k for k in co if co[k] != cn.get(k, 0)]))
        print('   new only:', fmt(cn, [k for k in cn if cn[k] != co.get(k, 0)]))
        if '--all' in sys.argv:
            print('   common  :', fmt(co, [k for k in co if k in cn]))


if __name__ == '__main__':
    main()
