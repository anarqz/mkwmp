"""Port main-module addresses from one game build to another by masked code signatures.

usage: python -I sigport.py <old exefs|main> <new exefs|main> <addrs.txt | hex,hex,...> [--cache DIR] [--tsv]

addrs.txt: one address per line, `<hex main offset or 0x70........> [kind] [# comment]`; kind is free text that is
echoed. Lines starting with # are ignored.

For each address A (anywhere in .text: a function entry, a mid-function pc, a return address):
  1. window search: the L1-normalised words [A, A+n) (n grows 6..64; clipped to the containing function, and
     extended backwards when A is near the function end) must occur exactly once in old and exactly once in new;
  2. if no L1 window works, the same with L2 (immediates masked too);
  3. the containing function is compared whole (L1 and L2): `fn=identical` means every instruction is the same up
     to relocated targets, so every struct offset the function uses is unchanged.
Confidence: exact  = unique L1 window and whole function L1-identical
            high   = unique L1 window (function differs elsewhere) or unique L2 window + L2-identical function
            medium = unique L2 window only
            none   = not found
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gamebin import load  # noqa: E402

WINDOWS = (6, 8, 12, 16, 24, 32, 48, 64, 96)


def port_window(old, new, addr, level):
    hay_old = old.l1b if level == 1 else old.l2b
    hay_new = new.l1b if level == 1 else new.l2b
    fs, fe = old.func_of(addr)
    for n in WINDOWS:
        a = addr
        if a + n * 4 > fe:  # slide back inside the function
            a = max(fs, fe - n * 4)
        if a + n * 4 > fe and n > WINDOWS[0]:
            # function shorter than the window: allow running into the next function
            a = min(addr, a)
        lo = a - old.text_off
        needle = hay_old[lo:lo + n * 4]
        if len(needle) < n * 4:
            break
        if len(old.find_all(hay_old, needle, 2)) != 1:
            continue
        hits = new.find_all(hay_new, needle, 2)
        if len(hits) == 1:
            return hits[0] + (addr - a), n
        if not hits:
            # a bigger window cannot match either, but a window placed elsewhere might: try backwards from A
            for m in WINDOWS:
                b = max(fs, addr - m * 4)
                lo = b - old.text_off
                needle = hay_old[lo:lo + (addr - b) + 16]
                if len(needle) < 24 or len(old.find_all(hay_old, needle, 2)) != 1:
                    continue
                hits = new.find_all(hay_new, needle, 2)
                if len(hits) == 1:
                    return hits[0] + (addr - b), -m
            return None, 0
    return None, 0


def compare_function(old, new, old_addr, new_addr):
    """Whole-function comparison assuming the same offset inside the function."""
    fs, fe = old.func_of(old_addr)
    ns = new_addr - (old_addr - fs)
    nfs, nfe = new.func_of(ns)
    size = fe - fs
    a, b = old.idx(fs), new.idx(ns)
    n = size // 4
    if b < 0 or b + n > len(new.words):
        return 'fn=?', fs, ns
    same1 = old.l1[a:a + n] == new.l1[b:b + n]
    same2 = old.l2[a:a + n] == new.l2[b:b + n]
    start_ok = nfs == ns
    if same1 and start_ok and nfe - nfs == size:
        return 'fn=identical', fs, ns
    if same1:
        return 'fn=identical-body', fs, ns
    d1 = sum(1 for k in range(n) if old.l1[a + k] != new.l1[b + k])
    if same2:
        return f'fn=same-shape({d1} imm diffs/{n})', fs, ns
    return f'fn=differs(size {size:#x}->{nfe - nfs:#x}, {d1}/{n} L1 diffs at same offset)', fs, ns


def port(old, new, addr):
    na, n = port_window(old, new, addr, 1)
    level = 1
    if na is None:
        na, n = port_window(old, new, addr, 2)
        level = 2
    if na is None:
        fs, _ = old.func_of(addr)
        return dict(old=addr, new=None, conf='none', how='no unique window', old_fn=fs, new_fn=None)
    fn, fs, ns = compare_function(old, new, addr, na)
    if level == 1:
        conf = 'exact' if fn.startswith('fn=identical') else 'high'
    else:
        conf = 'high' if fn.startswith('fn=same-shape') else 'medium'
    return dict(old=addr, new=na, conf=conf, how=f'L{level} window {n} insns; {fn}', old_fn=fs, new_fn=ns)


def parse_addrs(arg):
    out = []
    if os.path.exists(arg):
        for line in open(arg, encoding='utf-8'):
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            head, _, comment = line.partition('#')
            parts = head.split()
            out.append((int(parts[0], 16) & 0xFFFFFFFF if len(parts[0]) > 10 else int(parts[0], 16),
                        ' '.join(parts[1:]), comment.strip()))
    else:
        for tok in arg.split(','):
            v = int(tok, 16)
            out.append((v - 0x7000000000 if v >= 0x7000000000 else v, '', ''))
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    cache = sys.argv[sys.argv.index('--cache') + 1] if '--cache' in sys.argv else None
    if cache:
        args.remove(cache)
    old, new = load(args[0], cache), load(args[1], cache)
    print(f'# old build id {old.build_id[:16]}  new build id {new.build_id[:16]}')
    for addr, kind, comment in parse_addrs(args[2]):
        if addr >= 0x7000000000:
            addr -= 0x7000000000
        r = port(old, new, addr)
        new_s = f'{r["new"]:#09x}' if r['new'] is not None else 'UNRESOLVED'
        nf = f'{r["new_fn"]:#09x}' if r['new_fn'] is not None else '?'
        if '--tsv' in sys.argv:
            print('\t'.join([f'{addr:#09x}', new_s, r['conf'], r['how'], f'{r["old_fn"]:#09x}', nf, kind, comment]))
        else:
            print(f'{addr:#09x} -> {new_s}  [{r["conf"]}] {r["how"]}  (fn {r["old_fn"]:#x} -> {nf})  {kind} {comment}')


if __name__ == '__main__':
    main()
