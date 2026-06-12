namespace LibraDex;

/// <summary>
/// Reports the result of aborting a public encoded `SS8-8` durability batch.<br/>
/// Abort discards staged DataKernel writes that were not committed and leaves the durable backing state as the source of truth.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of encoded insert calls attempted before abort.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests abandoned by the abort.</param>
public readonly record struct Scalar8Scalar8BatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);

/// <summary>
/// Carries internal commit-path attribution for one `SS8-8` batch.<br/>
/// The counters are diagnostic-only and split batch publication work above the DataKernel commit boundary.<br/>
/// </summary>
internal struct Scalar8Scalar8BatchCommitAttributionTelemetry
{
    internal long DirtyShelfFlushTicks;
    internal long PublicationAttributionTicks;
    internal long DeltaSelectionTicks;
    internal long DeltaStagingTicks;
    internal long FullShelfStagingTicks;
    internal long SplitFallbackFullShelfStagingTicks;
    internal long DataKernelCommitTicks;
    internal long DeltaShelfCount;
    internal long DeltaUngroupedSpanCount;
    internal long DeltaUngroupedBytes;
    internal long DeltaGrouped512SpanCount;
    internal long DeltaGrouped512Bytes;
    internal long DeltaGrouped1024SpanCount;
    internal long DeltaGrouped1024Bytes;
    internal long DeltaGrouped4096SpanCount;
    internal long DeltaGrouped4096Bytes;
    internal long DeltaGrouped8192SpanCount;
    internal long DeltaGrouped8192Bytes;
    internal long DeltaPositiveGapCount;
    internal long DeltaPositiveGapBytes;
    internal long DeltaMaxPositiveGapBytes;
}

/// <summary>
/// Reports the result of committing a public encoded `SS8-8` durability batch.<br/>
/// Counts describe operation outcomes gathered by the batch, while commit telemetry describes the single DataKernel publication boundary.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of encoded insert calls attempted through the batch.</param>
/// <param name="InsertedCount">The number of encoded tuples physically inserted.</param>
/// <param name="AlreadyPresentCount">The number of encoded tuples that already existed.</param>
/// <param name="KeyConflictCount">The number of encoded tuples rejected by uniqueness policy.</param>
/// <param name="InitialShelfRouteCreateCount">The number of first-prefix shelf routes initialized by the batch.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests folded into this batch commit.</param>
/// <param name="Commit">The DataKernel commit telemetry for the batch publication.</param>
public readonly record struct Scalar8Scalar8BatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Carries internal timing counters for one `SS8-8` batch insert path attribution run.<br/>
/// The counters are diagnostic-only and are used by the harness to decide which lower-layer operation should be optimized next.<br/>
/// </summary>
internal struct Scalar8Scalar8BatchInsertAttributionTelemetry
{
    internal long AttemptCount;
    internal long CachedHandledCount;
    internal long CacheMissEmptyRouteCount;
    internal long CacheMissNonShelfTargetCount;
    internal long CacheMissFullShelfCount;
    internal long RouteCacheHitCount;
    internal long RouteCacheMissCount;
    internal long RouteCacheProbeTicks;
    internal long RootLookupTicks;
    internal long RouteWalkTicks;
    internal Scalar8Scalar8RouteWalkAttributionTelemetry RouteWalkAttribution;
    internal long RouteTargetClassificationTicks;
    internal long ShelfCacheLookupTicks;
    internal long ShelfCacheProbeTicks;
    internal long ShelfCacheHitCount;
    internal long ShelfCacheMissCount;
    internal long ShelfCacheReadTicks;
    internal long ShelfCacheSnapshotCloneTicks;
    internal long ShelfCacheEntryCreateTicks;
    internal long ShelfCacheEntryAddTicks;
    internal long ShelfInsertTicks;
    internal long ShelfAppendFastPathTicks;
    internal long ShelfAppendFastPathCount;
    internal long ShelfLowerBoundTicks;
    internal long ShelfDuplicateCheckTicks;
    internal long ShelfUniqueCheckTicks;
    internal long ShelfPayloadWriteTicks;
    internal long ShelfSlotMoveTicks;
    internal long ShelfHeaderWriteTicks;
}

/// <summary>
/// Carries internal dirty-shelf publication attribution for one `SS8-8` batch run.<br/>
/// The counters estimate how much of each cached dirty shelf changed before the current full-shelf rewrite is staged.<br/>
/// </summary>
internal struct Scalar8Scalar8BatchPublicationAttributionTelemetry
{
    internal long DirtyShelfCount;
    internal long FullShelfBytes;
    internal long RawChangedBytes;
    internal long RawChangedRangeCount;
    internal long DeltaCandidateBytes;
    internal long DeltaCandidateRangeCount;
    internal long HeaderDeltaBytes;
    internal long SlotDeltaBytes;
    internal long ItemDeltaBytes;
    internal long MaxDeltaCandidateBytesPerShelf;
    internal long FullShelfBetterOrEqualCount;
}

/// <summary>
/// Describes the public encoded insert outcome for the first `SS8-8` index wrapper.<br/>
/// The values intentionally stay operation-facing rather than exposing every internal routed split kind.<br/>
/// </summary>
public enum Scalar8Scalar8EncodedInsertOutcome
{
    /// <summary>
    /// The encoded tuple was physically inserted into the index.<br/>
    /// </summary>
    Inserted = 0,

    /// <summary>
    /// The exact encoded `(key, identity)` tuple was already present, so no physical insert was needed.<br/>
    /// </summary>
    AlreadyPresent = 1,

    /// <summary>
    /// The encoded key conflicted with an existing key under a uniqueness policy that rejects duplicates.<br/>
    /// </summary>
    KeyConflict = 2
}

/// <summary>
/// Reports the public result of inserting one encoded tuple through a `Scalar8Scalar8Index` wrapper.<br/>
/// First-in-prefix inserts may create an empty routed shelf before inserting the tuple; both commit shapes are reported so convenience does not hide structural I/O.<br/>
/// </summary>
/// <param name="Outcome">The operation-facing insert outcome.</param>
/// <param name="CreatedInitialShelfRoute">Whether the wrapper created the first shelf route for the tuple's root prefix before inserting.</param>
/// <param name="RouteCreateCommit">Commit telemetry for initial shelf-route creation, or default when no route was created.</param>
/// <param name="InsertCommit">Commit telemetry for the insert mutation, or default when the tuple already existed or was rejected.</param>
public readonly record struct Scalar8Scalar8EncodedInsertResult(
    Scalar8Scalar8EncodedInsertOutcome Outcome,
    bool CreatedInitialShelfRoute,
    DataKernelCommitTelemetry RouteCreateCommit,
    DataKernelCommitTelemetry InsertCommit)
{
    /// <summary>
    /// Gets the internal routed mutation kind selected by the storage path.<br/>
    /// This is internal harness-facing attribution data so public callers are not forced to reason about shelf/router implementation details.<br/>
    /// </summary>
    internal Scalar8Scalar8RoutedInsertKind StructuralKind { get; init; }

    /// <summary>
    /// Gets the primary storage offset affected by the routed mutation.<br/>
    /// For no-split inserts this is the rewritten shelf offset; for transform splits this is the new child-router offset.<br/>
    /// </summary>
    internal long PrimaryOffset { get; init; }

    /// <summary>
    /// Gets the left shelf offset after a routed split, or the rewritten shelf offset for no-split inserts.<br/>
    /// The value is zero when the insert did not mutate a shelf.<br/>
    /// </summary>
    internal long LeftShelfOffset { get; init; }

    /// <summary>
    /// Gets the right shelf offset after a routed split.<br/>
    /// The value is zero for no-split inserts and for rejected or no-op inserts.<br/>
    /// </summary>
    internal long RightShelfOffset { get; init; }
}

/// <summary>
/// Reports the public result of reading encoded identities from a `Scalar8Scalar8Index` wrapper.<br/>
/// The result keeps identity count and scratch/coalescing shape visible while the caller continues to own the output identity span.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned destination span.</param>
/// <param name="UsedPooledScratch">Whether the convenience read rented scratch from the shared array pool.</param>
/// <param name="CoalescingEnabled">Whether adjacent shelf coalescing was enabled for the read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents represented by the range scratch path.</param>
public readonly record struct Scalar8Scalar8EncodedRangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);

/// <summary>
/// Identifies one encoded `SS8-8` index instance for runtime operations.<br/>
/// The handle intentionally carries only the root router offset and shelf profile for this slice; catalog metadata, codecs, uniqueness policy, and public naming remain separate future layers.<br/>
/// </summary>
/// <param name="RootRouterOffset">The file offset where this index's root router starts.</param>
/// <param name="Profile">The `SS8-8` shelf profile used by shelves in this index.</param>
internal readonly record struct Scalar8Scalar8IndexHandle(
    long RootRouterOffset,
    Scalar8Scalar8Profile Profile)
{
    /// <summary>
    /// Validates the runtime handle before it is used by an encoded index operation.<br/>
    /// Offset zero is reserved for the superblock, so a valid root router offset must be positive.<br/>
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the handle cannot identify a valid runtime index root.</exception>
    public void Validate()
    {
        if (RootRouterOffset <= 0)
        {
            throw new InvalidDataException("An SS8-8 index handle must reference a positive root router offset.");
        }
    }
}

/// <summary>
/// Describes storage-facing policy for an `SS8-8` routed range read.<br/>
/// This is an internal API-shape checkpoint: callers choose route-cache behavior and whether multi-shelf coalescing is allowed instead of hiding those costs behind convenience defaults.<br/>
/// </summary>
/// <param name="RouteReadPolicy">The router/classifier read policy used while walking route targets.</param>
/// <param name="EnableCoalescing">Whether the reader may use caller-owned range scratch to coalesce adjacent shelf extents.</param>
/// <param name="MinimumRootPrefixSpanForTraversalCoalescing">The minimum inclusive root-prefix width before generalized traversal coalescing is considered.</param>
/// <param name="CoalescedShelfScratchCount">The preferred number of shelf extents to reserve when a pooled convenience wrapper owns range scratch.</param>
internal readonly record struct Scalar8Scalar8RangeReadOptions(
    Scalar8Scalar8RouteReadPolicy RouteReadPolicy,
    bool EnableCoalescing,
    int MinimumRootPrefixSpanForTraversalCoalescing,
    int CoalescedShelfScratchCount)
{
    /// <summary>
    /// Gets the conservative range-read policy.<br/>
    /// This mode uses the arena-aware route policy but does not opt into range coalescing, making it suitable for small or mixed route spans where coalescing may add reads or byte movement.<br/>
    /// </summary>
    public static Scalar8Scalar8RangeReadOptions ConservativeCached { get; } = new(
        Scalar8Scalar8RouteReadPolicy.PreferArenaCache,
        EnableCoalescing: false,
        MinimumRootPrefixSpanForTraversalCoalescing: 4,
        CoalescedShelfScratchCount: 1);

    /// <summary>
    /// Gets the wide-span range-read policy.<br/>
    /// This mode uses caller-owned range scratch for adjacent shelf coalescing and keeps generalized traversal coalescing behind the root-prefix-width gate.<br/>
    /// </summary>
    public static Scalar8Scalar8RangeReadOptions CoalescingCached { get; } = new(
        Scalar8Scalar8RouteReadPolicy.PreferArenaCache,
        EnableCoalescing: true,
        MinimumRootPrefixSpanForTraversalCoalescing: 4,
        CoalescedShelfScratchCount: 8);

    /// <summary>
    /// Validates the policy values before they are used by a routed range read.<br/>
    /// The gate must be positive because a zero-width or negative threshold would make generalized coalescing effectively unconditional and hide a performance-sensitive branch.<br/>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a numeric policy value is outside the supported range.</exception>
    public void Validate()
    {
        if (MinimumRootPrefixSpanForTraversalCoalescing <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumRootPrefixSpanForTraversalCoalescing),
                MinimumRootPrefixSpanForTraversalCoalescing,
                "The traversal coalescing root-prefix span gate must be positive.");
        }

        if (CoalescedShelfScratchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CoalescedShelfScratchCount),
                CoalescedShelfScratchCount,
                "The pooled coalescing shelf scratch count must be positive.");
        }
    }
}

/// <summary>
/// Carries caller-owned scratch buffers for an `SS8-8` routed range read.<br/>
/// The one-shelf buffer is required for the conservative path; the range buffer is optional and enables adjacent shelf coalescing when policy allows it.<br/>
/// </summary>
internal readonly ref struct Scalar8Scalar8RangeReadScratch
{
    /// <summary>
    /// Initializes range-read scratch from caller-owned buffers.<br/>
    /// Passing an empty range scratch span keeps the read on the conservative one-shelf path even when options allow coalescing.<br/>
    /// </summary>
    /// <param name="shelf">The caller-owned buffer for one profiled shelf extent.</param>
    /// <param name="range">The caller-owned buffer for one or more profiled shelf extents.</param>
    public Scalar8Scalar8RangeReadScratch(Span<byte> shelf, Span<byte> range)
    {
        Shelf = shelf;
        Range = range;
    }

    /// <summary>
    /// Gets the required one-shelf scratch buffer.<br/>
    /// This buffer is used by the conservative range path and as fallback when range coalescing is disabled or not sufficiently provisioned.<br/>
    /// </summary>
    public Span<byte> Shelf { get; }

    /// <summary>
    /// Gets the optional multi-shelf range scratch buffer.<br/>
    /// This buffer is used only when range coalescing is enabled and it can hold at least one profiled shelf extent.<br/>
    /// </summary>
    public Span<byte> Range { get; }
}

internal enum Scalar8Scalar8RouteReadPolicy
{
    Uncached = 0,
    PreferArenaCache = 1
}

/// <summary>
/// Carries internal timing counters for diagnostic `SS8-8` route walking.<br/>
/// The counters split direct-router and persisted-router work so routing experiments can be judged against concrete low-level operations.<br/>
/// </summary>
internal struct Scalar8Scalar8RouteWalkAttributionTelemetry
{
    internal long DirectViewLookupTicks;
    internal long DirectPrefixTicks;
    internal long DirectSlotLoadTicks;
    internal long DirectTargetClassificationTicks;
    internal long DirectRouterTargetCount;
    internal long DirectShelfTargetCount;
    internal long NonDirectRouterReadTicks;
    internal long NonDirectPrefixTicks;
    internal long NonDirectRouteLookupTicks;
    internal long NonDirectTargetClassificationTicks;
    internal long NonDirectRouterTargetCount;
    internal long NonDirectShelfTargetCount;
}
