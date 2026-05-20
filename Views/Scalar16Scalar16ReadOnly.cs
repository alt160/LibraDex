using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path projection over a `Scalar16Scalar16` (`SS16-16`) shelf.<br/>
/// The projection owns no bytes, performs no allocation, and reads sortable scalar fields in persisted big-endian form.<br/>
/// </summary>
internal readonly ref struct Scalar16Scalar16ReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;
    private readonly Scalar16Scalar16Profile profile;

    /// <summary>
    /// Creates a read-only projection over shelf bytes.<br/>
    /// The caller owns the bytes and is responsible for ensuring their lifetime covers this stack-only view.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    public Scalar16Scalar16ReadOnly(ReadOnlySpan<byte> bytes)
        : this(bytes, Scalar16Scalar16Profile.Default32KiB)
    {
    }

    /// <summary>
    /// Creates a read-only projection over shelf bytes using the supplied index-level shelf profile.<br/>
    /// The profile supplies extent-derived constants such as max item count and region offsets without storing them in each shelf.<br/>
    /// </summary>
    /// <param name="bytes">The shelf bytes to project.</param>
    /// <param name="profile">The index-level shelf sizing profile.</param>
    public Scalar16Scalar16ReadOnly(ReadOnlySpan<byte> bytes, Scalar16Scalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
    }

    public uint Magic => Scalar16Scalar16Layout.ReadMagic(bytes);

    public ushort FormatVersion => Scalar16Scalar16Layout.ReadFormatVersion(bytes);

    public ushort HeaderSize => Scalar16Scalar16Layout.ReadHeaderSize(bytes);

    public uint Flags => Scalar16Scalar16Layout.ReadFlags(bytes);

    public ushort ItemCount => Scalar16Scalar16Layout.ReadItemCount(bytes);

    public bool IsValid =>
        bytes.Length >= profile.ShelfExtentSize &&
        Magic == Scalar16Scalar16Layout.Magic &&
        FormatVersion == Scalar16Scalar16Layout.FormatVersion &&
        HeaderSize == Scalar16Scalar16Layout.HeaderSize &&
        ItemCount <= profile.MaxItemCount;

    /// <summary>
    /// Reads the encoded high key half for an item in sorted slot order.<br/>
    /// The returned key half is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable high key half.</returns>
    public ulong ReadKeyHighAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar16Layout.ReadItemKeyHigh(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded low key half for an item in sorted slot order.<br/>
    /// The returned key half is the canonical sortable scalar representation, not a decoded API value.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable low key half.</returns>
    public ulong ReadKeyLowAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar16Layout.ReadItemKeyLow(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded high identity half for an item in sorted slot order.<br/>
    /// The returned identity half is the canonical sortable scalar representation used after the key in tuple order.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable high identity half.</returns>
    public ulong ReadIdentityHighAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar16Layout.ReadItemIdentityHigh(bytes, itemOffset);
    }

    /// <summary>
    /// Reads the encoded low identity half for an item in sorted slot order.<br/>
    /// The returned identity half is the final canonical sortable scalar used as the duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded sortable low identity half.</returns>
    public ulong ReadIdentityLowAt(int slotIndex)
    {
        ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar16Layout.ReadItemIdentityLow(bytes, itemOffset);
    }

    /// <summary>
    /// Finds the first sorted slot whose tuple is greater than or equal to the supplied encoded tuple.<br/>
    /// This lower-bound search compares `(keyHigh, keyLow, identityHigh, identityLow)` without decoding persisted scalar payloads.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentityHigh">The encoded sortable high identity half.</param>
    /// <param name="encodedIdentityLow">The encoded sortable low identity half.</param>
    /// <returns>The lower-bound slot index, or `ItemCount` when all items sort before the tuple.</returns>
    public int LowerBound(ulong encodedKeyHigh, ulong encodedKeyLow, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar16Scalar16Profile localProfile = profile;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Scalar16Scalar16Layout.CompareItemTuple(localBytes, itemOffset, encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow);
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
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <returns>The first slot index at or after the key.</returns>
    public int LowerBoundKey(ulong encodedKeyHigh, ulong encodedKeyLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar16Scalar16Profile localProfile = profile;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(localBytes, localProfile, middle);
            int comparison = Scalar16Scalar16Layout.CompareItemKey(localBytes, itemOffset, encodedKeyHigh, encodedKeyLow);
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
    /// Duplicate-key indexes return the lowest encoded identity tuple for the key.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentityHigh">Receives the first encoded high identity half when the key exists; otherwise zero.</param>
    /// <param name="encodedIdentityLow">Receives the first encoded low identity half when the key exists; otherwise zero.</param>
    /// <returns>True when the key exists in the shelf; otherwise false.</returns>
    public bool TryFindFirstIdentity(ulong encodedKeyHigh, ulong encodedKeyLow, out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        int slotIndex = LowerBoundKey(encodedKeyHigh, encodedKeyLow);
        ushort count = ItemCount;
        if (slotIndex >= count)
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        if (Scalar16Scalar16Layout.CompareItemKey(bytes, itemOffset, encodedKeyHigh, encodedKeyLow) != 0)
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        encodedIdentityHigh = Scalar16Scalar16Layout.ReadItemIdentityHigh(bytes, itemOffset);
        encodedIdentityLow = Scalar16Scalar16Layout.ReadItemIdentityLow(bytes, itemOffset);
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
    /// <param name="encodedIdentityHighs">The caller-owned output span that receives matching encoded high identity halves.</param>
    /// <param name="encodedIdentityLows">The caller-owned output span that receives matching encoded low identity halves.</param>
    /// <returns>The number of identities copied into the output spans.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key is lower than the lower key or an output span is too small.</exception>
    public int CopyIdentitiesInKeyRange(
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow,
        Span<ulong> encodedIdentityHighs,
        Span<ulong> encodedIdentityLows)
    {
        if (upperKeyHigh < lowerKeyHigh || (upperKeyHigh == lowerKeyHigh && upperKeyLow < lowerKeyLow))
        {
            throw new ArgumentException("The upper encoded key must be greater than or equal to the lower encoded key.", nameof(upperKeyHigh));
        }

        int copied = 0;
        int slotIndex = LowerBoundKey(lowerKeyHigh, lowerKeyLow);
        ushort count = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        Scalar16Scalar16Profile localProfile = profile;
        for (int i = slotIndex; i < count; i++)
        {
            ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(localBytes, localProfile, i);
            int upperComparison = Scalar16Scalar16Layout.CompareItemKey(localBytes, itemOffset, upperKeyHigh, upperKeyLow);
            if (upperComparison > 0)
            {
                break;
            }

            if (copied >= encodedIdentityHighs.Length || copied >= encodedIdentityLows.Length)
            {
                throw new ArgumentException("The identity output spans are too small for the requested range.", nameof(encodedIdentityHighs));
            }

            encodedIdentityHighs[copied] = Scalar16Scalar16Layout.ReadItemIdentityHigh(localBytes, itemOffset);
            encodedIdentityLows[copied] = Scalar16Scalar16Layout.ReadItemIdentityLow(localBytes, itemOffset);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Tests whether the exact encoded `(keyHigh, keyLow, identityHigh, identityLow)` tuple exists in the shelf.<br/>
    /// The method uses lower-bound search and does not allocate or decode scalar fields.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded sortable key high half.</param>
    /// <param name="encodedKeyLow">The encoded sortable key low half.</param>
    /// <param name="encodedIdentityHigh">The encoded sortable identity high half.</param>
    /// <param name="encodedIdentityLow">The encoded sortable identity low half.</param>
    /// <returns>True when the exact tuple is present; otherwise false.</returns>
    public bool Contains(ulong encodedKeyHigh, ulong encodedKeyLow, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int slotIndex = LowerBound(encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow);
        if (slotIndex >= ItemCount)
        {
            return false;
        }

        ushort itemOffset = Scalar16Scalar16Layout.ReadSlot(bytes, profile, slotIndex);
        return Scalar16Scalar16Layout.CompareItemTuple(bytes, itemOffset, encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow) == 0;
    }
}
