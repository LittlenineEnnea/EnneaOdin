// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall. Heimdall 2.2.2 takes already-loose
// image files on its command line and has no archive reader at all, so neither
// this index nor the streaming flash path it exists for has an upstream
// counterpart.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OdinCore.Firmware;

/// <summary>
/// One member of a tar archive, with the absolute file offset of its data.
/// </summary>
/// <param name="EntryPath">The full path inside the archive, not the base name.</param>
/// <param name="Length">The member's byte count, zero included.</param>
/// <param name="DataOffset">
/// The absolute offset, in the archive file, of this member's first content
/// byte. This is the whole reason the type exists: with it, a member can be
/// opened on its own with one seek.
/// </param>
public sealed record TarIndexEntry(string EntryPath, long Length, long DataOffset);

/// <summary>
/// Walks a tar archive's headers and records where every member's data starts.
/// A streaming flash needs "give me THIS member, now", and answering that by
/// walking from the start each time is quadratic: on a 6.7 GB AP.tar that is
/// hundreds of gigabytes of reading. With an offset it is one seek.
///
/// Hand-written because no tar reader exposes an entry's absolute offset.
/// Cross-checked against SharpZipLib by <see cref="VerifyAgainst"/> before any
/// offset is believed.
/// </summary>
public static class TarIndex
{
    private const int BlockSize = 512;

    // Field offsets inside a 512-byte POSIX/ustar header.
    private const int NameOffset = 0;
    private const int NameLength = 100;
    private const int SizeOffset = 124;
    private const int SizeLength = 12;
    private const int TypeFlagOffset = 156;
    private const int MagicOffset = 257;
    private const int PrefixOffset = 345;
    private const int PrefixLength = 155;

    /// <summary>
    /// Read every regular member's path, length and data offset.
    /// </summary>
    /// <param name="path">The archive on disk.</param>
    /// <param name="payloadLength">
    /// How much of the file is tar. For a .tar.md5 this stops before the
    /// appended digest line, exactly as <see cref="TarArchiveReader"/> does.
    /// </param>
    /// <exception cref="InvalidDataException">The archive is damaged or is not a tar.</exception>
    public static IReadOnlyList<TarIndexEntry> Read(string path, long payloadLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var entries = new List<TarIndexEntry>();

        using FileStream file = File.OpenRead(path);

        long offset = 0;
        byte[] header = new byte[BlockSize];

        // A GNU long name arrives as its own member just before the entry it
        // names, so it has to be carried forward one iteration.
        string? pendingLongName = null;

        while (offset + BlockSize <= payloadLength)
        {
            file.Position = offset;

            if (!ReadExactly(file, header))
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" ends in the middle of a tar header at offset {1}.",
                    Path.GetFileName(path), offset.ToString(CultureInfo.InvariantCulture)));
            }

            offset += BlockSize;

            if (IsAllZero(header))
            {
                // Two zero blocks end the archive. One is not conclusive, but
                // stopping at the first costs nothing: a tar with content after
                // a zero block is not something this program should flash.
                break;
            }

            if (!LooksLikeHeader(header))
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" has no tar magic in the header at offset {1}; it is not a tar archive.",
                    Path.GetFileName(path),
                    (offset - BlockSize).ToString(CultureInfo.InvariantCulture)));
            }

            long size = ReadOctal(header, SizeOffset, SizeLength, path, offset - BlockSize);
            char type = (char)header[TypeFlagOffset];
            long dataOffset = offset;
            long dataBlocks = (size + BlockSize - 1) / BlockSize * BlockSize;

            switch (type)
            {
                case 'L':
                    // GNU long name: this member's CONTENT is the next
                    // member's path.
                    pendingLongName = ReadLongName(file, dataOffset, size, path);
                    break;

                case '0':
                case '\0':
                    entries.Add(new TarIndexEntry(
                        pendingLongName ?? ReadName(header), size, dataOffset));
                    pendingLongName = null;
                    break;

                case '5':   // directory
                case 'x':   // pax extended header for the next entry
                case 'g':   // pax global header
                case 'K':   // GNU long link name
                case '1':   // hard link
                case '2':   // symbolic link
                case '3':   // character device
                case '4':   // block device
                case '6':   // FIFO
                    // Not a regular member. A pax header could in principle
                    // override the next entry's size or path; Samsung's
                    // archives are plain ustar and do not use them, and
                    // VerifyAgainst is what would catch it if one ever did,
                    // because SharpZipLib does honour them and the two lists
                    // would then disagree.
                    pendingLongName = null;
                    break;

                default:
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                        "\"{0}\" has an unrecognised tar entry type '{1}' at offset {2}.",
                        Path.GetFileName(path), type,
                        (offset - BlockSize).ToString(CultureInfo.InvariantCulture)));
            }

            offset = dataOffset + dataBlocks;
        }

        return entries;
    }

    /// <summary>
    /// Assert that this index agrees with <see cref="TarArchiveReader.List"/>:
    /// the same members, in the same order, with the same lengths.
    /// </summary>
    /// <exception cref="InvalidDataException">The two readers disagree.</exception>
    public static void VerifyAgainst(IReadOnlyList<TarIndexEntry> index,
                                     IReadOnlyList<TarMember> reference, string path)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(reference);

        if (index.Count != reference.Count)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\": the offset index found {1} member(s) and the archive reader found {2}. " +
                "The streaming flash is refused rather than reading from an offset nothing agrees with.",
                Path.GetFileName(path),
                index.Count.ToString(CultureInfo.InvariantCulture),
                reference.Count.ToString(CultureInfo.InvariantCulture)));
        }

        for (int i = 0; i < index.Count; i++)
        {
            if (!string.Equals(index[i].EntryPath, reference[i].EntryPath, StringComparison.Ordinal) ||
                index[i].Length != reference[i].Length)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\": member {1} is \"{2}\" of {3} bytes in the offset index and \"{4}\" of " +
                    "{5} bytes in the archive reader.",
                    Path.GetFileName(path), i.ToString(CultureInfo.InvariantCulture),
                    index[i].EntryPath, index[i].Length.ToString(CultureInfo.InvariantCulture),
                    reference[i].EntryPath, reference[i].Length.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private static bool LooksLikeHeader(ReadOnlySpan<byte> header)
    {
        // "ustar\0" with a two-digit version, or GNU's "ustar  \0". Older v7
        // archives have neither, and Samsung does not ship them.
        return header[MagicOffset] == (byte)'u' &&
               header[MagicOffset + 1] == (byte)'s' &&
               header[MagicOffset + 2] == (byte)'t' &&
               header[MagicOffset + 3] == (byte)'a' &&
               header[MagicOffset + 4] == (byte)'r';
    }

    private static string ReadName(ReadOnlySpan<byte> header)
    {
        string name = Trimmed(header.Slice(NameOffset, NameLength));
        string prefix = Trimmed(header.Slice(PrefixOffset, PrefixLength));

        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    private static string ReadLongName(FileStream file, long dataOffset, long size, string path)
    {
        if (size is <= 0 or > 8192)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" has a GNU long-name record of {1} bytes, which is not a path.",
                Path.GetFileName(path), size.ToString(CultureInfo.InvariantCulture)));
        }

        byte[] raw = new byte[size];
        file.Position = dataOffset;

        if (!ReadExactly(file, raw))
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" ends inside a GNU long-name record.", Path.GetFileName(path)));
        }

        return Trimmed(raw);
    }

    private static string Trimmed(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
            end = field.Length;

        return Encoding.UTF8.GetString(field[..end]).TrimEnd(' ');
    }

    private static long ReadOctal(ReadOnlySpan<byte> header, int offset, int length,
                                  string path, long headerOffset)
    {
        ReadOnlySpan<byte> field = header.Slice(offset, length);

        // GNU base-256: the top bit of the first byte marks a binary size, used
        // for members over 8 GiB. The reference firmware's largest member is
        // 5,496,955,290 bytes and does NOT need it, but a 9 GiB super.img on a
        // larger device would, and silently reading one as octal would produce
        // a plausible wrong number rather than an error.
        if ((field[0] & 0x80) != 0)
        {
            long binary = field[0] & 0x7F;

            for (int i = 1; i < field.Length; i++)
                binary = (binary << 8) | field[i];

            return binary;
        }

        long value = 0;
        bool any = false;

        foreach (byte b in field)
        {
            if (b is 0 or (byte)' ')
            {
                if (any)
                    break;

                continue;
            }

            if (b is < (byte)'0' or > (byte)'7')
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" has a non-octal size field in the header at offset {1}.",
                    Path.GetFileName(path), headerOffset.ToString(CultureInfo.InvariantCulture)));
            }

            value = (value * 8) + (b - '0');
            any = true;
        }

        if (!any)
            return 0;

        return value;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> block)
    {
        foreach (byte b in block)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int done = 0;

        while (done < buffer.Length)
        {
            int got = stream.Read(buffer, done, buffer.Length - done);

            if (got <= 0)
                return false;

            done += got;
        }

        return true;
    }
}
