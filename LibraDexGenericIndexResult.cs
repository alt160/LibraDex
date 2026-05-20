namespace LibraDex;

/// <summary>
/// Reports the result of inserting one typed tuple through the generic public index API.<br/>
/// This mirrors the current encoded insert outcome without leaking shape-specific result type names through the generic facade.<br/>
/// </summary>
/// <param name="Inserted">Whether the tuple was inserted.</param>
/// <param name="CreatedInitialShelfRoute">Whether the insert initialized the first routed shelf for its root prefix.</param>
/// <param name="RouteCreateCommit">Commit telemetry for initial route creation, or default when no route was created.</param>
/// <param name="InsertCommit">Commit telemetry for the insert mutation, or default when the tuple was not inserted.</param>
public readonly record struct LibraDexGenericInsertResult(
    bool Inserted,
    bool CreatedInitialShelfRoute,
    DataKernelCommitTelemetry RouteCreateCommit,
    DataKernelCommitTelemetry InsertCommit);

/// <summary>
/// Reports the result of committing a generic public index batch.<br/>
/// Counts describe operation outcomes gathered by the generic facade, while commit telemetry describes the single DataKernel publication boundary.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of typed insert calls attempted through the batch.</param>
/// <param name="InsertedCount">The number of typed tuples physically inserted.</param>
/// <param name="InitialShelfRouteCreateCount">The number of first-prefix shelf routes initialized by the batch.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests folded into this batch commit.</param>
/// <param name="Commit">The DataKernel commit telemetry for the batch publication.</param>
public readonly record struct LibraDexGenericBatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Reports the result of aborting a generic public index batch.<br/>
/// Abort discards staged DataKernel writes that were not committed and leaves the durable backing state as the source of truth.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of typed insert calls attempted before abort.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests abandoned by the abort.</param>
public readonly record struct LibraDexGenericBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);
