// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using OdinCore.Diagnostics;

namespace EnneaOdin.ViewModels;

/// <summary>
/// One rendered log line, split into the three columns the pane shows.
/// </summary>
public sealed class LogRowViewModel
{
    private LogRowViewModel(string time, string level, string message, OdinLevel raw)
    {
        Time = time;
        Level = level;
        Message = message;
        IsInfo = raw == OdinLevel.Info;
        IsDebug = raw == OdinLevel.Debug;
        IsWarning = raw == OdinLevel.Warning;
        IsError = raw == OdinLevel.Error;

        // Heimdall's own shape: the message, and a prefix only where Heimdall
        // prints one.
        Line = raw switch
        {
            OdinLevel.Error => "ERROR: " + message,
            OdinLevel.Warning => "WARNING: " + message,
            OdinLevel.Debug => "  " + message,
            _ => message
        };

        bool success = raw == OdinLevel.Info
                       && (message.EndsWith(" successful", StringComparison.Ordinal)
                           || message.EndsWith(" successful.", StringComparison.Ordinal)
                           || message.EndsWith(" begun.", StringComparison.Ordinal));

        BrushKey = raw switch
        {
            OdinLevel.Error => "RedText",
            OdinLevel.Warning => "OrangeText",
            OdinLevel.Debug => "TextTertiary",
            _ => success ? "GreenText" : "TextPrimary"
        };
    }

    /// <summary>The line as the pane shows it, in Heimdall's shape.</summary>
    public string Line { get; }

    /// <summary>
    /// The Styles/Tokens.axaml brush key this line is drawn in. A key, not a
    /// brush: the view binds it as a DynamicResource so a light/dark switch
    /// repaints the log without rebuilding it.
    /// </summary>
    public string BrushKey { get; }

    /// <summary>"10:14:02".</summary>
    public string Time { get; }

    /// <summary>"INFO", "WARN", "ERR" or "DBG".</summary>
    public string Level { get; }

    public string Message { get; }

    public bool IsInfo { get; }
    public bool IsDebug { get; }
    public bool IsWarning { get; }
    public bool IsError { get; }

    public static LogRowViewModel From(OdinEvent e) => new(
        e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        LevelText(e.Level),
        e.Message,
        e.Level);

    /// <summary>
    /// The level column's text. Deliberately not derived from
    /// <see cref="OdinEvent.Prefix"/>, which carries its own brackets and
    /// trailing space for the saved-log form; the column wants the bare word.
    /// </summary>
    public static string LevelText(OdinLevel level) => level switch
    {
        OdinLevel.Error => "ERR",
        OdinLevel.Warning => "WARN",
        OdinLevel.Debug => "DBG",
        _ => "INFO"
    };

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", Time, Level, Message);
}
