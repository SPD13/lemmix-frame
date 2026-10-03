#!/usr/bin/env bash
# install.sh - installs or updates Lemmix on a Steam Frame, from a Mac or a Linux computer on the
# same network. The Frame needs Developer Mode on; nothing else is needed on the computer than
# what macOS and Linux have (bash, curl, ssh).
#
#   curl -fsSL https://github.com/SPD13/lemmix-frame/releases/latest/download/install.sh | bash
#   bash install.sh [--host ADDRESS] [--file lemmix-frame-linux-arm64.tar.gz] [--uninstall [--purge]]
#
# What it does:
#   1. finds the Frame on the network (the SteamOS devkit service it announces in Developer Mode),
#      or asks for its address;
#   2. the first time, pairs this computer with it: an ssh key made for the installer
#      (~/.config/lemmix-frame/frame_rsa) is sent to the Frame, which asks in the headset whether
#      to allow it, the same request the SteamOS Devkit Client sends;
#   3. over ssh, runs frame-install.sh on the Frame, which downloads the release there, installs it
#      in ~/devkit-game/lemmix and adds "lemmix" to the Steam library.
# Run it again to update. The game data the app downloads (levels, styles, settings) is kept.
#   --host       the Frame's name or IP address (default: found on the network, else frame.local)
#   --file       install this package instead of downloading the latest release
#   --uninstall  remove Lemmix from the Frame (--purge: its downloaded game data too)
# Environment: FRAME_HOST, FRAME_USER, FRAME_KEY (an ssh key that already works),
# LEMMIX_RELEASE_URL (where releases are downloaded from).
set -euo pipefail

BASE_URL="${LEMMIX_RELEASE_URL:-https://github.com/SPD13/lemmix-frame/releases/latest/download}"
ASSET=lemmix-frame-linux-arm64.tar.gz
NEW_KEY="$HOME/.config/lemmix-frame/frame_rsa"
DEVKIT_KEY="$HOME/.config/steamos-devkit/devkit_rsa"
SERVICE_PORT=32000
HERE=""
if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd); fi

say() { printf '%s\n' "$*"; }
step() { printf '\n\033[1m%s\033[0m\n' "$*"; }
die() { printf '\nerror: %s\n' "$*" >&2; exit 1; }
# ask PROMPT DEFAULT - reads from the terminal (stdin is the script itself under curl | bash)
ask() {
  local reply=""
  if [ -r /dev/tty ]; then read -r -p "$1" reply </dev/tty || true; fi
  printf '%s' "${reply:-$2}"
}

# discover - prints the host names of the Frames (SteamOS devkits) announced on the network
discover() {
  if command -v dns-sd >/dev/null; then # macOS
    local names name
    names=$( { dns-sd -B _steamos-devkit._tcp local. & p=$!; sleep 3; kill $p; } 2>/dev/null |
      awk '$2 == "Add" { $1=$2=$3=$4=$5=$6=""; sub(/^ +/, ""); print }' | sort -u)
    while IFS= read -r name; do
      [ -n "$name" ] || continue
      { dns-sd -L "$name" _steamos-devkit._tcp local. & p=$!; sleep 2; kill $p; } 2>/dev/null |
        sed -n 's/.*can be reached at \([^:]*\)\.:.*/\1/p' | head -1
    done <<<"$names"
  elif command -v avahi-browse >/dev/null; then # Linux
    avahi-browse -rpt _steamos-devkit._tcp 2>/dev/null | awk -F';' '$1 == "=" && $3 == "IPv4" { print $7 }'
  fi | sort -u
}

find_frame() {
  [ -n "$HOST" ] && return
  step "Looking for your Steam Frame on the network..."
  local found n
  found=$(discover || true)
  n=$(printf '%s' "$found" | grep -c . || true)
  if [ "$n" = 1 ]; then
    HOST=$found; say "found $HOST"
  elif [ "$n" -gt 1 ]; then
    say "found several:"; say "$found"
    HOST=$(ask "Which one? [$(head -1 <<<"$found")] " "$(head -1 <<<"$found")")
  else
    say "not found automatically (Developer Mode off, another network, or no mDNS here)."
    HOST=$(ask "The Frame's name or IP address [frame.local]: " frame.local)
  fi
}

ssh_with() { # KEY ARGS... - ssh with that key only (KEY empty: ssh's own keys)
  local key="$1"; shift
  ssh -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new ${key:+-i "$key" -o IdentitiesOnly=yes} "$@"
}

find_access() {
  local key err
  for key in "${FRAME_KEY:-}" "$NEW_KEY" "$DEVKIT_KEY" ""; do
    if [ -n "$key" ] && [ ! -f "$key" ]; then continue; fi
    if err=$(ssh_with "$key" -n -o BatchMode=yes "$USER_@$HOST" true 2>&1 >/dev/null); then KEY=$key; return 0; fi
    case "$err" in *"timed out"*|*refused*|*resolve*|*"No route"*|*unreachable*) return 1 ;; esac # no ssh there at all
  done
  return 1
}

pair() {
  step "Pairing this computer with the Frame (once)"
  curl -fsS --max-time 5 "http://$HOST:$SERVICE_PORT/properties.json" >/dev/null 2>&1 ||
    die "the Frame at $HOST does not answer on port $SERVICE_PORT.
Check that it is on, on the same network as this computer, and that Developer Mode is on
(Settings > System > Developer Mode). See the README's install section."
  if [ ! -f "$NEW_KEY" ]; then
    mkdir -p "$(dirname "$NEW_KEY")"
    ssh-keygen -q -t rsa -b 3072 -N "" -C "lemmix-installer@$(hostname -s | tr -c 'A-Za-z0-9._\n-' -)" -f "$NEW_KEY"
  fi
  say "The Frame will ask, in the headset, whether to allow this computer (\"Development host at IP ...\")."
  say "It must be accepted within 30 seconds, from Steam's menus (not in a game)."
  ask "Press Enter, then put the headset on and accept... " "" >/dev/null
  say "sending the pairing request..."
  local reply code
  reply=$(curl -sS --max-time 60 -w '\n%{http_code}' -X POST --data-binary @"$NEW_KEY.pub" \
    "http://$HOST:$SERVICE_PORT/register" 2>&1) || true
  code=$(tail -1 <<<"$reply")
  [ "$code" = 200 ] || die "pairing failed (HTTP $code): $(sed '$d' <<<"$reply" | tr -d '\n' | tail -c 300)
Declined, or no answer within 30 seconds: run the installer again and accept in the headset."
  say "paired"
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if ssh_with "$NEW_KEY" -n -o BatchMode=yes "$USER_@$HOST" true 2>/dev/null; then KEY=$NEW_KEY; return; fi
    sleep 2
  done
  die "paired, but ssh to $USER_@$HOST does not work. Try again in a minute."
}

# on_frame ARGS... - runs frame-install.sh on the Frame (this copy if it sits next to this script,
# else the release's)
on_frame() {
  local args; args=$(printf ' %q' "$@")
  if [ -n "$HERE" ] && [ -f "$HERE/frame-install.sh" ]; then
    ssh_with "$KEY" "$USER_@$HOST" "bash -s --$args" <"$HERE/frame-install.sh"
  else
    ssh_with "$KEY" -n "$USER_@$HOST" "set -o pipefail; curl -fsSL '$BASE_URL/frame-install.sh' | tr -d '\r' | bash -s --$args"
  fi
}

main() {
  HOST="${FRAME_HOST:-}" FILE="" MODE=install PURGE=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --host) HOST="$2"; shift 2 ;;
      --file) FILE="$2"; shift 2 ;;
      --uninstall) MODE=uninstall; shift ;;
      --purge) PURGE=--purge; shift ;;
      -h|--help) sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
      *) die "unknown option $1 (--help lists them)" ;;
    esac
  done
  [ -z "$FILE" ] || [ -f "$FILE" ] || die "no such file: $FILE"
  command -v ssh >/dev/null && command -v curl >/dev/null || die "this needs ssh and curl"

  say "Lemmix for Steam Frame - installer"
  find_frame
  HOST=${HOST%.}
  USER_=${FRAME_USER:-$(curl -fsS --max-time 5 "http://$HOST:$SERVICE_PORT/login-name" 2>/dev/null || echo steamos)}
  KEY=""
  step "Connecting to $USER_@$HOST"
  if find_access; then say "ssh access ok${KEY:+ (key $KEY)}"; else pair; fi

  if [ "$MODE" = uninstall ]; then
    step "Removing Lemmix from the Frame"
    on_frame --uninstall $PURGE
  elif [ -n "$FILE" ]; then
    step "Uploading $(basename "$FILE")"
    ssh_with "$KEY" -n "$USER_@$HOST" "mkdir -p devkit-game"
    scp -o ConnectTimeout=10 ${KEY:+-i "$KEY" -o IdentitiesOnly=yes} "$FILE" "$USER_@$HOST:devkit-game/.lemmix-upload.tar.gz"
    step "Installing on the Frame"
    on_frame --file devkit-game/.lemmix-upload.tar.gz || { ssh_with "$KEY" -n "$USER_@$HOST" "rm -f devkit-game/.lemmix-upload.tar.gz"; exit 1; }
    ssh_with "$KEY" -n "$USER_@$HOST" "rm -f devkit-game/.lemmix-upload.tar.gz"
  else
    step "Installing on the Frame (it downloads the latest release itself)"
    on_frame --url "$BASE_URL/$ASSET"
  fi
}

main "$@"
