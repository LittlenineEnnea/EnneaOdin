// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.h    (kVidSamsung, kPidGalaxyS,
//                                                kPidGalaxyS2, kPidDroidCharge)
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (supportedDevices,
//                                                USB_CLASS_CDC_DATA)
//   native/vendor/Heimdall/heimdall/60-heimdall.rules         (the udev rule set)
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace OdinCore.Usb;

/// <summary>A USB vendor and product id pair, as read from a device descriptor.</summary>
public readonly record struct UsbId(ushort VendorId, ushort ProductId)
{
    public override string ToString() =>
        VendorId.ToString("X4", CultureInfo.InvariantCulture) + ":" +
        ProductId.ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>
/// The single source of truth for the USB identities EnneaOdin recognises, and
/// for the udev rules that make them reachable on Linux. Nothing else in the
/// solution may hard-code a vendor or product id, and the rules file in the
/// repository root is generated from <see cref="UdevRulesText"/> so that the
/// scanner and the rules cannot drift apart.
/// </summary>
public static class SamsungIds
{
    /// <summary>Samsung Electronics.</summary>
    public const ushort VendorId = 0x04E8;          // BridgeManager.h:71 (kVidSamsung)

    /// <summary>Galaxy S class download mode.</summary>
    public const ushort PidGalaxyS = 0x6601;        // BridgeManager.h:76 (kPidGalaxyS)

    /// <summary>Galaxy S2 class download mode. This is the id current handsets use.</summary>
    public const ushort PidGalaxyS2 = 0x685D;       // BridgeManager.h:77 (kPidGalaxyS2)

    /// <summary>Droid Charge download mode.</summary>
    public const ushort PidDroidCharge = 0x68C3;    // BridgeManager.h:78 (kPidDroidCharge)

    // Heimdall's supportedDevices table holds exactly these three pairs:
    // BridgeManager.cpp:66-70, with kSupportedDeviceCount == 3 at
    // BridgeManager.h:59.
    private static readonly ushort[] ProductIds = { PidGalaxyS, PidGalaxyS2, PidDroidCharge };

    /// <summary>
    /// The three download-mode product ids, in the order upstream lists them.
    /// </summary>
    public static ReadOnlySpan<ushort> DownloadModeProductIds => ProductIds;

    /// <summary>
    /// The USB interface class a download-mode interface advertises: CDC-Data.
    /// Upstream defines this itself rather than using libusb's name for it.
    /// </summary>
    public const byte CdcDataInterfaceClass = 0x0A; // BridgeManager.cpp:61 (USB_CLASS_CDC_DATA)

    /// <summary>
    /// A download-mode interface has exactly two endpoints, one IN and one OUT.
    /// </summary>
    public const int RequiredEndpointCount = 2;     // BridgeManager.cpp:218

    /// <summary>
    /// Upstream always reads configuration index 0, which is the first
    /// configuration and not necessarily the active one.
    /// </summary>
    public const byte ConfigDescriptorIndex = 0;    // BridgeManager.cpp:172

    /// <summary>True when this vendor and product pair is a download-mode identity.</summary>
    public static bool IsDownloadMode(UsbId id) =>
        id.VendorId == VendorId && DownloadModeProductIds.Contains(id.ProductId);

    /// <summary>True when this is a Samsung device of any kind.</summary>
    public static bool IsSamsung(UsbId id) => id.VendorId == VendorId;

    /// <summary>
    /// A short, honest statement of how a device was matched, for the log and
    /// the device list. It never names a file or a device node, because neither
    /// takes any part in the match.
    /// </summary>
    public static string MatchBasis(UsbId id) =>
        IsDownloadMode(id)
            ? "matched vendor id " + id.VendorId.ToString("X4", CultureInfo.InvariantCulture) +
              " and product id " + id.ProductId.ToString("X4", CultureInfo.InvariantCulture) +
              " against Heimdall's supported-device table"
            : IsSamsung(id)
                ? "Samsung vendor id with a product id that is not a download-mode id"
                : "not a Samsung device";

    /// <summary>
    /// The exact contents of 51-samsung-download.rules, generated from the ids
    /// above so the two can never disagree.
    /// </summary>
    public static string UdevRulesText()
    {
        var text = new StringBuilder();

        text.Append("# 51-samsung-download.rules\n");
        text.Append("# Samsung download mode (Odin / LOKE protocol) - raw USB access for EnneaOdin.\n");
        text.Append("#\n");
        text.Append("# Install:\n");
        text.Append("#   sudo cp 51-samsung-download.rules /etc/udev/rules.d/\n");
        text.Append("#   sudo udevadm control --reload-rules && sudo udevadm trigger\n");
        text.Append("#\n");
        text.Append("# TAG+=\"uaccess\" grants access to the user logged in at the seat, which is\n");
        text.Append("# the mechanism native/vendor/Heimdall/heimdall/60-heimdall.rules uses. No group and no\n");
        text.Append("# world-writable mode: plugdev does not exist on every distribution, and\n");
        text.Append("# MODE=\"0666\" would make the raw USB node writable by every local process.\n");
        text.Append("#\n");
        text.Append("# Generated by OdinCore.Usb.SamsungIds.UdevRulesText(). Do not hand-edit:\n");
        text.Append("# the ids come from native/vendor/Heimdall/heimdall/source/BridgeManager.h:71,76-78.\n");
        text.Append('\n');

        foreach (ushort pid in ProductIds)
        {
            text.Append("SUBSYSTEM==\"usb\", ENV{DEVTYPE}==\"usb_device\", ATTR{idVendor}==\"");
            text.Append(VendorId.ToString("x4", CultureInfo.InvariantCulture));
            text.Append("\", ATTR{idProduct}==\"");
            text.Append(pid.ToString("x4", CultureInfo.InvariantCulture));
            text.Append("\", TAG+=\"uaccess\"\n");
        }

        return text.ToString();
    }
}
