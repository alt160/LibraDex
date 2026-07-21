namespace LibraDex;

/// <summary>
/// Reports the result of aborting a typed unsigned scalar `SS8-8` durability batch.<br/>
/// Abort discards staged DataKernel writes that were not committed and leaves the durable backing state as the source of truth.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of typed insert calls attempted before abort.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests abandoned by the abort.</param>
internal readonly record struct UnsignedScalar8Scalar8BatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);

/// <summary>
/// Reports the result of committing a typed unsigned scalar `SS8-8` durability batch.<br/>
/// Counts describe typed insert outcomes gathered by the batch, while commit telemetry describes the single DataKernel publication boundary.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of typed insert calls attempted through the batch.</param>
/// <param name="InsertedCount">The number of unsigned tuples physically inserted.</param>
/// <param name="AlreadyPresentCount">The number of unsigned tuples that already existed.</param>
/// <param name="KeyConflictCount">The number of unsigned tuples rejected by uniqueness policy.</param>
/// <param name="InitialShelfRouteCreateCount">The number of first-prefix shelf routes initialized by the batch.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests folded into this batch commit.</param>
/// <param name="Commit">The DataKernel commit telemetry for the batch publication.</param>
internal readonly record struct UnsignedScalar8Scalar8BatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Describes the public typed insert outcome for an unsigned scalar `SS8-8` index wrapper.<br/>
/// The values mirror the encoded core outcomes while keeping normal unsigned call sites free of encoded type names.<br/>
/// </summary>
internal enum UnsignedScalar8Scalar8InsertOutcome
{
    /// <summary>
    /// The unsigned `(key, identity)` tuple was physically inserted into the index.<br/>
    /// </summary>
    Inserted = 0,

    /// <summary>
    /// The exact unsigned `(key, identity)` tuple was already present, so no physical insert was needed.<br/>
    /// </summary>
    AlreadyPresent = 1,

    /// <summary>
    /// The unsigned key conflicted with an existing key under a uniqueness policy that rejects duplicates.<br/>
    /// </summary>
    KeyConflict = 2
}

/// <summary>
/// Reports the public result of inserting one unsigned scalar key and unsigned scalar identity through an `UnsignedScalar8Scalar8Index` wrapper.<br/>
/// The result mirrors the encoded insert telemetry while keeping the developer-facing operation typed as unsigned scalar values.<br/>
/// </summary>
/// <param name="Outcome">The typed operation-facing insert outcome.</param>
/// <param name="CreatedInitialShelfRoute">Whether the wrapper created the first shelf route for the tuple's root prefix before inserting.</param>
/// <param name="RouteCreateCommit">Commit telemetry for initial shelf-route creation, or default when no route was created.</param>
/// <param name="InsertCommit">Commit telemetry for the insert mutation, or default when the tuple already existed or was rejected.</param>
internal readonly record struct UnsignedScalar8Scalar8InsertResult(
    UnsignedScalar8Scalar8InsertOutcome Outcome,
    bool CreatedInitialShelfRoute,
    DataKernelCommitTelemetry RouteCreateCommit,
    DataKernelCommitTelemetry InsertCommit)
{
    /// <summary>
    /// Gets queued-writer path attribution when this result came from the typed queued writer facade.<br/>
    /// The value remains `None` for normal typed inserts and typed batch inserts.<br/>
    /// </summary>
    internal Scalar8Scalar8QueuedInsertPath QueuedInsertPath { get; init; }
}

/// <summary>
/// Reports the public result of reading unsigned scalar identities from an `UnsignedScalar8Scalar8Index` wrapper.<br/>
/// The caller owns the destination span; this result reports the number of identities copied plus the read scratch/coalescing shape.<br/>
/// </summary>
/// <param name="IdentityCount">The number of unsigned scalar identities copied into the caller-owned destination span.</param>
/// <param name="UsedPooledScratch">Whether the convenience read rented scratch from the shared array pool.</param>
/// <param name="CoalescingEnabled">Whether adjacent shelf coalescing was enabled for the read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents represented by the range scratch path.</param>
internal readonly record struct UnsignedScalar8Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);

/// <summary>
/// Maps encoded `SS8-8` public results into typed unsigned scalar wrapper results.<br/>
/// The mapper keeps public unsigned wrappers thin while preventing encoded type names from leaking through typed API return values.<br/>
/// </summary>
internal static class UnsignedScalar8Scalar8ResultMapper
{
    /// <summary>
    /// Maps an encoded insert outcome into the matching unsigned scalar insert outcome.<br/>
    /// </summary>
    /// <param name="outcome">The encoded core insert outcome.</param>
    /// <returns>The typed unsigned insert outcome.</returns>
    /// <exception cref="InvalidDataException">Thrown when the encoded core returns an unknown insert outcome.</exception>
    internal static UnsignedScalar8Scalar8InsertOutcome MapInsertOutcome(Scalar8Scalar8EncodedInsertOutcome outcome)
    {
        return outcome switch
        {
            Scalar8Scalar8EncodedInsertOutcome.Inserted => UnsignedScalar8Scalar8InsertOutcome.Inserted,
            Scalar8Scalar8EncodedInsertOutcome.AlreadyPresent => UnsignedScalar8Scalar8InsertOutcome.AlreadyPresent,
            Scalar8Scalar8EncodedInsertOutcome.KeyConflict => UnsignedScalar8Scalar8InsertOutcome.KeyConflict,
            _ => throw new InvalidDataException($"Unknown SS8-8 encoded insert outcome {outcome}.")
        };
    }

    /// <summary>
    /// Maps an encoded batch commit result into the typed unsigned scalar batch commit result.<br/>
    /// </summary>
    /// <param name="result">The encoded core batch commit result.</param>
    /// <returns>The typed unsigned batch commit result.</returns>
    internal static UnsignedScalar8Scalar8BatchCommitResult MapBatchCommit(Scalar8Scalar8BatchCommitResult result)
    {
        return new UnsignedScalar8Scalar8BatchCommitResult(
            result.AttemptedInsertCount,
            result.InsertedCount,
            result.AlreadyPresentCount,
            result.KeyConflictCount,
            result.InitialShelfRouteCreateCount,
            result.DeferredCommitRequests,
            result.Commit);
    }

    /// <summary>
    /// Maps an encoded insert result into the typed unsigned scalar insert result while preserving internal queued-writer attribution.<br/>
    /// </summary>
    /// <param name="result">The encoded core insert result.</param>
    /// <returns>The typed unsigned insert result.</returns>
    internal static UnsignedScalar8Scalar8InsertResult MapInsert(Scalar8Scalar8EncodedInsertResult result)
    {
        return new UnsignedScalar8Scalar8InsertResult(
            MapInsertOutcome(result.Outcome),
            result.CreatedInitialShelfRoute,
            result.RouteCreateCommit,
            result.InsertCommit)
        {
            QueuedInsertPath = result.QueuedInsertPath
        };
    }

    /// <summary>
    /// Maps an encoded batch abort result into the typed unsigned scalar batch abort result.<br/>
    /// </summary>
    /// <param name="result">The encoded core batch abort result.</param>
    /// <returns>The typed unsigned batch abort result.</returns>
    internal static UnsignedScalar8Scalar8BatchAbortResult MapBatchAbort(Scalar8Scalar8BatchAbortResult result)
    {
        return new UnsignedScalar8Scalar8BatchAbortResult(
            result.AttemptedInsertCount,
            result.DeferredCommitRequests);
    }
}
