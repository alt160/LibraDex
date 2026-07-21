using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Provides the first durable `FixedNScalar8` index handle over one programmable fixed-key shelf.<br/>
/// The handle is intentionally narrow: it proves the fixed slot-array storage path for BigInt keys before routed FSN split logic is attached.<br/>
/// </summary>
internal sealed class FixedNScalar8Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly FixedNScalar8IndexHandle handle;
    private readonly int slotIndex;
    private bool disposed;

    internal FixedNScalar8Index(LibraDexFileSession session, FixedNScalar8IndexHandle handle, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        handle.Validate();
        this.session = session;
        this.handle = handle;
        this.slotIndex = slotIndex;
    }

    /// <summary>
    /// Starts a session durability batch for a fixed-N facade that owns this index.<br/>
    /// The index keeps the session internal while allowing the public BigInt batch adapter to share the normal batch publication boundary.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.<br/></param>
    /// <returns>A durability batch owned by the session.</returns>
    internal LibraDexFileSessionDurabilityBatch BeginDurabilityBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        return session.BeginDurabilityBatch(writeIntent);
    }

    /// <summary>
    /// Adds one encoded fixed-width key and scalar-8 identity to the shelf.<br/>
    /// Ordinary same-shelf writes publish only the shelf bytes; physical counts are read from fixed-N shelf metadata instead of the index directory.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.</param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The insert outcome and commit telemetry.</returns>
    public (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) Insert(
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        ThrowIfDisposed();
        byte[] ownedKey = key.ToArray();
        if (!handle.IsRouted)
        {
            return session.RunFixedNMutation(() => InsertCore(ownedKey, encodedIdentity, allowDuplicateKeys));
        }

        if (TryInsertForWriteContext(key, encodedIdentity, allowDuplicateKeys, out (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) stagedResult))
        {
            return stagedResult;
        }

        return handle.IsRouted
            ? session.RunFixedNScalar8TopologyMutation(handle.RootOffset, () => InsertCore(ownedKey, encodedIdentity, allowDuplicateKeys))
            : session.RunFixedNMutation(() => InsertCore(ownedKey, encodedIdentity, allowDuplicateKeys));
    }

    private (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) InsertCore(
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        if (handle.IsRouted)
        {
            return session.InsertRoutedFixedNScalar8RootNoSplit(slotIndex, handle, key, encodedIdentity, allowDuplicateKeys);
        }

        byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytes(handle);
        FixedNScalar8 shelf = new(shelfBytes, handle.Profile);
        FixedNScalarInsertResult result = shelf.Insert(key, encodedIdentity, allowDuplicateKeys);
        if (result != FixedNScalarInsertResult.Inserted)
        {
            return (result, default);
        }

        DataKernelCommitTelemetry commit = session.RewriteFixedNScalar8Shelf(slotIndex, handle, shelfBytes);
        return (result, commit);
    }

    /// <summary>
    /// Stages one fixed-N scalar-8 insert into an active caller-owned durability batch.<br/>
    /// The first insert for a touched shelf reads and claims that shelf; later same-shelf inserts reuse the staged image so batch commit rewrites the shelf once.<br/>
    /// </summary>
    /// <param name="durabilityBatch">The active durability batch that owns publication.<br/></param>
    /// <param name="key">The encoded fixed-width key bytes.<br/></param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.<br/></param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.<br/></param>
    /// <returns>The structural insert result and whether the batch path handled the route.<br/></returns>
    internal (bool Handled, FixedNScalarInsertResult Result) InsertForDurabilityBatch(
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(durabilityBatch);
        LibraDexWriteContext writeContext = durabilityBatch.WriteContext;
        if (handle.IsRouted)
        {
            (bool handled, FixedNScalarInsertResult routedResult) = session.InsertRoutedFixedNScalar8NoSplitForWriteContext(
                writeContext,
                handle,
                key,
                encodedIdentity,
                allowDuplicateKeys);

            return (handled, routedResult);
        }

        LibraDexFileSession.RecordFixedNScalar8RootShelfClaimForWriteContext(writeContext, handle.RootOffset, handle.Profile);
        byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytesForWriteContext(writeContext, handle.RootOffset, handle.Profile);
        FixedNScalar8 shelf = new(shelfBytes, handle.Profile);
        FixedNScalarInsertResult result = shelf.Insert(key, encodedIdentity, allowDuplicateKeys);
        if (result == FixedNScalarInsertResult.Full)
        {
            return (false, default);
        }

        if (result == FixedNScalarInsertResult.Inserted)
        {
            session.StageFixedNScalar8ShelfRewriteForWriteContext(writeContext, handle.RootOffset, handle.Profile, shelfBytes);
        }

        return (true, result);
    }

    /// <summary>
    /// Stages one fixed-N scalar-8 exact delete into an active caller-owned durability batch.<br/>
    /// The delete mutates the batch-local shelf image and records a deferred item-count delta only when the tuple was present.<br/>
    /// </summary>
    /// <param name="durabilityBatch">The active durability batch that owns publication.<br/></param>
    /// <param name="key">The encoded fixed-width key bytes.<br/></param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.<br/></param>
    /// <returns>Whether the batch path handled the route and whether the tuple was deleted.<br/></returns>
    internal (bool Handled, bool Deleted) DeleteForDurabilityBatch(
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        ReadOnlySpan<byte> key,
        ulong encodedIdentity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(durabilityBatch);
        LibraDexWriteContext writeContext = durabilityBatch.WriteContext;
        bool deleted;
        if (handle.IsRouted)
        {
            deleted = session.DeleteRoutedFixedNScalar8ExactForWriteContext(writeContext, handle, key, encodedIdentity);
        }
        else
        {
            LibraDexFileSession.RecordFixedNScalar8RootShelfClaimForWriteContext(writeContext, handle.RootOffset, handle.Profile);
            byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytesForWriteContext(writeContext, handle.RootOffset, handle.Profile);
            FixedNScalar8 shelf = new(shelfBytes, handle.Profile);
            deleted = shelf.Delete(key, encodedIdentity);
            if (deleted)
            {
                session.StageFixedNScalar8ShelfRewriteForWriteContext(writeContext, handle.RootOffset, handle.Profile, shelfBytes);
            }
        }

        return (true, deleted);
    }

    /// <summary>
    /// Deletes one exact encoded fixed-width key and scalar-8 identity tuple from this index.<br/>
    /// Routed indexes rewrite only the selected leaf shelf and do not merge or remove empty routes in this first fixed-N mutation slice.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.<br/></param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.<br/></param>
    /// <returns>The delete result and commit telemetry.</returns>
    public (bool Deleted, DataKernelCommitTelemetry Commit) Delete(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        ThrowIfDisposed();
        byte[] ownedKey = key.ToArray();
        if (!handle.IsRouted)
        {
            return session.RunFixedNMutation(() => DeleteCore(ownedKey, encodedIdentity));
        }

        if (TryDeleteForWriteContext(key, encodedIdentity, out (bool Deleted, DataKernelCommitTelemetry Commit) stagedResult))
        {
            return stagedResult;
        }

        return handle.IsRouted
            ? session.RunFixedNScalar8TopologyMutation(handle.RootOffset, () => DeleteCore(ownedKey, encodedIdentity))
            : session.RunFixedNMutation(() => DeleteCore(ownedKey, encodedIdentity));
    }

    /// <summary>
    /// Attempts an `FSN-8` insert through shelf-local writer-context staging.<br/>
    /// The method returns <see langword="false"/> only for topology-changing cases such as route creation or full-shelf split; same-shelf conflicts spin and retry so callers keep a no-ceremony write API.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.<br/></param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.<br/></param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.<br/></param>
    /// <param name="result">The completed insert result when writer-context staging handled the operation.<br/></param>
    /// <returns><see langword="true"/> when writer-context staging completed the operation; otherwise <see langword="false"/> for serialized fallback.<br/></returns>
    private bool TryInsertForWriteContext(
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) result)
    {
        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginFixedNScalar8WriteContext();
            bool contextActive = true;
            try
            {
                FixedNScalarInsertResult insertResult;
                if (handle.IsRouted)
                {
                    (bool handled, FixedNScalarInsertResult routedInsertResult) = session.InsertRoutedFixedNScalar8NoSplitForWriteContext(
                        writeContext,
                        handle,
                        key,
                        encodedIdentity,
                        allowDuplicateKeys);
                    if (!handled)
                    {
                        session.AbortFixedNScalar8WriteContext(writeContext);
                        result = default;
                        return false;
                    }

                    insertResult = routedInsertResult;
                }
                else
                {
                    LibraDexFileSession.RecordFixedNScalar8RootShelfClaimForWriteContext(writeContext, handle.RootOffset, handle.Profile);
                    byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytesForWriteContext(writeContext, handle.RootOffset, handle.Profile);
                    FixedNScalar8 shelf = new(shelfBytes, handle.Profile);
                    insertResult = shelf.Insert(key, encodedIdentity, allowDuplicateKeys);
                    if (insertResult == FixedNScalarInsertResult.Full)
                    {
                        session.AbortFixedNScalar8WriteContext(writeContext);
                        result = default;
                        return false;
                    }

                    if (insertResult == FixedNScalarInsertResult.Inserted)
                    {
                        session.StageFixedNScalar8ShelfRewriteForWriteContext(writeContext, handle.RootOffset, handle.Profile, shelfBytes);
                    }
                }

                if (insertResult != FixedNScalarInsertResult.Inserted)
                {
                    session.AbortFixedNScalar8WriteContext(writeContext);
                    result = (insertResult, default);
                    return true;
                }

                contextActive = false;
                DataKernelCommitTelemetry commit = session.PublishFixedNScalar8WriteContext(writeContext, handle.Profile);
                result = (insertResult, commit);
                return true;
            }
            catch (LibraDexWriteContextFixedNScalar8ShelfOwnershipException ex)
            {
                if (contextActive)
                {
                    session.AbortFixedNScalar8WriteContext(writeContext);
                }

                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                if (contextActive)
                {
                    session.AbortFixedNScalar8WriteContext(writeContext);
                }

                result = default;
                return false;
            }
        }
    }

    /// <summary>
    /// Attempts an `FSN-8` exact delete through shelf-local writer-context staging.<br/>
    /// Same-shelf conflicts spin and retry; only unsupported topology work falls back to the serialized mutation path.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.<br/></param>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity.<br/></param>
    /// <param name="result">The completed delete result when writer-context staging handled the operation.<br/></param>
    /// <returns><see langword="true"/> when writer-context staging completed the operation; otherwise <see langword="false"/> for serialized fallback.<br/></returns>
    private bool TryDeleteForWriteContext(
        ReadOnlySpan<byte> key,
        ulong encodedIdentity,
        out (bool Deleted, DataKernelCommitTelemetry Commit) result)
    {
        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginFixedNScalar8WriteContext();
            bool contextActive = true;
            try
            {
                bool deleted;
                if (handle.IsRouted)
                {
                    deleted = session.DeleteRoutedFixedNScalar8ExactForWriteContext(writeContext, handle, key, encodedIdentity);
                }
                else
                {
                    LibraDexFileSession.RecordFixedNScalar8RootShelfClaimForWriteContext(writeContext, handle.RootOffset, handle.Profile);
                    byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytesForWriteContext(writeContext, handle.RootOffset, handle.Profile);
                    FixedNScalar8 shelf = new(shelfBytes, handle.Profile);
                    deleted = shelf.Delete(key, encodedIdentity);
                    if (deleted)
                    {
                        session.StageFixedNScalar8ShelfRewriteForWriteContext(writeContext, handle.RootOffset, handle.Profile, shelfBytes);
                    }
                }

                if (!deleted)
                {
                    session.AbortFixedNScalar8WriteContext(writeContext);
                    result = (false, default);
                    return true;
                }

                contextActive = false;
                DataKernelCommitTelemetry commit = session.PublishFixedNScalar8WriteContext(writeContext, handle.Profile);
                result = (true, commit);
                return true;
            }
            catch (LibraDexWriteContextFixedNScalar8ShelfOwnershipException ex)
            {
                if (contextActive)
                {
                    session.AbortFixedNScalar8WriteContext(writeContext);
                }

                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                if (contextActive)
                {
                    session.AbortFixedNScalar8WriteContext(writeContext);
                }

                result = default;
                return false;
            }
        }
    }

    private (bool Deleted, DataKernelCommitTelemetry Commit) DeleteCore(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        if (handle.IsRouted)
        {
            return session.DeleteRoutedFixedNScalar8RootNoMerge(slotIndex, handle, key, encodedIdentity);
        }

        byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytes(handle);
        FixedNScalar8 shelf = new(shelfBytes, handle.Profile);
        if (!shelf.Delete(key, encodedIdentity))
        {
            return (false, default);
        }

        DataKernelCommitTelemetry commit = session.RewriteFixedNScalar8Shelf(slotIndex, handle, shelfBytes);
        return (true, commit);
    }

    /// <summary>
    /// Reads encoded scalar-8 identities whose fixed-width keys are inside the inclusive encoded key range.<br/>
    /// The current bridge reads one shelf and performs shelf-local binary search with no key or identity decoding in the hot comparison path.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded fixed-width key.</param>
    /// <param name="upperKey">The inclusive upper encoded fixed-width key.</param>
    /// <returns>Encoded scalar-8 identities matching the requested key range.</returns>
    public ulong[] ReadIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.ReadRoutedFixedNScalar8IdentityRange(handle, lowerKey, upperKey);
        }

        byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytes(handle);
        FixedNScalar8ReadOnly shelf = new(shelfBytes, handle.Profile);
        ulong[] identities = new ulong[shelf.ItemCount];
        int count = shelf.CopyIdentitiesInKeyRange(lowerKey, upperKey, identities);
        if (count == identities.Length)
        {
            return identities;
        }

        Array.Resize(ref identities, count);
        return identities;
    }

    /// <summary>
    /// Counts ordinary fixed-key tuples owned by this `FSN-8` index.<br/>
    /// Single-shelf indexes read the shelf header count directly; routed indexes visit each reachable leaf shelf once and sum each shelf header count, without using the index-directory item count.<br/>
    /// </summary>
    /// <returns>The ordinary non-null tuple count represented by fixed-key shelves.<br/></returns>
    public long CountOrdinaryIdentities()
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.CountRoutedFixedNScalar8Identities(handle);
        }

        return session.ReadFixedNScalar8ShelfItemCountNarrow(handle.RootOffset, handle.Profile);
    }

    /// <summary>
    /// Counts ordinary fixed-key tuples whose keys are inside an inclusive encoded key range.<br/>
    /// The count path reads only shelf headers and slot offsets plus key bytes needed for range bounds; it does not copy, decode, or validate identity payloads.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded fixed-width key.<br/></param>
    /// <param name="upperKey">The inclusive upper encoded fixed-width key.<br/></param>
    /// <returns>The number of ordinary tuples in the requested encoded key range.<br/></returns>
    public long CountIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.CountRoutedFixedNScalar8IdentityRange(handle, lowerKey, upperKey);
        }

        byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytes(handle);
        FixedNScalar8ReadOnly shelf = new(shelfBytes, handle.Profile);
        return shelf.CountItemsInKeyRange(lowerKey, upperKey);
    }

    /// <summary>
    /// Adds one encoded scalar-8 identity to the scalar null key-state route owned by this fixed-N index slot.<br/>
    /// The route is identity-keyed and therefore avoids the fixed-key shelf/router write path for optional BigInteger values that have no concrete key.<br/>
    /// Duplicate identity writes are treated as no-ops by the underlying key-state route.<br/>
    /// </summary>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity to associate with the null key state.<br/></param>
    /// <returns><see langword="true"/> when the identity was newly inserted into the route.<br/></returns>
    public bool InsertScalarNullIdentity(ulong encodedIdentity)
    {
        ThrowIfDisposed();
        return session.InsertScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedIdentity);
    }

    /// <summary>
    /// Removes one encoded scalar-8 identity from the scalar null key-state route owned by this fixed-N index slot.<br/>
    /// This is the exact-delete counterpart to <see cref="InsertScalarNullIdentity"/> and does not scan fixed-key shelves.<br/>
    /// </summary>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity to remove from the null key state.<br/></param>
    /// <returns><see langword="true"/> when the identity was present and removed.<br/></returns>
    public bool DeleteScalarNullIdentity(ulong encodedIdentity)
    {
        ThrowIfDisposed();
        return session.DeleteScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedIdentity);
    }

    /// <summary>
    /// Tests whether one encoded scalar-8 identity is present in the scalar null key-state route owned by this fixed-N index slot.<br/>
    /// The check uses the compact identity route directly instead of reading ordinary fixed-key tuples.<br/>
    /// </summary>
    /// <param name="encodedIdentity">The encoded sortable scalar-8 identity to test.<br/></param>
    /// <returns><see langword="true"/> when the identity is associated with the null key state.<br/></returns>
    public bool ContainsScalarNullIdentity(ulong encodedIdentity)
    {
        ThrowIfDisposed();
        return session.ContainsScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedIdentity);
    }

    /// <summary>
    /// Reads encoded scalar-8 identities from the scalar null key-state route owned by this fixed-N index slot.<br/>
    /// Promoted terminal routes are followed by the session layer, so callers see the same route contents regardless of inline or promoted storage.<br/>
    /// </summary>
    /// <returns>The encoded identities currently associated with the scalar null key state.<br/></returns>
    public ulong[] ReadScalarNullIdentities()
    {
        ThrowIfDisposed();
        return session.ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Null);
    }

    /// <summary>
    /// Counts encoded scalar-8 identities in the scalar null key-state route owned by this fixed-N index slot.<br/>
    /// The count is read from existing key-state route metadata and does not copy identity values.<br/>
    /// </summary>
    /// <returns>The number of identities currently associated with the scalar null key state.<br/></returns>
    public long CountScalarNullIdentities()
    {
        ThrowIfDisposed();
        return session.CountScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Null);
    }

    /// <summary>
    /// Releases this lightweight handle.<br/>
    /// The owning catalog remains responsible for the underlying session lifetime.<br/>
    /// </summary>
    public void Dispose()
    {
        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(FixedNScalar8Index));
        }
    }
}
