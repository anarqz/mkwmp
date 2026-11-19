#!/usr/bin/env python3
"""Build the voice chat library (docs/contracts/VOICE_API.md) with zig c++.

    python build.py [--target windows|linux] [--zig PATH] [--jobs N] [--test] [--smoke]

--target windows (the default): build/mkwmp-voice.dll (x86_64-windows-gnu; WASAPI). --target linux:
build/linux/libmkwmp-voice.so (x86_64-linux-gnu, -fPIC; PulseAudio, libpulse loaded at run time, so the library
links only libc, libdl and libpthread). --test also builds the headless tests and runs them when the host can
(Windows tests on Windows, Linux tests on Linux): the loudness, the datagrams (voice_wire_test: the contract's vectors),
video/v1's chunks and pacing (voice_video_test), the engine, and chat rooms end to end (voice_room_test: the library
loaded twice against a relay inside the test; about 25 s). --smoke (Linux) builds and runs tests/voice_pulse_smoke.cpp
against the real sound server: it opens the default devices for about 3 s and plays only silence.

Third-party sources (libopus 1.5.2 with OSCE + deep PLC and no DRED, RNNoise 0.2, WebRTC AudioProcessing 2.1's AEC3
and the Abseil headers it needs) are fetched by fetch.ps1 or fetch.py (pinned SHA-256, not in git) into third_party/.
Nothing from them is run: their C/C++ files are compiled here. Objects are cached per target in build/obj and
build/linux/obj (rebuilt when the source or flags change). Works on any host with Zig 0.15.
"""
import argparse
import concurrent.futures
import hashlib
import os
import pathlib
import re
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
TP = HERE / "third_party"
TESTS = HERE / "tests"
DEFAULT_ZIG = r"C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
OPUS = TP / "opus-1.5.2"
RNN = TP / "rnnoise-0.2"
WAP = TP / "webrtc-audio-processing-2.1"
ABSL = TP / "abseil-cpp-20240722.0"
TARGETS = {
    # target: (zig triple, build dir, library name, compile flags of every unit, link libraries)
    "windows": ("x86_64-windows-gnu", HERE / "build", "mkwmp-voice.dll", [], ["-lole32", "-lws2_32", "-luuid"]),
    "linux": ("x86_64-linux-gnu", HERE / "build" / "linux", "libmkwmp-voice.so", ["-fPIC", "-fvisibility=hidden"],
              ["-ldl", "-lpthread"]),
}
TARGET = []  # ["-target", triple], set by main
OBJ = None   # the target's object cache, set by main


def read_mk(files):
    """libopus' own source lists (opus_sources.mk etc.), parsed as data."""
    out = {}
    for f in files:
        name = None
        for line in f.read_text().splitlines():
            m = re.match(r"^\s*([A-Z0-9_]+)\s*=\s*(.*)$", line)
            if m:
                name, rest = m.group(1), m.group(2)
                out[name] = []
            elif name:
                rest = line
            else:
                continue
            more = rest.rstrip().endswith("\\")
            out[name] += [t for t in rest.rstrip("\\ \t").split() if t.endswith(".c")]
            if not more:
                name = None
    return out


def units(target):
    """(source, language, flags) for every object of the library."""
    mk = read_mk([OPUS / "opus_sources.mk", OPUS / "celt_sources.mk", OPUS / "silk_sources.mk", OPUS / "lpcnet_sources.mk"])
    plain = ["OPUS_SOURCES", "OPUS_SOURCES_FLOAT", "CELT_SOURCES", "CELT_SOURCES_X86_RTCD", "CELT_SOURCES_SSE", "CELT_SOURCES_SSE2",
             "SILK_SOURCES", "SILK_SOURCES_FLOAT", "SILK_SOURCES_X86_RTCD", "DEEP_PLC_SOURCES", "OSCE_SOURCES", "DNN_SOURCES_X86_RTCD",
             "DNN_SOURCES_SSE2"]
    sse41 = ["CELT_SOURCES_SSE4_1", "SILK_SOURCES_SSE4_1", "DNN_SOURCES_SSE4_1"]
    avx2 = ["CELT_SOURCES_AVX2", "SILK_SOURCES_AVX2", "SILK_SOURCES_FLOAT_AVX2", "DNN_SOURCES_AVX2"]
    odefs = ["-DOPUS_BUILD", "-DUSE_ALLOCA", "-DENABLE_DEEP_PLC", "-DENABLE_OSCE", "-DDISABLE_DEBUG_FLOAT", "-DOPUS_HAVE_RTCD",
             "-DCPU_INFO_BY_ASM", "-DOPUS_X86_MAY_HAVE_SSE", "-DOPUS_X86_MAY_HAVE_SSE2", "-DOPUS_X86_MAY_HAVE_SSE4_1",
             "-DOPUS_X86_MAY_HAVE_AVX2", "-DOPUS_X86_PRESUME_SSE", "-DOPUS_X86_PRESUME_SSE2", '-DPACKAGE_VERSION="1.5.2"',
             f"-I{OPUS / 'include'}", f"-I{OPUS}", f"-I{OPUS / 'celt'}", f"-I{OPUS / 'silk'}", f"-I{OPUS / 'silk' / 'float'}",
             f"-I{OPUS / 'dnn'}"]
    out = []
    for group, extra in [(plain, []), (sse41, ["-msse4.1"]), (avx2, ["-mavx", "-mavx2", "-mfma"])]:
        for var in group:
            for s in mk.get(var, []):
                if "dred" in s or "lossgen" in s:
                    sys.exit(f"refusing DRED/lossgen source {s}")
                out.append((OPUS / s, "c", odefs + extra))
    rdefs = ["-DDISABLE_DEBUG_FLOAT", "-DOPUS_X86_MAY_HAVE_SSE2", f"-I{RNN / 'include'}", f"-I{RNN / 'src'}"]
    for s in ["denoise.c", "rnn.c", "pitch.c", "kiss_fft.c", "celt_lpc.c", "nnet.c", "nnet_default.c", "parse_lpcnet_weights.c",
              "rnnoise_data.c", "rnnoise_tables.c"]:
        out.append((RNN / "src" / s, "c", rdefs))
    # AEC3 and the 100 Hz high-pass only (as ForzaInfinity's build.ps1): every aec3/ source meson.build lists and the
    # few classes around it. No noise suppressor, AGC, AECM or VAD; logging compiled out.
    wsrc = WAP / "webrtc"
    meson = (wsrc / "modules" / "audio_processing" / "meson.build").read_text()
    aec3 = [f"modules/audio_processing/{m}" for m in re.findall(r"'(aec3/[a-z0-9_]+\.cc)'", meson)]
    wplain = [a for a in aec3 if not a.endswith("_avx2.cc")] + [
        "modules/audio_processing/audio_buffer.cc", "modules/audio_processing/high_pass_filter.cc",
        "modules/audio_processing/splitting_filter.cc", "modules/audio_processing/three_band_filter_bank.cc",
        "modules/audio_processing/utility/cascaded_biquad_filter.cc", "modules/audio_processing/logging/apm_data_dumper.cc",
        "api/audio/echo_canceller3_config.cc", "rtc_base/checks.cc", "rtc_base/memory/aligned_malloc.cc",
        "rtc_base/experiments/field_trial_parser.cc", "rtc_base/platform_thread_types.cc", "rtc_base/race_checker.cc",
        "rtc_base/string_encode.cc", "system_wrappers/source/field_trial.cc", "system_wrappers/source/metrics.cc",
        "system_wrappers/source/cpu_features.cc", "common_audio/audio_util.cc", "common_audio/resampler/push_sinc_resampler.cc",
        "common_audio/resampler/sinc_resampler.cc", "common_audio/resampler/sinc_resampler_sse.cc",
        "common_audio/third_party/ooura/fft_size_128/ooura_fft.cc", "common_audio/third_party/ooura/fft_size_128/ooura_fft_sse2.cc",
        "common_audio/signal_processing/splitting_filter.c"]
    wavx2 = [a for a in aec3 if a.endswith("_avx2.cc")] + ["common_audio/resampler/sinc_resampler_avx2.cc"]
    if len(wplain) < 70 or len(wavx2) != 6:
        sys.exit(f"unexpected WebRTC AEC3 source list ({len(wplain)} + {len(wavx2)})")
    platform = ["-DWEBRTC_WIN", "-DNOMINMAX", "-D_USE_MATH_DEFINES", "-DWIN32_LEAN_AND_MEAN"] if target == "windows" else [
        "-DWEBRTC_POSIX", "-DWEBRTC_LINUX", "-D_USE_MATH_DEFINES"]
    wdefs = platform + ["-DNDEBUG", "-DWEBRTC_ENABLE_AVX2", "-DWEBRTC_APM_DEBUG_DUMP=0", "-DRTC_DISABLE_LOGGING",
                        "-DRTC_DISABLE_CHECK_MSG", f"-I{wsrc}", f"-I{ABSL}"]
    for s in wplain:
        out.append((wsrc / s, "c" if s.endswith(".c") else "c++", wdefs))
    for s in wavx2:
        out.append((wsrc / s, "c++", wdefs + ["-mavx", "-mavx2", "-mfma"]))
    out.append((SRC / "voice_echo.cpp", "c++", wdefs))
    own = (["-DNOMINMAX", "-DWIN32_LEAN_AND_MEAN", "-DUNICODE", "-D_UNICODE"] if target == "windows" else []) + [
        f"-I{OPUS / 'include'}", f"-I{RNN / 'include'}", "-Wall", "-Wextra"]
    out.append((SRC / "voice_audio.cpp", "c++", own))
    out.append((SRC / ("voice_audio_wasapi.cpp" if target == "windows" else "voice_audio_pulse.cpp"), "c++", own))
    out.append((SRC / "mkwmp_voice.cpp", "c++", own))
    extra = TARGETS[target][3]
    return [(src, lang, flags + extra) for src, lang, flags in out]


def compile_one(zig, src, lang, flags):
    std = ["-std=c++20"] if lang == "c++" else ["-std=gnu11"]
    cmd = [zig, "c++" if lang == "c++" else "cc"] + TARGET + std + ["-O2", "-c", "-fno-sanitize=all", "-w" if "-Wall" not in flags else ""] + flags
    cmd = [c for c in cmd if c]
    key = hashlib.sha256((str(src) + "\0" + "\0".join(cmd)).encode()).hexdigest()[:16]
    obj = OBJ / f"{src.stem}-{key}.o"
    if obj.exists() and obj.stat().st_mtime >= src.stat().st_mtime and not any(
            h.stat().st_mtime > obj.stat().st_mtime for h in SRC.glob("*.h")):
        return obj, None
    r = subprocess.run(cmd + [str(src), "-o", str(obj)], capture_output=True, text=True)
    if r.returncode != 0:
        return None, f"{src}:\n{r.stderr[-4000:]}"
    if r.stderr.strip() and "-Wall" in flags:  # our own sources: their warnings are shown
        print(r.stderr, file=sys.stderr, end="", flush=True)
    return obj, None


def link(zig, args, out):
    r = subprocess.run([zig, "c++"] + TARGET + args + ["-o", str(out)])
    if r.returncode != 0:
        sys.exit(r.returncode)


def main():
    global TARGET, OBJ
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", choices=sorted(TARGETS), default="windows")
    ap.add_argument("--zig", default=None)
    ap.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    ap.add_argument("--test", action="store_true", help="also build the headless tests (and run them where the host can)")
    ap.add_argument("--smoke", action="store_true", help="linux: build and run the real-device smoke test (silence only)")
    args = ap.parse_args()
    triple, build, libname, _, libs = TARGETS[args.target]
    TARGET = ["-target", triple]
    OBJ = build / "obj"
    zig = args.zig or shutil.which("zig") or DEFAULT_ZIG
    for d, marker in [(OPUS, "COPYING"), (RNN, "COPYING"), (WAP, "COPYING"), (ABSL, "LICENSE")]:
        if not (d / marker).exists():
            fetch = f"powershell -File {HERE / 'fetch.ps1'}" if os.name == "nt" else f"python3 -I {HERE / 'fetch.py'}"
            sys.exit(f"{d.name} missing: run  {fetch}")
    OBJ.mkdir(parents=True, exist_ok=True)
    us = units(args.target)
    objs, errors = [], []
    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        for obj, err in pool.map(lambda u: compile_one(zig, *u), us):
            if err:
                errors.append(err)
            else:
                objs.append(obj)
    if errors:
        for e in errors[:5]:
            print(e, file=sys.stderr)
        sys.exit(f"{len(errors)} of {len(us)} units failed")
    rsp = build / "link.rsp"
    rsp.write_text("\n".join(str(o).replace("\\", "/") for o in objs))
    out = build / libname
    if args.target == "windows":
        link(zig, ["-shared", "-O2", "-s", f"@{rsp}"] + libs, out)
    else:
        # only the C ABI leaves the library: the codecs, libc++ and the engine stay inside, whatever else the game loads
        exports = build / "exports.map"
        exports.write_text("{\n  global: mkwmp_voice_*;\n  local: *;\n};\n")
        link(zig, ["-shared", "-fPIC", "-O2", "-s", f"-Wl,--version-script={exports}", f"-Wl,-soname,{libname}", f"@{rsp}"] + libs, out)
    print("built", out, out.stat().st_size, "bytes", f"({len(objs)} objects)", flush=True)
    if not (args.test or args.smoke):
        return
    # The tests link the engine's objects directly (not the library, which exports only the C ABI) and bring their own
    # log sink, so mkwmp_voice.cpp (the relay client and its thread) is left out.
    engine = [o for o in objs if not o.name.startswith("mkwmp_voice-")]
    erst = build / "engine.rsp"
    erst.write_text("\n".join(str(o).replace("\\", "/") for o in engine))
    native = (os.name == "nt") == (args.target == "windows") and sys.platform != "darwin"
    exe_suffix = ".exe" if args.target == "windows" else ""
    flags = ["-std=c++20", "-O2", "-Wall", "-Wextra", f"-I{SRC}", f"-I{OPUS / 'include'}"] + TARGETS[args.target][3]
    runs = []
    if args.test:
        for name in ["voice_loudness_test", "voice_wire_test", "voice_video_test"]:
            exe = build / f"{name}{exe_suffix}"
            link(zig, flags + [str(TESTS / f"{name}.cpp")], exe)
            runs.append(exe)
        exe = build / f"voice_engine_test{exe_suffix}"
        link(zig, flags + [str(TESTS / "voice_engine_test.cpp"), f"@{erst}"] + libs, exe)
        runs.append(exe)
        # chat rooms and video end to end: two copies of the library just built and a relay inside the test
        exe = build / f"voice_room_test{exe_suffix}"
        link(zig, flags + [str(TESTS / "voice_room_test.cpp")] + libs, exe)
        runs.append(exe)
    if args.smoke:
        if args.target != "linux":
            sys.exit("--smoke is the Linux (PulseAudio) device test")
        exe = build / "voice_pulse_smoke"
        link(zig, flags + [str(TESTS / "voice_pulse_smoke.cpp"), f"@{erst}"] + libs, exe)
        runs.append(exe)
    if not native:
        print("tests built; not run on this host", flush=True)
        return
    for exe in runs:
        args_ = [str(exe)] + ([str(out)] if exe.name == "voice_pulse_smoke" else [])
        if exe.stem == "voice_room_test":
            args_ += [str(out), str(build)]
        r = subprocess.run(args_)
        if r.returncode != 0:
            sys.exit(f"{exe.name} failed")
        print(exe.name, "passed", flush=True)


if __name__ == "__main__":
    main()
