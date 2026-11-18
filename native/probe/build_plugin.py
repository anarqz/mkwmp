#!/usr/bin/env python3
"""Build the probe as a native mkw-recomp plugin (src/mkw_plugin.h, API version 1) with zig cc.

    python build_plugin.py [--zig PATH] [--version X.Y.Z] [--target linux|windows|all] [--test]

Produces build/plugin/linux/mkwmp.so (x86_64-linux-gnu) and build/plugin/windows/mkwmp.dll (x86_64-windows-gnu, a
plain DLL, not the winmm.dll proxy): the core sources of build.py (minus the winmm loader, the COFF backend and the
keypad detours, as build_linux.py), the plat files of the target and src/backend_plugin.c. Both export only
mkw_plugin_init (Linux: a version script, linked with --no-undefined). --test also builds tests/test_plugin.c and runs
it against the Linux .so (on a Linux host): a fake MkwPluginApi, no runtime needed. Runs in CI
(.github/workflows/probe-linux.yml).
"""
import argparse
import pathlib
import platform
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True   # importing build.py must not leave __pycache__ in the tree
sys.path.insert(0, str(HERE))
from build import SOURCES  # noqa: E402  (new core files are picked up from the Windows build's list)
from build_linux import WINDOWS_ONLY, POSIX  # noqa: E402

SRC = HERE / "src"
OUT = HERE / "build" / "plugin"
BACKEND = "backend_plugin.c"
WIN_PLAT = ["plat_win.c", "plat_net_win.c"]
VERSION_SCRIPT = "{ global: mkw_plugin_init; local: *; };\n"


def run(cmd):
    print("+", " ".join(cmd), flush=True)
    r = subprocess.run(cmd)
    if r.returncode != 0:
        sys.exit(r.returncode)


def core():
    missing = WINDOWS_ONLY - set(SOURCES)
    if missing:
        sys.exit(f"build_plugin.py: WINDOWS_ONLY names files build.py no longer has: {sorted(missing)}")
    return [s for s in SOURCES if s not in WINDOWS_ONLY]


def build_linux(zig, version):
    out_dir = OUT / "linux"
    out_dir.mkdir(parents=True, exist_ok=True)
    script = out_dir / "mkwmp.map"
    script.write_text(VERSION_SCRIPT)
    out = out_dir / "mkwmp.so"
    run([zig, "cc", "-target", "x86_64-linux-gnu", "-shared", "-fPIC", "-fvisibility=hidden", "-O2",
         "-Wall", "-Wextra", "-Werror", f'-DMKWMP_VERSION="{version}"',
         "-Wl,--no-undefined", f"-Wl,--version-script={script}", "-o", str(out)]
        + [str(SRC / s) for s in core() + POSIX + [BACKEND]] + ["-lpthread", "-ldl"])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    return out


def build_windows(zig, version):
    out_dir = OUT / "windows"
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "mkwmp.dll"
    # only __declspec(dllexport) symbols are exported (mkw_plugin_init), no import library is needed
    run([zig, "cc", "-target", "x86_64-windows-gnu", "-shared", "-O2",
         "-Wall", "-Wextra", "-Werror", f'-DMKWMP_VERSION="{version}"', "-o", str(out)]
        + [str(SRC / s) for s in core() + WIN_PLAT + [BACKEND]] + ["-lws2_32"])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    return out


def test(zig, so):
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        sys.exit("build_plugin.py --test: runs the x86_64 Linux .so, so it needs an x86_64 Linux host")
    exe = OUT / "linux" / "test_plugin"
    run([zig, "cc", "-target", "x86_64-linux-gnu", "-O1", "-Wall", "-Wextra", "-Werror", "-o", str(exe),
         str(HERE / "tests" / "test_plugin.c"), "-ldl"])
    run([str(exe), str(so), str(SRC / "npln.c")])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zig", default=None, help="zig executable (default: zig on PATH)")
    ap.add_argument("--version", default="dev", help="version string logged by the probe (release tag)")
    ap.add_argument("--target", choices=["linux", "windows", "all"], default="all")
    ap.add_argument("--test", action="store_true", help="also build and run tests/test_plugin.c (Linux .so)")
    args = ap.parse_args()
    zig = args.zig or shutil.which("zig")
    if not zig:
        sys.exit("zig not found (Zig 0.15)")
    so = None
    if args.target in ("linux", "all") or args.test:
        so = build_linux(zig, args.version)
    if args.target in ("windows", "all"):
        build_windows(zig, args.version)
    if args.test:
        test(zig, so)


if __name__ == "__main__":
    main()
