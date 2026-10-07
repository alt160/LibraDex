using System.Threading;

namespace LibraDex;

/// <summary>
/// Provides the first runtime wrapper over one encoded `SS8-8` index resolved from a LibraDex session.<br/>
/// The wrapper owns the session when it is created by `LibraDex.Indexes.SS88` factory methods, keeps encoded operations explicit, and leaves developer-facing codecs for a later API layer.<br/>
/// </summary>
internal sealed class Scalar8Scalar8Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly Scalar8Scalar8IndexHandle handle;
    private readonly bool ownsSession;
    private readonly ReaderWriterLockSlim singleOperationAdmissionSync = new(LockRecursionPolicy.SupportsRecursion);
    private bool disposed;

    internal Scalar8Scalar8Index(
        LibraDexFileSession session,
        Scalar8Scalar8IndexHandle handle,
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
    /// The first public wrapper stays slot-based until richer index naming and metadata policy are deliberately widened.<br/>
    /// </summary>
    public int SlotIndex { get; }

    /// <summary>
    /// Gets the index name stored in the fixed index-directory slot.<br/>
    /// Names are still metadata labels in this slice; slot identity remains the primary create/open selector.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the owning session for internal batch implementations.<br/>
    /// This avoids widening the public API while allowing `Scalar8Scalar8Batch` to coordinate batch-local shelf mutation caching with the same routed index handle.<br/>
    /// </summary>
    internal LibraDexFileSession Session => session;

    /// <summary>
    /// Gets the resolved encoded `SS8-8` handle for internal batch implementations.<br/>
    /// The handle supplies the root router and shelf profile needed by batch-local mutation caches without adding public surface area.<br/>
    /// </summary>
    internal Scalar8Scalar8IndexHandle Handle => handle;

    /// <summary>
    /// Starts an encoded `SS8-8` durability batch for bulk mutation.<br/>
    /// Batch inserts use the same routed mutation path as normal inserts, but lower-level commit requests are deferred until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves the normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A batch object that accepts encoded inserts and controls the durability publication boundary.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another batch is already active on the owning session.</exception>
    public Scalar8Scalar8Batch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return new Scalar8Scalar8Batch(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Starts an internal writer object for shelf-local `SS8-8` concurrent-writer staging through this index wrapper.<br/>
    /// The writer owns one isolated context, exposes encoded inserts directly, and aborts unpublished staged work when disposed without publication.<br/>
    /// This is a low-friction internal facade over the current writer-context slice; it still accepts only no-split shelf-local writes.<br/>
    /// </summary>
    /// <returns>A writer object that stages shelf-local changes until it publishes or aborts.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    internal Scalar8Scalar8Writer BeginWriter()
    {
        ThrowIfDisposed();
        return new Scalar8Scalar8Writer(this, BeginWriteContext());
    }

    /// <summary>
    /// Starts an internal concurrent-write admission facade for low-friction overlapping caller use.<br/>
    /// Shelf-local inserts use independent writer contexts where possible, same-shelf ownership conflicts wait and retry, publication is short and serialized, and unsupported topology shapes fall back to the existing serialized insert path.<br/>
    /// This mode is intentionally internal until the public concurrency contract names its exact ownership and fallback behavior.<br/>
    /// </summary>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this explicit queued-writer factory.<br/></param>
    /// <returns>A queued writer facade for this index wrapper.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when the requested concurrency mode is not queued-writer mode.<br/></exception>
    internal Scalar8Scalar8QueuedWriter BeginQueuedWriter(LibraDexConcurrencyOptions? options = null)
    {
        ThrowIfDisposed();
        RequireQueuedWriterMode(options);
        return new Scalar8Scalar8QueuedWriter(this);
    }

    /// <summary>
    /// Starts an internal `SS8-8` writer context for shelf-local concurrent-writer staging through this index wrapper.<br/>
    /// The context can stage no-split inserts through <see cref="InsertEncodedForWriteContext"/> and must be completed with <see cref="PublishWriteContext"/> or <see cref="AbortWriteContext"/>.<br/>
    /// This is not a public concurrency contract; it is the narrow index-level wrapper used while the shelf-local writer model is being proven.<br/>
    /// </summary>
    /// <returns>A writer context that can stage shelf-local `SS8-8` changes for this session.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    internal LibraDexWriteContext BeginWriteContext()
    {
        ThrowIfDisposed();
        return session.BeginScalar8Scalar8WriteContext(handle.RootRouterOffset);
    }

    /// <summary>
    /// Publishes staged `SS8-8` changes from an internal writer context through the session publication seam.<br/>
    /// Publication is serialized with other session publication work even though staging can happen independently for different shelves.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to publish.<br/></param>
    /// <returns>The DataKernel commit telemetry for the writer-context publication.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    internal DataKernelCommitTelemetry PublishWriteContext(LibraDexWriteContext writeContext)
    {
        ThrowIfDisposed();
        return session.PublishScalar8Scalar8WriteContext(writeContext);
    }

    /// <summary>
    /// Abandons staged `SS8-8` changes from an internal writer context and releases its shelf ownership claims.<br/>
    /// No `DataKernel` pending writes are discarded because writer-context staging remains context-local until publication.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to abort.<br/></param>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    internal void AbortWriteContext(LibraDexWriteContext writeContext)
    {
        ThrowIfDisposed();
        session.AbortScalar8Scalar8WriteContext(writeContext);
    }

    /// <summary>
    /// Inserts one encoded `SS8-8` tuple into this index using an internal writer context.<br/>
    /// The method intentionally covers only pre-existing, non-empty, ordinary shelf routes where the tuple fits without split or route publication work.<br/>
    /// The returned insert commit telemetry is default because the change is only staged; <see cref="PublishWriteContext"/> performs the serialized publication commit.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged shelf bytes until publish or abort.<br/></param>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.<br/></param>
    /// <returns>The operation-facing insert outcome with structural attribution and default commit telemetry until publication.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when the route must be initialized, split, transformed, or otherwise changed outside shelf-local ownership.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal Scalar8Scalar8EncodedInsertResult InsertEncodedForWriteContext(
        LibraDexWriteContext writeContext,
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();

        byte rootPrefixByte = GetRootPrefix(encodedKey);
        long currentTarget = session.FindRouterTarget(handle.RootRouterOffset, rootPrefixByte);
        if (currentTarget == 0)
        {
            throw new InvalidOperationException("The SS8-8 writer-context insert cannot initialize a missing root-prefix route.");
        }

        Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8NoSplitForWriteContext(
            writeContext,
            handle.RootRouterOffset,
            handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8);

        return new Scalar8Scalar8EncodedInsertResult(
            MapInsertOutcome(result.InsertResult),
            CreatedInitialShelfRoute: false,
            RouteCreateCommit: default,
            InsertCommit: default)
        {
            StructuralKind = result.Kind,
            PrimaryOffset = result.PrimaryOffset,
            LeftShelfOffset = result.LeftShelfOffset,
            RightShelfOffset = result.RightShelfOffset
        };
    }

    /// <summary>
    /// Deletes one exact encoded `SS8-8` tuple using an internal writer context.<br/>
    /// The method stages shelf-local exact deletes, including bounded terminal identity and duplicate-run shelf rewrites that do not require route cleanup or relink.<br/>
    /// The returned commit telemetry is default because <see cref="PublishWriteContext"/> performs the serialized publication commit.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged shelf bytes until publish or abort.<br/></param>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <returns>True when one exact tuple was staged for deletion; false when the tuple was not present.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when the delete requires topology mutation outside shelf-local ownership.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal bool DeleteEncodedForWriteContext(
        LibraDexWriteContext writeContext,
        ulong encodedKey,
        ulong encodedIdentity)
    {
        ThrowIfDisposed();
        return session.DeleteWalkedRoutedScalar8Scalar8ExactForWriteContext(
            writeContext,
            handle.RootRouterOffset,
            handle.Profile,
            encodedKey,
            encodedIdentity,
            maxRouterHops: 8);
    }

    /// <summary>
    /// Inserts one encoded `SS8-8` tuple into this index.<br/>
    /// The wrapper uses the same internal concurrent-write admission path as <see cref="BeginQueuedWriter(LibraDexConcurrencyOptions?)"/> so callers get shelf-local writer-context staging without opting into ceremony.<br/>
    /// Shelf-local mutations can stage concurrently, while route creation, splits, transforms, and overflow topology publish through narrowed or serialized fallback sections as needed.<br/>
    /// The method remains encoded-only so this API layer does not silently choose key or identity codecs.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.</param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.</param>
    /// <returns>The operation-facing insert outcome plus any setup and insert commit telemetry.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.</exception>
    public Scalar8Scalar8EncodedInsertResult InsertEncoded(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        if (singleOperationAdmissionSync.TryEnterWriteLock(0))
        {
            try
            {
                return InsertEncodedSerializedFallback(encodedKey, encodedIdentity, allowDuplicateKeys);
            }
            finally
            {
                singleOperationAdmissionSync.ExitWriteLock();
            }
        }

        singleOperationAdmissionSync.EnterReadLock();
        try
        {
            return new Scalar8Scalar8QueuedWriter(this).InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
        }
        finally
        {
            singleOperationAdmissionSync.ExitReadLock();
        }
    }

    /// <summary>
    /// Runs the legacy direct `SS8-8` walked insertion path for a caller that already owns the serialized topology fallback boundary.<br/>
    /// This method is intentionally internal-only so ordinary public one-shot inserts cannot bypass writer-context admission and accidentally race topology publication.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.<br/></param>
    /// <returns>The operation-facing insert outcome plus any setup and insert commit telemetry.<br/></returns>
    internal Scalar8Scalar8EncodedInsertResult InsertEncodedSerializedFallback(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        ThrowIfDisposed();

        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        byte rootPrefixByte = GetRootPrefix(encodedKey);
        long currentTarget;
        session.EnterScalar8Scalar8WriterContextStaging(handle.RootRouterOffset);
        try
        {
            currentTarget = session.FindRouterTarget(handle.RootRouterOffset, rootPrefixByte);
        }
        finally
        {
            session.ExitScalar8Scalar8WriterContextStaging(handle.RootRouterOffset);
        }

        if (currentTarget == 0)
        {
            (_, routeCreateCommit) = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(
                handle.RootRouterOffset,
                rootPrefixByte,
                handle.Profile,
                itemCount: 0);
            createdInitialShelfRoute = true;
        }

        Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8(
            handle.RootRouterOffset,
            handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8);

        return new Scalar8Scalar8EncodedInsertResult(
            MapInsertOutcome(result.InsertResult),
            createdInitialShelfRoute,
            routeCreateCommit,
            result.Commit)
        {
            StructuralKind = result.Kind,
            PrimaryOffset = result.PrimaryOffset,
            LeftShelfOffset = result.LeftShelfOffset,
            RightShelfOffset = result.RightShelfOffset
        };
    }

    /// <summary>
    /// Deletes one exact encoded `SS8-8` tuple and publishes the mutation immediately.<br/>
    /// Empty shelves, duplicate-run shelves, and terminal roots remain structurally valid after tuple removal; route cleanup is left to future maintenance so exact deletes stay local and predictable.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="telemetry">Receives commit telemetry when one tuple was deleted.<br/></param>
    /// <returns>True when one exact tuple was deleted; false when the tuple was not present.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal bool DeleteEncoded(
        ulong encodedKey,
        ulong encodedIdentity,
        out DataKernelCommitTelemetry telemetry)
    {
        ThrowIfDisposed();
        return session.DeleteWalkedRoutedScalar8Scalar8Exact(
            handle.RootRouterOffset,
            handle.Profile,
            encodedKey,
            encodedIdentity,
            maxRouterHops: 8,
            Scalar8Scalar8RouteReadPolicy.PreferPromotedViews,
            out telemetry);
    }

    /// <summary>
    /// Reads encoded identities for an inclusive encoded key range using pooled scratch owned by this call.<br/>
    /// This is the low-friction wrapper path; hot callers should keep using lower-level explicit scratch APIs until a public reusable-scratch shape is promoted.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <param name="encodedIdentities">The caller-owned destination span that receives encoded identity values.</param>
    /// <param name="enableCoalescing">Whether the read may coalesce adjacent shelf extents when the route shape justifies it.</param>
    /// <param name="coalescedShelfScratchCount">The pooled scratch capacity, measured in profiled shelf extents, when coalescing is enabled.</param>
    /// <returns>The range-read result describing copied identity count and scratch/coalescing shape.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    /// <exception cref="InvalidDataException">Thrown when routing does not terminate at valid `SS8-8` shelves.</exception>
    public Scalar8Scalar8EncodedRangeReadResult ReadEncodedRange(
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        Span<ulong> encodedIdentities,
        bool enableCoalescing = false,
        int coalescedShelfScratchCount = 8)
    {
        ThrowIfDisposed();

        Scalar8Scalar8RangeReadOptions options = enableCoalescing
            ? Scalar8Scalar8RangeReadOptions.CoalescingCached with { CoalescedShelfScratchCount = coalescedShelfScratchCount }
            : Scalar8Scalar8RangeReadOptions.ConservativeCached;
        Scalar8Scalar8RangeReadResult result = session.ReadEncodedScalar8Scalar8IdentityRangePooled(
            handle,
            lowerEncodedKey,
            upperEncodedKey,
            options,
            encodedIdentities);
        return new Scalar8Scalar8EncodedRangeReadResult(
            result.IdentityCount,
            result.UsedPooledScratch,
            result.CoalescingEnabled,
            result.RangeScratchShelfCapacity);
    }

    /// <summary>
    /// Opens a forward-only encoded `SS8-8` range reader for an inclusive encoded key range.<br/>
    /// The reader exposes encoded keys and encoded identities without forcing callers to allocate an output array or materialize every row up front.<br/>
    /// This is the datareader-style counterpart to <see cref="ReadEncodedRange(ulong, ulong, Span{ulong}, bool, int)"/> for streaming analysis and identity-only iteration paths.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <returns>A cursor positioned before the first matching encoded tuple.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    public Scalar8Scalar8RangeReader OpenEncodedRangeReader(
        ulong lowerEncodedKey,
        ulong upperEncodedKey)
    {
        ThrowIfDisposed();
        return session.OpenScalar8Scalar8RangeReader(
            handle.RootRouterOffset,
            handle.Profile,
            lowerEncodedKey,
            upperEncodedKey);
    }

    /// <summary>
    /// Returns and resets accumulated DataKernel read telemetry for this index's owning session.<br/>
    /// This keeps the first wrapper useful for validation without adding logging or hidden counters to hot index operations.<br/>
    /// </summary>
    /// <returns>The accumulated read telemetry since the previous reset.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
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

    private static byte GetRootPrefix(ulong encodedKey)
    {
        return (byte)(encodedKey >> 56);
    }

    private static Scalar8Scalar8EncodedInsertOutcome MapInsertOutcome(Scalar8Scalar8InsertResult result)
    {
        return result switch
        {
            Scalar8Scalar8InsertResult.Inserted => Scalar8Scalar8EncodedInsertOutcome.Inserted,
            Scalar8Scalar8InsertResult.AlreadyPresent => Scalar8Scalar8EncodedInsertOutcome.AlreadyPresent,
            Scalar8Scalar8InsertResult.KeyConflict => Scalar8Scalar8EncodedInsertOutcome.KeyConflict,
            Scalar8Scalar8InsertResult.Full => throw new InvalidDataException("The routed SS8-8 insert returned Full after split handling."),
            _ => throw new InvalidDataException($"Unknown SS8-8 insert result {result}.")
        };
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar8Scalar8Index));
        }
    }

    /// <summary>
    /// Validates that a queued-writer factory was called with queued-writer concurrency options.<br/>
    /// Passing no options is accepted because the factory name already selects queued-writer mode.<br/>
    /// </summary>
    /// <param name="options">The requested concurrency options, or null for the factory default.<br/></param>
    /// <exception cref="NotSupportedException">Thrown when the options request a mode other than queued writer.<br/></exception>
    private static void RequireQueuedWriterMode(LibraDexConcurrencyOptions? options)
    {
        LibraDexConcurrencyMode mode = options?.Mode ?? LibraDexConcurrencyMode.QueuedWriter;
        if (mode != LibraDexConcurrencyMode.QueuedWriter)
        {
            throw new NotSupportedException($"The SS8-8 queued writer factory requires {nameof(LibraDexConcurrencyMode.QueuedWriter)} mode, not {mode}.");
        }
    }
}

/// <summary>
/// Owns one internal `SS8-8` writer context for shelf-local staged mutation through a `Scalar8Scalar8Index`.<br/>
/// The writer is intentionally narrower than `Scalar8Scalar8Batch`: it can stage independent no-split shelf writes and publish them later, but it does not initialize routes, split shelves, or promise public thread safety.<br/>
/// Disposing without publication aborts the context-local staged shelves and releases any shelf ownership claims.<br/>
/// </summary>
internal sealed class Scalar8Scalar8Writer : IDisposable
{
    private readonly Scalar8Scalar8Index index;
    private readonly LibraDexWriteContext writeContext;
    private bool completed;

    internal Scalar8Scalar8Writer(
        Scalar8Scalar8Index index,
        LibraDexWriteContext writeContext)
    {
        this.index = index;
        this.writeContext = writeContext;
    }

    /// <summary>
    /// Gets whether this writer context contains an accepted ordinary or terminal shelf mutation.<br/>
    /// Read/claim-only shelf images do not count, allowing the concurrent-batch facade to abort a truly empty conflicting context without discarding useful work.<br/>
    /// </summary>
    internal bool HasStagedMutations => writeContext.HasScalar8Scalar8Mutations;

    /// <summary>
    /// Inserts one encoded tuple into the owning index using this writer's isolated context.<br/>
    /// The tuple is staged only when the route resolves to an existing ordinary shelf and fits without split or route publication work.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.<br/></param>
    /// <returns>The operation-facing insert outcome with structural attribution and default commit telemetry until publication.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when the writer is already completed or when the insert requires unsupported topology work.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal Scalar8Scalar8EncodedInsertResult InsertEncoded(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        return index.InsertEncodedForWriteContext(writeContext, encodedKey, encodedIdentity, allowDuplicateKeys);
    }

    /// <summary>
    /// Deletes one exact encoded tuple from the owning index using this writer's isolated context.<br/>
    /// The tuple is staged only when the route resolves to an owned shelf-local mutation; terminal-root or topology-affecting deletes are rejected for queued fallback.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <returns>True when one exact tuple was staged for deletion; false when the tuple was not present.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when the writer is already completed or when the delete requires unsupported topology work.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal bool DeleteEncoded(
        ulong encodedKey,
        ulong encodedIdentity)
    {
        ThrowIfCompleted();
        return index.DeleteEncodedForWriteContext(writeContext, encodedKey, encodedIdentity);
    }

    /// <summary>
    /// Publishes all staged shelf-local writes owned by this writer through the serialized session publication seam.<br/>
    /// Staging can be independent across different shelves, but this publication step remains one-at-a-time per session.<br/>
    /// </summary>
    /// <returns>The DataKernel commit telemetry for the publication.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when the writer is already completed.<br/></exception>
    internal DataKernelCommitTelemetry Publish()
    {
        ThrowIfCompleted();
        DataKernelCommitTelemetry telemetry = index.PublishWriteContext(writeContext);
        completed = true;
        return telemetry;
    }

    /// <summary>
    /// Aborts unpublished shelf-local writes owned by this writer and releases any shelf ownership claims.<br/>
    /// This does not discard `DataKernel` pending writes because writer-context staging has not reached `DataKernel` before publication.<br/>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the writer is already completed.<br/></exception>
    internal void Abort()
    {
        ThrowIfCompleted();
        index.AbortWriteContext(writeContext);
        completed = true;
    }

    /// <summary>
    /// Aborts unpublished staged writes when the writer is disposed without explicit publication or abort.<br/>
    /// This makes `using` scopes safe for early exits while keeping `Publish` as the only visibility step.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed)
        {
            Abort();
        }
    }

    /// <summary>
    /// Rejects operations after the writer has published or aborted its context.<br/>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the writer is already completed.<br/></exception>
    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The SS8-8 writer has already been completed.");
        }
    }
}

/// <summary>
/// Provides an internal concurrent-write admission facade for callers that may overlap but do not want to manage writer-context conflicts themselves.<br/>
/// The facade first attempts the shelf-local writer-context path so different shelves can stage independently, waits and retries same-shelf conflicts, serializes publication, and falls back to the existing serialized insert path when the route needs unsupported topology work.<br/>
/// This is not the final public multi-writer contract; it is the first low-friction bridge between caller-side concurrency and the current shelf-local writer-context implementation.<br/>
/// </summary>
internal sealed class Scalar8Scalar8QueuedWriter
{
    private readonly Scalar8Scalar8Index index;
    private readonly object queueSync = new();

    internal Scalar8Scalar8QueuedWriter(Scalar8Scalar8Index index)
    {
        this.index = index;
    }

    /// <summary>
    /// Inserts one encoded tuple through the concurrent-write admission facade.<br/>
    /// The method uses writer-context staging for supported shelf-local routes, waits and retries when another context owns the same shelf, serializes publication, and falls back to the existing serialized insert path for route initialization, splits, linked duplicate-run work, or other unsupported writer-context shapes.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.<br/></param>
    /// <param name="cancellationToken">Cancellation observed while waiting for or processing the queued operation.<br/></param>
    /// <returns>The operation-facing insert outcome and the commit telemetry for the path that published the insert.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the owning index has already been disposed.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal Scalar8Scalar8EncodedInsertResult InsertEncoded(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            using Scalar8Scalar8Writer writer = index.BeginWriter();
            Scalar8Scalar8EncodedInsertResult stagedResult;
            try
            {
                stagedResult = writer.InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
            }
            catch (LibraDexWriteContextShelfOwnershipException ex)
            {
                writer.Abort();
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
                continue;
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
            {
                writer.Abort();
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
                continue;
            }
            catch (InvalidOperationException)
            {
                writer.Abort();
                byte rootPrefixByte = (byte)(encodedKey >> 56);
                if (index.Session.TryInsertScalar8Scalar8QueuedColdRootRoute(
                    index.Handle.RootRouterOffset,
                    rootPrefixByte,
                    index.Handle.Profile,
                    encodedKey,
                    encodedIdentity,
                    allowDuplicateKeys,
                    out Scalar8Scalar8EncodedInsertResult coldRouteResult))
                {
                    return coldRouteResult with
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                    };
                }

                Scalar8Scalar8EncodedInsertResult? narrowTopologyResult = index.Session.RunScalar8Scalar8NarrowTopologyMutation(
                    index.Handle.RootRouterOffset,
                    () => TryInsertEncodedThroughNarrowTopologyPublisher(encodedKey, encodedIdentity, allowDuplicateKeys));
                if (narrowTopologyResult.HasValue)
                {
                    return narrowTopologyResult.Value;
                }

                lock (queueSync)
                {
                    Scalar8Scalar8EncodedInsertResult fallbackResult = index.Session.RunScalar8Scalar8SerializedTopologyFallback(
                        index.Handle.RootRouterOffset,
                        () => index.InsertEncodedSerializedFallback(encodedKey, encodedIdentity, allowDuplicateKeys));
                    return fallbackResult with
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.SerializedFallback
                    };
                }
            }

            DataKernelCommitTelemetry commit = writer.Publish();
            return new Scalar8Scalar8EncodedInsertResult(
                stagedResult.Outcome,
                stagedResult.CreatedInitialShelfRoute,
                stagedResult.RouteCreateCommit,
                commit)
                {
                    StructuralKind = stagedResult.StructuralKind,
                    PrimaryOffset = stagedResult.PrimaryOffset,
                    LeftShelfOffset = stagedResult.LeftShelfOffset,
                    RightShelfOffset = stagedResult.RightShelfOffset,
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
            };
        }
    }

    /// <summary>
    /// Deletes one exact encoded tuple through the concurrent-write admission facade.<br/>
    /// Shelf-local deletes use writer-context staging and retry same-shelf ownership conflicts; only deletes that still need topology repair or unmodeled relink fall back to serialized exact-delete mutation.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="telemetry">Receives commit telemetry when one tuple was deleted and published.<br/></param>
    /// <param name="cancellationToken">Cancellation observed while waiting for or processing the queued operation.<br/></param>
    /// <param name="queuedPath">Receives the queued storage path used for the deletion.<br/></param>
    /// <returns>True when one exact tuple was deleted; false when the tuple was not present.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal bool DeleteEncoded(
        ulong encodedKey,
        ulong encodedIdentity,
        out DataKernelCommitTelemetry telemetry,
        out Scalar8Scalar8QueuedInsertPath queuedPath,
        CancellationToken cancellationToken = default)
    {
        telemetry = default;
        queuedPath = Scalar8Scalar8QueuedInsertPath.None;
        while (true)
        {
            using Scalar8Scalar8Writer writer = index.BeginWriter();
            bool deleted;
            try
            {
                deleted = writer.DeleteEncoded(encodedKey, encodedIdentity);
            }
            catch (LibraDexWriteContextShelfOwnershipException ex)
            {
                writer.Abort();
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
                continue;
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
            {
                writer.Abort();
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
                continue;
            }
            catch (InvalidOperationException)
            {
                writer.Abort();
                lock (queueSync)
                {
                    bool fallbackDeleted = index.DeleteEncoded(encodedKey, encodedIdentity, out DataKernelCommitTelemetry fallbackTelemetry);
                    telemetry = fallbackTelemetry;
                    queuedPath = fallbackDeleted
                        ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                        : Scalar8Scalar8QueuedInsertPath.None;
                    return fallbackDeleted;
                }
            }

            if (!deleted)
            {
                writer.Abort();
                return false;
            }

            telemetry = writer.Publish();
            queuedPath = Scalar8Scalar8QueuedInsertPath.WriterContext;
            return true;
        }
    }

    /// <summary>
    /// Attempts every bounded topology-changing `SS8-8` publisher for one encoded insert while the caller owns the per-root topology write gate.<br/>
    /// Cold root-route creation is deliberately attempted before this method under its independent root-prefix owner because an unset prefix cannot have a staged shelf writer and therefore does not require the whole-root drain barrier.<br/>
    /// The ordered probes preserve the existing root split/transform, parent split, duplicate-run, terminal-identity, and duplicate-key fallback preference while preventing any pre-existing writer context from retaining a private image across a topology rewrite.<br/>
    /// A null result means no narrow publisher accepted the current topology and the caller should use the broader serialized fallback.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="encodedIdentity">The already encoded 8-byte scalar identity value.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same encoded key.<br/></param>
    /// <returns>The published narrow-topology result, or <see langword="null"/> when broader fallback is required.<br/></returns>
    private Scalar8Scalar8EncodedInsertResult? TryInsertEncodedThroughNarrowTopologyPublisher(
        ulong encodedKey,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        byte rootPrefixByte = (byte)(encodedKey >> 56);
        if (index.Session.TrySplitScalar8Scalar8QueuedRootPrefix(
            index.Handle.RootRouterOffset,
            rootPrefixByte,
            index.Handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            out Scalar8Scalar8EncodedInsertResult rootPrefixSplitResult))
        {
            return rootPrefixSplitResult with
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
            };
        }

        if (index.Session.TrySplitScalar8Scalar8QueuedRootShelfTransform(
            index.Handle.RootRouterOffset,
            rootPrefixByte,
            index.Handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            out Scalar8Scalar8EncodedInsertResult rootShelfTransformResult))
        {
            return rootShelfTransformResult with
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
            };
        }

        if (index.Session.TrySplitScalar8Scalar8QueuedParentRoute(
            index.Handle.RootRouterOffset,
            index.Handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8,
            out Scalar8Scalar8EncodedInsertResult parentRouteSplitResult))
        {
            return parentRouteSplitResult with
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
            };
        }

        if (index.Session.TryInsertScalar8Scalar8QueuedDuplicateRunChain(
            index.Handle.RootRouterOffset,
            index.Handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8,
            out Scalar8Scalar8EncodedInsertResult duplicateRunChainResult))
        {
            return duplicateRunChainResult with
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
            };
        }

        if (index.Session.TryInsertScalar8Scalar8QueuedTerminalIdentityOverflow(
            index.Handle.RootRouterOffset,
            index.Handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8,
            out Scalar8Scalar8EncodedInsertResult terminalIdentityOverflowResult))
        {
            return terminalIdentityOverflowResult with
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
            };
        }

        if (index.Session.TryInsertScalar8Scalar8QueuedDuplicateKeyOverflow(
            index.Handle.RootRouterOffset,
            index.Handle.Profile,
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8,
            out Scalar8Scalar8EncodedInsertResult duplicateKeyOverflowResult))
        {
            return duplicateKeyOverflowResult with
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
            };
        }

        return null;
    }
}
