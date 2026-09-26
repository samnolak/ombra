#!/usr/bin/env bash
# Builds a throwaway HOME with a "harness" omp profile instantiated from the repository's harness
# template (../../../harness/profile) plus a models.yml that registers the stand-in model under
# the real route name qwen-local/flash-next-w4a16. For CI/cloud machines only: on the real
# Windows machine the existing %USERPROFILE%\.omp\profiles\harness is used as-is.
#
#   tools/stand-in-env/setup.sh <home-dir> [mock-port] [work-dir]
set -euo pipefail
HOME_DIR=${1:?home dir}
PORT=${2:-18080}
WORK=${3:-}
REPO=$(cd "$(dirname "$0")/../../.." && pwd)
P="$HOME_DIR/.omp/profiles/harness"
mkdir -p "$P/agent"
cp -r "$REPO/harness/profile/skills" "$P/"
cp "$REPO/harness/profile/agent/AGENTS.md" "$P/agent/"
cp -r "$REPO/harness/profile/agent/agents" "$P/agent/"
# config.yml = shipped seed + the default model role (on the real machine the user set this in the GUI)
{ cat "$REPO/harness/profile/agent/config.yml.seed"; printf '\nmodelRoles:\n  default: qwen-local/flash-next-w4a16\n'; } > "$P/agent/config.yml"
# mcp.json is omitted: codebase-memory-mcp is a Windows binary and context7 needs the network.
cat > "$P/agent/models.yml" <<EOF
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
echo "profile ready: $P"
