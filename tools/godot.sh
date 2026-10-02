#!/usr/bin/env bash
# Runs the Godot .NET editor binary on this Mac (.tools/, downloaded in phase 0).
exec "$(cd "$(dirname "$0")/.." && pwd)/.tools/Godot_mono.app/Contents/MacOS/Godot" "$@"
