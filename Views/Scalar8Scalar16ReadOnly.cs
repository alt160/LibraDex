using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path projection over a `Scalar8Scalar16` (`SS8-16`) shelf.<br/>
/// The projection owns no bytes, performs no allocation, and reads sortable scalar fields in persisted big-endian form.<br/>
/// </summary>
internal readonly ref struct Scalar8Scalar16ReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly Scalar8Scalar16Profile profile;

    /// <summary>
    /// Creates a read-only projection over shelf bytes.<br/>
    /// The caller owns the bytes and is responsible for ensuring their lifetime covers this stack-only view.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    public Scalar8Scalar16ReadOnly(ReadOnlySpan<byte> bytes)
        : this(bytes, Scalar8Scalar16Profile.Default32KiB)
    {
    }

    /// <summary>
    /// Creates a read-only projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants such as max item count and region offsets without storing them in each shelf.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar8Scalar16ReadOnly(ReadOnlySpan<byte> bytes, Scalar8Scalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public uint Magic => Scalar8Scalar16Layout.ReadMagic(bytes);

    public ushort FormatVersion => Scalar8Scalar16Layout.ReadFormatVersion(bytes);

    public ushort HeaderSize => Scalar8Scalar16Layout.ReadHeaderSize(bytes);

    public uint Flags => Scalar8Scalar16Layout.ReadFlags(bytes);

    public bool IsDescending => (Flags & Scalar8Scalar16Layout.DescendingFlag) != 0;

    public ushort ItemCount => Scalar8Scalar16Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid =>
        bytes.Length >= profile.ShelfExtentSize &&
        Magic == Scalar8Scalar16Layout.Magic &&
        FormatVersion == Scalar8Scalar16Layout.FormatVersion &&
        HeaderSize == Scalar8Scalar16Layout.HeaderSize &&
        ItemCount <= profile.MaxItemCount;

    /// <summary>
    /// Counts fixed deleted-slot sentinels in the physical slot table.<br/>
    /// This keeps read-only diagnostics aligned with batch-local tombstone images without changing ordinary compact shelf reads.<br/>
    /// </summary>
    /// <param name="physicalCount">The physical slot count to scan.</param>
    /// <returns>The number of slot entries currently marked deleted.</returns>
    private ushort CountDeletedSlots(ushort physicalCount)
    {
        ushort deleted = 0;
        for (int slotIndex = 0; slotIndex < physicalCount; slotIndex++)
        {
            if (Scalar8Scalar16Layout.ReadSlot(bytes, profile, slotIndex) == Scalar8Scalar16Layout.DeletedSlotOffset)
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Reads the encoded key for an item in sorted slot order.<br/>
    /// The returned key is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable key.</returns>
    public ulong ReadKeyAt(int slotIndex)
    {
        ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar16Layout.ReadItemKey(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded identity for an item in sorted slot order.<br/>
    /// The returned identity is the canonical sortable scalar representation used as a duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable identity.</returns>
    public ulong ReadIdentityHighAt(int slotIndex)
    {
        ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar16Layout.ReadItemIdentityHigh(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded low identity half for an item in sorted slot order.<br/>
    /// The returned identity half is the second canonical sortable scalar used as a duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded low identity half.</returns>
    public ulong ReadIdentityLowAt(int slotIndex)
    {
        ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar16Layout.ReadItemIdentityLow(bytes, itemOffset);
    }

    /// <summary>
    /// Finds the first sorted slot whose tuple is greater than or equal to the supplied encoded tuple.<br/>
    /// This lower-bound search compares `(key, identity)` without decoding persisted scalar payloads.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentityHigh">The encoded high identity half.</param>
    /// <param name="encodedIdentityLow">The encoded low identity half.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ulong encodedKey, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar8Scalar16Profile localProfile = profile;
        bool descending = IsDescending;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Scalar8Scalar16Layout.CompareItemTuple(localBytes, itemOffset, encodedKey, encodedIdentityHigh, encodedIdentityLow);
            if (descending ? comparison > 0 : comparison < 0)
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
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <returns>The first slot index at or after the key.</returns>
    public int LowerBoundKey(ulong encodedKey)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar8Scalar16Profile localProfile = profile;
        bool descending = IsDescending;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(localBytes, localProfile, middle);
            ulong itemKey = Scalar8Scalar16Layout.ReadItemKey(localBytes, itemOffset);
            if (descending ? itemKey > encodedKey : itemKey < encodedKey)
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
    /// Finds the first identity stored for an encoded key in sorted shelf order.<br/>
    /// This is the narrow point-lookup primitive for key-only reads; duplicate-key indexes return the lowest encoded identity for the key.<br/>
    /// The method performs one key lower-bound search and does not allocate or decode scalar payloads.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key to find.</param>
    /// <param name="encodedIdentityHigh">Receives the first encoded high identity half when the key exists; otherwise zero.</param>
    /// <param name="encodedIdentityLow">Receives the first encoded low identity half when the key exists; otherwise zero.</param>
    /// <returns>True when the key exists in the shelf; otherwise false.</returns>
    public bool TryFindFirstIdentity(ulong encodedKey, out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        int slotIndex = LowerBoundKey(encodedKey);
        ushort count = ItemCount;
        if (slotIndex >= count)
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        if (Scalar8Scalar16Layout.ReadItemKey(bytes, itemOffset) != encodedKey)
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        encodedIdentityHigh = Scalar8Scalar16Layout.ReadItemIdentityHigh(bytes, itemOffset);
        encodedIdentityLow = Scalar8Scalar16Layout.ReadItemIdentityLow(bytes, itemOffset);
        return true;
    }

    /// <summary>
    /// Copies encoded identities whose keys are inside an inclusive encoded key range.<br/>
    /// The scan starts with one key lower-bound and then walks sorted slots until a key exceeds the upper bound.<br/>
    /// This is the first fixed-shelf scoop primitive: it performs no allocation, does not decode scalar payloads, and writes identities into caller-owned output.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <param name="encodedIdentityHighs">The caller-owned output span that receives matching encoded high identity halves.</param>
    /// <param name="encodedIdentityLows">The caller-owned output span that receives matching encoded low identity halves.</param>
    /// <returns>The number of identities copied into <paramref name="encodedIdentities"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperEncodedKey"/> is lower than <paramref name="lowerEncodedKey"/> or the output span is too small.</exception>
    public int CopyIdentitiesInKeyRange(
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        Span<ulong> encodedIdentityHighs,
        Span<ulong> encodedIdentityLows)
    {
        if (upperEncodedKey < lowerEncodedKey)
        {
            throw new ArgumentException("The upper encoded key must be greater than or equal to the lower encoded key.", nameof(upperEncodedKey));
        }

        int copied = 0;
        bool descending = IsDescending;
        int slotIndex = LowerBoundKey(descending ? upperEncodedKey : lowerEncodedKey);
        ushort count = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar8Scalar16Profile localProfile = profile;
        for (int i = slotIndex; i < count; i++)
        {
            ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(localBytes, localProfile, i);
            ulong itemKey = Scalar8Scalar16Layout.ReadItemKey(localBytes, itemOffset);
            if (descending ? itemKey < lowerEncodedKey : itemKey > upperEncodedKey)
            {
                break;
            }

            if (copied >= encodedIdentityHighs.Length || copied >= encodedIdentityLows.Length)
            {
                throw new ArgumentException("The identity output spans are too small for the requested range.", nameof(encodedIdentityHighs));
            }

            encodedIdentityHighs[copied] = Scalar8Scalar16Layout.ReadItemIdentityHigh(localBytes, itemOffset);
            encodedIdentityLows[copied] = Scalar8Scalar16Layout.ReadItemIdentityLow(localBytes, itemOffset);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Tests whether the exact encoded `(key, identity)` tuple exists in the shelf.<br/>
    /// The method uses lower-bound search and does not allocate or decode scalar fields.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentityHigh">The encoded high identity half.</param>
    /// <param name="encodedIdentityLow">The encoded low identity half.</param>
    /// <returns>True when the exact tuple is present; otherwise false.</returns>
    public bool Contains(ulong encodedKey, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int slotIndex = LowerBound(encodedKey, encodedIdentityHigh, encodedIdentityLow);
        if (slotIndex >= ItemCount)
        {
            return false;
        }

        ushort itemOffset = Scalar8Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar16Layout.CompareItemTuple(bytes, itemOffset, encodedKey, encodedIdentityHigh, encodedIdentityLow) == 0;
    }
}
