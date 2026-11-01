// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// SPDX-License-Identifier: GPL-3.0-or-later

namespace OdinCore.Diagnostics;

/// <summary>
/// Severity of a single log event. Mirrors the four Interface:: print styles
/// heimdall uses (Print, PrintWarning, PrintError, and verbose-only output);
/// see native/vendor/Heimdall/heimdall/source/Interface.cpp.
/// </summary>
public enum OdinLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// One log event. Nothing in OdinCore may discard an event by level: filtering
/// happens only where rows are rendered, which is what makes a verbosity
/// toggle in the UI retroactive.
/// </summary>
public sealed record OdinEvent(DateTimeOffset Time, OdinLevel Level, string Message, string? Category = null)
{
    public string Prefix => Level switch
    {
        OdinLevel.Error => "[ERR] ",
        OdinLevel.Warning => "[WARN] ",
        OdinLevel.Debug => "[DBG] ",
        _ => "[INFO] "
    };

    public static OdinEvent Debug(string message, string? category = null) =>
        new(DateTimeOffset.Now, OdinLevel.Debug, message, category);

    public static OdinEvent Info(string message, string? category = null) =>
        new(DateTimeOffset.Now, OdinLevel.Info, message, category);

    public static OdinEvent Warning(string message, string? category = null) =>
        new(DateTimeOffset.Now, OdinLevel.Warning, message, category);

    public static OdinEvent Error(string message, string? category = null) =>
        new(DateTimeOffset.Now, OdinLevel.Error, message, category);

    public override string ToString() =>
        Time.ToString("HH:mm:ss") + " " + Prefix + Message;
}
