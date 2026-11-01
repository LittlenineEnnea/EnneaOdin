// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall: upstream reports libusb failures by
// printing a number (for example BridgeManager.cpp:127, :425) and returning a
// bool. A typed exception carrying the same number plus libusb's own name for
// it is this port's equivalent.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using OdinCore.Usb;

namespace OdinCore.Interop;

/// <summary>
/// A libusb call failed. Carries the libusb error code, libusb's symbolic name
/// for it and the operation that was attempted, so the message a user sees names
/// all three instead of a bare integer.
/// </summary>
public class LibUsbException : Exception
{
    public LibUsbException(string operation, int code)
        : base(operation + " failed: " + LibUsb.Describe(code))
    {
        Operation = operation;
        Code = code;
        ErrorName = LibUsb.ErrorName(code);
    }

    /// <summary>The libusb operation that failed, for example "libusb_open".</summary>
    public string Operation { get; }

    /// <summary>The raw libusb error code; see enum libusb_error in libusb.h:1300.</summary>
    public int Code { get; }

    /// <summary>libusb's symbolic name, for example LIBUSB_ERROR_ACCESS.</summary>
    public string ErrorName { get; }

    /// <summary>
    /// True for LIBUSB_ERROR_ACCESS (-3), which on both platforms means the
    /// process may not open this device. On Linux that is a udev rule or group
    /// problem; it is never a reason to suggest running the whole program as
    /// root.
    /// </summary>
    public bool IsPermissionDenied => Code == LibUsb.LIBUSB_ERROR_ACCESS;

    /// <summary>
    /// True for LIBUSB_ERROR_NO_DEVICE (-4), which mid-transfer means the cable
    /// or the phone went away and must be reported as that, not as a generic
    /// failure.
    /// </summary>
    public bool IsDeviceGone => Code == LibUsb.LIBUSB_ERROR_NO_DEVICE;

    /// <summary>True for LIBUSB_ERROR_BUSY (-6): something else holds the interface.</summary>
    public bool IsBusy => Code == LibUsb.LIBUSB_ERROR_BUSY;

    /// <summary>True for LIBUSB_ERROR_TIMEOUT (-7).</summary>
    public bool IsTimeout => Code == LibUsb.LIBUSB_ERROR_TIMEOUT;
}

/// <summary>
/// libusb itself could not be loaded. Distinct from <see cref="LibUsbException"/>
/// because the remedy is to install a package, not to re-plug a cable; the
/// status carries the per-platform install command.
/// </summary>
public sealed class LibUsbUnavailableException : Exception
{
    public LibUsbUnavailableException(LibUsbStatus status, Exception? inner = null)
        : base("libusb-1.0 is not available: " + (status.Error ?? "not found") +
               ". Install it with: " + status.InstallCommand, inner)
    {
        Status = status;
    }

    public LibUsbStatus Status { get; }
}
