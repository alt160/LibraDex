using System.Globalization;
using System.Threading;

namespace LibraDex;

#if LIBRADEX_PREFIX_COUNT_TELEMETRY
/// <summary>
/// Captures shape-native `VS8` prefix-count planner diagnostics for harness and audit work.<br/>
/// The counters describe which parts of the physical route walk were used without promoting diagnostics into the public LibraDex API.<br/>
/// </summary>
/// <param name="RoutersVisited">The number of routers visited by the prefix planner.<br/></param>
/// <param name="OneByteRoutersVisited">The number of one-byte routers visited by the prefix planner.<br/></param>
/// <param name="MultiByteRoutersVisited">The number of compressed multi-byte routers visited by the prefix planner.<br/></param>
/// <param name="MultiByteRoutesVisited">The number of compressed multi-byte routes examined by the prefix planner.<br/></param>
/// <param name="MultiByteRoutesStemMatched">The number of compressed multi-byte routes whose stem matched the requested prefix.<br/></param>
/// <param name="MultiByteRoutesContained">The number of compressed multi-byte routes whose target was proven fully contained by the prefix.<br/></param>
/// <param name="MultiByteRoutesAmbiguous">The number of compressed multi-byte routes that forced exact fallback because the final-byte range was wider than the prefix byte.<br/></param>
/// <param name="TargetsVisited">The number of route targets visited by the prefix planner.<br/></param>
/// <param name="TargetsCountedByMetadata">The number of route targets counted by contained count-all metadata traversal.<br/></param>
/// <param name="ShelfTargetsCountedNarrow">The number of shelf targets counted through narrow key-range boundary counting.<br/></param>
/// <param name="TerminalRootsCountedNarrow">The number of terminal identity roots counted through narrow key-range boundary counting.<br/></param>
/// <param name="ContainedRouterTargetScans">The number of contained routers whose direct route targets were scanned for count-all metadata traversal.<br/></param>
/// <param name="FallbackToRangeCount">One when the prefix planner failed closed to the generic encoded range counter; otherwise zero.<br/></param>
internal readonly record struct VarKeyScalar8PrefixCountTelemetry(
    long RoutersVisited,
    long OneByteRoutersVisited,
    long MultiByteRoutersVisited,
    long MultiByteRoutesVisited,
    long MultiByteRoutesStemMatched,
    long MultiByteRoutesContained,
    long MultiByteRoutesAmbiguous,
    long TargetsVisited,
    long TargetsCountedByMetadata,
    long ShelfTargetsCountedNarrow,
    long TerminalRootsCountedNarrow,
    long ContainedRouterTargetScans,
    long FallbackToRangeCount);
#endif

/// <summary>
/// Provides a public raw-byte wrapper over one routed `VS8` index: varlen key bytes and one encoded 8-byte scalar identity.<br/>
/// This surface intentionally stays codec-free so text, path, blob, and unmanaged projections can be layered later without changing the persisted shelf shape.<br/>
/// </summary>
internal sealed partial class VarKeyScalar8Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly VarKeyScalar8IndexHandle handle;
    private readonly bool ownsSession;
    private readonly object singleOperationFallbackSync = new();
    private readonly ReaderWriterLockSlim singleOperationTopologySync;
    private bool disposed;

    internal VarKeyScalar8Index(
        LibraDexFileSession session,
        VarKeyScalar8IndexHandle handle,
        int slotIndex,
        string name,
        bool ownsSession)
    {
        this.session = session;
        this.handle = handle;
        this.ownsSession = ownsSession;
        singleOperationTopologySync = session.GetVarKeyScalar8TopologyMutationSync(handle.RootRouterOffset);
        SlotIndex = slotIndex;
        Name = name;
    }

    /// <summary>
    /// Gets the fixed index-directory slot used to resolve this runtime index.<br/>
    /// Slot identity remains the first public selector until named-index lookup and richer metadata validation are promoted.<br/>
    /// </summary>
    public int SlotIndex { get; }

    /// <summary>
    /// Gets the index name stored in the fixed index-directory slot.<br/>
    /// The name is metadata in this slice; the slot number and root router offset are the operational identity.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the root router offset used by this routed `VS8` index.<br/>
    /// Exposing it keeps early validation transparent while the higher-level API remains intentionally small.<br/>
    /// </summary>
    public long RootRouterOffset => handle.RootRouterOffset;

    /// <summary>
    /// Gets the maximum developer-facing key payload length accepted by this wrapper.<br/>
    /// The routed `VS8` storage shape reserves one internal sentinel byte so null, empty, and non-empty variable-length keys remain distinct and sortable.<br/>
    /// </summary>
    public int MaxKeyLength => LibraDexVarLenKeyCodec.GetMaxLogicalLength(handle.MaxKeyLength);

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the initial `VS8` shelf profile for this index backing kind.<br/>
    /// File-backed indexes keep the historical var-key start size, while memory-backed indexes use a smaller process-level profile for lower retained slack on new routes.<br/>
    /// </summary>
    /// <returns>The initial `VS8` shelf profile used when a root-prefix route is first created.</returns>
    internal VarKeyScalar8Profile GetInitialProfile()
    {
        if (BackingKind != DataKernelBackingKind.Memory)
        {
            return VarKeyScalar8Profile.DefaultInitial with { Descending = handle.Descending };
        }

        return (ParseMemoryShelfKiB("LIBRADEX_MEMORY_VS8_SHELF_KB", 8) switch
        {
            4 => VarKeyScalar8Profile.Default4KiB,
            8 => VarKeyScalar8Profile.Default8KiB,
            16 => VarKeyScalar8Profile.Default16KiB,
            32 => VarKeyScalar8Profile.Default32KiB,
            64 => VarKeyScalar8Profile.Default64KiB,
            128 => VarKeyScalar8Profile.Default128KiB,
            _ => VarKeyScalar8Profile.Default8KiB
        }) with { Descending = handle.Descending };
    }

    internal LibraDexFileSession Session => session;

    internal VarKeyScalar8IndexHandle Handle => handle;

#if LIBRADEX_PREFIX_COUNT_TELEMETRY
    internal VarKeyScalar8PrefixCountTelemetry LastPrefixCountTelemetry { get; private set; }
#endif

    /// <summary>
    /// Starts a routed `VS8` durability batch for bulk mutation.<br/>
    /// Batch inserts use the same routed mutation path as one-shot inserts, but lower-level commit requests are deferred until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A batch object that accepts raw-byte keys and controls the durability publication boundary.</returns>
    public VarKeyScalar8Batch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return new VarKeyScalar8Batch(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one developer-facing key payload and encoded 8-byte identity into this routed `VS8` index.<br/>
    /// Null keys are available through the nullable byte-array overload; an empty span represents the distinct empty-key sentinel.<br/>
    /// The method uses the direct no-batch writer path for ordinary single writes, while explicit caller-owned batches remain available through <see cref="BeginBatch(LibraDexWriteIntent)"/>.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes.</param>
    /// <param name="encodedIdentity">The already encoded sortable 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus commit telemetry when storage changed.</returns>
    public VarKeyScalar8InsertOutcome Insert(
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        return InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
    }

    /// <summary>
    /// Inserts one nullable developer-facing key payload and encoded 8-byte identity into this routed `VS8` index.<br/>
    /// A null key sorts before an empty key, and both sort before non-empty payload keys by means of LibraDex's reserved first-byte sentinel.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes, or null for the null-key sentinel.</param>
    /// <param name="encodedIdentity">The already encoded sortable 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public VarKeyScalar8InsertOutcome Insert(
        byte[]? key,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        return InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
    }

    internal VarKeyScalar8InsertOutcome InsertEncoded(
        ReadOnlySpan<byte> encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedKey, handle.MaxKeyLength);
        if (!session.IsDurabilityBatchActive)
        {
            if (session.FindRouterTarget(handle.RootRouterOffset, encodedKey[0]) == 0 &&
                session.TryInsertVarKeyScalar8DirectColdRootRoute(
                    handle.RootRouterOffset,
                    encodedKey[0],
                    GetInitialProfile(),
                    encodedKey,
                    encodedIdentity,
                    allowDuplicateKeys,
                    out VarKeyScalar8RoutedInsertResult coldRouteResult))
            {
                return VarKeyScalar8InsertOutcome.FromStorage(coldRouteResult, createdInitialShelfRoute: true);
            }

            while (true)
            {
                singleOperationTopologySync.EnterReadLock();
                try
                {
                    LibraDexWriteContext writeContext = session.BeginVarKeyScalar8WriteContext();
                    try
                    {
                        VarKeyScalar8RoutedInsertResult writerResult = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
                            writeContext,
                            handle.RootRouterOffset,
                            handle.MaxKeyLength,
                            encodedKey,
                            encodedIdentity,
                            allowDuplicateKeys,
                            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
                        VarKeyScalar8InsertOutcome outcome = VarKeyScalar8InsertOutcome.FromStorage(writerResult, createdInitialShelfRoute: false);
                        if (!outcome.Inserted)
                        {
                            session.AbortVarKeyScalar8WriteContext(writeContext);
                            return outcome;
                        }

                        DataKernelCommitTelemetry writerCommit = session.PublishVarKeyScalar8WriteContext(writeContext);
                        return outcome with { Commit = writerCommit };
                    }
                    catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException ex)
                    {
                        session.AbortVarKeyScalar8WriteContext(writeContext);
                        session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                    }
                    catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
                    {
                        session.AbortVarKeyScalar8WriteContext(writeContext);
                        session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                    }
                    catch (LibraDexWriteContextVarKeyScalar8TopologyFallbackException)
                    {
                        session.AbortVarKeyScalar8WriteContext(writeContext);
                        break;
                    }
                }
                finally
                {
                    singleOperationTopologySync.ExitReadLock();
                }
            }
        }

        if (!session.IsDurabilityBatchActive)
        {
            VarKeyScalar8InsertOutcome fallbackOutcome;
            singleOperationTopologySync.EnterWriteLock();
            try
            {
                fallbackOutcome = InsertEncodedInCurrentScope(encodedKey, encodedIdentity, allowDuplicateKeys);
            }
            finally
            {
                singleOperationTopologySync.ExitWriteLock();
            }

            return fallbackOutcome;
        }

        lock (singleOperationFallbackSync)
        {
            using VarKeyScalar8Batch batch = BeginBatch();
            VarKeyScalar8InsertOutcome result = batch.InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
            VarKeyScalarBatchCommitResult commit = batch.Commit();
            return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
        }
    }

    /// <summary>
    /// Inserts one encoded key and encoded 8-byte identity using the caller's currently active durability scope.<br/>
    /// Maintained facades use this to insert exact and projection tuples inside one shared batch without nesting batch lifetimes.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact encoded key bytes.</param>
    /// <param name="encodedIdentity">The exact encoded identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    internal VarKeyScalar8InsertOutcome InsertEncodedInCurrentScope(
        ReadOnlySpan<byte> encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedKey, handle.MaxKeyLength);
        bool createdInitialShelfRoute = false;
        if (session.FindRouterTarget(handle.RootRouterOffset, encodedKey[0]) == 0)
        {
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, encodedKey[0], GetInitialProfile());
            createdInitialShelfRoute = true;
        }

        VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops,
            requestedRouteCount: handle.OptimizerRouteFanout);
        return VarKeyScalar8InsertOutcome.FromStorage(result, createdInitialShelfRoute);
    }

    /// <summary>
    /// Stages one already encoded `VS8` tuple in a caller-owned concurrent batch writer context.<br/>
    /// The method accepts only warmed shelf-local inserts; topology-changing cases throw so the caller can publish current staged work and use the existing topology-safe path for that logical operation.<br/>
    /// </summary>
    /// <param name="writeContext">The shared `VS8` writer context.<br/></param>
    /// <param name="encodedKey">The exact encoded key bytes, including the var-key sentinel.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.<br/></param>
    /// <returns>The staged insert outcome with deferred commit telemetry.</returns>
    internal VarKeyScalar8InsertOutcome InsertEncodedForConcurrentBatch(
        LibraDexWriteContext writeContext,
        ReadOnlySpan<byte> encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedKey, handle.MaxKeyLength);
        session.EnterVarKeyScalar8TopologyReadForWriteContext(writeContext, handle.RootRouterOffset);
        VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
            writeContext,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        return VarKeyScalar8InsertOutcome.FromStorage(result, createdInitialShelfRoute: false);
    }

    /// <summary>
    /// Deletes all tuples whose raw-byte key is inside an inclusive `VS8` range.<br/>
    /// The method opens a short durability batch so shelf-local deletes use the same tombstone sidecar and commit-time normalization path as bulk delete workloads.<br/>
    /// This is internal to keep public data selection anchored on the condition builder while still letting maintained facades remove physical tuples efficiently.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return DeleteEncodedRange(encodedLowerKey, encodedUpperKey);
    }

    /// <summary>
    /// Deletes all tuples whose already encoded raw-byte key is inside an inclusive `VS8` range.<br/>
    /// No-batch callers use writer-context staging for warmed ordinary shelves, while explicit batches and unsupported route shapes fall back to the existing durability-batch delete path.<br/>
    /// </summary>
    /// <param name="encodedLowerKey">The inclusive lower encoded key.</param>
    /// <param name="encodedUpperKey">The inclusive upper encoded key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteEncodedRange(ReadOnlySpan<byte> encodedLowerKey, ReadOnlySpan<byte> encodedUpperKey)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedLowerKey, handle.MaxKeyLength);
        ValidateKeyLength(encodedUpperKey, handle.MaxKeyLength);
        if (encodedLowerKey.SequenceCompareTo(encodedUpperKey) > 0)
        {
            return 0;
        }

        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginVarKeyScalar8WriteContext();
                try
                {
                    long writerDeleted = session.DeleteVarKeyScalar8KeyRangeForWriteContext(
                        writeContext,
                        handle.RootRouterOffset,
                        handle.MaxKeyLength,
                        encodedLowerKey,
                        encodedUpperKey);
                    if (writerDeleted == 0)
                    {
                        session.AbortVarKeyScalar8WriteContext(writeContext);
                        return 0;
                    }

                    _ = session.PublishVarKeyScalar8WriteContext(writeContext);
                    return writerDeleted;
                }
                catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException ex)
                {
                    session.AbortVarKeyScalar8WriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
                {
                    session.AbortVarKeyScalar8WriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                {
                    session.AbortVarKeyScalar8WriteContext(writeContext);
                    break;
                }
            }
        }

        using VarKeyScalar8Batch batch = BeginBatch();
        long deleted = batch.DeleteRange(encodedLowerKey, encodedUpperKey);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes one exact raw-key and encoded-identity tuple from this routed `VS8` index.<br/>
    /// The method opens a short durability batch so the physical delete uses the same tombstone sidecar path as condition-driven bulk deletes.<br/>
    /// This is internal because public selection and mutation should flow through the condition builder or higher-level maintained facades.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="encodedIdentity">The exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        return DeleteEncodedExactTuple(encodedKey, encodedIdentity);
    }

    /// <summary>
    /// Deletes one already encoded raw-key and encoded-identity tuple from this routed `VS8` index.<br/>
    /// Writer-context staging is used for warmed ordinary shelves when no explicit durability batch is active; unsupported topology shapes fall back to the existing short batch path.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact encoded key bytes already carrying the varlen sentinel marker.</param>
    /// <param name="encodedIdentity">The exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteEncodedExactTuple(ReadOnlySpan<byte> encodedKey, ulong encodedIdentity)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedKey, handle.MaxKeyLength);
        if (!session.IsDurabilityBatchActive &&
            session.FindRouterTarget(handle.RootRouterOffset, encodedKey[0]) != 0)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginVarKeyScalar8WriteContext();
                try
                {
                    bool writerDeleted = session.DeleteVarKeyScalar8ExactTupleForWriteContext(
                        writeContext,
                        handle.RootRouterOffset,
                        handle.MaxKeyLength,
                        encodedKey,
                        encodedIdentity);
                    if (!writerDeleted)
                    {
                        session.AbortVarKeyScalar8WriteContext(writeContext);
                        return false;
                    }

                    _ = session.PublishVarKeyScalar8WriteContext(writeContext);
                    return true;
                }
                catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException ex)
                {
                    session.AbortVarKeyScalar8WriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
                {
                    session.AbortVarKeyScalar8WriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                {
                    session.AbortVarKeyScalar8WriteContext(writeContext);
                    break;
                }
            }
        }

        if (!session.IsDurabilityBatchActive)
        {
            singleOperationTopologySync.EnterWriteLock();
            try
            {
                return DeleteExactTupleInCurrentScope(encodedKey, encodedIdentity);
            }
            finally
            {
                singleOperationTopologySync.ExitWriteLock();
            }
        }

        lock (singleOperationFallbackSync)
        {
            using VarKeyScalar8Batch batch = BeginBatch();
            bool deleted = batch.DeleteExactTuple(encodedKey, encodedIdentity);
            _ = batch.Commit();
            return deleted;
        }
    }

    /// <summary>
    /// Deletes one exact raw-key and encoded-identity tuple using the caller's currently active durability scope.<br/>
    /// Maintained facades use this to delete exact and projection tuples inside one shared batch without nesting batch lifetimes.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="encodedIdentity">The exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTupleInCurrentScope(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        ThrowIfDisposed();
        ValidateKeyLength(key, handle.MaxKeyLength);
        return session.DeleteVarKeyScalar8ExactTuple(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            key,
            encodedIdentity);
    }

    /// <summary>
    /// Stages one already encoded `VS8` exact tuple delete in a caller-owned concurrent batch writer context.<br/>
    /// The method accepts only warmed shelf-local deletes; unsupported duplicate-run, terminal, or topology shapes throw so the caller can fall back after publishing current staged work.<br/>
    /// </summary>
    /// <param name="writeContext">The shared `VS8` writer context.<br/></param>
    /// <param name="encodedKey">The exact encoded key bytes, including the var-key sentinel.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity.<br/></param>
    /// <returns><see langword="true"/> when one tuple was staged for deletion.</returns>
    internal bool DeleteEncodedExactTupleForConcurrentBatch(
        LibraDexWriteContext writeContext,
        ReadOnlySpan<byte> encodedKey,
        ulong encodedIdentity)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedKey, handle.MaxKeyLength);
        session.EnterVarKeyScalar8TopologyReadForWriteContext(writeContext, handle.RootRouterOffset);
        return session.DeleteVarKeyScalar8ExactTupleForWriteContext(
            writeContext,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedKey,
            encodedIdentity);
    }

    /// <summary>
    /// Creates a caller-owned `VS8` concurrent batch writer context.<br/>
    /// One context can stage an exact string index and its maintained string projections because ownership is claimed by physical shelf offset.<br/>
    /// </summary>
    /// <returns>A new writer context for `VS8` shelf-local mutation.</returns>
    internal LibraDexWriteContext BeginConcurrentBatchContext()
    {
        ThrowIfDisposed();
        return session.BeginVarKeyScalar8WriteContext();
    }

    /// <summary>
    /// Publishes a caller-owned `VS8` concurrent batch writer context.<br/>
    /// Publication is serialized at the DataKernel boundary while shelf-local mutation and projection maintenance were staged outside that boundary.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to publish.<br/></param>
    /// <returns>The publication telemetry.</returns>
    internal DataKernelCommitTelemetry PublishConcurrentBatchContext(LibraDexWriteContext writeContext)
    {
        ThrowIfDisposed();
        return session.PublishVarKeyScalar8WriteContext(writeContext);
    }

    /// <summary>
    /// Aborts a caller-owned `VS8` concurrent batch writer context.<br/>
    /// Staged shelf bytes are discarded and physical shelf ownership claims are released without writing to DataKernel.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to abort.<br/></param>
    internal void AbortConcurrentBatchContext(LibraDexWriteContext writeContext)
    {
        ThrowIfDisposed();
        session.AbortVarKeyScalar8WriteContext(writeContext);
    }

    /// <summary>
    /// Reads encoded 8-byte identities for an inclusive developer-facing key range into caller-owned storage.<br/>
    /// The span-based shape keeps the fixed-identity public API allocation-light while higher-level enumerable or projection APIs remain future layers.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload.</param>
    /// <param name="upperKey">The inclusive upper key payload.</param>
    /// <param name="encodedIdentities">The caller-owned destination span for matching encoded identities.</param>
    /// <returns>The number of identities copied.</returns>
    public int ReadRange(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        Span<ulong> encodedIdentities)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.ReadVarKeyScalar8IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            encodedIdentities);
    }

    /// <summary>
    /// Reads encoded 8-byte identities for an inclusive nullable developer-facing key range into caller-owned storage.<br/>
    /// Use this overload when either bound must target the null-key sentinel.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload, or null for the null-key sentinel.</param>
    /// <param name="upperKey">The inclusive upper key payload, or null for the null-key sentinel.</param>
    /// <param name="encodedIdentities">The caller-owned destination span for matching encoded identities.</param>
    /// <returns>The number of identities copied.</returns>
    public int ReadRange(
        byte[]? lowerKey,
        byte[]? upperKey,
        Span<ulong> encodedIdentities)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.ReadVarKeyScalar8IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            encodedIdentities);
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive developer-facing key range.<br/>
    /// The returned reader exposes each logical key payload as a temporary <see cref="ReadOnlySpan{T}"/> and exposes null keys through its current-key null flag.<br/>
    /// This complements <see cref="ReadRange(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{ulong})"/> for callers that need key iteration, tuple iteration, skip behavior, or explicit materialization control.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload.</param>
    /// <param name="upperKey">The inclusive upper key payload.</param>
    /// <returns>A forward-only reader over matching logical key and encoded identity rows.</returns>
    public VarKeyScalar8RangeReader OpenRangeReader(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.OpenVarKeyScalar8RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            decodeLogicalKeys: true,
            direction: direction);
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive nullable developer-facing key range.<br/>
    /// Use this overload when either range bound must target the null-key sentinel.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload, or null for the null-key sentinel.</param>
    /// <param name="upperKey">The inclusive upper key payload, or null for the null-key sentinel.</param>
    /// <returns>A forward-only reader over matching logical key and encoded identity rows.</returns>
    public VarKeyScalar8RangeReader OpenRangeReader(byte[]? lowerKey, byte[]? upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.OpenVarKeyScalar8RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            decodeLogicalKeys: true,
            direction: direction);
    }

    internal VarKeyScalar8RangeReader OpenEncodedRangeReader(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        QueryDirection direction = QueryDirection.Ascending,
        bool allowWriteUpgrade = false)
    {
        ThrowIfDisposed();
        return session.OpenVarKeyScalar8RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            lowerKey,
            upperKey,
            decodeLogicalKeys: false,
            direction: direction,
            allowWriteUpgrade: allowWriteUpgrade);
    }

    /// <summary>
    /// Counts routed `VS8` identities whose logical raw keys are inside an inclusive range.<br/>
    /// The method encodes the raw key bounds once at the index boundary, then delegates to the encoded-key range-count primitive used by aggregate execution.<br/>
    /// This keeps benchmark and future public-facing count call sites from accidentally mixing logical keys with physical sort-key bytes.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key.<br/></param>
    /// <returns>The number of matching routed identities.<br/></returns>
    public long CountIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return CountEncodedIdentityRange(
            LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey)),
            LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey)));
    }

    /// <summary>
    /// Counts routed `VS8` identities in an inclusive encoded-key range through the shape-native range-count primitive.<br/>
    /// Broad ranges can count fully covered root-prefix targets from shelf metadata, while boundary prefixes retain the established reader count for exact slot-bound correctness.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <returns>The number of matching encoded-key identities.<br/></returns>
    internal long CountEncodedIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.CountVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, lowerKey, upperKey);
    }

    /// <summary>
    /// Counts routed `VS8` identities whose encoded key starts with the supplied encoded prefix.<br/>
    /// The session keeps the operation shape-native so contained route targets can use shelf metadata while boundary or ambiguous targets fall back to exact range counting.<br/>
    /// This is intended for condition-builder prefix criteria where preserving prefix semantics is cheaper and more precise than lowering immediately to a generic inclusive range.<br/>
    /// </summary>
    /// <param name="encodedPrefix">The encoded key prefix to match.<br/></param>
    /// <returns>The number of matching encoded-key identities.<br/></returns>
    internal long CountEncodedIdentityPrefix(ReadOnlySpan<byte> encodedPrefix)
    {
        ThrowIfDisposed();
#if LIBRADEX_PREFIX_COUNT_TELEMETRY
        LastPrefixCountTelemetry = default;
#else
#endif
        using VarKeyScalar8RangeReader reader = OpenEncodedRangeReader(
            encodedPrefix,
            CreateEncodedPrefixUpperBound(encodedPrefix, handle.MaxKeyLength));
        return reader.Count;
    }

    /// <summary>
    /// Counts every ordinary routed `VS8` identity from shelf-local count metadata.<br/>
    /// This bypasses ordered range-reader progression for count-all while preserving the existing routed shelf ownership model.<br/>
    /// </summary>
    /// <returns>The ordinary non-key-state identity count.<br/></returns>
    internal long CountOrdinaryIdentities()
    {
        ThrowIfDisposed();
        return session.CountVarKeyScalar8Identities(handle.RootRouterOffset, handle.MaxKeyLength);
    }

    /// <summary>
    /// Creates the inclusive encoded upper bound for an encoded-prefix query.<br/>
    /// The lower bound is the prefix itself; padding the remaining physical key space with `0xFF` preserves exact-prefix and longer-key matches under LibraDex variable-key byte ordering.<br/>
    /// </summary>
    /// <param name="encodedPrefix">The encoded key prefix being matched.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <returns>The inclusive upper encoded key for the prefix extent.<br/></returns>
    private static byte[] CreateEncodedPrefixUpperBound(ReadOnlySpan<byte> encodedPrefix, int maxKeyLength)
    {
        int length = Math.Max(encodedPrefix.Length, maxKeyLength);
        byte[] upper = new byte[length];
        encodedPrefix.CopyTo(upper);
        upper.AsSpan(encodedPrefix.Length).Fill(0xFF);
        return upper;
    }

    /// <summary>
    /// Reads matching raw keys into a disconnected buffer object owned by the caller.<br/>
    /// The returned buffer stores keys in pooled flat payload storage plus offset/length metadata, avoiding `byte[][]` materialization while allowing array-like access after this method returns.<br/>
    /// Callers must dispose the buffer when done; spans and memory retrieved from it remain valid only while the buffer is alive.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="initialPayloadCapacity">Optional initial payload capacity in bytes for callers that already know the expected result size.</param>
    /// <returns>A disconnected key buffer owned by the caller.</returns>
    public LibraDexVarKeyBuffer ReadKeyBuffer(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int initialPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using VarKeyScalar8RangeReader reader = OpenRangeReader(lowerKey, upperKey);
        LibraDexVarKeyBuffer buffer = new(reader.Count, initialPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentKey);
        }

        return buffer;
    }

    /// <summary>
    /// Returns and resets accumulated DataKernel read telemetry for this index's owning session.<br/>
    /// This keeps validation and tuning visible without adding logging or hidden counters to hot index operations.<br/>
    /// </summary>
    /// <returns>The accumulated read telemetry since the previous reset.</returns>
    public DataKernelReadTelemetry GetAndResetReadTelemetry()
    {
        ThrowIfDisposed();
        return session.GetAndResetReadTelemetry();
    }

    /// <summary>
    /// Disposes the owning session when this index was created by a facade factory.<br/>
    /// Disposing a file-backed index releases the file handle; disposing a memory-backed index releases committed pooled memory segments.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (ownsSession)
        {
            session.Dispose();
        }

        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VarKeyScalar8Index));
        }
    }

    private static void ValidateKeyLength(ReadOnlySpan<byte> key, int maxKeyLength)
    {
        if (key.Length <= 0 || key.Length > maxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VS8 key length must be from 1 to {maxKeyLength} bytes.");
        }
    }

    /// <summary>
    /// Parses one memory var-key shelf-size environment override in KiB.<br/>
    /// The override is intentionally evaluated only when a new route is created, keeping ordinary route walks and shelf mutations free of environment lookup overhead.<br/>
    /// </summary>
    /// <param name="environmentVariableName">The environment variable that carries the KiB value.</param>
    /// <param name="defaultKiB">The KiB value to use when the environment variable is absent or invalid.</param>
    /// <returns>The parsed KiB value, or <paramref name="defaultKiB"/>.</returns>
    private static int ParseMemoryShelfKiB(string environmentVariableName, int defaultKiB)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariableName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kib)
            ? kib
            : defaultKiB;
    }
}

/// <summary>
/// Provides a public raw-byte wrapper over one routed `VS16` index: varlen key bytes and one encoded 16-byte scalar identity.<br/>
/// This is the 16-byte identity counterpart to <see cref="VarKeyScalar8Index"/> and keeps the same low-friction raw key API.<br/>
/// </summary>
internal sealed class VarKeyScalar16Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly VarKeyScalar16IndexHandle handle;
    private readonly bool ownsSession;
    private readonly object singleOperationFallbackSync = new();
    private readonly ReaderWriterLockSlim singleOperationTopologySync = new(LockRecursionPolicy.SupportsRecursion);
    private bool disposed;

    internal VarKeyScalar16Index(
        LibraDexFileSession session,
        VarKeyScalar16IndexHandle handle,
        int slotIndex,
        string name,
        bool ownsSession)
    {
        this.session = session;
        this.handle = handle;
        this.ownsSession = ownsSession;
        SlotIndex = slotIndex;
        Name = name;
    }

    /// <summary>
    /// Gets the fixed index-directory slot used to resolve this runtime index.<br/>
    /// Slot identity remains the first public selector until named-index lookup and richer metadata validation are promoted.<br/>
    /// </summary>
    public int SlotIndex { get; }

    /// <summary>
    /// Gets the index name stored in the fixed index-directory slot.<br/>
    /// The name is metadata in this slice; the slot number and root router offset are the operational identity.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the root router offset used by this routed `VS16` index.<br/>
    /// Exposing it keeps early validation transparent while the higher-level API remains intentionally small.<br/>
    /// </summary>
    public long RootRouterOffset => handle.RootRouterOffset;

    /// <summary>
    /// Gets the maximum developer-facing key payload length accepted by this wrapper.<br/>
    /// The routed `VS16` storage shape reserves one internal sentinel byte so null, empty, and non-empty variable-length keys remain distinct and sortable.<br/>
    /// </summary>
    public int MaxKeyLength => LibraDexVarLenKeyCodec.GetMaxLogicalLength(handle.MaxKeyLength);

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the initial `VS16` shelf profile for this index backing kind.<br/>
    /// File-backed indexes keep the historical var-key start size, while memory-backed indexes use a smaller process-level profile for lower retained slack on new routes.<br/>
    /// </summary>
    /// <returns>The initial `VS16` shelf profile used when a root-prefix route is first created.</returns>
    internal VarKeyScalar16Profile GetInitialProfile()
    {
        if (BackingKind != DataKernelBackingKind.Memory)
        {
            return VarKeyScalar16Profile.DefaultInitial with { Descending = handle.Descending };
        }

        VarKeyScalar16Profile profile = ParseMemoryShelfKiB("LIBRADEX_MEMORY_VS16_SHELF_KB", 8) switch
        {
            4 => VarKeyScalar16Profile.Default4KiB,
            8 => VarKeyScalar16Profile.Default8KiB,
            16 => VarKeyScalar16Profile.Default16KiB,
            32 => VarKeyScalar16Profile.Default32KiB,
            64 => VarKeyScalar16Profile.Default64KiB,
            128 => VarKeyScalar16Profile.Default128KiB,
            _ => VarKeyScalar16Profile.Default8KiB
        };
        return profile with { Descending = handle.Descending };
    }

    internal LibraDexFileSession Session => session;

    internal VarKeyScalar16IndexHandle Handle => handle;

    /// <summary>
    /// Starts a routed `VS16` durability batch for bulk mutation.<br/>
    /// Batch inserts use the same routed mutation path as one-shot inserts, but lower-level commit requests are deferred until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A batch object that accepts raw-byte keys and controls the durability publication boundary.</returns>
    public VarKeyScalar16Batch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return new VarKeyScalar16Batch(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one developer-facing key payload and encoded 16-byte identity into this routed `VS16` index.<br/>
    /// The high and low identity halves are compared in byte-ordinal scalar order by the underlying fixed-identity shelf.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the encoded sortable 16-byte identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the encoded sortable 16-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public VarKeyScalar16InsertOutcome Insert(
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        return InsertEncoded(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
    }

    /// <summary>
    /// Inserts one nullable developer-facing key payload and encoded 16-byte identity into this routed `VS16` index.<br/>
    /// A null key sorts before an empty key, and both sort before non-empty payload keys by means of LibraDex's reserved first-byte sentinel.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes, or null for the null-key sentinel.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the encoded sortable 16-byte identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the encoded sortable 16-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public VarKeyScalar16InsertOutcome Insert(
        byte[]? key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        return InsertEncoded(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
    }

    internal VarKeyScalar16InsertOutcome InsertEncoded(
        ReadOnlySpan<byte> encodedKey,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        ValidateKeyLength(encodedKey, handle.MaxKeyLength);
        if (!session.IsDurabilityBatchActive &&
            session.FindRouterTarget(handle.RootRouterOffset, encodedKey[0]) != 0)
        {
            while (true)
            {
                singleOperationTopologySync.EnterReadLock();
                try
                {
                    LibraDexWriteContext writeContext = session.BeginVarKeyScalar16WriteContext();
                    try
                    {
                        VarKeyScalar16RoutedInsertResult writerResult = session.InsertWalkedRoutedVarKeyScalar16NoSplitForWriteContext(
                            writeContext,
                            handle.RootRouterOffset,
                            handle.MaxKeyLength,
                            encodedKey,
                            encodedIdentityHigh,
                            encodedIdentityLow,
                            allowDuplicateKeys,
                            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
                        VarKeyScalar16InsertOutcome outcome = VarKeyScalar16InsertOutcome.FromStorage(writerResult, createdInitialShelfRoute: false);
                        if (!outcome.Inserted)
                        {
                            session.AbortVarKeyScalar16WriteContext(writeContext);
                            return outcome;
                        }

                        DataKernelCommitTelemetry writerCommit = session.PublishVarKeyScalar16WriteContext(writeContext);
                        return outcome with { Commit = writerCommit };
                    }
                    catch (LibraDexWriteContextVarKeyScalar16ShelfOwnershipException ex)
                    {
                        session.AbortVarKeyScalar16WriteContext(writeContext);
                        session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        session.AbortVarKeyScalar16WriteContext(writeContext);
                        break;
                    }
                }
                finally
                {
                    singleOperationTopologySync.ExitReadLock();
                }
            }
        }

        singleOperationTopologySync.EnterWriteLock();
        lock (singleOperationFallbackSync)
        {
            try
            {
                using VarKeyScalar16Batch batch = BeginBatch();
                VarKeyScalar16InsertOutcome result = batch.InsertEncoded(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
                VarKeyScalarBatchCommitResult commit = batch.Commit();
                return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
            }
            finally
            {
                singleOperationTopologySync.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Deletes all tuples whose raw-byte key is inside an inclusive `VS16` range.<br/>
    /// The method opens a short durability batch so shelf-local deletes use the same tombstone sidecar and commit-time normalization path as bulk delete workloads.<br/>
    /// This is internal to keep public data selection anchored on the condition builder while still letting maintained facades remove physical tuples efficiently.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginVarKeyScalar16WriteContext();
                try
                {
                    long writerDeleted = session.DeleteVarKeyScalar16KeyRangeForWriteContext(
                        writeContext,
                        handle.RootRouterOffset,
                        handle.MaxKeyLength,
                        encodedLowerKey,
                        encodedUpperKey,
                        maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
                    if (writerDeleted == 0)
                    {
                        session.AbortVarKeyScalar16WriteContext(writeContext);
                        return 0;
                    }

                    _ = session.PublishVarKeyScalar16WriteContext(writeContext);
                    return writerDeleted;
                }
                catch (LibraDexWriteContextVarKeyScalar16ShelfOwnershipException ex)
                {
                    session.AbortVarKeyScalar16WriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortVarKeyScalar16WriteContext(writeContext);
                    break;
                }
            }
        }

        using VarKeyScalar16Batch batch = BeginBatch();
        long deleted = batch.DeleteRange(encodedLowerKey, encodedUpperKey);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes one exact raw-key and encoded 16-byte identity tuple from this routed `VS16` index.<br/>
    /// The method opens a short durability batch so the physical delete uses the same tombstone sidecar path as condition-driven bulk deletes.<br/>
    /// This is internal because public selection and mutation should flow through the condition builder or higher-level maintained facades.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the exact encoded identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ReadOnlySpan<byte> key, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginVarKeyScalar16WriteContext();
                try
                {
                    bool writerDeleted = session.DeleteVarKeyScalar16ExactTupleForWriteContext(
                        writeContext,
                        handle.RootRouterOffset,
                        handle.MaxKeyLength,
                        encodedKey,
                        encodedIdentityHigh,
                        encodedIdentityLow,
                        maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
                    if (!writerDeleted)
                    {
                        session.AbortVarKeyScalar16WriteContext(writeContext);
                        return false;
                    }

                    _ = session.PublishVarKeyScalar16WriteContext(writeContext);
                    return true;
                }
                catch (LibraDexWriteContextVarKeyScalar16ShelfOwnershipException ex)
                {
                    session.AbortVarKeyScalar16WriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortVarKeyScalar16WriteContext(writeContext);
                    break;
                }
            }
        }

        using VarKeyScalar16Batch batch = BeginBatch();
        bool deleted = batch.DeleteExactTuple(encodedKey, encodedIdentityHigh, encodedIdentityLow);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes one exact raw-key and encoded 16-byte identity tuple using the caller's currently active durability scope.<br/>
    /// Maintained facades use this to delete exact and projection tuples inside one shared batch without nesting batch lifetimes.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the exact encoded identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTupleInCurrentScope(ReadOnlySpan<byte> key, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        ThrowIfDisposed();
        ValidateKeyLength(key, handle.MaxKeyLength);
        return session.DeleteVarKeyScalar16ExactTuple(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            key,
            encodedIdentityHigh,
            encodedIdentityLow);
    }

    /// <summary>
    /// Reads encoded 16-byte identities for an inclusive raw-byte key range into caller-owned storage.<br/>
    /// The high and low destination spans must have enough remaining capacity for the same copied identity count.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="encodedIdentityHighs">The caller-owned destination span for high identity halves.</param>
    /// <param name="encodedIdentityLows">The caller-owned destination span for low identity halves.</param>
    /// <returns>The number of identities copied.</returns>
    public int ReadRange(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        Span<ulong> encodedIdentityHighs,
        Span<ulong> encodedIdentityLows)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.ReadVarKeyScalar16IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            encodedIdentityHighs,
            encodedIdentityLows,
            handle.Descending);
    }

    /// <summary>
    /// Reads encoded 16-byte identities for an inclusive nullable developer-facing key range into caller-owned storage.<br/>
    /// Use this overload when either bound must target the null-key sentinel.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload, or null for the null-key sentinel.</param>
    /// <param name="upperKey">The inclusive upper key payload, or null for the null-key sentinel.</param>
    /// <param name="encodedIdentityHighs">The caller-owned destination span for high identity halves.</param>
    /// <param name="encodedIdentityLows">The caller-owned destination span for low identity halves.</param>
    /// <returns>The number of identities copied.</returns>
    public int ReadRange(
        byte[]? lowerKey,
        byte[]? upperKey,
        Span<ulong> encodedIdentityHighs,
        Span<ulong> encodedIdentityLows)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.ReadVarKeyScalar16IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            encodedIdentityHighs,
            encodedIdentityLows,
            handle.Descending);
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive raw-key range.<br/>
    /// The returned reader exposes each raw key as a temporary <see cref="ReadOnlySpan{T}"/> and each encoded 16-byte identity as high/low scalar halves while positioned on that row.<br/>
    /// This complements <see cref="ReadRange(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{ulong}, Span{ulong})"/> for callers that need key iteration, tuple iteration, skip behavior, or explicit materialization control.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>A forward-only reader over matching raw key and encoded identity rows.</returns>
    public VarKeyScalar16RangeReader OpenRangeReader(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.OpenVarKeyScalar16RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            decodeLogicalKeys: true,
            descending: handle.Descending);
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive nullable developer-facing key range.<br/>
    /// Use this overload when either range bound must target the null-key sentinel.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload, or null for the null-key sentinel.</param>
    /// <param name="upperKey">The inclusive upper key payload, or null for the null-key sentinel.</param>
    /// <returns>A forward-only reader over matching logical key and encoded identity rows.</returns>
    public VarKeyScalar16RangeReader OpenRangeReader(byte[]? lowerKey, byte[]? upperKey)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey));
        return session.OpenVarKeyScalar16RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            decodeLogicalKeys: true,
            descending: handle.Descending);
    }

    internal VarKeyScalar16RangeReader OpenEncodedRangeReader(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.OpenVarKeyScalar16RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            lowerKey,
            upperKey,
            decodeLogicalKeys: false,
            descending: handle.Descending);
    }

    /// <summary>
    /// Counts routed `VS16` identities whose logical raw keys are inside an inclusive range.<br/>
    /// Raw bounds are encoded at the index boundary so callers use the same physical ordering as `OpenRangeReader(...)`, deletes, and aggregate range counting.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key.<br/></param>
    /// <returns>The number of matching routed identities.<br/></returns>
    public long CountIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return CountEncodedIdentityRange(
            LibraDexVarLenKeyCodec.Encode(lowerKey, handle.MaxKeyLength, nameof(lowerKey)),
            LibraDexVarLenKeyCodec.Encode(upperKey, handle.MaxKeyLength, nameof(upperKey)));
    }

    /// <summary>
    /// Counts routed `VS16` identities in an inclusive encoded-key range through the shape-native range-count primitive.<br/>
    /// The count path can use narrow metadata traversal for fully covered direct root-prefix targets and falls back to reader counts for boundary or compressed cases.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <returns>The number of matching encoded-key identities.<br/></returns>
    internal long CountEncodedIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.CountVarKeyScalar16IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, lowerKey, upperKey);
    }

    /// <summary>
    /// Counts routed `VS16` identities whose encoded key starts with the supplied encoded prefix.<br/>
    /// The session keeps the operation shape-native so contained route targets can use shelf metadata while boundary or ambiguous targets fall back to exact range counting.<br/>
    /// </summary>
    /// <param name="encodedPrefix">The encoded key prefix to match.<br/></param>
    /// <returns>The number of matching encoded-key identities.<br/></returns>
    internal long CountEncodedIdentityPrefix(ReadOnlySpan<byte> encodedPrefix)
    {
        ThrowIfDisposed();
        using VarKeyScalar16RangeReader reader = OpenEncodedRangeReader(
            encodedPrefix,
            CreateEncodedPrefixUpperBound(encodedPrefix, handle.MaxKeyLength));
        return reader.Count;
    }

    /// <summary>
    /// Counts every ordinary routed `VS16` identity from shelf-local count metadata.<br/>
    /// This bypasses ordered range-reader progression for count-all while preserving the existing routed shelf ownership model.<br/>
    /// </summary>
    /// <returns>The ordinary non-key-state identity count.<br/></returns>
    internal long CountOrdinaryIdentities()
    {
        ThrowIfDisposed();
        return session.CountVarKeyScalar16Identities(handle.RootRouterOffset, handle.MaxKeyLength);
    }

    /// <summary>
    /// Creates the inclusive encoded upper bound for an encoded-prefix query.<br/>
    /// The lower bound is the prefix itself; padding the remaining physical key space with `0xFF` preserves exact-prefix and longer-key matches under LibraDex variable-key byte ordering.<br/>
    /// </summary>
    /// <param name="encodedPrefix">The encoded key prefix being matched.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <returns>The inclusive upper encoded key for the prefix extent.<br/></returns>
    private static byte[] CreateEncodedPrefixUpperBound(ReadOnlySpan<byte> encodedPrefix, int maxKeyLength)
    {
        int length = Math.Max(encodedPrefix.Length, maxKeyLength);
        byte[] upper = new byte[length];
        encodedPrefix.CopyTo(upper);
        upper.AsSpan(encodedPrefix.Length).Fill(0xFF);
        return upper;
    }

    /// <summary>
    /// Reads matching raw keys into a disconnected buffer object owned by the caller.<br/>
    /// The returned buffer stores keys in pooled flat payload storage plus offset/length metadata, avoiding `byte[][]` materialization while allowing array-like access after this method returns.<br/>
    /// Callers must dispose the buffer when done; spans and memory retrieved from it remain valid only while the buffer is alive.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="initialPayloadCapacity">Optional initial payload capacity in bytes for callers that already know the expected result size.</param>
    /// <returns>A disconnected key buffer owned by the caller.</returns>
    public LibraDexVarKeyBuffer ReadKeyBuffer(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int initialPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using VarKeyScalar16RangeReader reader = OpenRangeReader(lowerKey, upperKey);
        LibraDexVarKeyBuffer buffer = new(reader.Count, initialPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentKey);
        }

        return buffer;
    }

    /// <summary>
    /// Returns and resets accumulated DataKernel read telemetry for this index's owning session.<br/>
    /// This keeps validation and tuning visible without adding logging or hidden counters to hot index operations.<br/>
    /// </summary>
    /// <returns>The accumulated read telemetry since the previous reset.</returns>
    public DataKernelReadTelemetry GetAndResetReadTelemetry()
    {
        ThrowIfDisposed();
        return session.GetAndResetReadTelemetry();
    }

    /// <summary>
    /// Disposes the owning session when this index was created by a facade factory.<br/>
    /// Disposing a file-backed index releases the file handle; disposing a memory-backed index releases committed pooled memory segments.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (ownsSession)
        {
            session.Dispose();
        }

        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VarKeyScalar16Index));
        }
    }

    private static void ValidateKeyLength(ReadOnlySpan<byte> key, int maxKeyLength)
    {
        if (key.Length <= 0 || key.Length > maxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VS16 key length must be from 1 to {maxKeyLength} bytes.");
        }
    }

    /// <summary>
    /// Parses one memory var-key shelf-size environment override in KiB.<br/>
    /// The override is intentionally evaluated only when a new route is created, keeping ordinary route walks and shelf mutations free of environment lookup overhead.<br/>
    /// </summary>
    /// <param name="environmentVariableName">The environment variable that carries the KiB value.</param>
    /// <param name="defaultKiB">The KiB value to use when the environment variable is absent or invalid.</param>
    /// <returns>The parsed KiB value, or <paramref name="defaultKiB"/>.</returns>
    private static int ParseMemoryShelfKiB(string environmentVariableName, int defaultKiB)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariableName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kib)
            ? kib
            : defaultKiB;
    }
}

/// <summary>
/// Batches raw-byte `VS8` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
internal sealed class VarKeyScalar8Batch : IDisposable
{
    private readonly VarKeyScalar8Index index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private bool completed;

    internal VarKeyScalar8Batch(VarKeyScalar8Index index, LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
    }

    /// <summary>
    /// Inserts one developer-facing key payload and encoded 8-byte identity into the owning `VS8` index without forcing a durable commit per item.<br/>
    /// The first insert for an unset root-prefix route creates the prefix shelf inside this batch before using the normal routed insert path.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes.</param>
    /// <param name="encodedIdentity">The already encoded sortable 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    public VarKeyScalar8InsertOutcome Insert(ReadOnlySpan<byte> key, ulong encodedIdentity, bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key));
        return InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
    }

    internal VarKeyScalar8InsertOutcome InsertEncoded(ReadOnlySpan<byte> encodedKey, ulong encodedIdentity, bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        ValidateKeyLength(encodedKey, index.Handle.MaxKeyLength);
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(encodedKey[0]);
        VarKeyScalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedVarKeyScalar8(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops,
            requestedRouteCount: index.Handle.OptimizerRouteFanout);

        VarKeyScalar8InsertOutcome publicResult = VarKeyScalar8InsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
    }

    /// <summary>
    /// Deletes all tuples whose raw-byte key is inside an inclusive `VS8` range without forcing a durable commit per range.<br/>
    /// The owning session marks shelf-local tombstones during the batch and normalizes touched slot streams when the batch commits.<br/>
    /// This keeps bulk condition deletes on the same routed shelf path as range reads while avoiding per-row exact tuple materialization.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfCompleted();
        ValidateKeyRange(lowerKey, upperKey, index.Handle.MaxKeyLength);
        return index.Session.DeleteVarKeyScalar8KeyRange(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            lowerKey,
            upperKey);
    }

    /// <summary>
    /// Deletes one exact raw-key and encoded-identity tuple without forcing a durable commit per tuple.<br/>
    /// Projection-maintenance paths use this to remove exact projection rows without deleting other identities that share the same projected key.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="encodedIdentity">The exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        ThrowIfCompleted();
        ValidateKeyLength(key, index.Handle.MaxKeyLength);
        return index.Session.DeleteVarKeyScalar8ExactTuple(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            key,
            encodedIdentity);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise all-or-nothing item semantics beyond the staged writes that reach this publish point.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public VarKeyScalarBatchCommitResult Commit()
    {
        ThrowIfCompleted();
        completed = true;
        (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
        return new VarKeyScalarBatchCommitResult(
            attemptedInsertCount,
            insertedCount,
            alreadyPresentCount,
            keyConflictCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit);
    }

    /// <summary>
    /// Aborts the batch by discarding staged writes that were not published.<br/>
    /// This also discards any lazy root-prefix shelves created by this batch before commit.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.</returns>
    public VarKeyScalarBatchAbortResult Abort()
    {
        ThrowIfCompleted();
        completed = true;
        long deferredRequests = durabilityBatch.Abort();
        return new VarKeyScalarBatchAbortResult(attemptedInsertCount, deferredRequests);
    }

    /// <summary>
    /// Aborts uncommitted staged writes when the batch is disposed without explicit commit or abort.<br/>
    /// This makes `using` scopes safe for early exits while keeping `Commit` as the only durability publication call.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed)
        {
            Abort();
        }
    }

    private bool EnsureInitialShelfRoute(byte rootPrefix)
    {
        if (index.Session.FindRouterTarget(index.Handle.RootRouterOffset, rootPrefix) != 0)
        {
            return false;
        }

        _ = index.Session.CreateVarKeyScalar8ShelfAndLinkRootRoute(index.Handle.RootRouterOffset, rootPrefix, index.GetInitialProfile());
        return true;
    }

    private void Count(VarKeyScalar8InsertOutcome result)
    {
        attemptedInsertCount++;
        if (result.CreatedInitialShelfRoute)
        {
            initialShelfRouteCreateCount++;
        }

        if (result.Inserted)
        {
            insertedCount++;
        }
        else if (result.AlreadyPresent)
        {
            alreadyPresentCount++;
        }
        else if (result.KeyConflict)
        {
            keyConflictCount++;
        }
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The VS8 batch has already completed.");
        }
    }

    private static void ValidateKeyLength(ReadOnlySpan<byte> key, int maxKeyLength)
    {
        if (key.Length <= 0 || key.Length > maxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VS8 key length must be from 1 to {maxKeyLength} bytes.");
        }
    }

    private static void ValidateKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, int maxKeyLength)
    {
        ValidateKeyLength(lowerKey, maxKeyLength);
        ValidateKeyLength(upperKey, maxKeyLength);
    }

    /// <summary>
    /// Parses one memory var-key shelf-size environment override in KiB.<br/>
    /// The override is intentionally evaluated only when a new route is created, keeping ordinary route walks and shelf mutations free of environment lookup overhead.<br/>
    /// </summary>
    /// <param name="environmentVariableName">The environment variable that carries the KiB value.</param>
    /// <param name="defaultKiB">The KiB value to use when the environment variable is absent or invalid.</param>
    /// <returns>The parsed KiB value, or <paramref name="defaultKiB"/>.</returns>
    private static int ParseMemoryShelfKiB(string environmentVariableName, int defaultKiB)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariableName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kib)
            ? kib
            : defaultKiB;
    }
}

/// <summary>
/// Batches raw-byte `VS16` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
internal sealed class VarKeyScalar16Batch : IDisposable
{
    private readonly VarKeyScalar16Index index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private bool completed;

    internal VarKeyScalar16Batch(VarKeyScalar16Index index, LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
    }

    /// <summary>
    /// Inserts one raw-byte key and encoded 16-byte identity into the owning `VS16` index without forcing a durable commit per item.<br/>
    /// The first insert for an unset root-prefix route creates the prefix shelf inside this batch before using the normal routed insert path.<br/>
    /// </summary>
    /// <param name="key">The raw sortable key bytes.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the encoded sortable 16-byte identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the encoded sortable 16-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    public VarKeyScalar16InsertOutcome Insert(
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key));
        return InsertEncoded(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
    }

    internal VarKeyScalar16InsertOutcome InsertEncoded(
        ReadOnlySpan<byte> encodedKey,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        ValidateKeyLength(encodedKey, index.Handle.MaxKeyLength);
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(encodedKey[0]);
        VarKeyScalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedVarKeyScalar16(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            encodedKey,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops,
            descending: index.Handle.Descending);

        VarKeyScalar16InsertOutcome publicResult = VarKeyScalar16InsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
    }

    /// <summary>
    /// Deletes all tuples whose raw-byte key is inside an inclusive `VS16` range without forcing a durable commit per range.<br/>
    /// The owning session marks shelf-local tombstones during the batch and normalizes touched slot streams when the batch commits.<br/>
    /// This keeps bulk condition deletes on the same routed shelf path as range reads while avoiding per-row exact tuple materialization.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfCompleted();
        ValidateKeyRange(lowerKey, upperKey, index.Handle.MaxKeyLength);
        return index.Session.DeleteVarKeyScalar16KeyRange(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            lowerKey,
            upperKey);
    }

    /// <summary>
    /// Deletes one exact raw-key and encoded 16-byte identity tuple without forcing a durable commit per tuple.<br/>
    /// Projection-maintenance paths use this to remove exact projection rows without deleting other identities that share the same projected key.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="encodedIdentityHigh">The high 8 bytes of the exact encoded identity.</param>
    /// <param name="encodedIdentityLow">The low 8 bytes of the exact encoded identity.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ReadOnlySpan<byte> key, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        ThrowIfCompleted();
        ValidateKeyLength(key, index.Handle.MaxKeyLength);
        return index.Session.DeleteVarKeyScalar16ExactTuple(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            key,
            encodedIdentityHigh,
            encodedIdentityLow);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise all-or-nothing item semantics beyond the staged writes that reach this publish point.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public VarKeyScalarBatchCommitResult Commit()
    {
        ThrowIfCompleted();
        completed = true;
        (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
        return new VarKeyScalarBatchCommitResult(
            attemptedInsertCount,
            insertedCount,
            alreadyPresentCount,
            keyConflictCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit);
    }

    /// <summary>
    /// Aborts the batch by discarding staged writes that were not published.<br/>
    /// This also discards any lazy root-prefix shelves created by this batch before commit.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.</returns>
    public VarKeyScalarBatchAbortResult Abort()
    {
        ThrowIfCompleted();
        completed = true;
        long deferredRequests = durabilityBatch.Abort();
        return new VarKeyScalarBatchAbortResult(attemptedInsertCount, deferredRequests);
    }

    /// <summary>
    /// Aborts uncommitted staged writes when the batch is disposed without explicit commit or abort.<br/>
    /// This makes `using` scopes safe for early exits while keeping `Commit` as the only durability publication call.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed)
        {
            Abort();
        }
    }

    private bool EnsureInitialShelfRoute(byte rootPrefix)
    {
        if (index.Session.FindRouterTarget(index.Handle.RootRouterOffset, rootPrefix) != 0)
        {
            return false;
        }

        _ = index.Session.CreateVarKeyScalar16ShelfAndLinkRootRoute(index.Handle.RootRouterOffset, rootPrefix, index.GetInitialProfile());
        return true;
    }

    private void Count(VarKeyScalar16InsertOutcome result)
    {
        attemptedInsertCount++;
        if (result.CreatedInitialShelfRoute)
        {
            initialShelfRouteCreateCount++;
        }

        if (result.Inserted)
        {
            insertedCount++;
        }
        else if (result.AlreadyPresent)
        {
            alreadyPresentCount++;
        }
        else if (result.KeyConflict)
        {
            keyConflictCount++;
        }
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The VS16 batch has already completed.");
        }
    }

    private static void ValidateKeyLength(ReadOnlySpan<byte> key, int maxKeyLength)
    {
        if (key.Length <= 0 || key.Length > maxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VS16 key length must be from 1 to {maxKeyLength} bytes.");
        }
    }

    private static void ValidateKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, int maxKeyLength)
    {
        ValidateKeyLength(lowerKey, maxKeyLength);
        ValidateKeyLength(upperKey, maxKeyLength);
    }
}

/// <summary>
/// Describes one public raw-byte `VS8` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, or transformed a full shelf into a deeper router.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8InsertOutcome(
    bool Inserted,
    bool AlreadyPresent,
    bool KeyConflict,
    bool CreatedInitialShelfRoute,
    bool GrewShelf,
    bool SplitShelf,
    int TargetShelfItemCount,
    int TargetShelfExtentSize,
    ushort TargetRouterDepth,
    DataKernelCommitTelemetry Commit,
    long DeferredCommitRequests)
{
    internal static VarKeyScalar8InsertOutcome FromStorage(VarKeyScalar8RoutedInsertResult result, bool createdInitialShelfRoute)
    {
        return new VarKeyScalar8InsertOutcome(
            result.InsertResult == VarKeyScalar8InsertResult.Inserted,
            result.InsertResult == VarKeyScalar8InsertResult.AlreadyPresent,
            result.InsertResult == VarKeyScalar8InsertResult.KeyConflict,
            createdInitialShelfRoute,
            result.Kind == VarKeyScalar8RoutedInsertKind.WalkedGrow,
            result.Kind == VarKeyScalar8RoutedInsertKind.WalkedShelfTransformSplit,
            result.TargetShelfItemCount,
            result.TargetShelfExtentSize,
            result.TargetRouterDepth,
            default,
            0);
    }
}

/// <summary>
/// Describes one public raw-byte `VS16` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, or transformed a full shelf into a deeper router.<br/>
/// </summary>
internal readonly record struct VarKeyScalar16InsertOutcome(
    bool Inserted,
    bool AlreadyPresent,
    bool KeyConflict,
    bool CreatedInitialShelfRoute,
    bool GrewShelf,
    bool SplitShelf,
    int TargetShelfItemCount,
    int TargetShelfExtentSize,
    ushort TargetRouterDepth,
    DataKernelCommitTelemetry Commit,
    long DeferredCommitRequests)
{
    internal static VarKeyScalar16InsertOutcome FromStorage(VarKeyScalar16RoutedInsertResult result, bool createdInitialShelfRoute)
    {
        return new VarKeyScalar16InsertOutcome(
            result.InsertResult == VarKeyScalar16InsertResult.Inserted,
            result.InsertResult == VarKeyScalar16InsertResult.AlreadyPresent,
            result.InsertResult == VarKeyScalar16InsertResult.KeyConflict,
            createdInitialShelfRoute,
            result.Kind == VarKeyScalar16RoutedInsertKind.WalkedGrow,
            result.Kind == VarKeyScalar16RoutedInsertKind.WalkedShelfTransformSplit,
            result.TargetShelfItemCount,
            result.TargetShelfExtentSize,
            result.TargetRouterDepth,
            default,
            0);
    }
}

/// <summary>
/// Describes a committed public raw-byte `VS8` or `VS16` batch.<br/>
/// The counts describe attempted insert outcomes while the commit telemetry describes the folded DataKernel publication boundary.<br/>
/// </summary>
internal readonly record struct VarKeyScalarBatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Describes an aborted public raw-byte `VS8` or `VS16` batch.<br/>
/// The result exposes how much work was discarded without leaking internal staged-write objects to the public API.<br/>
/// </summary>
internal readonly record struct VarKeyScalarBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);
