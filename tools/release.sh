#!/usr/bin/env bash
# release.sh [--no-export] [--draft] - publishes a release of the Frame app on GitHub, where the
# installers download it from (https://github.com/SPD13/lemmix-frame/releases/latest/download/...):
#   1. checks that the tracked files are committed and that the tag v<config/version> is new
#      (bump config/version in app/project.godot for each release);
#   2. make export-linux (skipped with --no-export: the build in build/app/linux-arm64 is used);
#   3. tools/package.sh -> build/dist/;
#   4. gh release create v<version> with the package, its checksum and the three installers.
# Players get it by running the installer again. The repo must be public for them to download it
# (or set LEMMIX_RELEASE_URL in the installers to where the files are hosted).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export=1 draft=()
for a in "$@"; do
  case "$a" in
    --no-export) export=0 ;;
    --draft) draft=(--draft) ;;
    *) echo "usage: $0 [--no-export] [--draft]" >&2; exit 2 ;;
  esac
done
cd "$ROOT"
git diff --quiet HEAD || { echo "tracked files are modified: commit them, or release from a clean worktree" >&2; exit 1; }
version=$(sed -n 's/^config\/version="\(.*\)"/\1/p' app/project.godot)
tag="v$version"
! git rev-parse -q --verify "refs/tags/$tag" >/dev/null || { echo "$tag exists: bump config/version in app/project.godot" >&2; exit 1; }
[ "$(gh repo view --json visibility -q .visibility)" = PUBLIC ] ||
  echo "warning: the repo is private, so players cannot download this release; the installers need it public (or LEMMIX_RELEASE_URL)" >&2

[ $export = 0 ] || make export-linux
tools/package.sh
cd build/dist
notes="Lemmix for Steam Frame $version ($(git rev-parse --short HEAD)).

Install or update, from a Mac or Linux computer on the Frame's network (Developer Mode on):

    curl -fsSL https://github.com/SPD13/lemmix-frame/releases/latest/download/install.sh | bash

From Windows (PowerShell):

    powershell -ExecutionPolicy Bypass -c \"irm https://github.com/SPD13/lemmix-frame/releases/latest/download/install.ps1 | iex\"

Step by step: https://github.com/SPD13/lemmix-frame#install-on-your-steam-frame"
git tag "$tag"
git push origin "$tag"
gh release create "$tag" --verify-tag ${draft[@]+"${draft[@]}"} --title "Lemmix for Steam Frame $version" --notes "$notes" \
  lemmix-frame-linux-arm64.tar.gz lemmix-frame-linux-arm64.tar.gz.sha256 frame-install.sh install.sh install.ps1
