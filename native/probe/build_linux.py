#!/usr/bin/env python3
"""Build the platform-neutral probe core as a Linux shared object, to keep it portable.

    python build_linux.py [--zig PATH]

Produces build/libmkwmp-core.so for x86_64-linux-gnu: every source of build.py except the Windows-only ones
(loader, COFF backend, keypad detours, Win32 plat), plus the POSIX plat files, compiled with -Werror and linked with
--no-undefined against tests/stub_rtapi.c (an empty g_rt). Not something to ship: until mkw-recomp's plugin API
exists there is no backend, so the .so only proves that the core needs nothing from Windows
(resources/docs/PLUGIN-API-READINESS.md). Runs in CI (.github/workflows/probe-linux.yml).
"""
import argparse
import pathlib
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True   # importing build.py must not leave __pycache__ in the tree
sys.path.insert(0, str(HERE))
from build import SOURCES  # noqa: E402  (the Windows build's list: new core files are picked up from there)

SRC = HERE / "src"
BUILD = HERE / "build"
WINDOWS_ONLY = {"dllmain.c", "coff.c", "keypad.c", "detour.c", "plat_win.c", "plat_net_win.c"}
POSIX = ["plat_posix.c", "plat_net_posix.c"]
STUB = HERE / "tests" / "stub_rtapi.c"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zig", default=None, help="zig executable (default: zig on PATH)")
    args = ap.parse_args()
    zig = args.zig or shutil.which("zig")
    if not zig:
        sys.exit("zig not found (Zig 0.15)")
    missing = WINDOWS_ONLY - set(SOURCES)
    if missing:
        sys.exit(f"build_linux.py: WINDOWS_ONLY names files build.py no longer has: {sorted(missing)}")
    core = [s for s in SOURCES if s not in WINDOWS_ONLY] + POSIX
    BUILD.mkdir(exist_ok=True)
    out = BUILD / "libmkwmp-core.so"
    cmd = [
        zig, "cc",
        "-target", "x86_64-linux-gnu",
        "-shared", "-fPIC", "-O2",
        "-Wall", "-Wextra", "-Werror",
        "-Wl,--no-undefined",
        "-o", str(out),
    ] + [str(SRC / s) for s in core] + [str(STUB), "-lpthread", "-ldl"]
    print("+", " ".join(cmd))
    r = subprocess.run(cmd)
    if r.returncode != 0:
        sys.exit(r.returncode)
    print("built", out, out.stat().st_size, "bytes from", len(core), "core sources")


if __name__ == "__main__":
    main()
