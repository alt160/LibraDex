namespace LibraDex;

/// <summary>
/// Provides the first durable `FV` index handle over programmable fixed-width keys and variable-length identities.<br/>
/// The type is internal until the public identity DX is settled; storage behavior can be validated independently through harness paths.<br/>
/// </summary>
internal sealed class FixedNVarIdentityIndex : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly FixedNVarIdentityIndexHandle handle;
    private readonly int slotIndex;
    private bool disposed;

    internal FixedNVarIdentityIndex(LibraDexFileSession session, FixedNVarIdentityIndexHandle handle, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        handle.Validate();
        this.session = session;
        this.handle = handle;
        this.slotIndex = slotIndex;
    }

    /// <summary>
    /// Adds one fixed-width encoded key and raw variable-length identity to the index.<br/>
    /// </summary>
    /// <param name="key">The encoded fixed-width key bytes.</param>
    /// <param name="identity">The raw variable-length identity bytes.</param>
    /// <param name="allowDuplicateKeys">True for non-unique key behavior; false for unique key behavior.</param>
    /// <returns>The insert outcome and commit telemetry.</returns>
    public (FixedNVarIdentityInsertResult Result, DataKernelCommitTelemetry Commit) Insert(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.InsertRoutedFixedNVarIdentityRootNoSplit(slotIndex, handle, key, identity, allowDuplicateKeys);
        }

        byte[] shelfBytes = session.ReadFixedNVarIdentityShelfBytes(handle.RootOffset, handle.Profile);
        FixedNVarIdentityInsertResult result = LibraDex.Views.FixedNVarIdentity.InsertInPlace(shelfBytes, handle.Profile, key, identity, allowDuplicateKeys, out shelfBytes);
        if (result != FixedNVarIdentityInsertResult.Inserted)
        {
            return (result, default);
        }

        DataKernelCommitTelemetry commit = session.RewriteFixedNVarIdentityShelf(slotIndex, handle, shelfBytes);
        return (result, commit);
    }

    /// <summary>
    /// Reads raw variable-length identities whose fixed-width keys are inside the inclusive encoded key range.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded fixed-width key.</param>
    /// <param name="upperKey">The inclusive upper encoded fixed-width key.</param>
    /// <returns>Raw identity byte arrays matching the requested range.</returns>
    public IReadOnlyList<byte[]> ReadIdentityRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        ThrowIfDisposed();
        if (handle.IsRouted)
        {
            return session.ReadRoutedFixedNVarIdentityRange(handle, lowerKey, upperKey);
        }

        byte[] shelfBytes = session.ReadFixedNVarIdentityShelfBytes(handle.RootOffset, handle.Profile);
        LibraDex.Views.FixedNVarIdentityReadOnly shelf = new(shelfBytes, handle.Profile);
        List<byte[]> identities = [];
        shelf.CopyIdentitiesInKeyRange(lowerKey, upperKey, identities);
        return identities;
    }

    public void Dispose()
    {
        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(FixedNVarIdentityIndex));
        }
    }
}
