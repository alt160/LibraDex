using LibraDex.Layouts;
using LibraDex.Views;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LibraDex;

internal interface ISharedLibraDexBatch
{
    void PrepareSharedDurabilityCommit();

    void CompleteSharedDurabilityCommit();

    void AbortSharedDurabilityBatch();

    /// <summary>
    /// Stages and releases retained shelf images before another handle mutates a slot owned by this batch.<br/>
    /// The session durability boundary remains active and unpublished.<br/>
    /// </summary>
    void PrepareForExternalMutation();
}


/// <summary>
/// Batches generic public index mutations by deferring durability publication until commit.<br/>
/// The batch keeps call sites in typed CLR values while dispatching to the concrete fixed-scalar routed storage path selected by the owning index.<br/>
/// A batch is owned by one active catalog/session writer and should not be used as a concurrent writer or rollback boundary.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexBatch<TKey, TIdentity> : IDisposable, ISharedLibraDexBatch
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private readonly bool ownsDurabilityBatch;
    private Scalar8Scalar8Batch? scalar8Scalar8SharedBatch;
    private Scalar8Scalar8Index? scalar8Scalar8SharedIndex;
    private Dictionary<long, Scalar16Scalar8BatchShelfCacheEntry>? scalar16Scalar8ShelfCache;
    private Dictionary<Scalar16Scalar8BatchRouteCacheKey, Scalar16Scalar8RoutePathTarget>? scalar16Scalar8RouteTargetCache;
    private Dictionary<long, Scalar8Scalar16BatchShelfCacheEntry>? scalar8Scalar16ShelfCache;
    private Dictionary<Scalar8Scalar16BatchRouteCacheKey, Scalar8Scalar16RoutePathTarget>? scalar8Scalar16RouteTargetCache;
    private Dictionary<long, Scalar16Scalar16BatchShelfCacheEntry>? scalar16Scalar16ShelfCache;
    private Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar16RoutePathTarget>? scalar16Scalar16RouteTargetCache;
    private Dictionary<long, Fixed32Scalar8BatchShelfCacheEntry>? fixed32Scalar8ShelfCache;
    private Dictionary<Fixed32BatchRouteCacheKey, Fixed32Scalar8RoutePathTarget>? fixed32Scalar8RouteTargetCache;
    private Fixed32Scalar8RouteCursor fixed32Scalar8RouteCursor;
    private Fixed32Scalar8BatchShelfCacheEntry? fixed32Scalar8ActiveShelf;
    private long fixed32Scalar8ActiveShelfOffset;
    private Dictionary<long, Fixed32Scalar16BatchShelfCacheEntry>? fixed32Scalar16ShelfCache;
    private Dictionary<Fixed32BatchRouteCacheKey, Fixed32Scalar16RoutePathTarget>? fixed32Scalar16RouteTargetCache;
    private Fixed32Scalar16RouteCursor fixed32Scalar16RouteCursor;
    private Fixed32Scalar16BatchShelfCacheEntry? fixed32Scalar16ActiveShelf;
    private long fixed32Scalar16ActiveShelfOffset;
    private FixedScalarMonotonicRouteCursor fixedScalarMonotonicRouteCursor;
    private LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedIdentityKeyGuard;
    private long attemptedInsertCount;
    private long insertedCount;
    private long initialShelfRouteCreateCount;
    private long insertStatsBytesWritten;
    private readonly Fixed32BatchAttributionDiagnostics? fixed32Attribution;
    private readonly long fixed32AttributionBatchStartTicks;
    private readonly long fixed32AttributionBatchStartAllocatedBytes;
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
        if (index.Shape is LibraDexGenericScalarShape.FS328 or LibraDexGenericScalarShape.FS3216)
        {
            fixed32Attribution = Fixed32BatchAttributionDiagnostics.Current;
            if (fixed32Attribution is not null)
            {
                fixed32Attribution.BatchCount++;
                fixed32AttributionBatchStartTicks = Stopwatch.GetTimestamp();
                fixed32AttributionBatchStartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            }
        }
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
        LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedGuard = null;
        bool newlyTracked = false;
        if (index.IdentityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
        {
            stagedGuard = stagedIdentityKeyGuard ??= new LibraDexStagedIdentityKeyGuard<TKey, TIdentity>(index);
            if (!stagedGuard.CanInsert(identity, key, out newlyTracked))
            {
                return new LibraDexGenericInsertResult(false, false, default, default);
            }
        }

        bool allowDuplicateKeys = index.KeyContract == IndexKeys.NonUnique;
        LibraDexGenericInsertResult result;
        try
        {
            result = index.Shape switch
            {
                LibraDexGenericScalarShape.SS88 => InsertScalar8Scalar8(key, identity, allowDuplicateKeys),
                LibraDexGenericScalarShape.SS168 => InsertScalar16Scalar8(key, identity, allowDuplicateKeys),
                LibraDexGenericScalarShape.SS816 => InsertScalar8Scalar16(key, identity, allowDuplicateKeys),
                LibraDexGenericScalarShape.SS1616 => InsertScalar16Scalar16(key, identity, allowDuplicateKeys),
                LibraDexGenericScalarShape.FS328 => InsertFixed32Scalar8(key, identity, allowDuplicateKeys),
                LibraDexGenericScalarShape.FS3216 => InsertFixed32Scalar16(key, identity, allowDuplicateKeys),
                _ => throw new InvalidDataException($"Unsupported generic LibraDex shape {index.Shape}.")
            };
        }
        catch
        {
            stagedGuard?.CancelUnusedReservation(identity, newlyTracked);
            throw;
        }

        if (result.Inserted)
        {
            stagedGuard?.RecordInserted(identity, key);
            index.InsertExactReversedProjection(key, identity, durabilityBatch);
        }
        else
        {
            stagedGuard?.CancelUnusedReservation(identity, newlyTracked);
        }

        return result;
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

        long publishStartTicks = fixed32Attribution is null ? 0 : Stopwatch.GetTimestamp();
        PrepareSharedDurabilityCommit();
        (DataKernelCommitTelemetry commit, long deferredRequests, LibraDexBatchStorageDiagnostics storageDiagnostics) = durabilityBatch.Commit();
        completed = true;
        CompleteSharedDurabilityCommit();
        LibraDexGenericBatchCommitResult result = new(
            attemptedInsertCount,
            insertedCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            LibraDexOperationDiagnostics.FromDataKernel(commit),
            storageDiagnostics);
        index.Stats.RecordCommit(result);
        index.Catalog.Stats.RecordCommit(result);
        if (fixed32Attribution is not null)
        {
            fixed32Attribution.PublishTicks += Stopwatch.GetTimestamp() - publishStartTicks;
            fixed32Attribution.BatchTicks += Stopwatch.GetTimestamp() - fixed32AttributionBatchStartTicks;
            fixed32Attribution.BatchAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - fixed32AttributionBatchStartAllocatedBytes;
            fixed32Attribution.DeferredWriteRequests += deferredRequests;
            fixed32Attribution.CommittedBytes += commit.BytesWritten;
        }

        return result;
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the generic batch and closes this batch boundary.<br/>
    /// This is the preferred spelling for LibraDex and Abraxas-facing code because it describes reader visibility and file-backed durability cadence without implying SQL-style transaction commit or rollback semantics.<br/>
    /// The current implementation delegates to <see cref="Commit"/> so existing telemetry, storage diagnostics, and compatibility behavior remain identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel publication telemetry.</returns>
    public LibraDexGenericBatchCommitResult Publish()
    {
        return Commit();
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

        AbortSharedDurabilityBatch();
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

    /// <summary>
    /// Flushes any shape-specific shared-batch caches into the already active durability batch without publishing the DataKernel boundary.<br/>
    /// Catalog-level group batching calls this on every participating typed batch before it commits the single shared session batch.<br/>
    /// </summary>
    internal void PrepareSharedDurabilityCommit()
    {
        scalar8Scalar8SharedBatch?.PrepareSharedDurabilityCommit();
        FlushScalar16Scalar8ShelfCache();
        FlushScalar8Scalar16ShelfCache();
        FlushScalar16Scalar16ShelfCache();
        FlushFixed32Scalar8ShelfCache();
        FlushFixed32Scalar16ShelfCache();
    }

    /// <summary>
    /// Marks shape-specific shared-batch caches as successfully published after the owning catalog group commits the session batch.<br/>
    /// The current implementation keeps this explicit so cache cleanup never races ahead of the durable commit result.<br/>
    /// </summary>
    internal void CompleteSharedDurabilityCommit()
    {
        scalar8Scalar8SharedBatch?.CompleteSharedDurabilityCommit();
        scalar8Scalar8SharedBatch = null;
        scalar8Scalar8SharedIndex = null;
        StoreScalar16Scalar8CleanShelfCacheEntries();
        scalar16Scalar8ShelfCache?.Clear();
        scalar16Scalar8RouteTargetCache?.Clear();
        StoreScalar8Scalar16CleanShelfCacheEntries();
        scalar8Scalar16ShelfCache?.Clear();
        scalar8Scalar16RouteTargetCache?.Clear();
        StoreScalar16Scalar16CleanShelfCacheEntries();
        scalar16Scalar16ShelfCache?.Clear();
        scalar16Scalar16RouteTargetCache?.Clear();
        StoreFixed32Scalar8CleanShelfCacheEntries();
        if (fixed32Scalar8RouteCursor.TryGetRetained(out Fixed32Scalar8RoutePathTarget fixed32Scalar8PathTarget, out ulong fixed32Scalar8Key0, out ulong fixed32Scalar8Key1, out ulong fixed32Scalar8Key2, out ulong fixed32Scalar8Key3))
        {
            index.Session.StoreFixed32Scalar8BatchRouteHint(index.RootRouterOffset, fixed32Scalar8Key0, fixed32Scalar8Key1, fixed32Scalar8Key2, fixed32Scalar8Key3, fixed32Scalar8PathTarget);
        }
        fixed32Scalar8ShelfCache?.Clear();
        fixed32Scalar8RouteTargetCache?.Clear();
        fixed32Scalar8RouteCursor.Clear();
        StoreFixed32Scalar16CleanShelfCacheEntries();
        if (fixed32Scalar16RouteCursor.TryGetRetained(out Fixed32Scalar16RoutePathTarget fixed32Scalar16PathTarget, out ulong fixed32Scalar16Key0, out ulong fixed32Scalar16Key1, out ulong fixed32Scalar16Key2, out ulong fixed32Scalar16Key3))
        {
            index.Session.StoreFixed32Scalar16BatchRouteHint(index.RootRouterOffset, fixed32Scalar16Key0, fixed32Scalar16Key1, fixed32Scalar16Key2, fixed32Scalar16Key3, fixed32Scalar16PathTarget);
        }
        fixed32Scalar16ShelfCache?.Clear();
        fixed32Scalar16RouteTargetCache?.Clear();
        fixed32Scalar16RouteCursor.Clear();
        fixedScalarMonotonicRouteCursor.Clear();
        if (insertedCount != 0)
            index.InvalidateSingleKeyIdentityMapAfterStagedPublication();
        stagedIdentityKeyGuard?.ReleaseAllReservations();
        index.Stats.RecordBatchInserts(insertedCount, initialShelfRouteCreateCount, insertStatsBytesWritten);
        index.Catalog.Stats.RecordBatchInserts(insertedCount, initialShelfRouteCreateCount, insertStatsBytesWritten);
        completed = true;
    }

    /// <summary>
    /// Drops unpublished shape-specific shared-batch caches while leaving ownership of the outer durability batch with the caller.<br/>
    /// This is used by catalog group cancellation and by owned-batch abort before the session discards pending DataKernel writes.<br/>
    /// </summary>
    internal void AbortSharedDurabilityBatch()
    {
        scalar8Scalar8SharedBatch?.AbortSharedDurabilityBatch();
        scalar8Scalar8SharedBatch = null;
        scalar8Scalar8SharedIndex = null;
        scalar16Scalar8ShelfCache?.Clear();
        scalar16Scalar8RouteTargetCache?.Clear();
        scalar8Scalar16ShelfCache?.Clear();
        scalar8Scalar16RouteTargetCache?.Clear();
        scalar16Scalar16ShelfCache?.Clear();
        scalar16Scalar16RouteTargetCache?.Clear();
        fixed32Scalar8ShelfCache?.Clear();
        fixed32Scalar8RouteTargetCache?.Clear();
        fixed32Scalar8RouteCursor.Clear();
        fixed32Scalar16ShelfCache?.Clear();
        fixed32Scalar16RouteTargetCache?.Clear();
        fixed32Scalar16RouteCursor.Clear();
        fixedScalarMonotonicRouteCursor.Clear();
        stagedIdentityKeyGuard?.ReleaseAllReservations();
        completed = true;
    }

    /// <summary>
    /// Stages cached index-owned inserts and drops their local shelf images before a catalog-opened handle performs an exact mutation.<br/>
    /// Subsequent inserts rebuild their caches from the session's pending shelf state; this method never publishes or completes the batch.<br/>
    /// </summary>
    public void PrepareForExternalMutation()
    {
        if (completed || !ownsDurabilityBatch)
            throw new InvalidOperationException("Only an active index-owned batch can prepare an external exact mutation.");

        PrepareSharedDurabilityCommit();
        scalar16Scalar8ShelfCache?.Clear();
        scalar16Scalar8RouteTargetCache?.Clear();
        scalar8Scalar16ShelfCache?.Clear();
        scalar8Scalar16RouteTargetCache?.Clear();
        scalar16Scalar16ShelfCache?.Clear();
        scalar16Scalar16RouteTargetCache?.Clear();
        fixed32Scalar8ShelfCache?.Clear();
        fixed32Scalar8RouteTargetCache?.Clear();
        fixed32Scalar8RouteCursor.Clear();
        fixed32Scalar8ActiveShelf = null;
        fixed32Scalar8ActiveShelfOffset = 0;
        fixed32Scalar16ShelfCache?.Clear();
        fixed32Scalar16RouteTargetCache?.Clear();
        fixed32Scalar16RouteCursor.Clear();
        fixed32Scalar16ActiveShelf = null;
        fixed32Scalar16ActiveShelfOffset = 0;
        fixedScalarMonotonicRouteCursor.Clear();
    }

    void ISharedLibraDexBatch.PrepareSharedDurabilityCommit()
    {
        PrepareSharedDurabilityCommit();
    }

    void ISharedLibraDexBatch.CompleteSharedDurabilityCommit()
    {
        CompleteSharedDurabilityCommit();
    }

    void ISharedLibraDexBatch.AbortSharedDurabilityBatch()
    {
        AbortSharedDurabilityBatch();
    }

    private LibraDexGenericInsertResult InsertScalar8Scalar8(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        ulong encodedKey = index.EncodeKey8(key);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        if (!ownsDurabilityBatch)
        {
            Scalar8Scalar8EncodedInsertResult sharedResult = GetOrCreateScalar8Scalar8SharedBatch().InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
            return CompleteInsert(
                sharedResult.Outcome == Scalar8Scalar8EncodedInsertOutcome.Inserted,
                sharedResult.CreatedInitialShelfRoute,
                default,
                default);
        }

        Scalar8Scalar8Profile profile = index.GetScalar8Scalar8Profile();
        byte rootPrefix = (byte)(encodedKey >> 56);
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
        {
            (_, routeCreateCommit) = index.Session.CreateScalar8Scalar8ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, profile, itemCount: 0);
            createdInitialShelfRoute = true;
            initialShelfRouteCreateCount++;
        }

        Scalar8Scalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar8Scalar8(index.RootRouterOffset, profile, encodedKey, encodedIdentity, allowDuplicateKeys, maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar8Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    /// <summary>
    /// Creates the encoded `SS8-8` shared-batch adapter used by catalog group batching for this generic index.<br/>
    /// The adapter owns no session lifetime and only keeps shelf-local mutation caches until the outer group batch prepares and publishes its durability boundary.<br/>
    /// </summary>
    /// <returns>The cached encoded batch adapter for this generic batch.</returns>
    private Scalar8Scalar8Batch GetOrCreateScalar8Scalar8SharedBatch()
    {
        if (scalar8Scalar8SharedBatch is not null)
        {
            return scalar8Scalar8SharedBatch;
        }

        Scalar8Scalar8Profile profile = index.GetScalar8Scalar8Profile();
        scalar8Scalar8SharedIndex = new Scalar8Scalar8Index(
            index.Session,
            new Scalar8Scalar8IndexHandle(index.RootRouterOffset, profile),
            index.SlotIndex,
            index.Name,
            ownsSession: false);
        scalar8Scalar8SharedBatch = new Scalar8Scalar8Batch(scalar8Scalar8SharedIndex, durabilityBatch, ownsDurabilityBatch: false);
        return scalar8Scalar8SharedBatch;
    }

    private LibraDexGenericInsertResult InsertScalar16Scalar8(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        Dictionary<Scalar16Scalar8BatchRouteCacheKey, Scalar16Scalar8RoutePathTarget> routeCache = scalar16Scalar8RouteTargetCache ??= [];
        Dictionary<long, Scalar16Scalar8BatchShelfCacheEntry> shelfCache = scalar16Scalar8ShelfCache ??= [];
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        Scalar16Scalar8RoutePathTarget pathTarget;
        if (fixedScalarMonotonicRouteCursor.TryReuse16(index.Session, keyHigh, keyLow, out FixedScalarMonotonicRoute route))
        {
            pathTarget = new Scalar16Scalar8RoutePathTarget(
                new Scalar16Scalar8RouteTarget(Scalar16Scalar8RouteTargetKind.Shelf, route.TargetOffset, route.RouterDepth, route.AllocationClassId),
                route.ParentRouterOffset,
                route.ParentPrefixByte);
        }
        else if (!TryGetScalar16Scalar8CachedRouteTarget(routeCache, keyHigh, keyLow, out pathTarget))
        {
            byte rootPrefix = (byte)(keyHigh >> 56);
            if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
            {
                (_, routeCreateCommit) = index.Session.CreateScalar16Scalar8ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, profile, itemCount: 0);
                createdInitialShelfRoute = true;
                initialShelfRouteCreateCount++;
            }

            pathTarget = index.Session.WalkScalar16Scalar8RoutePathTarget(index.RootRouterOffset, keyHigh, keyLow, maxRouterHops: 8);
            if (pathTarget.Target.Kind != Scalar16Scalar8RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic SS16-8 batch route did not terminate at a shelf.");
            }

            routeCache[Scalar16Scalar8BatchRouteCacheKey.Create(keyHigh, keyLow, pathTarget.Target.RouterDepth)] = pathTarget;
        }

        Scalar16Scalar8RouteTarget target = pathTarget.Target;
        fixedScalarMonotonicRouteCursor.Set16(
            index.Session,
            keyHigh,
            keyLow,
            target.Offset,
            target.RouterDepth,
            target.AllocationClassId,
            pathTarget.ParentRouterOffset,
            pathTarget.RoutePrefixByte);

        if (!shelfCache.TryGetValue(target.Offset, out Scalar16Scalar8BatchShelfCacheEntry? entry))
        {
            entry = new Scalar16Scalar8BatchShelfCacheEntry(index.Session.ReadScalar16Scalar8ShelfBytesForBatch(target.Offset, profile));
            shelfCache[target.Offset] = entry;
        }

        Scalar16Scalar8 shelf = new(entry.Bytes, profile);
        Scalar16Scalar8InsertResult insertResult = shelf.InsertWithMutationBounds(keyHigh, keyLow, encodedIdentity, allowDuplicateKeys, out Scalar16Scalar8MutationBounds mutationBounds);
        if (insertResult != Scalar16Scalar8InsertResult.Full)
        {
            if (insertResult == Scalar16Scalar8InsertResult.Inserted)
            {
                entry.Dirty = true;
                entry.Include(mutationBounds);
            }

            return CompleteInsert(insertResult == Scalar16Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, default);
        }

        shelfCache.Remove(target.Offset);
        routeCache.Clear();
        fixedScalarMonotonicRouteCursor.Clear();
        Scalar16Scalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar16Scalar8FromShelfImage(
            index.RootRouterOffset,
            profile,
            target.Offset,
            entry.Bytes,
            keyHigh,
            keyLow,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar16Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertScalar8Scalar16(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        ulong encodedKey = index.EncodeKey8(key);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Scalar8Scalar16Profile profile = index.GetScalar8Scalar16Profile();
        Dictionary<Scalar8Scalar16BatchRouteCacheKey, Scalar8Scalar16RoutePathTarget> routeCache = scalar8Scalar16RouteTargetCache ??= [];
        Dictionary<long, Scalar8Scalar16BatchShelfCacheEntry> shelfCache = scalar8Scalar16ShelfCache ??= [];
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        Scalar8Scalar16RoutePathTarget pathTarget;
        if (fixedScalarMonotonicRouteCursor.TryReuse8(index.Session, encodedKey, out FixedScalarMonotonicRoute route))
        {
            pathTarget = new Scalar8Scalar16RoutePathTarget(
                new Scalar8Scalar16RouteTarget(Scalar8Scalar16RouteTargetKind.Shelf, route.TargetOffset, route.RouterDepth, route.AllocationClassId),
                route.ParentRouterOffset,
                route.ParentPrefixByte);
        }
        else if (!TryGetScalar8Scalar16CachedRouteTarget(routeCache, encodedKey, out pathTarget))
        {
            byte rootPrefix = (byte)(encodedKey >> 56);
            if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
            {
                (_, routeCreateCommit) = index.Session.CreateScalar8Scalar16ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, profile, itemCount: 0);
                createdInitialShelfRoute = true;
                initialShelfRouteCreateCount++;
            }

            pathTarget = index.Session.WalkScalar8Scalar16RoutePathTarget(index.RootRouterOffset, encodedKey, maxRouterHops: 8);
            if (pathTarget.Target.Kind != Scalar8Scalar16RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic SS8-16 batch route did not terminate at a shelf.");
            }

            routeCache[Scalar8Scalar16BatchRouteCacheKey.Create(encodedKey, pathTarget.Target.RouterDepth)] = pathTarget;
        }

        Scalar8Scalar16RouteTarget target = pathTarget.Target;
        fixedScalarMonotonicRouteCursor.Set8(
            index.Session,
            encodedKey,
            target.Offset,
            target.RouterDepth,
            target.AllocationClassId,
            pathTarget.ParentRouterOffset,
            pathTarget.ParentPrefixByte);

        if (!shelfCache.TryGetValue(target.Offset, out Scalar8Scalar16BatchShelfCacheEntry? entry))
        {
            entry = new Scalar8Scalar16BatchShelfCacheEntry(index.Session.ReadScalar8Scalar16ShelfBytesForBatch(target.Offset, profile));
            shelfCache[target.Offset] = entry;
        }

        Scalar8Scalar16 shelf = new(entry.Bytes, profile);
        Scalar8Scalar16InsertResult insertResult = shelf.InsertWithMutationBounds(encodedKey, identityHigh, identityLow, allowDuplicateKeys, out Scalar8Scalar16MutationBounds mutationBounds);
        if (insertResult != Scalar8Scalar16InsertResult.Full)
        {
            if (insertResult == Scalar8Scalar16InsertResult.Inserted)
            {
                entry.Dirty = true;
                entry.Include(mutationBounds);
            }

            return CompleteInsert(insertResult == Scalar8Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, default);
        }

        shelfCache.Remove(target.Offset);
        routeCache.Clear();
        fixedScalarMonotonicRouteCursor.Clear();
        Scalar8Scalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar8Scalar16FromShelfImage(
            index.RootRouterOffset,
            profile,
            target.Offset,
            entry.Bytes,
            encodedKey,
            identityHigh,
            identityLow,
            allowDuplicateKeys,
            maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar8Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertScalar16Scalar16(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Scalar16Scalar16Profile profile = index.GetScalar16Scalar16Profile();
        Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar16RoutePathTarget> routeCache = scalar16Scalar16RouteTargetCache ??= [];
        Dictionary<long, Scalar16Scalar16BatchShelfCacheEntry> shelfCache = scalar16Scalar16ShelfCache ??= [];
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        Scalar16Scalar16RoutePathTarget pathTarget;
        if (fixedScalarMonotonicRouteCursor.TryReuse16(index.Session, keyHigh, keyLow, out FixedScalarMonotonicRoute route))
        {
            pathTarget = new Scalar16Scalar16RoutePathTarget(
                new Scalar16Scalar16RouteTarget(Scalar16Scalar16RouteTargetKind.Shelf, route.TargetOffset, route.RouterDepth, route.AllocationClassId),
                route.ParentRouterOffset,
                route.ParentPrefixByte);
        }
        else if (!TryGetScalar16Scalar16CachedRouteTarget(routeCache, keyHigh, keyLow, out pathTarget))
        {
            byte rootPrefix = (byte)(keyHigh >> 56);
            if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
            {
                (_, routeCreateCommit) = index.Session.CreateScalar16Scalar16ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, profile, itemCount: 0);
                createdInitialShelfRoute = true;
                initialShelfRouteCreateCount++;
            }

            pathTarget = index.Session.WalkScalar16Scalar16RoutePathTarget(index.RootRouterOffset, keyHigh, keyLow, maxRouterHops: 8);
            if (pathTarget.Target.Kind != Scalar16Scalar16RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic SS16-16 batch route did not terminate at a shelf.");
            }

            routeCache[Scalar16Scalar16BatchRouteCacheKey.Create(keyHigh, keyLow, pathTarget.Target.RouterDepth)] = pathTarget;
        }

        Scalar16Scalar16RouteTarget target = pathTarget.Target;
        fixedScalarMonotonicRouteCursor.Set16(
            index.Session,
            keyHigh,
            keyLow,
            target.Offset,
            target.RouterDepth,
            target.AllocationClassId,
            pathTarget.ParentRouterOffset,
            pathTarget.ParentPrefixByte);

        if (!shelfCache.TryGetValue(target.Offset, out Scalar16Scalar16BatchShelfCacheEntry? entry))
        {
            entry = new Scalar16Scalar16BatchShelfCacheEntry(index.Session.ReadScalar16Scalar16ShelfBytesForBatch(target.Offset, profile));
            shelfCache[target.Offset] = entry;
        }

        Scalar16Scalar16 shelf = new(entry.Bytes, profile);
        Scalar16Scalar16InsertResult insertResult = shelf.InsertWithMutationBounds(keyHigh, keyLow, identityHigh, identityLow, allowDuplicateKeys, out Scalar16Scalar16MutationBounds mutationBounds);
        if (insertResult != Scalar16Scalar16InsertResult.Full)
        {
            if (insertResult == Scalar16Scalar16InsertResult.Inserted)
            {
                entry.Dirty = true;
                entry.Include(mutationBounds);
            }

            return CompleteInsert(insertResult == Scalar16Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, default);
        }

        shelfCache.Remove(target.Offset);
        routeCache.Clear();
        fixedScalarMonotonicRouteCursor.Clear();
        Scalar16Scalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedScalar16Scalar16FromShelfImage(
            index.RootRouterOffset,
            profile,
            target.Offset,
            entry.Bytes,
            keyHigh,
            keyLow,
            identityHigh,
            identityLow,
            allowDuplicateKeys,
            maxRouterHops: 8);
        return CompleteInsert(result.InsertResult == Scalar16Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult InsertFixed32Scalar8(TKey key, TIdentity identity, bool allowDuplicateKeys)
    {
        Fixed32BatchAttributionDiagnostics? attribution = fixed32Attribution;
        long routeStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        Fixed32Scalar8Profile profile = index.GetFixed32Scalar8Profile();
        Dictionary<Fixed32BatchRouteCacheKey, Fixed32Scalar8RoutePathTarget> routeCache = fixed32Scalar8RouteTargetCache ??= [];
        Dictionary<long, Fixed32Scalar8BatchShelfCacheEntry> shelfCache = fixed32Scalar8ShelfCache ??= [];
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        Fixed32Scalar8RoutePathTarget pathTarget;
        bool walkedRoute = false;
        if (!fixed32Scalar8RouteCursor.TryGet(index.Session, key0, key1, key2, key3, out pathTarget) &&
            !TryGetCachedFixed32Scalar8RouteTarget(routeCache, key0, key1, key2, key3, out pathTarget) &&
            !index.Session.TryGetFixed32Scalar8BatchRouteHint(index.RootRouterOffset, key0, key1, key2, key3, out pathTarget))
        {
            walkedRoute = true;
            byte rootPrefix = (byte)(key0 >> 56);
            if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
            {
                (_, routeCreateCommit) = index.Session.CreateFixed32Scalar8ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, profile, itemCount: 0);
                createdInitialShelfRoute = true;
                initialShelfRouteCreateCount++;
            }

            pathTarget = fixed32Scalar8RouteCursor.WalkFromRoot(index.Session, index.RootRouterOffset, key0, key1, key2, key3);
            Fixed32Scalar8RouteTarget walkedTarget = pathTarget.Target;
            if (walkedTarget.Kind != Fixed32Scalar8RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic FS32-8 batch route did not resolve to a shelf.");
            }

            routeCache[Fixed32BatchRouteCacheKey.Create(key0, key1, key2, key3, walkedTarget.RouterDepth)] = pathTarget;
        }

        if (attribution is not null)
        {
            attribution.RouteTicks += Stopwatch.GetTimestamp() - routeStartTicks;
            if (walkedRoute)
                attribution.RootRouteWalks++;
            else
                attribution.FastRouteResolutions++;
        }

        fixed32Scalar8RouteCursor.Set(index.Session, key0, key1, key2, key3, pathTarget);
        Fixed32Scalar8RouteTarget target = pathTarget.Target;

        Fixed32Scalar8BatchShelfCacheEntry? entry = fixed32Scalar8ActiveShelf;
        if (entry is null || fixed32Scalar8ActiveShelfOffset != target.Offset)
        {
            if (!shelfCache.TryGetValue(target.Offset, out entry))
            {
                long shelfReadStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
                byte[] shelfBytes = index.Session.ReadFixed32Scalar8ShelfBytesForBatch(target.Offset, profile);
                entry = new Fixed32Scalar8BatchShelfCacheEntry(shelfBytes);
                shelfCache[target.Offset] = entry;
                if (attribution is not null)
                {
                    attribution.ShelfReads++;
                    attribution.ShelfReadTicks += Stopwatch.GetTimestamp() - shelfReadStartTicks;
                }
            }
            else if (attribution is not null)
                attribution.ShelfCacheHits++;

            fixed32Scalar8ActiveShelf = entry;
            fixed32Scalar8ActiveShelfOffset = target.Offset;
        }
        else if (attribution is not null)
            attribution.ActiveShelfHits++;

        Fixed32Scalar8 shelf = new(entry.Bytes, profile);
        long insertStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        Fixed32Scalar8InsertResult insertResult = shelf.InsertWithMutationBounds(key0, key1, key2, key3, encodedIdentity, allowDuplicateKeys, out Fixed32Scalar8MutationBounds mutationBounds);
        if (attribution is not null)
        {
            attribution.InsertTicks += Stopwatch.GetTimestamp() - insertStartTicks;
            attribution.InsertAttempts++;
        }
        if (insertResult != Fixed32Scalar8InsertResult.Full)
        {
            if (insertResult == Fixed32Scalar8InsertResult.Inserted)
            {
                if (attribution is not null)
                    attribution.InPlaceInserts++;
                entry.Dirty = true;
                entry.Include(mutationBounds);
            }
            else if (attribution is not null)
                attribution.NonInsertOutcomes++;

            return CompleteInsert(insertResult == Fixed32Scalar8InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, default);
        }

        shelfCache.Remove(target.Offset);
        fixed32Scalar8ActiveShelf = null;
        fixed32Scalar8ActiveShelfOffset = 0;
        routeCache.Clear();
        fixed32Scalar8RouteCursor.Clear();
        long splitStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        Fixed32Scalar8RoutedInsertResult result = index.Session.SplitRoutedFixed32Scalar8FromShelfImage(pathTarget, profile, entry.Bytes, key0, key1, key2, key3, encodedIdentity, allowDuplicateKeys);
        if (attribution is not null)
            attribution.Record(result.Kind, Stopwatch.GetTimestamp() - splitStartTicks);
        if (result.InsertResult == Fixed32Scalar8InsertResult.Inserted)
        {
            if (result.Kind == Fixed32Scalar8RoutedInsertKind.WalkedSortedTailSplit)
            {
                entry.RetainSortedLeft(profile);
                shelfCache[result.LeftShelfOffset] = entry;
            }

            long continuationStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
            Fixed32Scalar8RoutePathTarget continuation = fixed32Scalar8RouteCursor.WalkFromRoot(index.Session, index.RootRouterOffset, key0, key1, key2, key3);
            if (attribution is not null)
                attribution.ContinuationTicks += Stopwatch.GetTimestamp() - continuationStartTicks;
            if (continuation.Target.Kind != Fixed32Scalar8RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic FS32-8 split continuation did not resolve to a shelf.");
            }

            fixed32Scalar8RouteCursor.Set(index.Session, key0, key1, key2, key3, continuation);
            routeCache[Fixed32BatchRouteCacheKey.Create(key0, key1, key2, key3, continuation.Target.RouterDepth)] = continuation;
        }

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
        Fixed32BatchAttributionDiagnostics? attribution = fixed32Attribution;
        long routeStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Fixed32Scalar16Profile profile = index.GetFixed32Scalar16Profile();
        Dictionary<Fixed32BatchRouteCacheKey, Fixed32Scalar16RoutePathTarget> routeCache = fixed32Scalar16RouteTargetCache ??= [];
        Dictionary<long, Fixed32Scalar16BatchShelfCacheEntry> shelfCache = fixed32Scalar16ShelfCache ??= [];
        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        Fixed32Scalar16RoutePathTarget pathTarget;
        bool walkedRoute = false;
        if (!fixed32Scalar16RouteCursor.TryGet(index.Session, key0, key1, key2, key3, out pathTarget) &&
            !TryGetCachedFixed32Scalar16RouteTarget(routeCache, key0, key1, key2, key3, out pathTarget) &&
            !index.Session.TryGetFixed32Scalar16BatchRouteHint(index.RootRouterOffset, key0, key1, key2, key3, out pathTarget))
        {
            walkedRoute = true;
            byte rootPrefix = (byte)(key0 >> 56);
            if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) == 0)
            {
                (_, routeCreateCommit) = index.Session.CreateFixed32Scalar16ShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, profile, itemCount: 0);
                createdInitialShelfRoute = true;
                initialShelfRouteCreateCount++;
            }

            pathTarget = fixed32Scalar16RouteCursor.WalkFromRoot(index.Session, index.RootRouterOffset, key0, key1, key2, key3);
            Fixed32Scalar16RouteTarget walkedTarget = pathTarget.Target;
            if (walkedTarget.Kind != Fixed32Scalar16RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic FS32-16 batch route did not resolve to a shelf.");
            }

            routeCache[Fixed32BatchRouteCacheKey.Create(key0, key1, key2, key3, walkedTarget.RouterDepth)] = pathTarget;
        }

        if (attribution is not null)
        {
            attribution.RouteTicks += Stopwatch.GetTimestamp() - routeStartTicks;
            if (walkedRoute)
                attribution.RootRouteWalks++;
            else
                attribution.FastRouteResolutions++;
        }

        fixed32Scalar16RouteCursor.Set(index.Session, key0, key1, key2, key3, pathTarget);
        Fixed32Scalar16RouteTarget target = pathTarget.Target;

        Fixed32Scalar16BatchShelfCacheEntry? entry = fixed32Scalar16ActiveShelf;
        if (entry is null || fixed32Scalar16ActiveShelfOffset != target.Offset)
        {
            if (!shelfCache.TryGetValue(target.Offset, out entry))
            {
                long shelfReadStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
                byte[] shelfBytes = index.Session.ReadFixed32Scalar16ShelfBytesForBatch(target.Offset, profile);
                entry = new Fixed32Scalar16BatchShelfCacheEntry(shelfBytes);
                shelfCache[target.Offset] = entry;
                if (attribution is not null)
                {
                    attribution.ShelfReads++;
                    attribution.ShelfReadTicks += Stopwatch.GetTimestamp() - shelfReadStartTicks;
                }
            }
            else if (attribution is not null)
                attribution.ShelfCacheHits++;

            fixed32Scalar16ActiveShelf = entry;
            fixed32Scalar16ActiveShelfOffset = target.Offset;
        }
        else if (attribution is not null)
            attribution.ActiveShelfHits++;

        Fixed32Scalar16 shelf = new(entry.Bytes, profile);
        long insertStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        Fixed32Scalar16InsertResult insertResult = shelf.InsertWithMutationBounds(key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys, out Fixed32Scalar16MutationBounds mutationBounds);
        if (attribution is not null)
        {
            attribution.InsertTicks += Stopwatch.GetTimestamp() - insertStartTicks;
            attribution.InsertAttempts++;
        }
        if (insertResult != Fixed32Scalar16InsertResult.Full)
        {
            if (insertResult == Fixed32Scalar16InsertResult.Inserted)
            {
                if (attribution is not null)
                    attribution.InPlaceInserts++;
                entry.Dirty = true;
                entry.Include(mutationBounds);
            }
            else if (attribution is not null)
                attribution.NonInsertOutcomes++;

            return CompleteInsert(insertResult == Fixed32Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, default);
        }

        shelfCache.Remove(target.Offset);
        fixed32Scalar16ActiveShelf = null;
        fixed32Scalar16ActiveShelfOffset = 0;
        routeCache.Clear();
        fixed32Scalar16RouteCursor.Clear();
        long splitStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        Fixed32Scalar16RoutedInsertResult result = index.Session.SplitRoutedFixed32Scalar16FromShelfImage(pathTarget, profile, entry.Bytes, key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys);
        if (attribution is not null)
            attribution.Record(result.Kind, Stopwatch.GetTimestamp() - splitStartTicks);
        if (result.InsertResult == Fixed32Scalar16InsertResult.Inserted)
        {
            if (result.Kind == Fixed32Scalar16RoutedInsertKind.WalkedSortedTailSplit)
            {
                entry.RetainSortedLeft(profile);
                shelfCache[result.LeftShelfOffset] = entry;
            }

            long continuationStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
            Fixed32Scalar16RoutePathTarget continuation = fixed32Scalar16RouteCursor.WalkFromRoot(index.Session, index.RootRouterOffset, key0, key1, key2, key3);
            if (attribution is not null)
                attribution.ContinuationTicks += Stopwatch.GetTimestamp() - continuationStartTicks;
            if (continuation.Target.Kind != Fixed32Scalar16RouteTargetKind.Shelf)
            {
                throw new InvalidDataException("The generic FS32-16 split continuation did not resolve to a shelf.");
            }

            fixed32Scalar16RouteCursor.Set(index.Session, key0, key1, key2, key3, continuation);
            routeCache[Fixed32BatchRouteCacheKey.Create(key0, key1, key2, key3, continuation.Target.RouterDepth)] = continuation;
        }

        return CompleteInsert(result.InsertResult == Fixed32Scalar16InsertResult.Inserted, createdInitialShelfRoute, routeCreateCommit, result.Commit);
    }

    private LibraDexGenericInsertResult CompleteInsert(bool inserted, bool createdInitialShelfRoute, DataKernelCommitTelemetry routeCreateCommit, DataKernelCommitTelemetry insertCommit)
    {
        if (inserted)
        {
            insertedCount++;
        }

        insertStatsBytesWritten += routeCreateCommit.BytesWritten + insertCommit.BytesWritten;

        return new LibraDexGenericInsertResult(
            inserted,
            createdInitialShelfRoute,
            LibraDexOperationDiagnostics.FromDataKernel(routeCreateCommit),
            LibraDexOperationDiagnostics.FromDataKernel(insertCommit));
    }

    /// <summary>
    /// Resolves an `SS16-8` shelf from the batch-local route cache using the longest matching encoded-key prefix.<br/>
    /// Structural split fallbacks clear the cache, so a hit always describes topology observed after the most recent structural mutation in this batch.<br/>
    /// </summary>
    /// <param name="cache">The batch-local route-target cache.<br/></param>
    /// <param name="encodedKeyHigh">The encoded high key lane.<br/></param>
    /// <param name="encodedKeyLow">The encoded low key lane.<br/></param>
    /// <param name="target">Receives the cached shelf target when one is available.<br/></param>
    /// <returns><see langword="true"/> when a matching shelf target was found; otherwise <see langword="false"/>.<br/></returns>
    private static bool TryGetScalar16Scalar8CachedRouteTarget(
        Dictionary<Scalar16Scalar8BatchRouteCacheKey, Scalar16Scalar8RoutePathTarget> cache,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        out Scalar16Scalar8RoutePathTarget target)
    {
        for (byte depth = 15; depth > 0; depth--)
        {
            if (cache.TryGetValue(Scalar16Scalar8BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Scalar16Scalar8BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, 0), out target);
    }

    /// <summary>
    /// Stages accumulated `SS16-8` shelf mutation ranges into the active session durability batch.<br/>
    /// Each shelf is retained and mutated once during insertion, then its header, slot, and item ranges are published once at the caller-selected batch boundary.<br/>
    /// </summary>
    private void FlushScalar16Scalar8ShelfCache()
    {
        if (scalar16Scalar8ShelfCache is null || scalar16Scalar8ShelfCache.Count == 0)
        {
            return;
        }

        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        foreach (KeyValuePair<long, Scalar16Scalar8BatchShelfCacheEntry> pair in scalar16Scalar8ShelfCache)
        {
            Scalar16Scalar8BatchShelfCacheEntry entry = pair.Value;
            if (!entry.Dirty)
            {
                continue;
            }

            Scalar16Scalar8ReadOnly readOnly = new(entry.Bytes, profile);
            if (!readOnly.IsValid)
            {
                throw new InvalidDataException("The generic SS16-8 batch attempted to publish an invalid cached shelf image.");
            }

            Scalar16Scalar8BatchChangedSpan header = entry.GetHeaderSpan();
            Scalar16Scalar8BatchChangedSpan slots = entry.GetSlotSpan();
            Scalar16Scalar8BatchChangedSpan item = entry.GetItemSpan();
            int deltaBytes = header.Length + slots.Length + item.Length;
            if (deltaBytes >= profile.ShelfExtentSize)
            {
                _ = index.Session.StageScalar16Scalar8ShelfRewriteForBatch(pair.Key, profile, entry.Bytes);
                continue;
            }

            StageScalar16Scalar8ChangedSpan(pair.Key, profile, entry.Bytes, header);
            StageScalar16Scalar8ChangedSpan(pair.Key, profile, entry.Bytes, slots);
            StageScalar16Scalar8ChangedSpan(pair.Key, profile, entry.Bytes, item);
        }
    }

    /// <summary>
    /// Stages one positive changed span from a retained `SS16-8` shelf image.<br/>
    /// Empty spans are ignored so unchanged shelf regions do not create deferred commit requests.<br/>
    /// </summary>
    /// <param name="shelfOffset">The physical shelf offset owning the changed bytes.<br/></param>
    /// <param name="profile">The active `SS16-8` shelf profile.<br/></param>
    /// <param name="shelfBytes">The retained final shelf image.<br/></param>
    /// <param name="span">The shelf-relative changed range to stage.<br/></param>
    private void StageScalar16Scalar8ChangedSpan(
        long shelfOffset,
        Scalar16Scalar8Profile profile,
        byte[] shelfBytes,
        Scalar16Scalar8BatchChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        _ = index.Session.StageScalar16Scalar8ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            shelfBytes.AsSpan(span.Offset, span.Length));
    }

    /// <summary>
    /// Promotes successfully published `SS16-8` shelf images into the session-local clean cache.<br/>
    /// Promotion occurs only after the owning durability commit succeeds, so later batches never observe unpublished shelf state through the clean cache.<br/>
    /// </summary>
    private void StoreScalar16Scalar8CleanShelfCacheEntries()
    {
        if (scalar16Scalar8ShelfCache is null || scalar16Scalar8ShelfCache.Count == 0)
        {
            return;
        }

        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        foreach (KeyValuePair<long, Scalar16Scalar8BatchShelfCacheEntry> pair in scalar16Scalar8ShelfCache)
        {
            index.Session.StoreScalar16Scalar8CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
        }
    }

    /// <summary>
    /// Retains one mutable `SS16-8` shelf image and the conservative regions changed by cached batch inserts.<br/>
    /// The retained image removes per-item full-shelf allocation while the accumulated ranges preserve narrow publication at the batch boundary.<br/>
    /// </summary>
    private sealed class Scalar16Scalar8BatchShelfCacheEntry
    {
        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        /// <summary>
        /// Creates a cache entry over one caller-owned mutable shelf image.<br/>
        /// The byte array remains owned by this batch until publication, abort, or structural split fallback removes the entry.<br/>
        /// </summary>
        /// <param name="bytes">The validated mutable shelf image retained by the batch.<br/></param>
        internal Scalar16Scalar8BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        /// <summary>
        /// Expands the accumulated dirty regions to include one successful shelf insertion.<br/>
        /// Region bounds are monotonic, keeping the hot insert path allocation-free after the shelf cache entry is created.<br/>
        /// </summary>
        /// <param name="bounds">The conservative header, slot, and item ranges changed by the insertion.<br/></param>
        internal void Include(Scalar16Scalar8MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        /// <summary>
        /// Gets the accumulated changed header range.<br/>
        /// </summary>
        /// <returns>The changed header span, or an empty span when the header is unchanged.<br/></returns>
        internal Scalar16Scalar8BatchChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        /// <summary>
        /// Gets the accumulated changed sorted-slot range.<br/>
        /// </summary>
        /// <returns>The changed slot span, or an empty span when the slot region is unchanged.<br/></returns>
        internal Scalar16Scalar8BatchChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        /// <summary>
        /// Gets the accumulated changed physical-item range.<br/>
        /// </summary>
        /// <returns>The changed item span, or an empty span when the item region is unchanged.<br/></returns>
        internal Scalar16Scalar8BatchChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        /// <summary>
        /// Merges one positive byte range into an accumulated start/end pair.<br/>
        /// </summary>
        /// <param name="start">The current inclusive range start.<br/></param>
        /// <param name="end">The current exclusive range end.<br/></param>
        /// <param name="offset">The candidate range start.<br/></param>
        /// <param name="length">The candidate range length.<br/></param>
        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        /// <summary>
        /// Creates a positive changed span from an accumulated start/end pair.<br/>
        /// </summary>
        /// <param name="start">The inclusive accumulated start, or <see cref="int.MaxValue"/> when unset.<br/></param>
        /// <param name="end">The exclusive accumulated end.<br/></param>
        /// <returns>The accumulated span, or an empty span when no range was included.<br/></returns>
        private static Scalar16Scalar8BatchChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new Scalar16Scalar8BatchChangedSpan(start, end - start);
        }
    }

    /// <summary>
    /// Identifies one cached `SS16-8` route by the encoded key prefix consumed through a router depth.<br/>
    /// </summary>
    /// <param name="Depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
    /// <param name="PrefixHigh">The retained high-lane prefix bits.<br/></param>
    /// <param name="PrefixLow">The retained low-lane prefix bits when the route extends beyond eight bytes.<br/></param>
    private readonly record struct Scalar16Scalar8BatchRouteCacheKey(byte Depth, ulong PrefixHigh, ulong PrefixLow)
    {
        /// <summary>
        /// Creates a route-cache key from the router depth that produced a shelf target.<br/>
        /// </summary>
        /// <param name="encodedKeyHigh">The encoded high key lane.<br/></param>
        /// <param name="encodedKeyLow">The encoded low key lane.<br/></param>
        /// <param name="routerDepth">The zero-based final routed key byte.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Scalar16Scalar8BatchRouteCacheKey Create(ulong encodedKeyHigh, ulong encodedKeyLow, ushort routerDepth)
        {
            if (routerDepth > 15)
            {
                throw new InvalidDataException($"The SS16-8 route depth {routerDepth} cannot be cached for a 16-byte encoded scalar key.");
            }

            return CreateForDepth(encodedKeyHigh, encodedKeyLow, (byte)routerDepth);
        }

        /// <summary>
        /// Creates a route-cache key for one explicit encoded-key byte depth.<br/>
        /// </summary>
        /// <param name="encodedKeyHigh">The encoded high key lane.<br/></param>
        /// <param name="encodedKeyLow">The encoded low key lane.<br/></param>
        /// <param name="depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Scalar16Scalar8BatchRouteCacheKey CreateForDepth(ulong encodedKeyHigh, ulong encodedKeyLow, byte depth)
        {
            int prefixBits = (depth + 1) * 8;
            if (prefixBits <= 64)
            {
                return new Scalar16Scalar8BatchRouteCacheKey(depth, encodedKeyHigh >> (64 - prefixBits), 0);
            }

            int lowPrefixBits = prefixBits - 64;
            return new Scalar16Scalar8BatchRouteCacheKey(depth, encodedKeyHigh, encodedKeyLow >> (64 - lowPrefixBits));
        }
    }

    /// <summary>
    /// Describes one positive shelf-relative byte range accumulated for `SS16-8` batch publication.<br/>
    /// </summary>
    /// <param name="Offset">The byte offset inside the shelf extent.<br/></param>
    /// <param name="Length">The positive byte count, or zero for an empty range.<br/></param>
    private readonly record struct Scalar16Scalar8BatchChangedSpan(int Offset, int Length);

    /// <summary>
    /// Resolves an `SS8-16` shelf from the batch-local route cache using the longest matching encoded-key prefix.<br/>
    /// Structural split fallbacks clear the cache, so returned targets always belong to the current batch topology.<br/>
    /// </summary>
    /// <param name="cache">The batch-local route-target cache.<br/></param>
    /// <param name="encodedKey">The encoded scalar key.<br/></param>
    /// <param name="target">Receives the cached shelf target when one is available.<br/></param>
    /// <returns><see langword="true"/> when a matching shelf target was found; otherwise <see langword="false"/>.<br/></returns>
    private static bool TryGetScalar8Scalar16CachedRouteTarget(
        Dictionary<Scalar8Scalar16BatchRouteCacheKey, Scalar8Scalar16RoutePathTarget> cache,
        ulong encodedKey,
        out Scalar8Scalar16RoutePathTarget target)
    {
        for (byte depth = 7; depth > 0; depth--)
        {
            if (cache.TryGetValue(Scalar8Scalar16BatchRouteCacheKey.CreateForDepth(encodedKey, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Scalar8Scalar16BatchRouteCacheKey.CreateForDepth(encodedKey, 0), out target);
    }

    /// <summary>
    /// Resolves an `SS16-16` shelf from the batch-local route cache using the longest matching encoded-key prefix.<br/>
    /// Structural split fallbacks clear the cache, so returned targets always belong to the current batch topology.<br/>
    /// </summary>
    /// <param name="cache">The batch-local route-target cache.<br/></param>
    /// <param name="encodedKeyHigh">The encoded high key lane.<br/></param>
    /// <param name="encodedKeyLow">The encoded low key lane.<br/></param>
    /// <param name="target">Receives the cached shelf target when one is available.<br/></param>
    /// <returns><see langword="true"/> when a matching shelf target was found; otherwise <see langword="false"/>.<br/></returns>
    private static bool TryGetScalar16Scalar16CachedRouteTarget(
        Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar16RoutePathTarget> cache,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        out Scalar16Scalar16RoutePathTarget target)
    {
        for (byte depth = 15; depth > 0; depth--)
        {
            if (cache.TryGetValue(Scalar16Scalar16BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Scalar16Scalar16BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, 0), out target);
    }

    /// <summary>
    /// Stages accumulated `SS8-16` shelf mutation ranges into the active session durability batch.<br/>
    /// Each retained shelf contributes only its conservative header, slot, and item ranges unless a full-shelf rewrite is smaller.<br/>
    /// </summary>
    private void FlushScalar8Scalar16ShelfCache()
    {
        if (scalar8Scalar16ShelfCache is null || scalar8Scalar16ShelfCache.Count == 0)
        {
            return;
        }

        Scalar8Scalar16Profile profile = index.GetScalar8Scalar16Profile();
        foreach (KeyValuePair<long, Scalar8Scalar16BatchShelfCacheEntry> pair in scalar8Scalar16ShelfCache)
        {
            Scalar8Scalar16BatchShelfCacheEntry entry = pair.Value;
            if (!entry.Dirty)
            {
                continue;
            }

            Scalar8Scalar16ReadOnly readOnly = new(entry.Bytes, profile);
            if (!readOnly.IsValid)
            {
                throw new InvalidDataException("The generic SS8-16 batch attempted to publish an invalid cached shelf image.");
            }

            FixedScalarBatchChangedSpan header = entry.GetHeaderSpan();
            FixedScalarBatchChangedSpan slots = entry.GetSlotSpan();
            FixedScalarBatchChangedSpan item = entry.GetItemSpan();
            int deltaBytes = header.Length + slots.Length + item.Length;
            if (deltaBytes >= profile.ShelfExtentSize)
            {
                _ = index.Session.StageScalar8Scalar16ShelfRewriteForBatch(pair.Key, profile, entry.Bytes);
                continue;
            }

            StageScalar8Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, header);
            StageScalar8Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, slots);
            StageScalar8Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, item);
        }
    }

    /// <summary>
    /// Stages accumulated `SS16-16` shelf mutation ranges into the active session durability batch.<br/>
    /// Each retained shelf contributes only its conservative header, slot, and item ranges unless a full-shelf rewrite is smaller.<br/>
    /// </summary>
    private void FlushScalar16Scalar16ShelfCache()
    {
        if (scalar16Scalar16ShelfCache is null || scalar16Scalar16ShelfCache.Count == 0)
        {
            return;
        }

        Scalar16Scalar16Profile profile = index.GetScalar16Scalar16Profile();
        foreach (KeyValuePair<long, Scalar16Scalar16BatchShelfCacheEntry> pair in scalar16Scalar16ShelfCache)
        {
            Scalar16Scalar16BatchShelfCacheEntry entry = pair.Value;
            if (!entry.Dirty)
            {
                continue;
            }

            Scalar16Scalar16ReadOnly readOnly = new(entry.Bytes, profile);
            if (!readOnly.IsValid)
            {
                throw new InvalidDataException("The generic SS16-16 batch attempted to publish an invalid cached shelf image.");
            }

            FixedScalarBatchChangedSpan header = entry.GetHeaderSpan();
            FixedScalarBatchChangedSpan slots = entry.GetSlotSpan();
            FixedScalarBatchChangedSpan item = entry.GetItemSpan();
            int deltaBytes = header.Length + slots.Length + item.Length;
            if (deltaBytes >= profile.ShelfExtentSize)
            {
                _ = index.Session.StageScalar16Scalar16ShelfRewriteForBatch(pair.Key, profile, entry.Bytes);
                continue;
            }

            StageScalar16Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, header);
            StageScalar16Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, slots);
            StageScalar16Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, item);
        }
    }

    /// <summary>
    /// Stages one positive changed span from a retained `SS8-16` shelf image.<br/>
    /// Empty spans are ignored so unchanged shelf regions do not create deferred commit requests.<br/>
    /// </summary>
    /// <param name="shelfOffset">The physical shelf offset owning the changed bytes.<br/></param>
    /// <param name="profile">The active `SS8-16` shelf profile.<br/></param>
    /// <param name="shelfBytes">The retained final shelf image.<br/></param>
    /// <param name="span">The shelf-relative changed range to stage.<br/></param>
    private void StageScalar8Scalar16ChangedSpan(
        long shelfOffset,
        Scalar8Scalar16Profile profile,
        byte[] shelfBytes,
        FixedScalarBatchChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        _ = index.Session.StageScalar8Scalar16ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            shelfBytes.AsSpan(span.Offset, span.Length));
    }

    /// <summary>
    /// Stages one positive changed span from a retained `SS16-16` shelf image.<br/>
    /// Empty spans are ignored so unchanged shelf regions do not create deferred commit requests.<br/>
    /// </summary>
    /// <param name="shelfOffset">The physical shelf offset owning the changed bytes.<br/></param>
    /// <param name="profile">The active `SS16-16` shelf profile.<br/></param>
    /// <param name="shelfBytes">The retained final shelf image.<br/></param>
    /// <param name="span">The shelf-relative changed range to stage.<br/></param>
    private void StageScalar16Scalar16ChangedSpan(
        long shelfOffset,
        Scalar16Scalar16Profile profile,
        byte[] shelfBytes,
        FixedScalarBatchChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        _ = index.Session.StageScalar16Scalar16ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            shelfBytes.AsSpan(span.Offset, span.Length));
    }

    /// <summary>
    /// Promotes successfully published `SS8-16` shelf images into the session-local clean cache.<br/>
    /// Promotion occurs only after the owning durability commit succeeds.<br/>
    /// </summary>
    private void StoreScalar8Scalar16CleanShelfCacheEntries()
    {
        if (scalar8Scalar16ShelfCache is null || scalar8Scalar16ShelfCache.Count == 0)
        {
            return;
        }

        Scalar8Scalar16Profile profile = index.GetScalar8Scalar16Profile();
        foreach (KeyValuePair<long, Scalar8Scalar16BatchShelfCacheEntry> pair in scalar8Scalar16ShelfCache)
        {
            index.Session.StoreScalar8Scalar16CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
        }
    }

    /// <summary>
    /// Promotes successfully published `SS16-16` shelf images into the session-local clean cache.<br/>
    /// Promotion occurs only after the owning durability commit succeeds.<br/>
    /// </summary>
    private void StoreScalar16Scalar16CleanShelfCacheEntries()
    {
        if (scalar16Scalar16ShelfCache is null || scalar16Scalar16ShelfCache.Count == 0)
        {
            return;
        }

        Scalar16Scalar16Profile profile = index.GetScalar16Scalar16Profile();
        foreach (KeyValuePair<long, Scalar16Scalar16BatchShelfCacheEntry> pair in scalar16Scalar16ShelfCache)
        {
            index.Session.StoreScalar16Scalar16CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
        }
    }

    /// <summary>
    /// Merges one positive mutation range into an accumulated fixed-scalar batch range.<br/>
    /// The helper is shared by the shape-specialized cache-entry types while remaining outside their insertion hot paths.<br/>
    /// </summary>
    /// <param name="start">The current inclusive range start.<br/></param>
    /// <param name="end">The current exclusive range end.<br/></param>
    /// <param name="offset">The candidate range start.<br/></param>
    /// <param name="length">The candidate range length.<br/></param>
    private static void IncludeFixedScalarBatchRange(ref int start, ref int end, int offset, int length)
    {
        if (length <= 0)
        {
            return;
        }

        start = Math.Min(start, offset);
        end = Math.Max(end, checked(offset + length));
    }

    /// <summary>
    /// Creates a positive changed span from an accumulated fixed-scalar batch range.<br/>
    /// </summary>
    /// <param name="start">The inclusive accumulated start, or <see cref="int.MaxValue"/> when unset.<br/></param>
    /// <param name="end">The exclusive accumulated end.<br/></param>
    /// <returns>The accumulated span, or an empty span when no range was included.<br/></returns>
    private static FixedScalarBatchChangedSpan GetFixedScalarBatchSpan(int start, int end)
    {
        return start == int.MaxValue ? default : new FixedScalarBatchChangedSpan(start, end - start);
    }

    /// <summary>
    /// Retains one mutable `SS8-16` shelf image and its conservative changed regions.<br/>
    /// The image remains batch-owned until publication, abort, or structural split fallback.<br/>
    /// </summary>
    private sealed class Scalar8Scalar16BatchShelfCacheEntry
    {
        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        /// <summary>
        /// Creates an `SS8-16` cache entry over one caller-owned mutable shelf image.<br/>
        /// </summary>
        /// <param name="bytes">The validated mutable shelf image retained by the batch.<br/></param>
        internal Scalar8Scalar16BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        /// <summary>
        /// Expands the accumulated dirty regions to include one successful `SS8-16` insertion.<br/>
        /// </summary>
        /// <param name="bounds">The conservative mutation bounds returned by the shelf insert.<br/></param>
        internal void Include(Scalar8Scalar16MutationBounds bounds)
        {
            IncludeFixedScalarBatchRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeFixedScalarBatchRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeFixedScalarBatchRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        /// <summary>
        /// Gets the accumulated changed header range.<br/>
        /// </summary>
        /// <returns>The changed header span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetHeaderSpan() => GetFixedScalarBatchSpan(headerStart, headerEnd);

        /// <summary>
        /// Gets the accumulated changed sorted-slot range.<br/>
        /// </summary>
        /// <returns>The changed slot span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetSlotSpan() => GetFixedScalarBatchSpan(slotStart, slotEnd);

        /// <summary>
        /// Gets the accumulated changed physical-item range.<br/>
        /// </summary>
        /// <returns>The changed item span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetItemSpan() => GetFixedScalarBatchSpan(itemStart, itemEnd);
    }

    /// <summary>
    /// Retains one mutable `SS16-16` shelf image and its conservative changed regions.<br/>
    /// The image remains batch-owned until publication, abort, or structural split fallback.<br/>
    /// </summary>
    private sealed class Scalar16Scalar16BatchShelfCacheEntry
    {
        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        /// <summary>
        /// Creates an `SS16-16` cache entry over one caller-owned mutable shelf image.<br/>
        /// </summary>
        /// <param name="bytes">The validated mutable shelf image retained by the batch.<br/></param>
        internal Scalar16Scalar16BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        /// <summary>
        /// Expands the accumulated dirty regions to include one successful `SS16-16` insertion.<br/>
        /// </summary>
        /// <param name="bounds">The conservative mutation bounds returned by the shelf insert.<br/></param>
        internal void Include(Scalar16Scalar16MutationBounds bounds)
        {
            IncludeFixedScalarBatchRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeFixedScalarBatchRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeFixedScalarBatchRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        /// <summary>
        /// Gets the accumulated changed header range.<br/>
        /// </summary>
        /// <returns>The changed header span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetHeaderSpan() => GetFixedScalarBatchSpan(headerStart, headerEnd);

        /// <summary>
        /// Gets the accumulated changed sorted-slot range.<br/>
        /// </summary>
        /// <returns>The changed slot span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetSlotSpan() => GetFixedScalarBatchSpan(slotStart, slotEnd);

        /// <summary>
        /// Gets the accumulated changed physical-item range.<br/>
        /// </summary>
        /// <returns>The changed item span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetItemSpan() => GetFixedScalarBatchSpan(itemStart, itemEnd);
    }

    /// <summary>
    /// Identifies one cached `SS8-16` route by the encoded key prefix consumed through a router depth.<br/>
    /// </summary>
    /// <param name="Depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
    /// <param name="Prefix">The retained encoded-key prefix bits.<br/></param>
    private readonly record struct Scalar8Scalar16BatchRouteCacheKey(byte Depth, ulong Prefix)
    {
        /// <summary>
        /// Creates an `SS8-16` route-cache key from the router depth that produced a shelf target.<br/>
        /// </summary>
        /// <param name="encodedKey">The encoded scalar key.<br/></param>
        /// <param name="routerDepth">The zero-based final routed key byte.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Scalar8Scalar16BatchRouteCacheKey Create(ulong encodedKey, ushort routerDepth)
        {
            if (routerDepth > 7)
            {
                throw new InvalidDataException($"The SS8-16 route depth {routerDepth} cannot be cached for an 8-byte encoded scalar key.");
            }

            return CreateForDepth(encodedKey, (byte)routerDepth);
        }

        /// <summary>
        /// Creates an `SS8-16` route-cache key for one explicit encoded-key byte depth.<br/>
        /// </summary>
        /// <param name="encodedKey">The encoded scalar key.<br/></param>
        /// <param name="depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Scalar8Scalar16BatchRouteCacheKey CreateForDepth(ulong encodedKey, byte depth)
        {
            int prefixBits = (depth + 1) * 8;
            return new Scalar8Scalar16BatchRouteCacheKey(depth, encodedKey >> (64 - prefixBits));
        }
    }

    /// <summary>
    /// Identifies one cached `SS16-16` route by the encoded key prefix consumed through a router depth.<br/>
    /// </summary>
    /// <param name="Depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
    /// <param name="PrefixHigh">The retained high-lane prefix bits.<br/></param>
    /// <param name="PrefixLow">The retained low-lane prefix bits when the route extends beyond eight bytes.<br/></param>
    private readonly record struct Scalar16Scalar16BatchRouteCacheKey(byte Depth, ulong PrefixHigh, ulong PrefixLow)
    {
        /// <summary>
        /// Creates an `SS16-16` route-cache key from the router depth that produced a shelf target.<br/>
        /// </summary>
        /// <param name="encodedKeyHigh">The encoded high key lane.<br/></param>
        /// <param name="encodedKeyLow">The encoded low key lane.<br/></param>
        /// <param name="routerDepth">The zero-based final routed key byte.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Scalar16Scalar16BatchRouteCacheKey Create(ulong encodedKeyHigh, ulong encodedKeyLow, ushort routerDepth)
        {
            if (routerDepth > 15)
            {
                throw new InvalidDataException($"The SS16-16 route depth {routerDepth} cannot be cached for a 16-byte encoded scalar key.");
            }

            return CreateForDepth(encodedKeyHigh, encodedKeyLow, (byte)routerDepth);
        }

        /// <summary>
        /// Creates an `SS16-16` route-cache key for one explicit encoded-key byte depth.<br/>
        /// </summary>
        /// <param name="encodedKeyHigh">The encoded high key lane.<br/></param>
        /// <param name="encodedKeyLow">The encoded low key lane.<br/></param>
        /// <param name="depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Scalar16Scalar16BatchRouteCacheKey CreateForDepth(ulong encodedKeyHigh, ulong encodedKeyLow, byte depth)
        {
            int prefixBits = (depth + 1) * 8;
            if (prefixBits <= 64)
            {
                return new Scalar16Scalar16BatchRouteCacheKey(depth, encodedKeyHigh >> (64 - prefixBits), 0);
            }

            int lowPrefixBits = prefixBits - 64;
            return new Scalar16Scalar16BatchRouteCacheKey(depth, encodedKeyHigh, encodedKeyLow >> (64 - lowPrefixBits));
        }
    }

    /// <summary>
    /// Retains the terminal radix frame selected by the previous fixed-scalar batch insert.<br/>
    /// Monotonic keys can reuse the selected shelf while their earlier routed bytes remain unchanged, including adjacent parent-router prefixes that still point at the same direct shelf offset.<br/>
    /// </summary>
    private struct FixedScalarMonotonicRouteCursor
    {
        private ulong lastKeyHigh;
        private ulong lastKeyLow;
        private long targetOffset;
        private long parentRouterOffset;
        private ushort routerDepth;
        private ushort allocationClassId;
        private byte parentPrefixByte;
        private byte prefixRangeStart;
        private byte prefixRangeEnd;
        private byte keyByteCount;
        private bool active;

        /// <summary>
        /// Attempts to reuse the retained terminal radix frame for one encoded eight-byte key.<br/>
        /// A changed parent prefix is accepted only when the current direct router still maps that prefix to the retained shelf offset.<br/>
        /// </summary>
        /// <param name="session">The owning file session used to validate a changed parent-router prefix.<br/></param>
        /// <param name="encodedKey">The next sortable encoded key.<br/></param>
        /// <param name="route">Receives the retained route metadata when reuse is safe.<br/></param>
        /// <returns><see langword="true"/> when the previous terminal route still owns the key; otherwise <see langword="false"/>.<br/></returns>
        internal bool TryReuse8(LibraDexFileSession session, ulong encodedKey, out FixedScalarMonotonicRoute route)
        {
            return TryReuse(session, encodedKey, 0, sizeof(ulong), out route);
        }

        /// <summary>
        /// Attempts to reuse the retained terminal radix frame for one encoded sixteen-byte key.<br/>
        /// The comparison follows the persisted high-lane then low-lane byte order used by fixed-scalar routers.<br/>
        /// </summary>
        /// <param name="session">The owning file session used to validate a changed parent-router prefix.<br/></param>
        /// <param name="encodedKeyHigh">The next sortable encoded key high lane.<br/></param>
        /// <param name="encodedKeyLow">The next sortable encoded key low lane.<br/></param>
        /// <param name="route">Receives the retained route metadata when reuse is safe.<br/></param>
        /// <returns><see langword="true"/> when the previous terminal route still owns the key; otherwise <see langword="false"/>.<br/></returns>
        internal bool TryReuse16(
            LibraDexFileSession session,
            ulong encodedKeyHigh,
            ulong encodedKeyLow,
            out FixedScalarMonotonicRoute route)
        {
            return TryReuse(session, encodedKeyHigh, encodedKeyLow, sizeof(ulong) * 2, out route);
        }

        /// <summary>
        /// Records the terminal radix frame selected for an encoded eight-byte key.<br/>
        /// The next monotonic insert can then bypass the generic longest-prefix route-cache probe when this frame still owns its key.<br/>
        /// </summary>
        /// <param name="session">The owning file session used to capture the selected direct-prefix range.<br/></param>
        /// <param name="encodedKey">The sortable encoded key that selected the route.<br/></param>
        /// <param name="selectedTargetOffset">The selected shelf offset.<br/></param>
        /// <param name="selectedRouterDepth">The parent router key-byte depth.<br/></param>
        /// <param name="selectedAllocationClassId">The selected shelf allocation class.<br/></param>
        /// <param name="selectedParentRouterOffset">The direct parent router offset.<br/></param>
        /// <param name="selectedParentPrefixByte">The parent-router prefix that selected the shelf.<br/></param>
        internal void Set8(
            LibraDexFileSession session,
            ulong encodedKey,
            long selectedTargetOffset,
            ushort selectedRouterDepth,
            ushort selectedAllocationClassId,
            long selectedParentRouterOffset,
            byte selectedParentPrefixByte)
        {
            Set(
                session,
                encodedKey,
                0,
                sizeof(ulong),
                selectedTargetOffset,
                selectedRouterDepth,
                selectedAllocationClassId,
                selectedParentRouterOffset,
                selectedParentPrefixByte);
        }

        /// <summary>
        /// Records the terminal radix frame selected for an encoded sixteen-byte key.<br/>
        /// The retained lanes also provide an allocation-free common-prefix comparison for the next insert.<br/>
        /// </summary>
        /// <param name="session">The owning file session used to capture the selected direct-prefix range.<br/></param>
        /// <param name="encodedKeyHigh">The sortable encoded key high lane.<br/></param>
        /// <param name="encodedKeyLow">The sortable encoded key low lane.<br/></param>
        /// <param name="selectedTargetOffset">The selected shelf offset.<br/></param>
        /// <param name="selectedRouterDepth">The parent router key-byte depth.<br/></param>
        /// <param name="selectedAllocationClassId">The selected shelf allocation class.<br/></param>
        /// <param name="selectedParentRouterOffset">The direct parent router offset.<br/></param>
        /// <param name="selectedParentPrefixByte">The parent-router prefix that selected the shelf.<br/></param>
        internal void Set16(
            LibraDexFileSession session,
            ulong encodedKeyHigh,
            ulong encodedKeyLow,
            long selectedTargetOffset,
            ushort selectedRouterDepth,
            ushort selectedAllocationClassId,
            long selectedParentRouterOffset,
            byte selectedParentPrefixByte)
        {
            Set(
                session,
                encodedKeyHigh,
                encodedKeyLow,
                sizeof(ulong) * 2,
                selectedTargetOffset,
                selectedRouterDepth,
                selectedAllocationClassId,
                selectedParentRouterOffset,
                selectedParentPrefixByte);
        }

        /// <summary>
        /// Invalidates retained route ownership after a structural split, publication, or abort boundary.<br/>
        /// The next insert performs one normal route walk and seeds a fresh cursor from current topology.<br/>
        /// </summary>
        internal void Clear()
        {
            this = default;
        }

        /// <summary>
        /// Validates monotonic order and terminal-parent ownership for one fixed-scalar encoded key.<br/>
        /// Bytes before the terminal router depth must match; a changed byte at the terminal depth is checked directly against the retained parent router.<br/>
        /// </summary>
        /// <param name="session">The owning file session used for direct route-slot validation.<br/></param>
        /// <param name="encodedKeyHigh">The next encoded key high lane.<br/></param>
        /// <param name="encodedKeyLow">The next encoded key low lane, or zero for an eight-byte key.<br/></param>
        /// <param name="encodedKeyByteCount">The encoded key width in bytes.<br/></param>
        /// <param name="route">Receives the retained terminal route when validation succeeds.<br/></param>
        /// <returns><see langword="true"/> when the route can be reused safely; otherwise <see langword="false"/>.<br/></returns>
        private bool TryReuse(
            LibraDexFileSession session,
            ulong encodedKeyHigh,
            ulong encodedKeyLow,
            int encodedKeyByteCount,
            out FixedScalarMonotonicRoute route)
        {
            route = default;
            if (!active || keyByteCount != encodedKeyByteCount)
            {
                return false;
            }

            if (encodedKeyHigh < lastKeyHigh ||
                (encodedKeyHigh == lastKeyHigh && encodedKeyLow < lastKeyLow))
            {
                return false;
            }

            int commonPrefixByteCount = GetCommonPrefixByteCount(lastKeyHigh, lastKeyLow, encodedKeyHigh, encodedKeyLow, encodedKeyByteCount);
            if (commonPrefixByteCount < routerDepth)
            {
                return false;
            }

            byte prefixByte = GetEncodedKeyByte(encodedKeyHigh, encodedKeyLow, routerDepth);
            if ((prefixByte < prefixRangeStart || prefixByte > prefixRangeEnd) &&
                !session.TryGetDirectRouterTargetPrefixRange(
                    parentRouterOffset,
                    prefixByte,
                    targetOffset,
                    out prefixRangeStart,
                    out prefixRangeEnd))
            {
                return false;
            }

            route = new FixedScalarMonotonicRoute(
                targetOffset,
                routerDepth,
                allocationClassId,
                parentRouterOffset,
                prefixByte);
            return true;
        }

        /// <summary>
        /// Stores one validated terminal radix frame and the encoded key that selected it.<br/>
        /// All state is inline in the batch object so the hot cursor path performs no heap allocation.<br/>
        /// </summary>
        /// <param name="session">The owning file session used to capture the selected direct-prefix range.<br/></param>
        /// <param name="encodedKeyHigh">The selected encoded key high lane.<br/></param>
        /// <param name="encodedKeyLow">The selected encoded key low lane.<br/></param>
        /// <param name="encodedKeyByteCount">The encoded key width in bytes.<br/></param>
        /// <param name="selectedTargetOffset">The selected shelf offset.<br/></param>
        /// <param name="selectedRouterDepth">The parent router key-byte depth.<br/></param>
        /// <param name="selectedAllocationClassId">The selected shelf allocation class.<br/></param>
        /// <param name="selectedParentRouterOffset">The direct parent router offset.<br/></param>
        /// <param name="selectedParentPrefixByte">The parent-router prefix that selected the shelf.<br/></param>
        private void Set(
            LibraDexFileSession session,
            ulong encodedKeyHigh,
            ulong encodedKeyLow,
            int encodedKeyByteCount,
            long selectedTargetOffset,
            ushort selectedRouterDepth,
            ushort selectedAllocationClassId,
            long selectedParentRouterOffset,
            byte selectedParentPrefixByte)
        {
            if (selectedRouterDepth >= encodedKeyByteCount)
            {
                throw new InvalidDataException($"The fixed-scalar monotonic cursor cannot retain router depth {selectedRouterDepth} for a {encodedKeyByteCount}-byte key.");
            }

            if (active &&
                keyByteCount == encodedKeyByteCount &&
                targetOffset == selectedTargetOffset &&
                routerDepth == selectedRouterDepth &&
                parentRouterOffset == selectedParentRouterOffset &&
                selectedParentPrefixByte >= prefixRangeStart &&
                selectedParentPrefixByte <= prefixRangeEnd)
            {
                lastKeyHigh = encodedKeyHigh;
                lastKeyLow = encodedKeyLow;
                parentPrefixByte = selectedParentPrefixByte;
                allocationClassId = selectedAllocationClassId;
                return;
            }

            byte selectedRangeStart = selectedParentPrefixByte;
            byte selectedRangeEnd = selectedParentPrefixByte;
            _ = session.TryGetDirectRouterTargetPrefixRange(
                selectedParentRouterOffset,
                selectedParentPrefixByte,
                selectedTargetOffset,
                out selectedRangeStart,
                out selectedRangeEnd);

            lastKeyHigh = encodedKeyHigh;
            lastKeyLow = encodedKeyLow;
            targetOffset = selectedTargetOffset;
            routerDepth = selectedRouterDepth;
            allocationClassId = selectedAllocationClassId;
            parentRouterOffset = selectedParentRouterOffset;
            parentPrefixByte = selectedParentPrefixByte;
            prefixRangeStart = selectedRangeStart;
            prefixRangeEnd = selectedRangeEnd;
            keyByteCount = checked((byte)encodedKeyByteCount);
            active = true;
        }

        /// <summary>
        /// Counts equal leading encoded-key bytes across the persisted high-lane then low-lane order.<br/>
        /// XOR plus leading-zero count replaces per-byte comparison on the sorted batch hot path.<br/>
        /// </summary>
        /// <param name="leftHigh">The previous encoded key high lane.<br/></param>
        /// <param name="leftLow">The previous encoded key low lane.<br/></param>
        /// <param name="rightHigh">The next encoded key high lane.<br/></param>
        /// <param name="rightLow">The next encoded key low lane.<br/></param>
        /// <param name="encodedKeyByteCount">The encoded key width in bytes.<br/></param>
        /// <returns>The number of equal leading encoded-key bytes.<br/></returns>
        private static int GetCommonPrefixByteCount(
            ulong leftHigh,
            ulong leftLow,
            ulong rightHigh,
            ulong rightLow,
            int encodedKeyByteCount)
        {
            ulong difference = leftHigh ^ rightHigh;
            if (difference != 0)
            {
                return System.Numerics.BitOperations.LeadingZeroCount(difference) / 8;
            }

            if (encodedKeyByteCount == sizeof(ulong))
            {
                return sizeof(ulong);
            }

            difference = leftLow ^ rightLow;
            return difference == 0
                ? sizeof(ulong) * 2
                : sizeof(ulong) + (System.Numerics.BitOperations.LeadingZeroCount(difference) / 8);
        }

        /// <summary>
        /// Extracts one persisted big-endian routing byte from an encoded fixed-scalar key.<br/>
        /// Depths zero through seven read the high lane; later depths read the low lane.<br/>
        /// </summary>
        /// <param name="encodedKeyHigh">The encoded key high lane.<br/></param>
        /// <param name="encodedKeyLow">The encoded key low lane.<br/></param>
        /// <param name="depth">The zero-based routing-byte depth.<br/></param>
        /// <returns>The encoded routing byte at the requested depth.<br/></returns>
        private static byte GetEncodedKeyByte(ulong encodedKeyHigh, ulong encodedKeyLow, int depth)
        {
            if (depth < sizeof(ulong))
            {
                return (byte)(encodedKeyHigh >> ((sizeof(ulong) - 1 - depth) * 8));
            }

            int lowDepth = depth - sizeof(ulong);
            return (byte)(encodedKeyLow >> ((sizeof(ulong) - 1 - lowDepth) * 8));
        }
    }

    /// <summary>
    /// Resolves an `FS32-8` shelf from the batch-local route cache using the longest matching encoded-key prefix.<br/>
    /// Structural split fallbacks clear the cache, so a hit always belongs to the current batch topology.<br/>
    /// </summary>
    /// <param name="cache">The batch-local route-target cache.<br/></param>
    /// <param name="key0">The first encoded fixed-key lane.<br/></param>
    /// <param name="key1">The second encoded fixed-key lane.<br/></param>
    /// <param name="key2">The third encoded fixed-key lane.<br/></param>
    /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
    /// <param name="target">The cached terminal shelf target when found.<br/></param>
    /// <returns><see langword="true"/> when a matching cached shelf target was found.<br/></returns>
    private static bool TryGetCachedFixed32Scalar8RouteTarget(
        Dictionary<Fixed32BatchRouteCacheKey, Fixed32Scalar8RoutePathTarget> cache,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        out Fixed32Scalar8RoutePathTarget target)
    {
        for (byte depth = 31; depth > 0; depth--)
        {
            if (cache.TryGetValue(Fixed32BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Fixed32BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, 0), out target);
    }

    /// <summary>
    /// Resolves an `FS32-16` shelf from the batch-local route cache using the longest matching encoded-key prefix.<br/>
    /// Structural split fallbacks clear the cache, so a hit always belongs to the current batch topology.<br/>
    /// </summary>
    /// <param name="cache">The batch-local route-target cache.<br/></param>
    /// <param name="key0">The first encoded fixed-key lane.<br/></param>
    /// <param name="key1">The second encoded fixed-key lane.<br/></param>
    /// <param name="key2">The third encoded fixed-key lane.<br/></param>
    /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
    /// <param name="target">The cached terminal shelf target when found.<br/></param>
    /// <returns><see langword="true"/> when a matching cached shelf target was found.<br/></returns>
    private static bool TryGetCachedFixed32Scalar16RouteTarget(
        Dictionary<Fixed32BatchRouteCacheKey, Fixed32Scalar16RoutePathTarget> cache,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        out Fixed32Scalar16RoutePathTarget target)
    {
        for (byte depth = 31; depth > 0; depth--)
        {
            if (cache.TryGetValue(Fixed32BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Fixed32BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, 0), out target);
    }

    /// <summary>
    /// Stages accumulated `FS32-8` shelf mutation ranges into the active durability batch.<br/>
    /// Retained shelves avoid per-entry reads and full-image copies; publication writes only conservative header, slot, and item ranges when smaller than the shelf.<br/>
    /// </summary>
    private void FlushFixed32Scalar8ShelfCache()
    {
        if (fixed32Scalar8ShelfCache is null || fixed32Scalar8ShelfCache.Count == 0)
        {
            return;
        }

        Fixed32BatchAttributionDiagnostics? attribution = fixed32Attribution;
        long flushStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        Fixed32Scalar8Profile profile = index.GetFixed32Scalar8Profile();
        foreach (KeyValuePair<long, Fixed32Scalar8BatchShelfCacheEntry> pair in fixed32Scalar8ShelfCache)
        {
            Fixed32Scalar8BatchShelfCacheEntry entry = pair.Value;
            if (!entry.Dirty)
            {
                continue;
            }

            Fixed32Scalar8ReadOnly readOnly = new(entry.Bytes, profile);
            if (!readOnly.IsValid)
            {
                throw new InvalidDataException("The generic FS32-8 batch attempted to publish an invalid cached shelf image.");
            }

            FixedScalarBatchChangedSpan header = entry.GetHeaderSpan();
            FixedScalarBatchChangedSpan slots = entry.GetSlotSpan();
            FixedScalarBatchChangedSpan item = entry.GetItemSpan();
            int deltaBytes = header.Length + slots.Length + item.Length;
            if (attribution is not null)
            {
                attribution.FlushShelfCount++;
                attribution.FlushBytes += deltaBytes >= profile.ShelfExtentSize ? profile.ShelfExtentSize : deltaBytes;
            }
            if (deltaBytes >= profile.ShelfExtentSize)
            {
                _ = index.Session.StageFixed32Scalar8RetainedShelfSliceRewriteForBatch(pair.Key, profile, 0, entry.Bytes, profile.ShelfExtentSize);
                continue;
            }

            StageFixed32Scalar8ChangedSpan(pair.Key, profile, entry.Bytes, header);
            StageFixed32Scalar8ChangedSpan(pair.Key, profile, entry.Bytes, slots);
            StageFixed32Scalar8ChangedSpan(pair.Key, profile, entry.Bytes, item);
        }

        if (attribution is not null)
            attribution.FlushTicks += Stopwatch.GetTimestamp() - flushStartTicks;
    }

    /// <summary>
    /// Stages accumulated `FS32-16` shelf mutation ranges into the active durability batch.<br/>
    /// Retained shelves avoid per-entry reads and full-image copies; publication writes only conservative header, slot, and item ranges when smaller than the shelf.<br/>
    /// </summary>
    private void FlushFixed32Scalar16ShelfCache()
    {
        if (fixed32Scalar16ShelfCache is null || fixed32Scalar16ShelfCache.Count == 0)
        {
            return;
        }

        Fixed32BatchAttributionDiagnostics? attribution = fixed32Attribution;
        long flushStartTicks = attribution is null ? 0 : Stopwatch.GetTimestamp();
        Fixed32Scalar16Profile profile = index.GetFixed32Scalar16Profile();
        foreach (KeyValuePair<long, Fixed32Scalar16BatchShelfCacheEntry> pair in fixed32Scalar16ShelfCache)
        {
            Fixed32Scalar16BatchShelfCacheEntry entry = pair.Value;
            if (!entry.Dirty)
            {
                continue;
            }

            Fixed32Scalar16ReadOnly readOnly = new(entry.Bytes, profile);
            if (!readOnly.IsValid)
            {
                throw new InvalidDataException("The generic FS32-16 batch attempted to publish an invalid cached shelf image.");
            }

            FixedScalarBatchChangedSpan header = entry.GetHeaderSpan();
            FixedScalarBatchChangedSpan slots = entry.GetSlotSpan();
            FixedScalarBatchChangedSpan item = entry.GetItemSpan();
            int deltaBytes = header.Length + slots.Length + item.Length;
            if (attribution is not null)
            {
                attribution.FlushShelfCount++;
                attribution.FlushBytes += deltaBytes >= profile.ShelfExtentSize ? profile.ShelfExtentSize : deltaBytes;
            }
            if (deltaBytes >= profile.ShelfExtentSize)
            {
                _ = index.Session.StageFixed32Scalar16RetainedShelfSliceRewriteForBatch(pair.Key, profile, 0, entry.Bytes, profile.ShelfExtentSize);
                continue;
            }

            StageFixed32Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, header);
            StageFixed32Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, slots);
            StageFixed32Scalar16ChangedSpan(pair.Key, profile, entry.Bytes, item);
        }

        if (attribution is not null)
            attribution.FlushTicks += Stopwatch.GetTimestamp() - flushStartTicks;
    }

    /// <summary>
    /// Stages one positive changed span from a retained `FS32-8` shelf image.<br/>
    /// Empty spans are ignored so unchanged shelf regions create no deferred write request.<br/>
    /// </summary>
    /// <param name="shelfOffset">The physical shelf offset owning the changed bytes.<br/></param>
    /// <param name="profile">The active `FS32-8` shelf profile.<br/></param>
    /// <param name="shelfBytes">The retained final shelf image.<br/></param>
    /// <param name="span">The shelf-relative changed range to stage.<br/></param>
    private void StageFixed32Scalar8ChangedSpan(long shelfOffset, Fixed32Scalar8Profile profile, byte[] shelfBytes, FixedScalarBatchChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        _ = index.Session.StageFixed32Scalar8RetainedShelfSliceRewriteForBatch(shelfOffset, profile, span.Offset, shelfBytes, span.Length);
    }

    /// <summary>
    /// Stages one positive changed span from a retained `FS32-16` shelf image.<br/>
    /// Empty spans are ignored so unchanged shelf regions create no deferred write request.<br/>
    /// </summary>
    /// <param name="shelfOffset">The physical shelf offset owning the changed bytes.<br/></param>
    /// <param name="profile">The active `FS32-16` shelf profile.<br/></param>
    /// <param name="shelfBytes">The retained final shelf image.<br/></param>
    /// <param name="span">The shelf-relative changed range to stage.<br/></param>
    private void StageFixed32Scalar16ChangedSpan(long shelfOffset, Fixed32Scalar16Profile profile, byte[] shelfBytes, FixedScalarBatchChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        _ = index.Session.StageFixed32Scalar16RetainedShelfSliceRewriteForBatch(shelfOffset, profile, span.Offset, shelfBytes, span.Length);
    }

    /// <summary>
    /// Promotes successfully published `FS32-8` shelf images into the session-local clean cache.<br/>
    /// Promotion occurs only after the owning durability commit succeeds.<br/>
    /// </summary>
    private void StoreFixed32Scalar8CleanShelfCacheEntries()
    {
        if (fixed32Scalar8ShelfCache is null || fixed32Scalar8ShelfCache.Count == 0)
        {
            return;
        }

        Fixed32Scalar8Profile profile = index.GetFixed32Scalar8Profile();
        foreach (KeyValuePair<long, Fixed32Scalar8BatchShelfCacheEntry> pair in fixed32Scalar8ShelfCache)
        {
            index.Session.StoreFixed32Scalar8CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
        }
    }

    /// <summary>
    /// Promotes successfully published `FS32-16` shelf images into the session-local clean cache.<br/>
    /// Promotion occurs only after the owning durability commit succeeds.<br/>
    /// </summary>
    private void StoreFixed32Scalar16CleanShelfCacheEntries()
    {
        if (fixed32Scalar16ShelfCache is null || fixed32Scalar16ShelfCache.Count == 0)
        {
            return;
        }

        Fixed32Scalar16Profile profile = index.GetFixed32Scalar16Profile();
        foreach (KeyValuePair<long, Fixed32Scalar16BatchShelfCacheEntry> pair in fixed32Scalar16ShelfCache)
        {
            index.Session.StoreFixed32Scalar16CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
        }
    }

    /// <summary>
    /// Describes one terminal fixed-scalar route retained by the monotonic batch cursor.<br/>
    /// The route is shape-neutral so `SS16-8`, `SS8-16`, and `SS16-16` share the same allocation-free validation logic.<br/>
    /// </summary>
    /// <param name="TargetOffset">The retained shelf offset.<br/></param>
    /// <param name="RouterDepth">The parent router key-byte depth.<br/></param>
    /// <param name="AllocationClassId">The retained shelf allocation class.<br/></param>
    /// <param name="ParentRouterOffset">The direct parent router offset.<br/></param>
    /// <param name="ParentPrefixByte">The parent-router prefix that selected the shelf.<br/></param>
    private readonly record struct FixedScalarMonotonicRoute(
        long TargetOffset,
        ushort RouterDepth,
        ushort AllocationClassId,
        long ParentRouterOffset,
        byte ParentPrefixByte);

    /// <summary>
    /// Describes one positive shelf-relative byte range accumulated for fixed-scalar batch publication.<br/>
    /// </summary>
    /// <param name="Offset">The byte offset inside the shelf extent.<br/></param>
    /// <param name="Length">The positive byte count, or zero for an empty range.<br/></param>
    private readonly record struct FixedScalarBatchChangedSpan(int Offset, int Length);

    /// <summary>
    /// Retains one mutable `FS32-8` shelf image and its conservative changed regions.<br/>
    /// The image remains batch-owned until publication, abort, or structural split fallback.<br/>
    /// </summary>
    private sealed class Fixed32Scalar8BatchShelfCacheEntry
    {
        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;
        private readonly int initialItemCount;

        /// <summary>
        /// Creates an `FS32-8` cache entry over one caller-owned mutable shelf image.<br/>
        /// </summary>
        /// <param name="bytes">The validated mutable shelf image retained by the batch.<br/></param>
        internal Fixed32Scalar8BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
            initialItemCount = Fixed32Scalar8Layout.ReadItemCount(bytes);
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        /// <summary>
        /// Expands the accumulated dirty regions to include one successful `FS32-8` insertion.<br/>
        /// </summary>
        /// <param name="bounds">The conservative mutation bounds returned by the shelf insert.<br/></param>
        internal void Include(Fixed32Scalar8MutationBounds bounds)
        {
            IncludeFixedScalarBatchRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeFixedScalarBatchRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeFixedScalarBatchRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        /// <summary>
        /// Retains the in-place left prefix after a sorted-tail split and clips publication to items created after this image was acquired.<br/>
        /// The split path already stages the truncated count, so only unpublished dense slots and payload cells that remain on the left are kept dirty.<br/>
        /// </summary>
        /// <param name="profile">The `FS32-8` profile that maps retained item indexes to slot and payload byte ranges.<br/></param>
        internal void RetainSortedLeft(Fixed32Scalar8Profile profile)
        {
            int retainedItemCount = Fixed32Scalar8Layout.ReadItemCount(Bytes);
            headerStart = int.MaxValue;
            headerEnd = 0;
            slotStart = int.MaxValue;
            slotEnd = 0;
            itemStart = int.MaxValue;
            itemEnd = 0;
            Dirty = initialItemCount < retainedItemCount;
            if (!Dirty)
            {
                return;
            }

            slotStart = Fixed32Scalar8Layout.GetSlotOffset(profile, initialItemCount);
            slotEnd = Fixed32Scalar8Layout.GetSlotOffset(profile, retainedItemCount);
            itemStart = Fixed32Scalar8Layout.GetItemOffset(profile, initialItemCount);
            itemEnd = Fixed32Scalar8Layout.GetItemOffset(profile, retainedItemCount);
        }

        /// <summary>
        /// Gets the accumulated changed header range.<br/>
        /// </summary>
        /// <returns>The changed header span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetHeaderSpan() => GetFixedScalarBatchSpan(headerStart, headerEnd);

        /// <summary>
        /// Gets the accumulated changed sorted-slot range.<br/>
        /// </summary>
        /// <returns>The changed slot span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetSlotSpan() => GetFixedScalarBatchSpan(slotStart, slotEnd);

        /// <summary>
        /// Gets the accumulated changed physical-item range.<br/>
        /// </summary>
        /// <returns>The changed item span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetItemSpan() => GetFixedScalarBatchSpan(itemStart, itemEnd);
    }

    /// <summary>
    /// Retains one mutable `FS32-16` shelf image and its conservative changed regions.<br/>
    /// The image remains batch-owned until publication, abort, or structural split fallback.<br/>
    /// </summary>
    private sealed class Fixed32Scalar16BatchShelfCacheEntry
    {
        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;
        private readonly int initialItemCount;

        /// <summary>
        /// Creates an `FS32-16` cache entry over one caller-owned mutable shelf image.<br/>
        /// </summary>
        /// <param name="bytes">The validated mutable shelf image retained by the batch.<br/></param>
        internal Fixed32Scalar16BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
            initialItemCount = Fixed32Scalar16Layout.ReadItemCount(bytes);
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        /// <summary>
        /// Expands the accumulated dirty regions to include one successful `FS32-16` insertion.<br/>
        /// </summary>
        /// <param name="bounds">The conservative mutation bounds returned by the shelf insert.<br/></param>
        internal void Include(Fixed32Scalar16MutationBounds bounds)
        {
            IncludeFixedScalarBatchRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeFixedScalarBatchRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeFixedScalarBatchRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        /// <summary>
        /// Retains the in-place left prefix after a sorted-tail split and clips publication to items created after this image was acquired.<br/>
        /// The split path already stages the truncated count, so only unpublished dense slots and payload cells that remain on the left are kept dirty.<br/>
        /// </summary>
        /// <param name="profile">The `FS32-16` profile that maps retained item indexes to slot and payload byte ranges.<br/></param>
        internal void RetainSortedLeft(Fixed32Scalar16Profile profile)
        {
            int retainedItemCount = Fixed32Scalar16Layout.ReadItemCount(Bytes);
            headerStart = int.MaxValue;
            headerEnd = 0;
            slotStart = int.MaxValue;
            slotEnd = 0;
            itemStart = int.MaxValue;
            itemEnd = 0;
            Dirty = initialItemCount < retainedItemCount;
            if (!Dirty)
            {
                return;
            }

            slotStart = Fixed32Scalar16Layout.GetSlotOffset(profile, initialItemCount);
            slotEnd = Fixed32Scalar16Layout.GetSlotOffset(profile, retainedItemCount);
            itemStart = Fixed32Scalar16Layout.GetItemOffset(profile, initialItemCount);
            itemEnd = Fixed32Scalar16Layout.GetItemOffset(profile, retainedItemCount);
        }

        /// <summary>
        /// Gets the accumulated changed header range.<br/>
        /// </summary>
        /// <returns>The changed header span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetHeaderSpan() => GetFixedScalarBatchSpan(headerStart, headerEnd);

        /// <summary>
        /// Gets the accumulated changed sorted-slot range.<br/>
        /// </summary>
        /// <returns>The changed slot span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetSlotSpan() => GetFixedScalarBatchSpan(slotStart, slotEnd);

        /// <summary>
        /// Gets the accumulated changed physical-item range.<br/>
        /// </summary>
        /// <returns>The changed item span, or an empty span when unchanged.<br/></returns>
        internal FixedScalarBatchChangedSpan GetItemSpan() => GetFixedScalarBatchSpan(itemStart, itemEnd);
    }

    /// <summary>
    /// Identifies one cached fixed-32 route by the encoded key prefix consumed through a router depth.<br/>
    /// `FS32-8` and `FS32-16` share the prefix representation while retaining shape-specific route targets.<br/>
    /// </summary>
    /// <param name="Depth">The zero-based final encoded-key byte included in the prefix.<br/></param>
    /// <param name="Prefix0">The retained prefix bits from the first key lane.<br/></param>
    /// <param name="Prefix1">The retained prefix bits from the second key lane.<br/></param>
    /// <param name="Prefix2">The retained prefix bits from the third key lane.<br/></param>
    /// <param name="Prefix3">The retained prefix bits from the fourth key lane.<br/></param>
    private readonly record struct Fixed32BatchRouteCacheKey(byte Depth, ulong Prefix0, ulong Prefix1, ulong Prefix2, ulong Prefix3)
    {
        /// <summary>
        /// Creates a fixed-32 route-cache key from the router depth that produced a shelf target.<br/>
        /// </summary>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <param name="routerDepth">The zero-based final routed key byte.<br/></param>
        /// <returns>The bounded encoded-prefix cache key.<br/></returns>
        internal static Fixed32BatchRouteCacheKey Create(ulong key0, ulong key1, ulong key2, ulong key3, ushort routerDepth)
        {
            if (routerDepth > 31)
            {
                throw new InvalidDataException($"The fixed-32 route depth {routerDepth} cannot be cached for a 32-byte encoded key.");
            }

            return CreateForDepth(key0, key1, key2, key3, (byte)routerDepth);
        }

        /// <summary>
        /// Creates a fixed-32 route-cache key for one already bounded key-byte depth.<br/>
        /// </summary>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <param name="depth">The zero-based final encoded-key byte to retain.<br/></param>
        /// <returns>The encoded-prefix cache key.<br/></returns>
        internal static Fixed32BatchRouteCacheKey CreateForDepth(ulong key0, ulong key1, ulong key2, ulong key3, byte depth)
        {
            int prefixBits = (depth + 1) * 8;
            ulong prefix0 = 0;
            ulong prefix1 = 0;
            ulong prefix2 = 0;
            ulong prefix3 = 0;
            if (prefixBits <= 64)
            {
                prefix0 = key0 >> (64 - prefixBits);
            }
            else if (prefixBits <= 128)
            {
                prefix0 = key0;
                prefix1 = key1 >> (128 - prefixBits);
            }
            else if (prefixBits <= 192)
            {
                prefix0 = key0;
                prefix1 = key1;
                prefix2 = key2 >> (192 - prefixBits);
            }
            else
            {
                prefix0 = key0;
                prefix1 = key1;
                prefix2 = key2;
                prefix3 = key3 >> (256 - prefixBits);
            }

            return new Fixed32BatchRouteCacheKey(depth, prefix0, prefix1, prefix2, prefix3);
        }
    }

    /// <summary>
    /// Compares two encoded fixed-32 keys in persisted lane order without allocating key objects.<br/>
    /// </summary>
    /// <param name="left0">The first lane of the left encoded key.<br/></param>
    /// <param name="left1">The second lane of the left encoded key.<br/></param>
    /// <param name="left2">The third lane of the left encoded key.<br/></param>
    /// <param name="left3">The fourth lane of the left encoded key.<br/></param>
    /// <param name="right0">The first lane of the right encoded key.<br/></param>
    /// <param name="right1">The second lane of the right encoded key.<br/></param>
    /// <param name="right2">The third lane of the right encoded key.<br/></param>
    /// <param name="right3">The fourth lane of the right encoded key.<br/></param>
    /// <returns>A negative, zero, or positive value when the left key is less than, equal to, or greater than the right key.<br/></returns>
    private static int CompareFixed32BatchKeys(
        ulong left0,
        ulong left1,
        ulong left2,
        ulong left3,
        ulong right0,
        ulong right1,
        ulong right2,
        ulong right3)
    {
        int comparison = left0.CompareTo(right0);
        if (comparison != 0) return comparison;
        comparison = left1.CompareTo(right1);
        if (comparison != 0) return comparison;
        comparison = left2.CompareTo(right2);
        return comparison != 0 ? comparison : left3.CompareTo(right3);
    }

    /// <summary>
    /// Counts equal leading bytes across two encoded fixed-32 keys using lane XOR and leading-zero counts.<br/>
    /// </summary>
    /// <param name="left0">The first lane of the left encoded key.<br/></param>
    /// <param name="left1">The second lane of the left encoded key.<br/></param>
    /// <param name="left2">The third lane of the left encoded key.<br/></param>
    /// <param name="left3">The fourth lane of the left encoded key.<br/></param>
    /// <param name="right0">The first lane of the right encoded key.<br/></param>
    /// <param name="right1">The second lane of the right encoded key.<br/></param>
    /// <param name="right2">The third lane of the right encoded key.<br/></param>
    /// <param name="right3">The fourth lane of the right encoded key.<br/></param>
    /// <returns>The number of equal leading encoded-key bytes from zero through thirty-two.<br/></returns>
    private static int GetFixed32BatchCommonPrefixByteCount(
        ulong left0,
        ulong left1,
        ulong left2,
        ulong left3,
        ulong right0,
        ulong right1,
        ulong right2,
        ulong right3)
    {
        ulong difference = left0 ^ right0;
        if (difference != 0) return System.Numerics.BitOperations.LeadingZeroCount(difference) / 8;
        difference = left1 ^ right1;
        if (difference != 0) return 8 + (System.Numerics.BitOperations.LeadingZeroCount(difference) / 8);
        difference = left2 ^ right2;
        if (difference != 0) return 16 + (System.Numerics.BitOperations.LeadingZeroCount(difference) / 8);
        difference = left3 ^ right3;
        return difference == 0 ? 32 : 24 + (System.Numerics.BitOperations.LeadingZeroCount(difference) / 8);
    }

    /// <summary>
    /// Extracts one persisted big-endian routing byte from an encoded fixed-32 key.<br/>
    /// </summary>
    /// <param name="key0">The first encoded fixed-key lane.<br/></param>
    /// <param name="key1">The second encoded fixed-key lane.<br/></param>
    /// <param name="key2">The third encoded fixed-key lane.<br/></param>
    /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
    /// <param name="depth">The zero-based routing-byte depth.<br/></param>
    /// <returns>The encoded routing byte at the requested depth.<br/></returns>
    private static byte GetFixed32BatchKeyByte(ulong key0, ulong key1, ulong key2, ulong key3, ushort depth)
    {
        if (depth > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "Fixed-32 keys expose exactly thirty-two routing bytes.");
        }

        int shift = (7 - (depth & 7)) * 8;
        return (depth >> 3) switch
        {
            0 => (byte)(key0 >> shift),
            1 => (byte)(key1 >> shift),
            2 => (byte)(key2 >> shift),
            _ => (byte)(key3 >> shift)
        };
    }

    /// <summary>
    /// Retains the most recently resolved `FS32-8` shelf route for allocation-free sequential reuse.<br/>
    /// A hit requires the next encoded key to share the exact routed prefix, so the cursor is valid for sorted runs and adjacent random keys alike.<br/>
    /// </summary>
    private struct Fixed32Scalar8RouteCursor
    {
        private Fixed32Scalar8RoutePathTarget pathTarget;
        private ulong key0;
        private ulong key1;
        private ulong key2;
        private ulong key3;
        private byte prefixRangeStart;
        private byte prefixRangeEnd;
        private Fixed32RouterOffsetFrontier routerOffsets;
        private bool active;

        /// <summary>
        /// Resolves the retained shelf when the encoded key still belongs to its routed prefix.<br/>
        /// </summary>
        /// <param name="session">The owning session used to validate an adjacent parent-router prefix.<br/></param>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <param name="resolvedTarget">The retained terminal shelf target when matched.<br/></param>
        /// <returns><see langword="true"/> when the retained route prefix matches the encoded key.<br/></returns>
        internal bool TryGet(LibraDexFileSession session, ulong key0, ulong key1, ulong key2, ulong key3, out Fixed32Scalar8RoutePathTarget resolvedTarget)
        {
            if (!active)
            {
                resolvedTarget = default;
                return false;
            }

            if (CompareFixed32BatchKeys(this.key0, this.key1, this.key2, this.key3, key0, key1, key2, key3) > 0)
            {
                routerOffsets = default;
                active = false;
                resolvedTarget = default;
                return false;
            }

            Fixed32Scalar8RouteTarget target = pathTarget.Target;
            int commonPrefixByteCount = GetFixed32BatchCommonPrefixByteCount(this.key0, this.key1, this.key2, this.key3, key0, key1, key2, key3);
            if (commonPrefixByteCount >= target.RouterDepth)
            {
                byte prefixByte = GetFixed32BatchKeyByte(key0, key1, key2, key3, target.RouterDepth);
                if ((prefixByte >= prefixRangeStart && prefixByte <= prefixRangeEnd) ||
                    session.TryGetDirectRouterTargetPrefixRange(pathTarget.ParentRouterOffset, prefixByte, target.Offset, out prefixRangeStart, out prefixRangeEnd))
                {
                    resolvedTarget = new Fixed32Scalar8RoutePathTarget(target, pathTarget.ParentRouterOffset, prefixByte);
                    return true;
                }
            }

            Span<long> offsets = routerOffsets;
            int restartDepth = Math.Min(commonPrefixByteCount, Fixed32Scalar8Layout.KeySize - 1);
            while (restartDepth >= 0 && offsets[restartDepth] == 0)
                restartDepth--;

            if (restartDepth >= 0)
            {
                long restartOffset = offsets[restartDepth];
                offsets[(restartDepth + 1)..].Clear();
                resolvedTarget = session.WalkFixed32Scalar8RoutePathTarget(
                    restartOffset,
                    key0,
                    key1,
                    key2,
                    key3,
                    maxRouterHops: 32,
                    offsets,
                    returnDefaultWhenUnset: true);
                return resolvedTarget.Target.Offset > 0;
            }

            resolvedTarget = default;
            return false;
        }

        /// <summary>
        /// Walks one complete `FS32-8` route and captures every visited router offset by encoded key depth.<br/>
        /// Later monotonic keys can restart at their deepest unchanged radix ancestor instead of rereading the route from the index root.<br/>
        /// </summary>
        /// <param name="session">The owning file session used for route classification.<br/></param>
        /// <param name="rootRouterOffset">The index root router offset.<br/></param>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <returns>The terminal shelf target plus its direct parent route.<br/></returns>
        internal Fixed32Scalar8RoutePathTarget WalkFromRoot(LibraDexFileSession session, long rootRouterOffset, ulong key0, ulong key1, ulong key2, ulong key3)
        {
            Span<long> offsets = routerOffsets;
            offsets.Clear();
            return session.WalkFixed32Scalar8RoutePathTarget(rootRouterOffset, key0, key1, key2, key3, maxRouterHops: 32, offsets);
        }

        /// <summary>
        /// Retains one validated `FS32-8` terminal route and its complete encoded-key prefix.<br/>
        /// </summary>
        /// <param name="session">The owning session used to capture the parent-router target range.<br/></param>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <param name="resolvedPathTarget">The terminal shelf target and its direct parent route.<br/></param>
        internal void Set(LibraDexFileSession session, ulong key0, ulong key1, ulong key2, ulong key3, Fixed32Scalar8RoutePathTarget resolvedPathTarget)
        {
            Fixed32Scalar8RouteTarget resolvedTarget = resolvedPathTarget.Target;
            if (!active ||
                pathTarget.Target.Offset != resolvedTarget.Offset ||
                pathTarget.Target.RouterDepth != resolvedTarget.RouterDepth ||
                pathTarget.ParentRouterOffset != resolvedPathTarget.ParentRouterOffset ||
                resolvedPathTarget.RoutePrefixByte < prefixRangeStart ||
                resolvedPathTarget.RoutePrefixByte > prefixRangeEnd)
            {
                prefixRangeStart = resolvedPathTarget.RoutePrefixByte;
                prefixRangeEnd = resolvedPathTarget.RoutePrefixByte;
                _ = session.TryGetDirectRouterTargetPrefixRange(resolvedPathTarget.ParentRouterOffset, resolvedPathTarget.RoutePrefixByte, resolvedTarget.Offset, out prefixRangeStart, out prefixRangeEnd);
            }

            pathTarget = resolvedPathTarget;
            this.key0 = key0;
            this.key1 = key1;
            this.key2 = key2;
            this.key3 = key3;
            active = true;
        }

        /// <summary>
        /// Returns the final classified key and path so successful publication can seed the next public batch.<br/>
        /// </summary>
        /// <param name="resolvedPathTarget">The retained terminal shelf and direct parent route.<br/></param>
        /// <param name="resolvedKey0">The first encoded lane of the final classified key.<br/></param>
        /// <param name="resolvedKey1">The second encoded lane of the final classified key.<br/></param>
        /// <param name="resolvedKey2">The third encoded lane of the final classified key.<br/></param>
        /// <param name="resolvedKey3">The fourth encoded lane of the final classified key.<br/></param>
        /// <returns><see langword="true"/> when the cursor contains a publication-safe route.<br/></returns>
        internal bool TryGetRetained(out Fixed32Scalar8RoutePathTarget resolvedPathTarget, out ulong resolvedKey0, out ulong resolvedKey1, out ulong resolvedKey2, out ulong resolvedKey3)
        {
            resolvedPathTarget = pathTarget;
            resolvedKey0 = key0;
            resolvedKey1 = key1;
            resolvedKey2 = key2;
            resolvedKey3 = key3;
            return active;
        }

        /// <summary>
        /// Invalidates the retained route after publication, abort, or a structural split.<br/>
        /// </summary>
        internal void Clear()
        {
            this = default;
        }
    }

    /// <summary>
    /// Retains the most recently resolved `FS32-16` shelf route for allocation-free sequential reuse.<br/>
    /// A hit requires the next encoded key to share the exact routed prefix, so the cursor is valid for sorted runs and adjacent random keys alike.<br/>
    /// </summary>
    private struct Fixed32Scalar16RouteCursor
    {
        private Fixed32Scalar16RoutePathTarget pathTarget;
        private ulong key0;
        private ulong key1;
        private ulong key2;
        private ulong key3;
        private byte prefixRangeStart;
        private byte prefixRangeEnd;
        private Fixed32RouterOffsetFrontier routerOffsets;
        private bool active;

        /// <summary>
        /// Resolves the retained shelf when the encoded key still belongs to its routed prefix.<br/>
        /// </summary>
        /// <param name="session">The owning session used to validate an adjacent parent-router prefix.<br/></param>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <param name="resolvedTarget">The retained terminal shelf target when matched.<br/></param>
        /// <returns><see langword="true"/> when the retained route prefix matches the encoded key.<br/></returns>
        internal bool TryGet(LibraDexFileSession session, ulong key0, ulong key1, ulong key2, ulong key3, out Fixed32Scalar16RoutePathTarget resolvedTarget)
        {
            if (!active)
            {
                resolvedTarget = default;
                return false;
            }

            if (CompareFixed32BatchKeys(this.key0, this.key1, this.key2, this.key3, key0, key1, key2, key3) > 0)
            {
                routerOffsets = default;
                active = false;
                resolvedTarget = default;
                return false;
            }

            Fixed32Scalar16RouteTarget target = pathTarget.Target;
            int commonPrefixByteCount = GetFixed32BatchCommonPrefixByteCount(this.key0, this.key1, this.key2, this.key3, key0, key1, key2, key3);
            if (commonPrefixByteCount >= target.RouterDepth)
            {
                byte prefixByte = GetFixed32BatchKeyByte(key0, key1, key2, key3, target.RouterDepth);
                if ((prefixByte >= prefixRangeStart && prefixByte <= prefixRangeEnd) ||
                    session.TryGetDirectRouterTargetPrefixRange(pathTarget.ParentRouterOffset, prefixByte, target.Offset, out prefixRangeStart, out prefixRangeEnd))
                {
                    resolvedTarget = new Fixed32Scalar16RoutePathTarget(target, pathTarget.ParentRouterOffset, prefixByte);
                    return true;
                }
            }

            Span<long> offsets = routerOffsets;
            int restartDepth = Math.Min(commonPrefixByteCount, Fixed32Scalar16Layout.KeySize - 1);
            while (restartDepth >= 0 && offsets[restartDepth] == 0)
                restartDepth--;

            if (restartDepth >= 0)
            {
                long restartOffset = offsets[restartDepth];
                offsets[(restartDepth + 1)..].Clear();
                resolvedTarget = session.WalkFixed32Scalar16RoutePathTarget(
                    restartOffset,
                    key0,
                    key1,
                    key2,
                    key3,
                    maxRouterHops: 32,
                    offsets,
                    returnDefaultWhenUnset: true);
                return resolvedTarget.Target.Offset > 0;
            }

            resolvedTarget = default;
            return false;
        }

        /// <summary>
        /// Walks one complete `FS32-16` route and captures every visited router offset by encoded key depth.<br/>
        /// Later monotonic keys can restart at their deepest unchanged radix ancestor instead of rereading the route from the index root.<br/>
        /// </summary>
        /// <param name="session">The owning file session used for route classification.<br/></param>
        /// <param name="rootRouterOffset">The index root router offset.<br/></param>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <returns>The terminal shelf target plus its direct parent route.<br/></returns>
        internal Fixed32Scalar16RoutePathTarget WalkFromRoot(LibraDexFileSession session, long rootRouterOffset, ulong key0, ulong key1, ulong key2, ulong key3)
        {
            Span<long> offsets = routerOffsets;
            offsets.Clear();
            return session.WalkFixed32Scalar16RoutePathTarget(rootRouterOffset, key0, key1, key2, key3, maxRouterHops: 32, offsets);
        }

        /// <summary>
        /// Retains one validated `FS32-16` terminal route and its complete encoded-key prefix.<br/>
        /// </summary>
        /// <param name="session">The owning session used to capture the parent-router target range.<br/></param>
        /// <param name="key0">The first encoded fixed-key lane.<br/></param>
        /// <param name="key1">The second encoded fixed-key lane.<br/></param>
        /// <param name="key2">The third encoded fixed-key lane.<br/></param>
        /// <param name="key3">The fourth encoded fixed-key lane.<br/></param>
        /// <param name="resolvedPathTarget">The terminal shelf target and its direct parent route.<br/></param>
        internal void Set(LibraDexFileSession session, ulong key0, ulong key1, ulong key2, ulong key3, Fixed32Scalar16RoutePathTarget resolvedPathTarget)
        {
            Fixed32Scalar16RouteTarget resolvedTarget = resolvedPathTarget.Target;
            if (!active ||
                pathTarget.Target.Offset != resolvedTarget.Offset ||
                pathTarget.Target.RouterDepth != resolvedTarget.RouterDepth ||
                pathTarget.ParentRouterOffset != resolvedPathTarget.ParentRouterOffset ||
                resolvedPathTarget.RoutePrefixByte < prefixRangeStart ||
                resolvedPathTarget.RoutePrefixByte > prefixRangeEnd)
            {
                prefixRangeStart = resolvedPathTarget.RoutePrefixByte;
                prefixRangeEnd = resolvedPathTarget.RoutePrefixByte;
                _ = session.TryGetDirectRouterTargetPrefixRange(resolvedPathTarget.ParentRouterOffset, resolvedPathTarget.RoutePrefixByte, resolvedTarget.Offset, out prefixRangeStart, out prefixRangeEnd);
            }

            pathTarget = resolvedPathTarget;
            this.key0 = key0;
            this.key1 = key1;
            this.key2 = key2;
            this.key3 = key3;
            active = true;
        }

        /// <summary>
        /// Returns the final classified key and path so successful publication can seed the next public batch.<br/>
        /// </summary>
        /// <param name="resolvedPathTarget">The retained terminal shelf and direct parent route.<br/></param>
        /// <param name="resolvedKey0">The first encoded lane of the final classified key.<br/></param>
        /// <param name="resolvedKey1">The second encoded lane of the final classified key.<br/></param>
        /// <param name="resolvedKey2">The third encoded lane of the final classified key.<br/></param>
        /// <param name="resolvedKey3">The fourth encoded lane of the final classified key.<br/></param>
        /// <returns><see langword="true"/> when the cursor contains a publication-safe route.<br/></returns>
        internal bool TryGetRetained(out Fixed32Scalar16RoutePathTarget resolvedPathTarget, out ulong resolvedKey0, out ulong resolvedKey1, out ulong resolvedKey2, out ulong resolvedKey3)
        {
            resolvedPathTarget = pathTarget;
            resolvedKey0 = key0;
            resolvedKey1 = key1;
            resolvedKey2 = key2;
            resolvedKey3 = key3;
            return active;
        }

        /// <summary>
        /// Invalidates the retained route after publication, abort, or a structural split.<br/>
        /// </summary>
        internal void Clear()
        {
            this = default;
        }
    }

    /// <summary>
    /// Stores one allocation-free fixed-key router offset for every possible encoded byte depth.<br/>
    /// The inline storage keeps monotonic route-frontier ownership inside the batch object without a per-batch array allocation.<br/>
    /// </summary>
    [InlineArray(32)]
    private struct Fixed32RouterOffsetFrontier
    {
        private long first;
    }
}
