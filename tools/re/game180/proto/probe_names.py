"""Check every SDK symbol name the probe registers/looks up against the import/export tables of two game builds.

usage: python -I probe_names.py <dump dir 1.0.0> <dump dir 1.8.0> [probe src dir]
(dump dirs are produced by dump.py). Prints a table: file:line, name, which modules import it (I) / export it (E)
in each build, and a verdict. Then lists near-miss candidates (same qualified function name, other signature).
"""
import glob
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..'))
MODS = ('main', 'subsdk0', 'subsdk1', 'sdk')


def load(d):
    imp, exp = {}, {}
    for m in MODS:
        p = os.path.join(d, m + '.imports.txt')
        if not os.path.isfile(p):
            continue
        for s in open(p).read().split():
            imp.setdefault(s, []).append(m)
        for line in open(os.path.join(d, m + '.exports.txt')):
            exp.setdefault(line.split()[1], []).append(m)
    return imp, exp


def where(name, tabs):
    imp, exp = tabs
    r = [f'I:{m}' for m in imp.get(name, [])] + [f'E:{m}' for m in exp.get(name, [])]
    return ','.join(r) or '-'


def stem(name):
    """Qualified-name prefix of an Itanium mangled name (up to the 'E' that closes the nested name)."""
    m = re.match(r'(_ZNK?(?:\d+[A-Za-z_]\w*?)+?)E', name)
    if not m:
        return name
    # walk length-prefixed components properly
    i = name.index('N') + 1
    if name[i] == 'K':
        i += 1
    while i < len(name) and name[i].isdigit():
        j = i
        while name[j].isdigit():
            j += 1
        i = j + int(name[i:j])
    return name[:i]


def main():
    a, b = load(sys.argv[1]), load(sys.argv[2])
    src = sys.argv[3] if len(sys.argv) > 3 else os.path.join(REPO, 'native', 'probe', 'src')
    seen = []
    for f in sorted(glob.glob(os.path.join(src, '*.[ch]'))):
        for ln, line in enumerate(open(f, encoding='utf-8', errors='replace'), 1):
            for m in re.finditer(r'"((?:_Z|nnssl|nnsocket|pthread_|clock_|__errno)[A-Za-z0-9_]*)"', line):
                seen.append((os.path.basename(f), ln, m.group(1)))
    names = set()
    for f, ln, name in seen:
        if name.startswith('_ZN5ImGui') or name.startswith('_ZN10ImDrawList') or 'menu_notice' in name:
            continue  # host (recomp) symbols, not guest
        w1, w2 = where(name, a), where(name, b)
        i1, i2 = 'I:main' in w1, 'I:main' in w2
        if i1 and i2:
            v = 'ok'
        elif i1 and not i2:
            v = 'GONE-FROM-MAIN-180' + (' (still exported by sdk)' if 'E:sdk' in w2 else ' (NOT in sdk either)')
        elif not i1 and i2:
            v = 'new-in-180'
        else:
            v = 'not imported by main in either' + ('' if ('E:' in w1 or 'E:' in w2) else ' / NOT EXPORTED')
        print(f'{f}:{ln}\t{v}\t100[{w1}]\t180[{w2}]\t{name}')
        names.add(name)
    print('\n== near misses: 1.8.0 main imports with the same qualified name but another signature ==')
    for name in sorted(names):
        st = stem(name)
        for cand in sorted(b[0]):
            if cand != name and cand.startswith(st) and stem(cand) == st and 'main' in b[0][cand]:
                tag = 'also in 100' if 'main' in a[0].get(cand, []) else 'NEW in 180'
                print(f'{name}\n    -> {cand}  [{tag}]')


if __name__ == '__main__':
    main()
