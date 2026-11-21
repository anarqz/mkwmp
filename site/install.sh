#!/bin/bash
# MKWMP installer for SteamOS / Steam Deck (Desktop Mode) and other Linux PCs with Steam.
#
#   curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash
#   curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash -s -- --help
#
# Since MKWMP 1.4.0 the launcher is the Windows one, run under Proton, installed by
# tools/linux/install-mkwmp.sh (docs/LINUX.md). The script is not a release asset: this fetches it, with its
# artwork.json, from the repository at the newest release's tag (else main) and runs it with your options.
# That script downloads the release's launcher zip and checks it against the release's SHA256SUMS, signed with
# the MKWMP release key; it takes the settings (name, PIN, server) of the old Linux launcher of MKWMP 1.3
# (~/mkwmp) on a first install, and removes that launcher's Steam shortcut.
set -euo pipefail
command -v python3 >/dev/null || { echo "mkwmp: python3 is needed (SteamOS has it; elsewhere: install python3)" >&2; exit 1; }
repo="anarqz/mkwmp"
dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
# The newest published release (not a draft, not a prerelease); none or no answer: main.
tag="$(curl -fsSL --retry 3 "https://api.github.com/repos/$repo/releases/latest" 2>/dev/null \
  | python3 -I -c 'import json, re, sys; t = json.load(sys.stdin).get("tag_name", ""); print(t if re.fullmatch(r"v[0-9][0-9A-Za-z.+-]*", t) else "")' 2>/dev/null || true)"
got=""
for ref in ${tag:+"$tag"} main; do
  base="https://raw.githubusercontent.com/$repo/$ref/tools/linux"
  if curl -fsSL --retry 3 -o "$dir/install-mkwmp.sh" "$base/install-mkwmp.sh" 2>/dev/null; then
    curl -fsSL --retry 3 -o "$dir/artwork.json" "$base/artwork.json" 2>/dev/null || rm -f "$dir/artwork.json"
    got="$ref"
    break
  fi
done
if [ -z "$got" ]; then
  echo "mkwmp: install-mkwmp.sh could not be downloaded from github.com/$repo." >&2
  echo "mkwmp: try again in a few minutes, or see https://anarqz.github.io/mkwmp/#steamos" >&2
  exit 1
fi
bash "$dir/install-mkwmp.sh" "$@"
