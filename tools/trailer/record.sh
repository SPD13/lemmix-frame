#!/usr/bin/env bash
# record.sh <clip.json> <out.mp4> - one trailer shot (app/Test/Trailer.cs) through Godot's Movie
# Maker (MJPEG AVI at 30 fps, the game's sound in it), the level's warm-up cut off by the
# "[trailer] rec/done" frames, encoded to H.264 + AAC. TRAILER_RES picks the size (1920x1080).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
clip="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; mp4="$2"
out="$(mktemp -d "${TMPDIR:-/tmp}/trailer-rec.XXXXXX")"
log="${mp4%.mp4}.log"
"$ROOT/tools/godot.sh" --path "$ROOT/app" --xr-mode off --write-movie "$out/f.avi" --fixed-fps 30 \
  --resolution "${TRAILER_RES:-1920x1080}" -- --trailer "$clip" >"$log" 2>&1 &
pid=$!
for ((i = 0; i < 6000; i++)); do
  grep -q "\[trailer\] done" "$log" && break
  kill -0 $pid 2>/dev/null || break
  sleep 0.1
done
# the quit writes the wav's sizes; Godot .NET can hang after it on macOS
for ((i = 0; i < 100; i++)); do kill -0 $pid 2>/dev/null || break; sleep 0.1; done
kill -9 $pid 2>/dev/null
rec=$(sed -n 's/.*\[trailer\] rec \([0-9]*\).*/\1/p' "$log"); done_=$(sed -n 's/.*\[trailer\] done \([0-9]*\).*/\1/p' "$log")
[ -n "$rec" ] && [ -n "$done_" ] || { rm -rf "$out"; tail -20 "$log"; echo "[record] no shot" >&2; exit 1; }
# frame n of the movie is the n-th drawn (1-based): the shot is frames rec+1 .. done
ffmpeg -hide_banner -loglevel error -y -ss "$(echo "scale=4; ($rec + 1) / 30" | bc)" -i "$out/f.avi" \
  -frames:v $((done_ - rec)) -c:v libx264 -crf 15 -preset slow -pix_fmt yuv420p -c:a aac -b:a 192k -ar 48000 "$mp4"
rm -rf "$out"
echo "[record] $(basename "$clip"): $((done_ - rec)) frames -> $mp4"
