#!/usr/bin/env bash
# frame-install.sh - runs ON the Steam Frame, as its user (steamos). Installs or updates Lemmix in
# ~/devkit-game/lemmix and registers it with the Frame's Steam client as a devkit title, as the
# SteamOS Devkit Client's Title Upload does, but without that client: the Steam commands below are
# the ones its devkit-utils send (devkit-1 IPC on ~/.steam/steam.pipe). The computer-side
# installers (install.sh, install.ps1) run it over ssh; it can also be run by hand on the Frame.
#
#   frame-install.sh [--url URL | --file TARBALL]   install or update (default: the latest release)
#   frame-install.sh --register [--argv JSON]       register only, the build already in place
#                                                   (tools/frame-deploy.sh; JSON a list, default
#                                                   ["Lemmix.arm64"])
#   frame-install.sh --uninstall [--purge]          remove the title; --purge also deletes the
#                                                   game data the app downloaded (levels, styles,
#                                                   settings, replays)
# LEMMIX_RELEASE_URL overrides where releases are downloaded from.
set -euo pipefail

GAMEID=lemmix
BASE_URL="${LEMMIX_RELEASE_URL:-https://github.com/SPD13/lemmix-frame/releases/latest/download}"
ASSET=lemmix-frame-linux-arm64.tar.gz
DEVKIT="$HOME/devkit-game"
DEST="$DEVKIT/$GAMEID"
DATA="$HOME/.local/share/godot/app_userdata/Lemmix"
RUNTIME=SteamLinuxRuntime_4-arm64

say() { printf '%s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

# steam <create|delete> - asks the running Steam client to add or remove the devkit title, and
# waits for its answer (Steam writes it to a response file).
steam() {
  python3 - "$1" "$GAMEID" <<'PY'
import os, sys, time, tempfile
from urllib.parse import quote_plus
action, gameid = sys.argv[1], sys.argv[2]
steam_dir = os.path.expanduser('~/.steam')
try:
    pid = int(open(os.path.join(steam_dir, 'steam.pid')).read())
    os.kill(pid, 0)
except Exception:
    print('Steam is not running on the Frame. Put the headset on (Steam starts with it) and run the installer again.')
    sys.exit(3)
with tempfile.TemporaryDirectory(prefix=action + '-shortcut') as tmp:
    response = os.path.join(tmp, 'response')
    cmd = f'{action}-shortcut?response={quote_plus(response)}&gameid={gameid}'
    token = open(os.path.join(steam_dir, 'steam.token')).read()
    with open(os.path.realpath(os.path.join(steam_dir, 'steam.pipe')), 'wb', 0) as pipe:
        pipe.write(f'devkit-1 steam://devkit-1/{token}/{cmd}\n'.encode())
    for _ in range(15):
        time.sleep(1)
        if os.path.exists(response + '.error'):
            print('Steam refused: ' + open(response + '.error').read().strip())
            sys.exit(4)
        if os.path.exists(response) and not os.path.exists(response + '.lock'):
            sys.exit(0)
    print('Steam did not answer. Is Developer Mode on? (Settings > System > Developer Mode)')
    sys.exit(5)
PY
}

register() {
  local argv="${1:-[\"Lemmix.arm64\"]}"
  printf '%s' "$argv" >"$DEVKIT/$GAMEID-argv.json"
  printf '{"steam_play": "0", "compat_tool": "%s"}' "$RUNTIME" >"$DEVKIT/$GAMEID-settings.json"
  steam create || die "the game is copied but not registered with Steam (see above)"
  say "registered with Steam as \"$GAMEID\" (runs in Steam Linux Runtime 4 ARM64)"
  [ -d "$HOME/.local/share/Steam/steamapps/common/$RUNTIME" ] ||
    say "note: Steam downloads Steam Linux Runtime 4 ARM64 at the first launch (about 430 MB)."
}

uninstall() {
  [ "$(pgrep -x Lemmix.arm64 || true)" ] && die "Lemmix is running: quit it first"
  rm -rf "$DEST" "$DEVKIT/$GAMEID-argv.json" "$DEVKIT/$GAMEID-settings.json" "$DEVKIT/$GAMEID-env.json"
  steam delete >/dev/null || say "note: Steam did not confirm; the title disappears at its next restart"
  say "Lemmix removed from the Frame."
  if [ "$1" = 1 ]; then
    rm -rf "$DATA"
    say "Its game data (levels, styles, settings, replays) is deleted too."
  elif [ -d "$DATA" ]; then
    say "Its game data stays in $DATA (--purge deletes it)."
  fi
}

install() {
  local url="$1" file="$2" tmp="$DEVKIT/.$GAMEID-install"
  rm -rf "$tmp"; mkdir -p "$tmp"
  # shellcheck disable=SC2064 # $tmp is local: expanded now, on purpose
  trap "rm -rf '$tmp'" EXIT
  if [ -z "$file" ]; then
    say "downloading $url"
    curl -fL --progress-bar -o "$tmp/$ASSET" "$url" || die "download failed: $url"
    if curl -fsL -o "$tmp/$ASSET.sha256" "$url.sha256"; then
      (cd "$tmp" && echo "$(cut -d' ' -f1 "$ASSET.sha256")  $ASSET" | sha256sum -c --quiet -) ||
        die "the download is damaged (checksum mismatch): run the installer again"
      say "checksum ok"
    else
      say "note: no checksum published next to it, not verified"
    fi
    file="$tmp/$ASSET"
  fi
  [ -f "$file" ] || die "no such file: $file"
  mkdir -p "$tmp/new"
  tar -xzf "$file" -C "$tmp/new" || die "cannot unpack $file"
  [ -x "$tmp/new/$GAMEID/Lemmix.arm64" ] || die "$file is not a Lemmix for Steam Frame package"
  local old="" new
  [ -f "$DEST/VERSION" ] && old=$(cat "$DEST/VERSION")
  new=$(cat "$tmp/new/$GAMEID/VERSION" 2>/dev/null || echo unknown)
  # The swap: a running Lemmix keeps its open files and picks up the new version at its next start.
  rm -rf "$DEST.old"
  [ -d "$DEST" ] && mv "$DEST" "$DEST.old"
  mv "$tmp/new/$GAMEID" "$DEST"
  rm -rf "$DEST.old"
  if [ -n "$old" ] && [ "$old" != "$new" ]; then say "updated Lemmix $old -> $new"
  else say "installed Lemmix $new in $DEST"; fi
  register
  say ""
  say "Done. In the headset, open your Library: Lemmix is listed as \"$GAMEID\" (non-Steam / devkit games)."
  [ -d "$DATA/assets/neolemmix" ] ||
    say "At its first start Lemmix opens its setup page to download NeoLemmix and the level packs (about 120 MB)."
}

[ "$(uname -m)" = aarch64 ] || die "this runs on the Steam Frame (aarch64), not on $(uname -sm)"
mode=install url="$BASE_URL/$ASSET" file="" argv="" purge=0
while [ $# -gt 0 ]; do
  case "$1" in
    --url) url="$2"; shift 2 ;;
    --file) file="$2"; shift 2 ;;
    --register) mode=register; shift ;;
    --argv) argv="$2"; shift 2 ;;
    --uninstall) mode=uninstall; shift ;;
    --purge) purge=1; shift ;;
    *) die "unknown option $1 (see the top of this script)" ;;
  esac
done
mkdir -p "$DEVKIT"
case "$mode" in
  install) install "$url" "$file" ;;
  register) [ -x "$DEST/Lemmix.arm64" ] || die "no build in $DEST"; register "$argv" ;;
  uninstall) uninstall "$purge" ;;
esac
