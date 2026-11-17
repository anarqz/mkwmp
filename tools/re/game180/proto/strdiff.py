"""Diff the string sets of one module between two dumps (dump.py output), filtered by a regex.

usage: python -I strdiff.py <dump 1.0.0> <dump 1.8.0> <module[:module180]> <regex> [--all] [--min N] [-i]
Prints '- ' strings only in the first build, '+ ' only in the second, and with --all also '  ' common ones.
Offsets are image offsets of the first occurrence in the build that has the string.
"""
import os
import re
import sys


def load(d, mod):
    out = {}
    for line in open(os.path.join(d, mod + '.strings.txt'), encoding='utf-8'):
        off, s = line.rstrip('\n').split('\t', 1)
        out.setdefault(s, off)
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    d1, d2, mod, rx = args[:4]
    m1, _, m2 = mod.partition(':')
    flags = re.I if '-i' in sys.argv else 0
    rx = re.compile(rx, flags)
    a, b = load(d1, m1), load(d2, m2 or m1)
    ka = {s for s in a if rx.search(s)}
    kb = {s for s in b if rx.search(s)}
    for s in sorted(ka - kb):
        print(f'- {a[s]} {s}')
    for s in sorted(kb - ka):
        print(f'+ {b[s]} {s}')
    if '--all' in sys.argv:
        for s in sorted(ka & kb):
            print(f'  {a[s]}/{b[s]} {s}')
    print(f'# only-100={len(ka - kb)} only-180={len(kb - ka)} common={len(ka & kb)}', file=sys.stderr)


if __name__ == '__main__':
    main()
