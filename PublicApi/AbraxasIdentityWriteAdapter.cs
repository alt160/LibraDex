namespace LibraDex;

/// <summary>
/// Provides the first narrow LibraDex boundary intended for Abraxas write/indexing integration.<br/>
/// The adapter binds one catalog identity group and creates queued writers for named indexes in that group.<br/>
/// It indexes caller-owned identities only; Abraxas or the caller still owns source-object mutation, source durability, catch-up state, and any `EventualIndexed` policy.<br/>
/// </summary>
/// <typeparam name="TIdentity">The caller-owned identity type written through this adapter.</typeparam>
public sealed class AbraxasIdentityWriteAdapter<TIdentity>
{
    private readonly CatalogIdentityGroupIndexes indexes;
    private readonly Dictionary<AdapterWriterKey, object> queuedWriters = [];
    private readonly object queuedWriterSync = new();

    internal AbraxasIdentityWriteAdapter(CatalogIdentityGroupIndexes indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        this.indexes = indexes;
    }

    /// <summary>
    /// Gets the LibraDex identity group bound to this adapter.<br/>
    /// Abraxas indexing intent supplied to this adapter must target this group so index-name resolution remains local and explicit.<br/>
    /// </summary>
    public string Group => indexes.Name;

    /// <summary>
    /// Opens a queued writer for one typed index inside the bound identity group.<br/>
    /// The writer accepts concurrent caller submissions and serializes them inside LibraDex; supported `SS8-8` shelf-local writes use writer-context publication while unsupported route shapes fall back to serialized insertion.<br/>
    /// The current implementation is limited to `SS8-8` physical indexes and fails explicitly for wider key or identity shapes.<br/>
    /// </summary>
    /// <typeparam name="TKey">The typed key accepted by the named index.<br/></typeparam>
    /// <param name="indexName">The index name inside the bound identity group.<br/></param>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this adapter factory.<br/></param>
    /// <returns>A queued writer for the selected typed index.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the named index cannot be opened as the requested typed index.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when the selected index is not currently supported by the queued-writer implementation.<br/></exception>
    public LibraDexQueuedWriter<TKey, TIdentity> For<TKey>(
        string indexName,
        LibraDexConcurrencyOptions? options = null)
    {
        return GetOrCreateQueuedWriter<TKey>(indexName, options);
    }

    /// <summary>
    /// Inserts one typed key and identity through an adapter-owned queued writer for the named index.<br/>
    /// Repeated calls for the same index name and key type reuse the same queued writer, so overlapping one-shot adapter calls share the queue rather than creating independent writer contexts.<br/>
    /// </summary>
    /// <typeparam name="TKey">The typed key accepted by the named index.<br/></typeparam>
    /// <param name="indexName">The index name inside the bound identity group.<br/></param>
    /// <param name="key">The typed key value to index.<br/></param>
    /// <param name="identity">The caller-owned identity value associated with the key.<br/></param>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this adapter method.<br/></param>
    /// <returns>The generic insert result with queued writer path attribution.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the named index cannot be opened as the requested typed index or when the route graph is invalid.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when the selected index is not currently supported by the queued-writer implementation.<br/></exception>
    public LibraDexGenericInsertResult Insert<TKey>(
        string indexName,
        TKey key,
        TIdentity identity,
        LibraDexConcurrencyOptions? options = null)
    {
        LibraDexQueuedWriter<TKey, TIdentity> writer = GetOrCreateQueuedWriter<TKey>(indexName, options);
        return writer.Insert(key, identity);
    }

    /// <summary>
    /// Deletes one typed key and identity through an adapter-owned queued writer for the named index.<br/>
    /// Repeated calls for the same index name and key type reuse the same queued writer, matching the adapter's insert behavior and keeping overlapping delete/insert submissions coordinated inside LibraDex.<br/>
    /// </summary>
    /// <typeparam name="TKey">The typed key accepted by the named index.<br/></typeparam>
    /// <param name="indexName">The index name inside the bound identity group.<br/></param>
    /// <param name="key">The typed key value to remove from the index.<br/></param>
    /// <param name="identity">The caller-owned identity value associated with the key.<br/></param>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this adapter method.<br/></param>
    /// <returns>The generic delete result with queued writer path attribution.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the named index cannot be opened as the requested typed index or when the route graph is invalid.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when the selected index is not currently supported by the queued-writer implementation.<br/></exception>
    public LibraDexGenericDeleteResult Delete<TKey>(
        string indexName,
        TKey key,
        TIdentity identity,
        LibraDexConcurrencyOptions? options = null)
    {
        LibraDexQueuedWriter<TKey, TIdentity> writer = GetOrCreateQueuedWriter<TKey>(indexName, options);
        return writer.Delete(key, identity);
    }

    /// <summary>
    /// Moves one identity from an old key to a new key through an adapter-owned queued writer for the named index.<br/>
    /// The operation is replacement insert followed by old tuple delete, not a database transaction; Abraxas remains responsible for source-object mutation and eventual-indexed failure state.<br/>
    /// </summary>
    /// <typeparam name="TKey">The typed key accepted by the named index.<br/></typeparam>
    /// <param name="indexName">The index name inside the bound identity group.<br/></param>
    /// <param name="identity">The caller-owned identity value to move between keys.<br/></param>
    /// <param name="oldKey">The old typed key value.<br/></param>
    /// <param name="newKey">The replacement typed key value.<br/></param>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this adapter method.<br/></param>
    /// <returns>The generic rekey result with replacement and removal leg diagnostics.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the named index cannot be opened as the requested typed index or when the route graph is invalid.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when the selected index is not currently supported by the queued-writer implementation.<br/></exception>
    public LibraDexGenericRekeyResult Rekey<TKey>(
        string indexName,
        TIdentity identity,
        TKey oldKey,
        TKey newKey,
        LibraDexConcurrencyOptions? options = null)
    {
        LibraDexQueuedWriter<TKey, TIdentity> writer = GetOrCreateQueuedWriter<TKey>(indexName, options);
        return writer.Rekey(identity, oldKey, newKey);
    }

    /// <summary>
    /// Gets an adapter-owned queued writer for the requested named index and key type.<br/>
    /// The cache is intentionally adapter-local so Abraxas can choose the adapter lifetime that matches its indexing lane.<br/>
    /// </summary>
    /// <typeparam name="TKey">The typed key accepted by the named index.<br/></typeparam>
    /// <param name="indexName">The index name inside the bound identity group.<br/></param>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode.<br/></param>
    /// <returns>A cached queued writer for the selected typed index.<br/></returns>
    private LibraDexQueuedWriter<TKey, TIdentity> GetOrCreateQueuedWriter<TKey>(
        string indexName,
        LibraDexConcurrencyOptions? options)
    {
        AdapterWriterKey key = new(indexName, typeof(TKey));
        lock (queuedWriterSync)
        {
            if (queuedWriters.TryGetValue(key, out object? writer))
            {
                return (LibraDexQueuedWriter<TKey, TIdentity>)writer;
            }

            LibraDexIndex<TKey, TIdentity> index = indexes.Index<TKey, TIdentity>(indexName);
            LibraDexQueuedWriter<TKey, TIdentity> created = index.BeginQueuedWriter(options ?? LibraDexConcurrencyOptions.QueuedWriter);
            queuedWriters.Add(key, created);
            return created;
        }
    }

    private readonly record struct AdapterWriterKey(string IndexName, Type KeyType);
}
