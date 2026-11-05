#!/bin/sh
# Fetches the image engine at the commit the app is built and measured with. It needs its submodules, so this is a clone.
set -e
COMMIT=58bb3c1b1cd4749e94e39f537e9adfce295dcaee
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/engine/image-engine"
[ -f "$DEST/CMakeLists.txt" ] && { echo "already there: $DEST"; exit 0; }
git clone --filter=blob:none https://github.com/leejet/stable-diffusion.cpp "$DEST"
git -C "$DEST" checkout -q "$COMMIT"
git -C "$DEST" submodule update --init --recursive --depth 1
echo "fetched: $DEST"
