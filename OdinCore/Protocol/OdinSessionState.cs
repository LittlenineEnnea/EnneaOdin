// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (Initialise, BeginSession, EndSession)
//   native/vendor/Heimdall/heimdall/source/DownloadPitAction.cpp
//   native/vendor/Heimdall/heimdall/source/PrintPitAction.cpp
//   native/vendor/Heimdall/heimdall/source/ClosePcScreenAction.cpp
//   native/vendor/Heimdall/doc/Heimdall-flash-instructions.md
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

namespace OdinCore.Protocol;

/// <summary>
/// Where a session is. The phases map one for one onto the steps of
/// BridgeManager::Initialise (BridgeManager.cpp:469-507) and
/// BridgeManager::BeginSession (:509-558).
/// </summary>
public enum OdinSessionPhase
{
    /// <summary>No handle, nothing claimed.</summary>
    Closed,

    /// <summary>Interface claimed and the alt-setting set; no handshake yet. BridgeManager.cpp:494-498.</summary>
    Claimed,

    /// <summary>"ODIN" sent and "LOKE" received, or skipped because this is a resume. BridgeManager.cpp:500-504.</summary>
    HandshakeDone,

    /// <summary>BeginSession acknowledged and the file-part size settled. BridgeManager.cpp:556.</summary>
    SessionOpen,

    /// <summary>EndSession acknowledged. BridgeManager.cpp:575-584.</summary>
    SessionEnded,

    /// <summary>An operation failed; the session must not be used again.</summary>
    Faulted
}

/// <summary>
/// The legal-transition table, and the resume and reboot semantics that the
/// heimdall command line encodes as --no-reboot and --resume.
/// </summary>
public sealed class OdinSessionTracker
{
    private static readonly Dictionary<OdinSessionPhase, OdinSessionPhase[]> Legal = new()
    {
        [OdinSessionPhase.Closed] = new[] { OdinSessionPhase.Claimed, OdinSessionPhase.Faulted },
        [OdinSessionPhase.Claimed] = new[] { OdinSessionPhase.HandshakeDone, OdinSessionPhase.Closed, OdinSessionPhase.Faulted },
        [OdinSessionPhase.HandshakeDone] = new[] { OdinSessionPhase.SessionOpen, OdinSessionPhase.Closed, OdinSessionPhase.Faulted },
        [OdinSessionPhase.SessionOpen] = new[] { OdinSessionPhase.SessionEnded, OdinSessionPhase.Closed, OdinSessionPhase.Faulted },
        [OdinSessionPhase.SessionEnded] = new[] { OdinSessionPhase.Closed, OdinSessionPhase.Faulted },
        [OdinSessionPhase.Faulted] = new[] { OdinSessionPhase.Closed }
    };

    public OdinSessionPhase Phase { get; private set; } = OdinSessionPhase.Closed;

    /// <summary>
    /// True once a session has been ended without a reboot request, so the
    /// handset is still sitting in download mode. BridgeManager.cpp:586 --
    /// the reboot packet is the only thing that takes it out.
    /// </summary>
    public bool DeviceLeftInDownloadMode { get; private set; }

    /// <summary>
    /// Whether the next open should skip the handshake. This is the whole
    /// meaning of --resume (BridgeManager.cpp:500).
    /// </summary>
    public bool NextOpenMustResume => DeviceLeftInDownloadMode;

    public bool CanTransitionTo(OdinSessionPhase next) =>
        Legal.TryGetValue(Phase, out var allowed) && Array.IndexOf(allowed, next) >= 0;

    /// <summary>
    /// Move to <paramref name="next"/>, or throw. An illegal transition is a
    /// programming error in the caller, not a device problem, so it is not
    /// reported as a protocol failure.
    /// </summary>
    public void TransitionTo(OdinSessionPhase next)
    {
        if (Phase == next)
            return;

        if (!CanTransitionTo(next))
            throw new InvalidOperationException($"Illegal session transition: {Phase} to {next}.");

        Phase = next;
    }

    /// <summary>
    /// Record how a session ended. After EndSession(reboot: false) the device
    /// is still in download mode and the next operation must resume; after
    /// EndSession(reboot: true) it is rebooting and the next operation starts
    /// from scratch.
    /// </summary>
    public void RecordSessionEnd(bool rebooted)
    {
        TransitionTo(OdinSessionPhase.SessionEnded);
        DeviceLeftInDownloadMode = !rebooted;
    }

    /// <summary>The device has gone away (unplugged, or rebooted on its own).</summary>
    public void RecordDeviceGone()
    {
        Phase = OdinSessionPhase.Closed;
        DeviceLeftInDownloadMode = false;
    }

    public void RecordFault()
    {
        Phase = OdinSessionPhase.Faulted;

        // A session that faulted after it was opened has not been ended, so
        // the device is still in download mode whether or not the user wanted
        // it there. Saying so is the honest reading, and it is what lets the
        // next attempt resume instead of failing a handshake.
        DeviceLeftInDownloadMode = true;
    }

    /// <summary>One plain sentence for the Session row of the device panel.</summary>
    public string Describe() => Phase switch
    {
        OdinSessionPhase.Closed => DeviceLeftInDownloadMode
            ? "closed, device left in download mode; the next operation will resume"
            : "closed",
        OdinSessionPhase.Claimed => "interface claimed, protocol not initialised",
        OdinSessionPhase.HandshakeDone => "protocol initialised (LOKE), session not begun",
        OdinSessionPhase.SessionOpen => "open",
        OdinSessionPhase.SessionEnded => DeviceLeftInDownloadMode
            ? "ended, device left in download mode; the next operation will resume"
            : "ended, device rebooting",
        OdinSessionPhase.Faulted => "faulted; the device is probably still in download mode",
        _ => "unknown"
    };
}
