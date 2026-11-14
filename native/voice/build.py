#!/usr/bin/env python3
"""Build mkwmp-voice.dll (lobby voice chat, docs/contracts/VOICE_API.md) with zig c++.

    python build.py [--zig PATH] [--jobs N] [--test]

Third-party sources (libopus 1.5.2 with OSCE + deep PLC and no DRED, RNNoise 0.2, WebRTC AudioProcessing 2.1's AEC3
and the Abseil headers it needs) are fetched by fetch.ps1 (pinned SHA-256, not in git) into third_party/. Nothing
from them is run: their C/C++ files are compiled here. Objects are cached in build/obj (rebuilt when the source or
flags change). Works on any host with Zig 0.15 (cross-compiles to x86_64-windows-gnu).
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
BUILD = HERE / "build"
OBJ = BUILD / "obj"
DEFAULT_ZIG = r"C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
OPUS = TP / "opus-1.5.2"
RNN = TP / "rnnoise-0.2"
WAP = TP / "webrtc-audio-processing-2.1"
ABSL = TP / "abseil-cpp-20240722.0"
TARGET = ["-target", "x86_64-windows-gnu"]


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


def units():
    """(source, language, flags) for every object of the DLL."""
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
    wdefs = ["-DWEBRTC_WIN", "-DNOMINMAX", "-D_USE_MATH_DEFINES", "-DWIN32_LEAN_AND_MEAN", "-DNDEBUG", "-DWEBRTC_ENABLE_AVX2",
             "-DWEBRTC_APM_DEBUG_DUMP=0", "-DRTC_DISABLE_LOGGING", "-DRTC_DISABLE_CHECK_MSG", f"-I{wsrc}", f"-I{ABSL}"]
    for s in wplain:
        out.append((wsrc / s, "c" if s.endswith(".c") else "c++", wdefs))
    for s in wavx2:
        out.append((wsrc / s, "c++", wdefs + ["-mavx", "-mavx2", "-mfma"]))
    out.append((SRC / "voice_echo.cpp", "c++", wdefs))
    own = ["-DNOMINMAX", "-DWIN32_LEAN_AND_MEAN", "-DUNICODE", "-D_UNICODE", f"-I{OPUS / 'include'}", f"-I{RNN / 'include'}",
           "-Wall", "-Wextra"]
    out.append((SRC / "voice_audio.cpp", "c++", own))
    out.append((SRC / "mkwmp_voice.cpp", "c++", own))
    return out


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
    return obj, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zig", default=None)
    ap.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    ap.add_argument("--test", action="store_true", help="also build and run the headless tests")
    args = ap.parse_args()
    zig = args.zig or shutil.which("zig") or DEFAULT_ZIG
    for d, marker in [(OPUS, "COPYING"), (RNN, "COPYING"), (WAP, "COPYING"), (ABSL, "LICENSE")]:
        if not (d / marker).exists():
            sys.exit(f"{d.name} missing: run  powershell -File {HERE / 'fetch.ps1'}")
    OBJ.mkdir(parents=True, exist_ok=True)
    us = units()
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
    rsp = BUILD / "link.rsp"
    rsp.write_text("\n".join(str(o).replace("\\", "/") for o in objs))
    out = BUILD / "mkwmp-voice.dll"
    cmd = [zig, "c++"] + TARGET + ["-shared", "-O2", "-s", "-o", str(out), f"@{rsp}", "-lole32", "-lws2_32", "-luuid"]
    r = subprocess.run(cmd)
    if r.returncode != 0:
        sys.exit(r.returncode)
    print("built", out, out.stat().st_size, "bytes", f"({len(objs)} objects)")
    if args.test:
        for name in ["voice_loudness_test", "voice_wire_test"]:
            exe = BUILD / f"{name}.exe"
            r = subprocess.run([zig, "c++"] + TARGET + ["-std=c++20", "-O2", "-Wall", "-Wextra", f"-I{SRC}",
                                                       str(HERE / "tests" / f"{name}.cpp"), "-o", str(exe)])
            if r.returncode != 0:
                sys.exit(r.returncode)
            if os.name == "nt":
                r = subprocess.run([str(exe)])
                if r.returncode != 0:
                    sys.exit(f"{name} failed")


if __name__ == "__main__":
    main()
