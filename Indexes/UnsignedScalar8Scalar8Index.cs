using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Provides the first typed public wrapper over an `SS8-8` index using unsigned scalar-8 keys and unsigned scalar-8 identities.<br/>
/// The wrapper keeps codec choice explicit while delegating storage, routing, batching, and telemetry to the encoded `Scalar8Scalar8Index` core.<br/>
/// </summary>
internal sealed class UnsignedScalar8Scalar8Index : IDisposable
{
    private readonly Scalar8Scalar8Index encodedIndex;

    internal UnsignedScalar8Scalar8Index(Scalar8Scalar8Index encodedIndex)
    {
        this.encodedIndex = encodedIndex;
    }

    /// <summary>
    /// Gets the fixed index-directory slot used to resolve this runtime index.<br/>
    /// Slot identity remains the primary create/open selector until richer index naming policy is deliberately widened.<br/>
    /// </summary>
    public int SlotIndex => encodedIndex.SlotIndex;

    /// <summary>
    /// Gets the index name stored in the fixed index-directory slot.<br/>
    /// Names are metadata labels in this slice; slot identity remains the primary create/open selector.<br/>
    /// </summary>
    public string Name => encodedIndex.Name;

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => encodedIndex.BackingKind;

    /// <summary>
    /// Starts a typed unsigned scalar `SS8-8` durability batch for bulk mutation.<br/>
    /// Batch inserts use unsigned scalar values at the call site and defer lower-level commit requests until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves the normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A typed batch object that accepts unsigned scalar inserts and controls the durability publication boundary.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another batch is already active on the owning session.</exception>
    public UnsignedScalar8Scalar8Batch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        return new UnsignedScalar8Scalar8Batch(encodedIndex.BeginBatch(writeIntent));
    }

    /// <summary>
    /// Starts an internal queued writer facade for typed unsigned scalar `SS8-8` inserts.<br/>
    /// The facade accepts unsigned scalar values, serializes overlapping caller operations, and delegates publication behavior to the encoded queued writer.<br/>
    /// This keeps Abraxas-style typed integration away from raw encoded values while preserving the current queued-writer contract.<br/>
    /// </summary>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this explicit queued-writer factory.<br/></param>
    /// <returns>A typed queued writer facade for this unsigned scalar index.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when the requested concurrency mode is not queued-writer mode.<br/></exception>
    internal UnsignedScalar8Scalar8QueuedWriter BeginQueuedWriter(LibraDexConcurrencyOptions? options = null)
    {
        return new UnsignedScalar8Scalar8QueuedWriter(encodedIndex.BeginQueuedWriter(options));
    }

    /// <summary>
    /// Inserts one unsigned scalar key and unsigned scalar identity into this index.<br/>
    /// Values are encoded with the unsigned scalar-8 codec before they enter the shared encoded routed mutation path.<br/>
    /// </summary>
    /// <param name="key">The unsigned scalar key to insert.</param>
    /// <param name="identity">The unsigned scalar identity value associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The typed insert result plus any setup and insert commit telemetry.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.</exception>
    public UnsignedScalar8Scalar8InsertResult Insert(
        ulong key,
        ulong identity,
        bool allowDuplicateKeys = true)
    {
        Scalar8Scalar8EncodedInsertResult result = encodedIndex.InsertEncoded(
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(key),
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(identity),
            allowDuplicateKeys);
        return UnsignedScalar8Scalar8ResultMapper.MapInsert(result);
    }

    /// <summary>
    /// Reads unsigned scalar identities for an inclusive unsigned scalar key range.<br/>
    /// The output span receives decoded unsigned scalar identities; for this codec the decoded value is the persisted unsigned scalar value.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower unsigned scalar key.</param>
    /// <param name="upperKey">The inclusive upper unsigned scalar key.</param>
    /// <param name="identities">The caller-owned destination span that receives unsigned scalar identity values.</param>
    /// <param name="enableCoalescing">Whether the read may coalesce adjacent shelf extents when the route shape justifies it.</param>
    /// <param name="coalescedShelfScratchCount">The pooled scratch capacity, measured in profiled shelf extents, when coalescing is enabled.</param>
    /// <returns>The range-read result describing copied identity count and scratch/coalescing shape.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    /// <exception cref="InvalidDataException">Thrown when routing does not terminate at valid `SS8-8` shelves.</exception>
    public UnsignedScalar8Scalar8RangeReadResult ReadRange(
        ulong lowerKey,
        ulong upperKey,
        Span<ulong> identities,
        bool enableCoalescing = false,
        int coalescedShelfScratchCount = 8)
    {
        Scalar8Scalar8EncodedRangeReadResult result = encodedIndex.ReadEncodedRange(
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(lowerKey),
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(upperKey),
            identities,
            enableCoalescing,
            coalescedShelfScratchCount);
        return new UnsignedScalar8Scalar8RangeReadResult(
            result.IdentityCount,
            result.UsedPooledScratch,
            result.CoalescingEnabled,
            result.RangeScratchShelfCapacity);
    }

    /// <summary>
    /// Opens a forward-only unsigned scalar `SS8-8` range reader for an inclusive key range.<br/>
    /// This is the streaming counterpart to <see cref="ReadRange(ulong, ulong, Span{ulong}, bool, int)"/> and exposes keys, identities, tuples, count, and skip without materializing the full range.<br/>
    /// The unsigned scalar codec is identity-preserving, so reader values are the same `ulong` values supplied by the public API.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower unsigned scalar key.</param>
    /// <param name="upperKey">The inclusive upper unsigned scalar key.</param>
    /// <returns>A cursor positioned before the first matching unsigned key/identity tuple.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    public UnsignedScalar8Scalar8RangeReader OpenRangeReader(ulong lowerKey, ulong upperKey)
    {
        return new UnsignedScalar8Scalar8RangeReader(
            encodedIndex.OpenEncodedRangeReader(
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(lowerKey),
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(upperKey)));
    }

    /// <summary>
    /// Returns and resets accumulated DataKernel read telemetry for this index's owning session.<br/>
    /// This keeps the typed wrapper useful for validation without adding logging or hidden counters to hot index operations.<br/>
    /// </summary>
    /// <returns>The accumulated read telemetry since the previous reset.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    public DataKernelReadTelemetry GetAndResetReadTelemetry()
    {
        return encodedIndex.GetAndResetReadTelemetry();
    }

    /// <summary>
    /// Disposes the owning encoded index wrapper.<br/>
    /// Disposing a file-backed index releases the file handle; disposing a memory-backed index releases committed pooled memory segments.<br/>
    /// </summary>
    public void Dispose()
    {
        encodedIndex.Dispose();
    }
}

/// <summary>
/// Provides a typed unsigned scalar facade over the encoded `SS8-8` queued writer.<br/>
/// The wrapper serializes overlapping inserts through the encoded queued writer while keeping callers on unsigned key and identity values.<br/>
/// </summary>
internal sealed class UnsignedScalar8Scalar8QueuedWriter
{
    private readonly Scalar8Scalar8QueuedWriter encodedWriter;

    internal UnsignedScalar8Scalar8QueuedWriter(Scalar8Scalar8QueuedWriter encodedWriter)
    {
        this.encodedWriter = encodedWriter;
    }

    /// <summary>
    /// Inserts one unsigned scalar key and identity through the typed queued writer facade.<br/>
    /// The values are encoded before entering the shared queued-writer path, so queued path attribution and commit telemetry remain identical to the encoded core.<br/>
    /// </summary>
    /// <param name="key">The unsigned scalar key to insert.<br/></param>
    /// <param name="identity">The unsigned scalar identity value associated with the key.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.<br/></param>
    /// <returns>The typed insert result and queued-writer attribution.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    internal UnsignedScalar8Scalar8InsertResult Insert(
        ulong key,
        ulong identity,
        bool allowDuplicateKeys = true)
    {
        Scalar8Scalar8EncodedInsertResult result = encodedWriter.InsertEncoded(
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(key),
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(identity),
            allowDuplicateKeys);
        return UnsignedScalar8Scalar8ResultMapper.MapInsert(result);
    }
}

/// <summary>
/// Provides a forward-only typed cursor over unsigned scalar `SS8-8` range results.<br/>
/// The reader wraps the encoded `SS8-8` cursor while preserving unsigned call-site values, giving the typed API the same datareader-style surface as the generic fixed facade.<br/>
/// Use <see cref="TryReadNextIdentity(out ulong)"/> for identity-only streaming, <see cref="TryReadNextKey(out ulong)"/> for key-only streaming, and <see cref="TryReadNext(out ulong, out ulong)"/> when both tuple fields are needed.<br/>
/// </summary>
internal sealed class UnsignedScalar8Scalar8RangeReader : IDisposable
{
    private readonly Scalar8Scalar8RangeReader encodedReader;
    private bool disposed;

    internal UnsignedScalar8Scalar8RangeReader(Scalar8Scalar8RangeReader encodedReader)
    {
        this.encodedReader = encodedReader;
    }

    /// <summary>
    /// Gets the number of matching rows available to this reader.<br/>
    /// The count is derived from planned shelf-local slot ranges and does not copy identities into a caller buffer.<br/>
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return encodedReader.Count;
        }
    }

    /// <summary>
    /// Gets the zero-based row ordinal after a successful <see cref="MoveNext"/> call.<br/>
    /// The value is `-1` before the first row and equals <see cref="Count"/> after the reader passes the final row.<br/>
    /// </summary>
    public int Ordinal
    {
        get
        {
            ThrowIfDisposed();
            return encodedReader.Ordinal;
        }
    }

    /// <summary>
    /// Gets the current unsigned scalar key.<br/>
    /// Call this only after <see cref="MoveNext"/> returns <see langword="true"/>; the unsigned scalar codec stores this value unchanged.<br/>
    /// </summary>
    public ulong CurrentKey
    {
        get
        {
            ThrowIfDisposed();
            return encodedReader.CurrentEncodedKey;
        }
    }

    /// <summary>
    /// Gets the current unsigned scalar identity.<br/>
    /// Call this only after <see cref="MoveNext"/> returns <see langword="true"/>; the unsigned scalar codec stores this value unchanged.<br/>
    /// </summary>
    public ulong CurrentIdentity
    {
        get
        {
            ThrowIfDisposed();
            return encodedReader.CurrentEncodedIdentity;
        }
    }

    /// <summary>
    /// Advances the reader and returns the next unsigned scalar identity without separately reading current-row properties.<br/>
    /// This is the lowest-overhead typed identity streaming path for callers that do not need keys.<br/>
    /// </summary>
    /// <param name="identity">Receives the unsigned scalar identity when a row is available.</param>
    /// <returns><see langword="true"/> when an identity was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextIdentity(out ulong identity)
    {
        ThrowIfDisposed();
        return encodedReader.TryReadNextEncodedIdentity(out identity);
    }

    /// <summary>
    /// Advances the reader and returns the next unsigned scalar key without separately reading identity data.<br/>
    /// This path is useful for range analysis where key values matter but identities can be skipped.<br/>
    /// </summary>
    /// <param name="key">Receives the unsigned scalar key when a row is available.</param>
    /// <returns><see langword="true"/> when a key was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextKey(out ulong key)
    {
        if (!MoveNext())
        {
            key = 0;
            return false;
        }

        key = encodedReader.CurrentEncodedKey;
        return true;
    }

    /// <summary>
    /// Advances the reader and returns the next unsigned scalar key/identity tuple.<br/>
    /// This combines movement and tuple projection so callers do not need separate current-property reads for common full-row streaming.<br/>
    /// </summary>
    /// <param name="key">Receives the unsigned scalar key when a row is available.</param>
    /// <param name="identity">Receives the unsigned scalar identity when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNext(out ulong key, out ulong identity)
    {
        if (!MoveNext())
        {
            key = 0;
            identity = 0;
            return false;
        }

        key = encodedReader.CurrentEncodedKey;
        identity = encodedReader.CurrentEncodedIdentity;
        return true;
    }

    /// <summary>
    /// Advances the cursor to the next matching unsigned key/identity row.<br/>
    /// The method performs no decoding because unsigned scalar values are stored as their sortable representation.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid row.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        return encodedReader.MoveNext();
    }

    /// <summary>
    /// Skips up to <paramref name="count"/> rows without reading key or identity values.<br/>
    /// The underlying fixed reader advances across planned shelf-local slot ranges instead of stepping one row at a time where possible.<br/>
    /// </summary>
    /// <param name="count">The maximum number of rows to skip.</param>
    /// <returns>The number of rows actually skipped.</returns>
    public int Skip(int count)
    {
        ThrowIfDisposed();
        return encodedReader.Skip(count);
    }

    /// <summary>
    /// Releases the wrapped encoded range reader and its retained shelf buffers.<br/>
    /// The typed reader must not be used after disposal.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        encodedReader.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(UnsignedScalar8Scalar8RangeReader));
        }
    }
}
