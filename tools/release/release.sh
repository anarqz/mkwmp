#!/bin/sh
# Manual client release, run on the MacBook. Nothing is released by CI.
#
#   tools/release/release.sh draft    build both servers (kept on this Mac for publish, not uploaded) and create
#                                     the DRAFT release v<version>
#   (Windows then builds the launcher (C#, launcher/), its update helper, the game patch, voice chat and the
#   mkw-recomp mod, and uploads them to the draft as one working file (tools/release/windows.ps1 has the detail):
#      tools/release/windows.ps1 -Zig <zig.exe>
#   -> mkwmp-launcher-files-windows.zip)
#   tools/release/release.sh publish  download the working file, build the two zips (make_zips.py), write and
#                                     sign SHA256SUMS over them, upload, delete the working file, check that the
#                                     release holds exactly its four assets, publish (a prerelease when the version
#                                     has a -suffix)
#   tools/release/release.sh publish --dry-run
#                                     the same up to the upload, nothing uploaded, deleted or published: lists the
#                                     assets the release would have
#
# The release, from 1.4.0, is exactly four assets:
#   mkwmp-<version>-windows-x64.zip   the launcher (mkwmp.exe, updater.exe, ...) with game/ (the mod, the game patch)
#   mkwmp-server-<version>.zip        the server: server (Linux, static), server.exe (Windows)
#   SHA256SUMS, SHA256SUMS.sig        the two zips' hashes, "# mkwmp <version>", signed with the release key
# Nothing loose: install-mkwmp.sh and artwork.json are read from the repository at the tag v<version>.
#
# <version> is the launcher's (launcher/Directory.Build.props); the tag is v<version>. rust/Cargo.toml's version is
# set by whoever releases the server, before draft.
#
# Env: MKWMP_RELEASE_KEY = path of the release signing seed (never in the repo).
set -eu
cd "$(dirname "$0")/../.."
export PATH="$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin:$HOME/.cargo/bin:$PATH"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' launcher/Directory.Build.props | head -1)"
[ -n "$version" ] || { echo "no <Version> in launcher/Directory.Build.props" >&2; exit 1; }
tag="v$version"
out="rust/target/release-assets/$tag"
zips="mkwmp-$version-windows-x64.zip mkwmp-server-$version.zip"
# Everything the published release holds, sorted as `sort` sorts it.
want="$(printf '%s\n' $zips SHA256SUMS SHA256SUMS.sig | sort)"
# Uploaded by Windows for make_zips.py only: deleted before publishing, so never an asset nor in SHA256SUMS.
work="mkwmp-launcher-files-windows.zip"
case "$version" in *-*) pre="--prerelease" ;; *) pre="--latest" ;; esac

case "${1:-}" in
draft)
  mkdir -p "$out/server"
  # The server (bin `server` of the crate mkwmp): static on Linux. Left in $out/server for publish.
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-pc-windows-gnu
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl
  cp rust/target/x86_64-pc-windows-gnu/release/server.exe "$out/server/server.exe"
  cp rust/target/x86_64-unknown-linux-musl/release/server "$out/server/server"
  # A draft is private and creates no tag; publishing tags the commit below.
  gh release view "$tag" >/dev/null 2>&1 || gh release create "$tag" --draft $pre \
    --target "$(git rev-parse HEAD)" --title "mkwmp $version" --notes-file tools/release/notes.md
  echo "draft $tag ready, servers in $out/server; Windows runs tools/release/windows.ps1 next (the working file $work)"
  ;;
publish)
  dry=""
  [ "${2:-}" = "--dry-run" ] && dry=1
  : "${MKWMP_RELEASE_KEY:?set MKWMP_RELEASE_KEY to the release seed file}"
  for b in server server.exe; do
    [ -f "$out/server/$b" ] || { echo "no $out/server/$b: run release.sh draft on this Mac first" >&2; exit 1; }
  done
  rm -rf "$out/dl" && mkdir -p "$out/dl"
  # A draft has no tag yet, so `gh release download <tag>` can't find it: fetch by asset id.
  rel="$(gh api "repos/{owner}/{repo}/releases" --jq ".[] | select(.tag_name == \"$tag\") | .id" | head -1)"
  [ -n "$rel" ] || { echo "no release $tag (release.sh draft makes it)" >&2; exit 1; }
  id="$(gh api "repos/{owner}/{repo}/releases/$rel/assets" --jq ".[] | select(.name == \"$work\") | .id")"
  [ -n "$id" ] || { echo "missing the working file $work (tools/release/windows.ps1 uploads it)" >&2; exit 1; }
  gh api -H "Accept: application/octet-stream" "repos/{owner}/{repo}/releases/assets/$id" > "$out/dl/$work"
  cp "$out/server/server" "$out/server/server.exe" "$out/dl/"
  python3 tools/release/make_zips.py "$version" "$MKWMP_RELEASE_KEY" "$out/dl"
  (cd "$out/dl" && python3 "$OLDPWD/tools/release/sign_sums.py" "$version" "$MKWMP_RELEASE_KEY" $zips)
  if [ -n "$dry" ]; then
    echo "dry run: nothing uploaded; the release $tag would hold:"
    (cd "$out/dl" && ls -l $zips SHA256SUMS SHA256SUMS.sig)
    exit 0
  fi
  (cd "$out/dl" && gh release upload "$tag" --clobber $zips SHA256SUMS SHA256SUMS.sig)
  # Exactly the four assets besides the working file, or not published (an asset an older draft left).
  have="$(gh api "repos/{owner}/{repo}/releases/$rel/assets" --jq '.[].name' | grep -vx "$work" | sort)"
  if [ "$have" != "$want" ]; then
    printf 'the draft %s holds:\n%s\nnot the four assets:\n%s\ndelete the others, then run publish again\n' "$tag" "$have" "$want" >&2
    exit 1
  fi
  gh api -X DELETE "repos/{owner}/{repo}/releases/assets/$id"
  gh release edit "$tag" --draft=false $pre
  echo "published $tag"
  ;;
*)
  sed -n '2,27p' "$0"; exit 2 ;;
esac
