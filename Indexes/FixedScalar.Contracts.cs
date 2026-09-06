using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Reports the structural effect of inserting an item into a `Scalar8Scalar8` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior later.<br/>
/// </summary>
internal enum Scalar8Scalar8InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(key, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Describes the conservative byte ranges changed by one `SS8-8` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Scalar8Scalar8MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);

/// <summary>
/// Describes the index-level shelf sizing profile for the `Scalar8Scalar8` (`SS8-8`) shape.<br/>
/// The key/identity shape remains fixed, while the shelf extent size can vary by index profile for measurement and tuning.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct Scalar8Scalar8Profile(
    int ShelfExtentSize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `Scalar8Scalar8` shelf profile from a fixed shelf extent size.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the extent cannot hold at least one `SS8-8` item plus slot.</exception>
    public static Scalar8Scalar8Profile Create(int shelfExtentSize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS8-8 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        int maxItemCount = (shelfExtentSize - Scalar8Scalar8Layout.HeaderSize) / (Scalar8Scalar8Layout.SlotSize + Scalar8Scalar8Layout.ItemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS8-8 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = Scalar8Scalar8Layout.HeaderSize;
        int slotRegionSize = maxItemCount * Scalar8Scalar8Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * Scalar8Scalar8Layout.ItemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;

        return new Scalar8Scalar8Profile(
            shelfExtentSize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Gets the 4 KiB `SS8-8` shelf profile used by memory-backed catalogs when retained slack matters more than large sequential disk scans.<br/>
    /// The extent matches the common OS page granularity while preserving the same encoded tuple layout and router semantics.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default4KiB => Create(4 * 1024);

    /// <summary>
    /// Gets the 8 KiB `SS8-8` shelf profile used as a middle point for memory-backed catalog tuning.<br/>
    /// This keeps retained slack lower than the disk-oriented 32 KiB shelf while reducing router fanout pressure compared with 4 KiB shelves.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default8KiB => Create(8 * 1024);

    /// <summary>
    /// Gets the 16 KiB `SS8-8` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while stressing the smaller-shelf route fanout boundary.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default16KiB => Create(16 * 1024);

    /// <summary>
    /// Gets the 24 KiB `SS8-8` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while probing whether a smaller fixed shelf extent improves locality enough to justify a named profile.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default24KiB => Create(24 * 1024);

    /// <summary>
    /// Gets the default `SS8-8` shelf profile used by the first implementation slice.<br/>
    /// This is the 32 KiB baseline profile, not a final statement that 32 KiB is optimal.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default32KiB => Create(32 * 1024);

    /// <summary>
    /// Gets the 48 KiB `SS8-8` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while probing the larger-shelf range fanout and locality boundary.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default48KiB => Create(48 * 1024);

    /// <summary>
    /// Gets the 64 KiB `SS8-8` shelf profile used for fixed-scalar shelf-size sweep validation.<br/>
    /// This keeps the encoded key and identity shape unchanged while stressing the largest 16-bit-offset shelf extent supported by this fixed scalar layout.<br/>
    /// </summary>
    public static Scalar8Scalar8Profile Default64KiB => Create(64 * 1024);

    /// <summary>
    /// Resolves the supported `SS8-8` shelf profile for a public creation request by shelf extent size.<br/>
    /// Unsupported sizes fail at the API boundary so public/reopenable indexes do not persist anonymous profile tuples.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The requested fixed shelf extent size in bytes.</param>
    /// <returns>The supported runtime `SS8-8` shelf profile for the requested extent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the shelf extent is not a supported public `SS8-8` profile.</exception>
    internal static Scalar8Scalar8Profile FromSupportedShelfExtentSize(int shelfExtentSize)
    {
        return shelfExtentSize switch
        {
            4 * 1024 => Default4KiB,
            8 * 1024 => Default8KiB,
            16 * 1024 => Default16KiB,
            24 * 1024 => Default24KiB,
            32 * 1024 => Default32KiB,
            48 * 1024 => Default48KiB,
            64 * 1024 => Default64KiB,
            _ => throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "Supported SS8-8 shelf-size sweep extents are 4096, 8192, 16384, 24576, 32768, 49152, and 65536 bytes.")
        };
    }
}

/// <summary>
/// Reports the outcome of an encoded `SS8-8` identity range read.<br/>
/// This is the first outer range result contract: it keeps the returned identity count explicit and records whether the call used pooled scratch so perf reports can distinguish convenience from reusable-scratch hot paths.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned output span.</param>
/// <param name="UsedPooledScratch">Whether the call rented temporary byte scratch from the shared array pool.</param>
/// <param name="CoalescingEnabled">Whether the selected options allowed adjacent shelf coalescing for this read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents the supplied or pooled range scratch could hold.</param>
internal readonly record struct Scalar8Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);

/// <summary>
/// Identifies the concrete structure found at a routed file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Scalar8Scalar8RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Scalar8Scalar8` shelf magic.<br/>
    /// </summary>
    Shelf = 2,

    /// <summary>
    /// The key route is exhausted and the target stores a terminal identity-only route root.<br/>
    /// </summary>
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures the result of routing an encoded `Scalar8Scalar8` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Scalar8Scalar8RouteTarget(
    Scalar8Scalar8RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Scalar8Scalar8` target plus the parent router route that produced it.<br/>
/// This is used by range continuation code that needs to enumerate adjacent routes from the same parent router without discovering a separate shelf-linked-list structure.<br/>
/// </summary>
/// <param name="Target">The classified route target.</param>
/// <param name="ParentRouterOffset">The file offset of the router that produced <paramref name="Target"/>.</param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected <paramref name="Target"/>.</param>
internal readonly record struct Scalar8Scalar8RoutePathTarget(
    Scalar8Scalar8RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte);

/// <summary>
/// Describes the structural path taken by an internal routed `Scalar8Scalar8` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Scalar8Scalar8RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the routed shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    NoSplit = 1,

    /// <summary>
    /// The tuple fit inside a shelf reached through one child router and only that shelf extent was rewritten.<br/>
    /// </summary>
    TwoLevelNoSplit = 5,

    /// <summary>
    /// The tuple fit inside a shelf reached by the classified route walker and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 6,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 7,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 8,

    /// <summary>
    /// The tuple required a root-prefix-visible split that rewrote the old shelf, appended one shelf, and rewrote the root router.<br/>
    /// </summary>
    RootPrefixSplit = 2,

    /// <summary>
    /// The tuple required a same-root-prefix split that transformed the old shelf offset into a child router and appended two shelves.<br/>
    /// </summary>
    ShelfTransformSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Scalar8Scalar8` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Scalar8Scalar8RoutedInsertResult(
    Scalar8Scalar8RoutedInsertKind Kind,
    Scalar8Scalar8InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Reports the structural effect of inserting an item into a `Scalar8Scalar16` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior later.<br/>
/// </summary>
internal enum Scalar8Scalar16InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(key, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Describes the conservative byte ranges changed by one `SS8-16` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking once the shape reaches batch support.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Scalar8Scalar16MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);

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
    /// Gets the 4 KiB `SS8-16` shelf profile used by memory-backed catalog sweeps.<br/>
    /// This profile preserves the encoded tuple layout while testing page-sized shelf slack for RAM-only indexes.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default4KiB => Create(4 * 1024);

    /// <summary>
    /// Gets the 8 KiB `SS8-16` shelf profile used by memory-backed catalogs unless a sweep override is active.<br/>
    /// It keeps retained shelf slack lower than the disk-oriented profile while avoiding the highest router fanout of 4 KiB shelves.<br/>
    /// </summary>
    public static Scalar8Scalar16Profile Default8KiB => Create(8 * 1024);

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
            4 * 1024 => Default4KiB,
            8 * 1024 => Default8KiB,
            16 * 1024 => Default16KiB,
            24 * 1024 => Default24KiB,
            32 * 1024 => Default32KiB,
            48 * 1024 => Default48KiB,
            64 * 1024 => Default64KiB,
            _ => throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "Supported SS8-16 shelf-size sweep extents are 4096, 8192, 16384, 24576, 32768, 49152, and 65536 bytes.")
        };
    }
}

/// <summary>
/// Reports the outcome of an encoded `SS8-16` identity range read.<br/>
/// The result keeps the copied identity-pair count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identity pairs copied into the caller-owned output spans.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Scalar8Scalar16RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);

/// <summary>
/// Identifies the concrete structure found at a routed `Scalar8Scalar16` file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Scalar8Scalar16RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Scalar8Scalar16` shelf magic.<br/>
    /// </summary>
    Shelf = 2,

    /// <summary>
    /// The target bytes begin with a fixed-key scalar-sixteen terminal identity root.<br/>
    /// </summary>
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures the result of routing an encoded `Scalar8Scalar16` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Scalar8Scalar16RouteTarget(
    Scalar8Scalar16RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Scalar8Scalar16` target plus the parent router route that selected it.<br/>
/// Parent-route split logic uses this to rewrite the exact router that currently fans multiple prefixes into one full shelf.<br/>
/// </summary>
/// <param name="Target">The classified final route target.</param>
/// <param name="ParentRouterOffset">The file offset of the parent router that selected <paramref name="Target"/>.</param>
/// <param name="ParentPrefixByte">The parent-router prefix byte that selected <paramref name="Target"/>.</param>
internal readonly record struct Scalar8Scalar16RoutePathTarget(
    Scalar8Scalar16RouteTarget Target,
    long ParentRouterOffset,
    byte ParentPrefixByte);

/// <summary>
/// Describes the structural path taken by an internal routed `Scalar8Scalar16` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Scalar8Scalar16RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the walked shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 1,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 2,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Scalar8Scalar16` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Scalar8Scalar16RoutedInsertResult(
    Scalar8Scalar16RoutedInsertKind Kind,
    Scalar8Scalar16InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Reports the structural effect of inserting an item into a `Scalar16Scalar8` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior after routed/public `SS16-8` support exists.<br/>
/// </summary>
internal enum Scalar16Scalar8InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(keyHigh, keyLow, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Describes the conservative byte ranges changed by one `SS16-8` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Scalar16Scalar8MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);

/// <summary>
/// Describes the index-level shelf sizing profile for the `Scalar16Scalar8` (`SS16-8`) shape.<br/>
/// The first profile keeps the same fixed scalar shelf discipline as `SS8-8` while widening the sortable key to 16 bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct Scalar16Scalar8Profile(
    int ShelfExtentSize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `Scalar16Scalar8` shelf profile from a fixed shelf extent size.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the extent cannot hold at least one `SS16-8` item plus slot.</exception>
    public static Scalar16Scalar8Profile Create(int shelfExtentSize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS16-8 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        int maxItemCount = (shelfExtentSize - Scalar16Scalar8Layout.HeaderSize) / (Scalar16Scalar8Layout.SlotSize + Scalar16Scalar8Layout.ItemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "SS16-8 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = Scalar16Scalar8Layout.HeaderSize;
        int slotRegionSize = maxItemCount * Scalar16Scalar8Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * Scalar16Scalar8Layout.ItemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;

        return new Scalar16Scalar8Profile(
            shelfExtentSize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Gets the 4 KiB `SS16-8` shelf profile used by memory-backed catalog sweeps.<br/>
    /// This page-sized option minimizes per-shelf slack while preserving the same routed fixed-scalar storage semantics.<br/>
    /// </summary>
    public static Scalar16Scalar8Profile Default4KiB => Create(4 * 1024);

    /// <summary>
    /// Gets the 8 KiB `SS16-8` shelf profile used by memory-backed catalogs unless a sweep override is active.<br/>
    /// It is the RAM-oriented midpoint selected before per-shape workload evidence justifies a different default.<br/>
    /// </summary>
    public static Scalar16Scalar8Profile Default8KiB => Create(8 * 1024);

    /// <summary>
    /// Gets the default `SS16-8` shelf profile used by the first primitive validation slice.<br/>
    /// This intentionally matches the current `SS8-8` default shelf extent so the first comparison isolates item width before shelf-size tuning.<br/>
    /// </summary>
    public static Scalar16Scalar8Profile Default32KiB => Create(32 * 1024);
}

/// <summary>
/// Reports the outcome of an encoded `SS16-8` identity range read.<br/>
/// The result keeps the copied identity count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned output span.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Scalar16Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);

/// <summary>
/// Identifies the concrete structure found at a routed `Scalar16Scalar8` file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Scalar16Scalar8RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Scalar16Scalar8` shelf magic.<br/>
    /// </summary>
    Shelf = 2,

    /// <summary>
    /// The target bytes begin with a fixed-key scalar-eight terminal identity root.<br/>
    /// </summary>
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures the result of routing an encoded `Scalar16Scalar8` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Scalar16Scalar8RouteTarget(
    Scalar16Scalar8RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Scalar16Scalar8` target plus the parent router route that produced it.<br/>
/// This keeps walked split decisions shape-local while letting the split rewrite only the router that owns the full shelf route.<br/>
/// </summary>
/// <param name="Target">The classified route target.</param>
/// <param name="ParentRouterOffset">The file offset of the router that produced <paramref name="Target"/>.</param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected <paramref name="Target"/>.</param>
internal readonly record struct Scalar16Scalar8RoutePathTarget(
    Scalar16Scalar8RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte);

/// <summary>
/// Describes the structural path taken by an internal routed `Scalar16Scalar8` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Scalar16Scalar8RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the routed shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 1,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 2,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Scalar16Scalar8` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Scalar16Scalar8RoutedInsertResult(
    Scalar16Scalar8RoutedInsertKind Kind,
    Scalar16Scalar8InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Reports the structural effect of inserting an item into a `Scalar16Scalar16` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior after routed/public `SS16-16` support exists.<br/>
/// </summary>
internal enum Scalar16Scalar16InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(keyHigh, keyLow, identityHigh, identityLow)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Describes the conservative byte ranges changed by one `SS16-16` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Scalar16Scalar16MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);

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
    /// Gets the 4 KiB `SS16-16` shelf profile used by memory-backed catalog sweeps.<br/>
    /// This page-sized option minimizes retained slack for the widest scalar/scalar shape before measuring router fanout costs.<br/>
    /// </summary>
    public static Scalar16Scalar16Profile Default4KiB => Create(4 * 1024);

    /// <summary>
    /// Gets the 8 KiB `SS16-16` shelf profile used by memory-backed catalogs unless a sweep override is active.<br/>
    /// It keeps RAM-backed shelf slack lower than the disk profile while preserving enough tuple capacity for ordinary routed scans.<br/>
    /// </summary>
    public static Scalar16Scalar16Profile Default8KiB => Create(8 * 1024);

    /// <summary>
    /// Gets the read-leaning balanced file-backed `SS16-16` shelf profile selected by the fixed-shape tuning pass.<br/>
    /// This keeps the widest scalar/scalar shape off the 32 KiB write plateau while preserving the stronger read result seen at 24 KiB.<br/>
    /// </summary>
    public static Scalar16Scalar16Profile Default24KiB => Create(24 * 1024);

    /// <summary>
    /// Gets the 32 KiB `SS16-16` shelf profile used by legacy validation anchors and explicit shelf-size sweeps.<br/>
    /// This remains available for comparisons against the smaller tuned file-backed default.<br/>
    /// </summary>
    public static Scalar16Scalar16Profile Default32KiB => Create(32 * 1024);
}

/// <summary>
/// Reports the outcome of an encoded `SS16-16` identity range read.<br/>
/// The result keeps the copied identity-pair count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identity pairs copied into the caller-owned output spans.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Scalar16Scalar16RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);

/// <summary>
/// Identifies the concrete structure found at a routed `Scalar16Scalar16` file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Scalar16Scalar16RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Scalar16Scalar16` shelf magic.<br/>
    /// </summary>
    Shelf = 2,

    /// <summary>
    /// The target bytes begin with a fixed-key scalar-sixteen terminal identity root.<br/>
    /// </summary>
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures the result of routing an encoded `Scalar16Scalar16` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Scalar16Scalar16RouteTarget(
    Scalar16Scalar16RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Scalar16Scalar16` target plus the parent router route that selected it.<br/>
/// Parent-route split logic uses this to rewrite the exact router that currently fans multiple prefixes into one full shelf.<br/>
/// </summary>
/// <param name="Target">The classified final route target.</param>
/// <param name="ParentRouterOffset">The file offset of the parent router that selected <paramref name="Target"/>.</param>
/// <param name="ParentPrefixByte">The parent-router prefix byte that selected <paramref name="Target"/>.</param>
internal readonly record struct Scalar16Scalar16RoutePathTarget(
    Scalar16Scalar16RouteTarget Target,
    long ParentRouterOffset,
    byte ParentPrefixByte);

/// <summary>
/// Describes the structural path taken by an internal routed `Scalar16Scalar16` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Scalar16Scalar16RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the walked shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 1,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 2,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Scalar16Scalar16` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Scalar16Scalar16RoutedInsertResult(
    Scalar16Scalar16RoutedInsertKind Kind,
    Scalar16Scalar16InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Reports the structural effect of inserting an item into a `Fixed32Scalar8` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior after routed/public `FS32-8` support exists.<br/>
/// </summary>
internal enum Fixed32Scalar8InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(keyHigh, keyLow, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Describes the conservative byte ranges changed by one `FS32-8` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Fixed32Scalar8MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);

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
    /// Gets the 4 KiB `FS32-8` shelf profile used by memory-backed catalog sweeps.<br/>
    /// This page-sized option minimizes retained slack for 32-byte fixed-key indexes when RAM locality is more important than disk scan size.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default4KiB => Create(4 * 1024);

    /// <summary>
    /// Gets the 8 KiB `FS32-8` shelf profile used by memory-backed catalogs unless a sweep override is active.<br/>
    /// It gives wide-key memory indexes a smaller default shelf without forcing the highest router fanout of 4 KiB shelves.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default8KiB => Create(8 * 1024);

    /// <summary>
    /// Gets the default `FS32-8` shelf profile used by the first primitive validation slice.<br/>
    /// This intentionally matches the current `SS8-8` default shelf extent so the first comparison isolates item width before shelf-size tuning.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default32KiB => Create(32 * 1024);

    /// <summary>
    /// Gets the tuned default `FS32-8` shelf profile used by routed runtime paths after the 2026-06 performance redo.<br/>
    /// This size avoids the observed 64 KiB write cliff while preserving a larger fixed-key shelf than the 32 KiB validation profile.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default40KiB => Create(40 * 1024);

    /// <summary>
    /// Gets the legacy 64 KiB `FS32-8` shelf profile retained for compatibility tests and sweep comparison.<br/>
    /// This is the largest shelf addressable by the current 16-bit in-shelf slot offsets and was selected from the first write/read sweep.<br/>
    /// </summary>
    public static Fixed32Scalar8Profile Default64KiB => Create(64 * 1024);
}

/// <summary>
/// Reports the outcome of an encoded `FS32-8` identity range read.<br/>
/// The result keeps the copied identity count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned output span.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Fixed32Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);

/// <summary>
/// Identifies the concrete structure found at a routed `Fixed32Scalar8` file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Fixed32Scalar8RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Fixed32Scalar8` shelf magic.<br/>
    /// </summary>
    Shelf = 2,

    /// <summary>
    /// The target bytes begin with a fixed-key scalar-eight terminal identity root.<br/>
    /// </summary>
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures the result of routing an encoded `Fixed32Scalar8` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Fixed32Scalar8RouteTarget(
    Fixed32Scalar8RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Fixed32Scalar8` target plus the parent router route that produced it.<br/>
/// This keeps walked split decisions shape-local while letting the split rewrite only the router that owns the full shelf route.<br/>
/// </summary>
/// <param name="Target">The classified route target.</param>
/// <param name="ParentRouterOffset">The file offset of the router that produced <paramref name="Target"/>.</param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected <paramref name="Target"/>.</param>
internal readonly record struct Fixed32Scalar8RoutePathTarget(
    Fixed32Scalar8RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte);

/// <summary>
/// Describes the structural path taken by an internal routed `Fixed32Scalar8` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Fixed32Scalar8RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the routed shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 1,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 2,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4,

    /// <summary>
    /// The tuple extended a dense sorted shelf whose left prefix stayed at the original offset while its right tail moved to one appended shelf.<br/>
    /// The owning batch must retain the truncated left image until its unpublished left-prefix mutations are published.<br/>
    /// </summary>
    WalkedSortedTailSplit = 5
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Fixed32Scalar8` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Fixed32Scalar8RoutedInsertResult(
    Fixed32Scalar8RoutedInsertKind Kind,
    Fixed32Scalar8InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Reports the structural effect of inserting an item into a `Fixed32Scalar16` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior after routed/public `FS32-16` support exists.<br/>
/// </summary>
internal enum Fixed32Scalar16InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(keyHigh, keyLow, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Describes the conservative byte ranges changed by one `FS32-16` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Fixed32Scalar16MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);

/// <summary>
/// Describes the index-level shelf sizing profile for the `Fixed32Scalar16` (`FS32-16`) shape.<br/>
/// The first profile keeps the same fixed scalar shelf discipline as `SS8-8` while widening the sortable key to 32 bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The fixed shelf extent size in bytes for this index profile.</param>
/// <param name="MaxItemCount">The maximum item count that fits in the profiled shelf extent.</param>
/// <param name="SlotRegionOffset">The byte offset where the fixed-width sorted slot region starts.</param>
/// <param name="SlotRegionSize">The byte length reserved for the sorted slot region.</param>
/// <param name="ItemRegionOffset">The byte offset where the fixed-width physical item region starts.</param>
/// <param name="ItemRegionSize">The byte length reserved for the physical item region.</param>
/// <param name="UnusedTailBytes">The unused bytes left at the end of the fixed shelf extent after partitioning.</param>
internal readonly record struct Fixed32Scalar16Profile(
    int ShelfExtentSize,
    ushort MaxItemCount,
    int SlotRegionOffset,
    int SlotRegionSize,
    int ItemRegionOffset,
    int ItemRegionSize,
    int UnusedTailBytes)
{
    /// <summary>
    /// Creates a `Fixed32Scalar16` shelf profile from a fixed shelf extent size.<br/>
    /// The profile precomputes max item count and region boundaries so hot shelf views do not repeat this math.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The fixed shelf extent size in bytes.</param>
    /// <returns>A profile with derived fixed slot and item regions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the extent cannot hold at least one `FS32-16` item plus slot.</exception>
    public static Fixed32Scalar16Profile Create(int shelfExtentSize)
    {
        const int maxAddressableBytes = ushort.MaxValue + 1;
        if (shelfExtentSize > maxAddressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FS32-16 shelf extent size must fit in 16-bit in-shelf offsets.");
        }

        int maxItemCount = (shelfExtentSize - Fixed32Scalar16Layout.HeaderSize) / (Fixed32Scalar16Layout.SlotSize + Fixed32Scalar16Layout.ItemSize);
        if (maxItemCount <= 0 || maxItemCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "FS32-16 shelf extent size must hold at least one addressable item.");
        }

        int slotRegionOffset = Fixed32Scalar16Layout.HeaderSize;
        int slotRegionSize = maxItemCount * Fixed32Scalar16Layout.SlotSize;
        int itemRegionOffset = slotRegionOffset + slotRegionSize;
        int itemRegionSize = maxItemCount * Fixed32Scalar16Layout.ItemSize;
        int unusedTailBytes = shelfExtentSize - itemRegionOffset - itemRegionSize;

        return new Fixed32Scalar16Profile(
            shelfExtentSize,
            checked((ushort)maxItemCount),
            slotRegionOffset,
            slotRegionSize,
            itemRegionOffset,
            itemRegionSize,
            unusedTailBytes);
    }

    /// <summary>
    /// Gets the 4 KiB `FS32-16` shelf profile used by memory-backed catalog sweeps.<br/>
    /// This page-sized option minimizes retained slack for the widest fixed-key/scalar-identity profile before measuring route fanout costs.<br/>
    /// </summary>
    public static Fixed32Scalar16Profile Default4KiB => Create(4 * 1024);

    /// <summary>
    /// Gets the 8 KiB `FS32-16` shelf profile used by memory-backed catalogs unless a sweep override is active.<br/>
    /// It is the RAM-oriented default for wide fixed-key indexes while file-backed indexes keep their larger sequential-scan profile.<br/>
    /// </summary>
    public static Fixed32Scalar16Profile Default8KiB => Create(8 * 1024);

    /// <summary>
    /// Gets the default `FS32-16` shelf profile used by the first primitive validation slice.<br/>
    /// This intentionally matches the current `SS8-8` default shelf extent so the first comparison isolates item width before shelf-size tuning.<br/>
    /// </summary>
    public static Fixed32Scalar16Profile Default32KiB => Create(32 * 1024);

    /// <summary>
    /// Gets the tuned default `FS32-16` shelf profile used by routed runtime paths after the 2026-06 performance redo.<br/>
    /// This size avoids the observed 64 KiB write cliff while preserving a larger fixed-key shelf than the 32 KiB validation profile.<br/>
    /// </summary>
    public static Fixed32Scalar16Profile Default40KiB => Create(40 * 1024);

    /// <summary>
    /// Gets the legacy 64 KiB `FS32-16` shelf profile retained for compatibility tests and sweep comparison.<br/>
    /// This is the largest shelf addressable by the current 16-bit in-shelf slot offsets and was selected from the first write/read sweep.<br/>
    /// </summary>
    public static Fixed32Scalar16Profile Default64KiB => Create(64 * 1024);
}

/// <summary>
/// Reports the outcome of an encoded `FS32-16` identity range read.<br/>
/// The result keeps the copied identity count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned output span or span pair.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Fixed32Scalar16RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);

/// <summary>
/// Identifies the concrete structure found at a routed `Fixed32Scalar16` file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Fixed32Scalar16RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Fixed32Scalar16` shelf magic.<br/>
    /// </summary>
    Shelf = 2,

    /// <summary>
    /// The target bytes begin with a fixed-key scalar-sixteen terminal identity root.<br/>
    /// </summary>
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures the result of routing an encoded `Fixed32Scalar16` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Fixed32Scalar16RouteTarget(
    Fixed32Scalar16RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Fixed32Scalar16` target plus the parent router route that produced it.<br/>
/// This keeps walked split decisions shape-local while letting the split rewrite only the router that owns the full shelf route.<br/>
/// </summary>
/// <param name="Target">The classified route target.</param>
/// <param name="ParentRouterOffset">The file offset of the router that produced <paramref name="Target"/>.</param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected <paramref name="Target"/>.</param>
internal readonly record struct Fixed32Scalar16RoutePathTarget(
    Fixed32Scalar16RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte);

/// <summary>
/// Describes the structural path taken by an internal routed `Fixed32Scalar16` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Fixed32Scalar16RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the routed shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 1,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 2,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4,

    /// <summary>
    /// The tuple extended a dense sorted shelf whose left prefix stayed at the original offset while its right tail moved to one appended shelf.<br/>
    /// The owning batch must retain the truncated left image until its unpublished left-prefix mutations are published.<br/>
    /// </summary>
    WalkedSortedTailSplit = 5
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Fixed32Scalar16` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Fixed32Scalar16RoutedInsertResult(
    Fixed32Scalar16RoutedInsertKind Kind,
    Fixed32Scalar16InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);
