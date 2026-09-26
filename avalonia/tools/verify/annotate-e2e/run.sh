#!/usr/bin/env bash
# Page comments in a real window: its own Xvfb + openbox, a local page, the app's main window with the fake omp, and
# real input (xdotool). Screenshots go to $1 (default: ./annotate-shots). Needs Linux, Xvfb, openbox, xdotool,
# ImageMagick (import), python3 and libwebkit2gtk-4.1.
#   tools/verify/annotate-e2e/run.sh [shots-dir]
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)
SHOTS=$(mkdir -p "${1:-annotate-shots}" && cd "${1:-annotate-shots}" && pwd)
export DISPLAY=${ANNOTATE_DISPLAY:-:94}
PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup EXIT
dotnet build "$ROOT/tests/OmpGui.FakeOmp" -v q -nologo
dotnet build "$HERE/AnnotateE2E.csproj" -v q -nologo
Xvfb "$DISPLAY" -screen 0 1400x900x24 -nolisten tcp > "$SHOTS/xvfb.log" 2>&1 & PIDS+=($!)
sleep 1
openbox > "$SHOTS/openbox.log" 2>&1 & PIDS+=($!)
python3 -m http.server 8766 --bind 127.0.0.1 --directory "$HERE/site" > "$SHOTS/http.log" 2>&1 & PIDS+=($!)
sleep 2
FAKE_OMP_DLL="$ROOT/tests/OmpGui.FakeOmp/bin/Debug/net10.0/OmpGui.FakeOmp.dll" SHOTS="$SHOTS" \
  timeout 240 dotnet "$HERE/bin/Debug/net10.0/AnnotateE2E.dll"
