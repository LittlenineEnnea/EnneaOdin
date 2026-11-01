#!/usr/bin/env bash
#
# EnneaOdin build. Copyright (c) 2025 EnneaOdin contributors. GPL-3.0.
#
#   ./build.sh              this machine, and on macOS the .app too
#   ./build.sh osx-arm64 | osx-x64 | linux-x64 | linux-arm64
#   ./build.sh all
#   ./build.sh --no-app     skip the .app
#
# Output: dist/<rid>/ is the publish directory; EnneaOdin.app is beside this
# script. The C++ engine is built first - a publish without it is not a
# working program - and is NOT cross-compiled.
#
# PublishSingleFile is not used: an Avalonia publish leaves libSkiaSharp and
# friends beside the binary and the binary alone cannot find them.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

OUT="$SCRIPT_DIR/dist"
PROJECT="$SCRIPT_DIR/EnneaOdin/EnneaOdin.csproj"
ICON_SRC="$SCRIPT_DIR/EnneaOdin/Assets/icon.png"
CONFIG="${CONFIG:-Release}"
NATIVE_LIB_NAME="libenneaodin.dylib"

SUPPORTED_RIDS="osx-arm64 osx-x64 linux-x64 linux-arm64"
MAKE_APP=1
HOST_RID=""

usage() {
  echo "Usage: $0 [osx-arm64|osx-x64|linux-x64|linux-arm64|all] [--no-app]"
}

current_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "$arch" in
    arm64|aarch64) arch="arm64" ;;
    x86_64|amd64)  arch="x64" ;;
    *) echo "build.sh: unsupported CPU: $arch" >&2; return 1 ;;
  esac

  case "$os" in
    Darwin) echo "osx-$arch" ;;
    Linux)  echo "linux-$arch" ;;
    *) echo "build.sh: EnneaOdin targets macOS and Linux only." >&2; return 1 ;;
  esac
}

native_library_name() {
  case "$1" in
    osx-*)   echo "libenneaodin.dylib" ;;
    linux-*) echo "libenneaodin.so" ;;
    *) return 1 ;;
  esac
}

native_library_built_path() {
  local dir="$SCRIPT_DIR/native/build"
  [[ "$CONFIG" == "Debug" ]] && dir="$SCRIPT_DIR/native/build-debug"
  echo "$dir/$(native_library_name "$HOST_RID")"
}

# ---------------------------------------------------------------- the engine

build_native() {
  echo "Building the native protocol engine ..."

  if ! command -v cmake >/dev/null 2>&1; then
    echo "build.sh: FAILED - cmake not found. macOS: brew install cmake" >&2
    echo "build.sh: Debian: apt install cmake   Fedora: dnf install cmake" >&2
    return 1
  fi

  if [[ -z "${CXX:-}" ]] \
     && ! command -v c++ >/dev/null 2>&1 \
     && ! command -v clang++ >/dev/null 2>&1 \
     && ! command -v g++ >/dev/null 2>&1; then
    echo "build.sh: FAILED - no C++ compiler. macOS: xcode-select --install" >&2
    echo "build.sh: Debian: apt install build-essential   Fedora: dnf install gcc-c++" >&2
    return 1
  fi

  # native/build.sh checks for libusb itself and prints the install command.
  if ! "$SCRIPT_DIR/native/build.sh" "$CONFIG"; then
    echo "build.sh: FAILED - the engine did not build; nothing was published." >&2
    return 1
  fi

  local built
  built="$(native_library_built_path)"

  if [[ ! -f "$built" ]]; then
    echo "build.sh: FAILED - the native build claims success but $built is missing." >&2
    return 1
  fi

  echo "OK   native: $built"
}

# OdinNativeLoader looks beside the managed assemblies first.
place_native_library() {
  local rid="$1" dest="$2" name built
  name="$(native_library_name "$rid")"
  built="$(native_library_built_path)"

  if [[ "$rid" != "$HOST_RID" ]]; then
    echo "WARN $rid: engine NOT copied - C++ is not cross-compiled here. The"
    echo "     publish is the managed half only and will refuse every device"
    echo "     operation until $name is placed beside it."
    return 0
  fi

  cp "$built" "$dest/$name"
  chmod +x "$dest/$name"
  echo "OK   engine placed: $dest/$name"
}

# --------------------------------------------------------------- the publish

publish_rid() {
  local rid="$1"
  local dest="$OUT/$rid"

  echo "Publishing $rid ($CONFIG, self-contained) ..."
  rm -rf "$dest"

  dotnet publish "$PROJECT" \
    -c "$CONFIG" -r "$rid" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none -o "$dest"

  # Check before chmod: under set -e a chmod on a missing file aborts with
  # chmod's message instead of the readable one below.
  if [[ ! -f "$dest/EnneaOdin" ]]; then
    echo "build.sh: FAILED - $dest/EnneaOdin was not produced" >&2
    return 1
  fi

  chmod +x "$dest/EnneaOdin"
  place_native_library "$rid" "$dest"

  echo "OK   $rid  ->  $dest  ($(find "$dest" -type f | wc -l | tr -d ' ') files, $(du -sh "$dest" | cut -f1))"
  echo "     run the whole directory, not the binary alone."
}

# ------------------------------------------------------------- the .app

project_version() {
  local v
  v="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$PROJECT" | head -1)"
  echo "${v:-1.0.0}"
}

# The dylib the engine actually linked against, so the bundled copy is that
# one rather than whatever a prefix guess finds.
find_libusb() {
  local candidate libdir

  if command -v pkg-config >/dev/null 2>&1; then
    libdir="$(pkg-config --variable=libdir libusb-1.0 2>/dev/null || true)"
    if [[ -n "$libdir" && -f "$libdir/libusb-1.0.0.dylib" ]]; then
      echo "$libdir/libusb-1.0.0.dylib"
      return 0
    fi
  fi

  for candidate in /opt/homebrew/lib/libusb-1.0.0.dylib \
                   /usr/local/lib/libusb-1.0.0.dylib \
                   /opt/local/lib/libusb-1.0.0.dylib; do
    [[ -f "$candidate" ]] && { echo "$candidate"; return 0; }
  done

  return 1
}

bundle_libusb() {
  local app="$1" src licence candidate

  src="$(find_libusb || true)"

  if [[ -z "$src" ]]; then
    echo "build.sh: FAILED - no libusb-1.0.0.dylib to bundle. brew install libusb" >&2
    return 1
  fi

  mkdir -p "$app/Contents/Frameworks"

  # -L: Homebrew's is a symlink into ../Cellar, a path nobody else has.
  cp -L "$src" "$app/Contents/Frameworks/libusb-1.0.0.dylib"
  chmod +w "$app/Contents/Frameworks/libusb-1.0.0.dylib"

  if command -v install_name_tool >/dev/null 2>&1; then
    install_name_tool -id "@rpath/libusb-1.0.0.dylib" \
      "$app/Contents/Frameworks/libusb-1.0.0.dylib" 2>/dev/null || true
  fi

  echo "OK   libusb bundled: $src"

  # LGPL-2.1-or-later: bundled unmodified as a replaceable dynamic library,
  # with its licence.
  licence=""
  for candidate in "$(dirname "$src")/../COPYING" \
                   "$(dirname "$src")/../share/doc/libusb/COPYING" \
                   "$(dirname "$src")/../share/licenses/libusb/COPYING"; do
    [[ -f "$candidate" ]] && { licence="$candidate"; break; }
  done

  if [[ -n "$licence" ]]; then
    cp "$licence" "$app/Contents/Resources/LICENCE-libusb.txt"
  else
    cat > "$app/Contents/Resources/LICENCE-libusb.txt" <<'LGPL'
libusb is bundled in Contents/Frameworks/libusb-1.0.0.dylib under the GNU
Lesser General Public License, version 2.1 or later. It is unmodified, is a
separate dynamic library, and may be replaced without rebuilding anything
else. Full text and source: https://libusb.info

The licence file that normally ships with the library was not present on the
build machine; this notice stands in its place and does not replace it.
LGPL
    echo "WARN libusb licence file not found; a pointer notice was written."
  fi
}

make_icns() {
  local app="$1" iconset size double

  if [[ ! -f "$ICON_SRC" ]] || ! command -v iconutil >/dev/null 2>&1 \
     || ! command -v sips >/dev/null 2>&1; then
    echo "WARN no icon: $ICON_SRC or sips/iconutil missing."
    return 1
  fi

  iconset="$(mktemp -d)/EnneaOdin.iconset"
  mkdir -p "$iconset"

  for size in 16 32 64 128 256 512; do
    double=$((size * 2))
    sips -z "$size" "$size" "$ICON_SRC" \
      --out "$iconset/icon_${size}x${size}.png" >/dev/null 2>&1
    sips -z "$double" "$double" "$ICON_SRC" \
      --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null 2>&1
  done

  if iconutil -c icns "$iconset" -o "$app/Contents/Resources/EnneaOdin.icns" >/dev/null 2>&1; then
    rm -rf "$(dirname "$iconset")"
    return 0
  fi

  rm -rf "$(dirname "$iconset")"
  echo "WARN iconutil failed; the bundle gets the generic icon."
  return 1
}

package_app() {
  local rid="$1"
  local pubdir="$OUT/$rid"
  local app="$SCRIPT_DIR/EnneaOdin.app"
  local version icon_entry=""

  version="$(project_version)"

  echo ""
  echo "Packaging EnneaOdin.app from $rid ..."

  rm -rf "$app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

  # The whole publish directory, so the native dylibs travel with the binary.
  cp -R "$pubdir"/. "$app/Contents/MacOS/"
  chmod +x "$app/Contents/MacOS/EnneaOdin"

  bundle_libusb "$app"

  if make_icns "$app"; then
    icon_entry="  <key>CFBundleIconFile</key>
  <string>EnneaOdin</string>
"
    echo "OK   icon: Contents/Resources/EnneaOdin.icns"
  fi

  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>EnneaOdin</string>
  <key>CFBundleDisplayName</key>
  <string>EnneaOdin</string>
  <key>CFBundleIdentifier</key>
  <string>com.ennea.odin.flash</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleExecutable</key>
  <string>EnneaOdin</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
$icon_entry  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

  sign_app "$app"

  echo "OK   EnneaOdin.app  ($(find "$app" -type f | wc -l | tr -d ' ') files, $(du -sh "$app" | cut -f1))"
  echo "     $app"
  echo "     Ad-hoc signed, not notarised. A downloaded copy is quarantined:"
  echo "       xattr -dr com.apple.quarantine \"$app\""
  echo "     libusb is inside, so no package manager is needed to run it."
}

sign_app() {
  local app="$1" macho sealed

  if ! command -v codesign >/dev/null 2>&1; then
    echo "build.sh: FAILED - codesign not found. xcode-select --install" >&2
    return 1
  fi

  # Nested Mach-Os first and by name, so a failure names the file. Then the
  # bundle with --deep, which IS needed here: a self-contained publish leaves
  # ~200 managed assemblies in Contents/MacOS, codesign counts each as nested
  # code, and sealing without it fails on the first .dll.
  while IFS= read -r macho; do
    codesign --force --sign - --timestamp=none "$macho" >/dev/null 2>&1 || {
      echo "build.sh: FAILED - could not sign $macho" >&2
      return 1
    }
  done < <(find "$app/Contents" -type f \( -name '*.dylib' -o -name '*.so' \))

  if ! codesign --force --deep --sign - --timestamp=none "$app" >/dev/null 2>&1; then
    echo "build.sh: FAILED - could not sign the bundle." >&2
    codesign --force --deep --sign - --timestamp=none "$app" >&2 || true
    return 1
  fi

  if ! codesign --verify --strict "$app" >/dev/null 2>&1; then
    echo "build.sh: FAILED - the bundle signature does not verify." >&2
    codesign --verify --strict --verbose=2 "$app" >&2 || true
    return 1
  fi

  if ! codesign --verify --strict "$app/Contents/Frameworks/libusb-1.0.0.dylib" >/dev/null 2>&1; then
    echo "build.sh: FAILED - the bundled libusb is not validly signed." >&2
    return 1
  fi

  if ! codesign --verify --strict "$app/Contents/MacOS/$NATIVE_LIB_NAME" >/dev/null 2>&1; then
    echo "build.sh: FAILED - the engine in the bundle is not validly signed." >&2
    return 1
  fi

  # The engine must be able to FIND the bundled libusb, or it silently falls
  # back to a system copy that may not exist.
  if command -v otool >/dev/null 2>&1; then
    if ! otool -l "$app/Contents/MacOS/$NATIVE_LIB_NAME" | grep -q "@loader_path/../Frameworks"; then
      echo "build.sh: FAILED - the engine has no @loader_path/../Frameworks rpath." >&2
      return 1
    fi
  fi

  # No "grep -q" on this one: with pipefail, grep exiting at the first match
  # SIGPIPEs codesign, the pipeline reports 141, and the check silently takes
  # the false branch.
  sealed="$(codesign --display --verbose=4 "$app" 2>&1 | grep 'Sealed Resources' || true)"

  if [[ -z "$sealed" ]]; then
    echo "build.sh: FAILED - no sealed resources; the signature misses Contents/MacOS." >&2
    return 1
  fi

  echo "OK   signed and verified; $sealed"
}

# ------------------------------------------------------------------ dispatch

validate_rid() {
  local rid
  for rid in $SUPPORTED_RIDS; do
    [[ "$1" == "$rid" ]] && return 0
  done
  echo "build.sh: unsupported runtime identifier: $1" >&2
  usage >&2
  return 1
}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "build.sh: dotnet not found; an 8.x SDK is required. See README.md." >&2
  exit 1
fi

TARGET=""

for arg in "$@"; do
  case "$arg" in
    -h|--help|help) usage; exit 0 ;;
    --no-app)       MAKE_APP=0 ;;
    *)              TARGET="$arg" ;;
  esac
done

HOST_RID="$(current_rid)"

build_native
echo ""

build_one() {
  local rid="$1"
  publish_rid "$rid"

  # Only for the rid this machine can build the engine for: a cross-published
  # macOS directory has no engine, and bundling one would be a bundle that
  # opens and refuses everything.
  if [[ "$MAKE_APP" == "1" && "$rid" == "$HOST_RID" && "$rid" == osx-* ]]; then
    package_app "$rid"
  fi
}

case "$TARGET" in
  ""|current) build_one "$HOST_RID" ;;
  all)
    for rid in $SUPPORTED_RIDS; do
      build_one "$rid"
      echo ""
    done
    ;;
  *)
    validate_rid "$TARGET"
    build_one "$TARGET"
    ;;
esac

echo ""
echo "Done. Publish directories under $OUT"
[[ "$HOST_RID" == linux-* ]] && \
  echo "Linux: sudo ./install-linux.sh $HOST_RID installs system-wide with udev rules."
exit 0
