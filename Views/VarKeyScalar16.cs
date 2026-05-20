using LibraDex.Layouts;

namespace LibraDex.Views;

internal static class VarKeyScalar16
{
    public static byte[] CreateEmpty(VarKeyScalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        VarKeyScalar16Layout.Initialize(bytes, profile);
        return bytes;
    }

    public static VarKeyScalar16InsertResult Insert(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyScalar16Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHint(
            existingBytes,
            profile,
            key,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _,
            out rewrittenBytes);
    }

    /// <summary>
    /// Inserts one raw key and encoded identity while producing a bounded adjacent-key mutation hint.<br/>
    /// The hint compares only the incoming key with the predecessor and successor around the already-computed insert position.<br/>
    /// This keeps ordinary write-side analysis outside route lookup and avoids any full-shelf scan; the hint remains advisory until a later cold transform validates the shelf population exactly.<br/>
    /// </summary>
    /// <param name="existingBytes">The existing shelf bytes.</param>
    /// <param name="profile">The `VS16` shelf profile used to decode and rewrite the shelf.</param>
    /// <param name="key">The incoming raw byte key.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the encoded 16-byte identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the encoded 16-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate raw keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for the route-local hint.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare per neighbor for the hint.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <param name="rewrittenBytes">The rewritten shelf bytes when insertion succeeds.</param>
    /// <returns>The insert result.</returns>
    public static VarKeyScalar16InsertResult InsertWithMutationHint(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyScalar16Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyScalar16MutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHintCore(
            existingBytes,
            mutableBytes: null,
            profile,
            key,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys,
            hintStartDepth,
            maxHintBytes,
            out mutationHint,
            out rewrittenBytes);
    }

    /// <summary>
    /// Inserts one raw key and encoded identity while allowing append-like mutations to update a disk-shaped shelf buffer in place.<br/>
    /// The provided byte array remains the authoritative runtime shelf image; decoded runtime slot offsets are used only as a sidecar to avoid repeated 3-byte slot decoding while adjusting persisted offsets.<br/>
    /// Non-append inserts may still rebuild into a replacement image because they materially reorder slots and records rather than extending the current record arena.<br/>
    /// </summary>
    /// <param name="existingBytes">The mutable, disk-shaped shelf byte buffer.</param>
    /// <param name="profile">The `VS16` shelf profile used to decode and mutate the shelf.</param>
    /// <param name="key">The incoming raw byte key.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the encoded 16-byte identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the encoded 16-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate raw keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for the route-local hint.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare per neighbor for the hint.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <param name="rewrittenBytes">The authoritative shelf bytes after the insert, either <paramref name="existingBytes"/> for in-place append or a replacement image for rebuild.</param>
    /// <returns>The insert result.</returns>
    public static VarKeyScalar16InsertResult InsertWithMutationHintInPlace(
        byte[] existingBytes,
        VarKeyScalar16Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyScalar16MutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHintCore(
            existingBytes,
            existingBytes,
            profile,
            key,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys,
            hintStartDepth,
            maxHintBytes,
            out mutationHint,
            out rewrittenBytes);
    }

    private static VarKeyScalar16InsertResult InsertWithMutationHintCore(
        ReadOnlyMemory<byte> existingBytes,
        byte[]? mutableBytes,
        VarKeyScalar16Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyScalar16MutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        mutationHint = default;
        if (key.Length <= 0 || key.Length > profile.MaxKeyLength)
        {
            return VarKeyScalar16InsertResult.Invalid;
        }

        VarKeyScalar16ReadOnly readOnly = new(existingBytes, profile);
        if (!readOnly.IsValid)
        {
            return VarKeyScalar16InsertResult.Invalid;
        }

        int insertIndex = readOnly.LowerBound(key, encodedIdentityHigh, encodedIdentityLow);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, encodedIdentityHigh, encodedIdentityLow))
        {
            return VarKeyScalar16InsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                return VarKeyScalar16InsertResult.KeyConflict;
            }
        }

        mutationHint = CreateMutationHint(readOnly, insertIndex, key, hintStartDepth, maxHintBytes);
        if (TryInsertIntoReservedSlotRegion(existingBytes.Span, mutableBytes, profile, readOnly, insertIndex, key, encodedIdentityHigh, encodedIdentityLow, out rewrittenBytes))
        {
            return VarKeyScalar16InsertResult.Inserted;
        }

        return TryRebuildWithInsert(existingBytes.Span, profile, readOnly, insertIndex, key, encodedIdentityHigh, encodedIdentityLow, out rewrittenBytes)
            ? VarKeyScalar16InsertResult.Inserted
            : VarKeyScalar16InsertResult.Full;
    }

    private static VarKeyScalar16MutationHint CreateMutationHint(
        VarKeyScalar16ReadOnly readOnly,
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
            : new VarKeyScalar16MutationHint(
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
        VarKeyScalar16Profile currentProfile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out VarKeyScalar16Profile newProfile,
        out byte[] rewrittenBytes,
        out VarKeyScalar16InsertResult insertResult)
    {
        newProfile = currentProfile.NextGrowthClass();
        rewrittenBytes = [];
        insertResult = VarKeyScalar16InsertResult.Full;
        if (newProfile.ShelfExtentSize == currentProfile.ShelfExtentSize)
        {
            return false;
        }

        VarKeyScalar16ReadOnly readOnly = new(existingBytes, currentProfile);
        if (!readOnly.IsValid)
        {
            insertResult = VarKeyScalar16InsertResult.Invalid;
            return false;
        }

        int insertIndex = readOnly.LowerBound(key, encodedIdentityHigh, encodedIdentityLow);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, encodedIdentityHigh, encodedIdentityLow))
        {
            insertResult = VarKeyScalar16InsertResult.AlreadyPresent;
            return true;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                insertResult = VarKeyScalar16InsertResult.KeyConflict;
                return true;
            }
        }

        if (!TryRebuildWithInsert(existingBytes.Span, newProfile, readOnly, insertIndex, key, encodedIdentityHigh, encodedIdentityLow, out rewrittenBytes))
        {
            insertResult = VarKeyScalar16InsertResult.Full;
            return false;
        }

        insertResult = VarKeyScalar16InsertResult.Inserted;
        return true;
    }

    private static bool TryInsertIntoReservedSlotRegion(
        ReadOnlySpan<byte> existingBytes,
        byte[]? mutableBytes,
        VarKeyScalar16Profile profile,
        VarKeyScalar16ReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        int slotStreamLength = VarKeyScalar16Layout.ReadSlotStreamLength(existingBytes);
        int slotCapacityBytes = VarKeyScalar16Layout.ReadSlotCapacityBytes(existingBytes);
        int recordArenaEnd = VarKeyScalar16Layout.ReadRecordArenaEnd(existingBytes);
        int recordLength = VarKeyScalar16Layout.GetNewRecordLength(key.Length);
        int slotBytes = VarKeyScalar16Layout.SlotSize;
        if (slotStreamLength + slotBytes > slotCapacityBytes)
        {
            return false;
        }

        int slotOffset = VarKeyScalar16Layout.HeaderSize + checked(insertIndex * slotBytes);
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

        VarKeyScalar16Layout.WriteRecord(rewrittenBytes, recordOffset, key, encodedIdentityHigh, encodedIdentityLow);
        VarKeyScalar16Layout.WriteSlotRecordOffset(rewrittenBytes, slotOffset, recordOffset);
        VarKeyScalar16Layout.WriteSlotKeyPrefix(rewrittenBytes, slotOffset, VarKeyScalar16Layout.CreateKeyPrefix(key));
        VarKeyScalar16Layout.WriteItemCount(rewrittenBytes, readOnly.ItemCount + 1);
        VarKeyScalar16Layout.WriteSlotStreamLength(rewrittenBytes, slotStreamLength + slotBytes);
        VarKeyScalar16Layout.WriteRecordArenaEnd(rewrittenBytes, newRecordArenaEnd);
        return true;
    }

    private static bool TryRebuildWithInsert(
        ReadOnlySpan<byte> existingBytes,
        VarKeyScalar16Profile targetProfile,
        VarKeyScalar16ReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        out byte[] rewrittenBytes)
    {
        int newCount = checked(readOnly.ItemCount + 1);
        rewrittenBytes = new byte[targetProfile.ShelfExtentSize];
        VarKeyScalar16Layout.Initialize(rewrittenBytes, targetProfile);
        int slotLength = checked(newCount * VarKeyScalar16Layout.SlotSize);
        int slotCapacityBytes = VarKeyScalar16Layout.ReadSlotCapacityBytes(rewrittenBytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = VarKeyScalar16Layout.HeaderSize;
        int recordCursor = VarKeyScalar16Layout.HeaderSize + slotCapacityBytes;
        for (int sourceIndex = 0, targetIndex = 0; targetIndex < newCount; targetIndex++)
        {
            bool isIncoming = targetIndex == insertIndex;
            ReadOnlySpan<byte> currentKey = isIncoming ? key : readOnly.ReadKeyAt(sourceIndex);
            ulong currentIdentityHigh;
            ulong currentIdentityLow;
            if (isIncoming)
            {
                currentIdentityHigh = encodedIdentityHigh;
                currentIdentityLow = encodedIdentityLow;
            }
            else
            {
                readOnly.ReadIdentityAt(sourceIndex, out currentIdentityHigh, out currentIdentityLow);
            }

            if (currentKey.Length <= 0 || currentKey.Length > targetProfile.MaxKeyLength)
            {
                return false;
            }

            int recordLength = VarKeyScalar16Layout.GetNewRecordLength(currentKey.Length);
            if (recordCursor + recordLength > targetProfile.ShelfExtentSize)
            {
                return false;
            }

            VarKeyScalar16Layout.WriteRecord(rewrittenBytes, recordCursor, currentKey, currentIdentityHigh, currentIdentityLow);
            VarKeyScalar16Layout.WriteSlotRecordOffset(rewrittenBytes, slotCursor, recordCursor);
            VarKeyScalar16Layout.WriteSlotKeyPrefix(rewrittenBytes, slotCursor, VarKeyScalar16Layout.CreateKeyPrefix(currentKey));
            slotCursor += VarKeyScalar16Layout.SlotSize;
            recordCursor += recordLength;
            if (!isIncoming)
            {
                sourceIndex++;
            }
        }

        VarKeyScalar16Layout.WriteItemCount(rewrittenBytes, newCount);
        VarKeyScalar16Layout.WriteSlotStreamLength(rewrittenBytes, slotCursor - VarKeyScalar16Layout.HeaderSize);
        VarKeyScalar16Layout.WriteRecordArenaEnd(rewrittenBytes, recordCursor);
        return true;
    }

    public static bool TryBuildFromSorted(ReadOnlySpan<byte[]> keys, ReadOnlySpan<ulong> identityHighs, ReadOnlySpan<ulong> identityLows, VarKeyScalar16Profile profile, out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        VarKeyScalar16Layout.Initialize(bytes, profile);
        if (keys.Length != identityHighs.Length || keys.Length != identityLows.Length)
        {
            return false;
        }

        int slotLength = checked(keys.Length * VarKeyScalar16Layout.SlotSize);
        int slotCapacityBytes = VarKeyScalar16Layout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int recordCursor = VarKeyScalar16Layout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            ReadOnlySpan<byte> key = keys[i];
            if (key.Length <= 0 || key.Length > profile.MaxKeyLength)
            {
                return false;
            }

            int recordLength = VarKeyScalar16Layout.GetNewRecordLength(key.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            recordOffsets[i] = recordCursor;
            VarKeyScalar16Layout.WriteRecord(bytes, recordCursor, key, identityHighs[i], identityLows[i]);
            recordCursor += recordLength;
        }

        int slotCursor = VarKeyScalar16Layout.HeaderSize;
        for (int i = 0; i < keys.Length; i++)
        {
            VarKeyScalar16Layout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            VarKeyScalar16Layout.WriteSlotKeyPrefix(bytes, slotCursor, VarKeyScalar16Layout.CreateKeyPrefix(keys[i]));
            slotCursor += VarKeyScalar16Layout.SlotSize;
        }

        VarKeyScalar16Layout.WriteItemCount(bytes, keys.Length);
        VarKeyScalar16Layout.WriteSlotStreamLength(bytes, slotCursor - VarKeyScalar16Layout.HeaderSize);
        VarKeyScalar16Layout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }
}
