using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable hot-path projection over a `Scalar16Scalar16` (`SS16-16`) shelf.<br/>
/// The projection can read and write shelf bytes but owns no memory and performs no file I/O.<br/>
/// </summary>
internal ref struct Scalar16Scalar16
{
    private Span<byte> bytes;
    private Scalar16Scalar16Profile profile;

    /// <summary>
    /// Creates a mutable projection over shelf bytes.<br/>
    /// The caller owns the bytes and controls when mutations are staged or committed through outer storage code.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    public Scalar16Scalar16(Span<byte> bytes)
        : this(bytes, Scalar16Scalar16Profile.Default24KiB)
    {
    }

    /// <summary>
    /// Creates a mutable projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants while the shelf header stores only local mutable state.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar16Scalar16(Span<byte> bytes, Scalar16Scalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public ushort ItemCount => Scalar16Scalar16Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid => AsReadOnly().IsValid;

    /// <summary>
    /// Initializes the shelf as an empty `Scalar16Scalar16` shelf.<br/>
    /// This clears the full shelf extent so inactive slots and payload cells have deterministic bytes for validation.<br/>
    /// </summary>
    public void Initialize()
    {
        bytes.Clear();
        Scalar16Scalar16Layout.WriteMagic(bytes, Scalar16Scalar16Layout.Magic);
        Scalar16Scalar16Layout.WriteFormatVersion(bytes, Scalar16Scalar16Layout.FormatVersion);
        Scalar16Scalar16Layout.WriteHeaderSize(bytes, Scalar16Scalar16Layout.HeaderSize);
        Scalar16Scalar16Layout.WriteFlags(bytes, 0);
        Scalar16Scalar16Layout.WriteItemCount(bytes, 0);
    }

    /// <summary>
    /// Inserts an encoded `(keyHigh, keyLow, identityHigh, identityLow)` tuple into the shelf.<br/>
    /// Non-unique mode uses tuple uniqueness and treats an already-present tuple as a physical no-op.<br/>
    /// Unique mode treats any existing equal key as a key conflict before identity tie-breaking can insert another tuple.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentityHigh">The encoded sortable identity high half.</param>
    /// <param name="encodedIdentityLow">The encoded sortable identity low half.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public Scalar16Scalar16InsertResult Insert(
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        return InsertWithMutationBounds(encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys, out _);
    }

    /// <summary>
    /// Inserts an encoded `(keyHigh, keyLow, identityHigh, identityLow)` tuple and returns the conservative byte ranges changed by a successful insert.<br/>
    /// The mutation bounds let batch publication stage known dirty shelf regions without rediscovering them by comparing full shelf images at commit.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentityHigh">The encoded sortable identity high half.</param>
    /// <param name="encodedIdentityLow">The encoded sortable identity low half.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Scalar16Scalar16InsertResult InsertWithMutationBounds(
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Scalar16Scalar16MutationBounds mutationBounds)
    {
        mutationBounds = default;
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return Scalar16Scalar16InsertResult.Full;
        }

        if (TryAppendInSortedOrder(encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys, count, out mutationBounds))
        {
            return Scalar16Scalar16InsertResult.Inserted;
        }

        Scalar16Scalar16ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            ushort existingOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Scalar16Scalar16Layout.CompareItemTuple(bytes, existingOffset, encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow);
            if (comparison == 0)
            {
                return Scalar16Scalar16InsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKeyHigh, encodedKeyLow);
            if (keyIndex < count)
            {
                ushort keyOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, keyIndex);
                if (Scalar16Scalar16Layout.CompareItemKey(bytes, keyOffset, encodedKeyHigh, encodedKeyLow) == 0)
                {
                    return Scalar16Scalar16InsertResult.KeyConflict;
                }
            }
        }

        int itemOffset = Scalar16Scalar16Layout.GetItemOffset(profile, count);
        Scalar16Scalar16Layout.WriteItemKey(bytes, itemOffset, encodedKeyHigh, encodedKeyLow);
        Scalar16Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            Span<byte> slots = bytes.Slice(
                Scalar16Scalar16Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * Scalar16Scalar16Layout.SlotSize);
            slots.CopyTo(bytes.Slice(Scalar16Scalar16Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
        }

        Scalar16Scalar16Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        Scalar16Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, insertIndex, count, itemOffset);
        return Scalar16Scalar16InsertResult.Inserted;
    }

    /// <summary>
    /// Removes a contiguous sorted-slot interval from this shelf.<br/>
    /// Surviving tuples are compacted into the dense fixed-payload prefix and their sorted slots are rewritten to those new payload cells.<br/>
    /// This is the `SS16-16` shelf-local primitive used by condition-driven delete.<br/>
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
            throw new ArgumentOutOfRangeException(nameof(startSlot), startSlot, "The SS16-16 remove start slot must be inside the physical slot table.");
        }

        if (removeCount < 0 || removeCount > count - startSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(removeCount), removeCount, "The SS16-16 remove count must fit inside the physical slot table.");
        }

        int marked = 0;
        for (int slotIndex = startSlot; slotIndex < startSlot + removeCount; slotIndex++)
        {
            if (Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex) == Scalar16Scalar16Layout.DeletedSlotOffset)
            {
                continue;
            }

            Scalar16Scalar16Layout.WriteSlot(bytes, profile, slotIndex, Scalar16Scalar16Layout.DeletedSlotOffset);
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
            if (Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex) == Scalar16Scalar16Layout.DeletedSlotOffset)
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

        byte[] compacted = new byte[physicalCount * Scalar16Scalar16Layout.ItemSize];
        int writeIndex = 0;
        for (int slotIndex = 0; slotIndex < physicalCount; slotIndex++)
        {
            ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
            if (itemOffset == Scalar16Scalar16Layout.DeletedSlotOffset)
            {
                continue;
            }

            bytes.Slice(itemOffset, Scalar16Scalar16Layout.ItemSize)
                .CopyTo(compacted.AsSpan(writeIndex * Scalar16Scalar16Layout.ItemSize, Scalar16Scalar16Layout.ItemSize));
            writeIndex++;
        }

        for (int itemIndex = 0; itemIndex < writeIndex; itemIndex++)
        {
            int itemOffset = Scalar16Scalar16Layout.GetItemOffset(profile, itemIndex);
            compacted.AsSpan(itemIndex * Scalar16Scalar16Layout.ItemSize, Scalar16Scalar16Layout.ItemSize)
                .CopyTo(bytes.Slice(itemOffset, Scalar16Scalar16Layout.ItemSize));
            Scalar16Scalar16Layout.WriteSlot(bytes, profile, itemIndex, checked((ushort)itemOffset));
        }

        Scalar16Scalar16Layout.WriteItemCount(bytes, checked((ushort)writeIndex));
        return physicalCount - writeIndex;
    }

    /// <summary>
    /// Appends an encoded tuple when the tuple naturally belongs after the current last sorted slot.<br/>
    /// This avoids binary lower-bound search and slot movement for append-shaped shelves while leaving non-append inserts on the general path.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentityHigh">The encoded sortable identity high half.</param>
    /// <param name="encodedIdentityLow">The encoded sortable identity low half.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="count">The current shelf item count already read by the caller.</param>
    /// <returns>True when the tuple was appended; otherwise false so the caller can use the general insert path.</returns>
    private bool TryAppendInSortedOrder(
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        ushort count,
        out Scalar16Scalar16MutationBounds mutationBounds)
    {
        mutationBounds = default;
        if (count > 0)
        {
            ushort lastOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, count - 1);
            int keyComparison = Scalar16Scalar16Layout.CompareItemKey(bytes, lastOffset, encodedKeyHigh, encodedKeyLow);
            if (keyComparison > 0)
            {
                return false;
            }

            if (keyComparison == 0)
            {
                if (!allowDuplicateKeys)
                {
                    return false;
                }

                ulong lastIdentityHigh = Scalar16Scalar16Layout.ReadItemIdentityHigh(bytes, lastOffset);
                ulong lastIdentityLow = Scalar16Scalar16Layout.ReadItemIdentityLow(bytes, lastOffset);
                if (lastIdentityHigh > encodedIdentityHigh || (lastIdentityHigh == encodedIdentityHigh && lastIdentityLow >= encodedIdentityLow))
                {
                    return false;
                }
            }
        }

        int itemOffset = Scalar16Scalar16Layout.GetItemOffset(profile, count);
        Scalar16Scalar16Layout.WriteItemKey(bytes, itemOffset, encodedKeyHigh, encodedKeyLow);
        Scalar16Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);
        Scalar16Scalar16Layout.WriteSlot(bytes, profile, count, checked((ushort)itemOffset));
        Scalar16Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, count, count, itemOffset);
        return true;
    }

    private static Scalar16Scalar16MutationBounds CreateInsertMutationBounds(
        Scalar16Scalar16Profile profile,
        int insertIndex,
        int previousCount,
        int itemOffset)
    {
        int slotOffset = Scalar16Scalar16Layout.GetSlotOffset(profile, insertIndex);
        int slotLength = checked((previousCount - insertIndex + 1) * Scalar16Scalar16Layout.SlotSize);
        return new Scalar16Scalar16MutationBounds(
            HeaderOffset: 0,
            HeaderLength: Scalar16Scalar16Layout.HeaderSize,
            SlotOffset: slotOffset,
            SlotLength: slotLength,
            ItemOffset: itemOffset,
            ItemLength: Scalar16Scalar16Layout.ItemSize);
    }

    /// <summary>
    /// Creates a read-only projection over the same shelf bytes.<br/>
    /// The returned stack-only view is useful for search and validation code that should not mutate the shelf.<br/>
    /// </summary>
    /// <returns>A read-only shelf projection.</returns>
    public Scalar16Scalar16ReadOnly AsReadOnly()
    {
        return new Scalar16Scalar16ReadOnly(bytes, profile);
    }
}
