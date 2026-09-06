using System.Buffers;
using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Holds one mutable, disk-shaped `SV8` shelf image plus decoded slot sidecars for session-local write hot paths.<br/>
/// The byte array remains authoritative for persistence and range reads; sidecars avoid decoding 3-byte offsets and key prefixes for every insert into the same shelf.<br/>
/// This type is runtime-only and does not change the `Scalar8VarIdentityLayout` persisted format.<br/>
/// </summary>
internal sealed class Scalar8VarIdentityMutableShelfView
{
    private readonly int[] recordOffsets;
    private readonly uint[] keyPrefixes;
    private readonly ulong[] keys;
    private readonly int[] sortedIndexes;
    private readonly bool[] deletedSlots;
    private readonly bool pooledBytes;
    private readonly bool pooledSidecars;
    private bool released;
    private int itemCount;
    private int slotStreamLength;
    private int recordArenaEnd;
    private int deletedItemCount;
    private int deletedPayloadBytes;
    private int dirtySlotStart;
    private int dirtySlotEnd;
    private int dirtyRecordStart;
    private int dirtyRecordEnd;

    private Scalar8VarIdentityMutableShelfView(
        byte[] bytes,
        Scalar8VarIdentityProfile profile,
        int[] recordOffsets,
        uint[] keyPrefixes,
        ulong[] keys,
        int[] sortedIndexes,
        bool[] deletedSlots,
        bool pooledBytes,
        bool pooledSidecars,
        int itemCount,
        int slotStreamLength,
        int slotCapacityBytes,
        int recordArenaEnd)
    {
        Bytes = bytes;
        Profile = profile;
        this.recordOffsets = recordOffsets;
        this.keyPrefixes = keyPrefixes;
        this.keys = keys;
        this.sortedIndexes = sortedIndexes;
        this.deletedSlots = deletedSlots;
        this.pooledBytes = pooledBytes;
        this.pooledSidecars = pooledSidecars;
        this.itemCount = itemCount;
        this.slotStreamLength = slotStreamLength;
        SlotCapacityBytes = slotCapacityBytes;
        this.recordArenaEnd = recordArenaEnd;
    }

    /// <summary>
    /// Gets the authoritative runtime shelf bytes that will be copied back to DataKernel staging when the session publishes the shelf.<br/>
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// Gets the profile used to validate extent size and maximum identity length.<br/>
    /// </summary>
    public Scalar8VarIdentityProfile Profile { get; }

    /// <summary>
    /// Gets the number of sorted tuple slots currently stored in the shelf.<br/>
    /// </summary>
    public int ItemCount => itemCount;

    public int PhysicalItemCount => itemCount;

    public int LiveItemCount => itemCount - deletedItemCount;

    public int DeletedItemCount => deletedItemCount;

    public int PayloadBytesUsed => recordArenaEnd - (Scalar8VarIdentityLayout.HeaderSize + SlotCapacityBytes);

    public int PayloadBytesLive => PayloadBytesUsed - deletedPayloadBytes;

    public int PayloadBytesDeleted => deletedPayloadBytes;

    /// <summary>
    /// Gets the reserved persisted slot-array byte capacity for this shelf extent.<br/>
    /// </summary>
    public int SlotCapacityBytes { get; }

    /// <summary>
    /// Gets whether this mutable shelf has changed since it was loaded into the session-local sidecar cache.<br/>
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Gets whether this shelf must be published as a full extent because the caller replaced or directly mutated bytes outside tracked insert ranges.<br/>
    /// </summary>
    internal bool RequiresFullRewrite { get; private set; }

    /// <summary>
    /// Gets whether header counters changed since the shelf was loaded into the mutable batch cache.<br/>
    /// </summary>
    internal bool HeaderDirty { get; private set; }

    /// <summary>
    /// Gets the first dirty slot-stream byte relative to the start of the shelf image, or zero when no slot bytes are dirty.<br/>
    /// </summary>
    internal int DirtySlotStart => dirtySlotStart;

    /// <summary>
    /// Gets the exclusive dirty slot-stream end relative to the start of the shelf image, or zero when no slot bytes are dirty.<br/>
    /// </summary>
    internal int DirtySlotEnd => dirtySlotEnd;

    /// <summary>
    /// Gets the first dirty record-arena byte relative to the start of the shelf image, or zero when no record bytes are dirty.<br/>
    /// </summary>
    internal int DirtyRecordStart => dirtyRecordStart;

    /// <summary>
    /// Gets the exclusive dirty record-arena end relative to the start of the shelf image, or zero when no record bytes are dirty.<br/>
    /// </summary>
    internal int DirtyRecordEnd => dirtyRecordEnd;

    /// <summary>
    /// Gets whether the persisted 7-byte slot stream in <see cref="Bytes"/> must be rebuilt from the decoded sidecars before disk publication or static shelf helpers read it.<br/>
    /// </summary>
    public bool SlotBytesDirty { get; private set; }

    /// <summary>
    /// Decodes an existing `SV8` shelf into mutable sidecars while keeping the supplied byte array as the authoritative image.<br/>
    /// Sidecar arrays are sized to the reserved slot capacity so ordinary inserts shift existing entries without reallocating slot metadata.<br/>
    /// </summary>
    /// <param name="bytes">The disk-shaped shelf byte image.</param>
    /// <param name="profile">The expected `SV8` shelf profile.</param>
    /// <param name="shelf">The decoded mutable shelf when validation succeeds.</param>
    /// <returns>`true` when the byte image is valid for <paramref name="profile"/>.</returns>
    public static bool TryCreate(byte[] bytes, Scalar8VarIdentityProfile profile, out Scalar8VarIdentityMutableShelfView shelf)
        => TryCreateCore(bytes, profile, pooledBytes: false, pooledSidecars: false, out shelf);

    /// <summary>
    /// Decodes an existing `SV8` shelf into a mutable view that returns its byte and sidecar buffers to shared pools on release.<br/>
    /// This is intended for durability-batch hot paths that create many short-lived mutable shelf views across small app batches.<br/>
    /// </summary>
    /// <param name="bytes">The pooled disk-shaped shelf byte image.</param>
    /// <param name="profile">The expected `SV8` shelf profile.</param>
    /// <param name="pooledBytes">Whether <paramref name="bytes"/> should be returned to the shared byte pool when released.</param>
    /// <param name="shelf">The decoded mutable shelf when validation succeeds.</param>
    /// <returns>`true` when the byte image is valid for <paramref name="profile"/>.</returns>
    public static bool TryCreatePooled(byte[] bytes, Scalar8VarIdentityProfile profile, bool pooledBytes, out Scalar8VarIdentityMutableShelfView shelf)
        => TryCreateCore(bytes, profile, pooledBytes, pooledSidecars: true, out shelf);

    private static bool TryCreateCore(
        byte[] bytes,
        Scalar8VarIdentityProfile profile,
        bool pooledBytes,
        bool pooledSidecars,
        out Scalar8VarIdentityMutableShelfView shelf)
    {
        shelf = null!;
        if (bytes.Length < profile.ShelfExtentSize ||
            Scalar8VarIdentityLayout.ReadMagic(bytes) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(bytes) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(bytes) != Scalar8VarIdentityLayout.HeaderSize ||
            Scalar8VarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        int count = Scalar8VarIdentityLayout.ReadItemCount(bytes);
        int slotStreamLength = Scalar8VarIdentityLayout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = Scalar8VarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * Scalar8VarIdentityLayout.SlotSize);
        int expectedSlotCapacityBytes = Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int reclaimablePayloadBytes = Scalar8VarIdentityLayout.ReadReclaimablePayloadBytes(bytes);
        if (count < 0 ||
            slotStreamLength != expectedSlotStreamLength ||
            slotStreamLength > slotCapacityBytes ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < recordArenaStart ||
            recordArenaEnd > profile.ShelfExtentSize ||
            reclaimablePayloadBytes > recordArenaEnd - recordArenaStart)
        {
            return false;
        }

        int slotCapacity = slotCapacityBytes / Scalar8VarIdentityLayout.SlotSize;
        int[] offsets = pooledSidecars ? ArrayPool<int>.Shared.Rent(slotCapacity) : new int[slotCapacity];
        uint[] prefixes = pooledSidecars ? ArrayPool<uint>.Shared.Rent(slotCapacity) : new uint[slotCapacity];
        ulong[] keys = pooledSidecars ? ArrayPool<ulong>.Shared.Rent(slotCapacity) : new ulong[slotCapacity];
        int[] indexes = pooledSidecars ? ArrayPool<int>.Shared.Rent(slotCapacity) : new int[slotCapacity];
        bool[] deleted = pooledSidecars ? ArrayPool<bool>.Shared.Rent(slotCapacity) : new bool[slotCapacity];
        if (pooledSidecars)
        {
            Array.Clear(deleted, 0, slotCapacity);
        }

        int cursor = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = Scalar8VarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                ReturnSidecarsIfPooled(pooledSidecars, offsets, prefixes, keys, indexes, deleted);
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                ReturnSidecarsIfPooled(pooledSidecars, offsets, prefixes, keys, indexes, deleted);
                return false;
            }

            offsets[i] = offset;
            prefixes[i] = Scalar8VarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            keys[i] = Scalar8VarIdentityLayout.ReadKey(bytes, offset);
            indexes[i] = i;
            cursor += Scalar8VarIdentityLayout.SlotSize;
        }

        shelf = new Scalar8VarIdentityMutableShelfView(bytes, profile, offsets, prefixes, keys, indexes, deleted, pooledBytes, pooledSidecars, count, slotStreamLength, slotCapacityBytes, recordArenaEnd);
        shelf.deletedPayloadBytes = reclaimablePayloadBytes;
        return true;
    }

    /// <summary>
    /// Inserts one scalar key and raw variable identity into the owned shelf image when reserved slot and record capacity are still available.<br/>
    /// Full results are non-mutating and tell the caller to use the existing grow, split, or duplicate-run overflow path with the current authoritative bytes.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="identity">The raw variable identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <returns>The insert result.</returns>
    public Scalar8VarIdentityInsertResult Insert(ulong encodedKey, ReadOnlySpan<byte> identity, bool allowDuplicateKeys)
    {
        if (deletedItemCount != 0)
        {
            _ = NormalizeDeletedSlotsForPublication();
        }

        if (identity.Length <= 0 || identity.Length > Profile.MaxIdentityLength)
        {
            return Scalar8VarIdentityInsertResult.Invalid;
        }

        uint prefix = Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey);
        int insertIndex = itemCount;
        bool sortedAppend = true;
        if (itemCount > 0)
        {
            int lastComparison = CompareSlotTuple(itemCount - 1, prefix, encodedKey, identity);
            if (lastComparison == 0)
            {
                return Scalar8VarIdentityInsertResult.AlreadyPresent;
            }

            sortedAppend = lastComparison < 0;
            if (!sortedAppend)
            {
                insertIndex = LowerBoundTuple(encodedKey, prefix, identity);
                if (insertIndex < itemCount && CompareSlotTuple(insertIndex, prefix, encodedKey, identity) == 0)
                {
                    return Scalar8VarIdentityInsertResult.AlreadyPresent;
                }
            }
        }

        if (!allowDuplicateKeys && ContainsKey(encodedKey, prefix, out _))
        {
            return Scalar8VarIdentityInsertResult.KeyConflict;
        }

        if (slotStreamLength + Scalar8VarIdentityLayout.SlotSize > SlotCapacityBytes)
        {
            return Scalar8VarIdentityInsertResult.Full;
        }

        int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > Profile.ShelfExtentSize && deletedPayloadBytes >= recordLength)
        {
            byte[]? compacted = BuildCompactedPayloadIfWorthwhile(recordLength, 0);
            if (compacted is not null)
            {
                compacted.AsSpan(0, Profile.ShelfExtentSize).CopyTo(Bytes.AsSpan(0, Profile.ShelfExtentSize));
                RebuildSidecarsAfterPayloadCompaction();
                recordOffset = recordArenaEnd;
                newRecordArenaEnd = recordOffset + recordLength;
            }
        }

        if (newRecordArenaEnd > Profile.ShelfExtentSize)
        {
            return Scalar8VarIdentityInsertResult.Full;
        }

        Scalar8VarIdentityLayout.WriteRecord(Bytes, recordOffset, encodedKey, identity);
        if (!sortedAppend)
        {
            Array.Copy(sortedIndexes, insertIndex, sortedIndexes, insertIndex + 1, itemCount - insertIndex);
        }

        int physicalIndex = itemCount;
        recordOffsets[physicalIndex] = recordOffset;
        keyPrefixes[physicalIndex] = prefix;
        keys[physicalIndex] = encodedKey;
        deletedSlots[physicalIndex] = false;
        sortedIndexes[insertIndex] = physicalIndex;
        itemCount++;
        slotStreamLength += Scalar8VarIdentityLayout.SlotSize;
        recordArenaEnd = newRecordArenaEnd;
        Scalar8VarIdentityLayout.WriteItemCount(Bytes, itemCount);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(Bytes, recordArenaEnd);
        if (sortedAppend)
        {
            int slotOffset = Scalar8VarIdentityLayout.HeaderSize + checked(insertIndex * Scalar8VarIdentityLayout.SlotSize);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffset);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, prefix);
            MarkDirtySlotRange(slotOffset, slotOffset + Scalar8VarIdentityLayout.SlotSize);
        }
        else
        {
            SlotBytesDirty = true;
            MarkDirtySlotRange(Scalar8VarIdentityLayout.HeaderSize, Scalar8VarIdentityLayout.HeaderSize + slotStreamLength);
        }

        MarkHeaderDirty();
        MarkDirtyRecordRange(recordOffset, newRecordArenaEnd);
        IsDirty = true;
        return Scalar8VarIdentityInsertResult.Inserted;
    }

    /// <summary>
    /// Rebuilds the persisted slot stream in the authoritative shelf image from the in-memory sidecars when needed.<br/>
    /// Batched write paths keep sidecars authoritative between inserts to avoid repeatedly shifting disk-shaped slot bytes in RAM.<br/>
    /// Callers must invoke this before publishing the shelf image or passing it to static helpers that only understand the persisted slot stream.<br/>
    /// </summary>
    internal void EnsureSlotBytesCurrent()
    {
        if (!SlotBytesDirty)
        {
            return;
        }

        int slotOffset = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < itemCount; i++)
        {
            int physicalIndex = sortedIndexes[i];
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffsets[physicalIndex]);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, keyPrefixes[physicalIndex]);
            slotOffset += Scalar8VarIdentityLayout.SlotSize;
        }

        SlotBytesDirty = false;
    }

    /// <summary>
    /// Marks the shelf image dirty after a caller performs a direct header-only mutation.<br/>
    /// </summary>
    public void MarkDirty()
    {
        IsDirty = true;
        RequiresFullRewrite = true;
    }

    /// <summary>
    /// Releases buffers owned by this mutable view back to their shared pools.<br/>
    /// Non-pooled views ignore the call except for optional clearing of their authoritative byte image.<br/>
    /// </summary>
    /// <param name="clearShelfBytes">Whether the used shelf extent should be cleared before the view is dropped or returned to the pool.</param>
    internal void Release(bool clearShelfBytes)
    {
        if (released)
        {
            return;
        }

        released = true;
        if (clearShelfBytes)
        {
            Bytes.AsSpan(0, Profile.ShelfExtentSize).Clear();
        }

        if (pooledSidecars)
        {
            ArrayPool<int>.Shared.Return(recordOffsets, clearArray: true);
            ArrayPool<uint>.Shared.Return(keyPrefixes, clearArray: true);
            ArrayPool<ulong>.Shared.Return(keys, clearArray: true);
            ArrayPool<int>.Shared.Return(sortedIndexes, clearArray: true);
            ArrayPool<bool>.Shared.Return(deletedSlots, clearArray: true);
        }

        if (pooledBytes)
        {
            ArrayPool<byte>.Shared.Return(Bytes, clearArray: false);
        }
    }

    /// <summary>
    /// Marks the persisted header as dirty after a tracked counter or link-field mutation.<br/>
    /// This lets commit publish only the compact header plus slot/record ranges for ordinary in-place inserts.<br/>
    /// </summary>
    internal void MarkHeaderDirty()
    {
        IsDirty = true;
        HeaderDirty = true;
    }

    /// <summary>
    /// Marks a sorted slot interval as deleted while retaining the current variable-identity record bytes.<br/>
    /// The slot sidecar remains physical until normalization so delete/repack policy can inspect live/deleted counts and orphaned payload bytes separately.<br/>
    /// </summary>
    /// <param name="startSlot">The first sorted slot to mark deleted.</param>
    /// <param name="deleteCount">The number of sorted slots to mark deleted.</param>
    /// <returns>The number of newly deleted live slots.</returns>
    internal int MarkSlotRangeDeleted(int startSlot, int deleteCount)
    {
        if (startSlot < 0 || startSlot > itemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(startSlot), startSlot, "The SV8 delete start slot must be inside the physical slot table.");
        }

        if (deleteCount < 0 || deleteCount > itemCount - startSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(deleteCount), deleteCount, "The SV8 delete count must fit inside the physical slot table.");
        }

        int marked = 0;
        for (int slotIndex = startSlot; slotIndex < startSlot + deleteCount; slotIndex++)
        {
            int physicalIndex = sortedIndexes[slotIndex];
            if (deletedSlots[physicalIndex])
            {
                continue;
            }

            deletedSlots[physicalIndex] = true;
            deletedItemCount++;
            deletedPayloadBytes += Scalar8VarIdentityLayout.GetRecordLength(Bytes, recordOffsets[physicalIndex]);
            marked++;
        }

        if (marked != 0)
        {
            Scalar8VarIdentityLayout.WriteReclaimablePayloadBytes(Bytes, deletedPayloadBytes);
            MarkDirty();
            SlotBytesDirty = true;
        }

        return marked;
    }

    /// <summary>
    /// Marks all live tuples whose encoded scalar key falls inside the inclusive key range as deleted.<br/>
    /// The search bounds are resolved against the sorted sidecar before tombstones are applied, keeping range deletes proportional to the matched key interval.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded key.</param>
    /// <returns>The number of newly deleted live tuples.</returns>
    internal int MarkKeyRangeDeleted(ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            return 0;
        }

        int startSlot = LowerBoundKey(lowerEncodedKey, Scalar8VarIdentityLayout.CreateKeyPrefix(lowerEncodedKey));
        int endSlot = LowerBoundKeyAfter(upperEncodedKey, Scalar8VarIdentityLayout.CreateKeyPrefix(upperEncodedKey));
        return MarkSlotRangeDeleted(startSlot, endSlot - startSlot);
    }

    /// <summary>
    /// Marks one exact encoded-key and raw-identity tuple as deleted when the tuple is currently live.<br/>
    /// This gives condition-driven delete a single-row primitive without requiring callers to scan a duplicate-key run themselves.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact encoded scalar key.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was newly deleted.</returns>
    internal bool MarkTupleDeleted(ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        uint prefix = Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey);
        int slotIndex = LowerBoundTuple(encodedKey, prefix, identity);
        if (slotIndex >= itemCount ||
            CompareSlotTuple(slotIndex, prefix, encodedKey, identity) != 0)
        {
            return false;
        }

        int physicalIndex = sortedIndexes[slotIndex];
        if (deletedSlots[physicalIndex])
        {
            return false;
        }

        return MarkSlotRangeDeleted(slotIndex, 1) == 1;
    }

    /// <summary>
    /// Removes deleted entries from the sorted sidecars while retaining record-arena bytes for a later full payload repack decision.<br/>
    /// Survivor metadata is first copied into pooled scratch arrays because sorted slot order is a permutation of physical sidecar indexes; compacting directly into the source arrays can overwrite metadata that a later survivor still references.<br/>
    /// This keeps ordinary search and insertion algorithms over live entries only while `PayloadBytesDeleted` continues to report orphaned record bytes.<br/>
    /// </summary>
    /// <returns>The number of deleted entries removed from the active slot stream.</returns>
    internal int NormalizeDeletedSlotsForPublication()
    {
        if (deletedItemCount == 0)
        {
            return 0;
        }

        int removed = deletedItemCount;
        int originalItemCount = itemCount;
        int[] compactedRecordOffsets = ArrayPool<int>.Shared.Rent(originalItemCount);
        uint[] compactedKeyPrefixes = ArrayPool<uint>.Shared.Rent(originalItemCount);
        ulong[] compactedKeys = ArrayPool<ulong>.Shared.Rent(originalItemCount);
        int writeIndex = 0;
        try
        {
            for (int slotIndex = 0; slotIndex < originalItemCount; slotIndex++)
            {
                int physicalIndex = sortedIndexes[slotIndex];
                if (deletedSlots[physicalIndex])
                {
                    continue;
                }

                compactedRecordOffsets[writeIndex] = recordOffsets[physicalIndex];
                compactedKeyPrefixes[writeIndex] = keyPrefixes[physicalIndex];
                compactedKeys[writeIndex] = keys[physicalIndex];
                writeIndex++;
            }

            Array.Copy(compactedRecordOffsets, recordOffsets, writeIndex);
            Array.Copy(compactedKeyPrefixes, keyPrefixes, writeIndex);
            Array.Copy(compactedKeys, keys, writeIndex);
            for (int slotIndex = 0; slotIndex < writeIndex; slotIndex++)
            {
                sortedIndexes[slotIndex] = slotIndex;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(compactedRecordOffsets, clearArray: false);
            ArrayPool<uint>.Shared.Return(compactedKeyPrefixes, clearArray: false);
            ArrayPool<ulong>.Shared.Return(compactedKeys, clearArray: false);
        }

        Array.Clear(deletedSlots, 0, originalItemCount);
        itemCount = writeIndex;
        slotStreamLength = checked(itemCount * Scalar8VarIdentityLayout.SlotSize);
        deletedItemCount = 0;
        Scalar8VarIdentityLayout.WriteItemCount(Bytes, itemCount);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        SlotBytesDirty = true;
        EnsureSlotBytesCurrent();
        MarkDirty();
        return removed;
    }

    /// <summary>
    /// Builds a compact replacement `SV8` shelf image when persisted orphaned identity bytes cross both supplied thresholds.<br/>
    /// The replacement preserves sorted scalar keys, raw identities, and the duplicate-run next-shelf link while resetting reclaimable-payload metadata.<br/>
    /// </summary>
    /// <param name="minimumDeletedPayloadBytes">The minimum orphaned payload bytes required before repack is considered.<br/></param>
    /// <param name="minimumDeletedPayloadPercent">The minimum orphaned percentage of used payload bytes required before repack is considered.<br/></param>
    /// <returns>A compact full shelf image, or null when no repack is warranted.<br/></returns>
    internal byte[]? BuildCompactedPayloadIfWorthwhile(int minimumDeletedPayloadBytes, int minimumDeletedPayloadPercent)
    {
        if (minimumDeletedPayloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDeletedPayloadBytes));
        if (minimumDeletedPayloadPercent < 0 || minimumDeletedPayloadPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumDeletedPayloadPercent));
        if (deletedItemCount != 0)
            _ = NormalizeDeletedSlotsForPublication();

        int deletedBytes = deletedPayloadBytes;
        int usedBytes = PayloadBytesUsed;
        if (deletedBytes <= 0 ||
            deletedBytes < minimumDeletedPayloadBytes ||
            usedBytes <= 0 ||
            checked((long)deletedBytes * 100L) < checked((long)usedBytes * minimumDeletedPayloadPercent))
        {
            return null;
        }

        byte[] compacted = new byte[Profile.ShelfExtentSize];
        Scalar8VarIdentityLayout.Initialize(compacted, Profile);
        Scalar8VarIdentityLayout.WriteNextShelfOffset(compacted, Scalar8VarIdentityLayout.ReadNextShelfOffset(Bytes));
        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + Scalar8VarIdentityLayout.ReadSlotCapacityBytes(compacted);
        for (int i = 0; i < itemCount; i++)
        {
            ulong key = ReadKeyAt(i);
            ReadOnlySpan<byte> identity = ReadIdentityAt(i);
            Scalar8VarIdentityLayout.WriteRecord(compacted, recordCursor, key, identity);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(compacted, slotCursor, recordCursor);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(compacted, slotCursor, Scalar8VarIdentityLayout.CreateKeyPrefix(key));
            recordCursor += Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
            slotCursor += Scalar8VarIdentityLayout.SlotSize;
        }

        Scalar8VarIdentityLayout.WriteItemCount(compacted, itemCount);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(compacted, checked(itemCount * Scalar8VarIdentityLayout.SlotSize));
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(compacted, recordCursor);
        Scalar8VarIdentityLayout.WriteReclaimablePayloadBytes(compacted, 0);
        return compacted;
    }

    /// <summary>
    /// Rebinds decoded `SV8` sidecars after a compact replacement image has been copied over the authoritative shelf bytes.<br/>
    /// The compact image is already in sorted tuple order, so physical and sorted indexes become identical and no record payload is decoded more than once.<br/>
    /// </summary>
    private void RebuildSidecarsAfterPayloadCompaction()
    {
        itemCount = Scalar8VarIdentityLayout.ReadItemCount(Bytes);
        slotStreamLength = Scalar8VarIdentityLayout.ReadSlotStreamLength(Bytes);
        recordArenaEnd = Scalar8VarIdentityLayout.ReadRecordArenaEnd(Bytes);
        deletedItemCount = 0;
        deletedPayloadBytes = 0;
        int slotOffset = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < itemCount; i++)
        {
            int recordOffset = Scalar8VarIdentityLayout.ReadSlotRecordOffset(Bytes, slotOffset);
            recordOffsets[i] = recordOffset;
            keyPrefixes[i] = Scalar8VarIdentityLayout.ReadSlotKeyPrefix(Bytes, slotOffset);
            keys[i] = Scalar8VarIdentityLayout.ReadKey(Bytes, recordOffset);
            sortedIndexes[i] = i;
            deletedSlots[i] = false;
            slotOffset += Scalar8VarIdentityLayout.SlotSize;
        }

        Scalar8VarIdentityLayout.WriteReclaimablePayloadBytes(Bytes, 0);
        SlotBytesDirty = false;
        MarkDirty();
    }

    /// <summary>
    /// Extends the tracked dirty slot-stream range for a mutation that changed persisted slot bytes.<br/>
    /// Ranges are shelf-relative, end-exclusive, and merged conservatively so publication can stage one slot segment per shelf.<br/>
    /// </summary>
    /// <param name="start">The first changed shelf-relative byte.</param>
    /// <param name="end">The exclusive changed byte end.</param>
    private void MarkDirtySlotRange(int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        if (dirtySlotEnd == 0)
        {
            dirtySlotStart = start;
            dirtySlotEnd = end;
            return;
        }

        dirtySlotStart = Math.Min(dirtySlotStart, start);
        dirtySlotEnd = Math.Max(dirtySlotEnd, end);
    }

    /// <summary>
    /// Extends the tracked dirty record-arena range for append-like variable identity bytes.<br/>
    /// Ranges are shelf-relative, end-exclusive, and merged conservatively so publication can stage one record segment per shelf.<br/>
    /// </summary>
    /// <param name="start">The first changed shelf-relative byte.</param>
    /// <param name="end">The exclusive changed byte end.</param>
    private void MarkDirtyRecordRange(int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        if (dirtyRecordEnd == 0)
        {
            dirtyRecordStart = start;
            dirtyRecordEnd = end;
            return;
        }

        dirtyRecordStart = Math.Min(dirtyRecordStart, start);
        dirtyRecordEnd = Math.Max(dirtyRecordEnd, end);
    }

    private static void ReturnSidecarsIfPooled(
        bool pooledSidecars,
        int[] recordOffsets,
        uint[] keyPrefixes,
        ulong[] keys,
        int[] sortedIndexes,
        bool[] deletedSlots)
    {
        if (!pooledSidecars)
        {
            return;
        }

        ArrayPool<int>.Shared.Return(recordOffsets, clearArray: true);
        ArrayPool<uint>.Shared.Return(keyPrefixes, clearArray: true);
        ArrayPool<ulong>.Shared.Return(keys, clearArray: true);
        ArrayPool<int>.Shared.Return(sortedIndexes, clearArray: true);
        ArrayPool<bool>.Shared.Return(deletedSlots, clearArray: true);
    }

    /// <summary>
    /// Reads the scalar key at a sorted slot from the decoded mutable sidecar.<br/>
    /// Duplicate-run fast paths use this to validate first/last key bounds without allocating a read-only decoder.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded scalar key at the requested slot.</returns>
    internal ulong ReadKeyAt(int slotIndex)
    {
        return keys[sortedIndexes[slotIndex]];
    }

    /// <summary>
    /// Reads the raw identity at a sorted slot from the decoded mutable sidecar.<br/>
    /// The returned span points into the authoritative shelf bytes and is valid until the shelf image is mutated again.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw identity bytes at the requested slot.</returns>
    internal ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return Scalar8VarIdentityLayout.ReadIdentity(Bytes, recordOffsets[sortedIndexes[slotIndex]]);
    }

    private bool ContainsKey(ulong encodedKey, uint prefix, out int index)
    {
        index = LowerBoundKey(encodedKey, prefix);
        return index < itemCount && CompareSlotKey(index, prefix, encodedKey) == 0;
    }

    private int LowerBoundKey(ulong encodedKey, uint prefix)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, encodedKey);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int LowerBoundKeyAfter(ulong encodedKey, uint prefix)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, encodedKey);
            if (comparison <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Finds the sorted sidecar insertion point for a tuple using binary search over the integer order index.<br/>
    /// This keeps non-monotonic inserts from moving record-offset and key-prefix sidecars; only the compact integer order index is shifted.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="prefix">The cached high 4-byte key prefix.</param>
    /// <param name="identity">The raw identity bytes to insert.</param>
    /// <returns>The sorted sidecar index where the tuple belongs.</returns>
    private int LowerBoundTuple(ulong encodedKey, uint prefix, ReadOnlySpan<byte> identity)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(middle, prefix, encodedKey, identity);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int CompareSlotKey(int slotIndex, uint prefix, ulong encodedKey)
    {
        int physicalIndex = sortedIndexes[slotIndex];
        uint slotPrefix = keyPrefixes[physicalIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        ulong slotKey = keys[physicalIndex];
        if (slotKey < encodedKey)
        {
            return -1;
        }

        return slotKey > encodedKey ? 1 : 0;
    }

    private int CompareSlotTuple(int slotIndex, uint prefix, ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        int physicalIndex = sortedIndexes[slotIndex];
        uint slotPrefix = keyPrefixes[physicalIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        ulong slotKey = keys[physicalIndex];
        if (slotKey < encodedKey)
        {
            return -1;
        }

        if (slotKey > encodedKey)
        {
            return 1;
        }

        return Scalar8VarIdentityLayout.CompareIdentityBytes(Scalar8VarIdentityLayout.ReadIdentity(Bytes, recordOffsets[physicalIndex]), identity);
    }
}
