#!/usr/bin/env bash
# macOS: builds this checkout as "Ombra Test.app" in /Applications, beside the installed "Ombra.app" (or "OMP GUI.app",
# its former name, until that one has updated).
# The test app has its own bundle id, omp runtime and updates (~/Library/Application Support/Ombra-Test), so it never
# installs updates over the installed app. Settings are shared: both read and write the installed app's settings file
# (a change in one shows in the other at its next start), as are the downloaded speech models and the chats' folders.
# omp's sessions and config (~/.omp) are shared too: the same chats show in both. The first build clones the
# installed app's omp runtime (APFS clone: no extra space). The test app never moves the installed app's data folder:
# OMBRA_CONFIG turns that migration off.
#   tools/package/test-app.sh [version]
set -euo pipefail
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
VERSION=${1:-0.0.3-test}
RID=osx-$(uname -m | sed 's/x86_64/x64/')
SUPPORT="$HOME/Library/Application Support"
# The installed app's folder: Ombra's, else OMP GUI's while that has not updated yet (its data still under the old name)
if [ -d "$SUPPORT/Ombra" ] || [ ! -d "$SUPPORT/OmpGui" ]; then PROD="$SUPPORT/Ombra"; CONFIG="$PROD/ombra.local.json"
else PROD="$SUPPORT/OmpGui"; CONFIG="$PROD/omp-gui.local.json"; fi
DATA="$SUPPORT/Ombra-Test"
DEST="/Applications/Ombra Test.app"
# The test app's own data and bundle from before the rename
[ -d "$SUPPORT/OmpGui-Test" ] && [ ! -e "$DATA" ] && mv "$SUPPORT/OmpGui-Test" "$DATA"
if [ -d "/Applications/OMP GUI Test.app" ]; then
  pkill -x -f "/Applications/OMP GUI Test.app/Contents/MacOS/OmpGui" 2>/dev/null || true
  rm -rf "/Applications/OMP GUI Test.app"
fi
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

dotnet publish "$HERE/src/Ombra.App" -c Release -r "$RID" --self-contained -o "$WORK/pub" \
  -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" -v q -nologo
# The package carries the engine; the installed pack is kept between runs (checked again each time) instead of
# downloading it for every build
OMBRA_ENGINE_BUILD_ROOT="${OMBRA_ENGINE_BUILD_ROOT:-$HOME/Library/Caches/Ombra-Test/engine-build}" \
  "$HERE/tools/package/package.sh" "$RID" "$WORK/pub" "$WORK/out" "$VERSION" >/dev/null
ditto -x -k "$WORK/out/ombra-$VERSION-$RID.zip" "$WORK/app"
APP="$WORK/app/ombra-$VERSION-$RID/Ombra.app"

PLIST="$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy \
  -c "Set :CFBundleIdentifier io.github.samnolak.ombra.test" \
  -c "Set :CFBundleName Ombra Test" \
  -c "Set :CFBundleDisplayName Ombra Test" \
  -c "Add :LSEnvironment dict" \
  -c "Add :LSEnvironment:OMBRA_CONFIG string $CONFIG" \
  -c "Add :LSEnvironment:OMBRA_RUNTIME_DIR string $DATA/runtimes" \
  -c "Add :LSEnvironment:OMBRA_UPDATES_DIR string $DATA/updates" \
  -c "Add :LSEnvironment:OMBRA_STT_DIR string $PROD/speech-models" \
  -c "Add :LSEnvironment:OMBRA_CHATS_DIR string $PROD/chats" \
  "$PLIST"
codesign --force --deep --sign - "$APP" >/dev/null 2>&1

mkdir -p "$PROD"
if [ ! -d "$DATA" ]; then
  mkdir -p "$DATA"
  [ -d "$PROD/runtimes" ] && cp -Rc "$PROD/runtimes" "$DATA/runtimes"
  echo "runtime copied from $PROD"
fi
# Earlier builds kept their own settings copy: the shared file replaces it (kept aside, not deleted)
for f in ombra.local.json omp-gui.local.json; do if [ -f "$DATA/$f" ]; then mv "$DATA/$f" "$DATA/$f.unshared"; fi; done

# Quit a running copy without Apple Events (no automation permission needed): a plain signal to its process
pkill -x -f "$DEST/Contents/MacOS/Ombra" 2>/dev/null || true
rm -rf "$DEST"
ditto "$APP" "$DEST"
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
echo "installed: $DEST ($VERSION, data in $DATA)"
