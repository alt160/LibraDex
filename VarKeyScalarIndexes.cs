namespace LibraDex;

/// <summary>
/// Provides a public raw-byte wrapper over one routed `VS8` index: varlen key bytes and one encoded 8-byte scalar identity.<br/>
/// This surface intentionally stays codec-free so text, path, blob, and unmanaged projections can be layered later without changing the persisted shelf shape.<br/>
/// </summary>
public sealed class VarKeyScalar8Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly VarKeyScalar8IndexHandle handle;
    private readonly bool ownsSession;
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
    /// Gets the maximum raw key length accepted by this wrapper.<br/>
    /// The value is an API-level guard over the current routed `VS8` profile family, not a text or path codec decision.<br/>
    /// </summary>
    public int MaxKeyLength => handle.MaxKeyLength;

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    internal LibraDexFileSession Session => session;

    internal VarKeyScalar8IndexHandle Handle => handle;

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
    /// Inserts one raw-byte key and encoded 8-byte identity into this routed `VS8` index.<br/>
    /// The method opens a short durability batch, applies one routed insert, and commits it so low-friction callers do not need an explicit batch for single writes.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="key">The raw sortable key bytes.</param>
    /// <param name="encodedIdentity">The already encoded sortable 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public VarKeyScalar8InsertOutcome Insert(
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        using VarKeyScalar8Batch batch = BeginBatch();
        VarKeyScalar8InsertOutcome result = batch.Insert(key, encodedIdentity, allowDuplicateKeys);
        VarKeyScalarBatchCommitResult commit = batch.Commit();
        return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
    }

    /// <summary>
    /// Reads encoded 8-byte identities for an inclusive raw-byte key range into caller-owned storage.<br/>
    /// The span-based shape keeps the fixed-identity public API allocation-light while higher-level enumerable or projection APIs remain future layers.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="encodedIdentities">The caller-owned destination span for matching encoded identities.</param>
    /// <returns>The number of identities copied.</returns>
    public int ReadRange(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        Span<ulong> encodedIdentities)
    {
        ThrowIfDisposed();
        return session.ReadVarKeyScalar8IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            lowerKey,
            upperKey,
            encodedIdentities);
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive raw-key range.<br/>
    /// The returned reader exposes each raw key as a temporary <see cref="ReadOnlySpan{T}"/> and each encoded identity as a scalar value while positioned on that row.<br/>
    /// This complements <see cref="ReadRange(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{ulong})"/> for callers that need key iteration, tuple iteration, skip behavior, or explicit materialization control.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>A forward-only reader over matching raw key and encoded identity rows.</returns>
    public VarKeyScalar8RangeReader OpenRangeReader(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.OpenVarKeyScalar8RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            lowerKey,
            upperKey);
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
}

/// <summary>
/// Provides a public raw-byte wrapper over one routed `VS16` index: varlen key bytes and one encoded 16-byte scalar identity.<br/>
/// This is the 16-byte identity counterpart to <see cref="VarKeyScalar8Index"/> and keeps the same low-friction raw key API.<br/>
/// </summary>
public sealed class VarKeyScalar16Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly VarKeyScalar16IndexHandle handle;
    private readonly bool ownsSession;
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
    /// Gets the maximum raw key length accepted by this wrapper.<br/>
    /// The value is an API-level guard over the current routed `VS16` profile family, not a text or path codec decision.<br/>
    /// </summary>
    public int MaxKeyLength => handle.MaxKeyLength;

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

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
    /// Inserts one raw-byte key and encoded 16-byte identity into this routed `VS16` index.<br/>
    /// The high and low identity halves are compared in byte-ordinal scalar order by the underlying fixed-identity shelf.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="key">The raw sortable key bytes.</param>
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
        using VarKeyScalar16Batch batch = BeginBatch();
        VarKeyScalar16InsertOutcome result = batch.Insert(key, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
        VarKeyScalarBatchCommitResult commit = batch.Commit();
        return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
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
        return session.ReadVarKeyScalar16IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            lowerKey,
            upperKey,
            encodedIdentityHighs,
            encodedIdentityLows);
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
        return session.OpenVarKeyScalar16RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            lowerKey,
            upperKey);
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
}

/// <summary>
/// Batches raw-byte `VS8` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
public sealed class VarKeyScalar8Batch : IDisposable
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
    /// Inserts one raw-byte key and encoded 8-byte identity into the owning `VS8` index without forcing a durable commit per item.<br/>
    /// The first insert for an unset root-prefix route creates the prefix shelf inside this batch before using the normal routed insert path.<br/>
    /// </summary>
    /// <param name="key">The raw sortable key bytes.</param>
    /// <param name="encodedIdentity">The already encoded sortable 8-byte identity.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    public VarKeyScalar8InsertOutcome Insert(ReadOnlySpan<byte> key, ulong encodedIdentity, bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        ValidateKeyLength(key, index.MaxKeyLength);
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(key[0]);
        VarKeyScalar8RoutedInsertResult result = index.Session.InsertWalkedRoutedVarKeyScalar8(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            key,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 8);

        VarKeyScalar8InsertOutcome publicResult = VarKeyScalar8InsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
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
        (DataKernelCommitTelemetry commit, long deferredRequests) = durabilityBatch.Commit();
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

        _ = index.Session.CreateVarKeyScalar8ShelfAndLinkRootRoute(index.Handle.RootRouterOffset, rootPrefix, VarKeyScalar8Profile.DefaultInitial);
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
}

/// <summary>
/// Batches raw-byte `VS16` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
public sealed class VarKeyScalar16Batch : IDisposable
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
        ValidateKeyLength(key, index.MaxKeyLength);
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(key[0]);
        VarKeyScalar16RoutedInsertResult result = index.Session.InsertWalkedRoutedVarKeyScalar16(
            index.Handle.RootRouterOffset,
            index.Handle.MaxKeyLength,
            key,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys,
            maxRouterHops: 8);

        VarKeyScalar16InsertOutcome publicResult = VarKeyScalar16InsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
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
        (DataKernelCommitTelemetry commit, long deferredRequests) = durabilityBatch.Commit();
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

        _ = index.Session.CreateVarKeyScalar16ShelfAndLinkRootRoute(index.Handle.RootRouterOffset, rootPrefix, VarKeyScalar16Profile.DefaultInitial);
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
}

/// <summary>
/// Describes one public raw-byte `VS8` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, or transformed a full shelf into a deeper router.<br/>
/// </summary>
public readonly record struct VarKeyScalar8InsertOutcome(
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
public readonly record struct VarKeyScalar16InsertOutcome(
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
public readonly record struct VarKeyScalarBatchCommitResult(
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
public readonly record struct VarKeyScalarBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);
