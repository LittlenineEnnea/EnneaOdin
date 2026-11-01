// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall. Heimdall 2.2.2 takes loose image
// files and has no decompressor at all.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OdinCore.Firmware;

/// <summary>One compressed block inside an LZ4 frame.</summary>
/// <param name="DataOffset">Absolute offset of the block's payload in the file.</param>
/// <param name="CompressedLength">Payload length in bytes.</param>
/// <param name="Stored">
/// True when the block is stored uncompressed, which LZ4 does for a block that
/// did not compress. Its uncompressed length is then its compressed length.
/// </param>
public sealed record Lz4Block(long DataOffset, int CompressedLength, bool Stored);

/// <summary>
/// An LZ4 frame's block layout, read from its headers without decompressing.
///
/// FLG bit 5 is block independence: when set, blocks decode in any order, so
/// the decompressed length of one 5 GiB member can be counted on every core
/// instead of one. Samsung sets it. This walks the block headers - a seek and
/// four bytes each, no payload - and hands back where every block is;
/// <see cref="Lz4Decompressor.MeasureParallelAsync"/> decodes them.
///
/// The declared content size is read but never trusted: the length the flash
/// uses is always counted.
/// </summary>
public static class Lz4FrameIndex
{
    private const uint FrameMagic = 0x184D2204;
    private const uint SkippableMagicMask = 0xFFFFFFF0;
    private const uint SkippableMagicBase = 0x184D2A50;

    /// <summary>The four block maximum sizes LZ4 defines, indexed by the BD field.</summary>
    private static readonly int[] BlockMaxSizes = { 0, 0, 0, 0, 64 * 1024, 256 * 1024, 1 << 20, 4 << 20 };

    /// <summary>
    /// Walk one LZ4 frame's headers.
    /// </summary>
    /// <param name="stream">Positioned at the frame magic. Must be seekable.</param>
    /// <param name="frameLength">How many bytes of the stream belong to the frame.</param>
    /// <param name="what">A name for error messages.</param>
    /// <exception cref="InvalidDataException">Not a readable LZ4 frame.</exception>
    public static Lz4FrameLayout Read(Stream stream, long frameLength, string what)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
            throw new ArgumentException("The frame index needs a seekable stream.", nameof(stream));

        long origin = stream.Position;
        Span<byte> head = stackalloc byte[19];

        if (!ReadExactly(stream, head[..7]))
            throw Bad(what, "the file is shorter than an LZ4 frame descriptor");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);

        if (magic != FrameMagic)
        {
            throw Bad(what, string.Format(CultureInfo.InvariantCulture,
                "the magic is 0x{0:X8}, not 0x{1:X8}", magic, FrameMagic));
        }

        byte flg = head[4];
        byte bd = head[5];

        int version = (flg >> 6) & 3;

        if (version != 1)
        {
            throw Bad(what, string.Format(CultureInfo.InvariantCulture,
                "frame version {0} is not supported", version));
        }

        bool blocksIndependent = ((flg >> 5) & 1) != 0;
        bool blockChecksums = ((flg >> 4) & 1) != 0;
        bool hasContentSize = ((flg >> 3) & 1) != 0;
        bool contentChecksum = ((flg >> 2) & 1) != 0;
        bool hasDictionaryId = (flg & 1) != 0;

        int blockMaxIndex = (bd >> 4) & 7;
        int blockMaxSize = BlockMaxSizes[blockMaxIndex];

        if (blockMaxSize == 0)
        {
            throw Bad(what, string.Format(CultureInfo.InvariantCulture,
                "block maximum size code {0} is reserved", blockMaxIndex));
        }

        // The descriptor is 2 bytes plus the optional fields plus the 1-byte
        // header checksum. Seek past it rather than re-reading: the two
        // optional fields are the only variable part.
        int descriptorLength = 2 + (hasContentSize ? 8 : 0) + (hasDictionaryId ? 4 : 0) + 1;
        long declaredContentSize = -1;

        if (hasContentSize)
        {
            stream.Position = origin + 6;
            Span<byte> size = stackalloc byte[8];

            if (!ReadExactly(stream, size))
                throw Bad(what, "the frame ends inside its content-size field");

            declaredContentSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(size);
        }

        long position = origin + 4 + descriptorLength;
        long frameEnd = origin + frameLength;
        var blocks = new List<Lz4Block>();
        Span<byte> blockHeader = stackalloc byte[4];

        while (true)
        {
            if (position + 4 > frameEnd)
                throw Bad(what, "the frame ends without an end mark");

            stream.Position = position;

            if (!ReadExactly(stream, blockHeader))
                throw Bad(what, "the frame ends inside a block header");

            uint word = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
            position += 4;

            if (word == 0)
                break;   // EndMark

            bool stored = (word & 0x80000000u) != 0;
            int compressed = (int)(word & 0x7FFFFFFFu);

            if (compressed <= 0 || position + compressed > frameEnd)
            {
                throw Bad(what, string.Format(CultureInfo.InvariantCulture,
                    "a block at offset {0} claims {1} bytes, which does not fit in the frame",
                    position - 4, compressed));
            }

            // Both kinds are bounded. A stored block IS its own output, so it
            // cannot exceed the maximum. A compressed block cannot either:
            // LZ4 stores a block that did not compress rather than emitting a
            // larger one. Without this check a malformed frame claiming a
            // huge compressed block reached the decoder, whose input buffer is
            // sized from blockMaxSize, and came out as an ArgumentException
            // from deep inside a read instead of "this frame is malformed".
            if (compressed > blockMaxSize)
            {
                throw Bad(what, string.Format(CultureInfo.InvariantCulture,
                    "a {0} block at offset {1} is {2} bytes, over the {3}-byte maximum",
                    stored ? "stored" : "compressed", position - 4, compressed, blockMaxSize));
            }

            blocks.Add(new Lz4Block(position, compressed, stored));
            position += compressed;

            if (blockChecksums)
                position += 4;
        }

        if (contentChecksum)
            position += 4;

        return new Lz4FrameLayout(blocks, blockMaxSize, blocksIndependent, blockChecksums,
                                  declaredContentSize, position - origin);
    }

    private static InvalidDataException Bad(string what, string why) =>
        new(string.Format(CultureInfo.InvariantCulture,
            "\"{0}\" is not a readable LZ4 frame: {1}.", what, why));

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int done = 0;

        while (done < buffer.Length)
        {
            int got = stream.Read(buffer[done..]);

            if (got <= 0)
                return false;

            done += got;
        }

        return true;
    }
}

/// <summary>What <see cref="Lz4FrameIndex.Read"/> found.</summary>
/// <param name="Blocks">Every block, in order.</param>
/// <param name="BlockMaxSize">The largest a block may decode to.</param>
/// <param name="BlocksIndependent">
/// True when FLG bit 5 is set, which is what makes parallel decoding valid. A
/// linked-block frame MUST be decoded in order and this being false is what
/// sends it down the sequential path.
/// </param>
/// <param name="BlockChecksums">True when each block carries a 4-byte checksum.</param>
/// <param name="DeclaredContentSize">
/// The frame's own claim about its decompressed size, or -1 when it does not
/// make one. Used only as a cross-check; the flash uses a counted length.
/// </param>
/// <param name="FrameLength">The frame's total length, as walked.</param>
public sealed record Lz4FrameLayout(IReadOnlyList<Lz4Block> Blocks, int BlockMaxSize,
                                    bool BlocksIndependent, bool BlockChecksums,
                                    long DeclaredContentSize, long FrameLength);
