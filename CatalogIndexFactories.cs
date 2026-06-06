using LibraDex.Layouts;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

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
    /// <param name="options">Optional per-index options override; null uses the contracts embedded in <paramref name="shape"/>.</param>
    /// <returns>A strict non-generic index handle for the created index.</returns>
    public IIndex Create(LibraDexIndexShapeSpec shape, IndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (TryGetInfo(shape.Group, shape.Name, out _))
        {
            throw new InvalidOperationException("The requested LibraDex index shape already exists.");
        }

        if (shape.KeyFamily == CatalogIndexKeyFamily.Composite)
        {
            return catalog.CreateCompositeIndex(shape, ResolveCreateSlot(null), options);
        }

        return catalog.CreateIndex(shape, ResolveCreateSlot(null), options);
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
    /// <param name="options">Optional per-index options override for the create branch; null uses the contracts embedded in <paramref name="shape"/>.</param>
    /// <returns>A strict non-generic index handle for the existing or created index.</returns>
    public IIndex CreateOrOpen(LibraDexIndexShapeSpec shape, IndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return TryGetInfo(shape.Group, shape.Name, out _)
            ? Open(shape)
            : Create(shape, options);
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

        if (shape.KeyFamily == CatalogIndexKeyFamily.Composite)
        {
            ValidateCompositeParts(shape.CompositeParts, info.CompositeParts);
        }
    }

    /// <summary>
    /// Validates requested composite part descriptors against persisted catalog metadata.<br/>
    /// Composite indexes are ordered tier contracts, so this checks ordinal, name, key family, projection flags, and CLR part type before an opened handle is returned.<br/>
    /// </summary>
    /// <param name="requestedParts">The composite parts requested by the caller's shape descriptor.</param>
    /// <param name="persistedParts">The composite parts decoded from catalog metadata.</param>
    /// <exception cref="InvalidDataException">Thrown when the requested shape would not reopen the exact persisted composite contract.</exception>
    private static void ValidateCompositeParts(
        IReadOnlyList<LibraDexCompositeKeyPartSpec> requestedParts,
        IReadOnlyList<LibraDexCompositeKeyPartSpec> persistedParts)
    {
        if (requestedParts.Count != persistedParts.Count)
        {
            throw new InvalidDataException("The requested composite index shape part count does not match persisted metadata.");
        }

        for (int i = 0; i < requestedParts.Count; i++)
        {
            LibraDexCompositeKeyPartSpec requested = requestedParts[i];
            LibraDexCompositeKeyPartSpec persisted = persistedParts[i];
            if (!string.Equals(requested.Name, persisted.Name, StringComparison.Ordinal) ||
                requested.KeyFamily != persisted.KeyFamily ||
                requested.StringKeys != persisted.StringKeys ||
                requested.GuidKeys != persisted.GuidKeys ||
                requested.DateKeys != persisted.DateKeys)
            {
                throw new InvalidDataException($"The requested composite index part at ordinal {i} does not match persisted metadata.");
            }

            ValidateShapeType(requested.KeyType, persisted.KeyType.AssemblyQualifiedName ?? persisted.KeyType.FullName ?? persisted.KeyType.Name, $"composite part '{requested.Name}'");
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
    /// Starts a low-friction condition builder by selecting an index name inside this identity group.<br/>
    /// Key typing and projection intent are selected after the index through members such as `.AsString`, `.AsGuid`, and `.AsInt64`, keeping the public grammar index-first instead of type-first.<br/>
    /// The selected index name remains descriptor-shaped until the condition is materialized, so reusable conditions can still be built before indexes are opened.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this identity group.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(string indexName)
    {
        return LibraDexCondition.ForGroup(Group).Index(indexName);
    }

    /// <summary>
    /// Starts a low-friction condition builder from an already opened index instance in this identity group.<br/>
    /// The handle supplies the index name and verifies the identity group immediately; key-type compatibility is still checked when the selected `.As...` family is materialized against the index.<br/>
    /// This keeps instance-based conditions concise without making callers repeat the index name.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied index handle belongs to a different LibraDex identity group.");
        }

        return Where(index.Name);
    }

    /// <summary>
    /// Starts a low-friction condition builder from a generic typed index instance and selects the index key type automatically.<br/>
    /// This enables `catalog.Indexes["group"].Where(age).GreaterOrEqual(18)` for typed scalar handles while preserving the existing `.Where(index).As...` form when callers need a richer projection family.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type carried by the opened index handle.</typeparam>
    /// <typeparam name="TIdentity">The identity type carried by the opened index handle.</typeparam>
    /// <param name="index">The opened generic index instance to select.</param>
    /// <returns>A typed operator for the selected index key type.</returns>
    public LibraDexConditionOperator<TKey> Where<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index)
    {
        return Where((IIndex)index).As<TKey>();
    }

    /// <summary>
    /// Starts a low-friction condition builder from a string index facade and selects string operators automatically.<br/>
    /// This enables `catalog.Indexes["group"].Where(name).StartsWith("A")` without requiring `.AsString` when the handle itself is already string-typed.<br/>
    /// </summary>
    /// <param name="index">The opened string index instance to select.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexStringConditionOperator Where(LibraDexStringScalar8Index index)
    {
        return Where((IIndex)index).AsString;
    }

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

    /// <summary>
    /// Materializes identities for a completed condition over this identity group.<br/>
    /// Index names referenced by the condition are resolved from the group metadata at materialization time, preserving deferred selector binding without making callers build resolver dictionaries by hand.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the terminal action.</typeparam>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of matching identities.</returns>
    public IReadOnlyList<TIdentity> GetIdentities<TIdentity>(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex identity group.");
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex);
        return criterion.IDsWith(ordering, deduplication, skip, take, bookmark).ToList<TIdentity>();
    }

    /// <summary>
    /// Opens a forward-only identity cursor for a completed condition over this identity group.<br/>
    /// Conditions remain filter descriptors; this method owns the materialization shape when callers want identity values without constructing a list.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the cursor.</typeparam>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A forward-only cursor over matching identities.</returns>
    public LibraDexIdentityCursor<TIdentity> GetCursor<TIdentity>(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex identity group.");
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex);
        return new LibraDexIdentityCursor<TIdentity>(
            criterion.IDsWith(ordering, deduplication, skip, take, bookmark).Iterate<TIdentity>());
    }

    /// <summary>
    /// Opens a forward-only target-index cursor for a completed condition over this identity group.<br/>
    /// The condition filters identities, while the target index owns the result shape and returns its stored key values directly.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type exposed by the target index.</typeparam>
    /// <typeparam name="TIdentity">The public identity type exposed by the target index.</typeparam>
    /// <param name="targetIndex">The index whose key/identity entries should be streamed.</param>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="skip">The number of matching target-index entries to skip.</param>
    /// <param name="take">The optional maximum number of target-index entries to return.</param>
    /// <returns>A forward-only cursor over matching target-index entries.</returns>
    public LibraDexIndexCursor<TKey, TIdentity> GetCursor<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> targetIndex,
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        return GetTargetCursor<TKey, TIdentity>(targetIndex, condition, skip, take);
    }

    /// <summary>
    /// Opens a forward-only string target-index cursor for a completed condition over this identity group.<br/>
    /// The condition filters identities, while the string target index owns the result shape and returns exact string keys directly.<br/>
    /// </summary>
    /// <param name="targetIndex">The string index whose key/identity entries should be streamed.</param>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="skip">The number of matching target-index entries to skip.</param>
    /// <param name="take">The optional maximum number of target-index entries to return.</param>
    /// <returns>A forward-only cursor over matching string-index entries.</returns>
    public LibraDexIndexCursor<string, ulong> GetCursor(
        LibraDexStringScalar8Index targetIndex,
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        return GetTargetCursor<string, ulong>(targetIndex, condition, skip, take);
    }

    private LibraDexIndexCursor<TKey, TIdentity> GetTargetCursor<TKey, TIdentity>(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        int skip,
        int? take)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex identity group.");
        }

        if (!string.Equals(targetIndex.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The target index belongs to a different LibraDex identity group.");
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex);
        _ = LibraDexConditionCursorExecutor.TryCreateDirectPrimitiveDeletePlan(
            criterion,
            targetIndex,
            skip,
            take,
            out IIdentityPrimitiveMutator? primitiveMutator,
            out LibraDexIdentityPrimitiveRequest? primitiveDeleteRequest);
        return new LibraDexIndexCursor<TKey, TIdentity>(
            LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, targetIndex, skip, take),
            targetIndex,
            targetIndex as IIdentityExactTupleMutator,
            primitiveMutator,
            primitiveDeleteRequest,
            skip,
            take);
    }

    /// <summary>
    /// Resolves a maintained projection index for one projection-backed condition leaf inside this identity group.<br/>
    /// Binary suffix conditions currently map to the owning index's hidden reversed exact-byte projection when the catalog metadata declares one.<br/>
    /// </summary>
    /// <param name="descriptor">The condition leaf requesting a projection route.</param>
    /// <param name="classification">The planner classification that identified the projection need.</param>
    /// <returns>The opened projection index, or <see langword="null"/> when no matching projection exists.</returns>
    private IIndex? ResolveProjectionIndex(
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexConditionLeafClassification classification)
    {
        if (classification.ProjectionKind != LibraDexIndexProjectionKind.Exact ||
            descriptor.ValueKind != LibraDexConditionValueKind.Binary ||
            descriptor.Operator != LibraDexConditionOperatorKind.EndsWith ||
            !owner.TryGetInfo(Group, descriptor.IndexName, out CatalogIndexInfo info) ||
            info.ExactReversedProjectionSlotIndex < 0)
        {
            return null;
        }

        LibraDexScalarWidth? keyWidth = ResolvePersistedBlobWidth(info.VarKeyMaxKeyLength);
        LibraDexScalarWidth? identityWidth = ResolvePersistedBlobWidth(info.VarIdentityMaxLength);
        Type identityType = ResolvePersistedType(info.IdentityTypeName);
        System.Reflection.MethodInfo openMethod = typeof(Catalog).GetMethod(
            "OpenGenericIndex",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Catalog), "OpenGenericIndex");
        try
        {
            object? opened = openMethod
                .MakeGenericMethod(typeof(byte[]), identityType)
                .Invoke(catalog, new object?[]
                {
                    info.ExactReversedProjectionSlotIndex,
                    new IndexOptions { Keys = info.KeyContract },
                    keyWidth,
                    identityWidth
                });
            return opened as IIndex;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Converts a persisted fixed byte width into the generic scalar-width enum used by metadata-driven opens.<br/>
    /// A zero width means the corresponding side is not a fixed byte-array scalar and should stay unspecified.<br/>
    /// </summary>
    /// <param name="byteWidth">The persisted byte width.</param>
    /// <returns>The matching scalar width, or <see langword="null"/> for non-byte-array sides.</returns>
    private static LibraDexScalarWidth? ResolvePersistedBlobWidth(int byteWidth)
    {
        return byteWidth switch
        {
            0 => null,
            8 => LibraDexScalarWidth.Bytes8,
            16 => LibraDexScalarWidth.Bytes16,
            32 => LibraDexScalarWidth.Bytes32,
            _ => throw new NotSupportedException("The persisted binary projection width is not supported by the generic scalar bridge.")
        };
    }

    /// <summary>
    /// Resolves a persisted CLR type name into a runtime type for reflection-based generic opens.<br/>
    /// Catalog metadata stores assembly-qualified names so generated and non-generic callers can reopen typed indexes without carrying generic arguments.<br/>
    /// </summary>
    /// <param name="typeName">The persisted CLR type name.</param>
    /// <returns>The resolved runtime type.</returns>
    private static Type ResolvePersistedType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidDataException("The catalog entry does not contain a persisted CLR type name.");
        }

        return Type.GetType(typeName, throwOnError: false)
            ?? throw new InvalidDataException($"The persisted CLR type '{typeName}' could not be resolved.");
    }

    /// <summary>
    /// Starts an ordered multi-key condition builder for already opened indexes in this identity group.<br/>
    /// Ordinal selectors such as `Where(0).AsGuid` and `Where(1).AsString` bind to this participant list, which is convenient for generated code and caller-owned index arrays.<br/>
    /// Name and handle selectors use the ordinary group surface through `Where(indexName)` or `Where(indexInstance)` so handwritten code remains index-first.<br/>
    /// </summary>
    /// <param name="indexes">The ordered indexes that participate in the condition.</param>
    /// <returns>An ordered multi-key condition builder.</returns>
    public LibraDexOrderedMultiKeyBuilder MultiKey(params IIndex[] indexes)
    {
        return new LibraDexOrderedMultiKeyBuilder(Group, indexes);
    }

    /// <summary>
    /// Starts an ordered multi-key condition builder for an enumerable of already opened indexes in this identity group.<br/>
    /// The enumerable is captured into an ordered array immediately so later condition assembly has stable ordinal-to-index mapping.<br/>
    /// </summary>
    /// <param name="indexes">The ordered indexes that participate in the condition.</param>
    /// <returns>An ordered multi-key condition builder.</returns>
    public LibraDexOrderedMultiKeyBuilder MultiKey(IEnumerable<IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return new LibraDexOrderedMultiKeyBuilder(Group, indexes.ToArray());
    }

    /// <summary>
    /// Describes a composite-key index inside this identity group without creating or opening physical storage.<br/>
    /// The returned builder is lazy: only its terminal lifecycle methods mutate or inspect catalog storage.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type stored by the composite index.</typeparam>
    /// <param name="name">The index name inside this identity group.</param>
    /// <param name="parts">The ordered composite-key parts.</param>
    /// <returns>A lifecycle builder for the named composite index.</returns>
    public CatalogCompositeIndexBuilder<TIdentity> Composite<TIdentity>(
        string name,
        params LibraDexCompositeKeyPartSpec[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CatalogCompositeIndexBuilder<TIdentity>(owner, Group, name, parts);
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
        Blob = new CatalogNamedBlobKeyBuilder(catalog, owner, group, name);
        String = new CatalogNamedStringKeyBuilder(catalog, owner, group, name);
        Shape = new CatalogNamedIndexShapeBuilder(group, name);
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
    /// Gets fixed-blob-key construction paths for this named index.<br/>
    /// </summary>
    public CatalogNamedBlobKeyBuilder Blob { get; }

    /// <summary>
    /// Gets descriptor-only logical index shape builders for this named index.<br/>
    /// Shape descriptors model Abraxas-style selector/index alignment without creating physical storage yet; the condition-builder port can target these projection specs directly.<br/>
    /// </summary>
    public CatalogNamedIndexShapeBuilder Shape { get; }

    /// <summary>
    /// Gets the reserved string-key construction path for this named index.<br/>
    /// </summary>
    public CatalogNamedStringKeyBuilder String { get; }

    /// <summary>
    /// Gets the reserved date-key construction path for this named index.<br/>
    /// </summary>
    public CatalogUnsupportedIndexFactory Date { get; }

    /// <summary>
    /// Selects an Int32 key family with a scalar identity family for this named index.<br/>
    /// The returned builder exposes lifecycle verbs such as `Create`, `Open`, and `CreateOrOpen` without requiring manual catalog slot selection.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for an Int32-key index.</returns>
    public CatalogNamedTypedIndexBuilder<int, TIdentity> Int32Keys<TIdentity>()
    {
        return Scalar.Scalar<int, TIdentity>();
    }

    /// <summary>
    /// Selects an Int64 key family with a scalar identity family for this named index.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for an Int64-key index.</returns>
    public CatalogNamedTypedIndexBuilder<long, TIdentity> Int64Keys<TIdentity>()
    {
        return Scalar.Scalar<long, TIdentity>();
    }

    /// <summary>
    /// Selects an Int128 key family with a scalar identity family for this named index.<br/>
    /// Int128 keys route through the existing fixed-16 scalar shelf shape with a signed sortable transform, so negative, zero, and positive values remain in numeric order without query-time conversion.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for an Int128-key index.</returns>
    public CatalogNamedTypedIndexBuilder<Int128, TIdentity> Int128Keys<TIdentity>()
    {
        return Scalar.Scalar<Int128, TIdentity>();
    }

    /// <summary>
    /// Selects a UInt128 key family with a scalar identity family for this named index.<br/>
    /// UInt128 keys route through the existing fixed-16 scalar shelf shape as two big-endian unsigned lanes, preserving natural numeric order for boundary, range, and membership conditions.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for a UInt128-key index.</returns>
    public CatalogNamedTypedIndexBuilder<UInt128, TIdentity> UInt128Keys<TIdentity>()
    {
        return Scalar.Scalar<UInt128, TIdentity>();
    }

    /// <summary>
    /// Selects a GUID key family with a scalar identity family for this named index.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for a GUID-key index.</returns>
    public CatalogNamedTypedIndexBuilder<Guid, TIdentity> GuidKeys<TIdentity>()
    {
        return Guid.Scalar<TIdentity>();
    }

    /// <summary>
    /// Selects a fixed-width BigInteger key family with a scalar 8-byte identity family for this named index.<br/>
    /// The required <paramref name="maxBytes"/> value caps the canonical BigInteger magnitude and guides callers toward bounded, lookup-oriented index keys.<br/>
    /// This is an index construction contract; BigInteger conditions use values only and do not carry storage sizing semantics.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="maxBytes">Maximum canonical BigInteger magnitude byte count; valid values are 1 through 512.</param>
    /// <returns>A BigInt lifecycle builder using fixed-width normalized keys.</returns>
    public CatalogNamedBigIntKeyBuilder<TIdentity> BigIntKeys<TIdentity>(int maxBytes)
    {
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        return new CatalogNamedBigIntKeyBuilder<TIdentity>(
            catalog,
            owner,
            Group,
            Name,
            maxBytes,
            LibraDexBigIntKeyStorage.FixedWidth);
    }

    /// <summary>
    /// Selects a variable-width BigInteger key family with a scalar 8-byte identity family for this named index.<br/>
    /// This name intentionally carries extra DX friction because variable-width BigInt keys trade lookup-oriented fixed width for smaller stored keys.<br/>
    /// The <paramref name="maxBytes"/> argument remains a BigInteger domain cap, not a configurable varlen physical key cap or query-time setting.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="maxBytes">Maximum canonical BigInteger magnitude byte count; valid values are 1 through 512.</param>
    /// <returns>A BigInt lifecycle builder using variable-width normalized keys.</returns>
    public CatalogNamedBigIntKeyBuilder<TIdentity> BigIntVarLenKeys<TIdentity>(int maxBytes)
    {
        return new CatalogNamedBigIntKeyBuilder<TIdentity>(
            catalog,
            owner,
            Group,
            Name,
            maxBytes,
            LibraDexBigIntKeyStorage.VariableWidth);
    }

    /// <summary>
    /// Selects a fixed-width BigInteger key family with raw variable-length identity bytes for this named index.<br/>
    /// The explicit method keeps variable identity storage discoverable without making `byte[]` scalar identities ambiguous.<br/>
    /// The byte limits are index construction contracts and are not part of condition/query semantics.<br/>
    /// </summary>
    /// <param name="maxBytes">Maximum canonical BigInteger magnitude byte count; valid values are 1 through 512.</param>
    /// <param name="maxIdentityBytes">Maximum raw identity byte count accepted by this index.</param>
    /// <returns>A BigInt variable-identity lifecycle builder using fixed-width normalized keys.</returns>
    public CatalogNamedBigIntVarIdentityKeyBuilder BigIntVarIdentityKeys(int maxBytes, int maxIdentityBytes)
    {
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        FixedNVarIdentityProfile.Create(
            FixedNVarIdentityProfile.DefaultShelfExtentSize,
            LibraDexBigIntCodec.GetFixedEncodedLength(maxBytes),
            maxIdentityBytes);
        return new CatalogNamedBigIntVarIdentityKeyBuilder(
            catalog,
            owner,
            Group,
            Name,
            maxBytes,
            maxIdentityBytes);
    }

    /// <summary>
    /// Selects the logical string-key family for this named index.<br/>
    /// The current physical string facade stores UInt64 identities, so the lifecycle builder is returned directly rather than taking an identity type parameter.<br/>
    /// </summary>
    /// <returns>A string-key lifecycle builder.</returns>
    public CatalogNamedStringKeyBuilder StringKeys()
    {
        return String;
    }

    /// <summary>
    /// Describes this named grouped index as a composite-key index without creating or opening physical storage.<br/>
    /// The returned builder captures the part descriptors once, then requires an explicit terminal lifecycle method such as `Create`, `Open`, or `CreateOrOpen`.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type stored by the composite index.</typeparam>
    /// <param name="parts">The ordered composite-key parts.</param>
    /// <returns>A lifecycle builder for this composite index.</returns>
    public CatalogCompositeIndexBuilder<TIdentity> Composite<TIdentity>(params LibraDexCompositeKeyPartSpec[] parts)
    {
        return new CatalogCompositeIndexBuilder<TIdentity>(owner, Group, Name, parts);
    }

    /// <summary>
    /// Creates this named grouped index using natural CLR scalar routing inferred from <typeparamref name="TKey"/> and <typeparamref name="TIdentity"/>.<br/>
    /// This is the short generic convenience path for callers that already know the logical field name and do not need the more explicit key-family lane for IntelliSense exploration.<br/>
    /// Byte-array fixed-width indexes still require the explicit family lane until fixed-width metadata is persisted.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        if (owner.TryGetInfo(Group, Name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex index already exists.");
        }

        return catalog.CreateGenericIndex<TKey, TIdentity>(
            Name,
            owner.ResolveCreateSlot(null),
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
    /// <param name="keys">The index-wide duplicate-key contract for a new index, or fallback contract for an older entry without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return owner.TryGetInfo(Group, Name, out _)
            ? Open<TKey, TIdentity>(keys, options)
            : Create<TKey, TIdentity>(keys, options);
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
/// Provides lifecycle methods for one named composite-key index descriptor.<br/>
/// Instances are cheap lazy builders: they only hold group, name, identity type, and ordered part metadata until a terminal method is called.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type stored by the composite index.</typeparam>
public sealed class CatalogCompositeIndexBuilder<TIdentity>
{
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly LibraDexCompositeKeyPartSpec[] parts;

    internal CatalogCompositeIndexBuilder(
        CatalogIndexFactories owner,
        string group,
        string name,
        IReadOnlyList<LibraDexCompositeKeyPartSpec> parts)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parts);
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.parts = parts.ToArray();
        if (this.parts.Length == 0)
        {
            throw new ArgumentException("Composite index builders require at least one key part.", nameof(parts));
        }
    }

    /// <summary>
    /// Gets the identity group name captured by this composite descriptor.<br/>
    /// </summary>
    public string Group => group;

    /// <summary>
    /// Gets the logical index name captured by this composite descriptor.<br/>
    /// </summary>
    public string Name => name;

    /// <summary>
    /// Gets the ordered composite-key part descriptors captured by this builder.<br/>
    /// The returned array is a defensive copy so callers cannot mutate the lifecycle contract after construction.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexCompositeKeyPartSpec> Parts => parts.ToArray();

    /// <summary>
    /// Builds the logical composite shape represented by this builder.<br/>
    /// Shape construction is metadata-only and does not create, open, or mutate catalog storage.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract for the full composite key.</param>
    /// <returns>A logical composite index shape descriptor.</returns>
    public LibraDexIndexShapeSpec Shape(IndexKeys keys = IndexKeys.NonUnique)
    {
        return new CatalogNamedIndexShapeBuilder(group, name).Composite<TIdentity>(parts, keys);
    }

    /// <summary>
    /// Creates the composite index represented by this descriptor.<br/>
    /// In the current slice this returns the in-process routed-component proof; durable DataKernel-backed mini-router storage will attach behind this same lifecycle method later.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract for the full composite key.</param>
    /// <param name="options">Optional per-index options for the future durable create branch.</param>
    /// <returns>A strict non-generic composite index handle.</returns>
    public IIndex Create(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return owner.Create(Shape(keys), options: options);
    }

    /// <summary>
    /// Opens the composite index represented by this descriptor and validates persisted metadata against the requested parts.<br/>
    /// Composite open will fail closed until durable composite metadata and DataKernel-backed mini-router storage are implemented.<br/>
    /// </summary>
    /// <param name="keys">The expected duplicate-key contract for the full composite key.</param>
    /// <returns>A strict non-generic composite index handle.</returns>
    public IIndex Open(IndexKeys keys = IndexKeys.NonUnique)
    {
        return owner.Open(Shape(keys));
    }

    /// <summary>
    /// Opens the composite index represented by this descriptor when it exists, or creates it when missing.<br/>
    /// The current metadata slice persists and validates the composite shape, while indexed contents remain in-process until durable mini-router storage is implemented.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract for the full composite key.</param>
    /// <param name="options">Optional per-index options for the future durable create branch.</param>
    /// <returns>A strict non-generic composite index handle.</returns>
    public IIndex CreateOrOpen(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return owner.CreateOrOpen(Shape(keys), options: options);
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
/// Provides fixed-blob-key identity-family choices for one named grouped index.<br/>
/// </summary>
public sealed class CatalogNamedBlobKeyBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;

    internal CatalogNamedBlobKeyBuilder(Catalog catalog, CatalogIndexFactories owner, string group, string name)
    {
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
    }

    /// <summary>
    /// Selects a scalar identity family for this fixed-blob-key grouped index.<br/>
    /// The key width is explicit because byte arrays do not have a natural persisted scalar width.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="keyWidth">The fixed byte width for blob keys.</param>
    /// <param name="directions">The byte-direction projections to maintain; forward is the ordinary index and reversed enables suffix routing without changing the returned handle type.</param>
    /// <returns>A typed grouped-index builder.</returns>
    public CatalogNamedTypedIndexBuilder<byte[], TIdentity> Scalar<TIdentity>(
        LibraDexScalarWidth keyWidth,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        return new CatalogNamedTypedIndexBuilder<byte[], TIdentity>(
            catalog,
            owner,
            group,
            name,
            keyWidth,
            identityWidth: null,
            CatalogIndexKeyFamily.Blob,
            CatalogIndexIdentityFamily.Scalar,
            directions);
    }
}

/// <summary>
/// Provides terminal lifecycle verbs for one named BigInteger-key/scalar-identity index.<br/>
/// BigInt key indexes require an explicit magnitude cap so persisted key size, validation, and routing behavior remain bounded and reviewable.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type stored by this BigInt index.</typeparam>
public sealed class CatalogNamedBigIntKeyBuilder<TIdentity>
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly int maxBytes;
    private readonly LibraDexBigIntKeyStorage storage;

    internal CatalogNamedBigIntKeyBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        int maxBytes,
        LibraDexBigIntKeyStorage storage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(owner);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.maxBytes = maxBytes;
        this.storage = storage;
    }

    /// <summary>
    /// Creates the named BigInt index.<br/>
    /// Creation persists the BigInt storage shape and magnitude cap so reopen validates the same fixed-width or variable-width contract later.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A BigInt index facade over scalar 8-byte identities.</returns>
    public LibraDexBigIntScalar8Index<TIdentity> Create(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex BigInt index already exists.");
        }

        return catalog.CreateBigIntScalar8Index<TIdentity>(
            group,
            name,
            owner.ResolveCreateSlot(null),
            maxBytes,
            storage,
            ResolveOptions(keys, options));
    }

    /// <summary>
    /// Opens the named BigInt index and validates its persisted storage shape, identity type, and magnitude cap.<br/>
    /// </summary>
    /// <param name="keys">The fallback duplicate-key contract for entries without rich metadata.</param>
    /// <returns>A BigInt index facade over scalar 8-byte identities.</returns>
    public LibraDexBigIntScalar8Index<TIdentity> Open(IndexKeys keys = IndexKeys.NonUnique)
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex BigInt index does not exist.");
        }

        _ = keys;
        return catalog.OpenBigIntScalar8Index<TIdentity>(info, storage);
    }

    /// <summary>
    /// Opens the named BigInt index when it exists, or creates it when missing.<br/>
    /// The open branch validates persisted shape metadata before returning the facade.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract for create, or fallback contract for older metadata.</param>
    /// <param name="options">Optional per-index options for create.</param>
    /// <returns>A BigInt index facade over scalar 8-byte identities.</returns>
    public LibraDexBigIntScalar8Index<TIdentity> CreateOrOpen(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open(keys)
            : Create(keys, options);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Provides terminal lifecycle verbs for one named BigInteger-key/raw-variable-identity index.<br/>
/// This builder is intentionally explicit because variable-length identities are a different physical contract than fixed `byte[]` scalar lanes.<br/>
/// </summary>
public sealed class CatalogNamedBigIntVarIdentityKeyBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly int maxBytes;
    private readonly int maxIdentityBytes;

    internal CatalogNamedBigIntVarIdentityKeyBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        int maxBytes,
        int maxIdentityBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(owner);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        FixedNVarIdentityProfile.Create(
            FixedNVarIdentityProfile.DefaultShelfExtentSize,
            LibraDexBigIntCodec.GetFixedEncodedLength(maxBytes),
            maxIdentityBytes);
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.maxBytes = maxBytes;
        this.maxIdentityBytes = maxIdentityBytes;
    }

    /// <summary>
    /// Creates the named BigInt variable-identity index.<br/>
    /// Creation persists both the fixed BigInt key width and the maximum raw identity byte length so reopen validates the same `FV` storage profile later.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A BigInt index facade over raw variable-length identity bytes.</returns>
    public LibraDexBigIntVarIdentityIndex Create(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex BigInt variable-identity index already exists.");
        }

        return catalog.CreateBigIntVarIdentityIndex(
            group,
            name,
            owner.ResolveCreateSlot(null),
            maxBytes,
            maxIdentityBytes,
            ResolveOptions(keys, options));
    }

    /// <summary>
    /// Opens the named BigInt variable-identity index and validates its persisted key and identity byte caps.<br/>
    /// </summary>
    /// <returns>A BigInt index facade over raw variable-length identity bytes.</returns>
    public LibraDexBigIntVarIdentityIndex Open()
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex BigInt variable-identity index does not exist.");
        }

        LibraDexBigIntVarIdentityIndex index = catalog.OpenBigIntVarIdentityIndex(info);
        if (index.MaxBytes != maxBytes || index.MaxIdentityBytes != maxIdentityBytes)
        {
            throw new InvalidDataException("The requested BigInt variable-identity caps do not match persisted catalog metadata.");
        }

        return index;
    }

    /// <summary>
    /// Opens the named BigInt variable-identity index when it exists, or creates it when missing.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract for create.</param>
    /// <param name="options">Optional per-index options for create.</param>
    /// <returns>A BigInt index facade over raw variable-length identity bytes.</returns>
    public LibraDexBigIntVarIdentityIndex CreateOrOpen(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open()
            : Create(keys, options);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }
}

/// <summary>
/// Provides string-key projection facade choices for one named grouped index.<br/>
/// The first physical string slice stores exact UTF-8 keys plus optional maintained folded UTF-8 and sort-key projection keys over routed `VS8` indexes.<br/>
/// </summary>
public sealed class CatalogNamedStringKeyBuilder
{
    private const int StringVarKeyPhysicalMaxLength = 1024;

    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;

    internal CatalogNamedStringKeyBuilder(Catalog catalog, CatalogIndexFactories owner, string group, string name)
    {
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
    }

    /// <summary>
    /// Creates a string-key/scalar-identity index with exact UTF-8 storage and optional maintained string projections.<br/>
    /// Projection slots are allocated automatically from the catalog directory so normal callers do not manage slot placement.<br/>
    /// String storage uses the standard variable-key cap; callers do not tune varlen key length per index.<br/>
    /// </summary>
    /// <param name="stringKeys">The string projection profile to physically maintain.</param>
    /// <param name="directions">The string projection byte directions to physically maintain.</param>
    /// <param name="foldedCulture">Optional culture name for folded-text projection values; null or empty means invariant culture.</param>
    /// <param name="sortKeyCulture">Optional culture name for sort-key projection values; null or empty means invariant culture.</param>
    /// <param name="stringComparisonPolicy">Optional runtime index-level string comparison policy for managed residual comparison and prepared membership fallback.</param>
    /// <returns>A string index facade over the exact and maintained projection indexes.</returns>
    public LibraDexStringScalar8Index Create(
        StringKeys stringKeys = StringKeys.ExactAndFolded,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward,
        string? foldedCulture = null,
        string? sortKeyCulture = null,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        if ((stringKeys & StringKeys.Exact) == 0)
        {
            throw new NotSupportedException("String facade creation requires exact string storage.");
        }

        HashSet<int> reservedSlots = new();
        int ResolveAutoSlot()
        {
            for (int i = 0; i < IndexDirectoryLayout.SlotCount; i++)
            {
                if (!reservedSlots.Contains(i) && !owner.TryGetInfo(i, out _))
                {
                    reservedSlots.Add(i);
                    return i;
                }
            }

            throw new InvalidOperationException("The fixed LibraDex index directory has no available slots.");
        }

        int exactSlot = ResolveAutoSlot();
        VarKeyScalar8Index? exactReversed = null;
        int exactReversedSlot = -1;
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0 &&
            (stringKeys & StringKeys.Exact) != 0)
        {
            exactReversedSlot = ResolveAutoSlot();
            exactReversed = catalog.CreateVarKeyScalar8Index($"{name}#exact-rev", exactReversedSlot, StringVarKeyPhysicalMaxLength);
        }

        VarKeyScalar8Index? folded = null;
        int foldedSlot = -1;
        if ((stringKeys & StringKeys.Folded) != 0)
        {
            foldedSlot = ResolveAutoSlot();
            folded = catalog.CreateVarKeyScalar8Index($"{name}#folded", foldedSlot, StringVarKeyPhysicalMaxLength);
        }

        VarKeyScalar8Index? sortKey = null;
        int sortKeySlot = -1;
        if ((stringKeys & StringKeys.SortKey) != 0)
        {
            sortKeySlot = ResolveAutoSlot();
            sortKey = catalog.CreateVarKeyScalar8Index($"{name}#sortkey", sortKeySlot, StringVarKeyPhysicalMaxLength);
        }

        VarKeyScalar8Index? foldedReversed = null;
        int foldedReversedSlot = -1;
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0 &&
            (stringKeys & StringKeys.Folded) != 0)
        {
            foldedReversedSlot = ResolveAutoSlot();
            foldedReversed = catalog.CreateVarKeyScalar8Index($"{name}#folded-rev", foldedReversedSlot, StringVarKeyPhysicalMaxLength);
        }

        LibraDexStringComparisonPolicy? effectiveStringComparisonPolicy = stringComparisonPolicy ?? catalog.Options.StringComparisonPolicy;
        CatalogIndexMetadata metadata = CreateStringMetadata(stringKeys, exactReversedSlot, foldedSlot, sortKeySlot, foldedReversedSlot, directions, foldedCulture, sortKeyCulture, effectiveStringComparisonPolicy);
        VarKeyScalar8Index exact = catalog.CreateVarKeyScalar8Index(name, exactSlot, StringVarKeyPhysicalMaxLength, metadata);
        return new LibraDexStringScalar8Index(group, name, exact, exactReversed, folded, sortKey, foldedReversed, foldedCulture, sortKeyCulture, effectiveStringComparisonPolicy);
    }

    /// <summary>
    /// Opens a persisted string-key/scalar-identity index and its owned projection subindexes.<br/>
    /// The logical index metadata supplies the projection slots, cultures, and variable-key length, so callers do not need to remember projection slot numbers after catalog creation.<br/>
    /// </summary>
    /// <param name="stringComparisonPolicy">Optional runtime index-level string comparison policy for the reopened facade.</param>
    /// <returns>A string index facade over the reopened exact and maintained projection indexes.</returns>
    public LibraDexStringScalar8Index Open(LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException($"String index '{group}/{name}' was not found in the catalog.");
        }

        ValidateStringInfo(info);
        VarKeyScalar8Index exact = catalog.OpenVarKeyScalar8Index(info.SlotIndex, info.VarKeyMaxKeyLength);
        VarKeyScalar8Index? exactReversed = info.ExactReversedProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.ExactReversedProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        VarKeyScalar8Index? folded = info.FoldedProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.FoldedProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        VarKeyScalar8Index? sortKey = info.SortKeyProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.SortKeyProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        VarKeyScalar8Index? foldedReversed = info.FoldedReversedProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.FoldedReversedProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        return new LibraDexStringScalar8Index(
            group,
            name,
            exact,
            exactReversed,
            folded,
            sortKey,
            foldedReversed,
            info.FoldedCulture,
            info.SortKeyCulture,
            ResolveOpenStringComparisonPolicy(info, stringComparisonPolicy));
    }

    /// <summary>
    /// Opens an existing string-key/scalar-identity index or creates it when the logical index is missing.<br/>
    /// Creation persists projection ownership metadata; reopen uses that metadata so generated callers can keep targeting the logical index name only.<br/>
    /// String storage uses the standard variable-key cap; callers do not tune varlen key length per index.<br/>
    /// </summary>
    /// <param name="stringKeys">The string projection profile to physically maintain on create.</param>
    /// <param name="directions">The string projection byte directions to physically maintain on create.</param>
    /// <param name="foldedCulture">Optional culture name for folded-text projection values; null or empty means invariant culture.</param>
    /// <param name="sortKeyCulture">Optional culture name for sort-key projection values; null or empty means invariant culture.</param>
    /// <param name="stringComparisonPolicy">Optional runtime index-level string comparison policy for managed residual comparison and prepared membership fallback.</param>
    /// <returns>A string index facade over the exact and maintained projection indexes.</returns>
    public LibraDexStringScalar8Index CreateOrOpen(
        StringKeys stringKeys = StringKeys.ExactAndFolded,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward,
        string? foldedCulture = null,
        string? sortKeyCulture = null,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open(stringComparisonPolicy)
            : Create(stringKeys, directions, foldedCulture, sortKeyCulture, stringComparisonPolicy);
    }

    private CatalogIndexMetadata CreateStringMetadata(
        StringKeys stringKeys,
        int exactReversedSlot,
        int foldedSlot,
        int sortKeySlot,
        int foldedReversedSlot,
        LibraDexProjectionDirectionSet directions,
        string? foldedCulture,
        string? sortKeyCulture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy)
    {
        List<LibraDexIndexProjectionSpec> projections = CreateProjectionKinds(stringKeys)
            .Select(kind => new LibraDexIndexProjectionSpec(kind, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending))
            .ToList();
        if (foldedReversedSlot >= 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Reversed, LibraDexIndexSortOrder.Ascending));
        }
        if (exactReversedSlot >= 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, LibraDexIndexSortOrder.Ascending));
        }

        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(string)),
            GetStableTypeName(typeof(ulong)),
            CatalogIndexKeyFamily.String,
            CatalogIndexIdentityFamily.Scalar,
            IndexKeys.NonUnique,
            stringKeys,
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            LibraDexIndexSortOrder.Ascending,
            projections,
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            StringVarKeyPhysicalMaxLength,
            0,
            exactReversedSlot,
            foldedSlot,
            sortKeySlot,
            foldedReversedSlot,
            foldedCulture ?? string.Empty,
            sortKeyCulture ?? string.Empty,
            stringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            stringComparisonPolicy?.CompareOptions ?? CompareOptions.None,
            stringComparisonPolicy?.CultureName ?? string.Empty,
            stringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true);
    }

    private static IReadOnlyList<LibraDexIndexProjectionKind> CreateProjectionKinds(StringKeys stringKeys)
    {
        List<LibraDexIndexProjectionKind> kinds = new();
        if ((stringKeys & StringKeys.Exact) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.Exact);
        }

        if ((stringKeys & StringKeys.Folded) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.FoldedText);
        }

        if ((stringKeys & StringKeys.SortKey) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.SortKey);
        }

        return kinds;
    }

    private static void ValidateStringInfo(CatalogIndexInfo info)
    {
        if (info.KeyFamily != CatalogIndexKeyFamily.String ||
            info.IdentityFamily != CatalogIndexIdentityFamily.Scalar ||
            info.VarKeyMaxKeyLength <= 0)
        {
            throw new InvalidDataException("The persisted catalog entry is not a reopenable string/scalar var-key index.");
        }
    }

    private LibraDexStringComparisonPolicy? ResolveOpenStringComparisonPolicy(CatalogIndexInfo info, LibraDexStringComparisonPolicy? runtimePolicy)
    {
        return runtimePolicy ??
            catalog.Options.StringComparisonPolicy ??
            LibraDexStringComparisonPolicy.FromPersisted(
                info.StringComparisonPolicyKind,
                info.StringComparisonCulture,
                info.StringComparisonCompareOptions,
                info.StringComparisonCustomComparerTypeName);
    }

    private static string GetStableTypeName(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
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
    private readonly LibraDexProjectionDirectionSet directions;

    internal CatalogNamedTypedIndexBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth,
        CatalogIndexKeyFamily keyFamily,
        CatalogIndexIdentityFamily identityFamily,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.keyWidth = keyWidth;
        this.identityWidth = identityWidth;
        this.keyFamily = keyFamily;
        this.identityFamily = identityFamily;
        this.directions = directions;
    }

    /// <summary>
    /// Creates the grouped index in the next available catalog directory slot.<br/>
    /// Named grouped indexes intentionally avoid exposing slot selection as normal developer ceremony.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> Create(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex index already exists.");
        }

        IndexOptions resolvedOptions = ResolveOptions(keys, options);
        LibraDexIndexShapeSpec? logicalShape = CreateLogicalShape(resolvedOptions.Keys);
        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = CreateExactReversedProjection(resolvedOptions, out int exactReversedProjectionSlotIndex);
        return catalog.CreateGenericIndex<TKey, TIdentity>(
            name,
            owner.ResolveCreateSlot(null),
            resolvedOptions,
            keyWidth,
            identityWidth,
            keyFamily,
            identityFamily,
            group,
            logicalShape,
            exactReversedProjectionSlotIndex,
            exactReversedProjection);
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
    /// <param name="keys">The index-wide duplicate-key contract for a new index, or fallback contract for an older entry without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle owned by the catalog lifetime.</returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open(keys, options)
            : Create(keys, options);
    }

    private static IndexOptions ResolveOptions(IndexKeys keys, IndexOptions? options)
    {
        return options ?? new IndexOptions { Keys = keys };
    }

    /// <summary>
    /// Creates logical shape metadata for fixed binary-key indexes that request maintained byte-direction projections.<br/>
    /// Non-binary typed builders return <see langword="null"/> so existing scalar metadata remains unchanged unless projection intent exists.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract selected for this index.</param>
    /// <returns>A logical shape descriptor for binary projection planning, or <see langword="null"/> for ordinary scalar indexes.</returns>
    private LibraDexIndexShapeSpec? CreateLogicalShape(IndexKeys keys)
    {
        if (keyFamily != CatalogIndexKeyFamily.Blob || typeof(TKey) != typeof(byte[]))
        {
            return null;
        }

        List<LibraDexIndexProjectionSpec> projections = new()
        {
            new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending)
        };
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, LibraDexIndexSortOrder.Ascending));
        }

        return new LibraDexIndexShapeSpec(
            group,
            name,
            typeof(TKey),
            typeof(TIdentity),
            keyFamily,
            identityFamily,
            keys,
            StringKeys.Exact,
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            LibraDexIndexSortOrder.Ascending,
            projections,
            Array.Empty<LibraDexCompositeKeyPartSpec>());
    }

    /// <summary>
    /// Creates the hidden exact reversed projection index for fixed binary-key indexes when the builder requested reversed byte direction.<br/>
    /// The projection is opened and maintained by the returned primary index, keeping the public handle as `LibraDexIndex&lt;byte[], TIdentity&gt;`.<br/>
    /// </summary>
    /// <param name="options">The resolved options for the projection's physical index.</param>
    /// <param name="slotIndex">Receives the hidden projection slot, or -1 when no projection was created.</param>
    /// <returns>The typed hidden projection handle, or <see langword="null"/> when no reversed projection was requested.</returns>
    private LibraDexIndex<TKey, TIdentity>? CreateExactReversedProjection(IndexOptions options, out int slotIndex)
    {
        slotIndex = -1;
        if (typeof(TKey) != typeof(byte[]) ||
            keyFamily != CatalogIndexKeyFamily.Blob ||
            (directions & LibraDexProjectionDirectionSet.Reversed) == 0)
        {
            return null;
        }

        slotIndex = owner.ResolveCreateSlot(null);
        LibraDexIndex<byte[], TIdentity> projection = catalog.CreateGenericIndex<byte[], TIdentity>(
            $"{name}#exact-rev",
            slotIndex,
            options,
            keyWidth,
            identityWidth,
            CatalogIndexKeyFamily.Blob,
            identityFamily,
            group: string.Empty);
        return (LibraDexIndex<TKey, TIdentity>)(object)projection;
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
/// <param name="DateTimeKeyEncoding">The persisted DateTime-like key encoding contract recorded in rich metadata.</param>
/// <param name="Directions">The persisted byte-direction projection contract recorded in rich metadata.</param>
/// <param name="SortOrder">The persisted physical sort-order intent recorded in rich metadata.</param>
/// <param name="Projections">The persisted logical projection descriptors recorded in rich metadata.</param>
/// <param name="CompositeParts">The persisted composite-key part descriptors recorded in rich metadata.</param>
/// <param name="VarKeyMaxKeyLength">The persisted maximum physical variable key length for var-key public facades.</param>
/// <param name="VarIdentityMaxLength">The persisted maximum variable identity length for var-identity public facades, or zero when not applicable.</param>
/// <param name="ExactReversedProjectionSlotIndex">The owned reversed exact-text projection slot, or -1 when no exact suffix projection is maintained.</param>
/// <param name="FoldedProjectionSlotIndex">The owned folded-text projection slot, or -1 when no folded projection is maintained.</param>
/// <param name="SortKeyProjectionSlotIndex">The owned sort-key projection slot, or -1 when no sort-key projection is maintained.</param>
/// <param name="FoldedReversedProjectionSlotIndex">The owned reversed folded-text projection slot, or -1 when no suffix projection is maintained.</param>
/// <param name="FoldedCulture">The folded projection culture name, or empty for invariant/not applicable.</param>
/// <param name="SortKeyCulture">The sort-key projection culture name, or empty for invariant/not applicable.</param>
/// <param name="StringComparisonPolicyKind">The persisted managed string comparison policy kind, or invariant for entries without policy metadata.</param>
/// <param name="StringComparisonCompareOptions">The persisted managed string comparison options for residual comparison and prepared membership.</param>
/// <param name="StringComparisonCulture">The persisted managed string comparison culture name, or empty for invariant/not applicable.</param>
/// <param name="StringComparisonCustomComparerTypeName">The persisted custom comparer type-name sentinel, or empty when not custom/not recorded.</param>
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
    DateTimeKeyEncoding DateTimeKeyEncoding,
    LibraDexProjectionDirectionSet Directions,
    LibraDexIndexSortOrder SortOrder,
    IReadOnlyList<LibraDexIndexProjectionSpec> Projections,
    IReadOnlyList<LibraDexCompositeKeyPartSpec> CompositeParts,
    int VarKeyMaxKeyLength,
    int VarIdentityMaxLength,
    int ExactReversedProjectionSlotIndex,
    int FoldedProjectionSlotIndex,
    int SortKeyProjectionSlotIndex,
    int FoldedReversedProjectionSlotIndex,
    string FoldedCulture,
    string SortKeyCulture,
    LibraDexStringComparisonPolicyKind StringComparisonPolicyKind,
    CompareOptions StringComparisonCompareOptions,
    string StringComparisonCulture,
    string StringComparisonCustomComparerTypeName)
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
            DateTimeKeyEncoding,
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
                DateTimeKeyEncoding.CalendarSdt,
                LibraDexProjectionDirectionSet.Forward,
                LibraDexIndexSortOrder.Ascending,
                Array.Empty<LibraDexIndexProjectionSpec>(),
                Array.Empty<LibraDexCompositeKeyPartSpec>(),
                0,
                0,
                -1,
                -1,
                -1,
                -1,
                string.Empty,
                string.Empty,
                LibraDexStringComparisonPolicyKind.Invariant,
                CompareOptions.None,
                string.Empty,
                string.Empty,
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
            metadata.DateTimeKeyEncoding,
            metadata.Directions,
            metadata.SortOrder,
            metadata.Projections,
            metadata.CompositeParts,
            metadata.VarKeyMaxKeyLength,
            metadata.VarIdentityMaxLength,
            metadata.ExactReversedProjectionSlotIndex,
            metadata.FoldedProjectionSlotIndex,
            metadata.SortKeyProjectionSlotIndex,
            metadata.FoldedReversedProjectionSlotIndex,
            metadata.FoldedCulture,
            metadata.SortKeyCulture,
            metadata.StringComparisonPolicyKind,
            metadata.StringComparisonCompareOptions,
            metadata.StringComparisonCulture,
            metadata.StringComparisonCustomComparerTypeName);
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
