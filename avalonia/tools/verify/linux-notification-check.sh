#!/usr/bin/env bash
# Notification check on the distributed Linux package: a run that ends while the window is minimized must reach the
# freedesktop notification service through notify-send (libnotify), with the answer's first line as its text.
# The service is tools/verify/notification-server.py on a private session bus (no desktop shell here): this checks
# the client → libnotify → D-Bus contract, not how a particular desktop draws the notification.
#
#   tools/verify/linux-notification-check.sh <omp-gui-…-linux-x64.tar.gz> <work-dir> [display-number]
# Needs: notify-send (libnotify-bin), dbus-daemon, Xvfb, openbox, xdotool, node; PYDBUS = a python with dbus-next.
set -uo pipefail
ARCHIVE=$(realpath "${1:?archive}"); WORK=${2:?work dir}; DNUM=${3:-80}
HERE=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd); H="$WORK/home"
chmod -R u+w "$WORK/app" 2>/dev/null; rm -rf "$WORK/app" "$H" "$WORK/notifications.jsonl"; mkdir -p "$WORK/app" "$H/proj"
export DISPLAY=":$DNUM"; PIDS=()
cleanup() { [ -n "${APP:-}" ] && kill "$APP" 2>/dev/null; for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; }
trap cleanup EXIT
result() { echo "$1" | tee -a "$WORK/result.txt"; }
: > "$WORK/result.txt"

Xvfb "$DISPLAY" -screen 0 1400x900x24 -nolisten tcp > "$WORK/xvfb.log" 2>&1 & PIDS+=($!); sleep 1
openbox > "$WORK/openbox.log" 2>&1 & PIDS+=($!)
eval "$(dbus-daemon --session --fork --print-address=1 --print-pid=1 | { read -r a; read -r p; echo "BUS=$a; BUSPID=$p"; })"; PIDS+=("$BUSPID")
DBUS_SESSION_BUS_ADDRESS=$BUS "${PYDBUS:?PYDBUS}" "$HERE/tools/verify/notification-server.py" "$WORK/notifications.jsonl" > "$WORK/server.log" 2>&1 & PIDS+=($!)
MOCK_PORT=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')
node "$HERE/tools/mock-model/server.mjs" "$MOCK_PORT" > "$WORK/mock.log" 2>&1 & PIDS+=($!)
sleep 1; grep -q ready "$WORK/server.log" || { result "FAIL notification server did not start"; exit 1; }

tar xzf "$ARCHIVE" -C "$WORK/app" && chmod -R a-w "$WORK/app"; EXE=$(ls "$WORK"/app/*/OmpGui)
run() { (cd "$H" && exec env -i PATH=/usr/local/bin:/usr/bin:/bin HOME="$H" DISPLAY="$DISPLAY" LANG=C.UTF-8 DBUS_SESSION_BUS_ADDRESS="$BUS" "$@"); }
run "$EXE" --self-test "$WORK/selftest.json" --install-runtime > "$WORK/selftest.log" 2>&1
mkdir -p "$H/.omp/agent" "$H/.config/OmpGui"
cat > "$H/.omp/agent/models.yml" <<EOF
providers:
  stand-in-mock:
    baseUrl: http://127.0.0.1:$MOCK_PORT/v1
    api: openai-completions
    apiKey: LOCAL_MODEL_API_KEY
    models:
      - id: scripted-stand-in
        name: Stand-in (scripted mock server, not a model)
        reasoning: false
        input: [text]
        maxTokens: 8192
        contextWindow: 131072
EOF
printf 'modelRoles:\n  default: stand-in-mock/scripted-stand-in\n' > "$H/.omp/agent/config.yml"
printf '{ "workingDirectory": "%s", "environment": { "LOCAL_MODEL_API_KEY": "none" } }\n' "$H/proj" > "$H/.config/OmpGui/omp-gui.local.json"

run "$EXE" >> "$WORK/app.log" 2>&1 &
for _ in $(seq 1 100); do for p in $(ls /proc | grep -E '^[0-9]+$'); do [ "$(readlink "/proc/$p/exe" 2>/dev/null)" = "$EXE" ] && APP=$p; done; [ -n "${APP:-}" ] && break; sleep 0.1; done
WIN=$(xdotool search --sync --onlyvisible --pid "$APP" | head -1); sleep 12   # omp starts and answers the handshake
xdotool type --delay 12 "SLOWSHORT reply please"; xdotool key Return; sleep 0.5
xdotool windowminimize "$WIN"; t0=$SECONDS
while [ $((SECONDS - t0)) -lt 90 ] && ! grep -q '"omp finished"' "$WORK/notifications.jsonl" 2>/dev/null; do sleep 0.5; done
if grep -q '"omp finished"' "$WORK/notifications.jsonl" 2>/dev/null; then
  result "PASS notification-on-run-end — minimized during a run; notify-send delivered: $(grep '"omp finished"' "$WORK/notifications.jsonl" | head -1)"
else
  result "FAIL notification-on-run-end — nothing arrived in 90 s ($(wc -l < "$WORK/notifications.jsonl" 2>/dev/null || echo 0) notifications)"
fi
xdotool windowactivate "$WIN" 2>/dev/null; sleep 1; import -window root "$WORK/after-restore.png" 2>/dev/null
xdotool key --window "$WIN" alt+F4; for _ in $(seq 1 100); do [ -e "/proc/$APP" ] || break; sleep 0.1; done
[ ! -e "/proc/$APP" ] && result "PASS closed" || result "FAIL app still running"
! grep -q ^FAIL "$WORK/result.txt"
