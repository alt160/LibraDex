using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

internal static class Scalar8VarIdentityLayout
{
    public const uint Magic = 0x385653U;
    public const ushort FormatVersion = 3;
    public const ushort HeaderSize = 48;
    public const int KeySize = sizeof(ulong);
    public const int SlotOffsetSize = 3;
    public const int SlotKeyPrefixSize = sizeof(uint);
    public const int SlotSize = SlotOffsetSize + SlotKeyPrefixSize;
    public const int MaxRecordOffset = 0xFF_FFFF;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int FlagsOffset = 8;
    public const int ItemCountOffset = 12;
    public const int ShelfExtentSizeOffset = 16;
    public const int SlotStreamLengthOffset = 20;
    public const int RecordArenaEndOffset = 24;
    public const int SlotCapacityBytesOffset = 28;
    public const int NextShelfOffsetOffset = 32;
    public const int TailShelfOffsetOffset = 40;
    public const int SlotReserveIdentityLengthFloor = 17;

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
    public static uint ReadFlags(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FlagsOffset, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFlags(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FlagsOffset, sizeof(uint)), value);
    }

    /// <summary>
    /// Reads exact orphaned variable-identity record bytes persisted in this SV8 shelf.<br/>
    /// </summary>
    /// <param name="source">The shelf header or complete shelf byte image.<br/></param>
    /// <returns>The exact non-negative orphaned payload byte count.<br/></returns>
    public static int ReadReclaimablePayloadBytes(ReadOnlySpan<byte> source)
        => ReclaimablePayloadFlags.Read(ReadFlags(source));

    /// <summary>
    /// Persists exact orphaned variable-identity record bytes without changing shelf extent or chain pointers.<br/>
    /// </summary>
    /// <param name="target">The writable shelf header or complete shelf byte image.<br/></param>
    /// <param name="byteCount">The exact non-negative orphaned payload byte count.<br/></param>
    public static void WriteReclaimablePayloadBytes(Span<byte> target, int byteCount)
        => WriteFlags(target, ReclaimablePayloadFlags.Write(ReadFlags(target), byteCount));

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

    /// <summary>
    /// Reads the next linked SV8 shelf offset for same-key duplicate-run overflow.<br/>
    /// A zero value means the shelf is the terminal segment for its run.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes containing the SV8 header.</param>
    /// <returns>The next linked shelf offset, or zero when no overflow shelf exists.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadNextShelfOffset(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(NextShelfOffsetOffset, sizeof(long)));
    }

    /// <summary>
    /// Writes the next linked SV8 shelf offset for same-key duplicate-run overflow.<br/>
    /// The value is persisted in the shelf header so route reads can traverse duplicate-run chains without extra router state.<br/>
    /// </summary>
    /// <param name="target">The shelf bytes containing the SV8 header.</param>
    /// <param name="value">The next linked shelf offset, or zero when this shelf terminates the run.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNextShelfOffset(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(NextShelfOffsetOffset, sizeof(long)), value);
    }

    /// <summary>
    /// Reads the duplicate-run tail shelf offset from the SV8 chain head.<br/>
    /// Non-head shelves and unchained shelves store zero, allowing readers to ignore the field unless they are optimizing append-like duplicate runs.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes containing the SV8 header.</param>
    /// <returns>The terminal duplicate-run shelf offset, or zero when no tail pointer is present.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadTailShelfOffset(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(TailShelfOffsetOffset, sizeof(long)));
    }

    /// <summary>
    /// Writes the duplicate-run tail shelf offset into the SV8 chain head.<br/>
    /// The value is a fast-path hint for sorted same-key identity inserts; correctness remains anchored by the `nextShelfOffset` chain.<br/>
    /// </summary>
    /// <param name="target">The shelf bytes containing the SV8 header.</param>
    /// <param name="value">The terminal duplicate-run shelf offset, or zero when no chain exists.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTailShelfOffset(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(TailShelfOffsetOffset, sizeof(long)), value);
    }

    public static void Initialize(Span<byte> target, Scalar8VarIdentityProfile profile)
    {
        target.Clear();
        WriteMagic(target, Magic);
        WriteFormatVersion(target, FormatVersion);
        WriteHeaderSize(target, HeaderSize);
        WriteFlags(target, 0);
        WriteItemCount(target, 0);
        WriteShelfExtentSize(target, profile.ShelfExtentSize);
        WriteSlotStreamLength(target, 0);
        int slotCapacityBytes = CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        WriteSlotCapacityBytes(target, slotCapacityBytes);
        WriteNextShelfOffset(target, 0);
        WriteTailShelfOffset(target, 0);
        WriteRecordArenaEnd(target, HeaderSize + slotCapacityBytes);
    }

    public static int CalculateSlotCapacityBytes(int shelfExtentSize)
    {
        int recordBytesAtFloor = KeySize + GetVarUInt32Length(SlotReserveIdentityLengthFloor) + SlotReserveIdentityLengthFloor;
        int itemBytesAtFloor = recordBytesAtFloor + SlotSize;
        int usableBytes = checked(shelfExtentSize - HeaderSize);
        int slotCount = Math.Max(1, usableBytes / itemBytesAtFloor);
        return checked(slotCount * SlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CreateKeyPrefix(ulong encodedKey)
    {
        return (uint)(encodedKey >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadKey(ReadOnlySpan<byte> source, int recordOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(recordOffset, KeySize));
    }

    public static ReadOnlySpan<byte> ReadIdentity(ReadOnlySpan<byte> source, int recordOffset)
    {
        int cursor = checked(recordOffset + KeySize);
        int identityLength = ReadVarUInt32(source, ref cursor);
        return source.Slice(cursor, identityLength);
    }

    public static int CompareRecordKey(ReadOnlySpan<byte> source, int recordOffset, ulong encodedKey)
    {
        ulong key = ReadKey(source, recordOffset);
        if (key < encodedKey)
        {
            return -1;
        }

        return key > encodedKey ? 1 : 0;
    }

    public static int CompareRecordTuple(ReadOnlySpan<byte> source, int recordOffset, ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        int keyComparison = CompareRecordKey(source, recordOffset, encodedKey);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        return CompareIdentityBytes(ReadIdentity(source, recordOffset), identity);
    }

    /// <summary>
    /// Compares raw variable identity bytes with byte-ordinal semantics while reading eight bytes at a time where possible.<br/>
    /// Big-endian chunk reads preserve normal lexicographic byte ordering: the first differing byte dominates the numeric chunk comparison.<br/>
    /// Remaining tail bytes and length are compared only after all full 8-byte chunks match.<br/>
    /// </summary>
    /// <param name="left">The left raw identity bytes.</param>
    /// <param name="right">The right raw identity bytes.</param>
    /// <returns>A negative value when <paramref name="left"/> sorts before <paramref name="right"/>, zero when equal, or a positive value when after.</returns>
    public static int CompareIdentityBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int commonLength = Math.Min(left.Length, right.Length);
        int chunkLength = commonLength & ~7;
        int offset = 0;
        while (offset < chunkLength)
        {
            ulong leftChunk = BinaryPrimitives.ReadUInt64BigEndian(left.Slice(offset, sizeof(ulong)));
            ulong rightChunk = BinaryPrimitives.ReadUInt64BigEndian(right.Slice(offset, sizeof(ulong)));
            if (leftChunk < rightChunk)
            {
                return -1;
            }

            if (leftChunk > rightChunk)
            {
                return 1;
            }

            offset += sizeof(ulong);
        }

        while (offset < commonLength)
        {
            byte leftByte = left[offset];
            byte rightByte = right[offset];
            if (leftByte < rightByte)
            {
                return -1;
            }

            if (leftByte > rightByte)
            {
                return 1;
            }

            offset++;
        }

        if (left.Length < right.Length)
        {
            return -1;
        }

        return left.Length > right.Length ? 1 : 0;
    }

    /// <summary>
    /// Tests raw variable identity byte equality while reading eight bytes at a time where possible.<br/>
    /// This is the equality counterpart to <see cref="CompareIdentityBytes(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> and preserves exact byte equality semantics.<br/>
    /// </summary>
    /// <param name="left">The left raw identity bytes.</param>
    /// <param name="right">The right raw identity bytes.</param>
    /// <returns>`true` when both spans contain exactly the same bytes; otherwise `false`.</returns>
    public static bool IdentityBytesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        int chunkLength = left.Length & ~7;
        int offset = 0;
        while (offset < chunkLength)
        {
            ulong leftChunk = BinaryPrimitives.ReadUInt64BigEndian(left.Slice(offset, sizeof(ulong)));
            ulong rightChunk = BinaryPrimitives.ReadUInt64BigEndian(right.Slice(offset, sizeof(ulong)));
            if (leftChunk != rightChunk)
            {
                return false;
            }

            offset += sizeof(ulong);
        }

        while (offset < left.Length)
        {
            if (left[offset] != right[offset])
            {
                return false;
            }

            offset++;
        }

        return true;
    }

    public static int GetRecordLength(ReadOnlySpan<byte> source, int recordOffset)
    {
        int cursor = checked(recordOffset + KeySize);
        int identityLength = ReadVarUInt32(source, ref cursor);
        return checked(KeySize + (cursor - (recordOffset + KeySize)) + identityLength);
    }

    public static int GetNewRecordLength(int identityLength)
    {
        return KeySize + GetVarUInt32Length(identityLength) + identityLength;
    }

    public static void WriteRecord(Span<byte> target, int recordOffset, ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(recordOffset, KeySize), encodedKey);
        int cursor = checked(recordOffset + KeySize);
        WriteVarUInt32(target, ref cursor, identity.Length);
        identity.CopyTo(target.Slice(cursor, identity.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotRecordOffset(ReadOnlySpan<byte> source, int slotOffset)
    {
        return source[slotOffset] |
            (source[slotOffset + 1] << 8) |
            (source[slotOffset + 2] << 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotRecordOffset(Span<byte> target, int slotOffset, int recordOffset)
    {
        if (recordOffset < 0 || recordOffset > MaxRecordOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(recordOffset), recordOffset, "The SV8 slot record offset must fit in 24 bits.");
        }

        target[slotOffset] = (byte)recordOffset;
        target[slotOffset + 1] = (byte)(recordOffset >> 8);
        target[slotOffset + 2] = (byte)(recordOffset >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadSlotKeyPrefix(ReadOnlySpan<byte> source, int slotOffset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(source.Slice(slotOffset + SlotOffsetSize, SlotKeyPrefixSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotKeyPrefix(Span<byte> target, int slotOffset, uint keyPrefix)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(slotOffset + SlotOffsetSize, SlotKeyPrefixSize), keyPrefix);
    }

    public static int ReadVarUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = source[offset++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return checked((int)value);
            }

            shift += 7;
        }

        throw new InvalidDataException("The SV8 varint exceeds 32-bit length.");
    }

    public static void WriteVarUInt32(Span<byte> target, ref int offset, int value)
    {
        uint remaining = checked((uint)value);
        while (remaining >= 0x80)
        {
            target[offset++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        target[offset++] = (byte)remaining;
    }

    public static int GetVarUInt32Length(int value)
    {
        uint remaining = checked((uint)value);
        int length = 1;
        while (remaining >= 0x80)
        {
            remaining >>= 7;
            length++;
        }

        return length;
    }
}
