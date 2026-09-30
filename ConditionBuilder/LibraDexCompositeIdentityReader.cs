namespace LibraDex;

/// <summary>
/// Traverses a routed composite index in natural key order while exposing only its identities.<br/>
/// Key routes are visited as borrowed page locations; no key part is converted to a CLR value during <see cref="Read"/>.<br/>
/// The reader owns its page cursor and must be disposed after use.<br/>
/// </summary>
/// <typeparam name="TIdentity">The exact persisted identity type.<br/></typeparam>
public sealed class LibraDexCompositeIdentityReader<TIdentity> : IDisposable
{
    private readonly LibraDexRoutedCompositeIndex.NativeEntryCursor cursor;
    private bool positioned;
    private bool disposed;

    /// <summary>Opens an unpositioned identity cursor over an already selected composite index.<br/></summary>
    /// <param name="index">The routed composite index to traverse.<br/></param>
    internal LibraDexCompositeIdentityReader(LibraDexRoutedCompositeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.IdentityType != typeof(TIdentity))
            throw new InvalidOperationException($"Composite index '{index.Name}' stores identity type {index.IdentityType.FullName}, not {typeof(TIdentity).FullName}.");
        cursor = index.OpenNativeEntryCursor();
    }

    /// <summary>Gets the current identity after a successful <see cref="Read"/>.<br/>Only identity bytes are decoded; key parts remain unmaterialized.<br/></summary>
    public TIdentity Identity => positioned
        ? cursor.ReadIdentity<TIdentity>()
        : throw new InvalidOperationException("The composite identity reader is not positioned on a row.");

    /// <summary>Advances to the next identity in natural composite key order.<br/>No key part is converted, boxed, or copied into a CLR object.<br/></summary>
    /// <returns><see langword="true"/> when <see cref="Identity"/> is available; otherwise <see langword="false"/>.<br/></returns>
    public bool Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        positioned = cursor.Read();
        return positioned;
    }

    /// <summary>Returns the rented page buffers and invalidates the current identity.<br/></summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        positioned = false;
        cursor.Dispose();
    }
}

/// <summary>Opens identity-only readers for routed composite indexes without requiring callers to declare key-part types.<br/></summary>
public static class LibraDexCompositeIdentityReaderExtensions
{
    /// <summary>Opens a named composite index in natural key order and reads only identities.<br/>The index must exist and have the requested persisted identity type.<br/></summary>
    /// <typeparam name="TIdentity">The exact persisted identity type.<br/></typeparam>
    /// <param name="indexSet">The identity group containing the composite index.<br/></param>
    /// <param name="name">The physical composite index name.<br/></param>
    /// <returns>An unpositioned disposable identity-only reader.<br/></returns>
    public static LibraDexCompositeIdentityReader<TIdentity> OpenCompositeIdentityReader<TIdentity>(
        this CatalogIdentityGroupIndexes indexSet, string name)
    {
        ArgumentNullException.ThrowIfNull(indexSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return indexSet.Index(name) is LibraDexRoutedCompositeIndex index
            ? new LibraDexCompositeIdentityReader<TIdentity>(index)
            : throw new InvalidOperationException($"Index '{name}' in index set '{indexSet.Name}' is not a routed composite index.");
    }
}
