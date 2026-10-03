#!/usr/bin/env bash
# frame-capture.sh [out-dir] - a picture of what the player sees on the Frame: asks the running app
# (DeviceCapture: user://capture.request), waits for the PNG and copies it to out-dir
# (build/frame/captures by default). FRAME_HOST / FRAME_USER / FRAME_KEY as frame-deploy.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${FRAME_HOST:-frame.local}"; USER_="${FRAME_USER:-steamos}"
KEY="${FRAME_KEY:-$HOME/.config/lemmix-frame/frame_rsa}"
[ -f "$KEY" ] || [ -n "${FRAME_KEY:-}" ] || KEY="$HOME/.config/steamos-devkit/devkit_rsa"
SSH="ssh -o ConnectTimeout=10"; [ -f "$KEY" ] && SSH="$SSH -i $KEY"
OUT="${1:-$ROOT/build/frame/captures}"
D=.local/share/godot/app_userdata/Lemmix
mkdir -p "$OUT"
last=$($SSH "$USER_@$HOST" "ls -t $D/capture-*.png 2>/dev/null | head -1" || true)
$SSH "$USER_@$HOST" "touch $D/capture.request"
for _ in $(seq 1 40); do
  sleep 0.5
  now=$($SSH "$USER_@$HOST" "[ -e $D/capture.request ] || ls -t $D/capture-*.png 2>/dev/null | head -1" || true)
  if [ -n "$now" ] && [ "$now" != "$last" ]; then
    sleep 1
    scp ${KEY:+$( [ -f "$KEY" ] && echo -i "$KEY")} -q "$USER_@$HOST:$now" "$OUT/" && echo "$OUT/$(basename "$now")"
    exit 0
  fi
done
echo "no capture: is lemmix running?" >&2; exit 1
