#!/usr/bin/env bash
# Makes a new release signing key pair (ECDSA P-256).
#   tools/release/keygen.sh <out-dir>
# <out-dir>/update-signing-key.pem is the private key: store it only as the repository secret UPDATE_SIGNING_KEY
# (Settings → Secrets and variables → Actions) and in the owner's password manager; never commit it.
# <out-dir>/update-signing.pub is the public key (base64 SubjectPublicKeyInfo): it replaces
# avalonia/packaging/update-signing.pub. Clients built with the old public key trust only manifests signed with
# the old private key, so a new key reaches users only through one last release signed with the old one.
set -euo pipefail
OUT=${1:?out dir}
mkdir -p "$OUT"
umask 077
openssl ecparam -name prime256v1 -genkey -noout -out "$OUT/ec.pem"
openssl pkcs8 -topk8 -nocrypt -in "$OUT/ec.pem" -out "$OUT/update-signing-key.pem"
rm "$OUT/ec.pem"
openssl pkey -in "$OUT/update-signing-key.pem" -pubout -outform DER | base64 | tr -d '\n' > "$OUT/update-signing.pub"
echo >> "$OUT/update-signing.pub"
echo "private: $OUT/update-signing-key.pem (secret UPDATE_SIGNING_KEY; never commit)"
echo "public:  $OUT/update-signing.pub -> avalonia/packaging/update-signing.pub"
