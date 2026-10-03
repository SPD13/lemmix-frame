#!/usr/bin/env bash
# make-splash.sh [gfx/menu dir] - app/Splash/splash.png, the boot splash, from a NeoLemmix
# release's menu graphics (default: the Frame's install, pulled over ssh into build/splash-menu;
# the newer releases' logo has no subtitle under it and their background is the dirt).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
menu="${1:-}"
if [ -z "$menu" ]; then
  menu="$ROOT/build/splash-menu"; mkdir -p "$menu"
  KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"; opts=(-o ConnectTimeout=10); [ -f "$KEY" ] && opts+=(-i "$KEY")
  scp -q "${opts[@]}" "${FRAME_USER:-steamos}@${FRAME_HOST:-frame.local}:.local/share/godot/app_userdata/Lemmix/assets/neolemmix/gfx/menu/{logo,background}.png" "$menu/"
fi
swift "$ROOT/tools/splash/make-splash.swift" "$menu" "$ROOT/app/Splash/splash.png"
