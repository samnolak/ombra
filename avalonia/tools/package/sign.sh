#!/usr/bin/env bash
# Code signing of the packages, only with credentials the repository owner has put into the CI secrets. Nothing is
# bought, no OS protection is bypassed, and a package that could not be signed says so in <package>.signing.txt
# (never "signed" without a signature that verifies).
#
#   tools/package/sign.sh windows  <folder> <record>          Authenticode (SHA-256, RFC 3161 timestamp) on Ombra.exe, OmpGui.exe and
#                                                             the client's own Ombra*.dll; verified with signtool verify /pa
#   tools/package/sign.sh macos    <app> <record>             Developer ID, hardened runtime, packaging/entitlements.plist,
#                                                             every Mach-O inside, then the bundle; else the project's own
#                                                             self-signed certificate; else an ad-hoc signature
#   tools/package/sign.sh notarize <zip> <app> <record>       notarytool submit --wait, staple the app, re-zip; else skipped
#
# Secrets (all optional; missing = unsigned, the package is still built):
#   WINDOWS_CERT_PFX_BASE64, WINDOWS_CERT_PASSWORD, WINDOWS_TIMESTAMP_URL (default http://timestamp.digicert.com)
#   MACOS_CERT_P12_BASE64, MACOS_CERT_PASSWORD, MACOS_SIGN_IDENTITY ("Developer ID Application: … (TEAMID)")
#   MACOS_SELFSIGN_P12_BASE64, MACOS_SELFSIGN_PASSWORD (tools/release/macos-selfsign-keygen.sh; used without a Developer ID)
#   APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD (an app-specific password, for notarization)
set -euo pipefail
MODE=${1:?mode}; TARGET=${2:?target}
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
TMP=$(mktemp -d)
KEYCHAINS=()        # the user's keychain search list before open_keychain changed it (SEARCH_LIST=1), restored on exit
cleanup() {
  if [ -n "${SEARCH_LIST:-}" ]; then security list-keychains -d user -s ${KEYCHAINS[@]+"${KEYCHAINS[@]}"} || true; fi
  if [ -n "${KEYCHAIN:-}" ]; then security delete-keychain "$KEYCHAIN" 2>/dev/null || true; fi
  rm -rf "$TMP"
}
trap cleanup EXIT
record() { echo "$1" >> "$REC"; echo "signing: $1"; }

# Nobody watches a CI machine: a keychain or SecurityAgent prompt would wait until the job's timeout. Every security and
# codesign call here has a hard deadline (SIGALRM through perl, which macOS has; it has no timeout(1)): past it the call
# is killed and the run fails. Only the tool and its verb are printed, never the arguments (passwords).
bounded() {
  local secs=$1 rc=0; shift
  perl -e 'alarm shift; exec { $ARGV[0] } @ARGV or die "exec $ARGV[0]: $!\n"' "$secs" "$@" || rc=$?
  [ "$rc" != 142 ] || echo "timed out after ${secs}s: ${1##*/} ${2:-}" >&2
  return "$rc"
}
security() { bounded 60 /usr/bin/security "$@"; }
codesign() { bounded 900 /usr/bin/codesign "$@"; }

# A throwaway keychain with only the signing identity, for this run: a random password, unlocked, codesign allowed to
# use the key without asking (-T and the partition list), and put first in the user's search list, where codesign looks
# for the identity even with --keychain. No trust setting is needed: codesign signs with an untrusted self-signed
# identity named by its SHA-1. On exit the search list is restored and the keychain deleted.
open_keychain() { # <p12> <password>
  local pass list k
  pass=$(openssl rand -hex 16)
  list=$(security list-keychains -d user)
  while IFS= read -r k; do [ -z "$k" ] || { k=${k#*\"}; KEYCHAINS+=("${k%\"*}"); }; done <<< "$list"
  KEYCHAIN="$TMP/signing.keychain-db"
  security create-keychain -p "$pass" "$KEYCHAIN"
  security set-keychain-settings -lut 3600 "$KEYCHAIN"
  security unlock-keychain -p "$pass" "$KEYCHAIN"
  security import "$1" -k "$KEYCHAIN" -P "$2" -T /usr/bin/codesign >/dev/null
  security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$pass" "$KEYCHAIN" >/dev/null
  SEARCH_LIST=1
  security list-keychains -d user -s "$KEYCHAIN" ${KEYCHAINS[@]+"${KEYCHAINS[@]}"}
}

# Every macOS mode enables hardened runtime. Local identities have no Team ID:
# library validation otherwise prevents loading even our own signed .NET libraries.
# Only that check is excepted locally; Developer ID retains library validation.
sign_macos() {
  local identity=$1; local trust=$2; shift 2
  local ent="$HERE/packaging/entitlements.plist"
  if [ "$trust" = local ]; then
    cp "$ent" "$TMP/local-entitlements.plist"
    ent="$TMP/local-entitlements.plist"
    /usr/libexec/PlistBuddy -c 'Add :com.apple.security.cs.disable-library-validation bool true' "$ent"
  fi
  local main
  main=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")
  local f
  while IFS= read -r -d '' f; do
    # Signing the main executable signs its enclosing bundle: defer it until every
    # nested binary is ready.
    [ "$f" = "$APP/Contents/MacOS/$main" ] && continue
    if file -b "$f" | grep -q 'Mach-O'; then
      codesign --force --options runtime --entitlements "$ent" "$@" --sign "$identity" "$f"
    fi
  done < <(find "$APP/Contents/MacOS" -type f -print0)
  # The bundled engine (package.sh): every Mach-O in it, before the bundle is sealed. Bun keeps its vendor's Developer ID
  # signature when that verifies (with its own hardened-runtime exceptions for its JIT and for native add-ons); the npm
  # prebuilt .node/.dylib files come ad-hoc signed and get this signature. An executable signed here gets Bun's exceptions
  # (packaging/engine-entitlements.plist), never the app's.
  local desc engine="$APP/Contents/Resources/engine" signed=0 kept=0
  if [ -d "$engine" ]; then
    while IFS= read -r -d '' f && IFS= read -r desc; do
      case "$desc" in *Mach-O*) ;; *) continue ;; esac
      if codesign --verify --strict "$f" 2>/dev/null && [[ $(codesign -dvv "$f" 2>&1) == *$'\nAuthority=Developer ID Application'* ]]; then
        kept=$((kept + 1)); continue
      fi
      case "$desc" in
        *executable*) codesign --force --options runtime --entitlements "$HERE/packaging/engine-entitlements.plist" "$@" --sign "$identity" "$f" ;;
        *) codesign --force --options runtime "$@" --sign "$identity" "$f" ;;
      esac
      signed=$((signed + 1))
    done < <(find "$engine" -type f -print0 | xargs -0 file -N -0)
    echo "engine: $signed Mach-O signed here, $kept kept with their vendor's Developer ID signature"
  fi
  # The current package also keeps managed DLLs/PDBs in Contents/MacOS. Deep signing
  # gives those generic nested-code signatures before sealing the enclosing bundle.
  codesign --force --deep --options runtime --entitlements "$ent" "$@" --sign "$identity" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
}

case "$MODE" in
  windows)
    REC=${3:?record}
    if [ -z "${WINDOWS_CERT_PFX_BASE64:-}" ] || [ -z "${WINDOWS_CERT_PASSWORD:-}" ]; then
      record "windows: UNSIGNED (no Authenticode certificate in the CI secrets)"; exit 0
    fi
    SIGNTOOL=$(ls -d "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/signtool.exe 2>/dev/null | sort -V | tail -1)
    [ -x "$SIGNTOOL" ] || { echo "signtool.exe not found"; exit 1; }
    printf '%s' "$WINDOWS_CERT_PFX_BASE64" | base64 -d > "$TMP/cert.pfx"
    FILES=("$TARGET/Ombra.exe")
    [ -f "$TARGET/OmpGui.exe" ] && FILES+=("$TARGET/OmpGui.exe") # the launcher copy for OMP GUI updaters (package.sh)
    for f in "$TARGET"/Ombra*.dll; do [ -f "$f" ] && FILES+=("$f"); done
    WIN=(); for f in "${FILES[@]}"; do WIN+=("$(cygpath -w "$f")"); done
    # Git Bash rewrites arguments that look like POSIX paths ("/fd" → "C:/Program Files/Git/fd"), so signtool lost
    # its switches; the paths above are already converted with cygpath.
    export MSYS2_ARG_CONV_EXCL='*'
    "$SIGNTOOL" sign /fd sha256 /td sha256 /tr "${WINDOWS_TIMESTAMP_URL:-http://timestamp.digicert.com}" \
      /f "$(cygpath -w "$TMP/cert.pfx")" /p "$WINDOWS_CERT_PASSWORD" "${WIN[@]}" > "$TMP/sign.log" 2>&1 || { cat "$TMP/sign.log"; exit 1; }
    "$SIGNTOOL" verify /pa "${WIN[@]}" > "$TMP/verify.log" 2>&1 || { cat "$TMP/verify.log"; exit 1; }
    record "windows: Authenticode, verified (signtool verify /pa): ${#FILES[@]} files ($(cd "$TARGET" && ls Ombra.exe OmpGui.exe Ombra*.dll 2>/dev/null | tr '\n' ' '))"
    ;;

  macos)
    REC=${3:?record}
    APP=$TARGET
    if [ -z "${MACOS_CERT_P12_BASE64:-}" ] || [ -z "${MACOS_CERT_PASSWORD:-}" ] || [ -z "${MACOS_SIGN_IDENTITY:-}" ]; then
      if [ -n "${MACOS_SELFSIGN_P12_BASE64:-}" ] && [ -n "${MACOS_SELFSIGN_PASSWORD:-}" ]; then
        # macOS keeps what the user allowed (Documents, Desktop, microphone…) for the app's designated requirement. An
        # ad-hoc signature's is its cdhash, different in every build: each update lost every permission and macOS asked
        # again. Signed with one long-lived certificate the requirement is "identifier and this certificate", the same
        # for every release, so a permission given once stays. Gatekeeper still warns (not a Developer ID).
        printf '%s' "$MACOS_SELFSIGN_P12_BASE64" | base64 -d > "$TMP/selfsign.p12"
        open_keychain "$TMP/selfsign.p12" "$MACOS_SELFSIGN_PASSWORD"
        HASH=$(security find-identity -p codesigning "$KEYCHAIN" | awk '$1 == "1)" { print $2; exit }')
        [ -n "$HASH" ] || { echo "no code signing identity in MACOS_SELFSIGN_P12_BASE64"; exit 1; }
        sign_macos "$HASH" local --keychain "$KEYCHAIN"
        REQ=$(codesign -d -r- "$APP" 2>&1 | sed -n 's/^designated => //p')
        case "$REQ" in *"certificate leaf = H\"$(echo "$HASH" | tr 'A-F' 'a-f')\""*|*"certificate root = H\"$(echo "$HASH" | tr 'A-F' 'a-f')\""*) ;;
          *) echo "unexpected designated requirement: $REQ"; exit 1 ;; esac
        CDHASH=$(codesign -dvvv "$APP" 2>&1 | sed -n 's/^CDHash=//p')
        record "macos: self-signed (project certificate $HASH; permissions stay across updates; cdhash $CDHASH), hardened runtime with local library-validation exception, verified; not a Developer ID, Gatekeeper will warn"
        exit 0
      fi
      sign_macos "-" local
      # Nightly pins this Ombra by its cdhash (zen.embed.peer-cdhash, written at launch; SECURITY.md).
      CDHASH=$(codesign -dvvv "$APP" 2>&1 | sed -n 's/^CDHash=//p')
      record "macos: UNSIGNED — ad-hoc signature only (cdhash $CDHASH), hardened runtime with local library-validation exception, verified (no certificate in the CI secrets); Gatekeeper will warn, and macOS asks for permissions again after every update"
      exit 0
    fi
    printf '%s' "$MACOS_CERT_P12_BASE64" | base64 -d > "$TMP/cert.p12"
    open_keychain "$TMP/cert.p12" "$MACOS_CERT_PASSWORD"
    sign_macos "$MACOS_SIGN_IDENTITY" developer --timestamp --keychain "$KEYCHAIN"
    record "macos: Developer ID ($MACOS_SIGN_IDENTITY), hardened runtime, verified (codesign --verify --deep --strict)"
    ;;

  notarize)
    ZIP=$TARGET; APP=${3:?app}; REC=${4:?record}
    if [ -z "${APPLE_ID:-}" ] || [ -z "${APPLE_TEAM_ID:-}" ] || [ -z "${APPLE_APP_PASSWORD:-}" ] || ! grep -q '^macos: Developer ID (' "$REC"; then
      record "macos: NOT NOTARIZED (needs a Developer ID signature and APPLE_ID / APPLE_TEAM_ID / APPLE_APP_PASSWORD)"; exit 0
    fi
    xcrun notarytool submit "$ZIP" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait --timeout 30m \
      | tee "$TMP/notary.log"
    grep -q "status: Accepted" "$TMP/notary.log" || { echo "notarization was not accepted"; exit 1; }
    xcrun stapler staple "$APP"
    xcrun stapler validate "$APP"
    spctl --assess --type execute --verbose=2 "$APP"
    PARENT=$(dirname "$APP")
    rm -f "$ZIP"
    (cd "$(dirname "$PARENT")" && ditto -c -k --sequesterRsrc --keepParent "$(basename "$PARENT")" "$ZIP")
    record "macos: notarized and stapled (notarytool: Accepted; spctl --assess passed)"
    ;;

  *) echo "unknown mode $MODE"; exit 1 ;;
esac
