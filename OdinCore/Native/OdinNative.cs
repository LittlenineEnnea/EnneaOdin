// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is the managed declaration of the C ABI in
//   native/include/odin_bridge.h
// Behind that ABI is Heimdall's C++, compiled into libenneaodin and loaded
// into this process. Nothing here reimplements the protocol; every entry point
// below lands in native/src/odin_bridge.cpp, which drives
// native/vendor/Heimdall/heimdall/source/BridgeManager.cpp.
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is distributed under the MIT licence. The copyright notices above
// and the permission notice in native/vendor/Heimdall/LICENSE are retained as
// that licence requires.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace OdinCore.Native;

/// <summary>
/// odin_status, odin_bridge.h:92-137. Every entry point returns one of these
/// and no entry point throws: the native side wraps each call in
/// ODIN_TRY / ODIN_CATCH so a C++ exception becomes
/// <see cref="OutOfMemory"/> or <see cref="InternalError"/> instead of
/// crossing the ABI.
/// </summary>
internal enum OdinStatus
{
    Ok = 0,
    NoDevice = 1,
    Failed = 2,
    Cancelled = 3,
    InvalidArgument = 4,
    InvalidState = 5,
    BufferTooSmall = 6,
    Unsupported = 7,
    OutOfMemory = 8,
    InternalError = 9
}

/// <summary>odin_destination, odin_bridge.h:200-204.</summary>
internal enum OdinDestination
{
    Phone = 0,
    Modem = 1
}

/// <summary>
/// One line of Heimdall's own printed output, already split on newlines by
/// the native side's capture. The pointer is owned by the caller in native
/// code and is valid only for the duration of the callback, so the managed
/// handler must copy it before returning.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void OdinLogFn(IntPtr line, IntPtr user);

/// <summary>
/// Byte counts from inside BridgeManager::SendFile's part loop. These are real
/// counts, not a percentage scraped back out of Heimdall's terminal output.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void OdinProgressFn(ulong transferred, ulong total, IntPtr user);

/// <summary>
/// Reads at most <paramref name="count"/> bytes. Returns the number read, 0 at
/// end of stream, or a negative value on error. Must not throw: the native
/// side is C++ with no handler for a managed exception at this point.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long OdinStreamReadFn(IntPtr user, IntPtr buffer, ulong count);

/// <summary>Seeks to an absolute offset. Returns 0 on success.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int OdinStreamSeekFn(IntPtr user, ulong offset);

/// <summary>odin_callbacks, odin_bridge.h:173-178. Blittable; three pointers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OdinCallbacks
{
    public IntPtr Log;
    public IntPtr Progress;
    public IntPtr User;
}

/// <summary>odin_stream, odin_bridge.h:484-496. Seek may be null.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OdinStream
{
    public IntPtr Read;
    public IntPtr Seek;
    public IntPtr User;
}

/// <summary>odin_session_geometry, added in ABI 2.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OdinSessionGeometry
{
    public uint FilePartSize;
    public uint SequenceMaxLength;
    public uint SequenceTimeoutMs;

    /// <summary>
    /// What the handset declared in the begin-session response. Added in
    /// ABI 3; before that the value was a local variable inside Heimdall's
    /// BeginSession and the managed side had to infer it.
    /// </summary>
    public uint DeviceDefaultPartSize;
}

/// <summary>
/// The whole imported surface of libenneaodin, in one place.
/// </summary>
internal static class OdinNative
{
    /// <summary>
    /// The name every DllImport below uses. It is not a filename: the resolver
    /// in <see cref="OdinNativeLoader"/> turns it into one.
    /// </summary>
    internal const string Library = "enneaodin";

    /// <summary>
    /// The ABI this managed code is written against. A library reporting
    /// anything else is refused rather than called: the structs and the
    /// argument lists are what would be wrong.
    /// </summary>
    /// <summary>
    /// 3: odin_session_geometry gained DeviceDefaultPartSize, so the struct
    /// grew and a mismatched pair would read one field past a shorter native
    /// struct. The loader refuses to use a library whose odin_abi_version
    /// does not match this exactly, which is the whole point of the number.
    /// </summary>
    internal const int ExpectedAbiVersion = 5;

    /// <summary>odin_bridge.h:351. A PIT is a few kilobytes; this handset's is 7412 bytes.</summary>
    internal const uint PitMaxSize = 65536;

    /// <summary>odin_bridge.h:207. The identifier a modem transfer must carry.</summary>
    internal const uint NoFileIdentifier = 0xFFFFFFFF;

    // ---- identity ---------------------------------------------------------
    //
    // Returned char pointers are static storage owned by the library. They are
    // taken as IntPtr and copied with Marshal.PtrToStringUTF8: declaring the
    // return type as string would hand a pointer the library owns to the
    // marshaller's free.

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr odin_bridge_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr odin_heimdall_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr odin_status_text(int status);

    // ---- session lifetime -------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_session_create(int verbose, int waitForDevice,
        out IntPtr session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void odin_session_release(IntPtr session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_close(IntPtr session, in OdinCallbacks cb);

    // ---- cancellation -----------------------------------------------------

    /// <summary>Safe from any thread, including while another call is in flight.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void odin_cancel(IntPtr session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_is_cancelled(IntPtr session);

    // ---- device operations ------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_open(IntPtr session, int resume, in OdinCallbacks cb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_begin_session(IntPtr session, in OdinCallbacks cb);

    /// <summary>
    /// odin_reset_device: libusb_reset_device on a handle of the engine's own.
    /// A reset re-enumerates the device and invalidates every handle to it, so
    /// it cannot be called on an open session; reset, wait, then open again.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_reset_device(in OdinCallbacks cb);

    /// <summary>
    /// odin_read_device_info: the protocol's "DVIF" command. Does not open a
    /// session and does not consume the handshake, so it is safe on connect.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_read_device_info(
        [Out] byte[] buffer, int size, in OdinCallbacks cb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_end_session(IntPtr session, int reboot, in OdinCallbacks cb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_close_pc_screen(IntPtr session, int reboot,
        in OdinCallbacks cb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_request_device_type(IntPtr session, uint request,
        out int value, in OdinCallbacks cb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_set_usb_log_level(IntPtr session, int level,
        in OdinCallbacks cb);

    /// <summary>Added in ABI 2. FlashAction.cpp:155-201.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_send_total_bytes(IntPtr session, ulong totalBytes,
        in OdinCallbacks cb);

    /// <summary>Added in ABI 2. FlashAction.cpp:434-466.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_enable_tflash(IntPtr session, in OdinCallbacks cb);

    /// <summary>
    /// Added in ABI 2. Reads the transfer geometry out of the BridgeManager,
    /// which is the only way to know whether BeginSession negotiated a larger
    /// file part or kept the defaults.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_session_geometry_of(IntPtr session,
        out OdinSessionGeometry geometry);

    // ---- PIT --------------------------------------------------------------

    /// <summary>
    /// The buffer is the CALLER's: the ABI never hands out a native
    /// allocation, because freeing it would need the matching allocator.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_download_pit(IntPtr session, byte[] buffer,
        nuint capacity, out nuint size, in OdinCallbacks cb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_send_pit(IntPtr session, byte[] data, nuint length,
        in OdinCallbacks cb);

    // ---- file transfer ----------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int odin_send_file_stream(IntPtr session, in OdinStream stream,
        ulong length, uint destination, uint deviceType, uint fileIdentifier,
        in OdinCallbacks cb);

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// The library's own sentence for a status code. Falls back to the numeric
    /// code if the library is not loadable, so a diagnostic path can call this
    /// without first proving the library is there.
    /// </summary>
    internal static string StatusText(OdinStatus status)
    {
        try
        {
            string? text = Marshal.PtrToStringUTF8(odin_status_text((int)status));

            if (!string.IsNullOrEmpty(text))
                return text;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return "native status " + (int)status;
    }
}
