#!/bin/sh
# Fetches the speech engine at the commit the app is built and measured with (release v1.9.5).
set -e
COMMIT=d1be6fde11ac6e0407606b4e42fe72d34add8037
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/engine/speech-engine"
[ -f "$DEST/CMakeLists.txt" ] && { echo "already there: $DEST"; exit 0; }
git clone --filter=blob:none https://github.com/ggml-org/whisper.cpp "$DEST"
git -C "$DEST" checkout -q "$COMMIT"
echo "fetched: $DEST"
