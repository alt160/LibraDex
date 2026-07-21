using System.Globalization;
using System.Threading;
using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Provides a public raw-byte wrapper over one routed `SV8` index: one encoded 8-byte scalar key and varlen identity bytes.<br/>
/// This surface intentionally stays codec-free so property values, text identities, path identities, and blob identities can be layered later without changing the persisted shelf shape.<br/>
/// </summary>
internal sealed class Scalar8VarIdentityIndex : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly bool ownsSession;
    private readonly object singleOperationFallbackSync = new();
    private readonly ReaderWriterLockSlim singleOperationTopologySync = new(LockRecursionPolicy.SupportsRecursion);
    private bool disposed;

    internal Scalar8VarIdentityIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        int maxIdentityLength,
        bool ownsSession,
        long readCacheMaxBytes = 0)
    {
        this.session = session;
        this.ownsSession = ownsSession;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        MaxIdentityLength = maxIdentityLength;
        if (readCacheMaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readCacheMaxBytes), readCacheMaxBytes, "The SV8 per-index read-cache limit cannot be negative.");
        }

        ReadCacheMaxBytes = readCacheMaxBytes;
        session.ConfigureScalar8VarIdentityReadCache(rootRouterOffset, readCacheMaxBytes);
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
    /// Gets the session-local immutable-shelf read-cache ceiling for this `SV8` index.<br/>
    /// Zero means no limit; a positive value constrains retained decoded shelf bytes for this physical index only.<br/>
    /// </summary>
    public long ReadCacheMaxBytes { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the initial `SV8` shelf profile for this index backing kind.<br/>
    /// File-backed indexes keep the historical var-identity start size, while memory-backed indexes use a smaller process-level profile for lower retained slack on new routes.<br/>
    /// </summary>
    /// <returns>The initial `SV8` shelf profile used when a root-prefix route is first created.</returns>
    internal Scalar8VarIdentityProfile GetInitialProfile()
    {
        if (BackingKind != DataKernelBackingKind.Memory)
        {
            return Scalar8VarIdentityProfile.DefaultInitial;
        }

        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_SV8_SHELF_KB", 4) switch
        {
            4 => Scalar8VarIdentityProfile.Default4KiB,
            8 => Scalar8VarIdentityProfile.Default8KiB,
            16 => Scalar8VarIdentityProfile.Default16KiB,
            32 => Scalar8VarIdentityProfile.Default32KiB,
            64 => Scalar8VarIdentityProfile.Default64KiB,
            128 => Scalar8VarIdentityProfile.Default128KiB,
            _ => Scalar8VarIdentityProfile.Default4KiB
        };
    }

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
        ValidateIdentityLength(identity);
        if (!session.IsDurabilityBatchActive &&
            session.FindRouterTarget(RootRouterOffset, (byte)(encodedKey >> 56)) != 0)
        {
            while (true)
            {
                singleOperationTopologySync.EnterReadLock();
                try
                {
                    LibraDexWriteContext writeContext = session.BeginScalar8VarIdentityWriteContext();
                    try
                    {
                        Scalar8VarIdentityRoutedInsertResult writerResult = session.InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
                            writeContext,
                            RootRouterOffset,
                            MaxIdentityLength,
                            encodedKey,
                            identity,
                            allowDuplicateKeys,
                            maxRouterHops: 32);
                        Scalar8VarIdentityInsertOutcome outcome = Scalar8VarIdentityInsertOutcome.FromStorage(writerResult, createdInitialShelfRoute: false);
                        if (!outcome.Inserted)
                        {
                            session.AbortScalar8VarIdentityWriteContext(writeContext);
                            return outcome;
                        }

                        DataKernelCommitTelemetry writerCommit = session.PublishScalar8VarIdentityWriteContext(writeContext);
                        return outcome with { Commit = writerCommit };
                    }
                    catch (LibraDexWriteContextScalar8VarIdentityShelfOwnershipException ex)
                    {
                        session.AbortScalar8VarIdentityWriteContext(writeContext);
                        session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        session.AbortScalar8VarIdentityWriteContext(writeContext);
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
                using Scalar8VarIdentityBatch batch = BeginBatch();
                Scalar8VarIdentityInsertOutcome result = batch.Insert(encodedKey, identity, allowDuplicateKeys);
                ScalarVarIdentityBatchCommitResult commit = batch.Commit();
                return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
            }
            finally
            {
                singleOperationTopologySync.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Inserts one encoded `SV8` tuple inside the caller's already-active durability scope.<br/>
    /// This is for catalog-level group batching paths that need to avoid opening a nested durability batch while still using the same routed insert and lazy root-prefix shelf creation behavior as <see cref="Scalar8VarIdentityBatch.Insert"/>.<br/>
    /// The returned outcome intentionally has default commit telemetry because the outer group batch owns publication and commit timing.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
    /// <param name="identity">The raw identity bytes associated with the key.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.<br/></param>
    /// <returns>The per-item raw-byte insert result without commit telemetry.<br/></returns>
    internal Scalar8VarIdentityInsertOutcome InsertInCurrentScope(
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys = true)
    {
        ThrowIfDisposed();
        if (identity.Length <= 0 || identity.Length > MaxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"SV8 identity length must be from 1 to {MaxIdentityLength} bytes.");
        }

        byte rootPrefix = (byte)(encodedKey >> 56);
        bool createdInitialShelfRoute = false;
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            _ = session.CreateScalar8VarIdentityShelfAndLinkRootRoute(RootRouterOffset, rootPrefix, GetInitialProfile());
            createdInitialShelfRoute = true;
        }

        Scalar8VarIdentityRoutedInsertResult result = session.InsertWalkedRoutedScalar8VarIdentity(
            RootRouterOffset,
            MaxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops: 32);

        return Scalar8VarIdentityInsertOutcome.FromStorage(result, createdInitialShelfRoute);
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
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginScalar8VarIdentityWriteContext();
                try
                {
                    long writerDeleted = session.DeleteScalar8VarIdentityKeyRangeForWriteContext(
                        writeContext,
                        RootRouterOffset,
                        MaxIdentityLength,
                        lowerEncodedKey,
                        upperEncodedKey,
                        maxRouterHops: 32);
                    if (writerDeleted == 0)
                    {
                        session.AbortScalar8VarIdentityWriteContext(writeContext);
                        return 0;
                    }

                    _ = session.PublishScalar8VarIdentityWriteContext(writeContext);
                    return writerDeleted;
                }
                catch (LibraDexWriteContextScalar8VarIdentityShelfOwnershipException ex)
                {
                    session.AbortScalar8VarIdentityWriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortScalar8VarIdentityWriteContext(writeContext);
                    break;
                }
            }
        }

        lock (singleOperationFallbackSync)
        {
            using Scalar8VarIdentityBatch batch = BeginBatch();
            long deleted = batch.DeleteRange(lowerEncodedKey, upperEncodedKey);
            _ = batch.Commit();
            return deleted;
        }
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
        ValidateIdentityLength(identity);
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginScalar8VarIdentityWriteContext();
                try
                {
                    bool writerDeleted = session.DeleteScalar8VarIdentityExactTupleForWriteContext(
                        writeContext,
                        RootRouterOffset,
                        MaxIdentityLength,
                        encodedKey,
                        identity,
                        maxRouterHops: 32);
                    if (!writerDeleted)
                    {
                        session.AbortScalar8VarIdentityWriteContext(writeContext);
                        return false;
                    }

                    _ = session.PublishScalar8VarIdentityWriteContext(writeContext);
                    return true;
                }
                catch (LibraDexWriteContextScalar8VarIdentityShelfOwnershipException ex)
                {
                    session.AbortScalar8VarIdentityWriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortScalar8VarIdentityWriteContext(writeContext);
                    break;
                }
            }
        }

        lock (singleOperationFallbackSync)
        {
            using Scalar8VarIdentityBatch batch = BeginBatch();
            bool deleted = batch.DeleteExactTuple(encodedKey, identity);
            _ = batch.Commit();
            return deleted;
        }
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
    /// Counts routed `SV8` identities in an inclusive encoded scalar-key range through the shape-native range-count primitive.<br/>
    /// Broad direct-root ranges can sum fully covered shelves from metadata, while boundary ranges retain reader-count semantics for exact slot bounds.<br/>
    /// </summary>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.<br/></param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.<br/></param>
    /// <returns>The number of matching raw identities.<br/></returns>
    public long CountIdentityRange(ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        ThrowIfDisposed();
        return session.CountScalar8VarIdentityRange(RootRouterOffset, MaxIdentityLength, lowerEncodedKey, upperEncodedKey);
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
    /// Counts every ordinary routed `SV8` identity from shelf-local count metadata.<br/>
    /// This bypasses range-reader progression for count-all while preserving routed shelf and terminal duplicate-key handling.<br/>
    /// </summary>
    /// <returns>The ordinary routed identity count.<br/></returns>
    public long CountOrdinaryIdentities()
    {
        ThrowIfDisposed();
        return session.CountScalar8VarIdentityIdentities(RootRouterOffset, MaxIdentityLength);
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

    /// <summary>
    /// Validates one `SV8` identity payload before a direct no-batch mutation is attempted.<br/>
    /// The batch type has its own private validation helper; this duplicate keeps the low-friction one-shot path from reaching session mutation with invalid identity bytes.<br/>
    /// </summary>
    /// <param name="identity">The raw identity bytes to validate.<br/></param>
    private void ValidateIdentityLength(ReadOnlySpan<byte> identity)
    {
        if (identity.Length <= 0 || identity.Length > MaxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"SV8 identity length must be from 1 to {MaxIdentityLength} bytes.");
        }
    }

    /// <summary>
    /// Parses one memory var-identity shelf-size environment override in KiB.<br/>
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
/// Provides a public raw-byte wrapper over one routed `SV16` index: one encoded 16-byte scalar key and varlen identity bytes.<br/>
/// This is the 16-byte scalar-key counterpart to <see cref="Scalar8VarIdentityIndex"/> and keeps the same raw identity API.<br/>
/// </summary>
internal sealed class Scalar16VarIdentityIndex : IDisposable
{
    internal const int DefaultMaxRouterHops = LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops;

    private readonly LibraDexFileSession session;
    private readonly bool ownsSession;
    private readonly object singleOperationFallbackSync = new();
    private readonly ReaderWriterLockSlim singleOperationTopologySync = new(LockRecursionPolicy.SupportsRecursion);
    private bool disposed;

    internal Scalar16VarIdentityIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        int maxIdentityLength,
        bool ownsSession,
        long readCacheMaxBytes = 0)
    {
        if (readCacheMaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readCacheMaxBytes), readCacheMaxBytes, "The SV16 per-index read-cache limit cannot be negative.");
        }

        this.session = session;
        this.ownsSession = ownsSession;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        MaxIdentityLength = maxIdentityLength;
        ReadCacheMaxBytes = readCacheMaxBytes;
        session.ConfigureScalar16VarIdentityReadCache(rootRouterOffset, readCacheMaxBytes);
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
    /// Gets the runtime immutable-shelf cache ceiling for this `SV16` index.<br/>
    /// Zero means no limit; positive values bound only this index's retained shelf bytes and decoded sidecars inside the owning session.<br/>
    /// </summary>
    public long ReadCacheMaxBytes { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the initial `SV16` shelf profile for this index backing kind.<br/>
    /// File-backed indexes keep the historical var-identity start size, while memory-backed indexes use a smaller process-level profile for lower retained slack on new routes.<br/>
    /// </summary>
    /// <returns>The initial `SV16` shelf profile used when a root-prefix route is first created.</returns>
    internal Scalar16VarIdentityProfile GetInitialProfile()
    {
        if (BackingKind != DataKernelBackingKind.Memory)
        {
            return Scalar16VarIdentityProfile.DefaultInitial;
        }

        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_SV16_SHELF_KB", 8) switch
        {
            4 => Scalar16VarIdentityProfile.Default4KiB,
            8 => Scalar16VarIdentityProfile.Default8KiB,
            16 => Scalar16VarIdentityProfile.Default16KiB,
            32 => Scalar16VarIdentityProfile.Default32KiB,
            64 => Scalar16VarIdentityProfile.Default64KiB,
            128 => Scalar16VarIdentityProfile.Default128KiB,
            _ => Scalar16VarIdentityProfile.Default8KiB
        };
    }

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
        ValidateIdentityLength(identity);
        if (!session.IsDurabilityBatchActive &&
            session.FindRouterTarget(RootRouterOffset, (byte)(encodedKeyHigh >> 56)) != 0)
        {
            while (true)
            {
                singleOperationTopologySync.EnterReadLock();
                try
                {
                    LibraDexWriteContext writeContext = session.BeginScalar16VarIdentityWriteContext();
                    try
                    {
                        Scalar16VarIdentityRoutedInsertResult writerResult = session.InsertWalkedRoutedScalar16VarIdentityNoSplitForWriteContext(
                            writeContext,
                            RootRouterOffset,
                            MaxIdentityLength,
                            encodedKeyHigh,
                            encodedKeyLow,
                            identity,
                            allowDuplicateKeys,
                            maxRouterHops: DefaultMaxRouterHops);
                        Scalar16VarIdentityInsertOutcome outcome = Scalar16VarIdentityInsertOutcome.FromStorage(writerResult, createdInitialShelfRoute: false);
                        if (!outcome.Inserted)
                        {
                            session.AbortScalar16VarIdentityWriteContext(writeContext);
                            return outcome;
                        }

                        DataKernelCommitTelemetry writerCommit = session.PublishScalar16VarIdentityWriteContext(writeContext);
                        return outcome with { Commit = writerCommit };
                    }
                    catch (LibraDexWriteContextScalar16VarIdentityShelfOwnershipException ex)
                    {
                        session.AbortScalar16VarIdentityWriteContext(writeContext);
                        session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        session.AbortScalar16VarIdentityWriteContext(writeContext);
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
                using Scalar16VarIdentityBatch batch = BeginBatch();
                Scalar16VarIdentityInsertOutcome result = batch.Insert(encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys);
                ScalarVarIdentityBatchCommitResult commit = batch.Commit();
                return result with { Commit = commit.Commit, DeferredCommitRequests = commit.DeferredCommitRequests };
            }
            finally
            {
                singleOperationTopologySync.ExitWriteLock();
            }
        }
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
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginScalar16VarIdentityWriteContext();
                try
                {
                    long writerDeleted = session.DeleteScalar16VarIdentityKeyRangeForWriteContext(
                        writeContext,
                        RootRouterOffset,
                        MaxIdentityLength,
                        lowerEncodedKeyHigh,
                        lowerEncodedKeyLow,
                        upperEncodedKeyHigh,
                        upperEncodedKeyLow,
                        maxRouterHops: DefaultMaxRouterHops);
                    if (writerDeleted == 0)
                    {
                        session.AbortScalar16VarIdentityWriteContext(writeContext);
                        return 0;
                    }

                    _ = session.PublishScalar16VarIdentityWriteContext(writeContext);
                    return writerDeleted;
                }
                catch (LibraDexWriteContextScalar16VarIdentityShelfOwnershipException ex)
                {
                    session.AbortScalar16VarIdentityWriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortScalar16VarIdentityWriteContext(writeContext);
                    break;
                }
            }
        }

        lock (singleOperationFallbackSync)
        {
            using Scalar16VarIdentityBatch batch = BeginBatch();
            long deleted = batch.DeleteRange(lowerEncodedKeyHigh, lowerEncodedKeyLow, upperEncodedKeyHigh, upperEncodedKeyLow);
            _ = batch.Commit();
            return deleted;
        }
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
        ValidateIdentityLength(identity);
        if (!session.IsDurabilityBatchActive)
        {
            while (true)
            {
                LibraDexWriteContext writeContext = session.BeginScalar16VarIdentityWriteContext();
                try
                {
                    bool writerDeleted = session.DeleteScalar16VarIdentityExactTupleForWriteContext(
                        writeContext,
                        RootRouterOffset,
                        MaxIdentityLength,
                        encodedKeyHigh,
                        encodedKeyLow,
                        identity,
                        maxRouterHops: DefaultMaxRouterHops);
                    if (!writerDeleted)
                    {
                        session.AbortScalar16VarIdentityWriteContext(writeContext);
                        return false;
                    }

                    _ = session.PublishScalar16VarIdentityWriteContext(writeContext);
                    return true;
                }
                catch (LibraDexWriteContextScalar16VarIdentityShelfOwnershipException ex)
                {
                    session.AbortScalar16VarIdentityWriteContext(writeContext);
                    session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    session.AbortScalar16VarIdentityWriteContext(writeContext);
                    break;
                }
            }
        }

        lock (singleOperationFallbackSync)
        {
            using Scalar16VarIdentityBatch batch = BeginBatch();
            bool deleted = batch.DeleteExactTuple(encodedKeyHigh, encodedKeyLow, identity);
            _ = batch.Commit();
            return deleted;
        }
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
    /// Counts routed `SV16` identities in an inclusive encoded scalar-key range through the shape-native range-count primitive.<br/>
    /// Broad direct-root ranges can sum fully covered shelves from metadata, while boundary ranges retain reader-count semantics for exact slot bounds.<br/>
    /// </summary>
    /// <param name="lowerEncodedKeyHigh">The high 8 bytes of the inclusive lower encoded scalar key.<br/></param>
    /// <param name="lowerEncodedKeyLow">The low 8 bytes of the inclusive lower encoded scalar key.<br/></param>
    /// <param name="upperEncodedKeyHigh">The high 8 bytes of the inclusive upper encoded scalar key.<br/></param>
    /// <param name="upperEncodedKeyLow">The low 8 bytes of the inclusive upper encoded scalar key.<br/></param>
    /// <returns>The number of matching raw identities.<br/></returns>
    public long CountIdentityRange(
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
        ThrowIfDisposed();
        return session.CountScalar16VarIdentityRange(
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
    /// Counts every ordinary routed `SV16` identity from shelf-local count metadata.<br/>
    /// This bypasses range-reader progression for count-all while preserving routed shelf and overflow-chain handling.<br/>
    /// </summary>
    /// <returns>The ordinary routed identity count.<br/></returns>
    public long CountOrdinaryIdentities()
    {
        ThrowIfDisposed();
        return session.CountScalar16VarIdentityIdentities(RootRouterOffset, MaxIdentityLength);
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

    /// <summary>
    /// Validates one raw `SV16` identity length before writer-context or durability-batch mutation.<br/>
    /// Keeping the guard on the wrapper prevents the fast path from depending on lower shelf validation for public argument errors.<br/>
    /// </summary>
    /// <param name="identity">The raw identity bytes provided by the caller.<br/></param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the identity is empty or longer than <see cref="MaxIdentityLength"/>.<br/></exception>
    private void ValidateIdentityLength(ReadOnlySpan<byte> identity)
    {
        if (identity.Length <= 0 || identity.Length > MaxIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), identity.Length, $"SV16 identity length must be from 1 to {MaxIdentityLength} bytes.");
        }
    }

    /// <summary>
    /// Parses one memory var-identity shelf-size environment override in KiB.<br/>
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
/// Batches raw-byte `SV8` mutations by deferring durability publication until commit.<br/>
/// The batch lazily creates root-prefix shelves inside the same durability scope as the insert that first needs them.<br/>
/// </summary>
internal sealed class Scalar8VarIdentityBatch : IDisposable
{
    private const int CoalescedSortMinimumSameKeyRun = 32;

    private readonly Scalar8VarIdentityIndex index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long alreadyPresentCount;
    private long keyConflictCount;
    private long initialShelfRouteCreateCount;
    private byte cachedInitialRootPrefix;
    private bool hasCachedInitialRootPrefix;
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
        return InsertValidated(encodedKey, identity, allowDuplicateKeys);
    }

    /// <summary>
    /// Inserts one already-validated encoded `SV8` tuple into the active batch.<br/>
    /// Callers that validate a whole prepared batch upfront use this helper to avoid repeating argument checks on the hot per-tuple path.<br/>
    /// </summary>
    /// <param name="encodedKey">The already encoded sortable 8-byte scalar key.</param>
    /// <param name="identity">The raw identity bytes associated with the key and already checked against the index maximum.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The per-item raw-byte insert result.</returns>
    private Scalar8VarIdentityInsertOutcome InsertValidated(ulong encodedKey, ReadOnlySpan<byte> identity, bool allowDuplicateKeys)
    {
        bool createdInitialShelfRoute = EnsureInitialShelfRoute(GetRootPrefix(encodedKey));
        Scalar8VarIdentityRoutedInsertResult result = index.Session.InsertWalkedRoutedScalar8VarIdentity(
            index.RootRouterOffset,
            index.MaxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops: 32);

        Scalar8VarIdentityInsertOutcome publicResult = Scalar8VarIdentityInsertOutcome.FromStorage(result, createdInitialShelfRoute);
        Count(publicResult);
        return publicResult;
    }

    /// <summary>
    /// Inserts a prepared set of encoded `SV8` tuples after sorting them by scalar key and raw identity inside this batch.<br/>
    /// This is an explicit bulk-ingest path for interleaved duplicate-heavy input where caller order is not semantically observable and grouped same-key writes avoid repeated mixed-overflow-chain walks.<br/>
    /// The method preserves normal duplicate/no-op/conflict accounting by routing every tuple through <see cref="Insert(ulong, ReadOnlySpan{byte}, bool)"/> after coalescing.<br/>
    /// </summary>
    /// <param name="items">The prepared encoded key plus raw identity tuples to insert.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.<br/></param>
    /// <param name="collectDiagnostics">Whether to collect per-item diagnostic path and routed-kind counters for the returned aggregate result.<br/></param>
    /// <returns>The aggregate insert counts accumulated by this coalesced call.<br/></returns>
    public Scalar8VarIdentityCoalescedInsertResult InsertCoalesced(
        ReadOnlySpan<Scalar8VarIdentityBatchInsert> items,
        bool allowDuplicateKeys = true,
        bool collectDiagnostics = true)
    {
        ThrowIfCompleted();
        if (items.Length == 0)
        {
            return default;
        }

        for (int i = 0; i < items.Length; i++)
        {
            ValidateIdentityLength(items[i].Identity, index.MaxIdentityLength, "SV8");
        }

        int largestSameKeyFrequency = GetLargestSameKeyFrequency(items);
        bool sorted = largestSameKeyFrequency >= CoalescedSortMinimumSameKeyRun;
        Scalar8VarIdentityBatchInsert[]? sortedItems = null;
        ReadOnlySpan<Scalar8VarIdentityBatchInsert> insertItems = items;
        if (sorted)
        {
            sortedItems = items.ToArray();
            Array.Sort(sortedItems, Scalar8VarIdentityBatchInsertComparer.Instance);
            insertItems = sortedItems;
        }
        long attempted = 0;
        long inserted = 0;
        long alreadyPresent = 0;
        long keyConflict = 0;
        long initialShelfRoutes = 0;
        long[]? diagnosticPathCounts = collectDiagnostics ? new long[Enum.GetValues<Scalar8VarIdentityInsertDiagnosticPath>().Length] : null;
        long[]? kindCounts = collectDiagnostics ? new long[Enum.GetValues<Scalar8VarIdentityRoutedInsertKind>().Length] : null;
        for (int i = 0; i < insertItems.Length; i++)
        {
            Scalar8VarIdentityBatchInsert item = insertItems[i];
            Scalar8VarIdentityInsertOutcome outcome = InsertValidated(item.EncodedKey, item.Identity, allowDuplicateKeys);
            attempted++;
            if (outcome.Inserted)
            {
                inserted++;
            }
            else if (outcome.AlreadyPresent)
            {
                alreadyPresent++;
            }
            else if (outcome.KeyConflict)
            {
                keyConflict++;
            }

            if (outcome.CreatedInitialShelfRoute)
            {
                initialShelfRoutes++;
            }

            int diagnosticPath = (int)outcome.DiagnosticPath;
            if (diagnosticPathCounts is not null && (uint)diagnosticPath < (uint)diagnosticPathCounts.Length)
            {
                diagnosticPathCounts[diagnosticPath]++;
            }

            int kind = (int)outcome.Kind;
            if (kindCounts is not null && (uint)kind < (uint)kindCounts.Length)
            {
                kindCounts[kind]++;
            }
        }

        return new Scalar8VarIdentityCoalescedInsertResult(attempted, inserted, alreadyPresent, keyConflict, initialShelfRoutes, diagnosticPathCounts, kindCounts, largestSameKeyFrequency, sorted);
    }

    /// <summary>
    /// Returns the largest same-key frequency in a candidate `SV8` coalesced insert batch without sorting it first.<br/>
    /// Sorting is useful when a batch contains enough duplicate-key locality to trigger compact terminal paths, but small scattered runs can create extra same-depth split pressure without enough same-key payoff.<br/>
    /// </summary>
    /// <param name="items">The caller-supplied batch items.<br/></param>
    /// <returns>The largest number of items with the same encoded scalar key.</returns>
    private static int GetLargestSameKeyFrequency(ReadOnlySpan<Scalar8VarIdentityBatchInsert> items)
    {
        if (items.Length == 0)
        {
            return 0;
        }

        if (items.Length < CoalescedSortMinimumSameKeyRun)
        {
            return items.Length;
        }

        Dictionary<ulong, int> counts = new(capacity: Math.Min(items.Length, 1024));
        int largest = 1;
        for (int i = 0; i < items.Length; i++)
        {
            ulong key = items[i].EncodedKey;
            counts.TryGetValue(key, out int count);
            count++;
            counts[key] = count;
            if (count > largest)
            {
                largest = count;
                if (largest >= CoalescedSortMinimumSameKeyRun)
                {
                    return largest;
                }
            }
        }

        return largest;
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
        (DataKernelCommitTelemetry commit, long deferredRequests, LibraDexBatchStorageDiagnostics storageDiagnostics) = durabilityBatch.Commit();
        return new ScalarVarIdentityBatchCommitResult(
            attemptedInsertCount,
            insertedCount,
            alreadyPresentCount,
            keyConflictCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit,
            storageDiagnostics);
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
        if (hasCachedInitialRootPrefix && cachedInitialRootPrefix == rootPrefix)
        {
            return false;
        }

        if (index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix) != 0)
        {
            cachedInitialRootPrefix = rootPrefix;
            hasCachedInitialRootPrefix = true;
            return false;
        }

        _ = index.Session.CreateScalar8VarIdentityShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, index.GetInitialProfile());
        cachedInitialRootPrefix = rootPrefix;
        hasCachedInitialRootPrefix = true;
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
internal sealed class Scalar16VarIdentityBatch : IDisposable
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
            maxRouterHops: Scalar16VarIdentityIndex.DefaultMaxRouterHops);

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
        (DataKernelCommitTelemetry commit, long deferredRequests, LibraDexBatchStorageDiagnostics storageDiagnostics) = durabilityBatch.Commit();
        return new ScalarVarIdentityBatchCommitResult(
            attemptedInsertCount,
            insertedCount,
            alreadyPresentCount,
            keyConflictCount,
            initialShelfRouteCreateCount,
            deferredRequests,
            commit,
            storageDiagnostics);
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

        _ = index.Session.CreateScalar16VarIdentityShelfAndLinkRootRoute(index.RootRouterOffset, rootPrefix, index.GetInitialProfile());
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
internal readonly record struct Scalar8VarIdentityInsertOutcome(
    bool Inserted,
    bool AlreadyPresent,
    bool KeyConflict,
    Scalar8VarIdentityRoutedInsertKind Kind,
    Scalar8VarIdentityInsertResult InsertResult,
    bool CreatedInitialShelfRoute,
    bool GrewShelf,
    bool SplitShelf,
    bool DuplicateRunOverflow,
    int TargetShelfItemCount,
    int TargetShelfExtentSize,
    ushort TargetRouterDepth,
    Scalar8VarIdentityInsertDiagnosticPath DiagnosticPath,
    long DiagnosticAllocatedBytes,
    DataKernelCommitTelemetry Commit,
    long DeferredCommitRequests)
{
    internal static Scalar8VarIdentityInsertOutcome FromStorage(Scalar8VarIdentityRoutedInsertResult result, bool createdInitialShelfRoute)
    {
        return new Scalar8VarIdentityInsertOutcome(
            result.InsertResult == Scalar8VarIdentityInsertResult.Inserted,
            result.InsertResult == Scalar8VarIdentityInsertResult.AlreadyPresent,
            result.InsertResult == Scalar8VarIdentityInsertResult.KeyConflict,
            result.Kind,
            result.InsertResult,
            createdInitialShelfRoute,
            result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedGrow,
            result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
            result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            result.TargetShelfItemCount,
            result.TargetShelfExtentSize,
            result.TargetRouterDepth,
            result.DiagnosticPath,
            result.DiagnosticAllocatedBytes,
            default,
            0);
    }
}

/// <summary>
/// Represents one prepared encoded `SV8` tuple for coalesced batch insertion.<br/>
/// The identity is owned by the caller for the duration of the batch call and is not retained after insertion completes.<br/>
/// </summary>
/// <param name="EncodedKey">The already encoded sortable 8-byte scalar key.<br/></param>
/// <param name="Identity">The raw identity bytes associated with the key.<br/></param>
internal readonly record struct Scalar8VarIdentityBatchInsert(ulong EncodedKey, byte[] Identity);

/// <summary>
/// Describes the items processed by one explicit coalesced `SV8` batch insertion call.<br/>
/// The owning batch also folds these same outcomes into its normal commit counters.<br/>
/// </summary>
/// <param name="AttemptedInsertCount">The number of prepared tuples routed by the call.<br/></param>
/// <param name="InsertedCount">The number of tuples inserted.<br/></param>
/// <param name="AlreadyPresentCount">The number of duplicate exact tuples reported as already present.<br/></param>
/// <param name="KeyConflictCount">The number of duplicate-key conflicts when duplicate keys were disabled.<br/></param>
/// <param name="InitialShelfRouteCreateCount">The number of lazy root-prefix shelves created while routing the call.<br/></param>
internal readonly record struct Scalar8VarIdentityCoalescedInsertResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long[]? DiagnosticPathCounts,
    long[]? KindCounts,
    int LargestSameKeyFrequency,
    bool Sorted);

/// <summary>
/// Sorts prepared `SV8` batch tuples by encoded scalar key and then raw identity bytes.<br/>
/// The singleton comparer keeps per-comparison work to scalar and span comparison while the caller owns the one sorted tuple array.<br/>
/// </summary>
internal sealed class Scalar8VarIdentityBatchInsertComparer : IComparer<Scalar8VarIdentityBatchInsert>
{
    internal static readonly Scalar8VarIdentityBatchInsertComparer Instance = new();

    /// <summary>
    /// Creates the singleton comparer used by explicit coalesced `SV8` batch inserts.<br/>
    /// </summary>
    private Scalar8VarIdentityBatchInsertComparer()
    {
    }

    /// <summary>
    /// Compares two prepared tuples by encoded scalar key and then raw identity bytes.<br/>
    /// </summary>
    /// <param name="left">The left prepared tuple.<br/></param>
    /// <param name="right">The right prepared tuple.<br/></param>
    /// <returns>A negative, zero, or positive ordering value.</returns>
    public int Compare(Scalar8VarIdentityBatchInsert left, Scalar8VarIdentityBatchInsert right)
    {
        int keyComparison = left.EncodedKey.CompareTo(right.EncodedKey);
        return keyComparison != 0
            ? keyComparison
            : Scalar8VarIdentityLayout.CompareIdentityBytes(left.Identity, right.Identity);
    }
}

/// <summary>
/// Describes one public raw-byte `SV16` insert outcome.<br/>
/// Shape flags expose whether the routed insert stayed in place, created an initial root-prefix shelf, grew a shelf, split a shelf, or appended to a duplicate-run overflow shelf.<br/>
/// </summary>
internal readonly record struct Scalar16VarIdentityInsertOutcome(
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
internal readonly record struct ScalarVarIdentityBatchCommitResult(
    long AttemptedInsertCount,
    long InsertedCount,
    long AlreadyPresentCount,
    long KeyConflictCount,
    long InitialShelfRouteCreateCount,
    long DeferredCommitRequests,
    DataKernelCommitTelemetry Commit,
    LibraDexBatchStorageDiagnostics StorageDiagnostics);

/// <summary>
/// Describes an aborted public raw-byte `SV8` or `SV16` batch.<br/>
/// The result exposes how much work was discarded without leaking internal staged-write objects to the public API.<br/>
/// </summary>
internal readonly record struct ScalarVarIdentityBatchAbortResult(
    long AttemptedInsertCount,
    long DeferredCommitRequests);
