#!/bin/sh
# Adds OMP GUI to the application menu for this user (~/.local/share/applications). Nothing else is changed;
# remove ~/.local/share/applications/omp-gui.desktop to undo.
set -eu
DIR=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$HOME/.local/share/applications"
sed "s|OMPGUI_DIR|$DIR|g" "$DIR/omp-gui.desktop" > "$HOME/.local/share/applications/omp-gui.desktop"
echo "Added $HOME/.local/share/applications/omp-gui.desktop"
