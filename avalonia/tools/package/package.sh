#!/usr/bin/env bash
# Packs a self-contained publish of Ombra for one RID into a per-platform archive (+ .sha256).
#   tools/package/package.sh <rid> <publish-dir> <out-dir> <version>
# linux-*: tar.gz (app, icon, .desktop entry + installer script)   win-*: zip   osx-*: "Ombra.app" in a zip.
# Signing (tools/package/sign.sh): Authenticode / Developer ID + notarization when the owner's certificates are in the
# CI secrets; otherwise unsigned (macOS: ad-hoc signature only, Apple Silicon refuses unsigned native code). What was
# done is written to <package>.signing.txt next to the archive.
# Every package also holds a copy of the app's launcher under the former name OmpGui (the same apphost, so it starts
# Ombra): an installed OMP GUI updater looks for that name in the package it downloads, and Windows/Linux shortcuts
# made for OMP GUI start it.
set -euo pipefail
RID=${1:?rid}; PUB=${2:?publish dir}; OUT=${3:?out dir}; VERSION=${4:?version}
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
PKG="$HERE/packaging"
ROOT=$(cd "$HERE/.." && pwd)
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
NAME="ombra-$VERSION-$RID"
SIGNING="$OUT/$NAME.signing.txt"
rm -f "$SIGNING"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

docs() {
  cp "$ROOT/LICENSE" "$1/LICENSE.txt"
  cp "$PKG/THIRD-PARTY-NOTICES.md" "$1/THIRD-PARTY-NOTICES.md"
  cp "$HERE/docs/USER_GUIDE.md" "$1/USER_GUIDE.md"
}

sha() { if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi; }

case "$RID" in
  linux-*)
    D="$STAGE/$NAME"; mkdir -p "$D"
    cp -R "$PUB"/. "$D/"
    cp "$HERE/src/Ombra.App/Assets/icon-256.png" "$D/ombra.png"
    cp "$PKG/ombra.desktop" "$PKG/install-desktop-entry.sh" "$D/"
    cp "$D/Ombra" "$D/OmpGui"
    chmod +x "$D/Ombra" "$D/OmpGui" "$D/install-desktop-entry.sh"
    docs "$D"
    echo "linux: no code signature (verify the archive with its .sha256 / SHA256SUMS)" > "$SIGNING"
    ARCHIVE="$OUT/$NAME.tar.gz"
    tar -C "$STAGE" -czf "$ARCHIVE" "$NAME"
    ;;
  win-*)
    D="$STAGE/$NAME"; mkdir -p "$D"
    cp -R "$PUB"/. "$D/"
    cp "$D/Ombra.exe" "$D/OmpGui.exe"
    docs "$D"
    "$HERE/tools/package/sign.sh" windows "$D" "$SIGNING"
    ARCHIVE="$OUT/$NAME.zip"
    if command -v 7z >/dev/null; then (cd "$STAGE" && 7z a -tzip -mx=7 "$ARCHIVE" "$NAME" >/dev/null)
    else (cd "$STAGE" && zip -qr "$ARCHIVE" "$NAME"); fi
    ;;
  osx-*)
    APP="$STAGE/$NAME/Ombra.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$PUB"/. "$APP/Contents/MacOS/"
    # The .NET launcher is linked against an older macOS SDK, and macOS draws such an app the old way (macOS 26: smaller
    # window corners than its own apps, so the rounded sheets inside stop following the frame). It names SDK 26 instead,
    # keeping its minimum macOS; vtool comes with Xcode's command line tools. The signature is made afterwards (sign.sh).
    MINOS=$(otool -l "$APP/Contents/MacOS/Ombra" | awk '/LC_BUILD_VERSION/{f=1} f&&/minos/{print $2; exit}')
    vtool -set-build-version macos "${MINOS:-12.0}" 26.0 -replace -output "$STAGE/Ombra.sdk" "$APP/Contents/MacOS/Ombra" 2>/dev/null
    mv "$STAGE/Ombra.sdk" "$APP/Contents/MacOS/Ombra"
    cp "$APP/Contents/MacOS/Ombra" "$APP/Contents/MacOS/OmpGui"
    chmod +x "$APP/Contents/MacOS/Ombra" "$APP/Contents/MacOS/OmpGui"
    sed "s/VERSION/${VERSION%%-*}/g" "$PKG/Info.plist" > "$APP/Contents/Info.plist"
    ICONSET="$STAGE/ombra.iconset"; mkdir -p "$ICONSET"
    SRC="$HERE/src/Ombra.App/Assets/icon-1024.png"
    for s in 16 32 128 256 512; do
      sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
      d=$((s * 2)); sips -z $d $d "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/ombra.icns"
    docs "$STAGE/$NAME"
    "$HERE/tools/package/sign.sh" macos "$APP" "$SIGNING"
    ARCHIVE="$OUT/$NAME.zip"
    (cd "$STAGE" && ditto -c -k --sequesterRsrc --keepParent "$NAME" "$ARCHIVE")
    "$HERE/tools/package/sign.sh" notarize "$ARCHIVE" "$APP" "$SIGNING"
    ;;
  *) echo "unknown rid $RID"; exit 1 ;;
esac

(cd "$OUT" && sha "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256")
ls -la "$ARCHIVE"; cat "$ARCHIVE.sha256" "$SIGNING"
