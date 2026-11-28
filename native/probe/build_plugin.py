#!/usr/bin/env python3
"""Build the probe as a native mkw-recomp plugin (src/mkw_plugin.h: mkw-recomp's plugin API version 1) with zig cc.

    python build_plugin.py [--zig PATH] [--version X.Y.Z] [--target linux|windows|all] [--test] [--test-ingame] [--package]

Produces build/plugin/linux/mkwmp.so (x86_64-linux-gnu) and build/plugin/windows/mkwmp.dll (x86_64-windows-gnu, a
plain DLL, not the winmm.dll proxy): the core sources of build.py (minus the winmm loader and the COFF backend, as
build_linux.py), the plat files of the target (with its HTTP: WinHTTP, or libcurl loaded at run
time; and its TLS client for the game's ghost transfers: Schannel, or OpenSSL loaded at run time), the plugin's seat client (seatapi.c, seatd.c), the GameChat camera (camera.c, camcodec.c, camlib.c; plugin
API 3), the chat room on the relay (roomnet.c), the overlay trigger (overlay.c) and src/backend_plugin.c. No options page or
settings tab: the MKWMP launcher sets everything. Both export only mkw_plugin_init (Linux: a version script, linked with --no-undefined). --test also builds tests/test_plugin.c and runs
it against the Linux .so (on a Linux host): a fake MkwPluginApi, no runtime needed. Runs in CI
(.github/workflows/probe-linux.yml).

The in-game overlay (src/ingame.h, src/ingame/: the launcher's toasts and its overlay page drawn in the game's frames)
is C++: its sources and the vendored Dear ImGui 1.91.8 (third_party/imgui, as Infinity's overlay vendors it) are
compiled to objects with zig c++ (C++20, no exceptions, no RTTI; ours with -Wall -Wextra -Werror, ImGui's without
-Werror) and linked into the same library with zig's own libc++, statically (-lc++), so the mod still needs no runtime
library of its own. Nunito is embedded from third_party/fonts by src/ingame/fonts.c (C23 #embed). --test-ingame builds
tests/test_ingame.cpp for the host (Windows or Linux) and runs it: a fake plugin API 5 and a stub launcher, no game.

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
# ghost data: the console's TLS service for the game's HTTP client (nnssl.c) and the log of its transfers (ghost.c)
PLUGIN_ONLY = PLUGIN_ONLY + ["nnssl.c", "ghost.c"]
WIN_PLAT = ["plat_win.c", "plat_net_win.c", "plat_http_win.c", "plat_tls_win.c"]
POSIX_HTTP = ["plat_http_posix.c", "plat_tls_posix.c"]
VERSION_SCRIPT = "{ global: mkw_plugin_init; local: *; };\n"
# the controller under Wine / Proton (XInput asked at load; a no-op elsewhere, so in both builds)
PLUGIN_ONLY = PLUGIN_ONLY + ["winepad.c"]
# the in-game overlay (src/ingame.h): its C part (the embedded fonts), its C++ parts, and the mod's own Dear ImGui
INGAME = SRC / "ingame"
IMGUI = HERE / "third_party" / "imgui"
INGAME_C = ["ingame/fonts.c"]
INGAME_CPP = ["core.cpp", "kit.cpp", "page.cpp", "toasts_exact.cpp", "toasts_basic.cpp"]
IMGUI_CPP = ["imgui.cpp", "imgui_draw.cpp", "imgui_tables.cpp", "imgui_widgets.cpp"]
CXX = ["-std=c++20", "-O2", "-fno-exceptions", "-fno-rtti", "-fvisibility=hidden",
       '-DIMGUI_USER_CONFIG="imconfig_mkwmp.h"', f"-I{INGAME}", f"-I{IMGUI}"]
WARN = ["-Wall", "-Wextra", "-Werror"]
EMBED = "-Wno-c23-extensions"   # fonts.c: #embed, in every C mode of zig's clang


def run(cmd):
    print("+", " ".join(cmd), flush=True)
    r = subprocess.run(cmd)
    if r.returncode != 0:
        sys.exit(r.returncode)


def ingame_objects(zig, target, out_dir):
    """The in-game overlay's C++ as objects for `target`, compiled side by side (zig caches each)."""
    from concurrent.futures import ThreadPoolExecutor
    obj = out_dir / "obj"
    obj.mkdir(parents=True, exist_ok=True)
    pic = ["-fPIC"] if "linux" in target else []
    jobs = []
    for folder, names, warn in ((IMGUI, IMGUI_CPP, []), (INGAME, INGAME_CPP, WARN)):
        for name in names:
            out = obj / (pathlib.Path(name).stem + ".o")
            jobs.append((out, [zig, "c++", "-target", target, "-c"] + CXX + pic + warn + ["-o", str(out), str(folder / name)]))

    def compile_one(job):
        print("+", " ".join(job[1]), flush=True)
        return subprocess.run(job[1]).returncode

    with ThreadPoolExecutor(max_workers=4) as pool:
        codes = list(pool.map(compile_one, jobs))
    if any(codes):
        sys.exit(next(c for c in codes if c))
    return [str(out) for out, _ in jobs]


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
    objects = ingame_objects(zig, "x86_64-linux-gnu", out_dir)
    run([zig, "cc", "-target", "x86_64-linux-gnu", "-shared", "-fPIC", "-fvisibility=hidden", "-O2",
         "-Wall", "-Wextra", "-Werror", EMBED, f'-DMKWMP_VERSION="{version}"',
         "-Wl,--no-undefined", "-Wl,--strip-debug", f"-Wl,--version-script={script}", "-o", str(out)]
        + [str(SRC / s) for s in core() + POSIX + POSIX_HTTP + PLUGIN_ONLY + INGAME_C + [BACKEND]] + objects
        + ["-lc++", "-lpthread", "-ldl"])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    return out


def build_windows(zig, version):
    out_dir = OUT / "windows"
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "mkwmp.dll"
    objects = ingame_objects(zig, "x86_64-windows-gnu", out_dir)
    # only __declspec(dllexport) symbols are exported (mkw_plugin_init), no import library is needed
    run([zig, "cc", "-target", "x86_64-windows-gnu", "-shared", "-O2",
         "-Wall", "-Wextra", "-Werror", EMBED, f'-DMKWMP_VERSION="{version}"', "-o", str(out)]
        + [str(SRC / s) for s in core() + WIN_PLAT + PLUGIN_ONLY + INGAME_C + [BACKEND]] + objects
        + ["-lc++", "-lws2_32", "-lwinhttp", "-lsecur32", "-lcrypt32"])
    print("built", out, out.stat().st_size, "bytes", flush=True)
    return out


def test_ingame(zig):
    """tests/test_ingame.cpp for the host: the in-game overlay against a fake plugin API 5 and a stub launcher."""
    windows = platform.system() == "Windows"
    if not windows and (platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64")):
        sys.exit("build_plugin.py --test-ingame: needs a Windows or an x86_64 Linux host")
    target = "x86_64-windows-gnu" if windows else "x86_64-linux-gnu"
    out_dir = OUT / "test-ingame"
    objects = ingame_objects(zig, target, out_dir)
    test_obj = out_dir / "obj" / "test_ingame.o"
    run([zig, "c++", "-target", target, "-c"] + CXX + WARN + ["-o", str(test_obj), str(HERE / "tests" / "test_ingame.cpp")])
    exe = out_dir / ("test_ingame.exe" if windows else "test_ingame")
    run([zig, "cc", "-target", target, "-O1", EMBED, "-o", str(exe), str(SRC / INGAME_C[0]), str(test_obj)] + objects
        + ["-lc++"] + ([] if windows else ["-lpthread"]))
    run([str(exe), str(HERE / "tests" / "data")])


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
    ap.add_argument("--test-ingame", action="store_true",
                    help="build and run tests/test_ingame.cpp on the host (the in-game overlay; with it alone, "
                         "no library is built)")
    ap.add_argument("--package", action="store_true", help="lay out build/plugin/mod/mkwmp/ and zip it")
    args = ap.parse_args()
    zig = args.zig or shutil.which("zig")
    if not zig:
        sys.exit("zig not found (Zig 0.15)")
    so = None
    only_ingame = args.test_ingame and not args.test and not args.package and not any(a.startswith("--target") for a in sys.argv)
    if not only_ingame and (args.target in ("linux", "all") or args.test):
        so = build_linux(zig, args.version)
    if not only_ingame and args.target in ("windows", "all"):
        build_windows(zig, args.version)
    if args.test_ingame or (args.test and platform.system() == "Linux"):
        test_ingame(zig)
    if args.test:
        test(zig, so)
    if args.package:
        package(args.version)


if __name__ == "__main__":
    main()
