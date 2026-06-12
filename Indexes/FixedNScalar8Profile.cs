using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Describes the index-level shelf sizing profile for the `FixedNScalar8` (`FSN-8`) programmable fixed-key shape.<br/>
/// The key byte width is fixed for the index, while the identity lane remains the existing sortable scalar-8 lane.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="KeySize">The fixed encoded key width in bytes.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct FixedNScalar8Profile(
    int ShelfExtentSize,
    int KeySize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `FixedNScalar8` shelf profile from a fixed shelf extent size and fixed key byte width.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <param name="keySize">The fixed encoded key width in bytes for this index.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    public static FixedNScalar8Profile Create(int shelfExtentSize, int keySize)
    {
        ValidateCommonInputs(shelfExtentSize, keySize);
        int itemSize = checked(keySize + FixedNScalar8Layout.IdentitySize);
        int maxItemCount = (shelfExtentSize - FixedNScalar8Layout.HeaderSize) / (FixedNScalar8Layout.SlotSize + itemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FSN-8 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = FixedNScalar8Layout.HeaderSize;
        int slotRegionSize = maxItemCount * FixedNScalar8Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * itemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;
        return new FixedNScalar8Profile(
            shelfExtentSize,
            keySize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Creates the default `FSN-8` shelf profile for the supplied key byte width.<br/>
    /// The default extent matches the tuned `FS32-8` extent so first comparisons isolate programmable key width before shelf-size tuning.<br/>
    /// </summary>
    /// <param name="keySize">The fixed encoded key width in bytes for this index.</param>
    /// <returns>A default 64 KiB profile for the supplied key width.</returns>
    public static FixedNScalar8Profile Default64KiB(int keySize)
    {
        return Create(64 * 1024, keySize);
    }

    internal int ItemSize => checked(KeySize + FixedNScalar8Layout.IdentitySize);

    private static void ValidateCommonInputs(int shelfExtentSize, int keySize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FSN-8 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        if (keySize <= 0 || keySize > FixedNShapeLimits.MaxKeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keySize), keySize, $"FSN-8 fixed key size must be between 1 and {FixedNShapeLimits.MaxKeySize} bytes.");
        }

        if (shelfExtentSize <= FixedNScalar8Layout.HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FSN-8 shelf extent size must leave room for fixed items.");
        }
    }
}

/// <summary>
/// Describes the index-level shelf sizing profile for the `FixedNScalar16` (`FSN-16`) programmable fixed-key shape.<br/>
/// The key byte width is fixed for the index, while the identity lane stores the existing sortable scalar-16 bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="KeySize">The fixed encoded key width in bytes.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct FixedNScalar16Profile(
    int ShelfExtentSize,
    int KeySize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `FixedNScalar16` shelf profile from a fixed shelf extent size and fixed key byte width.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <param name="keySize">The fixed encoded key width in bytes for this index.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    public static FixedNScalar16Profile Create(int shelfExtentSize, int keySize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FSN-16 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        if (keySize <= 0 || keySize > FixedNShapeLimits.MaxKeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keySize), keySize, $"FSN-16 fixed key size must be between 1 and {FixedNShapeLimits.MaxKeySize} bytes.");
        }

        int itemSize = checked(keySize + FixedNScalar16Layout.IdentitySize);
        int maxItemCount = (shelfExtentSize - FixedNScalar16Layout.HeaderSize) / (FixedNScalar16Layout.SlotSize + itemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FSN-16 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = FixedNScalar16Layout.HeaderSize;
        int slotRegionSize = maxItemCount * FixedNScalar16Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * itemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;
        return new FixedNScalar16Profile(
            shelfExtentSize,
            keySize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Creates the default `FSN-16` shelf profile for the supplied key byte width.<br/>
    /// The default extent matches the tuned fixed-key scalar extent so first comparisons isolate identity width before shelf-size tuning.<br/>
    /// </summary>
    /// <param name="keySize">The fixed encoded key width in bytes for this index.</param>
    /// <returns>A default 64 KiB profile for the supplied key width.</returns>
    public static FixedNScalar16Profile Default64KiB(int keySize)
    {
        return Create(64 * 1024, keySize);
    }

    internal int ItemSize => checked(KeySize + FixedNScalar16Layout.IdentitySize);
}

/// <summary>
/// Defines shared programmable fixed-key shape limits.<br/>
/// The first cap intentionally matches the BigInteger magnitude cap plus encoding header while leaving room for other fixed-width byte-key uses.<br/>
/// </summary>
internal static class FixedNShapeLimits
{
    public const int MaxKeySize = 515;
}
