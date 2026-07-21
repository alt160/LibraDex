namespace LibraDex;

/// <summary>
/// Reports the result of inserting one typed tuple through the generic public index API.<br/>
/// This mirrors the current encoded insert outcome without leaking shape-specific result type names through the generic facade.<br/>
/// </summary>
/// <param name="Inserted">Whether the tuple was inserted.</param>
/// <param name="CreatedInitialShelfRoute">Whether the insert initialized the first routed shelf for its root prefix.</param>
/// <param name="RouteCreateDiagnostics">Diagnostics for initial route creation, or default when no route was created.</param>
/// <param name="InsertDiagnostics">Diagnostics for the insert mutation, or default when the tuple was not inserted.</param>
public readonly record struct LibraDexGenericInsertResult(
    bool Inserted,
    bool CreatedInitialShelfRoute,
    LibraDexOperationDiagnostics RouteCreateDiagnostics,
    LibraDexOperationDiagnostics InsertDiagnostics)
{
    /// <summary>
    /// Gets queued-writer path attribution when this result came from a generic queued writer facade.<br/>
    /// The value remains `None` for normal generic inserts and generic batch inserts.<br/>
    /// </summary>
    internal Scalar8Scalar8QueuedInsertPath QueuedInsertPath { get; init; }
}

/// <summary>
/// Reports the result of deleting one typed tuple through a generic queued writer facade.<br/>
/// This is intentionally tuple-scoped: broader criteria/range deletes remain separate mutation operations because their concurrency and publication shape can touch many shelves.<br/>
/// </summary>
/// <param name="Deleted">Whether one exact key/identity tuple was removed.<br/></param>
/// <param name="DeleteDiagnostics">Diagnostics for the delete mutation, or default when no tuple was removed.<br/></param>
public readonly record struct LibraDexGenericDeleteResult(
    bool Deleted,
    LibraDexOperationDiagnostics DeleteDiagnostics)
{
    /// <summary>
    /// Gets queued-writer path attribution when this result came from a generic queued writer facade.<br/>
    /// The value remains `None` for normal generic exact deletes.<br/>
    /// </summary>
    internal Scalar8Scalar8QueuedInsertPath QueuedInsertPath { get; init; }
}

/// <summary>
/// Reports the result of moving one identity from an old key to a new key through a generic queued writer facade.<br/>
/// Rekey is modeled as replacement insert followed by old tuple delete because LibraDex is an identity index, not a transactional source-of-truth store.<br/>
/// </summary>
/// <param name="Changed">Whether the old key/identity tuple was removed after the replacement tuple was available.<br/></param>
/// <param name="Replacement">The queued insert result for the replacement tuple.<br/></param>
/// <param name="Removal">The queued delete result for the old tuple.<br/></param>
public readonly record struct LibraDexGenericRekeyResult(
    bool Changed,
    LibraDexGenericInsertResult Replacement,
    LibraDexGenericDeleteResult Removal);

/// <summary>
/// Reports the result of committing a generic public index batch.<br/>
/// Counts describe operation outcomes gathered by the generic facade, while commit telemetry describes the single DataKernel publication boundary.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of typed insert calls attempted through the batch.</param>
/// <param name="InsertedCount">The number of typed tuples physically inserted.</param>
/// <param name="InitialShelfRouteCreateCount">The number of first-prefix shelf routes initialized by the batch.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests folded into this batch commit.</param>
/// <param name="CommitDiagnostics">Diagnostics for the batch publication.</param>
public readonly record struct LibraDexGenericBatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    LibraDexOperationDiagnostics CommitDiagnostics,
    LibraDexBatchStorageDiagnostics StorageDiagnostics);

/// <summary>
/// Reports session-local storage shape at a batch publication boundary.<br/>
/// These counters describe how many dirty shelf containers and pending DataKernel writes were present immediately before commit flushing began.<br/>
/// They are diagnostic-only and do not describe semantic insert, delete, or query outcomes.<br/>
/// </summary>
/// <param name="Scalar8Scalar8DirtyShelves">Dirty `SS8-8` fixed shelves staged in the active durability batch.</param>
/// <param name="Scalar16Scalar8DirtyShelves">Dirty `SS16-8` fixed shelves staged in the active durability batch.</param>
/// <param name="Scalar8Scalar16DirtyShelves">Dirty `SS8-16` fixed shelves staged in the active durability batch.</param>
/// <param name="Scalar16Scalar16DirtyShelves">Dirty `SS16-16` fixed shelves staged in the active durability batch.</param>
/// <param name="Fixed32Scalar8DirtyShelves">Dirty `FS32-8` fixed shelves staged in the active durability batch.</param>
/// <param name="Fixed32Scalar16DirtyShelves">Dirty `FS32-16` fixed shelves staged in the active durability batch.</param>
/// <param name="FixedNScalar8DirtyShelves">Dirty `FSN-8` fixed-key shelves staged in the active durability batch.</param>
/// <param name="FixedNScalar16DirtyShelves">Dirty `FSN-16` fixed-key shelves staged in the active durability batch.</param>
/// <param name="VarKeyScalar8DirtyShelves">Dirty `VS8` mutable shelves staged in the active durability batch.</param>
/// <param name="VarKeyScalar16DirtyShelves">Dirty `VS16` mutable shelves staged in the active durability batch.</param>
/// <param name="VarKeyVarIdentityDirtyShelves">Dirty `VV` mutable shelves staged in the active durability batch.</param>
/// <param name="Scalar8VarIdentityDirtyShelves">Dirty `SV8` mutable shelves staged in the active durability batch.</param>
/// <param name="Scalar16VarIdentityDirtyShelves">Dirty `SV16` mutable shelves staged in the active durability batch.</param>
/// <param name="VarKeyScalar8MutableShelfBytes">Profiled byte extents retained by `VS8` mutable shelves in the active durability batch.</param>
/// <param name="VarKeyScalar16MutableShelfBytes">Profiled byte extents retained by `VS16` mutable shelves in the active durability batch.</param>
/// <param name="DeferredCommitRequests">Lower-level commit requests folded into the batch publication.</param>
/// <param name="RouterReadCacheInvalidationPending">Whether the batch requested router/cache invalidation before publication.</param>
public readonly record struct LibraDexBatchStorageDiagnostics(
    int Scalar8Scalar8DirtyShelves,
    int Scalar16Scalar8DirtyShelves,
    int Scalar8Scalar16DirtyShelves,
    int Scalar16Scalar16DirtyShelves,
    int Fixed32Scalar8DirtyShelves,
    int Fixed32Scalar16DirtyShelves,
    int FixedNScalar8DirtyShelves,
    int FixedNScalar16DirtyShelves,
    int VarKeyScalar8DirtyShelves,
    int VarKeyScalar16DirtyShelves,
    int VarKeyVarIdentityDirtyShelves,
    int Scalar8VarIdentityDirtyShelves,
    int Scalar16VarIdentityDirtyShelves,
    long VarKeyScalar8MutableShelfBytes,
    long VarKeyScalar16MutableShelfBytes,
    long DeferredCommitRequests,
    bool RouterReadCacheInvalidationPending)
{
    /// <summary>
    /// Gets the total dirty shelf container count represented by this diagnostic sample.<br/>
    /// This folds fixed, var-key, and scalar-var dirty shelf maps into one scalar for quick log scanning.<br/>
    /// </summary>
    public int TotalDirtyShelves =>
        Scalar8Scalar8DirtyShelves +
        Scalar16Scalar8DirtyShelves +
        Scalar8Scalar16DirtyShelves +
        Scalar16Scalar16DirtyShelves +
        Fixed32Scalar8DirtyShelves +
        Fixed32Scalar16DirtyShelves +
        FixedNScalar8DirtyShelves +
        FixedNScalar16DirtyShelves +
        VarKeyScalar8DirtyShelves +
        VarKeyScalar16DirtyShelves +
        VarKeyVarIdentityDirtyShelves +
        Scalar8VarIdentityDirtyShelves +
        Scalar16VarIdentityDirtyShelves;

    /// <summary>
    /// Gets the total profiled var-key scalar mutable shelf bytes retained by the active batch.<br/>
    /// This is transient working memory, not committed DataKernel bytes or file-backed write volume.<br/>
    /// </summary>
    public long TotalVarKeyScalarMutableShelfBytes =>
        VarKeyScalar8MutableShelfBytes +
        VarKeyScalar16MutableShelfBytes;
}

/// <summary>
/// Reports the result of aborting a generic public index batch.<br/>
/// Abort discards staged DataKernel writes that were not committed and leaves the durable backing state as the source of truth.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of typed insert calls attempted before abort.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests abandoned by the abort.</param>
public readonly record struct LibraDexGenericBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);

/// <summary>
/// Reports the result of publishing a concurrent generic write batch.<br/>
/// Counts describe caller-visible mutation attempts and outcomes, while `PublishedContextCount` describes how many writer-local contexts were published because the batch either reached its final boundary or had to release owned shelf domains before retrying contention/fallback work.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of insert calls attempted through the concurrent batch.<br/></param>
/// <param name="InsertedCount">The number of insert calls that staged or published a new tuple.<br/></param>
/// <param name="AttemptedDeleteCount">The number of exact delete calls attempted through the concurrent batch.<br/></param>
/// <param name="DeletedCount">The number of exact delete calls that staged or published a tuple removal.<br/></param>
/// <param name="AttemptedRekeyCount">The number of rekey calls attempted through the concurrent batch.<br/></param>
/// <param name="ChangedRekeyCount">The number of rekey calls that removed the old tuple after making the replacement tuple available.<br/></param>
/// <param name="PublishedContextCount">The number of writer contexts published by the concurrent batch.<br/></param>
/// <param name="PublishDiagnostics">Diagnostics for the final writer-context publication, or default when the batch had no final staged context.<br/></param>
public readonly record struct LibraDexConcurrentBatchPublishResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AttemptedDeleteCount,
    long DeletedCount,
    long AttemptedRekeyCount,
    long ChangedRekeyCount,
    long PublishedContextCount,
    LibraDexOperationDiagnostics PublishDiagnostics)
{
    /// <summary>
    /// Gets the number of shelf-ownership conflicts encountered while staging this batch.<br/>
    /// Each conflict causes the current implementation to publish its entire active context before retrying the contested operation.<br/>
    /// </summary>
    public long OwnershipConflictCount { get; init; }

    /// <summary>
    /// Gets the number of context publications performed specifically to release ownership after a conflict.<br/>
    /// This is separated from final publication so callers can distinguish useful requested batch boundaries from fragmentation.<br/>
    /// </summary>
    public long ConflictPublicationCount { get; init; }

    /// <summary>
    /// Gets the number of operations that left writer-context staging for topology or unsupported-shape fallback.<br/>
    /// The fallback may publish an active context before performing its immediate mutation.<br/>
    /// </summary>
    public long TopologyFallbackCount { get; init; }

    /// <summary>
    /// Gets the largest number of useful staged mutations retained in one writer context before publication.<br/>
    /// This exposes the effective context size independently from the caller's requested batch size.<br/>
    /// </summary>
    public long MaximumStagedMutationCount { get; init; }

    /// <summary>
    /// Gets the number of useful staged mutations present immediately before conflict-driven publications.<br/>
    /// Dividing this by <see cref="ConflictPublicationCount"/> gives the average effective context size lost to contention.<br/>
    /// </summary>
    public long StagedMutationCountBeforeConflictPublication { get; init; }

    /// <summary>
    /// Gets the number of read/claim-only writer contexts that were aborted instead of entering the publication seam.<br/>
    /// The context-owned dirty shelf signal proves these contexts contained no accepted ordinary or terminal shelf mutation.<br/>
    /// </summary>
    public long EmptyContextAbortCount { get; init; }
}

/// <summary>
/// Reports the public result of reading identities from a generic LibraDex index range.<br/>
/// The caller owns the destination span; this result reports the number of identities copied plus the scratch/coalescing shape used by the underlying routed read.<br/>
/// </summary>
/// <param name="IdentityCount">The number of identities copied into the caller-owned destination span.</param>
/// <param name="UsedPooledScratch">Whether the underlying convenience read rented pooled scratch for routed shelf range traversal.</param>
/// <param name="UsedEncodedIdentityScratch">Whether the generic wrapper rented encoded identity scratch before decoding into the caller-owned destination span.</param>
/// <param name="CoalescingEnabled">Whether adjacent shelf coalescing was enabled for the read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents represented by the routed range scratch path.</param>
public readonly record struct LibraDexGenericRangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool UsedEncodedIdentityScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);
