#!/usr/bin/env bash
# Makes the project's long-lived macOS code-signing certificate (self-signed, RSA 2048, 20 years), for releases signed
# without an Apple Developer ID (tools/package/sign.sh). macOS keeps the permissions a user gives the app (Documents,
# Desktop, microphone…) for "this identifier, signed by this certificate": the same certificate in every release keeps
# them across updates. A new certificate makes every user allow everything once more, so make it once.
#   tools/release/macos-selfsign-keygen.sh <out-dir>
# <out-dir>/omp-gui-selfsign.p12 and its password: store them only as the repository secrets MACOS_SELFSIGN_P12_BASE64
# (base64 of the file: <out-dir>/omp-gui-selfsign.p12.base64) and MACOS_SELFSIGN_PASSWORD, and in the owner's password
# manager; never commit them.
set -euo pipefail
OUT=${1:?out dir}
mkdir -p "$OUT"
umask 077
cat > "$OUT/cert.cnf" <<'EOF'
[req]
distinguished_name=dn
x509_extensions=ext
prompt=no
[dn]
CN=OMP GUI (self-signed)
[ext]
basicConstraints=critical,CA:false
keyUsage=critical,digitalSignature
extendedKeyUsage=critical,codeSigning
subjectKeyIdentifier=hash
EOF
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$OUT/key.pem" -out "$OUT/cert.pem" -days 7300 -config "$OUT/cert.cnf" 2>/dev/null
PASS=$(openssl rand -hex 24)
# -legacy: macOS's security import reads only the older PKCS#12 algorithms (OpenSSL 3 writes AES by default)
openssl pkcs12 -export -legacy -inkey "$OUT/key.pem" -in "$OUT/cert.pem" -name "OMP GUI (self-signed)" \
  -out "$OUT/omp-gui-selfsign.p12" -passout "pass:$PASS" 2>/dev/null \
  || openssl pkcs12 -export -inkey "$OUT/key.pem" -in "$OUT/cert.pem" -name "OMP GUI (self-signed)" \
       -out "$OUT/omp-gui-selfsign.p12" -passout "pass:$PASS"
base64 < "$OUT/omp-gui-selfsign.p12" | tr -d '\n' > "$OUT/omp-gui-selfsign.p12.base64"
printf '%s\n' "$PASS" > "$OUT/omp-gui-selfsign.password"
rm "$OUT/key.pem" "$OUT/cert.cnf"
echo "certificate: $(openssl x509 -in "$OUT/cert.pem" -noout -fingerprint -sha1)"
echo "secret MACOS_SELFSIGN_P12_BASE64 = contents of $OUT/omp-gui-selfsign.p12.base64"
echo "secret MACOS_SELFSIGN_PASSWORD   = contents of $OUT/omp-gui-selfsign.password"
