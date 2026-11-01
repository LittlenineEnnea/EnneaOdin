// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is NOT derived from Heimdall. A grep over the whole vendored tree,
// the Qt frontend included, finds no tar, no lz4 and no md5 code: Heimdall
// takes already-extracted partition images on its command line and never looks
// inside an Odin archive. The firmware-package layer is therefore new code,
// and the .tar.md5 convention it implements is not derivable from the vendored
// sources. See the remarks on ArchiveDigestReader.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OdinCore.Diagnostics;

namespace OdinCore.Firmware;

/// <summary>
/// What is known about an Odin archive's trailing MD5 line. Four states, not a
/// bool: "present but not checked" and "checked and wrong" are different
/// facts, and a bool that conflated them made every .tar.md5 look damaged
/// whenever verification was turned off.
/// </summary>
public enum DigestOutcome
{
    /// <summary>No trailing digest line; the file is a plain tar.</summary>
    Absent,

    /// <summary>A digest is there, and nothing has hashed the payload.</summary>
    NotChecked,

    /// <summary>Hashed, and the payload matches.</summary>
    Matched,

    /// <summary>Hashed, and the payload does not match. The archive is damaged.</summary>
    Mismatched
}

/// <summary>
/// The outcome of looking for, and checking, an Odin archive's trailing MD5
/// line.
/// </summary>
/// <param name="Outcome">What is known. See <see cref="DigestOutcome"/>.</param>
/// <param name="Expected">The digest from the file, lower-case hex.</param>
/// <param name="Actual">The digest computed over the payload, lower-case hex.</param>
/// <param name="PayloadLength">How many leading bytes of the file are the tar payload.</param>
public sealed record ArchiveDigest(DigestOutcome Outcome, string? Expected, string? Actual,
                                   long PayloadLength)
{
    /// <summary>A trailing digest line was found.</summary>
    public bool Present => Outcome != DigestOutcome.Absent;

    /// <summary>
    /// The payload was hashed and did NOT match. This is the only state that
    /// may stop a flash: not checking is not the same as failing.
    /// </summary>
    public bool IsDamaged => Outcome == DigestOutcome.Mismatched;

    public string Describe() => Outcome switch
    {
        DigestOutcome.Absent => "no trailing MD5 digest present",
        DigestOutcome.NotChecked => "trailing MD5 digest present, not checked",
        DigestOutcome.Matched => "trailing MD5 digest verified",
        _ => string.Format(CultureInfo.InvariantCulture,
            "trailing MD5 digest MISMATCH: file says {0}, payload is {1}",
            Expected ?? "(none)", Actual ?? "(none)")
    };
}

/// <summary>
/// Locates and checks the trailing digest that Samsung's .tar.md5 files carry.
/// </summary>
public static class ArchiveDigestReader
{
    /// <summary>How many trailing bytes are searched for the digest line.</summary>
    public const int TailScanBytes = 1024;

    private static readonly Regex DigestLine = new(
        @"^(?<hash>[0-9a-fA-F]{32})[ \t]+\*?(?<name>\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Inspect <paramref name="path"/>, and verify the digest if there is one.
    /// Never throws for a missing or malformed digest; does propagate a real
    /// I/O failure, because an unreadable file is not an absent digest.
    /// </summary>
    /// <summary>
    /// Find where the tar payload ends and what digest, if any, follows it.
    /// </summary>
    public static async Task<(string? Expected, long PayloadLength)> FindPayloadAsync(
        string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var info = new FileInfo(path);
        long length = info.Length;

        long payloadLength = length;
        string? expected = null;

        await using FileStream tail = File.OpenRead(path);

        int scan = (int)Math.Min(TailScanBytes, length);

        if (scan > 0)
        {
            var buffer = new byte[scan];
            tail.Position = length - scan;
            await ReadExactlyAsync(tail, buffer, ct).ConfigureAwait(false);

            (expected, payloadLength) = FindDigest(buffer, length - scan, length);
        }

        return (expected, payloadLength);
    }

    /// <summary>
    /// Hash the payload and compare it with the digest the archive carries.
    /// </summary>
    public static async Task<ArchiveDigest> VerifyAsync(string path, string expected,
                                                        long payloadLength,
                                                        IProgress<OdinEvent> log,
                                                        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(expected);
        ArgumentNullException.ThrowIfNull(log);

        string actual = await ComputeMd5Async(path, payloadLength, ct).ConfigureAwait(false);
        bool verified = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        var result = new ArchiveDigest(
            verified ? DigestOutcome.Matched : DigestOutcome.Mismatched,
            expected.ToLowerInvariant(), actual, payloadLength);

        if (verified)
        {
            log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "{0}: trailing MD5 digest verified over {1} bytes.",
                Path.GetFileName(path), payloadLength), "firmware"));
        }
        else
        {
            log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "{0}: trailing MD5 digest MISMATCH. The file says {1}; the first {2} bytes hash to {3}. The archive is damaged.",
                Path.GetFileName(path), result.Expected, payloadLength, actual), "firmware"));
        }

        return result;
    }

    public static async Task<ArchiveDigest> InspectAsync(string path, IProgress<OdinEvent> log,
                                                         CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);

        long length = new FileInfo(path).Length;

        (string? expected, long payloadLength) =
            await FindPayloadAsync(path, ct).ConfigureAwait(false);

        if (expected is null)
        {
            log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "{0}: no trailing MD5 digest in the last {1} bytes; reading it as a plain tar.",
                Path.GetFileName(path), Math.Min(TailScanBytes, length)), "firmware"));

            return new ArchiveDigest(DigestOutcome.Absent, null, null, length);
        }

        string actual = await ComputeMd5Async(path, payloadLength, ct).ConfigureAwait(false);
        bool verified = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        var result = new ArchiveDigest(
            verified ? DigestOutcome.Matched : DigestOutcome.Mismatched,
            expected.ToLowerInvariant(), actual, payloadLength);

        if (verified)
        {
            log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "{0}: trailing MD5 digest verified over {1} bytes.",
                Path.GetFileName(path), payloadLength), "firmware"));
        }
        else
        {
            log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "{0}: trailing MD5 digest MISMATCH. The file says {1}; the first {2} bytes hash to {3}. The archive is damaged.",
                Path.GetFileName(path), result.Expected, payloadLength, actual), "firmware"));
        }

        return result;
    }

    /// <summary>
    /// Scan a tail buffer for the last digest line, returning its hash and the
    /// payload length, which is the file offset at which that line begins.
    /// </summary>
    private static (string? Hash, long PayloadLength) FindDigest(ReadOnlySpan<byte> tail, long tailFileOffset,
                                                                long fileLength)
    {
        for (int start = tail.Length - 1; start >= 0; start--)
        {
            if (start != 0 && !IsSeparator(tail[start - 1]))
                continue;

            int end = start;
            while (end < tail.Length && !IsSeparator(tail[end]))
                end++;

            // 32 hex characters, white space, an optional star and a name:
            // nothing shorter can be a digest line.
            if (end - start < 34)
                continue;

            if (!IsOnlyPadding(tail[end..]))
                continue;

            string? text = TryDecodeAscii(tail[start..end]);
            if (text is null)
                continue;

            Match m = DigestLine.Match(text.Trim());
            if (m.Success)
                return (m.Groups["hash"].Value, tailFileOffset + start);
        }

        return (null, fileLength);
    }

    private static bool IsSeparator(byte b) => b == (byte)'\n' || b == (byte)'\r' || b == 0;

    private static bool IsOnlyPadding(ReadOnlySpan<byte> rest)
    {
        foreach (byte b in rest)
        {
            if (b != 0 && b != (byte)'\n' && b != (byte)'\r' && b != (byte)' ' && b != (byte)'\t')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Decode a candidate line, rejecting it outright if it holds anything
    /// that is not printable ASCII or a tab. A line of binary rubbish must not
    /// be fed to the regular expression at all.
    /// </summary>
    private static string? TryDecodeAscii(ReadOnlySpan<byte> line)
    {
        foreach (byte b in line)
        {
            bool printable = b >= 0x20 && b < 0x7F;
            if (!printable && b != (byte)'\t')
                return null;
        }

        return Encoding.ASCII.GetString(line);
    }

    private static async Task<string> ComputeMd5Async(string path, long payloadLength, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        await using var bounded = new BoundedReadStream(stream, payloadLength, leaveOpen: true);

        byte[] hash = await MD5.HashDataAsync(bounded, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException("The archive ended earlier than its length said it would.");

            read += n;
        }
    }
}

/// <summary>
/// A read-only view of the first N bytes of another stream, so the tar reader
/// and the MD5 pass both stop exactly at the payload boundary instead of
/// reading the digest line as archive content.
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly bool _leaveOpen;
    private long _position;

    internal BoundedReadStream(Stream inner, long limit, bool leaveOpen)
    {
        _inner = inner;
        _limit = limit;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _limit;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _limit - _position;
        if (remaining <= 0)
            return 0;

        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];

        int n = _inner.Read(buffer);
        _position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
