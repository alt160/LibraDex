namespace LibraDex;

/// <summary>
/// Batches generic public index mutations by deferring durability publication until commit.<br/>
/// The batch keeps call sites in typed CLR values while dispatching to the concrete fixed-scalar routed storage path selected by the owning index.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexBatch<TKey, TIdentity> : IDisposable
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private readonly bool ownsDurabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long initialShelfRouteCreateCount;
    private bool completed;

    internal LibraDexBatch(LibraDexIndex<TKey, TIdentity> index, LibraDexFileSessionDurabilityBatch durabilityBatch)
        : this(index, durabilityBatch, ownsDurabilityBatch: true)
    {
    }

    internal LibraDexBatch(LibraDexIndex<TKey, TIdentity> index, LibraDexFileSessionDurabilityBatch durabilityBatch, bool ownsDurabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
        this.ownsDurabilityBatch = ownsDurabilityBatch;
    }

    /// <summary>
    /// Inserts one typed key and typed identity into the owning index without forcing a durable commit per item.<br/>
    /// The concrete storage path is selected once by the owning generic index and this method only performs the matching scalar encoding and routed insert.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.</param>
    /// <param name="identity">The typed identity value associated with the key.</param>
    /// <returns>The typed generic insert result.</returns>
    public LibraDexGenericInsertResult Insert(TKey key, TIdentity identity)
    {
        attemptedInsertCount++;
        bool allowDuplicateKeys = index.KeyContract == IndexKeys.NonUnique;
        return index.Shape switch
        {
            LibraDexGenericScalarShape.SS88 => RecordInsert(InsertScalar8Scalar8(key, identity, allowDuplicateKeys)),
            LibraDexGenericScalarShape.SS168 => RecordInsert(InsertScalar16Scalar8(key, identity, allowDuplicateKeys)),
            LibraDexGenericScalarShape.SS816 => RecordInsert(InsertScalar8Scalar16(key, identity, allowDuplicateKeys)),
            LibraDexGenericScalarShape.SS1616 => RecordInsert(InsertScalar16Scalar16(key, identity, allowDuplicateKeys)),
            LibraDexGenericScalarShape.FS328 => RecordInsert(InsertFixed32Scalar8(key, identity, allowDuplicateKeys)),
            LibraDexGenericScalarShape.FS3216 => RecordInsert(InsertFixed32Scalar16(key, identity, allowDuplicateKeys)),
            _ => throw new InvalidDataException($"Unsupported generic LibraDex shape {index.Shape}.")
        };
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the generic batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise SQL-style all-or-nothing item semantics.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public LibraDexGenericBatchCommitResult Commit()
    {
        if (!ownsDurabilityBatch)
        {
            throw new InvalidOperationException("A shared LibraDex batch cannot publish the owning durability boundary directly.");
        }

        (DataKernelCommitTelemetry commit, long deferredRequests) = durabilityBatch.Commit();
        completed = true;
        LibraDexGenericBatchCommitResult result = new(
            attemptedInsertCount,
            insertedCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit);
        index.Stats.RecordCommit(result);
        index.Catalog?.Stats.RecordCommit(result);
        return result;
    }

    /// <summary>
    /// Aborts the generic batch by discarding staged writes that were not published.<br/>
    /// This is a revert-to-current-backing-state operation and is intentionally heavier than a normal successful commit path.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.</returns>
    public LibraDexGenericBatchAbortResult Abort()
    {
        if (!ownsDurabilityBatch)
        {
            throw new InvalidOperationException("A shared LibraDex batch cannot abort the owning durability boundary directly.");
        }

        long deferredRequests = durabilityBatch.Abort();
        completed = true;
        return new LibraDexGenericBatchAbortResult(attemptedInsertCount, deferredRequests);
    }

    /// <summary>
    /// Aborts uncommitted staged writes when the batch is disposed without explicit commit or abort.<br/>
    /// This makes `using` scopes safe for early exits while keeping `Commit` as the only durability publication call.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed && ownsDurabilityBatch)
        {
            _ = Abort();
        }
    }

    private LibraDexGenericInsertResult InsertScalar8Scalar8(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        ulong encodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(key);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        byte rootPrefix = (byte)(encodedKey >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateScalar8Scalar8ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Scalar8Scalar8Profile.Default32KiB, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Scalar8Scalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar8Scalar8(index.RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, encodedKey, encodedIdentity, allowDuplicateKeys, maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar8Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertScalar16Scalar8(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        byte rootPrefix = (byte)(keyHigh >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateScalar16Scalar8ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Scalar16Scalar8Profile.Default32KiB, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Scalar16Scalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar16Scalar8(index.RootRouterOffset, Scalar16Scalar8Profile.Default32KiB, keyHigh, keyLow, encodedIdentity, allowDuplicateKeys, maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar16Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertScalar8Scalar16(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        ulong encodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(key);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        byte rootPrefix = (byte)(encodedKey >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateScalar8Scalar16ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Scalar8Scalar16Profile.Default32KiB, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Scalar8Scalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar8Scalar16(index.RootRouterOffset, Scalar8Scalar16Profile.Default32KiB, encodedKey, identityHigh, identityLow, allowDuplicateKeys, maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar8Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertScalar16Scalar16(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        byte rootPrefix = (byte)(keyHigh >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateScalar16Scalar16ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Scalar16Scalar16Profile.Default32KiB, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Scalar16Scalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar16Scalar16(index.RootRouterOffset, Scalar16Scalar16Profile.Default32KiB, keyHigh, keyLow, identityHigh, identityLow, allowDuplicateKeys, maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar16Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertFixed32Scalar8(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        byte rootPrefix = (byte)(key0 >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateFixed32Scalar8ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Fixed32Scalar8Profile.Default64KiB, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Fixed32Scalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedFixed32Scalar8(index.RootRouterOffset, Fixed32Scalar8Profile.Default64KiB, key0, key1, key2, key3, encodedIdentity, allowDuplicateKeys, maxRouterHops: 32);
        return CompleteInsert(result.InsertResult == Fixed32Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    /// <summary>
    /// Inserts one generic key/identity pair into the routed `FS32-16` physical shape.<br/>
    /// The public key must encode to four 64-bit fixed-key lanes and the public identity must encode to two 64-bit identity lanes.<br/>
    /// This path creates the first root route lazily, then uses the classified walked insert so later shelf splits preserve the full 16-byte identity value.<br/>
    /// </summary>
    /// <param name="key">The public key value to encode as a 32-byte fixed key.</param>
    /// <param name="identity">The public identity value to encode as a 16-byte scalar identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <returns>The generic insert result plus route-create and insert commit telemetry.</returns>
    private LibraDexGenericInsertResult InsertFixed32Scalar16(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        byte rootPrefix = (byte)(key0 >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateFixed32Scalar16ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Fixed32Scalar16Profile.Default64KiB, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Fixed32Scalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedFixed32Scalar16(index.RootRouterOffset, Fixed32Scalar16Profile.Default64KiB, key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys, maxRouterHops: 32);
        return CompleteInsert(result.InsertResult == Fixed32Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult CompleteInsert(bool inserted, bool createdInitialShelfRoute, DataKernelCommitTelemetry routeCreateCommit, DataKernelCommitTelemetry insertCommit)
    {
        if (inserted)
        {
            insertedCount++;
        }

        return new LibraDexGenericInsertResult(inserted, createdInitialShelfRoute, routeCreateCommit, insertCommit);
    }

    private LibraDexGenericInsertResult RecordInsert(LibraDexGenericInsertResult result)
    {
        index.Stats.RecordInsert(result);
        index.Catalog?.Stats.RecordInsert(result);
        return result;
    }
}
