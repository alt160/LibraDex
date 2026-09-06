using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed byte layout for the LibraDex superblock at file offset zero.<br/>
/// This type owns offsets and primitive field access only; it does not own buffers or file I/O.<br/>
/// </summary>
internal static class SuperblockLayout
{
    public const int Size = 4096;
    public const ulong Magic = 0x5844444C4152424CUL;
    public const ushort FormatVersion = 1;
    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 8;
    public const int HeaderSizeOffset = 10;
    public const int FlagsOffset = 12;
    public const int ReservedPrefixBytesOffset = 16;
    public const int IndexDirectoryOffsetOffset = 24;
    public const int IndexDirectoryLengthOffset = 32;
    public const int IndexSlotCountOffset = 36;
    public const int FileGuidOffset = 64;
    public const int CreatedUtcTicksOffset = 80;
    public const int AllocationDirectoryOffsetOffset = 88;
    public const int DevBlockOffset = 1024;
    public const int DevIdentityOffset = DevBlockOffset;
    public const int DevIdentityByteLength = 128;
    public const int DevCustomTextOffset = DevIdentityOffset + DevIdentityByteLength;
    public const int DevCustomTextByteLength = 256;
    public const int DevGuidOffset = DevCustomTextOffset + DevCustomTextByteLength;
    public const int DevDate1UtcTicksOffset = DevGuidOffset + 16;
    public const int DevDate2UtcTicksOffset = DevDate1UtcTicksOffset + sizeof(long);
    public const int DevNumberOffset = DevDate2UtcTicksOffset + sizeof(long);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadMagic(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(MagicOffset, sizeof(ulong)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMagic(Span<byte> target, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(MagicOffset, sizeof(ulong)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadFormatVersion(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFormatVersion(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadHeaderSize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHeaderSize(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadFlags(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FlagsOffset, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFlags(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FlagsOffset, sizeof(uint)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadReservedPrefixBytes(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(ReservedPrefixBytesOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteReservedPrefixBytes(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(ReservedPrefixBytesOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadIndexDirectoryOffset(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(IndexDirectoryOffsetOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteIndexDirectoryOffset(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(IndexDirectoryOffsetOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadIndexDirectoryLength(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(IndexDirectoryLengthOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteIndexDirectoryLength(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(IndexDirectoryLengthOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadIndexSlotCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(IndexSlotCountOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteIndexSlotCount(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(IndexSlotCountOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid ReadFileGuid(ReadOnlySpan<byte> source)
    {
        return new Guid(source.Slice(FileGuidOffset, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFileGuid(Span<byte> target, Guid value)
    {
        value.TryWriteBytes(target.Slice(FileGuidOffset, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadCreatedUtcTicks(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(CreatedUtcTicksOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCreatedUtcTicks(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(CreatedUtcTicksOffset, sizeof(long)), value);
    }

    /// <summary>
    /// Reads the optional file-allocation directory offset.<br/>
    /// Zero identifies a legacy catalog that has not enabled durable extent segments.<br/>
    /// </summary>
    /// <param name="source">The complete superblock bytes.<br/></param>
    /// <returns>The allocation-directory offset, or zero.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadAllocationDirectoryOffset(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(AllocationDirectoryOffsetOffset, sizeof(long)));
    }

    /// <summary>
    /// Writes the optional file-allocation directory offset.<br/>
    /// The field occupies previously reserved system bytes and remains zero-compatible with existing format-version-one files.<br/>
    /// </summary>
    /// <param name="target">The complete superblock bytes.<br/></param>
    /// <param name="value">The allocation-directory offset, or zero.<br/></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAllocationDirectoryOffset(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(AllocationDirectoryOffsetOffset, sizeof(long)), value);
    }

    public static string ReadDevIdentity(ReadOnlySpan<byte> source)
    {
        return ReadFixedUtf8(source.Slice(DevIdentityOffset, DevIdentityByteLength));
    }

    public static void WriteDevIdentity(Span<byte> target, string? value)
    {
        WriteFixedUtf8(target.Slice(DevIdentityOffset, DevIdentityByteLength), value);
    }

    public static string ReadDevCustomText(ReadOnlySpan<byte> source)
    {
        return ReadFixedUtf8(source.Slice(DevCustomTextOffset, DevCustomTextByteLength));
    }

    public static void WriteDevCustomText(Span<byte> target, string? value)
    {
        WriteFixedUtf8(target.Slice(DevCustomTextOffset, DevCustomTextByteLength), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid ReadDevGuid(ReadOnlySpan<byte> source)
    {
        return new Guid(source.Slice(DevGuidOffset, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDevGuid(Span<byte> target, Guid value)
    {
        value.TryWriteBytes(target.Slice(DevGuidOffset, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadDevDate1UtcTicks(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DevDate1UtcTicksOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDevDate1UtcTicks(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(DevDate1UtcTicksOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadDevDate2UtcTicks(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DevDate2UtcTicksOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDevDate2UtcTicks(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(DevDate2UtcTicksOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadDevNumberUInt64(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(DevNumberOffset, sizeof(ulong)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadDevNumberInt64(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DevNumberOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDevNumber(Span<byte> target, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(DevNumberOffset, sizeof(ulong)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDevNumber(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(DevNumberOffset, sizeof(long)), value);
    }

    private static string ReadFixedUtf8(ReadOnlySpan<byte> source)
    {
        int length = source.IndexOf((byte)0);
        if (length < 0)
        {
            length = source.Length;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(source.Slice(0, length));
    }

    private static void WriteFixedUtf8(Span<byte> target, string? value)
    {
        target.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > target.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"UTF-8 value requires {byteCount} bytes but field allows {target.Length} bytes.");
        }

        Encoding.UTF8.GetBytes(value, target);
    }
}
