#!/usr/bin/env bash
# Native macOS feature smoke: a Release Ombra window driven in-process (FeatureSmoke.cs, compiled in through
# feature-smoke.targets only for this build) against the real omp engine of this repository (runtime/engine, run with
# the pinned runtime's Bun and dependencies) and the stand-in model (tools/mock-model). Isolated HOME, profile,
# settings and chats under <work-dir>. Needs Screen Recording permission for screencapture; no Accessibility.
# The window never takes the focus of the Mac's user (OMBRA_TEST_NO_ACTIVATE, src/Ombra.App/Platform/TestFocus.cs): it
# opens on the display left of the main one when there is one, and <work-dir>/front.log records the front app all along
# (tools/verify/front-app-watch.sh): the smoke fails if the app or its Nightly ever became the front app.
#
#   tools/verify/feature-smoke/run.sh <work-dir> <installed-runtime-dir> [sections,comma,separated]
#
# <installed-runtime-dir>: a runtime the client installed (ombra-engine-…-v1 with bun/ and omp/node_modules).
# Result: <work-dir>/shots/smoke.log (PASS/FAIL per check, RESULT line) and screenshots next to it.
# OMBRA_SMOKE_LANGUAGE=ru runs the window in that language (default: the system's).
set -euo pipefail
export OMBRA_TEST_NO_ACTIVATE=${OMBRA_TEST_NO_ACTIVATE:-1}
WORK=${1:?work dir}; RT=${2:?installed runtime dir}; ONLY=${3:-}
[ "$(uname -s)" = Darwin ] || { echo "macOS only"; exit 2; }
HERE=$(cd "$(dirname "$0")/../../.." && pwd)   # avalonia/
REPO=$(cd "$HERE/.." && pwd)
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd); RT=$(cd "$RT" && pwd)
PORT=${OMP_SMOKE_PORT:-18291}
BUN="$RT/bun/bun"

# The repository's engine with the pinned dependencies (Bun resolves omp/node_modules from omp/engine).
mkdir -p "$WORK/rt/omp"
rsync -a --delete --exclude node_modules --exclude .temp "$REPO/runtime/engine/" "$WORK/rt/omp/engine/"
cp "$RT/omp/bunfig.toml" "$RT/omp/package.json" "$WORK/rt/omp/"
ln -sfn "$RT/omp/node_modules" "$WORK/rt/omp/node_modules"
"$BUN" --config="$WORK/rt/omp/bunfig.toml" --no-env-file --no-install "$WORK/rt/omp/engine/host.ts" --self-check

# Profile, work folder, an untrusted project with its own extension and skill, two connectors (one broken).
rm -rf "$WORK/home" "$WORK/work" "$WORK/untrusted" "$WORK/settings" "$WORK/shots" "$WORK/teams"
"$HERE/tools/stand-in-env/setup.sh" "$WORK/home" "$PORT" "$WORK/work" > /dev/null
P="$WORK/untrusted"; mkdir -p "$P/.omp/extensions" "$P/.omp/skills/probe-skill"
printf 'import { writeFileSync } from "node:fs";\nwriteFileSync(%s, "1");\nexport default () => {};\n' "\"$WORK/ext-loaded.marker\"" > "$P/.omp/extensions/probe.ts"
printf -- '---\nname: probe-skill\ndescription: Untrusted project probe skill\n---\nSay PROBE.\n' > "$P/.omp/skills/probe-skill/SKILL.md"
echo "# untrusted" > "$P/README.md"
sed -n '/const string EchoServer = """/,/""";/p' "$HERE/tests/Ombra.Tests/ConnectorsTests.cs" | sed '1d;$d' | sed 's/^        //' > "$WORK/probe-mcp.js"
cat > "$WORK/home/.omp/profiles/harness/agent/mcp.json" <<EOF
{ "mcpServers": {
  "probe": { "type": "stdio", "command": "$BUN", "args": ["$WORK/probe-mcp.js"] },
  "broken": { "type": "stdio", "command": "$WORK/does-not-exist", "args": [] }
} }
EOF
mkdir -p "$WORK/settings"
# (built outside the here-doc: inside it, ${VAR:+…} drops the word's double quotes and the file is not JSON)
LANGUAGE_LINE=""; [ -n "${OMBRA_SMOKE_LANGUAGE:-}" ] && LANGUAGE_LINE="\"language\": \"$OMBRA_SMOKE_LANGUAGE\","
cat > "$WORK/settings/ombra.local.json" <<EOF
{
  "command": "$BUN",
  "prefixArgs": ["--config=$WORK/rt/omp/bunfig.toml", "--no-env-file", "--no-install", "$WORK/rt/omp/engine/host.ts"],
  "managementPrefixArgs": ["--config=$WORK/rt/omp/bunfig.toml", "--no-env-file", "--no-install", "$WORK/rt/omp/engine/coding-agent/src/cli.ts"],
  "profile": "harness", $LANGUAGE_LINE
  "workingDirectory": "$WORK/work",
  "environment": {
    "HOME": "$WORK/home", "PI_CODING_AGENT_DIR": null, "OMP_PROFILE": null, "PI_PROFILE": null,
    "QWEN_LOCAL_API_KEY": "dummy-not-a-secret",
    "AWS_ACCESS_KEY_ID": null, "AWS_SECRET_ACCESS_KEY": null, "AWS_SESSION_TOKEN": null
  }
}
EOF
# A settings file the app cannot read makes it run on the defaults — the user's omp profile, ~/.omp sessions and real
# model (a smoke once did): never start on one. The smoke checks the isolation again before anything starts.
python3 -m json.tool "$WORK/settings/ombra.local.json" > /dev/null || { echo "not JSON: $WORK/settings/ombra.local.json"; exit 1; }

# Release build with the smoke compiled in, kept apart from the normal build output.
(cd "$HERE" && dotnet build src/Ombra.App -c Release --artifacts-path "$WORK/artifacts" \
  -p:CustomAfterMicrosoftCommonTargets="$HERE/tools/verify/feature-smoke/feature-smoke.targets" > "$WORK/build.log" 2>&1) \
  || { tail -30 "$WORK/build.log"; exit 1; }

MOCK_LOG=1 "$BUN" "$HERE/tools/mock-model/server.mjs" "$PORT" > "$WORK/mock.log" 2>&1 &
MOCK=$!
# The "team" and "nightly-share" sections need a real team server (docker): started here, stopped on exit.
# "nightly-share" runs only when named, next to nightly-share-driver.py driving a dev Nightly.
TEAM=""
if [ -z "$ONLY" ] || [[ ",$ONLY," == *",team,"* || ",$ONLY," == *",nightly-share,"* ]]; then
  TEAM=$("$HERE/tools/verify/feature-smoke/team-server.sh" up "$WORK/team-server" | tail -1) || TEAM=""
fi
trap 'kill $MOCK 2>/dev/null || true; [ -n "$TEAM" ] && "$HERE/tools/verify/feature-smoke/team-server.sh" down "$WORK/team-server"' EXIT
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/v1/models" >/dev/null && break; sleep 0.2; done

OMBRA_CONFIG="$WORK/settings/ombra.local.json" OMBRA_CHATS_DIR="$WORK/chats" OMBRA_LOG_DIR="$WORK/logs" \
OMBRA_FEATURE_SMOKE="$WORK/shots" OMBRA_FEATURE_SMOKE_ONLY="$ONLY" \
OMBRA_FEATURE_SMOKE_UNTRUSTED="$P" OMBRA_FEATURE_SMOKE_MARKER="$WORK/ext-loaded.marker" OMBRA_FEATURE_SMOKE_MOCKLOG="$WORK/mock.log" \
OMBRA_FEATURE_SMOKE_TEAM="$TEAM" OMBRA_TEAMS_DIR="$WORK/teams" OMBRA_FEATURE_SMOKE_MOCKPORT="$PORT" \
  "$HERE/tools/verify/front-app-watch.sh" "$WORK/front.log" "$WORK/artifacts/bin/Ombra.App/release/Ombra" > "$WORK/app.log" 2>&1 || true
grep -E '^[0-9:.]+ (PASS|FAIL|RESULT)' "$WORK/shots/smoke.log"
tail -1 "$WORK/app.log"
if grep -q ' STOLEN ' "$WORK/front.log"; then echo "FAIL focus: the app became the front app"; grep ' STOLEN ' "$WORK/front.log"; exit 1; fi
grep -q 'RESULT PASS' "$WORK/shots/smoke.log"
