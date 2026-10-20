#!/bin/sh
# Adds Ombra to this user's application menu and installs its hicolor icon sizes.
# Remove ~/.local/share/applications/ombra.desktop and hicolor/*/apps/ombra.png to undo.
set -eu
DIR=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$HOME/.local/share/applications"
sed "s|OMBRA_DIR|$DIR|g" "$DIR/ombra.desktop" > "$HOME/.local/share/applications/ombra.desktop"
mkdir -p "$HOME/.local/share/icons"
cp -R "$DIR/icons/hicolor" "$HOME/.local/share/icons/"
echo "Added $HOME/.local/share/applications/ombra.desktop"
