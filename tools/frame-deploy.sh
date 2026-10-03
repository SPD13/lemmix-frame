#!/usr/bin/env bash
# frame-deploy.sh [play|probe|benchmark] - copies build/app/linux-arm64 to the Steam Frame over
# ssh, registers it with the Frame's Steam client as the Devkit Client's Title Upload does, and
# sets what the next launch runs.
#   FRAME_HOST  the Frame's address (default frame.local)
#   FRAME_USER  its user (default steamos)
#   FRAME_KEY   an ssh key to use (default: ~/.config/steamos-devkit/devkit_rsa if it exists,
#               else ssh's own keys)
# The build lands in ~/devkit-game/lemmix, the Devkit Client's folder; the title "lemmix" runs it
# in Steam Linux Runtime 4 ARM64 (as the Frame's other devkit titles do). Its command line is
# ~/devkit-game/lemmix-argv.json, read at each launch: the mode rewrites it, so the same title
# runs the game, the probe (-- --probe) or the benchmark (-- --benchmark). Launch it in the
# headset from the library (Non-Steam / Devkit games).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${FRAME_HOST:-frame.local}"
USER_="${FRAME_USER:-steamos}"
KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"
SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=10)
[ -f "$KEY" ] && SSH_OPTS+=(-i "$KEY")
SSH="ssh ${SSH_OPTS[*]}"
mode="${1:-play}"
# FRAME_GODOT_ARGS: engine arguments before the app's (e.g. --verbose), as a JSON list's items
g="${FRAME_GODOT_ARGS:+, $FRAME_GODOT_ARGS}"
case "$mode" in
  play) argv='["Lemmix.arm64"'"$g"']' ;;
  probe) argv='["Lemmix.arm64"'"$g"', "--", "--probe"]' ;;
  benchmark) argv='["Lemmix.arm64"'"$g"', "--", "--benchmark"]' ;;
  *) echo "usage: $0 [play|probe|benchmark]" >&2; exit 2 ;;
esac
[ -x "$ROOT/build/app/linux-arm64/Lemmix.arm64" ] || { echo "no build: make export-linux first" >&2; exit 1; }
rsync -az --delete -e "$SSH" "$ROOT/build/app/linux-arm64/" "$USER_@$HOST:devkit-game/lemmix/"
echo "deployed to $USER_@$HOST:~/devkit-game/lemmix"
parms=$(printf '{"gameid": "lemmix", "directory": "/home/%s/devkit-game/lemmix", "argv": %s, "env": {}, "force_appid": "", "settings": {"steam_play": "0", "compat_tool": "SteamLinuxRuntime_4-arm64"}}' "$USER_" "$argv")
$SSH "$USER_@$HOST" "python3 ~/devkit-utils/steam-client-create-shortcut --parms '$parms'"
echo "next launch of lemmix: $mode"
