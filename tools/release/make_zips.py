"""Assemble the per-platform release zips (run by release.sh publish).

    python tools/release/make_zips.py <version> <release key file> <assets dir>

<assets dir> holds the release's loose assets (launchers, winmm.dll,
mkwmp-voice.dll). Writes into it:

  mkwmp-<version>-windows-x64.zip and mkwmp-<version>-linux-x64.zip, each:
    mkwmp/mkwmp-launcher(.exe)    the launcher (updates itself)
    mkwmp/game/winmm.dll          the game patch; the launcher's INSTALL copies it
    mkwmp/game/mkwmp-voice.dll    voice chat, next to it in the game folder
    mkwmp/game/SHA256SUMS(.sig)   signed hashes of the two DLLs: the launcher
                                  checks the game folder against them offline
    mkwmp/LICENSE, NOTICE.md, licenses/, README.txt

The launcher keeps client.toml and logs/ next to itself, in mkwmp/.
"""

import pathlib
import subprocess
import sys
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
HERE = pathlib.Path(__file__).resolve().parent
DLLS = ["winmm.dll", "mkwmp-voice.dll"]
PLATFORMS = {
    "windows": ("mkwmp-launcher-x86_64-windows.exe", "mkwmp-launcher.exe", "README-windows.txt"),
    "linux": ("mkwmp-launcher-x86_64-linux", "mkwmp-launcher", "README-linux.txt"),
}


def add(z, arc, data, exe=False):
    info = zipfile.ZipInfo(f"mkwmp/{arc}", date_time=(2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # unix, so the mode below is honoured
    info.external_attr = ((0o755 if exe else 0o644) | 0o100000) << 16
    z.writestr(info, data)


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    version, key, assets = sys.argv[1], pathlib.Path(sys.argv[2]).resolve(), pathlib.Path(sys.argv[3]).resolve()
    with tempfile.TemporaryDirectory() as tmp:
        # The inner list: only the two DLLs, signed like SHA256SUMS.
        subprocess.run([sys.executable, str(HERE / "sign_sums.py"), version, str(key)] + [str(assets / d) for d in DLLS],
                       cwd=tmp, check=True, stdout=subprocess.DEVNULL)
        inner = {n: (pathlib.Path(tmp) / n).read_bytes() for n in ["SHA256SUMS", "SHA256SUMS.sig"]}
    for plat, (asset, exe_name, readme) in PLATFORMS.items():
        out = assets / f"mkwmp-{version}-{plat}-x64.zip"
        with zipfile.ZipFile(out, "w") as z:
            add(z, exe_name, (assets / asset).read_bytes(), exe=True)
            for d in DLLS:
                add(z, f"game/{d}", (assets / d).read_bytes())
            for n, data in inner.items():
                add(z, f"game/{n}", data)
            add(z, "LICENSE", (ROOT / "LICENSE").read_bytes())
            add(z, "NOTICE.md", (ROOT / "NOTICE.md").read_bytes())
            for lic in sorted((HERE / "licenses").iterdir()):
                add(z, f"licenses/{lic.name}", lic.read_bytes())
            add(z, "README.txt", (HERE / readme).read_text().replace("{version}", version).replace("\n", "\r\n" if plat == "windows" else "\n").encode())
        print(out.name)


if __name__ == "__main__":
    main()
