"""Assemble the release's two zips (run by release.sh publish).

    python tools/release/make_zips.py <version> <release key file> <dir>

<dir> holds the draft's working file mkwmp-launcher-files-windows.zip
(tools/release/windows.ps1: the launcher's publish folder, game/ with the
game's three files) and the two servers release.sh draft built, `server`
(Linux, static) and `server.exe` (Windows). Writes into it:

  mkwmp-<version>-windows-x64.zip       the launcher, under mkwmp/:
    mkwmp/mkwmp.exe                     the launcher (launcher/, C# WPF), with what
    mkwmp/*.dll                         the working file has beside it: its native
    mkwmp/updater.exe                   libraries, the update helper, i18n/,
    mkwmp/i18n/, licenses/, ...         licenses/, mkwmp-launcher.png, CHANGELOG.md
    mkwmp/game/winmm.dll                the game patch
    mkwmp/game/mkwmp-voice.dll          voice chat, next to it in the game folder
    mkwmp/game/mkwmp-mod.zip            the MKWMP mod for mkw-recomp
    mkwmp/game/SHA256SUMS(.sig)         signed hashes of the three: the launcher checks
                                        game/ against them offline (and downloads nothing)
    mkwmp/LICENSE, NOTICE.md, licenses/, README.txt

  mkwmp-server-<version>.zip            the server, flat (no top folder):
    server                              Linux x86-64, static (mode 755)
    server.exe                          Windows x86-64
    LICENSE, NOTICE.md, licenses/, README.txt

With SHA256SUMS and SHA256SUMS.sig (sign_sums.py over these two zips) they are
the whole release: nothing loose. The launcher zip is at once the download of a
new player, what install-mkwmp.sh installs under Proton, and the package the
launcher's update helper installs (launcher/docs/UPDATES.md). The launcher
keeps client.toml and logs/ next to itself, in mkwmp/.
"""

import io
import pathlib
import subprocess
import sys
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
HERE = pathlib.Path(__file__).resolve().parent
WORK = "mkwmp-launcher-files-windows.zip"
EXE = "mkwmp.exe"
HELPER = "updater.exe"
# game/ of the launcher zip: exactly these, signed by the inner SHA256SUMS.
GAME = ["winmm.dll", "mkwmp-voice.dll", "mkwmp-mod.zip"]
# Names of before 1.4.0's rename: a publish folder that still has them is stale.
FORMER = ("mkwmp-launcher.exe", "mkwmp-updater.exe")
# Never in a release zip (UPDATES.md section 5: the player's own files), whatever the working file holds.
PLAYERS = ("logs/", ".mkwmp-selfupdate/")
SERVERS = {"server": b"\x7fELF", "server.exe": b"MZ"}
SERVER_README = """mkwmp server {version}: the whole MKWMP server in one program (server on Linux, server.exe on Windows).
Setup and options: https://github.com/anarqz/mkwmp/blob/main/docs/HOSTING.md
On a Linux server, one command sets it up: curl -fsSL https://anarqz.github.io/mkwmp/deploy-mkwmp-server.sh | sudo bash
"""


def add(z, arc, data, exe=False, top="mkwmp/"):
    info = zipfile.ZipInfo(f"{top}{arc}", date_time=(2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # unix, so the mode below is honoured
    info.external_attr = ((0o755 if exe else 0o644) | 0o100000) << 16
    z.writestr(info, data)


def launcher_files(work):
    """(path, bytes) of the working file, checked: plain relative paths, the launcher, its helper and game/ there."""
    out = {}
    with zipfile.ZipFile(io.BytesIO(work.read_bytes())) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            name = info.filename
            parts = pathlib.PurePosixPath(name).parts
            low = name.lower()
            if name.startswith("/") or "\\" in name or ":" in name or any(p in ("", ".", "..") for p in parts):
                sys.exit(f"{work.name}: unsafe path {name!r}")
            if low in (n.lower() for n in out):
                sys.exit(f"{work.name}: {name} twice")
            if ("/" not in name and low.endswith(".toml")) or any(low.startswith(p) for p in PLAYERS):
                sys.exit(f"{work.name}: {name} is not a release file (a development folder was packed?)")
            if low.startswith("game/") and name[5:] not in GAME:
                sys.exit(f"{work.name}: {name} is not one of game/'s files ({', '.join(GAME)}): a development folder?")
            if low in FORMER:
                sys.exit(f"{work.name}: {name} is the launcher's name before 1.4.0 (a stale publish folder?)")
            out[name] = zf.read(info)
    for need in [EXE, HELPER, "i18n/en.json"] + [f"game/{g}" for g in GAME]:
        if need not in out:
            sys.exit(f"{work.name} has no {need}: not the launcher's publish folder (tools/release/windows.ps1)")
    for exe in (EXE, HELPER):
        if out[exe][:2] != b"MZ":
            sys.exit(f"{work.name}: {exe} is not a Windows executable")
    return out


def release_licenses(z, top, taken=()):
    for lic in sorted((HERE / "licenses").iterdir()):
        if f"licenses/{lic.name}" in taken:
            sys.exit(f"licenses/{lic.name} is both the launcher's and tools/release's")
        add(z, f"licenses/{lic.name}", lic.read_bytes(), top=top)


def launcher_zip(version, key, folder):
    files = launcher_files(folder / WORK)
    with tempfile.TemporaryDirectory() as tmp:
        # The inner list: only the game's files, signed like SHA256SUMS.
        for g in GAME:
            (pathlib.Path(tmp) / g).write_bytes(files[f"game/{g}"])
        subprocess.run([sys.executable, str(HERE / "sign_sums.py"), version, str(key)] + GAME,
                       cwd=tmp, check=True, stdout=subprocess.DEVNULL)
        inner = {n: (pathlib.Path(tmp) / n).read_bytes() for n in ["SHA256SUMS", "SHA256SUMS.sig"]}
    out = folder / f"mkwmp-{version}-windows-x64.zip"
    with zipfile.ZipFile(out, "w") as z:
        add(z, EXE, files[EXE], exe=True)
        for name in sorted(files, key=str.lower):
            if name != EXE:
                add(z, name, files[name], exe=name.lower().endswith(".exe"))
        for n, data in inner.items():
            add(z, f"game/{n}", data)
        add(z, "LICENSE", (ROOT / "LICENSE").read_bytes())
        add(z, "NOTICE.md", (ROOT / "NOTICE.md").read_bytes())
        release_licenses(z, "mkwmp/", files)
        add(z, "README.txt", (HERE / "README-windows.txt").read_text(encoding="utf-8").replace("{version}", version).replace("\n", "\r\n").encode())
    return out


def server_zip(version, folder):
    bins = {}
    for name, magic in SERVERS.items():
        path = folder / name
        if not path.is_file():
            sys.exit(f"no {path}: release.sh draft builds the servers (rust/target/<target>/release/{name})")
        data = path.read_bytes()
        if data[: len(magic)] != magic:
            sys.exit(f"{path} is not a {'Linux' if name == 'server' else 'Windows'} executable")
        bins[name] = data
    out = folder / f"mkwmp-server-{version}.zip"
    with zipfile.ZipFile(out, "w") as z:
        for name, data in bins.items():
            add(z, name, data, exe=True, top="")
        add(z, "LICENSE", (ROOT / "LICENSE").read_bytes(), top="")
        add(z, "NOTICE.md", (ROOT / "NOTICE.md").read_bytes(), top="")
        release_licenses(z, "")
        add(z, "README.txt", SERVER_README.format(version=version).encode(), top="")
    return out


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    version, key, folder = sys.argv[1], pathlib.Path(sys.argv[2]).resolve(), pathlib.Path(sys.argv[3]).resolve()
    print(launcher_zip(version, key, folder).name)
    print(server_zip(version, folder).name)


if __name__ == "__main__":
    main()
