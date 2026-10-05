#!/usr/bin/env bash
# cards.sh <gfx/menu dir> <out dir> - the trailer's captions and end card (cards.swift on top of
# tools/art/make-art.swift's drawing code, its own pictures left out).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
src="$(mktemp -d)/cards.swift"
sed '/^\/\/ ---- the boot splash/,$d' "$ROOT/tools/art/make-art.swift" > "$src"
cat "$ROOT/tools/trailer/cards.swift" >> "$src"
mkdir -p "$2"
# (make-art's styles dir: only its hero reads it, and that is cut off, so the menu dir stands in)
swift "$src" "$1" "$1" "$(cd "$2" && pwd)"
