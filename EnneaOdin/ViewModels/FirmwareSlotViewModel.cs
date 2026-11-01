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
using OdinCore.Firmware;

namespace EnneaOdin.ViewModels;

/// <summary>
/// One firmware row: a tick box, a coloured slot chip, where the archive comes
/// from, and its own Browse command.
/// </summary>
public sealed partial class FirmwareSlotViewModel : ObservableObject
{
    private readonly Func<FirmwareSlotViewModel, Task> _browse;
    private readonly Action _changed;

    // True while Fill or Clear is assigning FilePath, so the change handler
    // does not undo the BundleEntry that assignment exists to set.
    private bool _assigning;

    public FirmwareSlotViewModel(FirmwareSlot slot, string label, bool isPitRow,
                                 bool selectedByDefault,
                                 Func<FirmwareSlotViewModel, Task> browse, Action changed)
    {
        Slot = slot;
        Label = label;
        IsPitRow = isPitRow;
        _isSelected = selectedByDefault;
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>Which Odin container this row stands for.</summary>
    public FirmwareSlot Slot { get; }

    /// <summary>"BL", "AP", "CP", "CSC", "USERDATA" or "PIT".</summary>
    public string Label { get; }

    /// <summary>
    /// What the chip shows. The same as <see cref="Label"/> except on the CSC
    /// row, where a bundle entry may be the HOME_CSC archive and the chip has
    /// to say so: those two archives fill the same slot and differ in whether
    /// the user's data survives.
    /// </summary>
    public string DisplayLabel => BundleEntry?.Label ?? Label;

    /// <summary>
    /// What the row's browse button is for, as a sentence.
    /// </summary>
    public string BrowseHint => IsPitRow
        ? "Choose a PIT file"
        : "Choose the " + Label + " file";

    /// <summary>
    /// True for the PIT row, which is not a firmware container: it feeds
    /// FlashPlanner's localPit argument instead of being unpacked.
    /// </summary>
    public bool IsPitRow { get; }

    /// <summary>
    /// False for the PIT row. The tick box is about what gets flashed, and the
    /// PIT is not flashed: it is either the layout to write (with
    /// Re-partition) or a claim about the layout already there.
    /// </summary>
    public bool IsSelectable => !IsPitRow;

    /// <summary>
    /// Whether this slot takes part in the flash.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsTowardFlash))]
    private bool _isSelected;

    /// <summary>
    /// The archive inside an imported firmware zip that fills this slot, or
    /// null when the row is empty or holds a loose file.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFromBundle))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(HasPath))]
    [NotifyPropertyChangedFor(nameof(ErasesUserData))]
    [NotifyPropertyChangedFor(nameof(CountsTowardFlash))]
    private BundleEntry? _bundleEntry;

    /// <summary>True when this row is filled from an imported zip.</summary>
    public bool IsFromBundle => BundleEntry is not null;

    /// <summary>
    /// The path, editable by hand. Not read-only: a path the user cannot
    /// correct by typing is a path they have to re-pick through a dialogue.
    /// </summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>The PIT row is only enabled when repartitioning is selected.</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>True when this row names something, from either source.</summary>
    public bool HasPath => IsFromBundle || !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>
    /// True when this row will actually be flashed: it names something and it
    /// is ticked. The PIT row is never counted.
    /// </summary>
    public bool CountsTowardFlash => IsSelectable && IsSelected && HasPath;

    /// <summary>
    /// What fills this row, in one line. The entry name for a bundle row, the
    /// file name for a loose file, and a prompt when the row is empty.
    /// </summary>
    public string SourceText
    {
        get
        {
            if (BundleEntry is not null)
                return BundleEntry.EntryName;

            if (!string.IsNullOrWhiteSpace(FilePath))
                return Path.GetFileName(FilePath.Trim());

            return string.Empty;
        }
    }

    /// <summary>
    /// The archive's size, as a human figure. From the zip's central directory
    /// for a bundle row - which costs nothing, the figure is already read - and
    /// from the file system for a loose file. Empty when neither is available,
    /// rather than a zero that looks like a measurement.
    /// </summary>
    public string SizeText
    {
        get
        {
            if (BundleEntry is not null)
                return FormatBytes(BundleEntry.Length);

            string path = FilePath.Trim();
            if (path.Length == 0)
                return string.Empty;

            try
            {
                var info = new FileInfo(path);
                return info.Exists ? FormatBytes(info.Length) : "not found";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                // A path that cannot be stat'ed is reported as unreadable here
                // and refused later by the unpacker with the real reason. This
                // getter runs on every keystroke, so it cannot throw.
                return "unreadable";
            }
        }
    }

    /// <summary>
    /// True when flashing this row destroys user data: the USERDATA slot
    /// always, and the plain CSC archive, which is exactly why HOME_CSC
    /// exists. Drives the red callout on the row rather than being buried in
    /// a confirmation nobody reads.
    /// </summary>
    public bool ErasesUserData =>
        Slot == FirmwareSlot.UserData ||
        (Slot == FirmwareSlot.Csc && BundleEntry is not null && !BundleEntry.IsHomeCsc);

    // ------------------------------------------------------------------ hue
    //
    // A slot keeps one colour everywhere it appears. These are booleans
    // rather than a brush or a resource key because the chip's colour is set
    // by a style class, and a style class is what keeps every colour literal
    // inside Styles/Tokens.axaml: the markup writes
    // Classes.bgBlue="{Binding IsHueBlue}" and the class resolves the brush.

    public bool IsHuePurple => Slot == FirmwareSlot.Bl;
    public bool IsHueBlue => Slot == FirmwareSlot.Ap;
    public bool IsHueTeal => Slot == FirmwareSlot.Cp;
    public bool IsHueGreen => Slot == FirmwareSlot.Csc;
    public bool IsHueOrange => Slot == FirmwareSlot.UserData;
    public bool IsHuePink => IsPitRow;

    // -------------------------------------------------------------- filling

    /// <summary>
    /// Fill this row from an imported bundle. Clears any loose path, so the
    /// row has exactly one source.
    /// </summary>
    public void Fill(BundleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _assigning = true;
        try
        {
            FilePath = string.Empty;
        }
        finally
        {
            _assigning = false;
        }

        BundleEntry = entry;
        _changed();
    }

    /// <summary>Empty the row, from whichever source filled it.</summary>
    public void Clear()
    {
        _assigning = true;
        try
        {
            FilePath = string.Empty;
        }
        finally
        {
            _assigning = false;
        }

        BundleEntry = null;
        _changed();
    }

    [RelayCommand]
    private Task BrowseAsync() => _browse(this);

    [RelayCommand]
    private void ClearRow() => Clear();

    partial void OnFilePathChanged(string value)
    {
        // A typed or picked path replaces a bundle entry: the row shows a
        // path, so the row must flash that path. Done without going through
        // Clear(), which would wipe the value just assigned.
        if (!_assigning && BundleEntry is not null && !string.IsNullOrWhiteSpace(value))
            BundleEntry = null;

        OnPropertyChanged(nameof(HasPath));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(CountsTowardFlash));

        if (!_assigning)
            _changed();
    }

    partial void OnIsSelectedChanged(bool value) => _changed();

    /// <summary>
    /// Bytes as a human figure, binary units, two decimals above a kibibyte.
    /// Formatted with the invariant culture so a decimal comma never turns
    /// "6.31 GiB" into something a bug report cannot be grepped for.
    /// </summary>
    private static string FormatBytes(long bytes)
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
