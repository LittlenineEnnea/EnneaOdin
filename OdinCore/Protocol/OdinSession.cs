// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file no longer implements the Samsung download-mode protocol. It is the
// managed face of the native engine: every wire operation below is one call
// into native/include/odin_bridge.h, which drives Heimdall's own
// BridgeManager in this process.
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (the protocol)
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp    (total bytes, T-Flash)
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is distributed under the MIT licence. The above copyright notices
// and the permission notice in native/vendor/Heimdall/LICENSE are retained as
// that licence requires. NOTICE records every vendored file this project
// modified and why.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using OdinCore.Diagnostics;
using OdinCore.Native;
using OdinCore.Usb;

namespace OdinCore.Protocol;

/// <summary>A protocol-level failure: the device answered wrongly, or not at all.</summary>
public sealed class OdinProtocolException : Exception
{
    public OdinProtocolException(string message) : base(message)
    {
    }

    public OdinProtocolException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>How a session is opened.</summary>
/// <param name="Resume">
/// Skip the ODIN/LOKE handshake because the protocol is already up on the
/// device, which is the case after any session that ended without a reboot.
/// BridgeManager.cpp:500-504. This is the whole meaning of heimdall's
/// --resume, and it is derived from OdinSessionTracker rather than offered as
/// a free-standing checkbox.
/// </param>
/// <param name="UsbLogLevel">0 none, 1 error, 2 warning, 3 info, 4 debug. Upstream's default is error (BridgeManager.h:96).</param>
/// <param name="BeginSessionDelayMs">
/// Kept for source compatibility and no longer applied here. The pause after
/// the begin-session response is performed by the engine itself, inside
/// BridgeManager::BeginSession (BridgeManager.cpp:528, Sleep(3000)), so this
/// value is reported in the log and nothing else. Changing it would have to
/// change the C++.
/// </param>
/// <param name="VerboseTransport">Passed to BridgeManager's constructor as its verbose flag.</param>
public sealed record OdinSessionOptions(
    bool Resume = false,
    int UsbLogLevel = OdinConstants.UsbLogLevelError,
    int BeginSessionDelayMs = OdinConstants.BeginSessionDelayMs,
    bool VerboseTransport = false);

/// <summary>
/// One Odin/LOKE session, driven by the native engine.
/// </summary>
public sealed class OdinSession : IDisposable
{
    /// <summary>
    /// The one sentence a user interface should show when it offers to stop an
    /// operation. The numbers are measured facts about the engine, not an
    /// estimate: a transfer stops at a file-part boundary (131072 bytes before
    /// the file-part size is negotiated, 1 MiB after), a PIT download at a
    /// 500-byte part, and a wait for a device within one 1 s poll. A transfer
    /// already in flight cannot be aborted, so the worst case adds that
    /// transfer's timeout: 3 s normally (BridgeManager.h:83-84) and up to the
    /// end-of-sequence timeout while waiting for a sequence acknowledgement,
    /// which is 30 s before negotiation and 120 s after (BridgeManager.cpp:76,
    /// :532).
    /// </summary>
    public const string CancellationGranularityNote =
        "Cancellation takes effect at the next packet boundary, never inside a packet. " +
        "A file transfer stops at the end of the current file part, which can take up to " +
        "about 3 seconds, and up to 2 minutes while the device is being waited on for an " +
        "end-of-sequence acknowledgement. The session is left open at a packet boundary so " +
        "it can still be ended cleanly.";

    private readonly OdinSessionOptions _options;
    private readonly IProgress<OdinEvent> _log;
    private readonly OdinNativeCallbacks _callbacks;
    private readonly OdinSessionTracker _tracker = new();
    private readonly UsbDeviceRecord _device;

    private IntPtr _session;
    private bool _disposed;

    private OdinSession(IntPtr session, UsbDeviceRecord device, OdinSessionOptions options,
        IProgress<OdinEvent> log, OdinNativeCallbacks callbacks)
    {
        _session = session;
        _device = device;
        _options = options;
        _log = log;
        _callbacks = callbacks;

        // BridgeManager.cpp:74-76 -- the pre-negotiation defaults, which are
        // what the engine starts with. BeginSessionAsync replaces them with
        // whatever the engine settled on.
        FilePartSize = OdinConstants.DefaultFileTransferPacketSize;
        SequenceMaxLength = OdinConstants.DefaultFileTransferSequenceMaxLength;
        SequenceTimeoutMs = OdinConstants.DefaultFileTransferSequenceTimeoutMs;
    }

    /// <summary>The session's phase and the resume/reboot bookkeeping.</summary>
    public OdinSessionTracker Tracker => _tracker;

    public OdinSessionPhase Phase => _tracker.Phase;

    /// <summary>The descriptor snapshot of the device this session is driving.</summary>
    public UsbDeviceRecord Device => _device;

    /// <summary>
    /// Zero while the engine is using the pre-negotiation defaults; otherwise
    /// the file-part size the device accepted.
    /// </summary>
    public uint DeviceDefaultFilePartSize { get; private set; }

    /// <summary>Bytes per file part, read back from the engine.</summary>
    public int FilePartSize { get; private set; }

    /// <summary>Parts per sequence, read back from the engine.</summary>
    public int SequenceMaxLength { get; private set; }

    /// <summary>End-of-sequence timeout, read back from the engine.</summary>
    public int SequenceTimeoutMs { get; private set; }

    /// <summary>Which engine is behind this session, for the log and the device panel.</summary>
    public static OdinEngineStatus Engine => OdinNativeLoader.Probe();

    // ------------------------------------------------------------------
    // Open
    // ------------------------------------------------------------------

    /// <summary>
    /// Load the engine if necessary, pick the device, and open it. Calls
    /// odin_session_create, odin_set_usb_log_level and odin_open, which is
    /// BridgeManager::Initialise (BridgeManager.cpp:469-507): libusb
    /// initialisation, interface selection, claiming, and the ODIN/LOKE
    /// handshake unless this is a resume.
    /// </summary>
    public static Task<OdinSession> OpenAsync(byte busNumber, byte deviceAddress,
        OdinSessionOptions options, IProgress<OdinEvent> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        ct.ThrowIfCancellationRequested();

        OdinEngineStatus engine = OdinNativeLoader.Probe();

        if (!engine.Available)
        {
            // Typed, actionable, and not a crash: the caller reports the
            // message and the application carries on.
            log.Report(OdinEvent.Error(engine.Summary, "engine"));
            throw new OdinEngineUnavailableException(engine);
        }

        log.Report(OdinEvent.Info(engine.Summary, "engine"));

        UsbDeviceRecord device = SelectDevice(busNumber, deviceAddress, log, ct);

        var callbacks = new OdinNativeCallbacks(log, "session");

        // waitForDevice is 0: the device was just enumerated, and a GUI that
        // blocks inside a native poll loop cannot be told anything useful
        // while it waits. Heimdall's --wait-for-device is a command-line
        // convenience for a script.
        OdinStatus created = (OdinStatus)OdinNative.odin_session_create(
            options.VerboseTransport ? 1 : 0, 0, out IntPtr handle);

        if (created != OdinStatus.Ok || handle == IntPtr.Zero)
            throw Failure("create a session", created);

        var session = new OdinSession(handle, device, options, log, callbacks);

        try
        {
            session.Invoke("set the USB log level", ct, () =>
                OdinNative.odin_set_usb_log_level(session._session, options.UsbLogLevel,
                    callbacks.Native));

            session._tracker.TransitionTo(OdinSessionPhase.Claimed);

            if (options.Resume)
            {
                // BridgeManager.cpp:500-504 -- Initialise(resume) skips the
                // handshake, because the device's protocol state survived the
                // previous session's end.
                log.Report(OdinEvent.Info(
                    "Resuming an open session: skipping the ODIN/LOKE handshake.", "session"));
            }

            session.Invoke("open the device", ct, () =>
                OdinNative.odin_open(session._session, options.Resume ? 1 : 0, callbacks.Native));

            session._tracker.TransitionTo(OdinSessionPhase.HandshakeDone);
            session.RefreshGeometry();

            return Task.FromResult(session);
        }
        catch
        {
            session._tracker.RecordFault();
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open the device and begin a session, FALLING BACK to a full handshake
    /// if a resumed one does not take.
    ///
    /// Resume exists because a session ended without a reboot leaves the
    /// handset in download mode, so the next operation can skip the ODIN/LOKE
    /// handshake (BridgeManager.cpp:500-504). On a real SM-A166U that is not
    /// true: after a clean EndSession and a released interface, resuming gets
    /// past the open and then fails at "Beginning session!", leaving the user
    /// stuck until they re-enter download mode by hand. It is also exactly the
    /// state a device is in after the handshake needed libusb_reset_device,
    /// because the reset re-enumerated it.
    ///
    /// So resume is attempted and not trusted. One retry, with the handshake,
    /// which is what the device wanted in the first place.
    /// </summary>
    /// <summary>
    /// This session was opened after a resumed one was refused, so the caller
    /// should stop asking to resume: on a device that does not support it,
    /// every later operation would otherwise waste a failed BeginSession.
    /// </summary>
    public bool ResumeRefused { get; private set; }

    public static async Task<OdinSession> OpenAndBeginAsync(byte busNumber, byte deviceAddress,
        OdinSessionOptions options, IProgress<OdinEvent> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        // Attempt one: as asked.
        try
        {
            OdinSession session = await OpenAsync(busNumber, deviceAddress, options, log, ct)
                .ConfigureAwait(false);

            try
            {
                await session.BeginSessionAsync(ct).ConfigureAwait(false);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception first) when (options.Resume)
        {
            // ONLY after a RESUMED attempt, and the reason matters.
            //
            // A resumed attempt deliberately skips the ODIN/LOKE handshake, so
            // failing it costs nothing and trying the handshake is the obvious
            // next move.
            //
            // A failed attempt that DID handshake is a different thing
            // entirely. Measured on an SM-A166U: the handset answers the
            // handshake ONCE per entry into download mode, and a second
            // attempt gets the send through and no reply, on the bulk
            // endpoints and through the serial node alike. Retrying there
            // spends the one chance the user just gave us by re-entering
            // download mode, which is why this catch is now narrow. It used
            // to retry anything, and that is what burned the handshake.
            log.Report(OdinEvent.Warning(
                "Resuming did not work (" + first.Message + "). Starting over with a full " +
                "handshake.", "session"));
        }

        ct.ThrowIfCancellationRequested();

        // A reset re-enumerates the handset, so everything after it has to be
        // new: new libusb context, fresh enumeration, fresh claim, and the
        // handshake rather than a resume.
        //
        // Doing the reset INSIDE a half-open session is what an earlier
        // version got wrong. The handle it kept using afterwards was already
        // dead, and libusb said so: "no connection to an IOService", then
        // LIBUSB_ERROR_NO_DEVICE, for every later attempt in the loop.
        ResetDevice(log);

        // Re-enumeration takes a moment.
        await Task.Delay(1500, ct).ConfigureAwait(false);

        OdinSession fresh = await OpenAsync(busNumber, deviceAddress,
            options with { Resume = false }, log, ct).ConfigureAwait(false);

        fresh.ResumeRefused = options.Resume;

        try
        {
            await fresh.BeginSessionAsync(ct).ConfigureAwait(false);
            return fresh;
        }
        catch
        {
            fresh.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reset the attached handset through the engine, on a libusb handle of
    /// its own. Reported, never fatal: a device that refuses the reset is no
    /// worse off than one that was never reset.
    /// </summary>
    private static void ResetDevice(IProgress<OdinEvent> log)
    {
        var callbacks = new OdinNativeCallbacks(log, "session");

        try
        {
            var status = (OdinStatus)OdinNative.odin_reset_device(callbacks.Native);

            if (status != OdinStatus.Ok)
            {
                log.Report(OdinEvent.Warning(
                    "The device reset returned " + status + ".", "session"));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException)
        {
            log.Report(OdinEvent.Warning(
                "The engine has no device reset: " + ex.Message, "session"));
        }
        finally
        {
            callbacks.KeepAlive();
        }
    }

    /// <summary>
    /// Finds the record for the requested attachment, and refuses the cases
    /// where the engine's own choice could differ from the user's.
    /// </summary>
    private static UsbDeviceRecord SelectDevice(byte busNumber, byte deviceAddress,
        IProgress<OdinEvent> log, CancellationToken ct)
    {
        using var enumerator = new UsbEnumerator(null);
        UsbEnumerationResult found = enumerator.Enumerate(false, ct);

        IReadOnlyList<UsbDeviceRecord> devices = found.DownloadModeDevices;

        if (devices.Count == 0)
        {
            throw new OdinProtocolException(
                "No Samsung download-mode device is attached any more. " +
                (found.Diagnostic ?? "Nothing matched the three download-mode product ids."));
        }

        if (devices.Count > 1)
        {
            // The engine claims the first match libusb reports and cannot be
            // told which one to take. Guessing here could write a firmware to
            // the wrong handset, so it does not guess.
            throw new OdinProtocolException(string.Format(CultureInfo.InvariantCulture,
                "{0} Samsung download-mode devices are attached. The protocol engine selects " +
                "the device itself and cannot be told which one to use, so EnneaOdin will not " +
                "guess. Leave exactly one device connected.", devices.Count));
        }

        UsbDeviceRecord only = devices[0];

        if (only.BusNumber != busNumber || only.DeviceAddress != deviceAddress)
        {
            log.Report(OdinEvent.Warning(string.Format(CultureInfo.InvariantCulture,
                "The selected device was bus {0} address {1}, but the only download-mode device " +
                "attached now is bus {2} address {3} ({4}). Continuing with the attached one.",
                busNumber, deviceAddress, only.BusNumber, only.DeviceAddress, only.DisplayName),
                "session"));
        }

        return only;
    }

    // ------------------------------------------------------------------
    // Session
    // ------------------------------------------------------------------

    /// <summary>
    /// odin_begin_session, which is BridgeManager::BeginSession
    /// (BridgeManager.cpp:509-558): the begin-session exchange, the
    /// "may take up to 2 minutes" message, the 3 s pause and the file-part
    /// size negotiation. The pause is the engine's, not this layer's.
    /// </summary>
    public Task BeginSessionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (_options.BeginSessionDelayMs != OdinConstants.BeginSessionDelayMs)
        {
            _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "The begin-session pause is fixed at {0} ms inside the engine " +
                "(BridgeManager.cpp:528); the requested {1} ms is not applied.",
                OdinConstants.BeginSessionDelayMs, _options.BeginSessionDelayMs), "session"));
        }

        try
        {
            Invoke("begin the session", ct, () =>
                OdinNative.odin_begin_session(_session, _callbacks.Native));

            _tracker.TransitionTo(OdinSessionPhase.SessionOpen);
            RefreshGeometry();

            _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                DeviceDefaultFilePartSize != 0
                    ? "File part size negotiated: {0} bytes, {1} parts per sequence, {2} ms sequence timeout."
                    : "Device kept the default file part size: {0} bytes, {1} parts per sequence, {2} ms sequence timeout.",
                FilePartSize, SequenceMaxLength, SequenceTimeoutMs), "session"));

            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _tracker.RecordFault();
            throw;
        }
    }

    /// <summary>
    /// odin_enable_tflash: redirect the flash to the SD card. Port of
    /// FlashAction.cpp:434-466, which is where the 5000 ms response timeout
    /// comes from.
    /// </summary>
    public Task EnableTFlashAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        RequireSessionOpen();

        _log.Report(OdinEvent.Warning(
            "Enabling T-Flash: the flash will be written to the SD card, not to the handset's own storage.",
            "session"));

        return Guarded("enable T-Flash", ct, () =>
            OdinNative.odin_enable_tflash(_session, _callbacks.Native));
    }

    /// <summary>
    /// odin_send_total_bytes: declare the size of the whole transfer before
    /// the first image. Port of FlashAction.cpp:155-201, minus the summing,
    /// which the caller does because it is the one that knows the plan.
    /// </summary>
    public Task SendTotalBytesAsync(ulong totalBytes, CancellationToken ct)
    {
        ThrowIfDisposed();
        RequireSessionOpen();

        Task task = Guarded("declare the total transfer size", ct, () =>
            OdinNative.odin_send_total_bytes(_session, totalBytes, _callbacks.Native));

        _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
            "Total transfer size accepted: {0} bytes.", totalBytes), "session"));

        return task;
    }

    /// <summary>
    /// odin_end_session, which is BridgeManager::EndSession
    /// (BridgeManager.cpp:560-614). Not rebooting is what leaves the device in
    /// download mode for a follow-up operation, which the tracker records so
    /// the next open knows to resume.
    /// </summary>
    public Task EndSessionAsync(bool reboot, CancellationToken ct)
    {
        ThrowIfDisposed();


        try
        {
            Invoke("end the session", ct, () =>
                OdinNative.odin_end_session(_session, reboot ? 1 : 0, _callbacks.Native));

            if (!reboot)
            {
                _log.Report(OdinEvent.Info(
                    "Session ended without a reboot: the device is still in download mode.", "session"));
            }

            _tracker.RecordSessionEnd(reboot);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _tracker.RecordFault();
            throw;
        }
    }


    /// <summary>
    /// odin_request_device_type, which is BridgeManager::RequestDeviceType.
    /// The request ids are the ones in DeviceTypePacket.h; the answer is
    /// returned raw, because nothing in the vendored tree documents how to
    /// interpret it beyond the values Heimdall's own flash path uses.
    /// </summary>
    public Task<int> RequestDeviceTypeAsync(uint request, CancellationToken ct)
    {
        ThrowIfDisposed();
        RequireSessionOpen();

        int value = 0;

        try
        {
            Invoke("request the device type", ct, () =>
                OdinNative.odin_request_device_type(_session, request, out value,
                    _callbacks.Native));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _tracker.RecordFault();
            throw;
        }

        return Task.FromResult(value);
    }

    /// <summary>
    /// odin_set_usb_log_level. Valid at any time; the engine remembers the
    /// level and reapplies it to the libusb context it owns.
    /// </summary>
    public Task SetUsbLogLevelAsync(int level, CancellationToken ct)
    {
        ThrowIfDisposed();

        // Deliberately not Guarded: this touches no device, and a rejected log
        // level is a bad argument from the caller. Marking the session faulted
        // over it would abandon a usable session for a cosmetic setting.
        Invoke("set the USB log level", ct, () =>
            OdinNative.odin_set_usb_log_level(_session, level, _callbacks.Native));

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // PIT
    // ------------------------------------------------------------------

    /// <summary>
    /// odin_download_pit: the device's PIT as raw bytes. The engine runs
    /// BridgeManager::DownloadPitFile into a buffer this side owns, so no
    /// native allocation ever has to be freed from managed code.
    /// </summary>
    public Task<byte[]> ReceivePitAsync(IProgress<OdinEvent>? log, CancellationToken ct)
    {
        ThrowIfDisposed();
        RequireSessionOpen();

        IProgress<OdinEvent> report = log ?? _log;

        var buffer = new byte[OdinNative.PitMaxSize];
        nuint size = 0;

        try
        {
            Invoke("download the PIT", ct, () =>
                OdinNative.odin_download_pit(_session, buffer, (nuint)buffer.Length,
                    out size, _callbacks.Native));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _tracker.RecordFault();
            throw;
        }

        if (size == 0 || size > (nuint)buffer.Length)
        {
            _tracker.RecordFault();
            throw new OdinProtocolException(string.Format(CultureInfo.InvariantCulture,
                "The device reported a PIT of {0} bytes, which cannot be right.", (ulong)size));
        }

        var pit = new byte[(int)size];
        Array.Copy(buffer, pit, pit.Length);

        report.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
            "PIT downloaded: {0} bytes.", pit.Length), "pit"));

        return Task.FromResult(pit);
    }

    /// <summary>
    /// Download and parse the device's PIT.
    /// </summary>
    public async Task<PitData> DownloadPitAsync(IProgress<OdinEvent>? log, CancellationToken ct)
    {
        byte[] raw = await ReceivePitAsync(log, ct).ConfigureAwait(false);

        if (!PitData.TryUnpack(raw, out PitData? pit, out string? error) || pit is null)
            throw new OdinProtocolException("Failed to unpack the device's PIT file: " + error);

        (log ?? _log).Report(OdinEvent.Info(PitTextFormatter.FormatHeaderLine(pit), "pit"));
        return pit;
    }

    /// <summary>
    /// odin_send_pit: upload a PIT, which is what repartitioning means. The
    /// engine runs BridgeManager::SendPitData (BridgeManager.cpp:793-891),
    /// which sends the whole padded PIT in a single file-part packet.
    /// </summary>
    public Task SendPitAsync(PitData pit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pit);
        ThrowIfDisposed();
        RequireSessionOpen();

        _log.Report(OdinEvent.Warning(string.Format(CultureInfo.InvariantCulture,
            "Uploading PIT ({0} entries, {1} bytes padded). This repartitions the device.",
            pit.EntryCount, pit.PaddedSize), "pit"));

        // Pack() is OdinCore's, so the bytes that go out are the bytes the
        // planner and the confirmation dialogue were shown.
        byte[] packed = pit.Pack();

        Task task = Guarded("upload the PIT", ct, () =>
            OdinNative.odin_send_pit(_session, packed, (nuint)packed.Length, _callbacks.Native));

        _log.Report(OdinEvent.Info("PIT upload successful.", "pit"));            // FlashAction.cpp:246
        return task;
    }

    // ------------------------------------------------------------------
    // File transfer
    // ------------------------------------------------------------------

    /// <summary>
    /// odin_send_file_stream: upload one partition image. The engine runs
    /// BridgeManager::SendFile (BridgeManager.cpp:1004-1238) against a FILE *
    /// backed by <paramref name="source"/>, so nothing is spooled to disk and
    /// a 5.5 GB image is never buffered.
    /// </summary>
    /// <param name="source">The image. Forward-only works; the adapter seeks lazily.</param>
    /// <param name="length">The image's exact byte count.</param>
    /// <param name="destination">Phone or modem. BridgeManager.cpp:1006.</param>
    /// <param name="deviceType">The PIT entry's device type. FlashAction.cpp:263, :279.</param>
    /// <param name="fileIdentifier">The PIT entry's identifier, or 0xFFFFFFFF for the modem.</param>
    /// <param name="partitionName">Used only in progress reports and log lines.</param>
    public Task SendFileAsync(Stream source, long length, FileDestination destination,
        uint deviceType, uint fileIdentifier, string partitionName,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        RequireSessionOpen();

        if (destination != FileDestination.Phone && destination != FileDestination.Modem)
        {
            // BridgeManager.cpp:1006-1010.
            throw new ArgumentOutOfRangeException(nameof(destination), destination,
                "A file transfer is addressed either to the phone or to the modem.");
        }

        if (destination == FileDestination.Modem && fileIdentifier != OdinConstants.ModemFileIdentifier)
        {
            // BridgeManager.cpp:1012-1016.
            throw new ArgumentOutOfRangeException(nameof(fileIdentifier), fileIdentifier,
                "A modem transfer must carry the 0xFFFFFFFF file identifier.");
        }

        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "An image cannot have a negative length.");

        var adapter = new OdinNativeStream(source);

        progress?.Report(new TransferProgress(partitionName, 0, length, 0));    // BridgeManager.cpp:1060 prints "0%"

        int lastPercent = -1;

        _callbacks.ProgressHandler = (transferred, total) =>
        {
            long done = transferred > long.MaxValue ? long.MaxValue : (long)transferred;
            long all = total > long.MaxValue ? long.MaxValue : (long)total;
            int percent = all <= 0 ? 0 : (int)(100.0 * ((double)done / all));

            // One report per whole percent: BridgeManager.cpp:1232 prints on
            // the same condition, and a report per 131072-byte part would be
            // 42000 reports for a 5.5 GB image.
            if (percent == lastPercent && done != all)
                return;

            lastPercent = percent;
            progress?.Report(new TransferProgress(partitionName, done, all, percent));
        };

        try
        {
            Invoke("upload " + partitionName, ct, () =>
                OdinNative.odin_send_file_stream(_session, adapter.Native, (ulong)length,
                    (uint)destination, deviceType, fileIdentifier, _callbacks.Native));

            if (adapter.Failed)
            {
                // The native side already refuses to report success after a
                // failed read; this turns the recorded managed exception into
                // the message, which is more use than "the operation failed".
                _tracker.RecordFault();
                throw new OdinProtocolException(
                    "Reading " + partitionName + " failed part way through the upload: " +
                    (adapter.Error?.Message ?? "the image stream reported an error."),
                    adapter.Error ?? new IOException("The image stream reported an error."));
            }

            progress?.Report(new TransferProgress(partitionName, length, length, 100));
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _tracker.RecordFault();
            throw;
        }
        finally
        {
            _callbacks.ProgressHandler = null;
            adapter.KeepAlive();
        }
    }

    // ------------------------------------------------------------------
    // Calling the engine
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs one native call with cancellation wired up, and turns its status
    /// code into either nothing or an exception.
    /// </summary>
    private void Invoke(string what, CancellationToken ct, Func<int> call)
    {
        ct.ThrowIfCancellationRequested();

        if (_session == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(OdinSession));

        OdinStatus status;

        using (ct.Register(static state => ((OdinSession)state!).Cancel(), this))
        {
            try
            {
                status = (OdinStatus)call();
            }
            finally
            {
                _callbacks.KeepAlive();
            }
        }

        if (status == OdinStatus.Ok)
            return;

        // A cancelled session reports whatever the operation managed to do
        // before it stopped; the flag is the authority on why.
        if (status == OdinStatus.Cancelled || OdinNative.odin_is_cancelled(_session) != 0)
            throw new OperationCanceledException("Cancelled while trying to " + what + ".", ct);

        throw Failure(what, status);
    }

    /// <summary>Invoke, plus the fault bookkeeping every wire operation wants.</summary>
    private Task Guarded(string what, CancellationToken ct, Func<int> call)
    {
        try
        {
            Invoke(what, ct, call);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _tracker.RecordFault();
            throw;
        }
    }

    private void Cancel()
    {
        IntPtr session = _session;

        if (session != IntPtr.Zero)
            OdinNative.odin_cancel(session);
    }

    /// <summary>
    /// Reads the transfer geometry back out of the engine. Before
    /// BeginSession these are BridgeManager.cpp:74-76; after it they are
    /// whatever :530-534 settled on.
    /// </summary>
    private void RefreshGeometry()
    {
        if (_session == IntPtr.Zero)
            return;

        if ((OdinStatus)OdinNative.odin_session_geometry_of(_session,
                out OdinSessionGeometry geometry) != OdinStatus.Ok)
        {
            return;
        }

        FilePartSize = (int)geometry.FilePartSize;
        SequenceMaxLength = (int)geometry.SequenceMaxLength;
        SequenceTimeoutMs = (int)geometry.SequenceTimeoutMs;

        // READ, not inferred. This used to be derived from "the part size is
        // not the default, so the device must have accepted a change", which
        // is an inference with two holes in it: a device that happened to
        // default to 1 MiB would have been reported as having refused, and
        // the value reported was our part size rather than the device's. ABI
        // 3 carries what the handset actually said.
        DeviceDefaultFilePartSize = geometry.DeviceDefaultPartSize;
    }

    private static Exception Failure(string what, OdinStatus status)
    {
        string text = OdinNative.StatusText(status);
        string message = "Failed to " + what + ": " + text + ".";

        return status switch
        {
            OdinStatus.NoDevice => new OdinProtocolException(
                "Failed to " + what + ": no Samsung download-mode device answered. " +
                "Check the cable and that the handset is still in download mode."),
            OdinStatus.InvalidArgument => new ArgumentException(message),
            OdinStatus.InvalidState => new InvalidOperationException(message),
            OdinStatus.Unsupported => new PlatformNotSupportedException(message),
            OdinStatus.OutOfMemory => new OutOfMemoryException(message),
            _ => new OdinProtocolException(message)
        };
    }

    // ------------------------------------------------------------------
    // Teardown
    // ------------------------------------------------------------------

    /// <summary>
    /// Closes the engine's device handle and releases the native session. No
    /// end-session packet: see the class remarks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        IntPtr session = Interlocked.Exchange(ref _session, IntPtr.Zero);

        if (session == IntPtr.Zero)
            return;

        try
        {
            // Releases the interface, re-attaches the Linux kernel driver and
            // closes the handle, all inside ~BridgeManager.
            OdinNative.odin_close(session, _callbacks.Native);
        }
        catch (Exception ex)
        {
            _log.Report(OdinEvent.Warning("Closing the device reported: " + ex.Message, "session"));
        }
        finally
        {
            OdinNative.odin_session_release(session);
            _callbacks.KeepAlive();
        }

        if (_tracker.Phase is not (OdinSessionPhase.SessionEnded or OdinSessionPhase.Faulted))
            _tracker.TransitionTo(OdinSessionPhase.Closed);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void RequireSessionOpen()
    {
        if (_tracker.Phase != OdinSessionPhase.SessionOpen)
        {
            throw new InvalidOperationException(
                "The session is not open. BeginSessionAsync must succeed first; the session is " +
                _tracker.Describe() + ".");
        }
    }
}
