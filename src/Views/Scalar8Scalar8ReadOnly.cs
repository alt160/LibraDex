using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path projection over a `Scalar8Scalar8` (`SS8-8`) shelf.<br/>
/// The projection owns no bytes, performs no allocation, and reads sortable scalar fields in persisted big-endian form.<br/>
/// </summary>
internal readonly ref struct Scalar8Scalar8ReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly Scalar8Scalar8Profile profile;

    /// <summary>
    /// Creates a read-only projection over shelf bytes.<br/>
    /// The caller owns the bytes and is responsible for ensuring their lifetime covers this stack-only view.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    public Scalar8Scalar8ReadOnly(ReadOnlySpan<byte> bytes)
        : this(bytes, Scalar8Scalar8Profile.Default32KiB)
    {
    }

    /// <summary>
    /// Creates a read-only projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants such as max item count and region offsets without storing them in each shelf.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar8Scalar8ReadOnly(ReadOnlySpan<byte> bytes, Scalar8Scalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public uint Magic => Scalar8Scalar8Layout.ReadMagic(bytes);

    public ushort FormatVersion => Scalar8Scalar8Layout.ReadFormatVersion(bytes);

    public ushort HeaderSize => Scalar8Scalar8Layout.ReadHeaderSize(bytes);

    public uint Flags => Scalar8Scalar8Layout.ReadFlags(bytes);

    public bool IsDuplicateRun => Scalar8Scalar8Layout.HasDuplicateRunFlag(bytes);

    public bool IsDescending => (Flags & Scalar8Scalar8Layout.DescendingFlag) != 0;

    public long DuplicateRunNextOffset => IsDuplicateRun ? Scalar8Scalar8Layout.ReadDuplicateRunNextOffset(bytes) : 0;

    public ushort ItemCount => Scalar8Scalar8Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid =>
        bytes.Length >= profile.ShelfExtentSize &&
        Magic == Scalar8Scalar8Layout.Magic &&
        FormatVersion == Scalar8Scalar8Layout.FormatVersion &&
        HeaderSize == Scalar8Scalar8Layout.HeaderSize &&
        ItemCount <= (IsDuplicateRun ? Scalar8Scalar8Layout.GetDuplicateRunCapacity(profile) : profile.MaxItemCount);

    /// <summary>
    /// Counts fixed deleted-slot sentinels in the physical slot table.<br/>
    /// This keeps read-only diagnostics aligned with batch-local tombstone images without changing ordinary compact shelf reads.<br/>
    /// </summary>
    /// <param name="physicalCount">The physical slot count to scan.</param>
    /// <returns>The number of slot entries currently marked deleted.</returns>
    private ushort CountDeletedSlots(ushort physicalCount)
    {
        if (IsDuplicateRun)
        {
            return 0;
        }

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
    /// Reads the encoded key for an item in sorted slot order.<br/>
    /// The returned key is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable key.</returns>
    public ulong ReadKeyAt(int slotIndex)
    {
        if (IsDuplicateRun)
        {
            return Scalar8Scalar8Layout.ReadDuplicateRunKey(bytes);
        }

        ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar8Layout.ReadItemKey(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded identity for an item in sorted slot order.<br/>
    /// The returned identity is the canonical sortable scalar representation used as a duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable identity.</returns>
    public ulong ReadIdentityAt(int slotIndex)
    {
        if (IsDuplicateRun)
        {
            return Scalar8Scalar8Layout.ReadDuplicateRunIdentity(bytes, slotIndex);
        }

        ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar8Layout.ReadItemIdentity(bytes, itemOffset);
    }

    /// <summary>
    /// Finds the first sorted slot whose tuple is greater than or equal to the supplied encoded tuple.<br/>
    /// This lower-bound search compares `(key, identity)` without decoding persisted scalar payloads.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ulong encodedKey, ulong encodedIdentity)
    {
        if (IsDuplicateRun)
        {
            ulong runKey = Scalar8Scalar8Layout.ReadDuplicateRunKey(bytes);
            if (IsDescending ? runKey > encodedKey : runKey < encodedKey)
            {
                return ItemCount;
            }

            if (IsDescending ? runKey < encodedKey : runKey > encodedKey)
            {
                return 0;
            }

            int runLow = 0;
            int runHigh = ItemCount;
            while (runLow < runHigh)
            {
                int middle = runLow + ((runHigh - runLow) >> 1);
                ulong identity = Scalar8Scalar8Layout.ReadDuplicateRunIdentity(bytes, middle);
                if (IsDescending ? identity > encodedIdentity : identity < encodedIdentity)
                {
                    runLow = middle + 1;
                }
                else
                {
                    runHigh = middle;
                }
            }

            return runLow;
        }

        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar8Scalar8Profile localProfile = profile;
        bool descending = IsDescending;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Scalar8Scalar8Layout.CompareItemTuple(localBytes, itemOffset, encodedKey, encodedIdentity);
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
        if (IsDuplicateRun)
        {
            ulong runKey = Scalar8Scalar8Layout.ReadDuplicateRunKey(bytes);
            return (IsDescending ? runKey > encodedKey : runKey < encodedKey) ? ItemCount : 0;
        }

        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar8Scalar8Profile localProfile = profile;
        bool descending = IsDescending;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(localBytes, localProfile, middle);
            ulong itemKey = Scalar8Scalar8Layout.ReadItemKey(localBytes, itemOffset);
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
    /// <param name="encodedIdentity">Receives the first encoded sortable identity when the key exists; otherwise zero.</param>
    /// <returns>True when the key exists in the shelf; otherwise false.</returns>
    public bool TryFindFirstIdentity(ulong encodedKey, out ulong encodedIdentity)
    {
        if (IsDuplicateRun)
        {
            if (ItemCount == 0 || Scalar8Scalar8Layout.ReadDuplicateRunKey(bytes) != encodedKey)
            {
                encodedIdentity = 0;
                return false;
            }

            encodedIdentity = Scalar8Scalar8Layout.ReadDuplicateRunIdentity(bytes, 0);
            return true;
        }

        int slotIndex = LowerBoundKey(encodedKey);
        ushort count = ItemCount;
        if (slotIndex >= count)
        {
            encodedIdentity = 0;
            return false;
        }

        ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        if (Scalar8Scalar8Layout.ReadItemKey(bytes, itemOffset) != encodedKey)
        {
            encodedIdentity = 0;
            return false;
        }

        encodedIdentity = Scalar8Scalar8Layout.ReadItemIdentity(bytes, itemOffset);
        return true;
    }

    /// <summary>
    /// Copies encoded identities whose keys are inside an inclusive encoded key range.<br/>
    /// The scan starts with one key lower-bound and then walks sorted slots until a key exceeds the upper bound.<br/>
    /// This is the first fixed-shelf scoop primitive: it performs no allocation, does not decode scalar payloads, and writes identities into caller-owned output.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <param name="encodedIdentities">The caller-owned output span that receives matching encoded identities.</param>
    /// <returns>The number of identities copied into <paramref name="encodedIdentities"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperEncodedKey"/> is lower than <paramref name="lowerEncodedKey"/> or the output span is too small.</exception>
    public int CopyIdentitiesInKeyRange(
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        Span<ulong> encodedIdentities)
    {
        if (upperEncodedKey < lowerEncodedKey)
        {
            throw new ArgumentException("The upper encoded key must be greater than or equal to the lower encoded key.", nameof(upperEncodedKey));
        }

        if (IsDuplicateRun)
        {
            ulong runKey = Scalar8Scalar8Layout.ReadDuplicateRunKey(bytes);
            if (runKey < lowerEncodedKey || runKey > upperEncodedKey)
            {
                return 0;
            }

            ushort duplicateRunCount = ItemCount;
            if (duplicateRunCount > encodedIdentities.Length)
            {
                throw new ArgumentException("The identity output span is too small for the requested range.", nameof(encodedIdentities));
            }

            for (int i = 0; i < duplicateRunCount; i++)
            {
                encodedIdentities[i] = Scalar8Scalar8Layout.ReadDuplicateRunIdentity(bytes, i);
            }

            return duplicateRunCount;
        }

        int copied = 0;
        int slotIndex = LowerBoundKey(IsDescending ? upperEncodedKey : lowerEncodedKey);
        ushort count = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar8Scalar8Profile localProfile = profile;
        for (int i = slotIndex; i < count; i++)
        {
            ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(localBytes, localProfile, i);
            ulong itemKey = Scalar8Scalar8Layout.ReadItemKey(localBytes, itemOffset);
            if (IsDescending ? itemKey < lowerEncodedKey : itemKey > upperEncodedKey)
            {
                break;
            }

            if (copied >= encodedIdentities.Length)
            {
                throw new ArgumentException("The identity output span is too small for the requested range.", nameof(encodedIdentities));
            }

            encodedIdentities[copied++] = Scalar8Scalar8Layout.ReadItemIdentity(localBytes, itemOffset);
        }

        return copied;
    }

    /// <summary>
    /// Tests whether the exact encoded `(key, identity)` tuple exists in the shelf.<br/>
    /// The method uses lower-bound search and does not allocate or decode scalar fields.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="encodedIdentity">The encoded sortable identity.</param>
    /// <returns>True when the exact tuple is present; otherwise false.</returns>
    public bool Contains(ulong encodedKey, ulong encodedIdentity)
    {
        if (IsDuplicateRun)
        {
            if (Scalar8Scalar8Layout.ReadDuplicateRunKey(bytes) != encodedKey)
            {
                return false;
            }

            int duplicateRunSlotIndex = LowerBound(encodedKey, encodedIdentity);
            return duplicateRunSlotIndex < ItemCount && Scalar8Scalar8Layout.ReadDuplicateRunIdentity(bytes, duplicateRunSlotIndex) == encodedIdentity;
        }

        int slotIndex = LowerBound(encodedKey, encodedIdentity);
        if (slotIndex >= ItemCount)
        {
            return false;
        }

        ushort itemOffset = Scalar8Scalar8Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar8Scalar8Layout.CompareItemTuple(bytes, itemOffset, encodedKey, encodedIdentity) == 0;
    }
}
