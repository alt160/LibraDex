namespace LibraDex;

/// <summary>
/// Controls durability deferral for all opened indexes in one catalog identity group.<br/>
/// Group batching is about commit cadence only: normal index `Insert` calls remain the mutation surface, and the manager publishes staged writes when `Commit` or `CommitAndDisable` is called.<br/>
/// It does not provide SQL-style rollback, isolation, or transaction semantics.<br/>
/// </summary>
public sealed class CatalogIdentityGroupBatchManager
{
    private readonly Catalog catalog;
    private LibraDexFileSessionDurabilityBatch? activeBatch;
    private LibraDexWriteIntent activeWriteIntent;
    private long attemptedInsertCount;
    private long insertedCount;
    private long initialShelfRouteCreateCount;

    internal CatalogIdentityGroupBatchManager(Catalog catalog, string group)
    {
        this.catalog = catalog;
        Group = group;
    }

    /// <summary>
    /// Gets the identity group controlled by this batch manager.<br/>
    /// Only indexes with the same group name are allowed to route writes through this manager.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets whether group-level batch mode is currently enabled.<br/>
    /// When enabled, normal inserts through participating indexes defer durability until `Commit` or `CommitAndDisable` publishes the session batch.<br/>
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Gets the write-intent hint used when the current group batch cycle was enabled.<br/>
    /// The hint is advisory and lets storage code choose bulk, random, sorted, or latency-sensitive policy branches without changing public mutation calls.<br/>
    /// </summary>
    public LibraDexWriteIntent WriteIntent => activeWriteIntent;

    /// <summary>
    /// Enables group-level durability deferral for subsequent writes through indexes in this identity group.<br/>
    /// Calling this method while already enabled is a no-op so setup code can safely enable batching before a bulk ingest loop.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional write-pattern hints for the active batch cycle.</param>
    public void Enable(LibraDexWriteIntent writeIntent = default)
    {
        if (IsEnabled)
        {
            return;
        }

        activeWriteIntent = writeIntent;
        activeBatch = catalog.Session.BeginDurabilityBatch(writeIntent);
        ResetCycleCounters();
        IsEnabled = true;
    }

    /// <summary>
    /// Publishes staged writes and immediately starts a new group batch cycle while leaving group batch mode enabled.<br/>
    /// This lets bulk callers choose their own commit cadence without switching participating indexes back to per-insert durability after every publication boundary.<br/>
    /// </summary>
    /// <returns>The aggregate commit result for the staged writes that were just published.</returns>
    /// <exception cref="InvalidOperationException">Thrown when group batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult Commit()
    {
        LibraDexGenericBatchCommitResult result = CommitActiveBatch();
        activeBatch = catalog.Session.BeginDurabilityBatch(activeWriteIntent);
        ResetCycleCounters();
        IsEnabled = true;
        return result;
    }

    /// <summary>
    /// Publishes staged writes and returns the identity group to normal immediate-durability write behavior.<br/>
    /// This is the only public ending operation for group batch mode; there is no abort because the manager is about durability cadence rather than rollback semantics.<br/>
    /// </summary>
    /// <returns>The aggregate commit result for the final staged writes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when group batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult CommitAndDisable()
    {
        LibraDexGenericBatchCommitResult result = CommitActiveBatch();
        activeBatch = null;
        activeWriteIntent = default;
        ResetCycleCounters();
        IsEnabled = false;
        return result;
    }

    internal LibraDexGenericInsertResult Insert<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        TKey key,
        TIdentity identity)
    {
        if (!string.Equals(index.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The index does not belong to this identity group batch manager.");
        }

        LibraDexFileSessionDurabilityBatch batch = RequireActiveBatch();
        attemptedInsertCount++;
        var sharedBatch = new LibraDexBatch<TKey, TIdentity>(index, batch, ownsDurabilityBatch: false);
        LibraDexGenericInsertResult result = sharedBatch.Insert(key, identity);
        if (result.Inserted)
        {
            insertedCount++;
        }

        if (result.CreatedInitialShelfRoute)
        {
            initialShelfRouteCreateCount++;
        }

        return result;
    }

    private LibraDexGenericBatchCommitResult CommitActiveBatch()
    {
        LibraDexFileSessionDurabilityBatch batch = RequireActiveBatch();
        (DataKernelCommitTelemetry commit, long deferredRequests) = batch.Commit();
        LibraDexGenericBatchCommitResult result = new(
            attemptedInsertCount,
            insertedCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit);
        catalog.Stats.RecordCommit(result);
        return result;
    }

    private LibraDexFileSessionDurabilityBatch RequireActiveBatch()
    {
        if (!IsEnabled || activeBatch is null)
        {
            throw new InvalidOperationException("The identity group Batch manager is not enabled.");
        }

        return activeBatch;
    }

    private void ResetCycleCounters()
    {
        attemptedInsertCount = 0;
        insertedCount = 0;
        initialShelfRouteCreateCount = 0;
    }
}
