#!/usr/bin/env bash
# Update E2E on real packages (Linux): an installed OMP GUI updates itself from a signed release feed.
#
#   two self-contained linux-x64 packages (0.9.0 and 0.9.1, tools/package/package.sh) → a throwaway release key →
#   update.json for 0.9.1 (tools/release/make-manifest.py) signed with it (sign-manifest.sh) → served over http from
#   127.0.0.1 (plain http is accepted only on this computer; the signature is what is trusted) → the 0.9.0 package
#   unpacked in a folder of its own:
#     --version says 0.9.0 → --update offers 0.9.1 and changes nothing → a tampered manifest, and one checked with
#     another key, are refused and change nothing → --update --yes --restart: downloaded, verified, unpacked, the app
#     quits, the script swaps the folder, 0.9.0 is kept as .previous, the new version starts (its window on Xvfb) →
#     --version says 0.9.1 → the next check is up to date → the user's settings are untouched.
#
# Every check is one PASS / FAIL line in <work>/result.txt; exit 1 on any FAIL.
#   tools/verify/update-e2e.sh <work-dir> [old.tar.gz new.tar.gz]
# Without the two packages it publishes and packages them (about 2 min). Needs python3, openssl; Xvfb and xdotool
# for the restarted window (without them that one check is reported as skipped).
set -uo pipefail
WORK=${1:?work dir}
HERE=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
RESULT="$WORK/result.txt"; rm -f "$RESULT"
fails=0
PIDS=()
check() { if [ "$2" = 0 ]; then echo "PASS $1 — $3" | tee -a "$RESULT"; else echo "FAIL $1 — $3" | tee -a "$RESULT"; fails=$((fails + 1)); fi; }
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; pkill -f "$WORK/apps/" 2>/dev/null; true; }
trap cleanup EXIT

if [ $# -ge 3 ]; then
  OLD=$(realpath "$2"); NEW=$(realpath "$3")
else
  for v in 0.9.0 0.9.1; do
    rm -rf "$WORK/pub-$v"
    dotnet publish "$HERE/src/OmpGui.App" -c Release -r linux-x64 --self-contained -o "$WORK/pub-$v" \
      -p:Version=$v -p:InformationalVersion=$v >"$WORK/publish-$v.log" 2>&1 || { echo "publish $v failed (see publish-$v.log)"; exit 1; }
    "$HERE/tools/package/package.sh" linux-x64 "$WORK/pub-$v" "$WORK/pkg" $v >/dev/null || exit 1
  done
  OLD="$WORK/pkg/omp-gui-0.9.0-linux-x64.tar.gz"; NEW="$WORK/pkg/omp-gui-0.9.1-linux-x64.tar.gz"
fi

# The release: the new package, its manifest, signed with a throwaway key.
rm -rf "$WORK/key" "$WORK/feed"; mkdir -p "$WORK/feed/bad"
"$HERE/tools/release/keygen.sh" "$WORK/key" >/dev/null
cp "$NEW" "$WORK/feed/"; (cd "$WORK/feed" && sha256sum "$(basename "$NEW")" > "$(basename "$NEW").sha256")
PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
"$HERE/tools/release/make-manifest.py" "$WORK/feed" --version 0.9.1 --base-url "http://127.0.0.1:$PORT" --notes "E2E release" >/dev/null
"$HERE/tools/release/sign-manifest.sh" "$WORK/feed/update.json" "$WORK/key/update-signing-key.pem" "$WORK/key/update-signing.pub" >/dev/null
sed 's/E2E release/E2E release (changed after signing)/' "$WORK/feed/update.json" > "$WORK/feed/bad/update.json"
cp "$WORK/feed/update.json.sig" "$WORK/feed/bad/update.json.sig"
(cd "$WORK/feed" && exec python3 -m http.server "$PORT" --bind 127.0.0.1 >"$WORK/http.log" 2>&1) & PIDS+=($!)
for _ in $(seq 50); do curl -sf "http://127.0.0.1:$PORT/update.json" >/dev/null && break; sleep 0.1; done

# The installed 0.9.0, a HOME with the user's settings, no omp.
rm -rf "$WORK/apps" "$WORK/home"; mkdir -p "$WORK/apps" "$WORK/home/.config/OmpGui"
tar -C "$WORK/apps" -xzf "$OLD"
APP="$WORK/apps/omp-gui-0.9.0-linux-x64"
echo '{ "theme": "dark", "checkForUpdates": false }' > "$WORK/home/.config/OmpGui/omp-gui.local.json"
SETTINGS_SUM=$(sha256sum < "$WORK/home/.config/OmpGui/omp-gui.local.json")
export HOME="$WORK/home" XDG_CONFIG_HOME="$WORK/home/.config" XDG_DATA_HOME="$WORK/home/.local/share"
export OMPGUI_RUNTIME_DIR="$WORK/home/runtimes" OMPGUI_UPDATES_DIR="$WORK/home/updates"
export OMPGUI_UPDATE_PUBLIC_KEY=$(cat "$WORK/key/update-signing.pub")
FEED="http://127.0.0.1:$PORT/update.json"
tree_sum() { (cd "$WORK/apps" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum); }

out=$("$APP/OmpGui" --version); check version-old "$([ "$out" = "OMP GUI 0.9.0 linux-x64" ]; echo $?)" "$out"

before=$(tree_sum)
out=$("$APP/OmpGui" --update --feed "$FEED" 2>&1); rc=$?
check offer "$([ $rc = 3 ] && grep -q 'Version 0.9.1 is available (you have 0.9.0)' <<<"$out" && grep -q 'E2E release' <<<"$out"; echo $?)" "exit $rc: $(tr '\n' ' ' <<<"$out")"
check offer-changes-nothing "$([ "$(tree_sum)" = "$before" ]; echo $?)" "the app folder is as it was"

out=$("$APP/OmpGui" --update --yes --feed "http://127.0.0.1:$PORT/bad/update.json" 2>&1); rc=$?
check tampered-refused "$([ $rc = 1 ] && grep -q 'not trusted' <<<"$out"; echo $?)" "exit $rc: $(tr '\n' ' ' <<<"$out")"
out=$(OMPGUI_UPDATE_PUBLIC_KEY=$(cat "$HERE/packaging/update-signing.pub") "$APP/OmpGui" --update --yes --feed "$FEED" 2>&1); rc=$?
check other-key-refused "$([ $rc = 1 ] && grep -q 'not trusted' <<<"$out"; echo $?)" "exit $rc: $(tr '\n' ' ' <<<"$out")"
check refused-changes-nothing "$([ "$(tree_sum)" = "$before" ] && [ -z "$(ls -A "$WORK/apps" | grep -v '^omp-gui-0.9.0-linux-x64$')" ]; echo $?)" \
  "apps/: $(ls -A "$WORK/apps" | tr '\n' ' ')"

# The update, restarting into the new version on a display of its own.
XVFB=
if command -v Xvfb >/dev/null && command -v xdotool >/dev/null; then
  Xvfb :93 -screen 0 1280x800x24 >/dev/null 2>&1 & PIDS+=($!); XVFB=1; export DISPLAY=:93; sleep 1
fi
RESTART=; [ -n "$XVFB" ] && RESTART=--restart
out=$("$APP/OmpGui" --update --yes $RESTART --feed "$FEED" 2>&1); rc=$?
check install "$([ $rc = 0 ] && grep -q 'Downloaded and verified omp-gui-0.9.1-linux-x64.tar.gz' <<<"$out"; echo $?)" "exit $rc: $(tr '\n' ' ' <<<"$out")"
for _ in $(seq 100); do [ -f "$OMPGUI_UPDATES_DIR/last-update.json" ] && break; sleep 0.2; done
res=$(cat "$OMPGUI_UPDATES_DIR/last-update.json" 2>/dev/null)
check swapped "$(grep -q '"ok":true' <<<"$res"; echo $?)" "last-update.json: $res"
out=$("$APP/OmpGui" --version); check version-new "$([ "$out" = "OMP GUI 0.9.1 linux-x64" ]; echo $?)" "$out"
PREV="$WORK/apps/.omp-gui-0.9.0-linux-x64.previous"
out=$("$PREV/OmpGui" --version 2>&1); check previous-kept "$([ "$out" = "OMP GUI 0.9.0 linux-x64" ]; echo $?)" "$PREV: $out"
check no-leftovers "$([ -z "$(ls -A "$WORK/apps" | grep -Ev '^(omp-gui-0.9.0-linux-x64|\.omp-gui-0.9.0-linux-x64\.previous)$')" ]; echo $?)" "apps/: $(ls -A "$WORK/apps" | tr '\n' ' ')"

if [ -n "$XVFB" ]; then
  win=; pid=
  for _ in $(seq 150); do
    for p in $(pgrep -f "$APP/OmpGui"); do [ "$(readlink "/proc/$p/exe")" = "$APP/OmpGui" ] && pid=$p; done
    [ -n "$pid" ] && win=$(xdotool search --pid "$pid" --name "OMP GUI" 2>/dev/null | head -1)
    [ -n "$win" ] && break; sleep 0.2
  done
  check restarted "$([ -n "$win" ]; echo $?)" "new version running from $APP (pid ${pid:-none}), window ${win:-none}"
  sleep 4; import -window root "$WORK/restarted.png" 2>/dev/null || true
  [ -n "$pid" ] && kill "$pid" 2>/dev/null
else
  echo "SKIP restarted — no Xvfb/xdotool" | tee -a "$RESULT"
fi

out=$("$APP/OmpGui" --update --feed "$FEED" 2>&1); rc=$?
check up-to-date "$([ $rc = 0 ] && grep -q 'OMP GUI 0.9.1 is up to date' <<<"$out"; echo $?)" "exit $rc: $out"
check settings-untouched "$([ "$(sha256sum < "$WORK/home/.config/OmpGui/omp-gui.local.json")" = "$SETTINGS_SUM" ]; echo $?)" "omp-gui.local.json unchanged"
echo "--- apply.log"; cat "$OMPGUI_UPDATES_DIR/apply.log" 2>/dev/null
echo "$fails failed" | tee -a "$RESULT"
[ "$fails" = 0 ]
