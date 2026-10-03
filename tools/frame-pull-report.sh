#!/usr/bin/env bash
# Pulls the reports the app writes on the Frame (probe.json, perf.json, qa.json, controller-models.json) into build/frame/.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${FRAME_HOST:-frame.local}"; USER_="${FRAME_USER:-steamos}"
KEY="${FRAME_KEY:-$HOME/.config/lemmix-frame/frame_rsa}"
[ -f "$KEY" ] || [ -n "${FRAME_KEY:-}" ] || KEY="$HOME/.config/steamos-devkit/devkit_rsa"
SSH="ssh -o ConnectTimeout=10"; [ -f "$KEY" ] && SSH="$SSH -i $KEY"
mkdir -p "$ROOT/build/frame"
rsync -az -e "$SSH" "$USER_@$HOST:.local/share/godot/app_userdata/Lemmix/*.json" "$ROOT/build/frame/" && ls -la "$ROOT/build/frame"
