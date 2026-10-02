#!/usr/bin/env bash
# Runs the Lemmix.Core tests inside the Steam Linux Runtime sniper arm64 image (the Frame's
# runtime: case-sensitive filesystem, glibc, arm64). The tests are published self-contained on
# the Mac and streamed in with tar (colima does not share this folder). WEB_ASSETS, when set,
# is streamed too, limited to what the tests read (tests/assets.list) to keep it small.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/core-tests-linux"
rm -rf "$OUT"
dotnet publish "$ROOT/core/Lemmix.Core.Tests" -c Release -r linux-arm64 --self-contained -o "$OUT/bin" --nologo -v q >/dev/null
cp "$ROOT/native/libopenmpt/out/linux-arm64/libopenmpt.so" "$OUT/bin/" 2>/dev/null || true
mkdir -p "$OUT/oracle"; [ -d "$ROOT/oracle/out" ] && cp -R "$ROOT/oracle/out/." "$OUT/oracle/" || true
tar -C "$OUT" -c bin oracle | "$ROOT/tools/docker.sh" run -i --rm registry.gitlab.steamos.cloud/steamrt/sniper/platform/arm64:latest bash -c '
  mkdir /t && tar -x -C /t && cd /t/bin && ORACLE_DIR=/t/oracle ./Lemmix.Core.Tests 2>&1 | tail -3' | tee "$OUT/result.txt"
grep -qE "Failed: 0|Passed!" "$OUT/result.txt"
