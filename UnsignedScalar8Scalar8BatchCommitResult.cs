namespace LibraDex;

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
public readonly record struct UnsignedScalar8Scalar8BatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);
