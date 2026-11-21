#!/usr/bin/env bash
# deploy-mkwmp-server.sh: turn a fresh Linux VM into an MKWMP server ("node"), or upgrade one.
#
#   curl -fsSL https://anarqz.github.io/mkwmp/deploy-mkwmp-server.sh | sudo bash
#   curl -fsSL https://anarqz.github.io/mkwmp/deploy-mkwmp-server.sh | sudo bash -s -- --name "Friday kart" --region eu-west
#
# What it does (docs/HOSTING.md has the long version):
#   1. downloads mkwmp-server-x86_64-linux from the latest MKWMP release and checks it against the
#      release's SHA256SUMS, whose Ed25519 signature must verify with the release key below;
#   2. creates the `mkwmp` user and its data folder /var/lib/mkwmp (keys, registry.db, config);
#   3. writes the config: server name, region, seats, the privacy policy, and [announce], so the
#      server lists itself in the public server directory (https://beholder.anarqz.dev);
#   4. installs two systemd services, mkwmp-seat (launchers, HTTP) and mkwmp-lobby (the game);
#   5. opens the ports in ufw (SSH stays open); with --domain, puts Caddy (automatic HTTPS) in front;
#   6. checks that GET /ping answers and that the directory lists the server.
# Running it again upgrades to the latest release and keeps your data and settings.
#
# Needs: Ubuntu 22.04+ or Debian 12+ (x86_64, systemd), run as root. Open the same ports in your
# cloud provider's firewall too (the script prints them).
set -euo pipefail

REPO="anarqz/mkwmp"
# The public half of the MKWMP release key (rust/client/src/update.rs RELEASE_KEYS), base64url.
RELEASE_KEY="Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw"
DEFAULT_BEHOLDER="https://beholder.anarqz.dev"
PREFIX=/opt/mkwmp
DATA=/var/lib/mkwmp
SVC_USER=mkwmp
STATE="$DATA/deploy.env"          # the settings of the last run, reused by the next
UNITS="mkwmp-seat mkwmp-lobby"
TCP_PORTS="8905 8906"             # NPLN, Gamesync (the game); 7000 (seat API) unless --domain
UDP_PORTS="3478 8907 10025"       # TURN relay, voice/camera relay, NAT check
UDP_RANGE="49152:49407"           # TURN relay sockets

usage() {
  cat <<'EOF'
usage: deploy-mkwmp-server.sh [options]          install or upgrade
       deploy-mkwmp-server.sh --uninstall [--purge]

  --name NAME         server name in the launcher and the server list (1-48 characters)
  --region REGION     short region tag for the server list, e.g. sa-east, eu-west (A-Z a-z 0-9 -)
  --public-ip IPV4    the address players reach this VM at (default: this VM's public address)
  --domain NAME       serve the seat API over HTTPS at https://NAME with Caddy (point NAME's
                      DNS A record at this VM first); default: http://<public IP>:7000
  --capacity N        players seated at once; the rest queue (default 50)
  --privacy-file F    your privacy policy (Markdown); default: a template to edit
  --no-announce       don't list this server in the public server directory
  --beholder URL      list it in another directory (default https://beholder.anarqz.dev)
  --version TAG       install this release (e.g. v1.3.1) instead of the latest
  --no-firewall       leave ufw alone (open the ports yourself)
  --yes               don't ask; use the flags, the last run's settings, or the defaults
  --uninstall         stop and remove the services, the program and the ufw rules;
                      keeps /var/lib/mkwmp (gamertags, keys) unless --purge
EOF
}

say()  { printf '\033[1;36m==\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

# ---- arguments --------------------------------------------------------------------------------
NAME="" REGION="" PUBLIC_IP="" DOMAIN="" CAPACITY="" PRIVACY_FILE="" BEHOLDER="" VERSION=""
ANNOUNCE="" FIREWALL=yes ASSUME_YES=no UNINSTALL=no PURGE=no
while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME="${2:?--name needs a value}"; shift ;;
    --region) REGION="${2:?--region needs a value}"; shift ;;
    --public-ip) PUBLIC_IP="${2:?--public-ip needs a value}"; shift ;;
    --domain) DOMAIN="${2:?--domain needs a value}"; shift ;;
    --capacity) CAPACITY="${2:?--capacity needs a value}"; shift ;;
    --privacy-file) PRIVACY_FILE="${2:?--privacy-file needs a value}"; shift ;;
    --no-announce) ANNOUNCE=no ;;
    --beholder) BEHOLDER="${2:?--beholder needs a value}"; shift ;;
    --version) VERSION="${2:?--version needs a value}"; shift ;;
    --no-firewall) FIREWALL=no ;;
    --yes|-y) ASSUME_YES=yes ;;
    --uninstall) UNINSTALL=yes ;;
    --purge) PURGE=yes ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown option $1" ;;
  esac
  shift
done

[ "$(id -u)" -eq 0 ] || die "run as root: curl -fsSL ... | sudo bash"
command -v systemctl >/dev/null || die "this needs systemd"
[ "$(uname -m)" = x86_64 ] || die "the server binary is x86_64 only (this is $(uname -m))"

# ---- uninstall --------------------------------------------------------------------------------
if [ "$UNINSTALL" = yes ]; then
  say "stopping and removing the services"
  for u in $UNITS; do
    systemctl disable --now "$u" 2>/dev/null || true
    rm -f "/etc/systemd/system/$u.service"
  done
  systemctl daemon-reload
  rm -rf "$PREFIX"
  if [ -f /etc/caddy/Caddyfile ] && grep -q "mkwmp" /etc/caddy/Caddyfile; then
    rm -f /etc/caddy/Caddyfile && systemctl restart caddy 2>/dev/null || true
    warn "Caddy's MKWMP site removed (the caddy package stays installed)"
  fi
  if command -v ufw >/dev/null; then
    for p in 7000 $TCP_PORTS 80 443; do ufw delete allow "$p/tcp" >/dev/null 2>&1 || true; done
    for p in $UDP_PORTS; do ufw delete allow "$p/udp" >/dev/null 2>&1 || true; done
    ufw delete allow "$UDP_RANGE/udp" >/dev/null 2>&1 || true
  fi
  rm -f /etc/systemd/journald.conf.d/mkwmp-retention.conf
  if [ "$PURGE" = yes ]; then
    rm -rf "$DATA"
    userdel "$SVC_USER" 2>/dev/null || true
    say "removed, data included"
  else
    say "removed; your data (gamertags, friends, keys) is still in $DATA: --uninstall --purge deletes it"
  fi
  exit 0
fi

# ---- settings: flags, else the last run's, else ask, else defaults ----------------------------
LAST_NAME="" LAST_REGION="" LAST_PUBLIC_IP="" LAST_DOMAIN="" LAST_CAPACITY="" LAST_ANNOUNCE="" LAST_BEHOLDER=""
if [ -f "$STATE" ]; then
  # shellcheck disable=SC1090
  . "$STATE"
fi
TTY=no
if [ "$ASSUME_YES" = no ] && { : </dev/tty; } 2>/dev/null; then TTY=yes; fi
ask() { # ask VAR "question" default
  local var="$1" q="$2" def="$3" ans=""
  if [ -n "${!var}" ]; then return; fi
  if [ "$TTY" = yes ]; then
    printf '%s [%s]: ' "$q" "$def" >/dev/tty
    IFS= read -r ans </dev/tty || true
  fi
  printf -v "$var" '%s' "${ans:-$def}"
}

detect_ip() {
  # The source address of the default route: on Vultr and DigitalOcean that is the public IPv4.
  ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -1
}
is_public_ipv4() {
  local ip="$1" a b
  [[ "$ip" =~ ^([0-9]{1,3})\.([0-9]{1,3})\.[0-9]{1,3}\.[0-9]{1,3}$ ]] || return 1
  a="${BASH_REMATCH[1]}" b="${BASH_REMATCH[2]}"
  [ "$a" -le 255 ] || return 1
  ! { [ "$a" = 10 ] || [ "$a" = 127 ] || [ "$a" = 0 ] || { [ "$a" = 172 ] && [ "$b" -ge 16 ] && [ "$b" -le 31 ]; } ||
      { [ "$a" = 192 ] && [ "$b" = 168 ]; } || { [ "$a" = 169 ] && [ "$b" = 254 ]; } || { [ "$a" = 100 ] && [ "$b" -ge 64 ] && [ "$b" -le 127 ]; }; }
}

ask NAME "Server name (shown in the launcher's server list)" "${LAST_NAME:-$(hostname -s)}"
ask REGION "Region tag (e.g. sa-east, us-east, eu-west; may be empty)" "${LAST_REGION:-}"
ask PUBLIC_IP "Public IPv4 address players reach this VM at" "${LAST_PUBLIC_IP:-$(detect_ip)}"
DOMAIN="${DOMAIN:-$LAST_DOMAIN}"
CAPACITY="${CAPACITY:-${LAST_CAPACITY:-50}}"
ANNOUNCE="${ANNOUNCE:-${LAST_ANNOUNCE:-yes}}"
BEHOLDER="${BEHOLDER:-${LAST_BEHOLDER:-$DEFAULT_BEHOLDER}}"
BEHOLDER="${BEHOLDER%/}"

# The same rules the directory applies (docs/contracts/BEHOLDER_API.md §2), checked up front.
[ -n "$NAME" ] && [ "${#NAME}" -le 48 ] || die "--name must be 1-48 characters"
[[ "$NAME" != *'"'* && "$NAME" != *'\'* && "$NAME" == "${NAME#[[:space:]]}" && "$NAME" == "${NAME%[[:space:]]}" ]] ||
  die "--name can't contain quotes or backslashes, or start or end with a space"
[[ "$REGION" =~ ^[A-Za-z0-9-]{0,16}$ ]] || die "--region: up to 16 of A-Z a-z 0-9 -"
[[ "$CAPACITY" =~ ^[0-9]+$ ]] && [ "$CAPACITY" -ge 1 ] && [ "$CAPACITY" -le 100000 ] || die "--capacity: 1-100000"
[[ "$PUBLIC_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "--public-ip: an IPv4 address"
if ! is_public_ipv4 "$PUBLIC_IP"; then
  [ "$ANNOUNCE" = yes ] && die "$PUBLIC_IP is not a public address: players on the internet can't reach it. Pass --public-ip, or --no-announce for a LAN-only server"
  warn "$PUBLIC_IP is not a public address: only players on the same network can join"
fi
if [ -n "$DOMAIN" ]; then
  [[ "$DOMAIN" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$ ]] || die "--domain: a DNS name like mkw.example.net"
  ENDPOINT="https://$DOMAIN"
  SEAT_BIND="127.0.0.1:7000"
else
  ENDPOINT="http://$PUBLIC_IP:7000"
  SEAT_BIND="0.0.0.0:7000"
fi
[[ "$BEHOLDER" =~ ^https?://[^[:space:]]+$ ]] || die "--beholder: an http(s) URL"
[ -z "$PRIVACY_FILE" ] || [ -s "$PRIVACY_FILE" ] || die "--privacy-file: $PRIVACY_FILE is missing or empty"

say "server \"$NAME\"${REGION:+ ($REGION)} at $ENDPOINT, $CAPACITY seats, listed: $ANNOUNCE"

# ---- packages ---------------------------------------------------------------------------------
export DEBIAN_FRONTEND=noninteractive
need=""
for c in curl openssl ufw; do command -v "$c" >/dev/null || need="$need $c"; done
command -v ip >/dev/null || need="$need iproute2"
if [ -n "$need" ]; then
  say "installing:$need"
  apt-get update -qq && apt-get install -y -qq $need ca-certificates >/dev/null
fi
openssl version | grep -qE '^OpenSSL [3-9]' || die "needs OpenSSL 3 (Ubuntu 22.04+ or Debian 12+) to check the release signature"

# ---- the release: download and verify ---------------------------------------------------------
if [ -z "$VERSION" ]; then
  VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)"
  [ -n "$VERSION" ] || die "can't read the latest release from GitHub"
fi
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
b64url_decode() { local s; s="$(printf %s "$1" | tr -- '-_' '+/')"; while [ $(( ${#s} % 4 )) -ne 0 ]; do s="$s="; done; printf %s "$s" | base64 -d; }

say "downloading MKWMP $VERSION"
base="https://github.com/$REPO/releases/download/$VERSION"
for f in SHA256SUMS SHA256SUMS.sig mkwmp-server-x86_64-linux; do
  curl -fsSL --retry 3 -o "$WORK/$f" "$base/$f" || die "download failed: $base/$f"
done
{ printf '\x30\x2a\x30\x05\x06\x03\x2b\x65\x70\x03\x21\x00'; b64url_decode "$RELEASE_KEY"; } > "$WORK/release.der"
openssl pkey -pubin -inform DER -in "$WORK/release.der" -out "$WORK/release.pem" 2>/dev/null || die "bad release key"
b64url_decode "$(tr -d '\r\n' < "$WORK/SHA256SUMS.sig")" > "$WORK/sig.bin"
openssl pkeyutl -verify -pubin -inkey "$WORK/release.pem" -rawin -in "$WORK/SHA256SUMS" -sigfile "$WORK/sig.bin" >/dev/null 2>&1 ||
  die "SHA256SUMS signature does NOT verify: not installing"
[ "$(head -1 "$WORK/SHA256SUMS")" = "# mkwmp ${VERSION#v}" ] || die "SHA256SUMS is not for $VERSION: not installing"
want="$(awk '$2 == "mkwmp-server-x86_64-linux" {print $1}' "$WORK/SHA256SUMS")"
got="$(sha256sum "$WORK/mkwmp-server-x86_64-linux" | cut -d' ' -f1)"
[ -n "$want" ] && [ "$want" = "$got" ] || die "mkwmp-server-x86_64-linux doesn't match the signed SHA256SUMS: not installing"
say "signature and checksum OK ($got)"

# ---- user, program, keys, config --------------------------------------------------------------
id "$SVC_USER" >/dev/null 2>&1 || useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin "$SVC_USER"
install -d -m 755 "$PREFIX/bin"
install -d -m 700 -o "$SVC_USER" -g "$SVC_USER" "$DATA"
install -m 755 "$WORK/mkwmp-server-x86_64-linux" "$PREFIX/bin/mkwmp.new" && mv -f "$PREFIX/bin/mkwmp.new" "$PREFIX/bin/mkwmp"

# Keys (ticket key, lobby tokens, the lobby's TLS certificate) are made once; server.toml is
# rewritten from the settings on every run.
runuser -u "$SVC_USER" -- "$PREFIX/bin/mkwmp" host --init-only --data "$DATA" \
  --public-ip "$PUBLIC_IP" --name "$NAME" --capacity "$CAPACITY" --seat-bind "$SEAT_BIND" >/dev/null

if [ -n "$PRIVACY_FILE" ]; then
  install -m 600 -o "$SVC_USER" -g "$SVC_USER" "$PRIVACY_FILE" "$DATA/privacy.md"
elif [ ! -s "$DATA/privacy.md" ]; then
  # A starting point; players accept whatever this says, so make it true for your server.
  cat > "$DATA/privacy.md" <<EOF
# Privacy policy of "$NAME"

This MKWMP server is run by its host, not by Nintendo and not by the MKWMP project. Edit this file
($DATA/privacy.md) to describe your server, then run the deploy script again (or restart
mkwmp-seat). Players are asked to accept it again whenever it changes.

## Who can use it

Only people 18 or older.

## What the server keeps

Your gamertag, a hash of your PIN (never the PIN itself), when you registered and last played, which
version of this policy you accepted and when, your friends and friend requests, and the reports you
file about other players' voice chat or camera. No email, real name, age or payment details.

## What it only holds while it runs

Seats and the queue, who is online, the game lobbies, and rate-limit counters keyed by IP address.

## Logs

The logs can contain your gamertag, the game's user id and your IP address. They are deleted after 7
days.

## Voice chat and camera

Voice and camera pictures are relayed live to the players in your friend room and are never recorded.

## Who sees what

Your friends see whether you're playing, online or offline. Players in your lobby see your gamertag and,
as in any online game, may see your IP address. Only the host sees reports. This server lists itself
in the public MKWMP server directory with its name, address and how many seats are taken: no player
data.

## How long it is kept

A gamertag unused for 30 days is deleted with its friends. Reports are deleted 90 days after they're
filed.

## Your choices

Deleting your account in the launcher removes your gamertag, PIN hash, friends and the reports you
filed. You can withdraw your acceptance of this policy in the launcher.

## Contact

The host of this server: (add how players can reach you).
EOF
  chown "$SVC_USER:$SVC_USER" "$DATA/privacy.md"; chmod 600 "$DATA/privacy.md"
  warn "wrote a privacy policy template to $DATA/privacy.md: edit it to describe your server"
fi

{
  grep -q '^\[privacy\]' "$DATA/server.toml" || printf '\n[privacy]\nfile = "privacy.md"\nenforce = false\n'
  if [ "$ANNOUNCE" = yes ] && ! grep -q '^\[announce\]' "$DATA/server.toml"; then
    printf '\n[announce]\nbeholder_url = "%s"\nendpoint = "%s"\nname = "%s"\nregion = "%s"\nkey_file = "keys/beholder-ed25519.key"\n' \
      "$BEHOLDER" "$ENDPOINT" "$NAME" "$REGION"
  fi
} >> "$DATA/server.toml"
chown "$SVC_USER:$SVC_USER" "$DATA/server.toml"

cat > "$STATE" <<EOF
# Written by deploy-mkwmp-server.sh: the next run reuses these.
LAST_NAME=$(printf %q "$NAME")
LAST_REGION=$(printf %q "$REGION")
LAST_PUBLIC_IP=$(printf %q "$PUBLIC_IP")
LAST_DOMAIN=$(printf %q "$DOMAIN")
LAST_CAPACITY=$(printf %q "$CAPACITY")
LAST_ANNOUNCE=$(printf %q "$ANNOUNCE")
LAST_BEHOLDER=$(printf %q "$BEHOLDER")
EOF
chmod 600 "$STATE"

# ---- services ---------------------------------------------------------------------------------
unit() { # unit NAME "Description" SUBCOMMAND
  cat > "/etc/systemd/system/$1.service" <<EOF
# Written by deploy-mkwmp-server.sh.
[Unit]
Description=$2
After=network-online.target
Wants=network-online.target

[Service]
User=$SVC_USER
WorkingDirectory=$DATA
ExecStart=$PREFIX/bin/mkwmp $3 $DATA/server.toml
Restart=always
RestartSec=2
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
StateDirectory=mkwmp
StateDirectoryMode=0700
MemoryMax=384M

[Install]
WantedBy=multi-user.target
EOF
}
unit mkwmp-seat "MKWMP seat server (launchers, HTTP)" server
unit mkwmp-lobby "MKWMP lobby (the game: NPLN, Gamesync, TURN, voice/camera relay)" lobby

# Logs hold gamertags and IP addresses: 7 days, as the policy template says.
mkdir -p /etc/systemd/journald.conf.d
printf '[Journal]\nMaxRetentionSec=7day\nMaxFileSec=1day\n' > /etc/systemd/journald.conf.d/mkwmp-retention.conf
systemctl restart systemd-journald || true

# ---- firewall ---------------------------------------------------------------------------------
if [ "$FIREWALL" = yes ]; then
  say "firewall (ufw): SSH, then the MKWMP ports"
  ufw allow OpenSSH >/dev/null 2>&1 || ufw allow 22/tcp >/dev/null
  # SSH on another port too, whichever sshd listens on, so enabling ufw never locks you out.
  for p in $(ss -tlnpH 2>/dev/null | awk '/sshd/ {n = split($4, a, ":"); print a[n]}' | sort -u); do
    ufw allow "$p/tcp" >/dev/null
  done
  if [ -n "$DOMAIN" ]; then
    ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
    ufw delete allow 7000/tcp >/dev/null 2>&1 || true
  else
    ufw allow 7000/tcp >/dev/null
  fi
  for p in $TCP_PORTS; do ufw allow "$p/tcp" >/dev/null; done
  for p in $UDP_PORTS; do ufw allow "$p/udp" >/dev/null; done
  ufw allow "$UDP_RANGE/udp" >/dev/null
  ufw --force enable >/dev/null
fi

# ---- HTTPS (optional) -------------------------------------------------------------------------
if [ -n "$DOMAIN" ]; then
  if ! command -v caddy >/dev/null; then
    say "installing Caddy (automatic HTTPS)"
    apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https gnupg >/dev/null
    curl -fsSL 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    curl -fsSL 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
    apt-get update -qq && apt-get install -y -qq caddy >/dev/null
  fi
  cat > /etc/caddy/Caddyfile <<EOF
# Written by deploy-mkwmp-server.sh (mkwmp): HTTPS in front of the seat API.
$DOMAIN {
	reverse_proxy 127.0.0.1:7000 {
		# The seat server rate-limits per player address and reads it from this header on
		# loopback; Caddy overwrites whatever a client sent.
		header_up Cf-Connecting-Ip {remote_host}
	}
}
EOF
  systemctl enable caddy >/dev/null 2>&1 || true
  systemctl restart caddy
fi

# ---- start and check --------------------------------------------------------------------------
systemctl daemon-reload
for u in $UNITS; do systemctl enable "$u" >/dev/null 2>&1; systemctl restart "$u"; done

say "waiting for the seat server"
ok=no
for _ in $(seq 1 30); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7000/ping)" = 204 ]; then ok=yes; break; fi
  sleep 1
done
[ "$ok" = yes ] || { journalctl -u mkwmp-seat -n 30 --no-pager >&2; die "the seat server didn't answer /ping (log above)"; }
systemctl is-active -q mkwmp-lobby || { journalctl -u mkwmp-lobby -n 30 --no-pager >&2; die "the lobby didn't start (log above)"; }

public_ok=no
if [ "$(curl -s -m 10 -o /dev/null -w '%{http_code}' "$ENDPOINT/ping")" = 204 ]; then public_ok=yes; fi

listed=skipped
if [ "$ANNOUNCE" = yes ]; then
  say "waiting for $BEHOLDER to list it (up to 60 s)"
  listed=no
  for _ in $(seq 1 12); do
    if curl -fsS -m 10 "$BEHOLDER/beholder/v1/servers" 2>/dev/null | grep -qF "\"endpoint\":\"$ENDPOINT\""; then listed=yes; break; fi
    sleep 5
  done
fi

echo
say "MKWMP $VERSION is running"
echo "   players: set the launcher's server to   $ENDPOINT"
[ "$public_ok" = yes ] && echo "   $ENDPOINT/ping answers from this VM" ||
  warn "$ENDPOINT/ping did not answer from this VM: check the cloud firewall${DOMAIN:+ and that $DOMAIN points at $PUBLIC_IP}"
case "$listed" in
  yes) echo "   listed in $BEHOLDER" ;;
  no) warn "not listed in $BEHOLDER yet: see 'journalctl -u mkwmp-seat | grep announce'" ;;
esac
cat <<EOF

   Open these in your cloud provider's firewall as well (inbound):
     TCP ${DOMAIN:+80, 443}${DOMAIN:-7000}, 8905, 8906
     UDP 3478, 8907, 10025, 49152-49407
   Logs:      journalctl -u mkwmp-seat -u mkwmp-lobby -f
   Back up:   $DATA (registry.db = gamertags and friends; keys/ = this server's identity)
   Policy:    $DATA/privacy.md
   Upgrade:   run this script again
EOF
