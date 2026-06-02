using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path projection over a `Fixed32Scalar16` (`FS32-16`) shelf.<br/>
/// The projection owns no bytes, performs no allocation, and reads sortable scalar fields in persisted big-endian form.<br/>
/// </summary>
internal readonly ref struct Fixed32Scalar16ReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly Fixed32Scalar16Profile profile;

    /// <summary>
    /// Creates a read-only projection over shelf bytes.<br/>
    /// The caller owns the bytes and is responsible for ensuring their lifetime covers this stack-only view.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    public Fixed32Scalar16ReadOnly(ReadOnlySpan<byte> bytes)
        : this(bytes, Fixed32Scalar16Profile.Default64KiB)
    {
    }

    /// <summary>
    /// Creates a read-only projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants such as max item count and region offsets without storing them in each shelf.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Fixed32Scalar16ReadOnly(ReadOnlySpan<byte> bytes, Fixed32Scalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public uint Magic => Fixed32Scalar16Layout.ReadMagic(bytes);

    public ushort FormatVersion => Fixed32Scalar16Layout.ReadFormatVersion(bytes);

    public ushort HeaderSize => Fixed32Scalar16Layout.ReadHeaderSize(bytes);

    public uint Flags => Fixed32Scalar16Layout.ReadFlags(bytes);

    public ushort ItemCount => Fixed32Scalar16Layout.ReadItemCount(bytes);

    public ushort PhysicalItemCount => ItemCount;

    public ushort LiveItemCount => checked((ushort)(ItemCount - DeletedItemCount));

    public ushort DeletedItemCount => CountDeletedSlots(ItemCount);

    public bool IsValid =>
        bytes.Length >= profile.ShelfExtentSize &&
        Magic == Fixed32Scalar16Layout.Magic &&
        FormatVersion == Fixed32Scalar16Layout.FormatVersion &&
        HeaderSize == Fixed32Scalar16Layout.HeaderSize &&
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
            if (Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex) == Fixed32Scalar16Layout.DeletedSlotOffset)
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
    public ulong ReadKeyPart0At(int slotIndex)
    {
        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.ReadItemKeyPart0(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded low key half for an item in sorted slot order.<br/>
    /// The returned key half is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable low key half.</returns>
    public ulong ReadKeyPart1At(int slotIndex)
    {
        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.ReadItemKeyPart1(bytes, itemOffset);
    }

    /// <summary>
    /// Reads encoded key part 2 for an item in sorted slot order.<br/>
    /// The returned key part is the canonical sortable representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable key part 2.</returns>
    public ulong ReadKeyPart2At(int slotIndex)
    {
        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.ReadItemKeyPart2(bytes, itemOffset);
    }

    /// <summary>
    /// Reads encoded key part 3 for an item in sorted slot order.<br/>
    /// The returned key part is the canonical sortable representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable key part 3.</returns>
    public ulong ReadKeyPart3At(int slotIndex)
    {
        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.ReadItemKeyPart3(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded identity high lane for an item in sorted slot order.<br/>
    /// The returned lane is the canonical persisted identity bytes projected as a byte-order-preserving integer.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded identity high lane.</returns>
    public ulong ReadIdentityHighAt(int slotIndex)
    {
        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.ReadItemIdentityHigh(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded identity low lane for an item in sorted slot order.<br/>
    /// The returned lane is the canonical persisted identity bytes projected as a byte-order-preserving integer.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded identity low lane.</returns>
    public ulong ReadIdentityLowAt(int slotIndex)
    {
        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.ReadItemIdentityLow(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded low identity lane for an item in sorted slot order.<br/>
    /// This overload is for deterministic 64-bit identity fixtures stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded identity low lane.</returns>
    public ulong ReadIdentityAt(int slotIndex)
    {
        return ReadIdentityLowAt(slotIndex);
    }

    /// <summary>
    /// Finds the first sorted slot whose tuple is greater than or equal to the supplied encoded tuple.<br/>
    /// This lower-bound search compares `(key0, key1, key2, key3, identityHigh, identityLow)` without decoding persisted payloads.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ulong key0, ulong key1, ulong key2, ulong key3, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Fixed32Scalar16Profile localProfile = profile;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Fixed32Scalar16Layout.CompareItemTuple(localBytes, itemOffset, key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow);
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
    /// Finds the first sorted slot greater than or equal to a tuple with a zero high identity lane.<br/>
    /// This overload is for deterministic 64-bit identity fixtures stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ulong key0, ulong key1, ulong key2, ulong key3, ulong encodedIdentityLow)
    {
        return LowerBound(key0, key1, key2, key3, 0, encodedIdentityLow);
    }

    /// <summary>
    /// Finds the first sorted slot whose key is greater than or equal to the supplied encoded key.<br/>
    /// This is the range-scan entry point for key-only lower bounds.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <returns>The first slot index at or after the key.</returns>
    public int LowerBoundKey(ulong key0, ulong key1, ulong key2, ulong key3)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Fixed32Scalar16Profile localProfile = profile;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Fixed32Scalar16Layout.CompareItemKey(localBytes, itemOffset, key0, key1, key2, key3);
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
    /// Finds the first identity stored for an encoded key in sorted shelf order.<br/>
    /// Duplicate-key indexes return the lowest encoded identity for the key.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHigh">Receives the first encoded identity high lane when the key exists; otherwise zero.</param>
    /// <param name="encodedIdentityLow">Receives the first encoded identity low lane when the key exists; otherwise zero.</param>
    /// <returns>True when the key exists in the shelf; otherwise false.</returns>
    public bool TryFindFirstIdentity(ulong key0, ulong key1, ulong key2, ulong key3, out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        int slotIndex = LowerBoundKey(key0, key1, key2, key3);
        ushort count = ItemCount;
        if (slotIndex >= count)
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        if (Fixed32Scalar16Layout.CompareItemKey(bytes, itemOffset, key0, key1, key2, key3) != 0)
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        encodedIdentityHigh = Fixed32Scalar16Layout.ReadItemIdentityHigh(bytes, itemOffset);
        encodedIdentityLow = Fixed32Scalar16Layout.ReadItemIdentityLow(bytes, itemOffset);
        return true;
    }

    /// <summary>
    /// Finds the first low identity lane stored for an encoded key in sorted shelf order.<br/>
    /// This overload is for deterministic 64-bit identity fixtures stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityLow">Receives the first encoded identity low lane when the key exists; otherwise zero.</param>
    /// <returns>True when the key exists in the shelf; otherwise false.</returns>
    public bool TryFindFirstIdentity(ulong key0, ulong key1, ulong key2, ulong key3, out ulong encodedIdentityLow)
    {
        bool found = TryFindFirstIdentity(key0, key1, key2, key3, out _, out encodedIdentityLow);
        return found;
    }

    /// <summary>
    /// Copies encoded identities whose keys are inside an inclusive encoded key range.<br/>
    /// The scan starts with one key lower-bound and then walks sorted slots until a key exceeds the upper bound.<br/>
    /// </summary>
    /// <param name="lowerKey0">The inclusive lower encoded sortable key part 0.</param>
    /// <param name="lowerKey1">The inclusive lower encoded sortable key part 1.</param>
    /// <param name="lowerKey2">The inclusive lower encoded sortable key part 2.</param>
    /// <param name="lowerKey3">The inclusive lower encoded sortable key part 3.</param>
    /// <param name="upperKey0">The inclusive upper encoded sortable key part 0.</param>
    /// <param name="upperKey1">The inclusive upper encoded sortable key part 1.</param>
    /// <param name="upperKey2">The inclusive upper encoded sortable key part 2.</param>
    /// <param name="upperKey3">The inclusive upper encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHighs">The caller-owned output span that receives matching encoded identity high lanes.</param>
    /// <param name="encodedIdentityLows">The caller-owned output span that receives matching encoded identity low lanes.</param>
    /// <returns>The number of identities copied into the output spans.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key is lower than the lower key or the output span is too small.</exception>
    public int CopyIdentitiesInKeyRange(
        ulong lowerKey0,
        ulong lowerKey1,
        ulong lowerKey2,
        ulong lowerKey3,
        ulong upperKey0,
        ulong upperKey1,
        ulong upperKey2,
        ulong upperKey3,
        Span<ulong> encodedIdentityHighs,
        Span<ulong> encodedIdentityLows)
    {
        if (CompareKeys(upperKey0, upperKey1, upperKey2, upperKey3, lowerKey0, lowerKey1, lowerKey2, lowerKey3) < 0)
        {
            throw new ArgumentException("The upper encoded key must be greater than or equal to the lower encoded key.", nameof(upperKey0));
        }

        int copied = 0;
        int slotIndex = LowerBoundKey(lowerKey0, lowerKey1, lowerKey2, lowerKey3);
        ushort count = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Fixed32Scalar16Profile localProfile = profile;
        for (int i = slotIndex; i < count; i++)
        {
            ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(localBytes, localProfile, i);
            int upperComparison = Fixed32Scalar16Layout.CompareItemKey(localBytes, itemOffset, upperKey0, upperKey1, upperKey2, upperKey3);
            if (upperComparison > 0)
            {
                break;
            }

            if (copied >= encodedIdentityHighs.Length || copied >= encodedIdentityLows.Length)
            {
                throw new ArgumentException("The identity output spans are too small for the requested range.", nameof(encodedIdentityHighs));
            }

            encodedIdentityHighs[copied] = Fixed32Scalar16Layout.ReadItemIdentityHigh(localBytes, itemOffset);
            encodedIdentityLows[copied] = Fixed32Scalar16Layout.ReadItemIdentityLow(localBytes, itemOffset);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Copies low identity lanes whose keys are inside an inclusive encoded key range.<br/>
    /// This overload is for deterministic 64-bit identity fixtures stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="lowerKey0">The inclusive lower encoded sortable key part 0.</param>
    /// <param name="lowerKey1">The inclusive lower encoded sortable key part 1.</param>
    /// <param name="lowerKey2">The inclusive lower encoded sortable key part 2.</param>
    /// <param name="lowerKey3">The inclusive lower encoded sortable key part 3.</param>
    /// <param name="upperKey0">The inclusive upper encoded sortable key part 0.</param>
    /// <param name="upperKey1">The inclusive upper encoded sortable key part 1.</param>
    /// <param name="upperKey2">The inclusive upper encoded sortable key part 2.</param>
    /// <param name="upperKey3">The inclusive upper encoded sortable key part 3.</param>
    /// <param name="encodedIdentities">The caller-owned output span that receives matching encoded identity low lanes.</param>
    /// <returns>The number of identities copied into the output span.</returns>
    public int CopyIdentitiesInKeyRange(
        ulong lowerKey0,
        ulong lowerKey1,
        ulong lowerKey2,
        ulong lowerKey3,
        ulong upperKey0,
        ulong upperKey1,
        ulong upperKey2,
        ulong upperKey3,
        Span<ulong> encodedIdentities)
    {
        if (CompareKeys(upperKey0, upperKey1, upperKey2, upperKey3, lowerKey0, lowerKey1, lowerKey2, lowerKey3) < 0)
        {
            throw new ArgumentException("The upper encoded key must be greater than or equal to the lower encoded key.", nameof(upperKey0));
        }

        int copied = 0;
        int slotIndex = LowerBoundKey(lowerKey0, lowerKey1, lowerKey2, lowerKey3);
        ushort count = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Fixed32Scalar16Profile localProfile = profile;
        for (int i = slotIndex; i < count; i++)
        {
            ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(localBytes, localProfile, i);
            int upperComparison = Fixed32Scalar16Layout.CompareItemKey(localBytes, itemOffset, upperKey0, upperKey1, upperKey2, upperKey3);
            if (upperComparison > 0)
            {
                break;
            }

            if (copied >= encodedIdentities.Length)
            {
                throw new ArgumentException("The identity output span is too small for the requested range.", nameof(encodedIdentities));
            }

            encodedIdentities[copied] = Fixed32Scalar16Layout.ReadItemIdentityLow(localBytes, itemOffset);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Tests whether the exact encoded `(key0, key1, key2, key3, identityHigh, identityLow)` tuple exists in the shelf.<br/>
    /// The method uses lower-bound search and does not allocate or decode scalar fields.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <returns>True when the exact tuple is present; otherwise false.</returns>
    public bool Contains(ulong key0, ulong key1, ulong key2, ulong key3, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int slotIndex = LowerBound(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow);
        if (slotIndex >= ItemCount)
        {
            return false;
        }

        ushort itemOffset = Fixed32Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Fixed32Scalar16Layout.CompareItemTuple(bytes, itemOffset, key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow) == 0;
    }

    /// <summary>
    /// Tests whether an encoded tuple with a zero high identity lane exists in the shelf.<br/>
    /// This overload is for deterministic 64-bit identity fixtures stored as `FS32-16` identities with a zero high lane.<br/>
    /// </summary>
    /// <param name="key0">The encoded sortable key part 0.</param>
    /// <param name="key1">The encoded sortable key part 1.</param>
    /// <param name="key2">The encoded sortable key part 2.</param>
    /// <param name="key3">The encoded sortable key part 3.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane.</param>
    /// <returns>True when the exact tuple is present; otherwise false.</returns>
    public bool Contains(ulong key0, ulong key1, ulong key2, ulong key3, ulong encodedIdentityLow)
    {
        return Contains(key0, key1, key2, key3, 0, encodedIdentityLow);
    }

    private static int CompareKeys(ulong left0, ulong left1, ulong left2, ulong left3, ulong right0, ulong right1, ulong right2, ulong right3)
    {
        if (left0 != right0)
        {
            return left0 < right0 ? -1 : 1;
        }

        if (left1 != right1)
        {
            return left1 < right1 ? -1 : 1;
        }

        if (left2 != right2)
        {
            return left2 < right2 ? -1 : 1;
        }

        if (left3 != right3)
        {
            return left3 < right3 ? -1 : 1;
        }

        return 0;
    }
}
