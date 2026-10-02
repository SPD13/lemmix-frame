#!/usr/bin/env bash
# docker, minus the stale "credsStore": "desktop" of ~/.docker/config.json (this Mac runs colima,
# so docker-credential-desktop does not exist and every pull of a public image fails with it).
set -euo pipefail
CFG="${TMPDIR:-/tmp}/lemmix-frame-docker"
if [ ! -f "$CFG/config.json" ]; then
  mkdir -p "$CFG"
  ctx="$(docker context show 2>/dev/null || echo default)"
  printf '{"currentContext":"%s"}\n' "$ctx" > "$CFG/config.json"
  [ -d "$HOME/.docker/contexts" ] && cp -R "$HOME/.docker/contexts" "$CFG/"
fi
DOCKER_CONFIG="$CFG" exec docker "$@"
