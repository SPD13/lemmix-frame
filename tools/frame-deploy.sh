#!/usr/bin/env bash
# frame-deploy.sh [--probe|--run] - copies build/app/linux-arm64 to the Steam Frame over ssh.
# Needs the Frame in developer mode, paired with the SteamOS Devkit Client once (that creates
# ~/.config/steamos-devkit/devkit_rsa and authorises it on the device), and:
#   FRAME_HOST  the Frame's address on the LAN
#   FRAME_USER  the devkit user (shown by the Devkit Client; checked at device session 1)
# The build lands in ~/devkit-game/lemmix on the device, the folder the Devkit Client uses, so
# the Client's "Lemmix" title (registered once with Steam Linux Runtime 3.0 ARM64) launches it.
# --probe / --run start it over ssh instead, for when launching outside Steam is enough.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
: "${FRAME_HOST:?set FRAME_HOST}"; : "${FRAME_USER:?set FRAME_USER}"
KEY="${FRAME_KEY:-$HOME/.config/steamos-devkit/devkit_rsa}"
SSH="ssh -i $KEY -o StrictHostKeyChecking=accept-new"
rsync -az --delete -e "$SSH" "$ROOT/build/app/linux-arm64/" "$FRAME_USER@$FRAME_HOST:devkit-game/lemmix/"
echo "deployed to $FRAME_USER@$FRAME_HOST:~/devkit-game/lemmix"
case "${1:-}" in
  --probe) $SSH "$FRAME_USER@$FRAME_HOST" 'cd ~/devkit-game/lemmix && ./Lemmix.arm64 -- --probe' ;;
  --run)   $SSH "$FRAME_USER@$FRAME_HOST" 'cd ~/devkit-game/lemmix && ./Lemmix.arm64' ;;
esac
