using LibraDex.Layouts;

namespace LibraDex;

internal enum VarKeyScalar8InsertResult
{
    Inserted = 0,
    AlreadyPresent = 1,
    KeyConflict = 2,
    Full = 3,
    Invalid = 4
}

/// <summary>
/// Captures a bounded write-side observation for a routed `VS8` shelf mutation.<br/>
/// The hint is approximate and is not authoritative; cold transform code must still validate against the actual shelf key population before choosing a router shape.<br/>
/// The observation is produced from already-adjacent sorted keys so ordinary writes do not scan the shelf or allocate new analysis structures.<br/>
/// </summary>
/// <param name="Sampled">Whether the insert had at least one adjacent key to compare.</param>
/// <param name="ComparedNeighborCount">The number of adjacent keys compared against the incoming key.</param>
/// <param name="StartDepth">The key byte depth where the bounded comparison started.</param>
/// <param name="MaxCommonPrefixDepth">The deepest absolute key byte depth observed as common with an adjacent key.</param>
/// <param name="MaxCommonPrefixBytes">The number of common bytes observed at or after <paramref name="StartDepth"/>.</param>
internal readonly record struct VarKeyScalar8MutationHint(
    bool Sampled,
    int ComparedNeighborCount,
    int StartDepth,
    int MaxCommonPrefixDepth,
    int MaxCommonPrefixBytes);

/// <summary>
/// Aggregates session-local write-side observations for one routed `VS8` parent route.<br/>
/// The aggregate is intentionally small and advisory so it can steer cold transform policy without becoming a persisted routing contract.<br/>
/// </summary>
/// <param name="SampleCount">The number of sampled mutations recorded for the route.</param>
/// <param name="MaxCommonPrefixBytes">The largest bounded common-prefix byte count observed for the route.</param>
/// <param name="MaxCommonPrefixDepth">The deepest absolute common-prefix depth observed for the route.</param>
internal readonly record struct VarKeyScalar8RouteMutationHint(
    int SampleCount,
    int MaxCommonPrefixBytes,
    int MaxCommonPrefixDepth);

/// <summary>
/// Identifies one parent router route for session-local `VS8` mutation hint aggregation.<br/>
/// The route index is stored instead of only the prefix byte so compressed and multi-byte routers can participate without losing their selected route slot.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The file offset of the parent router that selected the shelf.</param>
/// <param name="RouteIndex">The selected route slot index in that parent router.</param>
internal readonly record struct VarKeyScalar8RouteHintKey(
    long ParentRouterOffset,
    int RouteIndex);

internal readonly record struct VarKeyScalar8Profile(
    int ShelfExtentSize,
    int MaxKeyLength)
{
    public static readonly VarKeyScalar8Profile Default4KiB = Create(4 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default8KiB = Create(8 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default16KiB = Create(16 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default32KiB = Create(32 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default64KiB = Create(64 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default128KiB = Create(128 * 1024, 1024);
    public static readonly VarKeyScalar8Profile DefaultInitial = Default16KiB;

    public static VarKeyScalar8Profile Create(int shelfExtentSize, int maxKeyLength)
    {
        if (shelfExtentSize < VarKeyScalar8Layout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The VS8 shelf extent is too small for the header and at least one record.");
        }

        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The first VS8 profile supports raw byte keys from 1 to 1024 bytes.");
        }

        return new VarKeyScalar8Profile(shelfExtentSize, maxKeyLength);
    }

    public VarKeyScalar8Profile NextGrowthClass()
    {
        if (ShelfExtentSize >= Default128KiB.ShelfExtentSize)
        {
            return this;
        }

        int nextSize = ShelfExtentSize <= 16 * 1024
            ? 32 * 1024
            : ShelfExtentSize <= 32 * 1024
                ? 64 * 1024
                : 128 * 1024;
        return Create(nextSize, MaxKeyLength);
    }
}

internal enum VarKeyScalar8RouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2,
    TerminalIdentityRoot = 3
}

internal readonly record struct VarKeyScalar8RouteTarget(
    VarKeyScalar8RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct VarKeyScalar8RoutePathTarget(
    VarKeyScalar8RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);

internal enum VarKeyScalar8RoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfTransformSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6
}

internal readonly record struct VarKeyScalar8RoutedInsertResult(
    VarKeyScalar8RoutedInsertKind Kind,
    VarKeyScalar8InsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    VarKeyScalar8WalkedWriteAttribution Attribution = default,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);

internal enum VarKeyScalar8TransformRouterKind
{
    ExpandedOneByte = 0,
    ExpandedOneByteChain = 1,
    CompressedMultiByte = 2
}

/// <summary>
/// Describes the physical router shape selected by the cold `VS8` shelf-transform policy.<br/>
/// Hot insert hints can influence this plan, but the plan is produced only after the transform path has exact shelf population evidence.<br/>
/// The router writer consumes this value as simple already-decided metadata, keeping lookup and range-read paths free of heuristic analysis.<br/>
/// </summary>
/// <param name="Kind">The physical router shape to write.</param>
/// <param name="PrefixByteCount">The number of prefix bytes consumed when <paramref name="Kind"/> is multi-byte.</param>
/// <param name="PrefixStem">The exact prefix stem bytes used when <paramref name="Kind"/> is multi-byte.</param>
internal readonly record struct VarKeyScalar8TransformRouterPlan(
    VarKeyScalar8TransformRouterKind Kind,
    byte PrefixByteCount,
    ReadOnlyMemory<byte> PrefixStem)
{
    public static VarKeyScalar8TransformRouterPlan ExpandedOneByte =>
        new(VarKeyScalar8TransformRouterKind.ExpandedOneByte, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar8TransformRouterPlan ExpandedOneByteChain =>
        new(VarKeyScalar8TransformRouterKind.ExpandedOneByteChain, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar8TransformRouterPlan CompressedMultiByte(byte prefixByteCount, ReadOnlyMemory<byte> prefixStem)
    {
        return new VarKeyScalar8TransformRouterPlan(
            VarKeyScalar8TransformRouterKind.CompressedMultiByte,
            prefixByteCount,
            prefixStem);
    }
}

/// <summary>
/// Attributes elapsed Stopwatch ticks inside one walked `VS8` write operation.<br/>
/// The counters are diagnostic only and are intended for harness-level performance attribution, not public API contracts.<br/>
/// </summary>
/// <param name="RouteWalkTicks">Ticks spent walking routers to the reached shelf.</param>
/// <param name="ShelfReadTicks">Ticks spent resolving the reached shelf bytes from dirty cache, read cache, or DataKernel.</param>
/// <param name="MutationTicks">Ticks spent decoding/searching/mutating the shelf image.</param>
/// <param name="StageTicks">Ticks spent staging the resulting shelf image or immediate rewrite request.</param>
/// <param name="StructuralTicks">Ticks spent in growth or shelf-transform structural work after the first shelf mutation reports full.</param>
internal readonly record struct VarKeyScalar8WalkedWriteAttribution(
    long RouteWalkTicks,
    long ShelfReadTicks,
    long MutationTicks,
    long StageTicks,
    long StructuralTicks)
{
    /// <summary>
    /// Adds two attribution rows together.<br/>
    /// This keeps benchmark aggregation allocation-free and makes each counter an independent elapsed-time bucket.<br/>
    /// </summary>
    /// <param name="left">The left attribution row.</param>
    /// <param name="right">The right attribution row.</param>
    /// <returns>The summed attribution row.</returns>
    public static VarKeyScalar8WalkedWriteAttribution operator +(
        VarKeyScalar8WalkedWriteAttribution left,
        VarKeyScalar8WalkedWriteAttribution right)
    {
        return new VarKeyScalar8WalkedWriteAttribution(
            left.RouteWalkTicks + right.RouteWalkTicks,
            left.ShelfReadTicks + right.ShelfReadTicks,
            left.MutationTicks + right.MutationTicks,
            left.StageTicks + right.StageTicks,
            left.StructuralTicks + right.StructuralTicks);
    }
}

internal enum VarKeyScalar16InsertResult
{
    Inserted = 0,
    AlreadyPresent = 1,
    KeyConflict = 2,
    Full = 3,
    Invalid = 4
}

/// <summary>
/// Captures a bounded write-side observation for a routed `VS16` shelf mutation.<br/>
/// The hint is approximate and is not authoritative; cold transform code must still validate against the actual shelf key population before choosing a router shape.<br/>
/// The observation is produced from already-adjacent sorted keys so ordinary writes do not scan the shelf or allocate new analysis structures.<br/>
/// </summary>
/// <param name="Sampled">Whether the insert had at least one adjacent key to compare.</param>
/// <param name="ComparedNeighborCount">The number of adjacent keys compared against the incoming key.</param>
/// <param name="StartDepth">The key byte depth where the bounded comparison started.</param>
/// <param name="MaxCommonPrefixDepth">The deepest absolute key byte depth observed as common with an adjacent key.</param>
/// <param name="MaxCommonPrefixBytes">The number of common bytes observed at or after <paramref name="StartDepth"/>.</param>
internal readonly record struct VarKeyScalar16MutationHint(
    bool Sampled,
    int ComparedNeighborCount,
    int StartDepth,
    int MaxCommonPrefixDepth,
    int MaxCommonPrefixBytes);

/// <summary>
/// Aggregates session-local write-side observations for one routed `VS16` parent route.<br/>
/// The aggregate is intentionally small and advisory so it can steer cold transform policy without becoming a persisted routing contract.<br/>
/// </summary>
/// <param name="SampleCount">The number of sampled mutations recorded for the route.</param>
/// <param name="MaxCommonPrefixBytes">The largest bounded common-prefix byte count observed for the route.</param>
/// <param name="MaxCommonPrefixDepth">The deepest absolute common-prefix depth observed for the route.</param>
internal readonly record struct VarKeyScalar16RouteMutationHint(
    int SampleCount,
    int MaxCommonPrefixBytes,
    int MaxCommonPrefixDepth);

/// <summary>
/// Identifies one parent router route for session-local `VS16` mutation hint aggregation.<br/>
/// The route index is stored instead of only the prefix byte so compressed and multi-byte routers can participate without losing their selected route slot.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The file offset of the parent router that selected the shelf.</param>
/// <param name="RouteIndex">The selected route slot index in that parent router.</param>
internal readonly record struct VarKeyScalar16RouteHintKey(
    long ParentRouterOffset,
    int RouteIndex);

internal readonly record struct VarKeyScalar16Profile(
    int ShelfExtentSize,
    int MaxKeyLength)
{
    public static readonly VarKeyScalar16Profile Default4KiB = Create(4 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default8KiB = Create(8 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default16KiB = Create(16 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default32KiB = Create(32 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default64KiB = Create(64 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default128KiB = Create(128 * 1024, 1024);
    public static readonly VarKeyScalar16Profile DefaultInitial = Default64KiB;

    public static VarKeyScalar16Profile Create(int shelfExtentSize, int maxKeyLength)
    {
        if (shelfExtentSize < VarKeyScalar16Layout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The VS16 shelf extent is too small for the header and at least one record.");
        }

        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The first VS16 profile supports raw byte keys from 1 to 1024 bytes.");
        }

        return new VarKeyScalar16Profile(shelfExtentSize, maxKeyLength);
    }

    public VarKeyScalar16Profile NextGrowthClass()
    {
        if (ShelfExtentSize >= Default128KiB.ShelfExtentSize)
        {
            return this;
        }

        int nextSize = ShelfExtentSize <= 16 * 1024
            ? 32 * 1024
            : ShelfExtentSize <= 32 * 1024
                ? 64 * 1024
                : 128 * 1024;
        return Create(nextSize, MaxKeyLength);
    }
}

internal enum VarKeyScalar16RouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2
}

internal readonly record struct VarKeyScalar16RouteTarget(
    VarKeyScalar16RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct VarKeyScalar16RoutePathTarget(
    VarKeyScalar16RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);

internal enum VarKeyScalar16RoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfTransformSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6
}

internal readonly record struct VarKeyScalar16RoutedInsertResult(
    VarKeyScalar16RoutedInsertKind Kind,
    VarKeyScalar16InsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    VarKeyScalar16WalkedWriteAttribution Attribution = default,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);

internal enum VarKeyScalar16TransformRouterKind
{
    ExpandedOneByte = 0,
    ExpandedOneByteChain = 1,
    CompressedMultiByte = 2
}

/// <summary>
/// Describes the physical router shape selected by the cold `VS16` shelf-transform policy.<br/>
/// Hot insert hints can influence this plan, but the plan is produced only after the transform path has exact shelf population evidence.<br/>
/// The router writer consumes this value as simple already-decided metadata, keeping lookup and range-read paths free of heuristic analysis.<br/>
/// </summary>
/// <param name="Kind">The physical router shape to write.</param>
/// <param name="PrefixByteCount">The number of prefix bytes consumed when <paramref name="Kind"/> is multi-byte.</param>
/// <param name="PrefixStem">The exact prefix stem bytes used when <paramref name="Kind"/> is multi-byte.</param>
internal readonly record struct VarKeyScalar16TransformRouterPlan(
    VarKeyScalar16TransformRouterKind Kind,
    byte PrefixByteCount,
    ReadOnlyMemory<byte> PrefixStem)
{
    public static VarKeyScalar16TransformRouterPlan ExpandedOneByte =>
        new(VarKeyScalar16TransformRouterKind.ExpandedOneByte, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar16TransformRouterPlan ExpandedOneByteChain =>
        new(VarKeyScalar16TransformRouterKind.ExpandedOneByteChain, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar16TransformRouterPlan CompressedMultiByte(byte prefixByteCount, ReadOnlyMemory<byte> prefixStem)
    {
        return new VarKeyScalar16TransformRouterPlan(
            VarKeyScalar16TransformRouterKind.CompressedMultiByte,
            prefixByteCount,
            prefixStem);
    }
}

/// <summary>
/// Attributes elapsed Stopwatch ticks inside one walked `VS16` write operation.<br/>
/// The counters are diagnostic only and are intended for harness-level performance attribution, not public API contracts.<br/>
/// </summary>
/// <param name="RouteWalkTicks">Ticks spent walking routers to the reached shelf.</param>
/// <param name="ShelfReadTicks">Ticks spent resolving the reached shelf bytes from dirty cache, read cache, or DataKernel.</param>
/// <param name="MutationTicks">Ticks spent decoding/searching/mutating the shelf image.</param>
/// <param name="StageTicks">Ticks spent staging the resulting shelf image or immediate rewrite request.</param>
/// <param name="StructuralTicks">Ticks spent in growth or shelf-transform structural work after the first shelf mutation reports full.</param>
internal readonly record struct VarKeyScalar16WalkedWriteAttribution(
    long RouteWalkTicks,
    long ShelfReadTicks,
    long MutationTicks,
    long StageTicks,
    long StructuralTicks)
{
    /// <summary>
    /// Adds two attribution rows together.<br/>
    /// This keeps benchmark aggregation allocation-free and makes each counter an independent elapsed-time bucket.<br/>
    /// </summary>
    /// <param name="left">The left attribution row.</param>
    /// <param name="right">The right attribution row.</param>
    /// <returns>The summed attribution row.</returns>
    public static VarKeyScalar16WalkedWriteAttribution operator +(
        VarKeyScalar16WalkedWriteAttribution left,
        VarKeyScalar16WalkedWriteAttribution right)
    {
        return new VarKeyScalar16WalkedWriteAttribution(
            left.RouteWalkTicks + right.RouteWalkTicks,
            left.ShelfReadTicks + right.ShelfReadTicks,
            left.MutationTicks + right.MutationTicks,
            left.StageTicks + right.StageTicks,
            left.StructuralTicks + right.StructuralTicks);
    }
}
