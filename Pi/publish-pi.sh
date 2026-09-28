#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
OUT="${1:-publish/pi-arm64}"
rm -rf "$OUT"
mkdir -p "$OUT"

dotnet publish FlexCompanion.Pi.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -o "$OUT"

chmod +x "$OUT/FlexCompanion" || true
TAR="FlexCompanion-v0.6.2-linux-arm64.tar.gz"
tar -C "$OUT" -czf "$TAR" .
echo "Built: $OUT/FlexCompanion"
echo "Package: $TAR"
