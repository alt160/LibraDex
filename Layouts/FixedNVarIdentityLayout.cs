using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

internal static class FixedNVarIdentityLayout
{
    public const uint Magic = 0x46564CU;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
    public const int SlotOffsetSize = 3;
    public const int SlotKeyPrefixSize = sizeof(uint);
    public const int SlotSize = SlotOffsetSize + SlotKeyPrefixSize;
    public const int MaxRecordOffset = 0xFF_FFFF;
    public const int SlotReserveIdentityLengthFloor = 17;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int FlagsOffset = 8;
    public const int ItemCountOffset = 12;
    public const int ShelfExtentSizeOffset = 16;
    public const int SlotStreamLengthOffset = 20;
    public const int RecordArenaEndOffset = 24;
    public const int SlotCapacityBytesOffset = 28;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadMagic(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMagic(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), value);
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
    public static int ReadItemCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ItemCountOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ItemCountOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadShelfExtentSize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ShelfExtentSizeOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteShelfExtentSize(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ShelfExtentSizeOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotStreamLength(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SlotStreamLengthOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotStreamLength(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(SlotStreamLengthOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadRecordArenaEnd(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(RecordArenaEndOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRecordArenaEnd(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(RecordArenaEndOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotCapacityBytes(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SlotCapacityBytesOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotCapacityBytes(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(SlotCapacityBytesOffset, sizeof(int)), value);
    }

    public static void Initialize(Span<byte> target, FixedNVarIdentityProfile profile)
    {
        target.Clear();
        WriteMagic(target, Magic);
        WriteFormatVersion(target, FormatVersion);
        WriteHeaderSize(target, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FlagsOffset, sizeof(uint)), 0);
        WriteItemCount(target, 0);
        WriteShelfExtentSize(target, profile.ShelfExtentSize);
        WriteSlotStreamLength(target, 0);
        int slotCapacityBytes = CalculateSlotCapacityBytes(profile.ShelfExtentSize, profile.KeySize);
        WriteSlotCapacityBytes(target, slotCapacityBytes);
        WriteRecordArenaEnd(target, HeaderSize + slotCapacityBytes);
    }

    public static int CalculateSlotCapacityBytes(int shelfExtentSize, int keySize)
    {
        int recordBytesAtFloor = keySize + GetVarUInt32Length(SlotReserveIdentityLengthFloor) + SlotReserveIdentityLengthFloor;
        int itemBytesAtFloor = recordBytesAtFloor + SlotSize;
        int usableBytes = checked(shelfExtentSize - HeaderSize);
        int slotCount = Math.Max(1, usableBytes / itemBytesAtFloor);
        return checked(slotCount * SlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CreateKeyPrefix(ReadOnlySpan<byte> key)
    {
        uint prefix = 0;
        int count = Math.Min(4, key.Length);
        for (int i = 0; i < count; i++)
        {
            prefix |= (uint)key[i] << ((3 - i) * 8);
        }

        return prefix;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadKey(ReadOnlySpan<byte> source, int recordOffset, int keySize)
    {
        return source.Slice(recordOffset, keySize);
    }

    public static ReadOnlySpan<byte> ReadIdentity(ReadOnlySpan<byte> source, int recordOffset, int keySize)
    {
        int cursor = checked(recordOffset + keySize);
        int identityLength = ReadVarUInt32(source, ref cursor);
        return source.Slice(cursor, identityLength);
    }

    public static int CompareRecordTuple(ReadOnlySpan<byte> source, int recordOffset, int keySize, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int keyComparison = ReadKey(source, recordOffset, keySize).SequenceCompareTo(key);
        return keyComparison != 0 ? keyComparison : CompareIdentityBytes(ReadIdentity(source, recordOffset, keySize), identity);
    }

    public static int GetRecordLength(ReadOnlySpan<byte> source, int recordOffset, int keySize)
    {
        int cursor = checked(recordOffset + keySize);
        int identityLengthOffset = cursor;
        int identityLength = ReadVarUInt32(source, ref cursor);
        return checked((identityLengthOffset - recordOffset) + GetVarUInt32Length(identityLength) + identityLength);
    }

    public static int GetNewRecordLength(int keySize, int identityLength)
    {
        return checked(keySize + GetVarUInt32Length(identityLength) + identityLength);
    }

    public static void WriteRecord(Span<byte> target, int recordOffset, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        key.CopyTo(target.Slice(recordOffset, key.Length));
        int cursor = checked(recordOffset + key.Length);
        WriteVarUInt32(target, ref cursor, identity.Length);
        identity.CopyTo(target.Slice(cursor, identity.Length));
    }

    public static int ReadSlotRecordOffset(ReadOnlySpan<byte> source, int slotOffset)
    {
        return source[slotOffset] | (source[slotOffset + 1] << 8) | (source[slotOffset + 2] << 16);
    }

    public static void WriteSlotRecordOffset(Span<byte> target, int slotOffset, int recordOffset)
    {
        if ((uint)recordOffset > MaxRecordOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(recordOffset), recordOffset, "The FV record offset cannot be encoded in a 3-byte slot.");
        }

        target[slotOffset] = (byte)recordOffset;
        target[slotOffset + 1] = (byte)(recordOffset >> 8);
        target[slotOffset + 2] = (byte)(recordOffset >> 16);
    }

    public static uint ReadSlotKeyPrefix(ReadOnlySpan<byte> source, int slotOffset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(source.Slice(slotOffset + SlotOffsetSize, SlotKeyPrefixSize));
    }

    public static void WriteSlotKeyPrefix(Span<byte> target, int slotOffset, uint prefix)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(slotOffset + SlotOffsetSize, SlotKeyPrefixSize), prefix);
    }

    public static int CompareIdentityBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return left.SequenceCompareTo(right);
    }

    public static int ReadVarUInt32(ReadOnlySpan<byte> source, ref int cursor)
    {
        uint value = 0;
        int shift = 0;
        while (shift < 35)
        {
            byte current = source[cursor++];
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return checked((int)value);
            }

            shift += 7;
        }

        throw new FormatException("The FV varuint32 value is too large.");
    }

    public static void WriteVarUInt32(Span<byte> target, ref int cursor, int value)
    {
        uint remaining = checked((uint)value);
        while (remaining >= 0x80)
        {
            target[cursor++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        target[cursor++] = (byte)remaining;
    }

    public static int GetVarUInt32Length(int value)
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
