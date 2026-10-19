#!/usr/bin/env bash
# Builds a signed release directory that install.sh verifies:
#   ./deploy/make-release.sh <version> <out-dir> <signing-key.pem>
# Layout: bin/ (self-contained server/, ombra-admin/, lib/libombra_core.so for --systemd), deploy/ (install.sh,
# compose.yaml, ombra-server.service), images/ombra-server.tar, SHA256SUMS (every file above), SHA256SUMS.sig.
# install.sh rejects any file of the tree that SHA256SUMS does not list. It cannot vouch for itself, so operators
# verify the release before running it with the one-liner printed at the end (also in the install.sh header).
# The signing key is an EC P-256 (or RSA) private key in PEM; publish its public key out of band (--release-key).
# OMBRA_RID selects the runtime of bin/ (default linux-x64); run on a matching Linux host (libombra_core.so is a host build).
set -euo pipefail
VERSION=${1:?version}
OUT=${2:?out dir}
KEY=${3:?signing key}
SRC=$(cd "$(dirname "$0")/.." && pwd)
RID=${OMBRA_RID:-linux-x64}
if command -v sha256sum >/dev/null; then SHA256=sha256sum; else SHA256="shasum -a 256"; fi
BUILD=$(mktemp -d)
trap 'rm -rf "$BUILD"' EXIT

rm -rf "$OUT"
mkdir -p "$OUT/deploy" "$OUT/images" "$OUT/bin/lib"
# Docker variant
docker build -f "$SRC/deploy/Dockerfile" -t "ombra-server:$VERSION" "$SRC"
docker save "ombra-server:$VERSION" -o "$OUT/images/ombra-server.tar"
# systemd variant (install.sh --systemd copies bin/ to /opt/ombra/bin, see ombra-server.service)
cargo build --release --locked --lib --manifest-path "$SRC/native/ombra-core/Cargo.toml" --target-dir "$BUILD/cargo"
cp "$BUILD/cargo/release/libombra_core.so" "$OUT/bin/lib/libombra_core.so"
dotnet publish "$SRC/src/Ombra.Server/Ombra.Server.csproj" -c Release -r "$RID" --self-contained true -o "$OUT/bin/server"
dotnet publish "$SRC/src/Ombra.Server.Cli/Ombra.Server.Cli.csproj" -c Release -r "$RID" --self-contained true -o "$OUT/bin/ombra-admin"
[ -x "$OUT/bin/server/Ombra.Server" ] && [ -x "$OUT/bin/ombra-admin/ombra-admin" ] \
  || { echo "make-release.sh: dotnet publish produced no bin/server/Ombra.Server or bin/ombra-admin/ombra-admin" >&2; exit 1; }
cp "$SRC/deploy/install.sh" "$SRC/deploy/compose.yaml" "$SRC/deploy/ombra-server.service" "$OUT/deploy/"
chmod 0755 "$OUT/deploy/install.sh"
(
  cd "$OUT"
  odd=$(find bin deploy images ! -type f ! -type d)
  [ -z "$odd" ] || { echo "make-release.sh: install.sh rejects non-regular files: $odd" >&2; exit 1; }
  find bin deploy images -type f -print0 | LC_ALL=C sort -z | xargs -0 $SHA256 > SHA256SUMS
)
openssl dgst -sha256 -sign "$KEY" -out "$OUT/SHA256SUMS.sig" "$OUT/SHA256SUMS"
echo "release $VERSION → $OUT"
cat <<'EOF'
Operators: install.sh cannot verify itself. BEFORE running it, in the unpacked release directory, with the publisher
key (release.pub) obtained out of band:
  openssl dgst -sha256 -verify release.pub -signature SHA256SUMS.sig SHA256SUMS && sha256sum -c SHA256SUMS
  (macOS: shasum -a 256 -c SHA256SUMS instead of sha256sum -c SHA256SUMS)
then: sudo ./deploy/install.sh --release-key release.pub --ip <address> | --domain <name> ...
EOF
