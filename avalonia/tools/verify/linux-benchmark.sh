#!/usr/bin/env bash
# Linux (Xvfb + openbox) startup, memory and CPU of the client with the Original OMP Core runtime, a harness
# profile and the stand-in model. Runs with the same machine, runtime, profile and model are comparable (for
# example two builds of the client, each under its own label).
#
#   tools/verify/linux-benchmark.sh <out-dir> <label> <home-dir> -- <client command...>
#
# Startup "usable" = process start -> window mapped and painted (>= 40 distinct colours in a screenshot).
# Memory = RSS summed over the process tree; the omp tree (bun running cli.ts, and its children) is
# reported separately from the GUI tree. CPU = utime+stime delta over a window, % of one core.
# The streaming scenario types "SLOW reply" into the composer (focused, or clicked at CLICK_REL="fx,fy") and presses Enter.
set -euo pipefail
OUT=${1:?out}; LABEL=${2:?label}; HOMEDIR=${3:?home}; shift 3; [ "${1:-}" = "--" ] && shift
export DISPLAY=${DISPLAY:-:99}
mkdir -p "$OUT"
LOG="$OUT/$LABEL.log"; : > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }
HZ=$(getconf CLK_TCK)

tree() { local p=$1; echo "$p"; for c in $(pgrep -P "$p" 2>/dev/null); do tree "$c"; done; }
split() { # $1 root -> sets GUI_PIDS, OMP_PIDS
  GUI_PIDS=""; OMP_PIDS=""
  for p in $(tree "$1"); do
    if tr '\0' ' ' < /proc/$p/cmdline 2>/dev/null | grep -q "cli.ts"; then OMP_PIDS="$OMP_PIDS $(tree "$p")"; fi
  done
  for p in $(tree "$1"); do case " $OMP_PIDS " in *" $p "*) ;; *) GUI_PIDS="$GUI_PIDS $p";; esac; done
}
rss() { local r=0; for p in "$@"; do [ -r /proc/$p/status ] && r=$((r + $(awk '/VmRSS/ {print $2}' /proc/$p/status))); done; echo $((r / 1024)); }
ticks() { local t=0; for p in "$@"; do [ -r /proc/$p/stat ] && t=$((t + $(awk '{print $14+$15}' /proc/$p/stat))); done; echo $t; }
cpu() { # $1 seconds, rest pids -> % of one core
  local s=$1; shift; local a; a=$(ticks "$@"); sleep "$s"; local b; b=$(ticks "$@")
  awk -v d=$((b - a)) -v s="$s" -v hz="$HZ" 'BEGIN {printf "%.2f", d / hz / s * 100}'
}
# first visible top-level window owned by any process in the tree of $1
window_of() {
  local pids; pids=" $(tree "$1" | tr '\n' ' ') "
  for w in $(xdotool search --onlyvisible --name '.' 2>/dev/null || true); do
    local p; p=$(xdotool getwindowpid "$w" 2>/dev/null || true)
    [ -n "$p" ] || continue   # windows without _NET_WM_PID (e.g. xmessage) are never ours
    case "$pids" in *" $p "*) echo "$w"; return 0;; esac
  done
  return 0
}
painted() { local c; c=$(import -window "$1" png:- 2>/dev/null | convert png:- -format %k info: 2>/dev/null || true); [ "${c:-0}" -ge 40 ]; }

start_gui() {
  local t0; t0=$(date +%s%N)
  env HOME="$HOMEDIR" "$@" > "$OUT/$LABEL.stdout" 2>&1 &
  PID=$!
  local w=""
  for _ in $(seq 1 600); do w=$(window_of "$PID"); [ -n "$w" ] && break; sleep 0.05; done
  for _ in $(seq 1 600); do [ -n "$w" ] && painted "$w" && break; sleep 0.05; w=${w:-$(window_of "$PID")}; done
  local t1; t1=$(date +%s%N)
  WIN=$w; USABLE_MS=$(( (t1 - t0) / 1000000 ))
}
stop_gui() { kill "$PID" 2>/dev/null || true; for _ in $(seq 1 100); do kill -0 "$PID" 2>/dev/null || break; sleep 0.1; done; kill -9 "$PID" 2>/dev/null || true; sleep 2; }

# 1. startup, 5 runs
for i in 1 2 3 4 5; do
  start_gui "$@"; say "startup_run$i usable_ms=$USABLE_MS"; stop_gui
done

# 2. idle / streaming / background on one more instance
start_gui "$@"
sleep 30
split "$PID"
say "idle gui_rss_mib=$(rss $GUI_PIDS) gui_procs=$(echo $GUI_PIDS | wc -w) omp_rss_mib=$(rss $OMP_PIDS) omp_procs=$(echo $OMP_PIDS | wc -w)"
say "idle_active gui_cpu_pct=$(cpu 30 $GUI_PIDS) omp_cpu_pct=$(cpu 1 $OMP_PIDS)"
xmessage -geometry 300x100+1000+750 "other app" & OTHER=$!; sleep 2
say "idle_inactive gui_cpu_pct=$(cpu 30 $GUI_PIDS)"
kill $OTHER 2>/dev/null || true; sleep 1
xdotool windowactivate --sync "$WIN" 2>/dev/null || true
if [ -n "${CLICK_REL:-}" ]; then # "fx,fy": click the composer at fractions of the window size
  eval "$(xdotool getwindowgeometry --shell "$WIN")"
  xdotool mousemove --window "$WIN" "$(awk -v f="${CLICK_REL%,*}" -v w="$WIDTH" 'BEGIN {printf "%d", f*w}')" \
    "$(awk -v f="${CLICK_REL#*,}" -v h="$HEIGHT" 'BEGIN {printf "%d", f*h}')" click 1
  sleep 0.5
fi
xdotool type --delay 20 "SLOW reply"; xdotool key Return
sleep 8
split "$PID"
say "streaming gui_rss_mib=$(rss $GUI_PIDS) omp_rss_mib=$(rss $OMP_PIDS)"
say "streaming gui_cpu_pct=$(cpu 20 $GUI_PIDS)"
import -window root "$OUT/$LABEL-streaming.png"
xdotool windowminimize "$WIN"; sleep 2
say "background gui_cpu_pct=$(cpu 20 $GUI_PIDS) gui_rss_mib=$(rss $GUI_PIDS)"
stop_gui
say "done"
