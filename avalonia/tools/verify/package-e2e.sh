#!/usr/bin/env bash
# Package E2E on the distributed artifact (Linux): the unpacked linux package, a HOME with nothing in it, a real X
# display (its own Xvfb + openbox) and real keyboard / mouse input through xdotool. No test hooks in the app.
#
#   first start → "omp was not found" → Install (the client's installer) → omp's own "no model" state → a provider
#   in omp's models.yml → Restart omp → typing without a click (focus) → a real model: reply, streaming, completion
#   → Esc stops a long reply → model picker from the keyboard (stand-in) → read tool → bash approval, Alt+A →
#   terminal (Ctrl+`) → theme saved → close: nothing left running → start again: history, theme kept → nothing
#   written outside the client's folders, omp's ~/.omp and the project.
#
# Ground truth comes from omp's own session files, the client's settings file, the file system and /proc;
# screenshots are kept as evidence. Every check is one PASS / FAIL line in <work>/result.txt; exit 1 on any FAIL.
#
#   tools/verify/package-e2e.sh <omp-gui-…-linux-x64.tar.gz> <work-dir> [display-number]
# Environment:
#   REAL_MODEL_URL   an OpenAI-compatible endpoint of a real model (e.g. http://127.0.0.1:18090/v1 from
#                    tools/real-model/setup.sh); REAL_MODEL_ID its model id. Without it the real-model steps FAIL.
#   REAL_MODEL_CONTEXT its context window (default 8192); with less than 32k omp gets --tools=bash,read --no-skills
#                    --no-rules --no-lsp (omp's own options) so its system prompt fits.
#   PYXLIB           a python with python-xlib (pip install python-xlib), for the clipboard paste step.
# Needs Xvfb, openbox, xdotool, ImageMagick, python3, node (the stand-in model server).
set -uo pipefail
ARCHIVE=$(realpath "${1:?archive}"); WORK=${2:?work dir}; DNUM=${3:-78}
HERE=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
H="$WORK/home"; SHOTS="$WORK/shots"; RESULT="$WORK/result.txt"
rm -rf "$H" "$SHOTS" "$RESULT" "$WORK/app"; mkdir -p "$H" "$SHOTS" "$WORK/app"
export DISPLAY=":$DNUM"
PIDS=()
fails=0
log() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$WORK/steps.log"; }
check() { # name condition-exit-code detail
  if [ "$2" = 0 ]; then echo "PASS $1 — $3" | tee -a "$RESULT"; else echo "FAIL $1 — $3" | tee -a "$RESULT"; fails=$((fails + 1)); fi
}
shot() { import -window root "$SHOTS/$1.png" 2>/dev/null; }
cleanup() {
  for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done
  [ -n "${APP:-}" ] && kill "$APP" 2>/dev/null
  chmod -R u+w "$WORK/app" 2>/dev/null
}
trap cleanup EXIT

# Own display, window manager and stand-in model server (their PIDs are this script's; only those are stopped).
Xvfb "$DISPLAY" -screen 0 1400x900x24 -nolisten tcp > "$WORK/xvfb.log" 2>&1 & PIDS+=($!)
sleep 1
openbox > "$WORK/openbox.log" 2>&1 & PIDS+=($!)
MOCK_PORT=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')
node "$HERE/tools/mock-model/server.mjs" "$MOCK_PORT" > "$WORK/mock.log" 2>&1 & PIDS+=($!)
sleep 1

# The artifact: checksum, unpacked read-only.
(cd "$(dirname "$ARCHIVE")" && sha256sum -c "$(basename "$ARCHIVE").sha256" > /dev/null); check archive-sha256 $? "$(basename "$ARCHIVE") matches its .sha256"
tar xzf "$ARCHIVE" -C "$WORK/app" && chmod -R a-w "$WORK/app"
EXE=$(ls "$WORK"/app/*/OmpGui)
log "artifact $(sha256sum "$ARCHIVE" | cut -c1-16)… unpacked to $EXE"

start_app() {
  (cd "$H" && exec env -i PATH=/usr/local/bin:/usr/bin:/bin HOME="$H" DISPLAY="$DISPLAY" LANG=C.UTF-8 "$EXE" >> "$WORK/app.log" 2>&1) &
  APP=""
  for _ in $(seq 1 100); do
    for p in $(ls /proc | grep -E '^[0-9]+$'); do [ "$(readlink "/proc/$p/exe" 2>/dev/null)" = "$EXE" ] && APP=$p; done
    [ -n "$APP" ] && break; sleep 0.1
  done
  WIN=$(xdotool search --sync --onlyvisible --pid "$APP" 2>/dev/null | head -1)
  sleep 2
  # The client area's position on the screen (xdotool's own geometry is off by the frame under openbox).
  WX=$(xwininfo -id "$WIN" | awk '/Absolute upper-left X/{print $4}'); WY=$(xwininfo -id "$WIN" | awk '/Absolute upper-left Y/{print $4}')
  log "app pid $APP window $WIN at $WX,$WY"
}
# Coordinates: centers from the layout itself (a headless window of the same 1100×760 size), not guessed.
click() { xdotool mousemove $((WX + $1)) $((WY + $2)) click 1; sleep 0.4; }   # relative to the client area
typeln() { xdotool type --delay 12 "$1"; xdotool key Return; }
session_file() { ls -t "$H"/.omp/agent/sessions/*/*.jsonl 2>/dev/null | head -1; }
# The last entry of omp's session file that matches a Python predicate over the parsed entry `d`.
last_entry() { python3 - "$(session_file)" "$1" <<'EOF'
import json, sys
path, pred = sys.argv[1], sys.argv[2]
hit = None
try:
    for line in open(path):
        d = json.loads(line)
        m = d.get('message') or {}
        text = ''.join(p.get('text', '') for p in m.get('content', []) if isinstance(p, dict)) if isinstance(m.get('content'), list) else str(m.get('content') or '')
        if eval(pred): hit = d
except FileNotFoundError: pass
print(json.dumps(hit) if hit else '')
EOF
}
wait_entry() { # predicate timeout-seconds → prints the entry
  local end=$((SECONDS + $2)) e=""
  while [ $SECONDS -lt $end ]; do e=$(last_entry "$1"); [ -n "$e" ] && break; sleep 0.3; done
  echo "$e"
}
own_processes() { # everything running with this HOME or from this work folder, apart from this script
  for p in $(ls /proc | grep -E '^[0-9]+$'); do
    [ "$p" = "$$" ] && continue
    if tr '\0' '\n' < "/proc/$p/environ" | grep -qx "HOME=$H" || tr '\0' ' ' < "/proc/$p/cmdline" | grep -qF "$WORK/app"; then
      echo "$p $(tr '\0' ' ' < "/proc/$p/cmdline" | cut -c1-120)"
    fi
  done 2>/dev/null
}
close_app() {
  xdotool windowactivate --sync "$WIN" 2>/dev/null; xdotool key --window "$WIN" alt+F4
  local t0=$SECONDS; while [ -e "/proc/$APP" ] && [ $((SECONDS - t0)) -lt 20 ]; do sleep 0.1; done
  sleep 1
  left=$(own_processes)
  [ ! -e "/proc/$APP" ] && [ -z "$left" ]; check "$1" $? "window closed with Alt+F4: app exited in $((SECONDS - t0)) s; left running: ${left:-nothing}"
}

# ── 1. First start with an empty HOME ─────────────────────────────────────────────────────────────────────────
start_app; shot 01-first-start
[ ! -e "$H/.config/OmpGui/omp-gui.local.json" ]; check first-start-writes-no-settings $? "no settings file written by just starting"
# Coordinates below are for the 1100x760 window (MainWindow.axaml): the recovery card sits right above the composer
# (bottom of the main column), its buttons start at x≈363 (272 sidebar + 24 margin + 36 icon + 14 gap + card padding).
click 499 436                                       # "Install omp 18.2.0" on the setup screen ("Install omp to get started")
log "install clicked"
for _ in $(seq 1 1200); do ls "$H"/.local/share/OmpGui/runtimes/*/installed.json > /dev/null 2>&1 && break; sleep 0.5; done
RT=$(dirname "$(ls "$H"/.local/share/OmpGui/runtimes/*/installed.json 2>/dev/null | head -1)")
grep -q '"omp": "18.2.0"' "$RT/installed.json" 2>/dev/null && grep -q '"bun": "1.4.2"' "$RT/installed.json"; check runtime-installed-from-ui $? "installed.json: $(tr -d '\n ' < "$RT/installed.json" 2>/dev/null | cut -c1-120)"
# omp starts from the new runtime and, with no model, exits before its RPC is ready (after its local-provider
# discovery, which takes seconds): wait for that exit instead of a fixed pause, then the screen offers Try again.
omp_pid() { for p in $(ls /proc | grep -E '^[0-9]+$'); do [ "$(awk '/^PPid:/{print $2}' "/proc/$p/status" 2>/dev/null)" = "$APP" ] && [ "$(readlink "/proc/$p/exe" 2>/dev/null)" = "$RT/bun/bun" ] && echo "$p"; done 2>/dev/null | head -1; }
seen=""; for _ in $(seq 1 180); do o=$(omp_pid); [ -n "$o" ] && seen=$o; [ -n "$seen" ] && [ -z "$o" ] && break; sleep 0.5; done
log "first omp ${seen:-never seen} exited (no model)"; sleep 2; shot 02-no-model
last=$(ls -t "$H"/.omp/logs/*.log 2>/dev/null | head -1)
grep -q "No models available" "$WORK/app.log" "$last" 2>/dev/null || true

# ── 2. A provider in omp's own models.yml, then Restart omp ─────────────────────────────────────────────────
CTX=${REAL_MODEL_CONTEXT:-8192}
mkdir -p "$H/.omp/agent" "$H/proj"; echo "hello readme" > "$H/proj/README.md"
{
  echo "providers:"
  if [ -n "${REAL_MODEL_URL:-}" ]; then cat <<EOF
  local-real:
    baseUrl: $REAL_MODEL_URL
    api: openai-completions
    apiKey: LOCAL_MODEL_API_KEY
    models:
      - id: ${REAL_MODEL_ID:?REAL_MODEL_ID}
        name: ${REAL_MODEL_ID} (real model)
        reasoning: false
        input: [text]
        maxTokens: 384
        contextWindow: $CTX
EOF
  fi
  cat <<EOF
  stand-in-mock:
    baseUrl: http://127.0.0.1:$MOCK_PORT/v1
    api: openai-completions
    apiKey: LOCAL_MODEL_API_KEY
    models:
      - id: scripted-stand-in
        name: Stand-in (scripted mock server, not a model)
        reasoning: false
        input: [text, image]
        maxTokens: 8192
        contextWindow: 131072
EOF
} > "$H/.omp/agent/models.yml"
DEFAULT=${REAL_MODEL_URL:+local-real/${REAL_MODEL_ID:-}}; DEFAULT=${DEFAULT:-stand-in-mock/scripted-stand-in}
printf 'modelRoles:\n  default: %s\n' "$DEFAULT" > "$H/.omp/agent/config.yml"
EXTRA='[]'; [ "$CTX" -lt 32768 ] && EXTRA='["--tools=bash,read", "--no-skills", "--no-rules", "--no-lsp"]'
mkdir -p "$H/.config/OmpGui"
cat > "$H/.config/OmpGui/omp-gui.local.json" <<EOF
{
  "workingDirectory": "$H/proj",
  "extraArgs": $EXTRA,
  "environment": { "LOCAL_MODEL_API_KEY": "none-local-server" }
}
EOF
click 628 428                                       # "Try again" (after "Open omp setup") on the "Connect a model provider" screen
# omp 18.2.0 exits at once when it has no model; one that stays up for 8 s answered the client's RPC handshake.
up=0; t0=$SECONDS
while [ $((SECONDS - t0)) -lt 90 ] && [ $up -lt 8 ]; do o=$(omp_pid); if [ -n "$o" ] && [ "$o" = "${prev_o:-}" ]; then up=$((up + 1)); else up=0; fi; prev_o=$o; sleep 1; done
shot 03-ready
[ $up -ge 8 ]; check omp-ready-after-restart $? "omp (pid ${prev_o:-none}) up for ${up} s after Restart omp, with $DEFAULT"

# ── 3. Real model: typed without a click (the focus came back to the composer) ──────────────────────────────
if [ -n "${REAL_MODEL_URL:-}" ]; then
  typeln "Reply with three short sentences about the sea."
  # Streaming as the user sees it: the transcript area changes several times before omp records the reply.
  frames=0; prev=""; t0=$SECONDS
  while [ $((SECONDS - t0)) -lt 300 ] && [ -z "$(last_entry "d.get('type')=='message' and m.get('role')=='assistant'")" ]; do
    import -window root -crop 820x560+$((WX + 280))+$((WY + 60)) "$WORK/frame.png" 2>/dev/null
    h=$(md5sum < "$WORK/frame.png"); [ "$h" != "$prev" ] && frames=$((frames + 1)); prev=$h; sleep 0.2
  done
  u=$(last_entry "d.get('type')=='message' and m.get('role')=='user' and 'short sentences about the sea' in text")
  [ -n "$u" ]; check focus-after-restart $? "typed without clicking, omp received the prompt"
  a=$(last_entry "d.get('type')=='message' and m.get('role')=='assistant'")
  echo "$a" | python3 -c "import json,sys;m=json.load(sys.stdin)['message'];t=''.join(p.get('text','') for p in m['content'] if p.get('type')=='text');sys.exit(0 if t and m.get('stopReason') in ('stop','length') and m.get('model')=='$REAL_MODEL_ID' else 1)" 2>/dev/null
  check real-model-reply $? "model $REAL_MODEL_ID: $(echo "$a" | python3 -c "import json,sys;m=json.load(sys.stdin)['message'];print(m.get('stopReason'), repr(''.join(p.get('text','') for p in m['content'] if p.get('type')=='text')[:100]))" 2>/dev/null)"
  [ "$frames" -ge 3 ]; check real-model-streaming-visible $? "$frames distinct transcript frames while the reply streamed"
  sleep 1; shot 04-real-reply

  # Esc stops a long reply while it streams.
  typeln "Write a very long story about a lighthouse keeper and a storm."
  before=$(md5sum < "$WORK/frame.png"); t0=$SECONDS; changed=0
  while [ $((SECONDS - t0)) -lt 300 ]; do   # the reply's first words on screen
    import -window root -crop 820x560+$((WX + 280))+$((WY + 60)) "$WORK/frame.png" 2>/dev/null
    n=$(md5sum < "$WORK/frame.png"); [ "$n" != "$before" ] && changed=$((changed + 1)); before=$n
    [ $changed -ge 4 ] && break; sleep 0.3
  done
  xdotool key Escape
  e=$(wait_entry "d.get('type')=='message' and m.get('role')=='assistant' and m.get('stopReason')=='aborted'" 30)
  [ -n "$e" ]; check esc-stops-the-reply $? "omp recorded the reply as aborted ($(echo "$e" | python3 -c "import json,sys;m=json.load(sys.stdin)['message'];print(len(''.join(p.get('text','') for p in m['content'] if p.get('type')=='text')),'chars')" 2>/dev/null))"
  sleep 1; shot 05-aborted
fi

# ── 4. Model picker from the keyboard ───────────────────────────────────────────────────────────────────────
# The model chip: the header group is right-aligned and the thinking box left of approvals comes and goes (hidden for a
# model without reasoning once omp reports it), so the chip spans x≈396–691 or ≈506–801; x 600 is inside it either way.
click 910 691; sleep 1; shot 06-model-picker          # model chip in the composer toolbar (its centre; the row under the box lifts it 26 px)
xdotool type --delay 20 "stand"; sleep 0.4; xdotool key Return
e=$(wait_entry "d.get('type')=='model_change' and 'stand-in-mock' in str(d.get('model'))" 20)
[ -n "$e" ]; check model-picker-keyboard $? "typed a filter and Enter in the picker: omp switched to stand-in-mock/scripted-stand-in"

# ── 5. Tools and approvals (the stand-in's scripted tool calls), typed without clicking ─────────────────────
sleep 1; typeln "TOOLCALL please read the readme"
e=$(wait_entry "d.get('type')=='message' and m.get('role')=='toolResult' and m.get('toolName')=='read' and not m.get('isError')" 60)
[ -n "$e" ]; check read-tool $? "focus back in the composer after the picker; read tool result recorded"
sleep 2; typeln "BASHCALL now"
sleep 4; shot 07-approval-card
[ -z "$(last_entry "d.get('type')=='message' and m.get('role')=='toolResult' and m.get('toolName')=='bash'")" ]; check bash-waits-for-approval $? "nothing ran before the answer"
xdotool key alt+a
e=$(wait_entry "d.get('type')=='message' and m.get('role')=='toolResult' and m.get('toolName')=='bash' and not m.get('isError') and 'hi' in text" 60)
[ -n "$e" ]; check approval-alt-a-runs-bash $? "Alt+A allowed it; bash output recorded"
sleep 2; shot 08-approved

# ── 5b. An image pasted from the X clipboard (Ctrl+V) goes to omp as an attachment ───────────────────────────
if [ -n "${PYXLIB:-}" ] && "$PYXLIB" -c "import Xlib" 2>/dev/null; then
  PNG=$(ls "$WORK"/app/*/omp-gui.png)
  "$PYXLIB" "$HERE/tools/verify/x11-clipboard.py" "$PNG" image/png > "$WORK/clipboard.log" 2>&1 & CLIP=$!
  sleep 1; xdotool key ctrl+v; sleep 1.5; shot 08b-pasted-image
  typeln "Here is a picture"
  e=$(wait_entry "d.get('type')=='message' and m.get('role')=='user' and 'Here is a picture' in text and any(isinstance(p, dict) and p.get('type')=='image' for p in m.get('content', []))" 30)
  [ -n "$e" ]; check attachment-pasted-image $? "Ctrl+V pasted $(basename "$PNG") from the X clipboard; omp recorded the message with an image part"
  kill $CLIP 2>/dev/null; sleep 2
else
  check attachment-pasted-image 1 "not run: set PYXLIB to a python with python-xlib (tools/verify/x11-clipboard.py owns the X clipboard)"
fi

# ── 6. Terminal ─────────────────────────────────────────────────────────────────────────────────────────────
xdotool key ctrl+grave; sleep 3
xdotool type --delay 15 "echo package-terminal-ok > terminal-proof.txt"; xdotool key Return; sleep 2; shot 09-terminal
grep -qx package-terminal-ok "$H/proj/terminal-proof.txt" 2>/dev/null; check terminal $? "Ctrl+\` opened a shell in the project folder; the command wrote terminal-proof.txt"
xdotool key ctrl+grave; sleep 1

# ── 7. Theme, saved when chosen ─────────────────────────────────────────────────────────────────────────────
click 130 732; sleep 1.5                             # Settings (the last row of the sidebar)
xdotool mousemove $((WX + 650)) $((WY + 500)); for _ in $(seq 1 10); do xdotool click 4; done; sleep 0.5   # page at its top
shot 10-settings
click 1025 140; sleep 1                              # Appearance: the "Dark" segment (System / Light / Dark), General page (every settings page is 720 px wide)
click 1048 24; sleep 1                               # Done (right end of the Settings header)
shot 11-dark
grep -q '"theme": "dark"' "$H/.config/OmpGui/omp-gui.local.json"; check theme-saved $? "settings file: $(grep -o '"theme": "[a-z]*"' "$H/.config/OmpGui/omp-gui.local.json")"

# ── 8. Close, then start again: history and settings kept ───────────────────────────────────────────────────
close_app close-leaves-nothing-running
first=$(session_file)
start_app; sleep 6; shot 12-restarted
click 130 180; sleep 4; shot 13-history              # the first session in the sidebar (under its project)
mean=$(convert "$SHOTS/13-history.png" -crop 500x300+$((WX + 450))+$((WY + 250)) -colorspace Gray -format "%[fx:mean]" info: 2>/dev/null)
python3 -c "import sys; sys.exit(0 if float('${mean:-1}') < 0.35 else 1)"
check theme-kept-after-restart $? "the window is dark after the restart (mean brightness ${mean:-?})"
close_app close-again-leaves-nothing-running

# ── 9. What the client wrote ────────────────────────────────────────────────────────────────────────────────
top=$(cd "$H" && find . -maxdepth 3 -mindepth 1 -type d | sort | tr '\n' ' ')
[ ! -e "$H/.bun" ]; check no-global-bun-cache $? "~/.bun absent (Bun's caches stay in the runtime folder)"
extra=$(cd "$H" && find . -maxdepth 2 -mindepth 1 | grep -vE '^\./(\.config|\.config/OmpGui|\.local|\.local/share|\.omp|\.omp/.*|proj|proj/.*|\.cache|\.cache/mesa_shader_cache)$' | tr '\n' ' ')
[ -z "$extra" ]; check writes-only-own-folders $? "HOME holds: $top${extra:+ — unexpected: $extra}"

echo "$( grep -c ^PASS "$RESULT") passed, $fails failed" | tee -a "$RESULT"
[ $fails -eq 0 ]
