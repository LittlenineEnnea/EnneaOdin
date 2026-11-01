// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall: Heimdall 2.2.2 takes file names on a
// command line and has no file-chooser code.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace EnneaOdin.Services;

/// <summary>
/// <see cref="IFilePicker"/> over Avalonia's StorageProvider.
/// </summary>
public sealed class StorageFilePicker : IFilePicker
{
    private readonly Window _owner;

    public StorageFilePicker(Window owner) =>
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>
    /// One filter with flat extensions, plus "all files".
    /// </summary>
    private static FilePickerFileType FirmwareType { get; } =
        new("Samsung firmware and images")
        {
            Patterns = new[] { "*.tar", "*.md5", "*.img", "*.bin", "*.mbn", "*.lz4", "*.pit" }
        };

    private static FilePickerFileType BundleType { get; } =
        new("Firmware bundle (.zip)")
        {
            Patterns = new[] { "*.zip" },
            MimeTypes = new[] { "application/zip" },
            AppleUniformTypeIdentifiers = new[] { "public.zip-archive" }
        };

    public async Task<string?> OpenFileAsync(string title,
                                             FilePickerKind kind = FilePickerKind.Firmware)
    {
        IStorageProvider provider = _owner.StorageProvider;

        // The bundle picker starts on .zip; without this it offered the
        // firmware filter, which has no *.zip in it, so a bundle was only
        // reachable through "All files".
        FilePickerFileType first = kind == FilePickerKind.Bundle ? BundleType : FirmwareType;

        IReadOnlyList<IStorageFile> chosen = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { first, FilePickerFileTypes.All }
        }).ConfigureAwait(true);

        return chosen.Count == 0 ? null : chosen[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string defaultExtension)
    {
        IStorageProvider provider = _owner.StorageProvider;

        // The save picker starts in the per-user application data directory,
        // which exists on both platforms and is writable without a prompt.
        IStorageFolder? start = null;
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                start = await provider.TryGetFolderFromPathAsync(folder).ConfigureAwait(true);
        }
        catch (IOException)
        {
            // A missing or unreadable start folder is not a reason to refuse to
            // save; the picker just opens wherever the platform defaults to.
        }

        IStorageFile? file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            SuggestedStartLocation = start
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }
}
