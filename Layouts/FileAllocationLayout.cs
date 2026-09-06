using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed allocator-directory page used by file-backed LibraDex catalogs.<br/>
/// The directory is current-state allocation metadata rather than a mutation journal: each entry identifies the newest homogeneous segment for one exact extent length.<br/>
/// </summary>
internal static class FileAllocationDirectoryLayout
{
    public const int Size = 4096;
    public const ulong Magic = 0x434F4C4C4144584CUL;
    public const ushort FormatVersion = 1;
    public const int EntryCount = 64;
    public const int HeaderSize = 64;
    public const int EntrySize = 32;
    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 8;
    public const int HeaderSizeOffset = 10;
    public const int EntryCountOffset = 12;
    public const int GenerationOffset = 16;
    public const int EntriesOffset = HeaderSize;
    public const int EntryExtentLengthOffset = 0;
    public const int EntrySlotsPerSegmentOffset = 4;
    public const int EntryHeadSegmentOffset = 8;
    public const int EntrySegmentCountOffset = 16;

    /// <summary>
    /// Initializes an empty allocator directory page.<br/>
    /// Unused entries remain zero so legacy/default byte semantics stay cheap to validate.<br/>
    /// </summary>
    /// <param name="target">The complete directory page to initialize.<br/></param>
    public static void Initialize(Span<byte> target)
    {
        if (target.Length < Size)
            throw new ArgumentException($"Allocator directory requires {Size} bytes.", nameof(target));

        target.Slice(0, Size).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(MagicOffset, sizeof(ulong)), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(EntryCountOffset, sizeof(ushort)), EntryCount);
    }

    /// <summary>
    /// Determines whether a directory page has the supported fixed header.<br/>
    /// Entry topology is validated separately while the allocator reconstructs its segment map.<br/>
    /// </summary>
    /// <param name="source">The directory bytes to inspect.<br/></param>
    /// <returns><see langword="true"/> when the fixed header is valid; otherwise <see langword="false"/>.<br/></returns>
    public static bool IsValid(ReadOnlySpan<byte> source)
    {
        return source.Length >= Size &&
            BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(MagicOffset, sizeof(ulong))) == Magic &&
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort))) == FormatVersion &&
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort))) == HeaderSize &&
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(EntryCountOffset, sizeof(ushort))) == EntryCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<byte> GetEntry(Span<byte> target, int entryIndex) =>
        target.Slice(GetEntryOffset(entryIndex), EntrySize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> GetEntry(ReadOnlySpan<byte> source, int entryIndex) =>
        source.Slice(GetEntryOffset(entryIndex), EntrySize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadExtentLength(ReadOnlySpan<byte> entry) =>
        BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(EntryExtentLengthOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteExtentLength(Span<byte> entry, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(EntryExtentLengthOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSlotsPerSegment(ReadOnlySpan<byte> entry) =>
        BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(EntrySlotsPerSegmentOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotsPerSegment(Span<byte> entry, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(EntrySlotsPerSegmentOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadHeadSegmentOffset(ReadOnlySpan<byte> entry) =>
        BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(EntryHeadSegmentOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHeadSegmentOffset(Span<byte> entry, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(EntryHeadSegmentOffset, sizeof(long)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSegmentCount(ReadOnlySpan<byte> entry) =>
        BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(EntrySegmentCountOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSegmentCount(Span<byte> entry, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(EntrySegmentCountOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadGeneration(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt64LittleEndian(source.Slice(GenerationOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGeneration(Span<byte> target, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(GenerationOffset, sizeof(long)), value);

    private static int GetEntryOffset(int entryIndex)
    {
        if ((uint)entryIndex >= EntryCount)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));

        return EntriesOffset + (entryIndex * EntrySize);
    }
}

/// <summary>
/// Defines the fixed header at the front of one homogeneous file-allocation segment.<br/>
/// Slot occupancy describes current reusable capacity directly; it contains no operation history and requires no replay.<br/>
/// </summary>
internal static class FileAllocationSegmentLayout
{
    public const int HeaderSize = 4096;
    public const ulong Magic = 0x474553434F4C4C41UL;
    public const ushort FormatVersion = 1;
    public const int MaximumSlotCount = 256;
    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 8;
    public const int HeaderSizeOffset = 10;
    public const int ExtentLengthOffset = 12;
    public const int SlotCountOffset = 16;
    public const int UsedCountOffset = 20;
    public const int NextSegmentOffset = 24;
    public const int DirectoryEntryIndexOffset = 32;
    public const int GenerationOffset = 40;
    public const int MaterializedCountOffset = 48;
    public const int BitmapOffset = 64;
    public const int SlotOffsetsOffset = 512;

    /// <summary>
    /// Initializes one empty segment header.<br/>
    /// Payload offsets are materialized on demand, preventing unused slot capacity from inflating the physical file.<br/>
    /// </summary>
    /// <param name="target">The complete segment header page.<br/></param>
    /// <param name="extentLength">The exact homogeneous slot length.<br/></param>
    /// <param name="slotCount">The bounded number of slots in this segment.<br/></param>
    /// <param name="nextSegmentOffset">The prior class head, or zero.<br/></param>
    /// <param name="directoryEntryIndex">The owning directory entry.<br/></param>
    public static void Initialize(
        Span<byte> target,
        int extentLength,
        ushort slotCount,
        long nextSegmentOffset,
        int directoryEntryIndex)
    {
        if (target.Length < HeaderSize)
            throw new ArgumentException($"Allocation segment header requires {HeaderSize} bytes.", nameof(target));

        target.Slice(0, HeaderSize).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(MagicOffset, sizeof(ulong)), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ExtentLengthOffset, sizeof(int)), extentLength);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(SlotCountOffset, sizeof(ushort)), slotCount);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(UsedCountOffset, sizeof(ushort)), 0);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(NextSegmentOffset, sizeof(long)), nextSegmentOffset);
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(DirectoryEntryIndexOffset, sizeof(int)), directoryEntryIndex);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(GenerationOffset, sizeof(long)), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(MaterializedCountOffset, sizeof(ushort)), 0);
    }

    /// <summary>
    /// Validates the fixed segment header and its bounded bitmap geometry.<br/>
    /// </summary>
    /// <param name="source">The segment header bytes.<br/></param>
    /// <returns><see langword="true"/> when the header and counts are structurally valid; otherwise <see langword="false"/>.<br/></returns>
    public static bool IsValid(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(MagicOffset, sizeof(ulong))) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort))) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort))) != HeaderSize)
        {
            return false;
        }

        ushort slotCount = ReadSlotCount(source);
        ushort usedCount = ReadUsedCount(source);
        ushort materializedCount = ReadMaterializedCount(source);
        return ReadExtentLength(source) > 0 &&
            slotCount > 0 &&
            slotCount <= MaximumSlotCount &&
            materializedCount <= slotCount &&
            usedCount <= materializedCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadExtentLength(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ExtentLengthOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSlotCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(SlotCountOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUsedCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(UsedCountOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUsedCount(Span<byte> target, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(UsedCountOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadNextSegmentOffset(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt64LittleEndian(source.Slice(NextSegmentOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadDirectoryEntryIndex(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt32LittleEndian(source.Slice(DirectoryEntryIndexOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadGeneration(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt64LittleEndian(source.Slice(GenerationOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGeneration(Span<byte> target, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(GenerationOffset, sizeof(long)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadMaterializedCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(MaterializedCountOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMaterializedCount(Span<byte> target, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(MaterializedCountOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadSlotOffset(ReadOnlySpan<byte> source, int slotIndex) =>
        BinaryPrimitives.ReadInt64LittleEndian(source.Slice(SlotOffsetsOffset + (slotIndex * sizeof(long)), sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotOffset(Span<byte> target, int slotIndex, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(SlotOffsetsOffset + (slotIndex * sizeof(long)), sizeof(long)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOccupied(ReadOnlySpan<byte> source, int slotIndex)
    {
        int byteIndex = BitmapOffset + (slotIndex >> 3);
        int mask = 1 << (slotIndex & 7);
        return (source[byteIndex] & mask) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOccupied(Span<byte> target, int slotIndex, bool occupied)
    {
        int byteIndex = BitmapOffset + (slotIndex >> 3);
        byte mask = checked((byte)(1 << (slotIndex & 7)));
        target[byteIndex] = occupied
            ? (byte)(target[byteIndex] | mask)
            : (byte)(target[byteIndex] & ~mask);
    }
}
