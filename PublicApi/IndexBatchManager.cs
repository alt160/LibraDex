namespace LibraDex;

/// <summary>
/// Controls index-global batch mode for a public generic index.<br/>
/// Batch mode changes durability cadence only: normal index mutation methods continue to be used, staged writes remain owned by the index, and `Commit` publishes staged writes without implying SQL transaction rollback semantics.<br/>
/// The manager still uses the owning catalog session write window, so it does not make same-session writers independent.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type for the owning index.</typeparam>
/// <typeparam name="TIdentity">The public identity type for the owning index.</typeparam>
public sealed class IndexBatchManager<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private LibraDexBatch<TKey, TIdentity>? activeBatch;
    private LibraDexWriteIntent activeWriteIntent;

    internal IndexBatchManager(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
    }

    /// <summary>
    /// Gets whether index-global batch mode is currently enabled.<br/>
    /// When enabled, normal index inserts stage writes through the active batch until `Commit` or `CommitAndDisable` is called.<br/>
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Gets the write-intent hint used when the current batch manager cycle was enabled.<br/>
    /// The hint is advisory and lets storage code choose bulk, random, sorted, or latency-sensitive policy branches without changing public mutation calls.<br/>
    /// </summary>
    public LibraDexWriteIntent WriteIntent => activeWriteIntent;

    /// <summary>
    /// Gets the active typed batch for the owning index, or <see langword="null"/> when index-global batch mode is disabled.<br/>
    /// The owning index uses this internal typed reference to keep ordinary inserts on a shallow, allocation-free batch path without delegate dispatch or a second enabled-state guard.<br/>
    /// </summary>
    internal LibraDexBatch<TKey, TIdentity>? ActiveBatch => IsEnabled ? activeBatch : null;

    /// <summary>
    /// Enables index-global batch mode for subsequent writes.<br/>
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
        activeBatch = index.BeginBatch(writeIntent);
        try
        {
            index.Catalog.RegisterActiveIndexBatch(index.SlotIndex, index.ExactReversedProjectionSlotIndex, activeBatch);
        }
        catch
        {
            activeBatch.Dispose();
            activeBatch = null;
            activeWriteIntent = default;
            throw;
        }
        IsEnabled = true;
    }

    /// <summary>
    /// Publishes staged writes and immediately starts a new batch cycle while leaving batch mode enabled.<br/>
    /// This lets bulk callers choose their own commit cadence without switching back to per-insert durability after every publication boundary.<br/>
    /// </summary>
    /// <returns>The aggregate commit result for the staged writes that were just published.</returns>
    /// <exception cref="InvalidOperationException">Thrown when batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult Commit()
    {
        LibraDexBatch<TKey, TIdentity> batch = RequireActiveBatch();
        LibraDexGenericBatchCommitResult result = batch.Commit();
        index.Catalog.UnregisterActiveIndexBatch(index.SlotIndex, index.ExactReversedProjectionSlotIndex, batch);
        IsEnabled = false;
        try
        {
            activeBatch = index.BeginBatch(activeWriteIntent);
            index.Catalog.RegisterActiveIndexBatch(index.SlotIndex, index.ExactReversedProjectionSlotIndex, activeBatch);
            IsEnabled = true;
        }
        catch
        {
            activeBatch?.Dispose();
            activeBatch = null;
            activeWriteIntent = default;
            throw;
        }

        return result;
    }

    /// <summary>
    /// Publishes staged writes and immediately starts a new batch cycle while leaving batch mode enabled.<br/>
    /// This is the preferred spelling for LibraDex and Abraxas-facing code because batch-manager mode controls publication cadence, not a database transaction boundary.<br/>
    /// The current implementation delegates to <see cref="Commit"/> so existing telemetry, diagnostics, and compatibility behavior remain identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome for the staged writes that were just published.</returns>
    /// <exception cref="InvalidOperationException">Thrown when batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult Publish()
    {
        return Commit();
    }

    /// <summary>
    /// Publishes staged writes and returns the index to normal immediate-durability write behavior.<br/>
    /// This is the only public ending operation for batch-manager mode; there is no abort because the manager is about durability cadence rather than rollback semantics.<br/>
    /// </summary>
    /// <returns>The aggregate commit result for the final staged writes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult CommitAndDisable()
    {
        LibraDexBatch<TKey, TIdentity> batch = RequireActiveBatch();
        LibraDexGenericBatchCommitResult result = batch.Commit();
        index.Catalog.UnregisterActiveIndexBatch(index.SlotIndex, index.ExactReversedProjectionSlotIndex, batch);
        activeBatch = null;
        IsEnabled = false;
        activeWriteIntent = default;
        return result;
    }

    /// <summary>
    /// Publishes staged writes and returns the index to normal immediate-publication write behavior.<br/>
    /// This is the preferred spelling for ending batch-manager mode when callers want publication cadence without database commit wording.<br/>
    /// The current implementation delegates to <see cref="CommitAndDisable"/> so compatibility behavior remains identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome for the final staged writes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when batch mode is not enabled.</exception>
    public LibraDexGenericBatchCommitResult PublishAndDisable()
    {
        return CommitAndDisable();
    }

    private LibraDexBatch<TKey, TIdentity> RequireActiveBatch()
    {
        if (!IsEnabled || activeBatch is null)
        {
            throw new InvalidOperationException("The index BatchManager is not enabled.");
        }

        return activeBatch;
    }
}
