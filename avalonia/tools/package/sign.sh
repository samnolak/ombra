#!/usr/bin/env bash
# Code signing of the packages, only with credentials the repository owner has put into the CI secrets. Nothing is
# bought, no OS protection is bypassed, and a package that could not be signed says so in <package>.signing.txt
# (never "signed" without a signature that verifies).
#
#   tools/package/sign.sh windows  <folder> <record>          Authenticode (SHA-256, RFC 3161 timestamp) on OmpGui.exe and
#                                                             the client's own OmpGui*.dll; verified with signtool verify /pa
#   tools/package/sign.sh macos    <app> <record>             Developer ID, hardened runtime, packaging/entitlements.plist,
#                                                             every Mach-O inside, then the bundle; else an ad-hoc signature
#   tools/package/sign.sh notarize <zip> <app> <record>       notarytool submit --wait, staple the app, re-zip; else skipped
#
# Secrets (all optional; missing = unsigned, the package is still built):
#   WINDOWS_CERT_PFX_BASE64, WINDOWS_CERT_PASSWORD, WINDOWS_TIMESTAMP_URL (default http://timestamp.digicert.com)
#   MACOS_CERT_P12_BASE64, MACOS_CERT_PASSWORD, MACOS_SIGN_IDENTITY ("Developer ID Application: … (TEAMID)")
#   APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD (an app-specific password, for notarization)
set -euo pipefail
MODE=${1:?mode}; TARGET=${2:?target}
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
TMP=$(mktemp -d)
cleanup() {
  if [ -n "${KEYCHAIN:-}" ]; then security delete-keychain "$KEYCHAIN" 2>/dev/null || true; fi
  rm -rf "$TMP"
}
trap cleanup EXIT
record() { echo "$1" >> "$REC"; echo "signing: $1"; }

case "$MODE" in
  windows)
    REC=${3:?record}
    if [ -z "${WINDOWS_CERT_PFX_BASE64:-}" ] || [ -z "${WINDOWS_CERT_PASSWORD:-}" ]; then
      record "windows: UNSIGNED (no Authenticode certificate in the CI secrets)"; exit 0
    fi
    SIGNTOOL=$(ls -d "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/signtool.exe 2>/dev/null | sort -V | tail -1)
    [ -x "$SIGNTOOL" ] || { echo "signtool.exe not found"; exit 1; }
    printf '%s' "$WINDOWS_CERT_PFX_BASE64" | base64 -d > "$TMP/cert.pfx"
    FILES=("$TARGET/OmpGui.exe")
    for f in "$TARGET"/OmpGui*.dll; do [ -f "$f" ] && FILES+=("$f"); done
    WIN=(); for f in "${FILES[@]}"; do WIN+=("$(cygpath -w "$f")"); done
    "$SIGNTOOL" sign /fd sha256 /td sha256 /tr "${WINDOWS_TIMESTAMP_URL:-http://timestamp.digicert.com}" \
      /f "$(cygpath -w "$TMP/cert.pfx")" /p "$WINDOWS_CERT_PASSWORD" "${WIN[@]}" > "$TMP/sign.log" 2>&1 || { cat "$TMP/sign.log"; exit 1; }
    "$SIGNTOOL" verify /pa "${WIN[@]}" > "$TMP/verify.log" 2>&1 || { cat "$TMP/verify.log"; exit 1; }
    record "windows: Authenticode, verified (signtool verify /pa): ${#FILES[@]} files ($(cd "$TARGET" && ls OmpGui.exe OmpGui*.dll | tr '\n' ' '))"
    ;;

  macos)
    REC=${3:?record}
    APP=$TARGET
    if [ -z "${MACOS_CERT_P12_BASE64:-}" ] || [ -z "${MACOS_CERT_PASSWORD:-}" ] || [ -z "${MACOS_SIGN_IDENTITY:-}" ]; then
      codesign --force --deep --sign - "$APP"          # ad-hoc: Apple Silicon refuses unsigned native code; not a Developer ID
      codesign --verify --deep --strict "$APP"
      record "macos: UNSIGNED — ad-hoc signature only (no Developer ID certificate in the CI secrets); Gatekeeper will warn"
      exit 0
    fi
    KEYCHAIN="$TMP/signing.keychain-db"
    KCPASS=$(openssl rand -hex 16)
    printf '%s' "$MACOS_CERT_P12_BASE64" | base64 -d > "$TMP/cert.p12"
    security create-keychain -p "$KCPASS" "$KEYCHAIN"
    security set-keychain-settings -lut 3600 "$KEYCHAIN"
    security unlock-keychain -p "$KCPASS" "$KEYCHAIN"
    security import "$TMP/cert.p12" -k "$KEYCHAIN" -P "$MACOS_CERT_PASSWORD" -T /usr/bin/codesign >/dev/null
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KCPASS" "$KEYCHAIN" >/dev/null
    security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"')
    ENT="$HERE/packaging/entitlements.plist"
    # Inside out: every Mach-O file (native libraries, the apphost), then the bundle itself.
    while IFS= read -r -d '' f; do
      if file -b "$f" | grep -q 'Mach-O'; then
        codesign --force --timestamp --options runtime --entitlements "$ENT" --keychain "$KEYCHAIN" --sign "$MACOS_SIGN_IDENTITY" "$f"
      fi
    done < <(find "$APP/Contents/MacOS" -type f -print0)
    codesign --force --timestamp --options runtime --entitlements "$ENT" --keychain "$KEYCHAIN" --sign "$MACOS_SIGN_IDENTITY" "$APP"
    codesign --verify --deep --strict --verbose=2 "$APP"
    record "macos: Developer ID ($MACOS_SIGN_IDENTITY), hardened runtime, verified (codesign --verify --deep --strict)"
    ;;

  notarize)
    ZIP=$TARGET; APP=${3:?app}; REC=${4:?record}
    if [ -z "${APPLE_ID:-}" ] || [ -z "${APPLE_TEAM_ID:-}" ] || [ -z "${APPLE_APP_PASSWORD:-}" ] || ! grep -q "Developer ID" "$REC"; then
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
