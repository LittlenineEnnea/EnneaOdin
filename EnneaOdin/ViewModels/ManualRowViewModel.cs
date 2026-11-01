// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EnneaOdin.ViewModels;

/// <summary>
/// One row of the manual partition list: a partition name and the file to
/// write to it.
/// </summary>
public sealed partial class ManualRowViewModel : ObservableObject
{
    private readonly Func<ManualRowViewModel, Task> _browse;
    private readonly Action<ManualRowViewModel> _remove;
    private readonly Action _changed;

    public ManualRowViewModel(Func<ManualRowViewModel, Task> browse,
                              Action<ManualRowViewModel> remove, Action changed)
    {
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>As typed. Matched against the PIT case-insensitively.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    private string _partitionName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private string _filePath = string.Empty;

    /// <summary>True when both halves are filled in.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(PartitionName) && !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>The file's size on disk, or why it could not be read.</summary>
    public string SizeText
    {
        get
        {
            string path = FilePath.Trim();

            if (path.Length == 0)
                return string.Empty;

            try
            {
                var info = new FileInfo(path);
                return info.Exists ? Human(info.Length) : "not found";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                // Runs on every keystroke, so it cannot throw.
                return "unreadable";
            }
        }
    }

    [RelayCommand]
    private Task BrowseAsync() => _browse(this);

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnPartitionNameChanged(string value) => _changed();

    partial void OnFilePathChanged(string value) => _changed();

    private static string Human(long bytes)
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
}
