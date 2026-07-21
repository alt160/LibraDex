using System.Threading;

namespace LibraDex;

/// <summary>
/// Provides a public raw-byte wrapper over one routed `VV` index: varlen key bytes and varlen identity bytes.<br/>
/// This surface intentionally stays codec-free so text, blob, and unmanaged projections can be layered later without changing the persisted shelf shape.<br/>
/// </summary>
internal sealed class VarKeyVarIdentityIndex : IDisposable
{
    internal const int DefaultMaxRouterHops = 64;

    private readonly LibraDexFileSession session;
    private readonly bool ownsSession;
    private readonly object singleOperationFallbackSync = new();
    private readonly ReaderWriterLockSlim singleOperationTopologySync = new(LockRecursionPolicy.SupportsRecursion);
    private bool disposed;

    internal VarKeyVarIdentityIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        bool ownsSession)
    {
        this.session = session;
        this.ownsSession = ownsSession;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        MaxPhysicalKeyLength = maxKeyLength;
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
    /// Gets the root router offset used by this routed `VV` index.<br/>
    /// Exposing it keeps early validation transparent while the higher-level API remains intentionally small.<br/>
    /// </summary>
    public long RootRouterOffset { get; }

    /// <summary>
    /// Gets the maximum developer-facing key payload length accepted by this wrapper.<br/>
    /// The routed `VV` storage shape reserves one internal sentinel byte so null, empty, and non-empty variable-length keys remain distinct and sortable.<br/>
    /// </summary>
    public int MaxKeyLength => LibraDexVarLenKeyCodec.GetMaxLogicalLength(MaxPhysicalKeyLength);

    internal int MaxPhysicalKeyLength { get; }

    /// <summary>
    /// Gets the maximum raw identity length accepted by this wrapper.<br/>
    /// The value is an API-level guard over the current routed `VV` profile family, not a text or blob codec decision.<br/>
    /// </summary>
    public int MaxIdentityLength { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the owning session for internal batch implementations.<br/>
    /// This keeps lower-level routed operations available to the companion batch without widening the public surface.<br/>
    /// </summary>
    internal LibraDexFileSession Session => session;

    /// <summary>
    /// Starts a routed `VV` durability batch for bulk mutation.<br/>
    /// Batch inserts use the same routed mutation path as one-shot inserts, but lower-level commit requests are deferred until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A batch object that accepts raw-byte inserts and controls the durability publication boundary.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another batch is already active on the owning session.</exception>
    public VarKeyVarIdentityBatch BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return new VarKeyVarIdentityBatch(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one developer-facing key payload and raw-byte identity into this routed `VV` index.<br/>
    /// The method opens a short durability batch, applies one routed insert, and commits it so low-friction callers do not need an explicit batch for single writes.<br/>
    /// Hot multi-write callers should prefer `BeginBatch` to control commit cadence.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the key or identity length is outside this wrapper's limits.</exception>
    public VarKeyVarIdentityIndexInsertResult Insert(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, MaxPhysicalKeyLength, nameof(key));
        return InsertEncoded(encodedKey, identity, allowDuplicateKeys);
    }

    private VarKeyVarIdentityIndexInsertResult InsertEncoded(
        ReadOnlySpan<byte> encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ValidateEncodedTupleLengths(encodedKey, identity);
        if (!session.IsDurabilityBatchActive &&
            session.FindRouterTarget(RootRouterOffset, LibraDexFileSession.GetVarKeyScalar8Prefix(encodedKey, 0)) != 0)
        {
            while (true)
            {
                singleOperationTopologySync.EnterReadLock();
                try
                {
                    LibraDexWriteContext writeContext = session.BeginVarKeyVarIdentityWriteContext();
                    try
                    {
                        VarKeyVarIdentityRoutedInsertResult writerResult = session.InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
                            writeContext,
                            RootRouterOffset,
                            MaxPhysicalKeyLength,
                            MaxIdentityLength,
                            encodedKey,
                            identity,
                            allowDuplicateKeys,
                            maxRouterHops: DefaultMaxRouterHops);
                        VarKeyVarIdentityIndexInsertResult outcome = VarKeyVarIdentityIndexInsertResult.FromStorage(writerResult);
                        if (!outcome.Inserted)
                        {
                            session.AbortVarKeyVarIdentityWriteContext(writeContext);
                            return outcome;
                        }

                        DataKernelCommitTelemetry writerCommit = session.PublishVarKeyVarIdentityWriteContext(writeContext);
                        return outcome with { Commit = writerCommit };
                    }
                    catch (LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException ex)
                    {
                        session.AbortVarKeyVarIdentityWriteContext(writeContext);
                        session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        session.AbortVarKeyVarIdentityWriteContext(writeContext);
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
                using VarKeyVarIdentityBatch batch = BeginBatch();
                VarKeyVarIdentityIndexInsertResult result = batch.InsertEncoded(encodedKey, identity, allowDuplicateKeys);
                VarKeyVarIdentityBatchCommitResult commit = batch.Commit();
                return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
            }
            finally
            {
                singleOperationTopologySync.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Inserts one nullable developer-facing key payload and raw-byte identity into this routed `VV` index.<br/>
    /// A null key sorts before an empty key, and both sort before non-empty payload keys by means of LibraDex's reserved first-byte sentinel.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes, or null for the null-key sentinel.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The operation-facing insert outcome plus batch commit telemetry.</returns>
    public VarKeyVarIdentityIndexInsertResult Insert(
        byte[]? key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, MaxPhysicalKeyLength, nameof(key));
        return InsertEncoded(encodedKey, identity, allowDuplicateKeys);
    }

    /// <summary>
    /// Inserts one developer-facing key payload and raw-byte identity into this routed `VV` index using the caller's active durability scope.<br/>
    /// This is for catalog/workbench group-batch paths that already own the commit boundary and must avoid nested one-row batches.<br/>
    /// </summary>
    /// <param name="key">The developer-facing key payload bytes.<br/></param>
    /// <param name="identity">The raw identity bytes associated with the key.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.<br/></param>
    /// <returns>The operation-facing insert outcome without committing the active durability scope.</returns>
    internal VarKeyVarIdentityIndexInsertResult InsertInCurrentScope(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, MaxPhysicalKeyLength, nameof(key));
        ValidateEncodedTupleLengths(encodedKey, identity);
        VarKeyVarIdentityRoutedInsertResult result = session.InsertWalkedRoutedVarKeyVarIdentity(
            RootRouterOffset,
            MaxPhysicalKeyLength,
            MaxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops: DefaultMaxRouterHops);
        return VarKeyVarIdentityIndexInsertResult.FromStorage(result);
    }

    /// <summary>
    /// Deletes all tuples whose raw-byte key is inside an inclusive `VV` range.<br/>
    /// The method opens a short durability batch so shelf-local deletes use the same tombstone sidecar and commit-time normalization path as bulk delete workloads.<br/>
    /// This remains internal until a typed logical facade maps adopted conditions onto the raw var-key/var-identity storage shape.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(lowerKey, MaxPhysicalKeyLength, nameof(lowerKey));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(upperKey, MaxPhysicalKeyLength, nameof(upperKey));
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginVarKeyVarIdentityWriteContext();
                try
                {
                    long writerDeleted = session.DeleteVarKeyVarIdentityKeyRangeForWriteContext(
                        writeContext,
                        RootRouterOffset,
                        MaxPhysicalKeyLength,
                        MaxIdentityLength,
                        encodedLowerKey,
                        encodedUpperKey,
                        maxRouterHops: DefaultMaxRouterHops);
                    if (writerDeleted == 0)
                    {
                        session.AbortVarKeyVarIdentityWriteContext(writeContext);
                        return 0;
                    }

                    _ = session.PublishVarKeyVarIdentityWriteContext(writeContext);
                    return writerDeleted;
                }
                catch (LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException ex)
                {
                    session.AbortVarKeyVarIdentityWriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortVarKeyVarIdentityWriteContext(writeContext);
                    break;
                }
            }
        }

        lock (singleOperationFallbackSync)
        {
            using VarKeyVarIdentityBatch batch = BeginBatch();
            long deleted = batch.DeleteRange(encodedLowerKey, encodedUpperKey);
            _ = batch.Commit();
            return deleted;
        }
    }

    /// <summary>
    /// Deletes one exact raw-key and raw-identity tuple from this routed `VV` index.<br/>
    /// The physical delete targets only the matching tuple and leaves other identities for the same key intact.<br/>
    /// This remains internal until a typed logical facade maps adopted conditions onto the raw var-key/var-identity storage shape.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, MaxPhysicalKeyLength, nameof(key));
        ValidateEncodedTupleLengths(encodedKey, identity);
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginVarKeyVarIdentityWriteContext();
                try
                {
                    bool writerDeleted = session.DeleteVarKeyVarIdentityExactTupleForWriteContext(
                        writeContext,
                        RootRouterOffset,
                        MaxPhysicalKeyLength,
                        MaxIdentityLength,
                        encodedKey,
                        identity,
                        maxRouterHops: DefaultMaxRouterHops);
                    if (!writerDeleted)
                    {
                        session.AbortVarKeyVarIdentityWriteContext(writeContext);
                        return false;
                    }

                    _ = session.PublishVarKeyVarIdentityWriteContext(writeContext);
                    return true;
                }
                catch (LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException ex)
                {
                    session.AbortVarKeyVarIdentityWriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortVarKeyVarIdentityWriteContext(writeContext);
                    break;
                }
            }
        }

        lock (singleOperationFallbackSync)
        {
            using VarKeyVarIdentityBatch batch = BeginBatch();
            bool deleted = batch.DeleteExactTuple(encodedKey, identity);
            _ = batch.Commit();
            return deleted;
        }
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive raw-key range.<br/>
    /// The returned reader exposes each raw key and raw identity as temporary <see cref="ReadOnlySpan{T}"/> values while positioned on that row, avoiding `byte[][]` materialization by default.<br/>
    /// Callers own the reader and should dispose it when finished; keys or identities that must outlive the current row can be copied through the reader's copy/materialization methods.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>A forward-only reader over matching raw key/identity rows.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    public VarKeyVarIdentityRangeReader OpenRangeReader(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.OpenVarKeyVarIdentityRangeReader(
            RootRouterOffset,
            MaxPhysicalKeyLength,
            MaxIdentityLength,
            LibraDexVarLenKeyCodec.Encode(lowerKey, MaxPhysicalKeyLength, nameof(lowerKey)),
            LibraDexVarLenKeyCodec.Encode(upperKey, MaxPhysicalKeyLength, nameof(upperKey)),
            maxRouterHops: DefaultMaxRouterHops,
            decodeLogicalKeys: true);
    }

    /// <summary>
    /// Opens a forward-only reader for an inclusive nullable developer-facing key range.<br/>
    /// Use this overload when either range bound must target the null-key sentinel.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key payload, or null for the null-key sentinel.</param>
    /// <param name="upperKey">The inclusive upper key payload, or null for the null-key sentinel.</param>
    /// <returns>A forward-only reader over matching logical key and raw identity rows.</returns>
    public VarKeyVarIdentityRangeReader OpenRangeReader(byte[]? lowerKey, byte[]? upperKey)
    {
        ThrowIfDisposed();
        return session.OpenVarKeyVarIdentityRangeReader(
            RootRouterOffset,
            MaxPhysicalKeyLength,
            MaxIdentityLength,
            LibraDexVarLenKeyCodec.Encode(lowerKey, MaxPhysicalKeyLength, nameof(lowerKey)),
            LibraDexVarLenKeyCodec.Encode(upperKey, MaxPhysicalKeyLength, nameof(upperKey)),
            maxRouterHops: DefaultMaxRouterHops,
            decodeLogicalKeys: true);
    }

    internal VarKeyVarIdentityRangeReader OpenEncodedRangeReader(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.OpenVarKeyVarIdentityRangeReader(
            RootRouterOffset,
            MaxPhysicalKeyLength,
            MaxIdentityLength,
            lowerKey,
            upperKey,
            maxRouterHops: DefaultMaxRouterHops,
            decodeLogicalKeys: false);
    }

    /// <summary>
    /// Counts every ordinary routed `VV` tuple from shelf-local count metadata.<br/>
    /// This bypasses range-reader progression for count-all while preserving routed shelf and terminal duplicate-key handling.<br/>
    /// </summary>
    /// <returns>The ordinary routed tuple count.<br/></returns>
    public long CountOrdinaryIdentities()
    {
        ThrowIfDisposed();
        return session.CountVarKeyVarIdentityIdentities(RootRouterOffset, MaxPhysicalKeyLength, MaxIdentityLength);
    }

    /// <summary>
    /// Counts ordinary routed `VV` tuples whose logical raw keys are inside an inclusive range.<br/>
    /// The logical keys are encoded once at the index boundary, then delegated to the encoded physical count primitive so range predicates share the same key ordering as readers and deletes.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key.<br/></param>
    /// <returns>The number of matching routed `VV` tuples.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    public long CountIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return CountEncodedIdentityRange(
            LibraDexVarLenKeyCodec.Encode(lowerKey, MaxPhysicalKeyLength, nameof(lowerKey)),
            LibraDexVarLenKeyCodec.Encode(upperKey, MaxPhysicalKeyLength, nameof(upperKey)));
    }

    /// <summary>
    /// Counts ordinary routed `VV` tuples whose already encoded physical keys are inside an inclusive range.<br/>
    /// This is the low-level aggregate entry point for codecs that already own logical-to-physical key encoding and should not pay for another encode pass.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded key.<br/></param>
    /// <param name="upperKey">The inclusive upper encoded key.<br/></param>
    /// <returns>The number of matching routed `VV` tuples.<br/></returns>
    internal long CountEncodedIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        return session.CountVarKeyVarIdentityRange(
            RootRouterOffset,
            MaxPhysicalKeyLength,
            MaxIdentityLength,
            lowerKey,
            upperKey);
    }

    /// <summary>
    /// Counts ordinary routed `VV` tuples whose already encoded physical keys start with the supplied encoded prefix.<br/>
    /// Prefix counting stays shape-native so contained route targets can use shelf metadata while boundary or ambiguous targets fall back to exact range counting.<br/>
    /// </summary>
    /// <param name="encodedPrefix">The encoded key prefix to match.<br/></param>
    /// <returns>The number of matching routed `VV` tuples.<br/></returns>
    internal long CountEncodedIdentityPrefix(ReadOnlySpan<byte> encodedPrefix)
    {
        ThrowIfDisposed();
        using VarKeyVarIdentityRangeReader reader = OpenEncodedRangeReader(
            encodedPrefix,
            CreateEncodedPrefixUpperBound(encodedPrefix, MaxPhysicalKeyLength));
        return reader.Count;
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
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    public LibraDexVarKeyBuffer ReadKeyBuffer(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int initialPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using VarKeyVarIdentityRangeReader reader = OpenRangeReader(lowerKey, upperKey);
        LibraDexVarKeyBuffer buffer = new(reader.Count, initialPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentKey);
        }

        return buffer;
    }

    /// <summary>
    /// Reads matching raw identities into a disconnected buffer object owned by the caller.<br/>
    /// The returned buffer stores identities in pooled flat payload storage plus offset/length metadata, avoiding `byte[][]` materialization while allowing array-like access after this method returns.<br/>
    /// Callers must dispose the buffer when done; spans and memory retrieved from it remain valid only while the buffer is alive.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="initialPayloadCapacity">Optional initial payload capacity in bytes for callers that already know the expected result size.</param>
    /// <returns>A disconnected identity buffer owned by the caller.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    public LibraDexVarIdentityBuffer ReadIdentityBuffer(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int initialPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using VarKeyVarIdentityRangeReader reader = OpenRangeReader(lowerKey, upperKey);
        LibraDexVarIdentityBuffer buffer = new(reader.Count, initialPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentIdentity);
        }

        return buffer;
    }

    /// <summary>
    /// Reads matching raw key and raw identity tuples into a disconnected buffer object owned by the caller.<br/>
    /// The returned buffer stores keys and identities in separate pooled flat payload buffers with matched row ordinals, avoiding tuple-shaped `byte[][]` materialization while allowing array-like access after this method returns.<br/>
    /// Callers must dispose the buffer when done; spans and memory retrieved from it remain valid only while the buffer is alive.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="initialKeyPayloadCapacity">Optional initial key payload capacity in bytes for callers that already know the expected result size.</param>
    /// <param name="initialIdentityPayloadCapacity">Optional initial identity payload capacity in bytes for callers that already know the expected result size.</param>
    /// <returns>A disconnected key/identity tuple buffer owned by the caller.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when the range is invalid.</exception>
    public LibraDexVarKeyIdentityBuffer ReadTupleBuffer(
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int initialKeyPayloadCapacity = 0,
        int initialIdentityPayloadCapacity = 0)
    {
        ThrowIfDisposed();
        using VarKeyVarIdentityRangeReader reader = OpenRangeReader(lowerKey, upperKey);
        LibraDexVarKeyIdentityBuffer buffer = new(reader.Count, initialKeyPayloadCapacity, initialIdentityPayloadCapacity);
        while (reader.MoveNext())
        {
            buffer.Add(reader.CurrentKey, reader.CurrentIdentity);
        }

        return buffer;
    }

    /// <summary>
    /// Returns and resets accumulated DataKernel read telemetry for this index's owning session.<br/>
    /// This keeps validation and tuning visible without adding logging or hidden counters to hot index operations.<br/>
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

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VarKeyVarIdentityIndex));
        }
    }

    private void ValidateEncodedTupleLengths(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        if (key.Length <= 0 || key.Length > MaxPhysicalKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VV encoded key length must be from 1 to {MaxPhysicalKeyLength} bytes.");
        }

        if (identity.Length <= 0 || identity.Length > MaxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"VV identity length must be from 1 to {MaxIdentityLength} bytes.");
        }
    }
}

/// <summary>
/// Batches raw-byte `VV` mutations by deferring durability publication until commit.<br/>
/// The batch is a developer-controlled durability cadence for bulk identity-index mutation, with abort implemented as discarding unpublished staged writes.<br/>
/// </summary>
internal sealed class VarKeyVarIdentityBatch : IDisposable
{
    private readonly VarKeyVarIdentityIndex index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private bool completed;

    internal VarKeyVarIdentityBatch(
        VarKeyVarIdentityIndex index,
        LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
    }

    /// <summary>
    /// Inserts one raw-byte tuple into the owning `VV` index without forcing a durable commit per item.<br/>
    /// The mutation uses the same routed storage path as one-shot inserts, but any internal commit requests are folded into the surrounding batch commit.<br/>
    /// </summary>
    /// <param name="key">The raw sortable key bytes.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the batch has already completed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the key or identity length is outside the owning index limits.</exception>
    public VarKeyVarIdentityIndexInsertResult Insert(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, index.MaxPhysicalKeyLength, nameof(key));
        return InsertEncoded(encodedKey, identity, allowDuplicateKeys);
    }

    internal VarKeyVarIdentityIndexInsertResult InsertEncoded(
        ReadOnlySpan<byte> encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfCompleted();
        ValidateEncodedTupleLengths(encodedKey, identity);
        VarKeyVarIdentityRoutedInsertResult result = index.Session.InsertWalkedRoutedVarKeyVarIdentity(
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            index.MaxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops: VarKeyVarIdentityIndex.DefaultMaxRouterHops);

        VarKeyVarIdentityIndexInsertResult publicResult = VarKeyVarIdentityIndexInsertResult.FromStorage(result);
        attemptedInsertCount++;
        if (publicResult.CreatedInitialShelfRoute)
        {
            initialShelfRouteCreateCount++;
        }

        if (publicResult.Inserted)
        {
            insertedCount++;
        }
        else if (publicResult.AlreadyPresent)
        {
            alreadyPresentCount++;
        }
        else if (publicResult.KeyConflict)
        {
            keyConflictCount++;
        }

        return publicResult;
    }

    /// <summary>
    /// Deletes all tuples whose raw-byte key is inside an inclusive `VV` range without forcing a durable commit per range.<br/>
    /// The owning session marks shelf-local tombstones during the batch and normalizes touched slot streams when the batch commits.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfCompleted();
        ValidateEncodedKeyLength(lowerKey);
        ValidateEncodedKeyLength(upperKey);
        return index.Session.DeleteVarKeyVarIdentityKeyRange(
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            index.MaxIdentityLength,
            lowerKey,
            upperKey);
    }

    /// <summary>
    /// Deletes one exact raw-key and raw-identity tuple without forcing a durable commit per tuple.<br/>
    /// The physical delete removes only the exact tuple and leaves other identities for the same key intact.<br/>
    /// </summary>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteExactTuple(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        ThrowIfCompleted();
        ValidateEncodedTupleLengths(key, identity);
        return index.Session.DeleteVarKeyVarIdentityExactTuple(
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            index.MaxIdentityLength,
            key,
            identity);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise all-or-nothing item semantics beyond the staged writes that reach this publish point.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the batch has already completed.</exception>
    public VarKeyVarIdentityBatchCommitResult Commit()
    {
        ThrowIfCompleted();
        completed = true;
        (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
        return new VarKeyVarIdentityBatchCommitResult(
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
    /// This is a revert-to-current-backing-state operation and is intentionally heavier than a normal successful commit path.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the batch has already completed.</exception>
    public VarKeyVarIdentityBatchAbortResult Abort()
    {
        ThrowIfCompleted();
        completed = true;
        long deferredRequests = durabilityBatch.Abort();
        return new VarKeyVarIdentityBatchAbortResult(attemptedInsertCount, deferredRequests);
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

    private void ValidateTupleLengths(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        if (key.Length > index.MaxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VV key payload length must be from 0 to {index.MaxKeyLength} bytes.");
        }

        if (identity.Length <= 0 || identity.Length > index.MaxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"VV identity length must be from 1 to {index.MaxIdentityLength} bytes.");
        }
    }

    private void ValidateEncodedTupleLengths(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        ValidateEncodedKeyLength(key);

        if (identity.Length <= 0 || identity.Length > index.MaxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"VV identity length must be from 1 to {index.MaxIdentityLength} bytes.");
        }
    }

    private void ValidateEncodedKeyLength(ReadOnlySpan<byte> key)
    {
        if (key.Length <= 0 || key.Length > index.MaxPhysicalKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Length, $"VV encoded key length must be from 1 to {index.MaxPhysicalKeyLength} bytes.");
        }
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The VV batch has already completed.");
        }
    }
}

/// <summary>
/// Describes one public raw-byte `VV` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, or transformed a full shelf into a deeper router.<br/>
/// </summary>
/// <param name="Inserted">True when the tuple was inserted.</param>
/// <param name="AlreadyPresent">True when the exact key and identity tuple already existed.</param>
/// <param name="KeyConflict">True when duplicate keys were disallowed and the key already existed with another identity.</param>
/// <param name="CreatedInitialShelfRoute">True when an unset root-prefix route was initialized for this insert.</param>
/// <param name="GrewShelf">True when the insert moved the target shelf to a larger extent class.</param>
/// <param name="SplitShelf">True when the insert transformed a full shelf into a child router and replacement shelves.</param>
/// <param name="ItemCount">The storage-reported item count for the affected shelf operation.</param>
/// <param name="ShelfExtentSize">The storage-reported shelf extent size before or after the affected operation.</param>
/// <param name="RouterDepth">The route depth reached by the insert before any split transform.</param>
/// <param name="Commit">The DataKernel commit telemetry for one-shot inserts; default when the result came from an uncommitted batch insert.</param>
/// <param name="DeferredCommitRequests">The deferred lower-level commit request count for one-shot inserts; zero when the result came from an uncommitted batch insert.</param>
internal readonly record struct VarKeyVarIdentityIndexInsertResult(
    bool Inserted,
    bool AlreadyPresent,
    bool KeyConflict,
    bool CreatedInitialShelfRoute,
    bool GrewShelf,
    bool SplitShelf,
    int ItemCount,
    int ShelfExtentSize,
    ushort RouterDepth,
    DataKernelCommitTelemetry Commit,
    long DeferredCommitRequests)
{
    internal static VarKeyVarIdentityIndexInsertResult FromStorage(VarKeyVarIdentityRoutedInsertResult result)
    {
        return new VarKeyVarIdentityIndexInsertResult(
            result.InsertResult == VarKeyVarIdentityInsertResult.Inserted,
            result.InsertResult == VarKeyVarIdentityInsertResult.AlreadyPresent,
            result.InsertResult == VarKeyVarIdentityInsertResult.KeyConflict,
            result.Kind == VarKeyVarIdentityRoutedInsertKind.WalkedCreatedInitialShelf,
            result.Kind == VarKeyVarIdentityRoutedInsertKind.WalkedGrow,
            result.Kind == VarKeyVarIdentityRoutedInsertKind.WalkedShelfTransformSplit,
            result.ItemCount,
            result.ShelfExtentSize,
            result.RouterDepth,
            default,
            0);
    }
}

/// <summary>
/// Describes a committed public raw-byte `VV` batch.<br/>
/// The counts describe attempted insert outcomes while the commit telemetry describes the folded DataKernel publication boundary.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of insert calls made against the batch.</param>
/// <param name="InsertedCount">The number of tuples inserted by the batch.</param>
/// <param name="AlreadyPresentCount">The number of duplicate no-op tuples found by the batch.</param>
/// <param name="KeyConflictCount">The number of rejected duplicate-key conflicts found by the batch.</param>
/// <param name="InitialShelfRouteCreateCount">The number of root-prefix routes initialized by the batch.</param>
/// <param name="DeferredCommitRequests">The number of lower-level commit requests folded into this batch commit.</param>
/// <param name="Commit">The DataKernel commit telemetry for the batch publication.</param>
internal readonly record struct VarKeyVarIdentityBatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit);

/// <summary>
/// Describes an aborted public raw-byte `VV` batch.<br/>
/// The result exposes how much work was discarded without leaking internal staged-write objects to the public API.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of insert calls made before abort.</param>
/// <param name="DeferredCommitRequests">The number of staged lower-level commit requests discarded by the abort.</param>
internal readonly record struct VarKeyVarIdentityBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);
