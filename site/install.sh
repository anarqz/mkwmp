#!/bin/bash
# mkwmp installer for SteamOS / Steam Deck (Desktop Mode) and other Linux PCs with Steam.
#
#   curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash
#
# 1. downloads the newest mkwmp release for Linux and checks it against the release's
#    SHA256SUMS, signed with the mkwmp release key (pinned below);
# 2. unpacks it into ~/mkwmp (your client.toml and logs/ are kept);
# 3. finds the game (mkw.exe) and Proton 10.0, writes ~/mkwmp/run-game.sh, and points
#    ~/mkwmp/client.toml at both;
# 4. closes Steam, adds (or updates) the "Mario Kart World (MKWMP)" shortcut with its artwork,
#    backs up shortcuts.vdf first, and starts Steam again.
#
# Options (after `bash -s --` when piped): --game <folder with mkw.exe>, --no-steam (skip
# step 4), --dir <install folder, default ~/mkwmp>.
set -euo pipefail
command -v python3 >/dev/null || { echo "mkwmp: python3 is needed (SteamOS has it)"; exit 1; }
exec python3 -c "$(cat <<'PY'
import hashlib, io, json, os, pathlib, re, shutil, signal, struct, subprocess, sys, time, urllib.request, zipfile, zlib

REPO = "anarqz/mkwmp"
# The release key (rust/client/src/update.rs RELEASE_KEYS).
RELEASE_KEY = "Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw"
NAME = "Mario Kart World (MKWMP)"
# Steam artwork (SteamGridDB), by grid file suffix: "p" portrait capsule, "" wide capsule,
# "_hero" banner, "_logo" logo, "_icon" icon. Empty: no artwork.
ART = {}
HOME = pathlib.Path.home()


def say(msg):
    print(f"mkwmp: {msg}", flush=True)


def die(msg):
    say(msg)
    sys.exit(1)


def ask(prompt):
    try:
        with open("/dev/tty") as tty:
            print(prompt, end="", flush=True)
            return tty.readline().strip()
    except OSError:
        return ""


def get(url, limit=200 << 20):
    req = urllib.request.Request(url, headers={"User-Agent": "mkwmp-installer", "Accept": "*/*"})
    with urllib.request.urlopen(req, timeout=60) as r:
        data = r.read(limit + 1)
    if len(data) > limit:
        die(f"{url}: too large")
    return data


# ---- Ed25519 verification (RFC 8032), pure Python: SteamOS has no `cryptography` ----
P = 2**255 - 19
Q = 2**252 + 27742317777372353535851937790883648493
D = -121665 * pow(121666, P - 2, P) % P
I = pow(2, (P - 1) // 4, P)


def _add(a, b):
    (x1, y1, z1, t1), (x2, y2, z2, t2) = a, b
    A = (y1 - x1) * (y2 - x2) % P
    B = (y1 + x1) * (y2 + x2) % P
    C = 2 * t1 * t2 * D % P
    Dd = 2 * z1 * z2 % P
    E, F, G, H = B - A, Dd - C, Dd + C, B + A
    return (E * F % P, G * H % P, F * G % P, E * H % P)


def _mul(s, pt):
    q = (0, 1, 1, 0)
    while s:
        if s & 1:
            q = _add(q, pt)
        pt = _add(pt, pt)
        s >>= 1
    return q


def _eq(a, b):
    return (a[0] * b[2] - b[0] * a[2]) % P == 0 and (a[1] * b[2] - b[1] * a[2]) % P == 0


def _decode(b):
    if len(b) != 32:
        return None
    y = int.from_bytes(b, "little")
    sign = y >> 255
    y &= (1 << 255) - 1
    if y >= P:
        return None
    x2 = (y * y - 1) * pow(D * y * y + 1, P - 2, P) % P
    x = pow(x2, (P + 3) // 8, P)
    if (x * x - x2) % P:
        x = x * I % P
    if (x * x - x2) % P:
        return None
    if x == 0 and sign:
        return None
    if x & 1 != sign:
        x = P - x
    return (x, y, 1, x * y % P)


GY = 4 * pow(5, P - 2, P) % P
G = _decode(GY.to_bytes(32, "little"))


def ed25519_ok(pub, msg, sig):
    A, R = _decode(pub), _decode(sig[:32])
    if A is None or R is None or len(sig) != 64:
        return False
    s = int.from_bytes(sig[32:], "little")
    if s >= Q:
        return False
    k = int.from_bytes(hashlib.sha512(sig[:32] + pub + msg).digest(), "little") % Q
    return _eq(_mul(s, G), _add(R, _mul(k, A)))


def b64url(s):
    import base64
    s = s.strip()
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


# ---- Steam's binary VDF (shortcuts.vdf) ----
def vdf_load(b):
    def read_map(i):
        out = {}
        while True:
            t = b[i]
            i += 1
            if t == 8:
                return out, i
            end = b.index(0, i)
            key = b[i:end].decode("utf-8", "replace")
            i = end + 1
            if t == 0:
                out[key], i = read_map(i)
            elif t == 1:
                end = b.index(0, i)
                out[key] = b[i:end].decode("utf-8", "replace")
                i = end + 1
            elif t == 2:
                out[key] = struct.unpack("<i", b[i:i + 4])[0]
                i += 4
            else:
                raise ValueError(f"vdf type {t}")
    m, _ = read_map(0)
    return m


def vdf_dump(m):
    out = bytearray()
    for k, v in m.items():
        if isinstance(v, dict):
            out += b"\x00" + k.encode() + b"\x00" + vdf_dump(v)
        elif isinstance(v, int):
            out += b"\x02" + k.encode() + b"\x00" + struct.pack("<i", v)
        else:
            out += b"\x01" + k.encode() + b"\x00" + str(v).encode() + b"\x00"
    return bytes(out) + b"\x08"


def shortcut_id(exe, name):
    return zlib.crc32((exe + name).encode()) | 0x80000000


# ---- steps ----
def args():
    a = {"game": None, "steam": True, "dir": HOME / "mkwmp"}
    it = iter(sys.argv[1:])
    for x in it:
        if x == "--game":
            a["game"] = pathlib.Path(next(it)).expanduser()
        elif x == "--no-steam":
            a["steam"] = False
        elif x == "--dir":
            a["dir"] = pathlib.Path(next(it)).expanduser()
        else:
            die(f"unknown option {x}")
    return a


def download():
    rel = json.loads(get(f"https://api.github.com/repos/{REPO}/releases/latest", 4 << 20))
    tag = rel["tag_name"]
    version = tag.lstrip("v")
    urls = {x["name"]: x["browser_download_url"] for x in rel["assets"]}
    zname = f"mkwmp-{version}-linux-x64.zip"
    for n in (zname, "SHA256SUMS", "SHA256SUMS.sig"):
        if n not in urls:
            die(f"release {tag} has no {n}")
    sums = get(urls["SHA256SUMS"], 1 << 20)
    sig = b64url(get(urls["SHA256SUMS.sig"], 1 << 10).decode())
    if not ed25519_ok(b64url(RELEASE_KEY), sums, sig):
        die("SHA256SUMS is not signed by the mkwmp release key: not installing")
    lines = sums.decode().splitlines()
    if lines[0].strip() != f"# mkwmp {version}":
        die(f"SHA256SUMS is for {lines[0]!r}, not {version}")
    want = dict(reversed(l.split("  ", 1)) for l in lines[1:] if "  " in l)
    say(f"downloading {zname}")
    z = get(urls[zname])
    if hashlib.sha256(z).hexdigest() != want.get(zname):
        die(f"{zname}: checksum mismatch")
    return version, z


def unpack(z, dest):
    dest.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(z)) as zf:
        for info in zf.infolist():
            name = info.filename
            if not name.startswith("mkwmp/") or ".." in pathlib.PurePosixPath(name).parts or info.is_dir():
                continue
            rel = name[len("mkwmp/"):]
            out = dest / rel
            out.parent.mkdir(parents=True, exist_ok=True)
            tmp = out.with_name(out.name + ".new")
            tmp.write_bytes(zf.read(info))
            tmp.chmod(0o755 if (info.external_attr >> 16) & 0o111 else 0o644)
            os.replace(tmp, out)  # a running launcher keeps its old inode


def steam_root():
    for p in (HOME / ".local/share/Steam", HOME / ".steam/steam"):
        if (p / "steamapps").is_dir():
            return p.resolve()
    return None


def libraries(root):
    libs = [root] if root else []
    if root:
        vdf = root / "steamapps/libraryfolders.vdf"
        if vdf.exists():
            libs += [pathlib.Path(m) for m in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(errors="replace"))]
    return libs


def find_proton(root):
    for lib in libraries(root):
        p = lib / "steamapps/common/Proton 10.0"
        if (p / "proton").exists():
            return p
    return None


def find_games():
    seen = []
    roots = [HOME] + sorted(pathlib.Path("/run/media").glob("*/*")) + sorted(pathlib.Path("/run/media").glob("*"))
    for r in roots:
        if not r.is_dir():
            continue
        for dirpath, dirs, files in os.walk(r):
            depth = len(pathlib.Path(dirpath).relative_to(r).parts)
            dirs[:] = [d for d in dirs if not d.startswith(".") and d not in ("compatdata", "shadercache", "proc")] if depth < 5 else []
            if "mkw.exe" in files:
                p = pathlib.Path(dirpath).resolve()
                if p not in seen:
                    seen.append(p)
    return seen


def toml_str(s):
    return '"' + str(s).replace("\\", "\\\\").replace('"', '\\"') + '"'


def set_client(dest, game, run):
    path = dest / "client.toml"
    want = {"game": toml_str(game) if game else None, "launch": "true", "launch_command": toml_str(run)}
    if not path.exists():
        body = [f"server = {toml_str('https://mkw.anarqz.dev')}", 'name = ""', "game = " + (want["game"] or '""'),
                "launch = true", f"launch_command = {want['launch_command']}", "", "[background]", "close_to_tray = false", ""]
        path.write_text("\n".join(body))
        return
    lines = path.read_text().splitlines()
    top = next((i for i, l in enumerate(lines) if l.strip().startswith("[")), len(lines))
    for key, val in want.items():
        if val is None:
            continue
        idx = next((i for i in range(top) if re.match(rf"\s*{key}\s*=", lines[i])), None)
        if idx is None:
            lines.insert(top, f"{key} = {val}")
            top += 1
        else:
            lines[idx] = f"{key} = {val}"
    shutil.copy2(path, path.with_name("client.toml.bak"))
    path.write_text("\n".join(lines) + "\n")


def write_runner(dest, game, proton, root):
    run = dest / "run-game.sh"
    if run.exists():
        say(f"keeping your {run} (delete it and run this again to have it rewritten)")
        return run
    prefix = dest / "proton-prefix"
    run.write_text(f"""#!/bin/sh
# Starts the game through Proton 10.0 (written by the mkwmp installer; delete it and run the
# installer again to have it rewritten). Saves live in the game folder (profile/), not in the prefix.
export STEAM_COMPAT_CLIENT_INSTALL_PATH={toml_str(root)}
export STEAM_COMPAT_DATA_PATH={toml_str(prefix)}
# The same as the site's Steam launch options: load the patch, native DNS, and the
# recomp's mesh/focus switches.
export WINEDLLOVERRIDES="winmm=n,b" GRPC_DNS_RESOLVER=native MKW_MESH_COMPUTE=1 MKW_ASSUME_FOCUS=1
mkdir -p "$STEAM_COMPAT_DATA_PATH" {toml_str(game / 'logs')}
cd {toml_str(game)} || exit 1
# waitforexitandrun: returns only when the game exits (the launcher then frees the seat).
exec {toml_str(proton / 'proton')} waitforexitandrun {toml_str(game / 'mkw.exe')} --play "$@" >> {toml_str(game / 'logs/proton-launcher.log')} 2>&1
""")
    run.chmod(0o755)
    return run


def steam_running():
    return subprocess.run(["pgrep", "-x", "steam"], capture_output=True).returncode == 0


def game_mode():
    return subprocess.run(["pgrep", "-f", "gamescope-session"], capture_output=True).returncode == 0 or \
        os.environ.get("XDG_CURRENT_DESKTOP", "").lower() == "gamescope"


def close_steam():
    """Steam rewrites shortcuts.vdf when it exits, so it must be closed first."""
    if not steam_running():
        return False
    if game_mode():
        die("Steam is in Game Mode: switch to Desktop Mode (Steam menu, Power, Switch to Desktop) and run this again")
    if ask("Steam is running. Close it now? It opens again at the end. [Y/n] ").lower().startswith("n"):
        die("close Steam yourself, then run this again")
    say("closing Steam")
    subprocess.run(["steam", "-shutdown"], capture_output=True, timeout=30)
    for _ in range(60):
        if not steam_running():
            time.sleep(2)  # let it finish writing its files
            return True
        time.sleep(1)
    die("Steam did not close; close it yourself (Steam menu, Exit) and run this again")


def compat_unmap(root, appid):
    """A shortcut forced through Proton has an entry in config.vdf CompatToolMapping; the
    launcher is a Linux program, so ours must not. Only that block is searched (the same
    appid also appears elsewhere in config.vdf)."""
    f = root / "config/config.vdf"
    if not f.exists():
        return
    text = f.read_text(errors="surrogateescape")
    m = re.search(r'"CompatToolMapping"\s*\{', text)
    if not m:
        return
    # The block's extent, by brace depth.
    depth, i = 1, m.end()
    while i < len(text) and depth:
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        i += 1
    block = text[m.end():i - 1]
    entry = re.search(r'\n?[ \t]*"' + str(appid) + r'"\s*\{[^{}]*\}', block)
    if not entry:
        return
    shutil.copy2(f, f.with_name(f"config.vdf.bak-mkwmp-{time.strftime('%Y%m%d-%H%M%S')}"))
    block = block[:entry.start()] + block[entry.end():]
    tmp = f.with_name("config.vdf.new")
    tmp.write_text(text[:m.end()] + block + text[i - 1:], errors="surrogateescape")
    os.replace(tmp, f)
    say("removed the Proton setting from the shortcut (the launcher runs natively)")


def add_shortcut(root, dest):
    exe = toml_str(dest / "mkwmp-launcher")
    users = [u for u in (root / "userdata").glob("*") if u.name.isdigit() and u.name != "0" and (u / "config").is_dir()]
    if not users:
        say("no Steam user found: add ~/mkwmp/mkwmp-launcher to Steam yourself (Add a Non-Steam Game)")
        return
    art = {}
    for suffix, url in ART.items():
        try:
            art[suffix] = (get(url, 20 << 20), pathlib.PurePosixPath(url).suffix or ".png")
        except Exception as e:  # artwork is a nicety: never fatal
            say(f"artwork {suffix or 'wide'}: {e}")
    stamp = time.strftime("%Y%m%d-%H%M%S")
    for u in users:
        f = u / "config/shortcuts.vdf"
        data = vdf_load(f.read_bytes()) if f.exists() else {"shortcuts": {}}
        if f.exists():
            shutil.copy2(f, f.with_name(f"shortcuts.vdf.bak-mkwmp-{stamp}"))
        sc = data.setdefault("shortcuts", {})
        # Ours: same launcher path, or the same name. An existing one keeps its appid (its
        # artwork and controller settings are filed under it).
        def field(v, name):  # older Steam writes some keys in lower case
            return next((x for k, x in v.items() if k.lower() == name.lower()), None)
        key = next((k for k, v in sc.items() if isinstance(v, dict) and (field(v, "Exe") == exe or field(v, "AppName") == NAME)), None)
        old = sc.get(key, {}) if key else {}
        entry = {"appid": field(old, "appid"), "LastPlayTime": field(old, "LastPlayTime") or 0, "icon": field(old, "icon") or "",
                 "tags": field(old, "tags") or {}}
        if not isinstance(entry["appid"], int):
            del entry["appid"]
        app = struct.unpack("<I", struct.pack("<i", entry["appid"]))[0] if isinstance(entry.get("appid"), int) else shortcut_id(exe, NAME)
        grid = u / "config/grid"
        grid.mkdir(parents=True, exist_ok=True)
        icon = ""
        for suffix, (bytes_, ext) in art.items():
            out = grid / f"{app}{suffix}{ext}"
            out.write_bytes(bytes_)
            if suffix == "_icon":
                icon = str(out)
        entry.update({
            "appid": struct.unpack("<i", struct.pack("<I", app))[0],
            "AppName": NAME,
            "Exe": exe,
            "StartDir": toml_str(dest),
            "icon": icon or entry.get("icon", ""),
            "ShortcutPath": "",
            "LaunchOptions": "",
            "IsHidden": 0,
            "AllowDesktopConfig": 1,
            "AllowOverlay": 1,
            "OpenVR": 0,
            "Devkit": 0,
            "DevkitGameID": "",
            "DevkitOverrideAppID": 0,
            "LastPlayTime": entry.get("LastPlayTime", 0),
            "FlatpakAppID": "",
        })
        if key is None:
            key = str(max([int(k) for k in sc if k.isdigit()] + [-1]) + 1)
        sc[key] = entry
        tmp = f.with_name("shortcuts.vdf.new")
        tmp.write_bytes(b"\x00shortcuts\x00" + vdf_dump(sc) + b"\x08")
        os.replace(tmp, f)
        compat_unmap(root, app)
        say(f"Steam shortcut \"{NAME}\" ready for Steam user {u.name}")


def start_steam():
    say("starting Steam")
    subprocess.Popen(["steam"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, stdin=subprocess.DEVNULL, start_new_session=True)


def main():
    a = args()
    dest = a["dir"]
    version, z = download()
    unpack(z, dest)
    say(f"mkwmp {version} installed in {dest}")
    root = steam_root()
    proton = find_proton(root)
    if not proton:
        say("Proton 10.0 not found: install it in Steam (Library, search \"Proton 10.0\"), then run this again")
    game = a["game"]
    if not game:
        found = find_games()
        if len(found) == 1:
            game = found[0]
        elif found:
            for i, g in enumerate(found, 1):
                print(f"  {i}. {g}")
            pick = ask("Which one is your game? [1] ")
            game = found[int(pick) - 1 if pick.isdigit() and 0 < int(pick) <= len(found) else 0]
        else:
            say("mkw.exe not found: set GAME FOLDER in the launcher, or run this again with --game <folder>")
    if game:
        say(f"game: {game}")
    run = write_runner(dest, game or HOME, proton or pathlib.Path("/nonexistent/Proton 10.0"), root or HOME / ".local/share/Steam") if root else None
    set_client(dest, game, run or "")
    if not a["steam"] or not root:
        say(f"done. Open {dest / 'mkwmp-launcher'}")
        return
    was_running = close_steam()
    try:
        add_shortcut(root, dest)
    finally:
        if was_running:
            start_steam()
    say(f"done. In Steam: {NAME}. First run: register a gamertag; the launcher installs the game patch (INSTALL under GAME FOLDER).")


try:
    main()
except KeyboardInterrupt:
    die("stopped")
PY
)" "$@"
