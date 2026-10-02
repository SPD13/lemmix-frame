#!/usr/bin/env bash
# godot-run.sh <deadline-seconds> <marker> <godot args...>
# Runs Godot headless and succeeds once <marker> is printed. Godot 4.7 .NET on macOS
# headless hangs after "XR: Removed interface" on quit, so the process is killed once the
# marker is seen (or the deadline passes) rather than waited for. The log goes to stdout.
set -uo pipefail
deadline="$1"; marker="$2"; shift 2
log="$(mktemp)"
"$(dirname "$0")/godot.sh" "$@" >"$log" 2>&1 &
pid=$!
for ((i = 0; i < deadline * 10; i++)); do
  if grep -q -- "$marker" "$log"; then sleep 0.3; kill -9 $pid 2>/dev/null; cat "$log"; rm -f "$log"; exit 0; fi
  kill -0 $pid 2>/dev/null || break
  sleep 0.1
done
kill -9 $pid 2>/dev/null; cat "$log"; rm -f "$log"
echo "[godot-run] marker '$marker' not seen within ${deadline}s" >&2
exit 1
