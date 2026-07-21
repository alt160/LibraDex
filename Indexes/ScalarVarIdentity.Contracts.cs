using LibraDex.Layouts;

namespace LibraDex;

internal enum Scalar8VarIdentityInsertResult
{
    Inserted = 0,
    AlreadyPresent = 1,
    KeyConflict = 2,
    Full = 3,
    Invalid = 4
}

internal readonly record struct Scalar8VarIdentityProfile(
    int ShelfExtentSize,
    int MaxIdentityLength)
{
    public static readonly Scalar8VarIdentityProfile Default4KiB = Create(4 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default8KiB = Create(8 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default16KiB = Create(16 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default32KiB = Create(32 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default64KiB = Create(64 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default128KiB = Create(128 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile DefaultInitial = Default64KiB;

    public static Scalar8VarIdentityProfile Create(int shelfExtentSize, int maxIdentityLength)
    {
        if (shelfExtentSize < Scalar8VarIdentityLayout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The SV8 shelf extent is too small for the header and at least one record.");
        }

        if (maxIdentityLength <= 0 || maxIdentityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, "The first SV8 profile supports raw byte identities from 1 to 1024 bytes.");
        }

        return new Scalar8VarIdentityProfile(shelfExtentSize, maxIdentityLength);
    }

    public Scalar8VarIdentityProfile NextGrowthClass()
    {
        if (ShelfExtentSize >= Default128KiB.ShelfExtentSize)
        {
            return this;
        }

        int nextSize = ShelfExtentSize switch
        {
            <= 4 * 1024 => 8 * 1024,
            <= 8 * 1024 => 16 * 1024,
            <= 16 * 1024 => 32 * 1024,
            <= 32 * 1024 => 64 * 1024,
            _ => 128 * 1024
        };
        return Create(nextSize, MaxIdentityLength);
    }
}

internal enum Scalar8VarIdentityRouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2,
    TerminalVarIdentityRoot = 3
}

internal readonly record struct Scalar8VarIdentityRouteTarget(
    Scalar8VarIdentityRouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct Scalar8VarIdentityRoutePathTarget(
    Scalar8VarIdentityRouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);

internal enum Scalar8VarIdentityRoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6,
    WalkedDuplicateRunOverflow = 7
}

internal enum Scalar8VarIdentityInsertDiagnosticPath
{
    None = 0,
    HeaderTailFast = 1,
    DuplicateRunChainInsert = 2,
    DuplicateRunTailAppend = 3,
    OverflowTailAppend = 4,
    TerminalTailAppend = 5,
    TerminalChainInsert = 6,
    TerminalFullRewrite = 7,
    OverflowChainLocal = 8,
    OverflowChainRewrite = 9,
    TerminalTailInPlace = 10,
    TerminalTailNewShelf = 11
}

internal readonly record struct Scalar8VarIdentityRoutedInsertResult(
    Scalar8VarIdentityRoutedInsertKind Kind,
    Scalar8VarIdentityInsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0,
    Scalar8VarIdentityInsertDiagnosticPath DiagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.None,
    long DiagnosticAllocatedBytes = 0);

/// <summary>
/// Attributes elapsed Stopwatch ticks inside one walked `SV8` write operation.<br/>
/// The counters are diagnostic only and are intended for harness-level performance attribution, not public API contracts.<br/>
/// </summary>
/// <param name="RouteWalkTicks">Ticks spent walking routers to the reached shelf.</param>
/// <param name="ShelfReadTicks">Ticks spent resolving the reached shelf bytes from dirty cache, read cache, or DataKernel.</param>
/// <param name="DuplicateChainTicks">Ticks spent attempting duplicate-run chain insertion before the reached shelf is mutated.</param>
/// <param name="MutationTicks">Ticks spent decoding/searching/mutating the reached shelf image.</param>
/// <param name="StageTicks">Ticks spent staging the no-split shelf image or immediate rewrite request.</param>
/// <param name="StructuralTicks">Ticks spent in growth, split, duplicate-tail, or duplicate-chain structural work after the first shelf mutation reports full.</param>
internal readonly record struct Scalar8VarIdentityWalkedWriteAttribution(
    long RouteWalkTicks,
    long ShelfReadTicks,
    long DuplicateChainTicks,
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
    public static Scalar8VarIdentityWalkedWriteAttribution operator +(
        Scalar8VarIdentityWalkedWriteAttribution left,
        Scalar8VarIdentityWalkedWriteAttribution right)
    {
        return new Scalar8VarIdentityWalkedWriteAttribution(
            left.RouteWalkTicks + right.RouteWalkTicks,
            left.ShelfReadTicks + right.ShelfReadTicks,
            left.DuplicateChainTicks + right.DuplicateChainTicks,
            left.MutationTicks + right.MutationTicks,
            left.StageTicks + right.StageTicks,
            left.StructuralTicks + right.StructuralTicks);
    }
}

internal enum Scalar16VarIdentityInsertResult
{
    Inserted = 0,
    AlreadyPresent = 1,
    KeyConflict = 2,
    Full = 3,
    Invalid = 4
}

internal readonly record struct Scalar16VarIdentityProfile(
    int ShelfExtentSize,
    int MaxIdentityLength)
{
    public static readonly Scalar16VarIdentityProfile Default4KiB = Create(4 * 1024, 1024);
    public static readonly Scalar16VarIdentityProfile Default8KiB = Create(8 * 1024, 1024);
    public static readonly Scalar16VarIdentityProfile Default16KiB = Create(16 * 1024, 1024);
    public static readonly Scalar16VarIdentityProfile Default32KiB = Create(32 * 1024, 1024);
    public static readonly Scalar16VarIdentityProfile Default64KiB = Create(64 * 1024, 1024);
    public static readonly Scalar16VarIdentityProfile Default128KiB = Create(128 * 1024, 1024);
    public static readonly Scalar16VarIdentityProfile DefaultInitial = Default64KiB;

    public static Scalar16VarIdentityProfile Create(int shelfExtentSize, int maxIdentityLength)
    {
        if (shelfExtentSize < Scalar16VarIdentityLayout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The SV16 shelf extent is too small for the header and at least one record.");
        }

        if (maxIdentityLength <= 0 || maxIdentityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, "The first SV16 profile supports raw byte identities from 1 to 1024 bytes.");
        }

        return new Scalar16VarIdentityProfile(shelfExtentSize, maxIdentityLength);
    }

    public Scalar16VarIdentityProfile NextGrowthClass()
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
        return Create(nextSize, MaxIdentityLength);
    }
}

internal enum Scalar16VarIdentityRouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2
}

internal readonly record struct Scalar16VarIdentityRouteTarget(
    Scalar16VarIdentityRouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct Scalar16VarIdentityRoutePathTarget(
    Scalar16VarIdentityRouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);

internal enum Scalar16VarIdentityRoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6,
    WalkedDuplicateRunOverflow = 7
}

internal readonly record struct Scalar16VarIdentityRoutedInsertResult(
    Scalar16VarIdentityRoutedInsertKind Kind,
    Scalar16VarIdentityInsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);

/// <summary>
/// Attributes elapsed Stopwatch ticks inside one walked `SV16` write operation.<br/>
/// The counters are diagnostic only and are intended for harness-level performance attribution, not public API contracts.<br/>
/// </summary>
/// <param name="RouteWalkTicks">Ticks spent walking routers to the reached shelf.</param>
/// <param name="ShelfReadTicks">Ticks spent resolving the reached shelf bytes from dirty cache, read cache, or DataKernel.</param>
/// <param name="DuplicateChainTicks">Ticks spent attempting duplicate-run chain insertion before the reached shelf is mutated.</param>
/// <param name="MutationTicks">Ticks spent decoding/searching/mutating the reached shelf image.</param>
/// <param name="StageTicks">Ticks spent staging the no-split shelf image or immediate rewrite request.</param>
/// <param name="StructuralTicks">Ticks spent in growth, split, duplicate-tail, or duplicate-chain structural work after the first shelf mutation reports full.</param>
internal readonly record struct Scalar16VarIdentityWalkedWriteAttribution(
    long RouteWalkTicks,
    long ShelfReadTicks,
    long DuplicateChainTicks,
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
    public static Scalar16VarIdentityWalkedWriteAttribution operator +(
        Scalar16VarIdentityWalkedWriteAttribution left,
        Scalar16VarIdentityWalkedWriteAttribution right)
    {
        return new Scalar16VarIdentityWalkedWriteAttribution(
            left.RouteWalkTicks + right.RouteWalkTicks,
            left.ShelfReadTicks + right.ShelfReadTicks,
            left.DuplicateChainTicks + right.DuplicateChainTicks,
            left.MutationTicks + right.MutationTicks,
            left.StageTicks + right.StageTicks,
            left.StructuralTicks + right.StructuralTicks);
    }
}
