#!/usr/bin/env bash
# Runs a GUI check and records that it never took the focus of the Mac's user (src/Ombra.App/Platform/TestFocus.cs):
#
#   tools/verify/front-app-watch.sh <log> <command> [args…]
#
# OMBRA_TEST_NO_ACTIVATE defaults to 1 for the command. Every 100 ms the frontmost app is read (lsappinfo front); the
# first one and each change go to <log> with its pid, bundle id and name, marked STOLEN when it is the command or one of
# its descendants (Ombra, the Nightly runtime it starts), PROMPT when it is macOS asking for a permission (any process
# may have caused it: a check without Screen Recording must not start screencapture), else "other" (the user himself).
# Exit: the command's status, or 3 when the command succeeded but something it started became the front app.
set -uo pipefail
LOG=${1:?log}; shift
[ $# -gt 0 ] || { echo "usage: $0 <log> <command> [args…]" >&2; exit 2; }
export OMBRA_TEST_NO_ACTIVATE=${OMBRA_TEST_NO_ACTIVATE:-1}
: > "$LOG"
"$@" &
CMD=$!

ours() {
  local p=$1
  while [ -n "$p" ] && [ "$p" -gt 1 ]; do
    [ "$p" = "$CMD" ] && return 0
    p=$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ')
  done
  return 1
}

PREV=""
look() {
  local asn pid bundle name tag
  asn=$(lsappinfo front 2>/dev/null)
  [ "$asn" = "$PREV" ] && return
  PREV=$asn
  pid=$(lsappinfo info -only pid "$asn" 2>/dev/null | sed -n 's/.*"pid"=\([0-9]*\).*/\1/p')
  bundle=$(lsappinfo info -only bundleid "$asn" 2>/dev/null | sed -n 's/.*"CFBundleIdentifier"="\(.*\)"/\1/p')
  name=$(lsappinfo info -only name "$asn" 2>/dev/null | sed -n 's/.*"LSDisplayName"="\(.*\)"/\1/p')
  tag=other
  case "$bundle" in
    com.apple.accessibility.universalAccessAuthWarn|com.apple.UserNotificationCenter|com.apple.CoreServicesUIAgent|com.apple.SecurityAgent) tag=PROMPT ;;
  esac
  ours "${pid:-0}" && tag=STOLEN
  echo "$(date +%H:%M:%S) $tag pid=${pid:-?} ${bundle:-?} $name" >> "$LOG"
}

look
while kill -0 "$CMD" 2>/dev/null; do
  look
  sleep 0.1
done
wait "$CMD"; STATUS=$?
STOLEN=$(grep -c ' STOLEN ' "$LOG")
echo "front-app: $(($(wc -l < "$LOG") - 1)) change(s), $STOLEN to the check's processes, $(grep -c ' PROMPT ' "$LOG") permission prompt(s) ($LOG)"
[ "$STATUS" = 0 ] && [ "$STOLEN" != 0 ] && STATUS=3
exit "$STATUS"
