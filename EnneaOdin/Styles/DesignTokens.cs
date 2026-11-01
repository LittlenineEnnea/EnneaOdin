// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace EnneaOdin.Styles;

/// <summary>
/// The startup guard over Styles/Tokens.axaml.
/// </summary>
internal static class DesignTokens
{
    /// <summary>
    /// Tokens that live in the theme dictionaries. Each one must resolve under
    /// Light and under Dark.
    /// </summary>
    private static readonly string[] ThemedKeys =
    {
        // surfaces and text
        "AppBackground", "CardBackground", "CardBorder", "SubtleFill", "Hairline",
        "TextPrimary", "TextSecondary", "TextTertiary", "TextOnAccent",

        // The seven hues, each in all three shapes. Listed out rather than
        // generated from a loop over hue names on purpose: a loop would pass
        // for a hue nothing references and would not fail for one the markup
        // spells differently, and this list is the record of what the markup
        // actually asks for.
        "BlueFill", "BlueTint", "BlueText",
        "GreenFill", "GreenTint", "GreenText",
        "OrangeFill", "OrangeTint", "OrangeText",
        "RedFill", "RedTint", "RedText",
        "PurpleFill", "PurpleTint", "PurpleText",
        "TealFill", "TealTint", "TealText",
        "PinkFill", "PinkTint", "PinkText",

        // the one accent, the band, the progress fill and the two shadows
        "AccentFill", "AccentFillHover", "AccentFillPressed", "RedDeep",
        "HeaderBandFill", "ProgressFill",
        "CardShadow", "BandShadow"
    };

    /// <summary>
    /// Tokens that are the same in both themes, so they sit at the dictionary
    /// root: the type scale, the spacing grid in both of the shapes Avalonia
    /// needs, the three composite thicknesses, the two corner radii and the
    /// monospace family.
    /// </summary>
    private static readonly string[] SharedKeys =
    {
        "FontSizeMicro", "FontSizeCaption", "FontSizeBody",
        "FontSizeSubtitle", "FontSizeTitle",
        "SpaceXs", "SpaceSm", "SpaceMd", "SpaceLg", "SpaceXl",
        "SpaceXsValue", "SpaceSmValue", "SpaceMdValue", "SpaceLgValue", "SpaceXlValue",
        "RowPadding", "RowRule", "GroupHeaderPadding", "BadgePadding",
        "CalloutPadding", "CalloutBar",
        "RadiusCard", "RadiusCardInner", "RadiusControl", "RadiusWell", "RadiusPill",
        "MonoFontFamily"
    };

    /// <summary>
    /// Resolve every required token, and throw naming all of the missing ones
    /// rather than the first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// One or more tokens could not be resolved. The message lists every one,
    /// so a typo is fixed in a single pass.
    /// </exception>
    internal static void Verify(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        var missing = new List<string>();

        foreach (string key in ThemedKeys)
        {
            if (!application.TryFindResource(key, ThemeVariant.Light, out _))
                missing.Add(key + " (Light)");

            if (!application.TryFindResource(key, ThemeVariant.Dark, out _))
                missing.Add(key + " (Dark)");
        }

        foreach (string key in SharedKeys)
        {
            if (!application.TryFindResource(key, null, out _))
                missing.Add(key);
        }

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            "Styles/Tokens.axaml is missing " + missing.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            " design token(s) that this project references: " +
            string.Join(", ", missing) +
            ". DynamicResource does not report a missing key, so this is checked here instead.");
    }

    /// <summary>
    /// The monospace family, for the two places a control is built in code
    /// rather than markup. Falls back to the platform default if the token is
    /// somehow absent, which <see cref="Verify"/> has already ruled out by the
    /// time anything can call this.
    /// </summary>
    internal static Avalonia.Media.FontFamily Monospace(Application application) =>
        application.TryFindResource("MonoFontFamily", null, out object? value) &&
        value is Avalonia.Media.FontFamily family
            ? family
            : Avalonia.Media.FontFamily.Default;
}
