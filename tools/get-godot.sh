#!/usr/bin/env bash
# get-godot.sh [--check] [--android] - the Godot engine this project is built with, from the
# official release (github.com/godotengine/godot-builds), checked against its SHA512-SUMS:
#   .tools/Godot_mono.app                           the macOS editor (tools/godot.sh), on a Mac
#   .tools/Godot_v4.7.2-stable_mono_linux_arm64/    the linux-arm64 editor (render box, Linux tests)
#   <templates dir>/4.7.2.stable.mono/              export templates: Linux ARM64 (and Android with --android)
# The engine is not kept in git (hundreds of MB); this pins it. --check only checks the release
# files and the checksums are there. The templates archive is 1.2 GB; only what is used is kept.
set -euo pipefail
VERSION=4.7.2
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REL="https://github.com/godotengine/godot-builds/releases/download/$VERSION-stable"
MAC_ZIP="Godot_v$VERSION-stable_mono_macos.universal.zip"
ARM_ZIP="Godot_v$VERSION-stable_mono_linux_arm64.zip"
TPZ="Godot_v$VERSION-stable_mono_export_templates.tpz"
CHECK=0; ANDROID=0
for a in "$@"; do
  case "$a" in
    --check) CHECK=1 ;;
    --android) ANDROID=1 ;;
    *) echo "usage: $0 [--check] [--android]" >&2; exit 2 ;;
  esac
done
if [ "$(uname)" = Darwin ]; then
  TEMPLATES="$HOME/Library/Application Support/Godot/export_templates/$VERSION.stable.mono"
else
  TEMPLATES="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates/$VERSION.stable.mono"
fi
FILES=("$ARM_ZIP" "$TPZ"); [ "$(uname)" = Darwin ] && FILES=("$MAC_ZIP" "${FILES[@]}")

if [ "$CHECK" = 1 ]; then
  sums=$(curl -fsSL "$REL/SHA512-SUMS.txt")
  for f in "${FILES[@]}"; do
    code=$(curl -sIL -o /dev/null -w '%{http_code}' "$REL/$f")
    grep -q " $f\$" <<<"$sums" && sum=listed || sum=MISSING
    echo "$f: http $code, checksum $sum"
  done
  exit 0
fi

DL="${GODOT_DOWNLOADS:-$ROOT/build/godot-downloads}"
mkdir -p "$DL" "$ROOT/.tools" "$TEMPLATES"
curl -fsSL "$REL/SHA512-SUMS.txt" -o "$DL/SHA512-SUMS.txt"
fetch() {
  local f="$1"
  [ -f "$DL/$f" ] || { echo "downloading $f"; curl -fL --retry 3 -C - "$REL/$f" -o "$DL/$f.part" && mv "$DL/$f.part" "$DL/$f"; }
  local want got
  want=$(grep " $f\$" "$DL/SHA512-SUMS.txt" | cut -d' ' -f1)
  got=$(shasum -a 512 "$DL/$f" | cut -d' ' -f1)
  [ -n "$want" ] && [ "$want" = "$got" ] || { echo "checksum mismatch for $f" >&2; rm -f "$DL/$f"; exit 1; }
}

# the macOS editor, as tools/godot.sh expects it
if [ "$(uname)" = Darwin ] && [ ! -d "$ROOT/.tools/Godot_mono.app" ]; then
  fetch "$MAC_ZIP"
  rm -rf "$DL/mac" && mkdir "$DL/mac" && unzip -q "$DL/$MAC_ZIP" -d "$DL/mac"
  mv "$DL/mac/Godot_mono.app" "$ROOT/.tools/Godot_mono.app"
  xattr -dr com.apple.quarantine "$ROOT/.tools/Godot_mono.app" 2>/dev/null || true
  rm -rf "$DL/mac"
fi

# the linux-arm64 editor (with its GodotSharp), as tools/render.sh expects it
ARM_DIR="$ROOT/.tools/Godot_v$VERSION-stable_mono_linux_arm64"
if [ ! -x "$ARM_DIR/Godot_v$VERSION-stable_mono_linux.arm64" ]; then
  fetch "$ARM_ZIP"
  unzip -q -o "$DL/$ARM_ZIP" -d "$ROOT/.tools"
fi

# the export templates: only what this project exports
if [ ! -f "$TEMPLATES/linux_release.arm64" ] || { [ "$ANDROID" = 1 ] && [ ! -f "$TEMPLATES/android_release.apk" ]; }; then
  fetch "$TPZ"
  want=('templates/version.txt' 'templates/linux_debug.arm64' 'templates/linux_release.arm64')
  [ "$ANDROID" = 1 ] && want+=('templates/android_debug.apk' 'templates/android_release.apk' 'templates/android_source.zip')
  rm -rf "$DL/tpl" && mkdir "$DL/tpl"
  unzip -q -o "$DL/$TPZ" "${want[@]}" -d "$DL/tpl"
  cp "$DL"/tpl/templates/* "$TEMPLATES/"
  rm -rf "$DL/tpl"
fi

echo "Godot $VERSION .NET ready:"
[ "$(uname)" = Darwin ] && echo "  $ROOT/.tools/Godot_mono.app"
echo "  $ARM_DIR"
echo "  $TEMPLATES ($(ls "$TEMPLATES" | tr '\n' ' '))"
echo "The downloads stay in $DL (delete it to free the space)."
