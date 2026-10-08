#!/usr/bin/env bash
# Windowless native WKWebView harness (docs/BROWSER_PLAN.md B0). A console NSApplication with activation policy
# "prohibited" (no Dock icon, no menu bar, never active), WKWebViews in windows that are never ordered on screen, and
# local test pages on 127.0.0.1. No permission prompts: no CGEventPost, no screen capture, no camera/mic, no keychain
# (non-persistent data store, session-only credentials), downloads go to a temp folder. macOS only.
#
#   tools/verify/webview-harness/run.sh --list
#   tools/verify/webview-harness/run.sh <scenario> [--hooks app|harness]   # one JSON object on stdout
#   tools/verify/webview-harness/run.sh all [--hooks app|harness]          # a JSON array, one process per scenario
#                                         (--hooks harness leaves out the scenarios of the app's own hooks only)
#
# Exit code 0 = every check passed and nothing was on screen. A PASS/FAIL line per scenario goes to stderr.
# The app's src/OmpGui.App/Platform/Mac/*.cs are compiled in when present (--hooks app is then the default).
# Environment:
#   HARNESS_ARTIFACTS        build output (default /tmp/ompgui-webview-harness)
#   HARNESS_NO_BUILD=1       skip the build
#   HARNESS_TIMEOUT_SEC      per scenario (default 90)
#   HARNESS_VERBOSE=1        delegate callbacks to stderr as they happen
#   HARNESS_ISOLATION_CHECK=1  afterwards: no harness process left, no TCC request from the harness in the unified log
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ART=${HARNESS_ARTIFACTS:-/tmp/ompgui-webview-harness}
DLL="$ART/bin/WebViewHarness/debug/WebViewHarness.dll"
[ "$(uname)" = Darwin ] || { echo "The WKWebView harness runs on macOS only." >&2; exit 2; }
[ $# -ge 1 ] || { echo "usage: $0 <scenario|all|--list> [--hooks app|harness]" >&2; exit 2; }

if [ -z "${HARNESS_NO_BUILD:-}" ]; then
  dotnet build "$HERE/WebViewHarness.csproj" -v q -nologo --artifacts-path "$ART" >&2 || exit 2
fi

START=$(date '+%Y-%m-%d %H:%M:%S')
OUT=$(mktemp -t webview-harness)
trap 'rm -f "$OUT"' EXIT
rc=0
if [ "$1" = all ]; then
  shift
  names=$(dotnet "$DLL" --list "$@" | awk '{print $1}')
  {
    echo "["
    first=1
    for n in $names; do
      [ $first = 1 ] || echo ","
      first=0
      dotnet "$DLL" "$n" "$@" || rc=1
    done
    echo "]"
  } > "$OUT"
else
  dotnet "$DLL" "$@" > "$OUT" || rc=$?
fi
cat "$OUT"

if [ -n "${HARNESS_ISOLATION_CHECK:-}" ]; then
  if pgrep -f "WebViewHarness.dll" > /dev/null; then
    echo "FAIL isolation: a harness process is still running" >&2; rc=1
  fi
  pids=$(grep -Eo '"pid": [0-9]+' "$OUT" | grep -Eo '[0-9]+' | sort -u | tr '\n' ' ')
  # tccd logs each access check: AUTHREQ_ATTRIBUTION names the processes (ours by pid, as requester or accessor),
  # AUTHREQ_CTX the service and whether it is a preflight (a status query that can never prompt). AppKit and WebKit
  # preflight ListenEvent / Microphone / Camera / AllFiles when they start; any non-preflight request from a harness
  # process could show a prompt, so it fails the check.
  tcc=$(log show --style compact --start "$START" --predicate 'subsystem == "com.apple.TCC"' 2>/dev/null \
    | awk -v pids=" $pids" '
        function id() { return match($0, /msgID=[0-9]+\.[0-9]+/) ? substr($0, RSTART + 6, RLENGTH - 6) : "" }
        /AUTHREQ_ATTRIBUTION/ { n = split(pids, p, " "); for (i = 1; i <= n; i++) if (index($0, "pid=" p[i] ",")) ours[id()] = 1 }
        /AUTHREQ_CTX/ { if (match($0, /service=[A-Za-z]+, preflight=[a-z]+/)) ctx[id()] = substr($0, RSTART, RLENGTH) }
        END { for (m in ours) if (m in ctx) print ctx[m] }' | sort | uniq -c)
  if printf '%s\n' "$tcc" | grep -q "preflight=no"; then
    echo "FAIL isolation: TCC requests that can prompt, from harness pids $pids:" >&2; printf '%s\n' "$tcc" >&2; rc=1
  else
    echo "PASS isolation: no harness process left; TCC since $START for pids $pids: preflight status queries only" >&2
    [ -z "$tcc" ] || printf '%s\n' "$tcc" | sed 's/^/  /' >&2
  fi
fi
exit $rc
