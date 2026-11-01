// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using OdinCore.Protocol;

namespace OdinCore.Flash;

/// <summary>
/// Every option the protocol can actually carry out, and nothing else.
/// </summary>
/// <param name="RebootWhenFinished">Reboot after ending the session; false is --no-reboot. BridgeManager.cpp:586.</param>
/// <param name="Repartition">Upload a PIT first. Refused without one. FlashAction.cpp:560-565.</param>
/// <param name="TFlash">Flash to the SD card. Implies SkipSizeCheck. FlashAction.cpp:512, :515, :602.</param>
/// <param name="SkipSizeCheck">Skip the image-versus-partition size check. FlashAction.cpp:328-340.</param>
/// <param name="ResumeSession">Skip the ODIN/LOKE handshake. Normally derived from the tracker. BridgeManager.cpp:500.</param>
/// <param name="WaitForDevice">Poll for a device instead of failing. BridgeManager.cpp:81, :112.</param>
/// <param name="UsbLogLevel">0 none, 1 error, 2 warning, 3 info, 4 debug. FlashAction.cpp:528-547.</param>
/// <param name="VerboseLog">Heimdall's --verbose: per-packet and per-retry detail.</param>
public sealed record FlashOptions(
    bool RebootWhenFinished = true,
    bool Repartition = false,
    bool TFlash = false,
    bool SkipSizeCheck = false,
    bool ResumeSession = false,
    bool WaitForDevice = false,
    int UsbLogLevel = OdinConstants.UsbLogLevelError,
    bool VerboseLog = false)
{
    /// <summary>
    /// The size check is skipped when it was asked to be, and also whenever
    /// T-Flash is on, because the target is then an SD card whose geometry the
    /// PIT does not describe. FlashAction.cpp:515.
    /// </summary>
    public bool SizeCheckSkipped => SkipSizeCheck || TFlash;

    /// <summary>The session options these flash options imply.</summary>
    public OdinSessionOptions ToSessionOptions() => new(
        Resume: ResumeSession,
        UsbLogLevel: UsbLogLevel,
        VerboseTransport: VerboseLog);
}
