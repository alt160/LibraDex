using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

internal static class VarKeyVarIdentityLayout
{
    public const uint Magic = 0x56564CU;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
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
    public const int SlotReserveKeyLengthFloor = 17;

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

    /// <summary>
    /// Reads the reserved byte capacity for the persisted VV slot array.<br/>
    /// Version 4 shelves reserve this region ahead of records so non-append inserts can shift slot bytes without moving existing records.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes containing the VV header.</param>
    /// <returns>The reserved slot-array byte capacity.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotCapacityBytes(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(SlotCapacityBytesOffset, sizeof(int)));
    }

    /// <summary>
    /// Writes the reserved byte capacity for the persisted VV slot array.<br/>
    /// The value is part of the disk-shaped runtime buffer and determines where the forward-growing record arena begins.<br/>
    /// </summary>
    /// <param name="target">The shelf bytes containing the VV header.</param>
    /// <param name="value">The reserved slot-array byte capacity.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotCapacityBytes(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(SlotCapacityBytesOffset, sizeof(int)), value);
    }

    /// <summary>
    /// Reads the exclusive end offset of the forward-growing VV record arena.<br/>
    /// Version 4 shelves place records directly after the reserved fixed-width slot region, so this value is the next record write offset.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes containing the VV header.</param>
    /// <returns>The exclusive record arena end offset.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadRecordArenaEnd(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(RecordArenaEndOffset, sizeof(int)));
    }

    /// <summary>
    /// Writes the exclusive end offset of the forward-growing VV record arena.<br/>
    /// This value advances as records are appended after the slot array and leaves remaining shelf capacity as trailing slack.<br/>
    /// </summary>
    /// <param name="target">The shelf bytes containing the VV header.</param>
    /// <param name="value">The exclusive record arena end offset.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRecordArenaEnd(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(RecordArenaEndOffset, sizeof(int)), value);
    }

    public static void Initialize(Span<byte> target, VarKeyVarIdentityProfile profile)
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
        WriteRecordArenaEnd(target, HeaderSize + slotCapacityBytes);
    }

    /// <summary>
    /// Calculates the reserved VV slot-array capacity for a shelf extent using the 17-byte fixed-shape boundary as the density floor.<br/>
    /// This deliberately optimizes varlen shelves for keys larger than the existing 16-byte fixed families while still allowing pathological short-key shelves to grow or split correctly.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The physical shelf extent size in bytes.</param>
    /// <returns>The reserved slot-array byte capacity.</returns>
    public static int CalculateSlotCapacityBytes(int shelfExtentSize)
    {
        int recordBytesAtFloor = GetVarUInt32Length(SlotReserveKeyLengthFloor) + SlotReserveKeyLengthFloor + GetVarUInt32Length(SlotReserveKeyLengthFloor) + SlotReserveKeyLengthFloor;
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

    public static ReadOnlySpan<byte> ReadIdentity(ReadOnlySpan<byte> source, int recordOffset, out int keyLength)
    {
        int cursor = recordOffset;
        keyLength = ReadVarUInt32(source, ref cursor);
        cursor += keyLength;
        int identityLength = ReadVarUInt32(source, ref cursor);
        return source.Slice(cursor, identityLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadKey(ReadOnlySpan<byte> source, int recordOffset)
    {
        int cursor = recordOffset;
        int keyLength = ReadVarUInt32(source, ref cursor);
        return source.Slice(cursor, keyLength);
    }

    public static int CompareRecordKey(ReadOnlySpan<byte> source, int recordOffset, ReadOnlySpan<byte> key)
    {
        return ReadKey(source, recordOffset).SequenceCompareTo(key);
    }

    public static int CompareRecordTuple(ReadOnlySpan<byte> source, int recordOffset, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int keyComparison = CompareRecordKey(source, recordOffset, key);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        return CompareIdentityBytes(ReadIdentity(source, recordOffset, out _), identity);
    }

    public static int GetRecordLength(ReadOnlySpan<byte> source, int recordOffset)
    {
        int cursor = recordOffset;
        int keyLength = ReadVarUInt32(source, ref cursor);
        cursor += keyLength;
        int identityLengthOffset = cursor;
        int identityLength = ReadVarUInt32(source, ref cursor);
        return checked((cursor - recordOffset) + identityLength);
    }

    public static int GetNewRecordLength(int keyLength, int identityLength)
    {
        return GetVarUInt32Length(keyLength) + keyLength + GetVarUInt32Length(identityLength) + identityLength;
    }

    public static void WriteRecord(Span<byte> target, int recordOffset, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int cursor = recordOffset;
        WriteVarUInt32(target, ref cursor, key.Length);
        key.CopyTo(target.Slice(cursor, key.Length));
        cursor += key.Length;
        WriteVarUInt32(target, ref cursor, identity.Length);
        identity.CopyTo(target.Slice(cursor, identity.Length));
    }

    /// <summary>
    /// Compares raw VV identity bytes with byte-ordinal semantics while reading eight bytes at a time where possible.<br/>
    /// Big-endian chunk reads preserve normal lexicographic byte ordering because the first differing byte dominates the numeric chunk comparison.<br/>
    /// </summary>
    /// <param name="left">The left raw identity bytes.</param>
    /// <param name="right">The right raw identity bytes.</param>
    /// <returns>A negative value when left sorts before right, zero when equal, or a positive value when after.</returns>
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
    /// Tests raw VV identity bytes for exact equality while reading eight bytes at a time where possible.<br/>
    /// This is the equality counterpart to `CompareIdentityBytes`, keeping duplicate detection on the same widened byte path without changing byte-ordinal ordering semantics.<br/>
    /// Length is checked first so unequal-width identities short-circuit before any chunk reads.<br/>
    /// </summary>
    /// <param name="left">The left raw identity bytes.</param>
    /// <param name="right">The right raw identity bytes.</param>
    /// <returns>`true` when both spans contain the same bytes in the same order; otherwise `false`.</returns>
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

    /// <summary>
    /// Reads a fixed 3-byte persisted record offset from a VV slot entry.<br/>
    /// Slots keep offsets fixed-width on disk so middle insert mutation can shift a predictable slot-array tail instead of dealing with varint width changes.<br/>
    /// Runtime views still promote this value to a normal integer for hot comparisons and record access.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes containing the slot entry.</param>
    /// <param name="slotOffset">The byte offset where the slot entry starts.</param>
    /// <returns>The decoded record offset.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSlotRecordOffset(ReadOnlySpan<byte> source, int slotOffset)
    {
        return source[slotOffset] |
            (source[slotOffset + 1] << 8) |
            (source[slotOffset + 2] << 16);
    }

    /// <summary>
    /// Writes a fixed 3-byte persisted record offset into a VV slot entry.<br/>
    /// The 24-bit range is intentionally larger than current shelf extents so the persisted slot format stays stable across planned shelf growth classes.<br/>
    /// </summary>
    /// <param name="target">The shelf bytes containing the slot entry.</param>
    /// <param name="slotOffset">The byte offset where the slot entry starts.</param>
    /// <param name="recordOffset">The record offset to persist.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="recordOffset"/> does not fit in 24 bits.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotRecordOffset(Span<byte> target, int slotOffset, int recordOffset)
    {
        if (recordOffset < 0 || recordOffset > MaxRecordOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(recordOffset), recordOffset, "The VV slot record offset must fit in 24 bits.");
        }

        target[slotOffset] = (byte)recordOffset;
        target[slotOffset + 1] = (byte)(recordOffset >> 8);
        target[slotOffset + 2] = (byte)(recordOffset >> 16);
    }

    /// <summary>
    /// Reads the fixed 4-byte key prefix from a VV slot entry.<br/>
    /// Prefix bytes are persisted big-endian so the numeric prefix order matches raw byte lexical order.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes containing the slot entry.</param>
    /// <param name="slotOffset">The byte offset where the slot entry starts.</param>
    /// <returns>The decoded key prefix.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadSlotKeyPrefix(ReadOnlySpan<byte> source, int slotOffset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(source.Slice(slotOffset + SlotOffsetSize, SlotKeyPrefixSize));
    }

    /// <summary>
    /// Writes the fixed 4-byte key prefix into a VV slot entry.<br/>
    /// The prefix is a comparison accelerator only; full key comparison still reads the record bytes when prefixes match.<br/>
    /// </summary>
    /// <param name="target">The shelf bytes containing the slot entry.</param>
    /// <param name="slotOffset">The byte offset where the slot entry starts.</param>
    /// <param name="keyPrefix">The big-endian lexical key prefix to persist.</param>
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

        throw new InvalidDataException("The VV varint exceeds 32-bit length.");
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
