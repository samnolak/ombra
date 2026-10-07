#!/usr/bin/env bash
# macOS gate for a candidate commit: one command on a Mac (or a macos-15 runner, workflow avalonia-macos-gate.yml).
# Prepared on Linux; NOT EXECUTED by its author (no macOS machine) — result.txt is the evidence.
#
#   tools/verify/macos-gate.sh <work-dir>          # from a clean checkout of the candidate commit, in avalonia/
#
# BUILD    restore, build, full test suite, publish + package osx-arm64 and osx-x64, package smoke on this Mac's
#          architecture (self-test incl. the client's runtime install + omp over RPC, window start / close).
# RUNTIME  (stand-in model; the Original OMP Core, its RPC and provider path are real)
#          omp startup, stdin/stdout RPC, protocol v2, prompt / stream / abort, shutdown, paths — the real-omp suite
#          on this Mac; $SHELL (zsh, and a non-macOS path as Git Bash would set it); the packaged app streaming a long
#          task: minimize (needs Accessibility for osascript, else reported NOT RUN), Core keeps folding events, UI
#          paused, restore, run-end notification through osascript (argv recorded by a PATH shim, then delivered by
#          the real osascript), quit through the standard Apple event, nothing left running.
# MANUAL   printed at the end: ⌘ vs Ctrl shortcuts, file dialogs, notification display, Retina — answer in result.txt.
# Needs: Xcode command line tools (codesign, ditto), .NET 10 SDK, node, python3.
set -uo pipefail
WORK=${1:?work dir}; mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
HERE=$(cd "$(dirname "$0")/../.." && pwd); cd "$HERE"
[ "$(uname -s)" = Darwin ] || { echo "run this on macOS"; exit 2; }
ARCH=$(uname -m); case "$ARCH" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) echo "unknown arch $ARCH"; exit 2 ;; esac
COMMIT=$(git rev-parse HEAD); SHORT=$(git rev-parse --short HEAD); V=0.1.0-rc.1.$SHORT
R=$WORK/result.txt; : > "$R"
say() { echo "$*" | tee -a "$R"; }
check() { if [ "$2" = 0 ]; then say "PASS $1${3:+ — $3}"; else say "FAIL $1${3:+ — $3}"; fi; }
say "macos gate $(date -u +%FT%TZ) commit $COMMIT on $(sw_vers -productName) $(sw_vers -productVersion) $ARCH"
[ -z "$(git status --porcelain --untracked-files=no)" ]; check "tracked-tree-clean" $? "$(git status --porcelain --untracked-files=no | wc -l | tr -d ' ') changed file(s)"

# ── BUILD ────────────────────────────────────────────────────────────────────────────────────────────────────────
dotnet --info > "$WORK/dotnet-info.txt" 2>&1
dotnet restore OmpGui.sln > "$WORK/restore.log" 2>&1; check "restore" $?
dotnet build OmpGui.sln -c Release --no-restore > "$WORK/build.log" 2>&1; check "build" $?
dotnet test tests/OmpGui.Tests -c Release --no-build --logger "console;verbosity=normal" --logger "trx;LogFileName=macos-full.trx" \
  --results-directory "$WORK/trx" > "$WORK/tests-full.log" 2>&1; check "unit+headless-tests" $? "$(grep -E '^\s*(Passed|Failed|Skipped):' "$WORK/tests-full.log" | tr -s ' ' | tr '\n' ' ')"
for rid in osx-arm64 osx-x64; do
  dotnet publish src/OmpGui.App -c Release -r $rid --self-contained -o "$WORK/pub-$rid" -p:Version=0.1.0 -p:InformationalVersion=$V > "$WORK/publish-$rid.log" 2>&1 \
    && tools/package/package.sh $rid "$WORK/pub-$rid" "$WORK/dist" $V > "$WORK/package-$rid.log" 2>&1
  check "package-$rid" $? "$(cd "$WORK/dist" 2>/dev/null && cat omp-gui-$V-$rid.zip.sha256 2>/dev/null)"
done
tools/package/smoke.sh $RID "$WORK/dist" "$WORK/smoke" $V > "$WORK/smoke.log" 2>&1
check "package-smoke-$RID (self-test, runtime install, omp over RPC, window start/close, codesign --verify)" $? "$(grep 'package smoke passed' "$WORK/smoke.log")"

# ── RUNTIME: the real omp suite on this Mac (installs the pinned runtime with the client's own installer) ─────
tools/ci/real-omp-smoke.sh "$WORK/omp" > "$WORK/real-omp.log" 2>&1
check "real-omp (startup, stdin/stdout RPC, protocol v2, prompt, stream, abort, shutdown, paths)" $? "$(grep -E '^\s*(Passed|Failed|Skipped):' "$WORK/real-omp.log" | tr -s ' ' | tr '\n' ' ')"
RT=$WORK/omp/runtimes/omp-18.8.0-bun-1.4.2

# $SHELL: zsh (macOS default) and a non-macOS path; the packaged app's self-test must still start a shell.
APP_DIR="$WORK/smoke/unpacked/omp-gui-$V-$RID/OMP GUI.app"; APP="$APP_DIR/Contents/MacOS/OmpGui"
for sh in /bin/zsh /usr/bin/bash-does-not-exist; do
  HOME=$WORK/smoke/home OMPGUI_RUNTIME_DIR=$WORK/smoke/runtimes OMPGUI_CONFIG=$WORK/smoke/home/omp-gui.local.json SHELL=$sh \
    "$APP" --self-test "$WORK/selftest-shell.json" > "$WORK/selftest-shell.log" 2>&1
  python3 -c "import json,sys; r=json.load(open(sys.argv[1])); c=[c for c in r['checks'] if c['name']=='pty'][0]; print(c['detail']); sys.exit(0 if c['ok'] else 1)" "$WORK/selftest-shell.json" > "$WORK/pty.txt" 2>&1
  check "shell-SHELL=$sh" $? "$(cat "$WORK/pty.txt")"
done

# ── RUNTIME: the packaged app streaming a long task (stand-in), minimized, notified, quit ─────────────────────
PORT=18181; H=$WORK/pkg-home; mkdir -p "$H" "$WORK/shim"
tools/stand-in-env/setup.sh "$H" $PORT "$WORK/pkg-work" > /dev/null
node tools/mock-model/server.mjs $PORT > "$WORK/pkg-mock.log" 2>&1 & MOCK=$!
# PATH shim: record the notification request exactly as the client makes it, then let macOS deliver it.
cat > "$WORK/shim/osascript" <<EOF
#!/bin/sh
printf '%s\n' "\$*" >> "$WORK/osascript-calls.txt"
exec /usr/bin/osascript "\$@"
EOF
chmod +x "$WORK/shim/osascript"
cat > "$WORK/pkg.json" <<EOF
{ "command": "$RT/bun/bun", "prefixArgs": ["--no-install", "$RT/omp/node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts"],
  "profile": "harness", "workingDirectory": "$WORK/pkg-work",
  "environment": { "HOME": "$H", "QWEN_LOCAL_API_KEY": "dummy-not-a-secret" } }
EOF
PATH="$WORK/shim:$PATH" "$APP" --config "$WORK/pkg.json" --timeline "$WORK/timeline.csv" \
  --startup-log "$WORK/pkg-startup.log" --auto-prompt "LONGTASK 3 minutes: keep checking" > "$WORK/pkg-app.log" 2>&1 & APPPID=$!
for _ in $(seq 1 120); do grep -q ",Running," "$WORK/timeline.csv" 2>/dev/null && break; sleep 1; done
grep -q ",Running," "$WORK/timeline.csv"; check "package-prompt-streams (omp started by the package, run streaming)" $? "$(tail -1 "$WORK/timeline.csv" 2>/dev/null)"
ompkids() { pgrep -P $APPPID | tr '\n' ' '; }
OMPS=$(ompkids)
sleep 10
if osascript -e 'tell application "System Events" to set miniaturized of window 1 of (first process whose unix id is '$APPPID') to true' > "$WORK/minimize.log" 2>&1 \
   || osascript -e 'tell application "System Events" to keystroke "m" using command down' >> "$WORK/minimize.log" 2>&1; then
  sleep 2; M0=$(tail -1 "$WORK/timeline.csv"); sleep 60; M1=$(tail -1 "$WORK/timeline.csv")
  python3 - "$M0" "$M1" > "$WORK/minimized.txt" <<'PY'
import sys
a, b = (l.split(',') for l in sys.argv[1:3])
state, frames0, frames1, ui0, ui1 = b[1], int(a[3]), int(b[3]), int(a[11]), int(b[11])
print(f"window {state}; frames {frames0}->{frames1}; UI applies {ui0}->{ui1}")
sys.exit(0 if state == 'Minimized' and frames1 > frames0 and ui1 - ui0 <= 1 else 1)
PY
  check "minimized-core-folds-ui-paused" $? "$(cat "$WORK/minimized.txt")"
  for _ in $(seq 1 180); do grep -q "omp finished" "$WORK/osascript-calls.txt" 2>/dev/null && break; sleep 1; done
  grep -q "omp finished" "$WORK/osascript-calls.txt" 2>/dev/null; check "notification-on-run-end (client → osascript)" $? "$(head -1 "$WORK/osascript-calls.txt" 2>/dev/null)"
  osascript -e 'tell application id "io.github.samnolak.omp-gui" to activate' >/dev/null 2>&1; sleep 2
  tail -1 "$WORK/timeline.csv" | grep -q ",Normal,"; check "restore-shows-current-state" $? "$(tail -1 "$WORK/timeline.csv")"
else
  say "NOT RUN minimize / background / notification — osascript has no Accessibility permission here ($(tail -1 "$WORK/minimize.log"))"
  for _ in $(seq 1 240); do tail -1 "$WORK/timeline.csv" | grep -q ",Ready," && break; sleep 1; done
fi
osascript -e 'tell application id "io.github.samnolak.omp-gui" to quit' > "$WORK/quit.log" 2>&1
for _ in $(seq 1 30); do kill -0 $APPPID 2>/dev/null || break; sleep 0.5; done
LEFT=""; kill -0 $APPPID 2>/dev/null && LEFT="app $APPPID"; for p in $OMPS; do kill -0 "$p" 2>/dev/null && LEFT="$LEFT omp $p"; done
[ -z "$LEFT" ]; check "quit-leaves-nothing-running (Apple event quit → app and omp gone)" $? "${LEFT:-nothing left}"
kill $APPPID $MOCK 2>/dev/null

# ── MANUAL (interactive; answer PASS / FAIL on each line of result.txt) ──────────────────────────────────────
cat >> "$R" <<EOF
MANUAL ⌘N new session · ⌘B sidebar · ⌘↩ sends · ⌘, opens Settings from the app menu · Ctrl+\` terminal (Ctrl on purpose)
MANUAL Option+A / Option+D answer an approval card without typing å / ∂ into the composer
MANUAL Attach (paperclip) and Open folder… open the native panels; a chosen PNG / folder is used
MANUAL A run that ends while the window is minimized shows a Notification Center banner "omp finished"
MANUAL Retina: text and icons sharp at 2x; the window keeps its layout when moved between displays
MANUAL Package opened from Finder (first start as in USER_GUIDE: Privacy & Security → Open Anyway on macOS 15+, Control-click → Open on 12–14) starts and reaches omp's first-run state; later starts from its icon in Launchpad / Dock
EOF
echo "result: $R"; ! grep -q "^FAIL" "$R"
