using LibraDex.Layouts;

namespace LibraDex.Views;

internal static class VarKeyVarIdentity
{
    public static byte[] CreateEmpty(VarKeyVarIdentityProfile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(bytes, profile);
        return bytes;
    }

    public static VarKeyVarIdentityInsertResult Insert(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHint(
            existingBytes,
            profile,
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _,
            out rewrittenBytes);
    }

    /// <summary>
    /// Inserts one raw key and raw variable-length identity while producing a bounded adjacent-key mutation hint.<br/>
    /// The hint compares only the incoming key with the predecessor and successor around the already-computed insert position.<br/>
    /// This keeps ordinary write-side analysis outside route lookup and avoids any full-shelf scan; the hint remains advisory until a later cold transform validates the shelf population exactly.<br/>
    /// </summary>
    /// <param name="existingBytes">The existing shelf bytes.</param>
    /// <param name="profile">The `VV` shelf profile used to decode and rewrite the shelf.</param>
    /// <param name="key">The incoming raw byte key.</param>
    /// <param name="identity">The incoming raw identity bytes.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate raw keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for the route-local hint.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare per neighbor for the hint.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <param name="rewrittenBytes">The rewritten shelf bytes when insertion succeeds.</param>
    /// <returns>The insert result.</returns>
    public static VarKeyVarIdentityInsertResult InsertWithMutationHint(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyVarIdentityMutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHintCore(
            existingBytes,
            mutableBytes: null,
            profile,
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth,
            maxHintBytes,
            out mutationHint,
            out rewrittenBytes);
    }

    /// <summary>
    /// Inserts one raw key and raw variable-length identity while allowing append-like mutations to update a disk-shaped shelf buffer in place.<br/>
    /// The provided byte array remains the authoritative runtime shelf image; decoded runtime slot offsets are used only as a sidecar to avoid repeated 3-byte slot decoding while adjusting persisted offsets.<br/>
    /// Non-append inserts may still rebuild into a replacement image because they materially reorder slots and records rather than extending the current record arena.<br/>
    /// </summary>
    /// <param name="existingBytes">The mutable, disk-shaped shelf byte buffer.</param>
    /// <param name="profile">The `VV` shelf profile used to decode and mutate the shelf.</param>
    /// <param name="key">The incoming raw byte key.</param>
    /// <param name="identity">The incoming raw identity bytes.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate raw keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for the route-local hint.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare per neighbor for the hint.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <param name="rewrittenBytes">The authoritative shelf bytes after the insert, either <paramref name="existingBytes"/> for in-place append or a replacement image for rebuild.</param>
    /// <returns>The insert result.</returns>
    public static VarKeyVarIdentityInsertResult InsertWithMutationHintInPlace(
        byte[] existingBytes,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyVarIdentityMutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHintCore(
            existingBytes,
            existingBytes,
            profile,
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth,
            maxHintBytes,
            out mutationHint,
            out rewrittenBytes);
    }

    private static VarKeyVarIdentityInsertResult InsertWithMutationHintCore(
        ReadOnlyMemory<byte> existingBytes,
        byte[]? mutableBytes,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyVarIdentityMutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        mutationHint = default;
        if (key.Length <= 0 || key.Length > profile.MaxKeyLength || identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
        {
            return VarKeyVarIdentityInsertResult.Invalid;
        }

        VarKeyVarIdentityReadOnly readOnly = new(existingBytes, profile);
        if (!readOnly.IsValid)
        {
            return VarKeyVarIdentityInsertResult.Invalid;
        }

        int insertIndex = readOnly.LowerBound(key, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, identity))
        {
            return VarKeyVarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                return VarKeyVarIdentityInsertResult.KeyConflict;
            }
        }

        mutationHint = CreateMutationHint(readOnly, insertIndex, key, hintStartDepth, maxHintBytes);
        if (TryInsertIntoReservedSlotRegion(existingBytes.Span, mutableBytes, profile, readOnly, insertIndex, key, identity, out rewrittenBytes))
        {
            return VarKeyVarIdentityInsertResult.Inserted;
        }

        return TryRebuildWithInsert(existingBytes.Span, profile, readOnly, insertIndex, key, identity, out rewrittenBytes)
            ? VarKeyVarIdentityInsertResult.Inserted
            : VarKeyVarIdentityInsertResult.Full;
    }

    private static VarKeyVarIdentityMutationHint CreateMutationHint(
        VarKeyVarIdentityReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        int hintStartDepth,
        int maxHintBytes)
    {
        if (maxHintBytes <= 0 || hintStartDepth < 0)
        {
            return default;
        }

        int compared = 0;
        int maxCommonBytes = 0;
        if (insertIndex > 0)
        {
            compared++;
            maxCommonBytes = Math.Max(maxCommonBytes, CountCommonPrefixBytes(readOnly.ReadKeyAt(insertIndex - 1), key, hintStartDepth, maxHintBytes));
        }

        if (insertIndex < readOnly.ItemCount)
        {
            compared++;
            maxCommonBytes = Math.Max(maxCommonBytes, CountCommonPrefixBytes(readOnly.ReadKeyAt(insertIndex), key, hintStartDepth, maxHintBytes));
        }

        return compared == 0
            ? default
            : new VarKeyVarIdentityMutationHint(
                Sampled: true,
                ComparedNeighborCount: compared,
                StartDepth: hintStartDepth,
                MaxCommonPrefixDepth: hintStartDepth + maxCommonBytes,
                MaxCommonPrefixBytes: maxCommonBytes);
    }

    private static int CountCommonPrefixBytes(
        ReadOnlySpan<byte> leftKey,
        ReadOnlySpan<byte> rightKey,
        int startDepth,
        int maxBytes)
    {
        int compared = 0;
        int leftRemaining = Math.Max(0, leftKey.Length - startDepth);
        int rightRemaining = Math.Max(0, rightKey.Length - startDepth);
        int limit = Math.Min(maxBytes, Math.Min(leftRemaining, rightRemaining));
        while (compared < limit && leftKey[startDepth + compared] == rightKey[startDepth + compared])
        {
            compared++;
        }

        return compared;
    }

    public static bool TryGrowAndInsert(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyVarIdentityProfile currentProfile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out VarKeyVarIdentityProfile newProfile,
        out byte[] rewrittenBytes,
        out VarKeyVarIdentityInsertResult insertResult)
    {
        newProfile = currentProfile.NextGrowthClass();
        rewrittenBytes = [];
        insertResult = VarKeyVarIdentityInsertResult.Full;
        if (newProfile.ShelfExtentSize == currentProfile.ShelfExtentSize)
        {
            return false;
        }

        VarKeyVarIdentityReadOnly readOnly = new(existingBytes, currentProfile);
        if (!readOnly.IsValid)
        {
            insertResult = VarKeyVarIdentityInsertResult.Invalid;
            return false;
        }

        int insertIndex = readOnly.LowerBound(key, identity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, identity))
        {
            insertResult = VarKeyVarIdentityInsertResult.AlreadyPresent;
            return true;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                insertResult = VarKeyVarIdentityInsertResult.KeyConflict;
                return true;
            }
        }

        if (!TryRebuildWithInsert(existingBytes.Span, newProfile, readOnly, insertIndex, key, identity, out rewrittenBytes))
        {
            insertResult = VarKeyVarIdentityInsertResult.Full;
            return false;
        }

        insertResult = VarKeyVarIdentityInsertResult.Inserted;
        return true;
    }

    private static bool TryInsertIntoReservedSlotRegion(
        ReadOnlySpan<byte> existingBytes,
        byte[]? mutableBytes,
        VarKeyVarIdentityProfile profile,
        VarKeyVarIdentityReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        int slotStreamLength = VarKeyVarIdentityLayout.ReadSlotStreamLength(existingBytes);
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(existingBytes);
        int recordArenaEnd = VarKeyVarIdentityLayout.ReadRecordArenaEnd(existingBytes);
        int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(key.Length, identity.Length);
        int slotBytes = VarKeyVarIdentityLayout.SlotSize;
        if (slotStreamLength + slotBytes > slotCapacityBytes)
        {
            return false;
        }

        int slotOffset = VarKeyVarIdentityLayout.HeaderSize + checked(insertIndex * slotBytes);
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

        VarKeyVarIdentityLayout.WriteRecord(rewrittenBytes, recordOffset, key, identity);
        VarKeyVarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotOffset, recordOffset);
        VarKeyVarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotOffset, VarKeyVarIdentityLayout.CreateKeyPrefix(key));
        VarKeyVarIdentityLayout.WriteItemCount(rewrittenBytes, readOnly.ItemCount + 1);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotStreamLength + slotBytes);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, newRecordArenaEnd);
        return true;
    }

    private static bool TryRebuildWithInsert(
        ReadOnlySpan<byte> existingBytes,
        VarKeyVarIdentityProfile targetProfile,
        VarKeyVarIdentityReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        out byte[] rewrittenBytes)
    {
        int newCount = checked(readOnly.ItemCount + 1);
        rewrittenBytes = new byte[targetProfile.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(rewrittenBytes, targetProfile);
        int slotLength = checked(newCount * VarKeyVarIdentityLayout.SlotSize);
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(rewrittenBytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = VarKeyVarIdentityLayout.HeaderSize;
        int recordCursor = VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int sourceIndex = 0, targetIndex = 0; targetIndex < newCount; targetIndex++)
        {
            bool isIncoming = targetIndex == insertIndex;
            ReadOnlySpan<byte> currentKey = isIncoming ? key : readOnly.ReadKeyAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = isIncoming ? identity : readOnly.ReadIdentityAt(sourceIndex);
            if (currentKey.Length <= 0 || currentKey.Length > targetProfile.MaxKeyLength || currentIdentity.Length <= 0 || currentIdentity.Length > targetProfile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(currentKey.Length, currentIdentity.Length);
            if (recordCursor + recordLength > targetProfile.ShelfExtentSize)
            {
                return false;
            }

            VarKeyVarIdentityLayout.WriteRecord(rewrittenBytes, recordCursor, currentKey, currentIdentity);
            VarKeyVarIdentityLayout.WriteSlotRecordOffset(rewrittenBytes, slotCursor, recordCursor);
            VarKeyVarIdentityLayout.WriteSlotKeyPrefix(rewrittenBytes, slotCursor, VarKeyVarIdentityLayout.CreateKeyPrefix(currentKey));
            slotCursor += VarKeyVarIdentityLayout.SlotSize;
            recordCursor += recordLength;
            if (!isIncoming)
            {
                sourceIndex++;
            }
        }

        VarKeyVarIdentityLayout.WriteItemCount(rewrittenBytes, newCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(rewrittenBytes, slotCursor - VarKeyVarIdentityLayout.HeaderSize);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(rewrittenBytes, recordCursor);
        return true;
    }

    public static bool TryBuildFromSorted(ReadOnlySpan<byte[]> keys, ReadOnlySpan<byte[]> identities, VarKeyVarIdentityProfile profile, out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(bytes, profile);
        if (keys.Length != identities.Length)
        {
            return false;
        }

        int slotLength = checked(keys.Length * VarKeyVarIdentityLayout.SlotSize);
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int recordCursor = VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            ReadOnlySpan<byte> key = keys[i];
            if (key.Length <= 0 || key.Length > profile.MaxKeyLength)
            {
                return false;
            }

            ReadOnlySpan<byte> identity = identities[i];
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(key.Length, identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            recordOffsets[i] = recordCursor;
            VarKeyVarIdentityLayout.WriteRecord(bytes, recordCursor, key, identity);
            recordCursor += recordLength;
        }

        int slotCursor = VarKeyVarIdentityLayout.HeaderSize;
        for (int i = 0; i < keys.Length; i++)
        {
            VarKeyVarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            VarKeyVarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, VarKeyVarIdentityLayout.CreateKeyPrefix(keys[i]));
            slotCursor += VarKeyVarIdentityLayout.SlotSize;
        }

        VarKeyVarIdentityLayout.WriteItemCount(bytes, keys.Length);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - VarKeyVarIdentityLayout.HeaderSize);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }
}
