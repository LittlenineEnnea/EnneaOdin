// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is not derived from Heimdall. The declarations below transcribe the
// C API of libusb 1.0 from /opt/homebrew/include/libusb-1.0/libusb.h as shipped
// by libusb 1.0.30 (LIBUSB_API_VERSION 0x0100010C). libusb is Copyright (c) its
// authors and is distributed under the LGPL-2.1-or-later; it is used here as an
// unmodified system shared library and no libusb source is copied into this
// repository. Each citation below of the form "libusb.h:NNN" refers to that
// header; each citation of the form "BridgeManager.cpp:NNN" names the call site
// in the vendored Heimdall that the import exists to serve.
//
// This is the ONLY file in the solution that declares a DllImport.
// Reviewer check: grep -rn "DllImport" OdinCore EnneaOdin

using System.Runtime.InteropServices;

namespace OdinCore.Interop;

/// <summary>
/// The complete P/Invoke surface for libusb-1.0, plus the libusb constants
/// EnneaOdin needs. Every entry point is cdecl: LIBUSB_CALL expands to nothing
/// on non-Windows platforms (libusb.h:122), and this program targets macOS and
/// Linux only.
/// </summary>
internal static class LibUsb
{
    /// <summary>
    /// Logical name every import is declared against. It is never a filename on
    /// disk; LibUsbLoader resolves it per platform through
    /// NativeLibrary.SetDllImportResolver.
    /// </summary>
    internal const string Library = LibUsbLoader.LibraryName;

    // ---------------------------------------------------------------------
    // enum libusb_error (libusb.h:1300-1344)
    // ---------------------------------------------------------------------

    internal const int LIBUSB_SUCCESS = 0;              // libusb.h:1302
    internal const int LIBUSB_ERROR_IO = -1;            // libusb.h:1305
    internal const int LIBUSB_ERROR_INVALID_PARAM = -2; // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_ACCESS = -3;        // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_NO_DEVICE = -4;     // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_NOT_FOUND = -5;     // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_BUSY = -6;          // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_TIMEOUT = -7;       // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_OVERFLOW = -8;      // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_PIPE = -9;          // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_INTERRUPTED = -10;  // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_NO_MEM = -11;       // libusb.h:1300-1344
    internal const int LIBUSB_ERROR_NOT_SUPPORTED = -12;// libusb.h:1300-1344
    internal const int LIBUSB_ERROR_OTHER = -99;        // libusb.h:1344

    // ---------------------------------------------------------------------
    // enum libusb_log_level (libusb.h:1563-1577). Passed to libusb_set_debug,
    // which is the non-variadic form of
    // libusb_set_option(ctx, LIBUSB_OPTION_LOG_LEVEL, level) that upstream uses
    // at BridgeManager.cpp:1249-1266. See LibUsbLoader for why the variadic
    // libusb_set_option is deliberately not imported.
    // ---------------------------------------------------------------------

    internal const int LIBUSB_LOG_LEVEL_NONE = 0;    // libusb.h:1565
    internal const int LIBUSB_LOG_LEVEL_ERROR = 1;   // libusb.h:1568
    internal const int LIBUSB_LOG_LEVEL_WARNING = 2; // libusb.h:1571
    internal const int LIBUSB_LOG_LEVEL_INFO = 3;    // libusb.h:1574
    internal const int LIBUSB_LOG_LEVEL_DEBUG = 4;   // libusb.h:1577

    /// <summary>Route log messages of one context to our callback (libusb.h:1592).</summary>
    internal const int LIBUSB_LOG_CB_CONTEXT = 1 << 1; // libusb.h:1592

    // ---------------------------------------------------------------------
    // enum libusb_speed (libusb.h:1270-1290)
    // ---------------------------------------------------------------------

    internal const int LIBUSB_SPEED_UNKNOWN = 0;       // libusb.h:1272
    internal const int LIBUSB_SPEED_LOW = 1;           // libusb.h:1275
    internal const int LIBUSB_SPEED_FULL = 2;          // libusb.h:1278
    internal const int LIBUSB_SPEED_HIGH = 3;          // libusb.h:1281
    internal const int LIBUSB_SPEED_SUPER = 4;         // libusb.h:1284
    internal const int LIBUSB_SPEED_SUPER_PLUS = 5;    // libusb.h:1287
    internal const int LIBUSB_SPEED_SUPER_PLUS_X2 = 6; // libusb.h:1290

    /// <summary>
    /// Direction bit of bEndpointAddress (libusb.h:384). Upstream tests exactly
    /// this bit to tell the IN endpoint from the OUT endpoint, at
    /// BridgeManager.cpp:211.
    /// </summary>
    internal const byte LIBUSB_ENDPOINT_IN = 0x80; // libusb.h:384

    // ---------------------------------------------------------------------
    // Context lifetime
    // ---------------------------------------------------------------------

    /// <summary>libusb_init: BridgeManager.cpp:421 (detect), :474 (open).</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern int libusb_init(out IntPtr ctx);

    /// <summary>libusb_exit: BridgeManager.cpp:415.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern void libusb_exit(IntPtr ctx);

    /// <summary>
    /// libusb_set_debug (libusb.h:1722). Deliberate deviation: upstream calls
    /// the variadic libusb_set_option(ctx, LIBUSB_OPTION_LOG_LEVEL, level) at
    /// BridgeManager.cpp:1249-1266. libusb_set_option is variadic in C, and on
    /// Apple arm64 the variadic ABI passes arguments on the stack while a plain
    /// non-variadic P/Invoke passes them in registers, so importing it directly
    /// would be wrong by construction. libusb implements that option as exactly
    /// this call, with the same LIBUSB_LOG_LEVEL_* values.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern void libusb_set_debug(IntPtr ctx, int level);

    /// <summary>libusb_get_version (libusb.h, const struct libusb_version *).</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern IntPtr libusb_get_version();

    // ---------------------------------------------------------------------
    // Device list and descriptors
    // ---------------------------------------------------------------------

    /// <summary>
    /// libusb_get_device_list: BridgeManager.cpp:91 (FindDeviceInterface),
    /// :440 (DetectDevice). Returns ssize_t, hence nint.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern nint libusb_get_device_list(IntPtr ctx, out IntPtr list);

    /// <summary>libusb_free_device_list: BridgeManager.cpp:111.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern void libusb_free_device_list(IntPtr list, int unrefDevices);

    /// <summary>libusb_ref_device: BridgeManager.cpp:103.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern IntPtr libusb_ref_device(IntPtr dev);

    /// <summary>libusb_unref_device: BridgeManager.cpp:412.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern void libusb_unref_device(IntPtr dev);

    /// <summary>libusb_get_device_descriptor: BridgeManager.cpp:95, :132.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern int libusb_get_device_descriptor(IntPtr dev, out libusb_device_descriptor descriptor);

    /// <summary>
    /// libusb_get_config_descriptor: BridgeManager.cpp:172, always with
    /// config_index 0 (the first configuration, not the active one).
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern int libusb_get_config_descriptor(IntPtr dev, byte configIndex, out IntPtr config);

    /// <summary>libusb_free_config_descriptor: BridgeManager.cpp:231.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern void libusb_free_config_descriptor(IntPtr config);

    /// <summary>libusb_get_bus_number: stable device key, and the Linux usbfs node path.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern byte libusb_get_bus_number(IntPtr dev);

    /// <summary>libusb_get_device_address: stable device key, and the Linux usbfs node path.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern byte libusb_get_device_address(IntPtr dev);

    /// <summary>libusb_get_device_speed: negotiated link speed for the info panel.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern int libusb_get_device_speed(IntPtr dev);

    // ---------------------------------------------------------------------
    // Handles and interfaces
    // ---------------------------------------------------------------------

    /// <summary>libusb_open: BridgeManager.cpp:124.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern int libusb_open(IntPtr dev, out IntPtr handle);

    /// <summary>libusb_close: BridgeManager.cpp:409.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern void libusb_close(IntPtr handle);

    /// <summary>
    /// libusb_get_string_descriptor_ascii: BridgeManager.cpp:143, :149, :155.
    /// Upstream uses a 128-byte buffer there; this port uses 256.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern int libusb_get_string_descriptor_ascii(IntPtr handle, byte descIndex, byte[] data, int length);

    // ---------------------------------------------------------------------
    // Claiming and transfers: deliberately absent
    // ---------------------------------------------------------------------
    //
    // libusb_claim_interface, libusb_release_interface,
    // libusb_set_interface_alt_setting, libusb_kernel_driver_active,
    // libusb_detach_kernel_driver, libusb_attach_kernel_driver,
    // libusb_reset_device, libusb_bulk_transfer and libusb_control_transfer
    // used to be imported here, for a managed transport that claimed the
    // download-mode interface and sent the Odin packets itself.
    //
    // The protocol now runs in the native engine, which owns its own libusb
    // context and does its own claiming (BridgeManager.cpp:246-281). They are
    // not imported any more, so this binding CANNOT claim an interface or move
    // a byte to a device: two libusb users in one process that could both
    // claim the same interface would be a real hazard, and removing the
    // imports removes it by construction rather than by convention. This
    // binding enumerates, reads descriptors and reports access state.

    // ---------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------

    /// <summary>libusb_error_name: the short symbolic name, e.g. LIBUSB_ERROR_ACCESS.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern IntPtr libusb_error_name(int code);

    /// <summary>libusb_strerror: the human-readable message for an error code.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    internal static extern IntPtr libusb_strerror(int code);

    /// <summary>
    /// libusb_log_cb (libusb.h:1702-1703). Held in a static field by
    /// LibUsbContext so the delegate is not collected while libusb holds it.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void libusb_log_cb(IntPtr ctx, int level, IntPtr str);

    /// <summary>
    /// libusb_set_log_cb (libusb.h:1724), available since libusb 1.0.23. Bound
    /// softly through NativeLibrary.TryGetExport rather than DllImport, so an
    /// older libusb cannot fault the process at first call.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void libusb_set_log_cb_fn(IntPtr ctx, libusb_log_cb? cb, int mode);

    // ---------------------------------------------------------------------
    // Helpers over the raw imports
    // ---------------------------------------------------------------------

    /// <summary>libusb_error_name as a managed string, with a numeric fallback.</summary>
    internal static string ErrorName(int code)
    {
        try
        {
            string? name = Marshal.PtrToStringAnsi(libusb_error_name(code));
            return string.IsNullOrEmpty(name) ? "libusb error " + code : name;
        }
        catch (DllNotFoundException)
        {
            return "libusb error " + code;
        }
        catch (EntryPointNotFoundException)
        {
            return "libusb error " + code;
        }
    }

    /// <summary>libusb_strerror as a managed string, with a numeric fallback.</summary>
    internal static string ErrorText(int code)
    {
        try
        {
            string? text = Marshal.PtrToStringAnsi(libusb_strerror(code));
            return string.IsNullOrEmpty(text) ? "libusb error " + code : text;
        }
        catch (DllNotFoundException)
        {
            return "libusb error " + code;
        }
        catch (EntryPointNotFoundException)
        {
            return "libusb error " + code;
        }
    }

    /// <summary>"LIBUSB_ERROR_ACCESS (-3): Access denied (insufficient permissions)".</summary>
    internal static string Describe(int code) => ErrorName(code) + " (" + code + "): " + ErrorText(code);
}

// -------------------------------------------------------------------------
// Descriptor structs. LayoutKind.Sequential with default packing is correct on
// macOS and Linux for both arm64 and x64; the sizes in each comment were
// measured with Marshal.SizeOf<T>() in a throwaway console probe on macOS
// arm64 against libusb 1.0.30, and libusb_device_descriptor's 18 bytes matches
// the C layout exactly. Array strides must always come from Marshal.SizeOf<T>(),
// never from these literals.
// -------------------------------------------------------------------------

/// <summary>struct libusb_device_descriptor (libusb.h). Measured size: 18 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct libusb_device_descriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public ushort bcdUSB;
    public byte bDeviceClass;
    public byte bDeviceSubClass;
    public byte bDeviceProtocol;
    public byte bMaxPacketSize0;
    public ushort idVendor;
    public ushort idProduct;
    public ushort bcdDevice;
    public byte iManufacturer;
    public byte iProduct;
    public byte iSerialNumber;
    public byte bNumConfigurations;
}

/// <summary>struct libusb_config_descriptor (libusb.h). Measured size: 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct libusb_config_descriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public ushort wTotalLength;
    public byte bNumInterfaces;
    public byte bConfigurationValue;
    public byte iConfiguration;
    public byte bmAttributes;
    public byte MaxPower;
    public IntPtr @interface;   // const struct libusb_interface *
    public IntPtr extra;
    public int extra_length;
}

/// <summary>struct libusb_interface (libusb.h). Measured size: 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct libusb_interface
{
    public IntPtr altsetting;   // const struct libusb_interface_descriptor *
    public int num_altsetting;
}

/// <summary>struct libusb_interface_descriptor (libusb.h). Measured size: 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct libusb_interface_descriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public byte bInterfaceNumber;
    public byte bAlternateSetting;
    public byte bNumEndpoints;
    public byte bInterfaceClass;
    public byte bInterfaceSubClass;
    public byte bInterfaceProtocol;
    public byte iInterface;
    public IntPtr endpoint;     // const struct libusb_endpoint_descriptor *
    public IntPtr extra;
    public int extra_length;
}

/// <summary>struct libusb_endpoint_descriptor (libusb.h). Measured size: 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct libusb_endpoint_descriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public byte bEndpointAddress;
    public byte bmAttributes;
    public ushort wMaxPacketSize;
    public byte bInterval;
    public byte bRefresh;
    public byte bSynchAddress;
    public IntPtr extra;
    public int extra_length;
}

/// <summary>struct libusb_version (libusb.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct libusb_version
{
    public ushort major;
    public ushort minor;
    public ushort micro;
    public ushort nano;
    public IntPtr rc;
    public IntPtr describe;
}
