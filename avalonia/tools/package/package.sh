#!/usr/bin/env bash
# Packs a self-contained publish of Ombra for one RID into a per-platform archive (+ .sha256).
#   tools/package/package.sh <rid> <publish-dir> <out-dir> <version>
# linux-*: tar.gz (app, icon, .desktop entry + installer script)   win-*: zip   osx-*: "Ombra.app" in a zip (what the
# updater installs) and the drag-to-install image ombra-<version>-<rid>.dmg for people (tools/package/dmg/make-dmg.py).
# Signing (tools/package/sign.sh): Authenticode / Developer ID + notarization when the owner's certificates are in the
# CI secrets; otherwise unsigned (macOS: ad-hoc signature only, Apple Silicon refuses unsigned native code). What was
# done is written to <package>.signing.txt next to the archive.
# Every package also holds a copy of the app's launcher under the former name OmpGui (the same apphost, so it starts
# Ombra): an installed OMP GUI updater looks for that name in the package it downloads, and Windows/Linux shortcuts
# made for OMP GUI start it.
# The engine is part of the package: the published app installs the pinned pack for the package's platform with its own
# installer (`Ombra --install-engine`: pinned Bun integrity, frozen lockfile, owned source artifact, pruning, the host's
# --self-check) and the pack goes into the package (macOS: Ombra.app/Contents/Resources/engine/<pack>, signed with the
# app; elsewhere engine/<pack> beside the executable), so the first start needs no download. A package whose platform
# this machine cannot run (linux-arm64/win-arm64 cross-builds) cannot pass that self-check: it is built without the
# engine, which then downloads on first start as before. What was done is written to <package>.engine.txt.
# OMBRA_ENGINE_BUILD_ROOT: keep the installed pack there between local runs (it is self-checked again when reused).
set -euo pipefail
RID=${1:?rid}; PUB=${2:?publish dir}; OUT=${3:?out dir}; VERSION=${4:?version}
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
PKG="$HERE/packaging"
ROOT=$(cd "$HERE/.." && pwd)
python3 "$ROOT/runtime/generate-engine-artifact.py" --check
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
NAME="ombra-$VERSION-$RID"
SIGNING="$OUT/$NAME.signing.txt"
ENGINE_REC="$OUT/$NAME.engine.txt"
rm -f "$SIGNING" "$ENGINE_REC"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

docs() {
  cp "$ROOT/LICENSE" "$1/LICENSE.txt"
  cp "$PKG/THIRD-PARTY-NOTICES.md" "$1/THIRD-PARTY-NOTICES.md"
  cp "$HERE/docs/USER_GUIDE.md" "$1/USER_GUIDE.md"
}

sha() { if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi; }
native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

# The pinned engine pack (currently omp 18.12.2 / Bun 1.4.2), installed by the published app itself,
# into $1 (the folder RuntimePack.BundledRoot names). Read its identity from the installer, never an older cache.
engine() {
  local key exe build pack
  case "$RID" in
    osx-arm64) key=darwin-aarch64 ;; osx-x64) key=darwin-x64 ;; linux-x64) key=linux-x64 ;;
    linux-arm64) key=linux-aarch64 ;; win-x64) key=windows-x64 ;; win-arm64) key=windows-aarch64 ;;
    *) echo "no engine platform for $RID"; exit 1 ;;
  esac
  exe="$PUB/Ombra"; [ -f "$PUB/Ombra.exe" ] && exe="$PUB/Ombra.exe"
  # osx-x64 runs under Rosetta 2 on an arm64 Mac (its Bun and native modules then are x64, as on an Intel Mac)
  if ! "$exe" --version > /dev/null 2>&1; then
    echo "engine: NOT BUNDLED — this machine cannot run the $RID app to install and self-check the pack; it is downloaded on first start" | tee "$ENGINE_REC"
    return 0
  fi
  build=${OMBRA_ENGINE_BUILD_ROOT:-$STAGE/engine-build}
  mkdir -p "$build"
  "$exe" --install-engine "$(native "$build")" --engine-platform "$key" > "$STAGE/engine.json"
  pack=$(sed -n 's/.*"pack":"\([^"]*\)".*/\1/p' "$STAGE/engine.json")
  [ -n "$pack" ] && [ -f "$build/$pack/installed.json" ] || { echo "the engine install named no pack:"; cat "$STAGE/engine.json"; exit 1; }
  # The Ombra harness (harness/profile) every session loads, installed from the owned source artifact
  [ -f "$build/$pack/omp/harness/agent/AGENTS.md" ] || { echo "the engine pack $pack has no Ombra harness (omp/harness)"; exit 1; }
  mkdir -p "$1"
  cp -a "$build/$pack" "$1/$pack"
  echo "engine: bundled $pack ($key, $(du -sh "$1/$pack" | cut -f1) installed; integrity-checked install, host --self-check passed)" | tee "$ENGINE_REC"
}

case "$RID" in
  linux-*)
    D="$STAGE/$NAME"; mkdir -p "$D"
    cp -R "$PUB"/. "$D/"
    cp "$HERE/src/Ombra.App/Assets/icon-256.png" "$D/ombra.png"
    for s in 16 24 32 48 64 128 256 512 1024; do
      mkdir -p "$D/icons/hicolor/${s}x${s}/apps"
      cp "$HERE/src/Ombra.App/Assets/icon-$s.png" "$D/icons/hicolor/${s}x${s}/apps/ombra.png"
    done
    cp "$PKG/ombra.desktop" "$PKG/install-desktop-entry.sh" "$D/"
    cp "$D/Ombra" "$D/OmpGui"
    chmod +x "$D/Ombra" "$D/OmpGui" "$D/install-desktop-entry.sh"
    engine "$D/engine"
    docs "$D"
    python3 "$HERE/tools/package/native-platform.py" "$RID" "$D" --prune
    echo "linux: no code signature (verify the archive with its .sha256 / SHA256SUMS)" > "$SIGNING"
    ARCHIVE="$OUT/$NAME.tar.gz"
    tar -C "$STAGE" -czf "$ARCHIVE" "$NAME"
    ;;
  win-*)
    D="$STAGE/$NAME"; mkdir -p "$D"
    cp -R "$PUB"/. "$D/"
    cp "$D/Ombra.exe" "$D/OmpGui.exe"
    engine "$D/engine"
    docs "$D"
    python3 "$HERE/tools/package/native-platform.py" "$RID" "$D" --prune
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
    engine "$APP/Contents/Resources/engine"
    sed "s/VERSION/${VERSION%%-*}/g" "$PKG/Info.plist" > "$APP/Contents/Info.plist"
    # The app icon (docs/PLATFORM_NOTES.md): the Icon Composer catalog (CFBundleIconName) and an Apple-shaped icns
    # (CFBundleIconFile) for whatever reads only that
    cp "$PKG/macos/Assets.car" "$PKG/macos/ombra.icns" "$APP/Contents/Resources/"
    docs "$STAGE/$NAME"
    python3 "$HERE/tools/package/native-platform.py" "$RID" "$APP" --prune
    "$HERE/tools/package/sign.sh" macos "$APP" "$SIGNING"
    ARCHIVE="$OUT/$NAME.zip"
    (cd "$STAGE" && ditto -c -k --sequesterRsrc --keepParent "$NAME" "$ARCHIVE")
    "$HERE/tools/package/sign.sh" notarize "$ARCHIVE" "$APP" "$SIGNING"
    # The installer image holds the app as signed (and, with the owner's certificate, stapled) above
    DMG="$OUT/$NAME.dmg"
    python3 "$HERE/tools/package/dmg/make-dmg.py" "$APP" "$DMG" --volume Ombra
    (cd "$OUT" && sha "$(basename "$DMG")" > "$(basename "$DMG").sha256")
    ;;
  *) echo "unknown rid $RID"; exit 1 ;;
esac

# Inspect the actual shipped bytes, after signing/notarization may have rewritten the archive.
# This also gates cross-built RIDs, where the app/engine self-test cannot execute on the runner.
python3 "$HERE/tools/package/native-platform.py" "$RID" "$ARCHIVE"

(cd "$OUT" && sha "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256")
ls -la "$ARCHIVE" ${DMG:+"$DMG"}; cat "$ARCHIVE.sha256" ${DMG:+"$DMG.sha256"} "$SIGNING" "$ENGINE_REC"
