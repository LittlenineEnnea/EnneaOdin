# EnneaOdin native bridge

One shared library, `libenneaodin.dylib` on macOS and `libenneaodin.so` on
Linux, that contains Heimdall's C++ and a C ABI over it. The managed side
P/Invokes that ABI; nothing here starts a process, and there is no `heimdall`
executable anywhere in the picture.

```
native/
  CMakeLists.txt             the whole build
  build.sh                   a two-line convenience wrapper over cmake
  cmake/
    FixLibusbInstallName.cmake   post-link: rewrite libusb's recorded path
  include/
    odin_bridge.h            the entire public surface. Read this first.
  src/
    odin_bridge.cpp          the ABI: sessions, PIT, transfers, cancellation
    OdinHooks.h/.cpp         what Heimdall's loops ask about Stop and progress
    OdinLogCapture.h/.cpp    Heimdall's stdout and stderr, as callback lines
    OdinStreamFile.h/.cpp    a managed stream turned into a real FILE *
  vendor/
    Heimdall/                Heimdall v2.2.2, MIT. LICENSE is in that directory.
```

## Building

```
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

or `./build.sh` for the same thing, `./build.sh Debug` for a debug build. The
build is out of source and refuses to configure in the source directory.

Requirements: CMake 3.20 or newer, a C++11 compiler, and libusb-1.0 with its
`.pc` file - `brew install libusb` on macOS, `libusb-1.0-0-dev` or
`libusb1-devel` on Linux. Qt is not needed: the Heimdall front end is not built.

Architecture follows the toolchain. On macOS pass
`-DCMAKE_OSX_ARCHITECTURES=arm64` or `=x86_64` to pick one. A universal binary
would need a universal libusb, which Homebrew does not ship, so it is not
attempted.

## What is in the library

Every Heimdall translation unit except `main.cpp`, plus `libpit`, plus the four
files in `src/`. All of Heimdall comes in because `Interface.cpp` - which every
other file needs for logging - names all eight Action classes in
`populateActionMap()`, so the whole command surface links in whether or not it
is called. `main.cpp` is excluded because it owns a process entry point, and
the process belongs to the host application.

Exactly 31 symbols are exported, all of them `odin_*`; everything else is
hidden. Check with `nm` or:

```
xcrun dyld_info -exports build/libenneaodin.dylib    # macOS
nm -D --defined-only build/libenneaodin.so           # Linux
```

## ABI version 2

`ODIN_ABI_VERSION` is 2. Version 1 was the first cut; 2 adds three entry points
that the managed side needs and nothing in 1 changed shape:

| Added | What it is |
|---|---|
| `odin_send_total_bytes` | The session-setup packet that declares the size of the whole flash before the first image. `FlashAction.cpp:155-201`. Driven through the public `BridgeManager::SendPacket` and `ReceivePacket` (`BridgeManager.h:158-159`), so no vendored code is duplicated. |
| `odin_enable_tflash` | Redirect the flash to the SD card. `FlashAction.cpp:434-466`, including its 5000 ms response timeout. |
| `odin_session_geometry_of` | The file-part size, parts per sequence and sequence timeout the BridgeManager is currently using, so a caller that shows them does not have to guess whether negotiation happened. This is what the three accessors added to `BridgeManager.h` are for. |

A managed caller refuses a library that reports a different number rather than
calling into it, because the argument lists and the struct layouts are what
would be wrong.

The device's raw begin-session response value is deliberately NOT exposed: it
is a local variable inside `BridgeManager::BeginSession`
(`BridgeManager.cpp:526`) that nothing records. All that can be established
afterwards is whether the file-part size moved off its default.

## libusb

libusb-1.0 is a system dependency. It is not bundled here and not linked
statically.

On macOS the linker would otherwise record Homebrew's absolute path, which only
exists on the machine that built the library. After linking, that recorded path
is rewritten to `@rpath/libusb-1.0.0.dylib` and a search list is baked in:
beside the library, the app bundle's `Frameworks` directory, then the Homebrew
and MacPorts prefixes. `cmake/FixLibusbInstallName.cmake` does this and then
fails the build if an absolute package-manager path survives, so the gate
cannot rot. A packaging step that wants to bundle libusb only has to copy the
dylib into `Contents/Frameworks`; no relinking is needed.

On Linux the ELF records the SONAME `libusb-1.0.so.0` and the loader finds it.
`$ORIGIN` is on the RPATH so a bundled copy beside the library would win.

## Cancellation

Heimdall's loops take no cancellation token, so two hooks were added to
`vendor/Heimdall/heimdall/source/BridgeManager.cpp`; `NOTICE` in the repository
root lists every edit to the vendored tree, which is now three files. What
cancellation can really do:

| Operation | Granularity |
|---|---|
| `odin_send_file_*` | one file part, 131072 bytes by default |
| `odin_download_pit` | one PIT part |
| `odin_detect`, `odin_open` while waiting for a device | one second |
| session handshakes | start of the call only, never mid-handshake |

A cancelled operation stops between packets, never inside one, so the device is
left at a packet boundary and the session can still be ended cleanly.

## What has not been tested

No Samsung device is attached to the machine this was written on, and none can
be. Everything that does not need a handset has been exercised: the version
string, detect and open with no device, every call with null callbacks, the
whole out-of-order state machine, libpit against this handset's real
`A16X_USA_SINGLE.pit`, the `FILE *` adapter over a managed stream byte for byte
by CRC-32, cancellation of a waiting detect from another thread, and a C++
exception being turned into a status code at the boundary.

The flashing path itself - begin session, send PIT, send a file, end session -
has never run against a device. Treat it as unverified.
