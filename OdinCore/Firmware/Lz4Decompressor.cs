// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is NOT derived from Heimdall: the vendored tree contains no lz4
// code at all (Heimdall is handed already-extracted images on its command
// line). It replaces the previous implementation's EstimateLz4Size, which
// guessed a decompressed length from an lz4 frame header and fell back to
// "compressed size times three" when the guess failed.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace OdinCore.Firmware;

/// <summary>
/// Decodes the LZ4 frame members Samsung ships inside Odin archives.
/// </summary>
public static class Lz4Decompressor
{
    /// <summary>LZ4 frame magic, little-endian 0x184D2204.</summary>
    private static ReadOnlySpan<byte> FrameMagic => new byte[] { 0x04, 0x22, 0x4D, 0x18 };

    /// <summary>
    /// True when the first four bytes are an LZ4 frame magic. Used to decide
    /// whether a member actually needs decoding, rather than trusting the
    /// ".lz4" in its name.
    /// </summary>
    public static bool LooksLikeLz4Frame(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[..4].SequenceEqual(FrameMagic);

    /// <summary>
    /// Count a frame's decompressed bytes using every core on the machine.
    /// </summary>
    /// <param name="open">
    /// Opens a fresh, independently seekable view of the frame. Called once
    /// per worker, because a Stream is not safe to share across threads.
    /// </param>
    /// <param name="frameLength">The frame's length in the file.</param>
    /// <param name="what">A name for error messages.</param>
    /// <param name="lanes">How many workers to use. Clamped to at least one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="InvalidDataException">The frame is malformed or truncated.</exception>
    public static async Task<Lz4Measurement> MeasureParallelAsync(Func<Stream> open, long frameLength,
                                                                  string what, int lanes,
                                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);

        Lz4FrameLayout layout;

        using (Stream header = open())
            layout = Lz4FrameIndex.Read(header, frameLength, what);

        if (layout.Blocks.Count == 0)
            return new Lz4Measurement(0, layout.DeclaredContentSize, 0, false);

        // THE FRAME ALREADY SAYS HOW LONG IT IS, so decoding it to find out is
        // work for nothing.
        //
        // An LZ4 frame descriptor carries the uncompressed length when FLG bit
        // 3 is set, and every image in this firmware sets it: super.img.lz4
        // declares 8641324824 bytes in eight bytes of header. Measuring it by
        // decoding meant decompressing 8 GiB before the flash could start, and
        // then decompressing the same 8 GiB again while sending it.
        //
        // The declaration is the compressor's own statement of what it put in,
        // so it is exactly as trustworthy as the frame itself; a wrong one
        // would mean a corrupt archive, which the block walk in
        // Lz4FrameIndex.Read has already rejected, and which the trailing MD5
        // digest catches when that is turned on. The decode path stays for
        // frames that do not declare it.
        if (layout.DeclaredContentSize >= 0)
        {
            return new Lz4Measurement(layout.DeclaredContentSize, layout.DeclaredContentSize,
                                      layout.Blocks.Count, false);
        }

        if (!layout.BlocksIndependent || lanes <= 1 || layout.Blocks.Count < 2)
        {
            long sequential = await Task.Run(() => MeasureSequential(open, what, ct), ct)
                                        .ConfigureAwait(false);

            return new Lz4Measurement(sequential, layout.DeclaredContentSize,
                                      layout.Blocks.Count, false);
        }

        int workers = Math.Max(1, Math.Min(lanes, layout.Blocks.Count));
        var lengths = new long[layout.Blocks.Count];

        await Parallel.ForEachAsync(
            Partition(layout.Blocks.Count, workers),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            async (range, token) =>
            {
                await Task.Run(() =>
                {
                    using Stream view = open();

                    // One output buffer per worker, sized to the frame's own
                    // declared maximum, reused for every block it handles.
                    // Both sized from the frame's own maximum, which
                    // Lz4FrameIndex has already checked every block against.
                    byte[] output = new byte[layout.BlockMaxSize];
                    byte[] input = new byte[layout.BlockMaxSize];

                    for (int i = range.Start; i < range.End; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        lengths[i] = MeasureBlock(view, layout.Blocks[i], input, output, what);
                    }
                }, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        long total = 0;

        foreach (long length in lengths)
            total += length;

        return new Lz4Measurement(total, layout.DeclaredContentSize, layout.Blocks.Count, true);
    }

    /// <summary>Contiguous block ranges, one per worker.</summary>
    private static IEnumerable<(int Start, int End)> Partition(int count, int workers)
    {
        int per = count / workers;
        int extra = count % workers;
        int start = 0;

        for (int w = 0; w < workers; w++)
        {
            int size = per + (w < extra ? 1 : 0);

            if (size == 0)
                continue;

            yield return (start, start + size);
            start += size;
        }
    }

    private static long MeasureBlock(Stream view, Lz4Block block, byte[] input, byte[] output,
                                     string what)
    {
        if (block.Stored)
        {
            // Stored blocks are their own output. Nothing to decode, and
            // nothing to read either: the length is in the header.
            return block.CompressedLength;
        }

        view.Position = block.DataOffset;

        int done = 0;
        while (done < block.CompressedLength)
        {
            int got = view.Read(input, done, block.CompressedLength - done);

            if (got <= 0)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" ends inside the block at offset {1}.", what, block.DataOffset));
            }

            done += got;
        }

        int decoded = LZ4Codec.Decode(input, 0, block.CompressedLength, output, 0, output.Length);

        if (decoded < 0)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" has a corrupt LZ4 block at offset {1}.", what, block.DataOffset));
        }

        return decoded;
    }

    private static long MeasureSequential(Func<Stream> open, string what, CancellationToken ct)
    {
        try
        {
            using Stream source = open();
            using var decoder = LZ4Stream.Decode(source, leaveOpen: true);

            byte[] buffer = new byte[1 << 20];
            long total = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int got = decoder.Read(buffer, 0, buffer.Length);

                if (got <= 0)
                    break;

                total += got;
            }

            return total;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is not a readable LZ4 frame: {1}", what, ex.Message), ex);
        }
    }

    /// <summary>
    /// Decode <paramref name="source"/> into <paramref name="destinationPath"/>
    /// and return the exact number of bytes written.
    /// </summary>
    /// <exception cref="InvalidDataException">The frame is malformed or truncated.</exception>
    public static async Task<long> DecompressToFileAsync(Stream source, string destinationPath,
                                                         CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        try
        {
            await using var decoder = LZ4Stream.Decode(source, leaveOpen: true);
            await using FileStream output = File.Create(destinationPath);

            await decoder.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);

            return output.Length;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            // A corrupt or truncated frame surfaces from K4os as any of several
            // exception types. Turning it into one named failure means the
            // caller can say "this .lz4 member is damaged" instead of
            // forwarding a library's internal wording.
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "Failed to decompress an LZ4 member into \"{0}\": {1}",
                Path.GetFileName(destinationPath), ex.Message), ex);
        }
    }
}

/// <summary>What a measuring pass found.</summary>
/// <param name="Length">The counted decompressed length. This is what the flash uses.</param>
/// <param name="DeclaredContentSize">
/// What the frame claimed, or -1 when it claimed nothing. Compared against
/// <paramref name="Length"/> purely so a disagreement can be reported.
/// </param>
/// <param name="BlockCount">How many blocks the frame holds.</param>
/// <param name="Parallel">True when the blocks were decoded concurrently.</param>
public readonly record struct Lz4Measurement(long Length, long DeclaredContentSize,
                                             int BlockCount, bool Parallel)
{
    /// <summary>
    /// True when the frame declared a size and that size is wrong. Not an
    /// error on its own - the counted length is used either way - but it means
    /// the archive misdescribes itself, which is worth a line in the log.
    /// </summary>
    public bool DeclarationDisagrees => DeclaredContentSize >= 0 && DeclaredContentSize != Length;
}
