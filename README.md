# EnneaOdin – Samsung Flash Tool for macOS & Linux

Cross-platform Samsung firmware flash tool built with **Avalonia UI (.NET 8)**.  
GUI front-end wrapping **heimdall** — the only correct open-source implementation
of Samsung's download-mode USB protocol on macOS/Linux.

```
┌─────────────────────────────────────────────────────────┐
│  ⚡ EnneaOdin – Samsung Flash Tool        heimdall: ✓     │
├──────────────────────────┬──────────────────────────────┤
│  PORT / DEVICE           │  LOG                   Debug │
│  Port [ /dev/cu... ] [⟳] │  ┌──────────────────────────┐│
│                          │  │ 08:12:01  [INFO]  Scan   ││
│  FIRMWARE SLOTS          │  │ 08:12:02  [OK]    Found  ││
│  BL       [.........] […]│  │ 08:12:10  [INFO]  Flash  ││
│  AP       [.........] […]│  │ 08:12:55  [OK]    Done ✓ ││
│  CP       [.........] […]│  └──────────────────────────┘│
│  CSC      [.........] […]│                              │
│  USERDATA [.........] […]│  Devices tab: detected USB   │
│                          │  devices + install guide     │
│  OPTIONS                 │                              │
│  ✓ Auto Reboot  ✓ Boot   │                              │
│  □ EFS Clear    □ Blank  │                              │
│  □ Repartition  □ Reset  │                              │
│  □ NAND Erase   □ Verify │                              │
│                          │                              │
│  [ ▶ FLASH ] [ ■ STOP ]  │                              │
│  [████████░░░░░] 62%     │                              │
│  Uploading AP...         │                              │
└──────────────────────────┴──────────────────────────────┘
```

---

## Requirements

### 1 — .NET 8 SDK

```bash
# macOS (Homebrew)
brew install dotnet@8

# Linux (Debian/Ubuntu)
wget https://dot.net/v1/dotnet-install.sh
bash dotnet-install.sh --channel 8.0
```

Verify: `dotnet --version`  → should print `8.x.x`

### 2 — heimdall

heimdall handles the actual USB communication with Samsung devices in download mode.

```bash
# macOS (arm64 or x64)
brew install heimdall

# Debian / Ubuntu
sudo apt install heimdall-flash

# Arch Linux
sudo pacman -S heimdall

# Build from source (all platforms)
git clone https://github.com/Benjamin-Dobell/Heimdall
cd Heimdall && cmake -S . -B build && cmake --build build
sudo cmake --install build
```

Verify: `heimdall version`

### 3 — Linux: USB permissions

On Linux you need a udev rule so you can access the Samsung USB device without root:

```bash
# Create udev rule for Samsung download mode
sudo tee /etc/udev/rules.d/51-samsung-download.rules << 'EOF'
# Samsung Electronics – Download Mode (Odin/Heimdall)
SUBSYSTEM=="usb", ATTR{idVendor}=="04e8", ATTR{idProduct}=="6601", MODE="0666", GROUP="plugdev"
SUBSYSTEM=="usb", ATTR{idVendor}=="04e8", ATTR{idProduct}=="685d", MODE="0666", GROUP="plugdev"
SUBSYSTEM=="usb", ATTR{idVendor}=="04e8", ATTR{idProduct}=="6860", MODE="0666", GROUP="plugdev"
SUBSYSTEM=="usb", ATTR{idVendor}=="04e8", ATTR{idProduct}=="6877", MODE="0666", GROUP="plugdev"
EOF

sudo udevadm control --reload-rules
sudo udevadm trigger

# Add yourself to plugdev group
sudo usermod -aG plugdev "$USER"
# Log out and back in for the group to take effect
```

---

## Build

```bash
# Clone / extract the project
cd EnneaOdin

# Make scripts executable
chmod +x build.sh package-macos.sh

# Build for macOS arm64 (Apple Silicon)
./build.sh macos

# Build for Linux x64
./build.sh linux

# Build for both
./build.sh all

# Or use dotnet directly
dotnet build EnneaOdin.sln
dotnet run --project EnneaOdin/EnneaOdin.csproj
```

Output binaries land in `dist/`:
- `dist/macos-arm64/EnneaOdin`  – self-contained, no .NET runtime needed
- `dist/linux-x64/EnneaOdin`    – self-contained, no .NET runtime needed

### macOS .app bundle

```bash
./build.sh macos
./package-macos.sh
# → dist/EnneaOdin.app

# Optional: create DMG
hdiutil create -volname EnneaOdin \
  -srcfolder dist/EnneaOdin.app \
  -ov -format UDZO \
  dist/EnneaOdin.dmg
```

---

## How to Flash

1. **Connect your phone in Download Mode**
   - Old (Home button): Power off → hold `Vol-Down + Home`, plug USB
   - New (no Home button): Power off → hold `Bixby + Vol-Down`, plug USB
   - Accept the warning with `Vol-Up`

2. **Open EnneaOdin**

3. **Click ⟳ Scan** — the port should appear automatically

4. **Browse (…)** to select your firmware files for each slot:
   - `BL_xxx.tar.md5`
   - `AP_xxx.tar.md5`
   - `CP_xxx.tar.md5`
   - `CSC_xxx.tar.md5` or `HOME_CSC_xxx.tar.md5`
   - `USERDATA_xxx.tar.md5` (optional, wipes user data)

5. **Set options** as needed (Auto Reboot is on by default)

6. **Click ▶ FLASH**

> ⚠ Using `HOME_CSC` instead of `CSC` preserves user data during a firmware update.  
> ⚠ `EFS Clear` erases IMEI/baseband calibration — only use if explicitly needed.

---

## Slot / Options Reference

| Slot | Contains |
|------|----------|
| BL | Bootloader partitions (sbl, tzsw, tz, rpm, …) |
| AP | Android system partitions (boot, system, vendor, odm, …) |
| CP | Modem / radio firmware |
| CSC | Regional config + apps (wipes data if `CSC`, preserves if `HOME_CSC`) |
| USERDATA | User partition (wipes all user data) |

| Option | Description |
|--------|-------------|
| Auto Reboot | Reboot to normal mode after flash |
| Boot Update | Update boot partition |
| EFS Clear | Clear EFS (IMEI) partition — **dangerous** |
| Blank Flash | Blank flash mode |
| Repartition | Repartition device using PIT from firmware |
| Reset Flash Count | Reset the flash counter |
| NAND Erase All | Full NAND erase before flash |
| Verify Flash | Verify written data (slower) |

---

## Project Structure

```
EnneaOdin/
├── EnneaOdin.sln
├── build.sh               ← build for macOS arm64 / Linux x64
├── package-macos.sh       ← wrap into .app bundle
│
├── OdinCore/              ← cross-platform library
│   ├── Models/Models.cs         shared data models
│   ├── Usb/DeviceScanner.cs     find Samsung devices via sysfs / system_profiler
│   ├── Tar/TarInspector.cs      inspect .tar / .tar.md5 firmware packages
│   └── Protocol/
│       └── HeimdallBackend.cs   heimdall CLI wrapper (flash, detect, PIT)
│
└── EnneaOdin/              ← Avalonia UI application
    ├── Program.cs
    ├── App.axaml / App.axaml.cs
    ├── Styles/AppStyle.axaml    dark Samsung-inspired stylesheet
    ├── Converters/              bool→color converter
    ├── ViewModels/
    │   └── MainWindowViewModel.cs   all UI state + commands
    └── Views/
        ├── MainWindow.axaml         UI layout (slots, options, log, progress)
        └── MainWindow.axaml.cs      file picker code-behind
```

---

## Architecture Notes

**Why heimdall and not a native C# USB stack?**

Samsung's download-mode protocol uses raw USB bulk transfers (not CDC-ACM serial).
On macOS and Linux this requires `libusb`. The only production-quality open-source
implementation is heimdall (C++, libusb). SharpOdinClient/Freya use `SerialPort`
which only works on Windows where Samsung's official driver virtualises the USB
device as a COM port. EnneaOdin wraps heimdall's CLI to get correct cross-platform
behaviour while providing a modern native GUI.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `heimdall not found` | `brew install heimdall` / `sudo apt install heimdall-flash` |
| `Permission denied` on Linux | Add udev rules (see above), re-login |
| Device not detected | Check USB cable, try different port, verify download mode |
| `Protocol initialisation failed` | Reconnect device; some USB hubs fail — use direct port |
| macOS Gatekeeper blocks app | `xattr -d com.apple.quarantine EnneaOdin.app` |

---

## License

GPL-3.0 — same licence as SharpOdinClient and Freya.  
heimdall is MIT licensed.
