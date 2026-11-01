// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp  (setupPartitionFlashInfo, flashPartitions, getPitData)
//   native/vendor/Heimdall/libpit/source/libpit.cpp         (FindEntry)
//   native/vendor/Heimdall/libpit/source/libpit.h
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using OdinCore.Firmware;
using OdinCore.Protocol;

namespace OdinCore.Flash;

/// <summary>
/// Resolves extracted firmware images to PIT partitions.
/// </summary>
/// <summary>One row of the manual partition list.</summary>
/// <param name="PartitionName">As the user typed it; matched case-insensitively.</param>
/// <param name="Source">The image, already measured and ready to stream.</param>
public sealed record ManualTarget(string PartitionName, FirmwareMember Source);

public static class FlashPlanner
{
    /// <summary>
    /// Build a plan, or refuse.
    /// </summary>
    /// <param name="package">The unpacked firmware.</param>
    /// <param name="devicePit">The PIT read from the device.</param>
    /// <param name="options">The flash options in force.</param>
    /// <param name="localPit">A PIT the user supplied, if any.</param>
    /// <exception cref="InvalidOperationException">
    /// Repartition was asked for without a PIT; a supplied PIT disagrees with
    /// the device's and repartition was not asked for; an archive's trailing
    /// digest did not match; or an image does not fit its partition.
    /// </exception>
    public static FlashPlan Build(FirmwarePackage package, PitData devicePit, FlashOptions options,
                                  PitData? localPit)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(devicePit);
        ArgumentNullException.ThrowIfNull(options);

        var warnings = new List<string>();

        // A damaged archive must never reach the wire. The digest check
        // happened when the package was opened; this is where it stops a flash.
        foreach ((string archivePath, ArchiveDigest digest) in package.Digests)
        {
            if (digest.IsDamaged)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" failed its own MD5 check ({1}). Flash aborted: the archive is damaged.",
                    Path.GetFileName(archivePath), digest.Describe()));
            }

            if (!digest.Present)
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" carries no MD5 digest, so its integrity could not be checked.",
                    Path.GetFileName(archivePath)));
            }
        }

        // The PIT to use for repartitioning: the user's, or one found inside
        // the firmware itself.
        PitData? repartitionPit = localPit ?? (package.PitMembers.Count > 0 ? package.PitMembers[0].Pit : null);

        if (options.Repartition && repartitionPit is null)
        {
            // FlashAction.cpp:560-565, verbatim wording.
            throw new InvalidOperationException(
                "If you wish to repartition then a PIT file must be specified.");
        }

        if (!options.Repartition && localPit is not null)
        {
            // FlashAction.cpp:416-428 -- a PIT supplied without --repartition
            // is a claim about what the device already has, and a mismatch
            // means the user is about to flash images for a different layout.
            if (!devicePit.Matches(localPit))
            {
                throw new InvalidOperationException(
                    "Local and device PIT files don't match and repartition wasn't specified! Flash aborted.");
            }
        }

        // FlashAction.cpp:398-402 -- when repartitioning, the PIT that
        // partitions are resolved against is the new one, because that is the
        // layout the device will have by the time the images are written.
        PitData resolvePit = options.Repartition && repartitionPit is not null ? repartitionPit : devicePit;

        var items = new List<FlashItem>();
        var unresolved = new List<string>();
        var pitEntryPaths = new HashSet<string>(package.PitMembers.Select(p => p.EntryPath), StringComparer.Ordinal);

        // FlashAction.cpp:351 -- "Flash partitions in the same order that
        // arguments were specified in", which here is the order the user
        // filled the slots and the order the archives listed their members.
        foreach (FirmwareMember member in package.Members)
        {
            // A PIT is uploaded by SendPitData, never written as a partition.
            // EndPhoneFileTransferPacket.h:36 is explicit about this:
            // "Don't flash the pit this way!".
            if (pitEntryPaths.Contains(member.EntryPath))
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} is a PIT and will not be written as a partition.", member.EntryPath));
                continue;
            }

            // Samsung's AP and CSC archives carry a "meta-data" directory
            // holding fota.zip and friends. Those are not partition images and
            // never were; Odin ignores them. Treating them as images that
            // failed to match made every stock firmware unflashable, because
            // an unmatched image refuses the whole plan.
            if (IsMetadata(member.EntryPath))
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} is bundle metadata, not a partition image. Skipped.", member.EntryPath));
                continue;
            }

            (PitEntry? target, string matchedBy) = Resolve(resolvePit, member);

            if (target is null)
            {
                unresolved.Add(member.EntryPath);
                continue;
            }

            items.Add(new FlashItem(member, target, member.Length, matchedBy,
                string.Format(CultureInfo.InvariantCulture, "{0} -> {1} [{2}]",
                    member.ResolvedBaseName, target.Describe(), matchedBy)));
        }

        Check(items, options, warnings);

        return new FlashPlan(items, unresolved, options.Repartition, repartitionPit,
                             items.Sum(i => i.Length), warnings);
    }

    /// <summary>
    /// Is this archive member metadata rather than a partition image? True for
    /// anything inside a "meta-data" directory, which is where Samsung puts
    /// fota.zip and the update scripts.
    /// </summary>
    private static bool IsMetadata(string entryPath)
    {
        foreach (string part in entryPath.Split('/', '\\'))
        {
            if (string.Equals(part, "meta-data", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Build a plan from partition names the user typed, each with a file.
    ///
    /// NO ARCHIVE AND NO TEMPORARY FILE. The partition is named outright, so
    /// there is nothing to match by file name, and each image is streamed
    /// straight off disk by FirmwareMember.OpenImage. Packing the files into a
    /// tar first and sending them through the AP slot would reach the same
    /// device state after writing and re-reading every byte.
    /// </summary>
    public static FlashPlan BuildManual(IReadOnlyList<ManualTarget> targets, PitData devicePit,
                                        FlashOptions options)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(devicePit);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Repartition)
        {
            throw new InvalidOperationException(
                "Re-partition is not available for a manual partition list: it rewrites the whole " +
                "layout, and these rows name partitions in the layout that is already there.");
        }

        var items = new List<FlashItem>();
        var unresolved = new List<string>();
        var warnings = new List<string>();

        foreach (ManualTarget target in targets)
        {
            string wanted = target.PartitionName.Trim();

            // Exact name, case-insensitive. A PIT calls it BOOT and a user
            // types boot; nothing else is guessed, because the point of this
            // page is that the user said which partition.
            PitEntry? entry = devicePit.Entries.FirstOrDefault(
                e => e.IsFlashable &&
                     string.Equals(e.PartitionName, wanted, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                unresolved.Add(wanted + "  (" + target.Source.EntryName + ")");
                continue;
            }

            items.Add(new FlashItem(target.Source, entry, target.Source.Length, "partition name",
                string.Format(CultureInfo.InvariantCulture, "{0} -> {1} [named]",
                    target.Source.EntryName, entry.Describe())));
        }

        Check(items, options, warnings);

        return new FlashPlan(items, unresolved, false, null,
                             items.Sum(i => i.Length), warnings);
    }

    /// <summary>
    /// The checks every plan gets, whichever way it was built: size against
    /// the partition, the read-only attribute, and two images at one
    /// partition.
    /// </summary>
    private static void Check(List<FlashItem> items, FlashOptions options, List<string> warnings)
    {
        // FlashAction.cpp:300-341 -- the size check, skipped for T-Flash and
        // for any device type that is neither MMC nor UFS.
        if (!options.SizeCheckSkipped)
        {
            foreach (FlashItem item in items)
            {
                long? partitionSize = item.Target.PartitionSize;

                if (partitionSize is null)
                    continue;                                   // FlashAction.cpp:328-330

                if (partitionSize == 0)
                    continue;                                   // FlashAction.cpp:335 -- size 0 disables the check

                if (item.Length > partitionSize)
                {
                    // FlashAction.cpp:337-339, with both sizes named so the
                    // user can see by how much.
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "{0} partition is too small for the given file: the partition holds {1} bytes and \"{2}\" is {3} bytes. Use \"Skip partition size check\" to flash anyway.",
                        item.Target.PartitionName, partitionSize, item.Source.ResolvedBaseName, item.Length));
                }
            }
        }
        else
        {
            warnings.Add(options.TFlash
                ? "Partition size checks are skipped because T-Flash is on."
                : "Partition size checks are skipped at your request.");
        }

        // A read-only partition is still writable by the protocol as far as
        // this tool can tell; the attribute is the PIT's claim about intent,
        // not an enforcement. Saying so is better than hiding it.
        foreach (FlashItem item in items)
        {
            if ((item.Target.Attributes & (uint)PitAttributes.Write) == 0)
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} is marked Read-Only in the PIT.", item.Target.PartitionName));
            }
        }

        // Two images resolving to the same partition: write only the LAST,
        // and say so.
        //
        // Stock firmware really does this - AP and CSC each carry a
        // vbmeta.img, so VBMETA was targeted twice and written twice, the
        // second overwriting the first. The earlier write was dead work and,
        // on a large partition, minutes of it. Last wins because that is what
        // the previous behaviour amounted to once both had run.
        var superseded = new List<FlashItem>();

        foreach (IGrouping<string, FlashItem> group in
                     items.GroupBy(i => i.Target.PartitionName, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
                continue;

            FlashItem[] all = group.ToArray();
            FlashItem kept = all[^1];

            for (int i = 0; i < all.Length - 1; i++)
                superseded.Add(all[i]);

            warnings.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} is targeted by {1} images ({2}). Writing only {3}; the others would be " +
                "overwritten by it anyway.",
                group.Key, all.Length,
                string.Join(", ", all.Select(i => i.Source.ResolvedBaseName)),
                kept.Source.ResolvedBaseName));
        }

        foreach (FlashItem dead in superseded)
            items.Remove(dead);
    }

    /// <summary>
    /// The three resolution attempts, in order, and nothing else.
    /// </summary>
    private static (PitEntry? Target, string MatchedBy) Resolve(PitData pit, FirmwareMember member)
    {
        // 1. The PIT's own "USB flash filename" field (libpit.h:96), against
        //    the entry's name as the archive spelled it.
        PitEntry? entry = pit.FindByFlashFilename(member.EntryName);
        if (entry is not null)
            return (entry, "flash filename");

        // 2. The same field, against the name with .md5 and .lz4 stripped.
        if (!string.Equals(member.ResolvedBaseName, member.EntryName, StringComparison.Ordinal))
        {
            entry = pit.FindByFlashFilename(member.ResolvedBaseName);
            if (entry is not null)
                return (entry, "flash filename, suffixes stripped");
        }

        // 3. The partition name, against the base name with its extension
        //    removed: "boot.img" against "BOOT".
        string withoutExtension = StripExtension(member.ResolvedBaseName);
        if (withoutExtension.Length > 0)
        {
            entry = pit.FindByNameIgnoreCase(withoutExtension);
            if (entry is not null)
                return (entry, "partition name");
        }

        return (null, "unresolved");
    }

    /// <summary>
    /// Drop the final extension. Ordinal, and only the last one: "modem.bin"
    /// becomes "modem", and a name with no dot is returned unchanged.
    /// </summary>
    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }
}
