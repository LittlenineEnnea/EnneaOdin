#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────
#  EnneaOdin – build script for macOS (arm64) and Linux (x64)
#  Usage:
#    ./build.sh            → build for current platform
#    ./build.sh macos      → macOS arm64 self-contained
#    ./build.sh linux      → Linux x64  self-contained
#    ./build.sh all        → both targets
# ─────────────────────────────────────────────────────────────────

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

OUT="$SCRIPT_DIR/dist"
PROJECT="EnneaOdin/EnneaOdin.csproj"

build_macos() {
  echo "▶  Building macOS arm64 (self-contained)..."
  dotnet publish "$PROJECT" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT/macos-arm64"
  echo "✓  Output: $OUT/macos-arm64/EnneaOdin"
}

build_linux() {
  echo "▶  Building Linux x64 (self-contained)..."
  dotnet publish "$PROJECT" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT/linux-x64"
  chmod +x "$OUT/linux-x64/EnneaOdin"
  echo "✓  Output: $OUT/linux-x64/EnneaOdin"
}

case "${1:-current}" in
  macos)  build_macos ;;
  linux)  build_linux ;;
  all)    build_macos; build_linux ;;
  current)
    OS=$(uname -s)
    if [[ "$OS" == "Darwin" ]]; then
      ARCH=$(uname -m)
      if [[ "$ARCH" == "arm64" ]]; then
        build_macos
      else
        echo "▶  Building macOS x64..."
        dotnet publish "$PROJECT" -c Release -r osx-x64 --self-contained true \
          -p:PublishSingleFile=true -o "$OUT/macos-x64"
      fi
    elif [[ "$OS" == "Linux" ]]; then
      build_linux
    else
      echo "❌  Unsupported OS: $OS"
      exit 1
    fi
    ;;
  *)
    echo "Usage: $0 [macos|linux|all|current]"
    exit 1
    ;;
esac

echo ""
echo "═══════════════════════════════════════"
echo " Build complete!  Files in: $OUT"
echo "═══════════════════════════════════════"
