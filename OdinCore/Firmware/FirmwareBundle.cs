// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is NOT derived from Heimdall: the vendored tree has no zip, tar,
// lz4 or md5 code at all, because heimdall is handed already-extracted archives
// on its command line. Reading a distributor's firmware zip is therefore new
// code rather than a port.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO.Compression;
using OdinCore.Diagnostics;

namespace OdinCore.Firmware;

/// <summary>
/// Which of the two regional archives fills the CSC slot.
/// </summary>
public enum CscMode
{
    /// <summary>
    /// The plain CSC archive. Writes the regional configuration AND the user
    /// data partition, so it ERASES USER DATA. This is Odin's own default and
    /// the one to use when changing region or recovering a broken device.
    /// </summary>
    Csc,

    /// <summary>
    /// The HOME_CSC archive. Writes the regional configuration but leaves the
    /// user data partition alone, so an update keeps the user's data.
    /// </summary>
    HomeCsc
}

/// <summary>
/// One archive inside a firmware bundle, as listed from the zip's central
/// directory. Nothing has been extracted at this point.
/// </summary>
/// <param name="Slot">The slot this archive fills.</param>
/// <param name="IsHomeCsc">
/// True for the HOME_CSC archive. Only meaningful when <paramref name="Slot"/>
/// is <see cref="FirmwareSlot.Csc"/>: both CSC archives fill the same slot and
/// are mutually exclusive.
/// </param>
/// <param name="EntryPath">The entry's full path inside the zip.</param>
/// <param name="EntryName">The entry's file name.</param>
/// <param name="Length">Uncompressed size in bytes, from the central directory.</param>
/// <param name="CompressedLength">Stored size in bytes.</param>
public sealed record BundleEntry(
    FirmwareSlot Slot,
    bool IsHomeCsc,
    string EntryPath,
    string EntryName,
    long Length,
    long CompressedLength)
{
    /// <summary>"BL", "AP", "CP", "CSC", "HOME_CSC", "USERDATA".</summary>
    public string Label => IsHomeCsc ? "HOME_CSC" : FirmwarePackage.SlotLabel(Slot);

    /// <summary>
    /// True when flashing this archive destroys user data: USERDATA always, and
    /// the plain CSC archive, which is why HOME_CSC exists.
    /// </summary>
    public bool ErasesUserData =>
        Slot == FirmwareSlot.UserData || (Slot == FirmwareSlot.Csc && !IsHomeCsc);
}

/// <summary>
/// A firmware zip as shipped by a distributor (SamFw, SamMobile, Frija and the
/// rest all use the same shape): six .tar.md5 archives named by slot, plus a
/// small text file.
/// </summary>
public sealed class FirmwareBundle : IDisposable
{
    // Longest prefix first: "HOME_CSC_" also starts with... nothing shared with
    // "CSC_", but "CSC_" IS a suffix-position match inside "HOME_CSC_", and a
    // naive StartsWith over an unordered list would still be fine. What is NOT
    // fine is testing "CSC" with Contains, which matches HOME_CSC too. The
    // ordering here is kept explicit so a later edit cannot reintroduce that.
    private static readonly (string Prefix, FirmwareSlot Slot, bool HomeCsc)[] SlotPrefixes =
    {
        ("HOME_CSC_", FirmwareSlot.Csc, true),
        ("USERDATA_", FirmwareSlot.UserData, false),
        ("CSC_", FirmwareSlot.Csc, false),
        ("BL_", FirmwareSlot.Bl, false),
        ("AP_", FirmwareSlot.Ap, false),
        ("CP_", FirmwareSlot.Cp, false)
    };

    private readonly ZipArchive _archive;

    // Kept so a parallel extraction can open the zip once PER WORKER.
    // ZipArchive is not thread-safe: its entries share one underlying stream,
    // so two concurrent Open() calls on the same archive corrupt each other.
    private readonly string _path;

    private readonly IProgress<OdinEvent> _log;
    private bool _disposed;

    private FirmwareBundle(string path, ZipArchive archive,
        IReadOnlyList<BundleEntry> entries, string? info, IProgress<OdinEvent> log)
    {
        Path = path;
        _path = path;
        _archive = archive;
        Entries = entries;
        InfoText = info;
        _log = log;
    }

    /// <summary>The zip on the user's disk.</summary>
    public string Path { get; }

    /// <summary>Every archive the zip contains, in slot order, longest first within a slot.</summary>
    public IReadOnlyList<BundleEntry> Entries { get; }

    /// <summary>
    /// The contents of the distributor's information text file, if there is one.
    /// It is not trusted for anything; it is shown so the user can read it.
    /// </summary>
    public string? InfoText { get; }

    /// <summary>True when the bundle offers both CSC archives, so the choice is real.</summary>
    public bool HasBothCscArchives =>
        Entries.Any(e => e.Slot == FirmwareSlot.Csc && !e.IsHomeCsc)
        && Entries.Any(e => e.Slot == FirmwareSlot.Csc && e.IsHomeCsc);

    /// <summary>
    /// Look at a path and say whether it is worth trying to open as a bundle.
    /// Cheap: extension plus the two-byte local-file-header signature.
    /// </summary>
    public static bool LooksLikeBundle(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return stream.Read(head) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Open a firmware zip and list what it offers. Reads the central directory
    /// only; no archive is extracted and no payload byte is read.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Not a zip, or a zip with nothing in it that looks like a firmware archive.
    /// </exception>
    public static FirmwareBundle Open(string path, IProgress<OdinEvent> log)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);

        if (!File.Exists(path))
            throw new FileNotFoundException("Firmware zip does not exist.", path);

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is not a readable zip: {1}", System.IO.Path.GetFileName(path), ex.Message),
                "firmware"));
            throw;
        }

        try
        {
            var entries = new List<BundleEntry>();
            string? info = null;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // A directory entry has an empty Name. Skip it rather than
                // classifying it, or "meta-data/" becomes a candidate.
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                if (Classify(entry.Name) is { } classified)
                {
                    entries.Add(new BundleEntry(classified.Slot, classified.HomeCsc,
                        entry.FullName, entry.Name, entry.Length, entry.CompressedLength));
                }
                else if (entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                         && entry.Length > 0 && entry.Length <= 64 * 1024)
                {
                    info ??= ReadTextEntry(entry);
                }
            }

            if (entries.Count == 0)
            {
                string names = string.Join(", ", archive.Entries.Take(8).Select(e => e.Name));
                log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" is a zip but holds no AP/BL/CP/CSC/USERDATA archive. It contains: {1}",
                    System.IO.Path.GetFileName(path), names), "firmware"));

                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" contains no firmware archives.", System.IO.Path.GetFileName(path)));
            }

            // Slot order for display, with the plain CSC before HOME_CSC so the
            // default lands first in the list the user reads.
            entries.Sort((a, b) =>
            {
                int bySlot = SlotOrder(a.Slot).CompareTo(SlotOrder(b.Slot));
                if (bySlot != 0)
                    return bySlot;
                return a.IsHomeCsc.CompareTo(b.IsHomeCsc);
            });

            var bundle = new FirmwareBundle(path, archive, entries, info, log);

            log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "Bundle \"{0}\": {1} archive(s).",
                System.IO.Path.GetFileName(path), entries.Count), "firmware"));

            foreach (BundleEntry e in entries)
            {
                log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
                    "  {0}  {1:N0} bytes", e.Label, e.Length), "firmware"));
            }

            return bundle;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What to select when a bundle is first opened: BL, AP, CP and the plain
    /// CSC archive.
    /// </summary>
    public IReadOnlyList<BundleEntry> DefaultSelection(CscMode cscMode = CscMode.Csc)
    {
        var chosen = new List<BundleEntry>();

        foreach (FirmwareSlot slot in new[]
                 { FirmwareSlot.Bl, FirmwareSlot.Ap, FirmwareSlot.Cp, FirmwareSlot.Csc })
        {
            BundleEntry? pick = slot == FirmwareSlot.Csc
                ? PickCsc(cscMode)
                : Entries.FirstOrDefault(e => e.Slot == slot);

            if (pick is not null)
                chosen.Add(pick);
        }

        return chosen;
    }

    /// <summary>
    /// The CSC archive for a mode, falling back to the other one when the
    /// bundle only ships one. A bundle with only HOME_CSC is common for
    /// carrier-specific releases, and refusing to flash it because the mode says
    /// "Csc" would be a worse answer than using what is there and saying so.
    /// </summary>
    public BundleEntry? PickCsc(CscMode mode)
    {
        bool wantHome = mode == CscMode.HomeCsc;

        BundleEntry? exact = Entries.FirstOrDefault(
            e => e.Slot == FirmwareSlot.Csc && e.IsHomeCsc == wantHome);
        if (exact is not null)
            return exact;

        BundleEntry? other = Entries.FirstOrDefault(e => e.Slot == FirmwareSlot.Csc);
        if (other is not null)
        {
            _log.Report(OdinEvent.Warning(string.Format(CultureInfo.InvariantCulture,
                "This bundle has no {0} archive; using {1} instead.",
                wantHome ? "HOME_CSC" : "CSC", other.Label), "firmware"));
        }

        return other;
    }

    /// <summary>
    /// The total number of bytes that will be written to disk when extracting a
    /// selection, so a caller can check free space before it starts rather than
    /// filling the volume and failing halfway.
    /// </summary>
    public static long ExtractedBytes(IEnumerable<BundleEntry> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Sum(e => e.Length);
    }

    /// <summary>
    /// Stream the chosen archives out of the zip into <paramref name="destinationDirectory"/>
    /// and return them as sources ready for <see cref="FirmwarePackage"/>.
    /// </summary>
    public async Task<IReadOnlyList<FirmwareSource>> ExtractAsync(
        IReadOnlyList<BundleEntry> selection, string destinationDirectory,
        IProgress<BundleProgress>? progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        if (selection.Count == 0)
            throw new ArgumentException("Nothing was selected to extract.", nameof(selection));

        Directory.CreateDirectory(destinationDirectory);

        var sources = new List<FirmwareSource>(selection.Count);
        long totalBytes = ExtractedBytes(selection);
        // CONCURRENTLY, one worker per archive. The members are independent
        // files, so the only thing that was serialising them was this loop.
        // Each worker opens the zip itself, because ZipArchive is not
        // thread-safe. Lanes are capped so four small archives do not spawn
        // more readers than the machine can feed.
        long doneBytes = 0;
        var results = new FirmwareSource?[selection.Count];
        var written = new long[selection.Count];
        int lanes = Math.Max(1, Math.Min(selection.Count, Environment.ProcessorCount));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, selection.Count),
            new ParallelOptions { MaxDegreeOfParallelism = lanes, CancellationToken = ct },
            async (index, token) =>
            {
                BundleEntry chosen = selection[index];

                string name = chosen.EntryName.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)
                    ? chosen.EntryName[..^4]
                    : chosen.EntryName;

                string finalPath = System.IO.Path.Combine(destinationDirectory, name);
                string partPath = finalPath + ".enneaodin-part";

                _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                    "Extracting {0}...", chosen.Label),
                    "firmware"));

                try
                {
                    // This worker's own handle on the zip.
                    using ZipArchive archive = ZipFile.OpenRead(_path);

                    ZipArchiveEntry entry = archive.GetEntry(chosen.EntryPath)
                        ?? throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                            "\"{0}\" is no longer in the zip.", chosen.EntryPath));

                    await using (Stream source = entry.Open())
                    await using (FileStream output = new(partPath, FileMode.Create, FileAccess.Write,
                                     FileShare.None, 1 << 20, useAsync: true))
                    {
                        byte[] buffer = new byte[1 << 20];
                        int read;
                        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            written[index] += read;

                            // Shared across lanes, so interlocked.
                            long total = Interlocked.Add(ref doneBytes, read);
                            progress?.Report(new BundleProgress(chosen, written[index], chosen.Length,
                                total, totalBytes));
                        }

                        await output.FlushAsync(token).ConfigureAwait(false);
                    }

                    // The central directory said how long this is. A short write
                    // means a truncated download that the CRC has not reached yet,
                    // or a full disk; either way it must not become a flash.
                    if (written[index] != chosen.Length)
                    {
                        throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                            "{0}: expected {1:N0} bytes but extracted {2:N0}. The zip is truncated or the disk is full.",
                            chosen.Label, chosen.Length, written[index]));
                    }

                    File.Move(partPath, finalPath, overwrite: true);
                }
                catch
                {
                    TryDelete(partPath);
                    throw;
                }

                results[index] = new FirmwareSource(chosen.Slot, finalPath);
            }).ConfigureAwait(false);

        // In the order the selection listed them, not the order they finished:
        // the slot order is what the flash plan follows.
        foreach (FirmwareSource? source in results)
        {
            if (source is not null)
                sources.Add(source);
        }

        return sources;
    }


    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _archive.Dispose();
    }

    private static (FirmwareSlot Slot, bool HomeCsc)? Classify(string name)
    {
        foreach ((string prefix, FirmwareSlot slot, bool home) in SlotPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (slot, home);
        }

        return null;
    }

    private static int SlotOrder(FirmwareSlot slot) => slot switch
    {
        FirmwareSlot.Bl => 0,
        FirmwareSlot.Ap => 1,
        FirmwareSlot.Cp => 2,
        FirmwareSlot.Csc => 3,
        FirmwareSlot.UserData => 4,
        _ => 5
    };

    private static string? ReadTextEntry(ZipArchiveEntry entry)
    {
        try
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }
}

/// <summary>Progress of one archive's extraction, and of the selection overall.</summary>
/// <param name="Entry">The archive being extracted.</param>
/// <param name="EntryBytes">Bytes written for this archive.</param>
/// <param name="EntryTotal">This archive's uncompressed size.</param>
/// <param name="TotalBytes">Bytes written across the whole selection.</param>
/// <param name="Total">The selection's total uncompressed size.</param>
public readonly record struct BundleProgress(
    BundleEntry Entry, long EntryBytes, long EntryTotal, long TotalBytes, long Total)
{
    /// <summary>Percent of this archive, 0 to 100.</summary>
    public double EntryPercent => EntryTotal > 0 ? 100.0 * EntryBytes / EntryTotal : 0;

    /// <summary>Percent of the whole selection, 0 to 100.</summary>
    public double TotalPercent => Total > 0 ? 100.0 * TotalBytes / Total : 0;
}
