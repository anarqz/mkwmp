#!/bin/sh
# Manual client release, run on the MacBook. Nothing is released by CI.
#
#   tools/release/release.sh draft    build both servers, create the DRAFT release v<version> and upload them
#                                     with the Linux installer (install-mkwmp.sh, artwork.json)
#   (Windows then builds the launcher (C#, launcher/), its update helper, the game patch, voice chat and
#   the mkw-recomp mod, and uploads them to the draft (tools/release/windows.ps1 has the detail):
#      tools/release/windows.ps1 -Zig <zig.exe>
#   which runs python native/probe/build.py --version <version> and
#      launcher/publish.ps1 -Out <out> -Version <version> -Loose mkwmp-launcher-x86_64-windows.exe
#   then uploads mkwmp-launcher-x86_64-windows.exe, winmm.dll, mkwmp-voice.dll, mkwmp-mod.zip and the
#   working file mkwmp-launcher-files-windows.zip)
#   tools/release/release.sh publish  download every asset and the working file, build the Windows zip
#                                     (make_zips.py), write and sign SHA256SUMS, upload, delete the
#                                     working file, publish (a prerelease when the version has a -suffix)
#
# <version> is the launcher's (launcher/Directory.Build.props). Since 1.4.0 the release has no Linux
# launcher (mkwmp-launcher-x86_64-linux) and no Linux zip: the launcher runs under Proton from the
# Windows zip, installed by install-mkwmp.sh. A 1.3.x native Linux launcher finds no asset of its name
# in the signed list, keeps running as it is, and keeps its game patch from its own release.
#
# Env: MKWMP_RELEASE_KEY = path of the release signing seed (never in the repo).
set -eu
cd "$(dirname "$0")/../.."
export PATH="$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin:$HOME/.cargo/bin:$PATH"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' launcher/Directory.Build.props | head -1)"
[ -n "$version" ] || { echo "no <Version> in launcher/Directory.Build.props" >&2; exit 1; }
tag="v$version"
out="rust/target/release-assets/$tag"
# The loose files: launchers from before the zip (the Rust launcher of 1.3.x and older) update themselves
# from the launcher asset, and a 1.0.0+ launcher with no zip copy fetches the DLLs by name.
# mkwmp-mod.zip (1.3.0+) is the mkw-recomp mod: mkwmp/ to drop into <game>/mods/.
assets="mkwmp-launcher-x86_64-windows.exe mkwmp-server-x86_64-windows.exe mkwmp-server-x86_64-linux winmm.dll mkwmp-voice.dll mkwmp-mod.zip install-mkwmp.sh artwork.json"
zips="mkwmp-$version-windows-x64.zip"
# Uploaded by Windows for make_zips.py only: deleted before publishing, so never an asset nor in SHA256SUMS.
work="mkwmp-launcher-files-windows.zip"
case "$version" in *-*) pre="--prerelease" ;; *) pre="--latest" ;; esac

case "${1:-}" in
draft)
  mkdir -p "$out"
  # The server (`mkwmp host` for players hosting on their own PC): static on Linux.
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-pc-windows-gnu
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl
  cp rust/target/x86_64-pc-windows-gnu/release/mkwmp.exe "$out/mkwmp-server-x86_64-windows.exe"
  cp rust/target/x86_64-unknown-linux-musl/release/mkwmp "$out/mkwmp-server-x86_64-linux"
  # The SteamOS / Linux installer and its artwork links (docs/LINUX.md), as they are in the repository.
  cp tools/linux/install-mkwmp.sh tools/linux/artwork.json "$out/"
  # A draft is private and creates no tag; publishing tags the commit below.
  gh release view "$tag" >/dev/null 2>&1 || gh release create "$tag" --draft $pre \
    --target "$(git rev-parse HEAD)" --title "mkwmp $version" --notes-file tools/release/notes.md
  gh release upload "$tag" --clobber "$out/mkwmp-server-x86_64-windows.exe" "$out/mkwmp-server-x86_64-linux" \
    "$out/install-mkwmp.sh" "$out/artwork.json"
  echo "draft $tag ready; Windows runs tools/release/windows.ps1 next (the launcher, winmm.dll, mkwmp-voice.dll, mkwmp-mod.zip)"
  ;;
publish)
  : "${MKWMP_RELEASE_KEY:?set MKWMP_RELEASE_KEY to the release seed file}"
  rm -rf "$out/dl" && mkdir -p "$out/dl"
  # A draft has no tag yet, so `gh release download <tag>` can't find it: fetch by asset id.
  rel="$(gh api "repos/{owner}/{repo}/releases" --jq ".[] | select(.tag_name == \"$tag\") | .id" | head -1)"
  for a in $assets $work; do
    id="$(gh api "repos/{owner}/{repo}/releases/$rel/assets" --jq ".[] | select(.name == \"$a\") | .id")"
    [ -n "$id" ] || { echo "missing asset $a" >&2; exit 1; }
    gh api -H "Accept: application/octet-stream" "repos/{owner}/{repo}/releases/assets/$id" > "$out/dl/$a"
  done
  python3 tools/release/make_zips.py "$version" "$MKWMP_RELEASE_KEY" "$out/dl"
  (cd "$out/dl" && python3 "$OLDPWD/tools/release/sign_sums.py" "$version" "$MKWMP_RELEASE_KEY" $assets $zips)
  (cd "$out/dl" && gh release upload "$tag" --clobber $zips SHA256SUMS SHA256SUMS.sig)
  id="$(gh api "repos/{owner}/{repo}/releases/$rel/assets" --jq ".[] | select(.name == \"$work\") | .id")"
  gh api -X DELETE "repos/{owner}/{repo}/releases/assets/$id"
  gh release edit "$tag" --draft=false $pre
  echo "published $tag"
  ;;
*)
  sed -n '2,22p' "$0"; exit 2 ;;
esac
