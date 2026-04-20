#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────
#  package-macos.sh  –  wrap the binary into a proper .app bundle
#  Run AFTER:  ./build.sh macos
# ─────────────────────────────────────────────────────────────────
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BIN="$SCRIPT_DIR/dist/macos-arm64/EnneaOdin"
APP="$SCRIPT_DIR/dist/EnneaOdin.app"

if [[ ! -f "$BIN" ]]; then
  echo "❌  Binary not found. Run ./build.sh macos first."
  exit 1
fi

echo "▶  Creating EnneaOdin.app bundle..."
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/EnneaOdin"
chmod +x "$APP/Contents/MacOS/EnneaOdin"

# Info.plist
cat > "$APP/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>EnneaOdin</string>
  <key>CFBundleDisplayName</key>
  <string>EnneaOdin – Samsung Flash Tool</string>
  <key>CFBundleIdentifier</key>
  <string>com.ennea.odin.flash</string>
  <key>CFBundleVersion</key>
  <string>1.0.0</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0.0</string>
  <key>CFBundleExecutable</key>
  <string>EnneaOdin</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSRequiresAquaSystemAppearance</key>
  <false/>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>NSUSBDeviceAddedMatchingArrayKey</key>
  <array>
    <dict>
      <key>idVendor</key>  <integer>1256</integer>
      <key>idProduct</key> <integer>26113</integer>
    </dict>
  </array>
</dict>
</plist>
PLIST

echo "✓  Bundle created: $APP"
echo ""
echo "To create a distributable DMG:"
echo "  hdiutil create -volname EnneaOdin -srcfolder $APP -ov -format UDZO $SCRIPT_DIR/dist/EnneaOdin.dmg"
