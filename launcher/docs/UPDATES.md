# The launcher's own update

How MKWMP Launcher updates itself: a download into a staging folder, "Install and Restart", and a
small separate helper (`mkwmp-updater.exe`) that swaps the files once the launcher is gone. The
design is Infinity Launcher's (`Infinity.Update` / `Infinity.Updater`: a Native AOT helper run from
a copy, a verified package, a backup that a failure puts back), reduced to what MKWMP's releases
are: assets on GitHub listed in a signed `SHA256SUMS`.

Not this document: the game runtime's install (`RuntimeInstaller`, `.mkwmp-update` in the game
folder) and the game patch's sync (`game/`), which have their own rules.

## 1. What the player sees

1. **Available.** At start (and "Check Now") the launcher asks GitHub for a newer release. Home's
   What's New tile says "Version 1.5.0 is available" with a "New" badge; Settings › Updates shows
   the row "Install Version 1.5.0". Both open the update card, which shows the release notes.
2. **Download.** The card shows the bar, "31.2 MB of 78.0 MB · 2.4 MB/s · 20 seconds remaining",
   Cancel and Close. Close leaves the download running; when it finishes behind another page a toast
   says "Update downloaded". Nothing installed is touched and nothing restarts.
3. **Ready to install.** The card offers **Install and Restart** and **Later**. After "Later" the
   tile says "Version 1.5.0 is ready to install" with a "Ready" badge and Settings › Updates shows
   "Install Version 1.5.0 and Restart". The download stays ready across restarts of the launcher,
   and without a connection.
4. **Install and Restart.** The launcher frees its seat and closes. The helper's small window
   (centred, no frame, the launcher's theme and language) says "Installing the update…" with the step
   under it: "Waiting for MKWMP to close", "Checking the download", "Replacing files" (a bar),
   "Starting MKWMP". Then the new launcher opens with the same arguments.
5. **A game is running.** Nothing is ever installed under a game: not during a PLAY session, not
   while the game runs by itself, not while the launcher stands by as the game's overlay host
   (OVERLAY.md §7). The download still happens in the background. The card then says so and offers
   **Install After the Game**: the launcher installs by itself once no game runs any more (it can be
   taken back). Without that choice the update simply stays "ready to install".
6. **It fails.** The helper says "The update was not installed" for a few seconds (a click or a key
   ends it sooner), every file is back as it was, and the previous launcher opens again and says why
   (Settings › Updates; `logs/updater.log` has the detail). The download is kept, so "Try again"
   needs no new download unless it was the download that was wrong.

`--headless` has no window to ask: a newer release found at start is downloaded, verified and
installed by the launcher process itself (the same swap, in place: a running executable can be
renamed aside), then the new launcher is started and its exit code passed on, as before.
`--no-update` and "Update Automatically" off: nothing is asked of GitHub, nothing is downloaded.

## 2. The pieces

| Project | What it is |
|---|---|
| `src/Mkwmp.Update` | The library both sides share (`net10.0`, no dependency, AOT-clean): the plan (`UpdatePlan`), the signed list (`ReleaseSums`), the package's files and path rules (`UpdatePackage`), the swap with backup and rollback (`UpdateInstaller`), the helper's whole run (`UpdateRunner`), its arguments (`UpdaterArgs`), strings (`UpdaterStrings`) and log (`UpdateLog`). |
| `src/Mkwmp.Updater` | `mkwmp-updater.exe`: `Program` (the run), the window (`UpdaterWindow`, `Painter`, `Native`) and the Ed25519 check (`ReleaseSignature`, BouncyCastle). |
| `src/Mkwmp.Core` | `Updates/Updater.cs` (GitHub, the resumable download), `Updates/SelfUpdate.cs` (staging), `Launcher.Updates.cs` (the states, the hand-over, "after the game"). |
| `src/Mkwmp.App` | `ViewModels/UpdateModal.cs` (the card), `ShellViewModel.Update.cs` (the toast), the tile and Settings › Updates. |

### The helper: why native, and how big

`mkwmp-updater.exe` is published with **Native AOT**: one native executable of **2.36 MB**
(2 358 784 bytes; the launcher's single file is 71 MB), no .NET runtime to unpack, so it is on
screen at once. Its window is plain **Win32 + GDI+** through P/Invoke (`user32`, `gdiplus`,
optionally `dwmapi` for Windows 11's rounded corners, `shlwapi` to decode the mark), all of which
Wine and Proton provide. WPF can be neither trimmed nor compiled ahead of time: a WPF helper would
be a second self-contained program of about the launcher's size, unpacking its runtime at the very
moment the player looks at an empty screen. Of the 2.36 MB, about 0.3 MB are the two Nunito
weights and the mark, embedded so the window looks like the launcher; the rest is the runtime,
`System.IO.Compression`, `System.Text.Json` and the Ed25519 part of BouncyCastle (the only package;
.NET has no Ed25519, and the launcher's NSec needs libsodium as a second file).

It carries an `asInvoker` manifest: without one Windows takes an executable with "update" in its
name for an installer and asks for elevation.

A plain `dotnet build` (and the tests) builds it as an ordinary managed program; only a publish
with a runtime identifier (`publish.ps1`) compiles it natively, which needs Visual Studio's C++ x64
tools for the link.

### The helper can be replaced like any other file

The launcher never runs the installed `mkwmp-updater.exe`. It runs `worker.exe`, a **copy in the
staging folder**: the new release's own helper, taken out of the verified zip (so the code that
installs a release is that release's), or, for a release that ships only an executable, a copy of
the installed helper. The installed one is then free to be replaced with everything else.
Infinity's approach, with the staging folder as the "temp name".

## 3. The staging folder

`<launcher folder>/.mkwmp-selfupdate/<version>/`, on the volume of the files it replaces, so that
every move is a rename:

```
SHA256SUMS, SHA256SUMS.sig   as downloaded (the signed bytes)
notes.md                     the release notes, for the card after a restart
package.zip                  the release's zip           (or package.zip.part while downloading)
launcher.exe                 instead: the single executable of a release without the zip
worker.exe                   the helper that will run
plan.json                    written at "Install and Restart" (below)
incoming/                    the package unpacked, during the install
backup/                      the files that were replaced; what a rollback puts back
```

`.mkwmp-selfupdate/installed.txt` lists the paths the last installed zip brought: a later release
that drops a file removes it.

At start the launcher removes the staging folders of its own version and older (the backup, the
worker's copy); a download of a newer version stays and is "ready to install" again after it is
verified once more.

## 4. The flow in detail

**Check** (`Launcher.CheckAsync`): the newest non-draft release above this version. If its verified
download is already staged, the state is `ReadyToInstall` at once. If GitHub cannot be reached but a
verified download waits, it is `ReadyToInstall` all the same.

**Download** (`ILauncher.DownloadUpdateAsync` → `SelfUpdate.StageAsync`):

1. `SHA256SUMS` and `SHA256SUMS.sig` are fetched and verified: Ed25519 by the release key
   (`release1:…`), and the list's first line must name the release's version (a signed list of an
   old release is not accepted under a new tag).
2. The package is chosen: `mkwmp-<version>-windows-x64.zip` when the release and the list have it,
   else `mkwmp-launcher-x86_64-windows.exe`.
3. It is streamed to `package.zip.part` with progress (`UpdateView.Received`, `Total`,
   `BytesPerSecond` over the last four seconds, `Remaining`). A part left by an earlier try (the
   launcher was closed, the line dropped, Cancel) is continued with an HTTP `Range` request; a server
   that ignores the range sends the whole file and the part starts over. 60 s without a byte is a
   timeout.
4. Its SHA-256 must be the listed one, else it is deleted and the state is `Failed`.
5. The zip's file list is checked (section 5's path rules), and `worker.exe` is put in place.

**Install and Restart** (`ILauncher.ApplyUpdateAsync`; it downloads first when that was not done):

1. Refused while a game runs (`UpdateView.GameBlocks`): a session of this launcher (`Session.Busy`
   or `GameRunning`), the game process of the selected target, or a game the launcher is the overlay
   host of. `ILauncher.InstallUpdateAfterGame(true)` arms the install for when that is over.
2. `plan.json` is written, the helper is started, the state is `Restarting`, the seat is freed
   (`WaitIdle`), everything is disposed, and the process ends.

The hand-over, exactly:

```
<staging>\worker.exe --plan <staging>\plan.json --pid <launcher pid> --theme light|dark --locale <xx[-YY]> [--quiet]
```

```json
{ "root": "C:\\Games\\mkwmp", "version": "1.5.0", "from": "1.4.0", "kind": "zip",
  "asset": "mkwmp-1.5.0-windows-x64.zip", "launcher": "mkwmp-launcher.exe", "arguments": ["--tray"] }
```

The plan names no path but the launcher's folder: the package, the list and the signature are where
section 3 says, and a plan is only accepted from the staging folder of the root it names. The plan
is not trusted for anything else.

**The helper** (`UpdateRunner.Run`):

1. *Waiting for MKWMP to close*: waits for the launcher's process, 60 s at most. If it never closes,
   nothing is touched and nothing is started (exit code 3).
2. *Checking the download*: verifies again, from the signature down: `SHA256SUMS.sig` by the release
   key **built into the helper**, the version in the list, the package's SHA-256. Whatever changed
   on disk since the launcher's own check is caught here.
3. *Replacing files* (`UpdateInstaller.Apply`): the package is unpacked into `incoming/`. A file that
   is byte for byte what is installed is left alone. Every other one is swapped: the installed file
   is renamed into `backup/`, the new one renamed into place; the launcher's executable last, so that
   a start in between still finds the old launcher. A rename is retried for 8 s (a scanner may still
   hold a file just after the launcher exits).
4. *Starting MKWMP*: starts the launcher with its original arguments and `MKWMP_UPDATED=1` (the new
   launcher does not look for an update again at once). A launcher that is gone again with an error
   within 2.5 s counts as not started.
5. Any failure in 2 to 4 **rolls back**: every swapped file is put back from `backup/`, newest
   change first; the previous launcher is started with `MKWMP_UPDATED=1` and
   `MKWMP_UPDATE_FAILED=<reason>`, which it shows. Exit code 1. If a file cannot be put back (exit
   code 4) the window says to download MKWMP again and the log names `backup/`.

Every step is a line in `<launcher folder>/logs/updater.log` (kept under 256 KiB), next to the
launcher's session logs.

## 5. What a release may and may not replace

- The zip's files are taken from under its top folder `mkwmp/`. `mkwmp-launcher.exe` is installed
  under the name the launcher has here (a player may have kept the download's name).
- **Player data is never replaced or removed**, whatever a zip holds: any `.toml` in the launcher's
  folder (client.toml), `logs/`, and `.mkwmp-selfupdate/`.
- A path must be relative and made of plain names: no `..`, no drive, no backslash, no stream. A
  duplicate (ignoring case), more than 512 files or more than 1 GiB unpacked refuse the whole
  package before anything is touched; so does a zip without `mkwmp-launcher.exe`.
- Files in the folder that no release brought are left alone.

## 6. The helper's strings

`--locale` names a catalog of the launcher: the helper reads `i18n/<locale>.json` and `i18n/en.json`
in the launcher's folder, the keys `updater.*`:

`updater.title`, `updater.waiting`, `updater.verifying`, `updater.replacing`, `updater.starting`,
`updater.failed_title`, `updater.failed_body`, `updater.failed_broken`, `updater.version`.

English and pt-BR are also built into the helper, for an install whose catalogs are older than the
helper or missing. Order: the locale's catalog, the built-in pt-BR for a Portuguese locale, the
English catalog, built-in English. A new language therefore needs only its catalog.

## 7. Looking at the helper without installing anything

```
mkwmp-updater.exe --shot out.png [--theme dark] [--locale pt-BR] [--step waiting|verifying|replacing|starting|failed]
                  [--progress 0.6] [--scale 2] [--i18n <folder of catalogs>]
```

writes one frame as a PNG and opens no window. The card's states are in the launcher's harness:
`shot.ps1 out.png --modal available | update | ready | ready-game`.

## 8. The release

Build: `launcher/publish.ps1 -Out <folder> [-Version 1.5.0] [-Zig <zig.exe>]` gives

```
mkwmp-launcher.exe     single file, self-contained, compressed (every managed assembly)
*.dll                  its native libraries, beside it: WPF's (*_cor3.dll, D3DCompiler_47_cor3.dll), libsodium
mkwmp-updater.exe      the helper, Native AOT              2.4 MB
mkwmp-launcher.png
i18n/*.json
licenses/              Nunito, Noto Sans CJK, BouncyCastle (the helper's)
game/mkwmp-mod.zip     the MKWMP mod built from this tree (plugin, voice and camera libraries)
game/SHA256SUMS        "# mkwmp <version>" and the zip's hash, to be signed
```

`game/` is the integration package the launcher installs into mkw-recomp (launcher/docs/TARGETS.md,
"The integration package"). `publish.ps1` builds the mod (`native/voice/build.py`,
`native/camera/build.py`, `native/probe/build_plugin.py --target all --package`; needs Zig and
Python; `-NoGame` skips it) and writes the list; the **release machine signs it**
(`tools/release/sign_sums.py` → `game/SHA256SUMS.sig`; `make_zips.py` does it for the release zip,
where `mkwmp-mod.zip` is in `game/` beside the older game patch). Without the signature the launcher
ignores the folder and downloads `mkwmp-mod.zip` from its own release, which is how a launcher that
updated itself (and so has an older `game/`) gets the new mod.

**Local test builds: `publish.ps1 -Dev`.** No release key on a developer's machine, so `-Dev` also
writes `game/DEV-PACKAGE` and, when there is none, a `client.toml` with `dev_package = true`. The
launcher takes an unsigned `game/` only with that key or with `MKWMP_DEV_PACKAGE=1`, and then says
"development package (unsigned)" in Settings › Game and in its log. A release build never accepts an
unsigned package silently, and a `-Dev` folder is never to be shipped.

The single file is published with `PublishSingleFile`, `EnableCompressionInSingleFile`, no symbols,
`SatelliteResourceLanguages` limited to the launcher's own languages, `UseSystemResourceKeys=false`
(exception messages stay). WPF is not trimmed (unsupported). **Portable**: the launcher writes nothing
outside its folder, so its native libraries are not in the single file
(`IncludeNativeLibrariesForSelfExtract=false`): bundled, .NET would unpack them into `%TEMP%\.net` at
every first start of a build. They are files of the release like any other: in the zip, swapped by the
helper with the exe (the exe last, all of them rolled back together), installed by
`tools/linux/install-mkwmp.sh`.

`publish.ps1 -Loose <file>` also makes the one-file build, native libraries inside (they unpack into
`%TEMP%\.net`), for the loose asset `mkwmp-launcher-x86_64-windows.exe` alone: a launcher from before
the zip replaces only its executable, so what it downloads must start with nothing beside it. Its
next update installs the zip.

### `tools/release` (applied for 1.4.0)

The release builds and packs this launcher, which ships several files, instead of the Rust launcher's
one. **No asset name changed**: the Windows assets are the same names with new contents. Added:
`install-mkwmp.sh` and `artwork.json` (below). Gone: the Rust launcher for Linux and the Linux zip
(below, "Linux").

| Asset | Up to 1.3.1 | From 1.4.0 |
|---|---|---|
| `mkwmp-launcher-x86_64-windows.exe` | the Rust launcher | the WPF launcher as one self-extracting file (`publish.ps1 -Loose`): **not** the zip's `mkwmp-launcher.exe`, which needs its native DLLs beside it. What the 1.3.0 Rust launcher's self-update and every launcher before this one download. |
| `mkwmp-<version>-windows-x64.zip` | `mkwmp/mkwmp-launcher.exe`, `game/`, `LICENSE`, `NOTICE.md`, `licenses/`, `README.txt` | the same, plus the rest of the publish folder under `mkwmp/`: the native `*.dll` beside the exe, `mkwmp-updater.exe`, `i18n/`, `mkwmp-launcher.png`, `CHANGELOG.md`, and the launcher's `licenses/`. It is at once the download for a new player and the package the helper installs. |
| `SHA256SUMS`, `SHA256SUMS.sig` | list and sign every asset | unchanged (`sign_sums.py` as it is). The zip's hash in the signed list is what authenticates every file of an update. |

Why the existing zip and not a new `mkwmp-launcher-windows-x64.zip`: it would be the same 70 MB a
second time in every release, and a second thing to keep in step. The zip's name is derived from
the version (`Updater.ZipAsset`), which the tag gives.

The scripts:

1. `release.sh draft` (the Mac): the two servers, `install-mkwmp.sh` and `artwork.json`; the version is
   the launcher's (`launcher/Directory.Build.props`), no longer `rust/Cargo.toml`'s.
2. `tools/release/windows.ps1` (Windows: the helper's native link needs MSVC): `native/probe/build.py`
   and `launcher/publish.ps1 -Out <out> -Version <version> -Loose mkwmp-launcher-x86_64-windows.exe`, then
   uploads the loose file, `winmm.dll`, `mkwmp-voice.dll`, `mkwmp-mod.zip` and one working file,
   `mkwmp-launcher-files-windows.zip` (everything in `<out>` but `game/`: `mkwmp-launcher.exe`, the native
   `*.dll`, `mkwmp-updater.exe`, `i18n/`, `licenses/`, `mkwmp-launcher.png`, `CHANGELOG.md`).
3. `release.sh publish` (the Mac): downloads every asset and the working file; `make_zips.py` takes
   `mkwmp/mkwmp-launcher.exe` and every other entry from the working file (not the loose asset, which
   is the self-extracting build), with the fixed date and modes it uses, refusing a working file with
   a `.toml`, `logs/`, `game/` or no helper; signs `SHA256SUMS`; deletes the working file from the draft
   (never an asset, never in `SHA256SUMS`); publishes.

Compatibility, both ways:

- **Old launcher → new release.** The Rust launcher of 1.3.x (and older, back to 0.0.1-rc.1) takes the
  newest release above its version, downloads `mkwmp-launcher-x86_64-windows.exe`, checks it against
  the signed list, renames itself to `mkwmp-launcher.exe.old`, puts the file in its place and starts
  it with its own arguments and `MKWMP_UPDATED=1`. This launcher then runs alone in the 1.3.x zip's
  folder: no `i18n/` (every catalog is built in: `Mkwmp.Core.Catalogs`), no helper, no native
  libraries beside it (the loose build unpacks them into `%TEMP%\.net`), `game/` of 1.3.x (its list
  names another version, so the mod comes from this launcher's own release), and the Rust launcher's
  `client.toml`, which it reads as it is (same file, same keys): a registered player with the consent
  goes straight home (`SetupDone` is set for a working online profile), name, PIN, server, game folder,
  voice and tray settings kept. It removes `mkwmp-launcher.exe.old`. `MKWMP_UPDATED` keeps it from
  checking at once; its *next* update installs the whole zip, helper and native libraries included
  (the helper always comes out of the new zip). Covered by
  `SelfUpdateTests.After_the_rust_launcher_swapped_this_one_in_…`.
- **New launcher → a release without the zip** (or with a zip that has no helper): it downloads the
  single executable and installs it with the installed helper's copy; with no helper at all, in
  place, as the launcher did before.
- **Linux.** From 1.4.0 the release has **no `mkwmp-launcher-x86_64-linux` and no Linux zip**: this
  launcher runs under Proton from the Windows zip, installed by `install-mkwmp.sh`. A 1.3.x native
  Linux launcher finds no asset of its name in the newest signed list: its update check fails with a
  line in its log ("… is not in SHA256SUMS"), and it goes on as before, its game patch from its own
  release (still published). Shipping the 1.3.1 binary again under 1.4.0 would make it "update" at
  every start (its version stays below the tag), and a Linux "bridge" would have to edit Steam's
  shortcuts while Steam runs it. The way over is `install-mkwmp.sh` (the site's `install.sh` runs it),
  which takes the old launcher's `client.toml` on a first install (docs/LINUX.md).

### The SteamOS / Linux installer in the release (applied for 1.4.0)

`tools/linux/install-mkwmp.sh` (docs/LINUX.md) installs the **Windows zip above** under Proton and makes
the Steam shortcut. It downloads `mkwmp-<version>-windows-x64.zip` and checks it against the same
signed `SHA256SUMS`, so the zip needs nothing new. What the release adds:

| Asset | What |
|---|---|
| `install-mkwmp.sh` | `tools/linux/install-mkwmp.sh` as it is, a loose asset listed in `SHA256SUMS`. The one command for players is `curl -fsSL https://github.com/anarqz/mkwmp/releases/latest/download/install-mkwmp.sh \| bash`; the site's `install.sh` fetches and runs the same file. |
| `artwork.json` | `tools/linux/artwork.json` (links to SteamGridDB only, no picture), a loose asset. The script reads the copy beside itself, else the one of the repository's `main`. |

In the scripts: `release.sh draft` uploads the two files; `sign_sums.py` lists them like any asset;
`make_zips.py` does not change (neither file goes into a zip: the Windows zip stays what the helper
installs, and a player on Linux needs the script before any zip). `README-linux.txt` and the Linux
zip describe the Rust launcher and go away with it.

When the launcher updates itself under Proton it replaces its own files in the same folder
(section 4); the script is only needed again to move the shortcut to another Proton or to repair it.
Both keep `client.toml`, `logs/` and `.mkwmp-selfupdate/`, and neither touches `mkw-recomp/`.

## 9. Tests

`tests/Mkwmp.Core.Tests/SelfUpdateTests.cs` (GitHub is a fake; no process is started, no window
opened) and `UpdaterTests.cs`:

- the plan round-trips and is only read from its own staging folder; the helper's arguments parse,
  bad ones are refused; the strings' fallback order, and en/pt-BR catalogs carry every `updater.*` key;
- the helper's BouncyCastle check and the launcher's NSec check agree on a signature, bit by bit;
- the swap: every changed file, unchanged ones left alone, player data never, the launcher under
  the name it has, a dropped file removed, a single-executable release;
- rollback: a file that cannot be moved half way puts everything back, and the install can be retried;
- refused untouched: a changed package, another signer, a replayed list, unsafe paths, no launcher;
- the helper's run: installed and started; a launcher that does not start is rolled back and the old
  one started with the reason; a launcher that never closes;
- the launcher: staged then ready, nothing installed until asked, the helper started with the plan,
  pid, theme and language, the stage cleared by the new launcher; a cut download continued with a
  Range request; a staged download that survives a restart, also offline; a tampered stage
  downloaded again; refused while a game runs, installed after the game when asked; `--headless` in
  place from the zip; a release without the zip through the installed helper.

## 10. Not done, and to check on a device

- **Under Wine/Proton nothing was run.** The helper uses only APIs Wine implements, but the window
  under gamescope (Game Mode has one focused window) and the rename of the running single-file
  executable (the `--headless` path) are to be tried on the Deck.
- **No start-up handshake.** Infinity's replacement launcher acknowledges its start before the
  update is committed; here the helper only watches for an early exit with an error. A release that
  starts and then fails is not rolled back.
- **No recovery after a power cut mid-swap.** The swap is a few renames; an interrupted one leaves
  `backup/` with the originals (a later attempt never overwrites it), but nothing restores it by itself.
- **The launcher ends with `Environment.Exit`** after disposing the core (as the update did before):
  the window's own exit path (`App.OnExit`, the tray icon) does not run.
- The release scripts (section 8) were run on Windows with a test key only (`windows.ps1`'s packing,
  `make_zips.py`, `sign_sums.py`), not yet for a real release; `release.sh` was not run.
