using LibraDex.Layouts;
using LibraDex.Views;
using System.Diagnostics;

namespace LibraDex;

/// <summary>
/// Batches encoded `SS8-8` mutations by deferring durability publication until commit.<br/>
/// The first batch model is not a SQL transaction: it is a developer-controlled durability cadence for bulk identity-index mutation, with abort implemented as discarding unpublished staged writes.<br/>
/// </summary>
internal sealed class Scalar8Scalar8Batch : IDisposable
{
    private readonly Scalar8Scalar8Index index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private readonly bool ownsDurabilityBatch;
    private readonly Dictionary<long, Scalar8Scalar8BatchShelfCacheEntry> shelfCache = [];
    private readonly Dictionary<Scalar8Scalar8BatchRouteCacheKey, Scalar8Scalar8RouteTarget> routeTargetCache = [];
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private Scalar8Scalar8BatchInsertAttributionTelemetry insertAttributionTelemetry;
    private Scalar8Scalar8BatchPublicationAttributionTelemetry publicationAttributionTelemetry;
    private Scalar8Scalar8BatchCommitAttributionTelemetry commitAttributionTelemetry;
    private bool insertAttributionEnabled;
    private bool publicationAttributionEnabled;
    private bool commitAttributionEnabled;
    private bool completed;

    internal Scalar8Scalar8Batch(
        Scalar8Scalar8Index index,
        LibraDexFileSessionDurabilityBatch durabilityBatch)
        : this(index, durabilityBatch, ownsDurabilityBatch: true)
    {
    }

    internal Scalar8Scalar8Batch(
        Scalar8Scalar8Index index,
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        bool ownsDurabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
        this.ownsDurabilityBatch = ownsDurabilityBatch;
    }

    /// <summary>
    /// Inserts one encoded tuple into the owning index without forcing a durable commit per item.<br/>
    /// The mutation uses the same routed storage path as normal encoded inserts, but any internal commit requests are folded into the surrounding batch commit.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.</param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.</param>
    /// <returns>The per-item encoded insert result.</returns>
    public Scalar8Scalar8EncodedInsertResult InsertEncoded(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();

        Scalar8Scalar8EncodedInsertResult result;
        if (!TryInsertCached(encodedKey, encodedIdentity, allowDuplicateKeys, out result, out long fullShelfOffset))
        {
            if (fullShelfOffset == 0 || !FlushSingleShelfForSplitFallback(fullShelfOffset))
            {
                FlushShelfCache(forceFullRewrite: true);
            }

            routeTargetCache.Clear();
            result = index.InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
        }

        attemptedInsertCount++;
        if (result.CreatedInitialShelfRoute)
        {
            initialShelfRouteCreateCount++;
        }

        switch (result.Outcome)
        {
            case Scalar8Scalar8EncodedInsertOutcome.Inserted:
                insertedCount++;
                break;
            case Scalar8Scalar8EncodedInsertOutcome.AlreadyPresent:
                alreadyPresentCount++;
                break;
            case Scalar8Scalar8EncodedInsertOutcome.KeyConflict:
                keyConflictCount++;
                break;
        }

        return result;
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise all-or-nothing item semantics beyond the staged writes that reach this publish point.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public Scalar8Scalar8BatchCommitResult Commit()
    {
        ThrowIfCompleted();
        if (!ownsDurabilityBatch)
        {
            throw new InvalidOperationException("A shared SS8-8 batch cannot publish the owning durability boundary directly.");
        }

        completed = true;
        long flushStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
        FlushShelfCache(forceFullRewrite: false, clearAfterFlush: false);
        if (commitAttributionEnabled)
        {
            commitAttributionTelemetry.DirtyShelfFlushTicks += Stopwatch.GetTimestamp() - flushStart;
        }

        long commitStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
        (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
        if (commitAttributionEnabled)
        {
            commitAttributionTelemetry.DataKernelCommitTicks += Stopwatch.GetTimestamp() - commitStart;
        }

        StoreCleanShelfCacheEntriesAfterCommit();
        shelfCache.Clear();
        routeTargetCache.Clear();
        return new Scalar8Scalar8BatchCommitResult(
            attemptedInsertCount,
            insertedCount,
            alreadyPresentCount,
            keyConflictCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the encoded `SS8-8` batch and closes this batch boundary.<br/>
    /// This is the preferred spelling for new code because it describes visibility and durability cadence without implying a database transaction commit.<br/>
    /// The current implementation delegates to <see cref="Commit"/> so existing telemetry, cache promotion, and compatibility behavior remain identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel publication telemetry.</returns>
    public Scalar8Scalar8BatchCommitResult Publish()
    {
        return Commit();
    }

    /// <summary>
    /// Aborts the batch by discarding staged writes that were not published.<br/>
    /// This is a revert-to-current-backing-state operation and is intentionally heavier than a normal successful commit path.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.</returns>
    public Scalar8Scalar8BatchAbortResult Abort()
    {
        ThrowIfCompleted();
        if (!ownsDurabilityBatch)
        {
            throw new InvalidOperationException("A shared SS8-8 batch cannot abort the owning durability boundary directly.");
        }

        completed = true;
        shelfCache.Clear();
        routeTargetCache.Clear();
        long deferredRequests = durabilityBatch.Abort();
        return new Scalar8Scalar8BatchAbortResult(attemptedInsertCount, deferredRequests);
    }

    /// <summary>
    /// Aborts uncommitted staged writes when the batch is disposed without explicit commit or abort.<br/>
    /// This makes `using` scopes safe for early exits while keeping `Commit` as the only durability publication call.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed && ownsDurabilityBatch)
        {
            Abort();
        }
    }

    /// <summary>
    /// Flushes dirty cached shelves into a shared outer durability batch without committing the session.<br/>
    /// Catalog group batching uses this to preserve the SS8-8 shelf-cache benefit while still publishing all participating indexes through one group commit.<br/>
    /// </summary>
    internal void PrepareSharedDurabilityCommit()
    {
        ThrowIfCompleted();
        FlushShelfCache(forceFullRewrite: false, clearAfterFlush: false);
    }

    /// <summary>
    /// Completes a shared outer durability commit after the owning batch manager has successfully published the session writes.<br/>
    /// Clean shelf images are made available to the session-local reuse cache only after the DataKernel commit succeeds.<br/>
    /// </summary>
    internal void CompleteSharedDurabilityCommit()
    {
        ThrowIfCompleted();
        StoreCleanShelfCacheEntriesAfterCommit();
        shelfCache.Clear();
        routeTargetCache.Clear();
        completed = true;
    }

    /// <summary>
    /// Drops shared-batch cached shelf state without touching the outer session durability batch.<br/>
    /// This is the shared-batch counterpart to abort, used when the owner will discard pending DataKernel writes itself.<br/>
    /// </summary>
    internal void AbortSharedDurabilityBatch()
    {
        if (completed)
        {
            return;
        }

        shelfCache.Clear();
        routeTargetCache.Clear();
        completed = true;
    }

    /// <summary>
    /// Returns the internal insert-path attribution counters accumulated by this batch.<br/>
    /// The harness uses this diagnostic snapshot to split cached batch insert time into routing, cache lookup, and shelf-local work without changing public batch semantics.<br/>
    /// </summary>
    /// <returns>The current insert-path attribution telemetry snapshot.</returns>
    internal Scalar8Scalar8BatchInsertAttributionTelemetry GetInsertAttributionTelemetry()
    {
        return insertAttributionTelemetry;
    }

    /// <summary>
    /// Returns the internal dirty-shelf publication attribution counters accumulated by this batch.<br/>
    /// The harness uses this diagnostic snapshot to estimate whether cached dirty shelves could be published as compact deltas instead of full shelf extents.<br/>
    /// </summary>
    /// <returns>The current publication attribution telemetry snapshot.</returns>
    internal Scalar8Scalar8BatchPublicationAttributionTelemetry GetPublicationAttributionTelemetry()
    {
        return publicationAttributionTelemetry;
    }

    /// <summary>
    /// Returns the internal commit-path attribution counters accumulated by this batch.<br/>
    /// The harness uses this diagnostic snapshot to split batch publication time above the DataKernel commit boundary.<br/>
    /// </summary>
    /// <returns>The current commit-path attribution telemetry snapshot.</returns>
    internal Scalar8Scalar8BatchCommitAttributionTelemetry GetCommitAttributionTelemetry()
    {
        return commitAttributionTelemetry;
    }

    /// <summary>
    /// Enables internal insert-path attribution for this batch.<br/>
    /// Normal public batches leave this disabled so diagnostic Stopwatch probes do not affect the regular bulk-write path.<br/>
    /// </summary>
    internal void EnableInsertAttribution()
    {
        insertAttributionEnabled = true;
    }

    /// <summary>
    /// Enables internal dirty-shelf publication attribution for this batch.<br/>
    /// Normal public batches leave this disabled so original shelf snapshots are not cloned unless a diagnostic run needs delta estimates.<br/>
    /// </summary>
    internal void EnablePublicationAttribution()
    {
        publicationAttributionEnabled = true;
    }

    /// <summary>
    /// Enables internal commit-path attribution for this batch.<br/>
    /// Normal public batches leave this disabled so diagnostic Stopwatch probes do not affect the regular bulk-write path.<br/>
    /// </summary>
    internal void EnableCommitAttribution()
    {
        commitAttributionEnabled = true;
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The SS8-8 batch has already completed.");
        }
    }

    /// <summary>
    /// Attempts to insert into a batch-local mutable shelf image without staging a full-shelf rewrite per item.<br/>
    /// The method only handles already-routed shelf targets that can accept the tuple without splitting; full shelves fall back to the established routed split path after cached shelves are flushed.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.</param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.</param>
    /// <param name="result">The cached insert result when the tuple was handled by the shelf cache.</param>
    /// <param name="fullShelfOffset">The cached dirty shelf offset that needs split-capable fallback when a cached insert reaches a full shelf.</param>
    /// <returns>True when the cache handled the insert; otherwise false so the caller can use the normal routed path.</returns>
    private bool TryInsertCached(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out Scalar8Scalar8EncodedInsertResult result,
        out long fullShelfOffset)
    {
        result = default;
        fullShelfOffset = 0;
        Scalar8Scalar8IndexHandle handle = index.Handle;
        byte rootPrefixByte = (byte)(encodedKey >> 56);

        if (!insertAttributionEnabled)
        {
            if (!TryGetCachedRouteTarget(encodedKey, handle.RootRouterOffset, rootPrefixByte, out Scalar8Scalar8RouteTarget fastTarget))
            {
                return false;
            }

            Scalar8Scalar8BatchShelfCacheEntry fastEntry = GetOrCreateShelfCacheEntry(fastTarget.Offset, handle.Profile);
            Scalar8Scalar8 fastShelf = new(fastEntry.Bytes, handle.Profile);
            Scalar8Scalar8InsertResult fastInsertResult = fastShelf.InsertWithMutationBounds(encodedKey, encodedIdentity, allowDuplicateKeys, out Scalar8Scalar8MutationBounds fastMutationBounds);
            if (fastInsertResult == Scalar8Scalar8InsertResult.Full)
            {
                fullShelfOffset = fastTarget.Offset;
                return false;
            }

            result = CreateCachedInsertResult(fastInsertResult, fastTarget.Offset);
            if (fastInsertResult == Scalar8Scalar8InsertResult.Inserted)
            {
                fastEntry.Dirty = true;
                fastEntry.Include(fastMutationBounds);
            }

            return true;
        }

        insertAttributionTelemetry.AttemptCount++;
        if (!TryGetCachedRouteTargetWithAttribution(encodedKey, handle.RootRouterOffset, rootPrefixByte, out Scalar8Scalar8RouteTarget target))
        {
            return false;
        }

        long cacheLookupStart = Stopwatch.GetTimestamp();
        Scalar8Scalar8BatchShelfCacheEntry entry = GetOrCreateShelfCacheEntryWithAttribution(target.Offset, handle.Profile);
        insertAttributionTelemetry.ShelfCacheLookupTicks += Stopwatch.GetTimestamp() - cacheLookupStart;
        Scalar8Scalar8 shelf = new(entry.Bytes, handle.Profile);
        long shelfInsertStart = Stopwatch.GetTimestamp();
        Scalar8Scalar8InsertResult insertResult = shelf.InsertWithAttributionAndMutationBounds(encodedKey, encodedIdentity, allowDuplicateKeys, ref insertAttributionTelemetry, out Scalar8Scalar8MutationBounds mutationBounds);
        insertAttributionTelemetry.ShelfInsertTicks += Stopwatch.GetTimestamp() - shelfInsertStart;
        if (insertResult == Scalar8Scalar8InsertResult.Full)
        {
            insertAttributionTelemetry.CacheMissFullShelfCount++;
            fullShelfOffset = target.Offset;
            return false;
        }

        if (insertResult == Scalar8Scalar8InsertResult.Inserted)
        {
            entry.Dirty = true;
            entry.Include(mutationBounds);
        }

        result = CreateCachedInsertResult(insertResult, target.Offset);
        insertAttributionTelemetry.CachedHandledCount++;
        return true;
    }

    /// <summary>
    /// Resolves the routed shelf target using the batch-local route-target cache when possible.<br/>
    /// The cache key is the encoded-key prefix through the router depth that produced the shelf, so cached hits only reuse a target for keys that follow the same deterministic route prefix.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable 8-byte scalar key.</param>
    /// <param name="rootRouterOffset">The root router offset for this index handle.</param>
    /// <param name="rootPrefixByte">The already extracted root prefix byte.</param>
    /// <param name="target">The resolved shelf route target when one is available.</param>
    /// <returns>True when a shelf target is available; otherwise false so the caller can use the split-capable path.</returns>
    private bool TryGetCachedRouteTarget(
        ulong encodedKey,
        long rootRouterOffset,
        byte rootPrefixByte,
        out Scalar8Scalar8RouteTarget target)
    {
        if (TryGetCachedRouteTargetByPrefix(encodedKey, out target))
        {
            return true;
        }

        if (index.Session.FindRouterTarget(rootRouterOffset, rootPrefixByte) == 0)
        {
            target = default;
            return false;
        }

        Scalar8Scalar8RoutePathTarget pathTarget = index.Session.WalkScalar8Scalar8RoutePathTarget(
            rootRouterOffset,
            encodedKey,
            maxRouterHops: 8,
            Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
        target = pathTarget.Target;
        if (target.Kind != Scalar8Scalar8RouteTargetKind.Shelf)
        {
            target = default;
            return false;
        }

        routeTargetCache[Scalar8Scalar8BatchRouteCacheKey.Create(encodedKey, target.RouterDepth)] = target;
        return true;
    }

    /// <summary>
    /// Resolves the routed shelf target while attributing route-cache, root-lookup, route-walk, and classification costs.<br/>
    /// The method mirrors the normal resolver so diagnostic evidence reflects the same cache behavior used by regular batch writes.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable 8-byte scalar key.</param>
    /// <param name="rootRouterOffset">The root router offset for this index handle.</param>
    /// <param name="rootPrefixByte">The already extracted root prefix byte.</param>
    /// <param name="target">The resolved shelf route target when one is available.</param>
    /// <returns>True when a shelf target is available; otherwise false so the caller can use the split-capable path.</returns>
    private bool TryGetCachedRouteTargetWithAttribution(
        ulong encodedKey,
        long rootRouterOffset,
        byte rootPrefixByte,
        out Scalar8Scalar8RouteTarget target)
    {
        long routeCacheProbeStart = Stopwatch.GetTimestamp();
        if (TryGetCachedRouteTargetByPrefix(encodedKey, out target))
        {
            insertAttributionTelemetry.RouteCacheProbeTicks += Stopwatch.GetTimestamp() - routeCacheProbeStart;
            insertAttributionTelemetry.RouteCacheHitCount++;
            return true;
        }

        insertAttributionTelemetry.RouteCacheProbeTicks += Stopwatch.GetTimestamp() - routeCacheProbeStart;
        insertAttributionTelemetry.RouteCacheMissCount++;
        long rootLookupStart = Stopwatch.GetTimestamp();
        if (index.Session.FindRouterTarget(rootRouterOffset, rootPrefixByte) == 0)
        {
            insertAttributionTelemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
            insertAttributionTelemetry.CacheMissEmptyRouteCount++;
            target = default;
            return false;
        }

        insertAttributionTelemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
        long routeWalkStart = Stopwatch.GetTimestamp();
        Scalar8Scalar8RoutePathTarget pathTarget = index.Session.WalkScalar8Scalar8RoutePathTargetWithAttribution(
            rootRouterOffset,
            encodedKey,
            maxRouterHops: 8,
            Scalar8Scalar8RouteReadPolicy.PreferPromotedViews,
            ref insertAttributionTelemetry.RouteWalkAttribution);
        insertAttributionTelemetry.RouteWalkTicks += Stopwatch.GetTimestamp() - routeWalkStart;
        long classificationStart = Stopwatch.GetTimestamp();
        target = pathTarget.Target;
        if (target.Kind != Scalar8Scalar8RouteTargetKind.Shelf)
        {
            insertAttributionTelemetry.RouteTargetClassificationTicks += Stopwatch.GetTimestamp() - classificationStart;
            insertAttributionTelemetry.CacheMissNonShelfTargetCount++;
            target = default;
            return false;
        }

        insertAttributionTelemetry.RouteTargetClassificationTicks += Stopwatch.GetTimestamp() - classificationStart;
        routeTargetCache[Scalar8Scalar8BatchRouteCacheKey.Create(encodedKey, target.RouterDepth)] = target;
        return true;
    }

    /// <summary>
    /// Looks up a cached route target by probing the small fixed set of possible encoded-key prefix depths.<br/>
    /// This avoids scanning the route-target cache while preserving the same prefix-match semantics for cached shelf targets.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable 8-byte scalar key.</param>
    /// <param name="target">The cached shelf target when one is found.</param>
    /// <returns>True when the route-target cache contains a matching prefix.</returns>
    private bool TryGetCachedRouteTargetByPrefix(ulong encodedKey, out Scalar8Scalar8RouteTarget target)
    {
        for (byte depth = 7; depth > 0; depth--)
        {
            if (routeTargetCache.TryGetValue(Scalar8Scalar8BatchRouteCacheKey.CreateForDepth(encodedKey, depth), out target))
            {
                return true;
            }
        }

        return routeTargetCache.TryGetValue(Scalar8Scalar8BatchRouteCacheKey.CreateForDepth(encodedKey, 0), out target);
    }

    /// <summary>
    /// Creates the public encoded insert result for an insert handled by the batch-local shelf cache.<br/>
    /// Keeping this mapping shared avoids divergence between the normal cached path and the attribution-enabled cached path.<br/>
    /// </summary>
    /// <param name="insertResult">The shelf-local insert result.</param>
    /// <param name="shelfOffset">The offset of the shelf mutated by the cached path.</param>
    /// <returns>The encoded insert result returned to the batch caller.</returns>
    private static Scalar8Scalar8EncodedInsertResult CreateCachedInsertResult(Scalar8Scalar8InsertResult insertResult, long shelfOffset)
    {
        Scalar8Scalar8RoutedInsertKind kind = insertResult switch
        {
            Scalar8Scalar8InsertResult.Inserted => Scalar8Scalar8RoutedInsertKind.WalkedNoSplit,
            Scalar8Scalar8InsertResult.AlreadyPresent => Scalar8Scalar8RoutedInsertKind.NoOp,
            Scalar8Scalar8InsertResult.KeyConflict => Scalar8Scalar8RoutedInsertKind.KeyConflict,
            _ => throw new InvalidDataException($"Unsupported batch-local SS8-8 insert result {insertResult}.")
        };
        return new Scalar8Scalar8EncodedInsertResult(
            MapInsertOutcome(insertResult),
            CreatedInitialShelfRoute: false,
            RouteCreateCommit: default,
            InsertCommit: default)
        {
            StructuralKind = kind,
            PrimaryOffset = shelfOffset,
            LeftShelfOffset = insertResult == Scalar8Scalar8InsertResult.Inserted ? shelfOffset : 0,
            RightShelfOffset = 0
        };
    }

    /// <summary>
    /// Gets an existing mutable shelf cache entry or reads the current shelf image from the owning session.<br/>
    /// The cached bytes become the authoritative batch-local image until they are flushed, committed, or aborted.<br/>
    /// </summary>
    /// <param name="shelfOffset">The shelf offset used as the cache key.</param>
    /// <param name="profile">The `SS8-8` profile that determines shelf extent size.</param>
    /// <returns>The mutable cache entry for the selected shelf.</returns>
    private Scalar8Scalar8BatchShelfCacheEntry GetOrCreateShelfCacheEntry(long shelfOffset, Scalar8Scalar8Profile profile)
    {
        if (shelfCache.TryGetValue(shelfOffset, out Scalar8Scalar8BatchShelfCacheEntry? existing))
        {
            return existing;
        }

        byte[] bytes = index.Session.ReadScalar8Scalar8ShelfBytesForBatch(shelfOffset, profile);
        byte[]? originalBytes = publicationAttributionEnabled ? bytes.ToArray() : null;
        Scalar8Scalar8BatchShelfCacheEntry created = new(bytes, originalBytes);
        shelfCache.Add(shelfOffset, created);
        return created;
    }

    /// <summary>
    /// Gets an existing mutable shelf cache entry while attributing the internal cache-hit and cache-miss costs.<br/>
    /// This diagnostic path mirrors <see cref="GetOrCreateShelfCacheEntry(long, Scalar8Scalar8Profile)"/> but splits dictionary probe, shelf read, snapshot clone, entry construction, and dictionary add time.<br/>
    /// </summary>
    /// <param name="shelfOffset">The shelf offset used as the cache key.</param>
    /// <param name="profile">The `SS8-8` profile that determines shelf extent size.</param>
    /// <returns>The mutable cache entry for the selected shelf.</returns>
    private Scalar8Scalar8BatchShelfCacheEntry GetOrCreateShelfCacheEntryWithAttribution(long shelfOffset, Scalar8Scalar8Profile profile)
    {
        long probeStart = Stopwatch.GetTimestamp();
        if (shelfCache.TryGetValue(shelfOffset, out Scalar8Scalar8BatchShelfCacheEntry? existing))
        {
            insertAttributionTelemetry.ShelfCacheProbeTicks += Stopwatch.GetTimestamp() - probeStart;
            insertAttributionTelemetry.ShelfCacheHitCount++;
            return existing;
        }

        insertAttributionTelemetry.ShelfCacheProbeTicks += Stopwatch.GetTimestamp() - probeStart;
        insertAttributionTelemetry.ShelfCacheMissCount++;

        long readStart = Stopwatch.GetTimestamp();
        byte[] bytes = index.Session.ReadScalar8Scalar8ShelfBytesForBatch(shelfOffset, profile);
        insertAttributionTelemetry.ShelfCacheReadTicks += Stopwatch.GetTimestamp() - readStart;

        long cloneStart = Stopwatch.GetTimestamp();
        byte[]? originalBytes = publicationAttributionEnabled ? bytes.ToArray() : null;
        insertAttributionTelemetry.ShelfCacheSnapshotCloneTicks += Stopwatch.GetTimestamp() - cloneStart;

        long createStart = Stopwatch.GetTimestamp();
        Scalar8Scalar8BatchShelfCacheEntry created = new(bytes, originalBytes);
        insertAttributionTelemetry.ShelfCacheEntryCreateTicks += Stopwatch.GetTimestamp() - createStart;

        long addStart = Stopwatch.GetTimestamp();
        shelfCache.Add(shelfOffset, created);
        insertAttributionTelemetry.ShelfCacheEntryAddTicks += Stopwatch.GetTimestamp() - addStart;
        return created;
    }

    /// <summary>
    /// Flushes every dirty batch-local shelf image into the session as one staged rewrite per shelf.<br/>
    /// The surrounding durability batch still controls the actual DataKernel publication boundary.<br/>
    /// </summary>
    private void FlushShelfCache(bool forceFullRewrite, bool clearAfterFlush = true)
    {
        if (shelfCache.Count == 0)
        {
            return;
        }

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        foreach (KeyValuePair<long, Scalar8Scalar8BatchShelfCacheEntry> pair in shelfCache)
        {
            if (pair.Value.Dirty)
            {
                if (publicationAttributionEnabled)
                {
                    long publicationStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
                    AddShelfPublicationAttribution(pair.Value.OriginalBytes, pair.Value.Bytes, profile);
                    if (commitAttributionEnabled)
                    {
                        commitAttributionTelemetry.PublicationAttributionTicks += Stopwatch.GetTimestamp() - publicationStart;
                    }
                }

                if (forceFullRewrite)
                {
                    long fullStageStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
                    index.Session.StageScalar8Scalar8ShelfRewriteForBatch(pair.Key, profile, pair.Value.Bytes);
                    if (commitAttributionEnabled)
                    {
                        commitAttributionTelemetry.FullShelfStagingTicks += Stopwatch.GetTimestamp() - fullStageStart;
                    }
                }
                else
                {
                    StageShelfDeltaRewrite(pair.Key, pair.Value, profile);
                }
            }
        }

        if (clearAfterFlush)
        {
            shelfCache.Clear();
            routeTargetCache.Clear();
        }
    }

    /// <summary>
    /// Publishes successfully committed shelf images into the session-local clean shelf cache.<br/>
    /// This extends clean shelf reuse across batch boundaries without changing abort behavior or treating the cache as durable state.<br/>
    /// </summary>
    private void StoreCleanShelfCacheEntriesAfterCommit()
    {
        if (shelfCache.Count == 0)
        {
            return;
        }

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        foreach (KeyValuePair<long, Scalar8Scalar8BatchShelfCacheEntry> pair in shelfCache)
        {
            index.Session.StoreScalar8Scalar8CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
        }
    }

    /// <summary>
    /// Stages one dirty cached shelf as a full shelf rewrite before handing that same shelf to the split-capable routed insert path.<br/>
    /// This preserves DataKernel read safety for the fallback split without forcing unrelated dirty shelves to publish as full-shelf rewrites.<br/>
    /// Remaining cached shelves stay batch-local and can still use delta publication during the final batch commit.<br/>
    /// </summary>
    /// <param name="shelfOffset">The dirty cached shelf offset that reached full capacity.</param>
    /// <returns>True when the requested dirty shelf was staged and removed from the cache; otherwise false so the caller can use the conservative full-cache flush path.</returns>
    private bool FlushSingleShelfForSplitFallback(long shelfOffset)
    {
        if (!shelfCache.TryGetValue(shelfOffset, out Scalar8Scalar8BatchShelfCacheEntry? entry) || !entry.Dirty)
        {
            return false;
        }

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        if (publicationAttributionEnabled)
        {
            long publicationStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
            AddShelfPublicationAttribution(entry.OriginalBytes, entry.Bytes, profile);
            if (commitAttributionEnabled)
            {
                commitAttributionTelemetry.PublicationAttributionTicks += Stopwatch.GetTimestamp() - publicationStart;
            }
        }

        long fullStageStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
        index.Session.StageScalar8Scalar8ShelfRewriteForBatch(shelfOffset, profile, entry.Bytes);
        if (commitAttributionEnabled)
        {
            commitAttributionTelemetry.SplitFallbackFullShelfStagingTicks += Stopwatch.GetTimestamp() - fullStageStart;
        }

        shelfCache.Remove(shelfOffset);
        return true;
    }

    /// <summary>
    /// Stages changed regions for one dirty cached shelf instead of blindly staging the full shelf extent.<br/>
    /// The current shelf image is validated as a whole, then the staged ranges are limited to a header span, a changed slot span, and a changed item-payload span when those ranges are smaller than the full shelf.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the dirty shelf.</param>
    /// <param name="entry">The dirty cached shelf entry with tracked mutation bounds.</param>
    /// <param name="profile">The `SS8-8` shelf profile that defines the shelf regions.</param>
    private void StageShelfDeltaRewrite(long shelfOffset, Scalar8Scalar8BatchShelfCacheEntry entry, Scalar8Scalar8Profile profile)
    {
        long selectionStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
        byte[] currentBytes = entry.Bytes;
        Scalar8Scalar8ReadOnly readOnly = new(currentBytes, profile);
        if (!readOnly.IsValid)
        {
            throw new InvalidDataException("The batch-local SS8-8 shelf cache attempted to stage invalid shelf bytes.");
        }

        ChangedSpan headerSpan = entry.GetHeaderSpan();
        ChangedSpan slotSpan = entry.GetSlotSpan();
        ChangedSpan itemSpan = entry.GetItemSpan();

        int deltaBytes = headerSpan.Length + slotSpan.Length + itemSpan.Length;
        if (commitAttributionEnabled)
        {
            AddShelfDeltaGroupingAttribution(headerSpan, slotSpan, itemSpan);
            commitAttributionTelemetry.DeltaSelectionTicks += Stopwatch.GetTimestamp() - selectionStart;
        }

        if (deltaBytes <= 0)
        {
            return;
        }

        if (deltaBytes >= profile.ShelfExtentSize)
        {
            long fullStageStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
            index.Session.StageScalar8Scalar8ShelfRewriteForBatch(shelfOffset, profile, currentBytes);
            if (commitAttributionEnabled)
            {
                commitAttributionTelemetry.FullShelfStagingTicks += Stopwatch.GetTimestamp() - fullStageStart;
            }

            return;
        }

        long deltaStageStart = commitAttributionEnabled ? Stopwatch.GetTimestamp() : 0;
        StageShelfChangedSpan(shelfOffset, profile, currentBytes, headerSpan);
        StageShelfChangedSpan(shelfOffset, profile, currentBytes, slotSpan);
        StageShelfChangedSpan(shelfOffset, profile, currentBytes, itemSpan);
        if (commitAttributionEnabled)
        {
            commitAttributionTelemetry.DeltaStagingTicks += Stopwatch.GetTimestamp() - deltaStageStart;
        }
    }

    private void StageShelfChangedSpan(long shelfOffset, Scalar8Scalar8Profile profile, byte[] currentBytes, ChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        index.Session.StageScalar8Scalar8ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            currentBytes.AsSpan(span.Offset, span.Length));
    }

    /// <summary>
    /// Adds diagnostic shelf-local grouping estimates for the dirty header, slot, and item spans in one shelf.<br/>
    /// The estimates are measurement-only and let the harness compare fewer positional writes against the extra unchanged bytes that grouping would publish.<br/>
    /// </summary>
    /// <param name="headerSpan">The changed header span tracked for the dirty shelf.</param>
    /// <param name="slotSpan">The changed slot-region span tracked for the dirty shelf.</param>
    /// <param name="itemSpan">The changed item-payload span tracked for the dirty shelf.</param>
    private void AddShelfDeltaGroupingAttribution(ChangedSpan headerSpan, ChangedSpan slotSpan, ChangedSpan itemSpan)
    {
        Span<ChangedSpan> spans = stackalloc ChangedSpan[3];
        int spanCount = AddPositiveSpan(spans, 0, headerSpan);
        spanCount = AddPositiveSpan(spans, spanCount, slotSpan);
        spanCount = AddPositiveSpan(spans, spanCount, itemSpan);
        if (spanCount == 0)
        {
            return;
        }

        commitAttributionTelemetry.DeltaShelfCount++;
        commitAttributionTelemetry.DeltaUngroupedSpanCount += spanCount;
        commitAttributionTelemetry.DeltaUngroupedBytes += CountGroupedSpanBytes(spans[..spanCount], maxGapBytes: -1, out _, out int positiveGapCount, out long positiveGapBytes, out int maxPositiveGapBytes);
        commitAttributionTelemetry.DeltaPositiveGapCount += positiveGapCount;
        commitAttributionTelemetry.DeltaPositiveGapBytes += positiveGapBytes;
        commitAttributionTelemetry.DeltaMaxPositiveGapBytes = Math.Max(commitAttributionTelemetry.DeltaMaxPositiveGapBytes, maxPositiveGapBytes);
        AddShelfDeltaGroupingCandidate(spans[..spanCount], 512, ref commitAttributionTelemetry.DeltaGrouped512SpanCount, ref commitAttributionTelemetry.DeltaGrouped512Bytes);
        AddShelfDeltaGroupingCandidate(spans[..spanCount], 1024, ref commitAttributionTelemetry.DeltaGrouped1024SpanCount, ref commitAttributionTelemetry.DeltaGrouped1024Bytes);
        AddShelfDeltaGroupingCandidate(spans[..spanCount], 4096, ref commitAttributionTelemetry.DeltaGrouped4096SpanCount, ref commitAttributionTelemetry.DeltaGrouped4096Bytes);
        AddShelfDeltaGroupingCandidate(spans[..spanCount], 8192, ref commitAttributionTelemetry.DeltaGrouped8192SpanCount, ref commitAttributionTelemetry.DeltaGrouped8192Bytes);
    }

    /// <summary>
    /// Appends one positive changed span into a compact stack span.<br/>
    /// Empty spans are ignored so grouping estimates only model staged byte ranges.<br/>
    /// </summary>
    /// <param name="spans">The stack span receiving positive changed spans.</param>
    /// <param name="count">The current positive changed-span count.</param>
    /// <param name="span">The candidate changed span.</param>
    /// <returns>The updated positive changed-span count.</returns>
    private static int AddPositiveSpan(Span<ChangedSpan> spans, int count, ChangedSpan span)
    {
        if (span.Length <= 0)
        {
            return count;
        }

        spans[count] = span;
        return count + 1;
    }

    /// <summary>
    /// Adds one shelf-local grouping candidate to the commit attribution counters.<br/>
    /// Candidate grouping joins consecutive dirty spans only when the unchanged gap between them is within the supplied threshold.<br/>
    /// </summary>
    /// <param name="spans">The ordered positive changed spans for one dirty shelf.</param>
    /// <param name="maxGapBytes">The maximum unchanged gap bytes that may be bridged inside the shelf.</param>
    /// <param name="groupedSpanCount">The aggregate grouped span-count counter to update.</param>
    /// <param name="groupedBytes">The aggregate grouped-byte counter to update.</param>
    private static void AddShelfDeltaGroupingCandidate(
        ReadOnlySpan<ChangedSpan> spans,
        int maxGapBytes,
        ref long groupedSpanCount,
        ref long groupedBytes)
    {
        groupedBytes += CountGroupedSpanBytes(spans, maxGapBytes, out int spanCount, out _, out _, out _);
        groupedSpanCount += spanCount;
    }

    /// <summary>
    /// Counts the byte length and grouped range count produced by joining ordered dirty spans across bounded unchanged gaps.<br/>
    /// A negative threshold disables joining and returns the ungrouped dirty-span byte count while still reporting positive gap geometry.<br/>
    /// </summary>
    /// <param name="spans">The ordered positive changed spans for one dirty shelf.</param>
    /// <param name="maxGapBytes">The maximum unchanged gap bytes that may be bridged, or a negative value to disable bridging.</param>
    /// <param name="groupedSpanCount">Receives the grouped range count.</param>
    /// <param name="positiveGapCount">Receives the number of positive unchanged gaps between staged spans.</param>
    /// <param name="positiveGapBytes">Receives the total positive unchanged gap bytes between staged spans.</param>
    /// <param name="maxPositiveGapBytes">Receives the largest positive unchanged gap between staged spans.</param>
    /// <returns>The total byte length that the grouped ranges would publish.</returns>
    private static long CountGroupedSpanBytes(
        ReadOnlySpan<ChangedSpan> spans,
        int maxGapBytes,
        out int groupedSpanCount,
        out int positiveGapCount,
        out long positiveGapBytes,
        out int maxPositiveGapBytes)
    {
        groupedSpanCount = 0;
        positiveGapCount = 0;
        positiveGapBytes = 0;
        maxPositiveGapBytes = 0;
        if (spans.Length == 0)
        {
            return 0;
        }

        groupedSpanCount = 1;
        long bytes = spans[0].Length;
        int groupEnd = checked(spans[0].Offset + spans[0].Length);
        for (int i = 1; i < spans.Length; i++)
        {
            ChangedSpan span = spans[i];
            int gap = Math.Max(0, span.Offset - groupEnd);
            if (gap > 0)
            {
                positiveGapCount++;
                positiveGapBytes += gap;
                maxPositiveGapBytes = Math.Max(maxPositiveGapBytes, gap);
            }

            if (maxGapBytes >= 0 && gap <= maxGapBytes)
            {
                bytes += gap + span.Length;
                groupEnd = Math.Max(groupEnd, checked(span.Offset + span.Length));
                continue;
            }

            groupedSpanCount++;
            bytes += span.Length;
            groupEnd = checked(span.Offset + span.Length);
        }

        return bytes;
    }

    /// <summary>
    /// Adds delta-publication estimates for one dirty cached shelf.<br/>
    /// The estimate keeps two views: raw changed byte runs and a practical three-region candidate made from changed header, slot-region, and item-region spans.<br/>
    /// </summary>
    /// <param name="originalBytes">The shelf image read before batch-local mutation.</param>
    /// <param name="currentBytes">The dirty shelf image about to be staged as a full rewrite.</param>
    /// <param name="profile">The `SS8-8` shelf profile that defines region boundaries.</param>
    private void AddShelfPublicationAttribution(byte[]? originalBytes, byte[] currentBytes, Scalar8Scalar8Profile profile)
    {
        if (originalBytes is null)
        {
            return;
        }

        if (originalBytes.Length != currentBytes.Length)
        {
            throw new InvalidDataException("The SS8-8 publication attribution shelf snapshots have different lengths.");
        }

        (long rawChangedBytes, long rawChangedRangeCount) = CountChangedBytesAndRanges(originalBytes, currentBytes);
        int headerDeltaBytes = GetChangedSpan(originalBytes, currentBytes, 0, Scalar8Scalar8Layout.HeaderSize).Length;
        int slotDeltaBytes = GetChangedSpan(originalBytes, currentBytes, profile.SlotRegionOffset, profile.SlotRegionSize).Length;
        int itemDeltaBytes = GetChangedSpan(originalBytes, currentBytes, profile.ItemRegionOffset, profile.ItemRegionSize).Length;
        long deltaCandidateBytes = headerDeltaBytes + slotDeltaBytes + itemDeltaBytes;
        long deltaCandidateRangeCount = CountPositiveSpans(headerDeltaBytes, slotDeltaBytes, itemDeltaBytes);

        publicationAttributionTelemetry.DirtyShelfCount++;
        publicationAttributionTelemetry.FullShelfBytes += profile.ShelfExtentSize;
        publicationAttributionTelemetry.RawChangedBytes += rawChangedBytes;
        publicationAttributionTelemetry.RawChangedRangeCount += rawChangedRangeCount;
        publicationAttributionTelemetry.DeltaCandidateBytes += deltaCandidateBytes;
        publicationAttributionTelemetry.DeltaCandidateRangeCount += deltaCandidateRangeCount;
        publicationAttributionTelemetry.HeaderDeltaBytes += headerDeltaBytes;
        publicationAttributionTelemetry.SlotDeltaBytes += slotDeltaBytes;
        publicationAttributionTelemetry.ItemDeltaBytes += itemDeltaBytes;
        publicationAttributionTelemetry.MaxDeltaCandidateBytesPerShelf = Math.Max(publicationAttributionTelemetry.MaxDeltaCandidateBytesPerShelf, deltaCandidateBytes);
        if (deltaCandidateBytes >= profile.ShelfExtentSize)
        {
            publicationAttributionTelemetry.FullShelfBetterOrEqualCount++;
        }
    }

    private static (long ChangedBytes, long RangeCount) CountChangedBytesAndRanges(ReadOnlySpan<byte> originalBytes, ReadOnlySpan<byte> currentBytes)
    {
        long changedBytes = 0;
        long rangeCount = 0;
        bool inRange = false;
        for (int i = 0; i < originalBytes.Length; i++)
        {
            if (originalBytes[i] == currentBytes[i])
            {
                inRange = false;
                continue;
            }

            changedBytes++;
            if (!inRange)
            {
                rangeCount++;
                inRange = true;
            }
        }

        return (changedBytes, rangeCount);
    }

    private static ChangedSpan GetChangedSpan(ReadOnlySpan<byte> originalBytes, ReadOnlySpan<byte> currentBytes, int offset, int length)
    {
        int end = offset + length;
        int first = -1;
        int last = -1;
        for (int i = offset; i < end; i++)
        {
            if (originalBytes[i] == currentBytes[i])
            {
                continue;
            }

            first = first < 0 ? i : first;
            last = i;
        }

        return first < 0 ? default : new ChangedSpan(first, last - first + 1);
    }

    private static int CountPositiveSpans(int headerDeltaBytes, int slotDeltaBytes, int itemDeltaBytes)
    {
        int count = 0;
        count += headerDeltaBytes > 0 ? 1 : 0;
        count += slotDeltaBytes > 0 ? 1 : 0;
        count += itemDeltaBytes > 0 ? 1 : 0;
        return count;
    }

    private static Scalar8Scalar8EncodedInsertOutcome MapInsertOutcome(Scalar8Scalar8InsertResult result)
    {
        return result switch
        {
            Scalar8Scalar8InsertResult.Inserted => Scalar8Scalar8EncodedInsertOutcome.Inserted,
            Scalar8Scalar8InsertResult.AlreadyPresent => Scalar8Scalar8EncodedInsertOutcome.AlreadyPresent,
            Scalar8Scalar8InsertResult.KeyConflict => Scalar8Scalar8EncodedInsertOutcome.KeyConflict,
            Scalar8Scalar8InsertResult.Full => throw new InvalidDataException("The batch-local SS8-8 cache cannot map a full shelf to a public insert outcome."),
            _ => throw new InvalidDataException($"Unknown SS8-8 insert result {result}.")
        };
    }

    private sealed class Scalar8Scalar8BatchShelfCacheEntry
    {
        internal Scalar8Scalar8BatchShelfCacheEntry(byte[] bytes, byte[]? originalBytes)
        {
            Bytes = bytes;
            OriginalBytes = originalBytes;
        }

        internal byte[] Bytes { get; }

        internal byte[]? OriginalBytes { get; }

        internal bool Dirty { get; set; }

        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        internal void Include(Scalar8Scalar8MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        internal ChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        internal ChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        internal ChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        private static ChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new ChangedSpan(start, end - start);
        }
    }

    private readonly record struct ChangedSpan(int Offset, int Length);

    private readonly record struct Scalar8Scalar8BatchRouteCacheKey(byte Depth, ulong Prefix)
    {
        internal static Scalar8Scalar8BatchRouteCacheKey Create(ulong encodedKey, ushort routerDepth)
        {
            if (routerDepth > 7)
            {
                throw new InvalidDataException($"The SS8-8 route depth {routerDepth} cannot be cached for an 8-byte encoded scalar key.");
            }

            return CreateForDepth(encodedKey, (byte)routerDepth);
        }

        internal static Scalar8Scalar8BatchRouteCacheKey CreateForDepth(ulong encodedKey, byte depth)
        {
            return new Scalar8Scalar8BatchRouteCacheKey(depth, ExtractPrefix(encodedKey, depth));
        }

        internal bool Matches(ulong encodedKey)
        {
            return ExtractPrefix(encodedKey, Depth) == Prefix;
        }

        private static ulong ExtractPrefix(ulong encodedKey, byte depth)
        {
            int prefixBits = (depth + 1) * 8;
            int shift = 64 - prefixBits;
            return encodedKey >> shift;
        }
    }
}
