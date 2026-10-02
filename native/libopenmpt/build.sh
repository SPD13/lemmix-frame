#!/usr/bin/env bash
# Builds libopenmpt (C API, no external decoders: the Lemmix music is .it/.xm/.mod/.s3m)
# for the targets the app runs on:
#   linux-arm64  inside the Steam Linux Runtime 3.0 (sniper) arm64 SDK, the Frame's runtime
#   macos-arm64  on this Mac, for the automated tests
# Output: native/libopenmpt/out/<rid>/libopenmpt.{so,dylib}
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
VER="${LIBOPENMPT_VERSION:-0.8.9}"
TAR="libopenmpt-$VER+release.autotools.tar.gz"
SRC="$HERE/src/libopenmpt-$VER+release.autotools"
mkdir -p "$HERE/src" "$HERE/out"
if [ ! -d "$SRC" ]; then
  curl -sSL -o "$HERE/src/$TAR" "https://lib.openmpt.org/files/libopenmpt/src/$TAR"
  tar -xzf "$HERE/src/$TAR" -C "$HERE/src"
fi
CONF="--disable-openmpt123 --disable-examples --disable-tests --disable-doxygen-doc
      --without-mpg123 --without-ogg --without-vorbis --without-vorbisfile
      --without-portaudio --without-portaudiocpp --without-pulseaudio --without-sndfile --without-flac
      --enable-shared --disable-static"

target="${1:-all}"
if [ "$target" = all ] || [ "$target" = linux-arm64 ]; then
  # colima shares only the folders listed in its config, so the sources go in and
  # the library comes out as tar streams instead of a bind mount
  mkdir -p "$HERE/out/linux-arm64"
  tar -C "$HERE/src" -cz "libopenmpt-$VER+release.autotools" |
  "$HERE/../../tools/docker.sh" run -i --rm \
    registry.gitlab.steamos.cloud/steamrt/sniper/sdk/arm64:latest bash -c "
      set -e; mkdir -p /s /b /o && tar -xz -C /s && cd /b
      /s/libopenmpt-$VER+release.autotools/configure $(echo $CONF) CXXFLAGS='-O2' >/dev/null
      make -j\$(nproc) >/dev/null
      cp -L .libs/libopenmpt.so.0 /o/libopenmpt.so && strip /o/libopenmpt.so
      tar -C /o -c ." | tar -x -C "$HERE/out/linux-arm64"
  echo "linux-arm64: $(file -b "$HERE/out/linux-arm64/libopenmpt.so" | cut -d, -f1-2)"
fi
if [ "$target" = all ] || [ "$target" = macos-arm64 ]; then
  B="$(mktemp -d)"; (cd "$B" && "$SRC/configure" $CONF CXXFLAGS='-O2' >/dev/null && make -j"$(sysctl -n hw.ncpu)" >/dev/null)
  mkdir -p "$HERE/out/macos-arm64"
  cp -L "$B/.libs/libopenmpt.0.dylib" "$HERE/out/macos-arm64/libopenmpt.dylib"
  install_name_tool -id @rpath/libopenmpt.dylib "$HERE/out/macos-arm64/libopenmpt.dylib"
  rm -rf "$B"
  echo "macos-arm64: $(file -b "$HERE/out/macos-arm64/libopenmpt.dylib")"
fi
