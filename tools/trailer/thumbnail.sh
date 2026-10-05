#!/usr/bin/env bash
# thumbnail.sh [clip] [seconds] [gfx/menu dir] - the trailer's YouTube thumbnail into
# build/trailer/thumbnail.png (1280 x 720): a frame of a recorded clip (default the patience bash,
# the crowd going through the pillar), its colours lifted, with the title and the "VR" lemming
# (thumbnail.swift on top of tools/art/make-art.swift's drawing code).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
B="$ROOT/build/trailer"
clip="${1:-$B/clips/04-patience-bash.mp4}"; at="${2:-2.9}"; menu="${3:-$ROOT/build/art-menu}"
tmp="$(mktemp -d)"
ffmpeg -v error -y -ss "$at" -i "$clip" -frames:v 1 -vf "eq=saturation=1.3:contrast=1.08:brightness=0.02" "$tmp/frame.png"
sed '/^\/\/ ---- the boot splash/,$d' "$ROOT/tools/art/make-art.swift" > "$tmp/thumbnail.swift"
cat "$ROOT/tools/trailer/thumbnail.swift" >> "$tmp/thumbnail.swift"
THUMB_FRAME="$tmp/frame.png" swift "$tmp/thumbnail.swift" "$menu" "$menu" "$B"  # (the styles dir unused, as in cards.sh)
