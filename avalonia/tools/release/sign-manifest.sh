#!/usr/bin/env bash
# Signs update.json with the release key: writes update.json.sig (base64 of a DER ECDSA P-256 signature over SHA-256),
# which the client checks against the public key built into it (packaging/update-signing.pub).
#   tools/release/sign-manifest.sh <update.json> <private-key.pem> [public-key file]
# The signature is verified against the public key before the script succeeds, so a release signed with the wrong
# key (one the clients would refuse) never leaves CI. The private key is never written anywhere by this script.
set -euo pipefail
MANIFEST=${1:?update.json}; KEY=${2:?private key .pem}
HERE=$(cd "$(dirname "$0")/../.." && pwd)
PUB=${3:-$HERE/packaging/update-signing.pub}
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT

# The key must be the one the clients trust.
openssl pkey -in "$KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n' > "$TMP/derived"
if [ "$(cat "$TMP/derived")" != "$(tr -d ' \r\n' < "$PUB")" ]; then
  echo "the signing key does not belong to the public key in $PUB: clients would refuse this manifest" >&2
  exit 1
fi
openssl dgst -sha256 -sign "$KEY" -out "$TMP/sig.der" "$MANIFEST"
base64 < "$TMP/sig.der" | tr -d '\n' > "$MANIFEST.sig"; echo >> "$MANIFEST.sig"

# Verify as the client will: public key from the file, signature from update.json.sig.
{ echo "-----BEGIN PUBLIC KEY-----"; tr -d ' \r\n' < "$PUB" | fold -w 64; echo; echo "-----END PUBLIC KEY-----"; } > "$TMP/pub.pem"
base64 -d < "$MANIFEST.sig" > "$TMP/check.der"
openssl dgst -sha256 -verify "$TMP/pub.pem" -signature "$TMP/check.der" "$MANIFEST"
echo "signed: $MANIFEST.sig"
