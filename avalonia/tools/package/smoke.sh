#!/usr/bin/env bash
# Package smoke: unpack the archive into a clean folder and run it with a HOME that has no omp, settings or model.
#   tools/package/smoke.sh <rid> <dist-dir> <work-dir> <version>
# 1. OmpGui --self-test --install-runtime: native libraries, pinned runtime installed by the client, omp over RPC.
# 2. GUI: the window starts, omp reaches its first-run state, the window closes itself, no omp is left running.
set -euo pipefail
RID=${1:?rid}; DIST=${2:?dist}; WORK=${3:?work}; VERSION=${4:?version}
NAME="omp-gui-$VERSION-$RID"
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
X="$WORK/unpacked"; rm -rf "$X"; mkdir -p "$X" "$WORK/home"

native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
alive() {
  if command -v tasklist.exe >/dev/null; then tasklist.exe //FI "PID eq $1" //NH | grep -q " $1 "
  else kill -0 "$1" 2>/dev/null; fi
}

case "$RID" in
  linux-*) tar -xzf "$DIST/$NAME.tar.gz" -C "$X"; APP="$X/$NAME/OmpGui" ;;
  osx-*)   ditto -x -k "$DIST/$NAME.zip" "$X"; APP="$X/$NAME/OMP GUI.app/Contents/MacOS/OmpGui"
           codesign --verify --deep --strict "$X/$NAME/OMP GUI.app"; /usr/bin/file "$APP" ;;
  win-*)   if command -v 7z >/dev/null; then 7z x -y -o"$X" "$DIST/$NAME.zip" >/dev/null; else unzip -q "$DIST/$NAME.zip" -d "$X"; fi
           APP="$X/$NAME/OmpGui.exe" ;;
esac
ls "$(dirname "$APP")" | wc -l | xargs echo "files in the app folder:"

# Nothing from the runner's own profile: a fresh home, the client's folders redirected into the work dir.
export HOME="$WORK/home" USERPROFILE="$(native "$WORK/home")"
export OMPGUI_CONFIG="$(native "$WORK/home/omp-gui.local.json")"
export OMPGUI_RUNTIME_DIR="$(native "$WORK/runtimes")"
export OMPGUI_STT_DIR="$(native "$WORK/speech")"
unset ANTHROPIC_API_KEY OPENAI_API_KEY GEMINI_API_KEY AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN || true

echo "::group::self-test"
set +e
"$APP" --self-test "$(native "$WORK/$RID-selftest.json")" --install-runtime > "$WORK/$RID-selftest.log" 2>&1
code=$?
set -e
cat "$WORK/$RID-selftest.log"
echo "::endgroup::"
[ $code -eq 0 ] || { echo "self-test failed ($code)"; exit 1; }

echo "::group::GUI start / close"
LOG="$WORK/$RID-gui.log"
case "$RID" in
  linux-*) command -v xvfb-run >/dev/null || { sudo apt-get update -qq && sudo apt-get install -y -qq xvfb >/dev/null; }
           xvfb-run -a "$APP" --smoke-exit-after-ms 3000 --startup-log "$LOG" ;;
  *)       "$APP" --smoke-exit-after-ms 3000 --startup-log "$(native "$LOG")" ;;
esac
cat "$LOG"
echo "::endgroup::"
grep -q "window_usable_ms" "$LOG" || { echo "the window never became usable"; exit 1; }
grep -q "smoke phase=" "$LOG" || { echo "omp never reached a state"; exit 1; }
grep -Eq "smoke phase=Ready|start_problem=NoModel" "$LOG" || { echo "omp did not start as expected"; exit 1; }
pid=$(grep -o "omp_pid=[0-9]*" "$LOG" | tail -1 | cut -d= -f2)
if [ -n "$pid" ] && alive "$pid"; then echo "omp (pid $pid) still running after the window closed"; exit 1; fi
echo "package smoke passed: $(grep 'smoke phase=' "$LOG")"
