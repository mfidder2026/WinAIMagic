#!/bin/sh
# Fetches the llama.cpp source the tiered engine builds against, at the commit pinned in engine/tiered/setup.py.
set -e
COMMIT=3cf03257f219afbe7334045ff7c6a06ac68c627d
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/engine/tiered/third_party/llama.cpp"
[ -f "$DEST/ggml/CMakeLists.txt" ] && { echo "already there: $DEST"; exit 0; }
TMP="$(mktemp -d)"
curl -fsSL "https://github.com/ggml-org/llama.cpp/archive/$COMMIT.zip" -o "$TMP/llama.zip"
unzip -q "$TMP/llama.zip" -d "$TMP"
mv "$TMP/llama.cpp-$COMMIT" "$DEST"
rm -rf "$TMP"
echo "fetched: $DEST"
