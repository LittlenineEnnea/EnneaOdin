#!/usr/bin/env bash
#
# EnneaOdin - install a Linux publish directory system-wide.
#
# Copyright (c) 2025 EnneaOdin contributors. GPL-3.0; see LICENCE.
#
# Usage:
#   ./build.sh linux-x64 && sudo ./install-linux.sh linux-x64
#   sudo ./install-linux.sh                 (runtime id of this machine)
#   sudo ./install-linux.sh --uninstall
#
# Installs:
#   /opt/enneaodin/                              the whole publish directory
#   /usr/local/bin/enneaodin                     a wrapper that launches it
#   /usr/share/applications/EnneaOdin.desktop    the menu entry
#   /usr/share/icons/hicolor/256x256/apps/enneaodin.png
#   /etc/udev/rules.d/51-samsung-download.rules  raw USB access, uaccess tagged
#
# The whole publish directory is installed, not just the binary: an Avalonia
# publish leaves libSkiaSharp.so, libHarfBuzzSharp.so and libAvaloniaNative.so
# beside the managed binary.
#
# libusb-1.0 is NOT installed here. It is a distribution package; this script
# checks for it and prints the right command for the distribution it finds.
#
# libenneaodin.so, the native protocol engine, IS installed: it is built from
# native/ by build.sh and published beside the managed assemblies, so it
# travels with the publish directory into /opt/enneaodin. The engine records
# SONAME libusb-1.0.so.0 and has $ORIGIN on its RPATH, so the distribution's
# own libusb is used and no path from the build machine is baked in.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

PREFIX_DIR="/opt/enneaodin"
WRAPPER="/usr/local/bin/enneaodin"
DESKTOP_DIR="/usr/share/applications"
ICON_DIR="/usr/share/icons/hicolor/256x256/apps"
UDEV_DIR="/etc/udev/rules.d"
RULES_NAME="51-samsung-download.rules"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "install-linux.sh: this script only runs on Linux." >&2
  exit 1
fi

if [[ "$(id -u)" != "0" ]]; then
  echo "install-linux.sh: root is required to write to $PREFIX_DIR, $WRAPPER and $UDEV_DIR." >&2
  echo "install-linux.sh: re-run with sudo." >&2
  exit 1
fi

uninstall() {
  echo "Removing EnneaOdin ..."
  rm -rf "$PREFIX_DIR"
  rm -f "$WRAPPER"
  rm -f "$DESKTOP_DIR/EnneaOdin.desktop"
  rm -f "$ICON_DIR/enneaodin.png"
  rm -f "$UDEV_DIR/$RULES_NAME"
  if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger || true
  fi
  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
  fi
  echo "OK   removed."
  exit 0
}

default_rid() {
  local arch
  arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64)  echo "linux-x64" ;;
    aarch64|arm64) echo "linux-arm64" ;;
    *)
      echo "install-linux.sh: unsupported CPU architecture: $arch" >&2
      return 1
      ;;
  esac
}

libusb_install_command() {
  local id="" id_like=""
  if [[ -r /etc/os-release ]]; then
    id="$(sed -n 's/^ID=//p' /etc/os-release | tr -d '"' | head -1)"
    id_like="$(sed -n 's/^ID_LIKE=//p' /etc/os-release | tr -d '"' | head -1)"
  fi
  case "$id $id_like" in
    *debian*|*ubuntu*) echo "sudo apt install libusb-1.0-0" ;;
    *fedora*|*rhel*|*centos*) echo "sudo dnf install libusb1" ;;
    *arch*) echo "sudo pacman -S libusb" ;;
    *suse*) echo "sudo zypper install libusb-1_0-0" ;;
    *) echo "install the libusb-1.0 runtime package for your distribution" ;;
  esac
}

if [[ "${1:-}" == "--uninstall" || "${1:-}" == "-u" ]]; then
  uninstall
fi

RID="${1:-$(default_rid)}"

case "$RID" in
  linux-x64|linux-arm64) ;;
  *)
    echo "install-linux.sh: expected linux-x64 or linux-arm64, got: $RID" >&2
    echo "Usage: $0 [linux-x64|linux-arm64|--uninstall]" >&2
    exit 1
    ;;
esac

PUBDIR="$SCRIPT_DIR/dist/$RID"

if [[ ! -d "$PUBDIR" ]]; then
  echo "install-linux.sh: publish directory not found: $PUBDIR" >&2
  echo "install-linux.sh: run ./build.sh $RID first." >&2
  exit 1
fi

if [[ ! -f "$PUBDIR/EnneaOdin" ]]; then
  echo "install-linux.sh: executable not found: $PUBDIR/EnneaOdin" >&2
  exit 1
fi

NATIVE_LIB="libenneaodin.so"

if [[ ! -f "$PUBDIR/$NATIVE_LIB" ]]; then
  echo "install-linux.sh: $NATIVE_LIB is not in $PUBDIR." >&2
  echo "install-linux.sh: that is the protocol engine. Without it the application" >&2
  echo "install-linux.sh: starts and shows the device list, but refuses every" >&2
  echo "install-linux.sh: device operation. Run ./build.sh $RID, which builds" >&2
  echo "install-linux.sh: native/ and copies the library into the publish directory." >&2
  exit 1
fi

echo "Installing $RID ..."

rm -rf "$PREFIX_DIR"
mkdir -p "$PREFIX_DIR"
cp -R "$PUBDIR"/. "$PREFIX_DIR/"
chmod +x "$PREFIX_DIR/EnneaOdin"
echo "OK   $PREFIX_DIR"

# Verified rather than assumed: the whole directory was copied, but the engine
# is the one file whose absence turns the application into a viewer.
if [[ -f "$PREFIX_DIR/$NATIVE_LIB" ]]; then
  chmod 0755 "$PREFIX_DIR/$NATIVE_LIB"
  echo "OK   $PREFIX_DIR/$NATIVE_LIB (the protocol engine)"
else
  echo "install-linux.sh: FAILED - $NATIVE_LIB did not survive the copy." >&2
  exit 1
fi

mkdir -p "$(dirname "$WRAPPER")"
cat > "$WRAPPER" <<'WRAP'
#!/usr/bin/env bash
# EnneaOdin launcher. The application must run from its install directory so
# the native libraries published beside it resolve.
exec /opt/enneaodin/EnneaOdin "$@"
WRAP
chmod 0755 "$WRAPPER"
echo "OK   $WRAPPER"

if [[ -f "$SCRIPT_DIR/EnneaOdin/Assets/icon.png" ]]; then
  mkdir -p "$ICON_DIR"
  install -m 0644 "$SCRIPT_DIR/EnneaOdin/Assets/icon.png" "$ICON_DIR/enneaodin.png"
  echo "OK   $ICON_DIR/enneaodin.png"
  if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor >/dev/null 2>&1 || true
  fi
else
  echo "WARN icon not found at EnneaOdin/Assets/icon.png; the menu entry gets no icon."
fi

if [[ -f "$SCRIPT_DIR/EnneaOdin.desktop" ]]; then
  mkdir -p "$DESKTOP_DIR"
  install -m 0644 "$SCRIPT_DIR/EnneaOdin.desktop" "$DESKTOP_DIR/EnneaOdin.desktop"
  echo "OK   $DESKTOP_DIR/EnneaOdin.desktop"
  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
  fi
else
  echo "WARN EnneaOdin.desktop not found; no menu entry installed."
fi

if [[ -f "$SCRIPT_DIR/$RULES_NAME" ]]; then
  mkdir -p "$UDEV_DIR"
  install -m 0644 "$SCRIPT_DIR/$RULES_NAME" "$UDEV_DIR/$RULES_NAME"
  echo "OK   $UDEV_DIR/$RULES_NAME"
  if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger || true
    echo "OK   udev rules reloaded"
  else
    echo "WARN udevadm not found; reload the rules yourself or reboot."
  fi
else
  echo "WARN $RULES_NAME not found; raw USB access will need root."
fi

echo ""
echo "Installed. Launch from the application menu, or run: enneaodin"
echo ""
echo "USB permissions"
echo "  The rules file tags the three Samsung download-mode devices with"
echo "  TAG+=\"uaccess\". On a systemd distribution that grants read and write"
echo "  access to the user logged in at the local seat, with no group"
echo "  membership and no world-writable device node. There is deliberately no"
echo "  GROUP=\"plugdev\" line: that group does not exist on Fedora, Arch or"
echo "  openSUSE, where udev then logs an unknown-group error and leaves the"
echo "  node owned by root. There is deliberately no MODE=\"0666\" line either:"
echo "  it would let every local process write to the raw USB node."
echo ""
echo "  uaccess applies on the NEXT plug-in. If the phone is attached now,"
echo "  unplug it and plug it back in."
echo ""
echo "  If EnneaOdin still reports \"permission denied\", it prints the exact"
echo "  device node it could not open. Check it with:"
echo "    ls -l /dev/bus/usb/<bus>/<address>"
echo "  A node owned by root with no ACL for your user means the rule did not"
echo "  match. A seatless session (ssh, or a login manager that does not set up"
echo "  a seat) does not get uaccess; in that case run EnneaOdin as root, or add"
echo "  a group rule for a group that exists on your distribution."
echo ""
echo "Protocol engine"
echo "  $PREFIX_DIR/$NATIVE_LIB is Heimdall's C++ compiled into a shared library"
echo "  and loaded into the application's own process. Nothing is spawned: there"
echo "  is no external flashing binary. The application finds it beside its own"
echo "  assemblies; ENNEAODIN_NATIVE_LIBRARY overrides the search with an"
echo "  absolute path if you move it."
echo ""
LIBUSB_CMD="$(libusb_install_command)"
if ldconfig -p 2>/dev/null | grep -q 'libusb-1\.0\.so\.0'; then
  echo "libusb-1.0: found by ldconfig. OK"
else
  echo "libusb-1.0: NOT found by ldconfig. EnneaOdin cannot talk to a device"
  echo "            without it, and will say so on startup. Install it with:"
  echo "              $LIBUSB_CMD"
fi
