#!/usr/bin/env bash
# macOS: builds this checkout as "OMP GUI Test.app" in /Applications, beside the installed "OMP GUI.app".
# The test app has its own bundle id, omp runtime and updates (~/Library/Application Support/OmpGui-Test), so it never
# installs updates over the installed app. Settings are shared: both read and write the installed app's
# omp-gui.local.json (a change in one shows in the other at its next start), as are the downloaded speech models.
# omp's sessions and config (~/.omp) are shared too: the same chats show in both. The first build clones the
# installed app's omp runtime (APFS clone: no extra space).
#   tools/package/test-app.sh [version]
set -euo pipefail
HERE=$(cd "$(dirname "$0")/../.." && pwd)          # avalonia/
VERSION=${1:-0.0.3-test}
RID=osx-$(uname -m | sed 's/x86_64/x64/')
PROD="$HOME/Library/Application Support/OmpGui"
DATA="$HOME/Library/Application Support/OmpGui-Test"
DEST="/Applications/OMP GUI Test.app"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

dotnet publish "$HERE/src/OmpGui.App" -c Release -r "$RID" --self-contained -o "$WORK/pub" \
  -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" -v q -nologo
"$HERE/tools/package/package.sh" "$RID" "$WORK/pub" "$WORK/out" "$VERSION" >/dev/null
ditto -x -k "$WORK/out/omp-gui-$VERSION-$RID.zip" "$WORK/app"
APP="$WORK/app/omp-gui-$VERSION-$RID/OMP GUI.app"

PLIST="$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy \
  -c "Set :CFBundleIdentifier io.github.samnolak.omp-gui.test" \
  -c "Set :CFBundleName OMP GUI Test" \
  -c "Set :CFBundleDisplayName OMP GUI Test" \
  -c "Add :LSEnvironment dict" \
  -c "Add :LSEnvironment:OMPGUI_CONFIG string $PROD/omp-gui.local.json" \
  -c "Add :LSEnvironment:OMPGUI_RUNTIME_DIR string $DATA/runtimes" \
  -c "Add :LSEnvironment:OMPGUI_UPDATES_DIR string $DATA/updates" \
  -c "Add :LSEnvironment:OMPGUI_STT_DIR string $PROD/speech-models" \
  "$PLIST"
codesign --force --deep --sign - "$APP" >/dev/null 2>&1

mkdir -p "$PROD"
if [ ! -d "$DATA" ]; then
  mkdir -p "$DATA"
  [ -d "$PROD/runtimes" ] && cp -Rc "$PROD/runtimes" "$DATA/runtimes"
  echo "runtime copied from $PROD"
fi
# Earlier builds kept their own settings copy: the shared file replaces it (kept aside, not deleted)
if [ -f "$DATA/omp-gui.local.json" ]; then mv "$DATA/omp-gui.local.json" "$DATA/omp-gui.local.json.unshared"; fi

# Quit a running copy without Apple Events (no automation permission needed): a plain signal to its process
pkill -x -f "$DEST/Contents/MacOS/OmpGui" 2>/dev/null || true
rm -rf "$DEST"
ditto "$APP" "$DEST"
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
echo "installed: $DEST ($VERSION, data in $DATA)"
