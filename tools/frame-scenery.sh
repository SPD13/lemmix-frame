#!/usr/bin/env bash
# frame-scenery.sh <style>... - copies galleries' sceneries (made by tools/scenery-gen into
# $WEB_ASSETS/3d/env/<style>/scenery/) to the Steam Frame's installed assets over ssh, where the
# app reads them (assets/3d/env/<style>/scenery/). FRAME_HOST / FRAME_USER / FRAME_KEY as
# frame-deploy.sh. `--remove <style>` takes one off the Frame again.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ASSETS="${WEB_ASSETS:-$ROOT/../LemmingsJS}"
HOST="${FRAME_HOST:-frame.local}"
USER_="${FRAME_USER:-steamos}"
KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"
SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=10)
[ -f "$KEY" ] && SSH_OPTS+=(-i "$KEY")
SSH="ssh ${SSH_OPTS[*]}"
D=.local/share/godot/app_userdata/Lemmix/assets/3d/env
[ $# -gt 0 ] || { echo "usage: $0 <style>... | --remove <style>" >&2; exit 2; }
if [ "$1" = --remove ]; then
  $SSH "$USER_@$HOST" "rm -rf '$D/$2/scenery'" && echo "removed $2's scenery from the Frame"
  exit 0
fi
for s in "$@"; do
  src="$ASSETS/3d/env/$s/scenery/"
  [ -f "$src/scenery.json" ] || { echo "no scenery for $s: tools/scenery-gen gen $s first" >&2; exit 1; }
  $SSH "$USER_@$HOST" "mkdir -p '$D/$s/scenery'"
  rsync -az --delete --exclude preview.png -e "$SSH" "$src" "$USER_@$HOST:$D/$s/scenery/"
  echo "scenery of $s on the Frame"
done
