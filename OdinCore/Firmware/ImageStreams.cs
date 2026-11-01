// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall, but it exists because of SendFile: a retry seeks
// back to the failed part's own offset, so a stream that cannot go back one
// packet cannot survive one. That is what RewindStream is for.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.IO;

namespace OdinCore.Firmware;

/// <summary>
/// A seekable window over part of a file: one tar member, read in place.
/// </summary>
internal sealed class FileRegionStream : Stream
{
    private readonly FileStream _file;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    internal FileRegionStream(string path, long start, long length)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // 1 MiB, and buffered reads only work if the position is left alone;
        // see Read below.
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                               bufferSize: 1 << 20, useAsync: false);
        _start = start;
        _length = length;

        if (_start + _length > _file.Length)
        {
            _file.Dispose();
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is {1} bytes but a member was indexed at offset {2} with length {3}.",
                Path.GetFileName(path),
                _file.Length.ToString(CultureInfo.InvariantCulture),
                start.ToString(CultureInfo.InvariantCulture),
                length.ToString(CultureInfo.InvariantCulture)));
        }

        _file.Position = _start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;

        if (remaining <= 0)
            return 0;

        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];

        // Seek ONLY when the handle is not already where it should be.
        //
        // This used to assign _file.Position unconditionally, with a comment
        // about not depending on nobody else having moved the cursor. The
        // comment was fine and the code was not: assigning Position discards
        // a FileStream's read buffer whether or not the value changed, so
        // every single read became a fresh syscall at a fresh offset and the
        // 1 MiB buffer above did nothing at all. Measured on the reference
        // firmware, the parallel measuring pass went from 14.5 s to 24.4 s at
        // 163% CPU because of this line - it was I/O-starved, not compute
        // bound.
        //
        // The guard keeps the original intent: if anything did move the
        // handle, this notices and corrects it.
        long wanted = _start + _position;

        if (_file.Position != wanted)
            _file.Position = wanted;

        int got = _file.Read(buffer);
        _position += got;
        return got;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _length)
        {
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "Seek to {0} is outside this member, which is {1} bytes.",
                target.ToString(CultureInfo.InvariantCulture),
                _length.ToString(CultureInfo.InvariantCulture)));
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>
/// A forward-only stream made seekable backwards for a bounded distance, by
/// keeping the bytes it last handed out. An LZ4 decoder cannot seek; SendFile's
/// retry goes back at most one packet, so the window only has to beat that.
/// A seek past the window throws rather than returning the wrong bytes.
/// </summary>
internal sealed class RewindStream : Stream
{
    /// <summary>
    /// 32 MiB: four times the 8 MiB file part the protocol settles on.
    ///
    /// THIS IS COUPLED TO THE ENGINE. A retry re-reads the whole part that
    /// failed, so the window has to be at least one part; four gives room for
    /// a retry that spans a part boundary. The engine's part size is
    /// fileTransferPacketSize in BridgeManager::BeginSession, and raising it
    /// without raising this throws "re-read from N bytes back" on the first
    /// retry of a big image. The window was 4 MiB when the part was 1 MiB.
    /// windowtest checks the two against each other so this cannot drift.
    /// </summary>
    internal const int DefaultWindowBytes = 32 << 20;

    private readonly Stream _inner;
    private readonly long _length;
    private readonly byte[] _window;

    // The ring buffer's write cursor, and how much of it is live.
    private int _cursor;
    private int _buffered;

    // How many bytes have been pulled out of the inner stream: the true
    // position of the decoder. Never goes backwards.
    private long _physical;

    // Where this stream claims to be. Can be behind _physical, by at most
    // _buffered.
    private long _position;

    private readonly bool _leaveOpen;

    internal RewindStream(Stream inner, long length, bool leaveOpen = false,
                          int windowBytes = DefaultWindowBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowBytes, 1 << 16);

        _length = length;
        _window = new byte[windowBytes];
        _leaveOpen = leaveOpen;
    }

    /// <summary>How far back a seek may go from the furthest point read.</summary>
    internal int WindowBytes => _window.Length;

    /// <summary>
    /// How many times a read was served out of the window rather than the
    /// decoder, which is to say how many retries this transfer survived.
    /// Reported in the log after each image.
    /// </summary>
    internal int RewindCount { get; private set; }

    public override bool CanRead => true;

    /// <summary>
    /// True, and that is a claim about the window and not about the decoder.
    /// OdinCore.Native.OdinNativeStream advertises the seek callback to the
    /// native side only when this is true, and native/src/OdinStreamFile.cpp
    /// calls it only for a genuine backwards jump.
    /// </summary>
    public override bool CanSeek => true;

    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;

        if (_position >= _length)
            return 0;

        if (_position < _physical)
            return ReadFromWindow(buffer);

        if (_position > _physical)
        {
            // A forward jump over bytes that were never read. Nothing in the
            // transfer loop does this, and silently skipping decoder output
            // would put a hole in a partition, so it is refused.
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "A forward seek to {0} skipped {1} byte(s) of a forward-only image stream. " +
                "That would leave a hole in the partition, so it is refused.",
                _position.ToString(CultureInfo.InvariantCulture),
                (_position - _physical).ToString(CultureInfo.InvariantCulture)));
        }

        long remaining = _length - _position;

        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];

        int got = _inner.Read(buffer);

        if (got <= 0)
            return 0;

        Remember(buffer[..got]);
        _physical += got;
        _position = _physical;
        return got;
    }

    private int ReadFromWindow(Span<byte> buffer)
    {
        long behind = _physical - _position;

        if (behind > _buffered)
        {
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "This image is being streamed rather than spooled to disk, and a retry asked to " +
                "re-read from {0} bytes back. The rewind window is {1} bytes. The transfer is " +
                "stopped rather than sending the wrong bytes for the partition.",
                behind.ToString(CultureInfo.InvariantCulture),
                _buffered.ToString(CultureInfo.InvariantCulture)));
        }

        int want = (int)Math.Min(buffer.Length, behind);

        // The window is a ring. The bytes wanted start "behind" bytes before
        // the cursor, which may wrap.
        int start = _cursor - (int)behind;
        if (start < 0)
            start += _window.Length;

        int firstRun = Math.Min(want, _window.Length - start);
        _window.AsSpan(start, firstRun).CopyTo(buffer);

        if (firstRun < want)
            _window.AsSpan(0, want - firstRun).CopyTo(buffer[firstRun..]);

        _position += want;
        RewindCount++;
        return want;
    }

    private void Remember(ReadOnlySpan<byte> data)
    {
        if (data.Length >= _window.Length)
        {
            // A single read larger than the whole window: keep its tail. The
            // transfer never does this - a part is at most 1 MiB and the window
            // is 4 - but a window that silently held nothing would be worse
            // than one that holds what it can.
            data[^_window.Length..].CopyTo(_window);
            _cursor = 0;
            _buffered = _window.Length;
            return;
        }

        int firstRun = Math.Min(data.Length, _window.Length - _cursor);
        data[..firstRun].CopyTo(_window.AsSpan(_cursor));

        if (firstRun < data.Length)
            data[firstRun..].CopyTo(_window.AsSpan(0));

        _cursor = (_cursor + data.Length) % _window.Length;
        _buffered = Math.Min(_buffered + data.Length, _window.Length);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _length)
        {
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "Seek to {0} is outside this image, which is {1} bytes.",
                target.ToString(CultureInfo.InvariantCulture),
                _length.ToString(CultureInfo.InvariantCulture)));
        }

        // The seek itself is lazy, exactly as the native adapter's is: it
        // records where the caller wants to be and lets the next Read decide
        // whether that is a window hit, a continuation, or an error. A seek
        // that threw here would fail the "seek to end, seek straight back"
        // pattern that costs nothing and moves nothing.
        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
