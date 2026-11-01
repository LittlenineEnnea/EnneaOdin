// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EnneaOdin.ViewModels;

/// <summary>
/// How bad the device status is, as a presentation value.
/// </summary>
public enum DeviceStatusLevel
{
    /// <summary>Nothing has gone wrong and nothing is confirmed ready.</summary>
    Info,

    /// <summary>A download-mode device is attached and its interface can be claimed.</summary>
    Ready,

    /// <summary>Something stands in the way; the detail line says what.</summary>
    Warning,

    /// <summary>libusb is missing or failed to start. Nothing can be done.</summary>
    Error
}
