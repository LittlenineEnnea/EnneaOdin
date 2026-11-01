# EnneaOdin

A Samsung download-mode flashing tool for macOS and Linux, with a desktop GUI.

The Samsung download-mode protocol (Odin / LOKE) runs **inside this
application's own process**. It is Heimdall's own C++, vendored under
`native/vendor/Heimdall/`, compiled into a shared library (`libenneaodin`) and
called through a small C ABI; `OdinCore` drives that ABI and does the
enumeration, firmware reading and flash planning in C#.

There is no external flashing binary: EnneaOdin does not require, bundle, call
or shell out to `heimdall`, and it never starts a child process to talk to a
device. There is also no second protocol implementation: the C# transcription
of Heimdall's packet layer that earlier versions carried has been deleted, so
the packets on the wire come from one place.

Licence: GPL-3.0. Heimdall is MIT. See [Licence and attribution](#licence-and-attribution).

## Status, stated plainly

**The flashing path has never been run against a Samsung device.** No handset was
available while this was written. What that means concretely:

| Area | State |
|---|---|
| Build | Verified. `dotnet build EnneaOdin.sln -c Release` is clean, 0 warnings; the native library builds with 0 warnings in Release and Debug. |
| Publish and packaging | Verified on macOS arm64: one `./build.sh` builds the engine, publishes, and produces a signed `EnneaOdin.app` that launches by double-click. Checked at run time that it loads its own bundled libusb and not Homebrew's. |
| The native bridge | Verified without a device: the library loads, its ABI version and the linked Heimdall version are read back, Heimdall's own printed output arrives in the log with a level, cancellation from another thread stops a wait-for-device open within one poll, and a managed `Stream` reaches `BridgeManager::SendFile`'s `FILE *` with a byte-for-byte matching CRC-32. 42 checks, 0 failures. |
| Missing native library | Verified: the application reports a typed diagnostic with build instructions and keeps running; no crash, no blank panel. |
| UI loads | Verified by a headless smoke test on real Skia: the one window realises, every region lands in the right corner, every design token resolves under both theme variants, and every realised string is plain ASCII. 136 checks, 0 failures. |
| USB enumeration, descriptor reads, string descriptors | Verified against non-Samsung devices on macOS, without root. |
| LOKE handshake, session, PIT read, flash | **Not verified.** No handset has ever answered. The code that runs is Heimdall's, and Heimdall's flashing path is widely used, but it has never been exercised from here. |
| Visual appearance | **Not reviewed on a screen** in either light or dark theme. |

Treat the flashing path as untested code that writes to partitions. Read
[Before you flash anything](#before-you-flash-anything).

## Requirements

### .NET 8 SDK (to build)

```sh
# macOS
brew install dotnet@8

# Debian / Ubuntu
sudo apt install dotnet-sdk-8.0

# Fedora
sudo dnf install dotnet-sdk-8.0

# Arch
sudo pacman -S dotnet-sdk-8.0
```

Check with `dotnet --version`. A published build does not need the SDK; it is
self-contained.

### CMake 3.20 or newer and a C++ compiler (to build)

The protocol engine is C++, so building EnneaOdin from source needs a C++
toolchain as well as the .NET SDK. `build.sh` checks for both and stops with
the right install command if either is missing, before it publishes anything.

```sh
# macOS: the compiler comes with the command line tools
xcode-select --install
brew install cmake

# Debian / Ubuntu
sudo apt install cmake build-essential libusb-1.0-0-dev

# Fedora
sudo dnf install cmake gcc-c++ libusb1-devel

# Arch
sudo pacman -S cmake base-devel libusb
```

The libusb **development** package (the `-dev`, `-devel` or headers variant) is
needed to build, as well as the runtime library below. A published build needs
only the runtime one.

### libusb-1.0 (to run)

The only runtime dependency. **The macOS `.app` bundles it**, so a bundle built
by `./build.sh` needs no package manager on the machine it runs on; see
the `.app` section below. A plain publish directory, and every Linux build,
still takes it from the platform package manager:

```sh
# macOS
brew install libusb

# Debian / Ubuntu
sudo apt install libusb-1.0-0

# Fedora
sudo dnf install libusb1

# Arch
sudo pacman -S libusb
```

If libusb cannot be loaded, the application still starts and says so in the
status bar with the install command for the platform it is running on. It does
not crash and it does not silently show an empty device list.

The library is resolved at run time by trying, in order on macOS: the bare
name, then the application's own directory, then `../Frameworks` relative to it
(which is where the `.app` puts the bundled copy), then `$HOMEBREW_PREFIX/lib`,
`/opt/homebrew/lib`, the dylib's own install name under
`/opt/homebrew/opt/libusb/lib`, `/usr/local/lib`, the MacPorts path, and finally
the unversioned name. **The application's own copy is tried before any package
manager's**, which is what makes the bundle self-contained rather than merely
self-contained-looking. On Linux, the SONAME `libusb-1.0.so.0` first, since that
is present without the `-dev` package, then `libusb-1.0.so`.

Note that the engine and the managed code resolve libusb **separately**: the
C++ goes through its `@rpath`, the C# through the list above. Both have to find
the bundled copy, and `build.sh` checks the first while the second is
the ordering just described.

### Linux: USB access

A udev rule is needed so the device node is reachable without root:

```sh
sudo cp 51-samsung-download.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```

`install-linux.sh` does this for you.

The rule uses `TAG+="uaccess"`, which grants access to the user logged in at the
seat. It deliberately does **not** use `GROUP="plugdev"` (that group does not
exist on Fedora, Arch or openSUSE, where udev then logs an unknown-group error
and leaves the node owned by root) and deliberately does **not** use
`MODE="0666"` (which would make the raw USB node writable by every local
process). This follows `native/vendor/Heimdall/heimdall/60-heimdall.rules`.

The rules file is generated from `OdinCore.Usb.SamsungIds.UdevRulesText()` so the
ids in it cannot drift from the ids the scanner matches on. Do not hand-edit it;
change the ids in `OdinCore/Usb/UsbIds.cs` and regenerate.

### macOS: accessory permission

On recent macOS a newly attached USB device may be blocked until you allow it.
If the device is seen but cannot be claimed, check
**System Settings > Privacy and Security > Allow accessories to connect**.

No root is required on macOS. Control transfers and device claiming work as a
normal user.

## Build

```sh
./native/build.sh                               # the protocol engine (C++)
dotnet build EnneaOdin.sln -c Release           # compile the managed side
dotnet run --project EnneaOdin/EnneaOdin.csproj # run from source

./build.sh                 # engine + publish for this machine
./build.sh osx-arm64       # or a named runtime identifier
./build.sh osx-x64
./build.sh linux-x64
./build.sh linux-arm64
./build.sh all
```

`./build.sh` builds the native engine **first** and stops if cmake, a C++
compiler or the libusb headers are missing: a publish directory with no engine
beside it is not a working application, so there is no point publishing
without one. It then publishes and copies `libenneaodin.dylib` (or `.so`) into
`dist/<rid>/`, which is the first place the application looks.

`dotnet run` finds the engine too: the resolver walks up from the assembly
looking for `native/build/`, so a source checkout works with no copy step once
`./native/build.sh` has run once.

Output goes to `dist/<rid>/`.

### Cross-publishing

`./build.sh all` publishes the managed half for every runtime identifier, but
the engine is C++ and this build does **not** cross-compile it. A publish for
a runtime identifier other than this machine's prints a warning and gets no
engine; build it on a machine of that kind. A macOS build also picks one CPU
architecture rather than making a universal binary, because Homebrew does not
ship a universal libusb.

**The output is a directory, and you must run it as a directory.**
`PublishSingleFile` is deliberately not used: an Avalonia publish leaves
`libSkiaSharp`, `libHarfBuzzSharp` and `libAvaloniaNative` beside the managed
binary, and the binary on its own cannot find them. A macOS arm64 publish is
222 files and about 106 MB.

### macOS .app bundle: a thing you can double-click

```sh
brew install libusb cmake        # once
./build.sh                       # engine, publish, bundle, sign
# -> EnneaOdin.app, beside build.sh
```

Then **double-click `EnneaOdin.app` in Finder**, or:

```sh
open EnneaOdin.app
```

There is no separate packaging script: `./build.sh` does it, and `--no-app`
skips it. It refuses to bundle without `libenneaodin.dylib` in the publish
directory, puts the whole publish inside `Contents/MacOS`, and **ad-hoc signs
inside out**: every native library first, then the bundle. That order matters
because the signature has to cover the engine and not just the apphost, and
because on Apple silicon a Mach-O with a broken signature will not load at
all. It then verifies the bundle, the engine and the bundled libusb, and fails
if any of the three does not.

A cross-published macOS directory gets no bundle: it has no engine, and a
bundle that opens and refuses everything is worse than none.

Ad-hoc signing is **not** notarisation and not a Developer ID. A bundle built
on this machine opens; a copy that has been downloaded is quarantined by
Gatekeeper, and the attribute has to be cleared:

```sh
xattr -dr com.apple.quarantine EnneaOdin.app
```

`libusb-1.0` **is** inside the bundle, in `Contents/Frameworks`. It used to not
be, and the bundle that produced opened on a double click and then could not
reach a handset on any Mac without Homebrew; "it opens" and "it works" were two
different claims and only the first was true.

No relinking is involved. The engine records `@rpath/libusb-1.0.0.dylib` and its
baked-in rpath searches `@loader_path`, `@loader_path/../Frameworks`,
`/opt/homebrew/lib`, `/usr/local/lib` and `/opt/local/lib`, in that order, so
the bundled copy wins and a machine with no libusb at all still works. The
packager dereferences the symlink (Homebrew's is a link into `../Cellar`), sets
the copy's own install name to `@rpath`, signs it, and then **fails the build**
if the engine has no `@loader_path/../Frameworks` rpath to find it through.

No absolute build-machine path is recorded as a dependency, and the cmake gate
fails the build if one is. The package-manager prefixes in `LC_RPATH` are
deliberate and are checked against an allow-list by the same gate - an rpath
entry that does not exist is skipped by dyld, where a dependency that does not
exist is a load failure.

libusb is LGPL-2.1-or-later. It is bundled unmodified, as a separate dynamic
library the user can replace, and its licence travels with it in
`Contents/Resources/LICENCE-libusb.txt`.

### Linux system-wide install

```sh
./build.sh linux-x64
sudo ./install-linux.sh linux-x64
```

That installs the publish directory to `/opt/enneaodin/` - including
`libenneaodin.so`, which the script refuses to install without - a launcher
wrapper at `/usr/local/bin/enneaodin`, the menu entry, the icon, and the udev
rule. `sudo ./install-linux.sh --uninstall` removes them.

The engine records SONAME `libusb-1.0.so.0` and has `$ORIGIN` on its RPATH, so
the distribution's own libusb is used and no path from the build machine is
baked in.

## The interface

One window, laid out the way Odin is, with macOS visual conventions. There are
no pages, no navigation rail and no tabs. The device region at the top and the
action strip at the bottom both span the full width and are always visible,
because a flash takes minutes and you must be able to read progress and reach
Stop at any moment.

```
+--------------------------------------------------------------+
|  EnneaOdin 1.0.0   (status pill)        [Refresh] [About]    |
+--------------------------------------------------------------+
|  DEVICE    picker; status, session, libusb, engine;          |
|            session actions; device and partition tabs        |
+----------------------------+---------------------------------+
|                            |  FIRMWARE  Import bundle, CSC /  |
|  LOG                       |            HOME_CSC, BL AP CP    |
|  monospace, level pills    |            CSC USERDATA, PIT     |
|  newest at the bottom      +---------------------------------+
|                            | [Firmware][Manual][Options]      |
|                            |                                  |
|                            |  Firmware  bundle import, slots  |
|                            |  Manual    partition -> file     |
|                            |  Options   the switches          |
+----------------------------+---------------------------------+
|  progress, phase             [ Reset ] [ Stop ] [ Start ]    |
+--------------------------------------------------------------+
```

A GridSplitter sits between the two columns. Nothing has a fixed row height, so
a larger system font grows the layout instead of clipping it.

| Region | Contents |
|---|---|
| **Band** | The program, its version, the handset's status as a coloured pill, and Refresh and About. |
| **DEVICE** | One box. It says it is waiting; when a handset appears, **its descriptors are read automatically** and the box shows the model, the identifiers and whether it can be claimed. A picker appears only when more than one device is attached, and a red callout only when the protocol engine is not loaded. Read PIT, Save PIT, Close PC screen and Reboot device sit in the title bar; the information and partition tables appear below, on two tabs, only once something has been read. |
| **LOG** | Heimdall's own output, coloured by level: green for a step that succeeded, amber for `WARNING:`, red for `ERROR:`, grey for debug. Monospace, wrapped, selectable as one block. Auto-scroll sits with it. |
| **Manual** | The right column's second page. A partition name and a file per row, written in that order: type `boot`, point at `boot.img`, add a row, repeat. **Nothing is packed and nothing is written to disk** - the partition is named outright, so there is nothing to match by file name, and each image is streamed straight off disk. A `.lz4` is decompressed on the way out. Names are matched against the device PIT ignoring case. Re-partition is refused on this page: it rewrites the layout these rows name. |
| **FIRMWARE** | The first page. **Import bundle** opens a distributor's firmware `.zip` and matches its archives to the rows; **CSC** and **HOME_CSC** are two mutually exclusive tick boxes, each with a badge saying what it does to user data, choosing which CSC archive fills the CSC row. Then `BL`, `AP`, `CP`, `CSC` and `USERDATA`: each is one tick box whose label IS the coloured slot chip, so clicking the word `AP` includes or excludes AP. Beside each, what fills it, its size, a browse button and Clear. Last a `PIT` row with its own **Load**, which parses a PIT and shows its partition table without a device. There is no Resolve button: Start builds the plan, shows it for confirmation, and writes it. |
| **Options** | The third page, holding every switch. OPTIONS: Auto reboot, Re-partition, T-Flash, Skip partition size check. ADVANCED: Resume an open session, Wait for a device, Verify archive MD5 digests, Stream images from the archive, Verbose log. Selecting this page does **not** change which of the other two the flash comes from. |
| **Action strip** | The progress bar, the partition and overall percentages, the phase, and Reset, Stop and Start. |

`Reset` clears the six file rows and returns every option to its default. It
does not touch the device and it does not clear the log, and it asks first when
there is anything to discard.

There is **no** "Reset flash count". Odin has one; the download-mode protocol
ported here has no flash-counter request, so there is nothing behind it. It was
on screen and permanently disabled for a while, which was worse than not having
it: a greyed-out control still reads as a feature.

**Verbose log is the only verbosity control.** It turns on the debug lines in
the log, Heimdall's own per-packet and per-retry detail, and raises libusb from
`error` to `info`. There used to be three separate switches for what is one
question.

The resolved plan is written to the log rather than shown in a panel of its
own: one line per partition, and a red line for anything that matched nothing.

`Stop` sits beside `Start` rather than replacing it, so it never appears under
the pointer mid-flash, and it becomes usable the instant an operation begins.
`Start` is the only accent-styled button in the application.

The window uses the stock operating-system title bar, the system UI font at the
system size, and follows the system light or dark setting. There is no custom
window chrome, no acrylic, vibrancy, Mica or blur, and no bundled font.

It does **not** follow the system accent colour, and that is deliberate. Every
colour in `Styles/Tokens.axaml` is measured for contrast against all three
surfaces in both themes, and an accent taken from the user's desktop is the one
colour in the window nobody checked - on a machine whose accent is pink, the
Start button would be our measured blue and every checkbox tick would be an
unmeasured pink. One accent, ours: white on `#0B63CE` measures 5.69 and white
on `#1A72DD` measures 4.67, both over the 4.5 threshold.

The ramp is seven hues, each as a saturated `Fill` for marks, a `Tint` for
capsule backgrounds and a `Text` that clears 4.5:1 on its own tint *and* on both
box surfaces. A slot keeps its hue everywhere: AP is the same blue in the
firmware list, in the plan and on the progress line. Nothing is colour-only -
every status is a word, every log level prints its name inside its pill, and
every destructive option says in a sentence what it destroys.

### Firmware slots

`BL`, `AP`, `CP`, `CSC`, `USERDATA`, and a separate `PIT` row. Each takes a
`.tar`, `.tar.md5`, or a loose image.

Each slot also has a **tick box**, and unticking one is a real exclusion: the
archive is neither read nor sent, and the plan is built without it. The
defaults are `BL`, `AP`, `CP` and `CSC` ticked and `USERDATA` clear, because
USERDATA's only effect is to wipe and rewrite the user partition and having it
ticked by default would make data loss the path of least resistance.

### Importing a firmware bundle

**Import bundle** takes the `.zip` a distributor ships - SamFw, SamMobile,
Frija and the rest all use the same shape - and matches its archives to the
slot rows. It reads the zip's **central directory only**: listing the reference
handset's 5.76 GiB firmware took 4 ms and decompressed nothing. Nothing is
extracted until Start, and only the ticked rows are extracted then.

A firmware zip normally ships both `CSC` and `HOME_CSC`. They fill the same
slot and differ in one consequential way the names do not state, so the
segmented control does:

| | What it writes |
|---|---|
| **CSC** | The regional configuration **and the user data partition**, so it **erases user data**. Odin's own default, and the default here. |
| **HOME_CSC** | The regional configuration only. An update that keeps the user's data. |

The default is `CSC`, which is the riskier of the two, because it is what a
full-firmware flash means. The row says `ERASES USER DATA` while it is selected
rather than the default being quietly made safe.

### Streaming, and the temporary directory

**Images are streamed out of their archives, not spooled to disk.** With
*Stream images from the archive* on - which is the default - Start indexes
each tar for the offset of every member, decodes every compressed member to a
counter on as many cores as the machine has to learn its exact decompressed
length, and then the flash reads each image out of its archive while the device
is waiting for it. Nothing is written.

That length has to be exact: the protocol declares the total before the first
packet (`FlashAction.cpp:155-201`) and the per-sequence arithmetic derives from
it (`BridgeManager.cpp:1042-1055`). It is **measured**, never taken from the
LZ4 frame's own content-size field, which is optional and which a writer may
set wrong.

Measured on the reference handset's own four archives, 30 members and
10,342,527,811 bytes decompressed from 6,970,464,984:

| | Time | Written to disk |
|---|---|---|
| Index and measure, as it ships | **4.3 s** | 0 bytes |
| Index and measure, with digest verification on | 21.5 s | 0 bytes |
| Extract to a temporary tree (the old path) | 28.3 s | 10,342,527,811 bytes |

Three things got it from 23.8 s to 4.3 s, and all three are measurements
rather than guesses:

**The measuring pass decodes blocks, not frames, on every core.** An LZ4
frame descriptor has a block-independence flag (FLG bit 5); when it is set,
every block decodes with no reference to any other, so the blocks of one
member can be decoded in any order. Samsung sets it - all 27 compressed
members of the reference firmware carry FLG `0x6c`. On `super.img.lz4` alone,
5,496,955,290 bytes in and 8,641,324,824 out over 8242 blocks, with the file
cached:

| Lanes | 1 | 2 | 4 | 8 | 10 | 16 |
|---|---|---|---|---|---|---|
| Time | 7408 ms | 4004 ms | 2498 ms | 1982 ms | 2079 ms | 1872 ms |

A frame WITHOUT the flag is decoded in order and the log says so. The whole
pass is 2.5 s for all 27 members.

**Members are scheduled biggest first,** two at a time, with every core on the
blocks of each. The members are wildly uneven - one of them is 5.5 GiB and the
other 26 are 1.4 GiB between them - so starting the big one last would leave
the machine waiting on it after everything else had finished.

**Digest verification is off by default and no longer serial.** It was 21 of
the original 23.8 s. See the option table below for why it is off and what
still checks the archive when it is.

Every streamed member was verified byte-identical to the extracted one, both
plainly and with a simulated retry on every fourth packet - 95 checks, the
largest being `super.img.lz4` at 5,496,955,290 bytes.

A retry matters because `SendFile` handles a failed part by seeking back to
that part's own offset and sending it again, and an LZ4 decoder cannot seek.
A streamed image therefore goes through a 4 MiB rewind window, four times the
largest packet the protocol uses. A seek further back than the window **throws
and stops the transfer**; it does not quietly return the wrong bytes.

The switch is in ADVANCED and the old path is one checkbox away, not one
rebuild away, because the streamed path has not been exercised against a
handset.

Whichever path runs, the temporary directory is `$TMPDIR/enneaodin/enneaodin-*`
and it is deleted on success, failure and cancellation alike. A crash used to
leave it behind for ever - nothing had ever deleted a workspace it did not
create in the same process - so **startup now sweeps** workspaces that have
been cold for thirty minutes, and says in the log what it reclaimed. Thirty
minutes rather than thirty seconds because a second copy of this program may be
flashing out of its own.

Before extracting archives out of a zip, the free space on the volume holding
the temporary directory is checked against the exact requirement plus 5% or
256 MiB, whichever is larger, and the operation is refused up front with both
figures rather than filling the volume and failing halfway.

### Options

| Option | What it does |
|---|---|
| Reboot when finished | Ends the session with a reboot. Off leaves the handset in download mode. |
| Resume an open session | Continues a session a previous operation left open instead of starting a new one. |
| Wait for a device to appear | Keeps scanning instead of failing when nothing is attached yet. |
| Stream images from the archive | On: each image is decompressed straight out of its archive while the device waits for it, and nothing is written to disk. Off: everything is extracted to a temporary directory first. |
| Verify archive MD5 digests | Hashes each `.tar.md5` against the digest it carries before anything is sent. **Off by default**: it costs about 21 s on this firmware because MD5 is a chain and cannot be spread across cores, and most of what it would catch is already covered - the LZ4 content checksum is verified by the decoder as the flash reads each compressed member, two independent tar readers must agree on every member and length before a byte offset is believed, and an archive extracted from a zip had every byte CRC-32 checked on the way out. What it adds is an *early* answer on a hand-supplied archive. When on, it runs beside the measuring pass rather than in front of it. |
| Verbose log | Adds protocol-level detail to the log. |
| Repartition | Rewrites the partition table from the supplied PIT. Changes the layout of the device. |
| T-Flash | Writes to the SD card rather than to internal storage. |
| Skip partition size check | Writes an image even when it does not match the size the PIT declares. |

libusb's own log level is not a separate control: it is `error` normally and
`info` with Verbose log on. `error` is also upstream's default
(`BridgeManager.h:96`).

### Device information

Read on demand, grouped by where each value actually comes from, so no field is
ever a guess:

| Group | Source |
|---|---|
| **USB** | The device and configuration descriptors, read over USB: vendor and product id, manufacturer, product, serial number, `bcdDevice`, `bcdUSB`, class, subclass, protocol, endpoint-0 max packet size, configuration count, bus and address, negotiated link speed, the download-mode interface and its endpoints, the access state, and the basis on which the device was matched. |
| **Session** | The open protocol session: the file-part size the device negotiated, the raw negotiation result, sequence length, sequence timeout, and the current phase. |
| **PIT header** | The PIT read from the device: entry count, the header strings, logic unit count, PIT size. |
| **Partition table** | One block per PIT entry: partition name, flash filename, FOTA filename, identifier, binary type, device type and the rest of the entry. |

With no device attached the panel shows the typed diagnosis - libusb missing, no
device, present but not claimable, or not in download mode - rather than a blank
panel or a placeholder value.

## Recognised devices

EnneaOdin matches a device by its USB vendor and product id:

| Id | Name in Heimdall | Source |
|---|---|---|
| `04e8:6601` | `kPidGalaxyS` | `native/vendor/Heimdall/heimdall/source/BridgeManager.h:76` |
| `04e8:685d` | `kPidGalaxyS2` | `native/vendor/Heimdall/heimdall/source/BridgeManager.h:77` |
| `04e8:68c3` | `kPidDroidCharge` | `native/vendor/Heimdall/heimdall/source/BridgeManager.h:78` |

Vendor id `0x04E8` is `kVidSamsung`, `BridgeManager.h:71`. These are exactly the
three pairs in Heimdall's own `supportedDevices` table
(`BridgeManager.cpp:66-70`, with `kSupportedDeviceCount == 3` at
`BridgeManager.h:59`) and exactly the three in `native/vendor/Heimdall/heimdall/60-heimdall.rules`.

**This is a real limitation worth knowing about.** That table is old. Current
handsets normally appear as `04e8:685d`, which is covered, but if yours
enumerates with some other product id EnneaOdin will not recognise it. Nothing
was added to the list on a guess: an id that appears neither in Heimdall's
`supportedDevices` nor in its udev rules has no evidence behind it that it can
complete a LOKE handshake. If a new id needs supporting, it belongs in a
separate, user-visible "unconfirmed" tier that cannot drive a flash.

A download-mode interface is identified structurally as well, not only by id: it
must advertise interface class CDC-Data (`0x0A`, `BridgeManager.cpp:61`) and have
exactly two endpoints, one IN and one OUT (`BridgeManager.cpp:218`).

## Before you flash anything

- **Enter download mode** on the handset: power off, then hold the volume-down
  and either Bixby or Home combination for your model while connecting USB, and
  confirm the warning with volume-up. The exact combination differs by model;
  EnneaOdin cannot put a device into download mode.
- **`HOME_CSC` versus `CSC`**: a `HOME_CSC` package preserves user data on an
  update, a plain `CSC` wipes it.
- **Repartition changes the layout of the device.** It needs a PIT that matches
  the firmware. Getting it wrong can leave the handset unbootable.
- **Interrupting a partition write** abandons an open session with a partially
  written partition. Stop takes effect at the next packet boundary, never inside
  a packet, and not instantly: a file transfer stops at the end of the current
  file part, which can take about 3 seconds, and up to 2 minutes while the
  device is being waited on for an end-of-sequence acknowledgement. A wait for a
  device stops within one 1-second poll - measured at 497 ms after the request.
  The session is deliberately left open at a packet boundary so it can still be
  ended cleanly, and the progress bar is left where it stopped rather than
  reset, so the evidence of how far the write got survives.
- Start asks for confirmation first, listing the device, every resolved partition
  with its byte count, and the options in force.

## Project layout

```
EnneaOdin.sln
build.sh                     publish for one or all runtime identifiers
EnneaOdin.app                the signed bundle ./build.sh produces
install-linux.sh             install a publish directory system-wide
51-samsung-download.rules    udev rule, generated from SamsungIds
LICENCE                      GPL-3.0
NOTICE                       vendored dependencies and attribution

OdinCore/                    the library. No UI types, no Avalonia.
  Native/                    the C ABI of the protocol engine, and its loader
  Interop/                   the libusb-1.0 binding, and the one DllImport resolver
  Usb/                       enumeration, descriptors, access diagnostics
  Protocol/                  the session facade over the engine, PIT model, PIT text
  Firmware/                  tar and tar.md5 reading, LZ4 decoding, digests
  Flash/                     the flash plan, options and runner
  Devices/                   the device-information projection
  Diagnostics/               the log ring and event types

EnneaOdin/                   the Avalonia GUI
  Styles/Tokens.axaml        the only file containing a colour literal
  Styles/Components.axaml    card, table-row and log-row styles
  Styles/DesignTokens.cs     startup guard: every token must resolve
  Views/                     MainWindow, the one window
  ViewModels/                all UI state and commands
  Services/                  file picker and confirmation dialog

native/                      the protocol engine, built with CMake
  build.sh                   cmake wrapper; ./native/build.sh [Release|Debug]
  include/odin_bridge.h      the whole C ABI the managed side calls
  src/                       the ABI, the log capture, the stream adapter
  cmake/                     the post-link libusb install-name fix
  vendor/Heimdall/           Heimdall v2.2.2, MIT, compiled into the library
```

### How the two halves meet

```
EnneaOdin (Avalonia)
  -> OdinCore.Flash / OdinCore.Firmware        plan, archives, images  (C#)
  -> OdinCore.Usb                              which devices exist     (C#, libusb)
  -> OdinCore.Protocol.OdinSession             one session             (C# facade)
       -> OdinCore.Native                      the C ABI               (P/Invoke)
            -> libenneaodin                    the ABI implementation  (C++)
                 -> Heimdall BridgeManager     the Odin/LOKE protocol  (C++, vendored)
                      -> libusb-1.0            the wire
```

Two things are enforced by layering rather than by convention. `[DllImport]`
appears in exactly two files, `OdinCore/Interop/LibUsb.cs` and
`OdinCore/Native/OdinNative.cs`, and the GUI project references neither. And
the managed libusb binding **cannot** claim an interface or move a byte to a
device: `libusb_claim_interface`, `libusb_bulk_transfer` and
`libusb_control_transfer` are not imported at all, so the two libusb users in
this process cannot fight over the same interface. The engine claims; the
managed side enumerates.

### If the engine is missing

Nothing crashes and nothing goes blank. The application starts, lists devices,
reads firmware and shows the log; the first device operation reports one line
naming the file it looked for and how to build it, and then refuses. Set
`ENNEAODIN_NATIVE_LIBRARY` to an absolute path to override the search
entirely - useful when the library lives somewhere unusual, and the one case
where a wrong value is reported instead of being silently fallen back from.

### Dependencies

| Package | Version | Why |
|---|---|---|
| Avalonia | 11.1.3 | The UI framework. |
| FluentAvaloniaUI | 2.2.0 | Fluent v2 controls: the navigation shell, the status bar, the grouped option rows and the confirmation dialogs. 2.2.0 is the last version that targets Avalonia 11.1.x; 2.3.0 and later require a newer Avalonia. |
| CommunityToolkit.Mvvm | 8.3.2 | The single MVVM stack. Source-generated properties and commands. |
| SharpZipLib | 1.4.2 | Reading Odin `.tar` and `.tar.md5` archives. |
| K4os.Compression.LZ4.Streams | 1.3.8 | LZ4 frame decoding for the `.lz4` members Samsung ships inside those archives. There is no equivalent in the base class library. |

Native, not a NuGet package:

| Component | Where | Why |
|---|---|---|
| `libenneaodin` | built from `native/`, shipped beside the application | Heimdall v2.2.2's `BridgeManager` and `libpit`, compiled into a shared library. This is the Odin/LOKE protocol. MIT; see `NOTICE`. |
| `libusb-1.0` | a system package, not bundled | The USB wire, for both the engine and the managed enumerator. LGPL-2.1; see `NOTICE`. |

## Licence and attribution

EnneaOdin is Copyright (c) 2025 EnneaOdin contributors, distributed under the
**GNU General Public License, version 3 or later**. The full text is in `LICENCE`.

`native/vendor/Heimdall/` is a copy of **Heimdall v2.2.2** from the grimler fork,
<https://git.sr.ht/~grimler/Heimdall>, distributed under the **MIT licence**:

```
Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
Copyright (c) 2021-2024 Henrik Grimler
```

The MIT permission notice in `native/vendor/Heimdall/LICENSE` is retained as
that licence requires. That tree is compiled into `libenneaodin`, and three of
its files are edited: a cancellation and progress hook in
`heimdall/source/BridgeManager.cpp`, three read-only geometry accessors in
`heimdall/source/BridgeManager.h`, and a two-word bounds fix in
`libpit/source/libpit.h`. Every edit is marked in place with an `EnneaOdin:`
comment and listed exactly in `NOTICE`. Nothing else is modified, reformatted
or regenerated.

Every file derived from Heimdall carries its derivation in its header comment,
and every protocol value still held in C# carries a `<file>:<line>` citation
into `native/vendor/Heimdall/` at the point of use, so it can be checked
against the C++ it came from.

See `NOTICE` for the full attribution statement, including why GPL-3.0 is
compatible with the MIT terms and is this project's own choice rather than an
obligation inherited from Heimdall.
