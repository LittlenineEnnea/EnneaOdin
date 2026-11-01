// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall. The command and its reply format were reversed
// from the handset's own bootloader; see BridgeManager::RequestDeviceInfo.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OdinCore.Diagnostics;
using OdinCore.Native;

namespace OdinCore.Devices;

/// <summary>
/// What the handset says it is, from the download-mode protocol's "DVIF"
/// command.
///
/// EnneaOdin used to state that the protocol does not report the model name,
/// firmware version or sales code. That was wrong: DVIF answers with all
/// three, and asking does not consume the one ODIN handshake the handset
/// grants per entry into download mode.
/// </summary>
public sealed record DownloadModeIdentity(
    string? Model,
    string? UniqueNumber,
    string? Capacity,
    string? Vendor,
    string? FirmwareVersion,
    string? Product,
    string? Provisioning,
    string? SalesCode,
    string? Version,
    string? DeviceId,
    int? TmuTemperature,
    int? NadTemperature,
    string Raw)
{
    /// <summary>Longest reply the bootloader formats, plus a terminator.</summary>
    public const int MaxReplyBytes = 257;

    /// <summary>
    /// Ask the attached handset. Null when nothing answered, which is not an
    /// error: a handset whose protocol server is not running answers nothing,
    /// and so does one that is not attached.
    /// </summary>
    public static DownloadModeIdentity? Read(IProgress<OdinEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var callbacks = new OdinNativeCallbacks(log, "device");
        var buffer = new byte[MaxReplyBytes];

        try
        {
            var status = (OdinStatus)OdinNative.odin_read_device_info(
                buffer, buffer.Length, callbacks.Native);

            if (status != OdinStatus.Ok)
                return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException)
        {
            log.Report(OdinEvent.Debug("The engine has no DVIF support: " + ex.Message, "device"));
            return null;
        }
        finally
        {
            callbacks.KeepAlive();
        }

        int end = Array.IndexOf(buffer, (byte)0);
        string raw = Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();

        return raw.Length == 0 ? null : Parse(raw);
    }

    /// <summary>
    /// Pick the fields out of
    /// "@#MODEL=..;UN=..;CAPA=..;...;NAD_TEMP=..;@#". Tolerant on purpose: a
    /// field the handset leaves out is null rather than a parse failure, and
    /// an unknown field is ignored rather than fatal.
    /// </summary>
    public static DownloadModeIdentity Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string piece in raw.Trim('@', '#', ' ').Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = piece.IndexOf('=');

            if (equals <= 0)
                continue;

            fields[piece[..equals]] = piece[(equals + 1)..];
        }

        return new DownloadModeIdentity(
            Get(fields, "MODEL"),
            Get(fields, "UN"),
            Get(fields, "CAPA"),
            Get(fields, "VENDOR"),
            Get(fields, "FWVER"),
            Get(fields, "PRODUCT"),
            Get(fields, "PROV"),
            Get(fields, "SALES"),
            Get(fields, "VER"),
            Get(fields, "DID"),
            Number(fields, "TMU_TEMP"),
            Number(fields, "NAD_TEMP"),
            raw);
    }

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    private static int? Number(Dictionary<string, string> fields, string key) =>
        Get(fields, key) is string text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    /// <summary>Where these rows come from, for the panel's source column.</summary>
    public const string GroupDevice = "DEVICE";

    private const string Source = "the handset's DVIF reply";

    /// <summary>The rows for the information panel, in the order they read.</summary>
    public IEnumerable<InfoField> Rows()
    {
        yield return new InfoField(GroupDevice, "Model", Text(Model), Source);
        yield return new InfoField(GroupDevice, "Firmware version", Text(FirmwareVersion), Source);
        yield return new InfoField(GroupDevice, "Sales code", Text(SalesCode), Source);
        yield return new InfoField(GroupDevice, "Capacity", Text(Capacity), Source);
        yield return new InfoField(GroupDevice, "Vendor", Text(Vendor), Source);
        yield return new InfoField(GroupDevice, "Product", Text(Product), Source);
        yield return new InfoField(GroupDevice, "Unique number", Text(UniqueNumber), Source);
        yield return new InfoField(GroupDevice, "Device id", Text(DeviceId), Source);
        yield return new InfoField(GroupDevice, "Provisioning", Text(Provisioning), Source);
        yield return new InfoField(GroupDevice, "Protocol version", Text(Version), Source);

        if (TmuTemperature is int tmu)
        {
            yield return new InfoField(GroupDevice, "TMU temperature",
                tmu.ToString(CultureInfo.InvariantCulture) + " C", Source);
        }

        if (NadTemperature is int nad)
        {
            yield return new InfoField(GroupDevice, "NAD temperature",
                nad.ToString(CultureInfo.InvariantCulture) + " C", Source);
        }
    }

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not reported)" : value;
}
