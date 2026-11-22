#!/usr/bin/env python3
"""Build the probe as a native mkw-recomp plugin (src/mkw_plugin.h: mkw-recomp's plugin API version 1) with zig cc.

    python build_plugin.py [--zig PATH] [--version X.Y.Z] [--target linux|windows|all] [--test] [--package]

Produces build/plugin/linux/mkwmp.so (x86_64-linux-gnu) and build/plugin/windows/mkwmp.dll (x86_64-windows-gnu, a
plain DLL, not the winmm.dll proxy): the core sources of build.py (minus the winmm loader and the COFF backend, as
build_linux.py), the plat files of the target (with its HTTP: WinHTTP, or libcurl loaded at run
time), the plugin's seat client (seatapi.c, seatd.c), the GameChat camera (camera.c, camcodec.c, camlib.c; plugin
API 3), the chat room on the relay (roomnet.c), the overlay trigger (overlay.c) and src/backend_plugin.c. No options page or
settings tab: the MKWMP launcher sets everything. Both export only mkw_plugin_init (Linux: a version script, linked with --no-undefined). --test also builds tests/test_plugin.c and runs
it against the Linux .so (on a Linux host): a fake MkwPluginApi, no runtime needed. Runs in CI
(.github/workflows/probe-linux.yml).

--package lays the mod out as mkw-recomp's Mods page imports it (and as `mkw --play --mod mkwmp` loads it from
<game>/mods/), with whatever was built, and zips it:

    build/plugin/mod/mkwmp/mod.json
    build/plugin/mod/mkwmp/plugin/mkwmp.dll, mkwmp.so    (only libraries with mkw_plugin_init go in plugin/: the
                                                         runtime loads every one there)
    build/plugin/mod/mkwmp/mkwmp-voice.dll, libmkwmp-voice.so   (when native/voice built them; the probe's home is
                                                         the mod folder, so it loads them from there)
    build/plugin/mod/mkwmp/mkwmp-camera.dll, libmkwmp-camera.so (when native/camera built them: the webcam capture)
    build/plugin/mkwmp-mod.zip
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
# the plugin's own parts: its seat client (no launcher running), the camera, the friend room, the overlay trigger
PLUGIN_ONLY = ["seatapi.c", "seatd.c", "camera.c", "camcodec.c", "camlib.c", "chatroom.c", "roomnet.c", "overlay.c"]
WIN_PLAT = ["plat_win.c", "plat_net_win.c", "plat_http_win.c"]
POSIX_HTTP = ["plat_http_posix.c"]
VERSION_SCRIPT = "{ global: mkw_plugin_init; local: *; };\n"
# the controller under Wine / Proton (XInput asked at load; a no-op elsewhere, so in both builds)
PLUGIN_ONLY = PLUGIN_ONLY + ["winepad.c"]


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
        + [str(SRC / s) for s in core() + POSIX + POSIX_HTTP + PLUGIN_ONLY + [BACKEND]] + ["-lpthread", "-ldl"])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    return out


def build_windows(zig, version):
    out_dir = OUT / "windows"
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "mkwmp.dll"
    # only __declspec(dllexport) symbols are exported (mkw_plugin_init), no import library is needed
    run([zig, "cc", "-target", "x86_64-windows-gnu", "-shared", "-O2",
         "-Wall", "-Wextra", "-Werror", f'-DMKWMP_VERSION="{version}"', "-o", str(out)]
        + [str(SRC / s) for s in core() + WIN_PLAT + PLUGIN_ONLY + [BACKEND]] + ["-lws2_32", "-lwinhttp"])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    return out


def test(zig, so):
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        sys.exit("build_plugin.py --test: runs the x86_64 Linux .so, so it needs an x86_64 Linux host")
    exe = OUT / "linux" / "test_plugin"
    run([zig, "cc", "-target", "x86_64-linux-gnu", "-O1", "-Wall", "-Wextra", "-Werror", "-o", str(exe),
         str(HERE / "tests" / "test_plugin.c"), "-ldl"])
    # the plugin's own sign-in (the launcher's settings) against a fake seat server (tests/fake_seat.py, plain HTTP)
    import os
    import tempfile
    import time
    with tempfile.TemporaryDirectory() as tmp:
        portfile = pathlib.Path(tmp) / "port"
        server = subprocess.Popen([sys.executable, "-I", str(HERE / "tests" / "fake_seat.py"), str(portfile)])
        try:
            for _ in range(100):
                if portfile.exists() and portfile.read_text():
                    break
                time.sleep(0.05)
            env = dict(os.environ, MKWMP_TEST_SERVER=f"http://127.0.0.1:{portfile.read_text()}")
            print("+", exe, so, flush=True)
            r = subprocess.run([str(exe), str(so), str(SRC / "npln.c")], env=env)
            if r.returncode:
                sys.exit(r.returncode)
            # the GameChat camera's gate: plugin API 3 + game 1.8.0 registers it, anything else leaves it off
            cam = OUT / "linux" / "test_plugin_camera"
            run([zig, "cc", "-target", "x86_64-linux-gnu", "-O1", "-Wall", "-Wextra", "-Werror", "-o", str(cam),
                 str(HERE / "tests" / "test_plugin_camera.c"), "-ldl"])
            for mode in ("v3", "v2", "game100"):
                mod_dir = pathlib.Path(tmp) / f"camera-{mode}"
                mod_dir.mkdir()
                run([str(cam), str(so), str(mod_dir), mode])
            # seatd.c alone (not exported by the .so): with the core sources it needs
            seatd = OUT / "linux" / "test_seatd"
            run([zig, "cc", "-target", "x86_64-linux-gnu", "-O1", "-Wall", "-Wextra", "-Werror", "-o", str(seatd),
                 str(HERE / "tests" / "test_seatd.c"), str(HERE / "tests" / "stub_rtapi.c")]
                + [str(SRC / s) for s in ["seatd.c", "seatapi.c", "seat.c", "config.c", "log.c", "launcher.c",
                                          "notify.c", "plat_posix.c", "plat_net_posix.c", "plat_http_posix.c"]]
                + ["-lpthread", "-ldl"])
            home = pathlib.Path(tmp) / "home"
            home.mkdir()
            run([str(seatd), env["MKWMP_TEST_SERVER"], str(home)])
        finally:
            server.kill()


VOICE = HERE.parent / "voice" / "build"
CAMERA = HERE.parent / "camera" / "build"


def package(version):
    import json
    import zipfile
    root = OUT / "mod"
    mod = root / "mkwmp"
    if mod.exists():
        shutil.rmtree(mod)
    (mod / "plugin").mkdir(parents=True)
    libs = [(OUT / "windows" / "mkwmp.dll", mod / "plugin" / "mkwmp.dll"),
            (OUT / "linux" / "mkwmp.so", mod / "plugin" / "mkwmp.so"),
            (VOICE / "mkwmp-voice.dll", mod / "mkwmp-voice.dll"),
            (VOICE / "linux" / "libmkwmp-voice.so", mod / "libmkwmp-voice.so"),
            (CAMERA / "mkwmp-camera.dll", mod / "mkwmp-camera.dll"),
            (CAMERA / "linux" / "libmkwmp-camera.so", mod / "libmkwmp-camera.so")]
    for src, dst in libs:
        if src.exists():
            shutil.copy2(src, dst)
            print("package:", dst.relative_to(root), flush=True)
    if not any((mod / "plugin").iterdir()):
        sys.exit("build_plugin.py --package: no plugin library was built")
    meta = {"title": "MKWMP online play", "version": version, "has_plugin": True}
    (mod / "mod.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    z = OUT / "mkwmp-mod.zip"
    with zipfile.ZipFile(z, "w", zipfile.ZIP_DEFLATED) as out:
        for f in sorted(mod.rglob("*")):
            if f.is_file():
                out.write(f, f.relative_to(root).as_posix())
    print("packaged", z, z.stat().st_size, "bytes", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zig", default=None, help="zig executable (default: zig on PATH)")
    ap.add_argument("--version", default="dev", help="version string logged by the probe (release tag)")
    ap.add_argument("--target", choices=["linux", "windows", "all"], default="all")
    ap.add_argument("--test", action="store_true", help="also build and run tests/test_plugin.c (Linux .so)")
    ap.add_argument("--package", action="store_true", help="lay out build/plugin/mod/mkwmp/ and zip it")
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
    if args.package:
        package(args.version)


if __name__ == "__main__":
    main()
