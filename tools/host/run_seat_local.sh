#!/bin/sh
# Run the seat server locally from a checkout (macOS/Linux):
#   tools/host/run_seat_local.sh [config/server.toml]
# On first run it creates the ticket signing key that config/server.toml
# names (scratch/keys/seat-ed25519.key, never committed).
set -eu
cd "$(dirname "$0")/../.."
cfg="${1:-config/server.toml}"
cargo build --release --manifest-path rust/Cargo.toml -p mkwmp
bin=rust/target/release/mkwmp
key=scratch/keys/seat-ed25519.key
if [ ! -s "$key" ]; then
  mkdir -p scratch/keys
  "$bin" keygen dev1 > scratch/keys/keygen.txt
  sed -n 2p scratch/keys/keygen.txt > "$key"
  chmod 600 "$key" scratch/keys/keygen.txt
  echo "created $key; public key entry for lobbies:"
  sed -n 4p scratch/keys/keygen.txt
fi
exec "$bin" server "$cfg"
