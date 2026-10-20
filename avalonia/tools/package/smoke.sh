#!/usr/bin/env bash
# Package smoke: unpack the archive into a clean folder and run it with a HOME that has no omp, settings or model.
#   tools/package/smoke.sh <rid> <dist-dir> <work-dir> <version>
# 1. Ombra --self-test --install-runtime: native libraries, the engine, omp over RPC. A package that carries the engine
#    (package.sh) runs it with the network blocked (proxy variables to a closed port; macOS: sandbox-exec denies every
#    outbound connection but loopback): the bundled pack must be used in place, nothing downloaded, and the app's code
#    signature must still verify afterwards (nothing written into the bundle). A bundled pack for another platform than
#    this machine's (osx-x64 under Rosetta 2: the app picks the native arm64 Bun) is the mismatch fallback: installed.
# 2. GUI: the window starts, omp reaches its first-run state, the window closes itself, no omp is left running.
set -euo pipefail
# The window never takes the focus of the machine's user (src/Ombra.App/Platform/TestFocus.cs).
export OMBRA_TEST_NO_ACTIVATE=${OMBRA_TEST_NO_ACTIVATE:-1}
RID=${1:?rid}; DIST=${2:?dist}; WORK=${3:?work}; VERSION=${4:?version}
NAME="ombra-$VERSION-$RID"
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
X="$WORK/unpacked"; rm -rf "$X"; mkdir -p "$X" "$WORK/home"

native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
alive() {
  if command -v tasklist.exe >/dev/null; then tasklist.exe //FI "PID eq $1" //NH | grep -q " $1 "
  else kill -0 "$1" 2>/dev/null; fi
}

case "$RID" in
  linux-*) tar -xzf "$DIST/$NAME.tar.gz" -C "$X"; APP="$X/$NAME/Ombra" ;;
  osx-*)   ditto -x -k "$DIST/$NAME.zip" "$X"; APP="$X/$NAME/Ombra.app/Contents/MacOS/Ombra"
           codesign --verify --deep --strict "$X/$NAME/Ombra.app"; /usr/bin/file "$APP" ;;
  win-*)   if command -v 7z >/dev/null; then 7z x -y -o"$X" "$DIST/$NAME.zip" >/dev/null; else unzip -q "$DIST/$NAME.zip" -d "$X"; fi
           APP="$X/$NAME/Ombra.exe" ;;
esac
ls "$(dirname "$APP")" | wc -l | xargs echo "files in the app folder:"
case "$RID" in osx-*) ENGINE="$X/$NAME/Ombra.app/Contents/Resources/engine" ;; *) ENGINE="$X/$NAME/engine" ;; esac
BUNDLED=$(sed -n 's/^ *"platform": "\([^"]*\)".*/\1/p' "$ENGINE"/*/installed.json 2>/dev/null | head -1)
echo "bundled engine: ${BUNDLED:-none}"

# Nothing from the runner's own profile: a fresh home, the client's folders redirected into the work dir.
export HOME="$WORK/home" USERPROFILE="$(native "$WORK/home")"
export OMBRA_CONFIG="$(native "$WORK/home/ombra.local.json")"
export OMBRA_RUNTIME_DIR="$(native "$WORK/runtimes")"
export OMBRA_STT_DIR="$(native "$WORK/speech")"
unset ANTHROPIC_API_KEY OPENAI_API_KEY GEMINI_API_KEY AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN || true

# No network for the app and everything it starts (the engine's Bun included)
NO_NET=(env HTTPS_PROXY=http://127.0.0.1:9 HTTP_PROXY=http://127.0.0.1:9 ALL_PROXY=http://127.0.0.1:9
  https_proxy=http://127.0.0.1:9 http_proxy=http://127.0.0.1:9 all_proxy=http://127.0.0.1:9 NO_PROXY= no_proxy=
  NPM_CONFIG_REGISTRY=http://127.0.0.1:9/)
OFFLINE=("${NO_NET[@]}")
case "$RID" in osx-*) OFFLINE+=(sandbox-exec -p '(version 1)(allow default)(deny network-outbound)(allow network-outbound (remote ip "localhost:*"))(allow network-outbound (remote unix-socket))') ;; esac
selftest() {
  set +e
  "$@" "$APP" --self-test "$(native "$WORK/$RID-selftest.json")" --install-runtime > "$WORK/$RID-selftest.log" 2>&1
  code=$?
  set -e
  cat "$WORK/$RID-selftest.log"
}

echo "::group::self-test"
GUI=()
if [ -n "$BUNDLED" ]; then
  selftest "${OFFLINE[@]}"
  HOST=$(sed -n 's/^ *"platform": "\([^"]*\)".*/\1/p' "$WORK/$RID-selftest.json" 2>/dev/null | head -1)
  if [ "$HOST" != "$BUNDLED" ]; then
    echo "bundled engine is for $BUNDLED, this machine runs $HOST: the mismatch fallback installs it"
    selftest
  else
    [ $code -eq 0 ] || { echo "self-test failed offline ($code)"; exit 1; }
    grep -q "bundled with the app" "$WORK/$RID-selftest.log" || { echo "the bundled engine was not used"; exit 1; }
    if grep -q '"runtime-install"' "$WORK/$RID-selftest.log"; then echo "the engine was installed although bundled"; exit 1; fi
    if ls -d "$WORK"/runtimes/ombra-engine-*/ > /dev/null 2>&1; then echo "an engine was written to the runtimes folder"; exit 1; fi
    case "$RID" in osx-*) codesign --verify --deep --strict "$X/$NAME/Ombra.app" && echo "signature still valid after the engine ran" ;; esac
    echo "engine: bundled pack used in place, network blocked, nothing downloaded"
    GUI=("${NO_NET[@]}") # the proxy variables only: sandbox-exec also denies the app's leftover-process check (ps)
  fi
else
  selftest
fi
echo "::endgroup::"
[ $code -eq 0 ] || { echo "self-test failed ($code)"; exit 1; }

echo "::group::GUI start / close"
LOG="$WORK/$RID-gui.log"
case "$RID" in
  linux-*) command -v xvfb-run >/dev/null || { sudo apt-get update -qq && sudo apt-get install -y -qq xvfb >/dev/null; }
           ${GUI[@]+"${GUI[@]}"} xvfb-run -a "$APP" --smoke-exit-after-ms 3000 --startup-log "$LOG" ;;
  *)       ${GUI[@]+"${GUI[@]}"} "$APP" --smoke-exit-after-ms 3000 --startup-log "$(native "$LOG")" ;;
esac
cat "$LOG"
echo "::endgroup::"
grep -q "window_usable_ms" "$LOG" || { echo "the window never became usable"; exit 1; }
grep -q "smoke phase=" "$LOG" || { echo "omp never reached a state"; exit 1; }
grep -Eq "smoke phase=Ready|start_problem=NoModel" "$LOG" || { echo "omp did not start as expected"; exit 1; }
pid=$(grep -o "omp_pid=[0-9]*" "$LOG" | tail -1 | cut -d= -f2)
if [ -n "$pid" ] && alive "$pid"; then echo "omp (pid $pid) still running after the window closed"; exit 1; fi
echo "package smoke passed: $(grep 'smoke phase=' "$LOG")"
case "$RID" in osx-*) codesign --verify --deep --strict "$X/$NAME/Ombra.app" ;; esac
