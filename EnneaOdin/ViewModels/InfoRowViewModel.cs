// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using OdinCore.Devices;

namespace EnneaOdin.ViewModels;

/// <summary>
/// One row of the device-information list: which group it belongs to, what it
/// is called, what it says, and where the value came from.
/// </summary>
public sealed class InfoRowViewModel
{
    public InfoRowViewModel(string group, string name, string value, string source)
    {
        Group = group;
        Name = name;
        Value = value;
        Source = source;
    }

    public string Group { get; }
    public string Name { get; }
    public string Value { get; }
    public string Source { get; }

    public static InfoRowViewModel From(InfoField field) =>
        new(field.Group, field.Name, field.Value, field.Source);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "{0}  {1}  {2}", Group, Name, Value);
}
