// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/libpit/source/libpit.h
//   native/vendor/Heimdall/libpit/source/libpit.cpp
//   native/vendor/Heimdall/heimdall/source/PrintPitAction.cpp
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OdinCore.Protocol;

/// <summary>
/// A parsed Partition Information Table. Port of libpit::PitData
/// (libpit.h:251-382, libpit.cpp:65-274).
/// </summary>
public sealed class PitData
{
    /// <summary>libpit.h:257 (kFileIdentifier).</summary>
    public const uint Magic = 0x12349876;

    /// <summary>libpit.h:258 (kHeaderDataSize). Entries start here (libpit.h:271).</summary>
    public const int HeaderSize = 28;

    /// <summary>libpit.h:259 (kPaddedSizeMultiplicand).</summary>
    public const int PaddedSizeMultiplicand = 4096;

    internal const int OffsetMagic = 0;
    internal const int OffsetEntryCount = 4;
    internal const int OffsetComTar2 = 8;
    internal const int OffsetCpuBlId = 16;
    internal const int OffsetLogicUnitCount = 24;

    /// <summary>The eight-byte field at +8. libpit.cpp:94-96; shown by PrintPit as "Unknown string" (Interface.cpp:210).</summary>
    public const int ComTar2Length = 8;

    /// <summary>The eight-byte field at +16. libpit.cpp:97-99; shown as "CPU/bootloader tag" (Interface.cpp:211).</summary>
    public const int CpuBlIdLength = 8;

    private readonly List<PitEntry> _entries = new();

    /// <summary>+8, 8 bytes. libpit.cpp:94.</summary>
    public string ComTar2 { get; set; } = string.Empty;

    /// <summary>+16, 8 bytes. libpit.cpp:97.</summary>
    public string CpuBlId { get; set; } = string.Empty;

    /// <summary>+24, uint16. libpit.cpp:101, libpit.h:269.</summary>
    public ushort LogicUnitCount { get; set; }

    /// <summary>+4. libpit.cpp:90; libpit.h:347-350 (GetEntryCount).</summary>
    public int EntryCount => _entries.Count;

    public IReadOnlyList<PitEntry> Entries => _entries;

    /// <summary>libpit.h:352-355 (GetDataSize).</summary>
    public int DataSize => HeaderSize + EntryCount * PitEntry.RecordSize;

    /// <summary>libpit.h:357-366 (GetPaddedSize): DataSize rounded up to 4096.</summary>
    public int PaddedSize
    {
        get
        {
            int dataSize = DataSize;
            int padded = dataSize / PaddedSizeMultiplicand * PaddedSizeMultiplicand;

            if (dataSize % PaddedSizeMultiplicand != 0)
                padded += PaddedSizeMultiplicand;

            return padded;
        }
    }

    /// <summary>libpit.cpp:222-230 (GetEntry).</summary>
    public PitEntry GetEntry(int index) => _entries[index];

    public void Add(PitEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }

    /// <summary>libpit.cpp:206-220 (Clear).</summary>
    public void Clear()
    {
        _entries.Clear();
        ComTar2 = string.Empty;
        CpuBlId = string.Empty;
        LogicUnitCount = 0;
    }

    /// <summary>
    /// Parse a PIT. Port of libpit.cpp:81-145, with the two checks upstream
    /// does not make.
    /// </summary>
    public static bool TryUnpack(ReadOnlySpan<byte> data, out PitData? pit, out string? error)
    {
        pit = null;

        if (data.Length < HeaderSize)
        {
            error = string.Format(CultureInfo.InvariantCulture,
                "PIT data is {0} bytes, which is shorter than the {1}-byte header.",
                data.Length, HeaderSize);
            return false;
        }

        // libpit.cpp:83 -- reject anything whose first four bytes are not the
        // file identifier.
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMagic..]);
        if (magic != Magic)
        {
            error = string.Format(CultureInfo.InvariantCulture,
                "Not a PIT file: expected magic 0x{0:X8} at offset 0, found 0x{1:X8}.",
                Magic, magic);
            return false;
        }

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetEntryCount..]);  // libpit.cpp:90

        if (entryCount == 0)
        {
            error = "PIT declares zero entries; a PIT with no partitions is not usable.";
            return false;
        }

        // Guard the read upstream does not: HeaderSize + entryCount * 132 must
        // fit. The multiplication is done in long so a bogus count cannot wrap.
        long required = HeaderSize + (long)entryCount * PitEntry.RecordSize;
        if (required > data.Length)
        {
            error = string.Format(CultureInfo.InvariantCulture,
                "PIT declares {0} entries, which needs {1} bytes, but only {2} bytes are available.",
                entryCount, required, data.Length);
            return false;
        }

        var result = new PitData
        {
            // libpit.cpp:94-99 -- eight raw bytes each, NUL terminated for display.
            ComTar2 = ReadFixedString(data.Slice(OffsetComTar2, ComTar2Length)),
            CpuBlId = ReadFixedString(data.Slice(OffsetCpuBlId, CpuBlIdLength)),

            // libpit.cpp:101.
            LogicUnitCount = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetLogicUnitCount..])
        };

        for (uint i = 0; i < entryCount; i++)
        {
            // libpit.cpp:108 -- kHeaderDataSize + i * kDataSize.
            int entryOffset = HeaderSize + (int)i * PitEntry.RecordSize;
            ReadOnlySpan<byte> record = data.Slice(entryOffset, PitEntry.RecordSize);

            result._entries.Add(new PitEntry
            {
                BinaryType = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetBinaryType..]),
                DeviceType = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetDeviceType..]),
                Identifier = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetIdentifier..]),
                Attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetAttributes..]),
                UpdateAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetUpdateAttributes..]),
                BlockSizeOrOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetBlockSizeOrOffset..]),
                BlockCount = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetBlockCount..]),
                FileOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetFileOffset..]),
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(record[PitEntry.OffsetFileSize..]),
                PartitionName = ReadFixedString(record.Slice(PitEntry.OffsetPartitionName, PitEntry.PartitionNameMaxLength)),
                FlashFilename = ReadFixedString(record.Slice(PitEntry.OffsetFlashFilename, PitEntry.FlashFilenameMaxLength)),
                FotaFilename = ReadFixedString(record.Slice(PitEntry.OffsetFotaFilename, PitEntry.FotaFilenameMaxLength))
            });
        }

        pit = result;
        error = null;
        return true;
    }

    /// <summary>
    /// Serialize to a buffer of exactly <see cref="PaddedSize"/> bytes, zero
    /// filled beyond <see cref="DataSize"/>. Port of libpit.cpp:147-183;
    /// the padding is what BridgeManager.cpp:841-844 allocates and sends.
    /// </summary>
    public byte[] Pack()
    {
        var buffer = new byte[PaddedSize];
        Span<byte> data = buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(data[OffsetMagic..], Magic);                 // libpit.cpp:149
        BinaryPrimitives.WriteUInt32LittleEndian(data[OffsetEntryCount..], (uint)EntryCount); // libpit.cpp:151

        // libpit.cpp:153-154 -- memcpy of exactly 8 bytes from a char[8+1]
        // buffer (libpit.h:265, :267). The ninth byte is the in-memory NUL and
        // never reaches the file, so all 8 bytes of each header field are
        // usable and no NUL is reserved inside them. The entry name fields
        // below are different: libpit.h:212-215 keeps a NUL inside the field.
        WriteFixedString(data.Slice(OffsetComTar2, ComTar2Length), ComTar2, reserveNul: false);
        WriteFixedString(data.Slice(OffsetCpuBlId, CpuBlIdLength), CpuBlId, reserveNul: false);

        BinaryPrimitives.WriteUInt16LittleEndian(data[OffsetLogicUnitCount..], LogicUnitCount);// libpit.cpp:156

        for (int i = 0; i < _entries.Count; i++)
        {
            PitEntry entry = _entries[i];
            int entryOffset = HeaderSize + i * PitEntry.RecordSize;            // libpit.cpp:162
            Span<byte> record = data.Slice(entryOffset, PitEntry.RecordSize);

            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetBinaryType..], entry.BinaryType);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetDeviceType..], entry.DeviceType);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetIdentifier..], entry.Identifier);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetAttributes..], entry.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetUpdateAttributes..], entry.UpdateAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetBlockSizeOrOffset..], entry.BlockSizeOrOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetBlockCount..], entry.BlockCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetFileOffset..], entry.FileOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(record[PitEntry.OffsetFileSize..], entry.FileSize);

            WriteFixedString(record.Slice(PitEntry.OffsetPartitionName, PitEntry.PartitionNameMaxLength), entry.PartitionName, reserveNul: true);
            WriteFixedString(record.Slice(PitEntry.OffsetFlashFilename, PitEntry.FlashFilenameMaxLength), entry.FlashFilename, reserveNul: true);
            WriteFixedString(record.Slice(PitEntry.OffsetFotaFilename, PitEntry.FotaFilenameMaxLength), entry.FotaFilename, reserveNul: true);
        }

        return buffer;
    }

    /// <summary>
    /// libpit.cpp:232-252 (FindEntry by name). Only flashable entries are
    /// considered (libpit.cpp:236), and the comparison is strcmp: ordinal and
    /// case-sensitive.
    /// </summary>
    public PitEntry? FindByName(string partitionName) =>
        _entries.FirstOrDefault(e => e.IsFlashable
            && string.Equals(e.PartitionName, partitionName, StringComparison.Ordinal));

    /// <summary>
    /// The case-insensitive form the flash planner uses to match a file name
    /// against a partition name. Still ordinal -- never ToLower, which under
    /// tr-TR does not map "I" to "i".
    /// </summary>
    public PitEntry? FindByNameIgnoreCase(string partitionName) =>
        _entries.FirstOrDefault(e => e.IsFlashable
            && string.Equals(e.PartitionName, partitionName, StringComparison.OrdinalIgnoreCase));

    /// <summary>libpit.cpp:254-274 (FindEntry by identifier). Flashable entries only.</summary>
    public PitEntry? FindByIdentifier(uint identifier) =>
        _entries.FirstOrDefault(e => e.IsFlashable && e.Identifier == identifier);

    /// <summary>
    /// Match a firmware member against a PIT entry's "Flash Filename" field,
    /// which is the field the protocol itself nominates for exactly this
    /// (libpit.h:96 -- "USB flash filename"). Ordinal, case-insensitive;
    /// entries with an empty flash filename never match.
    /// </summary>
    public PitEntry? FindByFlashFilename(string flashFilename)
    {
        if (string.IsNullOrEmpty(flashFilename))
            return null;

        return _entries.FirstOrDefault(e => e.IsFlashable
            && e.FlashFilename.Length != 0
            && string.Equals(e.FlashFilename, flashFilename, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// libpit.cpp:185-204 (PitData::Matches). Used to refuse a flash when the
    /// user supplied a local PIT without --repartition and it disagrees with
    /// the device's (FlashAction.cpp:416-428).
    /// </summary>
    public bool Matches(PitData other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (EntryCount != other.EntryCount
            || !string.Equals(ComTar2, other.ComTar2, StringComparison.Ordinal)
            || !string.Equals(CpuBlId, other.CpuBlId, StringComparison.Ordinal)
            || LogicUnitCount != other.LogicUnitCount)
        {
            return false;
        }

        for (int i = 0; i < EntryCount; i++)
        {
            if (!_entries[i].Matches(other._entries[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// ASCII up to the first NUL inside the fixed field. libpit.cpp:94-99 for
    /// the header fields; the entry fields are read by SetPartitionName and
    /// friends, which strcpy up to the NUL (libpit.h:213).
    /// </summary>
    private static string ReadFixedString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
            end = field.Length;

        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++)
        {
            byte b = field[i];
            if (b >= 0x20 && b < 0x7F)
                sb.Append((char)b);
        }

        return sb.ToString();
    }

    /// <summary>
    /// NUL-pad a string into a fixed field. libpit.cpp:178-181 (memcpy of the
    /// full field).
    /// </summary>
    private static void WriteFixedString(Span<byte> field, string value, bool reserveNul)
    {
        field.Clear();

        int capacity = reserveNul ? field.Length - 1 : field.Length;
        int n = Math.Min(value.Length, capacity);
        for (int i = 0; i < n; i++)
        {
            char c = value[i];
            field[i] = c < 0x80 ? (byte)c : (byte)'?';
        }
    }
}
