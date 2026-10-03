#!/usr/bin/env bash
# scenery-views.sh <style> <level id> <out-dir> [mode] - (re)makes a gallery's scenery with
# tools/scenery-gen, then renders the headset view of <level> in the render box looking ahead,
# left, behind and up: <out-dir>/{front,left,back,up}/vr-scene.png. mode is the room's (full by
# default; fog / none to compare). SKIP_GEN=1 renders without making the pictures again.
# Example: tools/scenery-views.sh orig_dirt 'Lemmings_Redux/Gentle/Just_dig!.nxlv' build/scenery-shots/orig_dirt
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export WEB_ASSETS="${WEB_ASSETS:-$(cd "$ROOT/.." && pwd)/LemmingsJS}"
style="$1"; level="$2"; out="$3"; mode="${4:-full}"
if [ "${SKIP_GEN:-0}" != 1 ]; then
  (cd "$ROOT/tools/scenery-gen" && dotnet run -c Release -- gen "$style" | grep -E "^\[scenery\] (wrote|layer)|error")
fi
(cd "$ROOT/app" && dotnet build -v q | grep -E " error " || true)
for v in "front:" "left:70,0" "back:180,5" "up:-60,30"; do
  name=${v%%:*}; look=${v#*:}
  if [ -n "$look" ]; then export SHOT_LOOK=$look; else unset SHOT_LOOK; fi
  SHOT_ENV="$mode" SHOT_LEVEL="$level" RENDER_ASSETS=1 RENDER_EXTRA="3d/env/$style/scenery" \
    "$ROOT/tools/render.sh" "$out/$name" --shot vr-scene /out/vr-scene.png 2>&1 | grep "shot saved" || echo "FAILED $name"
done
echo "views in $out"
