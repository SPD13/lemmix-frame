#!/usr/bin/env bash
# Runs the Lemmix.Core tests inside the Steam Linux Runtime sniper arm64 image (the Frame's
# runtime: case-sensitive filesystem, glibc, arm64). The tests are published self-contained on
# the Mac and streamed in with tar (colima does not share this folder), laid out as the repo is
# (a Makefile at the root, web/ data the tests read, oracle/out) so the tests find their files.
# TEST_ARGS="-class X" to run some; TEST_LINES=n for more of the output.
# LINUX_ASSETS=1 streams WEB_ASSETS' levels/ and neolemmix/ too (about 250 MB), so the oracle
# tests run on the case-sensitive filesystem; without it they skip.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/core-tests-linux"
rm -rf "$OUT"
R="$OUT/repo"
mkdir -p "$R/web/3d" "$R/oracle"
dotnet publish "$ROOT/core/Lemmix.Core.Tests" -c Release -r linux-arm64 --self-contained -o "$R/build/bin" --nologo -v q >/dev/null
cp "$ROOT/native/libopenmpt/out/linux-arm64/libopenmpt.so" "$R/build/bin/" 2>/dev/null || true
cp "$ROOT/Makefile" "$R/"
ln -sfn "$ROOT/web/3d/profiles" "$R/web/3d/profiles"
ln -sfn "$ROOT/web/solutions" "$R/web/solutions"
for f in tools lemmix 3d/js js; do mkdir -p "$(dirname "$R/web/$f")"; ln -sfn "$ROOT/web/$f" "$R/web/$f"; done
ln -sfn "$ROOT/oracle/out" "$R/oracle/out"
if [ "${LINUX_ASSETS:-0}" = 1 ]; then
  ln -sfn "${WEB_ASSETS:-$ROOT/../LemmingsJS}" "$OUT/assets"
fi
export COPYFILE_DISABLE=1
parts=(repo); [ -e "$OUT/assets" ] && parts+=(assets/levels assets/neolemmix assets/solutions assets/config.json)
tar --no-mac-metadata -h -C "$OUT" -c "${parts[@]}" | "$ROOT/tools/docker.sh" run -i --rm registry.gitlab.steamos.cloud/steamrt/sniper/platform/arm64:latest bash -c '
  mkdir /t && tar -x -C /t && cd /t/repo/build/bin && WEB_ASSETS=/t/assets ./Lemmix.Core.Tests '"${TEST_ARGS:-}"' 2>&1 | grep -vE "^\s*$" | grep -vE "Discover|Starting|Finished|Runner| at |End of stack"' > "$OUT/result.txt"
# what is not a skip (a skip's line and its reason dropped), the first lines of it, then the
# summary, however many tests skip
awk '/\[SKIP\]/ { drop = 2 } drop > 0 { drop--; next } !/Total:/' "$OUT/result.txt" | head -n "${TEST_LINES:-60}"
grep "Total:" "$OUT/result.txt"
grep -q "Failed: 0" "$OUT/result.txt"
