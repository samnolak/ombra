#!/usr/bin/env bash
# Ombra team server — one-command install / upgrade (SERVER_DESIGN §1 "Установка одной командой", §2.4, §6.2).
#
#   ./deploy/install.sh --ip 203.0.113.5        [--release-key KEY.pub] [--backup-recipient age1...] [--systemd]
#   ./deploy/install.sh --domain team.example.com [--acme-email ops@example.com] ...   (Docker only: Caddy/ACME)
#   ./deploy/install.sh --upgrade               (backup → migrate → start → health check, image rollback on failure)
#
# Run from an unpacked release (or a source checkout with --unsigned-dev). Never `curl | sh`.
# Every file of the release tree (bin/, deploy/, images/) must be listed in the signed SHA256SUMS.
# install.sh cannot vouch for itself: BEFORE running it, verify the release in its directory with the publisher key
# obtained out of band:
#   cd <release> && openssl dgst -sha256 -verify release.pub -signature SHA256SUMS.sig SHA256SUMS && sha256sum -c SHA256SUMS
#   (macOS: shasum -a 256 -c SHA256SUMS)
# Docker Engine + Compose v2 are used; with --systemd the server runs from bin/ with a local PostgreSQL instead.
set -euo pipefail
umask 077

ETC=${OMBRA_ETC:-/etc/ombra}
# The offline TLS next key (§6.2) is generated here: root-only, never mounted into or readable by the server.
OFFLINE=${OMBRA_OFFLINE_DIR:-$ETC-offline}
DEPLOY_DIR=$(cd "$(dirname "$0")" && pwd)
RELEASE_DIR=$(cd "$DEPLOY_DIR/.." && pwd)
INSTALL_DIR=${OMBRA_INSTALL_DIR:-/opt/ombra}
BACKUP_DIR=${OMBRA_BACKUP_DIR:-/var/backups/ombra}
APP_UID=1654   # "app" user of the mcr.microsoft.com/dotnet/aspnet image
# Files under $ETC the server process reads; nothing else there is readable by it (db_password, server.env stay root's).
APP_READ_FILES="token.key server-id backup-recipients.txt tls/current.crt tls/current.key"
MODE= ADDR= DOMAIN= ACME_EMAIL= RELEASE_KEY= BACKUP_RECIPIENT= UPGRADE=0 UNSIGNED=0 SYSTEMD=0 IMAGE_ARCHIVE=
STAGE_DIR= STAGED_ARCHIVE=
trap '[ -z "$STAGE_DIR" ] || rm -rf "$STAGE_DIR"' EXIT

die() { echo "install.sh: $*" >&2; exit 1; }
log() { echo "==> $*" >&2; }  # progress on stderr: stdout of helpers is captured

while [ $# -gt 0 ]; do
  case "$1" in
    --ip) MODE=ip; ADDR=${2:?}; shift 2 ;;
    --domain) MODE=domain; DOMAIN=${2:?}; shift 2 ;;
    --acme-email) ACME_EMAIL=${2:?}; shift 2 ;;
    --release-key) RELEASE_KEY=${2:?}; shift 2 ;;
    --backup-recipient) BACKUP_RECIPIENT=${2:?}; shift 2 ;;
    --image-archive) IMAGE_ARCHIVE=${2:?}; shift 2 ;;
    --upgrade) UPGRADE=1; shift ;;
    --systemd) SYSTEMD=1; shift ;;
    --unsigned-dev) UNSIGNED=1; shift ;;
    -h|--help) awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$0"; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done

# Domain mode is plain HTTP behind Caddy, and only the Docker variant ships Caddy: a systemd Kestrel would serve
# plain HTTP on the public port.
if [ "$SYSTEMD" = 1 ] && [ "$MODE" = domain ]; then
  die "--systemd supports only --ip (Kestrel terminates TLS); --domain needs the Docker variant (Caddy/ACME)"
fi
case "$OFFLINE/" in "$ETC"/*) die "OMBRA_OFFLINE_DIR ($OFFLINE) must be outside $ETC" ;; esac

[ "$(id -u)" = 0 ] || die "run as root"
command -v openssl >/dev/null || die "openssl is required"
command -v curl >/dev/null || die "curl is required"

# ---------- 1. release signature: every file of the tree is covered by the signed SHA256SUMS ----------
verify_release() {
  if [ "$UNSIGNED" = 1 ]; then
    echo "WARNING: --unsigned-dev: release signature NOT verified (development checkout only)" >&2
    return
  fi
  local sums="$RELEASE_DIR/SHA256SUMS" odd unlisted
  [ -f "$sums" ] && [ -f "$RELEASE_DIR/SHA256SUMS.sig" ] || die "SHA256SUMS(.sig) missing: not a signed release"
  [ -n "$RELEASE_KEY" ] || die "--release-key <publisher key obtained out of band> is required"
  openssl dgst -sha256 -verify "$RELEASE_KEY" -signature "$RELEASE_DIR/SHA256SUMS.sig" "$sums" >/dev/null \
    || die "release signature is INVALID"
  if grep -Evq '^[0-9a-f]{64}  [^/ ][^ ]*$' "$sums" || grep -Eq '(  |/)\.\.(/|$)' "$sums"; then
    die "SHA256SUMS has malformed or out-of-tree entries"
  fi
  (cd "$RELEASE_DIR" && sha256sum --quiet -c SHA256SUMS) || die "release files do not match SHA256SUMS"
  # `sha256sum -c` ignores files that are not listed: anything else in the tree is rejected.
  odd=$(cd "$RELEASE_DIR" && find . ! -type f ! -type d)
  [ -z "$odd" ] || die "release tree contains non-regular files (symlinks?): $(echo "$odd" | tr '\n' ' ')"
  unlisted=$(cd "$RELEASE_DIR" && LC_ALL=C comm -13 <(cut -c67- SHA256SUMS | LC_ALL=C sort) \
    <(find . -type f ! -path ./SHA256SUMS ! -path ./SHA256SUMS.sig | sed 's|^\./||' | LC_ALL=C sort))
  [ -z "$unlisted" ] || die "files not covered by the signed SHA256SUMS: $(echo "$unlisted" | tr '\n' ' ')"
  log "release signature OK (key sha256 $(openssl pkey -pubin -in "$RELEASE_KEY" -outform DER | sha256sum | cut -c1-64))"
}

# The image archive (default or --image-archive, wherever it lies) is first copied into a root-only directory; signed
# releases then require that copy to be byte-identical to a signed images/* entry, and exactly that copy is loaded
# later, so swapping the original after the check has no effect. Runs right after verify_release, before anything
# is changed on the host.
stage_image_archive() {
  local archive=${IMAGE_ARCHIVE:-$RELEASE_DIR/images/ombra-server.tar} sum
  if [ ! -f "$archive" ]; then
    [ "$UNSIGNED" = 1 ] || die "image archive not found ($archive)"
    return 0  # development: load_image builds from source
  fi
  STAGE_DIR=$(mktemp -d "${TMPDIR:-/var/tmp}/ombra-image.XXXXXX")
  chown 0:0 "$STAGE_DIR"; chmod 0700 "$STAGE_DIR"
  cp "$archive" "$STAGE_DIR/image.tar"
  STAGED_ARCHIVE=$STAGE_DIR/image.tar
  [ "$UNSIGNED" = 0 ] || return 0
  sum=$(sha256sum < "$STAGED_ARCHIVE" | cut -c1-64)
  awk -v s="$sum" '$1 == s && $2 ~ /^images\// { found = 1 } END { exit !found }' "$RELEASE_DIR/SHA256SUMS" \
    || die "image archive $archive (sha256 $sum) is not listed in the signed SHA256SUMS"
}

spki_hex() { openssl pkey -in "$1" -pubout -outform DER | openssl dgst -sha256 -binary | od -An -tx1 | tr -d ' \n'; }
spki_b64() { openssl pkey -in "$1" -pubout -outform DER | openssl dgst -sha256 -binary | openssl base64 -A; }

# A missing bind-mount source becomes a root-owned directory: every compose command that creates containers first
# makes sure the mounted files exist.
compose() {
  case "${1:-}" in up|run|create|start|restart) ensure_recipients_file "$APP_UID:$APP_UID" ;; esac
  docker compose --project-directory "$INSTALL_DIR" -f "$INSTALL_DIR/compose.yaml" "$@"
}

wait_health() {
  local url=$1; shift
  for _ in $(seq 1 60); do
    if curl -fsS --max-time 3 "$@" "$url" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

health_args() {
  if [ "$(cat "$ETC/mode")" = ip ]; then
    echo "--insecure --pinnedpubkey sha256//$(spki_b64 "$ETC/tls/current.key")"
  fi
}
health_url() {
  if [ "$(cat "$ETC/mode")" = ip ]; then echo "https://127.0.0.1/v1/health"; else echo "https://$(cat "$ETC/domain")/v1/health"; fi
}

load_image() {
  if [ -n "$STAGED_ARCHIVE" ]; then
    log "loading image ${IMAGE_ARCHIVE:-$RELEASE_DIR/images/ombra-server.tar} (verified copy $STAGED_ARCHIVE)"
    local loaded
    loaded=$(docker load -i "$STAGED_ARCHIVE" | sed -n 's/^Loaded image: //p' | tail -1)
    [ -n "$loaded" ] || die "docker load did not report an image"
    echo "$loaded"
  elif [ "$UNSIGNED" = 1 ] && [ -f "$RELEASE_DIR/deploy/Dockerfile" ]; then
    log "building image from source (development)"
    docker build -q -f "$RELEASE_DIR/deploy/Dockerfile" -t ombra-server:local "$RELEASE_DIR" >/dev/null
    echo "ombra-server:local"
  else
    die "image archive not found (${IMAGE_ARCHIVE:-$RELEASE_DIR/images/ombra-server.tar})"
  fi
}

backup_now() { # $1 = file name in $BACKUP_DIR
  [ -s "$ETC/backup-recipients.txt" ] || die "no $ETC/backup-recipients.txt: pass --backup-recipient age1... once"
  compose exec -T server ombra-admin backup --out "/backups/$1" --recipients-file /etc/ombra/backup-recipients.txt >/dev/null
  log "backup written: $BACKUP_DIR/$1"
}

prepare_offline() { mkdir -p "$OFFLINE"; chown 0:0 "$OFFLINE"; chmod 0700 "$OFFLINE"; }

# Releases before this one left the offline next key in $ETC/tls, readable by the server: move it out.
stash_next_key() {
  [ -e "$ETC/tls/next.key" ] || return 0
  prepare_offline
  local dest="$OFFLINE/next.key"
  [ ! -e "$dest" ] || dest="$OFFLINE/next.key.$(date -u +%Y%m%dT%H%M%SZ)"
  mv "$ETC/tls/next.key" "$dest"
  echo "WARNING: moved $ETC/tls/next.key to $dest; copy it off this host and delete it here (§6.2)" >&2
}

# backup-recipients.txt is bind-mounted read-only even while empty; it must be a regular file before compose runs.
recipients_not_dir() {
  local f=$ETC/backup-recipients.txt
  if [ -d "$f" ]; then rmdir "$f" 2>/dev/null || die "$f is a non-empty directory; it must be a file of age recipients"; fi
}
ensure_recipients_file() { # $1 = owner:group of the server process
  local f=$ETC/backup-recipients.txt
  recipients_not_dir
  [ -f "$f" ] || : > "$f"
  chown "$1" "$f"; chmod 0400 "$f"
}
# Written in place: a running container keeps the bind-mounted inode. The caller restores 0400 (secure_etc).
set_recipient() {
  local f=$ETC/backup-recipients.txt
  recipients_not_dir
  [ ! -e "$f" ] || chmod u+w "$f"
  echo "$1" > "$f"
}

# $ETC is root's; the server user ($1:$2) may only read $APP_READ_FILES (the Docker variant bind-mounts exactly these).
secure_etc() {
  local owner=$1 group=$2 f
  mkdir -p "$ETC/tls"
  ensure_recipients_file "$owner:$group"
  stash_next_key
  chown -R 0:0 "$ETC"
  chown "0:$group" "$ETC" "$ETC/tls"
  chmod 0750 "$ETC" "$ETC/tls"
  for f in $APP_READ_FILES; do
    if [ -e "$ETC/$f" ]; then chown "$owner:$group" "$ETC/$f"; chmod 0400 "$ETC/$f"; fi
  done
}

# ---------- upgrade ----------
if [ "$UPGRADE" = 1 ]; then
  [ -f "$ETC/server.env" ] || die "not installed ($ETC/server.env missing)"
  verify_release
  [ "$SYSTEMD" = 0 ] || die "--upgrade of the systemd variant: replace $INSTALL_DIR/bin and run ombra-admin migrate"
  stage_image_archive
  [ -z "$BACKUP_RECIPIENT" ] || set_recipient "$BACKUP_RECIPIENT"
  secure_etc "$APP_UID" "$APP_UID"
  # shellcheck disable=SC1091
  . "$INSTALL_DIR/.env"
  OLD_IMAGE=$OMBRA_IMAGE
  backup_now "pre-upgrade-$(date -u +%Y%m%dT%H%M%SZ).age"
  docker tag "$OLD_IMAGE" ombra-server:rollback
  NEW_IMAGE=$(load_image)
  cp "$DEPLOY_DIR/compose.yaml" "$INSTALL_DIR/compose.yaml"
  sed -i "s|^OMBRA_IMAGE=.*|OMBRA_IMAGE=$NEW_IMAGE|" "$INSTALL_DIR/.env"
  log "migrating to $NEW_IMAGE"
  if compose run --rm --no-deps --entrypoint ombra-admin server migrate && compose up -d && wait_health "$(health_url)" $(health_args); then
    log "upgrade OK: $OLD_IMAGE → $NEW_IMAGE"
    exit 0
  fi
  echo "upgrade FAILED: rolling back the image to $OLD_IMAGE (schema stays forward-compatible with N-1)" >&2
  sed -i "s|^OMBRA_IMAGE=.*|OMBRA_IMAGE=ombra-server:rollback|" "$INSTALL_DIR/.env"
  compose up -d
  wait_health "$(health_url)" $(health_args) || echo "rollback health check failed too; see: docker compose logs server" >&2
  exit 1
fi

# ---------- fresh install ----------
[ -n "$MODE" ] || die "--ip <address> or --domain <name> is required"
[ ! -e "$ETC/server.env" ] || die "$ETC/server.env exists: already installed (use --upgrade)"
verify_release
[ "$SYSTEMD" = 1 ] || stage_image_archive
if [ "$SYSTEMD" = 0 ]; then
  command -v docker >/dev/null || die "Docker Engine is required (or use --systemd)"
  docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required"
fi

# ---------- 2. secrets in /etc/ombra (0600) ----------
log "generating secrets in $ETC"
mkdir -p "$ETC/tls" "$BACKUP_DIR"
chmod 0700 "$ETC" "$ETC/tls"
DB_PASSWORD=$(openssl rand -hex 24)
printf '%s' "$DB_PASSWORD" > "$ETC/db_password"
openssl rand -base64 32 > "$ETC/token.key"
SERVER_ID=$(cat /proc/sys/kernel/random/uuid)
echo "$SERVER_ID" > "$ETC/server-id"
echo "$MODE" > "$ETC/mode"
[ -z "$BACKUP_RECIPIENT" ] || set_recipient "$BACKUP_RECIPIENT"

if [ "$SYSTEMD" = 1 ]; then DB_HOST=localhost; LISTEN_PORT=443; else DB_HOST=postgres; LISTEN_PORT=8443; fi

# ---------- 3. TLS: IP mode → long-lived SPKI key + offline next key; domain mode → Caddy/ACME ----------
if [ "$MODE" = ip ]; then
  log "generating TLS keys (ECDSA P-256): current in $ETC/tls, next in $OFFLINE (offline key)"
  openssl ecparam -name prime256v1 -genkey -noout -out "$ETC/tls/current.key.tmp"
  openssl pkcs8 -topk8 -nocrypt -in "$ETC/tls/current.key.tmp" -out "$ETC/tls/current.key"
  rm -f "$ETC/tls/current.key.tmp"
  prepare_offline
  [ ! -e "$OFFLINE/next.key" ] || die "$OFFLINE/next.key exists (offline key of an earlier install?): move it away first"
  openssl ecparam -name prime256v1 -genkey -noout -out "$OFFLINE/next.key.tmp"
  openssl pkcs8 -topk8 -nocrypt -in "$OFFLINE/next.key.tmp" -out "$OFFLINE/next.key"
  rm -f "$OFFLINE/next.key.tmp"
  chmod 0600 "$OFFLINE/next.key"
  openssl req -new -x509 -key "$ETC/tls/current.key" -out "$ETC/tls/current.crt" -days 3650 \
    -subj "/CN=$ADDR" -addext "subjectAltName=IP:$ADDR" 2>/dev/null
  PIN_CURRENT=$(spki_hex "$ETC/tls/current.key")
  PIN_NEXT=$(spki_hex "$OFFLINE/next.key")
  SCHEME_URL="https://0.0.0.0:$LISTEN_PORT"
  PUBLISH_ADDR=0.0.0.0; PUBLISH_PORT=443; PROFILES=
else
  echo "$DOMAIN" > "$ETC/domain"
  {
    if [ -n "$ACME_EMAIL" ]; then printf '{\n\temail %s\n}\n\n' "$ACME_EMAIL"; fi
    printf '%s {\n\ttls {\n\t\tissuer acme {\n\t\t\tdisable_http_challenge\n\t\t}\n\t}\n\treverse_proxy server:8443\n}\n' "$DOMAIN"
  } > "$ETC/Caddyfile"
  chmod 0644 "$ETC/Caddyfile"
  # Plain HTTP: 0.0.0.0 is the container's own interface (Caddy reaches server:8443 on the edge network);
  # on the host it is published on loopback only.
  SCHEME_URL="http://0.0.0.0:$LISTEN_PORT"
  PUBLISH_ADDR=127.0.0.1; PUBLISH_PORT=8443; PROFILES=domain
fi

cat > "$ETC/server.env" <<EOF
Ombra__ConnectionString=Host=$DB_HOST;Database=ombra;Username=ombra;Password=$DB_PASSWORD
Ombra__Mode=$MODE
Ombra__ListenUrl=$SCHEME_URL
Ombra__DataDir=/var/lib/ombra
Ombra__TokenKeyFile=/etc/ombra/token.key
Ombra__ServerIdFile=/etc/ombra/server-id
Ombra__TlsCertFile=/etc/ombra/tls/current.crt
Ombra__TlsKeyFile=/etc/ombra/tls/current.key
EOF

if [ "$SYSTEMD" = 1 ]; then
  # ---------- systemd variant: own PostgreSQL, binaries from the (verified) release ----------
  for f in server/Ombra.Server ombra-admin/ombra-admin; do
    [ -x "$RELEASE_DIR/bin/$f" ] || die "release has no bin/$f (self-contained build) for --systemd"
  done
  [ -f "$RELEASE_DIR/bin/lib/libombra_core.so" ] || die "release has no bin/lib/libombra_core.so for --systemd"
  apt-get update -qq && apt-get install -y -qq postgresql age >/dev/null
  id ombra >/dev/null 2>&1 || useradd --system --home /var/lib/ombra --shell /usr/sbin/nologin ombra
  mkdir -p /var/lib/ombra "$INSTALL_DIR"
  cp -a "$RELEASE_DIR/bin/." "$INSTALL_DIR/bin/"
  runuser -u postgres -- psql -q -c "CREATE ROLE ombra LOGIN PASSWORD '$DB_PASSWORD'" \
    -c "CREATE DATABASE ombra OWNER ombra"
  chown -R ombra:ombra /var/lib/ombra "$BACKUP_DIR"
  secure_etc ombra ombra
  chown ombra:ombra "$ETC/server.env"  # read by the ombra-admin wrapper below (runs as ombra)
  install -m 0644 "$DEPLOY_DIR/ombra-server.service" /etc/systemd/system/ombra-server.service
  printf '#!/bin/sh\nOMBRA_ENV_FILE=%s/server.env OMBRA_CORE_NATIVE_PATH=%s/bin/lib/libombra_core.so exec runuser -u ombra -- %s/bin/ombra-admin/ombra-admin "$@"\n' \
    "$ETC" "$INSTALL_DIR" "$INSTALL_DIR" > /usr/local/bin/ombra-admin
  chmod 0755 /usr/local/bin/ombra-admin
  ombra-admin migrate
  systemctl daemon-reload && systemctl enable --now ombra-server
  ADMIN="ombra-admin"
else
  # ---------- Docker variant ----------
  chown -R "$APP_UID:$APP_UID" "$BACKUP_DIR"
  secure_etc "$APP_UID" "$APP_UID"
  chmod 0644 "$ETC/db_password" # read by the postgres entrypoint (root) via a compose secret bind mount
  IMAGE=$(load_image)
  mkdir -p "$INSTALL_DIR"
  cp "$DEPLOY_DIR/compose.yaml" "$INSTALL_DIR/compose.yaml"
  cat > "$INSTALL_DIR/.env" <<EOF
OMBRA_IMAGE=$IMAGE
OMBRA_PUBLISH_ADDR=$PUBLISH_ADDR
OMBRA_PUBLISH_PORT=$PUBLISH_PORT
OMBRA_BACKUP_DIR=$BACKUP_DIR
COMPOSE_PROFILES=$PROFILES
EOF
  log "starting postgres"
  compose up -d postgres
  # ---------- 4. migrations, start ----------
  log "applying migrations"
  compose run --rm --entrypoint ombra-admin server migrate
  log "starting server"
  compose up -d
  ADMIN="compose exec -T server ombra-admin"
  # daily backups, 14 kept (§1, §8 п.1) — only when an operator recipient is configured
  CRON_DIR=/etc/cron.daily; [ -d "$CRON_DIR" ] || CRON_DIR=/etc/periodic/daily
  if [ -d "$CRON_DIR" ]; then
    cat > "$CRON_DIR/ombra-backup" <<EOF
#!/bin/sh
[ -s $ETC/backup-recipients.txt ] || exit 0
f=ombra-\$(date -u +%Y%m%d).age
docker compose --project-directory $INSTALL_DIR -f $INSTALL_DIR/compose.yaml exec -T server ombra-admin backup --out /backups/\$f --recipients-file /etc/ombra/backup-recipients.txt >/dev/null
ls -1t $BACKUP_DIR/ombra-*.age 2>/dev/null | tail -n +15 | xargs -r rm -f
EOF
    chmod 0755 "$CRON_DIR/ombra-backup"
  else
    echo "WARNING: no cron.daily directory; schedule '$ADMIN backup' yourself" >&2
  fi
fi

# ---------- 5. health check (fatal in every mode), then bootstrap code (one-time, TTL 1 h) + pin ----------
if [ "$SYSTEMD" = 1 ]; then ISSUE="ombra-admin"; else ISSUE="docker compose --project-directory $INSTALL_DIR exec server ombra-admin"; fi
if [ "$MODE" = ip ]; then ISSUE="$ISSUE bootstrap issue --pin $PIN_CURRENT --ttl-minutes 60"; else ISSUE="$ISSUE bootstrap issue --ttl-minutes 60"; fi
if ! wait_health "$(health_url)" $(health_args); then
  if [ "$MODE" = domain ]; then
    echo "https://$DOMAIN is not healthy (DNS record? port 443 reachable? ACME?); see: docker compose logs caddy server" >&2
  else
    echo "the server did not become healthy; see: $( [ "$SYSTEMD" = 1 ] && echo "journalctl -u ombra-server" || echo "docker compose logs server")" >&2
  fi
  die "install FAILED: no bootstrap code was issued; once the health check passes, run: $ISSUE"
fi

if [ "$MODE" = ip ]; then
  BOOT=$($ADMIN bootstrap issue --pin "$PIN_CURRENT" --ttl-minutes 60)
else
  BOOT=$($ADMIN bootstrap issue --ttl-minutes 60)
fi

echo
echo "================ Ombra server installed ================"
echo "serverId:          $SERVER_ID"
if [ "$MODE" = ip ]; then
  echo "endpoint:          https://$ADDR"
  echo "TLS pin (current): $PIN_CURRENT"
  echo "TLS pin (next):    $PIN_NEXT"
  echo "offline next key:  $OFFLINE/next.key"
  echo "                   Copy it OFF this host now and delete it here; it is needed only for 'ombra-admin tls rotate --to-next'."
else
  echo "endpoint:          https://$DOMAIN (Caddy, ACME tls-alpn-01; only port 443)"
fi
echo "$BOOT"
echo "Enter the bootstrap code$( [ "$MODE" = ip ] && echo " and the pin") on the first device; it becomes the server owner."
echo "========================================================"
