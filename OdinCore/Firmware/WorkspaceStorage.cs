// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall. Heimdall 2.2.2 reads an archive
// member straight off disk and has no temporary tree to manage, so neither the
// free-space check nor the sweep below has an upstream counterpart.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OdinCore.Firmware;

/// <summary>
/// Free space on the workspace volume, and workspaces left behind by a
/// previous run. Both exist because one resolve could put ~7 GiB in the
/// temporary tree: filling the volume used to surface as an IOException with
/// no figures in it, and nothing had ever deleted a workspace it did not
/// create in the same process.
/// </summary>
public static class WorkspaceStorage
{
    /// <summary>
    /// How much more than the measured requirement has to be free before an
    /// extraction is allowed to start, as a fraction.
    /// </summary>
    private const double Margin = 0.05;

    /// <summary>The floor under <see cref="Margin"/>: 256 MiB.</summary>
    private const long MinimumHeadroom = 256L * 1024 * 1024;

    /// <summary>
    /// A workspace is only swept when it has not been touched for this long.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The number of bytes free on the volume that holds
    /// <paramref name="path"/>, or null when the platform will not say.
    /// </summary>
    public static long? AvailableBytes(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or IOException)
        {
            return null;
        }

        DriveInfo? best = null;
        int bestLength = -1;

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string mount;
                try
                {
                    if (!drive.IsReady)
                        continue;

                    mount = drive.RootDirectory.FullName;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!IsUnder(full, mount))
                    continue;

                if (mount.Length > bestLength)
                {
                    best = drive;
                    bestLength = mount.Length;
                }
            }

            return best?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuse the operation now if the volume cannot hold
    /// <paramref name="requiredBytes"/> plus headroom.
    /// </summary>
    /// <returns>
    /// A sentence describing what was found, for the log. Never null: either
    /// the figures that were compared, or why no comparison was possible.
    /// </returns>
    /// <exception cref="IOException">
    /// The volume is readable and does not have room.
    /// </exception>
    public static string RequireFreeSpace(string path, long requiredBytes, string what)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(what);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);

        long needed = requiredBytes + Headroom(requiredBytes);
        long? available = AvailableBytes(path);

        if (available is null)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Free-space check skipped for {0}: this platform did not report the free space on " +
                "the volume holding \"{1}\". {2} needs {3}.",
                what, path, what, Human(needed));
        }

        if (available.Value < needed)
        {
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "Not enough free space for {0}. It needs {1} ({2} of data plus {3} of headroom) " +
                "and the volume holding \"{4}\" has {5} free. Free some space, or point the " +
                "temporary directory at a larger volume with the TMPDIR environment variable.",
                what, Human(needed), Human(requiredBytes), Human(Headroom(requiredBytes)),
                path, Human(available.Value)));
        }

        return string.Format(CultureInfo.InvariantCulture,
            "Free space: {0} needs {1} and the volume holding \"{2}\" has {3} free.",
            what, Human(needed), path, Human(available.Value));
    }

    /// <summary>
    /// Delete every workspace directory under <paramref name="root"/> that is
    /// not <paramref name="keep"/> and has been cold for
    /// <see cref="StaleAfter"/>.
    /// </summary>
    /// <returns>What was deleted, and how much it was worth.</returns>
    /// <summary>The name of the lock file a live workspace holds open.</summary>
    public const string LockFileName = ".enneaodin-lock";

    /// <summary>
    /// Create <paramref name="directory"/> and hold a lock file open inside it
    /// for as long as the returned handle lives.
    ///
    /// This is what lets the startup sweep clear leftovers IMMEDIATELY instead
    /// of waiting for them to go cold. Without it the only way to tell a
    /// workspace this program abandoned from one a second copy is flashing out
    /// of right now was its age, so the sweep had to leave everything younger
    /// than half an hour alone. A lock answers the question directly.
    /// </summary>
    public static IDisposable? Claim(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        try
        {
            Directory.CreateDirectory(directory);

            return new FileStream(Path.Combine(directory, LockFileName),
                                  FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // A workspace that cannot be locked still works; it is only swept
            // more conservatively, by age.
            return null;
        }
    }

    /// <summary>
    /// Is this workspace abandoned? A lock file nobody holds means yes, and
    /// says so regardless of age. No lock file at all means unknown, and the
    /// caller falls back to the age rule.
    /// </summary>
    private static bool? LockIsFree(string directory)
    {
        string lockPath = Path.Combine(directory, LockFileName);

        if (!File.Exists(lockPath))
            return null;

        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite,
                                             FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // Somebody has it open: a live workspace.
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static SweepResult SweepStaleWorkspaces(string root, string prefix, string? keep)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        if (!Directory.Exists(root))
            return new SweepResult(0, 0, 0, Array.Empty<string>());

        string? keepFull = null;
        if (!string.IsNullOrEmpty(keep))
        {
            try
            {
                keepFull = Path.GetFullPath(keep);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException)
            {
                keepFull = null;
            }
        }

        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        var deleted = new List<string>();
        long bytes = 0;
        int failed = 0;

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(root, prefix + "*",
                                                        SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SweepResult(0, 0, 1, Array.Empty<string>());
        }

        foreach (string directory in candidates.ToList())
        {
            try
            {
                string full = Path.GetFullPath(directory);

                if (keepFull is not null &&
                    string.Equals(full, keepFull, StringComparison.Ordinal))
                {
                    continue;
                }

                var info = new DirectoryInfo(full);

                if (!info.Exists)
                    continue;

                // The lock answers it outright when there is one.
                bool? free = LockIsFree(full);

                if (free == false)
                    continue;                       // a live workspace

                if (free is null)
                {
                    // No lock file: a workspace from an older build, or one
                    // whose lock could not be created. The newest timestamp
                    // anywhere inside it, not just the directory's own:
                    // creating a file in a subdirectory bumps that
                    // subdirectory and need not bump the top.
                    DateTime touched = NewestWriteUtc(info);

                    if (touched > cutoff)
                        continue;
                }

                long size = DirectorySize(info);

                Directory.Delete(full, true);
                deleted.Add(full);
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                // A workspace that will not delete is counted and reported, not
                // thrown: this runs at startup and must never stop the
                // application from opening.
                failed++;
            }
        }

        return new SweepResult(deleted.Count, bytes, failed, deleted);
    }

    /// <summary>Bytes as a human figure, binary units, invariant culture.</summary>
    public static string Human(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        string[] units = { "KiB", "MiB", "GiB", "TiB" };
        double value = bytes / 1024.0;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
    }

    private static long Headroom(long requiredBytes)
    {
        long proportional = (long)(requiredBytes * Margin);
        return proportional > MinimumHeadroom ? proportional : MinimumHeadroom;
    }

    private static bool IsUnder(string full, string mount)
    {
        if (mount.Length == 0)
            return false;

        // Case-sensitive comparison. macOS is case-insensitive by default and
        // Linux is not, and getting this wrong in the permissive direction
        // would match a volume that does not hold the path; the mount points
        // come from the same API that produced the path's own prefix, so an
        // ordinal comparison is the one that cannot be wrong in the direction
        // that matters.
        if (!full.StartsWith(mount, StringComparison.Ordinal))
            return false;

        // "/Volumes/Data" must not match "/Volumes/DataBackup/x".
        return mount.EndsWith(Path.DirectorySeparatorChar) ||
               full.Length == mount.Length ||
               full[mount.Length] == Path.DirectorySeparatorChar;
    }

    private static DateTime NewestWriteUtc(DirectoryInfo directory)
    {
        DateTime newest = directory.LastWriteTimeUtc;

        try
        {
            foreach (FileSystemInfo child in
                     directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (child.LastWriteTimeUtc > newest)
                    newest = child.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable means "assume it is live", which is the safe
            // direction: the workspace is then not swept.
            return DateTime.UtcNow;
        }

        return newest;
    }

    private static long DirectorySize(DirectoryInfo directory)
    {
        long total = 0;

        try
        {
            foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                total += file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A figure for the log, not an accounting record.
            return total;
        }

        return total;
    }
}

/// <summary>What one sweep deleted.</summary>
/// <param name="Deleted">How many workspace directories were removed.</param>
/// <param name="BytesReclaimed">How much they held, measured before deletion.</param>
/// <param name="Failed">How many could not be removed.</param>
/// <param name="Paths">The directories removed, for the log.</param>
public readonly record struct SweepResult(int Deleted, long BytesReclaimed, int Failed,
                                          IReadOnlyList<string> Paths)
{
    /// <summary>True when the sweep had nothing to do and nothing went wrong.</summary>
    public bool IsQuiet => Deleted == 0 && Failed == 0;

    /// <summary>One line for the log.</summary>
    public string Summary => Failed == 0
        ? string.Format(CultureInfo.InvariantCulture,
            "Swept {0} stale workspace(s), reclaiming {1}.",
            Deleted, WorkspaceStorage.Human(BytesReclaimed))
        : string.Format(CultureInfo.InvariantCulture,
            "Swept {0} stale workspace(s), reclaiming {1}; {2} could not be removed.",
            Deleted, WorkspaceStorage.Human(BytesReclaimed), Failed);
}
