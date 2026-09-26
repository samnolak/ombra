#!/usr/bin/env bash
# Real Original OMP Core smoke for CI (Linux, macOS, Windows via Git Bash).
# Installs the pinned runtime pack (omp 18.2.0 from npm on Bun 1.4.2) with the client's own installer, builds a
# throwaway harness profile, starts the stand-in model server and runs the gated RealOmp tests against it.
# Model: STAND-IN (tools/mock-model), not a real model. Everything else (omp, RPC, provider path, tools) is real.
#
#   tools/ci/real-omp-smoke.sh <work-dir>
set -euo pipefail
WORK=${1:?work dir}
PORT=${OMP_SMOKE_PORT:-18080}
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) EXE=.exe ;;
  *) EXE= ;;
esac

# The client's own installer (RuntimeInstaller): Bun from npm checked against its pinned sha512, omp from the pack
# lockfile (bun install --frozen-lockfile --ignore-scripts), omp --version checked. The same code a user runs.
echo "::group::Pinned runtime via the client's installer"
(cd "$HERE" && OMPGUI_RUNTIME_INSTALL_DIR="$WORK/runtimes" dotnet test tests/OmpGui.Tests -c Release --no-build \
  --filter "FullyQualifiedName~RealRuntimeInstallTests" --logger "console;verbosity=detailed")
RT="$WORK/runtimes/omp-18.2.0-bun-1.4.2"
BUN="$RT/bun/bun$EXE"
CLI="$RT/omp/node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts"
cat "$RT/installed.json"
"$BUN" --revision
"$BUN" --no-install "$CLI" --version
du -sh "$RT" || true
echo "::endgroup::"

"$HERE/tools/stand-in-env/setup.sh" "$WORK/home" "$PORT" "$WORK/work"
node "$HERE/tools/mock-model/server.mjs" "$PORT" > "$WORK/mock.log" 2>&1 &
MOCK=$!
trap 'kill $MOCK 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/v1/models" >/dev/null && break; sleep 0.2; done

native() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else echo "$1"; fi; }
# HOME redirects omp's ~/.omp; USERPROFILE is what omp reads for the home directory on Windows.
cat > "$WORK/omp-gui.local.json" <<EOF
{
  "command": "$(native "$BUN")",
  "prefixArgs": ["--no-install", "$(native "$CLI")"],
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
OMPGUI_TEST_CONFIG="$(native "$WORK/omp-gui.local.json")" dotnet test tests/OmpGui.Tests -c Release --no-build \
  --filter "FullyQualifiedName~RealOmp|FullyQualifiedName~AcceptanceTests" --logger "console;verbosity=normal"
