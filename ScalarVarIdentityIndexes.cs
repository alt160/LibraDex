namespace LibraDex;

/// <summary>
/// Provides a public raw-byte wrapper over one routed `SV8` index: one encoded 8-byte scalar key and varlen identity bytes.<br/>
/// This surface intentionally stays codec-free so property values, text identities, path identities, and blob identities can be layered later without changing the persisted shelf shape.<br/>
/// </summary>
public sealed class Scalar8VarIdentityIndex : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly bool ownsSession;
    private bool disposed;

    internal Scalar8VarIdentityIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        int maxIdentityLength,
        bool ownsSession)
    {
        this.session = session;
        this.ownsSession = ownsSession;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        MaxIdentityLength = maxIdentityLength;
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
    /// Gets the root router offset used by this routed `SV8` index.<br/>
    /// Exposing it keeps early validation transparent while the higher-level API remains intentionally small.<br/>
    /// </summary>
    public long RootRouterOffset { get; }

    /// <summary>
    /// Gets the maximum raw identity length accepted by this wrapper.<br/>
    /// The value is an API-level guard over the current routed `SV8` profile family, not a text or blob codec decision.<br/>
    /// </summary>
    public int MaxIdentityLength { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    internal LibraDexFileSession Session => session;

    /// <summary>
    /// Starts a routed `SV8` durability batch for bulk mutation.<br/>
    /// Batch inserts use the same routed mutation path as one-shot inserts, but lower-level commit requests are deferred until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A batch object that accepts encoded scalar keys and raw identity bytes.</returns>
    public Scalar8VarIdentityBatch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return new Scalar8VarIdentityBatch(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one encoded 8-byte scalar key and raw identity into this routed `SV8` index.<br/>
    /// The method opens a short durability batch, applies one routed insert, and commits it so low-friction callers do not need an explicit batch for single writes.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public Scalar8VarIdentityInsertOutcome Insert(
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        using Scalar8VarIdentityBatch batch = BeginBatch();
        Scalar8VarIdentityInsertOutcome result = batch.Insert(encodedKey, identity, allowDuplicateKeys);
        ScalarVarIdentityBatchCommitResult commit = batch.Commit();
        return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
    }

    /// <summary>
    /// Deletes all tuples whose encoded scalar key is inside an inclusive `SV8` range.<br/>
    /// The method opens a short durability batch so shelf-local deletes use the same tombstone sidecar and commit-time normalization path as bulk delete workloads.<br/>
    /// This remains internal until a typed logical facade maps adopted conditions onto the raw var-identity storage shape.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        ThrowIfDisposed();
        using Scalar8VarIdentityBatch batch = BeginBatch();
        long deleted = batch.DeleteRange(lowerEncodedKey, upperEncodedKey);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes one exact encoded-key and raw-identity tuple from this routed `SV8` index.<br/>
    /// The delete walks duplicate-key overflow chains when present and removes only the exact identity tuple.<br/>
    /// This remains internal until a typed logical facade maps adopted conditions onto the raw var-identity storage shape.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact encoded scalar key.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        ThrowIfDisposed();
        using Scalar8VarIdentityBatch batch = BeginBatch();
        bool deleted = batch.DeleteExactTuple(encodedKey, identity);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive encoded scalar-key range.<br/>
    /// The returned reader exposes each raw identity as a temporary <see cref="ReadOnlySpan{T}"/> while positioned on that row, avoiding `byte[][]` materialization by default.<br/>
    /// Callers own the reader and should dispose it when finished; identities that must outlive the current row can be copied through the reader's copy/materialization methods.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.</param>
    /// <returns>A forward-only reader over matching raw identities.</returns>
    public Scalar8VarIdentityRangeReader OpenRangeReader(ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        ThrowIfDisposed();
        return session.OpenScalar8VarIdentityRangeReader(RootRouterOffset, MaxIdentityLength, lowerEncodedKey, upperEncodedKey);
    }

    /// <summary>
    /// Reads matching raw identities into a disconnected buffer object owned by the caller.<br/>
    /// The returned buffer stores identities in pooled flat payload storage plus offset/length metadata, avoiding `byte[][]` materialization while allowing array-like access after this method returns.<br/>
    /// Callers must dispose the buffer when done; spans and memory retrieved from it remain valid only while the buffer is alive.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.</param>
    /// <param name="initialPayloadCapacity">Optional initial payload capacity in bytes for callers that already know the expected result size.</param>
    /// <returns>A disconnected identity buffer owned by the caller.</returns>
    public LibraDexVarIdentityBuffer ReadIdentityBuffer(
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        int initialPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using Scalar8VarIdentityRangeReader reader = OpenRangeReader(lowerEncodedKey, upperEncodedKey);
        LibraDexVarIdentityBuffer buffer = new(reader.Count, initialPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentIdentity);
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
            throw new ObjectDisposedException(nameof(Scalar8VarIdentityIndex));
        }
    }
}

/// <summary>
/// Provides a public raw-byte wrapper over one routed `SV16` index: one encoded 16-byte scalar key and varlen identity bytes.<br/>
/// This is the 16-byte scalar-key counterpart to <see cref="Scalar8VarIdentityIndex"/> and keeps the same raw identity API.<br/>
/// </summary>
public sealed class Scalar16VarIdentityIndex : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly bool ownsSession;
    private bool disposed;

    internal Scalar16VarIdentityIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        int maxIdentityLength,
        bool ownsSession)
    {
        this.session = session;
        this.ownsSession = ownsSession;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        MaxIdentityLength = maxIdentityLength;
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
    /// Gets the root router offset used by this routed `SV16` index.<br/>
    /// Exposing it keeps early validation transparent while the higher-level API remains intentionally small.<br/>
    /// </summary>
    public long RootRouterOffset { get; }

    /// <summary>
    /// Gets the maximum raw identity length accepted by this wrapper.<br/>
    /// The value is an API-level guard over the current routed `SV16` profile family, not a text or blob codec decision.<br/>
    /// </summary>
    public int MaxIdentityLength { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    internal LibraDexFileSession Session => session;

    /// <summary>
    /// Starts a routed `SV16` durability batch for bulk mutation.<br/>
    /// Batch inserts use the same routed mutation path as one-shot inserts, but lower-level commit requests are deferred until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A batch object that accepts encoded scalar keys and raw identity bytes.</returns>
    public Scalar16VarIdentityBatch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return new Scalar16VarIdentityBatch(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one encoded 16-byte scalar key and raw identity into this routed `SV16` index.<br/>
    /// The high and low key halves drive the same byte-ordinal route and tuple ordering used by the fixed-width scalar-key shapes.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The high 8 bytes of the encoded sortable 16-byte scalar key.</param>
    /// <param name="encodedKeyLow">The low 8 bytes of the encoded sortable 16-byte scalar key.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public Scalar16VarIdentityInsertOutcome Insert(
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        using Scalar16VarIdentityBatch batch = BeginBatch();
        Scalar16VarIdentityInsertOutcome result = batch.Insert(encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys);
        ScalarVarIdentityBatchCommitResult commit = batch.Commit();
        return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
    }

    /// <summary>
    /// Deletes all tuples whose encoded 16-byte scalar key is inside an inclusive `SV16` range.<br/>
    /// The method opens a short durability batch so shelf-local deletes use the same tombstone sidecar and commit-time normalization path as bulk delete workloads.<br/>
    /// This remains internal until a typed logical facade maps adopted conditions onto the raw var-identity storage shape.<br/>
    /// </summary>
    /// <param name="lowerEncodedKeyHigh">The high 8 bytes of the inclusive lower encoded key.</param>
    /// <param name="lowerEncodedKeyLow">The low 8 bytes of the inclusive lower encoded key.</param>
    /// <param name="upperEncodedKeyHigh">The high 8 bytes of the inclusive upper encoded key.</param>
    /// <param name="upperEncodedKeyLow">The low 8 bytes of the inclusive upper encoded key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
        ThrowIfDisposed();
        using Scalar16VarIdentityBatch batch = BeginBatch();
        long deleted = batch.DeleteRange(lowerEncodedKeyHigh, lowerEncodedKeyLow, upperEncodedKeyHigh, upperEncodedKeyLow);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes one exact encoded-key and raw-identity tuple from this routed `SV16` index.<br/>
    /// The delete walks duplicate-key overflow chains when present and removes only the exact identity tuple.<br/>
    /// This remains internal until a typed logical facade maps adopted conditions onto the raw var-identity storage shape.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The high 8 bytes of the exact encoded key.</param>
    /// <param name="encodedKeyLow">The low 8 bytes of the exact encoded key.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity)
    {
        ThrowIfDisposed();
        using Scalar16VarIdentityBatch batch = BeginBatch();
        bool deleted = batch.DeleteExactTuple(encodedKeyHigh, encodedKeyLow, identity);
        _ = batch.Commit();
        return deleted;
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive encoded 16-byte scalar-key range.<br/>
    /// The returned reader exposes each raw identity as a temporary <see cref="ReadOnlySpan{T}"/> while positioned on that row, avoiding `byte[][]` materialization by default.<br/>
    /// Callers own the reader and should dispose it when finished; identities that must outlive the current row can be copied through the reader's copy/materialization methods.<br/>
    /// </summary>
    /// <param name="lowerEncodedKeyHigh">The high 8 bytes of the inclusive lower encoded scalar key.</param>
    /// <param name="lowerEncodedKeyLow">The low 8 bytes of the inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKeyHigh">The high 8 bytes of the inclusive upper encoded scalar key.</param>
    /// <param name="upperEncodedKeyLow">The low 8 bytes of the inclusive upper encoded scalar key.</param>
    /// <returns>A forward-only reader over matching raw identities.</returns>
    public Scalar16VarIdentityRangeReader OpenRangeReader(
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
        ThrowIfDisposed();
        return session.OpenScalar16VarIdentityRangeReader(
            RootRouterOffset,
            MaxIdentityLength,
            lowerEncodedKeyHigh,
            lowerEncodedKeyLow,
            upperEncodedKeyHigh,
            upperEncodedKeyLow);
    }

    /// <summary>
    /// Reads matching raw identities into a disconnected buffer object owned by the caller.<br/>
    /// The returned buffer stores identities in pooled flat payload storage plus offset/length metadata, avoiding `byte[][]` materialization while allowing array-like access after this method returns.<br/>
    /// Callers must dispose the buffer when done; spans and memory retrieved from it remain valid only while the buffer is alive.<br/>
    /// </summary>
    /// <param name="lowerEncodedKeyHigh">The high 8 bytes of the inclusive lower encoded scalar key.</param>
    /// <param name="lowerEncodedKeyLow">The low 8 bytes of the inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKeyHigh">The high 8 bytes of the inclusive upper encoded scalar key.</param>
    /// <param name="upperEncodedKeyLow">The low 8 bytes of the inclusive upper encoded scalar key.</param>
    /// <param name="initialPayloadCapacity">Optional initial payload capacity in bytes for callers that already know the expected result size.</param>
    /// <returns>A disconnected identity buffer owned by the caller.</returns>
    public LibraDexVarIdentityBuffer ReadIdentityBuffer(
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow,
        int initialPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using Scalar16VarIdentityRangeReader reader = OpenRangeReader(
            lowerEncodedKeyHigh,
            lowerEncodedKeyLow,
            upperEncodedKeyHigh,
            upperEncodedKeyLow);
        LibraDexVarIdentityBuffer buffer = new(reader.Count, initialPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentIdentity);
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
            throw new ObjectDisposedException(nameof(Scalar16VarIdentityIndex));
        }
    }
}

/// <summary>
/// Batches raw-byte `SV8` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
public sealed class Scalar8VarIdentityBatch : IDisposable
{
    private readonly Scalar8VarIdentityIndex index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private bool completed;

    internal Scalar8VarIdentityBatch(Scalar8VarIdentityIndex index, LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
    }

    /// <summary>
    /// Inserts one encoded 8-byte scalar key and raw identity into the owning `SV8` index without forcing a durable commit per item.<br/>
    /// The first insert for an unset root-prefix route creates the prefix shelf inside this batch before using the normal routed insert path.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    public Scalar8VarIdentityInsertOutcome Insert(ulong encodedKey, ReadOnlySpan<byte> identity, bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        ValidateIdentityLength(identity, index.MaxIdentityLength, "SV8");
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(GetRootPrefix(encodedKey));
        Scalar8VarIdentityRoutedInsertResult result = index.Session.InsertWalkedRoutedScalar8VarIdentity(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops: 8);

        Scalar8VarIdentityInsertOutcome publicResult = Scalar8VarIdentityInsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
    }

    /// <summary>
    /// Deletes all tuples whose encoded scalar key is inside an inclusive `SV8` range without forcing a durable commit per range.<br/>
    /// The owning session marks shelf-local tombstones during the batch and normalizes touched slot streams when the batch commits.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        ThrowIfCompleted();
        return index.Session.DeleteScalar8VarIdentityKeyRange(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            lowerEncodedKey,
            upperEncodedKey);
    }

    /// <summary>
    /// Deletes one exact encoded-key and raw-identity tuple without forcing a durable commit per tuple.<br/>
    /// The delete walks duplicate-key overflow chains when present and removes only the exact identity tuple.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact encoded scalar key.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        ThrowIfCompleted();
        ValidateIdentityLength(identity, index.MaxIdentityLength, "SV8");
        return index.Session.DeleteScalar8VarIdentityExactTuple(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            encodedKey,
            identity);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise all-or-nothing item semantics beyond the staged writes that reach this publish point.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public ScalarVarIdentityBatchCommitResult Commit()
    {
        ThrowIfCompleted();
        completed = true;
        (DataKernelCommitTelemetry commit, long deferredRequests) = durabilityBatch.Commit();
        return new ScalarVarIdentityBatchCommitResult(
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
    public ScalarVarIdentityBatchAbortResult Abort()
    {
        ThrowIfCompleted();
        completed = true;
        long deferredRequests = durabilityBatch.Abort();
        return new ScalarVarIdentityBatchAbortResult(attemptedInsertCount, deferredRequests);
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
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) != 0)
        {
            return false;
        }

        _ = index.Session.CreateScalar8VarIdentityShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Scalar8VarIdentityProfile.DefaultInitial);
        return true;
    }

    private void Count(Scalar8VarIdentityInsertOutcome result)
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
            throw new InvalidOperationException("The SV8 batch has already completed.");
        }
    }

    private static byte GetRootPrefix(ulong encodedKey)
    {
        return (byte)(encodedKey >> 56);
    }

    private static void ValidateIdentityLength(ReadOnlySpan<byte> identity, int maxIdentityLength, string shapeName)
    {
        if (identity.Length <= 0 || identity.Length > maxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"{shapeName} identity length must be from 1 to {maxIdentityLength} bytes.");
        }
    }
}

/// <summary>
/// Batches raw-byte `SV16` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
public sealed class Scalar16VarIdentityBatch : IDisposable
{
    private readonly Scalar16VarIdentityIndex index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private bool completed;

    internal Scalar16VarIdentityBatch(Scalar16VarIdentityIndex index, LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
    }

    /// <summary>
    /// Inserts one encoded 16-byte scalar key and raw identity into the owning `SV16` index without forcing a durable commit per item.<br/>
    /// The first insert for an unset root-prefix route creates the prefix shelf inside this batch before using the normal routed insert path.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The high 8 bytes of the encoded sortable 16-byte scalar key.</param>
    /// <param name="encodedKeyLow">The low 8 bytes of the encoded sortable 16-byte scalar key.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    public Scalar16VarIdentityInsertOutcome Insert(
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        ValidateIdentityLength(identity, index.MaxIdentityLength, "SV16");
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(GetRootPrefix(encodedKeyHigh));
        Scalar16VarIdentityRoutedInsertResult result = index.Session.InsertWalkedRoutedScalar16VarIdentity(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            encodedKeyHigh,
            encodedKeyLow,
            identity,
            allowDuplicateKeys,
            maxRouterHops: 8);

        Scalar16VarIdentityInsertOutcome publicResult = Scalar16VarIdentityInsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
    }

    /// <summary>
    /// Deletes all tuples whose encoded 16-byte scalar key is inside an inclusive `SV16` range without forcing a durable commit per range.<br/>
    /// The owning session marks shelf-local tombstones during the batch and normalizes touched slot streams when the batch commits.<br/>
    /// </summary>
    /// <param name="lowerEncodedKeyHigh">The high 8 bytes of the inclusive lower encoded key.</param>
    /// <param name="lowerEncodedKeyLow">The low 8 bytes of the inclusive lower encoded key.</param>
    /// <param name="upperEncodedKeyHigh">The high 8 bytes of the inclusive upper encoded key.</param>
    /// <param name="upperEncodedKeyLow">The low 8 bytes of the inclusive upper encoded key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
        ThrowIfCompleted();
        return index.Session.DeleteScalar16VarIdentityKeyRange(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            lowerEncodedKeyHigh,
            lowerEncodedKeyLow,
            upperEncodedKeyHigh,
            upperEncodedKeyLow);
    }

    /// <summary>
    /// Deletes one exact encoded-key and raw-identity tuple without forcing a durable commit per tuple.<br/>
    /// The delete walks duplicate-key overflow chains when present and removes only the exact identity tuple.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The high 8 bytes of the exact encoded key.</param>
    /// <param name="encodedKeyLow">The low 8 bytes of the exact encoded key.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity)
    {
        ThrowIfCompleted();
        ValidateIdentityLength(identity, index.MaxIdentityLength, "SV16");
        return index.Session.DeleteScalar16VarIdentityExactTuple(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            encodedKeyHigh,
            encodedKeyLow,
            identity);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise all-or-nothing item semantics beyond the staged writes that reach this publish point.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public ScalarVarIdentityBatchCommitResult Commit()
    {
        ThrowIfCompleted();
        completed = true;
        (DataKernelCommitTelemetry commit, long deferredRequests) = durabilityBatch.Commit();
        return new ScalarVarIdentityBatchCommitResult(
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
    public ScalarVarIdentityBatchAbortResult Abort()
    {
        ThrowIfCompleted();
        completed = true;
        long deferredRequests = durabilityBatch.Abort();
        return new ScalarVarIdentityBatchAbortResult(attemptedInsertCount, deferredRequests);
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
        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) != 0)
        {
            return false;
        }

        _ = index.Session.CreateScalar16VarIdentityShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, Scalar16VarIdentityProfile.DefaultInitial);
        return true;
    }

    private void Count(Scalar16VarIdentityInsertOutcome result)
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
            throw new InvalidOperationException("The SV16 batch has already completed.");
        }
    }

    private static byte GetRootPrefix(ulong encodedKeyHigh)
    {
        return (byte)(encodedKeyHigh >> 56);
    }

    private static void ValidateIdentityLength(ReadOnlySpan<byte> identity, int maxIdentityLength, string shapeName)
    {
        if (identity.Length <= 0 || identity.Length > maxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"{shapeName} identity length must be from 1 to {maxIdentityLength} bytes.");
        }
    }
}

/// <summary>
/// Describes one public raw-byte `SV8` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, split a shelf, or appended to a duplicate-run overflow shelf.<br/>
/// </summary>
public readonly record struct Scalar8VarIdentityInsertOutcome(
    bool Inserted,
    bool AlreadyPresent,
    bool KeyConflict,
    bool CreatedInitialShelfRoute,
    bool GrewShelf,
    bool SplitShelf,
    bool DuplicateRunOverflow,
    int TargetShelfItemCount,
    int TargetShelfExtentSize,
    ushort TargetRouterDepth,
    DataKernelCommitTelemetry Commit,
    long DeferredCommitRequests)
{
    internal static Scalar8VarIdentityInsertOutcome FromStorage(Scalar8VarIdentityRoutedInsertResult result, bool createdInitialShelfRoute)
    {
        return new Scalar8VarIdentityInsertOutcome(
            result.InsertResult == Scalar8VarIdentityInsertResult.Inserted,
            result.InsertResult == Scalar8VarIdentityInsertResult.AlreadyPresent,
            result.InsertResult == Scalar8VarIdentityInsertResult.KeyConflict,
            createdInitialShelfRoute,
            result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedGrow,
            result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
            result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            result.TargetShelfItemCount,
            result.TargetShelfExtentSize,
            result.TargetRouterDepth,
            default,
            0);
    }
}

/// <summary>
/// Describes one public raw-byte `SV16` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, split a shelf, or appended to a duplicate-run overflow shelf.<br/>
/// </summary>
public readonly record struct Scalar16VarIdentityInsertOutcome(
    bool Inserted,
    bool AlreadyPresent,
    bool KeyConflict,
    bool CreatedInitialShelfRoute,
    bool GrewShelf,
    bool SplitShelf,
    bool DuplicateRunOverflow,
    int TargetShelfItemCount,
    int TargetShelfExtentSize,
    ushort TargetRouterDepth,
    DataKernelCommitTelemetry Commit,
    long DeferredCommitRequests)
{
    internal static Scalar16VarIdentityInsertOutcome FromStorage(Scalar16VarIdentityRoutedInsertResult result, bool createdInitialShelfRoute)
    {
        return new Scalar16VarIdentityInsertOutcome(
            result.InsertResult == Scalar16VarIdentityInsertResult.Inserted,
            result.InsertResult == Scalar16VarIdentityInsertResult.AlreadyPresent,
            result.InsertResult == Scalar16VarIdentityInsertResult.KeyConflict,
            createdInitialShelfRoute,
            result.Kind == Scalar16VarIdentityRoutedInsertKind.WalkedGrow,
            result.Kind == Scalar16VarIdentityRoutedInsertKind.WalkedShelfSplit,
            result.Kind == Scalar16VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            result.TargetShelfItemCount,
            result.TargetShelfExtentSize,
            result.TargetRouterDepth,
            default,
            0);
    }
}

/// <summary>
/// Describes a committed public raw-byte `SV8` or `SV16` batch.<br/>
/// The counts describe attempted insert outcomes while the commit telemetry describes the folded DataKernel publication boundary.<br/>
/// </summary>
public readonly record struct ScalarVarIdentityBatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Describes an aborted public raw-byte `SV8` or `SV16` batch.<br/>
/// The result exposes how much work was discarded without leaking internal staged-write objects to the public API.<br/>
/// </summary>
public readonly record struct ScalarVarIdentityBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);
