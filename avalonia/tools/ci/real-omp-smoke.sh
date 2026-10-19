#!/usr/bin/env bash
# Real owned Ombra engine smoke for CI (Linux, macOS, Windows via Git Bash).
# Installs the complete owned engine and pinned Bun/npm dependencies with the client's installer, builds a
# throwaway harness profile, starts the stand-in model server and runs the gated RealOmp tests against it.
# Model: STAND-IN (tools/mock-model), not a real model. Engine, RPC, provider path and tools are real.
#
#   tools/ci/real-omp-smoke.sh <work-dir>
set -euo pipefail
WORK=${1:?work dir}
PORT=${OMP_SMOKE_PORT:-18080}
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
# Optional isolated build output; callers build tests with this same path first.
ARTIFACT_ARGS=()
if [[ -n "${OMBRA_ARTIFACTS_PATH:-}" ]]; then
  mkdir -p "$OMBRA_ARTIFACTS_PATH"
  ARTIFACTS=$(cd "$OMBRA_ARTIFACTS_PATH" && pwd)
  ARTIFACT_ARGS=(--artifacts-path "$ARTIFACTS")
fi

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) EXE=.exe ;;
  *) EXE= ;;
esac

# Frozen dependencies and Bun are integrity checked; installed owned host --self-check imports the real SDK.
echo "::group::Pinned runtime via the client's installer"
(cd "$HERE" && OMBRA_RUNTIME_INSTALL_DIR="$WORK/runtimes" dotnet test tests/Ombra.Tests -c Release --no-build "${ARTIFACT_ARGS[@]}" \
  --filter "FullyQualifiedName~RealRuntimeInstallTests" --logger "console;verbosity=detailed")
RT="$WORK/runtimes/ombra-engine-18.12.2-bun-1.4.2-v1"
BUN="$RT/bun/bun$EXE"
CLI="$RT/omp/engine/coding-agent/src/cli.ts"
HOST="$RT/omp/engine/host.ts"
cat "$RT/installed.json"
"$BUN" --revision
"$BUN" --config="$RT/omp/bunfig.toml" --no-env-file --no-install "$HOST" --self-check
du -sh "$RT" || true
echo "::endgroup::"

"$HERE/tools/stand-in-env/setup.sh" "$WORK/home" "$PORT" "$WORK/work"
"$BUN" --no-env-file "$HERE/tools/mock-model/server.mjs" "$PORT" > "$WORK/mock.log" 2>&1 &
MOCK=$!
trap 'kill $MOCK 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/v1/models" >/dev/null && break; sleep 0.2; done
if ! kill -0 "$MOCK" 2>/dev/null || ! curl -sf "http://127.0.0.1:$PORT/v1/models" >/dev/null; then
  cat "$WORK/mock.log"
  echo "Stand-in model did not start on port $PORT" >&2
  exit 1
fi

native() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else echo "$1"; fi; }
# HOME redirects omp's ~/.omp; USERPROFILE is what omp reads for the home directory on Windows.
cat > "$WORK/ombra.local.json" <<EOF
{
  "command": "$(native "$BUN")",
  "prefixArgs": ["--config=$(native "$RT/omp/bunfig.toml")", "--no-env-file", "--no-install", "$(native "$HOST")"],
  "managementPrefixArgs": ["--config=$(native "$RT/omp/bunfig.toml")", "--no-env-file", "--no-install", "$(native "$CLI")"],
  "profile": "harness",
  "workingDirectory": "$(native "$WORK/work")",
  "environment": {
    "HOME": "$(native "$WORK/home")",
    "USERPROFILE": "$(native "$WORK/home")",
    "QWEN_LOCAL_API_KEY": "dummy-not-a-secret",
    "AWS_ACCESS_KEY_ID": null, "AWS_SECRET_ACCESS_KEY": null, "AWS_SESSION_TOKEN": null
  }
}
EOF

cd "$HERE"
OMBRA_TEST_CONFIG="$(native "$WORK/ombra.local.json")" dotnet test tests/Ombra.Tests -c Release --no-build "${ARTIFACT_ARGS[@]}" \
  --filter "FullyQualifiedName~RealOmp|FullyQualifiedName~AcceptanceTests" --logger "console;verbosity=normal"

# Perf-lite: 10k-message history open budget and 30 s of real-engine streaming without stalls. Headless renders on a
# synthetic tick, so it does not certify native 60 fps; the 30-minute RSS soak is a manual native release gate.
echo "::group::Performance budgets (headless, stand-in model)"
dotnet run --project tools/verify/performance/Ombra.Performance.csproj -c Release "${ARTIFACT_ARGS[@]}" -- \
  --config "$(native "$WORK/ombra.local.json")" --model-url "http://127.0.0.1:$PORT/v1" \
  --work "$(native "$WORK/perf")" --headless --seconds 30
echo "::endgroup::"
