#!/bin/bash
# MKWMP installer for SteamOS / Steam Deck (Desktop Mode) and other Linux PCs with Steam.
#
#   curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash
#   curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash -s -- --help
#
# Since MKWMP 1.4.0 the launcher is the Windows one, run under Proton, and the installer is a file of each
# release: install-mkwmp.sh (tools/linux/install-mkwmp.sh, docs/LINUX.md). This only fetches the newest
# release's copy and runs it with your options. That script checks what it installs against the release's
# SHA256SUMS, signed with the MKWMP release key; it takes the settings (name, PIN, server) of the old Linux
# launcher of MKWMP 1.3 (~/mkwmp) on a first install, and removes that launcher's Steam shortcut.
set -euo pipefail
url="https://github.com/anarqz/mkwmp/releases/latest/download/install-mkwmp.sh"
dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
if ! curl -fsSL --retry 3 -o "$dir/install-mkwmp.sh" "$url"; then
  echo "mkwmp: the newest release has no install-mkwmp.sh yet ($url)." >&2
  echo "mkwmp: try again in a few minutes, or see https://anarqz.github.io/mkwmp/#steamos" >&2
  exit 1
fi
bash "$dir/install-mkwmp.sh" "$@"
