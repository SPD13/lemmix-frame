#!/usr/bin/env bash
# render.sh <out-dir> <godot args...>
# Runs the app in the render box (Xvfb + software Vulkan, tools/render-box) and copies what it
# wrote to /out back into <out-dir>. The project goes in as a tar stream (built .NET assemblies
# included), with the Linux arm64 Godot editor; WEB_ASSETS too when a shot needs the assets
# (RENDER_ASSETS=1: levels/ and neolemmix/ only).
# Example: tools/render.sh build/shots --shot canvas-demo /out/canvas-demo.png   (app arguments, after the engine's --)
set -euo pipefail
export COPYFILE_DISABLE=1  # no macOS extended attributes in the tar streams
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="${APP_DIR:-$ROOT/app}"
out="$1"; shift
mkdir -p "$out"
GODOT_DIR="$ROOT/.tools/Godot_v4.7.2-stable_mono_linux_arm64"
ASSETS="${WEB_ASSETS:-$ROOT/../LemmingsJS}"
tmp="$(mktemp -d)"
mkdir -p "$tmp/p"
rsync -a --exclude bin --exclude obj "$APP/" "$tmp/p/app/"
mkdir -p "$tmp/p/app/.godot/mono/temp/bin" && rsync -a "$APP/.godot/mono/temp/bin/" "$tmp/p/app/.godot/mono/temp/bin/"
cp -R "$GODOT_DIR" "$tmp/p/godot"
if [ "${RENDER_ASSETS:-0}" = 1 ]; then mkdir -p "$tmp/p/assets" && cp -R "$ASSETS/levels" "$ASSETS/neolemmix" "$tmp/p/assets/"; fi
tar --no-mac-metadata -C "$tmp/p" -c . | "$ROOT/tools/docker.sh" run -i --rm -e WEB_ASSETS=/p/assets -e RENDER_DRIVER="${RENDER_DRIVER:-vulkan}" "${RENDER_IMAGE:-lemmix-render-box:trixie}" bash -c '
  mkdir -p /p /out && tar -x -C /p && cd /p
  G=$(ls /p/godot/Godot_v4.7.2-stable_mono_linux* | head -1)
  timeout 180 xvfb-run -a -s "-screen 0 1280x800x24" "$G" --path /p/app --rendering-driver ${RENDER_DRIVER:-vulkan} $( [ "${RENDER_DRIVER:-vulkan}" = vulkan ] && echo --rendering-method mobile ) --xr-mode off -- "$@" > /out/godot.log 2>&1 || true
  tar -C /out -c .' -- "$@" | tar -x -C "$out"
rm -rf "$tmp"
grep -E "\[lemmix\]|ERROR" "$out/godot.log" | head -20 || true
