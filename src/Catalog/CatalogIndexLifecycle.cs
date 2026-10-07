namespace LibraDex;

public sealed partial class CatalogIndexFactories
{
    /// <summary>
    /// Creates a new typed index by name inside an explicit index set.<br/>
    /// A catalog may contain multiple index sets with separate identity universes.<br/>
    /// Types are validated on open; creation uses the existing CLR type routing without changing storage layout.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type; explicit fixed-width/profile lanes remain available through Define.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type shared by this index's identity universe.<br/></typeparam>
    /// <param name="name">The index name within the set.<br/></param>
    /// <param name="indexSet">The identity universe name; the first created index establishes the set.<br/></param>
    /// <param name="keys">The duplicate-key contract for creation, or legacy metadata fallback on open.<br/></param>
    /// <param name="options">Optional index policy.<br/></param>
    /// <param name="identityLookupMode">The session-local identity lookup policy.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(string name, string indexSet,
        IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit) =>
        IndexSet(indexSet).Create<TKey, TIdentity>(name, keys, options, identityLookupMode);

    /// <summary>
    /// Opens an existing typed index by name inside an explicit index set.<br/>
    /// A catalog may contain multiple index sets with separate identity universes.<br/>
    /// Types are validated on open; creation uses the existing CLR type routing without changing storage layout.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type; explicit fixed-width/profile lanes remain available through Define.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type shared by this index's identity universe.<br/></typeparam>
    /// <param name="name">The index name within the set.<br/></param>
    /// <param name="indexSet">The identity universe name; the index must already exist.<br/></param>
    /// <param name="keys">The duplicate-key contract for creation, or legacy metadata fallback on open.<br/></param>
    /// <param name="options">Optional index policy.<br/></param>
    /// <param name="identityLookupMode">The session-local identity lookup policy.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Open<TKey, TIdentity>(string name, string indexSet,
        IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit) =>
        IndexSet(indexSet).Open<TKey, TIdentity>(name, keys, options, identityLookupMode);

    /// <summary>
    /// Opens an existing or creates a missing typed index by name inside an explicit index set.<br/>
    /// A catalog may contain multiple index sets with separate identity universes.<br/>
    /// Types are validated on open; creation uses the existing CLR type routing without changing storage layout.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type; explicit fixed-width/profile lanes remain available through Define.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type shared by this index's identity universe.<br/></typeparam>
    /// <param name="name">The index name within the set.<br/></param>
    /// <param name="indexSet">The identity universe name; the first created index establishes the set.<br/></param>
    /// <param name="keys">The duplicate-key contract for creation, or legacy metadata fallback on open.<br/></param>
    /// <param name="options">Optional index policy.<br/></param>
    /// <param name="identityLookupMode">The session-local identity lookup policy.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(string name, string indexSet,
        IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit) =>
        IndexSet(indexSet).CreateOrOpen<TKey, TIdentity>(name, keys, options, identityLookupMode);

}

public sealed partial class CatalogIdentityGroupIndexes
{
    /// <summary>
    /// Gets an explicit construction builder for a named index, without creating or opening index storage.<br/>
    /// Use this for fixed widths or key profiles not expressible through the generic lifecycle methods.<br/>
    /// Unlike the indexer, this method may name an index that does not yet exist.<br/>
    /// </summary>
    /// <param name="name">The nonempty index name within this identity universe.<br/></param>
    /// <returns>A configuration builder requiring an explicit lifecycle terminal.<br/></returns>
    public CatalogNamedIndexBuilder Define(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CatalogNamedIndexBuilder(catalog, owner, Name, name);
    }

    /// <summary>
    /// Creates a new typed index by name in this index set.<br/>
    /// An existing index throws instead of being overwritten.<br/>
    /// This delegates to the existing type-routing and lifecycle implementation.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The public identity type.<br/></typeparam>
    /// <param name="name">The index name within this set.<br/></param>
    /// <param name="keys">The duplicate-key creation contract or legacy reopen fallback.<br/></param>
    /// <param name="options">Optional index policy.<br/></param>
    /// <param name="identityLookupMode">The session-local identity lookup policy.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(string name,
        IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit) =>
        Define(name).Create<TKey, TIdentity>(keys, options, identityLookupMode);

    /// <summary>
    /// Opens an existing typed index by name in this index set.<br/>
    /// A missing index throws instead of creating storage.<br/>
    /// This delegates to the existing type-routing and lifecycle implementation.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The public identity type.<br/></typeparam>
    /// <param name="name">The index name within this set.<br/></param>
    /// <param name="keys">The duplicate-key creation contract or legacy reopen fallback.<br/></param>
    /// <param name="options">Optional index policy.<br/></param>
    /// <param name="identityLookupMode">The session-local identity lookup policy.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Open<TKey, TIdentity>(string name,
        IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit) =>
        Define(name).Open<TKey, TIdentity>(keys, options, identityLookupMode);

    /// <summary>
    /// Opens an existing or creates a missing typed index by name in this index set.<br/>
    /// Existing metadata governs reopen; missing indexes use the supplied creation policy.<br/>
    /// This delegates to the existing type-routing and lifecycle implementation.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The public identity type.<br/></typeparam>
    /// <param name="name">The index name within this set.<br/></param>
    /// <param name="keys">The duplicate-key creation contract or legacy reopen fallback.<br/></param>
    /// <param name="options">Optional index policy.<br/></param>
    /// <param name="identityLookupMode">The session-local identity lookup policy.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(string name,
        IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit) =>
        Define(name).CreateOrOpen<TKey, TIdentity>(keys, options, identityLookupMode);

}

