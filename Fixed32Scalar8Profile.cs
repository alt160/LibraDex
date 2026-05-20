using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Describes the index-level shelf sizing profile for the `Fixed32Scalar8` (`FS32-8`) shape.<br/>
/// The first profile keeps the same fixed scalar shelf discipline as `SS8-8` while widening the sortable key to 32 bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct Fixed32Scalar8Profile(
    int ShelfExtentSize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `Fixed32Scalar8` shelf profile from a fixed shelf extent size.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the extent cannot hold at least one `FS32-8` item plus slot.</exception>
    public static Fixed32Scalar8Profile Create(int shelfExtentSize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FS32-8 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        int maxItemCount = (shelfExtentSize - Fixed32Scalar8Layout.HeaderSize) / (Fixed32Scalar8Layout.SlotSize + Fixed32Scalar8Layout.ItemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FS32-8 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = Fixed32Scalar8Layout.HeaderSize;
        int slotRegionSize = maxItemCount * Fixed32Scalar8Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * Fixed32Scalar8Layout.ItemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;

        return new Fixed32Scalar8Profile(
            shelfExtentSize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Gets the default `FS32-8` shelf profile used by the first primitive validation slice.<br/>
    /// This intentionally matches the current `SS8-8` default shelf extent so the first comparison isolates item width before shelf-size tuning.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default32KiB => Create(32 * 1024);

    /// <summary>
    /// Gets the default `FS32-8` shelf profile used by routed runtime paths after shelf-size tuning.<br/>
    /// This is the largest shelf addressable by the current 16-bit in-shelf slot offsets and was selected from the first write/read sweep.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default64KiB => Create(64 * 1024);
}
