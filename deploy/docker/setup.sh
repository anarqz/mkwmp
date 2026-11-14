#!/bin/sh
# First start: keys, the lobby's TLS certificate and /data/server.toml.
# Later starts keep every key and the player registry, and rewrite
# server.toml from the environment (so changing .env and restarting applies).
set -eu
cd /data
umask 077

ip="${MKWMP_PUBLIC_IP:-}"
case "$ip" in
  ''|*[!0-9.]*) echo "mkwmp-setup: set MKWMP_PUBLIC_IP to the IPv4 address players reach this machine at (see .env.example)" >&2; exit 1 ;;
esac
name="${MKWMP_NAME:-mkwmp}"
capacity="${MKWMP_CAPACITY:-50}"
turn_port="${MKWMP_TURN_PORT:-3478}"
case "$name" in *[!A-Za-z0-9\ _.-]*) echo "mkwmp-setup: MKWMP_NAME may use letters, digits, space, _ . -" >&2; exit 1 ;; esac
case "$turn_port" in ''|*[!0-9]*) echo "mkwmp-setup: MKWMP_TURN_PORT must be a number" >&2; exit 1 ;; esac
case "$capacity" in ''|*[!0-9]*) echo "mkwmp-setup: MKWMP_CAPACITY must be a number" >&2; exit 1 ;; esac

mkdir -p keys
if [ ! -s keys/seat-ed25519.key ]; then
  mkwmp keygen k1 > keys/keygen.txt
  sed -n 2p keys/keygen.txt > keys/seat-ed25519.key
  echo "mkwmp-setup: created the ticket signing key"
fi
[ -s keys/lobby-tokens.txt ] || openssl rand -hex 32 > keys/lobby-tokens.txt
[ -s keys/admin-token.txt ] || openssl rand -hex 32 > keys/admin-token.txt
[ -s keys/lobby-token-secret ] || openssl rand 32 > keys/lobby-token-secret

# The lobby's certificate. The game still asks for Nintendo's host names, so
# the certificate names them; it is self-signed and is its own CA. Players'
# launchers fetch it from this server and hand it to the game patch.
if [ ! -s keys/lobby-cert.pem ] || [ ! -s keys/lobby-key.pem ]; then
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 3650 \
    -subj "/CN=mkwmp lobby ($name)" \
    -addext "subjectAltName=DNS:*.lp1.t.npln.srv.nintendo.net,DNS:gamesync.npln.nintendo.net,DNS:localhost" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -keyout keys/lobby-key.pem -out keys/lobby-cert.pem 2>/dev/null
  echo "mkwmp-setup: created the lobby certificate"
fi

public_key="$(sed -n 4p keys/keygen.txt)"
cat > server.toml <<TOML
## Written by mkwmp-setup on every start: edit .env, not this file.
[server]
name = "$name"
bind = "0.0.0.0:8905"

[seats]
capacity = $capacity
bind = "${MKWMP_SEAT_BIND:-0.0.0.0:7000}"
lobby_endpoint = "$ip:8905"
lobby_tokens_file = "keys/lobby-tokens.txt"

[registry]
file = "registry.db"
reserved_names = []
expire_days = 180
admin_token_file = "keys/admin-token.txt"

[ticket]
kid = "k1"
private_key_file = "keys/seat-ed25519.key"
public_keys = ["$public_key"]

[lobby]
host = "0.0.0.0"
public_host = "$ip"
turn_port = $turn_port
relay_ports = "49152:49407"
require_ticket = true
roster_url = "http://127.0.0.1:7000"
roster_token_file = "keys/lobby-tokens.txt"
token_secret_file = "keys/lobby-token-secret"

[tls]
cert = "keys/lobby-cert.pem"
key = "keys/lobby-key.pem"
TOML
url="http://$ip:7000"
[ -z "${MKWMP_DOMAIN:-}" ] || url="https://$MKWMP_DOMAIN"
echo "mkwmp-setup: ready: players set the launcher SERVER to $url"
