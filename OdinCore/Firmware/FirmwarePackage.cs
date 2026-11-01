// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is NOT derived from Heimdall. Heimdall takes already-extracted
// partition images as command-line arguments and has no archive handling of
// any kind (confirmed by grep over the whole vendored tree, Qt frontend
// included). What it does establish is why the unpacking has to happen here:
// the file transfer streams raw bytes straight at a partition
// (native/vendor/Heimdall/heimdall/source/BridgeManager.cpp:1096), so handing it a .tar
// would write ustar headers into that partition.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using K4os.Compression.LZ4.Streams;
using OdinCore.Diagnostics;
using OdinCore.Protocol;

namespace OdinCore.Firmware;

/// <summary>
/// The Odin container a file was supplied as. These are Odin's labels for
/// bundles of partition images, not partition names: the protocol addresses
/// partitions by their PIT entry, never by "AP" or "CSC". A slot exists so the
/// user interface can say where a file came from and so the flash order
/// follows the order the slots were filled.
/// </summary>
public enum FirmwareSlot
{
    Bl,
    Ap,
    Cp,
    Csc,
    UserData,

    /// <summary>A single image supplied on its own rather than inside a container.</summary>
    Loose
}

/// <summary>One file the user chose, and which container they chose it as.</summary>
public sealed record FirmwareSource(FirmwareSlot Slot, string Path);

/// <summary>
/// One image this flash can send, and where its bytes come from.
/// </summary>
/// <param name="Slot">The container it came from.</param>
/// <param name="ArchivePath">The source file on the user's disk.</param>
/// <param name="EntryPath">The full path inside the archive, or the file name for a loose image.</param>
/// <param name="EntryName">The last path segment of <paramref name="EntryPath"/>.</param>
/// <param name="ResolvedBaseName">The entry name with .md5 and .lz4 stripped, looped.</param>
/// <param name="ExtractedPath">Where it is on disk, or null when streamed.</param>
/// <param name="Length">
/// Exact decompressed byte count, always counted, never taken from the LZ4
/// frame's own content-size field.
/// </param>
/// <param name="WasLz4">Whether it arrived as an LZ4 frame.</param>
/// <param name="DataOffset">Offset of the member's bytes in the archive; 0 for a loose image.</param>
/// <param name="ArchiveLength">Bytes occupied inside the archive: the compressed size for LZ4.</param>
public sealed record FirmwareMember(FirmwareSlot Slot, string ArchivePath, string EntryPath, string EntryName,
                                    string ResolvedBaseName, string? ExtractedPath, long Length, bool WasLz4,
                                    long DataOffset = 0, long ArchiveLength = 0)
{
    /// <summary>True when this image is produced on demand rather than read from disk.</summary>
    public bool IsStreamed => ExtractedPath is null;

    /// <summary>
    /// Open the image's bytes.
    /// </summary>
    public Stream OpenImage()
    {
        if (ExtractedPath is not null)
            return File.OpenRead(ExtractedPath);

        var region = new FileRegionStream(ArchivePath, DataOffset, ArchiveLength);

        if (!WasLz4)
        {
            // Already exactly the bytes the device wants, and already
            // seekable, so a retry needs no window.
            return region;
        }

        try
        {
            return new RewindStream(LZ4Stream.Decode(region, leaveOpen: false), Length);
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }
}

/// <summary>
/// One or more Odin sources, unpacked into a scoped temporary directory, with
/// every member's exact byte count known before anything is sent.
/// </summary>
public sealed class FirmwarePackage : IAsyncDisposable
{
    /// <summary>
    /// Suffixes stripped, in a loop, to get from an archive entry name to the
    /// name the PIT would know. "boot.img.lz4.md5" reduces to "boot.img".
    /// </summary>
    private static readonly string[] StrippableSuffixes = { ".md5", ".lz4" };

    private readonly List<FirmwareMember> _members = new();

    // Archives whose trailing digest has been found but not yet checked.
    // Filled by IndexSourceAsync and drained by VerifyDigestsAsync.
    private readonly List<(string Path, string? Expected, long PayloadLength)> _pendingDigests = new();
    private readonly Dictionary<string, ArchiveDigest> _digests = new(StringComparer.Ordinal);
    private readonly List<(string EntryPath, PitData Pit)> _pitMembers = new();
    private readonly IProgress<OdinEvent> _log;

    // The workspace lock, held until this package is disposed.
    private readonly IDisposable? _claim;

    private bool _disposed;

    private FirmwarePackage(string workingDirectory, IProgress<OdinEvent> log,
                            IDisposable? claim)
    {
        WorkingDirectory = workingDirectory;
        _log = log;
        _claim = claim;
    }

    /// <summary>The scoped temporary directory everything was extracted into.</summary>
    public string WorkingDirectory { get; }

    public IReadOnlyList<FirmwareMember> Members => _members;

    /// <summary>Keyed by source path, as given.</summary>
    public IReadOnlyDictionary<string, ArchiveDigest> Digests => _digests;

    /// <summary>
    /// Any .pit members found, already parsed. A repartition PIT can come from
    /// the firmware itself rather than being asked for separately.
    /// </summary>
    public IReadOnlyList<(string EntryPath, PitData Pit)> PitMembers => _pitMembers;

    /// <summary>The sum of every member's measured length.</summary>
    public long TotalBytes => _members.Sum(m => m.Length);

    /// <summary>
    /// Open every source, verify what can be verified, and extract. Throws on
    /// any real failure rather than returning a short list: a damaged archive
    /// that produced three of nine members must not become a three-partition
    /// flash plan.
    /// </summary>
    public static async Task<FirmwarePackage> OpenAsync(IReadOnlyList<FirmwareSource> sources,
        string tempRoot, IProgress<OdinEvent> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(tempRoot);
        ArgumentNullException.ThrowIfNull(log);

        if (sources.Count == 0)
            throw new ArgumentException("No firmware sources were supplied.", nameof(sources));

        string working = Path.Combine(tempRoot, "enneaodin-" + Guid.NewGuid().ToString("N"));

        // Held for the life of the package, so the next startup sweep can tell
        // this workspace from one it should clear.
        IDisposable? claim = WorkspaceStorage.Claim(working);
        Directory.CreateDirectory(working);

        var package = new FirmwarePackage(working, log, claim);

        try
        {
            foreach (FirmwareSource source in sources)
            {
                ct.ThrowIfCancellationRequested();
                await package.AddSourceAsync(source, ct).ConfigureAwait(false);
            }

            if (package._members.Count == 0)
            {
                throw new InvalidDataException(
                    "None of the supplied firmware files contained anything to flash.");
            }

            log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "Firmware unpacked: {0} images, {1} bytes.",
                package._members.Count, package.TotalBytes), "firmware"));

            return package;
        }
        catch
        {
            await package.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Index every source and measure every member WITHOUT writing anything to
    /// disk.
    /// </summary>
    public static async Task<FirmwarePackage> OpenStreamingAsync(IReadOnlyList<FirmwareSource> sources,
        string tempRoot, IProgress<OdinEvent> log, bool verifyDigests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(tempRoot);
        ArgumentNullException.ThrowIfNull(log);

        if (sources.Count == 0)
            throw new ArgumentException("No firmware sources were supplied.", nameof(sources));

        string working = Path.Combine(tempRoot, "enneaodin-" + Guid.NewGuid().ToString("N"));

        // Held for the life of the package, so the next startup sweep can tell
        // this workspace from one it should clear.
        IDisposable? claim = WorkspaceStorage.Claim(working);
        Directory.CreateDirectory(working);

        var package = new FirmwarePackage(working, log, claim);

        try
        {
            foreach (FirmwareSource source in sources)
            {
                ct.ThrowIfCancellationRequested();
                await package.IndexSourceAsync(source, ct).ConfigureAwait(false);
            }

            if (package._members.Count == 0)
            {
                throw new InvalidDataException(
                    "None of the supplied firmware files contained anything to flash.");
            }

            // The two expensive passes run AT THE SAME TIME, because neither
            // needs the other's answer.
            //
            //   measuring  decodes every compressed member to learn its exact
            //              length. Spread across cores twice over, by member
            //              and by block. 2.5 s on the reference firmware.
            //   digests    hashes each archive against the MD5 line it
            //              carries. MD5 is a chain and cannot be split, so the
            //              only parallelism available is one archive against
            //              another - and against the measuring. 21 s on the
            //              reference firmware, essentially all of it AP.tar.
            //
            // Serially that was 23.5 s. Together it is whichever is slower.
            Task measuring = package.MeasureStreamedMembersAsync(ct);
            Task digests = package.VerifyDigestsAsync(verifyDigests, ct);

            await Task.WhenAll(measuring, digests).ConfigureAwait(false);

            foreach (FirmwareMember member in package._members)
                await package.TryReadPitMemberAsync(member, ct).ConfigureAwait(false);

            log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "Firmware ready: {0} images, {1} bytes.",
                package._members.Count, package.TotalBytes), "firmware"));

            return package;
        }
        catch
        {
            await package.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// One loose image on disk, measured and ready to stream, with no package
    /// and no temporary file.
    ///
    /// This is what the manual partition list uses: the user named the
    /// partition, so there is no archive to open and nothing to match. An LZ4
    /// file is measured by the same parallel decode the streamed flash uses;
    /// anything else is its own length.
    /// </summary>
    public static async Task<FirmwareMember> OpenLooseImageAsync(string path,
        IProgress<OdinEvent> log, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);

        if (!File.Exists(path))
            throw new FileNotFoundException("Image does not exist.", path);

        var info = new FileInfo(path);
        string name = Path.GetFileName(path);
        bool isLz4;

        await using (FileStream probe = File.OpenRead(path))
        {
            byte[] head = new byte[4];
            int got = await probe.ReadAsync(head, ct).ConfigureAwait(false);
            isLz4 = got == 4 && Lz4Decompressor.LooksLikeLz4Frame(head);
        }

        long length = info.Length;

        if (isLz4)
        {
            Lz4Measurement measured = await Lz4Decompressor.MeasureParallelAsync(
                () => new FileRegionStream(path, 0, info.Length),
                info.Length, name, Environment.ProcessorCount, ct).ConfigureAwait(false);

            length = measured.Length;

            log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} -> {2} bytes decompressed", name, info.Length, length), "firmware"));
        }

        return new FirmwareMember(FirmwareSlot.Loose, path, name, name,
                                  StripSuffixes(name), null, length, isLz4, 0, info.Length);
    }

    private async Task IndexSourceAsync(FirmwareSource source, CancellationToken ct)
    {
        if (!File.Exists(source.Path))
        {
            _log.Report(OdinEvent.Error("Firmware file does not exist: " + source.Path, "firmware"));
            throw new FileNotFoundException("Firmware file does not exist.", source.Path);
        }

        if (!TarArchiveReader.LooksLikeTar(source.Path))
        {
            // A loose image is its own file. It is still streamed: a file
            // region over itself costs nothing and keeps one code path.
            var info = new FileInfo(source.Path);
            string looseName = Path.GetFileName(source.Path);
            bool looseLz4;

            await using (FileStream probe = File.OpenRead(source.Path))
            {
                byte[] head = new byte[4];
                int got = await probe.ReadAsync(head, ct).ConfigureAwait(false);
                looseLz4 = got == 4 && Lz4Decompressor.LooksLikeLz4Frame(head);
            }

            _members.Add(new FirmwareMember(FirmwareSlot.Loose, source.Path, looseName, looseName,
                                            StripSuffixes(looseName), null,
                                            looseLz4 ? -1 : info.Length, looseLz4,
                                            0, info.Length));
            return;
        }

        // The CHEAP half only: where the tar payload stops, and what digest
        // follows it if any. Hashing is deferred to VerifyDigestsAsync, which
        // runs concurrently with the measuring pass.
        (string? expected, long payloadLength) =
            await ArchiveDigestReader.FindPayloadAsync(source.Path, ct).ConfigureAwait(false);

        _pendingDigests.Add((source.Path, expected, payloadLength));
        // NotChecked, not "failed". Verification may be off, in which case
        // nothing overwrites this and it is the final answer for this archive.
        _digests[source.Path] = new ArchiveDigest(
            expected is null ? DigestOutcome.Absent : DigestOutcome.NotChecked,
            expected?.ToLowerInvariant(), null, payloadLength);

        IReadOnlyList<TarIndexEntry> index = TarIndex.Read(source.Path, payloadLength);
        IReadOnlyList<TarMember> reference = TarArchiveReader.List(source.Path, payloadLength);

        // The offsets come from a parser written here; the names and lengths
        // come from SharpZipLib. If the two disagree about what is in the
        // archive, nothing is flashed from an offset.
        TarIndex.VerifyAgainst(index, reference, source.Path);

        if (index.Count == 0)
        {
            _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is a valid tar archive but contains no files.",
                Path.GetFileName(source.Path)), "firmware"));

            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" contains no files.", Path.GetFileName(source.Path)));
        }

        foreach (TarIndexEntry entry in index)
        {
            ct.ThrowIfCancellationRequested();

            string entryName = LastSegment(entry.EntryPath);
            bool isLz4 = entryName.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase);

            if (!isLz4 && entry.Length >= 4)
            {
                // The suffix is a hint, not the truth. A member named .img
                // that is in fact an LZ4 frame has to be decoded, and the
                // previous path established that by sniffing the first four
                // bytes, so this does too.
                await using var probe = new FileRegionStream(source.Path, entry.DataOffset, entry.Length);
                byte[] head = new byte[4];
                int got = await probe.ReadAsync(head, ct).ConfigureAwait(false);
                isLz4 = got == 4 && Lz4Decompressor.LooksLikeLz4Frame(head);
            }

            _members.Add(new FirmwareMember(
                source.Slot, source.Path, entry.EntryPath, entryName,
                StripSuffixes(entryName),
                null,
                // -1 means "not measured yet"; the measuring pass fills it in
                // and MeasureStreamedMembersAsync asserts none is left.
                isLz4 ? -1 : entry.Length,
                isLz4, entry.DataOffset, entry.Length));
        }
    }

    /// <summary>
    /// Hash each archive against the MD5 line it carries, all of them at once.
    /// </summary>
    private async Task VerifyDigestsAsync(bool enabled, CancellationToken ct)
    {
        if (_pendingDigests.Count == 0)
            return;

        int carrying = _pendingDigests.Count(d => d.Expected is not null);

        if (!enabled)
        {
            foreach ((string path, string? expected, long payloadLength) in _pendingDigests)
            {
                _log.Report(OdinEvent.Info(expected is null
                    ? string.Format(CultureInfo.InvariantCulture,
                        "{0}: no trailing MD5 digest; reading it as a plain tar.",
                        Path.GetFileName(path))
                    : string.Format(CultureInfo.InvariantCulture,
                        "{0}: carries a trailing MD5 digest over {1} bytes, NOT verified. " +
                        "Turn on \"Verify archive MD5 digests\" to check it.",
                        Path.GetFileName(path), payloadLength), "firmware"));
            }

            return;
        }

        if (carrying == 0)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
            "Verifying {0} MD5 digest(s)...", carrying), "firmware"));

        var results = new ArchiveDigest[_pendingDigests.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, _pendingDigests.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _pendingDigests.Count,
                                  CancellationToken = ct },
            async (i, token) =>
            {
                (string path, string? expected, long payloadLength) = _pendingDigests[i];

                if (expected is null)
                {
                    results[i] = new ArchiveDigest(DigestOutcome.Absent, null, null, payloadLength);
                    return;
                }

                results[i] = await ArchiveDigestReader
                    .VerifyAsync(path, expected, payloadLength, _log, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        for (int i = 0; i < results.Length; i++)
            _digests[_pendingDigests[i].Path] = results[i];

        _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
            "Digests verified in {0} ms.", stopwatch.ElapsedMilliseconds), "firmware"));

        ArchiveDigest? damaged = results.FirstOrDefault(d => d.IsDamaged);

        if (damaged is not null)
        {
            throw new InvalidDataException(
                "An archive's trailing MD5 digest does not match its contents. The download is " +
                "damaged and nothing has been sent.");
        }
    }

    /// <summary>
    /// Decode every LZ4 member to a counter, in parallel, and replace its
    /// placeholder length with the real one.
    /// </summary>
    private async Task MeasureStreamedMembersAsync(CancellationToken ct)
    {
        var todo = new List<int>();

        for (int i = 0; i < _members.Count; i++)
        {
            if (_members[i].Length < 0)
                todo.Add(i);
        }

        // BIGGEST FIRST. The members are wildly uneven - on the reference
        // firmware one of them is 5,496,955,290 bytes and the other
        // twenty-six come to 1,398,431,961 between them - so starting the big
        // one last would leave the whole machine waiting on it after
        // everything else had finished. The same reasoning as scheduling the
        // longest job first anywhere else.
        todo.Sort((a, b) => _members[b].ArchiveLength.CompareTo(_members[a].ArchiveLength));

        if (todo.Count == 0)
            return;

        var measured = new Lz4Measurement[_members.Count];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int cores = Environment.ProcessorCount;

        // Two members at a time, every core on the blocks of each. Measured
        // on super.img.lz4 (8242 blocks, cached): 1 lane 7408 ms, 2 4004,
        // 4 2498, 8 1982, 16 1872. Still improving past the core count
        // because the work mixes reading with decoding. Splitting the cores
        // evenly between the two levels was slower: the big member got five
        // lanes and the machine idled once the small ones finished.
        int memberLanes = Math.Min(2, todo.Count);
        int blockLanes = Math.Max(2, cores);

        _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
            "Measuring {0} image(s)...", todo.Count), "firmware"));

        _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
            "{0} core(s), biggest first, {1} image(s) at a time, {2} block(s) within each.",
            cores, memberLanes, blockLanes), "firmware"));

        await Parallel.ForEachAsync(todo,
            new ParallelOptions { MaxDegreeOfParallelism = memberLanes, CancellationToken = ct },
            async (index, token) =>
            {
                FirmwareMember member = _members[index];

                measured[index] = await Lz4Decompressor.MeasureParallelAsync(
                    () => new FileRegionStream(member.ArchivePath, member.DataOffset,
                                               member.ArchiveLength),
                    member.ArchiveLength, member.EntryPath, blockLanes, token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        foreach (int index in todo)
        {
            Lz4Measurement m = measured[index];
            _members[index] = _members[index] with { Length = m.Length };

            _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} -> {2} bytes decompressed from {3} bytes in {4} block(s){5}",
                SlotLabel(_members[index].Slot), _members[index].EntryPath,
                m.Length, _members[index].ArchiveLength, m.BlockCount,
                m.Parallel
                    ? ", measured by decoding in parallel"
                    : m.DeclaredContentSize >= 0
                        ? ", length read from the frame header"
                        : ", measured by decoding in order (linked blocks)"),
                "firmware"));

            // The counted length is what gets used either way. A frame whose
            // own declared content size disagrees with it is an archive that
            // misdescribes itself, and a user about to write it to a partition
            // should be told, even though nothing here depends on the claim.
            if (m.DeclarationDisagrees)
            {
                _log.Report(OdinEvent.Warning(string.Format(CultureInfo.InvariantCulture,
                    "{0} declares a decompressed size of {1} bytes but decodes to {2}. " +
                    "The measured length is used; the archive misdescribes itself.",
                    _members[index].EntryPath, m.DeclaredContentSize, m.Length), "firmware"));
            }
        }

        if (_members.Any(m => m.Length < 0))
        {
            throw new InvalidDataException(
                "An image was left without a measured length. The flash is refused: the protocol " +
                "declares every size before the first packet and a wrong one is a wrong packet count.");
        }

        _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
            "Measured {0} image(s) in {1} ms.",
            todo.Count, stopwatch.ElapsedMilliseconds), "firmware"));
    }


    private async Task AddSourceAsync(FirmwareSource source, CancellationToken ct)
    {
        if (!File.Exists(source.Path))
        {
            _log.Report(OdinEvent.Error("Firmware file does not exist: " + source.Path, "firmware"));
            throw new FileNotFoundException("Firmware file does not exist.", source.Path);
        }

        bool isTar;
        try
        {
            isTar = TarArchiveReader.LooksLikeTar(source.Path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "Cannot read \"{0}\": {1}", source.Path, ex.Message), "firmware"));
            throw;
        }

        if (isTar)
        {
            await AddArchiveAsync(source, ct).ConfigureAwait(false);
            return;
        }

        // Not a tar. Anything else is treated as a single raw image: .img,
        // .bin, .mbn, .pit, or whatever else the user pointed at. A file whose
        // name says ".tar.md5" but whose content is not a tar is called out,
        // because that is a damaged or mislabelled download and silently
        // flashing it as one image would write a zip into a partition.
        string name = Path.GetFileName(source.Path);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase))
        {
            _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is named like a tar archive but does not contain a tar header. It is damaged or is not an Odin archive.",
                name), "firmware"));

            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is not a tar archive.", name));
        }

        await AddLooseImageAsync(source, ct).ConfigureAwait(false);
    }

    private async Task AddArchiveAsync(FirmwareSource source, CancellationToken ct)
    {
        ArchiveDigest digest = await ArchiveDigestReader.InspectAsync(source.Path, _log, ct).ConfigureAwait(false);
        _digests[source.Path] = digest;

        string slotDirectory = Path.Combine(WorkingDirectory, SlotFolder(source.Slot));
        Directory.CreateDirectory(slotDirectory);

        int count = 0;

        await TarArchiveReader.ExtractAsync(source.Path, digest.PayloadLength,
            async (member, content, token) =>
            {
                string relative = SanitiseEntryPath(member.EntryPath, source.Path);
                string destination = Path.Combine(slotDirectory, relative);

                string? directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                (long length, bool wasLz4) = await ExtractMemberAsync(content, destination, token)
                    .ConfigureAwait(false);

                string entryName = LastSegment(member.EntryPath);
                string baseName = StripSuffixes(entryName);

                // The extracted file keeps the name the PIT would recognise,
                // not the ".lz4.md5" name it travelled under.
                string finalPath = Path.Combine(directory ?? slotDirectory, StripSuffixes(LastSegment(relative)));
                if (!string.Equals(finalPath, destination, StringComparison.Ordinal))
                {
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);

                    File.Move(destination, finalPath);
                }

                var record = new FirmwareMember(source.Slot, source.Path, member.EntryPath, entryName,
                                                baseName, finalPath, length, wasLz4,
                                                0, member.Length);
                _members.Add(record);
                count++;

                _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1} -> {2} bytes{3}",
                    SlotLabel(source.Slot), member.EntryPath, length,
                    wasLz4 ? " (LZ4)" : string.Empty), "firmware"));

                await TryReadPitMemberAsync(record, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        if (count == 0)
        {
            _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" is a valid tar archive but contains no files.", Path.GetFileName(source.Path)),
                "firmware"));

            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" contains no files.", Path.GetFileName(source.Path)));
        }
    }

    private async Task AddLooseImageAsync(FirmwareSource source, CancellationToken ct)
    {
        // Spec note: a raw image is recorded in the Loose slot whatever
        // container the user chose it as, because it is not a container. The
        // order of Members still reflects the order the user filled the slots,
        // which is what the flash order depends on (FlashAction.cpp:351).
        string slotDirectory = Path.Combine(WorkingDirectory, SlotFolder(FirmwareSlot.Loose));
        Directory.CreateDirectory(slotDirectory);

        string entryName = Path.GetFileName(source.Path);
        string baseName = StripSuffixes(entryName);
        string destination = Path.Combine(slotDirectory, baseName);

        await using FileStream input = File.OpenRead(source.Path);
        (long length, bool wasLz4) = await ExtractMemberAsync(input, destination, ct).ConfigureAwait(false);

        var record = new FirmwareMember(FirmwareSlot.Loose, source.Path, entryName, entryName,
                                        baseName, destination, length, wasLz4,
                                        0, new FileInfo(source.Path).Length);
        _members.Add(record);

        _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
            "Loose image: {0} -> {1} bytes{2}",
            entryName, length, wasLz4 ? " (LZ4)" : string.Empty), "firmware"));

        await TryReadPitMemberAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Copy or decompress one member to <paramref name="destination"/>,
    /// returning its measured length.
    /// </summary>
    private static async Task<(long Length, bool WasLz4)> ExtractMemberAsync(Stream content, string destination,
                                                                            CancellationToken ct)
    {
        var head = new byte[4];
        int headRead = 0;
        while (headRead < head.Length)
        {
            int n = await content.ReadAsync(head.AsMemory(headRead), ct).ConfigureAwait(false);
            if (n <= 0)
                break;

            headRead += n;
        }

        bool isLz4 = Lz4Decompressor.LooksLikeLz4Frame(head.AsSpan(0, headRead));

        await using var chained = new ConcatStream(head.AsMemory(0, headRead), content);

        if (isLz4)
        {
            long length = await Lz4Decompressor.DecompressToFileAsync(chained, destination, ct)
                .ConfigureAwait(false);
            return (length, true);
        }

        await using FileStream output = File.Create(destination);
        await chained.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        return (output.Length, false);
    }

    private async Task TryReadPitMemberAsync(FirmwareMember member, CancellationToken ct)
    {
        if (!member.ResolvedBaseName.EndsWith(".pit", StringComparison.OrdinalIgnoreCase))
            return;

        // A streamed member has no file to read, so it is opened the same way
        // the flash would open it and read into memory. A PIT is 7412 bytes on
        // the reference handset and the format caps it far below anything worth
        // streaming; the guard below is what keeps that assumption honest
        // rather than reading a 5 GiB member into the heap because somebody
        // named it ".pit".
        const long PitReadLimit = 1 << 20;

        if (member.Length > PitReadLimit)
        {
            _log.Report(OdinEvent.Warning(string.Format(CultureInfo.InvariantCulture,
                "{0} is named like a PIT but is {1} bytes, which no PIT is. It is left as an image.",
                member.EntryPath, member.Length), "firmware"));
            return;
        }

        byte[] raw;

        if (member.ExtractedPath is not null)
        {
            raw = await File.ReadAllBytesAsync(member.ExtractedPath, ct).ConfigureAwait(false);
        }
        else
        {
            await using Stream image = member.OpenImage();
            using var copy = new MemoryStream((int)Math.Max(member.Length, 0));
            await image.CopyToAsync(copy, ct).ConfigureAwait(false);
            raw = copy.ToArray();
        }

        if (PitData.TryUnpack(raw, out PitData? pit, out string? error) && pit is not null)
        {
            _pitMembers.Add((member.EntryPath, pit));
            _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
                "{0} is a PIT: {1}", member.EntryPath,
                PitTextFormatter.FormatHeaderLine(pit)), "firmware"));
        }
        else
        {
            // Reported, not thrown: the member may still be a flashable image
            // that merely ends in ".pit". If the user asked to repartition,
            // FlashPlanner refuses for want of a usable PIT, which is where
            // that decision belongs.
            _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "{0} is named like a PIT but could not be parsed: {1}", member.EntryPath, error), "firmware"));
        }
    }

    /// <summary>
    /// Strip ".md5" and ".lz4" repeatedly, in either order and in any case.
    /// Ordinal comparisons only.
    /// </summary>
    public static string StripSuffixes(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (string suffix in StrippableSuffixes)
            {
                if (name.Length > suffix.Length
                    && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    stripped = true;
                }
            }
        }

        return name;
    }

    /// <summary>
    /// Turn an archive entry path into a safe relative path, refusing anything
    /// that would escape the extraction directory.
    /// </summary>
    internal static string SanitiseEntryPath(string entryPath, string archivePath)
    {
        string[] segments = entryPath.Split('/', '\\');
        var kept = new List<string>(segments.Length);

        foreach (string raw in segments)
        {
            string segment = raw.Trim();

            if (segment.Length == 0 || segment == ".")
                continue;

            if (segment == "..")
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" contains an entry that tries to escape the extraction directory: \"{1}\".",
                    Path.GetFileName(archivePath), entryPath));
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" contains an entry with an absolute or device-qualified path: \"{1}\".",
                    Path.GetFileName(archivePath), entryPath));
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                if (segment.IndexOf(invalid) >= 0)
                {
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                        "\"{0}\" contains an entry with an unusable name: \"{1}\".",
                        Path.GetFileName(archivePath), entryPath));
                }
            }

            kept.Add(segment);
        }

        if (kept.Count == 0)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" contains an entry with an empty name.", Path.GetFileName(archivePath)));
        }

        return Path.Combine(kept.ToArray());
    }

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string SlotFolder(FirmwareSlot slot) => slot switch
    {
        FirmwareSlot.Bl => "bl",
        FirmwareSlot.Ap => "ap",
        FirmwareSlot.Cp => "cp",
        FirmwareSlot.Csc => "csc",
        FirmwareSlot.UserData => "userdata",
        _ => "loose"
    };

    /// <summary>The slot's label, as the user interface spells it.</summary>
    public static string SlotLabel(FirmwareSlot slot) => slot switch
    {
        FirmwareSlot.Bl => "BL",
        FirmwareSlot.Ap => "AP",
        FirmwareSlot.Cp => "CP",
        FirmwareSlot.Csc => "CSC",
        FirmwareSlot.UserData => "USERDATA",
        _ => "FILE"
    };

    /// <summary>
    /// Delete the whole temporary tree. Called on success, on failure and on
    /// cancellation; a failure to delete is logged rather than thrown, because
    /// losing the temp tree is less bad than masking the error that led here.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        // Before the delete: on Windows an open handle inside the directory
        // would stop it, and the lock file lives in there.
        _claim?.Dispose();

        try
        {
            if (Directory.Exists(WorkingDirectory))
                Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Report(OdinEvent.Warning(string.Format(CultureInfo.InvariantCulture,
                "Could not delete the temporary firmware directory \"{0}\": {1}",
                WorkingDirectory, ex.Message), "firmware"));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A read-only stream that serves a small in-memory prefix and then continues
/// from an inner stream. It exists so a member's first four bytes can be
/// examined for an LZ4 magic without a second pass over the data and without
/// requiring the inner stream to be seekable, which a tar entry is not.
/// </summary>
internal sealed class ConcatStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _inner;
    private int _prefixPosition;

    internal ConcatStream(ReadOnlyMemory<byte> prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < _prefix.Length)
        {
            int take = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
            _prefix.Span.Slice(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return _inner.Read(buffer);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
