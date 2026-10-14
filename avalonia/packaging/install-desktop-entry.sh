#!/bin/sh
# Adds Ombra to the application menu for this user (~/.local/share/applications). Nothing else is changed;
# remove ~/.local/share/applications/ombra.desktop to undo.
set -eu
DIR=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$HOME/.local/share/applications"
sed "s|OMBRA_DIR|$DIR|g" "$DIR/ombra.desktop" > "$HOME/.local/share/applications/ombra.desktop"
echo "Added $HOME/.local/share/applications/ombra.desktop"
