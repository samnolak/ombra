#!/usr/bin/env bash
# The packaged client in a real X11 window at 100 %, 150 % and 200 % (AVALONIA_GLOBAL_SCALE_FACTOR), in three window
# sizes and both themes, for the design review: what headless tests cannot show (they render at 100 % only).
# omp is the test suite's fake (tests/OmpGui.FakeOmp, scenario "plan"): a conversation with a plan and tool calls.
#
#   tools/verify/scale-shots.sh <package.tar.gz> <out-dir> [display]
# Needs Xvfb, openbox, xdotool, ImageMagick, dotnet and a built tests/OmpGui.FakeOmp.
set -euo pipefail
ARCHIVE=$(realpath "${1:?archive}"); OUT=$(mkdir -p "${2:?out dir}" && cd "$2" && pwd); DNUM=${3:-97}
HERE=$(cd "$(dirname "$0")/../.." && pwd)
FAKE="$HERE/tests/OmpGui.FakeOmp/bin/Debug/net10.0/OmpGui.FakeOmp.dll"
[ -f "$FAKE" ] || { echo "build tests/OmpGui.FakeOmp first" >&2; exit 2; }
DOTNET=$(command -v dotnet)
export DISPLAY=":$DNUM"
WORK=$(mktemp -d); PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done; rm -rf "$WORK"; }
trap cleanup EXIT
Xvfb "$DISPLAY" -screen 0 3900x2300x24 -nolisten tcp > "$OUT/xvfb.log" 2>&1 & PIDS+=($!)
sleep 1
openbox > "$OUT/openbox.log" 2>&1 & PIDS+=($!)
mkdir -p "$WORK/app" && tar xzf "$ARCHIVE" -C "$WORK/app"
EXE=$(ls "$WORK"/app/*/OmpGui)

for theme in light dark; do
  for scale in 1 1.5 2; do
    for size in 1280x800 1024x768 1920x1080; do
      w=${size%x*}; h=${size#*x}
      H="$WORK/home-$theme-$scale-$size"; mkdir -p "$H/.config/OmpGui" "$H/proj" "$H/sessions"
      cat > "$H/.config/OmpGui/omp-gui.local.json" <<JSON
{
  "command": "$DOTNET",
  "prefixArgs": ["$FAKE", "plan"],
  "workingDirectory": "$H/proj",
  "theme": "$theme",
  "environment": { "FAKE_SESSION_DIR": "$H/sessions", "FAKE_MODEL": "local/coder" }
}
JSON
      (cd "$H" && exec env -i PATH=/usr/local/bin:/usr/bin:/bin HOME="$H" DISPLAY="$DISPLAY" LANG=C.UTF-8 \
        AVALONIA_GLOBAL_SCALE_FACTOR="$scale" "$EXE" --auto-prompt "plan it" >> "$OUT/app.log" 2>&1) &
      APP=""
      for _ in $(seq 1 100); do
        for p in $(ls /proc | grep -E '^[0-9]+$'); do [ "$(readlink "/proc/$p/exe" 2>/dev/null)" = "$EXE" ] && APP=$p; done
        [ -n "$APP" ] && break; sleep 0.1
      done
      WIN=$(xdotool search --sync --onlyvisible --pid "$APP" 2>/dev/null | head -1)
      pw=$(python3 -c "print(int($w*$scale))"); ph=$(python3 -c "print(int($h*$scale))")
      xdotool windowmove "$WIN" 0 0 windowsize "$WIN" "$pw" "$ph"
      sleep 6
      WX=$(xwininfo -id "$WIN" | awk '/Absolute upper-left X/{print $4}'); WY=$(xwininfo -id "$WIN" | awk '/Absolute upper-left Y/{print $4}')
      CW=$(xwininfo -id "$WIN" | awk '/Width:/{print $2}'); CH=$(xwininfo -id "$WIN" | awk '/Height:/{print $2}')
      name="$theme-${size}-x$scale"
      import -window root -crop "${CW}x${CH}+${WX}+${WY}" "$OUT/$name-conversation.png"
      # The Views menu (⋮) and the Plan pane: Ctrl+Shift+P
      xdotool key --window "$WIN" ctrl+shift+p; sleep 1.5
      import -window root -crop "${CW}x${CH}+${WX}+${WY}" "$OUT/$name-plan.png"
      xdotool key --window "$WIN" ctrl+shift+p; sleep 0.8
      # Settings: the last row of the sidebar (130, height − 28 in layout pixels)
      xdotool mousemove $((WX + $(python3 -c "print(int(130*$scale))"))) $((WY + CH - $(python3 -c "print(int(28*$scale))"))) click 1; sleep 1.5
      import -window root -crop "${CW}x${CH}+${WX}+${WY}" "$OUT/$name-settings.png"
      echo "$name ${CW}x${CH}" >> "$OUT/shots.txt"
      kill "$APP" 2>/dev/null || true; sleep 1
      pkill -f "$FAKE" 2>/dev/null || true
    done
  done
done
echo "done: $(wc -l < "$OUT/shots.txt") sizes, $(ls "$OUT"/*.png | wc -l) screenshots in $OUT"
