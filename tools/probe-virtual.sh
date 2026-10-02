#!/usr/bin/env bash
# Phase 0 off-device probe: tools/probe-virtual run in the sniper arm64 platform image.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/probe-virtual"
dotnet publish "$ROOT/tools/probe-virtual" -c Release -r linux-arm64 --self-contained -o "$OUT/linux-arm64" --nologo -v q >/dev/null
cp "$ROOT/native/libopenmpt/out/linux-arm64/libopenmpt.so" "$OUT/linux-arm64/"
mkdir -p "$OUT/in"; cp "$(ls "${WEB_ASSETS:-$ROOT/../LemmingsJS}"/neolemmix/music/*.it | head -1)" "$OUT/in/music.it"
tar -C "$OUT" -c linux-arm64 in | "$ROOT/tools/docker.sh" run -i --rm registry.gitlab.steamos.cloud/steamrt/sniper/platform/arm64:latest \
  bash -c 'mkdir /p && tar -x -C /p && cd /p/linux-arm64 && LD_LIBRARY_PATH=. ./ProbeVirtual /p/in/music.it' | tee "$OUT/report.json"
