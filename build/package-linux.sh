#!/usr/bin/env bash
# Linux build. Supported on a best-effort basis, per the PRD.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RID="${1:-linux-x64}"
OUT="$ROOT/dist/$RID"

rm -rf "$OUT"
dotnet publish "$ROOT/src/OpenBurn.App/OpenBurn.App.csproj" \
  -c Release -r "$RID" --self-contained true -p:DebugType=none -o "$OUT"

mkdir -p "$OUT/devices" "$OUT/samples"
cp "$ROOT/devices/"*.json "$OUT/devices/"
cp "$ROOT/samples/"*.svg "$OUT/samples/" 2>/dev/null || true

echo "==> Built $OUT"
