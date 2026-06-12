namespace LibraDex;

/// <summary>
/// Represents a session-scoped durability batch that defers DataKernel publication until commit.<br/>
/// The batch is internal because public index shapes should expose operation-specific batch APIs instead of leaking session storage details.<br/>
/// </summary>
internal sealed class LibraDexFileSessionDurabilityBatch : IDisposable
{
    private readonly LibraDexFileSession session;
    private bool completed;

    internal LibraDexFileSessionDurabilityBatch(LibraDexFileSession session)
    {
        this.session = session;
    }

    /// <summary>
    /// Publishes all staged session writes through the DataKernel commit path.<br/>
    /// This is a durability-cadence boundary and does not imply SQL-style transaction isolation.<br/>
    /// </summary>
    /// <returns>The DataKernel commit telemetry and deferred commit request count.</returns>
    public (DataKernelCommitTelemetry Commit, long DeferredCommitRequests) Commit()
    {
        ThrowIfCompleted();
        (DataKernelCommitTelemetry Commit, long DeferredCommitRequests) result = session.CommitDurabilityBatch();
        completed = true;
        return result;
    }

    /// <summary>
    /// Discards staged session writes that have not been published.<br/>
    /// Abort reverts to the current backing state by dropping pending DataKernel buffers; it does not roll back already committed durable writes.<br/>
    /// </summary>
    /// <returns>The number of deferred commit requests abandoned by the abort.</returns>
    public long Abort()
    {
        ThrowIfCompleted();
        completed = true;
        return session.AbortDurabilityBatch();
    }

    /// <summary>
    /// Aborts uncommitted staged writes when the scope is disposed without an explicit commit or abort.<br/>
    /// This makes `using` batch scopes safe for early exits while preserving explicit commit as the durable publication point.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed)
        {
            Abort();
        }
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The LibraDex durability batch has already completed.");
        }
    }
}
