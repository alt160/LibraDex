using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Describes the index-level shelf sizing profile for the `Scalar16Scalar16` (`SS16-16`) shape.<br/>
/// The first profile keeps the fixed scalar shelf discipline while widening both the key and identity payloads to 16 bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct Scalar16Scalar16Profile(
    int ShelfExtentSize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `Scalar16Scalar16` shelf profile from a fixed shelf extent size.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the extent cannot hold at least one `SS16-16` item plus slot.</exception>
    public static Scalar16Scalar16Profile Create(int shelfExtentSize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS16-16 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        int maxItemCount = (shelfExtentSize - Scalar16Scalar16Layout.HeaderSize) / (Scalar16Scalar16Layout.SlotSize + Scalar16Scalar16Layout.ItemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS16-16 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = Scalar16Scalar16Layout.HeaderSize;
        int slotRegionSize = maxItemCount * Scalar16Scalar16Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * Scalar16Scalar16Layout.ItemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;

        return new Scalar16Scalar16Profile(
            shelfExtentSize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Gets the default `SS16-16` shelf profile used by the first primitive validation slice.<br/>
    /// This intentionally matches the current `SS8-8`, `SS16-8`, and `SS8-16` default shelf extent before shelf-size tuning.<br/>
    /// </summary>
    public static Scalar16Scalar16Profile Default32KiB => Create(32 * 1024);
}
