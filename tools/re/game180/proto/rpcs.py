"""Inventory of the NPLN gRPC method paths embedded in main, with the code that references each, for two builds.

usage: python -I rpcs.py <dump 1.0.0> <dump 1.8.0>
Prints one line per path: presence in each build (+ number of ADRP+ADD references and the first referencing
function). Paths only in the second build are marked NEW; paths our Rust lobby dispatches (rust/lobby/src/core.rs)
are marked SERVED.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from rx import Img, REPO  # noqa: E402

PATH = re.compile(rb'(/nn\.npln\.[a-z]+\.v\d+\.[A-Za-z]+/[A-Za-z]+)\x00')


def paths(im):
    idx = im.index()
    out = {}
    for m in PATH.finditer(im.b):
        a = m.start(1)
        refs = idx.get(a, [])
        out[m.group(1).decode()] = (a, refs)
    return out


def main():
    a = Img(os.path.join(sys.argv[1], 'main.bin'))
    b = Img(os.path.join(sys.argv[2], 'main.bin'))
    pa, pb = paths(a), paths(b)
    served = set()
    core = os.path.join(REPO, 'rust', 'lobby', 'src', 'core.rs')
    if os.path.isfile(core):
        served = set(re.findall(r'"(/nn\.npln\.[^"]+)"', open(core, encoding='utf-8').read()))
    for p in sorted(set(pa) | set(pb)):
        def col(d, im):
            if p not in d:
                return '-'
            addr, refs = d[p]
            f = f'{im.func_start(refs[0]):#x}' if refs else 'noref'
            return f'{addr:#x} refs={len(refs)} stub={f}'
        tag = ('NEW ' if p not in pa else 'GONE' if p not in pb else '    ') + (' SERVED' if p in served else '')
        print(f'{tag:12} {p:85} 100[{col(pa, a)}]  180[{col(pb, b)}]')
    print(f'# 100: {len(pa)} paths, 180: {len(pb)} paths, served by rust lobby: {len(served)}')


if __name__ == '__main__':
    main()
