// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (the descriptor dump at
//                                                lines 139-229)
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace OdinCore.Usb;

/// <summary>
/// Whether the process can claim this device. Three states, never two: "present
/// but not claimable" has to be sayable, with a reason.
/// </summary>
public enum UsbAccessState
{
    /// <summary>Not determined yet.</summary>
    Unknown,

    /// <summary>Nothing stands in the way of a claim as far as can be told without claiming.</summary>
    Claimable,

    /// <summary>The device node exists but this user may not open it.</summary>
    PermissionDenied,

    /// <summary>A kernel driver holds the interface. Linux only.</summary>
    DriverBound,

    /// <summary>The device is gone.</summary>
    NotPresent
}

/// <summary>enum libusb_speed (libusb.h:1270-1290).</summary>
public enum UsbSpeed
{
    Unknown = 0,      // libusb.h:1272
    Low = 1,          // libusb.h:1275
    Full = 2,         // libusb.h:1278
    High = 3,         // libusb.h:1281
    Super = 4,        // libusb.h:1284
    SuperPlus = 5,    // libusb.h:1287
    SuperPlusX2 = 6   // libusb.h:1290
}

/// <summary>
/// What an enumeration found, as a single typed value. The user interface turns
/// exactly one of these into exactly one status word, so a device that is
/// present but unusable can never be shown as ready.
/// </summary>
public enum UsbDiagnostic
{
    /// <summary>libusb-1.0 is not installed or could not be loaded.</summary>
    LibUsbUnavailable,

    /// <summary>libusb loaded but refused to initialise, or enumeration failed.</summary>
    LibUsbFailed,

    /// <summary>libusb works, no Samsung device of any kind is attached.</summary>
    NoDevice,

    /// <summary>A Samsung device is attached but its product id is not a download-mode id.</summary>
    SamsungNotInDownloadMode,

    /// <summary>A download-mode device is attached but it cannot be claimed.</summary>
    DownloadModeNotClaimable,

    /// <summary>A download-mode device is attached and nothing stands in the way of a claim.</summary>
    DownloadModeReady
}

/// <summary>
/// One endpoint of one alternate setting. Upstream prints the same three fields
/// at BridgeManager.cpp:206-208.
/// </summary>
public sealed record UsbEndpointInfo(byte Address, byte Attributes, ushort MaxPacketSize,
                                     byte Interval, bool IsInput)
{
    public string AddressText => "0x" + Address.ToString("X2", CultureInfo.InvariantCulture);

    public override string ToString() =>
        AddressText + " " + (IsInput ? "IN" : "OUT") +
        " max packet " + MaxPacketSize.ToString(CultureInfo.InvariantCulture) +
        " interval " + Interval.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One interface and alternate setting. InterfaceIndex and AltSettingIndex are
/// the array indices upstream iterates (BridgeManager.cpp:183-185);
/// InterfaceNumber and AlternateSetting are the values the descriptor reports.
/// Both are kept because upstream claims the array index, not the descriptor's
/// interface number: see DownloadInterface.
/// </summary>
public sealed record UsbInterfaceInfo(int InterfaceIndex, int AltSettingIndex, byte InterfaceNumber,
                                      byte AlternateSetting, byte Class, byte SubClass, byte Protocol,
                                      IReadOnlyList<UsbEndpointInfo> Endpoints)
{
    /// <summary>"0A.00.00", the form upstream prints at BridgeManager.cpp:191-194.</summary>
    public string ClassText =>
        Class.ToString("X2", CultureInfo.InvariantCulture) + "." +
        SubClass.ToString("X2", CultureInfo.InvariantCulture) + "." +
        Protocol.ToString("X2", CultureInfo.InvariantCulture);

    public override string ToString() =>
        "interface[" + InterfaceIndex + "].altsetting[" + AltSettingIndex + "] number " +
        InterfaceNumber + " class " + ClassText + " endpoints " + Endpoints.Count;
}

/// <summary>
/// The interface the Odin protocol runs over, selected by the rule at
/// BridgeManager.cpp:217-227: the first interface and alternate setting with
/// exactly two endpoints, interface class CDC-Data, and both an IN and an OUT
/// endpoint present.
/// </summary>
/// <param name="InterfaceIndex">
/// The array index the engine claims (BridgeManager.cpp:223, :246), which is
/// not necessarily bInterfaceNumber. Both are reported so a mismatch is
/// visible. Nothing in managed code claims an interface.
/// </param>
/// <param name="AltSettingIndex">Array index; the engine skips the set-alt call when 0. BridgeManager.cpp:276.</param>
public sealed record DownloadInterface(int InterfaceIndex, int AltSettingIndex, byte InterfaceNumber,
                                       byte AlternateSetting, byte InEndpoint, byte OutEndpoint)
{
    /// <summary>
    /// True when the array index upstream claims differs from the interface
    /// number the descriptor reports. For a typical download-mode configuration
    /// they are equal.
    /// </summary>
    public bool IndexDiffersFromNumber => InterfaceIndex != InterfaceNumber;

    public override string ToString() =>
        "interface index " + InterfaceIndex + " (number " + InterfaceNumber + "), alt setting " +
        AltSettingIndex + ", IN 0x" + InEndpoint.ToString("X2", CultureInfo.InvariantCulture) +
        ", OUT 0x" + OutEndpoint.ToString("X2", CultureInfo.InvariantCulture);
}

/// <summary>
/// An immutable snapshot of one USB device, built entirely from descriptors.
/// </summary>
public sealed record UsbDeviceRecord(
    UsbId Id, byte BusNumber, byte DeviceAddress,
    ushort BcdUsb, ushort BcdDevice,
    byte DeviceClass, byte DeviceSubClass, byte DeviceProtocol, byte MaxPacketSize0,
    byte ConfigurationCount, UsbSpeed Speed,
    string? Manufacturer, string? Product, string? SerialNumber,
    IReadOnlyList<UsbInterfaceInfo> Interfaces,
    DownloadInterface? Download,
    UsbAccessState Access, string? AccessDetail,
    string? BoundKernelDriver)
{
    /// <summary>A stable key for this attachment: "003/005".</summary>
    public string Key => string.Format(CultureInfo.InvariantCulture, "{0:D3}/{1:D3}", BusNumber, DeviceAddress);

    /// <summary>True when the vendor and product ids are a download-mode identity.</summary>
    public bool IsDownloadMode => SamsungIds.IsDownloadMode(Id);

    /// <summary>True for any device with Samsung's vendor id.</summary>
    public bool IsSamsung => SamsungIds.IsSamsung(Id);

    /// <summary>
    /// The one-line form the device list shows:
    /// "04E8:685D SAMSUNG bus 0 addr 5". Nothing in it is inferred.
    /// </summary>
    public string DisplayName
    {
        get
        {
            string name = Id.ToString();

            if (!string.IsNullOrWhiteSpace(Manufacturer))
                name += " " + Manufacturer!.Trim();
            else if (!string.IsNullOrWhiteSpace(Product))
                name += " " + Product!.Trim();

            return name + " bus " + BusNumber.ToString(CultureInfo.InvariantCulture) +
                   " addr " + DeviceAddress.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The status word, as a word and never as a colour:
    /// "download mode, claimable", "download mode, permission denied",
    /// "download mode, kernel driver bound", "present, not in download mode".
    /// </summary>
    public string StatusWord
    {
        get
        {
            string mode = IsDownloadMode ? "download mode" : "present, not in download mode";

            return Access switch
            {
                UsbAccessState.Claimable => mode + ", claimable",
                UsbAccessState.PermissionDenied => mode + ", permission denied",
                UsbAccessState.DriverBound => mode + ", kernel driver bound" +
                    (BoundKernelDriver is null ? "" : " (" + BoundKernelDriver + ")"),
                UsbAccessState.NotPresent => "not connected",
                _ => mode + ", access unknown"
            };
        }
    }

    public override string ToString() => DisplayName + " - " + StatusWord;
}

/// <summary>
/// The result of one enumeration pass.
/// </summary>
/// <param name="DownloadModeDevices">Every attached device whose identity is a download-mode identity.</param>
/// <param name="OtherSamsungDevices">
/// Samsung devices that are attached but not in download mode, so that "a
/// Samsung phone is attached but it is in normal mode" can be said out loud
/// instead of being reported as something flashable.
/// </param>
/// <param name="LibUsb">Whether libusb loaded, and from where.</param>
/// <param name="Kind">The typed diagnosis; the UI renders exactly one status word from it.</param>
/// <param name="Diagnostic">The same diagnosis as one plain sentence, including any detail.</param>
public sealed record UsbEnumerationResult(
    IReadOnlyList<UsbDeviceRecord> DownloadModeDevices,
    IReadOnlyList<UsbDeviceRecord> OtherSamsungDevices,
    LibUsbStatus LibUsb,
    UsbDiagnostic Kind,
    string? Diagnostic)
{
    /// <summary>True when at least one download-mode device looks claimable.</summary>
    public bool HasClaimableDevice
    {
        get
        {
            foreach (UsbDeviceRecord device in DownloadModeDevices)
                if (device.Access == UsbAccessState.Claimable)
                    return true;

            return false;
        }
    }
}
