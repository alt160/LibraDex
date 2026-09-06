namespace LibraDex;

/// <summary>
/// Controls durability deferral for all opened indexes in one catalog identity group.<br/>
/// Group batching is about commit cadence only: normal index `Insert` calls remain the mutation surface, and the manager publishes staged writes when `Commit` or `CommitAndDisable` is called.<br/>
/// It does not provide SQL-style rollback, isolation, or transaction semantics.<br/>
/// It also does not make grouped indexes independent concurrency domains; all participating writes still share the owning catalog session write window.<br/>
/// </summary>
public sealed class CatalogIdentityGroupBatchManager
{
    private readonly Catalog catalog;
    private LibraDexFileSessionDurabilityBatch? activeBatch;
    private LibraDexWriteIntent activeWriteIntent;
    private readonly Dictionary<IIndex, ISharedLibraDexBatch> activeTypedBatches = [];
    private long attemptedInsertCount;
    private long insertedCount;
    private long initialShelfRouteCreateCount;
    private LibraDexStringScalar8Index.LibraDexStringScalar8InsertScratch? stringScalar8Scratch;

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
        activeTypedBatches.Clear();
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
    /// Publishes staged writes and immediately starts a new group batch cycle while leaving group batch mode enabled.<br/>
    /// This is the preferred spelling for LibraDex and Abraxas-facing code because group batching controls publication cadence, not a database transaction boundary.<br/>
    /// The current implementation delegates to <see cref="Commit"/> so existing telemetry, diagnostics, and compatibility behavior remain identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome for the staged writes that were just published.</returns>
    /// <exception cref="InvalidOperationException">Thrown when group batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult Publish()
    {
        return Commit();
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

    /// <summary>
    /// Publishes staged writes and returns the identity group to normal immediate-publication write behavior.<br/>
    /// This is the preferred spelling for ending group batch mode when callers want publication cadence without database commit wording.<br/>
    /// The current implementation delegates to <see cref="CommitAndDisable"/> so compatibility behavior remains identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome for the final staged writes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when group batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult PublishAndDisable()
    {
        return CommitAndDisable();
    }

    /// <summary>
    /// Discards staged writes for the active group batch and disables group batch mode.<br/>
    /// This is intended for cooperative cancellation paths that need to leave the session usable without publishing the current partial batch.<br/>
    /// Already committed earlier batch cycles remain durable because this manager controls commit cadence, not transaction rollback.<br/>
    /// </summary>
    /// <returns>The number of deferred lower-level commit requests abandoned with the active batch.</returns>
    public long AbortAndDisable()
    {
        LibraDexFileSessionDurabilityBatch batch = RequireActiveBatch();
        AbortActiveTypedBatches();
        long deferredRequests = batch.Abort();
        activeBatch = null;
        activeWriteIntent = default;
        ResetCycleCounters();
        IsEnabled = false;
        return deferredRequests;
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
        LibraDexBatch<TKey, TIdentity> sharedBatch = GetOrCreateTypedBatch(index, batch);
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

    internal LibraDexGenericInsertResult Insert(
        LibraDexStringScalar8Index index,
        string? key,
        ulong identity)
    {
        if (!string.Equals(index.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The index does not belong to this identity group batch manager.");
        }

        _ = RequireActiveBatch();
        attemptedInsertCount++;
        stringScalar8Scratch ??= new LibraDexStringScalar8Index.LibraDexStringScalar8InsertScratch();
        LibraDexGenericInsertResult result = index.InsertInCurrentScope(key, identity, stringScalar8Scratch);
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

    /// <summary>
    /// Inserts one prepared string/scalar8 key through the active group batch.<br/>
    /// Prepared keys are used by bulk loaders for repeated string keys so projection bytes can be reused without bypassing group-level durability accounting.<br/>
    /// </summary>
    /// <param name="index">The participating string/scalar8 facade.</param>
    /// <param name="prepared">The prepared exact and projection bytes for the index.</param>
    /// <param name="identity">The scalar identity to insert.</param>
    /// <returns>The exact-index insert result.</returns>
    internal LibraDexGenericInsertResult InsertPrepared(
        LibraDexStringScalar8Index index,
        LibraDexStringScalar8Index.LibraDexStringScalar8PreparedKey prepared,
        ulong identity)
    {
        if (!string.Equals(index.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The index does not belong to this identity group batch manager.");
        }

        _ = RequireActiveBatch();
        attemptedInsertCount++;
        stringScalar8Scratch ??= new LibraDexStringScalar8Index.LibraDexStringScalar8InsertScratch();
        LibraDexGenericInsertResult result = index.InsertPreparedInCurrentScope(prepared, identity, stringScalar8Scratch);
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

    /// <summary>
    /// Inserts one borrowed UTF-8 logical string through the active identity-group durability scope.<br/>
    /// The group-owned string scratch adds LibraDex's exact marker without a managed allocation, while the logical string facade remains responsible for every maintained projection.<br/>
    /// </summary>
    /// <param name="index">The participating logical string/scalar8 facade.<br/></param>
    /// <param name="utf8Key">A validated UTF-8 string payload without an Inheto length prefix or LibraDex marker.<br/></param>
    /// <param name="identity">The scalar identity associated with the logical string.<br/></param>
    /// <returns>The exact-index insert result with group-level attempt accounting.<br/></returns>
    internal LibraDexGenericInsertResult InsertUtf8(
        LibraDexStringScalar8Index index,
        ReadOnlySpan<byte> utf8Key,
        ulong identity)
    {
        if (!string.Equals(index.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The index does not belong to this identity group batch manager.");
        }

        _ = RequireActiveBatch();
        attemptedInsertCount++;
        stringScalar8Scratch ??= new LibraDexStringScalar8Index.LibraDexStringScalar8InsertScratch();
        LibraDexGenericInsertResult result = index.InsertUtf8InCurrentScope(utf8Key, identity, stringScalar8Scratch);
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
        PrepareActiveTypedBatchesForCommit();
        (DataKernelCommitTelemetry commit, long deferredRequests, LibraDexBatchStorageDiagnostics storageDiagnostics) = batch.Commit();
        CompleteActiveTypedBatchesAfterCommit();
        LibraDexGenericBatchCommitResult result = new(
            attemptedInsertCount,
            insertedCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            LibraDexOperationDiagnostics.FromDataKernel(commit),
            storageDiagnostics);
        catalog.Stats.RecordCommit(result);
        return result;
    }

    /// <summary>
    /// Gets the per-index typed batch used for the current catalog group commit cycle.<br/>
    /// Reusing the typed batch lets shape-specific batch caches, especially `SS8-8` shelf mutation caches, survive across all inserts in the group interval.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type of the participating index.</typeparam>
    /// <typeparam name="TIdentity">The public identity type of the participating index.</typeparam>
    /// <param name="index">The participating index.</param>
    /// <param name="batch">The active outer durability batch.</param>
    /// <returns>The typed batch bound to the active group commit cycle.</returns>
    private LibraDexBatch<TKey, TIdentity> GetOrCreateTypedBatch<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        LibraDexFileSessionDurabilityBatch batch)
    {
        if (activeTypedBatches.TryGetValue(index, out ISharedLibraDexBatch? existing))
        {
            return (LibraDexBatch<TKey, TIdentity>)existing;
        }

        LibraDexBatch<TKey, TIdentity> created = new(index, batch, ownsDurabilityBatch: false);
        activeTypedBatches.Add(index, created);
        return created;
    }

    /// <summary>
    /// Flushes all typed batch caches into the active group durability batch before the session publishes writes.<br/>
    /// The DataKernel commit remains centralized in the group manager so all participating indexes share one publication boundary.<br/>
    /// </summary>
    private void PrepareActiveTypedBatchesForCommit()
    {
        foreach (ISharedLibraDexBatch batch in activeTypedBatches.Values)
        {
            batch.PrepareSharedDurabilityCommit();
        }
    }

    /// <summary>
    /// Completes and clears all typed batch caches after a successful group durability commit.<br/>
    /// Cache completion happens after publication so clean-shelf reuse cannot observe bytes that failed to commit.<br/>
    /// </summary>
    private void CompleteActiveTypedBatchesAfterCommit()
    {
        foreach (ISharedLibraDexBatch batch in activeTypedBatches.Values)
        {
            batch.CompleteSharedDurabilityCommit();
        }

        activeTypedBatches.Clear();
    }

    /// <summary>
    /// Clears all typed batch caches when the owning group batch is being abandoned.<br/>
    /// The session durability batch remains responsible for discarding pending DataKernel writes.<br/>
    /// </summary>
    private void AbortActiveTypedBatches()
    {
        foreach (ISharedLibraDexBatch batch in activeTypedBatches.Values)
        {
            batch.AbortSharedDurabilityBatch();
        }

        activeTypedBatches.Clear();
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
