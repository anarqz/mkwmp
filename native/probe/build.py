#!/usr/bin/env python3
"""Build the proxy winmm.dll probe with zig cc.

    python build.py [--zig PATH] [--install "<game dir>"] [--debug]

Produces build/winmm.dll. With --install, also copies it into the game folder (next to mkw.exe).
"""
import argparse
import pathlib
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
BUILD = HERE / "build"
DEFAULT_ZIG = r"C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
SOURCES = ["dllmain.c", "coff.c", "log.c", "npln.c"]
DEF = SRC / "winmm.def"
HOST_DLL = BUILD / "winmm_host.dll"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zig", default=DEFAULT_ZIG)
    ap.add_argument("--install", metavar="GAMEDIR", default=None)
    ap.add_argument("--debug", action="store_true")
    args = ap.parse_args()

    BUILD.mkdir(exist_ok=True)
    if not DEF.exists() or not HOST_DLL.exists():
        print("generating forwarder .def + winmm_host.dll ...")
        subprocess.run([sys.executable, str(HERE / "gen_def.py")], check=True)
    out = BUILD / "winmm.dll"
    cmd = [
        args.zig, "cc",
        "-target", "x86_64-windows-gnu",
        "-shared",
        "-O0" if args.debug else "-O2",
        "-g" if args.debug else "-O2",
        "-Wall", "-Wextra",
        "-o", str(out),
    ] + [str(SRC / s) for s in SOURCES] + [str(DEF), "-lws2_32"]

    print("+", " ".join(cmd))
    r = subprocess.run(cmd)
    if r.returncode != 0:
        sys.exit(r.returncode)
    print("built", out, out.stat().st_size, "bytes")

    if args.install:
        gamedir = pathlib.Path(args.install)
        shutil.copy2(out, gamedir / "winmm.dll")
        shutil.copy2(HOST_DLL, gamedir / "winmm_host.dll")
        print("installed", gamedir / "winmm.dll", "+ winmm_host.dll")


if __name__ == "__main__":
    main()
