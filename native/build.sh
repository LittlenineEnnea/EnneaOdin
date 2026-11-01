#!/usr/bin/env bash
#
# EnneaOdin - build the native bridge.
#
# Copyright (c) 2025 EnneaOdin contributors. GPL-3.0; see ../LICENCE.
#
# Usage:
#   ./build.sh            Release, into ./build
#   ./build.sh Debug      Debug, into ./build-debug
#
# This runs cmake and nothing else. There is no flashing binary to build or
# fetch: the protocol is Heimdall's C++ compiled into libenneaodin, which the
# application loads in process.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CONFIG="${1:-Release}"

case "$CONFIG" in
  Release) BUILD_DIR="$SCRIPT_DIR/build" ;;
  Debug)   BUILD_DIR="$SCRIPT_DIR/build-debug" ;;
  *)
    echo "build.sh: unknown configuration: $CONFIG" >&2
    echo "Usage: $0 [Release|Debug]" >&2
    exit 1
    ;;
esac

if ! command -v cmake >/dev/null 2>&1; then
  echo "build.sh: cmake not found on PATH. CMake 3.20 or newer is required." >&2
  exit 1
fi

if ! pkg-config --exists libusb-1.0 2>/dev/null; then
  echo "build.sh: libusb-1.0 was not found by pkg-config." >&2
  echo "build.sh: macOS: brew install libusb" >&2
  echo "build.sh: Debian/Ubuntu: sudo apt install libusb-1.0-0-dev" >&2
  echo "build.sh: Fedora: sudo dnf install libusb1-devel" >&2
  exit 1
fi

cmake -S "$SCRIPT_DIR" -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE="$CONFIG"
cmake --build "$BUILD_DIR"

echo ""
echo "Built $CONFIG into: $BUILD_DIR"
ls -l "$BUILD_DIR"/libenneaodin.* 2>/dev/null || true
