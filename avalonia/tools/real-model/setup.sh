#!/usr/bin/env bash
# REAL MODEL test environment: a real language model behind llama.cpp's OpenAI-compatible server, reached by the
# unmodified omp through its own provider path (models.yml). Nothing here touches a user's omp configuration: a
# throwaway HOME with a separate profile "realmodel" and an honest route name local-llamacpp/smollm2-135m-instruct-q4_1.
#
# Sources (both from PyPI, the only model host reachable from the project's cloud machine; hashes checked against
# PyPI's own digests):
#   model   SmolLM2-135M-Instruct Q4_1 GGUF, shipped inside the wheel llm-smollm2 0.1.2 (Simon Willison)
#   server  llama.cpp as vendored in the llama-cpp-python 0.3.35 sdist, built here (target llama-server)
# The chat template is llama.cpp's own ChatML-with-tools template (models/templates/Qwen-Qwen2.5-7B-Instruct.jinja):
# SmolLM2 uses the same ChatML tokens, and its bundled template has no tool section.
# SmolLM2 has an 8k context: omp's full tool set is ~20k tokens, so the profile starts omp with its own options
# --tools=bash,read --no-skills --no-rules --no-lsp, and the server caps a reply at 384 tokens (-n 384).
#
#   tools/real-model/setup.sh <work-dir> <omp-runtime-dir> [port]     # omp-runtime-dir: …/omp-18.8.0-bun-1.4.2
# Then:
#   OMPGUI_REAL_MODEL_CONFIG=<work-dir>/realmodel.json OMPGUI_REAL_MODEL_ROUTE=local-llamacpp/smollm2-135m-instruct-q4_1 \
#     dotnet test tests/OmpGui.Tests --filter FullyQualifiedName~RealModelTests
set -euo pipefail
WORK=${1:?work dir}; RT=${2:?omp runtime dir}; PORT=${3:-18090}
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd); RT=$(cd "$RT" && pwd)
cd "$WORK"

pypi_sha() { curl -fsS "https://pypi.org/pypi/$1/$2/json" | python3 -c "import json,sys;[print(u['digests']['sha256']) for u in json.load(sys.stdin)['urls'] if u['filename']==sys.argv[1]]" "$3"; }
fetch() { # name version filename
  local extra=(); [[ $3 == *.tar.gz ]] && extra=(--no-binary=:all:)
  [ -f "dl/$3" ] || { mkdir -p dl; python3 -m pip download -q --no-deps "${extra[@]}" "$1==$2" -d dl; }
  echo "$(pypi_sha "$1" "$2" "$3")  dl/$3" | sha256sum -c -
}

fetch llm-smollm2 0.1.2 llm_smollm2-0.1.2-py3-none-any.whl
python3 -m zipfile -e dl/llm_smollm2-0.1.2-py3-none-any.whl model
MODEL=$WORK/model/llm_smollm2/SmolLM2-135M-Instruct.Q4_1.gguf

fetch llama-cpp-python 0.3.35 llama_cpp_python-0.3.35.tar.gz
[ -d src/llama_cpp_python-0.3.35 ] || { mkdir -p src; tar xzf dl/llama_cpp_python-0.3.35.tar.gz -C src; }
L=$WORK/src/llama_cpp_python-0.3.35/vendor/llama.cpp
if [ ! -x "$L/build/bin/llama-server" ]; then
  cmake -S "$L" -B "$L/build" -DLLAMA_CURL=OFF -DGGML_NATIVE=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_SERVER=ON -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build "$L/build" --target llama-server -j"$(nproc)" >/dev/null
fi

setsid "$L/build/bin/llama-server" -m "$MODEL" --alias smollm2-135m-instruct-q4_1 --jinja \
  --chat-template-file "$L/models/templates/Qwen-Qwen2.5-7B-Instruct.jinja" -c 8192 -np 1 -n 384 \
  --host 127.0.0.1 --port "$PORT" > llama-server.log 2>&1 < /dev/null &
echo $! > llama-server.pid
for _ in $(seq 1 120); do curl -sf "http://127.0.0.1:$PORT/v1/models" >/dev/null && break; sleep 0.5; done

P=$WORK/home/.omp/profiles/realmodel/agent
mkdir -p "$P" "$WORK/work"
echo "hello readme" > "$WORK/work/README.md"
printf 'modelRoles:\n  default: local-llamacpp/smollm2-135m-instruct-q4_1\n' > "$P/config.yml"
cat > "$P/models.yml" <<EOF
providers:
  local-llamacpp:
    baseUrl: http://127.0.0.1:$PORT/v1
    api: openai-completions
    apiKey: LOCAL_LLAMACPP_API_KEY
    models:
      - id: smollm2-135m-instruct-q4_1
        name: SmolLM2-135M-Instruct Q4_1 (real model, llama.cpp on this machine)
        reasoning: false
        input: [text]
        maxTokens: 384
        contextWindow: 8192
EOF
cat > "$WORK/realmodel.json" <<EOF
{
  "command": "$RT/bun/bun",
  "prefixArgs": ["--no-install", "$RT/omp/node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts"],
  "profile": "realmodel",
  "workingDirectory": "$WORK/work",
  "extraArgs": ["--tools=bash,read", "--no-skills", "--no-rules", "--no-lsp"],
  "environment": { "HOME": "$WORK/home", "USERPROFILE": "$WORK/home", "LOCAL_LLAMACPP_API_KEY": "none-local-server",
                   "AWS_ACCESS_KEY_ID": null, "AWS_SECRET_ACCESS_KEY": null, "AWS_SESSION_TOKEN": null, "ANTHROPIC_BASE_URL": null }
}
EOF
echo "server pid $(cat llama-server.pid) on 127.0.0.1:$PORT; model sha256 $(sha256sum "$MODEL" | cut -c1-64)"
echo "config: $WORK/realmodel.json  route: local-llamacpp/smollm2-135m-instruct-q4_1"
