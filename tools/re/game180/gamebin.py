"""Loaded view of a game NSO for cross-version porting (reuses resources/tools/re/nsodump.Nso).

Adds to Nso: text/rodata/data bounds, R_AARCH64_RELATIVE relocations applied (so vtables hold real image
offsets), a function-start set (BL targets + relocated pointers into .text), and two "normalised" copies of
.text for signature search:
  L1  relocation-sensitive bits masked (BL/B imm26, ADRP/ADR imm, the ADD/LDR/STR imm12 that completes an ADRP,
      LDR-literal imm19). Two functions that differ only by where their callees/globals moved are L1-equal.
  L2  additionally every branch displacement and every imm12/imm16/imm9/imm7 operand masked ("shape"). Survives
      struct-layout changes; used as fallback and to diff field offsets.

Run with:  .venv\\Scripts\\python.exe -I tools/re/game180/<script>.py ...   (.deps is added to sys.path here)
"""
import array
import bisect
import os
import pickle
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
if os.path.join(ROOT, '.deps') not in sys.path:
    sys.path.insert(0, os.path.join(ROOT, '.deps'))

# resources/tools/re holds a dis.py that would shadow the stdlib module capstone needs: load nsodump by path instead
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location('nsodump', os.path.join(ROOT, 'resources', 'tools', 're', 'nsodump.py'))
_nsodump = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_nsodump)
Nso = _nsodump.Nso

DT_RELA, DT_RELASZ, DT_RELR, DT_RELRSZ = 7, 8, 0x24, 0x23
R_AARCH64_RELATIVE = 0x403


def _norm(words):
    """-> (L1 array, L2 array) of masked instruction words."""
    l1 = array.array('I', words)
    l2 = array.array('I', words)
    adrp_at = [-100] * 32  # index of the last ADRP per register
    for i, w in enumerate(words):
        top = w >> 24
        if (w & 0x7C000000) == 0x14000000:  # B / BL
            l1[i] = l2[i] = w & 0xFC000000
        elif (w & 0x1F000000) == 0x10000000:  # ADR / ADRP
            l1[i] = l2[i] = w & 0x9F00001F
            if w & 0x80000000:
                adrp_at[w & 31] = i
        elif (w & 0x7F800000) == 0x11000000:  # ADD imm (32/64), sh=0
            rn = (w >> 5) & 31
            m = w & 0xFFC003FF
            if i - adrp_at[rn] <= 8 and top == 0x91:
                l1[i] = m
                rd = w & 31
                if rd != rn:
                    adrp_at[rd] = -100
            l2[i] = m
        elif (w & 0x3B000000) == 0x39000000:  # LDR/STR unsigned imm12 (int + simd)
            rn = (w >> 5) & 31
            m = w & 0xFFC003FF
            if i - adrp_at[rn] <= 8:
                l1[i] = m
            l2[i] = m
        elif (w & 0x3B000000) == 0x18000000:  # LDR literal
            l1[i] = l2[i] = w & 0xFF00001F
        elif (w & 0x7F000000) in (0x31000000, 0x51000000, 0x71000000) or (w & 0x7F800000) == 0x11400000:
            l2[i] = w & 0xFFC003FF  # ADDS/SUB/SUBS imm, ADD imm sh=1
        elif (w & 0xFF000010) == 0x54000000:  # B.cond
            l2[i] = w & 0xFF00001F
        elif (w & 0x7E000000) == 0x34000000:  # CBZ/CBNZ
            l2[i] = w & 0xFF00001F
        elif (w & 0x7E000000) == 0x36000000:  # TBZ/TBNZ
            l2[i] = w & 0xFFF8001F
        elif (w & 0x3A000000) == 0x28000000:  # LDP/STP (imm7)
            l2[i] = w & 0xFFC07FFF
        elif (w & 0x3B200000) == 0x38000000:  # LDUR/STUR/pre/post (imm9)
            l2[i] = w & 0xFFE00FFF
        elif (w & 0x1F800000) == 0x12800000:  # MOVN/MOVZ/MOVK imm16
            l2[i] = w & 0xFFE0001F
    return l1, l2


class Game:
    def __init__(self, path, cache_dir=None):
        self.path = path
        raw = open(path, 'rb').read(0x100)
        self.build_id = raw[0x40:0x60].hex()
        self.text_off, self.text_size = struct.unpack_from('<I', raw, 0x14)[0], struct.unpack_from('<I', raw, 0x18)[0]
        self.ro_off, self.ro_size = struct.unpack_from('<I', raw, 0x24)[0], struct.unpack_from('<I', raw, 0x28)[0]
        self.data_off, self.data_size = struct.unpack_from('<I', raw, 0x34)[0], struct.unpack_from('<I', raw, 0x38)[0]
        cache = cache_dir and os.path.join(cache_dir, f'gamebin-{self.build_id[:16]}.pkl')
        if cache and os.path.exists(cache):
            with open(cache, 'rb') as f:
                d = pickle.load(f)
            self.image, self.relocs, self.words, self.l1, self.l2, self.funcs = d
        else:
            nso = Nso(path)
            self.image = bytes(self._relocate(nso))
            n = self.text_size // 4
            self.words = array.array('I', self.image[self.text_off:self.text_off + n * 4])
            self.l1, self.l2 = _norm(self.words)
            self.funcs = self._functions()
            if cache:
                os.makedirs(cache_dir, exist_ok=True)
                with open(cache, 'wb') as f:
                    pickle.dump((self.image, self.relocs, self.words, self.l1, self.l2, self.funcs), f, 4)
        self.l1b, self.l2b = self.l1.tobytes(), self.l2.tobytes()
        self.text_end = self.text_off + self.text_size

    def _relocate(self, nso):
        img = nso.image
        mod0 = nso.u32(4)
        off = mod0 + struct.unpack_from('<i', img, mod0 + 4)[0]
        dyn = {}
        while True:
            tag, val = nso.u64(off), nso.u64(off + 8)
            off += 16
            if tag == 0:
                break
            dyn.setdefault(tag, val)
        self.relocs = {}
        rela, size = dyn.get(DT_RELA, 0), dyn.get(DT_RELASZ, 0)
        for o in range(rela, rela + size, 24):
            r_off, r_info, addend = struct.unpack_from('<QQq', img, o)
            if r_info & 0xFFFFFFFF == R_AARCH64_RELATIVE:
                struct.pack_into('<Q', img, r_off, addend)
                self.relocs[r_off] = addend
        # DT_RELR: packed relative relocations (addend is stored in place); main uses these for most pointers
        relr, size = dyn.get(DT_RELR, 0), dyn.get(DT_RELRSZ, 0)
        where = 0
        for o in range(relr, relr + size, 8):
            e = nso.u64(o)
            if not e & 1:
                self.relocs[e] = nso.u64(e)
                where = e + 8
            else:
                bits, k = e >> 1, 0
                while bits:
                    if bits & 1:
                        self.relocs[where + k * 8] = nso.u64(where + k * 8)
                    bits >>= 1
                    k += 1
                where += 63 * 8
        return img

    def _functions(self):
        starts = set()
        t0, n = self.text_off, len(self.words)
        for i, w in enumerate(self.words):
            if (w & 0xFC000000) == 0x94000000:
                d = (w & 0x3FFFFFF) - ((w & 0x2000000) << 1)
                t = i + d
                if 0 <= t < n:
                    starts.add(t0 + t * 4)
        for a in self.relocs.values():
            if t0 <= a < t0 + n * 4 and not a & 3:
                starts.add(a)
        return sorted(starts)

    # ---- helpers -------------------------------------------------------------------------------------------
    def func_of(self, addr):
        """(start, end) of the function-start interval containing addr (end = next known start)."""
        i = bisect.bisect_right(self.funcs, addr) - 1
        start = self.funcs[i] if i >= 0 else self.text_off
        end = self.funcs[i + 1] if i + 1 < len(self.funcs) else self.text_end
        return start, end

    def idx(self, addr):
        return (addr - self.text_off) // 4

    def find_all(self, hay, needle, limit=3):
        out, pos = [], 0
        while len(out) < limit:
            pos = hay.find(needle, pos)
            if pos < 0:
                break
            if not pos & 3:
                out.append(self.text_off + pos)
                pos += 4
            else:
                pos += 1
        return out

    def u32(self, o): return struct.unpack_from('<I', self.image, o)[0]
    def u64(self, o): return struct.unpack_from('<Q', self.image, o)[0]

    def cstr(self, o, maxlen=256):
        e = self.image.find(b'\0', o, o + maxlen)
        return self.image[o:e if e >= 0 else o + maxlen].decode('utf-8', 'replace')

    def bl_target(self, addr):
        w = self.words[self.idx(addr)]
        if (w & 0x7C000000) != 0x14000000:
            return None
        return addr + ((w & 0x3FFFFFF) - ((w & 0x2000000) << 1)) * 4


def load(path, cache_dir=None):
    if os.path.isdir(path):
        path = os.path.join(path, 'main')
    return Game(path, cache_dir)
