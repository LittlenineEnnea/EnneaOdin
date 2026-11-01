// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (DetectDevice, lines 418-467;
//                                                FindDeviceInterface, lines
//                                                79-240)
//   native/vendor/Heimdall/heimdall/source/BridgeManager.h    (kVidSamsung, the PID set)
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
/// Enumerates USB devices with libusb and reports which of them are Samsung
/// download-mode devices. This is the port of BridgeManager::DetectDevice
/// (BridgeManager.cpp:418-467) together with the descriptor-reading half of
/// BridgeManager::FindDeviceInterface (:79-240).
/// </summary>
public sealed class UsbEnumerator : IDisposable
{
    private readonly IProgress<OdinEvent>? _log;

    // The last diagnostic this enumerator reported, so the same sentence is
    // not logged again by the next poll.
    private string? _lastDiagnostic;
    private readonly UsbDescriptorReader _reader;
    private readonly int _usbLogLevel;
    private readonly object _gate = new();

    private LibUsbContext? _context;
    private bool _disposed;

    /// <param name="log">Where descriptor and diagnostic events go. May be null.</param>
    /// <param name="usbLogLevel">
    /// A LIBUSB_LOG_LEVEL_* value, 0 (none) to 4 (debug). Upstream's default is
    /// error (BridgeManager.h:96, UsbLogLevel::Default).
    /// </param>
    public UsbEnumerator(IProgress<OdinEvent>? log = null, int usbLogLevel = LibUsb.LIBUSB_LOG_LEVEL_ERROR)
    {
        _log = log;
        _reader = new UsbDescriptorReader(log);
        _usbLogLevel = usbLogLevel;
    }

    /// <summary>
    /// One enumeration pass.
    /// </summary>
    /// <param name="readStrings">
    /// When true the manufacturer, product and serial string descriptors are
    /// read, which requires opening each Samsung device. The one-second device
    /// poll passes false so that a rescan never opens a device; "Read device
    /// info" passes true.
    /// </param>
    public Task<UsbEnumerationResult> EnumerateAsync(bool readStrings, CancellationToken ct) =>
        Task.Run(() => Enumerate(readStrings, ct), ct);

    /// <summary>
    /// Polls until a download-mode device appears or the token is cancelled.
    /// This is upstream's --wait: "Waiting for device..." plus Sleep(1000) at
    /// BridgeManager.cpp:81-82 and :112-113. Always enumerates without opening
    /// anything, so polling cannot disturb a device.
    /// </summary>
    public async Task<UsbEnumerationResult> WaitForDownloadDeviceAsync(TimeSpan pollInterval,
                                                                       CancellationToken ct)
    {
        if (pollInterval <= TimeSpan.Zero)
            pollInterval = TimeSpan.FromSeconds(1);

        Log(OdinLevel.Info, "Waiting for device...");   // BridgeManager.cpp:112

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            UsbEnumerationResult result = await EnumerateAsync(false, ct).ConfigureAwait(false);

            if (result.Kind == UsbDiagnostic.LibUsbUnavailable || result.DownloadModeDevices.Count > 0)
                return result;

            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The synchronous core. Safe to call directly from a worker thread.</summary>
    public UsbEnumerationResult Enumerate(bool readStrings, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        LibUsbStatus status = LibUsbAvailability.Probe();

        if (!status.Available)
        {
            Log(OdinLevel.Error, "libusb-1.0 is not available: " + (status.Error ?? "not found"));
            Log(OdinLevel.Error, "Install it with: " + status.InstallCommand);

            return new UsbEnumerationResult(Array.Empty<UsbDeviceRecord>(), Array.Empty<UsbDeviceRecord>(),
                status, UsbDiagnostic.LibUsbUnavailable,
                "libusb-1.0 could not be loaded. Install it with: " + status.InstallCommand);
        }

        LibUsbContext context;
        try
        {
            context = EnsureContext();
        }
        catch (LibUsbException ex)
        {
            Log(OdinLevel.Error, ex.Message);
            return new UsbEnumerationResult(Array.Empty<UsbDeviceRecord>(), Array.Empty<UsbDeviceRecord>(),
                status, UsbDiagnostic.LibUsbFailed, ex.Message);
        }
        catch (LibUsbUnavailableException ex)
        {
            Log(OdinLevel.Error, ex.Message);
            return new UsbEnumerationResult(Array.Empty<UsbDeviceRecord>(), Array.Empty<UsbDeviceRecord>(),
                ex.Status, UsbDiagnostic.LibUsbUnavailable, ex.Message);
        }

        var downloadMode = new List<UsbDeviceRecord>();
        var otherSamsung = new List<UsbDeviceRecord>();

        nint count = LibUsb.libusb_get_device_list(context.Handle, out IntPtr list);

        if (count < 0)
        {
            string message = "libusb_get_device_list failed: " + LibUsb.Describe((int)count);
            Log(OdinLevel.Error, message);
            return new UsbEnumerationResult(downloadMode, otherSamsung, status,
                UsbDiagnostic.LibUsbFailed, message);
        }

        try
        {
            int pointerSize = IntPtr.Size;

            for (nint index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();

                IntPtr device = Marshal.ReadIntPtr(IntPtr.Add(list, (int)(index * pointerSize)));
                if (device == IntPtr.Zero)
                    continue;

                int result = LibUsb.libusb_get_device_descriptor(device, out libusb_device_descriptor descriptor);
                if (result != LibUsb.LIBUSB_SUCCESS)
                {
                    // Upstream ignores this result entirely (BridgeManager.cpp:95,
                    // :444). This port reports it and moves on, because a device
                    // whose descriptor cannot be read must not be shown at all.
                    Log(OdinLevel.Debug, "Skipping a device whose descriptor could not be read: " +
                        LibUsb.Describe(result));
                    continue;
                }

                var id = new UsbId(descriptor.idVendor, descriptor.idProduct);
                if (!SamsungIds.IsSamsung(id))
                    continue;

                UsbDeviceRecord record = _reader.BuildRecord(device, descriptor, id, readStrings);

                if (record.IsDownloadMode)
                {
                    downloadMode.Add(record);
                    Log(OdinLevel.Debug, "Device detected: " + record.DisplayName + " (" +
                        SamsungIds.MatchBasis(id) + ")");
                }
                else
                {
                    otherSamsung.Add(record);
                    Log(OdinLevel.Debug, "Samsung device " + id + " on bus " + record.BusNumber +
                        " address " + record.DeviceAddress +
                        " is not in download mode (" + SamsungIds.MatchBasis(id) + ").");
                }
            }
        }
        finally
        {
            // unref_devices = 1: this port refs nothing during enumeration.
            // Upstream calls libusb_ref_device on a match inside the loop
            // (BridgeManager.cpp:103) and its break at :104 leaves only the inner
            // loop, so with two matching devices attached the last one wins and
            // the earlier ref is leaked. Here every match is returned to the
            // caller as a record, and a device is only opened later, by
            // (bus, address), after the caller has chosen one.
            LibUsb.libusb_free_device_list(list, 1);   // BridgeManager.cpp:111
        }

        (UsbDiagnostic kind, string? diagnostic) = Diagnose(downloadMode, otherSamsung);

        // Only when it CHANGED. Debug rather than Warning for the same
        // reason: the sentence is already the status line under the device
        // box, and repeating a paragraph about accessory approval on every
        // two-second poll buried everything else. Repeating the READY line
        // was just as bad - two enumerations in a row, one from the poll and
        // one from the device read, printed it twice for one connect.
        if (diagnostic is not null
            && !string.Equals(_lastDiagnostic, diagnostic, StringComparison.Ordinal))
        {
            _lastDiagnostic = diagnostic;

            Log(kind == UsbDiagnostic.DownloadModeReady ? OdinLevel.Info : OdinLevel.Debug,
                diagnostic);
        }
        else if (diagnostic is null)
        {
            _lastDiagnostic = null;
        }

        return new UsbEnumerationResult(downloadMode, otherSamsung, status, kind, diagnostic);
    }

    private static (UsbDiagnostic, string?) Diagnose(List<UsbDeviceRecord> downloadMode,
                                                     List<UsbDeviceRecord> otherSamsung)
    {
        if (downloadMode.Count == 0)
        {
            if (otherSamsung.Count > 0)
            {
                return (UsbDiagnostic.SamsungNotInDownloadMode,
                    "A Samsung device is attached but it is not in download mode. " +
                    "Power the handset off and enter download mode, then rescan.");
            }

            string detail = UsbPlatform.IsMacOS
                ? " " + UsbAccess.MacAccessoryApprovalHint()
                : UsbPlatform.IsLinux
                    ? " If the handset is in download mode and still does not appear, " +
                      UsbAccess.LinuxRulesInstallHint()
                    : "";

            return (UsbDiagnostic.NoDevice, "No Samsung download-mode device found." + detail);
        }

        foreach (UsbDeviceRecord device in downloadMode)
        {
            if (device.Access == UsbAccessState.Claimable)
            {
                return (UsbDiagnostic.DownloadModeReady,
                    downloadMode.Count == 1
                        ? "One download-mode device found: " + device.DisplayName + "."
                        : downloadMode.Count.ToString(CultureInfo.InvariantCulture) +
                          " download-mode devices found; choose one.");
            }
        }

        UsbDeviceRecord first = downloadMode[0];
        return (UsbDiagnostic.DownloadModeNotClaimable,
            "A download-mode device is attached but it cannot be claimed: " +
            first.StatusWord + ". " + (first.AccessDetail ?? ""));
    }

    private LibUsbContext EnsureContext()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Upstream creates a context per BridgeManager and so per detection
            // run (BridgeManager.cpp:421). One context is kept here for the life
            // of the enumerator instead, because the UI re-enumerates every two
            // seconds and an init/exit pair per poll is pure churn. The context
            // is still per object, never global.
            return _context ??= LibUsbContext.Create(_usbLogLevel, _log);
        }
    }

    private void Log(OdinLevel level, string message) =>
        _log?.Report(new OdinEvent(DateTimeOffset.Now, level, message, "usb"));

    public void Dispose()
    {
        LibUsbContext? context;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            context = _context;
            _context = null;
        }

        context?.Dispose();
    }
}
