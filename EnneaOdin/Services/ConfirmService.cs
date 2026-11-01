// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using EnneaOdin.Styles;
using FluentAvalonia.UI.Controls;

namespace EnneaOdin.Services;

/// <summary>
/// Modal confirmation and modal failure reporting, as a FluentAvalonia
/// <see cref="ContentDialog"/> rather than a second top-level window.
/// </summary>
public sealed class ConfirmService : IConfirmService
{
    /// <summary>
    /// How tall the body may get before it scrolls. Not a row height: the
    /// content inside sizes itself, and this only bounds the dialogue.
    /// </summary>
    private const double MaxBodyHeight = 380;

    private readonly Window _owner;

    public ConfirmService(Window owner) =>
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel,
                                   bool monospaceBody = false) =>
        ShowAsync(title, message, confirmLabel, "Cancel", monospaceBody);

    public async Task ShowMessageAsync(string title, string message) =>
        await ShowAsync(title, message, "OK", null, false).ConfigureAwait(true);

    private async Task<bool> ShowAsync(string title, string message, string affirmative,
                                       string? negative, bool monospaceBody)
    {
        var text = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };

        if (monospaceBody && Application.Current is not null)
        {
            text.FontFamily = DesignTokens.Monospace(Application.Current);

            if (Application.Current.TryFindResource("FontSizeCaption", null, out object? size) &&
                size is double caption)
            {
                text.FontSize = caption;
            }
        }

        var body = new ScrollViewer
        {
            Content = text,
            MaxHeight = MaxBodyHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = affirmative,
            DefaultButton = ContentDialogButton.Primary
        };

        if (negative is not null)
            dialog.CloseButtonText = negative;

        // ShowAsync needs the window it is drawn into; the service is given the
        // one window this program has.
        ContentDialogResult result = await dialog.ShowAsync(_owner).ConfigureAwait(true);

        return result == ContentDialogResult.Primary;
    }
}
