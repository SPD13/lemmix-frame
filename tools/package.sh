#!/usr/bin/env bash
# package.sh [build dir] - packs an exported build (default build/app/linux-arm64, from
# `make export-linux`) into build/dist/, ready to publish as a release:
#   lemmix-frame-linux-arm64.tar.gz (+ .sha256)   lemmix/{Lemmix.arm64, Lemmix.pck, data_*/, VERSION}
#   frame-install.sh, install.sh, install.ps1      the installers (dist/), which download the
#                                                  package from the release they sit in
# VERSION is the app's config/version and the commit, with "-dirty" if tracked files are modified.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD="${1:-$ROOT/build/app/linux-arm64}"
OUT="$ROOT/build/dist"
[ -x "$BUILD/Lemmix.arm64" ] && [ -f "$BUILD/Lemmix.pck" ] || { echo "no build in $BUILD: make export-linux first" >&2; exit 1; }
[ -f "$BUILD/data_Lemmix_linuxbsd_arm64/libopenmpt.so" ] || echo "warning: no libopenmpt.so in the build, the game will have no music" >&2

version=$(sed -n 's/^config\/version="\(.*\)"/\1/p' "$ROOT/app/project.godot")
version="$version ($(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet HEAD || echo -dirty))"

rm -rf "$OUT"; mkdir -p "$OUT/stage/lemmix"
cp -R "$BUILD/." "$OUT/stage/lemmix/"
echo "$version" >"$OUT/stage/lemmix/VERSION"
tarball=lemmix-frame-linux-arm64.tar.gz
# COPYFILE_DISABLE: no macOS ._ files; owner 0 so the archive does not carry this Mac's uid.
COPYFILE_DISABLE=1 tar --uid 0 --gid 0 -czf "$OUT/$tarball" -C "$OUT/stage" lemmix
rm -rf "$OUT/stage"
(cd "$OUT" && shasum -a 256 "$tarball" >"$tarball.sha256")
cp "$ROOT/dist/frame-install.sh" "$ROOT/dist/install.sh" "$ROOT/dist/install.ps1" "$OUT/"
echo "$OUT: Lemmix $version, $(du -h "$OUT/$tarball" | cut -f1)"
