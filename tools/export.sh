#!/usr/bin/env bash
# export.sh linux-arm64 - exports the Godot app (release) to build/app/<target>/.
# Godot 4.7 .NET on macOS hangs at exit when headless, so this waits for the files instead.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
target="${1:-linux-arm64}"
case "$target" in
  linux-arm64) preset="Linux ARM64"; out="$ROOT/build/app/linux-arm64"; bin="Lemmix.arm64" ;;
  *) echo "unknown target $target" >&2; exit 2 ;;
esac
rm -rf "$out"; mkdir -p "$out"
log="$ROOT/build/export-$target.log"
"$ROOT/tools/godot.sh" --headless --path "$ROOT/app" --export-release "$preset" "$out/$bin" >"$log" 2>&1 &
pid=$!
for _ in $(seq 1 300); do
  if grep -q "completed\|savepack.*DONE" "$log" 2>/dev/null || ! kill -0 $pid 2>/dev/null; then break; fi
  sleep 1
done
sleep 2; kill -9 $pid 2>/dev/null || true
if grep -q "^ERROR" "$log"; then grep "^ERROR" "$log" >&2; exit 1; fi
[ -f "$out/$bin" ] && [ -f "$out/Lemmix.pck" ] && [ -d "$out/data_Lemmix_linuxbsd_arm64" ] || { echo "export incomplete, see $log" >&2; exit 1; }
cp "$ROOT/native/libopenmpt/out/linux-arm64/libopenmpt.so" "$out/data_Lemmix_linuxbsd_arm64/" 2>/dev/null || true
echo "$out ($(du -sh "$out" | cut -f1))"
