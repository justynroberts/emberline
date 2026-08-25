#!/usr/bin/env bash
# Build the macOS .icns and the Windows .ico from the master PNG.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ASSETS="$HERE/../src/Emberline.App/Assets"
MASTER="$ASSETS/emberline.png"
ICONSET="$HERE/emberline.iconset"

[ -f "$MASTER" ] || { echo "missing $MASTER" >&2; exit 1; }

rm -rf "$ICONSET"
mkdir -p "$ICONSET"

for size in 16 32 64 128 256 512; do
  sips -z $size $size "$MASTER" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$MASTER" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done

iconutil -c icns "$ICONSET" -o "$ASSETS/emberline.icns"
rm -rf "$ICONSET"
echo "wrote $ASSETS/emberline.icns"
