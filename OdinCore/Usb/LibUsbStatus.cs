// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall: heimdall links libusb at build time
// and has no equivalent of a run-time availability check.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using OdinCore.Interop;

namespace OdinCore.Usb;

/// <summary>
/// Whether libusb-1.0 could be loaded, where it came from, and what to run if
/// it could not. Lives in OdinCore.Usb rather than OdinCore.Interop because the
/// user interface consumes it, and the interop namespace stays internal.
/// </summary>
public sealed record LibUsbStatus(bool Available, string? Path, string? Error,
                                  string InstallCommand, string? RuntimeVersion)
{
    /// <summary>
    /// One plain line for the window: either where libusb was loaded from and
    /// its version, or that it was not found. ASCII only, no decoration.
    /// </summary>
    public string Summary =>
        Available
            ? "loaded from " + (Path ?? "an unnamed path") +
              (RuntimeVersion is null ? "" : " (" + RuntimeVersion + ")")
            : "not found - " + (Error ?? "no libusb-1.0 on this system");
}

/// <summary>
/// Probes for libusb once and caches the answer. Never throws: a missing libusb
/// is a status line with an install command, not a crash.
/// </summary>
public static class LibUsbAvailability
{
    private static readonly object Gate = new();
    private static LibUsbStatus? _cached;

    /// <summary>
    /// Loads libusb if it is not loaded yet, calls libusb_init and libusb_exit
    /// immediately, and reports the outcome. Cached after the first call.
    /// </summary>
    public static LibUsbStatus Probe()
    {
        lock (Gate)
        {
            if (_cached is not null)
                return _cached;

            _cached = ProbeCore();
            return _cached;
        }
    }

    private static LibUsbStatus ProbeCore()
    {
        string install = UsbPlatform.LibUsbInstallCommand();

        if (!UsbPlatform.IsSupported)
        {
            return new LibUsbStatus(false, null,
                "EnneaOdin supports macOS and Linux only", install, null);
        }

        LibUsbLoader.EnsureRegistered();

        try
        {
            int result = LibUsb.libusb_init(out IntPtr context);

            if (result != LibUsb.LIBUSB_SUCCESS)
            {
                return new LibUsbStatus(false, LibUsbLoader.LoadedPath,
                    "libusb_init failed: " + LibUsb.Describe(result), install, ReadVersion());
            }

            LibUsb.libusb_exit(context);

            return new LibUsbStatus(true, LibUsbLoader.LoadedPath, null, install, ReadVersion());
        }
        catch (DllNotFoundException ex)
        {
            return new LibUsbStatus(false, null, DescribeMissing(ex), install, null);
        }
        catch (EntryPointNotFoundException ex)
        {
            return new LibUsbStatus(false, LibUsbLoader.LoadedPath,
                "the libusb found on this system is missing an entry point this program needs: " +
                ex.Message, install, null);
        }
        catch (BadImageFormatException ex)
        {
            return new LibUsbStatus(false, LibUsbLoader.LoadedPath,
                "the libusb found on this system is built for a different architecture: " +
                ex.Message, install, null);
        }
    }

    private static string DescribeMissing(DllNotFoundException ex)
    {
        string[] attempted = LibUsbLoader.Attempted();

        string where = attempted.Length == 0
            ? "no candidate paths for this platform"
            : "tried " + string.Join(", ", attempted);

        return "libusb-1.0 could not be loaded (" + where + "). " + ex.Message;
    }

    /// <summary>libusb_get_version rendered as major.minor.micro with the rc suffix, if any.</summary>
    private static string? ReadVersion()
    {
        try
        {
            IntPtr pointer = LibUsb.libusb_get_version();
            if (pointer == IntPtr.Zero)
                return null;

            libusb_version version = Marshal.PtrToStructure<libusb_version>(pointer);
            string text = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}",
                version.major, version.minor, version.micro);

            string? rc = version.rc == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(version.rc);
            return string.IsNullOrEmpty(rc) ? text : text + rc;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}
