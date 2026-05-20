using LibraDex.Layouts;
namespace LibraDex.Views;

/// <summary>
/// Provides a mutable hot-path projection over a `Scalar8Scalar16` (`SS8-16`) shelf.<br/>
/// The projection can read and write shelf bytes but owns no memory and performs no file I/O.<br/>
/// </summary>
internal ref struct Scalar8Scalar16
{
    private Span<byte> bytes;
    private Scalar8Scalar16Profile profile;

    /// <summary>
    /// Creates a mutable projection over shelf bytes.<br/>
    /// The caller owns the bytes and controls when mutations are staged or committed through outer storage code.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    public Scalar8Scalar16(Span<byte> bytes)
        : this(bytes, Scalar8Scalar16Profile.Default32KiB)
    {
    }

    /// <summary>
    /// Creates a mutable projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants while the shelf header stores only local mutable state.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar8Scalar16(Span<byte> bytes, Scalar8Scalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public ushort ItemCount => Scalar8Scalar16Layout.ReadItemCount(bytes);

    public bool IsValid => AsReadOnly().IsValid;

    /// <summary>
    /// Initializes the shelf as an empty `Scalar8Scalar16` shelf.<br/>
    /// This clears the full shelf extent so inactive slots and payload cells have deterministic bytes for validation.<br/>
    /// </summary>
    public void Initialize()
    {
        bytes.Clear();
        Scalar8Scalar16Layout.WriteMagic(bytes, Scalar8Scalar16Layout.Magic);
        Scalar8Scalar16Layout.WriteFormatVersion(bytes, Scalar8Scalar16Layout.FormatVersion);
        Scalar8Scalar16Layout.WriteHeaderSize(bytes, Scalar8Scalar16Layout.HeaderSize);
        Scalar8Scalar16Layout.WriteFlags(bytes, 0);
        Scalar8Scalar16Layout.WriteItemCount(bytes, 0);
    }

    /// <summary>
    /// Inserts an encoded `(key, identity)` tuple into the shelf.<br/>
    /// Non-unique mode uses tuple uniqueness and treats an already-present tuple as a physical no-op.<br/>
    /// Unique mode treats any existing equal key as a key conflict before identity tie-breaking can insert another tuple.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentityHigh">The encoded high identity half.</param>
    /// <param name="encodedIdentityLow">The encoded low identity half.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public Scalar8Scalar16InsertResult Insert(ulong encodedKey, ulong encodedIdentityHigh, ulong encodedIdentityLow, bool allowDuplicateKeys)
    {
        return InsertWithMutationBounds(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys, out _);
    }

    /// <summary>
    /// Inserts an encoded `(key, identity)` tuple and returns the conservative byte ranges changed by a successful insert.<br/>
    /// The mutation bounds let batch publication stage known dirty shelf regions without rediscovering them by comparing full shelf images at commit.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentityHigh">The encoded high identity half.</param>
    /// <param name="encodedIdentityLow">The encoded low identity half.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the result is `Inserted`.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    internal Scalar8Scalar16InsertResult InsertWithMutationBounds(
        ulong encodedKey,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Scalar8Scalar16MutationBounds mutationBounds)
    {
        mutationBounds = default;
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return Scalar8Scalar16InsertResult.Full;
        }

        if (TryAppendInSortedOrder(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys, count, out mutationBounds))
        {
            return Scalar8Scalar16InsertResult.Inserted;
        }

        Scalar8Scalar16ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(encodedKey, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            ushort existingOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = Scalar8Scalar16Layout.CompareItemTuple(bytes, existingOffset, encodedKey, encodedIdentityHigh, encodedIdentityLow);
            if (comparison == 0)
            {
                return Scalar8Scalar16InsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(encodedKey);
            if (keyIndex < count)
            {
                ushort keyOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, keyIndex);
                if (Scalar8Scalar16Layout.ReadItemKey(bytes, keyOffset) == encodedKey)
                {
                    return Scalar8Scalar16InsertResult.KeyConflict;
                }
            }
        }

        int itemOffset = Scalar8Scalar16Layout.GetItemOffset(profile, count);
        Scalar8Scalar16Layout.WriteItemKey(bytes, itemOffset, encodedKey);
        Scalar8Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);

        if (insertIndex < count)
        {
            Span<byte> slots = bytes.Slice(
                Scalar8Scalar16Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * Scalar8Scalar16Layout.SlotSize);
            slots.CopyTo(bytes.Slice(Scalar8Scalar16Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
        }

        Scalar8Scalar16Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        Scalar8Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, insertIndex, count, itemOffset);
        return Scalar8Scalar16InsertResult.Inserted;
    }

    /// <summary>
    /// Appends an encoded tuple when the tuple naturally belongs after the current last sorted slot.<br/>
    /// This avoids binary lower-bound search and slot movement for append-shaped shelves while leaving non-append inserts on the existing general path.<br/>
    /// The method returns false for exact duplicates, unique-key conflicts, and out-of-order tuples so the caller can preserve the normal result semantics.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key to insert.</param>
    /// <param name="encodedIdentityHigh">The encoded high identity half to insert.</param>
    /// <param name="encodedIdentityLow">The encoded low identity half to insert.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <param name="count">The current shelf item count already read by the caller.</param>
    /// <param name="mutationBounds">The conservative shelf-relative changed ranges when the tuple was appended.</param>
    /// <returns>True when the tuple was appended; otherwise false so the caller can use the general insert path.</returns>
    private bool TryAppendInSortedOrder(
        ulong encodedKey,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        ushort count,
        out Scalar8Scalar16MutationBounds mutationBounds)
    {
        mutationBounds = default;
        if (count > 0)
        {
            ushort lastOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, count - 1);
            ulong lastKey = Scalar8Scalar16Layout.ReadItemKey(bytes, lastOffset);
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

                ulong lastIdentityHigh = Scalar8Scalar16Layout.ReadItemIdentityHigh(bytes, lastOffset);
                ulong lastIdentityLow = Scalar8Scalar16Layout.ReadItemIdentityLow(bytes, lastOffset);
                if (lastIdentityHigh > encodedIdentityHigh || (lastIdentityHigh == encodedIdentityHigh && lastIdentityLow >= encodedIdentityLow))
                {
                    return false;
                }
            }
        }

        int itemOffset = Scalar8Scalar16Layout.GetItemOffset(profile, count);
        Scalar8Scalar16Layout.WriteItemKey(bytes, itemOffset, encodedKey);
        Scalar8Scalar16Layout.WriteItemIdentity(bytes, itemOffset, encodedIdentityHigh, encodedIdentityLow);
        Scalar8Scalar16Layout.WriteSlot(bytes, profile, count, checked((ushort)itemOffset));
        Scalar8Scalar16Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        mutationBounds = CreateInsertMutationBounds(profile, count, count, itemOffset);
        return true;
    }

    private static Scalar8Scalar16MutationBounds CreateInsertMutationBounds(
        Scalar8Scalar16Profile profile,
        int insertIndex,
        int previousCount,
        int itemOffset)
    {
        int slotOffset = Scalar8Scalar16Layout.GetSlotOffset(profile, insertIndex);
        int slotLength = checked((previousCount - insertIndex + 1) * Scalar8Scalar16Layout.SlotSize);
        return new Scalar8Scalar16MutationBounds(
            HeaderOffset: 0,
            HeaderLength: Scalar8Scalar16Layout.HeaderSize,
            SlotOffset: slotOffset,
            SlotLength: slotLength,
            ItemOffset: itemOffset,
            ItemLength: Scalar8Scalar16Layout.ItemSize);
    }

    /// <summary>
    /// Creates a read-only projection over the same shelf bytes.<br/>
    /// The returned stack-only view is useful for search and validation code that should not mutate the shelf.<br/>
    /// </summary>
    /// <returns>A read-only shelf projection.</returns>
    public Scalar8Scalar16ReadOnly AsReadOnly()
    {
        return new Scalar8Scalar16ReadOnly(bytes, profile);
    }
}
