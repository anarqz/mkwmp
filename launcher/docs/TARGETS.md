# Game targets

MKWMP is one server. A **target** is one way of running the game that MKWMP can hook into:
mkw-recomp today (`MkwRecompTarget`), an emulator or another recompilation later. The launcher
core (`Mkwmp.Core.Launcher`) and the window know no game: everything that differs per solution
is behind `IGameTarget` (`src/Mkwmp.Core/Targets/IGameTarget.cs`), and the target in use is
`client.toml` `target = "<id>"` (`LauncherSettings.Target`).

## What stays generic (do not put it in a target)

- client.toml, the profile (gamertag + PIN), consent, friends, reports, notifications.
- The seat: join, queue, heartbeat, rejoin, release (`Net/SeatSession.cs`).
- The local API the hook calls: `http://127.0.0.1:<local_port>/local/v1/{seat,friends,voice,voice/devices,voice/mute}`
  with `Authorization: Bearer <token>` (`Net/LocalApi.cs`; contract: `docs/contracts/LAUNCHER_API.md`,
  `docs/contracts/VOICE_API.md` §4).
- Finding the files of an integration: the signed `game/` folder next to the launcher, else this
  launcher's GitHub release, verified against the signed `SHA256SUMS` (`Launcher.Game.cs`).
- The order of what blocks PLAY: settings, the target's gates, the local API port, the integration.

## What the hook inside the game must speak

Whatever form it takes (a DLL beside the exe, a mod, an emulator plugin or script), the hook:

1. Reads the **session file** the launcher writes before the game starts (for mkw-recomp:
   `mkwmp.toml` beside mkw.exe): the lobby `server`, the player's `name`, `launcher` (host:port of
   the local API), `launcher_token`, `voice_port`, and `ticket` once seated.
2. Calls **`POST /local/v1/seat`** with the token when the game goes online, and again at least
   every 20 s while online (it is the keepalive). `seated` gives the ticket and lobby to use;
   `queued` a position to show; `refused` a message; `unavailable` means stay offline.
3. Optionally `GET /local/v1/friends` (the game's friend list) and the voice routes.

A hook that does this works with the launcher unchanged, whatever runs the game.

## Adding a target

Implement `IGameTarget` in `src/Mkwmp.Core/Targets/<Name>/` and add it to the list the app passes
as `LauncherOptions.Targets` (the default list is in `Launcher`'s constructor). Each member:

| Member | What it must do |
| --- | --- |
| `Id`, `DisplayName`, `FolderHint` | A stable id for client.toml; the name shown; what a folder must hold ("the one with mkw.exe"), used in the picker and in "Set the game folder (…)". |
| `Capabilities` | What this target supports. Without `Launch` the launcher never starts the game ("start it from Steam / the emulator"). |
| `Detect(folder)` | The install in that folder, or null. Cheap and never throwing: it runs on every folder of the look-around. Fill `Version` (the runtime's), `GameVersion`, `DataRoot` when known. |
| `SearchRoots()` | Folders worth a shallow (two-level) look when nothing was chosen. |
| `Check(install)` | The gates: `Unsupported` (with `FixUrl`/`FixLabel` when there is a build to get), `NeedsRebuild`, else `Ready`. `Problem` is shown verbatim and blocks PLAY; `Explain`, when set, is added in brackets to a failed PLAY. |
| `PackageAssets` | The release asset names the hook is made of. The core downloads and verifies them and hands them over as an `IntegrationPackage` (name to a verified file on disk, and its sha256). |
| `Inspect(install, package)` | The state of the hook's files against the package (null: nothing to compare with): `Installed`, `Outdated`, `Damaged`, `Missing`..., a summary and a line per file. Called every 2 s: keep it to reading files. |
| `InstallAsync` / `UninstallAsync` | Put the files in place (check each against `package.Sha256` again), or take every MKWMP file out and put back what was there. Refuse while the game runs. Throw `LauncherException` with a message for the player. |
| `NeedsPackage(install)`, `KeepsOwnIntegration` | Optional. False / true when the game already carries the hook some other way (mkw-recomp: the MKWMP mod is enabled; a developer's build is kept). |
| `WriteSession(install, session)` | Write the session file atomically (temporary file, then rename) where the hook reads it; with `LobbyCaPem`, the lobby's certificate (remove a stale one when null). Called again whenever the ticket changes. |
| `ForgetTicket(install)` | Drop the ticket from the session file (the profile was deleted). |
| `Launch(install, options)` | Start the game (`options.CustomCommand` wins when set) and return a handle the session loop watches; null when the player starts it elsewhere. |
| `IsRunning(install)` | Whether the game runs, also when not started by the launcher. |
| `SupportItems(install)` | Files for the bug-report zip (game logs; the session file with secrets masked via `SupportBundle.RedactToml`), and a `system.txt` item whose text is appended to the report's system.txt. |
| `NativeLibraryFolders(install)` | Optional: where mkwmp-camera.dll may be, for the launcher's own camera test. |

### An emulator, concretely

- `Detect`: the emulator's executable (and version, from its file version or a hash table).
- `Check`: the emulator version the hook supports, and the game/update version it has tables for.
- `PackageAssets` + `InstallAsync`: the plugin or patch files, into the emulator's plugin/mod folder.
- `WriteSession`: the session file where that plugin reads it (the emulator's user folder, or an
  environment variable passed by `Launch`).
- `Launch`: the emulator with the game's path, or null with `Capabilities` lacking `Launch`.
- The plugin implements the three steps of "What the hook inside the game must speak".

Tests: see `tests/Mkwmp.Core.Tests` (`RecompAndProbeTests.cs` for a target on its own,
`LauncherTests.cs` for PLAY against a fake seat server with a target whose game is a command).
