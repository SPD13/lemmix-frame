#!/usr/bin/env bash
# scenery-all.sh [pack...] - makes the scenery of every gallery the levels use: the THEME of each
# .nxlv under $WEB_ASSETS/levels/<pack> (by default every Lemmix pack there), one tools/scenery-gen
# run per installed style, into $WEB_ASSETS/3d/env/<style>/scenery/. Styles that are not
# installed are listed and skipped. REVIEW=<dir> also writes review sheets of the previews there
# (8 a sheet, the styles of each in sheets.txt).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export WEB_ASSETS="${WEB_ASSETS:-$(cd "$ROOT/.." && pwd)/LemmingsJS}"
cd "$WEB_ASSETS/levels"
packs=("$@"); [ ${#packs[@]} -gt 0 ] || packs=($(ls -d */ | tr -d / | grep -v -E '^(lemmings|lemmings_ohNo)$'))
styles=$(for p in "${packs[@]}"; do find "$p" -iname '*.nxlv' -print0 | xargs -0 grep -hi '^ *THEME ' || true; done | tr -d '\r' | awk '{print tolower($2)}' | sort | uniq -c | sort -rn | awk '{print $2}')
(cd "$ROOT/tools/scenery-gen" && dotnet build -c Release -v q | grep -E " error " || true)
GEN="$ROOT/tools/scenery-gen/bin/Release/net8.0/SceneryGen.dll"
made=()
for s in $styles; do
  if [ ! -d "$WEB_ASSETS/neolemmix/styles/$s/terrain" ]; then echo "skip $s: style not installed"; continue; fi
  if dotnet "$GEN" gen "$s" > /tmp/scenery-$s.log 2>&1; then made+=("$s"); echo "made $s"; else echo "FAILED $s (/tmp/scenery-$s.log)"; fi
done
echo "${#made[@]} sceneries"
if [ -n "${REVIEW:-}" ]; then
  mkdir -p "$REVIEW"; : > "$REVIEW/sheets.txt"
  for ((i = 0; i < ${#made[@]}; i += 8)); do
    batch=("${made[@]:i:8}"); files=()
    for s in "${batch[@]}"; do files+=("$WEB_ASSETS/3d/env/$s/scenery/preview.png"); done
    dotnet "$GEN" montage "$REVIEW/sheet-$((i / 8)).png" "${files[@]}"
    echo "sheet-$((i / 8)): ${batch[*]}" >> "$REVIEW/sheets.txt"
  done
  echo "review sheets in $REVIEW"
fi
