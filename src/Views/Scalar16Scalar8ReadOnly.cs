using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path projection over a `Scalar16Scalar8` (`SS16-8`) shelf.<br/>
/// The projection owns no bytes, performs no allocation, and reads sortable scalar fields in persisted big-endian form.<br/>
/// </summary>
internal readonly ref struct Scalar16Scalar8ReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly Scalar16Scalar8Profile profile;

    /// <summary>
    /// Creates a read-only projection over shelf bytes.<br/>
    /// The caller owns the bytes and is responsible for ensuring their lifetime covers this stack-only view.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    public Scalar16Scalar8ReadOnly(ReadOnlySpan<byte> bytes)
        : this(bytes, Scalar16Scalar8Profile.Default32KiB)
    {
    }

    /// <summary>
    /// Creates a read-only projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants such as max item count and region offsets without storing them in each shelf.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar16Scalar8ReadOnly(ReadOnlySpan<byte> bytes, Scalar16Scalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public uint Magic => Scalar16Scalar8Layout.ReadMagic(bytes);

    public ushort FormatVersion => Scalar16Scalar8Layout.ReadFormatVersion(bytes);

    public ushort HeaderSize => Scalar16Scalar8Layout.ReadHeaderSize(bytes);

    public uint Flags => Scalar16Scalar8Layout.ReadFlags(bytes);

    public bool IsDescending => (Flags & Scalar16Scalar8Layout.DescendingFlag) != 0;

    public ushort ItemCount => Scalar16Scalar8Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid =>
        bytes.Length >= profile.ShelfExtentSize &&
        Magic == Scalar16Scalar8Layout.Magic &&
        FormatVersion == Scalar16Scalar8Layout.FormatVersion &&
        HeaderSize == Scalar16Scalar8Layout.HeaderSize &&
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
            if (Scalar16Scalar8Layout.ReadSlot(bytes, profile, slotIndex) == Scalar16Scalar8Layout.DeletedSlotOffset)
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Reads the encoded high key half for an item in sorted slot order.<br/>
    /// The returned key half is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable high key half.</returns>
    public ulong ReadKeyHighAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar8Layout.ReadItemKeyHigh(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded low key half for an item in sorted slot order.<br/>
    /// The returned key half is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable low key half.</returns>
    public ulong ReadKeyLowAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar8Layout.ReadItemKeyLow(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded identity for an item in sorted slot order.<br/>
    /// The returned identity is the canonical encoded value used as the duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable identity.</returns>
    public ulong ReadIdentityAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar8Layout.ReadItemIdentity(bytes, itemOffset);
    }

    /// <summary>
    /// Finds the first sorted slot whose tuple is greater than or equal to the supplied encoded tuple.<br/>
    /// This lower-bound search compares `(keyHigh, keyLow, identity)` without decoding persisted scalar payloads.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ulong encodedKeyHigh, ulong encodedKeyLow, ulong encodedIdentity)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar16Scalar8Profile localProfile = profile;
        bool descending = IsDescending;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Scalar16Scalar8Layout.CompareItemTuple(localBytes, itemOffset, encodedKeyHigh, encodedKeyLow, encodedIdentity);
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
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <returns>The first slot index at or after the key.</returns>
    public int LowerBoundKey(ulong encodedKeyHigh, ulong encodedKeyLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar16Scalar8Profile localProfile = profile;
        bool descending = IsDescending;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Scalar16Scalar8Layout.CompareItemKey(localBytes, itemOffset, encodedKeyHigh, encodedKeyLow);
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
    /// Finds the first identity stored for an encoded key in sorted shelf order.<br/>
    /// Duplicate-key indexes return the lowest encoded identity for the key.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentity">Receives the first encoded sortable identity when the key exists; otherwise zero.</param>
    /// <returns>True when the key exists in the shelf; otherwise false.</returns>
    public bool TryFindFirstIdentity(ulong encodedKeyHigh, ulong encodedKeyLow, out ulong encodedIdentity)
    {
        int slotIndex = LowerBoundKey(encodedKeyHigh, encodedKeyLow);
        ushort count = ItemCount;
        if (slotIndex >= count)
        {
            encodedIdentity = 0;
            return false;
        }

        ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        if (Scalar16Scalar8Layout.CompareItemKey(bytes, itemOffset, encodedKeyHigh, encodedKeyLow) != 0)
        {
            encodedIdentity = 0;
            return false;
        }

        encodedIdentity = Scalar16Scalar8Layout.ReadItemIdentity(bytes, itemOffset);
        return true;
    }

    /// <summary>
    /// Copies encoded identities whose keys are inside an inclusive encoded key range.<br/>
    /// The scan starts with one key lower-bound and then walks sorted slots until a key exceeds the upper bound.<br/>
    /// </summary>
    /// <param name="lowerKeyHigh">The inclusive lower encoded sortable key high half.</param>
    /// <param name="lowerKeyLow">The inclusive lower encoded sortable key low half.</param>
    /// <param name="upperKeyHigh">The inclusive upper encoded sortable key high half.</param>
    /// <param name="upperKeyLow">The inclusive upper encoded sortable key low half.</param>
    /// <param name="encodedIdentities">The caller-owned output span that receives matching encoded identities.</param>
    /// <returns>The number of identities copied into <paramref name="encodedIdentities"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key is lower than the lower key or the output span is too small.</exception>
    public int CopyIdentitiesInKeyRange(
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow,
        Span<ulong> encodedIdentities)
    {
        if (upperKeyHigh < lowerKeyHigh || (upperKeyHigh == lowerKeyHigh && upperKeyLow < lowerKeyLow))
        {
            throw new ArgumentException("The upper encoded key must be greater than or equal to the lower encoded key.", nameof(upperKeyHigh));
        }

        bool descending = IsDescending;
        int copied = 0;
        int slotIndex = LowerBoundKey(descending ? upperKeyHigh : lowerKeyHigh, descending ? upperKeyLow : lowerKeyLow);
        ushort count = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar16Scalar8Profile localProfile = profile;
        for (int i = slotIndex; i < count; i++)
        {
            ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(localBytes, localProfile, i);
            int boundaryComparison = Scalar16Scalar8Layout.CompareItemKey(
                localBytes, itemOffset,
                descending ? lowerKeyHigh : upperKeyHigh,
                descending ? lowerKeyLow : upperKeyLow);
            if (descending ? boundaryComparison < 0 : boundaryComparison > 0)
            {
                break;
            }

            if (copied >= encodedIdentities.Length)
            {
                throw new ArgumentException("The identity output span is too small for the requested range.", nameof(encodedIdentities));
            }

            encodedIdentities[copied++] = Scalar16Scalar8Layout.ReadItemIdentity(localBytes, itemOffset);
        }

        return copied;
    }

    /// <summary>
    /// Tests whether the exact encoded `(keyHigh, keyLow, identity)` tuple exists in the shelf.<br/>
    /// The method uses lower-bound search and does not allocate or decode scalar fields.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <returns>True when the exact tuple is present; otherwise false.</returns>
    public bool Contains(ulong encodedKeyHigh, ulong encodedKeyLow, ulong encodedIdentity)
    {
        int slotIndex = LowerBound(encodedKeyHigh, encodedKeyLow, encodedIdentity);
        if (slotIndex >= ItemCount)
        {
            return false;
        }

        ushort itemOffset = Scalar16Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar8Layout.CompareItemTuple(bytes, itemOffset, encodedKeyHigh, encodedKeyLow, encodedIdentity) == 0;
    }
}
