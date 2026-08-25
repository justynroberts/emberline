#!/usr/bin/env bash
# Build the Windows distribution. Runs on any platform — the output is a folder
# that can be zipped and handed over, no installer required.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RID="${1:-win-x64}"
OUT="$ROOT/dist/$RID"

echo "==> Publishing $RID"
rm -rf "$OUT"
dotnet publish "$ROOT/src/Emberline.App/Emberline.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -o "$OUT"

mkdir -p "$OUT/devices" "$OUT/samples"
cp "$ROOT/devices/"*.json "$OUT/devices/"
cp "$ROOT/samples/"*.svg "$OUT/samples/" 2>/dev/null || true
cp "$ROOT/README.md" "$OUT/" 2>/dev/null || true

echo "==> Built $OUT"
du -sh "$OUT"
