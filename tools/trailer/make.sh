#!/usr/bin/env bash
# make.sh [gfx/menu dir] - the trailer, start to finish, into build/trailer/lemmix-frame-trailer.mp4:
# the captions and end card (cards.sh, from a NeoLemmix release's menu graphics: default
# build/art-menu, as tools/art/make-art.sh pulls it), the music bed (NeoLemmix's awesome.it played
# twice by openmpt123), each shot in shots/ recorded at 1080p (record.sh; a shot already there is
# kept - delete it to record it again), then the cut (assemble.py, edit.json).
# Needs ffmpeg and openmpt123 (brew install ffmpeg libopenmpt) and the assets at WEB_ASSETS or
# ../LemmingsJS.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
T="$ROOT/tools/trailer"; B="$ROOT/build/trailer"
ASSETS="${WEB_ASSETS:-$(cd "$ROOT/.." && pwd)/LemmingsJS}"
mkdir -p "$B/clips" "$B/music"
"$T/cards.sh" "${1:-$ROOT/build/art-menu}" "$B/cards"
cp "$ROOT/app/Splash/splash.png" "$B/cards/"
if [ ! -f "$B/music/awesome.wav" ]; then
  cp "$ASSETS/neolemmix/music/awesome.it" "$B/music/"
  (cd "$B/music" && openmpt123 --render --repeat 1 --samplerate 48000 --no-float --force awesome.it >/dev/null && mv awesome.it.wav awesome.wav && rm awesome.it)
fi
for s in "$T"/shots/*.json; do
  n="$(basename "$s" .json)"
  [ -f "$B/clips/$n.mp4" ] || "$T/record.sh" "$s" "$B/clips/$n.mp4"
done
python3 "$T/assemble.py" "$T/edit.json" "$B/lemmix-frame-trailer.mp4"
