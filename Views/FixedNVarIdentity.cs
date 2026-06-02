using LibraDex.Layouts;

namespace LibraDex.Views;

internal static class FixedNVarIdentity
{
    public static byte[] CreateEmpty(FixedNVarIdentityProfile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        FixedNVarIdentityLayout.Initialize(bytes, profile);
        return bytes;
    }

    public static FixedNVarIdentityInsertResult Insert(
        ReadOnlyMemory<byte> existingBytes,
        FixedNVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertCore(existingBytes, mutableBytes: null, profile, key, identity, allowDuplicateKeys, out rewrittenBytes);
    }

    public static FixedNVarIdentityInsertResult InsertInPlace(
        byte[] existingBytes,
        FixedNVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = existingBytes;
        if (key.Length != profile.KeySize || identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
        {
            return FixedNVarIdentityInsertResult.Invalid;
        }

        if (!TryReadMutableHeader(existingBytes, profile, out int count, out int slotStreamLength, out int slotCapacityBytes, out int recordArenaEnd))
        {
            return FixedNVarIdentityInsertResult.Invalid;
        }

        int insertIndex = LowerBoundTuple(existingBytes, profile, count, key, identity);
        if (insertIndex < count && CompareSlotTuple(existingBytes, profile, insertIndex, key, identity) == 0)
        {
            return FixedNVarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = LowerBoundKey(existingBytes, profile, count, key);
            if (keyIndex < count && CompareSlotKey(existingBytes, profile, keyIndex, key) == 0)
            {
                return FixedNVarIdentityInsertResult.KeyConflict;
            }
        }

        if (slotStreamLength + FixedNVarIdentityLayout.SlotSize > slotCapacityBytes)
        {
            return FixedNVarIdentityInsertResult.Full;
        }

        int recordLength = FixedNVarIdentityLayout.GetNewRecordLength(profile.KeySize, identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > profile.ShelfExtentSize)
        {
            return FixedNVarIdentityInsertResult.Full;
        }

        int slotOffset = FixedNVarIdentityLayout.HeaderSize + checked(insertIndex * FixedNVarIdentityLayout.SlotSize);
        int slotTailLength = slotStreamLength - checked(insertIndex * FixedNVarIdentityLayout.SlotSize);
        if (slotTailLength > 0)
        {
            existingBytes.AsSpan(slotOffset, slotTailLength).CopyTo(existingBytes.AsSpan(slotOffset + FixedNVarIdentityLayout.SlotSize, slotTailLength));
        }

        FixedNVarIdentityLayout.WriteRecord(existingBytes, recordOffset, key, identity);
        FixedNVarIdentityLayout.WriteSlotRecordOffset(existingBytes, slotOffset, recordOffset);
        FixedNVarIdentityLayout.WriteSlotKeyPrefix(existingBytes, slotOffset, FixedNVarIdentityLayout.CreateKeyPrefix(key));
        FixedNVarIdentityLayout.WriteItemCount(existingBytes, count + 1);
        FixedNVarIdentityLayout.WriteSlotStreamLength(existingBytes, slotStreamLength + FixedNVarIdentityLayout.SlotSize);
        FixedNVarIdentityLayout.WriteRecordArenaEnd(existingBytes, newRecordArenaEnd);
        return FixedNVarIdentityInsertResult.Inserted;
    }

    private static FixedNVarIdentityInsertResult InsertCore(
        ReadOnlyMemory<byte> existingBytes,
        byte[]? mutableBytes,
        FixedNVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        if (key.Length != profile.KeySize || identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
        {
            return FixedNVarIdentityInsertResult.Invalid;
        }

        FixedNVarIdentityReadOnly readOnly = new(existingBytes, profile);
        if (!readOnly.IsValid)
        {
            return FixedNVarIdentityInsertResult.Invalid;
        }

        int insertIndex = readOnly.LowerBound(key, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, identity))
        {
            return FixedNVarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                return FixedNVarIdentityInsertResult.KeyConflict;
            }
        }

        if (TryInsertIntoReservedSlotRegion(existingBytes.Span, mutableBytes, profile, readOnly, insertIndex, key, identity, out rewrittenBytes))
        {
            return FixedNVarIdentityInsertResult.Inserted;
        }

        return TryRebuildWithInsert(profile, readOnly, insertIndex, key, identity, out rewrittenBytes)
            ? FixedNVarIdentityInsertResult.Inserted
            : FixedNVarIdentityInsertResult.Full;
    }

    public static bool TryBuildFromSorted(ReadOnlySpan<byte[]> keys, ReadOnlySpan<byte[]> identities, FixedNVarIdentityProfile profile, out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        FixedNVarIdentityLayout.Initialize(bytes, profile);
        if (keys.Length != identities.Length)
        {
            return false;
        }

        int slotLength = checked(keys.Length * FixedNVarIdentityLayout.SlotSize);
        int slotCapacityBytes = FixedNVarIdentityLayout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int recordCursor = FixedNVarIdentityLayout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length != profile.KeySize || identities[i].Length <= 0 || identities[i].Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = FixedNVarIdentityLayout.GetNewRecordLength(profile.KeySize, identities[i].Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            recordOffsets[i] = recordCursor;
            FixedNVarIdentityLayout.WriteRecord(bytes, recordCursor, keys[i], identities[i]);
            recordCursor += recordLength;
        }

        int slotCursor = FixedNVarIdentityLayout.HeaderSize;
        for (int i = 0; i < keys.Length; i++)
        {
            FixedNVarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            FixedNVarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, FixedNVarIdentityLayout.CreateKeyPrefix(keys[i]));
            slotCursor += FixedNVarIdentityLayout.SlotSize;
        }

        FixedNVarIdentityLayout.WriteItemCount(bytes, keys.Length);
        FixedNVarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - FixedNVarIdentityLayout.HeaderSize);
        FixedNVarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    private static bool TryInsertIntoReservedSlotRegion(
        ReadOnlySpan<byte> existingBytes,
        byte[]? mutableBytes,
        FixedNVarIdentityProfile profile,
        FixedNVarIdentityReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        int slotStreamLength = FixedNVarIdentityLayout.ReadSlotStreamLength(existingBytes);
        int slotCapacityBytes = FixedNVarIdentityLayout.ReadSlotCapacityBytes(existingBytes);
        int recordArenaEnd = FixedNVarIdentityLayout.ReadRecordArenaEnd(existingBytes);
        if (slotStreamLength + FixedNVarIdentityLayout.SlotSize > slotCapacityBytes)
        {
            return false;
        }

        int recordLength = FixedNVarIdentityLayout.GetNewRecordLength(profile.KeySize, identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > profile.ShelfExtentSize)
        {
            return false;
        }

        rewrittenBytes = mutableBytes ?? existingBytes.ToArray();
        int slotOffset = FixedNVarIdentityLayout.HeaderSize + checked(insertIndex * FixedNVarIdentityLayout.SlotSize);
        int slotTailLength = slotStreamLength - checked(insertIndex * FixedNVarIdentityLayout.SlotSize);
        if (slotTailLength < 0)
        {
            return false;
        }

        if (slotTailLength > 0)
        {
            rewrittenBytes.AsSpan(slotOffset, slotTailLength).CopyTo(rewrittenBytes.AsSpan(slotOffset + FixedNVarIdentityLayout.SlotSize, slotTailLength));
        }

        FixedNVarIdentityLayout.WriteRecord(rewrittenBytes, recordOffset, key, identity);
        FixedNVarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotOffset, recordOffset);
        FixedNVarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotOffset, FixedNVarIdentityLayout.CreateKeyPrefix(key));
        FixedNVarIdentityLayout.WriteItemCount(rewrittenBytes, readOnly.ItemCount + 1);
        FixedNVarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotStreamLength + FixedNVarIdentityLayout.SlotSize);
        FixedNVarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, newRecordArenaEnd);
        return true;
    }

    private static bool TryReadMutableHeader(
        ReadOnlySpan<byte> bytes,
        FixedNVarIdentityProfile profile,
        out int count,
        out int slotStreamLength,
        out int slotCapacityBytes,
        out int recordArenaEnd)
    {
        count = 0;
        slotStreamLength = 0;
        slotCapacityBytes = 0;
        recordArenaEnd = 0;
        if (bytes.Length < profile.ShelfExtentSize ||
            FixedNVarIdentityLayout.ReadMagic(bytes) != FixedNVarIdentityLayout.Magic ||
            FixedNVarIdentityLayout.ReadFormatVersion(bytes) != FixedNVarIdentityLayout.FormatVersion ||
            FixedNVarIdentityLayout.ReadHeaderSize(bytes) != FixedNVarIdentityLayout.HeaderSize ||
            FixedNVarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        count = FixedNVarIdentityLayout.ReadItemCount(bytes);
        slotStreamLength = FixedNVarIdentityLayout.ReadSlotStreamLength(bytes);
        slotCapacityBytes = FixedNVarIdentityLayout.ReadSlotCapacityBytes(bytes);
        recordArenaEnd = FixedNVarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotCapacityBytes = FixedNVarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize, profile.KeySize);
        int recordArenaStart = FixedNVarIdentityLayout.HeaderSize + slotCapacityBytes;
        return count >= 0 &&
            slotStreamLength == checked(count * FixedNVarIdentityLayout.SlotSize) &&
            slotStreamLength <= slotCapacityBytes &&
            slotCapacityBytes == expectedSlotCapacityBytes &&
            recordArenaEnd >= recordArenaStart &&
            recordArenaEnd <= profile.ShelfExtentSize;
    }

    private static int LowerBoundTuple(ReadOnlySpan<byte> bytes, FixedNVarIdentityProfile profile, int count, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            int comparison = CompareSlotTuple(bytes, profile, mid, key, identity);
            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int LowerBoundKey(ReadOnlySpan<byte> bytes, FixedNVarIdentityProfile profile, int count, ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            int comparison = CompareSlotKey(bytes, profile, mid, key);
            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int CompareSlotKey(ReadOnlySpan<byte> bytes, FixedNVarIdentityProfile profile, int slotIndex, ReadOnlySpan<byte> key)
    {
        int slotOffset = FixedNVarIdentityLayout.HeaderSize + checked(slotIndex * FixedNVarIdentityLayout.SlotSize);
        uint existingPrefix = FixedNVarIdentityLayout.ReadSlotKeyPrefix(bytes, slotOffset);
        uint prefix = FixedNVarIdentityLayout.CreateKeyPrefix(key);
        if (existingPrefix != prefix)
        {
            return existingPrefix < prefix ? -1 : 1;
        }

        int recordOffset = FixedNVarIdentityLayout.ReadSlotRecordOffset(bytes, slotOffset);
        return FixedNVarIdentityLayout.ReadKey(bytes, recordOffset, profile.KeySize).SequenceCompareTo(key);
    }

    private static int CompareSlotTuple(ReadOnlySpan<byte> bytes, FixedNVarIdentityProfile profile, int slotIndex, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int keyComparison = CompareSlotKey(bytes, profile, slotIndex, key);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        int slotOffset = FixedNVarIdentityLayout.HeaderSize + checked(slotIndex * FixedNVarIdentityLayout.SlotSize);
        int recordOffset = FixedNVarIdentityLayout.ReadSlotRecordOffset(bytes, slotOffset);
        return FixedNVarIdentityLayout.CompareIdentityBytes(FixedNVarIdentityLayout.ReadIdentity(bytes, recordOffset, profile.KeySize), identity);
    }

    private static bool TryRebuildWithInsert(
        FixedNVarIdentityProfile profile,
        FixedNVarIdentityReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        int newCount = checked(readOnly.ItemCount + 1);
        rewrittenBytes = new byte[profile.ShelfExtentSize];
        FixedNVarIdentityLayout.Initialize(rewrittenBytes, profile);
        int slotLength = checked(newCount * FixedNVarIdentityLayout.SlotSize);
        int slotCapacityBytes = FixedNVarIdentityLayout.ReadSlotCapacityBytes(rewrittenBytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = FixedNVarIdentityLayout.HeaderSize;
        int recordCursor = FixedNVarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int sourceIndex = 0, targetIndex = 0; targetIndex < newCount; targetIndex++)
        {
            bool isIncoming = targetIndex == insertIndex;
            ReadOnlySpan<byte> currentKey = isIncoming ? key : readOnly.ReadKeyAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = isIncoming ? identity : readOnly.ReadIdentityAt(sourceIndex);
            int recordLength = FixedNVarIdentityLayout.GetNewRecordLength(profile.KeySize, currentIdentity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            FixedNVarIdentityLayout.WriteRecord(rewrittenBytes, recordCursor, currentKey, currentIdentity);
            FixedNVarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotCursor, recordCursor);
            FixedNVarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotCursor, FixedNVarIdentityLayout.CreateKeyPrefix(currentKey));
            slotCursor += FixedNVarIdentityLayout.SlotSize;
            recordCursor += recordLength;
            if (!isIncoming)
            {
                sourceIndex++;
            }
        }

        FixedNVarIdentityLayout.WriteItemCount(rewrittenBytes, newCount);
        FixedNVarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotCursor - FixedNVarIdentityLayout.HeaderSize);
        FixedNVarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, recordCursor);
        return true;
    }
}

internal sealed class FixedNVarIdentityReadOnly
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly FixedNVarIdentityProfile profile;
    private readonly int[] recordOffsets;
    private readonly uint[] keyPrefixes;

    public FixedNVarIdentityReadOnly(ReadOnlyMemory<byte> bytes, FixedNVarIdentityProfile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
        IsValid = TryDecodeSlots(bytes.Span, profile, out recordOffsets, out keyPrefixes);
        ItemCount = IsValid ? recordOffsets.Length : 0;
    }

    public bool IsValid { get; }

    public int ItemCount { get; }

    public int LowerBound(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        uint prefix = FixedNVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            int comparison = CompareSlotTuple(mid, prefix, key, identity);
            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    public int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        uint prefix = FixedNVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            int comparison = CompareSlotKey(mid, prefix, key);
            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    public bool Contains(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int slotIndex = LowerBound(key, identity);
        return slotIndex < ItemCount && CompareSlotTuple(slotIndex, FixedNVarIdentityLayout.CreateKeyPrefix(key), key, identity) == 0;
    }

    public ReadOnlySpan<byte> ReadKeyAt(int slotIndex)
    {
        return FixedNVarIdentityLayout.ReadKey(bytes.Span, recordOffsets[slotIndex], profile.KeySize);
    }

    public ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return FixedNVarIdentityLayout.ReadIdentity(bytes.Span, recordOffsets[slotIndex], profile.KeySize);
    }

    public void CopyIdentitiesInKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, List<byte[]> identities)
    {
        int start = LowerBoundKey(lowerKey);
        uint upperPrefix = FixedNVarIdentityLayout.CreateKeyPrefix(upperKey);
        for (int i = start; i < ItemCount; i++)
        {
            int comparison = CompareSlotKey(i, upperPrefix, upperKey);
            if (comparison > 0)
            {
                break;
            }

            identities.Add(ReadIdentityAt(i).ToArray());
        }
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, FixedNVarIdentityProfile profile, out int[] recordOffsets, out uint[] keyPrefixes)
    {
        recordOffsets = [];
        keyPrefixes = [];
        if (bytes.Length < profile.ShelfExtentSize ||
            FixedNVarIdentityLayout.ReadMagic(bytes) != FixedNVarIdentityLayout.Magic ||
            FixedNVarIdentityLayout.ReadFormatVersion(bytes) != FixedNVarIdentityLayout.FormatVersion ||
            FixedNVarIdentityLayout.ReadHeaderSize(bytes) != FixedNVarIdentityLayout.HeaderSize ||
            FixedNVarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        int count = FixedNVarIdentityLayout.ReadItemCount(bytes);
        int slotStreamLength = FixedNVarIdentityLayout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = FixedNVarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = FixedNVarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * FixedNVarIdentityLayout.SlotSize);
        int expectedSlotCapacityBytes = FixedNVarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize, profile.KeySize);
        int recordArenaStart = FixedNVarIdentityLayout.HeaderSize + slotCapacityBytes;
        if (count < 0 ||
            slotStreamLength != expectedSlotStreamLength ||
            slotStreamLength > slotCapacityBytes ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < recordArenaStart ||
            recordArenaEnd > profile.ShelfExtentSize)
        {
            return false;
        }

        recordOffsets = new int[count];
        keyPrefixes = new uint[count];
        int cursor = FixedNVarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = FixedNVarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = FixedNVarIdentityLayout.GetRecordLength(bytes, offset, profile.KeySize);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            recordOffsets[i] = offset;
            keyPrefixes[i] = FixedNVarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += FixedNVarIdentityLayout.SlotSize;
        }

        return true;
    }

    private int CompareSlotKey(int slotIndex, uint prefix, ReadOnlySpan<byte> key)
    {
        uint existingPrefix = keyPrefixes[slotIndex];
        if (existingPrefix != prefix)
        {
            return existingPrefix < prefix ? -1 : 1;
        }

        return ReadKeyAt(slotIndex).SequenceCompareTo(key);
    }

    private int CompareSlotTuple(int slotIndex, uint prefix, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int keyComparison = CompareSlotKey(slotIndex, prefix, key);
        return keyComparison != 0 ? keyComparison : FixedNVarIdentityLayout.CompareIdentityBytes(ReadIdentityAt(slotIndex), identity);
    }
}
