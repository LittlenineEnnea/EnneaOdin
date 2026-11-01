// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/libpit/source/libpit.h
//   native/vendor/Heimdall/libpit/source/libpit.cpp
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp
//   native/vendor/Heimdall/heimdall/source/Interface.cpp
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace OdinCore.Protocol;

/// <summary>Which processor a partition belongs to. libpit.h:53-57.</summary>
public enum PitBinaryType : uint
{
    ApplicationProcessor = 0,   // libpit.h:55 (kBinaryTypeApplicationProcessor)
    CommunicationProcessor = 1  // libpit.h:56 (kBinaryTypeCommunicationProcessor)
}

/// <summary>
/// Which storage device holds a partition. libpit.h:59-66. The enum is
/// unnamed and partly implicit upstream: OneNand is 0 and File, MMC and All
/// follow at 1, 2 and 3, then UFS jumps to 8.
/// </summary>
public enum PitDeviceType : uint
{
    OneNand = 0,    // libpit.h:61 (kDeviceTypeOneNand)
    File = 1,       // libpit.h:62 (kDeviceTypeFile, "FAT")
    Mmc = 2,        // libpit.h:63 (kDeviceTypeMMC)
    All = 3,        // libpit.h:64 (kDeviceTypeAll, marked "?" upstream)
    Ufs = 8         // libpit.h:65 (kDeviceTypeUFS)
}

/// <summary>libpit.h:68-73. Bit 2 is commented out upstream and is not named here.</summary>
[Flags]
public enum PitAttributes : uint
{
    None = 0,
    Write = 1,      // libpit.h:70 (kAttributeWrite)
    Stl = 1 << 1    // libpit.h:71 (kAttributeSTL)
}

/// <summary>libpit.h:75-79.</summary>
[Flags]
public enum PitUpdateAttributes : uint
{
    None = 0,
    Fota = 1,       // libpit.h:77 (kUpdateAttributeFota)
    Secure = 1 << 1 // libpit.h:78 (kUpdateAttributeSecure)
}

/// <summary>
/// One 132-byte PIT record. libpit.h:41-249; the field offsets are read in
/// libpit.cpp:106-142 and written in libpit.cpp:160-182.
/// </summary>
public sealed class PitEntry
{
    /// <summary>libpit.h:47 (kDataSize).</summary>
    public const int RecordSize = 132;

    /// <summary>libpit.h:48 (kPartitionNameMaxLength).</summary>
    public const int PartitionNameMaxLength = 32;

    /// <summary>libpit.h:49 (kFlashFilenameMaxLength).</summary>
    public const int FlashFilenameMaxLength = 32;

    /// <summary>libpit.h:50 (kFotaFilenameMaxLength).</summary>
    public const int FotaFilenameMaxLength = 32;

    // Field offsets within the record, as read in libpit.cpp:112-141.
    internal const int OffsetBinaryType = 0;
    internal const int OffsetDeviceType = 4;
    internal const int OffsetIdentifier = 8;
    internal const int OffsetAttributes = 12;
    internal const int OffsetUpdateAttributes = 16;
    internal const int OffsetBlockSizeOrOffset = 20;
    internal const int OffsetBlockCount = 24;
    internal const int OffsetFileOffset = 28;
    internal const int OffsetFileSize = 32;
    internal const int OffsetPartitionName = 36;
    internal const int OffsetFlashFilename = 68;    // 36 + 32, libpit.cpp:140
    internal const int OffsetFotaFilename = 100;    // 36 + 32 + 32, libpit.cpp:141

    private string _partitionName = string.Empty;
    private string _flashFilename = string.Empty;
    private string _fotaFilename = string.Empty;

    /// <summary>+0. libpit.cpp:112.</summary>
    public uint BinaryType { get; set; }

    /// <summary>+4. libpit.cpp:115.</summary>
    public uint DeviceType { get; set; }

    /// <summary>+8. libpit.cpp:118.</summary>
    public uint Identifier { get; set; }

    /// <summary>+12. libpit.cpp:121.</summary>
    public uint Attributes { get; set; }

    /// <summary>+16. libpit.cpp:124.</summary>
    public uint UpdateAttributes { get; set; }

    /// <summary>
    /// +20. libpit.cpp:127. Upstream's accessor carries the warning that
    /// "different versions of Loke (secondary bootloaders) on different
    /// devices interpret this differently" (libpit.h:161), so it is shown
    /// as a raw number and never used in a calculation.
    /// </summary>
    public uint BlockSizeOrOffset { get; set; }

    /// <summary>+24. libpit.cpp:130.</summary>
    public uint BlockCount { get; set; }

    /// <summary>+28, obsolete. libpit.cpp:133, libpit.h:92.</summary>
    public uint FileOffset { get; set; }

    /// <summary>+32, obsolete. libpit.cpp:136, libpit.h:93.</summary>
    public uint FileSize { get; set; }

    /// <summary>
    /// +36, 32 bytes. libpit.cpp:139.
    /// </summary>
    public string PartitionName
    {
        get => _partitionName;
        set => _partitionName = Truncate(value, PartitionNameMaxLength);
    }

    /// <summary>+68, 32 bytes. libpit.cpp:140.</summary>
    public string FlashFilename
    {
        get => _flashFilename;
        set => _flashFilename = Truncate(value, FlashFilenameMaxLength);
    }

    /// <summary>+100, 32 bytes. libpit.cpp:141.</summary>
    public string FotaFilename
    {
        get => _fotaFilename;
        set => _fotaFilename = Truncate(value, FotaFilenameMaxLength);
    }

    /// <summary>
    /// libpit.h:106-109 (IsFlashable). Both FindEntry overloads require it
    /// (libpit.cpp:236, :258), so an entry with an empty partition name can
    /// never be a flash target.
    /// </summary>
    public bool IsFlashable => _partitionName.Length != 0;

    /// <summary>
    /// The partition's capacity in bytes: block count times 512, or times
    /// 4096 when the device type is UFS. FlashAction.cpp:331-335.
    /// </summary>
    public long? PartitionSize
    {
        get
        {
            if (DeviceType != (uint)PitDeviceType.Mmc && DeviceType != (uint)PitDeviceType.Ufs)
                return null;                                    // FlashAction.cpp:328-330

            long blockSize = DeviceType == (uint)PitDeviceType.Ufs ? 4096 : 512;  // FlashAction.cpp:332-334
            return (long)BlockCount * blockSize;                // FlashAction.cpp:331
        }
    }

    /// <summary>The binary type as a label, in Interface::PrintPit's wording. Interface.cpp:221-234.</summary>
    public string BinaryTypeLabel => BinaryType switch
    {
        (uint)PitBinaryType.ApplicationProcessor => "AP",       // Interface.cpp:224
        (uint)PitBinaryType.CommunicationProcessor => "CP",     // Interface.cpp:228
        _ => "Unknown"                                          // Interface.cpp:232
    };

    /// <summary>The device type as a label, in Interface::PrintPit's wording. Interface.cpp:240-265.</summary>
    public string DeviceTypeLabel => DeviceType switch
    {
        (uint)PitDeviceType.OneNand => "OneNAND",               // Interface.cpp:243
        (uint)PitDeviceType.File => "File/FAT",                 // Interface.cpp:247
        (uint)PitDeviceType.Mmc => "MMC",                       // Interface.cpp:251
        (uint)PitDeviceType.All => "All (?)",                   // Interface.cpp:255
        (uint)PitDeviceType.Ufs => "UFS",                       // Interface.cpp:259
        _ => "Unknown"                                          // Interface.cpp:263
    };

    /// <summary>The attribute bits as text, in PrintPit's order. Interface.cpp:271-284.</summary>
    public string AttributesLabel
    {
        get
        {
            // Interface.cpp:273-282 -- "STL " first when set, then exactly one
            // of "Read/Write" or "Read-Only".
            string stl = (Attributes & (uint)PitAttributes.Stl) != 0 ? "STL " : string.Empty;
            string write = (Attributes & (uint)PitAttributes.Write) != 0 ? "Read/Write" : "Read-Only";
            return stl + write;
        }
    }

    /// <summary>The update-attribute bits as text. Interface.cpp:286-310.</summary>
    public string UpdateAttributesLabel
    {
        get
        {
            if (UpdateAttributes == 0)
                return string.Empty;                            // Interface.cpp:307-310

            bool fota = (UpdateAttributes & (uint)PitUpdateAttributes.Fota) != 0;
            bool secure = (UpdateAttributes & (uint)PitUpdateAttributes.Secure) != 0;

            // Interface.cpp:292-303.
            if (fota)
                return secure ? "FOTA, Secure" : "FOTA";

            return secure ? "Secure" : string.Empty;
        }
    }

    /// <summary>libpit.cpp:47-61 (PitEntry::Matches). Strings compare with strcmp semantics.</summary>
    public bool Matches(PitEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return BinaryType == other.BinaryType
            && DeviceType == other.DeviceType
            && Identifier == other.Identifier
            && Attributes == other.Attributes
            && UpdateAttributes == other.UpdateAttributes
            && BlockSizeOrOffset == other.BlockSizeOrOffset
            && BlockCount == other.BlockCount
            && FileOffset == other.FileOffset
            && FileSize == other.FileSize
            && string.Equals(_partitionName, other._partitionName, StringComparison.Ordinal)
            && string.Equals(_flashFilename, other._flashFilename, StringComparison.Ordinal)
            && string.Equals(_fotaFilename, other._fotaFilename, StringComparison.Ordinal);
    }

    public PitEntry Clone() => new()
    {
        BinaryType = BinaryType,
        DeviceType = DeviceType,
        Identifier = Identifier,
        Attributes = Attributes,
        UpdateAttributes = UpdateAttributes,
        BlockSizeOrOffset = BlockSizeOrOffset,
        BlockCount = BlockCount,
        FileOffset = FileOffset,
        FileSize = FileSize,
        PartitionName = _partitionName,
        FlashFilename = _flashFilename,
        FotaFilename = _fotaFilename
    };

    /// <summary>
    /// "BOOT (id 7, 102400 blocks, MMC)" -- the one-line form a plan row and a
    /// log line use.
    /// </summary>
    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "{0} (id {1}, {2} blocks, {3})",
        _partitionName.Length == 0 ? "(unnamed)" : _partitionName,
        Identifier, BlockCount, DeviceTypeLabel);

    public override string ToString() => Describe();

    /// <summary>
    /// Keep at most <paramref name="fieldLength"/> - 1 characters, so a NUL
    /// always fits in the fixed field. libpit.h:212-215 for the partition
    /// name; see the PartitionName remarks for the two buggy siblings.
    /// </summary>
    private static string Truncate(string? value, int fieldLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length < fieldLength ? value : value[..(fieldLength - 1)];
    }
}
