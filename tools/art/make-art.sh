#!/usr/bin/env bash
# make-art.sh [neolemmix dir] - the app's own artwork (tools/art/make-art.swift: the boot splash,
# the lobby's subtitle, the Steam library's capsule, header, hero and logo) from a NeoLemmix
# install's gfx/menu and styles (default: the Frame's install, the files it needs pulled over ssh
# into build/art-neolemmix; the newer releases' logo has no subtitle under it and their
# background is the dirt).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
nl="${1:-}"
if [ -z "$nl" ]; then
  nl="$ROOT/build/art-neolemmix"; mkdir -p "$nl"
  KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"; opts=(-o ConnectTimeout=10); [ -f "$KEY" ] && opts+=(-i "$KEY")
  ssh "${opts[@]}" "${FRAME_USER:-steamos}@${FRAME_HOST:-frame.local}" \
    'cd ~/.local/share/godot/app_userdata/Lemmix/assets/neolemmix && tar -c gfx/menu/logo.png gfx/menu/background.png gfx/menu/sign_group.png styles/default/lemmings styles/orig_dirt/terrain styles/orig_dirt/objects' \
    | tar -x -C "$nl"
fi
swift "$ROOT/tools/art/make-art.swift" "$nl/gfx/menu" "$nl/styles" "$ROOT"
