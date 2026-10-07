using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

internal static class TerminalVarIdentityShelfLayout
{
    public const uint Magic = 0x56534954U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 40;
    public const int SlotOffsetSize = 3;
    public const int SlotSize = SlotOffsetSize;
    public const int MaxRecordOffset = 0xFF_FFFF;
    public const int SlotReserveIdentityLengthFloor = 17;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int ItemCountOffset = 8;
    public const int ShelfExtentSizeOffset = 12;
    public const int SlotStreamLengthOffset = 16;
    public const int RecordArenaEndOffset = 20;
    public const int SlotCapacityBytesOffset = 24;
    public const int NextShelfOffsetOffset = 32;

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
    public static int ReadShelfExtentSize(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ShelfExtentSizeOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteShelfExtentSize(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ShelfExtentSizeOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotStreamLength(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SlotStreamLengthOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotStreamLength(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(SlotStreamLengthOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadRecordArenaEnd(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(RecordArenaEndOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRecordArenaEnd(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(RecordArenaEndOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotCapacityBytes(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SlotCapacityBytesOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotCapacityBytes(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(SlotCapacityBytesOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadNextShelfOffset(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt64LittleEndian(source.Slice(NextShelfOffsetOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNextShelfOffset(Span<byte> target, long value) => BinaryPrimitives.WriteInt64LittleEndian(target.Slice(NextShelfOffsetOffset, sizeof(long)), value);

    public static void Initialize(Span<byte> target, int shelfExtentSize)
    {
        target.Clear();
        WriteMagic(target, Magic);
        WriteFormatVersion(target, FormatVersion);
        WriteHeaderSize(target, HeaderSize);
        WriteItemCount(target, 0);
        WriteShelfExtentSize(target, shelfExtentSize);
        WriteSlotStreamLength(target, 0);
        int slotCapacityBytes = CalculateSlotCapacityBytes(shelfExtentSize);
        WriteSlotCapacityBytes(target, slotCapacityBytes);
        WriteRecordArenaEnd(target, HeaderSize + slotCapacityBytes);
        WriteNextShelfOffset(target, 0);
    }

    public static int CalculateSlotCapacityBytes(int shelfExtentSize)
    {
        int recordBytesAtFloor = GetVarUInt32Length(SlotReserveIdentityLengthFloor) + SlotReserveIdentityLengthFloor;
        int itemBytesAtFloor = recordBytesAtFloor + SlotSize;
        int usableBytes = checked(shelfExtentSize - HeaderSize);
        int slotCount = Math.Max(1, usableBytes / itemBytesAtFloor);
        return checked(slotCount * SlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotRecordOffset(ReadOnlySpan<byte> source, int slotOffset)
    {
        return (source[slotOffset] << 16) | (source[slotOffset + 1] << 8) | source[slotOffset + 2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotRecordOffset(Span<byte> target, int slotOffset, int recordOffset)
    {
        if ((uint)recordOffset > MaxRecordOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(recordOffset), recordOffset, "The terminal var identity record offset exceeds the 24-bit slot encoding.");
        }

        target[slotOffset] = (byte)(recordOffset >> 16);
        target[slotOffset + 1] = (byte)(recordOffset >> 8);
        target[slotOffset + 2] = (byte)recordOffset;
    }

    public static ReadOnlySpan<byte> ReadIdentity(ReadOnlySpan<byte> source, int recordOffset)
    {
        int cursor = recordOffset;
        int length = ReadVarUInt32(source, ref cursor);
        return source.Slice(cursor, length);
    }

    public static ReadOnlySpan<byte> ReadIdentityAt(ReadOnlySpan<byte> source, int slotIndex)
    {
        int slotOffset = HeaderSize + checked(slotIndex * SlotSize);
        return ReadIdentity(source, ReadSlotRecordOffset(source, slotOffset));
    }

    public static int GetNewRecordLength(int identityLength) => checked(GetVarUInt32Length(identityLength) + identityLength);

    public static int GetRecordLength(ReadOnlySpan<byte> source, int recordOffset)
    {
        int cursor = recordOffset;
        int identityLength = ReadVarUInt32(source, ref cursor);
        return checked(cursor - recordOffset + identityLength);
    }

    public static void WriteRecord(Span<byte> target, int recordOffset, ReadOnlySpan<byte> identity)
    {
        int cursor = recordOffset;
        WriteVarUInt32(target, ref cursor, identity.Length);
        identity.CopyTo(target.Slice(cursor, identity.Length));
    }

    public static bool TryBuild(ReadOnlySpan<byte[]> identities, int start, int count, int shelfExtentSize, out byte[] bytes)
    {
        bytes = new byte[shelfExtentSize];
        Initialize(bytes, shelfExtentSize);
        int slotCapacityBytes = ReadSlotCapacityBytes(bytes);
        int slotCursor = HeaderSize;
        int recordCursor = HeaderSize + slotCapacityBytes;
        for (int i = 0; i < count; i++)
        {
            byte[] identity = identities[start + i];
            int recordLength = GetNewRecordLength(identity.Length);
            if (slotCursor + SlotSize > HeaderSize + slotCapacityBytes || recordCursor + recordLength > shelfExtentSize)
            {
                return false;
            }

            WriteRecord(bytes, recordCursor, identity);
            WriteSlotRecordOffset(bytes, slotCursor, recordCursor);
            slotCursor += SlotSize;
            recordCursor += recordLength;
        }

        WriteItemCount(bytes, count);
        WriteSlotStreamLength(bytes, checked(count * SlotSize));
        WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    public static int GetChunkCount(ReadOnlySpan<byte[]> identities, int start, int available, int shelfExtentSize)
    {
        int slotCapacityBytes = CalculateSlotCapacityBytes(shelfExtentSize);
        int slotCursor = HeaderSize;
        int recordCursor = HeaderSize + slotCapacityBytes;
        int count = 0;
        while (count < available)
        {
            byte[] identity = identities[start + count];
            int recordLength = GetNewRecordLength(identity.Length);
            if (slotCursor + SlotSize > HeaderSize + slotCapacityBytes || recordCursor + recordLength > shelfExtentSize)
            {
                break;
            }

            slotCursor += SlotSize;
            recordCursor += recordLength;
            count++;
        }

        return count;
    }

    public static void Validate(ReadOnlySpan<byte> bytes, int shelfExtentSize)
    {
        if (bytes.Length < shelfExtentSize ||
            ReadMagic(bytes) != Magic ||
            ReadFormatVersion(bytes) != FormatVersion ||
            ReadHeaderSize(bytes) != HeaderSize ||
            ReadShelfExtentSize(bytes) != shelfExtentSize)
        {
            throw new InvalidDataException("The terminal variable identity shelf bytes are invalid.");
        }

        int count = ReadItemCount(bytes);
        int slotStreamLength = ReadSlotStreamLength(bytes);
        int slotCapacityBytes = ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = ReadRecordArenaEnd(bytes);
        int recordArenaStart = HeaderSize + slotCapacityBytes;
        if (count < 0 ||
            slotStreamLength != count * SlotSize ||
            slotStreamLength > slotCapacityBytes ||
            slotCapacityBytes != CalculateSlotCapacityBytes(shelfExtentSize) ||
            recordArenaEnd < recordArenaStart ||
            recordArenaEnd > shelfExtentSize)
        {
            throw new InvalidDataException("The terminal variable identity shelf header is invalid.");
        }
    }

    private static int ReadVarUInt32(ReadOnlySpan<byte> source, ref int cursor)
    {
        uint result = 0;
        int shift = 0;
        while (shift <= 28)
        {
            byte value = source[cursor++];
            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return checked((int)result);
            }

            shift += 7;
        }

        throw new InvalidDataException("The terminal variable identity length is invalid.");
    }

    private static void WriteVarUInt32(Span<byte> target, ref int cursor, int value)
    {
        uint remaining = checked((uint)value);
        while (remaining >= 0x80)
        {
            target[cursor++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        target[cursor++] = (byte)remaining;
    }

    private static int GetVarUInt32Length(int value)
    {
        uint remaining = checked((uint)value);
        int length = 1;
        while (remaining >= 0x80)
        {
            length++;
            remaining >>= 7;
        }

        return length;
    }
}
