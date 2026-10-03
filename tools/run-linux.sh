#!/usr/bin/env bash
# run-linux.sh <out-dir> <app args...>
# Runs the exported Linux ARM64 build (build/app/linux-arm64) headless in the Steam Linux Runtime
# sniper arm64 image - the Frame's runtime, its CPU architecture, a Release build - with the assets
# streamed in (WEB_ASSETS' levels/ and neolemmix/), and copies the app's user files (perf.json,
# probe.json, qa.json, the store) and its log back into <out-dir>.
# Example: tools/run-linux.sh build/linux-bench --benchmark
set -euo pipefail
export COPYFILE_DISABLE=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
out="$1"; shift
mkdir -p "$out"
ASSETS="${WEB_ASSETS:-$ROOT/../LemmingsJS}"
tmp="$(mktemp -d)"
ln -s "$ROOT/build/app/linux-arm64" "$tmp/app"
ln -s "$ASSETS/levels" "$tmp/levels"
ln -s "$ASSETS/neolemmix" "$tmp/neolemmix"
tar --no-mac-metadata --no-xattrs -h -C "$tmp" -c app levels neolemmix | "$ROOT/tools/docker.sh" run -i --rm registry.gitlab.steamos.cloud/steamrt/sniper/platform/arm64:latest bash -c '
  mkdir -p /p/assets /out && tar -x -C /p && mv /p/levels /p/neolemmix /p/assets/
  cd /p/app && (timeout '"${RUN_TIMEOUT:-900}"' ./Lemmix.arm64 --headless --xr-mode off -- --assets=/p/assets '"$*"' > /out/app.log 2>&1 || true)
  cp ~/.local/share/godot/app_userdata/Lemmix/*.json /out/ 2>/dev/null || true
  tar -C /out -c .' | tar -x -C "$out"
rm -rf "$tmp"
grep -E "\[lemmix\]|ERROR|Unhandled" "$out/app.log" | tail -30 || true
