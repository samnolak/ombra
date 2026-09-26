#!/usr/bin/env bash
# Background reliability test (Linux + Xvfb + openbox). Starts the GUI with a long agent task,
# minimizes it, focuses another window, keeps it minimized for MINUTES real minutes, restores it and
# captures what the window shows right after restore. Core and UI progress are logged by the app
# (--timeline); this script adds window state, process samples and screenshots.
#
#   tools/verify/background-test.sh <out-dir> <config.json> [minutes=20] [task-minutes=24]
# APP_BIN=<path to a packaged OmpGui executable> runs a package instead of the Release build output.
set -euo pipefail
OUT=${1:?out dir}; CONFIG=${2:?config}; MINUTES=${3:-20}; TASK=${4:-24}
APP=$(cd "$(dirname "$0")/../.." && pwd)/src/OmpGui.App/bin/Release/net10.0/OmpGui.dll
if [ -n "${APP_BIN:-}" ]; then RUN=("$APP_BIN"); else RUN=(dotnet "$APP"); fi
export DISPLAY=${DISPLAY:-:99}
mkdir -p "$OUT"
log() { echo "$(date -u +%FT%T.%3NZ) $*" | tee -a "$OUT/events.log"; }

xdpyinfo >/dev/null 2>&1 || { Xvfb "$DISPLAY" -screen 0 1400x900x24 >/dev/null 2>&1 & sleep 1; }
pgrep -x openbox >/dev/null || { openbox >/dev/null 2>&1 & sleep 1; }

log "launch app (LONGTASK $TASK)"
"${RUN[@]}" --config "$CONFIG" --timeline "$OUT/timeline.csv" --startup-log "$OUT/startup.log" \
  --auto-prompt "LONGTASK $TASK minutes: keep checking" > "$OUT/app.out" 2>&1 &
APP_PID=$!
echo "$APP_PID" > "$OUT/app.pid"

# wait for the run to start
for _ in $(seq 1 120); do
  grep -q ",Running," "$OUT/timeline.csv" 2>/dev/null && break; sleep 1
done
WIN=$(xdotool search --sync --pid "$APP_PID" --name "OMP GUI" | head -1)
log "running; window $WIN"
sleep 5
import -window root "$OUT/1-before-minimize.png"

xdotool windowminimize "$WIN"; sleep 1
log "minimized; _NET_WM_STATE: $(xprop -id "$WIN" _NET_WM_STATE | cut -d= -f2)"
xmessage -geometry 600x200+100+100 "another application has focus" & OTHER=$!
sleep 1; log "other app focused: $(xdotool getactivewindow getwindowname 2>/dev/null || echo '?')"
MIN_AT=$(date +%s)

tree() { local p=$1; echo "$p"; for c in $(pgrep -P "$p"); do tree "$c"; done; }
ticks() { local t=0; for p in "$@"; do [ -r /proc/$p/stat ] && t=$((t + $(awk '{print $14+$15}' /proc/$p/stat))); done; echo $t; }
rss() { local r=0; for p in "$@"; do [ -r /proc/$p/status ] && r=$((r + $(awk '/VmRSS/ {print $2}' /proc/$p/status))); done; echo $((r / 1024)); }
HZ=$(getconf CLK_TCK)
OMP_ROOT=$(pgrep -P "$APP_PID" | head -1)
PREV_GUI=$(ticks "$APP_PID"); PREV_OMP=$(ticks $(tree "$OMP_ROOT")); PREV_T=$(date +%s)
sample() {
  local omp_pids; omp_pids=$(tree "$OMP_ROOT")
  local now; now=$(date +%s); local dt=$((now - PREV_T)); [ $dt -lt 1 ] && dt=1
  local g; g=$(ticks "$APP_PID"); local o; o=$(ticks $omp_pids)
  local gcpu; gcpu=$(awk -v a=$((g - PREV_GUI)) -v d=$dt -v hz=$HZ 'BEGIN {printf "%.1f", a / hz / d * 100}')
  local ocpu; ocpu=$(awk -v a=$((o - PREV_OMP)) -v d=$dt -v hz=$HZ 'BEGIN {printf "%.1f", a / hz / d * 100}')
  PREV_GUI=$g; PREV_OMP=$o; PREV_T=$now
  log "sample gui_rss_mib=$(rss "$APP_PID") gui_cpu_pct=$gcpu omp_rss_mib=$(rss $omp_pids) omp_cpu_pct=$ocpu omp_procs=$(echo $omp_pids | wc -w) hidden=$(xprop -id "$WIN" _NET_WM_STATE | grep -c HIDDEN) timeline=$(tail -1 "$OUT/timeline.csv")"
}
while [ $(( $(date +%s) - MIN_AT )) -lt $(( MINUTES * 60 )) ]; do
  sleep 60; sample
done
log "background period over: $(( ($(date +%s) - MIN_AT) )) s minimized"
kill "$OTHER" 2>/dev/null || true

wmctrl -i -a "$(printf '0x%08x' "$WIN")" || xdotool windowactivate "$WIN"
log "restored"
sleep 0.5; import -window root "$OUT/2-after-restore-0.5s.png"
log "screenshot 0.5 s after restore; core now: $(tail -1 "$OUT/timeline.csv")"
sleep 5; import -window root "$OUT/3-after-restore-5s.png"
# Visible again: keep sampling until the task finishes (or 8 minutes), then capture the final state.
for _ in $(seq 1 16); do
  sleep 30; sample
  tail -1 "$OUT/timeline.csv" | grep -q ",Ready," && break
done
import -window root "$OUT/4-final.png"
log "final core: $(tail -1 "$OUT/timeline.csv")"
# Own processes only: the app and the omp tree it started, recorded before closing (never a name pattern).
OWN=$(tree "$APP_PID" | tr '\n' ' ')
log "processes before close: $OWN"
wmctrl -i -c "$(printf '0x%08x' "$WIN")" || kill "$APP_PID" 2>/dev/null || true   # window close: the app stops omp itself
for _ in $(seq 1 30); do kill -0 "$APP_PID" 2>/dev/null || break; sleep 0.5; done
LEFT=""; for p in $OWN; do kill -0 "$p" 2>/dev/null && LEFT="$LEFT $p"; done
log "after close: app exited=$(kill -0 "$APP_PID" 2>/dev/null && echo no || echo yes); own processes left:${LEFT:- none}"
