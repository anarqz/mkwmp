# The launcher's own update

How MKWMP Launcher updates itself: a download into a staging folder, "Install and Restart", and a
small separate helper (`updater.exe`) that swaps the files once the launcher is gone. The
design is Infinity Launcher's (`Infinity.Update` / `Infinity.Updater`: a Native AOT helper run from
a copy, a verified package, a backup that a failure puts back), reduced to what MKWMP's releases
are: a zip on GitHub listed in a signed `SHA256SUMS`.

Not this document: the game runtime's install (`RuntimeInstaller`, `.mkwmp-update` in the game
folder) and the game patch's sync (`game/`), which have their own rules.

## 1. What the player sees

**An update is not an offer: it is required** (from 1.4.4; the user's rule: "Launcher must enforce users to
update to latest version when identified a new version exists"). The player cannot turn the check off:
client.toml's old `update = false` is read and ignored, and Settings › Updates has no switch for it.

1. **Found.** At start (and "Check Now") the launcher asks GitHub for a newer release. When there is one it
   starts the download **by itself, at once**, and the update card opens by itself and **cannot be closed**
   (no Later, no Close, B does nothing): "Updating MKWMP… Version 1.5.0 is needed to keep playing." PLAY is
   refused by the core while the update is pending (`UpdateView.Required`), and nothing behind the card can
   be reached. The card's bottom button is **Quit MKWMP**, in every blocking state.
2. **Download.** The card shows the bar, "31.2 MB of 78.0 MB · 2.4 MB/s · 20 seconds remaining". No Cancel.
   Nothing installed is touched yet. A download cut by closing the launcher is continued at the next start.
3. **Ready to install.** The card offers **Install and Restart** and counts down ("Installing in 10
   seconds"): at zero (`UpdateModal.AutoInstallSeconds`) it presses it itself. Home's tile and Settings ›
   Updates say "ready to install" behind it.
4. **Install and Restart.** The launcher frees its seat and closes. The helper's small window
   (centred, no frame, the launcher's theme and language) says "Installing the update…" with the step
   under it: "Waiting for MKWMP to close", "Checking the download", "Replacing files" (a bar),
   "Starting MKWMP". Then the new launcher opens with the same arguments.
5. **A game is running: it does not block, and a game is never killed.** Nothing is ever installed under a
   game: not during a PLAY session, not while the game runs by itself, not while the launcher stands by as
   the game's overlay host (OVERLAY.md §7). The download still happens in the background, the card does not
   hold the player (it can be closed and says "The update is installed as soon as the game closes"), and the
   launcher **installs by itself once no game runs any more**. This cannot be taken back.
6. **It fails** (the download, or the install). Never a dead end:
   - *The download fails:* the card says why, with **Try Again**, **Continue Without Updating** and Quit.
   - *The install fails:* the helper says "The update was not installed" for a few seconds, every file is
     back as it was, and the previous launcher opens again (`MKWMP_UPDATED=1`, `MKWMP_UPDATE_FAILED=<reason>`).
     That launcher shows the same card with the reason and the same three buttons. It **does not check or
     install anything by itself in that run**, so a release that cannot be installed on this PC never loops.
     The download is kept: Try Again needs no new download unless it was the download that was wrong.
   - **Continue Offline** (`update.continue_without`) lets this run go on **offline only**, until the launcher
     is closed. PLAY works and is the offline one (the game starts with no seat). Everything that talks to the
     game server is off, with one reason, "Update MKWMP to play online": no status, no sign-in, no friends,
     no reports, no consent written, no server list measured, and the game asking this launcher for a seat
     (`/local/v1/seat`) is refused (409, "Update MKWMP to play online: open the MKWMP launcher (Settings,
     Updates)"). Nothing saved is touched (the server, the PIN, the consent stay in client.toml). The way back
     is always there and never blocks: Home's Friends tile and dock button become "Try the Update Again",
     every locked place (the friends panel, Settings › Online, Voice & Camera) shows the reason with that
     button, and Settings › Updates has the row; all open the update's card (Try again, Close). GitHub and
     the mod browser are not affected. **The next start requires the update again.** Offered only after a
     failure.
7. **Not blocked at all:** GitHub cannot be reached or the PC is offline (the check finds nothing: play as
   before, and a verified download that already waits is still installed); a game running (5);
   `--headless` (below); `--no-update` (development and tests only: nothing is asked of GitHub).

`--headless` has no window to ask: a newer release found at start is downloaded, verified and
installed by the launcher process itself (the same swap, in place: a running executable can be
renamed aside), then the new launcher is started and its exit code passed on, as before.

**Who this binds.** Only launchers from 1.4.4 on. A 1.4.3 (or older 1.4.x) launcher still only *offers* the
update (What's New tile, Settings › Updates, "Later"): nothing in a newer release can make an older launcher
enforce it.
## 2. The pieces

| Project | What it is |
|---|---|
| `src/Mkwmp.Update` | The library both sides share (`net10.0`, no dependency, AOT-clean): the plan (`UpdatePlan`), the signed list (`ReleaseSums`), the package's files and path rules (`UpdatePackage`), the swap with backup and rollback (`UpdateInstaller`), the helper's whole run (`UpdateRunner`), its arguments (`UpdaterArgs`), strings (`UpdaterStrings`) and log (`UpdateLog`). |
| `src/Mkwmp.Updater` | `updater.exe`: `Program` (the run), the window (`UpdaterWindow`, `Painter`, `Native`) and the Ed25519 check (`ReleaseSignature`, BouncyCastle). |
| `src/Mkwmp.Core` | `Updates/Updater.cs` (GitHub, the resumable download), `Updates/SelfUpdate.cs` (staging), `Launcher.Updates.cs` (the states, the hand-over, "after the game"). |
| `src/Mkwmp.App` | `ViewModels/UpdateModal.cs` (the card), `ShellViewModel.Update.cs` (the toast), the tile and Settings › Updates. |

### The helper: why native, and how big

`updater.exe` is published with **Native AOT**: one native executable of **2.36 MB**
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

The launcher never runs the installed `updater.exe`. It runs `worker.exe`, a **copy in the
staging folder**: the new release's own helper, taken out of the verified zip (so the code that
installs a release is that release's; a zip without one would get a copy of the installed helper).
The installed one is then free to be replaced with everything else.
Infinity's approach, with the staging folder as the "temp name".

## 3. The staging folder

`<launcher folder>/.mkwmp-selfupdate/<version>/`, on the volume of the files it replaces, so that
every move is a rename:

```
SHA256SUMS, SHA256SUMS.sig   as downloaded (the signed bytes)
notes.md                     the release notes, for the card after a restart
package.zip                  the release's zip           (or package.zip.part while downloading)
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

**Check** (`Launcher.CheckAsync`): the newest non-draft release above this version that the update channel
offers (`Updater.Newest`; "Channels" below). If its verified
download is already staged, the state is `ReadyToInstall` at once. If GitHub cannot be reached but a
verified download waits, it is `ReadyToInstall` all the same.

**Required** (`Launcher.EnforceUpdateAsync`, when `LauncherOptions.RequireUpdates`: what `LauncherOptions.Parse`
gives every real run; not headless, and only a launcher that can replace itself, so not under `dotnet`). A
check that found a release sets `UpdateView.Required` on every view from then on and downloads at once. Then:
a game runs → `InstallUpdateAfterGame(true)` by itself (and `InstallUpdateAfterGame(false)` is ignored); no
game → the view is `ReadyToInstall` and the window installs (`UpdateModal`: the button, or its countdown).
`PlayAsync` returns without starting while `Required`. `ILauncher.ContinueWithoutUpdate()` clears `Required`
for the run, and only from `Failed`, and makes the run **offline only** (`ILauncher.OnlineBlockedByUpdate`).
That is not a second offline: it is the launcher's one offline state (section 12 of DESIGN.md, "locked
online"), entered without clearing the server. `Launcher.IsOnline(settings)`, the gate every server call
already went through, is now "a server is set **and** the run is not offline only" (`Launcher.HasServer` is
the old meaning, for what is only about the file), and `ILauncher.OnlineEnabled` follows it; entering does
what leaving a server does (`ServerChanged`: signed out there, friends and reports cleared, a session's seat
given back). The window reads `Shell.OnlineBlockedByUpdate`: `Shell.ConfigureOnline()` opens the update's
card instead of Settings › Online, and `v:OnlineLocked` shows the one reason in every locked place. The window blocks on `UpdateModal.Blocks`: `Required`, no game
(`GameBlocks`, `AfterGame`, a session), not in overlay mode; `ShellViewModel.WatchUpdate` opens the card.
A launcher started by the helper (`MKWMP_UPDATED`) checks nothing at start; after a rollback
(`MKWMP_UPDATE_FAILED`) its view is `Failed` and `Required`, and "Try Again" is `CheckForUpdatesAsync`.

**Channels** (`UpdateChannel`, `update_channel` in client.toml, Settings › Updates › Update channel; from the release after 1.5.0).

- **Stable** (the default; the key is not written): only stable releases, `Release.IsStable`: published, not
  flagged as a prerelease on GitHub, tagged `v<version>` with no pre-release suffix.
- **Alpha** (`update_channel = "alpha"`): stable releases and release candidates, `Release.IsAlpha`: a version
  that is exactly `X.Y.Z-rc.N` (`SemVer.IsReleaseCandidate`), tagged `alpha-v<version>` (what `release.sh` cuts) or
  `v<version>`. Any other pre-release (`-beta.1`, `-dev.abc`, a plain version flagged as a prerelease) is in no
  channel. The highest version by semver wins: `1.5.1-rc.2` over `1.5.1-rc.1`, `1.5.1` over both.
- The signature, the version in the signed list and the hash are checked the same way for both. Nothing about a
  release candidate is taken on less.
- **Required per channel.** The rule above ("Required") applies to whatever `Updater.Newest` returns for the
  channel: an alpha launcher must take the newest of both kinds, a stable launcher is never given a release
  candidate. A download staged on the other channel is not offered (`Updater.Takes`: `FindStaged`, `Clean`).
- **Changing the channel** (`ILauncher.SetUpdateChannelAsync`) saves it and checks at once.
- **Leaving alpha on a release candidate.** On stable, `1.5.1-rc.2` waits for the next stable release at or above
  its numbers (`1.5.1` is newer than `1.5.1-rc.2`) and takes it as an ordinary required update. Until there is
  one, `UpdateView.StableReturn` names the newest stable release (`Updater.StableReturn`), and Settings › Updates
  shows "Return to Stable": asked once when the channel is changed, and there as a row afterwards. Declined, the
  launcher stays as it is. Never required, never by itself.
- **"Return to Stable" is the only downgrade** (`ILauncher.ReturnToStableAsync`), and it is checked three times:
  the launcher (a release candidate runs, the channel is stable, the release is stable and older, no game runs);
  the plan (`return_to_stable: true`, written for this install only); the installer
  (`UpdateInstaller.CheckDirection`, in the helper and in the in-place install): a plan for an older version is
  refused unless it says so, and one that says so is refused unless what is installed (the launcher exe's product
  version, else the plan's `from`) is a pre-release and the target is a stable release below it. The helper that
  runs is the installed one (the older release's own would not know the rule). `SelfUpdate.FindStaged` only ever
  finds newer versions, so a staged older release is never picked up by anything else; other staged downloads are
  removed before the hand-over. client.toml, `logs/`, mods and the game are not in a release and are not touched
  (section 5).
- Release candidates are tagged `alpha-v…` because launchers up to 1.5.0 take the highest `v…` tag whatever it is
  (`tools/release/README.md`).
- The version is shown with its channel ("1.5.1-rc.2 · Alpha": Settings › Updates, About, What's New) and named in
  bug reports ("1.5.1-rc.2 (alpha channel)"). What's New lists the candidates' own sections on alpha; on stable
  they are left out once their release is in the list (`Changelog.For`).
- Demo: `shot.ps1 out.png --page settings --category updates --state alpha | rc-on-stable`.
- Tests: `SelfUpdateTests.Channels.cs`.

**Download** (`ILauncher.DownloadUpdateAsync` → `SelfUpdate.StageAsync`):

1. `SHA256SUMS` and `SHA256SUMS.sig` are fetched and verified: Ed25519 by the release key
   (`release1:…`), and the list's first line must name the release's version (a signed list of an
   old release is not accepted under a new tag).
2. The package is `mkwmp-<version>-windows-x64.zip`, the only one: a release (or a list) without it
   has nothing to install (`Failed`). No loose executable is ever downloaded.
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
  "asset": "mkwmp-1.5.0-windows-x64.zip", "launcher": "mkwmp.exe", "arguments": ["--tray"] }
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

- The zip's files are taken from under its top folder `mkwmp/`. `mkwmp.exe` is installed under the
  name the launcher has here (a player may have renamed it), and that name is started afterwards.
- **Player data is never replaced or removed**, whatever a zip holds: any `.toml` in the launcher's
  folder (client.toml), `logs/`, and `.mkwmp-selfupdate/`.
- A path must be relative and made of plain names: no `..`, no drive, no backslash, no stream. A
  duplicate (ignoring case), more than 512 files or more than 1 GiB unpacked refuse the whole
  package before anything is touched; so does a zip without `mkwmp.exe`.
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
updater.exe --shot out.png [--theme dark] [--locale pt-BR] [--step waiting|verifying|replacing|starting|failed]
            [--progress 0.6] [--scale 2] [--i18n <folder of catalogs>]
```

writes one frame as a PNG and opens no window. The card's states are in the launcher's harness:
`shot.ps1 out.png --modal available | update | ready | ready-game | update-failed` (the last four as a real launcher shows them: required).

## 8. The release

Build: `launcher/publish.ps1 -Out <folder> [-Version 1.5.0] [-Zig <zig.exe>]` gives

```
mkwmp.exe              single file, self-contained, compressed (every managed assembly)
*.dll                  its native libraries, beside it: WPF's (*_cor3.dll, D3DCompiler_47_cor3.dll), libsodium
updater.exe            the helper, Native AOT              2.4 MB
mkwmp-launcher.png
i18n/*.json
licenses/              Nunito, Noto Sans CJK, BouncyCastle (the helper's)
game/mkwmp-mod.zip     the MKWMP mod built from this tree (plugin, voice and camera libraries)
game/SHA256SUMS        "# mkwmp <version>" and the zip's hash (unsigned: for -Dev)
```

`game/` is the integration package the launcher installs into mkw-recomp (launcher/docs/TARGETS.md,
"The integration package"). `publish.ps1` builds the mod (`native/voice/build.py`,
`native/camera/build.py`, `native/probe/build_plugin.py --target all --package`; needs Zig and
Python; `-NoGame` skips it). In a release `game/` also holds the game patch and voice chat, and its
list, over the three, is signed on the release machine (`make_zips.py`). **The launcher downloads
nothing for the game**: it takes `game/` beside itself when its signed list is for its own version,
and every update installs the whole zip, `game/` included, so the two never part. A `game/` that is
missing or another version's is said ("The MKWMP files for version … are not beside the launcher …:
download MKWMP … again").

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

### `tools/release` (from 1.4.0)

A release is **exactly four assets**:

| Asset | What |
|---|---|
| `mkwmp-<version>-windows-x64.zip` | the launcher, under `mkwmp/`: `mkwmp.exe`, its native `*.dll`, `updater.exe`, `i18n/`, `licenses/`, `mkwmp-launcher.png`, `CHANGELOG.md`, `game/` (`winmm.dll`, `mkwmp-voice.dll`, `mkwmp-mod.zip`, their signed `SHA256SUMS`), `LICENSE`, `NOTICE.md`, `README.txt`. At once the download for a new player, what `install-mkwmp.sh` installs under Proton, and the package the helper installs. |
| `mkwmp-server-<version>.zip` | the server, flat: `server` (Linux x86-64, static, mode 755), `server.exe` (Windows x86-64), `LICENSE`, `NOTICE.md`, `licenses/`, `README.txt` (docs/HOSTING.md and the deploy script). |
| `SHA256SUMS`, `SHA256SUMS.sig` | `# mkwmp <version>` and the two zips' hashes, signed (Ed25519) with the release key. The zip's hash in it authenticates every file of an update. |

Nothing loose: no executable, DLL or mod zip of its own, no Linux launcher. `install-mkwmp.sh` and
`artwork.json` are read from the repository at the tag (`raw.githubusercontent.com/anarqz/mkwmp/v<version>/tools/linux/…`;
`site/install.sh` falls back to `main`). The version is the launcher's (`launcher/Directory.Build.props`),
the tag `v<version>`. A test version (`X.Y.Z-rc.N`, the alpha channel) is the same four assets under the tag
`alpha-v<version>`, a GitHub prerelease that is never "Latest": `tools/release/README.md`.

The scripts:

1. `release.sh draft` (the Mac): builds the two servers (`cargo zigbuild -p mkwmp`, the crate's bin
   `server`, for `x86_64-unknown-linux-musl` and `x86_64-pc-windows-gnu`), keeps them in
   `rust/target/release-assets/v<version>/server/` on the Mac (not uploaded), and creates the draft.
2. `tools/release/windows.ps1` (Windows: the helper's native link needs MSVC): `native/probe/build.py`
   and `launcher/publish.ps1 -Out <out> -Version <version>`, puts `winmm.dll` and `mkwmp-voice.dll` into
   its `game/` beside the mod (and drops publish.ps1's unsigned list), then uploads one **working file**,
   `mkwmp-launcher-files-windows.zip` (the whole folder). `-NoUpload` stops before the upload.
3. `release.sh publish` (the Mac): downloads the working file; `make_zips.py` builds the launcher zip
   from it (fixed date and modes, refusing a `.toml`, `logs/`, anything in `game/` but the three files,
   a missing `mkwmp.exe` / `updater.exe`, or the pre-1.4.0 names) with `game/SHA256SUMS(.sig)` signed,
   and the server zip from the two servers; `sign_sums.py` lists and signs the two zips; uploads the
   four; checks the draft holds exactly them besides the working file (else it stops, unpublished);
   deletes the working file; publishes. `release.sh publish --dry-run` does everything up to the
   upload and lists the four files.

Compatibility:

- **1.3.x → 1.4.0: by hand.** No asset of 1.4.0 has the name a 1.3.x launcher looks for
  (`mkwmp-launcher-x86_64-windows.exe`, `mkwmp-launcher-x86_64-linux`): its update check finds nothing
  in the signed list, logs it, and it keeps working with its own release's game patch. The player
  downloads the 1.4.0 zip. Unzipped into the 1.3.x folder, `mkwmp.exe` stands beside the Rust
  `mkwmp-launcher.exe` (README.txt says to delete it) and reads the Rust launcher's `client.toml` as it
  is (same file, same keys): a registered player with the consent goes straight home (`SetupDone` is
  set for a working online profile); name, PIN, server, game folder, voice and tray settings kept;
  the start-up entry (`HKCU\…\Run`, `MKWMP`, the same value) is pointed at `mkwmp.exe`. Covered by
  `SelfUpdateTests.A_1_3_folder_the_new_zip_was_unzipped_into_…`. On Linux the way over is
  `install-mkwmp.sh` (docs/LINUX.md), which takes the old launcher's `client.toml` on a first install.
- **1.4.x → later.** The zip, through the helper (sections 3 to 5).
- **The rename (`mkwmp-launcher.exe` → `mkwmp.exe`, `mkwmp-updater.exe` → `updater.exe`).** No release
  had the old names for this launcher (1.3.1, the newest release, is the Rust one), so only test
  installs have them. Such an install updating from a zip keeps its launcher's name (section 5: the
  zip's `mkwmp.exe` is installed as `mkwmp-launcher.exe`, so its shortcuts keep working) and the new
  `updater.exe` arrives beside it; the old helper goes when `installed.txt` listed it. To take the new
  names, unzip the release over it and delete the two old files; on Linux run `install-mkwmp.sh`
  again, which removes them and moves the Steam shortcut (section below).
- **`MKWMP_UPDATED` and `<exe>.old`.** The helper still sets `MKWMP_UPDATED=1` on the launcher it
  starts; the launcher still removes an `<exe>.old` at start, which nothing makes any more.

### The SteamOS / Linux installer

`tools/linux/install-mkwmp.sh` (docs/LINUX.md) installs the **launcher zip above** under Proton and
makes the Steam shortcut. It downloads `mkwmp-<version>-windows-x64.zip` and checks it against the
same signed `SHA256SUMS`. It is not a release asset: players run
`curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash`, and `site/install.sh` fetches the
script and `artwork.json` from the repository at the newest release's tag (else `main`). The script
reads the `artwork.json` beside itself, else the repository's at `v<version>`, else `main`'s.

The shortcut's id is derived from the exe's path (`crc32("\"<dir>/mkwmp.exe\"" + "MKWMP")`), so the
rename changes it. Run again on a test install made with `mkwmp-launcher.exe`, the script finds that
shortcut by its old id or exe and updates it in place (new id and exe; its place, play time and tags
kept), renames its grid artwork and moves its Proton prefix (`compatdata/<old id>`) to the new id,
moves the Proton setting to the new id, and removes `mkwmp-launcher.exe` and `mkwmp-updater.exe`
from the folder. No second shortcut is made.

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
  the name it has, a dropped file removed; a plan of another kind than the zip refused;
- rollback: a file that cannot be moved half way puts everything back, and the install can be retried;
- refused untouched: a changed package, another signer, a replayed list, unsafe paths, no launcher;
- the helper's run: installed and started; a launcher that does not start is rolled back and the old
  one started with the reason; a launcher that never closes;
- the launcher: staged then ready, nothing installed until asked, the helper started with the plan,
  pid, theme and language, the stage cleared by the new launcher; a cut download continued with a
  Range request; a staged download that survives a restart, also offline; a tampered stage
  downloaded again; refused while a game runs, installed after the game when asked; `--headless` in
  place from the zip;
- required updates: found → downloaded by itself, `Required`, PLAY refused, installed when the window says
  so; GitHub down → nothing required; a game running → armed for after the game, cannot be taken back,
  installed when it ends; a failed download → `Failed` and `Required`, Continue Offline lifts it,
  Try Again finishes the download; after Continue Offline (OfflineFirstTests): `OnlineEnabled` false with the
  server still saved, signed out, register / friends / reports refused with "Update MKWMP to play online" and
  no request, the local API's seat refused (409), PLAY asks for no seat, and the next start requires again; a rolled-back install (`MKWMP_UPDATE_FAILED`) → said once, no request to
  GitHub and no install by itself (no loop), Try Again installs from the kept download; a 1.3.x folder the zip was unzipped into (the Rust launcher's client.toml);
- the game's package: only `game/` beside the launcher, never downloaded (UpdaterTests, ModInstallTests).

## 10. Not done, and to check on a device

- **The required update was not run end to end against a real release.** The core's states are tested with
  a fake GitHub and the card's states in the harness (`shot.ps1 out.png --modal update | ready | ready-game |
  update-failed`); the first real one is 1.4.4 → the release after it.
- **Offline only binds this launcher, not a game started without it.** The MKWMP mod keeps the account the
  launcher handed it earlier (`mod-settings/mkwmp.ini`); a game started from mkw-recomp or Steam while this
  launcher is not its host can still go online by itself. A game started by PLAY, or one that asks this
  launcher for its seat, stays offline. A server that must refuse old clients has to do so itself.
- **A session that holds a seat when "Continue Offline" is chosen** gets STOP (the seat is given back); the
  card only shows without a game, so this is a corner.
- **A game that runs by itself while the download is still running** is only seen once the download is done
  (the check for a game process is made then): until then the card blocks the launcher's window, which is
  not in front of that game anyway. In overlay mode the card never blocks.
- **The card replaces another modal that is open** when the update is found (the game setup's card, a
  question): what that modal was doing goes on in the core.

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
- The release scripts (section 8) were run on Windows with a test key and stand-in servers only
  (`windows.ps1 -NoUpload`, `make_zips.py`, `sign_sums.py`), not yet for a real release; `release.sh`
  was only read (no shell here), not run.
