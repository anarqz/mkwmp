# Can mkwmp ship as an mkw-recomp mod? (feasibility, 2026-11-13)

Question: deliver multiplayer entirely inside the mkw-recomp launcher (a MULTIPLAYER option, their
mod standard) instead of our launcher + proxy `winmm.dll`.

**Verdict: not possible today.** mkw-recomp 0.1.10's mod system is a *data* mod system. The only
ways to get a MULTIPLAYER entry in their start screen are changes to mkw-recomp itself (a plugin
API upstream, or our code in the runtime), and upstream's stated position on internet play is an
obstacle to both. Sources: upstream `https://github.com/adriadam10/mkw-recomp` (main at `8dd7801`),
its releases, `CHANGELOG.md`, `tools/mod/README.md`, `runtime/`; local `scratch\game\mkw.exe`
(0.1.10).

## What their mods can do (verified in source)

| Capability | Supported |
|---|---|
| RomFS file overlays (`<game>/mods/<name>/romfs`) | yes |
| ARM64 code patches (`.ips` / `.pchtxt`), applied before the game is re-lifted (minutes, per build id) | yes |
| `MKW_*` config switches (8 per mod, values < 32 chars) | yes |
| host-function overrides (`hostfn_add_override` is "for probes", no mod path calls it) | no |
| native DLL/.so plugins (only `mkwgame.dll` is loaded) | no |
| scripting | no |
| launcher UI from a mod (one on/off row on the Mods page) | no |
| networking / threads from a mod | no |

The stock runtime gives guest code no TCP, TLS or DNS. Its LAN mode refuses any non-local address
("so LAN play cannot reach any server on the internet"), so even a code-patch mod cannot reach our
server.

## Mapping

Nothing of ours fits. The probe's ~64 host overrides, the TLS/UDP redirection, seat keepalive, friends
roster, COFF resolution and the Proton fixes all need native code before `ldr_load`. All the launcher
roles (server address, name + PIN, queue, friends, signed self-update) need UI and networking. Only
cosmetic RomFS changes or guest-code patches could ever be mods, and we need neither.

## Facts that matter to us anyway

- `hostfn_add_override` has a hard cap: `MAX_ADDED 64`, fatal on the 65th (`runtime/cpu/hostfn.c`).
- The runtime internals we use are not exported; we reach them through the COFF symbol table. No
  stability promise exists (ten releases in five days).
- 0.1.11 (tagged, about to be released) adds "Choose game folder" (`MKW_GAME_DIR`: game files, saves, mods
  and logs move, the exe stays) and launcher Profiles (name up to 10 chars, `MKW_PLAYER`). These overlap
  with our assumptions (`winmm.dll`, `mkwmp.toml`, `mkwmp.log` beside `mkw.exe`; our `GetNickname`).
- License MIT. Upstream: "There is no online play"; maintainer in discussion #148: "A LAN mode must
  never be able to talk to a server on the internet, whatever the game asks for."

## Options

- **A (now):** stay as we are and harden against upstream churn, 1-2 days:
  - test 0.1.11 (game folder, Profiles, nickname);
  - have the launcher find `mkw.exe` separately from the data folder;
  - add a probe self-check that logs clearly when a COFF symbol or the 64-slot budget is missing.
- **B:** ask upstream for a native plugin entry point (`plugin.dll` in a mod, loaded before `ldr_load`, with
  an API table: overrides, stock-handler lookup, `rt_call`, `cfg_get`, log, Cpu accessors; `MAX_ADDED`
  raised). The probe then installs from their Mods page with no proxy DLL; our launcher stays for
  server/PIN/queue/friends.
  - Effort: 2-4 days for the PR, then 1-2 days to convert the probe.
  - Acceptance uncertain.
- **C:** multiplayer in the runtime itself, as an upstream PR or a fork. This is the only way to get a
  literal MULTIPLAYER page: an ImGui page, overrides as `HOST_OVERRIDE` tables, cross-platform.
  - Effort: 3-5 weeks.
  - As a PR it conflicts with the maintainer's position. As a fork we would ship our own recomp build.

Suggested: do A now. Ask upstream in a GitHub Discussion (not a PR) whether they would accept B and
what they think of third-party servers. Decide B / fork / A from the answer.
