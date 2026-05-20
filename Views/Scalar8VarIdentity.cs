using LibraDex.Layouts;

namespace LibraDex.Views;

internal static class Scalar8VarIdentity
{
    public static byte[] CreateEmpty(Scalar8VarIdentityProfile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8VarIdentityLayout.Initialize(bytes, profile);
        return bytes;
    }

    public static Scalar8VarIdentityInsertResult Insert(
        ReadOnlyMemory<byte> existingBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertCore(existingBytes, mutableBytes: null, profile, encodedKey, identity, allowDuplicateKeys, out rewrittenBytes);
    }

    /// <summary>
    /// Inserts one scalar key and raw variable identity while allowing the supplied disk-shaped shelf buffer to be updated in place.<br/>
    /// Append-record mutations reuse the provided byte array and shift only persisted slot bytes; rebuild-required mutations still return a replacement image.<br/>
    /// This is the `SV8` durability-batch hot path counterpart to the `VS8` mutable shelf insert strategy.<br/>
    /// </summary>
    /// <param name="existingBytes">The mutable, disk-shaped shelf byte buffer.</param>
    /// <param name="profile">The `SV8` shelf profile used to decode and mutate the shelf.</param>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="identity">The raw variable identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <param name="rewrittenBytes">The authoritative shelf bytes after the insert.</param>
    /// <returns>The insert result.</returns>
    public static Scalar8VarIdentityInsertResult InsertInPlace(
        byte[] existingBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertCore(existingBytes, existingBytes, profile, encodedKey, identity, allowDuplicateKeys, out rewrittenBytes);
    }

    private static Scalar8VarIdentityInsertResult InsertCore(
        ReadOnlyMemory<byte> existingBytes,
        byte[]? mutableBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
        {
            return Scalar8VarIdentityInsertResult.Invalid;
        }

        Scalar8VarIdentityReadOnly readOnly = new(existingBytes, profile);
        if (!readOnly.IsValid)
        {
            return Scalar8VarIdentityInsertResult.Invalid;
        }

        int insertIndex = readOnly.LowerBound(encodedKey, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(encodedKey, identity))
        {
            return Scalar8VarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKey);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex) == encodedKey)
            {
                return Scalar8VarIdentityInsertResult.KeyConflict;
            }
        }

        if (TryInsertIntoReservedSlotRegion(existingBytes.Span, mutableBytes, profile, readOnly, insertIndex, encodedKey, identity, out rewrittenBytes))
        {
            return Scalar8VarIdentityInsertResult.Inserted;
        }

        return TryRebuildWithInsert(existingBytes.Span, profile, readOnly, insertIndex, encodedKey, identity, out rewrittenBytes)
            ? Scalar8VarIdentityInsertResult.Inserted
            : Scalar8VarIdentityInsertResult.Full;
    }

    public static bool TryGrowAndInsert(
        ReadOnlyMemory<byte> existingBytes,
        Scalar8VarIdentityProfile currentProfile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityProfile newProfile,
        out byte[] rewrittenBytes,
        out Scalar8VarIdentityInsertResult insertResult)
    {
        newProfile = currentProfile.NextGrowthClass();
        rewrittenBytes = [];
        insertResult = Scalar8VarIdentityInsertResult.Full;
        if (newProfile.ShelfExtentSize == currentProfile.ShelfExtentSize)
        {
            return false;
        }

        Scalar8VarIdentityReadOnly readOnly = new(existingBytes, currentProfile);
        if (!readOnly.IsValid)
        {
            insertResult = Scalar8VarIdentityInsertResult.Invalid;
            return false;
        }

        int insertIndex = readOnly.LowerBound(encodedKey, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(encodedKey, identity))
        {
            insertResult = Scalar8VarIdentityInsertResult.AlreadyPresent;
            return true;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKey);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex) == encodedKey)
            {
                insertResult = Scalar8VarIdentityInsertResult.KeyConflict;
                return true;
            }
        }

        if (!TryRebuildWithInsert(existingBytes.Span, newProfile, readOnly, insertIndex, encodedKey, identity, out rewrittenBytes))
        {
            insertResult = Scalar8VarIdentityInsertResult.Full;
            return false;
        }

        insertResult = Scalar8VarIdentityInsertResult.Inserted;
        return true;
    }

    private static bool TryInsertIntoReservedSlotRegion(
        ReadOnlySpan<byte> existingBytes,
        byte[]? mutableBytes,
        Scalar8VarIdentityProfile profile,
        Scalar8VarIdentityReadOnly readOnly,
        int insertIndex,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        int slotStreamLength = Scalar8VarIdentityLayout.ReadSlotStreamLength(existingBytes);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(existingBytes);
        int recordArenaEnd = Scalar8VarIdentityLayout.ReadRecordArenaEnd(existingBytes);
        int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
        int slotBytes = Scalar8VarIdentityLayout.SlotSize;
        if (slotStreamLength + slotBytes > slotCapacityBytes)
        {
            return false;
        }

        int slotOffset = Scalar8VarIdentityLayout.HeaderSize + checked(insertIndex * slotBytes);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > profile.ShelfExtentSize)
        {
            return false;
        }

        rewrittenBytes = mutableBytes ?? existingBytes.ToArray();
        int slotTailLength = slotStreamLength - checked(insertIndex * slotBytes);
        if (slotTailLength < 0)
        {
            return false;
        }

        if (slotTailLength > 0)
        {
            rewrittenBytes.AsSpan(slotOffset, slotTailLength).CopyTo(rewrittenBytes.AsSpan(slotOffset + slotBytes, slotTailLength));
        }

        Scalar8VarIdentityLayout.WriteRecord(rewrittenBytes, recordOffset, encodedKey, identity);
        Scalar8VarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotOffset, recordOffset);
        Scalar8VarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotOffset, Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey));
        Scalar8VarIdentityLayout.WriteItemCount(rewrittenBytes, readOnly.ItemCount + 1);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotStreamLength + slotBytes);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, newRecordArenaEnd);
        return true;
    }

    private static bool TryRebuildWithInsert(
        ReadOnlySpan<byte> existingBytes,
        Scalar8VarIdentityProfile targetProfile,
        Scalar8VarIdentityReadOnly readOnly,
        int insertIndex,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        int newCount = checked(readOnly.ItemCount + 1);
        rewrittenBytes = new byte[targetProfile.ShelfExtentSize];
        Scalar8VarIdentityLayout.Initialize(rewrittenBytes, targetProfile);
        int slotLength = checked(newCount * Scalar8VarIdentityLayout.SlotSize);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(rewrittenBytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int sourceIndex = 0, targetIndex = 0; targetIndex < newCount; targetIndex++)
        {
            bool isIncoming = targetIndex == insertIndex;
            ulong currentKey = isIncoming ? encodedKey : readOnly.ReadKeyAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = isIncoming ? identity : readOnly.ReadIdentityAt(sourceIndex);
            if (currentIdentity.Length <= 0 || currentIdentity.Length > targetProfile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(currentIdentity.Length);
            if (recordCursor + recordLength > targetProfile.ShelfExtentSize)
            {
                return false;
            }

            Scalar8VarIdentityLayout.WriteRecord(rewrittenBytes, recordCursor, currentKey, currentIdentity);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotCursor, recordCursor);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotCursor, Scalar8VarIdentityLayout.CreateKeyPrefix(currentKey));
            slotCursor += Scalar8VarIdentityLayout.SlotSize;
            recordCursor += recordLength;
            if (!isIncoming)
            {
                sourceIndex++;
            }
        }

        Scalar8VarIdentityLayout.WriteItemCount(rewrittenBytes, newCount);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotCursor - Scalar8VarIdentityLayout.HeaderSize);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, recordCursor);
        return true;
    }

    public static bool TryBuildFromSorted(ReadOnlySpan<ulong> keys, ReadOnlySpan<byte[]> identities, Scalar8VarIdentityProfile profile, out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        Scalar8VarIdentityLayout.Initialize(bytes, profile);
        if (keys.Length != identities.Length)
        {
            return false;
        }

        int slotLength = checked(keys.Length * Scalar8VarIdentityLayout.SlotSize);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            ReadOnlySpan<byte> identity = identities[i];
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            recordOffsets[i] = recordCursor;
            Scalar8VarIdentityLayout.WriteRecord(bytes, recordCursor, keys[i], identity);
            recordCursor += recordLength;
        }

        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < keys.Length; i++)
        {
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, Scalar8VarIdentityLayout.CreateKeyPrefix(keys[i]));
            slotCursor += Scalar8VarIdentityLayout.SlotSize;
        }

        Scalar8VarIdentityLayout.WriteItemCount(bytes, keys.Length);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - Scalar8VarIdentityLayout.HeaderSize);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }
}
