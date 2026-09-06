using LibraDex.Layouts;

namespace LibraDex.Views;

internal static class VarKeyScalar8
{
    public static byte[] CreateEmpty(VarKeyScalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        VarKeyScalar8Layout.Initialize(bytes, profile);
        return bytes;
    }

    public static VarKeyScalar8InsertResult Insert(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyScalar8Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHint(
            existingBytes,
            profile,
            key,
            encodedIdentity,
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
    /// <param name="profile">The `VS8` shelf profile used to decode and rewrite the shelf.</param>
    /// <param name="key">The incoming raw byte key.</param>
    /// <param name="encodedIdentity">The encoded 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate raw keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for the route-local hint.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare per neighbor for the hint.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <param name="rewrittenBytes">The rewritten shelf bytes when insertion succeeds.</param>
    /// <returns>The insert result.</returns>
    public static VarKeyScalar8InsertResult InsertWithMutationHint(
        ReadOnlyMemory<byte> existingBytes,
        VarKeyScalar8Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyScalar8MutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHintCore(
            existingBytes,
            mutableBytes: null,
            profile,
            key,
            encodedIdentity,
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
    /// <param name="profile">The `VS8` shelf profile used to decode and mutate the shelf.</param>
    /// <param name="key">The incoming raw byte key.</param>
    /// <param name="encodedIdentity">The encoded 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate raw keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for the route-local hint.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare per neighbor for the hint.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <param name="rewrittenBytes">The authoritative shelf bytes after the insert, either <paramref name="existingBytes"/> for in-place append or a replacement image for rebuild.</param>
    /// <returns>The insert result.</returns>
    public static VarKeyScalar8InsertResult InsertWithMutationHintInPlace(
        byte[] existingBytes,
        VarKeyScalar8Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyScalar8MutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        return InsertWithMutationHintCore(
            existingBytes,
            existingBytes,
            profile,
            key,
            encodedIdentity,
            allowDuplicateKeys,
            hintStartDepth,
            maxHintBytes,
            out mutationHint,
            out rewrittenBytes);
    }

    private static VarKeyScalar8InsertResult InsertWithMutationHintCore(
        ReadOnlyMemory<byte> existingBytes,
        byte[]? mutableBytes,
        VarKeyScalar8Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyScalar8MutationHint mutationHint,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        mutationHint = default;
        if (key.Length <= 0 || key.Length > profile.MaxKeyLength)
        {
            return VarKeyScalar8InsertResult.Invalid;
        }

        VarKeyScalar8ReadOnly readOnly = new(existingBytes, profile);
        if (!readOnly.IsValid)
        {
            return VarKeyScalar8InsertResult.Invalid;
        }

        int insertIndex = readOnly.LowerBound(key, encodedIdentity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, encodedIdentity))
        {
            return VarKeyScalar8InsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                return VarKeyScalar8InsertResult.KeyConflict;
            }
        }

        mutationHint = CreateMutationHint(readOnly, insertIndex, key, hintStartDepth, maxHintBytes);
        if (TryInsertIntoReservedSlotRegion(existingBytes.Span, mutableBytes, profile, readOnly, insertIndex, key, encodedIdentity, out rewrittenBytes))
        {
            return VarKeyScalar8InsertResult.Inserted;
        }

        return TryRebuildWithInsert(existingBytes.Span, profile, readOnly, insertIndex, key, encodedIdentity, out rewrittenBytes)
            ? VarKeyScalar8InsertResult.Inserted
            : VarKeyScalar8InsertResult.Full;
    }

    private static VarKeyScalar8MutationHint CreateMutationHint(
        VarKeyScalar8ReadOnly readOnly,
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
            : new VarKeyScalar8MutationHint(
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
        VarKeyScalar8Profile currentProfile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out VarKeyScalar8Profile newProfile,
        out byte[] rewrittenBytes,
        out VarKeyScalar8InsertResult insertResult)
    {
        newProfile = currentProfile.NextGrowthClass();
        rewrittenBytes = [];
        insertResult = VarKeyScalar8InsertResult.Full;
        if (newProfile.ShelfExtentSize == currentProfile.ShelfExtentSize)
        {
            return false;
        }

        VarKeyScalar8ReadOnly readOnly = new(existingBytes, currentProfile);
        if (!readOnly.IsValid)
        {
            insertResult = VarKeyScalar8InsertResult.Invalid;
            return false;
        }

        int insertIndex = readOnly.LowerBound(key, encodedIdentity);
        if (insertIndex < readOnly.ItemCount && readOnly.Contains(key, encodedIdentity))
        {
            insertResult = VarKeyScalar8InsertResult.AlreadyPresent;
            return true;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < readOnly.ItemCount && readOnly.ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                insertResult = VarKeyScalar8InsertResult.KeyConflict;
                return true;
            }
        }

        if (!TryRebuildWithInsert(existingBytes.Span, newProfile, readOnly, insertIndex, key, encodedIdentity, out rewrittenBytes))
        {
            insertResult = VarKeyScalar8InsertResult.Full;
            return false;
        }

        insertResult = VarKeyScalar8InsertResult.Inserted;
        return true;
    }

    private static bool TryInsertIntoReservedSlotRegion(
        ReadOnlySpan<byte> existingBytes,
        byte[]? mutableBytes,
        VarKeyScalar8Profile profile,
        VarKeyScalar8ReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        out byte[] rewrittenBytes)
    {
        rewrittenBytes = [];
        int slotStreamLength = VarKeyScalar8Layout.ReadSlotStreamLength(existingBytes);
        int slotCapacityBytes = VarKeyScalar8Layout.ReadSlotCapacityBytes(existingBytes);
        int recordArenaEnd = VarKeyScalar8Layout.ReadRecordArenaEnd(existingBytes);
        int recordLength = VarKeyScalar8Layout.GetNewRecordLength(key.Length);
        int slotBytes = VarKeyScalar8Layout.SlotSize;
        if (slotStreamLength + slotBytes > slotCapacityBytes)
        {
            return false;
        }

        int slotOffset = VarKeyScalar8Layout.HeaderSize + checked(insertIndex * slotBytes);
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

        VarKeyScalar8Layout.WriteRecord(rewrittenBytes, recordOffset, key, encodedIdentity);
        VarKeyScalar8Layout.WriteSlotRecordOffset(rewrittenBytes, slotOffset, recordOffset);
        VarKeyScalar8Layout.WriteSlotKeyPrefix(rewrittenBytes, slotOffset, VarKeyScalar8Layout.CreateKeyPrefix(key));
        VarKeyScalar8Layout.WriteItemCount(rewrittenBytes, readOnly.ItemCount + 1);
        VarKeyScalar8Layout.WriteSlotStreamLength(rewrittenBytes, slotStreamLength + slotBytes);
        VarKeyScalar8Layout.WriteRecordArenaEnd(rewrittenBytes, newRecordArenaEnd);
        return true;
    }

    private static bool TryRebuildWithInsert(
        ReadOnlySpan<byte> existingBytes,
        VarKeyScalar8Profile targetProfile,
        VarKeyScalar8ReadOnly readOnly,
        int insertIndex,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        out byte[] rewrittenBytes)
    {
        int newCount = checked(readOnly.ItemCount + 1);
        rewrittenBytes = new byte[targetProfile.ShelfExtentSize];
        VarKeyScalar8Layout.Initialize(rewrittenBytes, targetProfile);
        int slotLength = checked(newCount * VarKeyScalar8Layout.SlotSize);
        int slotCapacityBytes = VarKeyScalar8Layout.ReadSlotCapacityBytes(rewrittenBytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = VarKeyScalar8Layout.HeaderSize;
        int recordCursor = VarKeyScalar8Layout.HeaderSize + slotCapacityBytes;
        for (int sourceIndex = 0, targetIndex = 0; targetIndex < newCount; targetIndex++)
        {
            bool isIncoming = targetIndex == insertIndex;
            ReadOnlySpan<byte> currentKey = isIncoming ? key : readOnly.ReadKeyAt(sourceIndex);
            ulong currentIdentity = isIncoming ? encodedIdentity : readOnly.ReadIdentityAt(sourceIndex);
            if (currentKey.Length <= 0 || currentKey.Length > targetProfile.MaxKeyLength)
            {
                return false;
            }

            int recordLength = VarKeyScalar8Layout.GetNewRecordLength(currentKey.Length);
            if (recordCursor + recordLength > targetProfile.ShelfExtentSize)
            {
                return false;
            }

            VarKeyScalar8Layout.WriteRecord(rewrittenBytes, recordCursor, currentKey, currentIdentity);
            VarKeyScalar8Layout.WriteSlotRecordOffset(rewrittenBytes, slotCursor, recordCursor);
            VarKeyScalar8Layout.WriteSlotKeyPrefix(rewrittenBytes, slotCursor, VarKeyScalar8Layout.CreateKeyPrefix(currentKey));
            slotCursor += VarKeyScalar8Layout.SlotSize;
            recordCursor += recordLength;
            if (!isIncoming)
            {
                sourceIndex++;
            }
        }

        VarKeyScalar8Layout.WriteItemCount(rewrittenBytes, newCount);
        VarKeyScalar8Layout.WriteSlotStreamLength(rewrittenBytes, slotCursor - VarKeyScalar8Layout.HeaderSize);
        VarKeyScalar8Layout.WriteRecordArenaEnd(rewrittenBytes, recordCursor);
        return true;
    }

    public static bool TryBuildFromSorted(ReadOnlySpan<byte[]> keys, ReadOnlySpan<ulong> identities, VarKeyScalar8Profile profile, out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        VarKeyScalar8Layout.Initialize(bytes, profile);
        if (keys.Length != identities.Length)
        {
            return false;
        }

        int slotLength = checked(keys.Length * VarKeyScalar8Layout.SlotSize);
        int slotCapacityBytes = VarKeyScalar8Layout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int recordCursor = VarKeyScalar8Layout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            ReadOnlySpan<byte> key = keys[i];
            if (key.Length <= 0 || key.Length > profile.MaxKeyLength)
            {
                return false;
            }

            int recordLength = VarKeyScalar8Layout.GetNewRecordLength(key.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            recordOffsets[i] = recordCursor;
            VarKeyScalar8Layout.WriteRecord(bytes, recordCursor, key, identities[i]);
            recordCursor += recordLength;
        }

        int slotCursor = VarKeyScalar8Layout.HeaderSize;
        for (int i = 0; i < keys.Length; i++)
        {
            VarKeyScalar8Layout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            VarKeyScalar8Layout.WriteSlotKeyPrefix(bytes, slotCursor, VarKeyScalar8Layout.CreateKeyPrefix(keys[i]));
            slotCursor += VarKeyScalar8Layout.SlotSize;
        }

        VarKeyScalar8Layout.WriteItemCount(bytes, keys.Length);
        VarKeyScalar8Layout.WriteSlotStreamLength(bytes, slotCursor - VarKeyScalar8Layout.HeaderSize);
        VarKeyScalar8Layout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    /// <summary>
    /// Builds one shelf directly from a stable ordinal tuple source without materializing per-key managed arrays.<br/>
    /// The source range must already be sorted by encoded key then identity and remain stable for the duration of this call.<br/>
    /// </summary>
    /// <param name="source">Stable encoded tuple source.<br/></param>
    /// <param name="start">Inclusive source ordinal.<br/></param>
    /// <param name="end">Exclusive source ordinal.<br/></param>
    /// <param name="profile">Target shelf profile.<br/></param>
    /// <param name="bytes">Completed shelf bytes when the range fits.<br/></param>
    /// <returns><see langword="true"/> when the complete range fits the supplied profile; otherwise <see langword="false"/>.<br/></returns>
    internal static bool TryBuildFromSorted(
        IVarKeyScalar8SortedTupleSource source,
        int start,
        int end,
        VarKeyScalar8Profile profile,
        out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (start < 0 || end < start || end > source.Count)
            throw new ArgumentOutOfRangeException(nameof(start));

        int count = end - start;
        bytes = new byte[profile.ShelfExtentSize];
        VarKeyScalar8Layout.Initialize(bytes, profile);
        int slotLength = checked(count * VarKeyScalar8Layout.SlotSize);
        int slotCapacityBytes = VarKeyScalar8Layout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
            return false;

        int recordCursor = VarKeyScalar8Layout.HeaderSize + slotCapacityBytes;
        int[] recordOffsets = GC.AllocateUninitializedArray<int>(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> key = source.GetKey(start + i);
            if (key.Length <= 0 || key.Length > profile.MaxKeyLength)
                return false;
            int recordLength = VarKeyScalar8Layout.GetNewRecordLength(key.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
                return false;
            recordOffsets[i] = recordCursor;
            VarKeyScalar8Layout.WriteRecord(bytes, recordCursor, key, source.GetIdentity(start + i));
            recordCursor += recordLength;
        }

        int slotCursor = VarKeyScalar8Layout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> key = source.GetKey(start + i);
            VarKeyScalar8Layout.WriteSlotRecordOffset(bytes, slotCursor, recordOffsets[i]);
            VarKeyScalar8Layout.WriteSlotKeyPrefix(bytes, slotCursor, VarKeyScalar8Layout.CreateKeyPrefix(key));
            slotCursor += VarKeyScalar8Layout.SlotSize;
        }

        VarKeyScalar8Layout.WriteItemCount(bytes, count);
        VarKeyScalar8Layout.WriteSlotStreamLength(bytes, slotCursor - VarKeyScalar8Layout.HeaderSize);
        VarKeyScalar8Layout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }
}
