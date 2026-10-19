#!/usr/bin/env bash
# A real team server for the feature smoke's "team" section, as install.sh runs it in IP mode: Postgres 17 and the
# server image built from server/deploy/Dockerfile (ombra-server:smoke) on a private docker
# network, TLS 1.3 with a fresh self-signed P-256 key whose SPKI SHA-256 is the pin, and one bootstrap code.
# Usage: team-server.sh up <dir>   → prints "address|pin|code" (also written to <dir>/team-server.txt)
#        team-server.sh down <dir>
set -euo pipefail
CMD=${1:?up|down}; DIR=${2:?dir}
export DOCKER_HOST=${DOCKER_HOST:-unix://$HOME/.colima/default/docker.sock}
DOCKER=$(command -v docker || echo /opt/homebrew/bin/docker)
NAME=ombra-smoke-$(basename "$DIR" | tr -cd 'a-zA-Z0-9')
mkdir -p "$DIR"; DIR=$(cd "$DIR" && pwd)

down() {
  "$DOCKER" rm -f "$NAME-server" "$NAME-pg" >/dev/null 2>&1 || true
  "$DOCKER" network rm "$NAME" >/dev/null 2>&1 || true
}
[ "$CMD" = down ] && { down; exit 0; }

down
REPO=$(cd "$(dirname "$0")/../../../.." && pwd)
"$DOCKER" build -q -t ombra-server:smoke -f "$REPO/server/deploy/Dockerfile" "$REPO/server" >/dev/null
cd "$DIR"
openssl ecparam -name prime256v1 -genkey -noout -out tls.key 2>/dev/null
openssl req -new -x509 -key tls.key -out tls.crt -days 30 -subj "/CN=127.0.0.1" -addext "subjectAltName=IP:127.0.0.1" 2>/dev/null
PIN=$(openssl x509 -in tls.crt -pubkey -noout | openssl pkey -pubin -outform DER | shasum -a 256 | cut -d' ' -f1)
head -c 32 /dev/urandom | base64 > token.key
uuidgen | tr 'A-Z' 'a-z' > server-id
chmod 644 tls.key tls.crt token.key server-id

"$DOCKER" network create "$NAME" >/dev/null
"$DOCKER" run -d --name "$NAME-pg" --network "$NAME" --network-alias pg -e POSTGRES_PASSWORD=smoke -e POSTGRES_DB=ombra postgres:17-alpine >/dev/null
for _ in $(seq 1 60); do "$DOCKER" exec "$NAME-pg" pg_isready -U postgres -d ombra >/dev/null 2>&1 && break; sleep 0.5; done
"$DOCKER" run -d --name "$NAME-server" --network "$NAME" -p 127.0.0.1::8443 \
  -e Ombra__ConnectionString="Host=pg;Database=ombra;Username=postgres;Password=smoke" \
  -e Ombra__Mode=ip -e Ombra__ListenUrl=https://0.0.0.0:8443 \
  -e Ombra__TlsCertFile=/etc/ombra/tls/current.crt -e Ombra__TlsKeyFile=/etc/ombra/tls/current.key \
  -e Ombra__TokenKeyFile=/etc/ombra/token.key -e Ombra__ServerIdFile=/etc/ombra/server-id \
  -e Ombra__DataDir=/var/lib/ombra -e Ombra__AgePath=/usr/bin/age -e Ombra__Limits__PreAuthRequestsPerMinute=100000 \
  -v "$DIR/tls.crt:/etc/ombra/tls/current.crt:ro" -v "$DIR/tls.key:/etc/ombra/tls/current.key:ro" \
  -v "$DIR/token.key:/etc/ombra/token.key:ro" -v "$DIR/server-id:/etc/ombra/server-id:ro" \
  ombra-server:smoke >/dev/null
for _ in $(seq 1 120); do "$DOCKER" logs "$NAME-server" 2>&1 | grep -q "Now listening" && break; sleep 0.5; done
PORT=$("$DOCKER" port "$NAME-server" 8443 | head -1 | sed 's/.*://')

# One bootstrap code, stored as ombra-admin bootstrap issue does: only SHA-256 of the normalised code, bound to the pin.
RAW=$(python3 -c 'import secrets; print("".join(secrets.choice("ABCDEFGHJKLMNPQRSTUVWXYZ23456789") for _ in range(26)))')
HASH=$(printf %s "$RAW" | shasum -a 256 | cut -d' ' -f1)
"$DOCKER" exec "$NAME-pg" psql -q -U postgres -d ombra -c \
  "INSERT INTO bootstrap_codes (code_hash, spki_pin, created_at, expires_at) VALUES (decode('$HASH','hex'), '$PIN', now(), now() + interval '1 hour')"
CODE=$(echo "$RAW" | sed -E 's/(.{5})/\1-/g; s/-$//')
echo "https://127.0.0.1:$PORT|$PIN|$CODE" | tee "$DIR/team-server.txt"
