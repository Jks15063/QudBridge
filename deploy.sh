#!/usr/bin/env bash
# Copy src/ into the Windows Mods folder as Mods/QudBridge/. The game reads only from there
# and can't follow WSL symlinks, so this is a real copy. Restart the game to recompile.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/src"
MODS="/mnt/c/Users/Jake/AppData/LocalLow/Freehold Games/CavesOfQud/Mods"
DEST="$MODS/QudBridge"

[[ -d "$MODS" ]] || { echo "Mods folder not found: $MODS" >&2; exit 1; }

# Wipe first so deleted/renamed .cs files don't linger and get compiled.
rm -rf "$DEST"
mkdir -p "$DEST"
cp -r "$SRC"/. "$DEST"/

echo "Deployed to $DEST:"
ls -1 "$DEST"
