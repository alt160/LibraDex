using LibraDex.Layouts;

namespace LibraDex.Views;

internal static class Scalar16VarIdentity
{
    public static byte[] CreateEmpty(Scalar16VarIdentityProfile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16VarIdentityLayout.Initialize(bytes, profile);
        return bytes;
    }

    public static Scalar16VarIdentityInsertResult Insert(
        ReadOnlyMemory<byte> existingBytes,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertCore(existingBytes, mutableBytes: null, profile, encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys, out rewrittenBytes);
    }

    /// <summary>
    /// Inserts one scalar key and raw variable identity while allowing the supplied disk-shaped shelf buffer to be updated in place.<br/>
    /// Append-record mutations reuse the provided byte array and shift only persisted slot bytes; rebuild-required mutations still return a replacement image.<br/>
    /// This is the `SV16` durability-batch hot path counterpart to the `VS8` mutable shelf insert strategy.<br/>
    /// </summary>
    /// <param name="existingBytes">The mutable, disk-shaped shelf byte buffer.</param>
    /// <param name="profile">The `SV16` shelf profile used to decode and mutate the shelf.</param>
    /// <param name="encodedKeyHigh">The encoded scalar key high half to insert.</param>
    /// <param name="encodedKeyLow">The encoded scalar key low half to insert.</param>
    /// <param name="identity">The raw variable identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <param name="rewrittenBytes">The authoritative shelf bytes after the insert.</param>
    /// <returns>The insert result.</returns>
    public static Scalar16VarIdentityInsertResult InsertInPlace(
        byte[] existingBytes,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertCore(existingBytes, existingBytes, profile, encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys, out rewrittenBytes);
    }

    private static Scalar16VarIdentityInsertResult InsertCore(
        ReadOnlyMemory<byte> existingBytes,
        byte[]? mutableBytes,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
        {
            return Scalar16VarIdentityInsertResult.Invalid;
        }

        Scalar16VarIdentityReadOnly readOnly = new(existingBytes, profile);
        if (!readOnly.IsValid)
        {
            return Scalar16VarIdentityInsertResult.Invalid;
        }

        int insertIndex = readOnly.LowerBound(encodedKeyHigh, encodedKeyLow, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(encodedKeyHigh, encodedKeyLow, identity))
        {
            return Scalar16VarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKeyHigh, encodedKeyLow);
            if (keyIndex < readOnly.ItemCount &&
                readOnly.ReadKeyHighAt(keyIndex) == encodedKeyHigh &&
                readOnly.ReadKeyLowAt(keyIndex) == encodedKeyLow)
            {
                return Scalar16VarIdentityInsertResult.KeyConflict;
            }
        }

        if (TryInsertIntoReservedSlotRegion(existingBytes.Span, mutableBytes, profile, readOnly, insertIndex, encodedKeyHigh, encodedKeyLow, identity, out rewrittenBytes))
        {
            return Scalar16VarIdentityInsertResult.Inserted;
        }

        return TryRebuildWithInsert(existingBytes.Span, profile, readOnly, insertIndex, encodedKeyHigh, encodedKeyLow, identity, out rewrittenBytes)
            ? Scalar16VarIdentityInsertResult.Inserted
            : Scalar16VarIdentityInsertResult.Full;
    }

    public static bool TryGrowAndInsert(
        ReadOnlyMemory<byte> existingBytes,
        Scalar16VarIdentityProfile currentProfile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar16VarIdentityProfile newProfile,
        out byte[] rewrittenBytes,
        out Scalar16VarIdentityInsertResult insertResult)
    {
        newProfile = currentProfile.NextGrowthClass();
        rewrittenBytes = [];
        insertResult = Scalar16VarIdentityInsertResult.Full;
        if (newProfile.ShelfExtentSize == currentProfile.ShelfExtentSize)
        {
            return false;
        }

        Scalar16VarIdentityReadOnly readOnly = new(existingBytes, currentProfile);
        if (!readOnly.IsValid)
        {
            insertResult = Scalar16VarIdentityInsertResult.Invalid;
            return false;
        }

        int insertIndex = readOnly.LowerBound(encodedKeyHigh, encodedKeyLow, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(encodedKeyHigh, encodedKeyLow, identity))
        {
            insertResult = Scalar16VarIdentityInsertResult.AlreadyPresent;
            return true;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKeyHigh, encodedKeyLow);
            if (keyIndex < readOnly.ItemCount &&
                readOnly.ReadKeyHighAt(keyIndex) == encodedKeyHigh &&
                readOnly.ReadKeyLowAt(keyIndex) == encodedKeyLow)
            {
                insertResult = Scalar16VarIdentityInsertResult.KeyConflict;
                return true;
            }
        }

        if (!TryRebuildWithInsert(existingBytes.Span, newProfile, readOnly, insertIndex, encodedKeyHigh, encodedKeyLow, identity, out rewrittenBytes))
        {
            insertResult = Scalar16VarIdentityInsertResult.Full;
            return false;
        }

        insertResult = Scalar16VarIdentityInsertResult.Inserted;
        return true;
    }

    private static bool TryInsertIntoReservedSlotRegion(
        ReadOnlySpan<byte> existingBytes,
        byte[]? mutableBytes,
        Scalar16VarIdentityProfile profile,
        Scalar16VarIdentityReadOnly readOnly,
        int insertIndex,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        int slotStreamLength = Scalar16VarIdentityLayout.ReadSlotStreamLength(existingBytes);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(existingBytes);
        int recordArenaEnd = Scalar16VarIdentityLayout.ReadRecordArenaEnd(existingBytes);
        int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(identity.Length);
        int slotBytes = Scalar16VarIdentityLayout.SlotSize;
        if (slotStreamLength + slotBytes > slotCapacityBytes)
        {
            return false;
        }

        int slotOffset = Scalar16VarIdentityLayout.HeaderSize + checked(insertIndex * slotBytes);
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

        Scalar16VarIdentityLayout.WriteRecord(rewrittenBytes, recordOffset, encodedKeyHigh, encodedKeyLow, identity);
        Scalar16VarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotOffset, recordOffset);
        Scalar16VarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotOffset, Scalar16VarIdentityLayout.CreateKeyPrefix(encodedKeyHigh, encodedKeyLow));
        Scalar16VarIdentityLayout.WriteItemCount(rewrittenBytes, readOnly.ItemCount + 1);
        Scalar16VarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotStreamLength + slotBytes);
        Scalar16VarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, newRecordArenaEnd);
        return true;
    }

    private static bool TryRebuildWithInsert(
        ReadOnlySpan<byte> existingBytes,
        Scalar16VarIdentityProfile targetProfile,
        Scalar16VarIdentityReadOnly readOnly,
        int insertIndex,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        int newCount = checked(readOnly.ItemCount + 1);
        rewrittenBytes = new byte[targetProfile.ShelfExtentSize];
        Scalar16VarIdentityLayout.Initialize(rewrittenBytes, targetProfile);
        int slotLength = checked(newCount * Scalar16VarIdentityLayout.SlotSize);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(rewrittenBytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = Scalar16VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar16VarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int sourceIndex = 0, targetIndex = 0; targetIndex < newCount; targetIndex++)
        {
            bool isIncoming = targetIndex == insertIndex;
            ulong currentKeyHigh = isIncoming ? encodedKeyHigh : readOnly.ReadKeyHighAt(sourceIndex);
            ulong currentKeyLow = isIncoming ? encodedKeyLow : readOnly.ReadKeyLowAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = isIncoming ? identity : readOnly.ReadIdentityAt(sourceIndex);
            if (currentIdentity.Length <= 0 || currentIdentity.Length > targetProfile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(currentIdentity.Length);
            if (recordCursor + recordLength > targetProfile.ShelfExtentSize)
            {
                return false;
            }

            Scalar16VarIdentityLayout.WriteRecord(rewrittenBytes, recordCursor, currentKeyHigh, currentKeyLow, currentIdentity);
            Scalar16VarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotCursor, recordCursor);
            Scalar16VarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotCursor, Scalar16VarIdentityLayout.CreateKeyPrefix(currentKeyHigh, currentKeyLow));
            slotCursor += Scalar16VarIdentityLayout.SlotSize;
            recordCursor += recordLength;
            if (!isIncoming)
            {
                sourceIndex++;
            }
        }

        Scalar16VarIdentityLayout.WriteItemCount(rewrittenBytes, newCount);
        Scalar16VarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotCursor - Scalar16VarIdentityLayout.HeaderSize);
        Scalar16VarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, recordCursor);
        return true;
    }

    public static bool TryBuildFromSorted(ReadOnlySpan<ulong> keyHighs, ReadOnlySpan<ulong> keyLows, ReadOnlySpan<byte[]> identities, Scalar16VarIdentityProfile profile, out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        Scalar16VarIdentityLayout.Initialize(bytes, profile);
        if (keyHighs.Length != keyLows.Length || keyHighs.Length != identities.Length)
        {
            return false;
        }

        int slotLength = checked(keyHighs.Length * Scalar16VarIdentityLayout.SlotSize);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int recordCursor = Scalar16VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = new int[keyHighs.Length];
        for (int i = 0; i < keyHighs.Length; i++)
        {
            ReadOnlySpan<byte> identity = identities[i];
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            recordOffsets[i] = recordCursor;
            Scalar16VarIdentityLayout.WriteRecord(bytes, recordCursor, keyHighs[i], keyLows[i], identity);
            recordCursor += recordLength;
        }

        int slotCursor = Scalar16VarIdentityLayout.HeaderSize;
        for (int i = 0; i < keyHighs.Length; i++)
        {
            Scalar16VarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            Scalar16VarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, Scalar16VarIdentityLayout.CreateKeyPrefix(keyHighs[i], keyLows[i]));
            slotCursor += Scalar16VarIdentityLayout.SlotSize;
        }

        Scalar16VarIdentityLayout.WriteItemCount(bytes, keyHighs.Length);
        Scalar16VarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - Scalar16VarIdentityLayout.HeaderSize);
        Scalar16VarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }
}
