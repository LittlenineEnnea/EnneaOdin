// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// The values below are read from Heimdall, each carrying the file and line:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp
//   native/vendor/Heimdall/heimdall/source/BridgeManager.h
//   native/vendor/Heimdall/heimdall/source/EndFileTransferPacket.h
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is distributed under the MIT licence. The above copyright notices
// and the permission notice in native/vendor/Heimdall/LICENSE are retained as
// that licence requires.
//
// SPDX-License-Identifier: GPL-3.0-or-later

namespace OdinCore.Protocol;

/// <summary>
/// The protocol facts the managed side still needs now that the wire is the
/// native engine's, each carrying the vendored C++ file and line it was read
/// from.
/// </summary>
public static class OdinConstants
{
    /// <summary>
    /// The file identifier a modem transfer must carry. SendFile rejects any
    /// other value for a modem destination (BridgeManager.cpp:1012) and this is
    /// also the default argument of SendFile itself (BridgeManager.h:167).
    /// </summary>
    public const uint ModemFileIdentifier = 0xFFFFFFFF; // BridgeManager.h:167, BridgeManager.cpp:1012

    // ---- Transfer geometry, before negotiation -----------------------------
    //
    // These are what a BridgeManager is constructed with. After BeginSession
    // the engine may have moved to 1 MiB parts, 30 parts per sequence and a
    // 120 s sequence timeout (BridgeManager.cpp:530-534); OdinSession reads
    // the values back through odin_session_geometry_of rather than assuming
    // which set is in force.

    public const int DefaultFileTransferPacketSize = 131072;      // BridgeManager.cpp:75
    public const int DefaultFileTransferSequenceMaxLength = 800;  // BridgeManager.cpp:74
    public const int DefaultFileTransferSequenceTimeoutMs = 30000;// BridgeManager.cpp:76

    /// <summary>
    /// The pause after the begin-session response, BridgeManager.cpp:528.
    /// </summary>
    public const int BeginSessionDelayMs = 3000;        // BridgeManager.cpp:528

    // ---- libusb log levels, as heimdall's five named settings -------------
    //
    // The engine applies these through BridgeManager::SetUsbLogLevel
    // (BridgeManager.cpp:1249-1266). OdinCore's own enumerator applies the
    // same five values to its own libusb context; see OdinCore/Interop/LibUsb.cs.

    public const int UsbLogLevelNone = 0;
    public const int UsbLogLevelError = 1;              // BridgeManager.h:96 (UsbLogLevel::Default == Error)
    public const int UsbLogLevelWarning = 2;
    public const int UsbLogLevelInfo = 3;
    public const int UsbLogLevelDebug = 4;

    /// <summary>
    /// The five spellings heimdall's --usb-log-level accepts
    /// (FlashAction.cpp:528-547). "info" is accepted by the parser even though
    /// the usage string omits it, so it is offered here too.
    /// </summary>
    public static IReadOnlyList<string> UsbLogLevelNames { get; } =
        new[] { "none", "error", "warning", "info", "debug" };

    public static int ParseUsbLogLevel(string name) => name switch
    {
        "none" => UsbLogLevelNone,
        "error" => UsbLogLevelError,
        "warning" => UsbLogLevelWarning,
        "info" => UsbLogLevelInfo,
        "debug" => UsbLogLevelDebug,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown USB log level.")
    };

    public static string UsbLogLevelName(int level) =>
        level >= 0 && level < UsbLogLevelNames.Count ? UsbLogLevelNames[level] : "error";
}

/// <summary>Where a file transfer is addressed. EndFileTransferPacket.h:35-36.</summary>
public enum FileDestination
{
    Phone = 0x00,   // EndFileTransferPacket.h:35
    Modem = 0x01    // EndFileTransferPacket.h:36
}
