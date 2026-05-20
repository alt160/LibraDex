using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable hot-path projection over a `Fixed32Scalar8` (`FS32-8`) shelf.<br/>
/// The projection can read and write shelf bytes but owns no memory and performs no file I/O.<br/>
/// </summary>
internal ref struct Fixed32Scalar8
{
    private Span<byte> bytes;
    private Fixed32Scalar8Profile profile;

    /// <summary>
    /// Creates a mutable projection over shelf bytes.<br/>
    /// The caller owns the bytes and controls when mutations are staged or committed through outer storage code.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    public Fixed32Scalar8(Span<byte> bytes)
        : this(bytes, Fixed32Scalar8Profile.Default64KiB)
    {
    }

    /// <summary>
    /// Creates a mutable projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants while the shelf header stores only local mutable state.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Fixed32Scalar8(Span<byte> bytes, Fixed32Scalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public ushort ItemCount => Fixed32Scalar8Layout.ReadItemCount(bytes);

    public bool IsValid => AsReadOnly().IsValid;

    /// <summary>
    /// Initializes the shelf as an empty `Fixed32Scalar8` shelf.<br/>
    /// This clears the full shelf extent so inactive slots and payload cells have deterministic bytes for validation.<br/>
    /// </summary>
    public void Initialize()
    {
        bytes.Clear();
        Fixed32Scalar8Layout.WriteMagic(bytes, Fixed32Scalar8Layout.Magic);
        Fixed32Scalar8Layout.WriteFormatVersion(bytes, Fixed32Scalar8Layout.FormatVersion);
        Fixed32Scalar8Layout.WriteHeaderSize(bytes, Fixed32Scalar8Layout.HeaderSize);
        Fixed32Scalar8Layout.WriteFlags(bytes, 0);
        Fixed32Scalar8Layout.WriteItemCount(bytes, 0);
    }

    /// <summary>
    /// Inserts an encoded `(key0, key1, key2, key3, identity)` tuple into the shelf.<br/>
    /// Non-unique mode uses tuple uniqueness and treats an already-present tuple as a physical no-op.<br/>
    /// Unique mode treats any existing equal key as a key conflict before identity tie-breaking can insert another tuple.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public Fixed32Scalar8InsertResult Insert(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        return InsertWithMutationBounds(key0, key1, key2, key3, encodedIdentity, allowDuplicateKeys, out _);
    }

    /// <summary>
    /// Inserts an encoded `(key0, key1, key2, key3, identity)` tuple and returns the conservative byte ranges changed by a successful insert.<br/>
    /// The mutation bounds let batch publication stage known dirty shelf regions without rediscovering them by comparing full shelf images at commit.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Fixed32Scalar8InsertResult InsertWithMutationBounds(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out Fixed32Scalar8MutationBounds mutationBounds)
    {
        mutationBounds = default;
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return Fixed32Scalar8InsertResult.Full;
        }

        if (TryAppendInSortedOrder(key0, key1, key2, key3, encodedIdentity, allowDuplicateKeys, count, out mutationBounds))
        {
            return Fixed32Scalar8InsertResult.Inserted;
        }

        Fixed32Scalar8ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(key0, key1, key2, key3, encodedIdentity);

        if (insertIndex < count)
        {
            ushort existingOffset = Fixed32Scalar8Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Fixed32Scalar8Layout.CompareItemTuple(bytes, existingOffset, key0, key1, key2, key3, encodedIdentity);
            if (comparison == 0)
            {
                return Fixed32Scalar8InsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key0, key1, key2, key3);
            if (keyIndex < count)
            {
                ushort keyOffset = Fixed32Scalar8Layout.ReadSlot(bytes, profile, keyIndex);
                if (Fixed32Scalar8Layout.CompareItemKey(bytes, keyOffset, key0, key1, key2, key3) == 0)
                {
                    return Fixed32Scalar8InsertResult.KeyConflict;
                }
            }
        }

        int itemOffset = Fixed32Scalar8Layout.GetItemOffset(profile, count);
        Fixed32Scalar8Layout.WriteItemKey(bytes, itemOffset, key0, key1, key2, key3);
        Fixed32Scalar8Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentity);

        if (insertIndex < count)
        {
            Span<byte> slots = bytes.Slice(
                Fixed32Scalar8Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * Fixed32Scalar8Layout.SlotSize);
            slots.CopyTo(bytes.Slice(Fixed32Scalar8Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
        }

        Fixed32Scalar8Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        Fixed32Scalar8Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, insertIndex, count, itemOffset);
        return Fixed32Scalar8InsertResult.Inserted;
    }

    /// <summary>
    /// Appends an encoded tuple when the tuple naturally belongs after the current last sorted slot.<br/>
    /// This avoids binary lower-bound search and slot movement for append-shaped shelves while leaving non-append inserts on the general path.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="count">The current shelf item count already read by the caller.</param>
    /// <returns>True when the tuple was appended; otherwise false so the caller can use the general insert path.</returns>
    private bool TryAppendInSortedOrder(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        ushort count,
        out Fixed32Scalar8MutationBounds mutationBounds)
    {
        mutationBounds = default;
        if (count > 0)
        {
            ushort lastOffset = Fixed32Scalar8Layout.ReadSlot(bytes, profile, count - 1);
            int keyComparison = Fixed32Scalar8Layout.CompareItemKey(bytes, lastOffset, key0, key1, key2, key3);
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

                ulong lastIdentity = Fixed32Scalar8Layout.ReadItemIdentity(bytes, lastOffset);
                if (lastIdentity >= encodedIdentity)
                {
                    return false;
                }
            }
        }

        int itemOffset = Fixed32Scalar8Layout.GetItemOffset(profile, count);
        Fixed32Scalar8Layout.WriteItemKey(bytes, itemOffset, key0, key1, key2, key3);
        Fixed32Scalar8Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentity);
        Fixed32Scalar8Layout.WriteSlot(bytes, profile, count, checked((ushort)itemOffset));
        Fixed32Scalar8Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, count, count, itemOffset);
        return true;
    }

    private static Fixed32Scalar8MutationBounds CreateInsertMutationBounds(
        Fixed32Scalar8Profile profile,
        int insertIndex,
        int previousCount,
        int itemOffset)
    {
        int slotOffset = Fixed32Scalar8Layout.GetSlotOffset(profile, insertIndex);
        int slotLength = checked((previousCount - insertIndex + 1) * Fixed32Scalar8Layout.SlotSize);
        return new Fixed32Scalar8MutationBounds(
            HeaderOffset: 0,
            HeaderLength: Fixed32Scalar8Layout.HeaderSize,
            SlotOffset: slotOffset,
            SlotLength: slotLength,
            ItemOffset: itemOffset,
            ItemLength: Fixed32Scalar8Layout.ItemSize);
    }

    /// <summary>
    /// Creates a read-only projection over the same shelf bytes.<br/>
    /// The returned stack-only view is useful for search and validation code that should not mutate the shelf.<br/>
    /// </summary>
    /// <returns>A read-only shelf projection.</returns>
    public Fixed32Scalar8ReadOnly AsReadOnly()
    {
        return new Fixed32Scalar8ReadOnly(bytes, profile);
    }
}
