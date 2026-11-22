#!/usr/bin/env python3
"""check_release.py: does a new mkw-recomp Windows release still fit the MKWMP probe?

Compares two mkw-recomp Windows builds (OLD = one the probe is known to work with, NEW = the candidate) against
everything the proxy winmm.dll ("probe", native/probe/src) takes from mkw.exe by name, by byte pattern or by layout.
Nothing from the releases is executed: the files are only read and parsed (PE headers, COFF symbol table, sections).

What it prints, for OLD and NEW side by side:
   1. mkw.exe SHA-256 (and whether rust/client/src/recomp.rs KNOWN / SUPPORTED lists it), version strings
   2. PE facts: timestamp, linker, sections, COFF symbol count, imported DLLs (toolchain changes show here)
   3. every COFF name of native/probe/src/coff.c kNames (parsed from that file): count, RVA, section, first bytes
   4. PE exports (mkwgame.dll imports rt_dispatch from mkw.exe: npln.c install_profile_dispatch_trace)
   5. the .ovr table (what npln.c stock_handler reads): names added/removed, and for each npln.c kRegs name
      whether it has a stock handler (that decides which probe overrides are gated)
   6. (removed: the keypad's detours; the probe draws nothing into the game any more)
   7. rt_dispatch's first bytes (mov rax,[rcx+0x138]: Cpu.thread at 312, recomp.h)
   8. the span of g_host (distance to the next symbol of the section) and the layout of hostfn.c's added[]: the
      fixed array of RUNTIME_OVERRIDE_MAX entries (up to 0.1.13) or the pointer grown with realloc (from 0.1.15)
   9. WINMM imports of mkw.exe and SDL3.dll against native/probe/src/winmm.def; the SDL3 exports the probe
      resolves with GetProcAddress
  10. RT_GAME_ID (the 16-hex id of the game code mkw.exe accepts; a change means mkwgame.dll must be rebuilt
      before `mkw.exe --play` works) and, with --game-dir, the folder's mkwgame.id / mkwgame.dll imports
  11. the MKW_* keys MKWMP sets, as strings of mkw.exe (cfg.c known[])
  12. the native plugin API (runtime/plugin/plugin.c api_template: its size and version) against the mod's
      mkw_plugin.h (the MKWMP mod is the probe built as a plugin, backend_plugin.c): which of the versions the mod
      uses the runtime has (2 the mod's settings, 3 native images for the camera, 4 the overlay's pad filter,
      menu_open and frame capture). Upstream may be behind the mod (those parts stay off); MKWMP's own release
      line (a version ending in -mkwmp) should carry the mod's version (a WARN when it does not)

Usage (from the repo root; -I keeps Python from importing anything next to the inputs):

    .venv\\Scripts\\python.exe -I tools\\recomp\\check_release.py OLD NEW [--game-dir DIR] [--strict] [--ovr-dump DIR]

  OLD, NEW     each a release zip (mkw-recomp-<v>-windows-x64.zip, read in memory, never unpacked), a folder
               holding mkw.exe (searched two levels deep), or an mkw.exe itself. SDL3.dll is taken from beside it.
  --game-dir   a set-up game folder (read only): its mkwgame.id is compared with NEW's RT_GAME_ID and its
               mkwgame.dll import table is checked for mkw.exe!rt_dispatch.
  --repo       the MKWMP checkout (default: two folders above this file).
  --strict     warnings also give a non-zero exit code.
  --ovr-dump   write ovr-old.txt / ovr-new.txt (the .ovr names, one per line) into DIR.

Exit code: 0 when everything the probe needs is present and unchanged in NEW, 1 when something is missing or
changed (lines marked FAIL; with --strict also WARN), 2 for bad arguments or unreadable inputs.

Lines are marked  ok / NOTE (changed, not used by the probe) / WARN (needs a look or a live test) / FAIL.
Only the standard library is needed; with capstone (the repo's .deps) the prologues are also disassembled, the
stolen bytes are checked to be whole position-independent instructions, and functions are compared by their
instructions instead of their raw bytes.

A FAIL is a reason to read the upstream diff, not a verdict; an all-ok run does not replace the live test
(behaviour changes do not show in symbols). See research/docs/RECOMP-<version>.md for the reports written
from this output.
"""

import argparse
import hashlib
import io
import re
import struct
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve()
DEFAULT_REPO = HERE.parents[2]

# coff.c kNames entries the probe only tests for presence: absent from older releases by design
VERSION_MARKERS = {"plugins_load": "a runtime with the native plugin loader, 0.1.15 on: dllmain.c's mod-wins rule"}

FAILS: list[str] = []
WARNS: list[str] = []


def out(line: str = "") -> None:
    sys.stdout.write(line + "\n")


def ok(msg: str) -> None:
    out(f"  ok    {msg}")


def note(msg: str) -> None:
    out(f"  NOTE  {msg}")


def warn(msg: str) -> None:
    WARNS.append(msg)
    out(f"  WARN  {msg}")


def fail(msg: str) -> None:
    FAILS.append(msg)
    out(f"  FAIL  {msg}")


def hexs(b: bytes) -> str:
    return " ".join(f"{x:02X}" for x in b)


# ---------------------------------------------------------------------------------------------------------------- PE


class PE:
    """The parts of a PE32+ image this check reads. Offsets are file offsets; `rva` values are image-relative."""

    def __init__(self, data: bytes, label: str):
        self.d = data
        self.label = label
        if data[:2] != b"MZ":
            raise ValueError(f"{label}: not an MZ image")
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe : pe + 4] != b"PE\0\0":
            raise ValueError(f"{label}: no PE signature")
        (self.machine, nsec, self.timestamp, self.symptr, self.nsym, optsz, self.characteristics) = struct.unpack_from(
            "<HHIIIHH", data, pe + 4
        )
        opt = pe + 24
        magic = struct.unpack_from("<H", data, opt)[0]
        if magic != 0x20B:
            raise ValueError(f"{label}: not PE32+ (magic {magic:#x})")
        self.linker = (data[opt + 2], data[opt + 3])
        self.entry = struct.unpack_from("<I", data, opt + 16)[0]
        self.image_base = struct.unpack_from("<Q", data, opt + 24)[0]
        self.size_of_image = struct.unpack_from("<I", data, opt + 56)[0]
        self.subsystem = struct.unpack_from("<H", data, opt + 68)[0]
        ndir = struct.unpack_from("<I", data, opt + 108)[0]
        self.dirs = [struct.unpack_from("<II", data, opt + 112 + 8 * i) for i in range(min(ndir, 16))]
        self.sections = []  # (name, vaddr, vsize, rawptr, rawsize, flags)
        sh = opt + optsz
        self._strtab = self.symptr + self.nsym * 18 if self.symptr and self.nsym else 0
        for i in range(nsec):
            raw = data[sh + 40 * i : sh + 40 * i + 40]
            name = raw[:8].rstrip(b"\0").decode("latin-1")
            if name.startswith("/") and self._strtab:  # long section name: /offset into the string table
                try:
                    name = self._cstr(self._strtab + int(name[1:]))
                except ValueError:
                    pass
            vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", raw, 8)
            flags = struct.unpack_from("<I", raw, 36)[0]
            self.sections.append((name, vaddr, vsize, rawptr, rawsize, flags))
        self._symbols = None

    def _cstr(self, off: int, limit: int = 4096) -> str:
        end = self.d.find(b"\0", off, off + limit)
        if end < 0:
            end = off + limit
        return self.d[off:end].decode("latin-1")

    def section(self, name: str):
        for s in self.sections:
            if s[0] == name:
                return s
        return None

    def section_of(self, rva: int):
        for s in self.sections:
            if s[1] <= rva < s[1] + max(s[2], s[4]):
                return s
        return None

    def off(self, rva: int):
        """File offset of an RVA, None when it has no bytes in the file (.bss)."""
        s = self.section_of(rva)
        if not s or rva - s[1] >= s[4]:
            return None
        return s[3] + rva - s[1]

    def read(self, rva: int, n: int) -> bytes:
        o = self.off(rva)
        return b"" if o is None else self.d[o : o + n]

    def cstr_rva(self, rva: int) -> str:
        o = self.off(rva)
        return "" if o is None else self._cstr(o)

    # -- COFF symbols: the same walk as native/probe/src/coff.c (defined symbols only: section number > 0)
    def symbols(self):
        """[(name, rva, section name, storage class, type)] of every symbol defined in a section."""
        if self._symbols is not None:
            return self._symbols
        res = []
        if self.symptr and self.nsym:
            d, strtab = self.d, self._strtab
            i = 0
            while i < self.nsym:
                o = self.symptr + 18 * i
                value, secno, typ, scl, naux = struct.unpack_from("<IhHBB", d, o + 8)
                if 0 < secno <= len(self.sections):
                    if d[o : o + 4] == b"\0\0\0\0":
                        name = self._cstr(strtab + struct.unpack_from("<I", d, o + 4)[0])
                    else:
                        name = d[o : o + 8].rstrip(b"\0").decode("latin-1")
                    sec = self.sections[secno - 1]
                    res.append((name, sec[1] + value, sec[0], scl, typ))
                i += 1 + naux
        self._symbols = res
        return res

    def find(self, name: str):
        return [s for s in self.symbols() if s[0] == name]

    def span(self, rva: int):
        """Distance from rva to the next higher symbol address of the same section (None: no symbol after it)."""
        sec = self.section_of(rva)
        if not sec:
            return None
        nxt = [s[1] for s in self.symbols() if s[2] == sec[0] and s[1] > rva]
        end = sec[1] + sec[2]
        return (min(nxt) if nxt else end) - rva

    # -- exports / imports
    def exports(self):
        rva, size = self.dirs[0] if self.dirs else (0, 0)
        if not rva:
            return "", []
        o = self.off(rva)
        name_rva, _base, _nfun, nnames, _funs, names, _ords = struct.unpack_from("<IIIIIII", self.d, o + 12)
        no = self.off(names)
        return self.cstr_rva(name_rva), sorted(
            self.cstr_rva(struct.unpack_from("<I", self.d, no + 4 * i)[0]) for i in range(nnames)
        )

    def imports(self):
        """{dll name as written: [function names]} in import-table order."""
        res = {}
        rva, size = self.dirs[1] if len(self.dirs) > 1 else (0, 0)
        if not rva:
            return res
        o = self.off(rva)
        while True:
            oft, _ts, _fc, name, ft = struct.unpack_from("<IIIII", self.d, o)
            if not name and not ft:
                break
            names = []
            t = self.off(oft or ft)
            while t is not None:
                v = struct.unpack_from("<Q", self.d, t)[0]
                if not v:
                    break
                names.append(f"#{v & 0xFFFF}" if v >> 63 else self.cstr_rva((v & 0x7FFFFFFF) + 2))
                t += 8
            res.setdefault(self.cstr_rva(name), []).extend(names)
            o += 20
        return res

    # -- the .ovr table, as npln.c stock_handler reads it: {name pointer, function pointer} pairs of 16 bytes,
    #    entries with a null half skipped. The pointers are virtual addresses at the preferred image base.
    def ovr(self):
        sec = self.section(".ovr")
        if not sec:
            return None
        res = []
        raw = self.d[sec[3] : sec[3] + min(sec[2], sec[4])]
        for k in range(sec[2] // 16):
            if 16 * k + 16 > len(raw):
                break
            name_va, fn_va = struct.unpack_from("<QQ", raw, 16 * k)
            if not name_va or not fn_va:
                continue
            res.append((self.cstr_rva(name_va - self.image_base), fn_va - self.image_base))
        return res

    def has_cstring(self, s: str) -> bool:
        """`s` as a whole NUL-terminated string somewhere in the file."""
        needle = b"\0" + s.encode() + b"\0"
        return needle in self.d


# ------------------------------------------------------------------------------------------------------------ inputs


class Release:
    def __init__(self, arg: str):
        self.arg = arg
        self.sdl = None
        p = Path(arg)
        if p.is_file() and p.suffix.lower() == ".zip":
            with zipfile.ZipFile(p) as z:
                exe = [n for n in z.namelist() if n.lower().endswith("mkw.exe")]
                if not exe:
                    raise ValueError(f"{arg}: no mkw.exe in the zip")
                exe.sort(key=len)
                self.where = f"{p.name}!{exe[0]}"
                self.exe_bytes = z.read(exe[0])
                sdl = exe[0][: -len("mkw.exe")] + "SDL3.dll"
                if sdl in z.namelist():
                    self.sdl = z.read(sdl)
        else:
            if p.is_dir():
                found = [c for c in (p / "mkw.exe", *p.glob("*/mkw.exe"), *p.glob("*/*/mkw.exe")) if c.is_file()]
                if not found:
                    raise ValueError(f"{arg}: no mkw.exe in the folder")
                p = found[0]
            if not p.is_file():
                raise ValueError(f"{arg}: not a zip, a folder or an mkw.exe")
            self.where = str(p)
            self.exe_bytes = p.read_bytes()
            if (p.parent / "SDL3.dll").is_file():
                self.sdl = (p.parent / "SDL3.dll").read_bytes()
        self.sha = hashlib.sha256(self.exe_bytes).hexdigest()
        self.pe = PE(self.exe_bytes, self.where)
        self.sdl_pe = PE(self.sdl, "SDL3.dll") if self.sdl else None


# -------------------------------------------------------------------------------------------- what the probe needs


def c_block(text: str, start_re: str) -> str:
    """The text between the `{` that follows start_re and its matching `};`."""
    m = re.search(start_re, text)
    if not m:
        return ""
    i = text.index("{", m.end() - 1)
    end = text.find("};", i)
    return text[i + 1 : end if end >= 0 else len(text)]


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


class Probe:
    """Everything the check needs to know about the probe, read from its sources so the two stay in step."""

    def __init__(self, repo: Path):
        src = repo / "native/probe/src"
        self.src = src
        rd = lambda n: (src / n).read_text(encoding="utf-8", errors="replace")  # noqa: E731
        coff = strip_comments(rd("coff.c"))
        self.names = re.findall(r'"([^"]+)"', c_block(coff, r"kNames\s*\[\s*\]\s*="))
        m = re.search(r"\bN_RT\s*=\s*(\d+)", coff)
        self.n_required = int(m.group(1)) if m else len(self.names)
        if not self.names:
            raise ValueError("could not parse kNames from coff.c")

        # (the keypad's detours, once step 6, are gone: the probe detours no mkw.exe function any more)

        npln = strip_comments(rd("npln.c"))
        self.regs = re.findall(r'\{\s*"([^"]+)"\s*,', c_block(npln, r"kRegs\s*\[\s*\]\s*="))
        self.extra_regs = re.findall(r'(?:hostfn_add_override|g_rt\.override)\(\s*"([^"]+)"', npln)
        # names the probe leaves to the runtime and whose stock handler it relies on (static const char kStock...[])
        self.stock_needed = re.findall(r'\bkStock\w+\[\]\s*=\s*"([^"]+)"', npln)
        m = re.search(r"RUNTIME_OVERRIDE_MAX\s*=\s*(\d+)", npln)
        self.override_max = int(m.group(1)) if m else 64
        m = re.search(r"HOST_CAP\s*=\s*(\d+)", coff)
        self.host_cap = int(m.group(1)) if m else 8192
        m = re.search(r"//\s*Offsets:.*thread at (\d+)", rd("recomp.h"))
        self.thread_off = int(m.group(1)) if m else 312

        self.winmm_def = set()
        for line in rd("winmm.def").splitlines():
            m = re.match(r"\s+(\w+)\s*(=|@|$)", line)
            if m:
                self.winmm_def.add(m.group(1))

        self.sdl_names, self.mkw_keys = set(), set()
        for f in sorted(src.glob("*.[ch]")):
            t = f.read_text(encoding="utf-8", errors="replace")
            self.sdl_names.update(re.findall(r'"(SDL_[A-Za-z0-9_]+)"', strip_comments(t)))
            self.mkw_keys.update(re.findall(r'"(MKW_[A-Z0-9_]+)"', strip_comments(t)))
        tools = sorted(p for p in (repo / "resources/tools").glob("*") if p.suffix in (".ps1", ".sh", ".py"))
        for extra in [*sorted((repo / "rust/client/src").glob("*.rs")), repo / "site/install.sh", *tools]:
            if extra.is_file():
                self.mkw_keys.update(re.findall(r"\b(MKW_[A-Z0-9_]{3,})\b", extra.read_text("utf-8", "replace")))
        self.sdl_names = {n for n in self.sdl_names if not n.isupper()}  # hints (SDL_HINT_...) are not exports
        self.mkw_keys -= {"MKW_LAUNCHED", "MKW_NO_LAUNCHER"}  # read with getenv in main.c, not cfg.c keys

        # the plugin build of the probe (backend_plugin.c, overlay.c): the API header it was written against. Every
        # member of MkwPluginApi after size and version is a pointer, so its size is 8 + 8 * members
        self.plugin_version, self.plugin_members, self.plugin_v4_used = 0, 0, []
        if (src / "mkw_plugin.h").is_file():
            hdr = strip_comments(rd("mkw_plugin.h"))
            m = re.search(r"#define\s+MKW_PLUGIN_API_VERSION\s+(\d+)", hdr)
            self.plugin_version = int(m.group(1)) if m else 0
            m = re.search(r"typedef struct MkwPluginApi \{(.*?)\}\s*MkwPluginApi\s*;", hdr, flags=re.S)
            fields = [f for f in (m.group(1).split(";") if m else []) if f.strip()]
            if len(fields) > 2 and all("uint32_t" in f and "(" not in f for f in fields[:2]) \
                    and all("*" in f for f in fields[2:]):
                self.plugin_members = len(fields) - 2
            uses = "".join(strip_comments(f.read_text("utf-8", "replace")) for f in sorted(src.glob("*.c")))
            self.plugin_v4_used = [n for n in ("input_filter", "menu_open", "frame_capture")
                                   if re.search(rf"\bapi->{n}\b", uses)]

        rs = repo / "rust/client/src/recomp.rs"
        self.known, self.supported = {}, []
        if rs.is_file():
            t = rs.read_text("utf-8", "replace")
            self.known = {h.lower(): v for v, h in re.findall(r'\("([\d.]+)",\s*"([0-9a-fA-F]{64})"\)', t)}
            m = re.search(r"SUPPORTED[^=]*=\s*&\[([^\]]*)\]", t)
            self.supported = re.findall(r'"([\d.]+)"', m.group(1)) if m else []


# ---------------------------------------------------------------------------------------------------------- capstone


def load_capstone(repo: Path):
    try:
        import capstone  # noqa: PLC0415
    except ImportError:
        deps = repo / ".deps"
        if not deps.is_dir():
            return None
        sys.path.append(str(deps))
        try:
            import capstone  # noqa: PLC0415
        except ImportError:
            return None
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    return capstone, md


def disasm(cs, code: bytes, rva: int, count: int):
    """[(size, text, position_dependent)] of the first `count` instructions."""
    if not cs:
        return []
    capstone, md = cs
    res = []
    for ins in md.disasm(code, rva):
        dep = False
        if ins.group(capstone.CS_GRP_JUMP) or ins.group(capstone.CS_GRP_CALL) or ins.group(capstone.CS_GRP_RET):
            dep = True
        for op in ins.operands:
            if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                dep = True
        res.append((ins.size, f"{ins.mnemonic} {ins.op_str}".strip(), dep))
        if len(res) >= count:
            break
    return res


def shape(cs, code: bytes, rva: int, count: int = 6):
    """The first instructions with rip-relative displacements and branch targets blanked, for comparing two builds."""
    res = []
    for _size, text, dep in disasm(cs, code, rva, count):
        res.append(re.sub(r"0x[0-9a-f]+", "N", text) if dep else text)
    return res


# ------------------------------------------------------------------------------------------------------------ checks


def game_id_candidates(pe: PE):
    """16 lowercase hex characters as a whole NUL-terminated string in a data section: RT_GAME_ID (build.py game_id,
    compared by gamelib.c check and rt_game_present). Usually exactly one."""
    res = []
    for name, vaddr, vsize, rawptr, rawsize, flags in pe.sections:
        if flags & 0x20 or not rawsize:  # code
            continue
        blob = pe.d[rawptr : rawptr + rawsize]
        for m in re.finditer(rb"(?<![0-9a-f])([0-9a-f]{16})\0", blob):
            if m.start() and blob[m.start() - 1] not in (0,):
                continue
            s = m.group(1).decode()
            if len(set(s)) > 4 and s not in ("0123456789abcdef", "fedcba9876543210"):  # no digit tables, no 0000...
                res.append((s, vaddr + m.start(), name))
    return res


def port_versions(pe: PE):
    """The port's own version (rt_port_version, git describe at build time: main.c logs "MKW Recomp <it>"): whole
    NUL-terminated strings like 0.1.13 or 0.1.13-4-gabcdef in a data section. Other libraries' versions may show too."""
    res = []
    for name, _va, _vs, rawptr, rawsize, flags in pe.sections:
        if flags & 0x20 or not rawsize or name.startswith(".debug") or name in (".rsrc", ".reloc"):
            continue
        for m in re.finditer(rb"\0(\d{1,2}\.\d{1,3}\.\d{1,3}(?:-[0-9A-Za-z.-]{1,24})?)\0", pe.d[rawptr : rawptr + rawsize]):
            if m.group(1).decode() not in res:
                res.append(m.group(1).decode())
    return res


def section_line(pe: PE) -> str:
    return " ".join(f"{n}:{vs:#x}" for n, _va, vs, _rp, _rs, _fl in pe.sections)


def main() -> int:
    ap = argparse.ArgumentParser(description="mkw-recomp release check against the MKWMP probe (see the docstring)")
    ap.add_argument("old")
    ap.add_argument("new")
    ap.add_argument("--repo", type=Path, default=DEFAULT_REPO)
    ap.add_argument("--game-dir", type=Path)
    ap.add_argument("--strict", action="store_true")
    ap.add_argument("--ovr-dump", type=Path)
    args = ap.parse_args()
    try:
        probe = Probe(args.repo)
        old, new = Release(args.old), Release(args.new)
    except (OSError, ValueError, zipfile.BadZipFile, struct.error) as e:
        sys.stderr.write(f"check_release: {e}\n")
        return 2
    cs = load_capstone(args.repo)
    o, n = old.pe, new.pe

    out("mkw-recomp release check against the MKWMP probe")
    out(f"  probe sources: {probe.src}")
    out(f"  old: {old.where}")
    out(f"  new: {new.where}")
    out(f"  capstone: {'yes' if cs else 'no (byte comparison only)'}")

    # 1 ---------------------------------------------------------------------------------------------------------
    out("\n[1] mkw.exe SHA-256")
    for tag, r in (("old", old), ("new", new)):
        v = probe.known.get(r.sha)
        state = (
            f"recomp.rs KNOWN as {v}" + (", SUPPORTED" if v in probe.supported else ", not in SUPPORTED")
            if v
            else "not in recomp.rs KNOWN"
        )
        out(f"  {tag}  {r.sha}  {len(r.exe_bytes)} bytes  ({state})")
    for tag, pe in (("old", o), ("new", n)):
        out(f"  {tag}  version strings in the exe (rt_port_version among them): {' '.join(port_versions(pe)[:12]) or 'none'}")
    if old.sha == new.sha:
        warn("old and new are the same file")
    v = probe.known.get(new.sha)
    if not v:
        warn("the launcher does not know NEW's hash: add it to KNOWN (and SUPPORTED) in rust/client/src/recomp.rs")
    elif v not in probe.supported:
        warn(f"the launcher knows NEW as {v} but refuses it: SUPPORTED in rust/client/src/recomp.rs")
    else:
        ok(f"the launcher accepts NEW ({v})")

    # 2 ---------------------------------------------------------------------------------------------------------
    out("\n[2] PE facts")
    for tag, pe in (("old", o), ("new", n)):
        out(
            f"  {tag}  timestamp {pe.timestamp:#x}  linker {pe.linker[0]}.{pe.linker[1]}  image base {pe.image_base:#x}"
            f"  size {pe.size_of_image:#x}  subsystem {pe.subsystem}  COFF symbols {pe.nsym}"
        )
        out(f"       sections {section_line(pe)}")
        out(f"       imports  {' '.join(pe.imports())}")
    if not n.symptr or not n.nsym:
        fail("NEW has no COFF symbol table (stripped): coff.c resolves nothing, the probe cannot load its hooks")
    if [s[0] for s in o.sections] != [s[0] for s in n.sections]:
        note("the section list changed (toolchain or link change): read the checks below with that in mind")
    if o.linker != n.linker:
        note(f"linker version changed {o.linker} -> {n.linker}")
    if o.image_base != n.image_base:
        note(f"image base changed {o.image_base:#x} -> {n.image_base:#x}")
    oi, ni = {k.lower() for k in o.imports()}, {k.lower() for k in n.imports()}
    if oi != ni:
        note(f"imported DLLs: added {sorted(ni - oi) or '-'}, removed {sorted(oi - ni) or '-'}")

    # 3 ---------------------------------------------------------------------------------------------------------
    out(f"\n[3] COFF names of coff.c kNames ({len(probe.names)}; the first {probe.n_required} are required)")
    out(f"  {'name':<58} {'old RVA':>10} {'new RVA':>10}  sec/class  first bytes")
    extra = [x for x in ("rt_dispatch", "ov_show_keyboard", "ovr_start", "ovr_stop", "added") if x not in probe.names]
    rows = [(nm, i < probe.n_required, True) for i, nm in enumerate(probe.names)] + [(x, False, False) for x in extra]
    for name, required, in_probe in rows:
        a, b = o.find(name), n.find(name)
        ra = "/".join(f"{s[1]:#x}" for s in a) or "-"
        rb = "/".join(f"{s[1]:#x}" for s in b) or "-"
        cmpres = ""
        if len(a) == 1 and len(b) == 1:
            sa, sb = a[0], b[0]
            cls = f"{sb[2]}/{sb[3]}"
            if (sa[2], sa[3]) != (sb[2], sb[3]):
                cls += f" (was {sa[2]}/{sa[3]})"
            ca, cb = o.read(sa[1], 32), n.read(sb[1], 32)
            sec_b = n.section_of(sb[1])
            if sec_b and sec_b[5] & 0x20 and ca and cb:  # code
                if ca[:16] == cb[:16]:
                    cmpres = "same 16 bytes"
                elif cs and shape(cs, ca, 0) == shape(cs, cb, 0):
                    cmpres = "same instructions (displacements differ)"
                else:
                    cmpres = f"DIFFERENT: {hexs(ca[:12])} -> {hexs(cb[:12])}"
            else:
                cmpres = "data"
        else:
            cls = "-"
        tag = "" if in_probe else " (not in kNames)"
        out(f"  {name[:58]:<58} {ra:>10} {rb:>10}  {cls}  {cmpres}{tag}")
        if in_probe:
            if not b and name in VERSION_MARKERS:
                note(f"{name}: absent from NEW (it only marks {VERSION_MARKERS[name]}; coff.c does not need it)")
            elif not b:
                fail(f"{name}: absent from NEW's symbol table" + (" (required: the probe stays off)" if required else ""))
            elif len(b) > 1:
                fail(f"{name}: {len(b)} symbols of that name in NEW; coff.c takes the first one in table order")
            elif cmpres.startswith("DIFFERENT"):
                note(f"{name}: first instructions differ; compare its source in the upstream diff")
        elif name == "rt_dispatch" and len(b) != 1:
            fail("rt_dispatch: not exactly one symbol in NEW")

    # 4 ---------------------------------------------------------------------------------------------------------
    out("\n[4] PE exports")
    (odll, oe), (ndll, ne) = o.exports(), n.exports()
    out(f"  old ({odll}): {' '.join(oe)}")
    out(f"  new ({ndll}): {' '.join(ne)}")
    if set(oe) != set(ne):
        note(f"exports added {sorted(set(ne) - set(oe)) or '-'}, removed {sorted(set(oe) - set(ne)) or '-'}")
    if "rt_dispatch" in ne:
        ok("rt_dispatch is exported (mkwgame.dll imports it; npln.c patches that import)")
    else:
        fail("rt_dispatch is not exported by NEW: the import npln.c patches in mkwgame.dll cannot exist")
    if ndll.lower() != "mkw.exe":
        fail(f"the export table names the module {ndll!r}; npln.c looks for the import module 'mkw.exe'")

    # 5 ---------------------------------------------------------------------------------------------------------
    out("\n[5] .ovr table (stock host handlers; npln.c stock_handler)")
    oo, no_ = o.ovr(), n.ovr()
    if no_ is None:
        fail("NEW has no .ovr section: stock_handler finds nothing, every gated override becomes ungated")
        no_ = []
    oo = oo or []
    so, sn = o.section(".ovr"), n.section(".ovr")
    out(f"  old: {len(oo)} entries, section size {so[2] if so else 0:#x}; new: {len(no_)} entries, size {sn[2] if sn else 0:#x}")
    on_, nn_ = [e[0] for e in oo], [e[0] for e in no_]
    if on_ == nn_:
        ok("the name list is identical, in the same order")
    else:
        added_, removed = sorted(set(nn_) - set(on_)), sorted(set(on_) - set(nn_))
        if not added_ and not removed:
            note("same names, another order")
        for x in added_:
            note(f"new stock handler: {x}")
        for x in removed:
            note(f"stock handler removed: {x}")
    dup = sorted({x for x in nn_ if nn_.count(x) > 1})
    if dup:
        note(f"names listed more than once in NEW: {dup}")
    changed = 0
    regs = probe.regs + [x for x in probe.extra_regs if x not in probe.regs]
    for name in regs:
        a, b = name in on_, name in nn_
        if a != b:
            changed += 1
            fail(f"override {name}: stock handler {'appeared' if b else 'disappeared'} in NEW (changes its gating)")
    stock = sum(1 for x in probe.regs if x in nn_)
    if not changed:
        ok(f"{len(regs)} probe override names: {stock} of the {len(probe.regs)} kRegs have a stock handler, as before")
    if len(regs) > probe.override_max + 2:
        warn(f"{len(regs)} override names against a runtime table of {probe.override_max}")
    for name in probe.stock_needed:
        if name in regs:
            fail(f"{name}: npln.c both overrides it and relies on its stock handler")
        elif name in nn_:
            ok(f"stock handler the probe relies on (not overridden): {name}")
        else:
            fail(f"{name}: no stock handler in NEW; npln.c relies on it (typed GetAddrInfo results are never freed "
                 "by the SDK), so it falls back to overriding it and drops its newest override")
    if args.ovr_dump:
        args.ovr_dump.mkdir(parents=True, exist_ok=True)
        (args.ovr_dump / "ovr-old.txt").write_text("\n".join(on_) + "\n")
        (args.ovr_dump / "ovr-new.txt").write_text("\n".join(nn_) + "\n")
        out(f"  names written to {args.ovr_dump}")

    # 7 ---------------------------------------------------------------------------------------------------------
    out("\n[7] rt_dispatch prologue (Cpu.thread offset)")
    want = b"\x48\x8b\x81" + struct.pack("<I", probe.thread_off)
    for tag, pe in (("old", o), ("new", n)):
        f = pe.find("rt_dispatch")
        if len(f) != 1:
            continue
        code = pe.read(f[0][1], 24)
        ins = "; ".join(t for _s, t, _d in disasm(cs, code, f[0][1], 4))
        out(f"  {tag}  rt_dispatch @ {f[0][1]:#x}: {hexs(code[:16])}  {ins}")
        if tag == "new":
            if code.startswith(want):
                ok(f"starts with mov rax,[rcx+{probe.thread_off:#x}]: Cpu.thread at {probe.thread_off} (recomp.h)")
            else:
                fail(f"does not start with {hexs(want)} (mov rax,[rcx+{probe.thread_off:#x}]): check the Cpu layout "
                     "in runtime/cpu/cpu.h against recomp.h")

    # 8 ---------------------------------------------------------------------------------------------------------
    out("\n[8] g_host span and hostfn.c added[] layout")
    expect = {"g_host": probe.host_cap * 16}
    for name, want_span in expect.items():
        a, b = o.find(name), n.find(name)
        sa = [o.span(s[1]) for s in a]
        sb = [n.span(s[1]) for s in b]
        fmt = lambda xs: "/".join(f"{x:#x}" if x is not None else "?" for x in xs) or "-"  # noqa: E731
        out(f"  {name:<10} old {fmt(sa):>9}  new {fmt(sb):>9}  expected {want_span:#x}")
        if not b:
            fail(f"{name}: not in NEW's symbol table")
        elif want_span not in sb:
            if sa and want_span not in sa:
                warn(f"{name}: span {fmt(sb)} is not {want_span:#x}, and neither was OLD's ({fmt(sa)}): heuristic "
                     "only, check its declaration upstream")
            else:
                fail(f"{name}: span {fmt(sb)} instead of {want_span:#x}: its capacity or entry size changed")
        elif len(b) > 1 and name != "added":
            fail(f"{name}: {len(b)} symbols of that name")
        else:
            ok(f"{name}: {want_span:#x}")

    # hostfn.c's table of added overrides (hostfn_add_override: what g_rt.override calls). Two layouts are fine:
    #   up to 0.1.13   static RtHostEntry added[MAX_ADDED];   a fixed array, 16 bytes an entry: more is fatal, so
    #                  the probe keeps to RUNTIME_OVERRIDE_MAX (npln.c) and the span must be exactly that many
    #   from 0.1.15    static RtHostEntry *added; static int nadded, cap_added;   grown with realloc: no limit
    # Several files have a static `added`; hostfn.c's is the one beside `nadded` (and `cap_added` when dynamic).
    def added_layout(pe: PE):
        """('array', entries) | ('dynamic', None) | (None, why) for hostfn.c's added[]."""
        cands = pe.find("added")
        if not cands:
            return None, "no symbol named added"
        near = lambda nm, rva: any(s[2] == ".data" and abs(s[1] - rva) <= 0x20 for s in pe.find(nm))  # noqa: E731
        seen = []
        for s in cands:
            span = pe.span(s[1])
            seen.append(f"{span:#x}" if span is not None else "?")
            if span == 8 and near("nadded", s[1]) and near("cap_added", s[1]):
                return "dynamic", None
            if span and span >= 0x100 and span % 16 == 0 and near("nadded", s[1]) and not pe.find("cap_added"):
                return "array", span // 16
        return None, f"spans {'/'.join(seen)}, no nadded/cap_added neighbour that fits either layout"

    for tag, pe in (("old", o), ("new", n)):
        kind, info = added_layout(pe)
        desc = (f"fixed array of {info} entries" if kind == "array"
                else "pointer + nadded + cap_added (grown with realloc)" if kind == "dynamic" else f"unknown: {info}")
        out(f"  added      {tag}  {desc}")
        if tag != "new":
            continue
        fn = pe.find("hostfn_add_override")
        body = pe.read(fn[0][1], 0xA0) if len(fn) == 1 else b""
        texts = [t for _s, t, _d in disasm(cs, body, fn[0][1] if fn else 0, 40)]
        if kind == "array":
            if info < probe.override_max:
                fail(f"added[]: {info} entries, the probe registers up to {probe.override_max} "
                     "(npln.c RUNTIME_OVERRIDE_MAX): hostfn_add_override would end the process")
            elif info > probe.override_max:
                note(f"added[]: {info} entries, more than the probe's budget of {probe.override_max}")
                ok("added[]: the probe's override budget fits")
            else:
                ok(f"added[]: {info} entries = npln.c RUNTIME_OVERRIDE_MAX")
        elif kind == "dynamic":
            if texts and any(re.search(r"cmp \w+, 0x40$", t) for t in texts):
                warn("added is a pointer, but hostfn_add_override still compares a count with 0x40: read hostfn.c")
            else:
                ok(f"added: dynamic, no fixed limit (the probe's budget of {probe.override_max} is for the older "
                   "runtimes); hostfn_override searches it newest first, so a later registration of a name wins")
        else:
            fail(f"added: {info}: hostfn.c's override table has a layout this check does not know")

    # 9 ---------------------------------------------------------------------------------------------------------
    out("\n[9] WINMM imports against winmm.def; SDL3 exports the probe resolves")
    for tag, r in (("old", old), ("new", new)):
        for what, pe in (("mkw.exe", r.pe), ("SDL3.dll", r.sdl_pe)):
            if pe is None:
                (warn if tag == "new" else note)(f"{tag}: no SDL3.dll beside mkw.exe, not checked")
                continue
            imp = {k.lower(): v for k, v in pe.imports().items()}
            names = imp.get("winmm.dll", [])
            out(f"  {tag}  {what} imports WINMM.dll: {' '.join(names) or '(none)'}")
            if tag != "new":
                continue
            missing = [x for x in names if x not in probe.winmm_def]
            if missing:
                fail(f"{what} imports {missing} from WINMM.dll, which winmm.def does not forward: the process "
                     "would not start with the proxy")
            elif names:
                ok(f"{what}: all {len(names)} forwarded by winmm.def ({len(probe.winmm_def)} exports)")
            elif what == "mkw.exe":
                fail("mkw.exe no longer imports WINMM.dll: the proxy winmm.dll is never loaded")
    if new.sdl_pe:
        _nm, se = new.sdl_pe.exports()
        _nm, soe = old.sdl_pe.exports() if old.sdl_pe else ("", [])
        missing = sorted(x for x in probe.sdl_names if x not in se)
        out(f"  SDL3.dll exports: old {len(soe)}, new {len(se)}; the probe resolves {len(probe.sdl_names)}: "
            f"{' '.join(sorted(probe.sdl_names))}")
        if missing:
            fail(f"SDL3.dll of NEW does not export {missing}")
        else:
            ok("every SDL name the probe resolves is exported")
        if old.sdl and new.sdl:
            same = hashlib.sha256(old.sdl).digest() == hashlib.sha256(new.sdl).digest()
            (ok if same else note)("SDL3.dll is byte-identical" if same else "SDL3.dll differs between the releases")

    # 10 --------------------------------------------------------------------------------------------------------
    out("\n[10] RT_GAME_ID (the game code id mkw.exe accepts)")
    go, gn = game_id_candidates(o), game_id_candidates(n)
    for tag, g in (("old", go), ("new", gn)):
        out(f"  {tag}  " + (", ".join(f"{s} ({sec}+{rva:#x})" for s, rva, sec in g) or "not found"))
    ids_o, ids_n = {x[0] for x in go}, {x[0] for x in gn}
    if not gn:
        warn("no 16-hex string found in NEW: locate RT_GAME_ID by hand (tools/build/build.py game_id)")
    elif ids_o == ids_n:
        ok("unchanged: a mkwgame.dll built by OLD's setup is accepted by NEW")
    else:
        warn(f"changed {sorted(ids_o)} -> {sorted(ids_n)}: mkwgame.dll must be rebuilt by NEW's setup before "
             "`mkw.exe --play` starts (gamelib.c: 'built for another version of the runtime')")
    if args.game_dir:
        idf = args.game_dir / "mkwgame.id"
        if idf.is_file():
            have = idf.read_text(errors="replace").strip()[:16]
            (ok if have in ids_n else warn)(f"{idf}: {have} " + ("matches NEW" if have in ids_n else "is not NEW's id"))
        else:
            note(f"{idf}: absent")
        dll = args.game_dir / "mkwgame.dll"
        if dll.is_file():
            try:
                imp = {k.lower(): v for k, v in PE(dll.read_bytes(), str(dll)).imports().items()}
                frm = imp.get("mkw.exe", [])
                out(f"  {dll.name} imports from mkw.exe: {' '.join(frm) or '(none)'}")
                if "rt_dispatch" in frm:
                    ok("mkwgame.dll imports mkw.exe!rt_dispatch by name (the slot npln.c patches)")
                else:
                    fail("mkwgame.dll does not import rt_dispatch from mkw.exe by name")
                missing = [x for x in frm if x not in ne]
                if missing:
                    warn(f"mkwgame.dll imports {missing}, which NEW does not export: it is from another runtime")
            except (ValueError, struct.error, OSError) as e:
                warn(f"{dll}: {e}")

    # 11 --------------------------------------------------------------------------------------------------------
    out("\n[11] MKW_* keys MKWMP uses (cfg.c known[] holds them without the prefix)")
    for key in sorted(probe.mkw_keys):
        bare = key[4:]
        a, b = o.has_cstring(bare), n.has_cstring(bare)
        if b:
            ok(f"{key}")
        elif a:
            warn(f"{key}: the string {bare!r} was in OLD and is not in NEW (key renamed or dropped?)")
        else:
            note(f"{key}: {bare!r} is in neither exe as a whole string (read some other way)")

    # 12 --------------------------------------------------------------------------------------------------------
    out("\n[12] native plugin API (plugin.c api_template) against the mod's mkw_plugin.h")

    def plugin_api(pe: PE):
        """(size, version) of the table every plugin gets, None when the exe has none or several."""
        f = pe.find("api_template")
        d = pe.read(f[0][1], 8) if len(f) == 1 else b""
        return struct.unpack("<II", d) if len(d) == 8 else None

    v4_runtime = ("plugins_input_filter", "plugins_frame_wanted", "plugins_frame_captured")  # input.c, gfx_present.c
    ao, an = plugin_api(o), plugin_api(n)
    want_size = 8 + 8 * probe.plugin_members if probe.plugin_members else 0
    for tag, a in (("old", ao), ("new", an)):
        out(f"  {tag}  " + (f"version {a[1]}, {a[0]} bytes ({(a[0] - 8) // 8} members)" if a else "no api_template"))
    out(f"  mod  mkw_plugin.h version {probe.plugin_version}" + (f", {want_size} bytes" if want_size else "")
        + (f"; version 4 members used: {' '.join(probe.plugin_v4_used)}" if probe.plugin_v4_used else ""))
    own_line = any(v.endswith("-mkwmp") for v in port_versions(n))  # MKWMP's release line of mkw-recomp
    if not probe.plugin_version:
        note("the probe has no mkw_plugin.h (no plugin build): nothing to compare")
    elif an is None:
        if n.find("plugins_load"):
            warn("NEW has plugins_load but no single api_template symbol: read runtime/plugin/plugin.c")
        else:
            note("NEW has no plugin loader: the mod cannot load there, winmm.dll is in charge")
    else:
        size, version = an
        if ao and (version < ao[1] or size < ao[0]):
            fail(f"the plugin API went back: version {ao[1]} ({ao[0]} bytes) -> {version} ({size} bytes)")
        if (size - 8) % 8:
            fail(f"api_template is {size} bytes: not two uint32_t and pointers, the mod's offsets do not hold")
        if version >= probe.plugin_version:
            if want_size and size < want_size:
                fail(f"NEW says version {version} but its table is {size} bytes; the mod's version "
                     f"{probe.plugin_version} table is {want_size}")
            else:
                ok(f"NEW has the mod's plugin API (version {version} >= {probe.plugin_version})")
        else:
            off = [what for v, what in ((2, "the mod's settings, on_exit"),
                                        (3, "native images (the GameChat camera)"),
                                        (4, "the overlay (pad filter, menu_open, frame capture)"))
                   if version < v <= probe.plugin_version]
            msg = f"NEW has plugin API {version}, the mod is written for {probe.plugin_version}: off there: {'; '.join(off)}"
            if own_line:
                warn(msg + " (MKWMP's own release line should carry the mod's version)")
            else:
                note(msg + " (the mod checks api->size and goes on without them)")
        if version >= 4:
            missing = [nm for nm in v4_runtime if len(n.find(nm)) != 1]
            if missing:
                fail(f"plugin API {version} without exactly one symbol each of {missing} (input.c / gfx_present.c call them)")
            else:
                ok("version 4's runtime side is linked in: " + " ".join(v4_runtime))
        elif any(n.find(nm) for nm in v4_runtime):
            warn(f"NEW has version 4's functions but says version {version}")

    # ----------------------------------------------------------------------------------------------------------
    out("\nSummary")
    out(f"  new mkw.exe sha256: {new.sha}")
    out(f"  {len(FAILS)} FAIL, {len(WARNS)} WARN")
    for x in FAILS:
        out(f"    FAIL  {x}")
    for x in WARNS:
        out(f"    WARN  {x}")
    if not FAILS:
        out("  Nothing the probe resolves, patches or reads changed. Behaviour still needs the live test.")
    return 1 if FAILS or (args.strict and WARNS) else 0


if __name__ == "__main__":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace", newline="\n")
    sys.exit(main())
