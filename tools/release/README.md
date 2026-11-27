# Releasing MKWMP

Manual, from two machines; nothing is released by CI. The detail is in the header of each script and in
`launcher/docs/UPDATES.md` (section 8).

1. Set the version: `<Version>` in `launcher/Directory.Build.props` (and `version` in `rust/Cargo.toml` when the
   server changed). Write the notes at the top of `tools/release/notes.md` and the section in `CHANGELOG.md`.
2. Mac: `tools/release/release.sh draft` (the servers, the draft).
3. Windows: `tools/release/windows.ps1 -Zig <zig.exe>` (the launcher, the helper, the game's files; one working file
   uploaded to the draft).
4. Mac: `tools/release/release.sh publish --dry-run`, then `tools/release/release.sh publish` (the two zips, the
   signed `SHA256SUMS`, publish).

## Stable release: `X.Y.Z`

Tag `v<version>`, marked **Latest**. Every launcher takes it: it is a required update.

## Test version (the alpha channel): `X.Y.Z-rc.N`

The same four steps with a version like `1.5.1-rc.1`. The scripts then, by themselves:

- tag the release **`alpha-v<version>`** (`alpha-v1.5.1-rc.1`), not `v<version>`;
- create and publish it as a GitHub **prerelease** with `--latest=false`, and `publish` stops with an error if
  GitHub's "Latest" is the test version afterwards;
- refuse any other suffix (`-beta.1`, `-rc1`, `-rc.01`): only `-rc.N` is a test version.

Everything else is the same: the same four assets (`mkwmp-<version>-windows-x64.zip`, `mkwmp-server-<version>.zip`,
`SHA256SUMS`, `SHA256SUMS.sig`), `# mkwmp <version>` in the signed list, the same release key. The launcher verifies
a test version exactly as it verifies a stable one.

Who gets it: launchers whose Settings › Updates › Update channel is **Alpha** (`update_channel = "alpha"` in
client.toml), as a required update, the highest of stable and `-rc.N`. A launcher on **Stable** never does. The
site, `site/install.sh`, `deploy-mkwmp-server.sh` and README's download link follow "Latest", and
`tools/linux/install-mkwmp.sh` skips prereleases: none of them sees a test version.

**Why not `v1.5.1-rc.1`.** Launchers up to 1.5.0 have no channel: they install the highest `v…` tag of the release
list, prerelease or not, as a required update. A test version under a `v` tag would be forced onto every player
still on one of them, and a player who returned to stable 1.5.0 would be sent straight back. They do not read
`alpha-v…` tags. (Launchers with the channel setting read both forms, by the same rules.)

Order of things:

- The channel setting must first ship in a **stable** release (nobody can choose Alpha before that).
- `1.5.1-rc.1`, `1.5.1-rc.2`, … then the release `1.5.1` (plain version, `v1.5.1`, Latest): alpha players get it
  too, since `1.5.1` is newer than `1.5.1-rc.N`.
- Never reuse a version, and never publish a test version below the newest stable release (nobody would take it).
- Withdrawing a test version: delete the GitHub release (and its tag). Alpha players who already have it stay on it
  until the next one; "Return to Stable" in Settings › Updates takes them back to the newest stable release.
