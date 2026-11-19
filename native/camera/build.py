#!/usr/bin/env python3
"""Build the camera capture library (src/mkwmp_camera.h) with zig cc.

    python build.py [--target windows|linux] [--zig PATH] [--test] [--list]

--target windows (the default): build/mkwmp-camera.dll (x86_64-windows-gnu; DirectShow). --target linux:
build/linux/libmkwmp-camera.so (x86_64-linux-gnu; no capture yet: an empty device list). No third-party source.
--test also builds tests/camera_convert_test.c for the host and runs it. --list builds tests/camera_list.c and runs it
against the library just built, when the host can: it prints the cameras this machine has and opens none.
Works on any host with Zig 0.15; probe/build_plugin.py --package puts the libraries into the mod's folder.
"""
import argparse
import os
import pathlib
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
TESTS = HERE / "tests"
DEFAULT_ZIG = r"C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
FLAGS = ["-O2", "-Wall", "-Wextra", "-Werror", "-DMKWMP_CAMERA_BUILD", f"-I{SRC}"]
TARGETS = {
    # target: (zig triple, build dir, library name, sources, compile and link flags)
    "windows": ("x86_64-windows-gnu", HERE / "build", "mkwmp-camera.dll", ["mkwmp_camera_dshow.c"],
                ["-s", "-lole32", "-loleaut32"]),
    "linux": ("x86_64-linux-gnu", HERE / "build" / "linux", "libmkwmp-camera.so", ["mkwmp_camera_stub.c"],
              ["-fPIC", "-fvisibility=hidden", "-s", "-Wl,--no-undefined", "-Wl,-soname,libmkwmp-camera.so"]),
}


def run(cmd):
    print("+", " ".join(str(c) for c in cmd), flush=True)
    r = subprocess.run([str(c) for c in cmd])
    if r.returncode != 0:
        sys.exit(r.returncode)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", choices=sorted(TARGETS), default="windows")
    ap.add_argument("--zig", default=None)
    ap.add_argument("--test", action="store_true", help="also build and run the picture conversion test (host)")
    ap.add_argument("--list", action="store_true", help="also list this machine's cameras through the library")
    args = ap.parse_args()
    zig = args.zig or shutil.which("zig") or DEFAULT_ZIG
    triple, build, libname, sources, extra = TARGETS[args.target]
    build.mkdir(parents=True, exist_ok=True)
    out = build / libname
    run([zig, "cc", "-target", triple, "-shared"] + FLAGS + extra + ["-o", out] + [SRC / s for s in sources])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    exe = ".exe" if os.name == "nt" else ""
    if args.test:
        test = build / f"camera_convert_test{exe}"
        run([zig, "cc", "-O2", "-Wall", "-Wextra", "-Werror", f"-I{SRC}", "-o", test, TESTS / "camera_convert_test.c"])
        run([test])
    if args.list:
        native = (os.name == "nt") == (args.target == "windows") and sys.platform != "darwin"
        suffix = ".exe" if args.target == "windows" else ""
        lister = build / f"camera_list{suffix}"
        run([zig, "cc", "-target", triple, "-O2", "-Wall", "-Wextra", "-Werror", f"-I{SRC}", "-o", lister,
             TESTS / "camera_list.c"] + ([] if args.target == "windows" else ["-ldl"]))
        if native:
            run([lister, out])
        else:
            print("camera_list built; not run on this host", flush=True)


if __name__ == "__main__":
    main()
