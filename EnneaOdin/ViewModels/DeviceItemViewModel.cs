// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using OdinCore.Usb;

namespace EnneaOdin.ViewModels;

/// <summary>
/// One entry in the device list, keyed by bus and address so that a refresh
/// can update a device in place instead of clearing the list and losing the
/// user's selection.
/// </summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    public DeviceItemViewModel(UsbDeviceRecord record)
    {
        Key = record.Key;
        _record = record;
    }

    /// <summary>"003/005": stable for as long as the device stays attached.</summary>
    public string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    [NotifyPropertyChangedFor(nameof(StatusWord))]
    private UsbDeviceRecord _record;

    /// <summary>"04E8:685D SAMSUNG bus 0 addr 5". Nothing in it is inferred.</summary>
    public string Display => Record.DisplayName;

    /// <summary>A word, never a colour.</summary>
    public string StatusWord => Record.StatusWord;

    public override string ToString() => Display;
}
