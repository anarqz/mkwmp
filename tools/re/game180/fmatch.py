"""Function matcher between two builds of the game's main module (a small BinDiff).

The 1.0.0 -> 1.8.0 update was built with a different compiler revision: instructions are re-scheduled inside
basic blocks, virtual slots and struct offsets move. Byte signatures therefore only cover part of the code; this
matcher pairs whole functions instead and propagates along the call graph and data references.

usage: python -I fmatch.py <old exefs|main> <new exefs|main> --cache DIR [addr ...]
  Builds (and caches in DIR) the match table, prints coverage, then for each <addr> (hex, old build) the match.

Passes (each accepted pair is 1:1; `how` records the pass):
  str    function is the only one in its build referencing a given set of strings (same text in both builds)
  l1     identical L1-normalised instruction sequence, unique in both builds
  l2     identical L2-normalised sequence, unique in both
  bag    identical multiset of L2 words (re-scheduled instructions), unique in both
  call   k-th direct callee of a matched pair (same number of BLs on both sides)
  callx  callee sequences of a matched pair aligned on their already-matched members; unmatched ones in gaps of
         equal length are paired
  vt     same slot of a matched pointer table (vtable) when both tables have the same length
  ref    function address taken (ADRP+ADD / GOT) at the same position of an l1/l2-identical pair
Data map: the k-th ADRP-relative target of an l1/l2-identical pair (and what a GOT slot points at) gives
old data address -> new data address votes (globals, vtables).

API: m = Matcher(old, new, cache_dir); m.fn(old_addr) -> (new_start, how) | None; m.data(old_addr) -> {new: votes}
"""
import collections
import os
import pickle
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gamebin import load  # noqa: E402

PACIBSP = 0xD503237F


class Fn:
    __slots__ = ('start', 'end', 'calls', 'refs', 'strs', 'h1', 'h2', 'bag', 'n')


def analyse(g):
    """-> {start: Fn} with calls (BL targets in order), refs ((kind, target) in order), strings, hashes."""
    words, l1, l2, t0 = g.words, g.l1, g.l2, g.text_off
    n = len(words)
    starts = set(g.funcs)
    for i, w in enumerate(words):
        if w == PACIBSP:
            starts.add(t0 + i * 4)
    starts = sorted(s for s in starts if t0 <= s < t0 + n * 4)
    g.funcs = starts
    fns = {}
    image, ro0, ro1 = g.image, g.ro_off, g.ro_off + g.ro_size
    relocs = g.relocs
    for k, s in enumerate(starts):
        e = starts[k + 1] if k + 1 < len(starts) else t0 + n * 4
        a, b = (s - t0) // 4, (e - t0) // 4
        # drop trailing padding / literal zeros
        while b > a and words[b - 1] == 0:
            b -= 1
        f = Fn()
        f.start, f.end, f.n = s, t0 + b * 4, b - a
        calls, refs, strs = [], [], []
        page = {}
        for i in range(a, b):
            w = words[i]
            if (w & 0xFC000000) == 0x94000000:
                calls.append(t0 + (i + (w & 0x3FFFFFF) - ((w & 0x2000000) << 1)) * 4)
            elif (w & 0x9F000000) == 0x90000000:
                imm = (((w >> 5) & 0x7FFFF) << 2 | (w >> 29) & 3) << 12
                if imm & (1 << 32):
                    imm -= 1 << 33
                page[w & 31] = (((t0 + i * 4) & ~0xFFF) + imm, i)
            elif (w & 0xFFC00000) == 0x91000000:
                rn = (w >> 5) & 31
                p = page.get(rn)
                if p and i - p[1] <= 8:
                    t = p[0] + ((w >> 10) & 0xFFF)
                    refs.append(('a', t))
                    if ro0 <= t < ro1:
                        strs.append(t)
            elif (w & 0x3B000000) == 0x39000000:
                rn = (w >> 5) & 31
                p = page.get(rn)
                if p and i - p[1] <= 8:
                    size = w >> 30
                    if (w >> 26) & 1 and (w >> 23) & 1:  # 128-bit simd
                        size = 4
                    t = p[0] + (((w >> 10) & 0xFFF) << size)
                    if t in relocs:
                        refs.append(('g', relocs[t]))
                        if ro0 <= relocs[t] < ro1:
                            strs.append(relocs[t])
                    else:
                        refs.append(('m', t))
        f.calls, f.refs, f.strs = calls, refs, strs
        f.h1 = hash(l1[a:b].tobytes())
        f.h2 = hash(l2[a:b].tobytes())
        f.bag = hash(tuple(sorted(l2[a:b])))
        fns[s] = f
    return fns


def _cstr(g, a):
    e = g.image.find(b'\0', a, a + 200)
    if e < 0 or e - a < 6:
        return None
    s = g.image[a:e]
    if any(c < 0x20 or c > 0x7e for c in s):
        return None
    return s


class Matcher:
    def __init__(self, old, new, cache_dir=None):
        self.old, self.new = old, new
        self.fo, self.fn_ = self._analysed(old, cache_dir), self._analysed(new, cache_dir)
        self.m = {}       # old start -> (new start, how)
        self.rev = {}
        self.dmap = collections.defaultdict(collections.Counter)
        self._run()

    @staticmethod
    def _analysed(g, cache_dir):
        cache = cache_dir and os.path.join(cache_dir, f'fana-{g.build_id[:16]}.pkl')
        if cache and os.path.exists(cache):
            with open(cache, 'rb') as f:
                fns, g.funcs = pickle.load(f)
            return fns
        fns = analyse(g)
        if cache:
            with open(cache, 'wb') as f:
                pickle.dump((fns, g.funcs), f, 4)
        return fns

    # ---- passes ----------------------------------------------------------------------------------------
    def _accept(self, a, b, how):
        if a in self.m or b in self.rev or a not in self.fo or b not in self.fn_:
            return False
        self.m[a] = (b, how)
        self.rev[b] = a
        self.queue.append(a)
        return True

    def _unique(self, key, how):
        ko, kn = collections.defaultdict(list), collections.defaultdict(list)
        for s, f in self.fo.items():
            if s not in self.m and f.n >= 6:
                ko[key(f)].append(s)
        for s, f in self.fn_.items():
            if s not in self.rev and f.n >= 6:
                kn[key(f)].append(s)
        c = 0
        for k, lo in ko.items():
            ln = kn.get(k)
            if k is not None and ln and len(lo) == 1 and len(ln) == 1:
                c += self._accept(lo[0], ln[0], how)
        return c

    def _strings(self):
        def table(g, fns):
            by = collections.defaultdict(set)
            for s, f in fns.items():
                for a in f.strs:
                    t = _cstr(g, a)
                    if t:
                        by[t].add(s)
            return by
        so, sn = table(self.old, self.fo), table(self.new, self.fn_)
        c = 0
        for t, fo in so.items():
            fn = sn.get(t)
            if fn and len(fo) == 1 and len(fn) == 1:
                c += self._accept(next(iter(fo)), next(iter(fn)), 'str')
        return c

    def _tables(self, g):
        """pointer tables in data: {table start: [text targets]} (maximal runs of relocated text pointers)."""
        t0, t1 = g.text_off, g.text_off + g.text_size
        slots = sorted(a for a, v in g.relocs.items() if t0 <= v < t1)
        tabs, run = {}, []
        for a in slots:
            if run and a != run[-1] + 8:
                tabs[run[0]] = [g.relocs[x] for x in run]
                run = []
            run.append(a)
        if run:
            tabs[run[0]] = [g.relocs[x] for x in run]
        return tabs

    def _propagate(self):
        fo, fn = self.fo, self.fn_
        c = 0
        while self.queue:
            a = self.queue.pop()
            b, how = self.m[a]
            f, g = fo[a], fn[b]
            if len(f.calls) == len(g.calls):
                for x, y in zip(f.calls, g.calls):
                    c += self._accept(x, y, 'call')
            else:
                c += self._align_calls(f.calls, g.calls)
            if how in ('l1', 'l2', 'str', 'call', 'vt', 'ref', 'callx', 'bag') and f.h2 == g.h2 and \
                    len(f.refs) == len(g.refs):
                for (k1, x), (k2, y) in zip(f.refs, g.refs):
                    if k1 != k2:
                        continue
                    self.dmap[x][y] += 1
                    if x in fo and y in fn:
                        c += self._accept(x, y, 'ref')
        return c

    def _align_calls(self, xs, ys):
        # anchors: positions whose callees are already matched to each other, in increasing order (greedy LCS)
        c, j0, anchors = 0, 0, []
        for i, x in enumerate(xs):
            mx = self.m.get(x)
            if not mx:
                continue
            for j in range(j0, len(ys)):
                if ys[j] == mx[0]:
                    anchors.append((i, j))
                    j0 = j + 1
                    break
        anchors = [(-1, -1)] + anchors + [(len(xs), len(ys))]
        for (i0, j0), (i1, j1) in zip(anchors, anchors[1:]):
            if i1 - i0 == j1 - j0:
                for d in range(1, i1 - i0):
                    c += self._accept(xs[i0 + d], ys[j0 + d], 'callx')
        return c

    def _vtables(self):
        to, tn = self._tables(self.old), self._tables(self.new)
        self.tabs_old, self.tabs_new = to, tn
        c = 0
        # tables paired through the data map (same start) or through a majority of matched slots
        for a, slots in to.items():
            cand = self.dmap.get(a)
            if not cand:
                continue
            b = max(cand, key=cand.get)
            ns = tn.get(b)
            if ns and len(ns) == len(slots):
                for x, y in zip(slots, ns):
                    c += self._accept(x, y, 'vt')
        return c

    def _bag(self, g, f):
        return collections.Counter(g.l2[g.idx(f.start):g.idx(f.start) + f.n])

    def _sim(self, f, h):
        if not f.n or not h.n or not 0.5 <= f.n / h.n <= 2.0:
            return 0.0
        x, y = self._bag(self.old, f), self._bag(self.new, h)
        return sum((x & y).values()) / max(f.n, h.n)

    def _order(self, max_gap=48, min_sim=0.45):
        """Functions keep their link order in most of the image: align the unmatched ones between two matched
        neighbours (old order) whose images are also in order, by L2-bag similarity (monotone DP)."""
        fo_list, fn_list = self.old.funcs, self.new.funcs
        pos_new = {s: i for i, s in enumerate(fn_list)}
        c = 0
        prev = None
        for i, a in enumerate(fo_list):
            r = self.m.get(a)
            if not r:
                continue
            j = pos_new[r[0]]
            if prev is not None:
                pi, pj = prev
                go, gn = i - pi - 1, j - pj - 1
                if 0 < go <= max_gap and 0 < gn <= max_gap:
                    xs = [fo_list[k] for k in range(pi + 1, i) if fo_list[k] not in self.m]
                    ys = [fn_list[k] for k in range(pj + 1, j) if fn_list[k] not in self.rev]
                    if xs and ys:
                        c += self._dp(xs, ys, min_sim)
            prev = (i, j)
        return c

    def _dp(self, xs, ys, min_sim):
        n, m = len(xs), len(ys)
        sim = [[self._sim(self.fo[x], self.fn_[y]) for y in ys] for x in xs]
        best = [[0.0] * (m + 1) for _ in range(n + 1)]
        for i in range(n - 1, -1, -1):
            for j in range(m - 1, -1, -1):
                v = max(best[i + 1][j], best[i][j + 1])
                if sim[i][j] >= min_sim:
                    v = max(v, best[i + 1][j + 1] + sim[i][j])
                best[i][j] = v
        i = j = c = 0
        while i < n and j < m:
            if sim[i][j] >= min_sim and best[i][j] == best[i + 1][j + 1] + sim[i][j]:
                c += self._accept(xs[i], ys[j], 'ord')
                i += 1
                j += 1
            elif best[i][j] == best[i + 1][j]:
                i += 1
            else:
                j += 1
        return c

    def _run(self):
        self.queue = []
        print('functions', len(self.fo), len(self.fn_), file=sys.stderr)
        print('str', self._strings(), file=sys.stderr)
        print('l1', self._unique(lambda f: f.h1 if f.n >= 8 else None, 'l1'), file=sys.stderr)
        print('l2', self._unique(lambda f: f.h2 if f.n >= 10 else None, 'l2'), file=sys.stderr)
        print('bag', self._unique(lambda f: f.bag if f.n >= 16 else None, 'bag'), file=sys.stderr)
        for rnd in range(16):
            c = self._propagate()
            v = self._vtables()
            c2 = self._realign()
            o = self._order() if not (c or v or c2) or rnd >= 1 else 0
            print(f'round {rnd}: propagate {c} vt {v} realign {c2} order {o} total {len(self.m)}', file=sys.stderr)
            if not c and not v and not c2 and not o:
                break

    def _realign(self):
        c = 0
        for a, (b, _) in list(self.m.items()):
            f, g = self.fo[a], self.fn_[b]
            if len(f.calls) != len(g.calls):
                c += self._align_calls(f.calls, g.calls)
            else:
                for x, y in zip(f.calls, g.calls):
                    c += self._accept(x, y, 'call')
        return c

    # ---- queries ---------------------------------------------------------------------------------------
    def fn(self, addr):
        s, _ = self.old.func_of(addr)
        r = self.m.get(s)
        return (s, r[0], r[1]) if r else (s, None, None)

    def data(self, addr):
        return self.dmap.get(addr, {})

    def _callers(self):
        if not hasattr(self, 'co'):
            self.co, self.cn = collections.defaultdict(set), collections.defaultdict(set)
            for s, f in self.fo.items():
                for t in f.calls:
                    self.co[t].add(s)
            for s, f in self.fn_.items():
                for t in f.calls:
                    self.cn[t].add(s)
        return self.co, self.cn

    def evidence(self, a, b):
        """Independent support for old fn a == new fn b: (bag similarity, callees agreeing/known, callers agreeing/known,
        pointer-table slots agreeing)."""
        f, g = self.fo[a], self.fn_[b]
        sim = self._sim(f, g)
        known = [self.m[x][0] for x in set(f.calls) if x in self.m]
        callee = sum(1 for y in known if y in set(g.calls))
        co, cn = self._callers()
        kc = [self.m[x][0] for x in co.get(a, ()) if x in self.m]
        caller = sum(1 for y in kc if y in cn.get(b, ()))
        return sim, (callee, len(known)), (caller, len(kc))

    def slots(self, g, addr):
        """[(table start, slot index)] of pointer-table slots holding addr."""
        if not hasattr(g, 'slot_index'):
            g.slot_index = collections.defaultdict(list)
            t0, t1 = g.text_off, g.text_off + g.text_size
            ptrs = {a: v for a, v in g.relocs.items() if t0 <= v < t1}
            for a, v in ptrs.items():
                s = a
                while s - 8 in ptrs:
                    s -= 8
                g.slot_index[v].append((s, (a - s) // 8))
        return g.slot_index.get(addr, [])


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    cache = sys.argv[sys.argv.index('--cache') + 1] if '--cache' in sys.argv else None
    if cache:
        args.remove(cache)
    old, new = load(args[0], cache), load(args[1], cache)
    m = Matcher(old, new, cache)
    hows = collections.Counter(h for _, h in m.m.values())
    print(f'matched {len(m.m)} of {len(m.fo)} old / {len(m.fn_)} new functions: {dict(hows)}')
    for tok in args[2:]:
        a = int(tok, 16) & 0xFFFFFFFF if len(tok) > 9 else int(tok, 16)
        if a >= old.text_end:
            print(f'{a:#x} data -> ' + ', '.join(f'{k:#x} x{v}' for k, v in sorted(m.data(a).items(), key=lambda kv: -kv[1])))
            continue
        s, ns, how = m.fn(a)
        fo = m.fo[s]
        if ns is None:
            print(f'{a:#x} (fn {s:#x}, {fo.n} insns) -> unmatched')
        else:
            fn = m.fn_[ns]
            same = 'L1-identical' if fo.h1 == fn.h1 else 'L2-identical' if fo.h2 == fn.h2 else \
                'same bag' if fo.bag == fn.bag else f'differs ({fo.n} vs {fn.n} insns)'
            sim, ce, cr = m.evidence(s, ns)
            so = ' '.join(f'{t:#x}[{k}]' for t, k in m.slots(old, s)[:3])
            sn = ' '.join(f'{t:#x}[{k}]' for t, k in m.slots(new, ns)[:3])
            print(f'{a:#x} (fn {s:#x}+{a - s:#x}) -> fn {ns:#x} [{how}] {same}; sim {sim:.2f} callees {ce[0]}/{ce[1]} '
                  f'callers {cr[0]}/{cr[1]}; slots old {so or "-"} new {sn or "-"}')


if __name__ == '__main__':
    import fmatch  # so that pickled Fn objects belong to the module, not __main__
    fmatch.main()
