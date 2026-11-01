// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp
//   native/vendor/Heimdall/libpit/source/libpit.h
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using OdinCore.Firmware;
using OdinCore.Protocol;

namespace OdinCore.Flash;

/// <summary>
/// One image resolved to one PIT partition.
/// </summary>
/// <param name="Source">The extracted image.</param>
/// <param name="Target">The PIT entry it will be written to.</param>
/// <param name="Length">The image's exact byte count.</param>
/// <param name="MatchedBy">How the resolution was made, so the user can check it.</param>
/// <param name="Explanation">"boot.img -> BOOT (id 7, 102400 blocks, MMC)".</param>
public sealed record FlashItem(FirmwareMember Source, PitEntry Target, long Length,
                               string MatchedBy, string Explanation)
{
    /// <summary>
    /// Phone or modem, from the PIT entry's binary type. A CP partition goes
    /// to the modem (FlashAction.cpp:258, :262), everything else to the phone
    /// (FlashAction.cpp:274, :278).
    /// </summary>
    public FileDestination Destination =>
        Target.BinaryType == (uint)PitBinaryType.CommunicationProcessor
            ? FileDestination.Modem
            : FileDestination.Phone;

    /// <summary>
    /// The PIT entry's identifier for a phone transfer (FlashAction.cpp:279),
    /// or 0xFFFFFFFF for a modem transfer, which has no identifier
    /// (BridgeManager.cpp:1012, BridgeManager.h:167).
    /// </summary>
    public uint FileIdentifier =>
        Destination == FileDestination.Modem ? OdinConstants.ModemFileIdentifier : Target.Identifier;

    /// <summary>The PIT entry's device type. FlashAction.cpp:263, :279.</summary>
    public uint DeviceType => Target.DeviceType;
}

/// <summary>
/// A complete, reviewable description of what a flash would do, built before
/// anything is sent.
/// </summary>
public sealed record FlashPlan(
    IReadOnlyList<FlashItem> Items,
    IReadOnlyList<string> Unresolved,
    bool Repartition,
    PitData? RepartitionPit,
    long TotalBytes,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when this plan is safe to execute.</summary>
    public bool IsExecutable => Items.Count > 0 && Unresolved.Count == 0;

    /// <summary>
    /// The total that goes on the wire in a TotalBytesPacket: every image plus
    /// the PIT when repartitioning. FlashAction.cpp:157-169.
    /// </summary>
    public ulong WireTotalBytes
    {
        get
        {
            long total = TotalBytes;

            if (Repartition && RepartitionPit is not null)
            {
                // FlashAction.cpp:166-167 adds the PIT file's size on disk.
                // What actually goes on the wire is the padded size
                // (BridgeManager.cpp:795), which for any real PIT file is the
                // same number, since a PIT file is stored padded.
                total += RepartitionPit.PaddedSize;
            }

            return (ulong)total;
        }
    }
}

/// <summary>
/// What a flash run actually did, assembled from acknowledgements.
/// </summary>
public sealed record FlashOutcome(
    bool Success,
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> Failed,
    bool Cancelled,
    string? Error,
    bool DeviceLeftInDownloadMode,
    bool PitUploaded,
    long BytesTransferred);
