#!/bin/sh
# Fetches llama.cpp for the second engine at the commit the Mac app pins and applies engine/llama-patches.
set -e
COMMIT=ddf48f85c933c1f438212a02eb21e7b001d2d37d
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/engine/llama.cpp"
[ -f "$DEST/CMakeLists.txt" ] && { echo "already there: $DEST"; exit 0; }
TMP="$(mktemp -d)"
curl -fsSL "https://github.com/ggml-org/llama.cpp/archive/$COMMIT.zip" -o "$TMP/llama.zip"
unzip -q "$TMP/llama.zip" -d "$TMP"
mv "$TMP/llama.cpp-$COMMIT" "$DEST"
rm -rf "$TMP"
cd "$DEST"
for p in "$ROOT"/engine/llama-patches/*.patch; do patch -p1 --binary < "$p"; done
echo "fetched and patched: $DEST"
