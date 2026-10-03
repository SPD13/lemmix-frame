#!/usr/bin/env bash
# make-art.sh [gfx/menu dir] - the app's own artwork (tools/art/make-art.swift: the boot splash,
# the lobby's subtitle, the Steam library's capsule, header, hero and logo) from a NeoLemmix
# release's menu graphics (default: the Frame's install, pulled over ssh into build/art-menu;
# the newer releases' logo has no subtitle under it and their background is the dirt).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
menu="${1:-}"
if [ -z "$menu" ]; then
  menu="$ROOT/build/art-menu"; mkdir -p "$menu"
  KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"; opts=(-o ConnectTimeout=10); [ -f "$KEY" ] && opts+=(-i "$KEY")
  scp -q "${opts[@]}" "${FRAME_USER:-steamos}@${FRAME_HOST:-frame.local}:.local/share/godot/app_userdata/Lemmix/assets/neolemmix/gfx/menu/{logo,background,sign_group}.png" "$menu/"
fi
swift "$ROOT/tools/art/make-art.swift" "$menu" "$ROOT"
