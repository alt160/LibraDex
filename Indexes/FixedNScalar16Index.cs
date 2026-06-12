using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Provides the first durable `FixedNScalar16` index handle over one programmable fixed-key shelf.<br/>
/// This bridges fixed BigInt keys to 16-byte scalar identities while routed `FSN-16` split behavior remains a later storage slice.<br/>
/// </summary>
internal sealed class FixedNScalar16Index : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly FixedNScalar16IndexHandle handle;
    private readonly int slotIndex;
    private bool disposed;

    internal FixedNScalar16Index(LibraDexFileSession session, FixedNScalar16IndexHandle handle, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        handle.Validate();
        this.session = session;
        this.handle = handle;
        this.slotIndex = slotIndex;
    }

    /// <summary>
    /// Adds one encoded fixed-width key and scalar-16 identity to the shelf.<br/>
    /// The current bridge rewrites the single shelf and updates the catalog item count; routed split handling is reserved for the next `FSN-16` extension.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.</param>
    /// <param name="encodedIdentity">The encoded sortable scalar-16 identity bytes.</param>
    /// <param name="allowDuplicateKeys">True for non-unique index behavior; false for unique key behavior.</param>
    /// <returns>The insert outcome and commit telemetry.</returns>
    public (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) Insert(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> encodedIdentity,
        bool allowDuplicateKeys)
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.InsertRoutedFixedNScalar16RootNoSplit(slotIndex, handle, key, encodedIdentity, allowDuplicateKeys);
        }

        byte[] shelfBytes = session.ReadFixedNScalar16ShelfBytes(handle);
        FixedNScalar16 shelf = new(shelfBytes, handle.Profile);
        FixedNScalarInsertResult result = shelf.Insert(key, encodedIdentity, allowDuplicateKeys);
        if (result != FixedNScalarInsertResult.Inserted)
        {
            return (result, default);
        }

        DataKernelCommitTelemetry commit = session.RewriteFixedNScalar16Shelf(slotIndex, handle, shelfBytes, shelf.ItemCount);
        return (result, commit);
    }

    /// <summary>
    /// Reads encoded scalar-16 identities whose fixed-width keys are inside the inclusive encoded key range.<br/>
    /// The current bridge reads one shelf and performs shelf-local binary search with no key or identity decoding in the hot comparison path.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded fixed-width key.</param>
    /// <param name="upperKey">The inclusive upper encoded fixed-width key.</param>
    /// <returns>Concatenated encoded scalar-16 identities matching the requested key range.</returns>
    public byte[] ReadIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.ReadRoutedFixedNScalar16IdentityRange(handle, lowerKey, upperKey);
        }

        byte[] shelfBytes = session.ReadFixedNScalar16ShelfBytes(handle);
        FixedNScalar16ReadOnly shelf = new(shelfBytes, handle.Profile);
        byte[] identities = new byte[shelf.ItemCount * 16];
        int count = shelf.CopyIdentitiesInKeyRange(lowerKey, upperKey, identities);
        Array.Resize(ref identities, count * 16);
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
            throw new ObjectDisposedException(nameof(FixedNScalar16Index));
        }
    }
}
