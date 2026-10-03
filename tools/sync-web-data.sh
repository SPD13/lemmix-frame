#!/usr/bin/env bash
# Copies the data the app ships from the pinned web/ submodule into app/Data (res://Data):
# the depth profiles (3d/profiles), the solver's solutions (solutions/: index.json and the
# .nxrp files). Run after bumping web/. (The setup page's credits are the app's own: VrSetupPage.Credits.)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
D="$ROOT/app/Data"
rm -rf "$D/profiles" "$D/solutions"
mkdir -p "$D/profiles" "$D/solutions"
cp "$ROOT"/web/3d/profiles/*.json "$D/profiles/"
(cd "$ROOT/web/solutions" && find . -name '*.nxrp' -o -name 'index.json' | while read -r f; do mkdir -p "$D/solutions/$(dirname "$f")"; cp "$f" "$D/solutions/$f"; done)
echo "profiles: $(ls "$D/profiles" | wc -l | tr -d ' '), solutions: $(find "$D/solutions" -name '*.nxrp' | wc -l | tr -d ' ')"
