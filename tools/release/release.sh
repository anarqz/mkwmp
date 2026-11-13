#!/bin/sh
# Manual client release, run on the MacBook. Nothing is released by CI.
#
#   tools/release/release.sh draft    build both launchers, create the DRAFT
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
assets="mkwmp-launcher-x86_64-windows.exe mkwmp-launcher-x86_64-linux mkwmp-probe-winmm.dll"

case "${1:-}" in
draft)
  mkdir -p "$out"
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p client --target x86_64-pc-windows-gnu
  cargo zigbuild --release --manifest-path rust/Cargo.toml -p client --target x86_64-unknown-linux-musl
  cp rust/target/x86_64-pc-windows-gnu/release/mkwmp-launcher.exe "$out/mkwmp-launcher-x86_64-windows.exe"
  cp rust/target/x86_64-unknown-linux-musl/release/mkwmp-launcher "$out/mkwmp-launcher-x86_64-linux"
  git tag -a "$tag" -m "mkwmp $version" 2>/dev/null || echo "tag $tag exists"
  git push origin "$tag"
  gh release view "$tag" >/dev/null 2>&1 || gh release create "$tag" --draft --prerelease \
    --title "mkwmp $version" --notes-file tools/release/notes.md
  gh release upload "$tag" --clobber "$out/mkwmp-launcher-x86_64-windows.exe" "$out/mkwmp-launcher-x86_64-linux"
  echo "draft $tag ready; Windows uploads mkwmp-probe-winmm.dll next"
  ;;
publish)
  : "${MKWMP_RELEASE_KEY:?set MKWMP_RELEASE_KEY to the release seed file}"
  rm -rf "$out/dl" && mkdir -p "$out/dl"
  for a in $assets; do gh release download "$tag" -p "$a" -D "$out/dl"; done
  (cd "$out/dl" && python3 "$OLDPWD/tools/release/sign_sums.py" "$version" "$MKWMP_RELEASE_KEY" $assets)
  gh release upload "$tag" --clobber "$out/dl/SHA256SUMS" "$out/dl/SHA256SUMS.sig"
  gh release edit "$tag" --draft=false --prerelease
  echo "published $tag"
  ;;
*)
  sed -n '2,15p' "$0"; exit 2 ;;
esac
