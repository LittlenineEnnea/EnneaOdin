// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall: Heimdall 2.2.2 takes file names on a
// command line and has no file-chooser code.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Tasks;

namespace EnneaOdin.Services;

/// <summary>
/// The only file-system dialogue the view model knows about. It exists so the
/// view model holds no reference to a Window and can be exercised without one.
/// </summary>
/// <summary>Which filter the open dialogue starts on.</summary>
public enum FilePickerKind
{
    /// <summary>.tar, .md5, .img, .bin, .mbn, .lz4, .pit.</summary>
    Firmware,

    /// <summary>A distributor's firmware .zip.</summary>
    Bundle
}

public interface IFilePicker
{
    /// <summary>One existing file, or null if the user cancelled.</summary>
    Task<string?> OpenFileAsync(string title, FilePickerKind kind = FilePickerKind.Firmware);

    /// <summary>
    /// A path to write, or null if the user cancelled. The picker is
    /// responsible for the platform's own overwrite confirmation; nothing here
    /// overwrites silently.
    /// </summary>
    Task<string?> SaveFileAsync(string title, string suggestedName, string defaultExtension);
}

/// <summary>
/// Modal confirmation and modal failure reporting, for the same reason.
/// </summary>
public interface IConfirmService
{
    /// <summary>
    /// True when the user chose the affirmative button.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel,
                            bool monospaceBody = false);

    /// <summary>One acknowledgement button; used for a hard failure.</summary>
    Task ShowMessageAsync(string title, string message);
}
