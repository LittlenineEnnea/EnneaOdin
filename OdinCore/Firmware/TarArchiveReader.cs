// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is NOT derived from Heimdall: the vendored tree contains no tar
// code at all. It replaces the previous implementation's TarInspector, whose
// enumeration swallowed every exception into a partial list, collapsed each
// entry to its base name and dropped zero-length members.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using ICSharpCode.SharpZipLib.Tar;

namespace OdinCore.Firmware;

/// <summary>One member of a tar archive, as the archive describes it.</summary>
/// <param name="EntryPath">The full path inside the archive, not the base name.</param>
/// <param name="Length">The member's byte count, zero included.</param>
public sealed record TarMember(string EntryPath, long Length);

/// <summary>
/// Reads Odin .tar and .tar.md5 archives.
/// </summary>
public static class TarArchiveReader
{
    /// <summary>Offset and value of the ustar magic in a tar header block.</summary>
    private const int UstarMagicOffset = 257;

    private static ReadOnlySpan<byte> UstarMagic => "ustar"u8;

    /// <summary>
    /// Sniff whether a file begins with a tar header, rather than trusting its
    /// extension. A ".tar.md5" that is actually a zip is a common download
    /// mistake, and it must be named as such instead of producing an empty
    /// member list.
    /// </summary>
    public static bool LooksLikeTar(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream stream = File.OpenRead(path);
        if (stream.Length < 512)
            return false;

        Span<byte> header = stackalloc byte[512];
        int read = 0;
        while (read < header.Length)
        {
            int n = stream.Read(header[read..]);
            if (n <= 0)
                return false;

            read += n;
        }

        return header.Slice(UstarMagicOffset, UstarMagic.Length).SequenceEqual(UstarMagic);
    }

    /// <summary>
    /// List every member. <paramref name="payloadLength"/> bounds the read so
    /// a trailing MD5 line is not parsed as archive content.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is damaged or is not a tar.</exception>
    public static IReadOnlyList<TarMember> List(string path, long payloadLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var members = new List<TarMember>();

        using FileStream file = File.OpenRead(path);
        using var bounded = new BoundedReadStream(file, payloadLength, leaveOpen: true);
        using var tar = new TarInputStream(bounded, Encoding.UTF8);

        try
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.IsDirectory)
                    continue;

                members.Add(new TarMember(entry.Name, entry.Size));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" could not be read as a tar archive: {1}", Path.GetFileName(path), ex.Message), ex);
        }

        return members;
    }

    /// <summary>
    /// Extract every member, calling <paramref name="onMember"/> once per
    /// member with its entry path and a stream positioned at its content.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is damaged or is not a tar.</exception>
    public static async Task ExtractAsync(string path, long payloadLength,
        Func<TarMember, Stream, CancellationToken, Task> onMember, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(onMember);

        await using FileStream file = File.OpenRead(path);
        await using var bounded = new BoundedReadStream(file, payloadLength, leaveOpen: true);
        using var tar = new TarInputStream(bounded, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TarEntry? entry;
            try
            {
                entry = tar.GetNextEntry();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" is a damaged tar archive: {1}", Path.GetFileName(path), ex.Message), ex);
            }

            if (entry is null)
                break;

            if (entry.IsDirectory)
                continue;

            // TarInputStream reads the current entry's content and stops at its
            // end, so it can be handed to the callback directly.
            await onMember(new TarMember(entry.Name, entry.Size), tar, ct).ConfigureAwait(false);
        }
    }
}
