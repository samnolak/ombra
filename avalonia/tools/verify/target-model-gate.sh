#!/usr/bin/env bash
# TARGET MODEL / PROVIDER gate (e.g. a local Qwen server): the same real-window tests as REAL MODEL GENERIC,
# pointed at the target, with tool calling REQUIRED (a model that answers in prose fails instead of skipping) and a
# longer reply required. The unmodified omp reaches the model through its own provider path (models.yml); nothing is
# written to the user's omp configuration — a throwaway HOME with its own profile "target".
#
#   TARGET_BASE_URL=http://host:port/v1 TARGET_MODEL_ID=<id as the server names it> \
#     tools/verify/target-model-gate.sh <work-dir>
#
# Required:  TARGET_BASE_URL, TARGET_MODEL_ID
# Optional:  TARGET_PROVIDER      provider name in the route (default target-local) → route <provider>/<model id>
#            TARGET_API           omp api kind (default openai-completions)
#            TARGET_API_KEY_VAR   NAME of an environment variable that holds the key; the value is never written to a
#                                 file — omp reads it from its environment (default: none, for a keyless local server)
#            TARGET_CONTEXT       context window (default 32768) · TARGET_MAX_TOKENS (default 4096)
#            TARGET_REASONING     true if the model reasons (default false)
#            TARGET_LONG_MIN_CHARS  minimum length of the long reply (default 3000)
#            TARGET_OMP_ARGS      extra omp flags, space-separated (default none; for a small context e.g.
#                                 "--tools=bash,read --no-skills --no-rules --no-lsp")
#            OMP_RUNTIME          installed pinned runtime (default: the client's own install,
#                                 ~/.local/share/OmpGui/runtimes/omp-18.2.0-bun-1.4.2)
# Result: <work-dir>/target-report.txt (every step) and <work-dir>/result.txt (PASS / FAIL per check).
set -uo pipefail
WORK=${1:?work dir}; mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
: "${TARGET_BASE_URL:?TARGET_BASE_URL (e.g. http://127.0.0.1:8000/v1)}" "${TARGET_MODEL_ID:?TARGET_MODEL_ID}"
PROVIDER=${TARGET_PROVIDER:-target-local}; ROUTE="$PROVIDER/$TARGET_MODEL_ID"
RT=${OMP_RUNTIME:-$HOME/.local/share/OmpGui/runtimes/omp-18.2.0-bun-1.4.2}
HERE=$(cd "$(dirname "$0")/../.." && pwd)
[ -x "$RT/bun/bun" ] || [ -f "$RT/bun/bun.exe" ] || { echo "no pinned runtime at $RT (install it from the app, or set OMP_RUNTIME)"; exit 2; }
KEYVAR=${TARGET_API_KEY_VAR:-TARGET_NO_KEY}
if [ "$KEYVAR" = TARGET_NO_KEY ]; then export TARGET_NO_KEY=none-local-server
elif [ -z "${!KEYVAR:-}" ]; then echo "TARGET_API_KEY_VAR=$KEYVAR names an empty variable"; exit 2; fi
: > "$WORK/result.txt"
result() { echo "$1" | tee -a "$WORK/result.txt"; }

# 1. The server answers and lists the model (OpenAI-compatible /models; other API kinds skip this preflight).
if [ "${TARGET_API:-openai-completions}" = openai-completions ]; then
  auth=(); [ "$KEYVAR" != TARGET_NO_KEY ] && auth=(-H "Authorization: Bearer ${!KEYVAR}")
  if curl -fsS -m 20 "${auth[@]}" "$TARGET_BASE_URL/models" -o "$WORK/models.json"; then
    python3 -c "import json,sys; ids=[m.get('id') for m in json.load(open(sys.argv[1])).get('data',[])]; print('server lists:', ids); sys.exit(0 if sys.argv[2] in ids else 1)" \
      "$WORK/models.json" "$TARGET_MODEL_ID" && result "PASS server-lists-model" || result "FAIL server-lists-model — $TARGET_MODEL_ID not in the server's /models"
  else result "FAIL server-reachable — $TARGET_BASE_URL/models did not answer"; exit 1; fi
fi

# 2. A throwaway omp profile that routes to the target.
P=$WORK/home/.omp/profiles/target/agent
mkdir -p "$P" "$WORK/work"; echo "hello readme" > "$WORK/work/README.md"
printf 'modelRoles:\n  default: %s\n' "$ROUTE" > "$P/config.yml"
cat > "$P/models.yml" <<EOF
providers:
  $PROVIDER:
    baseUrl: $TARGET_BASE_URL
    api: ${TARGET_API:-openai-completions}
    apiKey: $KEYVAR
    models:
      - id: $TARGET_MODEL_ID
        name: $TARGET_MODEL_ID (target model gate)
        reasoning: ${TARGET_REASONING:-false}
        input: [text]
        maxTokens: ${TARGET_MAX_TOKENS:-4096}
        contextWindow: ${TARGET_CONTEXT:-32768}
EOF
EXTRA=$(python3 -c "import json,sys; print(json.dumps(sys.argv[1].split()))" "${TARGET_OMP_ARGS:-}")
BUN=$RT/bun/bun; [ -f "$RT/bun/bun.exe" ] && BUN=$RT/bun/bun.exe
cat > "$WORK/target.json" <<EOF
{
  "command": "$BUN",
  "prefixArgs": ["--no-install", "$RT/omp/node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts"],
  "profile": "target",
  "workingDirectory": "$WORK/work",
  "extraArgs": $EXTRA,
  "environment": { "HOME": "$WORK/home", "USERPROFILE": "$WORK/home" }
}
EOF

# 3. The real window against it: discovery + choice, prompt, streaming, abort, recovery, long reply, tool + approval.
cd "$HERE"
OMPGUI_REAL_MODEL_CONFIG=$WORK/target.json OMPGUI_REAL_MODEL_ROUTE=$ROUTE OMPGUI_REAL_MODEL_REPORT=$WORK/target-report.txt \
OMPGUI_REAL_MODEL_REQUIRE_TOOLS=1 OMPGUI_REAL_MODEL_LONG_MIN_CHARS=${TARGET_LONG_MIN_CHARS:-3000} OMPGUI_SHOT_DIR=$WORK/shots \
  dotnet test tests/OmpGui.Tests -c Release --filter "FullyQualifiedName~RealModelTests" \
  --logger "console;verbosity=normal" --logger "trx;LogFileName=target.trx" --results-directory "$WORK/trx" > "$WORK/tests.log" 2>&1
code=$?
for t in The_model_is_discovered A_prompt_streams_and_completes A_long_response A_tool_call; do
  line=$(grep -E "^\s+(Passed|Failed|Skipped) OmpGui.Tests.RealModelTests.$t" "$WORK/tests.log" | head -1 | awk '{print $1}')
  result "${line:-MISSING} $t"
done
grep -q "^Passed" "$WORK/result.txt" && [ $code -eq 0 ] && ! grep -qE "^(FAIL|Failed|Skipped|MISSING)" "$WORK/result.txt" \
  && { result "TARGET MODEL GATE: PASS ($ROUTE)"; exit 0; } || { result "TARGET MODEL GATE: FAIL ($ROUTE) — see $WORK/tests.log"; exit 1; }
