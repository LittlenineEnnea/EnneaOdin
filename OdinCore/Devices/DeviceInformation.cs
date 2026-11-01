// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (DetectDevice, FindDeviceInterface)
//   native/vendor/Heimdall/heimdall/source/Interface.cpp      (PrintPit)
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp
//   native/vendor/Heimdall/libpit/source/libpit.h
//   native/vendor/Heimdall/libpit/source/libpit.cpp
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using OdinCore.Native;
using OdinCore.Protocol;
using OdinCore.Usb;

namespace OdinCore.Devices;

/// <summary>
/// One labelled row of device information.
/// </summary>
/// <param name="Group">"USB", "Session", "PIT header" or "Partition table".</param>
/// <param name="Name">The label shown to the user.</param>
/// <param name="Value">The value, as text.</param>
/// <param name="Source">
/// Where the value came from, in a form a reader can check: "USB device
/// descriptor idVendor", "PIT entry +24 Block Count". It exists so that nobody
/// can add a field later without stating its provenance, which is exactly how
/// the previous implementation came to display a model name the protocol never
/// reports.
/// </param>
public sealed record InfoField(string Group, string Name, string Value, string Source);

/// <summary>
/// Everything this tool can honestly say about a download-mode device, and
/// nothing more.
/// </summary>
public static class DeviceInformation
{
    public const string GroupUsb = "USB";
    public const string GroupSession = "Session";
    public const string GroupPitHeader = "PIT header";
    public const string GroupPartitionTable = "Partition table";

    /// <summary>
    /// The sentence the information panel ends with. Plain, and deliberately
    /// not apologetic.
    /// </summary>
    public const string ScopeNote =
        "The download-mode protocol does not report the model name, firmware version or sales code. " +
        "These fields are deliberately absent.";

    /// <summary>
    /// Everything readable from descriptors alone: no session, no claim, no
    /// reboot. These are the same values heimdall prints at
    /// BridgeManager.cpp:139-168, minus the quoting and the indentation.
    /// </summary>
    public static IReadOnlyList<InfoField> FromUsb(UsbDeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var c = CultureInfo.InvariantCulture;
        var rows = new List<InfoField>
        {
            new(GroupUsb, "Vendor ID", device.Id.VendorId.ToString("X4", c),
                "USB device descriptor idVendor"),                                  // BridgeManager.cpp:139
            new(GroupUsb, "Product ID", device.Id.ProductId.ToString("X4", c),
                "USB device descriptor idProduct"),                                 // BridgeManager.cpp:139
            new(GroupUsb, "Manufacturer", Text(device.Manufacturer),
                "USB string descriptor at iManufacturer"),                          // BridgeManager.cpp:146
            new(GroupUsb, "Product", Text(device.Product),
                "USB string descriptor at iProduct"),                               // BridgeManager.cpp:152

            // Labelled "USB serial number" on purpose: it is the USB string
            // descriptor, not the handset serial number, which the protocol
            // does not report at all.
            new(GroupUsb, "USB serial number", Text(device.SerialNumber),
                "USB string descriptor at iSerialNumber"),                          // BridgeManager.cpp:158

            new(GroupUsb, "Device release (bcdDevice)", device.BcdDevice.ToString("X4", c),
                "USB device descriptor bcdDevice"),                                 // BridgeManager.cpp:165
            new(GroupUsb, "USB version (bcdUSB)", device.BcdUsb.ToString("X4", c),
                "USB device descriptor bcdUSB"),
            new(GroupUsb, "Device class", device.DeviceClass.ToString("X2", c),
                "USB device descriptor bDeviceClass"),                              // BridgeManager.cpp:162
            new(GroupUsb, "Device subclass", device.DeviceSubClass.ToString("X2", c),
                "USB device descriptor bDeviceSubClass"),                           // BridgeManager.cpp:162
            new(GroupUsb, "Device protocol", device.DeviceProtocol.ToString("X2", c),
                "USB device descriptor bDeviceProtocol"),                           // BridgeManager.cpp:162
            new(GroupUsb, "Max packet size, endpoint 0", device.MaxPacketSize0.ToString(c),
                "USB device descriptor bMaxPacketSize0"),
            new(GroupUsb, "Configuration count", device.ConfigurationCount.ToString(c),
                "USB device descriptor bNumConfigurations"),                        // BridgeManager.cpp:168
            new(GroupUsb, "Bus / address",
                string.Format(c, "{0} / {1}", device.BusNumber, device.DeviceAddress),
                "libusb_get_bus_number, libusb_get_device_address"),
            new(GroupUsb, "Negotiated link speed", SpeedText(device.Speed),
                "libusb_get_device_speed, enum libusb_speed"),
            new(GroupUsb, "Match basis", SamsungIds.MatchBasis(device.Id),
                "Comparison against the supported-device table, BridgeManager.cpp:66-70")
        };

        foreach (UsbInterfaceInfo iface in device.Interfaces)
        {
            // BridgeManager.cpp:189-194 prints interface number, class triple
            // and endpoint count per interface and alt setting.
            rows.Add(new InfoField(GroupUsb,
                string.Format(c, "Interface {0}.{1}", iface.InterfaceIndex, iface.AltSettingIndex),
                string.Format(c, "number {0}, class {1}, {2} endpoint(s)",
                    iface.InterfaceNumber, iface.ClassText, iface.Endpoints.Count),
                "USB interface descriptor bInterfaceNumber, bInterfaceClass.bInterfaceSubClass.bInterfaceProtocol, bNumEndpoints"));

            foreach (UsbEndpointInfo endpoint in iface.Endpoints)
            {
                // BridgeManager.cpp:206-208 prints address, max packet size
                // and interval per endpoint.
                rows.Add(new InfoField(GroupUsb,
                    string.Format(c, "  Endpoint {0}", endpoint.AddressText),
                    string.Format(c, "{0}, max packet {1}, interval {2}",
                        endpoint.IsInput ? "IN" : "OUT", endpoint.MaxPacketSize, endpoint.Interval),
                    "USB endpoint descriptor bEndpointAddress, wMaxPacketSize, bInterval"));
            }
        }

        rows.Add(device.Download is { } download
            ? new InfoField(GroupUsb, "Download-mode interface", download.ToString(),
                "Selection rule at BridgeManager.cpp:217-227: first interface with 2 endpoints, class 0A, and both directions present")
            : new InfoField(GroupUsb, "Download-mode interface",
                "none found (no CDC-Data interface with two endpoints)",
                "Selection rule at BridgeManager.cpp:217-227"));

        rows.Add(new InfoField(GroupUsb, "Access", AccessText(device),
            UsbPlatform.IsLinux
                ? "/dev/bus/usb permission check and the interface driver link"
                : "libusb open result and the macOS accessory-approval setting"));

        if (!string.IsNullOrEmpty(device.AccessDetail))
        {
            rows.Add(new InfoField(GroupUsb, "Access detail", device.AccessDetail,
                "OdinCore.Usb.UsbAccess"));
        }

        if (device.Download is { IndexDiffersFromNumber: true } mismatch)
        {
            // Worth surfacing: upstream claims the interface ARRAY INDEX
            // (BridgeManager.cpp:223, used at :246), not bInterfaceNumber. For
            // a typical download-mode configuration the two are equal. When
            // they are not, this port still claims what upstream claims, and
            // says so here rather than being silently wrong.
            rows.Add(new InfoField(GroupUsb, "Interface index warning",
                string.Format(c,
                    "The claimed interface array index ({0}) differs from bInterfaceNumber ({1}). Heimdall claims the array index; this port does the same.",
                    mismatch.InterfaceIndex, mismatch.InterfaceNumber),
                "BridgeManager.cpp:223, :246"));
        }

        return rows;
    }

    /// <summary>
    /// What a session reports about itself: the transfer geometry the engine
    /// settled on, and which engine that is.
    /// </summary>
    public static IReadOnlyList<InfoField> FromSession(OdinSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var c = CultureInfo.InvariantCulture;

        // Zero means one of two things and the session's own phase is what
        // tells them apart: before a session is begun the device has not been
        // asked, and after it zero is the device saying it will not accept a
        // different packet size (BridgeManager.cpp:558).
        bool negotiated = session.DeviceDefaultFilePartSize != 0;

        string value = negotiated
            ? string.Format(c,
                "supported; the device declared {0} byte parts and this session uses {1}",
                session.DeviceDefaultFilePartSize, session.FilePartSize)
            : string.Format(c, "not supported or not yet negotiated, using {0} byte parts",
                            session.FilePartSize);

        OdinEngineStatus engine = OdinSession.Engine;

        return new[]
        {
            new InfoField(GroupSession, "Device file-part size negotiation", value,
                negotiated
                    ? "the begin-session response, kept on the BridgeManager by EnneaOdin and read through odin_session_geometry_of"
                    : "BridgeManager.cpp:75, read through odin_session_geometry_of"),

            // 30 parts of 1 MiB is 30 MiB per sequence, and the sequence is
            // the unit the device acknowledges. Both numbers decide how long
            // a stall can last before it is noticed, which is why they are on
            // screen rather than only in the protocol.
            new InfoField(GroupSession, "Bytes per sequence",
                string.Format(c, "{0} bytes ({1} parts of {2})",
                    (long)session.SequenceMaxLength * session.FilePartSize,
                    session.SequenceMaxLength, session.FilePartSize),
                "derived from the two values below, both read through odin_session_geometry_of"),

            new InfoField(GroupSession, "Sequence length",
                string.Format(c, "{0} parts per sequence", session.SequenceMaxLength),
                negotiated ? "BridgeManager.cpp:534" : "BridgeManager.cpp:74"),

            new InfoField(GroupSession, "Sequence timeout",
                string.Format(c, "{0} ms", session.SequenceTimeoutMs),
                negotiated ? "BridgeManager.cpp:532" : "BridgeManager.cpp:76"),

            new InfoField(GroupSession, "Phase", session.Tracker.Describe(),
                "OdinCore.Protocol.OdinSessionTracker"),

            new InfoField(GroupSession, "Protocol engine",
                string.Format(c, "Heimdall {0}, bridge {1}, ABI {2}",
                    engine.HeimdallVersion ?? "(not reported)",
                    engine.BridgeVersion ?? "(not reported)", engine.AbiVersion),
                "odin_heimdall_version, which returns the global at Interface.cpp:45 of the linked C++"),

            new InfoField(GroupSession, "Engine library", engine.Path ?? "(not loaded)",
                "The candidate OdinCore.Native.OdinNativeLoader loaded")
        };
    }

    /// <summary>
    /// The PIT header, with the labels Interface::PrintPit uses.
    /// </summary>
    public static IReadOnlyList<InfoField> FromPit(PitData pit)
    {
        ArgumentNullException.ThrowIfNull(pit);

        var c = CultureInfo.InvariantCulture;

        return new[]
        {
            new InfoField(GroupPitHeader, "Entry count", pit.EntryCount.ToString(c),
                "PIT header +4 (libpit.cpp:90); label Interface.cpp:209"),
            new InfoField(GroupPitHeader, "Unknown string", Text(pit.ComTar2),
                "PIT header +8, 8 bytes (libpit.cpp:94); label Interface.cpp:210"),
            new InfoField(GroupPitHeader, "CPU/bootloader tag", Text(pit.CpuBlId),
                "PIT header +16, 8 bytes (libpit.cpp:97); label Interface.cpp:211"),
            new InfoField(GroupPitHeader, "Logic unit count", pit.LogicUnitCount.ToString(c),
                "PIT header +24, uint16 (libpit.cpp:101); label Interface.cpp:212"),
            new InfoField(GroupPitHeader, "PIT size",
                string.Format(c, "{0} bytes ({1} padded)", pit.DataSize, pit.PaddedSize),
                "Computed: libpit.h:352 (GetDataSize), libpit.h:357 (GetPaddedSize)")
        };
    }

    /// <summary>
    /// One row per PIT entry, with every field from the offsets in PitEntry.
    /// </summary>
    public static IReadOnlyList<InfoField> PartitionTable(PitData pit)
    {
        ArgumentNullException.ThrowIfNull(pit);

        var c = CultureInfo.InvariantCulture;
        var rows = new List<InfoField>();

        for (int i = 0; i < pit.EntryCount; i++)
        {
            PitEntry e = pit.GetEntry(i);
            string label = string.Format(c, "Entry #{0}", i);

            rows.Add(new InfoField(GroupPartitionTable, label + " Partition Name",
                Text(e.PartitionName), "PIT entry +36 (libpit.cpp:139)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Flash Filename",
                Text(e.FlashFilename), "PIT entry +68 (libpit.cpp:140)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " FOTA Filename",
                Text(e.FotaFilename), "PIT entry +100 (libpit.cpp:141)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Identifier",
                e.Identifier.ToString(c), "PIT entry +8 (libpit.cpp:118)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Binary Type",
                string.Format(c, "{0} ({1})", e.BinaryType, e.BinaryTypeLabel),
                "PIT entry +0 (libpit.cpp:112); labels libpit.h:55-56"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Device Type",
                string.Format(c, "{0} ({1})", e.DeviceType, e.DeviceTypeLabel),
                "PIT entry +4 (libpit.cpp:115); labels libpit.h:61-65"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Attributes",
                string.Format(c, "{0} ({1})", e.Attributes, e.AttributesLabel),
                "PIT entry +12 (libpit.cpp:121); bits libpit.h:70-71"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Update Attributes",
                e.UpdateAttributesLabel.Length == 0
                    ? e.UpdateAttributes.ToString(c)
                    : string.Format(c, "{0} ({1})", e.UpdateAttributes, e.UpdateAttributesLabel),
                "PIT entry +16 (libpit.cpp:124); bits libpit.h:77-78"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Block Size/Offset",
                e.BlockSizeOrOffset.ToString(c), "PIT entry +20 (libpit.cpp:127)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Block Count",
                e.BlockCount.ToString(c), "PIT entry +24 (libpit.cpp:130)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " Partition size",
                e.PartitionSize is { } size
                    ? string.Format(c, "{0} bytes", size)
                    : "not applicable for this device type",
                "Computed: block count times 512, or times 4096 for UFS (FlashAction.cpp:331-335)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " File Offset (obsolete)",
                e.FileOffset.ToString(c), "PIT entry +28 (libpit.cpp:133)"));
            rows.Add(new InfoField(GroupPartitionTable, label + " File Size (obsolete)",
                e.FileSize.ToString(c), "PIT entry +32 (libpit.cpp:136)"));
        }

        return rows;
    }

    /// <summary>Everything available without a session.</summary>
    public static IReadOnlyList<InfoField> Collect(UsbDeviceRecord device, PitData? pit, OdinSession? session)
    {
        var rows = new List<InfoField>(FromUsb(device));

        if (session is not null)
            rows.AddRange(FromSession(session));

        if (pit is not null)
        {
            rows.AddRange(FromPit(pit));
            rows.AddRange(PartitionTable(pit));
        }

        return rows;
    }

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not reported)" : value;

    private static string SpeedText(UsbSpeed speed) => speed switch
    {
        UsbSpeed.Low => "low (1.5 Mbit/s)",
        UsbSpeed.Full => "full (12 Mbit/s)",
        UsbSpeed.High => "high (480 Mbit/s)",
        UsbSpeed.Super => "super (5 Gbit/s)",
        UsbSpeed.SuperPlus => "super plus (10 Gbit/s)",
        UsbSpeed.SuperPlusX2 => "super plus x2 (20 Gbit/s)",
        _ => "not reported"
    };

    private static string AccessText(UsbDeviceRecord device) => device.Access switch
    {
        UsbAccessState.Claimable => "claimable",
        UsbAccessState.PermissionDenied => "permission denied",
        UsbAccessState.DriverBound => device.BoundKernelDriver is { Length: > 0 } driver
            ? "kernel driver bound: " + driver
            : "kernel driver bound",
        UsbAccessState.NotPresent => "not present",
        _ => "not determined"
    };
}
