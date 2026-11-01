// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/Interface.cpp  (Interface::PrintPit)
//   native/vendor/Heimdall/libpit/source/libpit.h
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace OdinCore.Protocol;

/// <summary>
/// Renders a PIT in Interface::PrintPit's field order and with its exact
/// labels (Interface.cpp:206-324), for "Save PIT report" and for the log.
/// </summary>
public static class PitTextFormatter
{
    public static string Format(PitData pit)
    {
        ArgumentNullException.ThrowIfNull(pit);

        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;

        sb.AppendLine("--- PIT Header ---");                                        // Interface.cpp:208
        sb.AppendLine(string.Format(c, "Entry Count: {0}", pit.EntryCount));         // Interface.cpp:209
        sb.AppendLine(string.Format(c, "Unknown string: {0}", pit.ComTar2));         // Interface.cpp:210
        sb.AppendLine(string.Format(c, "CPU/bootloader tag: {0}", pit.CpuBlId));     // Interface.cpp:211
        sb.AppendLine(string.Format(c, "Logic unit count: {0}", pit.LogicUnitCount));// Interface.cpp:212

        // Not in PrintPit: the computed sizes from libpit.h:352 and :357. A
        // dump that came back short shows up here and nowhere else.
        sb.AppendLine(string.Format(c, "PIT size: {0} bytes ({1} padded)", pit.DataSize, pit.PaddedSize));

        for (int i = 0; i < pit.EntryCount; i++)
        {
            PitEntry entry = pit.GetEntry(i);

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(string.Format(c, "--- Entry #{0} ---", i));                // Interface.cpp:218

            sb.AppendLine(string.Format(c, "Binary Type: {0} ({1})",
                entry.BinaryType, entry.BinaryTypeLabel));                           // Interface.cpp:219-236

            sb.AppendLine(string.Format(c, "Device Type: {0} ({1})",
                entry.DeviceType, entry.DeviceTypeLabel));                           // Interface.cpp:238-267

            sb.AppendLine(string.Format(c, "Identifier: {0}", entry.Identifier));     // Interface.cpp:269

            sb.AppendLine(string.Format(c, "Attributes: {0} ({1})",
                entry.Attributes, entry.AttributesLabel));                            // Interface.cpp:271-284

            // Interface.cpp:286-310 -- the parenthesis is printed only when
            // the field is non-zero.
            string updateLabel = entry.UpdateAttributesLabel;
            sb.AppendLine(updateLabel.Length == 0
                ? string.Format(c, "Update Attributes: {0}", entry.UpdateAttributes)
                : string.Format(c, "Update Attributes: {0} ({1})", entry.UpdateAttributes, updateLabel));

            sb.AppendLine(string.Format(c, "Partition Block Size/Offset: {0}", entry.BlockSizeOrOffset)); // Interface.cpp:312
            sb.AppendLine(string.Format(c, "Partition Block Count: {0}", entry.BlockCount));              // Interface.cpp:313

            sb.AppendLine(string.Format(c, "File Offset (Obsolete): {0}", entry.FileOffset));             // Interface.cpp:315
            sb.AppendLine(string.Format(c, "File Size (Obsolete): {0}", entry.FileSize));                 // Interface.cpp:316

            sb.AppendLine(string.Format(c, "Partition Name: {0}", entry.PartitionName));                  // Interface.cpp:318
            sb.AppendLine(string.Format(c, "Flash Filename: {0}", entry.FlashFilename));                  // Interface.cpp:319
            sb.AppendLine(string.Format(c, "FOTA Filename: {0}", entry.FotaFilename));                    // Interface.cpp:320
        }

        sb.AppendLine();                                                             // Interface.cpp:323
        return sb.ToString();
    }

    /// <summary>
    /// The header alone, as one line, for a log entry that should not drag a
    /// whole partition table into the pane.
    /// </summary>
    public static string FormatHeaderLine(PitData pit)
    {
        ArgumentNullException.ThrowIfNull(pit);

        return string.Format(CultureInfo.InvariantCulture,
            "PIT: {0} entries, logic units {1}, tag \"{2}\", string \"{3}\", {4} bytes",
            pit.EntryCount, pit.LogicUnitCount, pit.CpuBlId, pit.ComTar2, pit.DataSize);
    }
}
