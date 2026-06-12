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
    /// Adds one encoded fixed-width key and scalar-8 identity to the shelf.<br/>
    /// The current bridge rewrites the single shelf and updates the catalog item count; routed split handling is the next storage-layer extension.<br/>
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

        DataKernelCommitTelemetry commit = session.RewriteFixedNScalar8Shelf(slotIndex, handle, shelfBytes, shelf.ItemCount);
        return (result, commit);
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
