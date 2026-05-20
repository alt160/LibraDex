namespace LibraDex;

/// <summary>
/// Provides the first runtime wrapper over one encoded `SS8-8` index resolved from a LibraDex session.<br/>
/// The wrapper owns the session when it is created by `LibraDex.Indexes.SS88` factory methods, keeps encoded operations explicit, and leaves developer-facing codecs for a later API layer.<br/>
/// </summary>
public sealed class Scalar8Scalar8Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly Scalar8Scalar8IndexHandle handle;
    private readonly bool ownsSession;
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
    /// Inserts one encoded `SS8-8` tuple into this index.<br/>
    /// If the tuple's root-prefix route has not been initialized yet, the wrapper creates an empty shelf for that prefix first and reports that setup commit separately.<br/>
    /// The mutation then uses the classified route walker so public inserts keep working after shelf-to-router transforms add another routing level.<br/>
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
        ThrowIfDisposed();

        bool createdInitialShelfRoute = false;
        DataKernelCommitTelemetry routeCreateCommit = default;
        byte rootPrefixByte = GetRootPrefix(encodedKey);
        long currentTarget = session.FindRouterTarget(handle.RootRouterOffset, rootPrefixByte);
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
}
