// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp  (Execute, flashPartitions, flashFile, sendTotalTransferSize)
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using OdinCore.Diagnostics;
using OdinCore.Protocol;

namespace OdinCore.Flash;

/// <summary>
/// Executes a confirmed plan on an open session. Port of the sequencing in
/// FlashAction::Execute (FlashAction.cpp:591-631) and flashPartitions
/// (:292-358), with the command-line parts removed.
/// </summary>
public sealed class FlashRunner
{
    private readonly OdinSession _session;
    private readonly IProgress<OdinEvent> _log;

    public FlashRunner(OdinSession session, IProgress<OdinEvent> log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<FlashOutcome> RunAsync(FlashPlan plan, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (!plan.IsExecutable)
        {
            string reason = plan.Items.Count == 0
                ? "The plan contains nothing to flash."
                : string.Format(CultureInfo.InvariantCulture,
                    "The plan has {0} unresolved image(s): {1}.",
                    plan.Unresolved.Count, string.Join(", ", plan.Unresolved));

            _log.Report(OdinEvent.Error(reason, "flash"));
            return new FlashOutcome(false, Array.Empty<string>(), Array.Empty<string>(), false,
                                    reason, DeviceInDownloadMode(), false, 0);
        }

        var completed = new List<string>();
        var failed = new List<string>();
        var runClock = System.Diagnostics.Stopwatch.StartNew();
        bool cancelRequested = false;
        bool pitUploaded = false;
        long overallDone = 0;
        long overallTotal = plan.TotalBytes;

        // The caller that BUILT the plan reports these; see
        // MainWindowViewModel.ApplyPlan. Reported here as debug so a headless
        // caller still has them without the interactive one saying each twice.
        foreach (string warning in plan.Warnings)
            _log.Report(OdinEvent.Debug(warning, "flash"));

        try
        {
            progress?.Report(new FlashProgress(null, 0, 0, overallTotal, 0, true, "starting"));

            // FlashAction.cpp:602-608 -- T-Flash first, if asked for.
            if (options.TFlash)
            {
                progress?.Report(new FlashProgress(null, 0, overallDone, overallTotal, 0, true, "enabling T-Flash"));
                await _session.EnableTFlashAsync(ct).ConfigureAwait(false);
            }

            // FlashAction.cpp:610 -- the total, before any image goes out.
            progress?.Report(new FlashProgress(null, 0, overallDone, overallTotal, 0, true, "declaring total size"));
            await _session.SendTotalBytesAsync(plan.WireTotalBytes, ct).ConfigureAwait(false);

            // FlashAction.cpp:344-349 -- "If we're repartitioning then we need
            // to flash the PIT file first".
            if (plan.Repartition)
            {
                if (plan.RepartitionPit is null)
                {
                    throw new InvalidOperationException(
                        "The plan asks to repartition but carries no PIT. This is a planning error, not a device failure.");
                }

                progress?.Report(new FlashProgress("PIT", 0, overallDone, overallTotal, 0, true, "uploading PIT"));
                await _session.SendPitAsync(plan.RepartitionPit, ct).ConfigureAwait(false);
                pitUploaded = true;
            }

            // FlashAction.cpp:351-356 -- one image at a time, in plan order,
            // stopping at the first failure.
            for (int i = 0; i < plan.Items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                FlashItem item = plan.Items[i];
                string partition = item.Target.PartitionName;
                long doneBefore = overallDone;

                string phase = string.Format(CultureInfo.InvariantCulture,
                    "uploading {0} ({1} of {2})", partition, i + 1, plan.Items.Count);

                // Odin prints a time elapsed of its own at the end of a flash.
                // Per image as well here, because without it a pasted log says
                // nothing about where a slow run spent its time.
                var itemClock = System.Diagnostics.Stopwatch.StartNew();

                // Indeterminate between partitions: the device has not yet
                // acknowledged anything for this one.
                progress?.Report(new FlashProgress(partition, 0, overallDone, overallTotal,
                    Percent(overallDone, overallTotal), true, phase));

                // FlashAction.cpp:258. Heimdall prints the partition only.
                _log.Report(OdinEvent.Info("Uploading " + partition, "flash"));


                _log.Report(OdinEvent.Debug(string.Format(CultureInfo.InvariantCulture,
                    "{0} <- {1}, {2} bytes, matched by {3}",
                    partition, item.Source.ResolvedBaseName, item.Length, item.MatchedBy), "flash"));

                var perFile = new SyncProgress<TransferProgress>(tp =>
                {
                    long done = doneBefore + tp.BytesSentForFile;
                    progress?.Report(new FlashProgress(partition, tp.PercentForFile, done, overallTotal,
                        Percent(done, overallTotal), false, phase));
                });

                try
                {
                    // Streamed from the archive when the package indexed it,
                    // read from the temporary tree when it spooled it. The
                    // member knows which it is; nothing here chooses.
                    await using Stream image = item.Source.OpenImage();

                    // FlashAction.cpp:258-289 -- a CP partition goes to the
                    // modem with no file identifier; everything else goes to
                    // the phone with the PIT entry's identifier.
                    await _session.SendFileAsync(image, item.Length, item.Destination,
                        item.DeviceType, item.FileIdentifier, partition, perFile, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // FlashAction.cpp:270, :286 -- name the partition that
                    // failed, and stop. Carrying on after a failed partition
                    // would leave a half-written device and keep writing.
                    failed.Add(partition);
                    _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                        "{0} upload failed: {1}", partition, ex.Message), "flash"));

                    return new FlashOutcome(false, completed, failed, false, ex.Message,
                                            DeviceInDownloadMode(), pitUploaded, overallDone);
                }

                // The transfer returned without throwing, which means every
                // part and every end-of-sequence packet was acknowledged. That
                // acknowledgement is the only evidence of success there is.
                overallDone = doneBefore + item.Length;
                completed.Add(partition);

                // FlashAction.cpp:265, :281, with the duration appended.
                _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                    "{0} upload successful   {1}", partition,
                    Elapsed(itemClock.Elapsed)), "flash"));

                progress?.Report(new FlashProgress(partition, 100, overallDone, overallTotal,
                    Percent(overallDone, overallTotal), false, phase));
            }
        }
        catch (OperationCanceledException)
        {
            cancelRequested = true;

            _log.Report(OdinEvent.Warning(
                "Flash cancelled by user. A partition that was being written is now partially written and the session is still open.",
                "flash"));

            return new FlashOutcome(false, completed, failed, true, null,
                                    DeviceInDownloadMode(), pitUploaded, overallDone);
        }
        catch (Exception ex)
        {
            _log.Report(OdinEvent.Error("Flash failed: " + ex.Message, "flash"));

            return new FlashOutcome(false, completed, failed, cancelRequested, ex.Message,
                                    DeviceInDownloadMode(), pitUploaded, overallDone);
        }

        // Success is evidence: every planned item acknowledged, and the PIT
        // uploaded if one was planned.
        bool success = completed.Count == plan.Items.Count
                       && failed.Count == 0
                       && (!plan.Repartition || pitUploaded);

        if (success)
        {
            _log.Report(OdinEvent.Info(string.Format(CultureInfo.InvariantCulture,
                "All {0} partition(s) written: {1} bytes in {2}.",
                completed.Count, overallDone, Elapsed(runClock.Elapsed)), "flash"));
        }
        else
        {
            // Reachable only if the loop finished without completing every
            // item, which would be a bug here rather than a device fault. It is
            // still reported rather than rounded up to success.
            _log.Report(OdinEvent.Error(string.Format(CultureInfo.InvariantCulture,
                "Flash finished without completing every planned partition: {0} of {1} done.",
                completed.Count, plan.Items.Count), "flash"));
        }

        progress?.Report(new FlashProgress(null, 100, overallDone, overallTotal,
            Percent(overallDone, overallTotal), false, success ? "done" : "FAILED"));

        return new FlashOutcome(success, completed, failed, false, null,
                                DeviceInDownloadMode(), pitUploaded, overallDone);
    }

    /// <summary>
    /// Whether the handset is still in download mode. The runner never ends a
    /// session, so while a session is open the answer is yes; after the caller
    /// ends one, the tracker's own record applies.
    /// </summary>
    private bool DeviceInDownloadMode() =>
        _session.Phase == OdinSessionPhase.SessionOpen
        || _session.Phase == OdinSessionPhase.Faulted
        || _session.Tracker.DeviceLeftInDownloadMode;

    private static int Percent(long done, long total) =>
        total <= 0 ? 0 : (int)(100.0 * ((double)done / total));

    /// <summary>A duration, short enough to sit at the end of a log line.</summary>
    private static string Elapsed(TimeSpan took)
    {
        if (took.TotalMinutes >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s",
                                 (int)took.TotalMinutes, took.Seconds);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", took.TotalSeconds);
    }
}
