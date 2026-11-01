// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// The log lines this file forwards are produced by Heimdall's own Interface
// (native/vendor/Heimdall/heimdall/source/Interface.cpp), captured by
// native/src/OdinLogCapture.cpp.
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is distributed under the MIT licence; the permission notice in
// native/vendor/Heimdall/LICENSE is retained as that licence requires.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using OdinCore.Diagnostics;

namespace OdinCore.Native;

/// <summary>
/// The callback pair handed to one native call: Heimdall's printed output on
/// its way into the application log, and real byte counts on their way into
/// the progress reporting.
/// </summary>
internal sealed class OdinNativeCallbacks
{
    private readonly OdinLogFn _logFn;
    private readonly OdinProgressFn _progressFn;
    private readonly IProgress<OdinEvent> _sink;
    private readonly string _category;

    private Action<ulong, ulong>? _onProgress;

    internal OdinNativeCallbacks(IProgress<OdinEvent> sink, string category)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _category = category;

        _logFn = OnLog;
        _progressFn = OnProgress;

        Native = new OdinCallbacks
        {
            Log = Marshal.GetFunctionPointerForDelegate(_logFn),
            Progress = Marshal.GetFunctionPointerForDelegate(_progressFn),

            // No user pointer: the state this needs is captured by the
            // instance the two delegates are bound to. A GCHandle would be a
            // second thing to leak.
            User = IntPtr.Zero
        };
    }

    /// <summary>The struct to pass to the ABI. Three pointers, blittable.</summary>
    internal OdinCallbacks Native { get; }

    /// <summary>
    /// Where this call's byte counts go. Set before the call, cleared after,
    /// so a stale handler from a finished operation cannot be fed.
    /// </summary>
    internal Action<ulong, ulong>? ProgressHandler
    {
        get => _onProgress;
        set => _onProgress = value;
    }

    /// <summary>Reports a line of this layer's own, in the same channel and category.</summary>
    internal void Report(OdinLevel level, string message) =>
        _sink.Report(new OdinEvent(DateTimeOffset.Now, level, message, _category));

    /// <summary>
    /// Holds a reference to both delegates. Called in a finally after every
    /// native call; see the delegate-lifetime note on this class.
    /// </summary>
    internal void KeepAlive()
    {
        GC.KeepAlive(_logFn);
        GC.KeepAlive(_progressFn);
    }

    private void OnLog(IntPtr line, IntPtr user)
    {
        try
        {
            string? text = Marshal.PtrToStringUTF8(line);

            if (string.IsNullOrWhiteSpace(text))
                return;

            (OdinLevel level, string message) = Classify(text.TrimEnd());

            if (message.Length == 0)
                return;

            _sink.Report(new OdinEvent(DateTimeOffset.Now, level, message, _category));
        }
        catch
        {
            // A log line is not worth tearing down a flash for, and throwing
            // here would unwind into C++.
        }
    }

    private void OnProgress(ulong transferred, ulong total, IntPtr user)
    {
        try
        {
            _onProgress?.Invoke(transferred, total);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Gives a captured line a level, from the prefix Heimdall's Interface
    /// writes: "ERROR: " at Interface.cpp:142,148 and "WARNING: " at :100,106.
    /// The prefix is then removed, because OdinEvent renders its own.
    /// </summary>
    private static (OdinLevel Level, string Message) Classify(string line)
    {
        const string error = "ERROR: ";
        const string warning = "WARNING: ";

        if (line.StartsWith(error, StringComparison.Ordinal))
            return (OdinLevel.Error, line.Substring(error.Length).Trim());

        if (line.StartsWith(warning, StringComparison.Ordinal))
            return (OdinLevel.Warning, line.Substring(warning.Length).Trim());

        return (OdinLevel.Info, line.Trim());
    }
}

/// <summary>
/// A managed <see cref="Stream"/> presented to the ABI as an odin_stream,
/// which the native side turns into a real FILE * for
/// BridgeManager::SendFile.
/// </summary>
internal sealed class OdinNativeStream
{
    private readonly Stream _stream;
    private readonly OdinStreamReadFn _readFn;
    private readonly OdinStreamSeekFn? _seekFn;

    private byte[] _buffer = Array.Empty<byte>();

    internal OdinNativeStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead)
            throw new ArgumentException("The image stream is not readable.", nameof(stream));

        _readFn = OnRead;

        // Seek is advertised only when the stream can actually do it. The
        // native adapter treats a null seek as a forward-only stream, which is
        // what makes an LZ4 decoder usable without spooling to a temp file.
        if (stream.CanSeek)
            _seekFn = OnSeek;

        Native = new OdinStream
        {
            Read = Marshal.GetFunctionPointerForDelegate(_readFn),
            Seek = _seekFn is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_seekFn),
            User = IntPtr.Zero
        };
    }

    internal OdinStream Native { get; }

    /// <summary>True once a read or a seek failed.</summary>
    internal bool Failed { get; private set; }

    /// <summary>What went wrong, for the message the caller reports.</summary>
    internal Exception? Error { get; private set; }

    /// <summary>How many bytes the native side actually took.</summary>
    internal long BytesRead { get; private set; }

    internal void KeepAlive()
    {
        GC.KeepAlive(_readFn);
        GC.KeepAlive(_seekFn);
    }

    private long OnRead(IntPtr user, IntPtr buffer, ulong count)
    {
        try
        {
            if (buffer == IntPtr.Zero)
                return -1;

            if (count == 0)
                return 0;

            // One part is 131072 bytes before negotiation and 1 MiB after, so
            // the buffer settles at the first read and is then reused.
            int want = count > int.MaxValue ? int.MaxValue : (int)count;

            if (_buffer.Length < want)
                _buffer = new byte[want];

            int read = _stream.Read(_buffer, 0, want);

            if (read > 0)
            {
                Marshal.Copy(_buffer, 0, buffer, read);
                BytesRead += read;
            }

            return read;
        }
        catch (Exception ex)
        {
            Failed = true;
            Error ??= ex;
            return -1;
        }
    }

    private int OnSeek(IntPtr user, ulong offset)
    {
        try
        {
            if (offset > long.MaxValue)
                return -1;

            _stream.Seek((long)offset, SeekOrigin.Begin);
            return 0;
        }
        catch (Exception ex)
        {
            Failed = true;
            Error ??= ex;
            return -1;
        }
    }
}
