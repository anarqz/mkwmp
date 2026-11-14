#!/bin/sh
# Manual client release, run on the MacBook. Nothing is released by CI.
#
#   tools/release/release.sh draft    build both launchers and both servers, create the DRAFT
#                                     release v<version> and upload them
#   (Windows then builds the game patch and uploads it to the draft:
#      python native/probe/build.py --version <version>
#      gh release upload v<version> <copy of winmm.dll named mkwmp-probe-winmm.dll>)
#   tools/release/release.sh publish  download every asset, write and sign
#                                     SHA256SUMS, upload it, publish as prerelease
#
# Env: MKWMP_RELEASE_KEY = path of the release signing seed (never in the repo).
set -eu
cd "$(dirname "$0")/../.."
export PATH="$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin:$HOME/.cargo/bin:$PATH"
version="$(sed -n 's/^version = "\(.*\)"/\1/p' rust/Cargo.toml | head -1)"
tag="v$version"
out="rust/target/release-assets/$tag"
assets="mkwmp-launcher-x86_64-windows.exe mkwmp-launcher-x86_64-linux mkwmp-server-x86_64-windows.exe mkwmp-server-x86_64-linux mkwmp-probe-winmm.dll"

case "${1:-}" in
draft)
  mkdir -p "$out"
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p client --target x86_64-pc-windows-gnu
  # The window needs the system's GL/X11/Wayland libraries: glibc, not musl.
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p client --target x86_64-unknown-linux-gnu.2.31
  cp rust/target/x86_64-pc-windows-gnu/release/mkwmp-launcher.exe "$out/mkwmp-launcher-x86_64-windows.exe"
  cp rust/target/x86_64-unknown-linux-gnu/release/mkwmp-launcher "$out/mkwmp-launcher-x86_64-linux"
  # The server (`mkwmp host` for players hosting on their own PC): static on Linux.
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-pc-windows-gnu
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl
  cp rust/target/x86_64-pc-windows-gnu/release/mkwmp.exe "$out/mkwmp-server-x86_64-windows.exe"
  cp rust/target/x86_64-unknown-linux-musl/release/mkwmp "$out/mkwmp-server-x86_64-linux"
  # A draft is private and creates no tag; publishing tags the commit below.
  gh release view "$tag" >/dev/null 2>&1 || gh release create "$tag" --draft --prerelease \
    --target "$(git rev-parse HEAD)" --title "mkwmp $version" --notes-file tools/release/notes.md
  gh release upload "$tag" --clobber "$out/mkwmp-launcher-x86_64-windows.exe" "$out/mkwmp-launcher-x86_64-linux" \
    "$out/mkwmp-server-x86_64-windows.exe" "$out/mkwmp-server-x86_64-linux"
  echo "draft $tag ready; Windows uploads mkwmp-probe-winmm.dll next"
  ;;
publish)
  : "${MKWMP_RELEASE_KEY:?set MKWMP_RELEASE_KEY to the release seed file}"
  rm -rf "$out/dl" && mkdir -p "$out/dl"
  # A draft has no tag yet, so `gh release download <tag>` can't find it: fetch by asset id.
  rel="$(gh api "repos/{owner}/{repo}/releases" --jq ".[] | select(.tag_name == \"$tag\") | .id" | head -1)"
  for a in $assets; do
    id="$(gh api "repos/{owner}/{repo}/releases/$rel/assets" --jq ".[] | select(.name == \"$a\") | .id")"
    [ -n "$id" ] || { echo "missing asset $a" >&2; exit 1; }
    gh api -H "Accept: application/octet-stream" "repos/{owner}/{repo}/releases/assets/$id" > "$out/dl/$a"
  done
  (cd "$out/dl" && python3 "$OLDPWD/tools/release/sign_sums.py" "$version" "$MKWMP_RELEASE_KEY" $assets)
  gh release upload "$tag" --clobber "$out/dl/SHA256SUMS" "$out/dl/SHA256SUMS.sig"
  gh release edit "$tag" --draft=false --prerelease
  echo "published $tag"
  ;;
*)
  sed -n '2,15p' "$0"; exit 2 ;;
esac
