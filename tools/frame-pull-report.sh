#!/usr/bin/env bash
# Pulls the reports the app writes on the Frame (probe.json, perf.json, qa.json) into build/frame/.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
: "${FRAME_HOST:?set FRAME_HOST}"; : "${FRAME_USER:?set FRAME_USER}"
KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"
mkdir -p "$ROOT/build/frame"
rsync -az -e "ssh -i $KEY" "$FRAME_USER@$FRAME_HOST:.local/share/godot/app_userdata/Lemmix/*.json" "$ROOT/build/frame/" && ls -la "$ROOT/build/frame"
