using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable hot-path projection over a `Fixed32Scalar16` (`FS32-16`) shelf.<br/>
/// The projection can read and write shelf bytes but owns no memory and performs no file I/O.<br/>
/// </summary>
internal ref struct Fixed32Scalar16
{
    private Span<byte> bytes;
    private Fixed32Scalar16Profile profile;

    /// <summary>
    /// Creates a mutable projection over shelf bytes.<br/>
    /// The caller owns the bytes and controls when mutations are staged or committed through outer storage code.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    public Fixed32Scalar16(Span<byte> bytes)
        : this(bytes, Fixed32Scalar16Profile.Default40KiB)
    {
    }

    /// <summary>
    /// Creates a mutable projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants while the shelf header stores only local mutable state.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Fixed32Scalar16(Span<byte> bytes, Fixed32Scalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public ushort ItemCount => Fixed32Scalar16Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid => AsReadOnly().IsValid;

    /// <summary>
    /// Initializes the shelf as an empty `Fixed32Scalar16` shelf.<br/>
    /// This clears the full shelf extent so inactive slots and payload cells have deterministic bytes for validation.<br/>
    /// </summary>
    public void Initialize()
    {
        bytes.Clear();
        Fixed32Scalar16Layout.WriteMagic(bytes, Fixed32Scalar16Layout.Magic);
        Fixed32Scalar16Layout.WriteFormatVersion(bytes, Fixed32Scalar16Layout.FormatVersion);
        Fixed32Scalar16Layout.WriteHeaderSize(bytes, Fixed32Scalar16Layout.HeaderSize);
        Fixed32Scalar16Layout.WriteFlags(bytes, profile.Descending ? Fixed32Scalar16Layout.DescendingFlag : 0);
        Fixed32Scalar16Layout.WriteItemCount(bytes, 0);
    }

    /// <summary>
    /// Inserts an encoded `(key0, key1, key2, key3, identityHigh, identityLow)` tuple into the shelf.<br/>
    /// Non-unique mode uses tuple uniqueness and treats an already-present tuple as a physical no-op.<br/>
    /// Unique mode treats any existing equal key as a key conflict before identity tie-breaking can insert another tuple.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public Fixed32Scalar16InsertResult Insert(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        return InsertWithMutationBounds(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys, out _);
    }

    /// <summary>
    /// Inserts an encoded tuple with a zero high identity lane and the supplied low identity lane.<br/>
    /// This overload is for deterministic 64-bit identity fixtures that are physically stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public Fixed32Scalar16InsertResult Insert(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        return Insert(key0, key1, key2, key3, 0, encodedIdentityLow, allowDuplicateKeys);
    }

    /// <summary>
    /// Inserts an encoded `(key0, key1, key2, key3, identityHigh, identityLow)` tuple and returns the conservative byte ranges changed by a successful insert.<br/>
    /// The mutation bounds let batch publication stage known dirty shelf regions without rediscovering them by comparing full shelf images at commit.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Fixed32Scalar16InsertResult InsertWithMutationBounds(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Fixed32Scalar16MutationBounds mutationBounds)
    {
        mutationBounds = default;
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return ClassifyFullInsert(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
        }

        if (TryAppendInSortedOrder(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys, count, out mutationBounds))
        {
            return Fixed32Scalar16InsertResult.Inserted;
        }

        Fixed32Scalar16ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            ushort existingOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Fixed32Scalar16Layout.CompareItemTuple(bytes, existingOffset, key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow);
            if (comparison == 0)
            {
                return Fixed32Scalar16InsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key0, key1, key2, key3);
            if (keyIndex < count)
            {
                ushort keyOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, keyIndex);
                if (Fixed32Scalar16Layout.CompareItemKey(bytes, keyOffset, key0, key1, key2, key3) == 0)
                {
                    return Fixed32Scalar16InsertResult.KeyConflict;
                }
            }
        }

        int itemOffset = Fixed32Scalar16Layout.GetItemOffset(profile, count);
        Fixed32Scalar16Layout.WriteItemKey(bytes, itemOffset, key0, key1, key2, key3);
        Fixed32Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            Span<byte> slots = bytes.Slice(
                Fixed32Scalar16Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * Fixed32Scalar16Layout.SlotSize);
            slots.CopyTo(bytes.Slice(Fixed32Scalar16Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
        }

        Fixed32Scalar16Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        Fixed32Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, insertIndex, count, itemOffset);
        return Fixed32Scalar16InsertResult.Inserted;
    }

    /// <summary>
    /// Removes a contiguous sorted-slot interval from this shelf.<br/>
    /// Surviving tuples are compacted into the dense fixed-payload prefix and their sorted slots are rewritten to those new payload cells.<br/>
    /// This gives `FS32-16` condition-driven delete a shelf-local mutation primitive without rebuilding routes or the whole index.<br/>
    /// </summary>
    /// <param name="startSlot">The first sorted slot to remove.</param>
    /// <param name="removeCount">The number of sorted slots to remove.</param>
    /// <returns>The number of tuples removed from the active shelf view.</returns>
    internal int RemoveSlotRange(int startSlot, int removeCount)
    {
        ushort count = ItemCount;
        int marked = MarkSlotRangeDeleted(startSlot, removeCount);
        if (marked == 0)
        {
            return 0;
        }

        return NormalizeDeletedSlots(count);
    }

    /// <summary>
    /// Marks a contiguous sorted-slot interval with the fixed deleted-slot sentinel without compacting the shelf.<br/>
    /// The physical slot count is intentionally retained so a batch-local shelf can accumulate multiple deletes and expose truthful live/deleted counts before publication.<br/>
    /// Callers that will publish or reuse the shelf for ordinary fixed-shelf search should call <see cref="NormalizeDeletedSlotsForPublication"/> before exposing the image to those paths.<br/>
    /// </summary>
    /// <param name="startSlot">The first sorted slot to mark deleted.</param>
    /// <param name="removeCount">The number of sorted slots to mark deleted.</param>
    /// <returns>The number of newly tombstoned active slots.</returns>
    internal int MarkSlotRangeDeleted(int startSlot, int removeCount)
    {
        ushort count = ItemCount;
        if (startSlot < 0 || startSlot > count)
        {
            throw new ArgumentOutOfRangeException(nameof(startSlot), startSlot, "The FS32-16 remove start slot must be inside the physical slot table.");
        }

        if (removeCount < 0 || removeCount > count - startSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(removeCount), removeCount, "The FS32-16 remove count must fit inside the physical slot table.");
        }

        int marked = 0;
        for (int slotIndex = startSlot; slotIndex < startSlot + removeCount; slotIndex++)
        {
            if (Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex) == Fixed32Scalar16Layout.DeletedSlotOffset)
            {
                continue;
            }

            Fixed32Scalar16Layout.WriteSlot(bytes, profile, slotIndex, Fixed32Scalar16Layout.DeletedSlotOffset);
            marked++;
        }

        return marked;
    }

    /// <summary>
    /// Compacts any deleted-slot sentinels back into dense sorted-slot and payload prefixes for publication.<br/>
    /// This is the explicit fixed-shelf boundary between batch-local tombstone accumulation and ordinary persisted shelf bytes.<br/>
    /// </summary>
    /// <returns>The number of tombstoned slots removed during normalization.</returns>
    internal int NormalizeDeletedSlotsForPublication()
    {
        return NormalizeDeletedSlots(ItemCount);
    }

    /// <summary>
    /// Counts fixed deleted-slot sentinels in the physical slot table.<br/>
    /// The count is intentionally derived from the slot table until a persisted metadata field is introduced for fixed shelves.<br/>
    /// </summary>
    /// <param name="physicalCount">The physical slot count to scan.</param>
    /// <returns>The number of slot entries currently marked deleted.</returns>
    private ushort CountDeletedSlots(ushort physicalCount)
    {
        ushort deleted = 0;
        for (int slotIndex = 0; slotIndex < physicalCount; slotIndex++)
        {
            if (Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex) == Fixed32Scalar16Layout.DeletedSlotOffset)
            {
                deleted++;
            }
        }

        return deleted;
    }

    private int NormalizeDeletedSlots(ushort physicalCount)
    {
        if (CountDeletedSlots(physicalCount) == 0)
        {
            return 0;
        }

        byte[] compacted = new byte[physicalCount * Fixed32Scalar16Layout.ItemSize];
        int writeIndex = 0;
        for (int slotIndex = 0; slotIndex < physicalCount; slotIndex++)
        {
            ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
            if (itemOffset == Fixed32Scalar16Layout.DeletedSlotOffset)
            {
                continue;
            }

            bytes.Slice(itemOffset, Fixed32Scalar16Layout.ItemSize)
                .CopyTo(compacted.AsSpan(writeIndex * Fixed32Scalar16Layout.ItemSize, Fixed32Scalar16Layout.ItemSize));
            writeIndex++;
        }

        for (int itemIndex = 0; itemIndex < writeIndex; itemIndex++)
        {
            int itemOffset = Fixed32Scalar16Layout.GetItemOffset(profile, itemIndex);
            compacted.AsSpan(itemIndex * Fixed32Scalar16Layout.ItemSize, Fixed32Scalar16Layout.ItemSize)
                .CopyTo(bytes.Slice(itemOffset, Fixed32Scalar16Layout.ItemSize));
            Fixed32Scalar16Layout.WriteSlot(bytes, profile, itemIndex, checked((ushort)itemOffset));
        }

        Fixed32Scalar16Layout.WriteItemCount(bytes, checked((ushort)writeIndex));
        return physicalCount - writeIndex;
    }

    /// <summary>
    /// Inserts an encoded tuple with a zero high identity lane and returns the conservative mutation bounds.<br/>
    /// This overload is for deterministic 64-bit identity fixtures that are physically stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Fixed32Scalar16InsertResult InsertWithMutationBounds(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Fixed32Scalar16MutationBounds mutationBounds)
    {
        return InsertWithMutationBounds(key0, key1, key2, key3, 0, encodedIdentityLow, allowDuplicateKeys, out mutationBounds);
    }

    /// <summary>
    /// Appends an encoded tuple when the tuple naturally belongs after the current last sorted slot.<br/>
    /// This avoids binary lower-bound search and slot movement for append-shaped shelves while leaving non-append inserts on the general path.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="count">The current shelf item count already read by the caller.</param>
    /// <returns>True when the tuple was appended; otherwise false so the caller can use the general insert path.</returns>
    private bool TryAppendInSortedOrder(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        ushort count,
        out Fixed32Scalar16MutationBounds mutationBounds)
    {
        mutationBounds = default;
        if (count > 0)
        {
            ushort lastOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, count - 1);
            int keyComparison = Fixed32Scalar16Layout.CompareItemKey(bytes, lastOffset, key0, key1, key2, key3);
            if (profile.Descending ? keyComparison < 0 : keyComparison > 0)
            {
                return false;
            }

            if (keyComparison == 0)
            {
                if (!allowDuplicateKeys)
                {
                    return false;
                }

                ulong lastIdentityHigh = Fixed32Scalar16Layout.ReadItemIdentityHigh(bytes, lastOffset);
                ulong lastIdentityLow = Fixed32Scalar16Layout.ReadItemIdentityLow(bytes, lastOffset);
                if (profile.Descending
                    ? lastIdentityHigh < encodedIdentityHigh ||
                        (lastIdentityHigh == encodedIdentityHigh && lastIdentityLow <= encodedIdentityLow)
                    : lastIdentityHigh > encodedIdentityHigh ||
                        (lastIdentityHigh == encodedIdentityHigh && lastIdentityLow >= encodedIdentityLow))
                {
                    return false;
                }
            }
        }

        int itemOffset = Fixed32Scalar16Layout.GetItemOffset(profile, count);
        Fixed32Scalar16Layout.WriteItemKey(bytes, itemOffset, key0, key1, key2, key3);
        Fixed32Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);
        Fixed32Scalar16Layout.WriteSlot(bytes, profile, count, checked((ushort)itemOffset));
        Fixed32Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, count, count, itemOffset);
        return true;
    }

    private static Fixed32Scalar16MutationBounds CreateInsertMutationBounds(
        Fixed32Scalar16Profile profile,
        int insertIndex,
        int previousCount,
        int itemOffset)
    {
        int slotOffset = Fixed32Scalar16Layout.GetSlotOffset(profile, insertIndex);
        int slotLength = checked((previousCount - insertIndex + 1) * Fixed32Scalar16Layout.SlotSize);
        return new Fixed32Scalar16MutationBounds(
            HeaderOffset: 0,
            HeaderLength: Fixed32Scalar16Layout.HeaderSize,
            SlotOffset: slotOffset,
            SlotLength: slotLength,
            ItemOffset: itemOffset,
            ItemLength: Fixed32Scalar16Layout.ItemSize);
    }

    /// <summary>
    /// Creates a read-only projection over the same shelf bytes.<br/>
    /// The returned stack-only view is useful for search and validation code that should not mutate the shelf.<br/>
    /// </summary>
    /// <returns>A read-only shelf projection.</returns>
    public Fixed32Scalar16ReadOnly AsReadOnly()
    {
        return new Fixed32Scalar16ReadOnly(bytes, profile);
    }

    /// <summary>
    /// Appends one tuple whose total tuple order is already known to follow every active tuple in this shelf.<br/>
    /// The caller owns ordering and duplicate validation; this primitive performs only capacity validation plus direct payload, slot, and count writes.<br/>
    /// It is intended for rebuilding a newly initialized shelf from a previously sorted tuple stream, where repeating the general lower-bound and last-tuple checks would be redundant.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.<br/></param>
    /// <param name="key1">The encoded sortable key part 1.<br/></param>
    /// <param name="key2">The encoded sortable key part 2.<br/></param>
    /// <param name="key3">The encoded sortable key part 3.<br/></param>
    /// <param name="encodedIdentityHigh">The encoded high identity lane.<br/></param>
    /// <param name="encodedIdentityLow">The encoded low identity lane.<br/></param>
    /// <returns><see cref="Fixed32Scalar16InsertResult.Inserted"/> when appended; otherwise <see cref="Fixed32Scalar16InsertResult.Full"/>.<br/></returns>
    internal Fixed32Scalar16InsertResult AppendKnownSorted(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow)
    {
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return Fixed32Scalar16InsertResult.Full;
        }

        int itemOffset = Fixed32Scalar16Layout.GetItemOffset(profile, count);
        Fixed32Scalar16Layout.WriteItemKey(bytes, itemOffset, key0, key1, key2, key3);
        Fixed32Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);
        Fixed32Scalar16Layout.WriteSlot(bytes, profile, count, checked((ushort)itemOffset));
        Fixed32Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        return Fixed32Scalar16InsertResult.Inserted;
    }

    /// <summary>Classifies a capacity-bound insertion before the caller attempts any structural growth.<br/>
    /// Exact tuples remain idempotent and unique-key conflicts remain conflicts even when no payload slot is free.<br/>
    /// Only the full-shelf branch calls this allocation-free binary-search path; ordinary insert and append code is unchanged.<br/></summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private Fixed32Scalar16InsertResult ClassifyFullInsert(ulong key0, ulong key1, ulong key2, ulong key3, ulong encodedIdentityHigh, ulong encodedIdentityLow, bool allowDuplicateKeys)
    {
        ushort count = ItemCount;
        Fixed32Scalar16ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            ushort existingOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Fixed32Scalar16Layout.CompareItemTuple(bytes, existingOffset, key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow);
            if (comparison == 0)
            {
                return Fixed32Scalar16InsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key0, key1, key2, key3);
            if (keyIndex < count)
            {
                ushort keyOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, keyIndex);
                if (Fixed32Scalar16Layout.CompareItemKey(bytes, keyOffset, key0, key1, key2, key3) == 0)
                {
                    return Fixed32Scalar16InsertResult.KeyConflict;
                }
            }
        }

        return Fixed32Scalar16InsertResult.Full;
    }
}
