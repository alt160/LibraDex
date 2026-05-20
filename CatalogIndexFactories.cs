using LibraDex.Layouts;
using System.Diagnostics.CodeAnalysis;

namespace LibraDex;

/// <summary>
/// Provides grouped index factories under a catalog.<br/>
/// The grouping is intentionally IntelliSense-friendly: callers choose the key class, then the identity class, then the terminal verb `Create`, `Open`, or `CreateOrOpen`.<br/>
/// </summary>
public sealed class CatalogIndexFactories
{
    private readonly Catalog catalog;

    internal CatalogIndexFactories(Catalog catalog)
    {
        this.catalog = catalog;
        Scalar = new CatalogScalarKeyFactories(catalog);
        Blob = new CatalogBlobKeyFactories(catalog);
        String = new CatalogStringKeyFactories(catalog);
        Guid = new CatalogGuidKeyFactories(catalog);
        Date = new CatalogDateKeyFactories(catalog);
    }

    /// <summary>
    /// Gets the identity-group scoped index factory surface for the supplied group name.<br/>
    /// This is shorthand for `Group(group)` and is the preferred low-friction entry point for grouped catalogs.<br/>
    /// </summary>
    /// <param name="group">The identity group name.</param>
    /// <returns>An identity-group scoped factory surface.</returns>
    public CatalogIdentityGroupIndexes this[string group] => Group(group);

    /// <summary>
    /// Gets the identity-group scoped index factory surface for the supplied group name.<br/>
    /// Identity groups declare that a set of indexes share one identity universe for future multi-criteria composition.<br/>
    /// </summary>
    /// <param name="group">The identity group name.</param>
    /// <returns>An identity-group scoped factory surface.</returns>
    public CatalogIdentityGroupIndexes Group(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return new CatalogIdentityGroupIndexes(catalog, this, group);
    }

    /// <summary>
    /// Gets factories for numeric, char, GUID-as-scalar, and other fixed scalar key indexes.<br/>
    /// </summary>
    public CatalogScalarKeyFactories Scalar { get; }

    /// <summary>
    /// Gets factories for byte-array key indexes.<br/>
    /// Fixed 8-byte, 16-byte, and 32-byte byte-array keys are available now; variable-length blob keys remain a named public lane for the varlen facade.<br/>
    /// </summary>
    public CatalogBlobKeyFactories Blob { get; }

    /// <summary>
    /// Gets factories for string key indexes.<br/>
    /// The profile options capture exact, folded, and sort-key intent before the string-specific physical facade is widened.<br/>
    /// </summary>
    public CatalogStringKeyFactories String { get; }

    /// <summary>
    /// Gets factories for first-class GUID key indexes.<br/>
    /// Exact GUID keys route to the current fixed 16-byte scalar path; segment and text projections are reserved profile intent.<br/>
    /// </summary>
    public CatalogGuidKeyFactories Guid { get; }

    /// <summary>
    /// Gets factories for first-class date/time key indexes.<br/>
    /// Structured date-part projections are reserved profile intent based on the Abraxas-style packed numeric date model.<br/>
    /// </summary>
    public CatalogDateKeyFactories Date { get; }

    /// <summary>
    /// Gets whether the fixed catalog slot currently contains an active index.<br/>
    /// This is the lowest-friction discovery check for callers that own stable slot assignments and want to avoid exception-driven open attempts.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed catalog slot to inspect.</param>
    /// <returns><see langword="true"/> when the slot is active; otherwise <see langword="false"/>.</returns>
    public bool Exists(int slotIndex)
    {
        return TryGetInfo(slotIndex, out _);
    }

    /// <summary>
    /// Gets whether an active index with the supplied name exists.<br/>
    /// Name lookup is metadata-oriented in this slice; slot identity remains the authoritative open/create selector until richer catalog metadata is connected.<br/>
    /// </summary>
    /// <param name="name">The public index name to inspect.</param>
    /// <returns><see langword="true"/> when any active slot has the supplied name; otherwise <see langword="false"/>.</returns>
    public bool Exists(string name)
    {
        return TryGetInfo(name, out _);
    }

    /// <summary>
    /// Tries to get public metadata for an active fixed catalog slot.<br/>
    /// The returned info is a discovery snapshot and should not be treated as a live mutable handle.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed catalog slot to inspect.</param>
    /// <param name="info">Receives index metadata when the slot is active.</param>
    /// <returns><see langword="true"/> when metadata was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetInfo(int slotIndex, out CatalogIndexInfo info)
    {
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            if (activeSlots[i].SlotIndex == slotIndex)
            {
                info = CreateInfo(activeSlots[i]);
                return true;
            }
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Tries to get public metadata for an active index by name.<br/>
    /// If multiple active slots share a name, the first active slot in catalog-directory order is returned; callers that require uniqueness should use slot-based discovery.<br/>
    /// </summary>
    /// <param name="name">The public index name to inspect.</param>
    /// <param name="info">Receives index metadata when a matching active slot is found.</param>
    /// <returns><see langword="true"/> when metadata was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetInfo(string name, out CatalogIndexInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            CatalogIndexInfo candidate = CreateInfo(activeSlots[i]);
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                info = candidate;
                return true;
            }
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Tries to get public metadata for an active index by identity group and index name.<br/>
    /// This is the durable lookup shape behind `catalog.Indexes[group][name]`; it uses rich metadata when present and does not rely on parsing fixed slot names.<br/>
    /// </summary>
    /// <param name="group">The identity group name to inspect.</param>
    /// <param name="name">The public index name inside the identity group.</param>
    /// <param name="info">Receives index metadata when a matching active slot is found.</param>
    /// <returns><see langword="true"/> when metadata was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetInfo(string group, string name, out CatalogIndexInfo info)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            CatalogIndexInfo candidate = CreateInfo(activeSlots[i]);
            if (string.Equals(candidate.Group, group, StringComparison.Ordinal) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                info = candidate;
                return true;
            }
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Lists active index metadata snapshots from the catalog directory.<br/>
    /// The returned array is disconnected from the catalog session so callers can inspect it after additional catalog operations without holding a reader open.<br/>
    /// </summary>
    /// <returns>All active index metadata snapshots in directory order.</returns>
    public CatalogIndexInfo[] List()
    {
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
        CatalogIndexInfo[] infos = new CatalogIndexInfo[activeSlots.Length];
        for (int i = 0; i < activeSlots.Length; i++)
        {
            infos[i] = CreateInfo(activeSlots[i]);
        }

        return infos;
    }

    /// <summary>
    /// Creates an index from a logical shape descriptor.<br/>
    /// This is the programmatic counterpart to the grouped fluent factories: higher-level adapters can build one shape object, then pass it to catalog creation without reconstructing generic factory calls.<br/>
    /// </summary>
    /// <param name="shape">The logical index shape to create.</param>
    /// <param name="slotIndex">Optional fixed directory slot to create.</param>
    /// <param name="options">Optional per-index options override; null uses the contracts embedded in <paramref name="shape"/>.</param>
    /// <returns>A strict non-generic index handle for the created index.</returns>
    public IIndex Create(LibraDexIndexShapeSpec shape, int? slotIndex = null, IndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.KeyFamily == CatalogIndexKeyFamily.Composite)
        {
            throw new NotSupportedException("Composite index shapes are descriptor-only until physical composite-key storage is implemented.");
        }

        if (TryGetInfo(shape.Group, shape.Name, out _))
        {
            throw new InvalidOperationException("The requested LibraDex index shape already exists.");
        }

        return catalog.CreateIndex(shape, ResolveCreateSlot(slotIndex), options);
    }

    /// <summary>
    /// Opens an existing index that matches a logical shape descriptor.<br/>
    /// The shape is used as an expected contract, and persisted catalog metadata is validated before the non-generic handle is returned.<br/>
    /// </summary>
    /// <param name="shape">The expected logical index shape.</param>
    /// <returns>A strict non-generic index handle for the opened index.</returns>
    public IIndex Open(LibraDexIndexShapeSpec shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!TryGetInfo(shape.Group, shape.Name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested LibraDex index shape does not exist.");
        }

        ValidateShape(shape, info);
        return catalog.OpenIndex(info);
    }

    /// <summary>
    /// Opens an existing index matching a logical shape descriptor, or creates it when missing.<br/>
    /// This keeps descriptor-driven setup low-friction for generated code while preserving metadata validation on the open branch.<br/>
    /// </summary>
    /// <param name="shape">The logical index shape to open or create.</param>
    /// <param name="slotIndex">Optional fixed directory slot to create when the shape is missing.</param>
    /// <param name="options">Optional per-index options override for the create branch; null uses the contracts embedded in <paramref name="shape"/>.</param>
    /// <returns>A strict non-generic index handle for the existing or created index.</returns>
    public IIndex CreateOrOpen(LibraDexIndexShapeSpec shape, int? slotIndex = null, IndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return TryGetInfo(shape.Group, shape.Name, out _)
            ? Open(shape)
            : Create(shape, slotIndex, options);
    }

    /// <summary>
    /// Lists identity group names present in rich catalog index metadata.<br/>
    /// Empty groups from older scaffold entries are omitted because they do not represent a deliberate identity-group declaration.<br/>
    /// </summary>
    /// <returns>Distinct identity group names in catalog directory order.</returns>
    public string[] Groups()
    {
        CatalogIndexInfo[] infos = List();
        List<string> groups = new();
        for (int i = 0; i < infos.Length; i++)
        {
            string group = infos[i].Group;
            if (group.Length == 0 || groups.Contains(group, StringComparer.Ordinal))
            {
                continue;
            }

            groups.Add(group);
        }

        return groups.ToArray();
    }

    private static void ValidateShape(LibraDexIndexShapeSpec shape, CatalogIndexInfo info)
    {
        if (!string.Equals(shape.Group, info.Group, StringComparison.Ordinal) ||
            !string.Equals(shape.Name, info.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The requested LibraDex index shape does not match the persisted group/name metadata.");
        }

        ValidateShapeType(shape.KeyType, info.KeyTypeName, "key");
        ValidateShapeType(shape.IdentityType, info.IdentityTypeName, "identity");
        if (info.KeyFamily != CatalogIndexKeyFamily.Unknown && info.KeyFamily != shape.KeyFamily)
        {
            throw new InvalidDataException("The requested LibraDex index shape key family does not match persisted metadata.");
        }

        if (info.IdentityFamily != CatalogIndexIdentityFamily.Unknown && info.IdentityFamily != shape.IdentityFamily)
        {
            throw new InvalidDataException("The requested LibraDex index shape identity family does not match persisted metadata.");
        }

        if (info.KeyContract != shape.KeyContract)
        {
            throw new InvalidDataException("The requested LibraDex index shape key contract does not match persisted metadata.");
        }
    }

    private static void ValidateShapeType(Type requestedType, string persistedTypeName, string role)
    {
        if (string.IsNullOrWhiteSpace(persistedTypeName))
        {
            return;
        }

        Type? persistedType = Type.GetType(persistedTypeName, throwOnError: false);
        if (persistedType is not null && persistedType != requestedType)
        {
            throw new InvalidDataException($"The requested {role} type {requestedType.FullName} does not match persisted LibraDex metadata type {persistedType.FullName}.");
        }
    }

    private CatalogIndexInfo CreateInfo(IndexDirectorySlotSnapshot slot)
    {
        return catalog.Session.TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata)
            ? CatalogIndexInfo.FromSlot(slot, metadata)
            : CatalogIndexInfo.FromSlot(slot);
    }

    internal int ResolveCreateSlot(int? slotIndex)
    {
        if (slotIndex is int requested)
        {
            if ((uint)requested >= IndexDirectoryLayout.SlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), requested, "Index directory slot is outside the fixed directory.");
            }

            return requested;
        }

        bool[] active = new bool[IndexDirectoryLayout.SlotCount];
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            active[activeSlots[i].SlotIndex] = true;
        }

        for (int i = 0; i < active.Length; i++)
        {
            if (!active[i])
            {
                return i;
            }
        }

        throw new InvalidOperationException("The fixed LibraDex index directory has no available slots.");
    }
}

/// <summary>
/// Provides index factories scoped to one identity group.<br/>
/// The group name is persisted into rich index metadata so grouped lookup can be rehydrated after reopening a catalog.<br/>
/// </summary>
public sealed class CatalogIdentityGroupIndexes
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;

    internal CatalogIdentityGroupIndexes(Catalog catalog, CatalogIndexFactories owner, string group)
    {
        this.catalog = catalog;
        this.owner = owner;
        Group = group;
        Batch = catalog.GetIdentityGroupBatch(group);
    }

    /// <summary>
    /// Gets the identity group name represented by this scoped factory surface.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the group-level durability batch manager for indexes that share this identity group.<br/>
    /// When enabled, participating index inserts defer durability through this group manager until `Commit` or `CommitAndDisable` is called.<br/>
    /// </summary>
    public CatalogIdentityGroupBatchManager Batch { get; }

    /// <summary>
    /// Gets a name-first builder for an index inside this identity group.<br/>
    /// </summary>
    /// <param name="name">The index name inside this identity group.</param>
    /// <returns>A name-first index builder.</returns>
    public CatalogNamedIndexBuilder this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return new CatalogNamedIndexBuilder(catalog, owner, Group, name);
        }
    }

    /// <summary>
    /// Lists active indexes that declare this identity group in rich catalog metadata.<br/>
    /// </summary>
    /// <returns>Disconnected metadata snapshots for indexes in this identity group.</returns>
    public CatalogIndexInfo[] List()
    {
        CatalogIndexInfo[] all = owner.List();
        List<CatalogIndexInfo> matches = new();
        for (int i = 0; i < all.Length; i++)
        {
            if (string.Equals(all[i].Group, Group, StringComparison.Ordinal))
            {
                matches.Add(all[i]);
            }
        }

        return matches.ToArray();
    }

    /// <summary>
    /// Tries to get public metadata for an active index inside this identity group.<br/>
    /// </summary>
    /// <param name="name">The index name inside this identity group.</param>
    /// <param name="info">Receives index metadata when a matching active slot is found.</param>
    /// <returns><see langword="true"/> when metadata was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetInfo(string name, out CatalogIndexInfo info)
    {
        return owner.TryGetInfo(Group, name, out info);
    }
}

/// <summary>
/// Provides key-family builders for one named index inside an identity group.<br/>
/// </summary>
public sealed class CatalogNamedIndexBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;

    internal CatalogNamedIndexBuilder(Catalog catalog, CatalogIndexFactories owner, string group, string name)
    {
        this.catalog = catalog;
        this.owner = owner;
        Group = group;
        Name = name;
        Scalar = new CatalogNamedScalarKeyBuilder(catalog, owner, group, name);
        Guid = new CatalogNamedGuidKeyBuilder(catalog, owner, group, name);
        Shape = new CatalogNamedIndexShapeBuilder(group, name);
        Blob = new CatalogUnsupportedIndexFactory("Grouped blob-key indexes need the grouped blob facade before they can be created.");
        String = new CatalogUnsupportedIndexFactory("Grouped string-key indexes need the string-key projection facade before they can be created.");
        Date = new CatalogUnsupportedIndexFactory("Grouped date-key indexes need the structured date codec facade before they can be created.");
    }

    /// <summary>
    /// Gets the identity group name for this named index builder.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the index name for this named index builder.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets scalar-key construction paths for this named index.<br/>
    /// </summary>
    public CatalogNamedScalarKeyBuilder Scalar { get; }

    /// <summary>
    /// Gets GUID-key construction paths for this named index.<br/>
    /// </summary>
    public CatalogNamedGuidKeyBuilder Guid { get; }

    /// <summary>
    /// Gets descriptor-only logical index shape builders for this named index.<br/>
    /// Shape descriptors model Abraxas-style selector/index alignment without creating physical storage yet; the condition-builder port can target these projection specs directly.<br/>
    /// </summary>
    public CatalogNamedIndexShapeBuilder Shape { get; }

    /// <summary>
    /// Gets the reserved blob-key construction path for this named index.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Blob { get; }

    /// <summary>
    /// Gets the reserved string-key construction path for this named index.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory String { get; }

    /// <summary>
    /// Gets the reserved date-key construction path for this named index.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Date { get; }

    /// <summary>
    /// Creates this named grouped index using natural CLR scalar routing inferred from <typeparamref name="TKey"/> and <typeparamref name="TIdentity"/>.<br/>
    /// This is the short generic convenience path for callers that already know the logical field name and do not need the more explicit key-family lane for IntelliSense exploration.<br/>
    /// Byte-array fixed-width indexes still require the explicit family lane until fixed-width metadata is persisted.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="slotIndex">Optional fixed directory slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(
        int? slotIndex = null,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        if (owner.TryGetInfo(Group, Name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex index already exists.");
        }

        return catalog.CreateGenericIndex<TKey, TIdentity>(
            Name,
            owner.ResolveCreateSlot(slotIndex),
            ResolveOptions(keys, options),
            keyWidth: null,
            identityWidth: null,
            ResolveKeyFamily<TKey>(),
            ResolveIdentityFamily<TIdentity>(),
            Group);
    }

    /// <summary>
    /// Opens this named grouped index using explicit generic type arguments and validates those type arguments against persisted catalog metadata.<br/>
    /// This supports compact call sites such as `catalog.Indexes["people"]["age"].Open<int, long>()` while preserving the safety of metadata-driven reopen.<br/>
    /// </summary>
    /// <typeparam name="TKey">The expected public key type.</typeparam>
    /// <typeparam name="TIdentity">The expected public identity type.</typeparam>
    /// <param name="keys">The fallback duplicate-key contract for older scaffold entries without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Open<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        if (!owner.TryGetInfo(Group, Name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex index does not exist.");
        }

        ValidateRequestedTypes<TKey, TIdentity>(info);
        return catalog.OpenGenericIndex<TKey, TIdentity>(info.SlotIndex, ResolveOptions(keys, options), keyWidth: null, identityWidth: null);
    }

    /// <summary>
    /// Opens this named grouped index when it exists or creates it using natural CLR scalar routing when it is missing.<br/>
    /// The open branch validates the requested generic type arguments against persisted metadata before returning a typed handle.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="slotIndex">Optional fixed directory slot to create when the grouped index is missing.</param>
    /// <param name="keys">The index-wide duplicate-key contract for a new index, or fallback contract for an older entry without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(
        int? slotIndex = null,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return owner.TryGetInfo(Group, Name, out _)
            ? Open<TKey, TIdentity>(keys, options)
            : Create<TKey, TIdentity>(slotIndex, keys, options);
    }

    /// <summary>
    /// Opens an existing grouped index as a strict non-generic handle using the CLR key and identity types persisted in catalog metadata.<br/>
    /// This is the preferred programmatic path for query builders, serializers, and adapters that enumerate an identity group first and only choose criteria at runtime.<br/>
    /// The returned handle still validates runtime key values before creating queries, so callers do not lose the catalog's type contract by avoiding generic type arguments.<br/>
    /// </summary>
    /// <returns>A non-generic index handle owned by the catalog lifetime.</returns>
    public IIndex Open()
    {
        if (!owner.TryGetInfo(Group, Name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex index does not exist.");
        }

        return catalog.OpenIndex(info);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }

    private static CatalogIndexKeyFamily ResolveKeyFamily<TKey>()
    {
        Type keyType = typeof(TKey);
        if (keyType == typeof(byte[]))
        {
            throw new NotSupportedException("Short generic grouped factories do not support byte[] keys until fixed-width metadata is persisted.");
        }

        return keyType == typeof(Guid)
            ? CatalogIndexKeyFamily.Guid
            : CatalogIndexKeyFamily.Scalar;
    }

    private static CatalogIndexIdentityFamily ResolveIdentityFamily<TIdentity>()
    {
        if (typeof(TIdentity) == typeof(byte[]))
        {
            throw new NotSupportedException("Short generic grouped factories do not support byte[] identities until fixed-width metadata is persisted.");
        }

        return CatalogIndexIdentityFamily.Scalar;
    }

    private static void ValidateRequestedTypes<TKey, TIdentity>(CatalogIndexInfo info)
    {
        ValidateRequestedType(typeof(TKey), info.KeyTypeName, "key");
        ValidateRequestedType(typeof(TIdentity), info.IdentityTypeName, "identity");
    }

    private static void ValidateRequestedType(Type requestedType, string persistedTypeName, string role)
    {
        if (string.IsNullOrWhiteSpace(persistedTypeName))
        {
            return;
        }

        Type? persistedType = Type.GetType(persistedTypeName, throwOnError: false);
        if (persistedType is not null && persistedType != requestedType)
        {
            throw new InvalidDataException($"The requested {role} type {requestedType.FullName} does not match persisted LibraDex metadata type {persistedType.FullName}.");
        }
    }
}

/// <summary>
/// Provides scalar-key identity-family choices for one named grouped index.<br/>
/// </summary>
public sealed class CatalogNamedScalarKeyBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;

    internal CatalogNamedScalarKeyBuilder(Catalog catalog, CatalogIndexFactories owner, string group, string name)
    {
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
    }

    /// <summary>
    /// Selects a scalar identity family for this scalar-key grouped index.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key type.</typeparam>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed grouped-index builder.</returns>
    public CatalogNamedTypedIndexBuilder<TKey, TIdentity> Scalar<TKey, TIdentity>()
    {
        return new CatalogNamedTypedIndexBuilder<TKey, TIdentity>(
            catalog,
            owner,
            group,
            name,
            keyWidth: null,
            identityWidth: null,
            CatalogIndexKeyFamily.Scalar,
            CatalogIndexIdentityFamily.Scalar);
    }
}

/// <summary>
/// Provides GUID-key identity-family choices for one named grouped index.<br/>
/// </summary>
public sealed class CatalogNamedGuidKeyBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;

    internal CatalogNamedGuidKeyBuilder(Catalog catalog, CatalogIndexFactories owner, string group, string name)
    {
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
    }

    /// <summary>
    /// Selects a scalar identity family for this GUID-key grouped index.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed grouped-index builder.</returns>
    public CatalogNamedTypedIndexBuilder<Guid, TIdentity> Scalar<TIdentity>()
    {
        return new CatalogNamedTypedIndexBuilder<Guid, TIdentity>(
            catalog,
            owner,
            group,
            name,
            keyWidth: null,
            identityWidth: null,
            CatalogIndexKeyFamily.Guid,
            CatalogIndexIdentityFamily.Scalar);
    }
}

/// <summary>
/// Provides terminal construction verbs for one fully typed grouped index definition.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class CatalogNamedTypedIndexBuilder<TKey, TIdentity>
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly LibraDexScalarWidth? keyWidth;
    private readonly LibraDexScalarWidth? identityWidth;
    private readonly CatalogIndexKeyFamily keyFamily;
    private readonly CatalogIndexIdentityFamily identityFamily;

    internal CatalogNamedTypedIndexBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth,
        CatalogIndexKeyFamily keyFamily,
        CatalogIndexIdentityFamily identityFamily)
    {
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.keyWidth = keyWidth;
        this.identityWidth = identityWidth;
        this.keyFamily = keyFamily;
        this.identityFamily = identityFamily;
    }

    /// <summary>
    /// Creates the grouped index in a fixed directory slot.<br/>
    /// When <paramref name="slotIndex"/> is null, the first empty fixed slot is selected so simple grouped catalogs do not need manual slot assignment.<br/>
    /// </summary>
    /// <param name="slotIndex">Optional fixed directory slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Create(int? slotIndex = null, IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex index already exists.");
        }

        return catalog.CreateGenericIndex<TKey, TIdentity>(
            name,
            owner.ResolveCreateSlot(slotIndex),
            ResolveOptions(keys, options),
            keyWidth,
            identityWidth,
            keyFamily,
            identityFamily,
            group);
    }

    /// <summary>
    /// Opens an existing grouped index by group and index name.<br/>
    /// The lookup is metadata-driven and does not require the caller to know the fixed directory slot after the catalog has been created.<br/>
    /// </summary>
    /// <param name="keys">The fallback duplicate-key contract for older scaffold entries without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Open(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex index does not exist.");
        }

        return catalog.OpenGenericIndex<TKey, TIdentity>(info.SlotIndex, ResolveOptions(keys, options), keyWidth, identityWidth);
    }

    /// <summary>
    /// Opens an existing grouped index or creates it when it does not exist.<br/>
    /// This is the low-friction setup path for applications that prefer stable group/index names over manual slot assignment.<br/>
    /// </summary>
    /// <param name="slotIndex">Optional fixed directory slot to create when the grouped index is missing.</param>
    /// <param name="keys">The index-wide duplicate-key contract for a new index, or fallback contract for an older entry without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen(int? slotIndex = null, IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open(keys, options)
            : Create(slotIndex, keys, options);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Describes one active index discovered in a catalog directory.<br/>
/// This is public metadata for inspection and compatibility checks; it is not an opened index handle.<br/>
/// </summary>
/// <param name="SlotIndex">The fixed directory slot containing the index.</param>
/// <param name="Name">The public index name stored in the slot.</param>
/// <param name="RootRouterOffset">The root-router offset for diagnostics and compatibility reporting.</param>
/// <param name="ItemCount">The catalog directory's cached item count for the index, when maintained.</param>
/// <param name="Generation">The index generation recorded in the directory slot.</param>
/// <param name="KeyProfileId">The persisted key profile identifier.</param>
/// <param name="IdentityProfileId">The persisted identity profile identifier.</param>
/// <param name="RouterProfileId">The persisted router profile identifier.</param>
/// <param name="AllocationClassId">The persisted allocation class identifier.</param>
/// <param name="Group">The persisted identity group name when rich metadata is present; otherwise an empty string.</param>
/// <param name="KeyTypeName">The persisted CLR key type name when rich metadata is present; otherwise an empty string.</param>
/// <param name="IdentityTypeName">The persisted CLR identity type name when rich metadata is present; otherwise an empty string.</param>
/// <param name="KeyFamily">The logical key family recorded in rich metadata.</param>
/// <param name="IdentityFamily">The logical identity family recorded in rich metadata.</param>
/// <param name="KeyContract">The persisted duplicate-key contract recorded in rich metadata.</param>
/// <param name="StringKeys">The persisted string-key projection contract recorded in rich metadata.</param>
/// <param name="GuidKeys">The persisted GUID-key projection contract recorded in rich metadata.</param>
/// <param name="DateKeys">The persisted date-key projection contract recorded in rich metadata.</param>
/// <param name="Directions">The persisted byte-direction projection contract recorded in rich metadata.</param>
/// <param name="SortOrder">The persisted physical sort-order intent recorded in rich metadata.</param>
/// <param name="Projections">The persisted logical projection descriptors recorded in rich metadata.</param>
/// <param name="CompositeParts">The persisted composite-key part descriptors recorded in rich metadata.</param>
public readonly record struct CatalogIndexInfo(
    int SlotIndex,
    string Name,
    long RootRouterOffset,
    long ItemCount,
    long Generation,
    ushort KeyProfileId,
    ushort IdentityProfileId,
    ushort RouterProfileId,
    ushort AllocationClassId,
    string Group,
    string KeyTypeName,
    string IdentityTypeName,
    CatalogIndexKeyFamily KeyFamily,
    CatalogIndexIdentityFamily IdentityFamily,
    IndexKeys KeyContract,
    StringKeys StringKeys,
    GuidKeys GuidKeys,
    DateKeys DateKeys,
    LibraDexProjectionDirectionSet Directions,
    LibraDexIndexSortOrder SortOrder,
    IReadOnlyList<LibraDexIndexProjectionSpec> Projections,
    IReadOnlyList<LibraDexCompositeKeyPartSpec> CompositeParts)
{
    /// <summary>
    /// Attempts to reconstruct the logical shape descriptor recorded for this catalog entry.<br/>
    /// Entries created before shape metadata was persisted, or entries whose CLR type names cannot be resolved in the current process, return <see langword="false"/>.<br/>
    /// </summary>
    /// <param name="shape">The reconstructed logical shape when available.</param>
    /// <returns><see langword="true"/> when a logical shape descriptor could be reconstructed; otherwise <see langword="false"/>.</returns>
    public bool TryCreateShape([NotNullWhen(true)] out LibraDexIndexShapeSpec? shape)
    {
        shape = null;
        if (Projections.Count == 0 ||
            string.IsNullOrWhiteSpace(KeyTypeName) ||
            string.IsNullOrWhiteSpace(IdentityTypeName) ||
            KeyFamily == CatalogIndexKeyFamily.Unknown ||
            IdentityFamily == CatalogIndexIdentityFamily.Unknown)
        {
            return false;
        }

        Type? keyType = Type.GetType(KeyTypeName, throwOnError: false);
        Type? identityType = Type.GetType(IdentityTypeName, throwOnError: false);
        if (keyType is null || identityType is null)
        {
            return false;
        }

        shape = new LibraDexIndexShapeSpec(
            Group,
            Name,
            keyType,
            identityType,
            KeyFamily,
            IdentityFamily,
            KeyContract,
            StringKeys,
            GuidKeys,
            DateKeys,
            Directions,
            SortOrder,
            Projections,
            CompositeParts);
        return true;
    }

    /// <summary>
    /// Reconstructs the logical shape descriptor recorded for this catalog entry.<br/>
    /// Use <see cref="TryCreateShape"/> when inspecting mixed old/new catalogs where some entries may not have durable shape metadata.<br/>
    /// </summary>
    /// <returns>The reconstructed logical shape descriptor.</returns>
    /// <exception cref="InvalidOperationException">Thrown when this entry does not contain enough persisted metadata to reconstruct a shape.</exception>
    public LibraDexIndexShapeSpec CreateShape()
    {
        return TryCreateShape(out LibraDexIndexShapeSpec? shape)
            ? shape
            : throw new InvalidOperationException("This catalog index entry does not contain reconstructable logical shape metadata.");
    }

    internal static CatalogIndexInfo FromSlot(IndexDirectorySlotSnapshot slot)
    {
        return FromSlot(
            slot,
            new CatalogIndexMetadata(
                string.Empty,
                slot.Name,
                string.Empty,
                string.Empty,
                CatalogIndexKeyFamily.Unknown,
                CatalogIndexIdentityFamily.Unknown,
                IndexKeys.NonUnique,
                StringKeys.Exact,
                GuidKeys.Exact,
                DateKeys.Exact,
                LibraDexProjectionDirectionSet.Forward,
                LibraDexIndexSortOrder.Ascending,
                Array.Empty<LibraDexIndexProjectionSpec>(),
                Array.Empty<LibraDexCompositeKeyPartSpec>(),
                false));
    }

    internal static CatalogIndexInfo FromSlot(IndexDirectorySlotSnapshot slot, CatalogIndexMetadata metadata)
    {
        return new CatalogIndexInfo(
            slot.SlotIndex,
            string.IsNullOrWhiteSpace(metadata.IndexName) ? slot.Name : metadata.IndexName,
            slot.RootRouterOffset,
            slot.ItemCount,
            slot.Generation,
            slot.KeyProfileId,
            slot.IdentityProfileId,
            slot.RouterProfileId,
            slot.AllocationClassId,
            metadata.Group,
            metadata.KeyTypeName,
            metadata.IdentityTypeName,
            metadata.KeyFamily,
            metadata.IdentityFamily,
            metadata.KeyContract,
            metadata.StringKeys,
            metadata.GuidKeys,
            metadata.DateKeys,
            metadata.Directions,
            metadata.SortOrder,
            metadata.Projections,
            metadata.CompositeParts);
    }
}

/// <summary>
/// Provides scalar-key factory groups.<br/>
/// </summary>
public sealed class CatalogScalarKeyFactories
{
    internal CatalogScalarKeyFactories(Catalog catalog)
    {
        Scalar = new CatalogScalarScalarIndexFactories(catalog);
        Blob = new CatalogScalarBlobIndexFactories(catalog);
        String = new CatalogUnsupportedIndexFactory("Scalar/String indexes need the string identity facade before they can be created.");
    }

    /// <summary>
    /// Gets factories for scalar key and scalar identity indexes.<br/>
    /// </summary>
    public CatalogScalarScalarIndexFactories Scalar { get; }

    /// <summary>
    /// Gets factories for scalar key and blob identity indexes.<br/>
    /// </summary>
    public CatalogScalarBlobIndexFactories Blob { get; }

    /// <summary>
    /// Gets the reserved scalar key and string identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory String { get; }
}

/// <summary>
/// Provides blob-key factory groups.<br/>
/// </summary>
public sealed class CatalogBlobKeyFactories
{
    internal CatalogBlobKeyFactories(Catalog catalog)
    {
        Scalar = new CatalogBlobScalarIndexFactories(catalog);
        Blob = new CatalogUnsupportedIndexFactory("Blob/Blob indexes need the public varlen tuple facade before they can be created.");
        String = new CatalogUnsupportedIndexFactory("Blob/String indexes need the public varlen string identity facade before they can be created.");
    }

    /// <summary>
    /// Gets factories for blob key and scalar identity indexes.<br/>
    /// </summary>
    public CatalogBlobScalarIndexFactories Scalar { get; }

    /// <summary>
    /// Gets the reserved blob key and blob identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Blob { get; }

    /// <summary>
    /// Gets the reserved blob key and string identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory String { get; }
}

/// <summary>
/// Provides string-key factory groups.<br/>
/// </summary>
public sealed class CatalogStringKeyFactories
{
    internal CatalogStringKeyFactories(Catalog catalog)
    {
        Scalar = new CatalogUnsupportedIndexFactory("String/Scalar indexes need the string-key projection facade before they can be created.");
        Blob = new CatalogUnsupportedIndexFactory("String/Blob indexes need the string-key projection facade before they can be created.");
        String = new CatalogUnsupportedIndexFactory("String/String indexes need the string-key projection facade before they can be created.");
    }

    /// <summary>
    /// Gets the reserved string key and scalar identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Scalar { get; }

    /// <summary>
    /// Gets the reserved string key and blob identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Blob { get; }

    /// <summary>
    /// Gets the reserved string key and string identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory String { get; }
}

/// <summary>
/// Provides GUID-key factory groups.<br/>
/// </summary>
public sealed class CatalogGuidKeyFactories
{
    private readonly Catalog catalog;

    internal CatalogGuidKeyFactories(Catalog catalog)
    {
        this.catalog = catalog;
        Scalar = new CatalogGuidScalarIndexFactories(catalog);
        Blob = new CatalogUnsupportedIndexFactory("Guid/Blob indexes need a concrete blob identity facade before they can be created.");
        String = new CatalogUnsupportedIndexFactory("Guid/String indexes need a concrete string identity facade before they can be created.");
    }

    /// <summary>
    /// Gets factories for GUID key and scalar identity indexes.<br/>
    /// </summary>
    public CatalogGuidScalarIndexFactories Scalar { get; }

    /// <summary>
    /// Gets the reserved GUID key and blob identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Blob { get; }

    /// <summary>
    /// Gets the reserved GUID key and string identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory String { get; }

    /// <summary>
    /// Creates a GUID key and scalar identity index using the exact 16-byte GUID projection.<br/>
    /// This convenience keeps the common GUID path short while the grouped `Guid.Scalar` lane remains available for exploration.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="name">The public index name stored in the catalog directory.</param>
    /// <param name="slotIndex">The fixed catalog slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable GUID/scalar index owned by the catalog lifetime.</returns>
    public LibraDexIndex<Guid, TIdentity> Create<TIdentity>(
        string name,
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.CreateGenericIndex<Guid, TIdentity>(
            name,
            slotIndex,
            ResolveOptions(keys, options),
            null,
            null,
            CatalogIndexKeyFamily.Guid,
            CatalogIndexIdentityFamily.Scalar);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Provides date-key factory groups.<br/>
/// </summary>
public sealed class CatalogDateKeyFactories
{
    internal CatalogDateKeyFactories(Catalog catalog)
    {
        Scalar = new CatalogUnsupportedIndexFactory("Date/Scalar indexes need the structured date codec facade before they can be created.");
        Blob = new CatalogUnsupportedIndexFactory("Date/Blob indexes need the structured date codec facade before they can be created.");
        String = new CatalogUnsupportedIndexFactory("Date/String indexes need the structured date codec facade before they can be created.");
    }

    /// <summary>
    /// Gets the reserved date key and scalar identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Scalar { get; }

    /// <summary>
    /// Gets the reserved date key and blob identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Blob { get; }

    /// <summary>
    /// Gets the reserved date key and string identity factory lane.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory String { get; }
}

/// <summary>
/// Creates scalar-key/scalar-identity indexes over the currently supported generic fixed-scalar physical shapes.<br/>
/// Numeric CLR types and `char` infer 8-byte scalar storage; `Guid` infers 16-byte scalar storage.<br/>
/// </summary>
public sealed class CatalogScalarScalarIndexFactories
{
    private readonly Catalog catalog;

    internal CatalogScalarScalarIndexFactories(Catalog catalog)
    {
        this.catalog = catalog;
    }

    /// <summary>
    /// Creates a scalar-key/scalar-identity index.<br/>
    /// The duplicate-key contract can be supplied as the common enum shortcut or through `IndexOptions` when additional options are needed.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key type.</typeparam>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="name">The public index name stored in the catalog directory.</param>
    /// <param name="slotIndex">The fixed catalog slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable index owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(
        string name,
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.CreateGenericIndex<TKey, TIdentity>(name, slotIndex, ResolveOptions(keys, options), null, null);
    }

    /// <summary>
    /// Opens a scalar-key/scalar-identity index from an existing catalog slot.<br/>
    /// The supplied options describe the public contract expected by the caller; persisted contract validation will be added when index metadata stores these profiles.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key type.</typeparam>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="slotIndex">The fixed catalog slot to open.</param>
    /// <param name="keys">The expected index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable index owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Open<TKey, TIdentity>(
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.OpenGenericIndex<TKey, TIdentity>(slotIndex, ResolveOptions(keys, options), null, null);
    }

    /// <summary>
    /// Opens a scalar-key/scalar-identity index or creates it when the slot is empty.<br/>
    /// This is the idempotent setup path for applications that own their catalog layout.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key type.</typeparam>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="name">The public index name stored when a new catalog slot is created.</param>
    /// <param name="slotIndex">The fixed catalog slot to open or create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable index owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(
        string name,
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.CreateOrOpenGenericIndex<TKey, TIdentity>(name, slotIndex, ResolveOptions(keys, options), null, null);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Creates scalar-key/fixed-blob-identity indexes over explicit byte-array identity widths.<br/>
/// Variable-length blob identities remain reserved for the varlen facade so callers do not accidentally infer a physical shape from the first value.<br/>
/// </summary>
public sealed class CatalogScalarBlobIndexFactories
{
    private readonly Catalog catalog;

    internal CatalogScalarBlobIndexFactories(Catalog catalog)
    {
        this.catalog = catalog;
    }

    /// <summary>
    /// Creates a scalar-key/fixed-blob-identity index.<br/>
    /// The identity width is explicit because byte arrays do not have a natural fixed persisted width.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key type.</typeparam>
    /// <param name="name">The public index name stored in the catalog directory.</param>
    /// <param name="identityWidth">The fixed byte width for blob identities.</param>
    /// <param name="slotIndex">The fixed catalog slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable index owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, byte[]> Create<TKey>(
        string name,
        LibraDexScalarWidth identityWidth,
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.CreateGenericIndex<TKey, byte[]>(
            name,
            slotIndex,
            ResolveOptions(keys, options),
            null,
            identityWidth,
            CatalogIndexKeyFamily.Scalar,
            CatalogIndexIdentityFamily.Blob);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Creates fixed-blob-key/scalar-identity indexes over explicit byte-array key widths.<br/>
/// Variable-length blob keys remain reserved for the varlen facade so callers do not accidentally infer a physical shape from the first value.<br/>
/// </summary>
public sealed class CatalogBlobScalarIndexFactories
{
    private readonly Catalog catalog;

    internal CatalogBlobScalarIndexFactories(Catalog catalog)
    {
        this.catalog = catalog;
    }

    /// <summary>
    /// Creates a fixed-blob-key/scalar-identity index.<br/>
    /// The key width is explicit because byte arrays do not have a natural fixed persisted width.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="name">The public index name stored in the catalog directory.</param>
    /// <param name="keyWidth">The fixed byte width for blob keys.</param>
    /// <param name="slotIndex">The fixed catalog slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable index owned by the catalog lifetime.</returns>
    public LibraDexIndex<byte[], TIdentity> Create<TIdentity>(
        string name,
        LibraDexScalarWidth keyWidth,
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.CreateGenericIndex<byte[], TIdentity>(
            name,
            slotIndex,
            ResolveOptions(keys, options),
            keyWidth,
            null,
            CatalogIndexKeyFamily.Blob,
            CatalogIndexIdentityFamily.Scalar);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Creates GUID-key/scalar-identity indexes over the currently supported fixed 16-byte GUID projection.<br/>
/// Segment and text GUID profiles are represented in `IndexOptions.GuidKeys` but are not yet persisted as companion projections.<br/>
/// </summary>
public sealed class CatalogGuidScalarIndexFactories
{
    private readonly Catalog catalog;

    internal CatalogGuidScalarIndexFactories(Catalog catalog)
    {
        this.catalog = catalog;
    }

    /// <summary>
    /// Creates a GUID-key/scalar-identity index.<br/>
    /// The physical key projection is the exact 16-byte GUID value; additional GUID segment/text projections are reserved for a later sub-index implementation.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="name">The public index name stored in the catalog directory.</param>
    /// <param name="slotIndex">The fixed catalog slot to create.</param>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A disposable GUID/scalar index owned by the catalog lifetime.</returns>
    public LibraDexIndex<Guid, TIdentity> Create<TIdentity>(
        string name,
        int slotIndex = 0,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return catalog.CreateGenericIndex<Guid, TIdentity>(
            name,
            slotIndex,
            ResolveOptions(keys, options),
            null,
            null,
            CatalogIndexKeyFamily.Guid,
            CatalogIndexIdentityFamily.Scalar);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Marks a public factory lane that has an agreed name but no connected physical facade yet.<br/>
/// Keeping the lane visible in code captures the DX decision without pretending unsupported combinations are operational.<br/>
/// </summary>
public sealed class CatalogUnsupportedIndexFactory
{
    private readonly string message;

    internal CatalogUnsupportedIndexFactory(string message)
    {
        this.message = message;
    }

    /// <summary>
    /// Throws because this factory lane is reserved but not yet implemented.<br/>
    /// This method gives exploratory callers a clear runtime answer instead of a missing member while the underlying physical shape work catches up.<br/>
    /// </summary>
    /// <returns>This method never returns.</returns>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public object Create()
    {
        throw new NotSupportedException(message);
    }
}
