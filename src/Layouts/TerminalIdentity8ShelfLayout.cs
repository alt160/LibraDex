using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

internal static class TerminalIdentity8ShelfLayout
{
    public const uint Magic = 0x38534954U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
    public const int IdentitySize = sizeof(ulong);

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int ItemCountOffset = 8;
    public const int NextShelfOffsetOffset = 16;
    public const int IdentityBytesOffset = HeaderSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadMagic(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMagic(Span<byte> target, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadFormatVersion(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFormatVersion(Span<byte> target, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadHeaderSize(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHeaderSize(Span<byte> target, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadItemCount(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ItemCountOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ItemCountOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadNextShelfOffset(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt64LittleEndian(source.Slice(NextShelfOffsetOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNextShelfOffset(Span<byte> target, long value) => BinaryPrimitives.WriteInt64LittleEndian(target.Slice(NextShelfOffsetOffset, sizeof(long)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCapacity(int shelfExtentSize) => checked((shelfExtentSize - IdentityBytesOffset) / IdentitySize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadIdentity(ReadOnlySpan<byte> source, int slotIndex)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(IdentityBytesOffset + (slotIndex * IdentitySize), IdentitySize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteIdentity(Span<byte> target, int slotIndex, ulong encodedIdentity)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(IdentityBytesOffset + (slotIndex * IdentitySize), IdentitySize), encodedIdentity);
    }

    public static void Initialize(Span<byte> target, IReadOnlyList<ulong> identities, int startIndex, int count, long nextShelfOffset)
    {
        target.Clear();
        WriteMagic(target, Magic);
        WriteFormatVersion(target, FormatVersion);
        WriteHeaderSize(target, HeaderSize);
        WriteItemCount(target, count);
        WriteNextShelfOffset(target, nextShelfOffset);
        for (int i = 0; i < count; i++)
        {
            WriteIdentity(target, i, identities[startIndex + i]);
        }
    }
}
