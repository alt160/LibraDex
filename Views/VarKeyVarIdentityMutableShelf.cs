using LibraDex.Layouts;
using System.Buffers;

namespace LibraDex.Views;

/// <summary>
/// Holds one mutable, disk-shaped `VV` shelf image plus decoded slot sidecars for session-local write hot paths.<br/>
/// The byte array remains authoritative for persistence and range reads; the sidecar arrays only avoid decoding 3-byte offsets and prefixes again for every insert into the same shelf.<br/>
/// This type is intentionally shelf-local and non-persisted so the on-disk format stays identical to `VarKeyVarIdentityLayout` version 4.<br/>
/// </summary>
internal sealed class VarKeyVarIdentityMutableShelf
{
    private readonly int[] recordOffsets;
    private readonly uint[] keyPrefixes;
    private readonly bool[] deletedSlots;
    private readonly bool ownsBytes;
    private readonly bool ownsSidecars;
    private int itemCount;
    private int slotStreamLength;
    private int recordArenaEnd;
    private int deletedItemCount;
    private int deletedPayloadBytes;
    private bool released;

    private VarKeyVarIdentityMutableShelf(
        byte[] bytes,
        VarKeyVarIdentityProfile profile,
        int[] recordOffsets,
        uint[] keyPrefixes,
        bool[] deletedSlots,
        bool ownsBytes,
        bool ownsSidecars,
        int itemCount,
        int slotStreamLength,
        int slotCapacityBytes,
        int recordArenaEnd)
    {
        Bytes = bytes;
        Profile = profile;
        this.recordOffsets = recordOffsets;
        this.keyPrefixes = keyPrefixes;
        this.deletedSlots = deletedSlots;
        this.ownsBytes = ownsBytes;
        this.ownsSidecars = ownsSidecars;
        this.itemCount = itemCount;
        this.slotStreamLength = slotStreamLength;
        SlotCapacityBytes = slotCapacityBytes;
        this.recordArenaEnd = recordArenaEnd;
    }

    /// <summary>
    /// Gets the authoritative runtime shelf bytes that will be written back to the DataKernel when the session publishes the shelf.<br/>
    /// Mutations update this array in the same layout used on disk, including the fixed-width slot array and forward-growing record arena.<br/>
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// Gets the shelf profile used to validate capacity, maximum key length, and physical extent size for this mutable shelf.<br/>
    /// The profile is derived from the shelf header when existing bytes are loaded from the file.<br/>
    /// </summary>
    public VarKeyVarIdentityProfile Profile { get; }

    /// <summary>
    /// Gets the number of sorted tuple slots currently stored in the shelf.<br/>
    /// This mirrors the persisted header value and is updated after successful insert mutations.<br/>
    /// </summary>
    public int ItemCount => itemCount;

    public int PhysicalItemCount => itemCount;

    public int LiveItemCount => itemCount - deletedItemCount;

    public int DeletedItemCount => deletedItemCount;

    public int PayloadBytesUsed => recordArenaEnd - (VarKeyVarIdentityLayout.HeaderSize + SlotCapacityBytes);

    public int PayloadBytesLive => PayloadBytesUsed - deletedPayloadBytes;

    public int PayloadBytesDeleted => deletedPayloadBytes;

    /// <summary>
    /// Gets the reserved persisted slot-array byte capacity for this shelf extent.<br/>
    /// Inserts may shift bytes only inside this reserved region; when it is exhausted the caller must grow or split the shelf.<br/>
    /// </summary>
    public int SlotCapacityBytes { get; }

    /// <summary>
    /// Gets whether this mutable shelf has changed since it was loaded into the session-local sidecar cache.<br/>
    /// Clean sidecars may be retained during a batch to avoid repeated decode work, but only dirty sidecars should be flushed to DataKernel staging.<br/>
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Decodes an existing `VV` shelf into a mutable sidecar while keeping the supplied byte array as the authoritative image.<br/>
    /// The sidecar arrays are sized to the reserved slot capacity instead of the current item count, so ordinary inserts only shift existing entries and do not reallocate slot metadata.<br/>
    /// </summary>
    /// <param name="bytes">The disk-shaped shelf byte image to own for mutation.</param>
    /// <param name="profile">The `VV` shelf profile expected by the caller.</param>
    /// <param name="ownsBytes">Whether the created mutable shelf should return <paramref name="bytes"/> to the shared byte pool when released.</param>
    /// <param name="rentSidecars">Whether decoded slot sidecars should be rented from shared pools instead of allocated as ordinary arrays.</param>
    /// <param name="shelf">The decoded mutable shelf when validation succeeds.</param>
    /// <returns>`true` when the byte image is a valid `VV` shelf for <paramref name="profile"/>.</returns>
    public static bool TryCreate(
        byte[] bytes,
        VarKeyVarIdentityProfile profile,
        bool ownsBytes,
        bool rentSidecars,
        out VarKeyVarIdentityMutableShelf shelf)
    {
        shelf = null!;
        if (bytes.Length < profile.ShelfExtentSize ||
            VarKeyVarIdentityLayout.ReadMagic(bytes) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(bytes) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(bytes) != VarKeyVarIdentityLayout.HeaderSize ||
            VarKeyVarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize ||
            ((VarKeyVarIdentityLayout.ReadFlags(bytes) & VarKeyVarIdentityLayout.DescendingFlag) != 0) != profile.Descending)
        {
            if (ownsBytes)
            {
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            }

            return false;
        }

        int count = VarKeyVarIdentityLayout.ReadItemCount(bytes);
        int slotStreamLength = VarKeyVarIdentityLayout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = VarKeyVarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * VarKeyVarIdentityLayout.SlotSize);
        int expectedSlotCapacityBytes = VarKeyVarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes;
        int reclaimablePayloadBytes = VarKeyVarIdentityLayout.ReadReclaimablePayloadBytes(bytes);
        if (count < 0 ||
            slotStreamLength != expectedSlotStreamLength ||
            slotStreamLength > slotCapacityBytes ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < recordArenaStart ||
            recordArenaEnd > profile.ShelfExtentSize ||
            reclaimablePayloadBytes > recordArenaEnd - recordArenaStart)
        {
            if (ownsBytes)
            {
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            }

            return false;
        }

        int slotCapacity = slotCapacityBytes / VarKeyVarIdentityLayout.SlotSize;
        int[] offsets = rentSidecars ? ArrayPool<int>.Shared.Rent(slotCapacity) : new int[slotCapacity];
        uint[] prefixes = rentSidecars ? ArrayPool<uint>.Shared.Rent(slotCapacity) : new uint[slotCapacity];
        bool[] deleted = rentSidecars ? ArrayPool<bool>.Shared.Rent(slotCapacity) : new bool[slotCapacity];
        if (rentSidecars)
        {
            deleted.AsSpan(0, slotCapacity).Clear();
        }
        int cursor = VarKeyVarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = VarKeyVarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                ReleaseFailedDecodeBuffers(bytes, offsets, prefixes, deleted, ownsBytes, rentSidecars);
                return false;
            }

            int recordLength = VarKeyVarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                ReleaseFailedDecodeBuffers(bytes, offsets, prefixes, deleted, ownsBytes, rentSidecars);
                return false;
            }

            offsets[i] = offset;
            prefixes[i] = VarKeyVarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += VarKeyVarIdentityLayout.SlotSize;
        }

        shelf = new VarKeyVarIdentityMutableShelf(
            bytes,
            profile,
            offsets,
            prefixes,
            deleted,
            ownsBytes,
            rentSidecars,
            count,
            slotStreamLength,
            slotCapacityBytes,
            recordArenaEnd);
        shelf.deletedPayloadBytes = reclaimablePayloadBytes;
        return true;
    }

    /// <summary>
    /// Returns rented shelf and sidecar buffers owned by this mutable view to their shared pools.<br/>
    /// Callers must release only after the authoritative shelf bytes have either been copied into DataKernel staging or deliberately discarded by batch abort or structural replacement.<br/>
    /// The method is idempotent so structural paths and cleanup paths can safely converge without double-returning arrays.<br/>
    /// </summary>
    /// <param name="clearShelfBytes">Whether the rented shelf byte buffer should be cleared before returning it to the shared pool.</param>
    public void Release(bool clearShelfBytes)
    {
        if (released)
        {
            return;
        }

        released = true;
        if (ownsSidecars)
        {
            ArrayPool<int>.Shared.Return(recordOffsets, clearArray: false);
            ArrayPool<uint>.Shared.Return(keyPrefixes, clearArray: false);
            ArrayPool<bool>.Shared.Return(deletedSlots, clearArray: false);
        }

        if (ownsBytes)
        {
            ArrayPool<byte>.Shared.Return(Bytes, clearArray: clearShelfBytes);
        }
    }

    /// <summary>
    /// Inserts one raw key and raw variable-length identity into the owned shelf image when reserved slot and record capacity are still available.<br/>
    /// The slot bytes and decoded sidecar arrays are shifted together, while the new record is appended at the current record arena end.<br/>
    /// Full results are non-mutating and tell the caller to use the existing grow or split path with the current authoritative bytes.<br/>
    /// </summary>
    /// <param name="key">The raw byte key to insert.</param>
    /// <param name="identity">The raw byte identity to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for route-local mutation evidence.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare against adjacent keys.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <returns>The insert result.</returns>
    public VarKeyVarIdentityInsertResult InsertWithMutationHint(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyVarIdentityMutationHint mutationHint)
    {
        mutationHint = default;
        if (deletedItemCount != 0)
        {
            _ = NormalizeDeletedSlotsForPublication();
        }

        if (key.Length <= 0 || key.Length > Profile.MaxKeyLength || identity.Length <= 0 || identity.Length > Profile.MaxIdentityLength)
        {
            return VarKeyVarIdentityInsertResult.Invalid;
        }

        int insertIndex = LowerBound(key, identity);
        if (insertIndex < itemCount && CompareSlotTuple(insertIndex, VarKeyVarIdentityLayout.CreateKeyPrefix(key), key, identity) == 0)
        {
            return VarKeyVarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = LowerBoundKey(key);
            if (keyIndex < itemCount && ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                return VarKeyVarIdentityInsertResult.KeyConflict;
            }
        }

        mutationHint = CreateMutationHint(insertIndex, key, hintStartDepth, maxHintBytes);
        if (slotStreamLength + VarKeyVarIdentityLayout.SlotSize > SlotCapacityBytes)
        {
            return VarKeyVarIdentityInsertResult.Full;
        }

        int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(key.Length, identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > Profile.ShelfExtentSize &&
            deletedPayloadBytes >= recordLength &&
            RepackPayloadIfWorthwhile(recordLength, 0))
        {
            recordOffset = recordArenaEnd;
            newRecordArenaEnd = recordOffset + recordLength;
        }

        if (newRecordArenaEnd > Profile.ShelfExtentSize)
        {
            return VarKeyVarIdentityInsertResult.Full;
        }

        int slotOffset = VarKeyVarIdentityLayout.HeaderSize + checked(insertIndex * VarKeyVarIdentityLayout.SlotSize);
        int slotTailLength = slotStreamLength - checked(insertIndex * VarKeyVarIdentityLayout.SlotSize);
        if (slotTailLength < 0)
        {
            return VarKeyVarIdentityInsertResult.Invalid;
        }

        if (slotTailLength > 0)
        {
            Bytes.AsSpan(slotOffset, slotTailLength).CopyTo(Bytes.AsSpan(slotOffset + VarKeyVarIdentityLayout.SlotSize, slotTailLength));
            Array.Copy(recordOffsets, insertIndex, recordOffsets, insertIndex + 1, itemCount - insertIndex);
            Array.Copy(keyPrefixes, insertIndex, keyPrefixes, insertIndex + 1, itemCount - insertIndex);
            Array.Copy(deletedSlots, insertIndex, deletedSlots, insertIndex + 1, itemCount - insertIndex);
        }

        uint keyPrefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        VarKeyVarIdentityLayout.WriteRecord(Bytes, recordOffset, key, identity);
        VarKeyVarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffset);
        VarKeyVarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, keyPrefix);
        recordOffsets[insertIndex] = recordOffset;
        keyPrefixes[insertIndex] = keyPrefix;
        deletedSlots[insertIndex] = false;
        itemCount++;
        slotStreamLength += VarKeyVarIdentityLayout.SlotSize;
        recordArenaEnd = newRecordArenaEnd;
        VarKeyVarIdentityLayout.WriteItemCount(Bytes, itemCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(Bytes, recordArenaEnd);
        IsDirty = true;
        return VarKeyVarIdentityInsertResult.Inserted;
    }

    /// <summary>
    /// Marks a sorted slot interval as deleted while retaining the current record arena bytes.<br/>
    /// The slot table remains physical until normalization, letting delete/repack policy inspect live counts and orphaned payload byte counts separately.<br/>
    /// </summary>
    /// <param name="startSlot">The first sorted slot to mark deleted.</param>
    /// <param name="deleteCount">The number of sorted slots to mark deleted.</param>
    /// <returns>The number of newly deleted live slots.</returns>
    internal int MarkSlotRangeDeleted(int startSlot, int deleteCount)
    {
        if (startSlot < 0 || startSlot > itemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(startSlot), startSlot, "The VV delete start slot must be inside the physical slot table.");
        }

        if (deleteCount < 0 || deleteCount > itemCount - startSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(deleteCount), deleteCount, "The VV delete count must fit inside the physical slot table.");
        }

        int marked = 0;
        for (int slotIndex = startSlot; slotIndex < startSlot + deleteCount; slotIndex++)
        {
            if (deletedSlots[slotIndex])
            {
                continue;
            }

            deletedSlots[slotIndex] = true;
            deletedItemCount++;
            deletedPayloadBytes += VarKeyVarIdentityLayout.GetRecordLength(Bytes, recordOffsets[slotIndex]);
            marked++;
        }

        if (marked != 0)
        {
            VarKeyVarIdentityLayout.WriteReclaimablePayloadBytes(Bytes, deletedPayloadBytes);
            IsDirty = true;
        }

        return marked;
    }

    /// <summary>
    /// Marks all live tuples whose raw key falls inside the inclusive key range as deleted.<br/>
    /// The method uses the sorted key sidecar to bound the affected slot interval before applying tombstones, so routed range deletes avoid scanning unrelated shelf rows.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of newly deleted live tuples.</returns>
    internal int MarkKeyRangeDeleted(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        int startSlot = LowerBoundKey(Profile.Descending ? upperKey : lowerKey);
        int endSlot = LowerBoundKeyAfter(Profile.Descending ? lowerKey : upperKey);
        return MarkSlotRangeDeleted(startSlot, endSlot - startSlot);
    }

    /// <summary>
    /// Counts live tuples whose raw key is inside an inclusive key range.<br/>
    /// The count uses the mutable tombstone sidecar, keeping durability-batch aggregate reads aligned with uncommitted shelf-local deletes.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key bound.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key bound.<br/></param>
    /// <returns>The number of live shelf-local tuples in the requested key range.<br/></returns>
    internal int CountLiveItemsInKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        int count = 0;
        int startSlot = LowerBoundKey(Profile.Descending ? upperKey : lowerKey);
        int endSlot = LowerBoundKeyAfter(Profile.Descending ? lowerKey : upperKey);
        for (int i = startSlot; i < endSlot; i++)
        {
            if (!deletedSlots[i])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Marks one exact raw-key and raw-identity tuple as deleted when the tuple is currently live.<br/>
    /// Exact tuple delete is the primitive needed when a higher-level condition resolves to a single identity-bearing key rather than a whole key range.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was newly deleted.</returns>
    internal bool MarkTupleDeleted(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int slotIndex = LowerBound(key, identity);
        if (slotIndex >= itemCount ||
            deletedSlots[slotIndex] ||
            CompareSlotTuple(slotIndex, VarKeyVarIdentityLayout.CreateKeyPrefix(key), key, identity) != 0)
        {
            return false;
        }

        return MarkSlotRangeDeleted(slotIndex, 1) == 1;
    }

    /// <summary>
    /// Removes deleted slots from the sorted slot stream while retaining record-arena bytes for a later full payload repack decision.<br/>
    /// This keeps ordinary search and insertion algorithms over live slots only while `PayloadBytesDeleted` continues to report orphaned record bytes.<br/>
    /// </summary>
    /// <returns>The number of deleted slots removed from the active slot stream.</returns>
    internal int NormalizeDeletedSlotsForPublication()
    {
        if (deletedItemCount == 0)
        {
            return 0;
        }

        int removed = deletedItemCount;
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < itemCount; readIndex++)
        {
            if (deletedSlots[readIndex])
            {
                deletedSlots[readIndex] = false;
                continue;
            }

            if (writeIndex != readIndex)
            {
                recordOffsets[writeIndex] = recordOffsets[readIndex];
                keyPrefixes[writeIndex] = keyPrefixes[readIndex];
            }

            writeIndex++;
        }

        Array.Clear(deletedSlots, writeIndex, itemCount - writeIndex);
        itemCount = writeIndex;
        slotStreamLength = checked(itemCount * VarKeyVarIdentityLayout.SlotSize);
        deletedItemCount = 0;
        VarKeyVarIdentityLayout.WriteItemCount(Bytes, itemCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        RewriteSlotBytes();
        IsDirty = true;
        return removed;
    }

    /// <summary>
    /// Rebuilds the `VV` record arena into a compact live-record prefix when persisted orphaned bytes cross both supplied thresholds.<br/>
    /// The rewrite preserves sorted raw key/identity tuples and the current shelf extent while resetting exact reclaimable-payload metadata to zero.<br/>
    /// </summary>
    /// <param name="minimumDeletedPayloadBytes">The minimum orphaned payload bytes required before repack is considered.<br/></param>
    /// <param name="minimumDeletedPayloadPercent">The minimum orphaned percentage of used payload bytes required before repack is considered.<br/></param>
    /// <returns><see langword="true"/> when the record arena was rebuilt.<br/></returns>
    internal bool RepackPayloadIfWorthwhile(int minimumDeletedPayloadBytes, int minimumDeletedPayloadPercent)
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
            return false;
        }

        byte[] compacted = new byte[Profile.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(compacted, Profile);
        int slotCursor = VarKeyVarIdentityLayout.HeaderSize;
        int recordCursor = VarKeyVarIdentityLayout.HeaderSize + VarKeyVarIdentityLayout.ReadSlotCapacityBytes(compacted);
        for (int i = 0; i < itemCount; i++)
        {
            ReadOnlySpan<byte> key = ReadKeyAt(i);
            ReadOnlySpan<byte> identity = ReadIdentityAt(i);
            VarKeyVarIdentityLayout.WriteRecord(compacted, recordCursor, key, identity);
            VarKeyVarIdentityLayout.WriteSlotRecordOffset(compacted, slotCursor, recordCursor);
            VarKeyVarIdentityLayout.WriteSlotKeyPrefix(compacted, slotCursor, keyPrefixes[i]);
            recordOffsets[i] = recordCursor;
            recordCursor += VarKeyVarIdentityLayout.GetNewRecordLength(key.Length, identity.Length);
            slotCursor += VarKeyVarIdentityLayout.SlotSize;
        }

        VarKeyVarIdentityLayout.WriteItemCount(compacted, itemCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(compacted, checked(itemCount * VarKeyVarIdentityLayout.SlotSize));
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(compacted, recordCursor);
        compacted.CopyTo(Bytes, 0);
        slotStreamLength = checked(itemCount * VarKeyVarIdentityLayout.SlotSize);
        recordArenaEnd = recordCursor;
        deletedPayloadBytes = 0;
        VarKeyVarIdentityLayout.WriteReclaimablePayloadBytes(Bytes, 0);
        IsDirty = true;
        return true;
    }

    private void RewriteSlotBytes()
    {
        int slotOffset = VarKeyVarIdentityLayout.HeaderSize;
        for (int slotIndex = 0; slotIndex < itemCount; slotIndex++)
        {
            VarKeyVarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffsets[slotIndex]);
            VarKeyVarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, keyPrefixes[slotIndex]);
            slotOffset += VarKeyVarIdentityLayout.SlotSize;
        }
    }

    private int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, key);
            if (Profile.Descending ? comparison > 0 : comparison < 0)
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

    private int LowerBound(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(middle, prefix, key, identity);
            if (Profile.Descending ? comparison > 0 : comparison < 0)
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

    private int LowerBoundKeyAfter(ReadOnlySpan<byte> key)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, key);
            if (Profile.Descending ? comparison >= 0 : comparison <= 0)
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
    /// Reads the raw key bytes for an existing sorted slot without allocating or decoding a second shelf view.<br/>
    /// Structural split paths use this to consume the already-decoded mutable shelf sidecars while keeping the byte image authoritative.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw key bytes stored at <paramref name="slotIndex"/>.</returns>
    internal ReadOnlySpan<byte> ReadKeyAt(int slotIndex)
    {
        return VarKeyVarIdentityLayout.ReadKey(Bytes, recordOffsets[slotIndex]);
    }

    /// <summary>
    /// Reads the byte offset and length of a key payload for an existing sorted slot without allocating.<br/>
    /// Split builders use this once per source tuple so later boundary scans can index directly into the authoritative shelf bytes.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to inspect.</param>
    /// <param name="keyOffset">The byte offset where the raw key payload begins.</param>
    /// <param name="keyLength">The raw key length in bytes.</param>
    internal void ReadKeyLocationAt(int slotIndex, out int keyOffset, out int keyLength)
    {
        int cursor = recordOffsets[slotIndex];
        keyLength = VarKeyVarIdentityLayout.ReadVarUInt32(Bytes, ref cursor);
        keyOffset = cursor;
    }

    /// <summary>
    /// Reads the raw variable-length identity for an existing sorted slot without allocating or cloning key data.<br/>
    /// This is intended for structural transforms that need to rebuild shelves from the mutable sidecar source order.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw identity stored at <paramref name="slotIndex"/>.</returns>
    internal ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return VarKeyVarIdentityLayout.ReadIdentity(Bytes, recordOffsets[slotIndex], out _);
    }

    /// <summary>
    /// Reads the byte offset and length of an identity payload for an existing sorted slot without allocating.<br/>
    /// Split builders use this to rebuild replacement shelves directly from the authoritative shelf bytes while preserving raw identity ordering.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to inspect.</param>
    /// <param name="identityOffset">The byte offset where the raw identity payload begins.</param>
    /// <param name="identityLength">The raw identity length in bytes.</param>
    internal void ReadIdentityLocationAt(int slotIndex, out int identityOffset, out int identityLength)
    {
        int cursor = recordOffsets[slotIndex];
        int keyLength = VarKeyVarIdentityLayout.ReadVarUInt32(Bytes, ref cursor);
        cursor += keyLength;
        identityLength = VarKeyVarIdentityLayout.ReadVarUInt32(Bytes, ref cursor);
        identityOffset = cursor;
    }

    private VarKeyVarIdentityMutationHint CreateMutationHint(
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
            maxCommonBytes = Math.Max(maxCommonBytes, CountCommonPrefixBytes(ReadKeyAt(insertIndex - 1), key, hintStartDepth, maxHintBytes));
        }

        if (insertIndex < itemCount)
        {
            compared++;
            maxCommonBytes = Math.Max(maxCommonBytes, CountCommonPrefixBytes(ReadKeyAt(insertIndex), key, hintStartDepth, maxHintBytes));
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

    private int CompareSlotKey(int slotIndex, uint prefix, ReadOnlySpan<byte> key)
    {
        uint slotPrefix = keyPrefixes[slotIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        return VarKeyVarIdentityLayout.CompareRecordKey(Bytes, recordOffsets[slotIndex], key);
    }

    private int CompareSlotTuple(int slotIndex, uint prefix, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        uint slotPrefix = keyPrefixes[slotIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        return VarKeyVarIdentityLayout.CompareRecordTuple(Bytes, recordOffsets[slotIndex], key, identity);
    }

    private static void ReleaseFailedDecodeBuffers(
        byte[] bytes,
        int[] offsets,
        uint[] prefixes,
        bool[] deletedSlots,
        bool ownsBytes,
        bool ownsSidecars)
    {
        if (ownsSidecars)
        {
            ArrayPool<int>.Shared.Return(offsets, clearArray: false);
            ArrayPool<uint>.Shared.Return(prefixes, clearArray: false);
            ArrayPool<bool>.Shared.Return(deletedSlots, clearArray: false);
        }

        if (ownsBytes)
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }
}
