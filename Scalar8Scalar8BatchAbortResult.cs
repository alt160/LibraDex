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
