#!/usr/bin/env bash
# macOS: builds this checkout as "OMP GUI Test.app" in /Applications, beside the installed "OMP GUI.app".
# The test app has its own bundle id and its own data folder (~/Library/Application Support/OmpGui-Test), so it never
# touches the installed app's settings, runtimes or updates. The first build copies the installed app's settings and
# clones its omp runtime (APFS clone: no extra space); later builds keep the test app's own settings.
# omp's own sessions and config (~/.omp) are shared with the installed app: the same chats show in both.
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
  -c "Add :LSEnvironment:OMPGUI_CONFIG string $DATA/omp-gui.local.json" \
  -c "Add :LSEnvironment:OMPGUI_RUNTIME_DIR string $DATA/runtimes" \
  -c "Add :LSEnvironment:OMPGUI_UPDATES_DIR string $DATA/updates" \
  -c "Add :LSEnvironment:OMPGUI_STT_DIR string $DATA/speech-models" \
  "$PLIST"
codesign --force --deep --sign - "$APP" >/dev/null 2>&1

if [ ! -d "$DATA" ]; then
  mkdir -p "$DATA"
  [ -f "$PROD/omp-gui.local.json" ] && cp "$PROD/omp-gui.local.json" "$DATA/"
  [ -d "$PROD/runtimes" ] && cp -Rc "$PROD/runtimes" "$DATA/runtimes"
  [ -d "$PROD/speech-models" ] && cp -Rc "$PROD/speech-models" "$DATA/speech-models"
  echo "settings copied from $PROD"
fi

osascript -e 'tell application "OMP GUI Test" to quit' >/dev/null 2>&1 || true
rm -rf "$DEST"
ditto "$APP" "$DEST"
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
echo "installed: $DEST ($VERSION, data in $DATA)"
