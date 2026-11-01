// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (FindDeviceInterface, lines
//                                                131-240: the descriptor dump,
//                                                the string-descriptor reads and
//                                                the download-interface
//                                                selection rule)
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using OdinCore.Diagnostics;
using OdinCore.Interop;

namespace OdinCore.Usb;

/// <summary>
/// Reads every descriptor EnneaOdin shows or needs, from one libusb_device, and
/// applies the download-interface selection rule of BridgeManager.cpp:217-227.
/// </summary>
internal sealed class UsbDescriptorReader
{
    private const int StringBufferLength = 256;   // upstream uses 128 at BridgeManager.cpp:141

    private readonly IProgress<OdinEvent>? _log;

    internal UsbDescriptorReader(IProgress<OdinEvent>? log) => _log = log;

    private void Log(OdinLevel level, string message) =>
        _log?.Report(new OdinEvent(DateTimeOffset.Now, level, message, "usb"));

    internal UsbDeviceRecord BuildRecord(IntPtr device, libusb_device_descriptor descriptor, UsbId id,
                                        bool readStrings)
    {
        byte bus = LibUsb.libusb_get_bus_number(device);
        byte address = LibUsb.libusb_get_device_address(device);
        UsbSpeed speed = MapSpeed(LibUsb.libusb_get_device_speed(device));

        (IReadOnlyList<UsbInterfaceInfo> interfaces, DownloadInterface? download) =
            ReadInterfaces(device, id, bus, address);

        string? manufacturer = null;
        string? product = null;
        string? serial = null;
        UsbAccessState accessOverride = UsbAccessState.Unknown;
        string? accessOverrideDetail = null;

        // NOT ON macOS FOR A DOWNLOAD-MODE DEVICE, whatever the caller asked.
        //
        // Reading these three strings means libusb_open, and on macOS any
        // libusb touch of this device leaves the kernel driver's own pipes
        // broken until it re-enumerates - which breaks the serial node the
        // protocol has to use there, because libusb cannot take the interface
        // on macOS at all (measured as root, with auto-detach, in one handle:
        // the claim succeeds, the driver stays, every transfer times out).
        //
        // The cost is the manufacturer, product and serial strings for a
        // handset in download mode on macOS. The alternative was that opening
        // the application and plugging the handset in made it unflashable,
        // because the automatic descriptor read ran on its own and broke the
        // path before anybody pressed anything.
        bool wouldBreakTheSerialPath = UsbPlatform.IsMacOS && download is not null;

        if (readStrings && wouldBreakTheSerialPath)
        {
            Log(OdinLevel.Debug, "Not opening " + id + " to read its string descriptors: on " +
                "macOS that breaks the serial node the protocol needs.");
        }

        if (readStrings && !wouldBreakTheSerialPath)
        {
            int result = LibUsb.libusb_open(device, out IntPtr handle);   // BridgeManager.cpp:124

            if (result == LibUsb.LIBUSB_SUCCESS)
            {
                try
                {
                    manufacturer = ReadString(handle, descriptor.iManufacturer);  // BridgeManager.cpp:143
                    product = ReadString(handle, descriptor.iProduct);            // BridgeManager.cpp:149
                    serial = ReadString(handle, descriptor.iSerialNumber);        // BridgeManager.cpp:155
                }
                finally
                {
                    LibUsb.libusb_close(handle);   // BridgeManager.cpp:409
                }
            }
            else
            {
                // This is direct evidence about access, better than any guess
                // from the filesystem, so it wins over UsbAccess.Inspect.
                Log(OdinLevel.Warning, "Could not open " + id + " on bus " + bus + " address " + address +
                    " to read its string descriptors: " + LibUsb.Describe(result));

                if (result == LibUsb.LIBUSB_ERROR_ACCESS)
                {
                    accessOverride = UsbAccessState.PermissionDenied;
                    accessOverrideDetail = "libusb_open returned " + LibUsb.ErrorName(result) + ". " +
                        (UsbPlatform.IsLinux
                            ? UsbAccess.LinuxRulesInstallHint()
                            : "Check that no other program holds the device.");
                }
                else if (result == LibUsb.LIBUSB_ERROR_NO_DEVICE)
                {
                    accessOverride = UsbAccessState.NotPresent;
                    accessOverrideDetail = "The device disappeared while it was being read.";
                }
            }
        }

        var record = new UsbDeviceRecord(
            id, bus, address,
            descriptor.bcdUSB, descriptor.bcdDevice,
            descriptor.bDeviceClass, descriptor.bDeviceSubClass, descriptor.bDeviceProtocol,
            descriptor.bMaxPacketSize0,
            descriptor.bNumConfigurations, speed,
            manufacturer, product, serial,
            interfaces, download,
            UsbAccessState.Unknown, null, null);

        if (accessOverride != UsbAccessState.Unknown)
        {
            return record with
            {
                Access = accessOverride,
                AccessDetail = accessOverrideDetail,
                BoundKernelDriver = UsbAccess.FindBoundDriver(record)
            };
        }

        (UsbAccessState state, string? detail, string? driver) = UsbAccess.Inspect(record);
        return record with { Access = state, AccessDetail = detail, BoundKernelDriver = driver };
    }

    /// <summary>
    /// Reads configuration descriptor 0 and walks every interface, alternate
    /// setting and endpoint, then applies upstream's download-interface
    /// selection rule.
    /// </summary>
    internal (IReadOnlyList<UsbInterfaceInfo>, DownloadInterface?) ReadInterfaces(IntPtr device, UsbId id,
                                                                                 byte bus, byte address)
    {
        var interfaces = new List<UsbInterfaceInfo>();
        DownloadInterface? download = null;

        // Index 0, the first configuration and not necessarily the active one,
        // exactly as upstream: BridgeManager.cpp:172.
        int result = LibUsb.libusb_get_config_descriptor(device, SamsungIds.ConfigDescriptorIndex,
            out IntPtr configPointer);

        if (result != LibUsb.LIBUSB_SUCCESS || configPointer == IntPtr.Zero)
        {
            Log(OdinLevel.Warning, "Failed to retrieve config descriptor for " + id + " on bus " + bus +
                " address " + address + ": " + LibUsb.Describe(result));
            return (interfaces, null);
        }

        try
        {
            libusb_config_descriptor config = Marshal.PtrToStructure<libusb_config_descriptor>(configPointer);

            int interfaceStride = Marshal.SizeOf<libusb_interface>();
            int altStride = Marshal.SizeOf<libusb_interface_descriptor>();
            int endpointStride = Marshal.SizeOf<libusb_endpoint_descriptor>();

            for (int i = 0; i < config.bNumInterfaces; i++)   // BridgeManager.cpp:183
            {
                if (config.@interface == IntPtr.Zero)
                    break;

                libusb_interface usbInterface =
                    Marshal.PtrToStructure<libusb_interface>(IntPtr.Add(config.@interface, i * interfaceStride));

                for (int j = 0; j < usbInterface.num_altsetting; j++)   // BridgeManager.cpp:185
                {
                    if (usbInterface.altsetting == IntPtr.Zero)
                        break;

                    libusb_interface_descriptor alt =
                        Marshal.PtrToStructure<libusb_interface_descriptor>(
                            IntPtr.Add(usbInterface.altsetting, j * altStride));

                    var endpoints = new List<UsbEndpointInfo>(alt.bNumEndpoints);
                    int inEndpoint = -1;
                    int outEndpoint = -1;

                    for (int k = 0; k < alt.bNumEndpoints; k++)   // BridgeManager.cpp:200
                    {
                        if (alt.endpoint == IntPtr.Zero)
                            break;

                        libusb_endpoint_descriptor endpoint =
                            Marshal.PtrToStructure<libusb_endpoint_descriptor>(
                                IntPtr.Add(alt.endpoint, k * endpointStride));

                        bool isInput = (endpoint.bEndpointAddress & LibUsb.LIBUSB_ENDPOINT_IN) != 0;

                        endpoints.Add(new UsbEndpointInfo(endpoint.bEndpointAddress, endpoint.bmAttributes,
                            endpoint.wMaxPacketSize, endpoint.bInterval, isInput));

                        // BridgeManager.cpp:211-214: the direction bit decides,
                        // and a later endpoint of the same direction overwrites
                        // an earlier one.
                        if (isInput)
                            inEndpoint = endpoint.bEndpointAddress;
                        else
                            outEndpoint = endpoint.bEndpointAddress;
                    }

                    interfaces.Add(new UsbInterfaceInfo(i, j, alt.bInterfaceNumber, alt.bAlternateSetting,
                        alt.bInterfaceClass, alt.bInterfaceSubClass, alt.bInterfaceProtocol, endpoints));

                    // The selection rule, BridgeManager.cpp:217-227. The
                    // interfaceIndex < 0 guard makes it first-wins, so a later
                    // matching interface never replaces an earlier one.
                    if (download is null
                        && alt.bNumEndpoints == SamsungIds.RequiredEndpointCount
                        && alt.bInterfaceClass == SamsungIds.CdcDataInterfaceClass
                        && inEndpoint != -1
                        && outEndpoint != -1)
                    {
                        download = new DownloadInterface(i, j, alt.bInterfaceNumber, alt.bAlternateSetting,
                            (byte)inEndpoint, (byte)outEndpoint);
                    }
                }
            }
        }
        finally
        {
            LibUsb.libusb_free_config_descriptor(configPointer);   // BridgeManager.cpp:231
        }

        if (download is null)
        {
            if (SamsungIds.IsDownloadMode(id))
            {
                Log(OdinLevel.Warning, "Device " + id + " on bus " + bus + " address " + address +
                    " has no interface with two endpoints and class " +
                    SamsungIds.CdcDataInterfaceClass.ToString("X2", CultureInfo.InvariantCulture) +
                    " (CDC-Data), so the Odin protocol cannot run over it.");
            }
        }
        else if (download.IndexDiffersFromNumber)
        {
            // Heimdall claims the array index, not bInterfaceNumber
            // (BridgeManager.cpp:223 then :246). This port does the same so the
            // behaviour is identical, but says so when the two differ instead of
            // being silently wrong.
            Log(OdinLevel.Warning, "The download interface's array index (" + download.InterfaceIndex +
                ") differs from its bInterfaceNumber (" + download.InterfaceNumber +
                "). Heimdall claims the array index and EnneaOdin does the same; " +
                "report this device if claiming fails.");
        }

        return (interfaces, download);
    }

    /// <summary>
    /// One string descriptor, or null when there is none.
    /// </summary>
    internal string? ReadString(IntPtr handle, byte index)
    {
        if (index == 0)
            return null;

        byte[] buffer = new byte[StringBufferLength];
        int length = LibUsb.libusb_get_string_descriptor_ascii(handle, index, buffer, buffer.Length);

        if (length < 0)
        {
            Log(OdinLevel.Debug, "String descriptor " + index + " could not be read: " +
                LibUsb.Describe(length));
            return null;
        }

        if (length == 0)
            return null;

        string text = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Min(length, buffer.Length));
        text = text.TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    internal static UsbSpeed MapSpeed(int speed) => speed switch
    {
        LibUsb.LIBUSB_SPEED_LOW => UsbSpeed.Low,
        LibUsb.LIBUSB_SPEED_FULL => UsbSpeed.Full,
        LibUsb.LIBUSB_SPEED_HIGH => UsbSpeed.High,
        LibUsb.LIBUSB_SPEED_SUPER => UsbSpeed.Super,
        LibUsb.LIBUSB_SPEED_SUPER_PLUS => UsbSpeed.SuperPlus,
        LibUsb.LIBUSB_SPEED_SUPER_PLUS_X2 => UsbSpeed.SuperPlusX2,
        _ => UsbSpeed.Unknown
    };
}
