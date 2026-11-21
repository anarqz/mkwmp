#!/usr/bin/env bash
# MKWMP installer for SteamOS (Steam Deck, Desktop Mode) and Linux PCs with Steam.
#
#   bash install-mkwmp.sh                      install the newest release and add it to Steam
#   bash install-mkwmp.sh --help               every option
#   curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash
#                                              the same, this script fetched from the repository at the newest
#                                              release's tag (it is not a release asset)
#
# What it does (docs/LINUX.md has the reasons):
#   1. downloads the MKWMP launcher (the Windows build; it runs under Proton) and checks it against the
#      release's SHA256SUMS, signed with the MKWMP release key;
#   2. unpacks it into ~/Games/MKWMP. The launcher is portable: its settings, its logs and the game it
#      installs all live in that folder. A first install takes the settings of the old Linux launcher
#      (MKWMP 1.3 and older, ~/mkwmp): your name, its PIN and your server carry over;
#   3. adds the Steam shortcut "MKWMP" (or updates the one it added before), forces a Proton version on it,
#      and gives it a gamepad layout on the Steam Deck. A shortcut an older MKWMP installer made
#      ("Mario Kart World (MKWMP)", the old Linux launcher) is removed; no other shortcut is touched;
#   4. downloads MKWMP's own library artwork (capsules, hero, logo, icon) from the repository: the links are in
#      artwork.json (tools/linux/art/); SteamGridDB's public search is the fallback when a link is gone.
#
# Steam must be closed while its files are edited. This script never closes Steam: it stops and asks you to.
# Every Steam file it changes is backed up beside the original first (*.bak-mkwmp-<time>).
set -euo pipefail
command -v python3 >/dev/null || { echo "mkwmp: python3 is needed (SteamOS has it; elsewhere: install python3)" >&2; exit 1; }
MKWMP_SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" 2>/dev/null && pwd || true)" exec python3 -I -c "$(cat <<'MKWMP_PY'
import base64, hashlib, io, json, os, pathlib, re, shutil, struct, sys, time, urllib.error, urllib.parse, urllib.request, zipfile, zlib

REPO = "anarqz/mkwmp"
# The release keys (launcher/src/Mkwmp.Update/ReleaseSums.cs ReleaseKeys), kid:base64url.
RELEASE_KEYS = ["release1:Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw"]
NAME = "MKWMP"
EXE = "mkwmp.exe"
# The C# launcher's files under their names before 1.4.0's rename (now mkwmp.exe, updater.exe). No release had them,
# only test builds: an install of one is moved over (its shortcut keeps its place, artwork, Proton and prefix) and the
# two files are removed.
FORMER_EXE = "mkwmp-launcher.exe"
FORMER_FILES = (FORMER_EXE, "mkwmp-updater.exe")
MARKER = ".mkwmp-install"
# What a run under Proton needs besides the compatibility tool: nothing. The launcher sets the per-application
# DLL override itself, and the game is Vulkan (no DXVK / VKD3D in its path). See docs/LINUX.md, "Launch options".
LAUNCH_OPTIONS = ""
# The Steam Deck's layout: a plain gamepad (the right pad as a mouse). Its View button is the game's Select,
# which the overlay opens on (held for a second).
DECK_TEMPLATE = "controller_neptune_gamepad_joystick.vdf"
# The launcher's own files in its folder that an install never replaces (launcher/docs/UPDATES.md, section 5).
KEEP_DIRS = ("logs", ".mkwmp-selfupdate")
HOME = pathlib.Path.home()
DRY = False

HELP = """MKWMP installer for SteamOS and Linux (Steam + Proton).

  bash install-mkwmp.sh [options]

Install
  --dir <folder>          where MKWMP goes (default ~/Games/MKWMP). Everything stays in this folder.
  --version <x.y.z>       that release instead of the newest
  --from-zip <file>       a local mkwmp-<version>-windows-x64.zip (a test build): nothing is downloaded,
                          and nothing is checked against a signature
  --import <client.toml>  the settings to start from (name, PIN, server...), when the install folder has
                          none yet. Default: the old Linux launcher's (MKWMP 1.3 and older: the folder of
                          its Steam shortcut, else ~/mkwmp); that file itself is only read
  --no-import             start from no settings at all

Steam
  --proton <name>         the Proton to run it with: "11", "10", "experimental", "GE-Proton11-1", ...
                          (default: the newest stable Proton installed, else Experimental)
  --launch-options <txt>  Steam launch options of the shortcut (default: none are needed)
  --steam-root <folder>   Steam's folder, when it is not found by itself
  --no-steam              install the files only; touch nothing of Steam

Artwork (MKWMP's own, downloaded from the repository; SteamGridDB as the fallback, no key needed)
  --artwork <file>        another list of links (the form of tools/linux/artwork.json)
  --art-only              only (re)download the artwork of the installed shortcut
  --no-art                no artwork: the shortcut gets MKWMP's own icon

Other
  --uninstall             remove the shortcut, its Proton setting, its artwork and its layout entry
  --purge                 with --uninstall: also delete the install folder and the shortcut's Proton prefix
  --dry-run               say what would be done; write nothing
  --yes                   do not ask before --purge
"""


def say(msg):
    print(f"mkwmp: {msg}", flush=True)


def die(msg, code=1):
    print(f"mkwmp: {msg}", file=sys.stderr, flush=True)
    sys.exit(code)


def ask(prompt):
    try:
        with open("/dev/tty") as tty:
            print(prompt, end="", flush=True)
            return tty.readline().strip()
    except OSError:
        return ""


def get(url, limit=400 << 20, headers=None, progress=None):
    req = urllib.request.Request(url, headers={"User-Agent": "mkwmp-installer", "Accept": "*/*", **(headers or {})})
    with urllib.request.urlopen(req, timeout=60) as r:
        total = int(r.headers.get("Content-Length") or 0)
        out, last = bytearray(), 0.0
        while True:
            chunk = r.read(1 << 16)
            if not chunk:
                break
            out += chunk
            if len(out) > limit:
                die(f"{url}: too large")
            if progress and total and time.time() - last > 2:
                last = time.time()
                say(f"  {progress}: {len(out) / 1e6:.0f} of {total / 1e6:.0f} MB")
    return bytes(out)


# ---- files: every write goes through here (atomic, and nothing at all with --dry-run) ----
def write_atomic(path, data, mode=None):
    if DRY:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".mkwmp-new")
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    if mode is None and path.exists():
        mode = path.stat().st_mode & 0o777
    if mode is not None:
        os.chmod(tmp, mode)
    os.replace(tmp, path)


def backup(path, stamp):
    if DRY or not path.exists():
        return
    copy, n = path.with_name(f"{path.name}.bak-mkwmp-{stamp}"), 1
    while copy.exists():  # never over an earlier copy
        n += 1
        copy = path.with_name(f"{path.name}.bak-mkwmp-{stamp}-{n}")
    shutil.copy2(path, copy)


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
    if len(sig) != 64:
        return False
    A, R = _decode(pub), _decode(sig[:32])
    if A is None or R is None:
        return False
    s = int.from_bytes(sig[32:], "little")
    if s >= Q:
        return False
    k = int.from_bytes(hashlib.sha512(sig[:32] + pub + msg).digest(), "little") % Q
    return _eq(_mul(s, G), _add(R, _mul(k, A)))


def b64url(s):
    s = s.strip()
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def verified_sums(sums, sig_text, version):
    """SHA256SUMS as {name: hex}, only if a release key signed these bytes for this version."""
    try:
        sig = b64url(sig_text)
    except ValueError:
        die("SHA256SUMS.sig is malformed: not installing")
    if not any(ed25519_ok(b64url(k.split(":", 1)[1]), sums, sig) for k in RELEASE_KEYS):
        die("SHA256SUMS is not signed by the MKWMP release key: not installing")
    lines = sums.decode("utf-8").splitlines()
    if not lines or lines[0].strip() != f"# mkwmp {version}":
        die(f"SHA256SUMS is for {lines[:1]!r}, not {version}: not installing")
    out = {}
    for line in lines[1:]:
        if "  " in line:
            h, n = line.split("  ", 1)
            out[n.strip()] = h.lower()
    return out


# ---- Steam's binary VDF (shortcuts.vdf). Kept as ordered lists so that an untouched file is written
#      back byte for byte: [type, key bytes, value]; value: list (map), bytes (string), int (uint32) ----
def bvdf_load(b):
    def read_map(i):
        out = []
        while True:
            t = b[i]
            i += 1
            if t == 8:
                return out, i
            end = b.index(0, i)
            key = b[i:end]
            i = end + 1
            if t == 0:
                v, i = read_map(i)
            elif t == 1:
                end = b.index(0, i)
                v = b[i:end]
                i = end + 1
            elif t == 2:
                v = struct.unpack_from("<I", b, i)[0]
                i += 4
            else:
                raise ValueError(f"binary vdf: type {t} at {i - 1}")
            out.append([t, key, v])
    root, end = read_map(0)
    if end != len(b):
        raise ValueError("binary vdf: bytes after the end")
    return root


def bvdf_dump(m):
    out = bytearray()
    for t, k, v in m:
        out += bytes([t]) + k + b"\x00"
        if t == 0:
            out += bvdf_dump(v)
        elif t == 1:
            out += v + b"\x00"
        else:
            out += struct.pack("<I", v)
    return bytes(out) + b"\x08"


def bget(m, key):
    """The item [type, key, value] whose key is `key`, whatever its case (Steam changed the case of some)."""
    k = key.lower().encode()
    return next((it for it in m if it[1].lower() == k), None)


def bset(m, key, t, value):
    it = bget(m, key)
    if it is None:
        m.append([t, key.encode(), value])
    else:
        it[0], it[2] = t, value


def shortcut_id(exe, name):
    """The id Steam itself gives a shortcut with this (quoted) exe and name: stable for an install folder."""
    return zlib.crc32((exe + name).encode()) | 0x80000000


SHORTCUT_FIELDS = [  # a new entry, in Steam's order
    ("appid", 2, 0), ("appname", 1, b""), ("exe", 1, b""), ("StartDir", 1, b""), ("icon", 1, b""), ("ShortcutPath", 1, b""),
    ("LaunchOptions", 1, b""), ("IsHidden", 2, 0), ("AllowDesktopConfig", 2, 1), ("AllowOverlay", 2, 1), ("OpenVR", 2, 0),
    ("Devkit", 2, 0), ("DevkitGameID", 1, b""), ("DevkitOverrideAppID", 2, 0), ("LastPlayTime", 2, 0), ("FlatpakAppID", 1, b""),
    ("sortas", 1, b""), ("tags", 0, None)]


LEGACY_NAMES = ("mario kart world (mkwmp)", "mario kart world (debug)")
LEGACY_EXE = "mkwmp-launcher"  # the Linux launcher of MKWMP 1.3 and older (no .exe): ~/mkwmp, ~/mkw-180-launcher, ...


def is_legacy(entry):
    """A shortcut an older MKWMP installer made: by its name, or by an exe that is the old Linux launcher.
    Nothing else matches: another program's shortcut is never touched."""
    n, e = bget(entry, "appname"), bget(entry, "exe")
    name = n[2].decode("utf-8", "replace").strip().lower() if n is not None and n[0] == 1 else ""
    exe = e[2].decode("utf-8", "replace").strip() if e is not None and e[0] == 1 else ""
    m = re.match(r'"([^"]*)"', exe)  # the program is the first quoted part; what follows are its arguments
    program = m.group(1) if m else exe.split(" ")[0]
    return name in LEGACY_NAMES or program.rsplit("/", 1)[-1] == LEGACY_EXE


def shortcuts_edit(raw, appid, exe, startdir, name, launch_options, icon, remove=False, legacy=None, former=()):
    """The new bytes of shortcuts.vdf and what happened ('added', 'updated', 'unchanged', 'removed', 'absent').
    Every other entry keeps its bytes. With a list as `legacy` (an install), the shortcuts of older MKWMP
    installers are removed too and described there as (id, name, exe). `former`: (id, exe) pairs of this
    install's own shortcut under an earlier exe name: that entry is this shortcut, updated to the new id and exe."""
    if raw:
        root = bvdf_load(raw)
        if bvdf_dump(root)[:-1] != raw and bvdf_dump(root) != raw:
            raise ValueError("shortcuts.vdf does not read back the same: left alone")
        trailing = raw.endswith(b"\x08\x08") and bvdf_dump(root)[:-1] == raw
    else:
        root, trailing = [[0, b"shortcuts", []]], True
    holder = bget(root, "shortcuts")
    if holder is None or holder[0] != 0:
        raise ValueError("shortcuts.vdf has no shortcuts list: left alone")
    items = holder[2]

    def is_ours(entry):
        a, e = bget(entry, "appid"), bget(entry, "exe")
        return any((a is not None and a[2] == i) or (e is not None and e[2] == x.encode()) for i, x in [(appid, exe), *former])
    mine = [it for it in items if it[0] == 0 and is_ours(it[2])]
    if legacy is not None and not remove:
        for it in [it for it in items if it[0] == 0 and it not in mine and is_legacy(it[2])]:
            a, n, e = bget(it[2], "appid"), bget(it[2], "appname"), bget(it[2], "exe")
            legacy.append((a[2] if a is not None and a[0] == 2 else None, n[2].decode("utf-8", "replace") if n else "",
                           e[2].decode("utf-8", "replace") if e else ""))
            items.remove(it)
    if remove:
        if not mine:
            return raw, "absent"
        for it in mine:
            items.remove(it)
        what = "removed"
    else:
        want = {"appid": (2, appid), "appname": (1, name.encode()), "exe": (1, exe.encode()), "StartDir": (1, startdir.encode()),
                "LaunchOptions": (1, launch_options.encode())}
        if icon:
            want["icon"] = (1, icon.encode())
        if mine:
            entry, what = mine[0][2], "updated"
            before = bvdf_dump(entry)
        else:
            # key spelling as the file's other entries have it (older Steam: AppName, Exe)
            sample = next((it[2] for it in items if it[0] == 0), [])
            entry = []
            for key, t, default in SHORTCUT_FIELDS:
                seen = bget(sample, key)
                entry.append([t, seen[1] if seen is not None else key.encode(), [] if t == 0 else default])
            index = str(max([int(it[1]) for it in items if it[1].isdigit()] + [-1]) + 1).encode()
            items.append([0, index, entry])
            what, before = "added", None
        for key, (t, v) in want.items():
            bset(entry, key, t, v)
        if before is not None and bvdf_dump(entry) == before:
            if not legacy:
                return raw, "unchanged"
            what = "unchanged"
    out = bvdf_dump(root)
    return (out[:-1] if trailing else out), what


# ---- Steam's text VDF (config.vdf, configset_*.vdf): edited in place, nothing else is rewritten ----
def _skip(t, i):
    n = len(t)
    while i < n:
        if t[i] in " \t\r\n":
            i += 1
        elif t.startswith("//", i):
            j = t.find("\n", i)
            i = n if j < 0 else j
        else:
            break
    return i


def _string(t, i):
    if t[i] != '"':
        raise ValueError(f"text vdf: unexpected {t[i]!r} at {i}")
    j = i + 1
    while t[j] != '"':
        j += 2 if t[j] == "\\" else 1
    return t[i + 1:j], j + 1


def _block_end(t, i):
    """t[i] is '{': the index of its '}'."""
    i += 1
    while True:
        i = _skip(t, i)
        if t[i] == "}":
            return i
        if t[i] == "{":
            i = _block_end(t, i) + 1
        else:
            _, i = _string(t, i)


def tvdf_entries(t, open_i):
    """(key, key start, value start, value end, is block) for each entry of the block opened at open_i."""
    out, i = [], open_i + 1
    while True:
        i = _skip(t, i)
        if t[i] == "}":
            return out
        ks = i
        key, i = _string(t, i)
        i = _skip(t, i)
        if t[i] == "{":
            e = _block_end(t, i) + 1
            out.append((key, ks, i, e, True))
        else:
            _, e = _string(t, i)
            out.append((key, ks, i, e, False))
        i = e


def tvdf_find(t, path):
    """The '{' of the block at `path` (keys, any case), or None."""
    i = _skip(t, 0)
    key, i = _string(t, i)
    i = _skip(t, i)
    if key.lower() != path[0].lower() or t[i] != "{":
        return None
    for name in path[1:]:
        hit = next((e for e in tvdf_entries(t, i) if e[4] and e[0].lower() == name.lower()), None)
        if hit is None:
            return None
        i = hit[2]
    return i


def _indent(t, i):
    """The white space before position i on its line."""
    s = t.rfind("\n", 0, i) + 1
    return t[s:i] if t[s:i].strip() == "" else ""


def tvdf_set_block(t, open_i, key, pairs):
    """`key { pairs }` inside the block at open_i: replaced when there, else added as its last entry. None = remove."""
    close = _block_end(t, open_i)
    inner = _indent(t, open_i) + "\t"
    old = next((e for e in tvdf_entries(t, open_i) if e[0] == key), None)
    body = ""
    if pairs is not None:
        body = f'{inner}"{key}"\n{inner}{{\n' + "".join(f'{inner}\t"{k}"\t\t"{v}"\n' for k, v in pairs) + f"{inner}}}\n"
    if old is not None:
        start = t.rfind("\n", 0, old[1]) + 1
        end = old[3]
        end = t.find("\n", end) + 1 if t.find("\n", end) >= 0 else end
        return t[:start] + body + t[end:]
    if pairs is None:
        return t
    start = t.rfind("\n", 0, close) + 1
    return t[:start] + body + t[start:]


def compat_edit(text, appid, tool):
    """config.vdf with CompatToolMapping[appid] = tool (None: removed). Raises ValueError on a file it cannot read."""
    path = ["InstallConfigStore", "Software", "Valve", "Steam"]
    steam = tvdf_find(text, path)
    if steam is None:
        raise ValueError("config.vdf has no Software/Valve/Steam section")
    mapping = tvdf_find(text, path + ["CompatToolMapping"])
    if mapping is None:
        if tool is None:
            return text
        close = _block_end(text, steam)
        inner = _indent(text, steam) + "\t"
        start = text.rfind("\n", 0, close) + 1
        text = text[:start] + f'{inner}"CompatToolMapping"\n{inner}{{\n{inner}}}\n' + text[start:]
        mapping = tvdf_find(text, path + ["CompatToolMapping"])
    pairs = None if tool is None else [("name", tool), ("config", ""), ("priority", "250")]
    return tvdf_set_block(text, mapping, str(appid), pairs)


def configset_edit(text, name, template):
    top = tvdf_find(text, ["controller_config"])
    if top is None:
        raise ValueError("no controller_config section")
    return tvdf_set_block(text, top, name.lower(), None if template is None else [("template", template)])


def read_text(path):
    return path.read_bytes().decode("utf-8", "surrogateescape")


# ---- Steam on this machine ----
def steam_roots():
    c = [HOME / ".local/share/Steam", HOME / ".steam/steam", HOME / ".steam/root", HOME / ".steam/debian-installation",
         HOME / ".var/app/com.valvesoftware.Steam/.local/share/Steam", HOME / ".var/app/com.valvesoftware.Steam/data/Steam",
         HOME / "snap/steam/common/.local/share/Steam"]
    seen = []
    for p in c:
        try:
            r = p.resolve()
        except OSError:
            continue
        if (r / "userdata").is_dir() and r not in seen:
            seen.append(r)
    return seen


def is_flatpak(root):
    return "/.var/app/com.valvesoftware.Steam/" in str(root) + "/"


def steam_running():
    """A process named steam (the client; also inside Flatpak's sandbox, whose processes /proc shows)."""
    try:
        pids = [p for p in os.listdir("/proc") if p.isdigit()]
    except OSError:
        return False
    for pid in pids:
        try:
            comm = open(f"/proc/{pid}/comm").read().strip()
        except OSError:
            continue
        if comm in ("steam", "steamwebhelper"):
            return True
    return False


def launcher_running(dest):
    targets = [str(dest / n) for n in (EXE, FORMER_EXE)]
    try:
        pids = [p for p in os.listdir("/proc") if p.isdigit()]
    except OSError:
        return False
    for pid in pids:
        try:
            cmd = open(f"/proc/{pid}/cmdline", "rb").read().decode("utf-8", "replace").replace("\\", "/")
        except OSError:
            continue
        if any(t in cmd or ("Z:" + t) in cmd for t in targets):
            return True
    return False


def libraries(root):
    libs = [root]
    vdf = root / "steamapps/libraryfolders.vdf"
    if vdf.exists():
        libs += [pathlib.Path(m) for m in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(errors="replace"))]
    out = []
    for lib in libs:
        if (lib / "steamapps").is_dir() and lib not in out:
            out.append(lib)
    return out


def protons(root):
    """Installed Proton builds: (rank, internal name, shown name, folder), best first.
    Valve's are named proton_<major>, proton_experimental, proton_hotfix in CompatToolMapping; a custom
    build (GE) by the name its compatibilitytool.vdf declares."""
    found = []
    for lib in libraries(root):
        for p in sorted((lib / "steamapps/common").glob("Proton*")):
            if not (p / "proton").is_file():
                continue
            m = re.match(r"Proton (\d+)\.\d+", p.name)
            if m:
                major = int(m.group(1))
                # a "(Beta)" is behind every released one
                found.append(((1 if "Beta" in p.name else 3, major), f"proton_{major}", p.name, p))
            elif "Experimental" in p.name:
                found.append(((2, 0), "proton_experimental", p.name, p))
            elif "Hotfix" in p.name:
                found.append(((0, 1), "proton_hotfix", p.name, p))
    for d in (root / "compatibilitytools.d", pathlib.Path("/usr/share/steam/compatibilitytools.d")):
        for p in sorted(d.glob("*")) if d.is_dir() else []:
            if not (p / "proton").is_file():
                continue
            name = p.name
            try:
                t = read_text(p / "compatibilitytool.vdf")
                tools = tvdf_find(t, ["compatibilitytools", "compat_tools"])
                name = tvdf_entries(t, tools)[0][0]
            except (OSError, ValueError, IndexError, TypeError):
                pass
            found.append(((0, 0), name, p.name, p))
    found.sort(key=lambda f: f[0], reverse=True)
    return found


def pick_proton(root, wanted):
    have = protons(root)
    if wanted:
        w = wanted.lower().strip()
        for _, internal, shown, path in have:
            if w in (internal.lower(), shown.lower()) or internal.lower() == "proton_" + w or (w.isdigit() and shown.lower().startswith(f"proton {w}.")):
                return internal, shown
        hit = [(i, s) for _, i, s, _ in have if w in s.lower() or w in i.lower()]
        if len(hit) == 1:
            return hit[0]
        die(f"--proton {wanted}: not installed. Installed: " + (", ".join(s for _, _, s, _ in have) or "none"))
    if have:
        return have[0][1], have[0][2]
    return None, None


def steam_users(root):
    return sorted(u for u in (root / "userdata").glob("*") if u.name.isdigit() and u.name != "0" and (u / "config").is_dir())


# ---- the release ----
# The first release whose Windows zip holds this launcher (C#, launcher/). The zips before it hold the Rust
# launcher of MKWMP 1.3, which is not made to run under Proton.
FIRST = (1, 4, 0)


def release_number(v):
    m = re.match(r"(\d+)\.(\d+)\.(\d+)", v)
    return tuple(int(x) for x in m.groups()) if m else (0, 0, 0)


def download(version):
    api = os.environ.get("MKWMP_UPDATE_API", "https://api.github.com").rstrip("/")
    if version and release_number(version) < FIRST:
        die(f"--version {version}: MKWMP before {'.'.join(map(str, FIRST))} has no launcher for Proton")
    try:
        if version:
            rel = json.loads(get(f"{api}/repos/{REPO}/releases/tags/v{version}", 4 << 20))
        else:
            rels = json.loads(get(f"{api}/repos/{REPO}/releases?per_page=20", 8 << 20))
            rels = [r for r in rels if not r.get("draft") and not r.get("prerelease") and release_number(r["tag_name"].lstrip("v")) >= FIRST
                    and any(a["name"] == f"mkwmp-{r['tag_name'].lstrip('v')}-windows-x64.zip" for a in r.get("assets", []))]
            if not rels:
                die(f"no MKWMP release with the launcher for Proton ({'.'.join(map(str, FIRST))} or newer) was found yet")
            rel = max(rels, key=lambda r: release_number(r["tag_name"].lstrip("v")))
    except urllib.error.URLError as e:
        die(f"GitHub cannot be reached ({e}). Try again, or use --from-zip")
    version = rel["tag_name"].lstrip("v")
    urls = {a["name"]: a["browser_download_url"] for a in rel["assets"]}
    zname = f"mkwmp-{version}-windows-x64.zip"
    for n in (zname, "SHA256SUMS", "SHA256SUMS.sig"):
        if n not in urls:
            die(f"release {rel['tag_name']} has no {n}")
    sums = verified_sums(get(urls["SHA256SUMS"], 1 << 20), get(urls["SHA256SUMS.sig"], 1 << 10).decode("ascii", "replace"), version)
    say(f"downloading {zname}")
    z = get(urls[zname], progress=zname)
    if hashlib.sha256(z).hexdigest() != sums.get(zname):
        die(f"{zname}: checksum mismatch: not installing")
    say(f"{zname}: signature and checksum are good")
    return version, z


def zip_members(zf):
    """(path in the install folder, info) for the launcher's files: from under mkwmp/ when the zip has that folder."""
    infos = [i for i in zf.infolist() if not i.is_dir()]
    top = "mkwmp/" if any(i.filename == "mkwmp/" + EXE for i in infos) else ""
    out, seen = [], set()
    for i in infos:
        if not i.filename.startswith(top):
            continue
        rel = i.filename[len(top):]
        parts = pathlib.PurePosixPath(rel).parts
        if not parts or rel.startswith("/") or "\\" in rel or ":" in rel or any(p in ("..", ".") for p in parts):
            die(f"the zip has an unsafe path ({i.filename}): not installing")
        if rel.lower() in seen:
            die(f"the zip has {rel} twice: not installing")
        seen.add(rel.lower())
        out.append((rel, i))
    if EXE.lower() not in seen:
        die(f"the zip has no {EXE}: it is not the MKWMP launcher for Windows")
    if len(out) > 512 or sum(i.file_size for _, i in out) > 1 << 30:
        die("the zip is larger than a launcher can be: not installing")
    return out


def zip_version(zf, members):
    """A local build's version: from the SHA256SUMS it carries, else unknown."""
    for rel, info in members:
        if rel == "game/SHA256SUMS":
            m = re.match(r"# mkwmp (\S+)", zf.read(info).decode("utf-8", "replace"))
            if m:
                return m.group(1)
    return "local"


def unpack(z, dest):
    with zipfile.ZipFile(io.BytesIO(z)) as zf:
        members = zip_members(zf)
        version = zip_version(zf, members)
        written = kept = 0
        for rel, info in members:
            out = dest / rel
            first = pathlib.PurePosixPath(rel).parts[0]
            # the player's: settings are never replaced, logs and the update's staging never touched
            if first in KEEP_DIRS or ("/" not in rel and rel.lower().endswith(".toml") and out.exists()):
                kept += 1
                continue
            data = zf.read(info)
            if out.is_file() and out.stat().st_size == len(data) and hashlib.sha256(out.read_bytes()).digest() == hashlib.sha256(data).digest():
                continue
            write_atomic(out, data, 0o755 if rel.lower().endswith(".exe") else 0o644)
            written += 1
        names = {rel.lower() for rel, _ in members}
    say(f"{'would write' if DRY else 'wrote'} {written} files into {dest}" + (f" ({kept} of yours kept)" if kept else ""))
    # A test build's launcher under its former names: the zip's mkwmp.exe and updater.exe take their place.
    for n in FORMER_FILES:
        old = dest / n
        if n.lower() not in names and old.is_file():
            if not DRY:
                old.unlink()
            say(f"{'would remove' if DRY else 'removed'} {old} (the launcher's name before 1.4.0)")
    return version


# ---- the settings of the old Linux launcher (MKWMP 1.3 and older: native, not under Proton) ----
OLD_HOMES = ("mkwmp", "mkw-180-launcher")  # where the site's installer of 1.3 and the older guides put it


def old_launcher_dirs(root):
    """Folders the old Linux launcher may be in: its Steam shortcuts' (shortcuts.vdf is only read), then the usual ones."""
    out = []
    for u in steam_users(root) if root else []:
        f = u / "config/shortcuts.vdf"
        try:
            top = bget(bvdf_load(f.read_bytes()), "shortcuts") if f.is_file() else None
        except (OSError, ValueError, IndexError, struct.error):
            top = None
        for it in top[2] if top is not None and top[0] == 0 else []:
            if it[0] != 0 or not is_legacy(it[2]):
                continue
            e = bget(it[2], "exe")
            exe = e[2].decode("utf-8", "replace").strip() if e is not None and e[0] == 1 else ""
            m = re.match(r'"([^"]*)"', exe)
            program = m.group(1) if m else exe.split(" ")[0]
            if os.path.isabs(program):
                out.append(pathlib.Path(program).parent)
    out += [HOME / d for d in OLD_HOMES]
    return list(dict.fromkeys(out))


def toml_top_string(text, key):
    """(line index, value) of a top-level `key = "..."` / `key = '...'` line, or (None, None)."""
    for i, line in enumerate(text.split("\n")):
        if line.lstrip().startswith("["):
            break
        m = re.match(rf"\s*{key}\s*=\s*(\"(?:[^\"\\]|\\.)*\"|'[^']*')\s*(#.*)?$", line)
        if m:
            v = m.group(1)
            try:
                return i, (json.loads(v) if v.startswith('"') else v[1:-1])
            except ValueError:
                return i, None
    return None, None


def windows_settings(text):
    """The old launcher's client.toml as this launcher reads it under Proton (launcher/src/Mkwmp.Core/ClientConfig.cs:
    the same keys). Two values meant the native launcher and change: `game`, a Linux path, becomes the same folder
    on Wine's Z: drive; `launch_command` (run-game.sh, how the old launcher started the game through Proton) goes,
    since the launcher now runs in Proton and starts the game itself. `launch = false` (the old SteamOS default) the
    launcher itself takes for its default again. Every other line stays as it is."""
    lines = text.split("\n")
    i, game = toml_top_string(text, "game")
    if i is not None and game and game.startswith("/"):
        lines[i] = "game = " + json.dumps("Z:" + game.replace("/", "\\"), ensure_ascii=False)
    i, command = toml_top_string(text, "launch_command")
    if i is not None and command:
        lines[i] = 'launch_command = ""'
    return "\n".join(lines)


def import_settings(dest, given, root):
    """A first install starts from the old launcher's settings, so the name and its PIN (shown once, at registration)
    are not lost. Nothing is done when the install folder has its own client.toml already; the old file is never changed."""
    target = dest / "client.toml"
    if target.exists():
        return
    if given:
        src = pathlib.Path(given).expanduser()
        if not src.is_file():
            die(f"--import {given}: no such file")
    else:
        src = next((d / "client.toml" for d in old_launcher_dirs(root) if (d / "client.toml").is_file() and d.resolve() != dest.resolve()), None)
        if src is None:
            return
    try:
        text = read_text(src)
    except OSError as e:
        say(f"the old settings in {src} could not be read ({e}): the launcher starts with its welcome")
        return
    _, name = toml_top_string(text, "name")
    _, pin = toml_top_string(text, "pin")
    write_atomic(target, windows_settings(text).encode("utf-8", "surrogateescape"), 0o600)
    who = f" ({name}" + (", with its PIN" if pin else "") + ")" if name else ""
    say(f"{'would take' if DRY else 'took'} your settings{who} from {src}; that file is left as it was")


# ---- artwork: links only (tools/linux/artwork.json); the pictures are downloaded here, on the player's machine ----
ART_SLOTS = {  # slot: (grid file suffix, asset type of SteamGridDB's search, its dimensions filter)
    "grid": ("p", "grid", ["600x900"]),
    "wide": ("", "grid", ["920x430", "460x215"]),
    "hero": ("_hero", "hero", None),
    "logo": ("_logo", "logo", None),
    "icon": ("_icon", "icon", None),
}
ART_UA = {"User-Agent": "mkwmp-installer/1.0"}
# Not a release asset: the repository's copy at the release's tag, else main's.
ART_LIST = "https://raw.githubusercontent.com/" + REPO + "/{ref}/tools/linux/artwork.json"
ART_SEARCH = "https://www.steamgriddb.com/api/public/search/main/games"


def fetch(url, limit, data=None, headers=None):
    """A GET (or a POST of `data`) with a 30 s limit, tried three times. A 4xx answer is final."""
    last = None
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, data=data, headers={**ART_UA, "Accept": "*/*", **(headers or {})})
            with urllib.request.urlopen(req, timeout=30) as r:
                body = r.read(limit + 1)
            if len(body) > limit:
                raise ValueError("too large")
            return body
        except urllib.error.HTTPError as e:
            if 400 <= e.code < 500 and e.code != 429:
                raise
            last = e
        except (OSError, ValueError) as e:
            last = e
        time.sleep(2 * (attempt + 1))
    raise last


def image_ext(data):
    """The extension for what the bytes are; None for what Steam does not show."""
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return ".png"
    if data[:3] == b"\xff\xd8\xff":
        return ".jpg"
    if data[:4] == b"\x00\x00\x01\x00":
        return ".ico"
    return None


def art_list(given, version=None):
    """artwork.json: the file given, the one beside this script, else the repository's at the tag v<version>, else main's."""
    here = os.environ.get("MKWMP_SELF_DIR", "")
    for p in [given, os.path.join(here, "artwork.json") if here else None]:
        if p and os.path.isfile(p):
            return json.loads(pathlib.Path(p).read_text(encoding="utf-8"))
    if given:
        die(f"--artwork {given}: no such file")
    if version and re.fullmatch(r"[0-9][0-9A-Za-z.+-]*", version):
        try:
            return json.loads(fetch(ART_LIST.format(ref="v" + version), 1 << 20))
        except urllib.error.HTTPError:
            pass  # no such tag (a test build's version): main's
    return json.loads(fetch(ART_LIST.format(ref="main"), 1 << 20))


def art_search(slot, term, game_id):
    """The fallback for a link that is gone: SteamGridDB's public search (no key; not a documented API).
    Of the game's pictures: no nsfw / humor / epilepsy mark, not animated, in English or without a language;
    the official style first, then votes."""
    _, kind, dims = ART_SLOTS[slot]
    body = {"asset_type": kind, "term": term, "offset": 0}
    if dims:
        body["filters"] = {"dimensions": dims}
    r = json.loads(fetch(ART_SEARCH, 8 << 20, json.dumps(body).encode(), {"Content-Type": "application/json"}))
    games = (r.get("data") or {}).get("games") or []
    game = next((g for g in games if (g.get("game") or {}).get("id") == game_id), None)
    if game is None:
        raise ValueError("the game is not in SteamGridDB's answer")
    good = [a for a in game.get("assets") or [] if a.get("url") and not a.get("nsfw") and not a.get("humor") and not a.get("epilepsy")
            and not a.get("is_animated") and a.get("language") in ("en", None, "")]
    if dims:  # the first size asked for, when there is one of it
        good = [a for a in good if f"{a.get('width')}x{a.get('height')}" == dims[0]] or good
    if not good:
        raise ValueError("no usable picture in SteamGridDB's answer")
    # max() keeps the first of equals: SteamGridDB's own order decides a tie
    return max(good, key=lambda a: (a.get("style") == "official", (a.get("upvotes") or 0) - (a.get("downvotes") or 0)))["url"]


def art_download(given, version=None):
    """{slot: (bytes, extension)}. A slot that fails is left out: artwork never stops an install."""
    out = {}
    try:
        listed = art_list(given, version)
    except (OSError, ValueError) as e:
        say(f"artwork: the list of links could not be read ({e})")
        listed = {}
    term, game_id = listed.get("search_term", "Mario Kart World"), listed.get("steamgriddb_game_id", 5483943)
    for slot in ART_SLOTS:
        url = ((listed.get("assets") or {}).get(slot) or {}).get("url")
        for source in ("link", "search"):
            try:
                if source == "search":
                    url = art_search(slot, term, game_id)
                if not url or urllib.parse.urlparse(url).scheme != "https":
                    continue
                data = fetch(url, 32 << 20)
                ext = image_ext(data)
                if ext is None or (ext == ".ico" and slot != "icon"):
                    raise ValueError("not a PNG or JPEG picture")
                out[slot] = (data, ext)
                say(f"artwork: {slot} ({len(data) // 1024} KB" + (", found by search)" if source == "search" else ")"))
                break
            except (OSError, ValueError, KeyError, TypeError) as e:
                say(f"artwork: {slot}: {'link' if source == 'link' else 'search'} failed ({e})")
    return out

def art_files(grid, appid):
    """The grid files of this shortcut, every extension."""
    out = []
    for suffix, *_ in ART_SLOTS.values():
        out += [f for f in grid.glob(f"{appid}{suffix}.*") if f.suffix.lower() in (".png", ".jpg", ".jpeg", ".ico", ".json", ".webp")]
    return sorted(set(out))


def art_install(grid, appid, art):
    """Writes the pictures as Steam looks them up; the icon's path for the shortcut, or None."""
    icon = None
    for slot, (data, ext) in art.items():
        suffix = ART_SLOTS[slot][0]
        out = grid / f"{appid}{suffix}{ext}"
        if not DRY:
            for old in grid.glob(f"{appid}{suffix}.*"):
                if old != out and old.suffix.lower() in (".png", ".jpg", ".jpeg", ".ico", ".webp"):  # .json is Steam's logo position: kept
                    old.unlink()
        write_atomic(out, data, 0o644)
        if slot == "icon":
            icon = out
    return icon


# ---- the Steam side of an install / uninstall ----
def steam_apply(root, dest, proton, launch_options, art, stamp, remove=False):
    exe = f'"{dest / EXE}"'
    appid = shortcut_id(exe, NAME)
    # This install's shortcut as a test build before the rename made it: its id came from mkwmp-launcher.exe.
    former_exe = f'"{dest / FORMER_EXE}"'
    former = [(shortcut_id(former_exe, NAME), former_exe)]
    users = steam_users(root)
    old_ids = {i for i, _ in former}  # their Proton setting goes (the new id gets it)
    if not users:
        say(f"no Steam user in {root}/userdata: sign in to Steam once, then run this again")
        return appid, False
    for fid, _ in [] if remove else former:
        # Its Proton prefix (nothing of the player's, but minutes to make again) goes with it.
        pfx, new_pfx = root / "steamapps/compatdata" / str(fid), root / "steamapps/compatdata" / str(appid)
        if pfx.is_dir() and not new_pfx.exists():
            if not DRY:
                pfx.rename(new_pfx)
            say(f"{'would move' if DRY else 'moved'} the shortcut's Proton prefix {pfx.name} to {new_pfx.name} (its id before the rename)")
    for u in users:
        cfg = u / "config"
        f = cfg / "shortcuts.vdf"
        icon = None
        # The artwork of the shortcut under its former id: renamed to the new id (or removed with it).
        for fid, _ in former:
            for g in art_files(cfg / "grid", fid):
                if remove:
                    if not DRY:
                        g.unlink()
                    continue
                to = g.with_name(str(appid) + g.name[len(str(fid)):])
                taken = to.exists()  # the new id has its own already
                if not DRY:
                    g.unlink() if taken else g.rename(to)
                say(f"Steam user {u.name}: {'would ' if DRY else ''}{'remove' if taken else 'move'}{'' if DRY else 'd'} {g.name}"
                    + ("" if taken else f" to {to.name}") + " (the shortcut's id before the rename)")
        if not remove:
            icon = art_install(cfg / "grid", appid, art) if art else None
            if icon is None:
                have = sorted((cfg / "grid").glob(f"{appid}_icon.*"))
                own = dest / "mkwmp-launcher.png"
                icon = have[0] if have else own if own.exists() or DRY else None
        raw = f.read_bytes() if f.exists() else b""
        legacy = []
        try:
            new, what = shortcuts_edit(raw, appid, exe, f'"{dest}"', NAME, launch_options, str(icon) if icon else "", remove,
                                       None if remove else legacy, former)
        except (ValueError, IndexError) as e:
            die(f"{f}: {e}. Nothing of Steam was changed for user {u.name}")
        if new != raw:
            backup(f, stamp)
            write_atomic(f, new, 0o644)
        say(f"Steam user {u.name}: shortcut {what}" + (" (dry run)" if DRY and new != raw else ""))
        for old_id, old_name, old_exe in legacy:
            say(f"Steam user {u.name}: {'would remove' if DRY else 'removed'} the old MKWMP shortcut \"{old_name}\" (id {old_id}, {old_exe})")
            if old_id is None:
                continue
            old_ids.add(old_id)
            gone = art_files(cfg / "grid", old_id)
            for g in gone:
                if not DRY:
                    backup(g, stamp)
                    g.unlink()
            if gone:
                say(f"Steam user {u.name}: its {len(gone)} artwork files {'would go' if DRY else 'removed (copies: *.bak-mkwmp-' + stamp + ')'}")
        if remove:
            gone = art_files(cfg / "grid", appid)
            for g in gone:
                if not DRY:
                    g.unlink()
            if gone:
                say(f"Steam user {u.name}: {len(gone)} artwork files removed")
        # The Steam Deck's layout, by the shortcut's name in lower case (as Steam files a non-Steam game's choice).
        for cs in sorted((root / "steamapps/common/Steam Controller Configs" / u.name / "config").glob("configset_controller_neptune.vdf")):
            try:
                text = read_text(cs)
                top = tvdf_find(text, ["controller_config"])
                has = top is not None and any(e[0] == NAME.lower() for e in tvdf_entries(text, top))
                if remove and has:
                    new_text = configset_edit(text, NAME, None)
                elif not remove and not has:  # a layout the player chose is theirs
                    new_text = configset_edit(text, NAME, DECK_TEMPLATE)
                else:
                    continue
                backup(cs, stamp)
                write_atomic(cs, new_text.encode("utf-8", "surrogateescape"))
                say(f"Steam user {u.name}: Steam Deck layout {'entry removed' if remove else 'set to Gamepad'}")
            except (ValueError, IndexError, OSError) as e:
                say(f"Steam Deck layout not set ({e}): choose the Gamepad template in the shortcut's controller settings")
    cv = root / "config/config.vdf"
    tool = None if remove else proton
    if cv.exists() and (remove or tool):
        try:
            text = read_text(cv)
            new_text = compat_edit(text, appid, tool)
            for old_id in sorted(old_ids):
                cleaned = compat_edit(new_text, old_id, None)
                if cleaned != new_text:
                    new_text = cleaned
                    say(f"the old shortcut {old_id}'s Proton setting {'would go' if DRY else 'removed'}")
            if new_text != text:
                backup(cv, stamp)
                write_atomic(cv, new_text.encode("utf-8", "surrogateescape"))
            say("Proton setting " + ("removed" if remove else f"is {tool}") + ("" if new_text != text else " (already)"))
        except (ValueError, IndexError) as e:
            say(f"{cv}: {e}. Set the compatibility tool yourself: the shortcut's Properties > Compatibility")
    elif not remove:
        say("the compatibility tool is not set: choose Proton in the shortcut's Properties > Compatibility")
    return appid, True


def need_steam_closed(what):
    if steam_running() and not DRY:
        die(f"Steam is running. Close it (Steam menu > Exit; on a Steam Deck do this in Desktop Mode), then run this again.\n"
            f"mkwmp: Steam keeps its shortcut list in memory and writes it back when it closes, so {what} now would be lost.\n"
            f"mkwmp: This script never closes Steam for you.", 3)


def parse_args(argv):
    a = {"dir": None, "version": None, "zip": None, "proton": None, "launch": LAUNCH_OPTIONS, "root": None, "steam": True,
         "artwork": None, "art_only": False, "art": True, "import": None, "no_import": False,
         "uninstall": False, "purge": False, "dry": False, "yes": False}
    flags = {"--no-steam": ("steam", False), "--art-only": ("art_only", True), "--no-art": ("art", False), "--uninstall": ("uninstall", True),
             "--purge": ("purge", True), "--dry-run": ("dry", True), "--yes": ("yes", True), "--no-import": ("no_import", True)}
    values = {"--dir": "dir", "--version": "version", "--from-zip": "zip", "--proton": "proton", "--launch-options": "launch",
              "--steam-root": "root", "--artwork": "artwork", "--import": "import"}
    it = iter(argv)
    for x in it:
        if x in ("-h", "--help"):
            print(HELP)
            sys.exit(0)
        elif x in flags:
            a[flags[x][0]] = flags[x][1]
        elif x in values:
            try:
                a[values[x]] = next(it)
            except StopIteration:
                die(f"{x} needs a value")
        else:
            die(f"unknown option {x} (--help lists them)")
    a["dir"] = pathlib.Path(a["dir"]).expanduser().absolute() if a["dir"] else HOME / "Games/MKWMP"
    if a["version"]:
        a["version"] = a["version"].lstrip("v")
    return a


def main():
    global DRY
    a = parse_args(sys.argv[1:])
    DRY = a["dry"]
    if os.name == "posix" and os.geteuid() == 0:
        die("run this as your normal user, not root")
    dest = a["dir"]
    stamp = time.strftime("%Y%m%d-%H%M%S")
    root = None
    if a["steam"]:
        roots = [pathlib.Path(a["root"]).expanduser().resolve()] if a["root"] else steam_roots()
        if not roots:
            die("Steam was not found. Install Steam and sign in once, or pass --steam-root, or use --no-steam")
        root = roots[0]
        if len(roots) > 1:
            say(f"more than one Steam found; using {root} (--steam-root picks another)")
        say(f"Steam: {root}" + (" (Flatpak)" if is_flatpak(root) else ""))

    if a["uninstall"]:
        appid = shortcut_id(f'"{dest / EXE}"', NAME)
        if root:
            need_steam_closed("removing the shortcut")
            steam_apply(root, dest, None, "", {}, stamp, remove=True)
        if a["purge"]:
            if not (dest / MARKER).is_file():
                die(f"{dest} was not installed by this script (no {MARKER}): delete it yourself")
            if launcher_running(dest):
                die("MKWMP is running: close it first")
            prefix = root / "steamapps/compatdata" / str(appid) if root else None
            targets = [dest] + ([prefix] if prefix and prefix.is_dir() else [])
            say("this deletes, with your settings, your account's PIN and the game MKWMP built from your copy:")
            for t in targets:
                say(f"  {t}")
            if not a["yes"] and ask("Delete them? [y/N] ").lower() != "y":
                die("nothing was deleted")
            for t in targets:
                if not DRY:
                    shutil.rmtree(t)  # never follows a link out of the folder
                say(f"deleted {t}")
        else:
            say(f"{dest} was kept (your settings and the game). --uninstall --purge deletes it")
        return

    if a["art_only"]:
        if not root:
            die("--art-only needs Steam")
        try:
            marker = json.loads((dest / MARKER).read_text())
        except (OSError, ValueError):
            die(f"MKWMP is not installed in {dest} (--dir names another folder)")
        art = art_download(a["artwork"], marker.get("version"))
        if not art:
            die("no artwork was downloaded")
        need_steam_closed("setting the icon")
        steam_apply(root, dest, marker.get("proton"), marker.get("launch_options", a["launch"]), art, stamp)
        say("artwork installed. Open Steam")
        return

    # 1. everything that can fail without having changed anything
    if root:
        need_steam_closed("adding the shortcut")
    if launcher_running(dest):
        die("MKWMP is running from that folder: close it first")
    proton = shown = None
    if root:
        proton, shown = pick_proton(root, a["proton"])
        if proton:
            say(f"Proton: {shown}")
        else:
            proton, shown = "proton_experimental", "Proton Experimental"
            say("no Proton is installed yet: the shortcut is set to Proton Experimental, which Steam offers to install at the first start "
                "(or install it before: Library > search \"Proton Experimental\")")
    if a["zip"]:
        z = pathlib.Path(a["zip"]).expanduser().read_bytes()
        version = None
        say(f"installing from {a['zip']} (a local build: not checked against the release key)")
    else:
        version, z = download(a["version"])

    # 2. the files, and the old launcher's settings on a first install
    zv = unpack(z, dest)
    version = version or zv
    if not a["no_import"]:
        import_settings(dest, a["import"], root)
    write_atomic(dest / MARKER, json.dumps({"version": version, "installed": stamp, "proton": proton, "launch_options": a["launch"]}).encode() + b"\n", 0o644)
    say(f"MKWMP {version} is in {dest}")

    # 3. Steam
    if not root:
        say(f"done (files only). Add {dest / EXE} to Steam yourself and force Proton on it")
        return
    art = {}
    if a["art"]:
        art = art_download(a["artwork"], version)
    appid, ok = steam_apply(root, dest, proton, a["launch"], art, stamp)
    if not ok:
        return
    if is_flatpak(root) and not str(dest).startswith(str(HOME / ".var/app/com.valvesoftware.Steam")):
        say("Flatpak Steam cannot see that folder until you allow it:")
        say(f"  flatpak override --user --filesystem='{dest}' com.valvesoftware.Steam")

    # 4. what is next
    print()
    say("Done. Next:")
    say("  1. Open Steam. MKWMP is in your library under Non-Steam (Game Mode: Library > Non-Steam).")
    say("  2. Start it. The first start takes a minute: Proton makes its prefix.")
    say("  3. In MKWMP: choose a name, point it at your own copy of the game, and let it set the game up.")
    say("     The setup builds the game on this device: tens of minutes on a Steam Deck. Keep it plugged in.")
    say("  4. In a race, hold View (Select) for a second to bring MKWMP over the game.")
    if a["art"] and len(art) < len(ART_SLOTS):
        say("Some library artwork could not be downloaded. To try again later, with Steam closed:")
        say("  bash install-mkwmp.sh --art-only")
    say(f"Shortcut id {appid}. To remove: bash install-mkwmp.sh --uninstall")


try:
    main()
except KeyboardInterrupt:
    die("stopped")
MKWMP_PY
)" "$@"
