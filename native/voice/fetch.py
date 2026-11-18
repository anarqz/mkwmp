#!/usr/bin/env python3
"""Fetch the voice chat's third-party sources into third_party/ and refuse any other bytes (fetch.ps1's twin for Linux
and macOS hosts): libopus 1.5.2, RNNoise 0.2, the WebRTC AudioProcessing module 2.1 (freedesktop's standalone release,
for the AEC3 echo canceller) and the Abseil 20240722.0 it builds against, the release tarballs pinned by SHA-256. They
are not in git. Nothing from the archives is run: each is downloaded into a fresh empty temp dir, its hash checked,
then extracted by Python's tarfile (no absolute paths, no "..", no links leaving the tree, no devices) and moved into
place. build.py compiles their C/C++ files with zig.

    python3 -I native/voice/fetch.py
"""
import hashlib
import os
import pathlib
import shutil
import sys
import tarfile
import tempfile
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
THIRD = HERE / "third_party"

SOURCES = [
    ("opus-1.5.2", "https://github.com/xiph/opus/releases/download/v1.5.2/opus-1.5.2.tar.gz",
     "65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1", "COPYING"),
    ("rnnoise-0.2", "https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz",
     "90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37", "COPYING"),
    ("webrtc-audio-processing-2.1",
     "https://freedesktop.org/software/pulseaudio/webrtc-audio-processing/webrtc-audio-processing-2.1.tar.xz",
     "ae9302824b2038d394f10213cab05312c564a038434269f11dbf68f511f9f9fe", "COPYING"),
    ("abseil-cpp-20240722.0", "https://github.com/abseil/abseil-cpp/releases/download/20240722.0/abseil-cpp-20240722.0.tar.gz",
     "f50e5ac311a81382da7fa75b97310e4b9006474f9560ac46f54a9967f07d4ae3", "LICENSE"),
]


def download(url, path):
    req = urllib.request.Request(url, headers={"User-Agent": "fetch.py"})
    with urllib.request.urlopen(req, timeout=120) as r, open(path, "wb") as f:
        shutil.copyfileobj(r, f)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def safe_members(tar, name):
    """Every member stays under `name`/: regular files and directories only (no links, devices or fifos)."""
    out = []
    for m in tar.getmembers():
        p = m.name.replace("\\", "/")
        parts = [x for x in p.split("/") if x not in ("", ".")]
        if p.startswith("/") or (len(p) > 1 and p[1] == ":") or ".." in parts:
            sys.exit(f"{name}: refusing archive member {m.name!r} (absolute or ..)")
        if not parts:
            continue
        if parts[0] != name:
            sys.exit(f"{name}: refusing archive member {m.name!r} (outside {name}/)")
        if m.issym() or m.islnk():
            print(f"{name}: skipping link {m.name!r}")
            continue
        if not (m.isfile() or m.isdir()):
            sys.exit(f"{name}: refusing archive member {m.name!r} (not a file or directory)")
        m.mode = 0o644 if m.isfile() else 0o755  # nothing executable, no setuid/setgid
        m.uid = m.gid = 0
        m.uname = m.gname = ""
        out.append(m)
    return out


def fetch(name, url, digest, marker):
    target = THIRD / name
    if (target / marker).exists():
        return
    if target.exists():
        shutil.rmtree(target)  # a partial earlier fetch
    work = pathlib.Path(tempfile.mkdtemp(prefix="mkwmp-voice-fetch-"))  # fresh and empty
    try:
        archive = work / url.rsplit("/", 1)[-1]
        download(url, archive)
        got = sha256(archive)
        if got != digest:
            sys.exit(f"{name}: sha256 {got} is not the pinned {digest}")
        out = work / "x"
        out.mkdir()
        with tarfile.open(archive, "r:*") as tar:
            members = safe_members(tar, name)
            if hasattr(tarfile, "data_filter"):
                tar.extractall(out, members=members, filter="data")
            else:
                tar.extractall(out, members=members)
        if not (out / name / marker).exists():
            sys.exit(f"{name}: {marker} missing from the archive")
        THIRD.mkdir(parents=True, exist_ok=True)
        shutil.move(str(out / name), str(target))
        print(f"{name}: fetched, sha256 ok")
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main():
    for s in SOURCES:
        fetch(*s)


if __name__ == "__main__":
    main()
