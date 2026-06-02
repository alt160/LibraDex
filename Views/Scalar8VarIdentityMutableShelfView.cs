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
    private int itemCount;
    private int slotStreamLength;
    private int recordArenaEnd;
    private int deletedItemCount;
    private int deletedPayloadBytes;

    private Scalar8VarIdentityMutableShelfView(
        byte[] bytes,
        Scalar8VarIdentityProfile profile,
        int[] recordOffsets,
        uint[] keyPrefixes,
        ulong[] keys,
        int[] sortedIndexes,
        bool[] deletedSlots,
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
        if (count < 0 ||
            slotStreamLength != expectedSlotStreamLength ||
            slotStreamLength > slotCapacityBytes ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < recordArenaStart ||
            recordArenaEnd > profile.ShelfExtentSize)
        {
            return false;
        }

        int slotCapacity = slotCapacityBytes / Scalar8VarIdentityLayout.SlotSize;
        int[] offsets = new int[slotCapacity];
        uint[] prefixes = new uint[slotCapacity];
        ulong[] keys = new ulong[slotCapacity];
        int[] indexes = new int[slotCapacity];
        bool[] deleted = new bool[slotCapacity];
        int cursor = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = Scalar8VarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            offsets[i] = offset;
            prefixes[i] = Scalar8VarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            keys[i] = Scalar8VarIdentityLayout.ReadKey(bytes, offset);
            indexes[i] = i;
            cursor += Scalar8VarIdentityLayout.SlotSize;
        }

        shelf = new Scalar8VarIdentityMutableShelfView(bytes, profile, offsets, prefixes, keys, indexes, deleted, count, slotStreamLength, slotCapacityBytes, recordArenaEnd);
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
        }
        else
        {
            SlotBytesDirty = true;
        }

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
            IsDirty = true;
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
        int writeIndex = 0;
        for (int slotIndex = 0; slotIndex < itemCount; slotIndex++)
        {
            int physicalIndex = sortedIndexes[slotIndex];
            if (deletedSlots[physicalIndex])
            {
                deletedSlots[physicalIndex] = false;
                continue;
            }

            if (writeIndex != physicalIndex)
            {
                recordOffsets[writeIndex] = recordOffsets[physicalIndex];
                keyPrefixes[writeIndex] = keyPrefixes[physicalIndex];
                keys[writeIndex] = keys[physicalIndex];
            }

            sortedIndexes[writeIndex] = writeIndex;
            writeIndex++;
        }

        Array.Clear(deletedSlots, writeIndex, itemCount - writeIndex);
        itemCount = writeIndex;
        slotStreamLength = checked(itemCount * Scalar8VarIdentityLayout.SlotSize);
        deletedItemCount = 0;
        Scalar8VarIdentityLayout.WriteItemCount(Bytes, itemCount);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        SlotBytesDirty = true;
        EnsureSlotBytesCurrent();
        IsDirty = true;
        return removed;
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
