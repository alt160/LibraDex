using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Describes the index-level shelf sizing profile for the `Scalar8Scalar16` (`SS8-16`) shape.<br/>
/// The key/identity shape remains fixed, while the shelf extent size can vary by index profile for measurement and tuning.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct Scalar8Scalar16Profile(
    int ShelfExtentSize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `Scalar8Scalar16` shelf profile from a fixed shelf extent size.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the extent cannot hold at least one `SS8-16` item plus slot.</exception>
    public static Scalar8Scalar16Profile Create(int shelfExtentSize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS8-16 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        int maxItemCount = (shelfExtentSize - Scalar8Scalar16Layout.HeaderSize) / (Scalar8Scalar16Layout.SlotSize + Scalar8Scalar16Layout.ItemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS8-16 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = Scalar8Scalar16Layout.HeaderSize;
        int slotRegionSize = maxItemCount * Scalar8Scalar16Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * Scalar8Scalar16Layout.ItemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;

        return new Scalar8Scalar16Profile(
            shelfExtentSize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Gets the 16 KiB `SS8-16` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while stressing the smaller-shelf route fanout boundary.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default16KiB => Create(16 * 1024);

    /// <summary>
    /// Gets the 24 KiB `SS8-16` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while probing whether a smaller fixed shelf extent improves locality enough to justify a named profile.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default24KiB => Create(24 * 1024);

    /// <summary>
    /// Gets the default `SS8-16` shelf profile used by the first implementation slice.<br/>
    /// This is the 32 KiB baseline profile, not a final statement that 32 KiB is optimal.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default32KiB => Create(32 * 1024);

    /// <summary>
    /// Gets the 48 KiB `SS8-16` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while probing the larger-shelf range fanout and locality boundary.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default48KiB => Create(48 * 1024);

    /// <summary>
    /// Gets the 64 KiB `SS8-16` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while stressing the largest 16-bit-offset shelf extent supported by this fixed scalar layout.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default64KiB => Create(64 * 1024);

    /// <summary>
    /// Resolves the supported `SS8-16` shelf profile for a public creation request by shelf extent size.<br/>
    /// Unsupported sizes fail at the API boundary so public/reopenable indexes do not persist anonymous profile tuples.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The requested fixed shelf extent size in bytes.</param>
    /// <returns>The supported runtime `SS8-16` shelf profile for the requested extent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the shelf extent is not a supported public `SS8-16` profile.</exception>
    internal static Scalar8Scalar16Profile FromSupportedShelfExtentSize(int shelfExtentSize)
    {
        return shelfExtentSize switch
        {
            16 * 1024 => Default16KiB,
            24 * 1024 => Default24KiB,
            32 * 1024 => Default32KiB,
            48 * 1024 => Default48KiB,
            64 * 1024 => Default64KiB,
            _ => throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "Supported SS8-16 shelf-size sweep extents are 16384, 24576, 32768, 49152, and 65536 bytes.")
        };
    }
}
