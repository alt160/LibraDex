using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable hot-path projection over a programmable fixed-key `FixedNScalar8` (`FSN-8`) shelf.<br/>
/// The projection can read and write shelf bytes but owns no memory and performs no file I/O.<br/>
/// </summary>
internal ref struct FixedNScalar8
{
    private Span<byte> bytes;
    private FixedNScalar8Profile profile;

    /// <summary>
    /// Creates a mutable projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies the fixed encoded key width and extent-derived region boundaries.<br/>
    /// </summary>
    /// <param name="bytes">The writable shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public FixedNScalar8(Span<byte> bytes, FixedNScalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public ushort ItemCount => FixedNScalar8Layout.ReadItemCount(bytes);

    public bool IsValid => AsReadOnly().IsValid;

    /// <summary>
    /// Initializes the shelf as an empty `FixedNScalar8` shelf.<br/>
    /// The fixed key width is persisted in the shelf header so reopen validation can reject mismatched profiles.<br/>
    /// </summary>
    public void Initialize()
    {
        bytes.Clear();
        FixedNScalar8Layout.WriteMagic(bytes, FixedNScalar8Layout.Magic);
        FixedNScalar8Layout.WriteFormatVersion(bytes, FixedNScalar8Layout.FormatVersion);
        FixedNScalar8Layout.WriteHeaderSize(bytes, FixedNScalar8Layout.HeaderSize);
        FixedNScalar8Layout.WriteFlags(bytes, 0);
        FixedNScalar8Layout.WriteItemCount(bytes, 0);
        FixedNScalar8Layout.WriteKeySize(bytes, checked((ushort)profile.KeySize));
    }

    /// <summary>
    /// Inserts an encoded fixed-width key and scalar-8 identity tuple into the shelf.<br/>
    /// Non-unique mode uses tuple uniqueness; unique mode rejects a second identity for an existing key.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.</param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The structural result of the insert attempt.</returns>
    public FixedNScalarInsertResult Insert(ReadOnlySpan<byte> key, ulong encodedIdentity, bool allowDuplicateKeys)
    {
        ValidateKey(key);
        ushort count = ItemCount;
        if (count >= profile.MaxItemCount)
        {
            return FixedNScalarInsertResult.Full;
        }

        FixedNScalar8ReadOnly readOnly = AsReadOnly();
        int insertIndex = readOnly.LowerBound(key, encodedIdentity);
        if (insertIndex < count)
        {
            ushort existingOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, insertIndex);
            int comparison = FixedNScalar8Layout.CompareItemTuple(bytes, existingOffset, profile, key, encodedIdentity);
            if (comparison == 0)
            {
                return FixedNScalarInsertResult.AlreadyPresent;
            }
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = readOnly.LowerBoundKey(key);
            if (keyIndex < count)
            {
                ushort keyOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, keyIndex);
                if (FixedNScalar8Layout.CompareItemKey(bytes, keyOffset, profile, key) == 0)
                {
                    return FixedNScalarInsertResult.KeyConflict;
                }
            }
        }

        int itemOffset = FixedNScalar8Layout.GetItemOffset(profile, count);
        FixedNScalar8Layout.WriteItem(bytes, itemOffset, profile, key, encodedIdentity);
        if (insertIndex < count)
        {
            Span<byte> slots = bytes.Slice(
                FixedNScalar8Layout.GetSlotOffset(profile, insertIndex),
                (count - insertIndex) * FixedNScalar8Layout.SlotSize);
            slots.CopyTo(bytes.Slice(FixedNScalar8Layout.GetSlotOffset(profile, insertIndex + 1), slots.Length));
        }

        FixedNScalar8Layout.WriteSlot(bytes, profile, insertIndex, checked((ushort)itemOffset));
        FixedNScalar8Layout.WriteItemCount(bytes, checked((ushort)(count + 1)));
        return FixedNScalarInsertResult.Inserted;
    }

    private FixedNScalar8ReadOnly AsReadOnly()
    {
        return new FixedNScalar8ReadOnly(bytes, profile);
    }

    private void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != profile.KeySize)
        {
            throw new ArgumentException("FSN-8 encoded key length must match the profile key size.", nameof(key));
        }
    }
}

/// <summary>
/// Provides a read-only hot-path projection over a programmable fixed-key `FixedNScalar8` (`FSN-8`) shelf.<br/>
/// The projection owns no bytes, performs no allocation, and compares persisted key bytes directly in sortable order.<br/>
/// </summary>
internal readonly ref struct FixedNScalar8ReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly FixedNScalar8Profile profile;

    /// <summary>
    /// Creates a read-only projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The caller owns the bytes and is responsible for ensuring their lifetime covers this stack-only view.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public FixedNScalar8ReadOnly(ReadOnlySpan<byte> bytes, FixedNScalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public uint Magic => FixedNScalar8Layout.ReadMagic(bytes);

    public ushort FormatVersion => FixedNScalar8Layout.ReadFormatVersion(bytes);

    public ushort HeaderSize => FixedNScalar8Layout.ReadHeaderSize(bytes);

    public ushort ItemCount => FixedNScalar8Layout.ReadItemCount(bytes);

    public ushort KeySize => FixedNScalar8Layout.ReadKeySize(bytes);

    public bool IsValid =>
        bytes.Length >= profile.ShelfExtentSize &&
        Magic == FixedNScalar8Layout.Magic &&
        FormatVersion == FixedNScalar8Layout.FormatVersion &&
        HeaderSize == FixedNScalar8Layout.HeaderSize &&
        KeySize == profile.KeySize &&
        ItemCount <= profile.MaxItemCount;

    /// <summary>
    /// Finds the first sorted slot whose tuple is greater than or equal to the supplied encoded tuple.<br/>
    /// This lower-bound search compares fixed key bytes first, then scalar-8 identity bytes as the duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.</param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        int low = 0;
        int high = ItemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, middle);
            int comparison = FixedNScalar8Layout.CompareItemTuple(bytes, itemOffset, profile, key, encodedIdentity);
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

    /// <summary>
    /// Finds the first sorted slot whose key is greater than or equal to the supplied encoded key.<br/>
    /// This is the range-scan entry point for key-only lower bounds.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.</param>
    /// <returns>The first slot index at or after the key.</returns>
    public int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = ItemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, middle);
            int comparison = FixedNScalar8Layout.CompareItemKey(bytes, itemOffset, profile, key);
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

    /// <summary>
    /// Reads the encoded identity for an item in sorted slot order.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable scalar-8 identity.</returns>
    public ulong ReadIdentityAt(int slotIndex)
    {
        ushort itemOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return FixedNScalar8Layout.ReadItemIdentity(bytes, itemOffset, profile);
    }

    /// <summary>
    /// Copies the encoded fixed-width key for an item in sorted slot order.<br/>
    /// This supports routed split code that needs to repartition existing shelf contents without decoding developer-facing values.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <param name="destination">The destination span that receives the encoded key bytes.</param>
    public void CopyKeyAt(int slotIndex, Span<byte> destination)
    {
        if (destination.Length != profile.KeySize)
        {
            throw new ArgumentException("FSN-8 key copy destination length must match the profile key size.", nameof(destination));
        }

        ushort itemOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, slotIndex);
        FixedNScalar8Layout.ReadItemKey(bytes, itemOffset, profile).CopyTo(destination);
    }

    /// <summary>
    /// Copies encoded identities whose keys are inside an inclusive encoded key range.<br/>
    /// The method performs shelf-local binary search and then streams contiguous sorted slots until the upper key is exceeded.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded fixed-width key.</param>
    /// <param name="upperKey">The inclusive upper encoded fixed-width key.</param>
    /// <param name="encodedIdentities">The destination span for encoded identities.</param>
    /// <returns>The number of identities copied.</returns>
    public int CopyIdentitiesInKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, Span<ulong> encodedIdentities)
    {
        int slotIndex = LowerBoundKey(lowerKey);
        int copied = 0;
        ushort count = ItemCount;
        while (slotIndex < count && copied < encodedIdentities.Length)
        {
            ushort itemOffset = FixedNScalar8Layout.ReadSlot(bytes, profile, slotIndex);
            if (FixedNScalar8Layout.CompareItemKey(bytes, itemOffset, profile, upperKey) > 0)
            {
                break;
            }

            encodedIdentities[copied++] = FixedNScalar8Layout.ReadItemIdentity(bytes, itemOffset, profile);
            slotIndex++;
        }

        return copied;
    }
}
