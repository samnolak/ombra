#!/usr/bin/env bash
# Builds a throwaway HOME whose default omp agent dir (~/.omp/agent) holds only a models.yml that registers the
# stand-in model under the real route name qwen-local/flash-next-w4a16, plus a config.yml with that default model
# role. For CI/cloud machines only. The harness itself is not copied: omp reads it in place from
# OMBRA_HARNESS_DIR (the repository's harness/profile here; the runtime pack's omp/harness in the app), which the
# callers set and this script prints for direct engine runs.
#
#   tools/stand-in-env/setup.sh <home-dir> [mock-port] [work-dir]
set -euo pipefail
HOME_DIR=${1:?home dir}
PORT=${2:-18080}
WORK=${3:-}
REPO=$(cd "$(dirname "$0")/../../.." && pwd)
A="$HOME_DIR/.omp/agent"
mkdir -p "$A"
# The default model role (on a real machine the user picks it in the GUI); harness settings come from its layer.
printf 'modelRoles:\n  default: qwen-local/flash-next-w4a16\n' > "$A/config.yml"
cat > "$A/models.yml" <<EOF
providers:
  qwen-local:
    baseUrl: http://127.0.0.1:$PORT/v1
    api: openai-completions
    apiKey: QWEN_LOCAL_API_KEY
    models:
      - id: flash-next-w4a16
        name: Stand-in for flash-next-w4a16 (mock server, not a model)
        reasoning: false
        input: [text]
        maxTokens: 8192
        contextWindow: 131072
EOF
if [ -n "$WORK" ]; then
  mkdir -p "$WORK"
  echo "hello readme" > "$WORK/README.md"
  for i in $(seq 0 9); do printf 'note %s\nline two of note %s\n' "$i" "$i" > "$WORK/notes-$i.md"; done
  echo "work dir ready: $WORK"
fi
echo "agent dir ready: $A"
echo "OMBRA_HARNESS_DIR=$REPO/harness/profile"
