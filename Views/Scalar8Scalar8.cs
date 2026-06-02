using LibraDex.Layouts;
using System.Diagnostics;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable hot-path projection over a `Scalar8Scalar8` (`SS8-8`) shelf.<br/>
/// The projection can read and write shelf bytes but owns no memory and performs no file I/O.<br/>
/// </summary>
internal ref struct Scalar8Scalar8
{
    private Span<byte> bytes;
    private Scalar8Scalar8Profile profile;

    /// <summary>
    /// Creates a mutable projection over shelf bytes.<br/>
    /// The caller owns the bytes and controls when mutations are staged or committed through outer storage code.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    public Scalar8Scalar8(Span<byte> bytes)
        : this(bytes, Scalar8Scalar8Profile.Default32KiB)
    {
    }

    /// <summary>
    /// Creates a mutable projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants while the shelf header stores only local mutable state.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar8Scalar8(Span<byte> bytes, Scalar8Scalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public ushort ItemCount => Scalar8Scalar8Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid => AsReadOnly().IsValid;

    /// <summary>
    /// Initializes the shelf as an empty `Scalar8Scalar8` shelf.<br/>
    /// This clears the full shelf extent so inactive slots and payload cells have deterministic bytes for validation.<br/>
    /// </summary>
    public void Initialize()
    {
        bytes.Clear();
        Scalar8Scalar8Layout.WriteMagic(bytes, Scalar8Scalar8Layout.Magic);
        Scalar8Scalar8Layout.WriteFormatVersion(bytes, Scalar8Scalar8Layout.FormatVersion);
        Scalar8Scalar8Layout.WriteHeaderSize(bytes, Scalar8Scalar8Layout.HeaderSize);
        Scalar8Scalar8Layout.WriteFlags(bytes, 0);
        Scalar8Scalar8Layout.WriteItemCount(bytes, 0);
    }

    /// <summary>
    /// Inserts an encoded `(key, identity)` tuple into the shelf.<br/>
    /// Non-unique mode uses tuple uniqueness and treats an already-present tuple as a physical no-op.<br/>
    /// Unique mode treats any existing equal key as a key conflict before identity tie-breaking can insert another tuple.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public Scalar8Scalar8InsertResult Insert(ulong encodedKey, ulong encodedIdentity, bool allowDuplicateKeys)
    {
        return InsertWithMutationBounds(encodedKey, encodedIdentity, allowDuplicateKeys, out _);
    }

    /// <summary>
    /// Inserts an encoded `(key, identity)` tuple and returns the conservative byte ranges changed by a successful insert.<br/>
    /// The mutation bounds let batch publication stage known dirty shelf regions without rediscovering them by comparing full shelf images at commit.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Scalar8Scalar8InsertResult InsertWithMutationBounds(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out Scalar8Scalar8MutationBounds mutationBounds)
    {
        mutationBounds = default;
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return Scalar8Scalar8InsertResult.Full;
        }

        if (TryAppendInSortedOrder(encodedKey, encodedIdentity, allowDuplicateKeys, count, out mutationBounds))
        {
            return Scalar8Scalar8InsertResult.Inserted;
        }

        Scalar8Scalar8ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(encodedKey, encodedIdentity);

        if (insertIndex < count)
        {
            ushort existingOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Scalar8Scalar8Layout.CompareItemTuple(bytes, existingOffset, encodedKey, encodedIdentity);
            if (comparison == 0)
            {
                return Scalar8Scalar8InsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKey);
            if (keyIndex < count)
            {
                ushort keyOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, keyIndex);
                if (Scalar8Scalar8Layout.ReadItemKey(bytes, keyOffset) == encodedKey)
                {
                    return Scalar8Scalar8InsertResult.KeyConflict;
                }
            }
        }

        int itemOffset = Scalar8Scalar8Layout.GetItemOffset(profile, count);
        Scalar8Scalar8Layout.WriteItemKey(bytes, itemOffset, encodedKey);
        Scalar8Scalar8Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentity);

        if (insertIndex < count)
        {
            Span<byte> slots = bytes.Slice(
                Scalar8Scalar8Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * Scalar8Scalar8Layout.SlotSize);
            slots.CopyTo(bytes.Slice(Scalar8Scalar8Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
        }

        Scalar8Scalar8Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        Scalar8Scalar8Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, insertIndex, count, itemOffset);
        return Scalar8Scalar8InsertResult.Inserted;
    }

    /// <summary>
    /// Inserts an encoded `(key, identity)` tuple while attributing shelf-local CPU work to smaller phases.<br/>
    /// This method intentionally mirrors `Insert` so the harness can measure lower-bound search, duplicate checks, slot movement, and payload/header writes without changing public behavior.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="telemetry">The batch insert attribution accumulator that receives shelf-local tick counters.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Scalar8Scalar8InsertResult InsertWithAttribution(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        ref Scalar8Scalar8BatchInsertAttributionTelemetry telemetry)
    {
        return InsertWithAttributionAndMutationBounds(encodedKey, encodedIdentity, allowDuplicateKeys, ref telemetry, out _);
    }

    /// <summary>
    /// Inserts an encoded `(key, identity)` tuple while attributing shelf-local CPU work and returning changed byte ranges.<br/>
    /// This diagnostic path mirrors `InsertWithAttribution` while allowing the batch to keep the same dirty-region tracking used by regular cached inserts.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="telemetry">The batch insert attribution accumulator that receives shelf-local tick counters.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Scalar8Scalar8InsertResult InsertWithAttributionAndMutationBounds(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        ref Scalar8Scalar8BatchInsertAttributionTelemetry telemetry,
        out Scalar8Scalar8MutationBounds mutationBounds)
    {
        mutationBounds = default;
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return Scalar8Scalar8InsertResult.Full;
        }

        long appendStart = Stopwatch.GetTimestamp();
        if (TryAppendInSortedOrder(encodedKey, encodedIdentity, allowDuplicateKeys, count, out mutationBounds))
        {
            telemetry.ShelfAppendFastPathTicks += Stopwatch.GetTimestamp() - appendStart;
            telemetry.ShelfAppendFastPathCount++;
            return Scalar8Scalar8InsertResult.Inserted;
        }

        Scalar8Scalar8ReadOnly readOnly = AsReadOnly();
        long lowerBoundStart = Stopwatch.GetTimestamp();
        int insertIndex = readOnly.LowerBound(encodedKey, encodedIdentity);
        telemetry.ShelfLowerBoundTicks += Stopwatch.GetTimestamp() - lowerBoundStart;

        long duplicateStart = Stopwatch.GetTimestamp();
        if (insertIndex < count)
        {
            ushort existingOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Scalar8Scalar8Layout.CompareItemTuple(bytes, existingOffset, encodedKey, encodedIdentity);
            if (comparison == 0)
            {
                telemetry.ShelfDuplicateCheckTicks += Stopwatch.GetTimestamp() - duplicateStart;
                return Scalar8Scalar8InsertResult.AlreadyPresent;
            }
        }

        telemetry.ShelfDuplicateCheckTicks += Stopwatch.GetTimestamp() - duplicateStart;

        if (!allowDuplicateKeys)
        {
            long uniqueStart = Stopwatch.GetTimestamp();
            int keyIndex = readOnly.LowerBoundKey(encodedKey);
            if (keyIndex < count)
            {
                ushort keyOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, keyIndex);
                if (Scalar8Scalar8Layout.ReadItemKey(bytes, keyOffset) == encodedKey)
                {
                    telemetry.ShelfUniqueCheckTicks += Stopwatch.GetTimestamp() - uniqueStart;
                    return Scalar8Scalar8InsertResult.KeyConflict;
                }
            }

            telemetry.ShelfUniqueCheckTicks += Stopwatch.GetTimestamp() - uniqueStart;
        }

        int itemOffset = Scalar8Scalar8Layout.GetItemOffset(profile, count);
        long payloadStart = Stopwatch.GetTimestamp();
        Scalar8Scalar8Layout.WriteItemKey(bytes, itemOffset, encodedKey);
        Scalar8Scalar8Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentity);
        telemetry.ShelfPayloadWriteTicks += Stopwatch.GetTimestamp() - payloadStart;

        if (insertIndex < count)
        {
            long slotMoveStart = Stopwatch.GetTimestamp();
            Span<byte> slots = bytes.Slice(
                Scalar8Scalar8Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * Scalar8Scalar8Layout.SlotSize);
            slots.CopyTo(bytes.Slice(Scalar8Scalar8Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
            telemetry.ShelfSlotMoveTicks += Stopwatch.GetTimestamp() - slotMoveStart;
        }

        long headerStart = Stopwatch.GetTimestamp();
        Scalar8Scalar8Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        Scalar8Scalar8Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        telemetry.ShelfHeaderWriteTicks += Stopwatch.GetTimestamp() - headerStart;
        mutationBounds = CreateInsertMutationBounds(profile, insertIndex, count, itemOffset);
        return Scalar8Scalar8InsertResult.Inserted;
    }

    /// <summary>
    /// Removes a contiguous sorted-slot interval from this shelf and immediately normalizes the shelf image.<br/>
    /// This preserves the existing compact public-delete behavior while sharing the same tombstone marker path used by batch-local delete staging.<br/>
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
            throw new ArgumentOutOfRangeException(nameof(startSlot), startSlot, "The SS8-8 remove start slot must be inside the physical slot table.");
        }

        if (removeCount < 0 || removeCount > count - startSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(removeCount), removeCount, "The SS8-8 remove count must fit inside the physical slot table.");
        }

        int marked = 0;
        for (int slotIndex = startSlot; slotIndex < startSlot + removeCount; slotIndex++)
        {
            if (Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex) == Scalar8Scalar8Layout.DeletedSlotOffset)
            {
                continue;
            }

            Scalar8Scalar8Layout.WriteSlot(bytes, profile, slotIndex, Scalar8Scalar8Layout.DeletedSlotOffset);
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
            if (Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex) == Scalar8Scalar8Layout.DeletedSlotOffset)
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Compacts a shelf that may contain deleted-slot sentinels back into dense sorted-slot and payload prefixes.<br/>
    /// Persisted shelves stay compact in this slice; batch-local shelves can keep tombstones until commit by calling this method only at publication boundaries.<br/>
    /// </summary>
    /// <param name="physicalCount">The physical slot count to scan for live entries.</param>
    /// <returns>The number of tombstoned slots removed during normalization.</returns>
    private int NormalizeDeletedSlots(ushort physicalCount)
    {
        byte[] compacted = new byte[physicalCount * Scalar8Scalar8Layout.ItemSize];
        int writeIndex = 0;
        for (int slotIndex = 0; slotIndex < physicalCount; slotIndex++)
        {
            ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
            if (itemOffset == Scalar8Scalar8Layout.DeletedSlotOffset)
            {
                continue;
            }

            bytes.Slice(itemOffset, Scalar8Scalar8Layout.ItemSize)
                .CopyTo(compacted.AsSpan(writeIndex * Scalar8Scalar8Layout.ItemSize, Scalar8Scalar8Layout.ItemSize));
            writeIndex++;
        }

        for (int itemIndex = 0; itemIndex < writeIndex; itemIndex++)
        {
            int itemOffset = Scalar8Scalar8Layout.GetItemOffset(profile, itemIndex);
            compacted.AsSpan(itemIndex * Scalar8Scalar8Layout.ItemSize, Scalar8Scalar8Layout.ItemSize)
                .CopyTo(bytes.Slice(itemOffset, Scalar8Scalar8Layout.ItemSize));
            Scalar8Scalar8Layout.WriteSlot(bytes, profile, itemIndex, checked((ushort)itemOffset));
        }

        Scalar8Scalar8Layout.WriteItemCount(bytes, checked((ushort)writeIndex));
        return physicalCount - writeIndex;
    }

    /// <summary>
    /// Appends an encoded tuple when the tuple naturally belongs after the current last sorted slot.<br/>
    /// This avoids binary lower-bound search and slot movement for append-shaped shelves while leaving non-append inserts on the existing general path.<br/>
    /// The method returns false for exact duplicates, unique-key conflicts, and out-of-order tuples so the caller can preserve the normal result semantics.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key to insert.</param>
    /// <param name="encodedIdentity">The encoded sortable identity to insert.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="count">The current shelf item count already read by the caller.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the tuple was appended.</param>
    /// <returns>True when the tuple was appended; otherwise false so the caller can use the general insert path.</returns>
    private bool TryAppendInSortedOrder(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        ushort count,
        out Scalar8Scalar8MutationBounds mutationBounds)
    {
        mutationBounds = default;
        if (count > 0)
        {
            ushort lastOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, count - 1);
            ulong lastKey = Scalar8Scalar8Layout.ReadItemKey(bytes, lastOffset);
            if (lastKey > encodedKey)
            {
                return false;
            }

            if (lastKey == encodedKey)
            {
                if (!allowDuplicateKeys)
                {
                    return false;
                }

                ulong lastIdentity = Scalar8Scalar8Layout.ReadItemIdentity(bytes, lastOffset);
                if (lastIdentity >= encodedIdentity)
                {
                    return false;
                }
            }
        }

        int itemOffset = Scalar8Scalar8Layout.GetItemOffset(profile, count);
        Scalar8Scalar8Layout.WriteItemKey(bytes, itemOffset, encodedKey);
        Scalar8Scalar8Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentity);
        Scalar8Scalar8Layout.WriteSlot(bytes, profile, count, checked((ushort)itemOffset));
        Scalar8Scalar8Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, count, count, itemOffset);
        return true;
    }

    private static Scalar8Scalar8MutationBounds CreateInsertMutationBounds(
        Scalar8Scalar8Profile profile,
        int insertIndex,
        int previousCount,
        int itemOffset)
    {
        int slotOffset = Scalar8Scalar8Layout.GetSlotOffset(profile, insertIndex);
        int slotLength = checked((previousCount - insertIndex + 1) * Scalar8Scalar8Layout.SlotSize);
        return new Scalar8Scalar8MutationBounds(
            HeaderOffset: 0,
            HeaderLength: Scalar8Scalar8Layout.HeaderSize,
            SlotOffset: slotOffset,
            SlotLength: slotLength,
            ItemOffset: itemOffset,
            ItemLength: Scalar8Scalar8Layout.ItemSize);
    }

    /// <summary>
    /// Creates a read-only projection over the same shelf bytes.<br/>
    /// The returned stack-only view is useful for search and validation code that should not mutate the shelf.<br/>
    /// </summary>
    /// <returns>A read-only shelf projection.</returns>
    public Scalar8Scalar8ReadOnly AsReadOnly()
    {
        return new Scalar8Scalar8ReadOnly(bytes, profile);
    }
}
