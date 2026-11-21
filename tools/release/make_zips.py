"""Assemble the release zip (run by release.sh publish).

    python tools/release/make_zips.py <version> <release key file> <assets dir>

<assets dir> holds the release's loose assets (winmm.dll, mkwmp-voice.dll,
mkwmp-mod.zip, ...) and the draft's working file
mkwmp-launcher-files-windows.zip (tools/release/windows.ps1: the launcher's
publish folder without game/). Writes into it:

  mkwmp-<version>-windows-x64.zip:
    mkwmp/mkwmp-launcher.exe      the launcher (launcher/, C# WPF), with what
    mkwmp/*.dll                   the working file has beside it: its native
    mkwmp/mkwmp-updater.exe       libraries, the update helper, i18n/,
    mkwmp/i18n/, licenses/, ...   licenses/, mkwmp-launcher.png, CHANGELOG.md
    mkwmp/game/winmm.dll          the game patch
    mkwmp/game/mkwmp-voice.dll    voice chat, next to it in the game folder
    mkwmp/game/mkwmp-mod.zip      the mkw-recomp mod: what the launcher installs
    mkwmp/game/SHA256SUMS(.sig)   signed hashes of the three: the launcher
                                  checks the game folder against them offline
    mkwmp/LICENSE, NOTICE.md, licenses/, README.txt

It is at once the download of a new player, what install-mkwmp.sh installs
under Proton, and the package the launcher's update helper installs
(launcher/docs/UPDATES.md). The loose mkwmp-launcher-x86_64-windows.exe is
NOT taken: it is the self-extracting single file for launchers from before
the zip, which swap only their exe.

Since 1.4.0 there is no Linux zip (the Linux launcher was the Rust one).
The launcher keeps client.toml and logs/ next to itself, in mkwmp/.
"""

import io
import pathlib
import subprocess
import sys
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
HERE = pathlib.Path(__file__).resolve().parent
DLLS = ["winmm.dll", "mkwmp-voice.dll", "mkwmp-mod.zip"]
WORK = "mkwmp-launcher-files-windows.zip"
EXE = "mkwmp-launcher.exe"
# Never in a release zip (UPDATES.md section 5: the player's own files), whatever the working file holds.
PLAYERS = ("logs/", ".mkwmp-selfupdate/", "game/")


def add(z, arc, data, exe=False):
    info = zipfile.ZipInfo(f"mkwmp/{arc}", date_time=(2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # unix, so the mode below is honoured
    info.external_attr = ((0o755 if exe else 0o644) | 0o100000) << 16
    z.writestr(info, data)


def launcher_files(work):
    """(path, bytes) of the working file, checked: plain relative paths, the launcher and its helper there."""
    out = {}
    with zipfile.ZipFile(io.BytesIO(work.read_bytes())) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            name = info.filename
            parts = pathlib.PurePosixPath(name).parts
            if name.startswith("/") or "\\" in name or ":" in name or any(p in ("", ".", "..") for p in parts):
                sys.exit(f"{work.name}: unsafe path {name!r}")
            if name.lower() in (n.lower() for n in out):
                sys.exit(f"{work.name}: {name} twice")
            if ("/" not in name and name.lower().endswith(".toml")) or any(name.lower().startswith(p) for p in PLAYERS):
                sys.exit(f"{work.name}: {name} is not a release file (a development folder was packed?)")
            out[name] = zf.read(info)
    for need in (EXE, "mkwmp-updater.exe", "i18n/en.json"):
        if need not in out:
            sys.exit(f"{work.name} has no {need}: not the launcher's publish folder (tools/release/windows.ps1)")
    if out[EXE][:2] != b"MZ":
        sys.exit(f"{work.name}: {EXE} is not a Windows executable")
    return out


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    version, key, assets = sys.argv[1], pathlib.Path(sys.argv[2]).resolve(), pathlib.Path(sys.argv[3]).resolve()
    files = launcher_files(assets / WORK)
    with tempfile.TemporaryDirectory() as tmp:
        # The inner list: only the game's files (the two DLLs and the mod), signed like SHA256SUMS.
        subprocess.run([sys.executable, str(HERE / "sign_sums.py"), version, str(key)] + [str(assets / d) for d in DLLS],
                       cwd=tmp, check=True, stdout=subprocess.DEVNULL)
        inner = {n: (pathlib.Path(tmp) / n).read_bytes() for n in ["SHA256SUMS", "SHA256SUMS.sig"]}
    out = assets / f"mkwmp-{version}-windows-x64.zip"
    with zipfile.ZipFile(out, "w") as z:
        add(z, EXE, files[EXE], exe=True)
        for name in sorted(files, key=str.lower):
            if name != EXE:
                add(z, name, files[name], exe=name.lower().endswith(".exe"))
        for d in DLLS:
            add(z, f"game/{d}", (assets / d).read_bytes())
        for n, data in inner.items():
            add(z, f"game/{n}", data)
        add(z, "LICENSE", (ROOT / "LICENSE").read_bytes())
        add(z, "NOTICE.md", (ROOT / "NOTICE.md").read_bytes())
        for lic in sorted((HERE / "licenses").iterdir()):
            if f"licenses/{lic.name}" in files:
                sys.exit(f"licenses/{lic.name} is both the launcher's and tools/release's")
            add(z, f"licenses/{lic.name}", lic.read_bytes())
        add(z, "README.txt", (HERE / "README-windows.txt").read_text().replace("{version}", version).replace("\n", "\r\n").encode())
    print(out.name)


if __name__ == "__main__":
    main()
