using LibraDex.Layouts;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace LibraDex;

internal static class CatalogIndexFactoryOptions
{
    /// <summary>
    /// Resolves the effective index options for catalog factory create/open calls.<br/>
    /// Caller-supplied options are preserved exactly; otherwise the duplicate-key shortcut is converted into the standard options object used by catalog creation and open paths.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key shortcut supplied by the factory method.</param>
    /// <param name="options">Optional caller-supplied index options.</param>
    /// <returns>The caller-supplied options, or a new options object carrying <paramref name="keys"/>.</returns>
    internal static IndexOptions Resolve(IndexKeys keys, IndexOptions? options)
        => options ?? new IndexOptions { Keys = keys };
}

/// <summary>
/// Provides index factories under a catalog.<br/>
/// The shape is intentionally IntelliSense-friendly: callers choose an index set or key class, then the identity class, then the terminal verb `Create`, `Open`, or `CreateOrOpen`.<br/>
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
    /// Gets the index-set index-set surface for the supplied identity universe name.<br/>
    /// This is shorthand for `IndexSet(name)` and is the preferred low-friction entry point for catalog-backed condition code.<br/>
    /// </summary>
    /// <param name="name">The index-set name.</param>
    /// <returns>An index-set index-set surface.</returns>
    public CatalogIdentityGroupIndexes this[string name] => IndexSet(name);

    /// <summary>
    /// Gets the index-set index-set surface for the supplied identity universe name.<br/>
    /// Index sets declare that a collection of indexes share one identity universe for multi-criteria composition.<br/>
    /// </summary>
    /// <param name="name">The index-set name.</param>
    /// <returns>An index-set index-set surface.</returns>
    public CatalogIdentityGroupIndexes IndexSet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CatalogIdentityGroupIndexes(catalog, this, name);
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
        => TryGetInfo(slotIndex, out _);

    /// <summary>
    /// Gets whether an active index with the supplied name exists.<br/>
    /// Name lookup is metadata-oriented in this slice; slot identity remains the authoritative open/create selector until richer catalog metadata is connected.<br/>
    /// </summary>
    /// <param name="name">The public index name to inspect.</param>
    /// <returns><see langword="true"/> when any active slot has the supplied name; otherwise <see langword="false"/>.</returns>
    public bool Exists(string name)
        => TryGetInfo(name, out _);

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
    /// Tries to get public metadata for an active index by index-set name and index name.<br/>
    /// This is the durable lookup shape behind `catalog.Indexes[indexSet][name]`; it uses rich metadata when present and does not rely on parsing fixed slot names.<br/>
    /// </summary>
    /// <param name="indexSet">The index-set name to inspect.</param>
    /// <param name="name">The public index name inside the index set.</param>
    /// <param name="info">Receives index metadata when a matching active slot is found.</param>
    /// <returns><see langword="true"/> when metadata was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetInfo(string indexSet, string name, out CatalogIndexInfo info)
    {
        ArgumentNullException.ThrowIfNull(indexSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            CatalogIndexInfo candidate = CreateInfo(activeSlots[i]);
            if (string.Equals(candidate.Group, indexSet, StringComparison.Ordinal) &&
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
    /// Permanently removes one logical index definition from the supplied index set.<br/>
    /// The catalog directory deactivates the owner slot and every owned text-projection companion atomically, then retires allocator-owned current-format topology only after the root removal is durable and coherent readers have quiesced.<br/>
    /// Legacy, append-fallback, malformed, or otherwise unclassified extents remain conservatively unreachable for later closed-file compaction rather than being guessed into a reusable allocation class.<br/>
    /// Existing handles to the dropped index become detached and must not be used after this method returns.<br/>
    /// </summary>
    /// <param name="indexSet">The identity-universe/index-set name containing the index.<br/></param>
    /// <param name="name">The logical index name to remove.<br/></param>
    /// <returns><c>true</c> when an active logical index was dropped; otherwise <c>false</c>.<br/></returns>
    public bool Drop(string indexSet, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!TryGetInfo(indexSet, name, out CatalogIndexInfo info))
            return false;

        catalog.DropIndex(info);
        return true;
    }

    /// <summary>
    /// Permanently removes every logical index definition in one index set.<br/>
    /// All logical-owner and owned projection slots are deactivated in one durable catalog-directory commit, so reopen observes either the complete prior set or no set.<br/>
    /// Allocator-owned current-format topology is retired only after the directory removal is durable and coherent readers have quiesced; legacy or unclassified extents remain conservatively unreachable for later closed-file compaction.<br/>
    /// Existing handles to any dropped index become detached and must not be used after this method returns.<br/>
    /// </summary>
    /// <param name="indexSet">The identity-universe/index-set name to remove.<br/></param>
    /// <returns><c>true</c> when at least one active logical index was dropped; otherwise <c>false</c>.<br/></returns>
    public bool DropIndexSet(string indexSet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexSet);
        CatalogIndexInfo[] infos = List();
        var count = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            if (string.Equals(infos[i].Group, indexSet, StringComparison.Ordinal))
                count++;
        }

        if (count == 0)
            return false;

        var owners = new CatalogIndexInfo[count];
        var ownerIndex = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            if (string.Equals(infos[i].Group, indexSet, StringComparison.Ordinal))
                owners[ownerIndex++] = infos[i];
        }

        catalog.DropIndexSet(indexSet, owners);
        return true;
    }

    /// <summary>
    /// Creates an index from a logical shape descriptor.<br/>
    /// This is the programmatic counterpart to the grouped fluent factories: higher-level adapters can build one shape object, then pass it to catalog creation without reconstructing generic factory calls.<br/>
    /// </summary>
    /// <param name="shape">The logical index shape to create.<br/></param>
    /// <param name="options">Optional per-index options override; null uses the contracts embedded in <paramref name="shape"/>.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the created index's inversion is complete.<br/></param>
    /// <returns>A strict non-generic index handle for the created index.<br/></returns>
    public IIndex Create(
        LibraDexIndexShapeSpec shape,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
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

        return catalog.CreateIndex(shape, ResolveCreateSlot(null), options, identityLookupMode);
    }

    /// <summary>
    /// Opens an existing index that matches a logical shape descriptor.<br/>
    /// The shape is used as an expected contract, and persisted catalog metadata is validated before the non-generic handle is returned.<br/>
    /// </summary>
    /// <param name="shape">The expected logical index shape.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the opened index's inversion is complete.<br/></param>
    /// <returns>A strict non-generic index handle for the opened index.<br/></returns>
    public IIndex Open(
        LibraDexIndexShapeSpec shape,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!TryGetInfo(shape.Group, shape.Name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested LibraDex index shape does not exist.");
        }

        ValidateShape(shape, info);
        IIndex index = catalog.OpenIndex(info);
        if (identityLookupMode != IdentityLookupMode.Explicit)
            index.IdentityLookup.Configure(identityLookupMode);
        return index;
    }

    /// <summary>
    /// Opens an existing index matching a logical shape descriptor, or creates it when missing.<br/>
    /// This keeps descriptor-driven setup low-friction for generated code while preserving metadata validation on the open branch.<br/>
    /// </summary>
    /// <param name="shape">The logical index shape to open or create.<br/></param>
    /// <param name="options">Optional per-index options override for the create branch; null uses the contracts embedded in <paramref name="shape"/>.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the returned index's inversion is complete.<br/></param>
    /// <returns>A strict non-generic index handle for the existing or created index.<br/></returns>
    public IIndex CreateOrOpen(
        LibraDexIndexShapeSpec shape,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return TryGetInfo(shape.Group, shape.Name, out _)
            ? Open(shape, identityLookupMode)
            : Create(shape, options, identityLookupMode);
    }

    /// <summary>
    /// Lists index-set names present in rich catalog index metadata.<br/>
    /// Empty groups from older scaffold entries are omitted because they do not represent a deliberate index-set declaration.<br/>
    /// </summary>
    /// <returns>Distinct index-set names in catalog directory order.</returns>
    public string[] IndexSetNames()
    {
        CatalogIndexInfo[] infos = List();
        List<string> groups = new();
        for (int i = 0; i < infos.Length; i++)
        {
            string name = infos[i].Group;
            if (name.Length == 0 || groups.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            groups.Add(name);
        }

        return groups.ToArray();
    }

    /// <summary>
    /// Lists active index metadata grouped by index-set name across the whole catalog.<br/>
    /// Empty set names from older scaffold entries are omitted because they do not represent an intentional identity universe.<br/>
    /// </summary>
    /// <returns>Disconnected index-set metadata snapshots in catalog directory order.</returns>
    public CatalogIndexSetInfo[] IndexSets()
    {
        CatalogIndexInfo[] infos = List();
        List<CatalogIndexSetInfo> sets = new();
        for (int i = 0; i < infos.Length; i++)
        {
            string name = infos[i].Group;
            if (name.Length == 0)
            {
                continue;
            }

            int existingIndex = -1;
            for (int j = 0; j < sets.Count; j++)
            {
                if (string.Equals(sets[j].Name, name, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                sets.Add(new CatalogIndexSetInfo(name, new[] { infos[i] }));
                continue;
            }

            CatalogIndexInfo[] current = sets[existingIndex].Indexes;
            CatalogIndexInfo[] next = new CatalogIndexInfo[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = infos[i];
            sets[existingIndex] = new CatalogIndexSetInfo(name, next);
        }

        return sets.ToArray();
    }

    private static void ValidateShape(LibraDexIndexShapeSpec shape, CatalogIndexInfo info)
    {
        if (!string.Equals(shape.Group, info.Group, StringComparison.Ordinal) ||
            !string.Equals(shape.Name, info.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The requested LibraDex index shape does not match the persisted group/index-set metadata.");
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
/// Provides index factories scoped to one index set.<br/>
/// The index-set name is persisted into rich index metadata so grouped lookup can be rehydrated after reopening a catalog.<br/>
/// </summary>
public sealed partial class CatalogIdentityGroupIndexes
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;

    internal CatalogIdentityGroupIndexes(Catalog catalog, CatalogIndexFactories owner, string name)
    {
        this.catalog = catalog;
        this.owner = owner;
        Name = name;
        Batch = catalog.GetIdentityGroupBatch(name);
    }

    /// <summary>
    /// Gets the index-set name represented by this index-set surface.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the catalog-level required identity CLR type, or null when this catalog allows mixed identity types.<br/>
    /// Owners such as Abraxas use this to reject unconstrained catalogs before record IDs can be mirrored into indexes with an incompatible identity contract.<br/>
    /// </summary>
    public Type? RequiredIdentityType => catalog.Options.RequiredIdentityType;

    internal Catalog Catalog => catalog;

    /// <summary>
    /// Gets the index-set durability batch manager for indexes that share this identity universe.<br/>
    /// When enabled, participating index inserts defer durability through this set manager until `Commit` or `CommitAndDisable` is called.<br/>
    /// </summary>
    public CatalogIdentityGroupBatchManager Batch { get; }

    /// <summary>
    /// Gets identity-family condition stubs for this index set.<br/>
    /// Identity family selection is separate from `.As...` key-family selection so catalog-root external sources can carry identity type without looking like an index-key projection.<br/>
    /// </summary>
    public CatalogIdentityGroupIdentityTypeSelector Identities => new(this);

    /// <summary>
    /// Creates the narrow Abraxas-facing identity query adapter for this index set.<br/>
    /// The adapter executes completed condition descriptors through the existing catalog condition bridge and returns only caller-owned identities, leaving record hydration and mutation outside LibraDex.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the Abraxas integration boundary.</typeparam>
    /// <returns>An identity-only query adapter bound to this index set.</returns>
    public AbraxasIdentityQueryAdapter<TIdentity> AbraxasIdentityQuery<TIdentity>()
    {
        catalog.ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(AbraxasIdentityQuery));
        return new(this);
    }

    /// <summary>
    /// Creates the narrow Abraxas-facing identity write adapter for this index set.<br/>
    /// The adapter binds queued writer access to named generic indexes while keeping source-object mutation and hydration outside LibraDex.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the Abraxas integration boundary.</typeparam>
    /// <returns>An identity-only write adapter bound to this index set.</returns>
    public AbraxasIdentityWriteAdapter<TIdentity> AbraxasIdentityWrite<TIdentity>()
    {
        catalog.ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(AbraxasIdentityWrite));
        return new(this);
    }

    /// <summary>
    /// Gets the optional inverse key-map surface for this index set.<br/>
    /// The inverse map is scoped to one identity universe and answers identity-to-indexed-key lookups for selected indexes.<br/>
    /// This runtime implementation builds from current forward index tuples and keeps the public lifecycle shape reserved for the durable maintained inverse index.<br/>
    /// </summary>
    public CatalogIndexSetInverse Inverse => catalog.GetIndexSetInverse(this);

    /// <summary>
    /// Permanently removes one logical index definition from this index set.<br/>
    /// The operation deactivates the logical owner and any owned text-projection companions in one durable catalog-directory commit, then retires allocator-owned current-format topology after publication and coherent-reader safety.<br/>
    /// Legacy or unclassified topology remains conservatively unreachable for later closed-file compaction.<br/>
    /// Existing handles to the dropped index become detached and must not be used after this method returns.<br/>
    /// </summary>
    /// <param name="name">The logical index name to remove.<br/></param>
    /// <returns><c>true</c> when an active logical index was dropped; otherwise <c>false</c>.<br/></returns>
    public bool Drop(string name)
        => owner.Drop(Name, name);

    /// <summary>
    /// Permanently removes this complete index set in one durable catalog-directory commit.<br/>
    /// Every logical index and owned projection becomes unreachable together; allocator-owned current-format topology becomes reusable after publication and coherent-reader safety, while legacy or unclassified topology remains for catalog compaction.<br/>
    /// Existing handles to any index in this set become detached and must not be used after this method returns.<br/>
    /// </summary>
    /// <returns><c>true</c> when at least one active logical index was dropped; otherwise <c>false</c>.<br/></returns>
    public bool Drop()
        => owner.DropIndexSet(Name);

    /// <summary>
    /// Starts an inverse-key-map condition from identities rather than from a forward key lookup.<br/>
    /// Use this when the caller already has identities, or when an identity range should be filtered by indexed key values without object hydration.<br/>
    /// </summary>
    public LibraDexInverseIdentityTypeSelector WhereInverse => new(Name, Inverse);

    /// <summary>
    /// Starts a negated first condition clause for this index set.<br/>
    /// This keeps root negation on the catalog-backed index-set surface, so callers can write `catalog.IndexSet("users").Not.Where("status")...` or `catalog.IndexSet("users").Not.Group(fragment)` without falling back to the legacy explicit condition root.<br/>
    /// </summary>
    public LibraDexConditionClause Not => LibraDexCondition.Group(Name).Not;

    /// <summary>
    /// Adds a completed condition fragment as the first node for this index set.<br/>
    /// The fragment must belong to the same index set, which keeps reusable grouped predicates on the catalog-backed surface instead of requiring callers to drop to `LibraDexCondition.Group(...)` manually.<br/>
    /// </summary>
    /// <param name="groupCondition">The completed grouped condition fragment.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd Group(LibraDexConditionEndCondition groupCondition)
        => LibraDexCondition.Group(Name).Group(groupCondition);

    /// <summary>
    /// Starts a low-friction condition builder by selecting an index name inside this index set.<br/>
    /// Key typing and projection intent are selected after the index through members such as `.AsString`, `.AsGuid`, and `.AsInt64`, keeping the public grammar index-first instead of type-first.<br/>
    /// The selected index name remains descriptor-shaped until the condition is materialized, so reusable conditions can still be built before indexes are opened.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this index set.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(string indexName)
        => LibraDexCondition.Group(Name).Where(indexName);

    /// <summary>
    /// Starts a reusable condition with an execution-time index-name parameter.<br/>
    /// The selected name is read once when the condition executes, allowing the same descriptor to target another compatible index without a caller lambda.<br/>
    /// </summary>
    /// <param name="indexName">The parameter containing the current index name.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(LibraDexParameter<string> indexName)
        => LibraDexCondition.Group(Name).Where(indexName);

    /// <summary>
    /// Starts a reusable condition with an execution-time opened-index parameter.<br/>
    /// The selected handle is snapshotted when execution begins and its group remains validated by the ordinary condition materializer.<br/>
    /// </summary>
    /// <typeparam name="TIndex">The opened index handle type.</typeparam>
    /// <param name="index">The parameter containing the current opened index.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where<TIndex>(LibraDexParameter<TIndex> index)
        where TIndex : IIndex
        => LibraDexCondition.Group(Name).Where(index);

    /// <summary>
    /// Opens an index by name inside this index set using metadata-driven catalog lookup.<br/>
    /// This is the canonical grouped untyped open path for callers that need a handle before choosing a key family or terminal operation.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this index set.</param>
    /// <returns>An opened index handle.</returns>
    public IIndex Index(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return this[indexName].Open();
    }

    /// <summary>
    /// Opens a typed index by name inside this index set and validates the requested key and identity types against catalog metadata.<br/>
    /// This is the compiler-checked counterpart to <see cref="Index(string)"/> for ordinary non-composite indexes.<br/>
    /// </summary>
    /// <typeparam name="TKey">The expected public key type.</typeparam>
    /// <typeparam name="TIdentity">The expected public identity type.</typeparam>
    /// <param name="indexName">The index name inside this index set.</param>
    /// <param name="keys">The fallback duplicate-key contract for older entries without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed index handle.</returns>
    public LibraDexIndex<TKey, TIdentity> Index<TKey, TIdentity>(
        string indexName,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return this[indexName].Open<TKey, TIdentity>(keys, options);
    }

    /// <summary>
    /// Starts a typed condition builder by opening a named index inside this index set and validating the requested key and identity types.<br/>
    /// The returned opened-index grammar is the short path for compiler-checked ordinary index predicates such as `cat.Where&lt;int, long&gt;("age").GreaterOrEqual(18)`.<br/>
    /// </summary>
    /// <typeparam name="TKey">The expected public key type.</typeparam>
    /// <typeparam name="TIdentity">The expected public identity type.</typeparam>
    /// <param name="indexName">The index name inside this index set.</param>
    /// <param name="keys">The fallback duplicate-key contract for older entries without rich metadata.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A typed opened-index condition root.</returns>
    public LibraDexIndexWhere<TKey, TIdentity> Where<TKey, TIdentity>(
        string indexName,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return Index<TKey, TIdentity>(indexName, keys, options).Where;
    }

    /// <summary>
    /// Starts a low-friction condition builder from an already opened index instance in this index set.<br/>
    /// The handle supplies the index name and verifies the index set immediately; key-type compatibility is still checked when the selected `.As...` family is materialized against the index.<br/>
    /// This keeps instance-based conditions concise without making callers repeat the index name.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied index handle belongs to a different LibraDex index set.");
        }

        return Where(index.Name);
    }

    /// <summary>
    /// Starts a low-friction condition builder from a generic typed index instance and selects the index key type automatically.<br/>
    /// This enables `catalog.Indexes["users"].Where(age).GreaterOrEqual(18)` for typed scalar handles while preserving the existing `.Where(index).As...` form when callers need a richer projection family.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type carried by the opened index handle.</typeparam>
    /// <typeparam name="TIdentity">The identity type carried by the opened index handle.</typeparam>
    /// <param name="index">The opened generic index instance to select.</param>
    /// <returns>A typed operator for the selected index key type.</returns>
    public LibraDexConditionOperator<TKey> Where<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index)
        => Where((IIndex)index).AsKnown<TKey>();

    /// <summary>
    /// Starts a low-friction condition builder from a string index facade and selects string operators automatically.<br/>
    /// This enables `catalog.Indexes["users"].Where(name).StartsWith("A")` without requiring `.AsString` when the handle itself is already string-typed.<br/>
    /// </summary>
    /// <param name="index">The opened string index instance to select.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexStringConditionOperator Where(LibraDexStringScalar8Index index)
        => Where((IIndex)index).AsString;

    /// <summary>
    /// Starts a composite condition builder by selecting a composite index name inside this index set.<br/>
    /// This string-name path validates that the selected catalog entry is composite, then returns composite-specific IntelliSense such as `KeyPart(...)` and `FullKey(...)` without requiring low-level descriptor ceremony.<br/>
    /// </summary>
    /// <param name="indexName">The composite index name inside this index set.</param>
    /// <returns>A composite condition root for the selected index.</returns>
    public LibraDexCompositeConditionWhere CompositeWhere(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (!owner.TryGetInfo(Name, indexName, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex composite index does not exist.");
        }

        if (info.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new InvalidOperationException($"Index '{indexName}' in index set '{Name}' is {info.KeyFamily}, not Composite.");
        }

        return LibraDexCondition.Group(Name).Where(indexName).CompositeWhereRoot;
    }

    /// <summary>
    /// Opens a typed two-part composite index by name and validates its persisted part and identity types.<br/>
    /// The returned handle exposes tuple-style `Where.Part1` and `Where.Part2` selectors, while still preserving named `KeyPart(...)` access for dynamic callers.<br/>
    /// </summary>
    /// <typeparam name="TPart1">The expected first composite key-part type.</typeparam>
    /// <typeparam name="TPart2">The expected second composite key-part type.</typeparam>
    /// <typeparam name="TIdentity">The expected identity type.</typeparam>
    /// <param name="name">The composite index name inside this index set.</param>
    /// <returns>A typed composite index handle.</returns>
    public LibraDexCompositeIndex<TPart1, TPart2, TIdentity> CompositeIndex<TPart1, TPart2, TIdentity>(string name)
        => new(OpenTypedCompositeIndex(name, typeof(TIdentity), typeof(TPart1), typeof(TPart2)));

    /// <summary>
    /// Opens a typed three-part composite index by name and validates its persisted part and identity types.<br/>
    /// The returned handle exposes tuple-style `Where.Part1`, `Where.Part2`, and `Where.Part3` selectors for compiler-checked ordinal parts.<br/>
    /// </summary>
    /// <typeparam name="TPart1">The expected first composite key-part type.</typeparam>
    /// <typeparam name="TPart2">The expected second composite key-part type.</typeparam>
    /// <typeparam name="TPart3">The expected third composite key-part type.</typeparam>
    /// <typeparam name="TIdentity">The expected identity type.</typeparam>
    /// <param name="name">The composite index name inside this index set.</param>
    /// <returns>A typed composite index handle.</returns>
    public LibraDexCompositeIndex<TPart1, TPart2, TPart3, TIdentity> CompositeIndex<TPart1, TPart2, TPart3, TIdentity>(string name)
        => new(OpenTypedCompositeIndex(name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3)));

    /// <summary>
    /// Gets a name-first builder for an index inside this index set.<br/>
    /// </summary>
    /// <param name="name">The index name inside this index set.</param>
    /// <returns>A name-first index builder.</returns>
    public CatalogNamedIndexBuilder this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return new CatalogNamedIndexBuilder(catalog, owner, Name, name);
        }
    }

    private LibraDexRoutedCompositeIndex OpenTypedCompositeIndex(string name, Type identityType, params Type[] partTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(identityType);
        ArgumentNullException.ThrowIfNull(partTypes);
        if (!owner.TryGetInfo(Name, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex composite index does not exist.");
        }

        if (info.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new InvalidOperationException($"Index '{name}' in index set '{Name}' is {info.KeyFamily}, not Composite.");
        }

        if (info.CompositeParts.Count != partTypes.Length)
        {
            throw new InvalidOperationException($"Composite index '{name}' has {info.CompositeParts.Count} key parts, not {partTypes.Length}.");
        }

        Type? persistedIdentityType = Type.GetType(info.IdentityTypeName, throwOnError: false);
        if (persistedIdentityType != identityType)
        {
            throw new InvalidOperationException($"Composite index '{name}' stores identity type {info.IdentityTypeName}, not {identityType.FullName}.");
        }

        for (int i = 0; i < partTypes.Length; i++)
        {
            if (info.CompositeParts[i].KeyType != partTypes[i])
            {
                throw new InvalidOperationException($"Composite index '{name}' part {i + 1} stores {info.CompositeParts[i].KeyType.FullName}, not {partTypes[i].FullName}.");
            }
        }

        IIndex opened = catalog.OpenIndex(info);
        return opened is LibraDexRoutedCompositeIndex composite
            ? composite
            : throw new InvalidOperationException("The metadata-driven composite index open did not return a routed composite index handle.");
    }

    /// <summary>
    /// Lists active indexes that declare this index set in rich catalog metadata.<br/>
    /// </summary>
    /// <returns>Disconnected metadata snapshots for indexes in this index set.</returns>
    public CatalogIndexInfo[] List()
    {
        CatalogIndexInfo[] all = owner.List();
        List<CatalogIndexInfo> matches = new();
        for (int i = 0; i < all.Length; i++)
        {
            if (string.Equals(all[i].Group, Name, StringComparison.Ordinal))
            {
                matches.Add(all[i]);
            }
        }

        return matches.ToArray();
    }

    /// <summary>
    /// Tries to get public metadata for an active index inside this index set.<br/>
    /// </summary>
    /// <param name="name">The index name inside this index set.</param>
    /// <param name="info">Receives index metadata when a matching active slot is found.</param>
    /// <returns><see langword="true"/> when metadata was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetInfo(string name, out CatalogIndexInfo info)
        => owner.TryGetInfo(Name, name, out info);

    /// <summary>
    /// Gets whether an active index definition exists in this identity group.<br/>
    /// The method inspects catalog metadata only; use an index count or entry enumeration when the question is whether that index currently contains data.<br/>
    /// </summary>
    /// <param name="name">The index definition name.</param>
    /// <returns><see langword="true"/> when the definition is active.</returns>
    public bool HasIndex(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return owner.TryGetInfo(Name, name, out _);
    }

    /// <summary>
    /// Gets whether at least one identity matches a completed condition in this identity group.<br/>
    /// The condition is materialized through the normal execution planner, including execution-time availability guards; result projection and return-shape descriptors are intentionally irrelevant to this Boolean producer.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to test.</param>
    /// <returns><see langword="true"/> when the condition produces at least one identity.</returns>
    public bool Exists(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        return LibraDexIdentityExecutionPlanner.Exists(criterion, IdentityDeduplication.Distinct);
    }

    /// <summary>
    /// Materializes identities for a completed condition over this index set.<br/>
    /// Index names referenced by the condition are resolved from the index-set metadata at materialization time, preserving deferred selector binding without making callers build resolver dictionaries by hand.<br/>
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
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        return criterion.IDsWith(ordering, deduplication, skip, take, bookmark).ToList<TIdentity>();
    }

    /// <summary>
    /// Opens a forward-only identity cursor for a completed condition over this index set.<br/>
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
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        return new LibraDexIdentityCursor<TIdentity>(
            criterion.IDsWith(ordering, deduplication, skip, take, bookmark).Iterate<TIdentity>());
    }

    /// <summary>
    /// Opens a target-index cursor for a completed condition over this index set.<br/>
    /// The condition filters identities, while the target index owns the result shape and returns its stored key values directly.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type exposed by the target index.</typeparam>
    /// <typeparam name="TIdentity">The public identity type exposed by the target index.</typeparam>
    /// <param name="targetIndex">The index whose key/identity entries should be streamed.</param>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="skip">The number of matching target-index entries to skip.</param>
    /// <param name="take">The optional maximum number of target-index entries to return.</param>
    /// <param name="direction">The requested target-index key traversal direction.</param>
    /// <returns>A cursor over matching target-index entries.</returns>
    public LibraDexIndexCursor<TKey, TIdentity> GetCursor<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> targetIndex,
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        return GetTargetCursor<TKey, TIdentity>(targetIndex, condition, skip, take, direction);
    }

    /// <summary>
    /// Opens a string target-index cursor for a completed condition over this index set.<br/>
    /// The condition filters identities, while the string target index owns the result shape and returns exact string keys directly.<br/>
    /// </summary>
    /// <param name="targetIndex">The string index whose key/identity entries should be streamed.</param>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="skip">The number of matching target-index entries to skip.</param>
    /// <param name="take">The optional maximum number of target-index entries to return.</param>
    /// <param name="direction">The requested exact-string key traversal direction.</param>
    /// <returns>A cursor over matching string-index entries.</returns>
    public LibraDexIndexCursor<string, ulong> GetCursor(
        LibraDexStringScalar8Index targetIndex,
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        return GetTargetCursor<string, ulong>(targetIndex, condition, skip, take, direction);
    }

    /// <summary>
    /// Materializes key/identity tuples from one target index after applying a completed condition over this index set.<br/>
    /// This non-generic projection is intended for diagnostics, adapters, and workbench views that need to inspect one selected physical index without hydrating source records.<br/>
    /// </summary>
    /// <param name="targetIndex">The selected target index whose key/identity entries should be returned.</param>
    /// <param name="condition">The completed condition used to filter visible identities.</param>
    /// <param name="skip">The number of target-index entries to skip.</param>
    /// <param name="take">The optional maximum number of target-index entries to return.</param>
    /// <param name="direction">The requested target-index key traversal direction.</param>
    /// <returns>Runtime key/identity tuples from the selected target index.</returns>
    public IReadOnlyList<LibraDexRuntimeTuple> GetTuples(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        }

        if (!string.Equals(targetIndex.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The target index belongs to a different LibraDex index set.");
        }

        if (TryReadDirectTargetAllTuples(targetIndex, condition, skip, take, direction, out IReadOnlyList<LibraDexRuntimeTuple>? directRows))
        {
            return directRows;
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        if (TryReadDirectTargetLeafTuples(targetIndex, criterion, skip, take, direction, out IReadOnlyList<LibraDexRuntimeTuple>? leafRows))
        {
            return leafRows;
        }

        List<LibraDexRuntimeTuple> rows = new();
        foreach (LibraDexObjectTuple tuple in LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, targetIndex, skip, take, direction))
        {
            rows.Add(new LibraDexRuntimeTuple(tuple.Key, tuple.Identity));
        }

        return rows;
    }

    /// <summary>
    /// Streams key/identity tuples from one target index after applying a completed condition over this index set.<br/>
    /// This iterator is intended for adapters that need ordered key context without hydrating source records or reaching into internal tuple executor contracts.<br/>
    /// </summary>
    /// <param name="targetIndex">The selected target index whose key/identity entries should be returned.<br/></param>
    /// <param name="condition">The completed condition used to filter visible identities.<br/></param>
    /// <param name="direction">The requested target-index key traversal direction.<br/></param>
    /// <returns>Runtime key/identity tuples from the selected target index.<br/></returns>
    public IEnumerable<LibraDexRuntimeTuple> IterateTuples(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        }

        if (!string.Equals(targetIndex.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The target index belongs to a different LibraDex index set.");
        }

        IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = condition.Leaves;
        if (leaves.Count == 1 &&
            leaves[0].Operator == LibraDexConditionOperatorKind.All &&
            string.Equals(leaves[0].IndexName, targetIndex.Name, StringComparison.Ordinal))
        {
            LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: null, direction);
            if (targetIndex is IIdentityPrimitiveTupleStreamer tupleStreamer)
            {
                foreach (LibraDexObjectTuple tuple in tupleStreamer.IterateTuplePrimitive(request))
                    yield return new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);

                yield break;
            }

            if (targetIndex is IIdentityPrimitiveTupleExecutor tupleExecutor)
            {
                IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(request);
                for (int i = 0; i < tuples.Count; i++)
                    yield return new LibraDexRuntimeTuple(tuples[i].Key, tuples[i].Identity);

                yield break;
            }
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        foreach (LibraDexObjectTuple tuple in LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, targetIndex, skip: 0, take: null, direction))
            yield return new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);
    }

    /// <summary>
    /// Streams every key/identity tuple from one target index in physical key order without requiring a manufactured universal condition.<br/>
    /// Unfiltered selection remains implicit at the developer-facing condition grammar while adapters and ordered-result consumers can request the complete tuple stream directly.<br/>
    /// Execution uses the index's native tuple primitive and therefore preserves explicit null and empty key states supported by binary and text indexes.<br/>
    /// </summary>
    /// <param name="targetIndex">The selected target index whose complete tuple stream should be returned.<br/></param>
    /// <param name="direction">The requested target-index key traversal direction.<br/></param>
    /// <returns>Every runtime key/identity tuple from the selected target index in the requested direction.<br/></returns>
    /// <exception cref="InvalidOperationException">The target index belongs to a different identity group.<br/></exception>
    /// <exception cref="NotSupportedException">The target index does not expose a native tuple primitive.<br/></exception>
    public IEnumerable<LibraDexRuntimeTuple> IterateTuples(
        IIndex targetIndex,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (!string.Equals(targetIndex.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The target index belongs to a different LibraDex index set.");

        LibraDexIdentityPrimitiveRequest request = new(
            LibraDexCriteriaKind.All,
            Array.Empty<object?>(),
            TakeLimit: null,
            direction);
        if (targetIndex is IIdentityPrimitiveTupleStreamer tupleStreamer)
        {
            foreach (LibraDexObjectTuple tuple in tupleStreamer.IterateTuplePrimitive(request))
                yield return new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);

            yield break;
        }

        if (targetIndex is IIdentityPrimitiveTupleExecutor tupleExecutor)
        {
            IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(request);
            for (int i = 0; i < tuples.Count; i++)
                yield return new LibraDexRuntimeTuple(tuples[i].Key, tuples[i].Identity);

            yield break;
        }

        throw new NotSupportedException(
            $"Index '{targetIndex.Name}' does not expose a native tuple primitive for unfiltered iteration.");
    }

    /// <summary>
    /// Streams condition-matched tuples from a target index beginning at an inclusive physical key boundary.<br/>
    /// The boundary narrows the sorted target-index cursor before identity membership filtering, allowing detached bookmarks to seek near their logical anchor without replaying earlier target keys.<br/>
    /// The caller remains responsible for discarding the anchor tuple itself because more than one identity may share the boundary key.<br/>
    /// </summary>
    /// <param name="targetIndex">The sorted target index whose tuples provide result order.<br/></param>
    /// <param name="condition">The frozen completed condition used to select visible identities.<br/></param>
    /// <param name="boundaryValue">The inclusive target-index key at which traversal begins.<br/></param>
    /// <param name="direction">The requested target-index traversal direction.<br/></param>
    /// <returns>Condition-matched tuples beginning at the inclusive key boundary.<br/></returns>
    internal IEnumerable<LibraDexRuntimeTuple> IterateTuplesFrom(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        object? boundaryValue,
        QueryDirection direction)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        if (!string.Equals(targetIndex.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The target index belongs to a different LibraDex index set.");

        IIdentityCriterion filterCriterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        LibraDexCriteriaKind boundaryKind = direction == QueryDirection.Ascending
            ? LibraDexCriteriaKind.AtOrAfter
            : LibraDexCriteriaKind.AtOrBefore;
        IIdentityCriterion boundaryCriterion = LibraDexIdentityCriterion.Leaf(
            targetIndex,
            boundaryKind,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath),
            boundaryValue);
        IIdentityCriterion criterion = boundaryCriterion.And(filterCriterion);
        foreach (LibraDexObjectTuple tuple in LibraDexConditionCursorExecutor.IterateTargetIndexTuples(
            criterion,
            targetIndex,
            skip: 0,
            take: null,
            direction))
        {
            yield return new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);
        }
    }

    /// <summary>
    /// Reads an unfiltered `All` tuple projection from the supplied target index without rematerializing the condition index handle.<br/>
    /// This preserves physical target-index inspection for workbench and diagnostic callers when an index implementation keeps mutable in-memory state that a fresh logical open cannot share.<br/>
    /// The method intentionally handles only the single-leaf, same-index `All` shape; filtered and composed conditions still flow through the condition cursor bridge.<br/>
    /// </summary>
    /// <param name="targetIndex">The selected target index whose tuples should be streamed.<br/></param>
    /// <param name="condition">The completed condition requested by the caller.<br/></param>
    /// <param name="skip">The number of target tuples to skip.<br/></param>
    /// <param name="take">The optional maximum number of target tuples to return.<br/></param>
    /// <param name="direction">The requested tuple traversal direction.<br/></param>
    /// <param name="rows">Receives materialized runtime tuples when the direct path applies.<br/></param>
    /// <returns><see langword="true"/> when the direct target-index path handled the request.<br/></returns>
    private static bool TryReadDirectTargetAllTuples(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        int skip,
        int? take,
        QueryDirection direction,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<LibraDexRuntimeTuple>? rows)
    {
        rows = null;
        IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = condition.Leaves;
        if (leaves.Count != 1 ||
            leaves[0].Operator != LibraDexConditionOperatorKind.All ||
            !string.Equals(leaves[0].IndexName, targetIndex.Name, StringComparison.Ordinal))
        {
            return false;
        }

        int? takeLimit = take is null ? null : checked(skip + take.Value);
        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>(), takeLimit, direction);
        if (targetIndex is IIdentityPrimitiveTupleStreamer tupleStreamer)
        {
            rows = MaterializeRuntimeTuples(tupleStreamer.IterateTuplePrimitive(request), skip, take);
            return true;
        }

        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor)
        {
            return false;
        }

        IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(
            request);
        int start = Math.Min(skip, tuples.Count);
        int count = take is null
            ? tuples.Count - start
            : Math.Min(take.Value, tuples.Count - start);
        LibraDexRuntimeTuple[] materialized = new LibraDexRuntimeTuple[count];
        for (int i = 0; i < count; i++)
        {
            LibraDexObjectTuple tuple = tuples[start + i];
            materialized[i] = new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);
        }

        rows = materialized;
        return true;
    }

    /// <summary>
    /// Reads a single-leaf tuple projection directly from the caller-supplied target index after condition materialization normalized the primitive request.<br/>
    /// The materialized criterion may hold a freshly opened logical index, so matching by group/name preserves workbench tuple semantics without relying on reference equality.<br/>
    /// </summary>
    /// <param name="targetIndex">The selected target index whose tuples should be streamed.</param>
    /// <param name="criterion">The materialized criterion tree.</param>
    /// <param name="skip">The number of target tuples to skip.</param>
    /// <param name="take">The optional maximum number of target tuples to return.</param>
    /// <param name="direction">The requested tuple traversal direction.</param>
    /// <param name="rows">Receives materialized runtime tuples when the direct path applies.</param>
    /// <returns><see langword="true"/> when the direct target-index path handled the request.</returns>
    private static bool TryReadDirectTargetLeafTuples(
        IIndex targetIndex,
        IIdentityCriterion criterion,
        int skip,
        int? take,
        QueryDirection direction,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<LibraDexRuntimeTuple>? rows)
    {
        rows = null;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.Index is not IIndex criterionIndex ||
            criterion.CriteriaKind is null ||
            !string.Equals(criterionIndex.Group, targetIndex.Group, StringComparison.Ordinal) ||
            !string.Equals(criterionIndex.Name, targetIndex.Name, StringComparison.Ordinal))
        {
            if (targetIndex is LibraDexStringScalar8Index stringIndex &&
                stringIndex.TryReadProjectionTuples(criterion, skip, take, direction, out IReadOnlyList<LibraDexRuntimeTuple>? projectionRows))
            {
                rows = projectionRows;
                return true;
            }

            return false;
        }

        int? takeLimit = take is null ? null : checked(skip + take.Value);
        LibraDexIdentityPrimitiveRequest request = new(criterion.CriteriaKind.Value, criterion.Values, takeLimit, direction);
        if (targetIndex is IIdentityPrimitiveTupleStreamer tupleStreamer)
        {
            rows = MaterializeRuntimeTuples(tupleStreamer.IterateTuplePrimitive(request), skip, take);
            return true;
        }

        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor)
        {
            return false;
        }

        IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(
            request);
        int start = Math.Min(skip, tuples.Count);
        int count = take is null
            ? tuples.Count - start
            : Math.Min(take.Value, tuples.Count - start);
        LibraDexRuntimeTuple[] materialized = new LibraDexRuntimeTuple[count];
        for (int i = 0; i < count; i++)
        {
            LibraDexObjectTuple tuple = tuples[start + i];
            materialized[i] = new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);
        }

        rows = materialized;
        return true;
    }

    /// <summary>
    /// Materializes runtime tuples from a streaming primitive tuple source after applying the public adapter skip/take window.<br/>
    /// This helper keeps streaming-capable physical indexes from building an intermediate primitive tuple list before `GetTuples` creates its required caller-owned result.<br/>
    /// </summary>
    /// <param name="tuples">The forward-only tuple source.</param>
    /// <param name="skip">The number of tuples to skip.</param>
    /// <param name="take">The optional maximum number of tuples to return.</param>
    /// <returns>Caller-owned runtime tuples for the requested adapter window.</returns>
    private static IReadOnlyList<LibraDexRuntimeTuple> MaterializeRuntimeTuples(IEnumerable<LibraDexObjectTuple> tuples, int skip, int? take)
    {
        if (take == 0)
        {
            return Array.Empty<LibraDexRuntimeTuple>();
        }

        List<LibraDexRuntimeTuple> materialized = take.HasValue ? new List<LibraDexRuntimeTuple>(take.Value) : new List<LibraDexRuntimeTuple>();
        int seen = 0;
        foreach (LibraDexObjectTuple tuple in tuples)
        {
            if (seen < skip)
            {
                seen++;
                continue;
            }

            materialized.Add(new LibraDexRuntimeTuple(tuple.Key, tuple.Identity));
            if (take.HasValue && materialized.Count >= take.Value)
            {
                break;
            }
        }

        return materialized.ToArray();
    }

    private LibraDexIndexCursor<TKey, TIdentity> GetTargetCursor<TKey, TIdentity>(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        int skip,
        int? take,
        QueryDirection direction)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        }

        if (!string.Equals(targetIndex.Group, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The target index belongs to a different LibraDex index set.");
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        _ = LibraDexConditionCursorExecutor.TryCreateDirectPrimitiveDeletePlan(
            criterion,
            targetIndex,
            skip,
            take,
            out IIdentityPrimitiveMutator? primitiveMutator,
            out LibraDexIdentityPrimitiveRequest? primitiveDeleteRequest);
        return new LibraDexIndexCursor<TKey, TIdentity>(
            LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, targetIndex, skip, take, direction),
            targetIndex,
            targetIndex as IIdentityExactTupleMutator,
            primitiveMutator,
            primitiveDeleteRequest,
            skip,
            take);
    }

    /// <summary>
    /// Resolves a maintained projection index for one projection-backed condition leaf inside this index set.<br/>
    /// String projections delegate to the logical string facade so folded, sort-key, and reversed projection wrappers preserve their public key types.<br/>
    /// Binary suffix conditions map to the owning index's hidden reversed exact-byte projection when the catalog metadata declares one.<br/>
    /// </summary>
    /// <param name="descriptor">The condition leaf requesting a projection route.</param>
    /// <param name="classification">The planner classification that identified the projection need.</param>
    /// <returns>The opened projection index, or <see langword="null"/> when no matching projection exists.</returns>
    /// <summary>
    /// Tries to open one condition index without throwing when its definition is currently absent.<br/>
    /// This resolver is reserved for structural condition guards; ordinary leaves continue through the strict name-first open path.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this identity group.</param>
    /// <returns>The opened index, or <see langword="null"/> when no active definition is resolvable.</returns>
    private IIndex? TryResolveConditionIndex(string indexName)
    {
        return owner.TryGetInfo(Name, indexName, out CatalogIndexInfo info)
            ? catalog.OpenIndex(info)
            : null;
    }

    private IIndex? ResolveProjectionIndex(
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexConditionLeafClassification classification)
    {
        if (!owner.TryGetInfo(Name, descriptor.IndexName, out CatalogIndexInfo info))
        {
            return null;
        }

        if (descriptor.ValueKind == LibraDexConditionValueKind.String &&
            info.KeyFamily == CatalogIndexKeyFamily.String &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Scalar)
        {
            return this[descriptor.IndexName].String.Open().ResolveProjection(descriptor, classification);
        }

        if (classification.ProjectionKind != LibraDexIndexProjectionKind.Exact ||
            descriptor.ValueKind != LibraDexConditionValueKind.Binary ||
            descriptor.Operator != LibraDexConditionOperatorKind.EndsWith ||
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
                    new IndexOptions { Keys = info.KeyContract, IdentityKeyMultiplicity = info.IdentityKeyMultiplicity },
                    keyWidth,
                    identityWidth,
                    IdentityLookupMode.Explicit
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
    /// Starts an ordered multi-key condition builder for already opened indexes in this index set.<br/>
    /// Ordinal selectors such as `Where(0).AsGuid` and `Where(1).AsString` bind to this participant list, which is convenient for generated code and caller-owned index arrays.<br/>
    /// Name and handle selectors use the ordinary index-set surface through `Where(indexName)` or `Where(indexInstance)` so handwritten code remains index-first.<br/>
    /// </summary>
    /// <param name="indexes">The ordered indexes that participate in the condition.</param>
    /// <returns>An ordered multi-key condition builder.</returns>
    public LibraDexOrderedMultiKeyBuilder MultiKey(params IIndex[] indexes)
        => new LibraDexOrderedMultiKeyBuilder(Name, indexes);

    /// <summary>
    /// Starts an ordered multi-key condition builder for an enumerable of already opened indexes in this index set.<br/>
    /// The enumerable is captured into an ordered array immediately so later condition assembly has stable ordinal-to-index mapping.<br/>
    /// </summary>
    /// <param name="indexes">The ordered indexes that participate in the condition.</param>
    /// <returns>An ordered multi-key condition builder.</returns>
    public LibraDexOrderedMultiKeyBuilder MultiKey(IEnumerable<IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return new LibraDexOrderedMultiKeyBuilder(Name, indexes.ToArray());
    }

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied anchored identity filter for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(filter)`, preserving the same index-set context as `.Where(...)` while avoiding the more verbose explicit builder root.<br/>
    /// The predicate-only external form still needs composition with an indexed sibling before execution can supply candidate identities.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives each candidate identity and returns whether it should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<object, bool> filter)
        => LibraDexCondition.Group(Name).External(filter);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied anchored identity filter and candidate-stream metadata for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(filter)`, preserving index-set context while exposing candidate identity, ordinal, and first-candidate state to caller code.<br/>
    /// The predicate-only external form still needs composition with an indexed sibling before execution can supply candidate identities.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives candidate identity, zero-based candidate ordinal, and first-candidate flag, then returns whether the identity should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<object, long, bool, bool> filter)
        => LibraDexCondition.Group(Name).External(filter);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied anchored identity filter context for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(filter)`, keeping generated or named-delegate code on the same contextual surface as `.Where(...)`.<br/>
    /// The predicate-only external form still needs composition with an indexed sibling before execution can supply candidate identities.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives candidate identity context and returns whether the identity should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<LibraDexExternalIdentityContext, bool> filter)
        => LibraDexCondition.Group(Name).External(filter);

    /// <summary>
    /// Starts a low-friction condition builder with caller-supplied identities as an external source for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(identities)`, preserving index-set context while letting caller-owned identity streams stand alone or compose with indexed criteria.<br/>
    /// Use this form when caller code already has identities, not runtime keys.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity value type supplied by caller code.<br/></typeparam>
    /// <param name="identities">The identities to expose as an external source stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External<TIdentity>(IEnumerable<TIdentity> identities)
        => LibraDexCondition.Group(Name).External(identities);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied identity source factory for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(identityFactory)`, preserving index-set context while evaluating the identity source at execution time.<br/>
    /// Use this form for request-time identity lists, precomputed identity caches, or identities supplied by another storage engine.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity value type supplied by caller code.<br/></typeparam>
    /// <param name="identityFactory">Factory that returns the identities to expose as an external source stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External<TIdentity>(Func<IEnumerable<TIdentity>> identityFactory)
        => LibraDexCondition.Group(Name).External(identityFactory);

    /// <summary>
    /// Starts a low-friction condition builder with caller-supplied runtime key/identity entries for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(entries)`, allowing caller-owned typed key/identity pairs to behave like a temporary condition branch without creating a stored index.<br/>
    /// The returned operator applies typed key predicates before composing matching identities with the rest of the condition tree.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external entries.<br/></typeparam>
    /// <param name="entries">The external key/identity entries to expose as a runtime index-like branch.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(IEnumerable<LibraDexExternalEntry<TKey, TIdentity>> entries)
        => LibraDexCondition.Group(Name).External(entries);

    /// <summary>
    /// Starts a low-friction condition builder with caller-supplied runtime key/identity pairs for this index set.<br/>
    /// This overload accepts the standard .NET key/value pair shape and treats the pair value as the typed LibraDex identity for this Name.<br/>
    /// The returned operator applies typed key predicates before composing matching identities with the rest of the condition tree.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external pairs.<br/></typeparam>
    /// <param name="entries">The external key/identity pairs to expose as a runtime index-like branch.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(IEnumerable<KeyValuePair<TKey, TIdentity>> entries)
        => LibraDexCondition.Group(Name).External(entries);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied runtime key/identity entry source for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(entryFactory)`, preserving index-set context while evaluating caller-owned typed key/identity entries at execution time.<br/>
    /// Use this form when caller-owned data behaves like a request-local or externally backed index.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external entries.<br/></typeparam>
    /// <param name="entryFactory">Factory that returns external key/identity entries.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(Func<IEnumerable<LibraDexExternalEntry<TKey, TIdentity>>> entryFactory)
        => LibraDexCondition.Group(Name).External(entryFactory);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied runtime key/identity pair source for this index set.<br/>
    /// This overload accepts the standard .NET key/value pair shape and treats the pair value as the typed LibraDex identity for this Name.<br/>
    /// Use this form when caller-owned data behaves like a request-local or externally backed index.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external pairs.<br/></typeparam>
    /// <param name="entryFactory">Factory that returns external key/identity pairs.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(Func<IEnumerable<KeyValuePair<TKey, TIdentity>>> entryFactory)
        => LibraDexCondition.Group(Name).External(entryFactory);

    /// <summary>
    /// Starts a low-friction condition builder with a correlated caller-supplied external key source for this index set.<br/>
    /// This is the catalog index-set stub for `LibraDexCondition.Group(Name).External(candidateKeyFactory)`, preserving index-set context while deriving caller-owned keys from each indexed candidate identity.<br/>
    /// The correlated form still needs composition with an indexed sibling before execution can supply candidate identities.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="candidateKeyFactory">Factory that returns external keys for one candidate identity.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(Func<object, IEnumerable<TKey>> candidateKeyFactory)
        => LibraDexCondition.Group(Name).External(candidateKeyFactory);

    /// <summary>
    /// Describes a composite-key index inside this index set without creating or opening physical storage.<br/>
    /// The returned builder is lazy: only its terminal lifecycle methods mutate or inspect catalog storage.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type stored by the composite index.</typeparam>
    /// <param name="name">The index name inside this index set.</param>
    /// <param name="parts">The ordered composite-key parts.</param>
    /// <returns>A lifecycle builder for the named composite index.</returns>
    public CatalogCompositeIndexBuilder<TIdentity> Composite<TIdentity>(
        string name,
        params LibraDexCompositeKeyPartSpec[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CatalogCompositeIndexBuilder<TIdentity>(owner, Name, name, parts);
    }

    /// <summary>
    /// Opens an existing index matching a logical shape in this identity group, or creates it when missing.<br/>
    /// The group check prevents higher-level adapters from accidentally creating a descriptor under a different identity universe while retaining the low-friction group-owned lifecycle boundary.<br/>
    /// </summary>
    /// <param name="shape">The logical shape whose group must match <see cref="Name"/>.<br/></param>
    /// <param name="options">Optional per-index options override for the create branch; null uses the contracts embedded in <paramref name="shape"/>.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the returned index's inversion is complete.<br/></param>
    /// <returns>A strict non-generic index handle owned by this group's catalog.<br/></returns>
    public IIndex CreateOrOpen(
        LibraDexIndexShapeSpec shape,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!string.Equals(shape.Group, Name, StringComparison.Ordinal))
            throw new ArgumentException($"Index shape group '{shape.Group}' does not match bound identity group '{Name}'.", nameof(shape));

        return owner.CreateOrOpen(shape, options, identityLookupMode);
    }
}

/// <summary>
/// Provides a typed condition-building view over one catalog index set.<br/>
/// The wrapper keeps the Name identity type attached to caller-owned external entries while reusing the untyped catalog Name for index discovery, materialization, and ordinary `.Where(...)` condition roots.<br/>
/// </summary>
/// <typeparam name="TIdentity">The identity type shared by indexes in this index set.<br/></typeparam>
public sealed class CatalogIdentityGroupIndexes<TIdentity>
{
    private readonly CatalogIdentityGroupIndexes inner;

    internal CatalogIdentityGroupIndexes(CatalogIdentityGroupIndexes inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// Gets the index set name represented by this typed index-set surface.<br/>
    /// </summary>
    public string Name => inner.Name;

    /// <summary>
    /// Gets the index-set durability batch manager for indexes that share this index set.<br/>
    /// </summary>
    public CatalogIdentityGroupBatchManager Batch => inner.Batch;

    /// <summary>
    /// Starts a negated first condition clause for this typed index set.<br/>
    /// This forwards to the untyped catalog Name so typed callers can keep root negation on the same `catalog.IndexSet(...)` surface as ordinary predicates.<br/>
    /// </summary>
    public LibraDexConditionClause Not => inner.Not;

    /// <summary>
    /// Adds a completed condition fragment as the first node for this typed index set.<br/>
    /// The fragment must belong to the same index set, and the typed wrapper keeps caller code on the catalog index-set surface for reusable predicate composition.<br/>
    /// </summary>
    /// <param name="groupCondition">The completed grouped condition fragment.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd Group(LibraDexConditionEndCondition groupCondition)
        => inner.Group(groupCondition);

    /// <summary>
    /// Starts a low-friction condition builder by selecting an index name inside this typed index set.<br/>
    /// This forwards to the untyped Name condition stub; index key typing is still selected after the index name.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this index set.<br/></param>
    /// <returns>A value-family selector for the chosen index.<br/></returns>
    public LibraDexConditionValueTypeSelector Where(string indexName)
        => inner.Where(indexName);

    /// <summary>
    /// Starts a reusable typed-index-set condition with an execution-time index-name parameter.<br/>
    /// </summary>
    /// <param name="indexName">The parameter containing the current index name.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(LibraDexParameter<string> indexName)
        => inner.Where(indexName);

    /// <summary>
    /// Starts a reusable typed-index-set condition with an execution-time opened-index parameter.<br/>
    /// </summary>
    /// <typeparam name="TIndex">The opened index handle type.</typeparam>
    /// <param name="index">The parameter containing the current opened index.</param>
    /// <returns>A value-family selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where<TIndex>(LibraDexParameter<TIndex> index)
        where TIndex : IIndex
        => inner.Where(index);

    /// <summary>
    /// Opens an index by name inside this typed index set using metadata-driven catalog lookup.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this index set.<br/></param>
    /// <returns>An opened index handle.<br/></returns>
    public IIndex Index(string indexName)
        => inner.Index(indexName);

    /// <summary>
    /// Opens a typed index by name inside this typed index set and validates the requested key type against catalog metadata.<br/>
    /// The identity type is supplied by this grouped identity surface.<br/>
    /// </summary>
    /// <typeparam name="TKey">The expected public key type.<br/></typeparam>
    /// <param name="indexName">The index name inside this index set.<br/></param>
    /// <param name="keys">The fallback duplicate-key contract for older entries without rich metadata.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <returns>A typed index handle.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Index<TKey>(
        string indexName,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return inner.Index<TKey, TIdentity>(indexName, keys, options);
    }

    /// <summary>
    /// Starts a typed condition builder by opening a named index inside this typed index set and validating the requested key type.<br/>
    /// </summary>
    /// <typeparam name="TKey">The expected public key type.<br/></typeparam>
    /// <param name="indexName">The index name inside this index set.<br/></param>
    /// <param name="keys">The fallback duplicate-key contract for older entries without rich metadata.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <returns>A typed opened-index condition root.<br/></returns>
    public LibraDexIndexWhere<TKey, TIdentity> Where<TKey>(
        string indexName,
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return inner.Where<TKey, TIdentity>(indexName, keys, options);
    }

    /// <summary>
    /// Starts a low-friction condition builder from an already opened index instance in this typed index set.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select.<br/></param>
    /// <returns>A value-family selector for the chosen index.<br/></returns>
    public LibraDexConditionValueTypeSelector Where(IIndex index)
        => inner.Where(index);

    /// <summary>
    /// Starts a low-friction condition builder from a generic typed index instance and selects the index key type automatically.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type carried by the opened index handle.<br/></typeparam>
    /// <param name="index">The opened generic index instance to select.<br/></param>
    /// <returns>A typed operator for the selected index key type.<br/></returns>
    public LibraDexConditionOperator<TKey> Where<TKey>(LibraDexIndex<TKey, TIdentity> index)
        => inner.Where(index);

    /// <summary>
    /// Starts a low-friction condition builder from a string index facade and selects string operators automatically.<br/>
    /// </summary>
    /// <param name="index">The opened string index instance to select.<br/></param>
    /// <returns>String operators for the selected index.<br/></returns>
    public LibraDexStringConditionOperator Where(LibraDexStringScalar8Index index)
        => inner.Where(index);

    /// <summary>
    /// Starts a composite condition builder by selecting a composite index name inside this typed index set.<br/>
    /// </summary>
    /// <param name="indexName">The composite index name inside this index set.<br/></param>
    /// <returns>A composite condition root for the selected index.<br/></returns>
    public LibraDexCompositeConditionWhere CompositeWhere(string indexName)
        => inner.CompositeWhere(indexName);

    /// <summary>
    /// Opens a typed two-part composite index by name and validates persisted part types against catalog metadata.<br/>
    /// The identity type is supplied by this grouped identity surface.<br/>
    /// </summary>
    /// <typeparam name="TPart1">The expected first composite key-part type.<br/></typeparam>
    /// <typeparam name="TPart2">The expected second composite key-part type.<br/></typeparam>
    /// <param name="name">The composite index name inside this index set.<br/></param>
    /// <returns>A typed composite index handle.<br/></returns>
    public LibraDexCompositeIndex<TPart1, TPart2, TIdentity> CompositeIndex<TPart1, TPart2>(string name)
        => inner.CompositeIndex<TPart1, TPart2, TIdentity>(name);

    /// <summary>
    /// Opens a typed three-part composite index by name and validates persisted part types against catalog metadata.<br/>
    /// The identity type is supplied by this grouped identity surface.<br/>
    /// </summary>
    /// <typeparam name="TPart1">The expected first composite key-part type.<br/></typeparam>
    /// <typeparam name="TPart2">The expected second composite key-part type.<br/></typeparam>
    /// <typeparam name="TPart3">The expected third composite key-part type.<br/></typeparam>
    /// <param name="name">The composite index name inside this index set.<br/></param>
    /// <returns>A typed composite index handle.<br/></returns>
    public LibraDexCompositeIndex<TPart1, TPart2, TPart3, TIdentity> CompositeIndex<TPart1, TPart2, TPart3>(string name)
        => inner.CompositeIndex<TPart1, TPart2, TPart3, TIdentity>(name);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied anchored identity filter for this typed index set.<br/>
    /// The predicate-only external form still needs composition with an indexed sibling before execution can supply candidate identities.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives each candidate identity and returns whether it should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<TIdentity, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return inner.External(identity => filter((TIdentity)identity));
    }

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied anchored identity filter and candidate-stream metadata for this typed index set.<br/>
    /// The predicate-only external form still needs composition with an indexed sibling before execution can supply candidate identities.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives candidate identity, zero-based candidate ordinal, and first-candidate flag, then returns whether the identity should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<TIdentity, long, bool, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return inner.External((identity, ordinal, isFirst) => filter((TIdentity)identity, ordinal, isFirst));
    }

    /// <summary>
    /// Starts a low-friction condition builder with caller-supplied typed identities as an external source for this index set.<br/>
    /// </summary>
    /// <param name="identities">The identities to expose as an external source stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(IEnumerable<TIdentity> identities)
        => inner.External(identities);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied typed identity source factory for this index set.<br/>
    /// </summary>
    /// <param name="identityFactory">Factory that returns the identities to expose as an external source stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<IEnumerable<TIdentity>> identityFactory)
        => inner.External(identityFactory);

    /// <summary>
    /// Starts a low-friction condition builder with caller-supplied runtime key/identity entries for this typed index set.<br/>
    /// The entry identity type is fixed to this Name's <typeparamref name="TIdentity"/> so mismatched external entries fail at compile time.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="entries">The external key/identity entries to expose as a runtime index-like branch.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(IEnumerable<LibraDexExternalEntry<TKey, TIdentity>> entries)
        => inner.External<TKey, TIdentity>(entries);

    /// <summary>
    /// Starts a low-friction condition builder with caller-supplied runtime key/identity pairs for this typed index set.<br/>
    /// The pair value type is fixed to this Name's <typeparamref name="TIdentity"/> so mismatched external pairs fail at compile time.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="entries">The external key/identity pairs to expose as a runtime index-like branch.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(IEnumerable<KeyValuePair<TKey, TIdentity>> entries)
        => inner.External<TKey, TIdentity>(entries);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied runtime key/identity entry source for this typed index set.<br/>
    /// The entry identity type is fixed to this Name's <typeparamref name="TIdentity"/> so mismatched external entries fail at compile time.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="entryFactory">Factory that returns external key/identity entries.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(Func<IEnumerable<LibraDexExternalEntry<TKey, TIdentity>>> entryFactory)
        => inner.External<TKey, TIdentity>(entryFactory);

    /// <summary>
    /// Starts a low-friction condition builder with a caller-supplied runtime key/identity pair source for this typed index set.<br/>
    /// The pair value type is fixed to this Name's <typeparamref name="TIdentity"/> so mismatched external pairs fail at compile time.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="entryFactory">Factory that returns external key/identity pairs.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(Func<IEnumerable<KeyValuePair<TKey, TIdentity>>> entryFactory)
        => inner.External<TKey, TIdentity>(entryFactory);

    /// <summary>
    /// Starts a low-friction condition builder with a correlated caller-supplied external key source for this typed index set.<br/>
    /// The candidate identity parameter is fixed to this Name's <typeparamref name="TIdentity"/> so caller code does not need to cast from object.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="candidateKeyFactory">Factory that returns external keys for one candidate identity.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(Func<TIdentity, IEnumerable<TKey>> candidateKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(candidateKeyFactory);
        return inner.External<TKey>(identity => candidateKeyFactory((TIdentity)identity));
    }
}

/// <summary>
/// Provides identity-family typed condition stubs for one catalog index set.<br/>
/// These selectors declare the identity value family for caller-owned external sources; key typing still belongs after an index selection through `.Where(...).As...` or typed index handles.<br/>
/// </summary>
public sealed class CatalogIdentityGroupIdentityTypeSelector
{
    private readonly CatalogIdentityGroupIndexes group;

    internal CatalogIdentityGroupIdentityTypeSelector(CatalogIdentityGroupIndexes group) { this.group = group ?? throw new ArgumentNullException(nameof(group)); }

    /// <summary>
    /// Gets a typed condition-building view for `int` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<int> Int32 => Create<int>();

    /// <summary>
    /// Gets a typed condition-building view for `uint` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<uint> UInt32 => Create<uint>();

    /// <summary>
    /// Gets a typed condition-building view for `long` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<long> Int64 => Create<long>();

    /// <summary>
    /// Gets a typed condition-building view for `ulong` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<ulong> UInt64 => Create<ulong>();

    /// <summary>
    /// Gets a typed condition-building view for `short` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<short> Int16 => Create<short>();

    /// <summary>
    /// Gets a typed condition-building view for `ushort` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<ushort> UInt16 => Create<ushort>();

    /// <summary>
    /// Gets a typed condition-building view for `byte` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<byte> UInt8 => Create<byte>();

    /// <summary>
    /// Gets a typed condition-building view for `sbyte` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<sbyte> Int8 => Create<sbyte>();

    /// <summary>
    /// Gets a typed condition-building view for `Int128` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<Int128> Int128 => Create<Int128>();

    /// <summary>
    /// Gets a typed condition-building view for `UInt128` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<UInt128> UInt128 => Create<UInt128>();

    /// <summary>
    /// Gets a typed condition-building view for exact `decimal` identities.<br/>
    /// Decimal identities use the same canonical scalar-16 encoding as Decimal keys.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<decimal> Decimal => Create<decimal>();

    /// <summary>
    /// Gets a typed condition-building view for native Single identities.<br/>
    /// Single identities use the same canonical ordered scalar-8 encoding as Single keys, including canonical NaN and zero representations.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<float> Single => Create<float>();

    /// <summary>
    /// Gets a typed condition-building view for native Double identities.<br/>
    /// Double identities use the same canonical ordered scalar-8 encoding as Double keys, including canonical NaN and zero representations.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<double> Double => Create<double>();

    /// <summary>
    /// Gets a typed condition-building view for `Guid` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<Guid> Guid => Create<Guid>();

    /// <summary>
    /// Gets a typed condition-building view for `DateTime` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<DateTime> DateTime => Create<DateTime>();

    /// <summary>
    /// Gets a typed condition-building view for `DateOnly` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<DateOnly> DateOnly => Create<DateOnly>();

    /// <summary>
    /// Gets a typed condition-building view for `TimeOnly` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<TimeOnly> TimeOnly => Create<TimeOnly>();

    /// <summary>
    /// Gets a typed condition-building view for `TimeSpan` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<TimeSpan> TimeSpan => Create<TimeSpan>();

    /// <summary>
    /// Gets a typed condition-building view for `string` identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<string> String => Create<string>();

    /// <summary>
    /// Gets a typed condition-building view for raw byte-array identities.<br/>
    /// </summary>
    public CatalogIdentityGroupIndexes<byte[]> Bytes => Create<byte[]>();

    private CatalogIdentityGroupIndexes<TIdentity> Create<TIdentity>()
    {
        group.Catalog.ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(CatalogIdentityGroupIdentityTypeSelector));
        return new CatalogIdentityGroupIndexes<TIdentity>(group);
    }
}

/// <summary>
/// Provides key-family builders for one named index inside an index set.<br/>
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
    /// Gets the index set name for this named index builder.<br/>
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
        => Scalar.Scalar<int, TIdentity>();

    /// <summary>
    /// Selects an Int64 key family with a scalar identity family for this named index.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for an Int64-key index.</returns>
    public CatalogNamedTypedIndexBuilder<long, TIdentity> Int64Keys<TIdentity>()
        => Scalar.Scalar<long, TIdentity>();

    /// <summary>
    /// Selects an Int128 key family with a scalar identity family for this named index.<br/>
    /// Int128 keys route through the existing fixed-16 scalar shelf shape with a signed sortable transform, so negative, zero, and positive values remain in numeric order without query-time conversion.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for an Int128-key index.</returns>
    public CatalogNamedTypedIndexBuilder<Int128, TIdentity> Int128Keys<TIdentity>()
        => Scalar.Scalar<Int128, TIdentity>();

    /// <summary>
    /// Selects a UInt128 key family with a scalar identity family for this named index.<br/>
    /// UInt128 keys route through the existing fixed-16 scalar shelf shape as two big-endian unsigned lanes, preserving natural numeric order for boundary, range, and membership conditions.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for a UInt128-key index.</returns>
    public CatalogNamedTypedIndexBuilder<UInt128, TIdentity> UInt128Keys<TIdentity>()
        => Scalar.Scalar<UInt128, TIdentity>();

    /// <summary>
    /// Selects a Decimal key family with a scalar identity family for this named index.<br/>
    /// Decimal keys route through the fixed-16 scalar shelf shape using LibraDex's exact canonical ordered encoding, so callers retain CLR Decimal equality and range semantics without scale adapters.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.<br/></typeparam>
    /// <returns>A typed lifecycle builder for a Decimal-key index.<br/></returns>
    public CatalogNamedTypedIndexBuilder<decimal, TIdentity> DecimalKeys<TIdentity>()
        => Scalar.Scalar<decimal, TIdentity>();

    /// <summary>
    /// Selects a native Single key family with a scalar identity family for this named index.<br/>
    /// Single keys use a canonical ordered scalar-8 representation, so numeric ranges, infinities, and exact NaN lookups require no caller-supplied transform.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.<br/></typeparam>
    /// <returns>A typed lifecycle builder for a Single-key index.<br/></returns>
    public CatalogNamedTypedIndexBuilder<float, TIdentity> SingleKeys<TIdentity>()
        => Scalar.Scalar<float, TIdentity>();

    /// <summary>
    /// Selects a native Double key family with a scalar identity family for this named index.<br/>
    /// Double keys use a canonical ordered scalar-8 representation, so numeric ranges, infinities, and exact NaN lookups require no caller-supplied transform.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.<br/></typeparam>
    /// <returns>A typed lifecycle builder for a Double-key index.<br/></returns>
    public CatalogNamedTypedIndexBuilder<double, TIdentity> DoubleKeys<TIdentity>()
        => Scalar.Scalar<double, TIdentity>();

    /// <summary>
    /// Selects a GUID key family with a scalar identity family for this named index.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <returns>A typed lifecycle builder for a GUID-key index.</returns>
    public CatalogNamedTypedIndexBuilder<Guid, TIdentity> GuidKeys<TIdentity>()
        => Guid.Scalar<TIdentity>();

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
    /// Selects a UInt64 key family with raw variable-length identity bytes for this named index.<br/>
    /// This maps to routed `SV8` storage and is intended for path/blob identity tests where a scalar key still needs normal condition-builder range predicates.<br/>
    /// </summary>
    /// <param name="maxIdentityBytes">Maximum raw identity byte count accepted by this index.</param>
    /// <returns>A UInt64 variable-identity lifecycle builder.</returns>
    public CatalogNamedUInt64VarIdentityKeyBuilder UInt64VarIdentityKeys(int maxIdentityBytes)
    {
        Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default8KiB.ShelfExtentSize, maxIdentityBytes);
        return new CatalogNamedUInt64VarIdentityKeyBuilder(
            catalog,
            owner,
            Group,
            Name,
            maxIdentityBytes);
    }

    /// <summary>
    /// Selects a raw-byte variable-key family with raw variable-length identity bytes for this named index.<br/>
    /// This internal catalog-owned `VV` path is intended for workbench and adapter tests that need byte-native keys and identities before a string/blob condition facade is promoted.<br/>
    /// </summary>
    /// <param name="maxKeyBytes">Maximum physical variable-key bytes, including the internal sentinel byte.<br/></param>
    /// <param name="maxIdentityBytes">Maximum raw identity byte count accepted by this index.<br/></param>
    /// <returns>A raw `VV` lifecycle builder.</returns>
    internal CatalogNamedVarKeyVarIdentityKeyBuilder VarKeyVarIdentityKeys(int maxKeyBytes, int maxIdentityBytes)
    {
        VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default8KiB.ShelfExtentSize, maxKeyBytes, maxIdentityBytes);
        return new CatalogNamedVarKeyVarIdentityKeyBuilder(
            catalog,
            owner,
            Group,
            Name,
            maxKeyBytes,
            maxIdentityBytes);
    }

    /// <summary>
    /// Selects the logical string-key family for this named index.<br/>
    /// The current physical string facade stores UInt64 identities, so the lifecycle builder is returned directly rather than taking an identity type parameter.<br/>
    /// </summary>
    /// <returns>A string-key lifecycle builder.</returns>
    public CatalogNamedStringKeyBuilder StringKeys()
        => String;

    /// <summary>
    /// Describes this named grouped index as a composite-key index without creating or opening physical storage.<br/>
    /// The returned builder captures the part descriptors once, then requires an explicit terminal lifecycle method such as `Create`, `Open`, or `CreateOrOpen`.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type stored by the composite index.</typeparam>
    /// <param name="parts">The ordered composite-key parts.</param>
    /// <returns>A lifecycle builder for this composite index.</returns>
    public CatalogCompositeIndexBuilder<TIdentity> Composite<TIdentity>(params LibraDexCompositeKeyPartSpec[] parts)
        => new CatalogCompositeIndexBuilder<TIdentity>(owner, Group, Name, parts);

    /// <summary>
    /// Creates this named grouped index using natural CLR scalar routing inferred from <typeparamref name="TKey"/> and <typeparamref name="TIdentity"/>.<br/>
    /// This is the short generic convenience path for callers that already know the logical field name and do not need the more explicit key-family lane for IntelliSense exploration.<br/>
    /// Byte-array fixed-width indexes still require the explicit family lane until fixed-width metadata is persisted.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="keys">The index-wide duplicate-key contract.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the created index's inversion is complete.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        if (owner.TryGetInfo(Group, Name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex index already exists.");
        }

        return catalog.CreateGenericIndex<TKey, TIdentity>(
            Name,
            owner.ResolveCreateSlot(null),
            CatalogIndexFactoryOptions.Resolve(keys, options),
            keyWidth: null,
            identityWidth: null,
            ResolveKeyFamily<TKey>(),
            ResolveIdentityFamily<TIdentity>(),
            Group,
            identityLookupMode: identityLookupMode);
    }

    /// <summary>
    /// Opens this named grouped index using explicit generic type arguments and validates those type arguments against persisted catalog metadata.<br/>
    /// This supports compact call sites such as `catalog.Indexes["people"]["age"].Open<int, long>()` while preserving the safety of metadata-driven reopen.<br/>
    /// </summary>
    /// <typeparam name="TKey">The expected public key type.</typeparam>
    /// <typeparam name="TIdentity">The expected public identity type.</typeparam>
    /// <param name="keys">The fallback duplicate-key contract for older scaffold entries without rich metadata.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the opened index's inversion is complete.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Open<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        if (!owner.TryGetInfo(Group, Name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex index does not exist.");
        }

        ValidateRequestedTypes<TKey, TIdentity>(info);
        return catalog.OpenGenericIndex<TKey, TIdentity>(
            info.SlotIndex,
            CatalogIndexFactoryOptions.Resolve(keys, options),
            keyWidth: null,
            identityWidth: null,
            identityLookupMode: identityLookupMode);
    }

    /// <summary>
    /// Opens this named grouped index when it exists or creates it using natural CLR scalar routing when it is missing.<br/>
    /// The open branch validates the requested generic type arguments against persisted metadata before returning a typed handle.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="keys">The index-wide duplicate-key contract for a new index, or fallback contract for an older entry without rich metadata.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the returned index's inversion is complete.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        return owner.TryGetInfo(Group, Name, out _)
            ? Open<TKey, TIdentity>(keys, options, identityLookupMode)
            : Create<TKey, TIdentity>(keys, options, identityLookupMode);
    }

    /// <summary>
    /// Opens an existing grouped index as a strict non-generic handle using the CLR key and identity types persisted in catalog metadata.<br/>
    /// This is the preferred programmatic path for query builders, serializers, and adapters that enumerate an index set first and only choose criteria at runtime.<br/>
    /// The returned handle still validates runtime key values before creating queries, so callers do not lose the catalog's type contract by avoiding generic type arguments.<br/>
    /// </summary>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the opened index's inversion is complete.<br/></param>
    /// <returns>A non-generic index handle owned by the catalog lifetime.<br/></returns>
    public IIndex Open(IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        if (!owner.TryGetInfo(Group, Name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex index does not exist.");
        }

        IIndex index = catalog.OpenIndex(info);
        if (identityLookupMode != IdentityLookupMode.Explicit)
            index.IdentityLookup.Configure(identityLookupMode);
        return index;
    }

    /// <summary>
    /// Deletes every tuple in this target index whose identity is selected by <paramref name="condition"/>.<br/>
    /// The condition describes selection only; this named index owns the physical mutation.<br/>
    /// </summary>
    /// <param name="condition">The completed condition selecting identities in this index's identity group.<br/></param>
    /// <returns>The matched and deleted target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult Delete(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CatalogIdentityGroupIndexes indexes = RequireConditionGroup(condition.Group);
        IIndex target = Open();
        IIdentityCriterion criterion = condition.Materialize(name => indexes.Index(name));
        return LibraDexIdentityExecutionPlanner.ExecuteTargetDelete(criterion, target);
    }

    /// <summary>
    /// Deletes every tuple in this target index whose identity wins the grouped aggregate selection in <paramref name="condition"/>.<br/>
    /// Any <c>Return</c> or <c>ReturnKeys</c> projection is intentionally ignored; grouping, filtering, and aggregate-winner selection remain in force.<br/>
    /// </summary>
    /// <typeparam name="TResult">The condition's retrieval result type, which does not affect mutation selection.<br/></typeparam>
    /// <param name="condition">The completed typed condition selecting aggregate-winning identities.<br/></param>
    /// <returns>The matched and deleted target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult Delete<TResult>(LibraDexCondition<TResult> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CatalogIdentityGroupIndexes indexes = RequireConditionGroup(condition.Group);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetDelete(condition.IterateSelectedIdentities(indexes), Open());
    }

    /// <summary>
    /// Deletes every physical tuple from this target index while retaining the index and its catalog definition.<br/>
    /// Use this explicit name when the intended scope is the whole index; selective deletion uses <see cref="Delete(LibraDexConditionEndCondition)"/>.<br/>
    /// </summary>
    /// <returns>The matched and deleted target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult DeleteAll()
        => LibraDexIdentityExecutionPlanner.ExecuteTargetDeleteAll(Open());

    /// <summary>
    /// Replaces the target-index key of every tuple whose identity is selected by <paramref name="condition"/>.<br/>
    /// Replacement tuples are established before old tuples are removed so a failed insert does not discard the original tuple.<br/>
    /// </summary>
    /// <param name="condition">The completed condition selecting identities in this index's identity group.<br/></param>
    /// <param name="newKey">The replacement target-index key.<br/></param>
    /// <returns>The matched and changed target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult SetKey(LibraDexConditionEndCondition condition, object? newKey)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CatalogIdentityGroupIndexes indexes = RequireConditionGroup(condition.Group);
        IIdentityCriterion criterion = condition.Materialize(name => indexes.Index(name));
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(criterion, Open(), hasNewKey: true, newKey, newKeyFactory: null);
    }

    /// <summary>
    /// Replaces the target-index key of every tuple whose identity wins the grouped aggregate selection in <paramref name="condition"/>.<br/>
    /// Any declared return projection is ignored without weakening the grouped aggregate selection.<br/>
    /// </summary>
    /// <typeparam name="TResult">The condition's retrieval result type, which does not affect mutation selection.<br/></typeparam>
    /// <param name="condition">The completed typed condition selecting aggregate-winning identities.<br/></param>
    /// <param name="newKey">The replacement target-index key.<br/></param>
    /// <returns>The matched and changed target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult SetKey<TResult>(LibraDexCondition<TResult> condition, object? newKey)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CatalogIdentityGroupIndexes indexes = RequireConditionGroup(condition.Group);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(
            condition.IterateSelectedIdentities(indexes),
            Open(),
            hasNewKey: true,
            newKey,
            newKeyFactory: null,
            factoryUsesOldKey: false);
    }

    /// <summary>
    /// Replaces each selected target tuple's key using a transform of that tuple's current key.<br/>
    /// The transform runs only after the condition has selected identities and the target index has captured their exact tuples.<br/>
    /// </summary>
    /// <param name="condition">The completed condition selecting identities in this index's identity group.<br/></param>
    /// <param name="newKeyFactory">A transform receiving the current target-index key and returning its replacement.<br/></param>
    /// <returns>The matched and changed target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult SetKeyUsing(
        LibraDexConditionEndCondition condition,
        Func<object, object?> newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        CatalogIdentityGroupIndexes indexes = RequireConditionGroup(condition.Group);
        IIdentityCriterion criterion = condition.Materialize(name => indexes.Index(name));
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(
            LibraDexIdentityExecutionPlanner.Iterate(criterion, LibraDexIdentityQueryOptions.Default),
            Open(),
            hasNewKey: false,
            newKey: null,
            newKeyFactory,
            factoryUsesOldKey: true);
    }

    /// <summary>
    /// Replaces each aggregate-selected target tuple's key using a transform of that tuple's current key.<br/>
    /// Any declared return projection is ignored; the same grouped aggregate winners used by retrieval supply the mutation identity set.<br/>
    /// </summary>
    /// <typeparam name="TResult">The condition's retrieval result type, which does not affect mutation selection.<br/></typeparam>
    /// <param name="condition">The completed typed condition selecting aggregate-winning identities.<br/></param>
    /// <param name="newKeyFactory">A transform receiving the current target-index key and returning its replacement.<br/></param>
    /// <returns>The matched and changed target-tuple counts.<br/></returns>
    public LibraDexIdentityMutationResult SetKeyUsing<TResult>(
        LibraDexCondition<TResult> condition,
        Func<object, object?> newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        CatalogIdentityGroupIndexes indexes = RequireConditionGroup(condition.Group);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(
            condition.IterateSelectedIdentities(indexes),
            Open(),
            hasNewKey: false,
            newKey: null,
            newKeyFactory,
            factoryUsesOldKey: true);
    }

    private CatalogIdentityGroupIndexes RequireConditionGroup(string conditionGroup)
    {
        if (!string.Equals(Group, conditionGroup, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Condition group '{conditionGroup}' cannot mutate target index '{Name}' in identity group '{Group}'.");
        }

        return catalog[Group];
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
/// Instances are cheap lazy builders: they only hold Name, name, identity type, and ordered part metadata until a terminal method is called.<br/>
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
    /// Gets the index set name captured by this composite descriptor.<br/>
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
        => new CatalogNamedIndexShapeBuilder(group, name).Composite<TIdentity>(parts, keys);

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
        => owner.Open(Shape(keys));

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

    /// <summary>
    /// Selects a bounded variable-length blob-key index with scalar identities.<br/>
    /// The maximum is the developer-facing payload byte count; LibraDex reserves and persists its internal key-state sentinel separately.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type encoded into an 8-byte identity lane.<br/></typeparam>
    /// <param name="maxKeyBytes">The maximum raw blob payload length accepted by the index.<br/></param>
    /// <returns>A lifecycle builder for the bounded variable-blob index.</returns>
    public CatalogNamedVariableBlobKeyBuilder<TIdentity> Variable<TIdentity>(int maxKeyBytes)
    {
        return new CatalogNamedVariableBlobKeyBuilder<TIdentity>(
            catalog,
            owner,
            group,
            name,
            maxKeyBytes);
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
            CatalogIndexFactoryOptions.Resolve(keys, options));
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
            CatalogIndexFactoryOptions.Resolve(keys, options));
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
    /// <param name="keys">The index-wide duplicate-key contract for create.<br/></param>
    /// <param name="options">Optional per-index options for create.<br/></param>
    /// <returns>A BigInt index facade over raw variable-length identity bytes.</returns>
    public LibraDexBigIntVarIdentityIndex CreateOrOpen(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open()
            : Create(keys, options);
    }

}

/// <summary>
/// Provides terminal lifecycle verbs for one named UInt64-key/raw-variable-identity index.<br/>
/// The builder keeps variable identity storage explicit so callers do not confuse raw path identities with fixed-width `byte[]` scalar identities.<br/>
/// </summary>
public sealed class CatalogNamedUInt64VarIdentityKeyBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly int maxIdentityBytes;

    internal CatalogNamedUInt64VarIdentityKeyBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        int maxIdentityBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(owner);
        Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default8KiB.ShelfExtentSize, maxIdentityBytes);
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.maxIdentityBytes = maxIdentityBytes;
    }

    /// <summary>
    /// Creates the named UInt64 variable-identity index.<br/>
    /// Creation persists the variable identity byte cap so metadata-driven reopen can reconstruct the same `SV8` facade later.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A UInt64 key index facade over raw variable-length identity bytes.</returns>
    public LibraDexUInt64VarIdentityIndex Create(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex UInt64 variable-identity index already exists.");
        }

        return catalog.CreateUInt64VarIdentityIndex(
            group,
            name,
            owner.ResolveCreateSlot(null),
            maxIdentityBytes,
            CatalogIndexFactoryOptions.Resolve(keys, options));
    }

    /// <summary>
    /// Opens the named UInt64 variable-identity index and validates its persisted identity byte cap.<br/>
    /// </summary>
    /// <returns>A UInt64 key index facade over raw variable-length identity bytes.</returns>
    public LibraDexUInt64VarIdentityIndex Open()
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex UInt64 variable-identity index does not exist.");
        }

        LibraDexUInt64VarIdentityIndex index = catalog.OpenUInt64VarIdentityIndex(info);
        if (index.MaxIdentityBytes != maxIdentityBytes)
        {
            throw new InvalidDataException("The requested UInt64 variable-identity cap does not match persisted catalog metadata.");
        }

        return index;
    }

    /// <summary>
    /// Opens the named UInt64 variable-identity index when it exists, or creates it when missing.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract for create.</param>
    /// <param name="options">Optional per-index options for create.</param>
    /// <returns>A UInt64 key index facade over raw variable-length identity bytes.</returns>
    public LibraDexUInt64VarIdentityIndex CreateOrOpen(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open()
            : Create(keys, options);
    }
}

/// <summary>
/// Provides terminal lifecycle verbs for one named raw-byte variable-key/raw-variable-identity index.<br/>
/// The builder stays internal while the promoted public API decides how string/blob codecs should surface over `VV` storage.<br/>
/// </summary>
internal sealed class CatalogNamedVarKeyVarIdentityKeyBuilder
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly int maxKeyBytes;
    private readonly int maxIdentityBytes;

    internal CatalogNamedVarKeyVarIdentityKeyBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        int maxKeyBytes,
        int maxIdentityBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(owner);
        VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default8KiB.ShelfExtentSize, maxKeyBytes, maxIdentityBytes);
        this.catalog = catalog;
        this.owner = owner;
        this.group = group;
        this.name = name;
        this.maxKeyBytes = maxKeyBytes;
        this.maxIdentityBytes = maxIdentityBytes;
    }

    /// <summary>
    /// Creates the named raw `VV` index.<br/>
    /// Creation persists both variable byte caps so metadata-driven reopen can reconstruct the same raw tuple facade later.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract.</param>
    /// <param name="options">Optional per-index options.</param>
    /// <returns>A raw variable-key/variable-identity index facade.</returns>
    internal VarKeyVarIdentityIndex Create(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex raw VV index already exists.");
        }

        return catalog.CreateVarKeyVarIdentityIndex(
            group,
            name,
            owner.ResolveCreateSlot(null),
            maxKeyBytes,
            maxIdentityBytes,
            CatalogIndexFactoryOptions.Resolve(keys, options));
    }

    /// <summary>
    /// Opens the named raw `VV` index and validates its persisted key and identity byte caps.<br/>
    /// </summary>
    /// <returns>A raw variable-key/variable-identity index facade.</returns>
    internal VarKeyVarIdentityIndex Open()
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex raw VV index does not exist.");
        }

        VarKeyVarIdentityIndex index = catalog.OpenVarKeyVarIdentityIndex(info);
        if (index.MaxPhysicalKeyLength != maxKeyBytes || index.MaxIdentityLength != maxIdentityBytes)
        {
            throw new InvalidDataException("The requested raw VV byte caps do not match persisted catalog metadata.");
        }

        return index;
    }

    /// <summary>
    /// Opens the named raw `VV` index when it exists, or creates it when missing.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract for create.</param>
    /// <param name="options">Optional per-index options for create.</param>
    /// <returns>A raw variable-key/variable-identity index facade.</returns>
    internal VarKeyVarIdentityIndex CreateOrOpen(IndexKeys keys = IndexKeys.NonUnique, IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open()
            : Create(keys, options);
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
    /// The current facade requires `StringKeys.Exact` because exact storage is the primary projection used for tuple capture, mutation, and projection maintenance.<br/>
    /// </summary>
    /// <param name="stringKeys">The valid string projection profile to physically maintain.</param>
    /// <param name="directions">The string projection byte directions to physically maintain.</param>
    /// <param name="sortOrder">The key traversal order recorded for maintained string projections.</param>
    /// <param name="foldedCulture">Optional culture name for folded-text projection values; null or empty means invariant culture.</param>
    /// <param name="sortKeyCulture">Optional culture name for sort-key projection values; null or empty means invariant culture.</param>
    /// <param name="stringComparisonPolicy">Optional runtime index-level string comparison policy for managed residual comparison and prepared membership fallback.</param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the created index's inversion is complete.<br/></param>
    /// <param name="sortKeyProfiles">Optional explicit culture and comparison-options profiles to maintain as owned sort-key subindexes; null preserves the legacy single <paramref name="sortKeyCulture"/> behavior.<br/></param>
    /// <returns>A string index facade over the exact and maintained projection indexes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="stringKeys"/> is not a valid public string-key profile.<br/></exception>
    public LibraDexStringScalar8Index Create(
        StringKeys stringKeys = StringKeys.ExactAndFolded,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        string? foldedCulture = null,
        string? sortKeyCulture = null,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit,
        IReadOnlyList<LibraDexStringSortKeyProfile>? sortKeyProfiles = null)
    {
        catalog.ValidateIdentityType(typeof(ulong), CatalogIndexIdentityFamily.Scalar, nameof(Create));
        ValidateStringKeys(stringKeys);
        IReadOnlyList<LibraDexStringSortKeyProfile> effectiveSortKeyProfiles = ResolveSortKeyProfiles(stringKeys, sortKeyCulture, sortKeyProfiles);
        if (effectiveSortKeyProfiles.Count != 0)
        {
            stringKeys |= StringKeys.SortKey;
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
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0)
        {
            exactReversedSlot = ResolveAutoSlot();
            exactReversed = catalog.CreateVarKeyScalar8Index($"{name}#exact-rev", exactReversedSlot, StringVarKeyPhysicalMaxLength);
        }

        VarKeyScalar8Index? folded = null;
        int foldedSlot = -1;
        if (HasFoldedText(stringKeys))
        {
            foldedSlot = ResolveAutoSlot();
            folded = catalog.CreateVarKeyScalar8Index($"{name}#folded", foldedSlot, StringVarKeyPhysicalMaxLength);
        }

        List<LibraDexStringSortKeyProjectionBinding> sortKeyBindings = new(effectiveSortKeyProfiles.Count);
        List<LibraDexStringSortKeyProjectionMetadata> sortKeyMetadata = new(effectiveSortKeyProfiles.Count);
        int sortKeySlot = -1;
        for (int i = 0; i < effectiveSortKeyProfiles.Count; i++)
        {
            LibraDexStringSortKeyProfile profile = effectiveSortKeyProfiles[i];
            CultureInfo culture = ResolveSortKeyCulture(profile.CultureName);
            ValidateSortKeyCompareOptions(culture, profile.CompareOptions);
            SortVersion version = culture.CompareInfo.Version;
            int slot = ResolveAutoSlot();
            string physicalName = CreateSortKeyPhysicalName(name, i);
            VarKeyScalar8Index physical = catalog.CreateVarKeyScalar8Index(physicalName, slot, StringVarKeyPhysicalMaxLength);
            string cultureName = culture.Equals(CultureInfo.InvariantCulture) ? string.Empty : culture.Name;
            sortKeyBindings.Add(new LibraDexStringSortKeyProjectionBinding(
                physical,
                physicalName,
                cultureName,
                profile.CompareOptions,
                version.FullVersion,
                version.SortId));
            sortKeyMetadata.Add(new LibraDexStringSortKeyProjectionMetadata(
                slot,
                cultureName,
                profile.CompareOptions,
                version.FullVersion,
                version.SortId));
            if (i == 0)
            {
                sortKeySlot = slot;
            }
        }

        VarKeyScalar8Index? foldedReversed = null;
        int foldedReversedSlot = -1;
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0 &&
            HasFoldedText(stringKeys))
        {
            foldedReversedSlot = ResolveAutoSlot();
            foldedReversed = catalog.CreateVarKeyScalar8Index($"{name}#folded-rev", foldedReversedSlot, StringVarKeyPhysicalMaxLength);
        }

        VarKeyScalar8Index? normalized = null;
        int normalizedSlot = -1;
        if (HasNormalizedText(stringKeys))
        {
            normalizedSlot = ResolveAutoSlot();
            normalized = catalog.CreateVarKeyScalar8Index($"{name}#normalized", normalizedSlot, StringVarKeyPhysicalMaxLength);
        }

        VarKeyScalar8Index? normalizedReversed = null;
        int normalizedReversedSlot = -1;
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0 &&
            HasNormalizedText(stringKeys))
        {
            normalizedReversedSlot = ResolveAutoSlot();
            normalizedReversed = catalog.CreateVarKeyScalar8Index($"{name}#normalized-rev", normalizedReversedSlot, StringVarKeyPhysicalMaxLength);
        }

        LibraDexStringComparisonPolicy? effectiveStringComparisonPolicy = stringComparisonPolicy ?? catalog.Options.StringComparisonPolicy;
        CatalogIndexMetadata metadata = CreateStringMetadata(stringKeys, exactReversedSlot, foldedSlot, sortKeySlot, foldedReversedSlot, normalizedSlot, normalizedReversedSlot, directions, sortOrder, foldedCulture, sortKeyMetadata, effectiveStringComparisonPolicy);
        VarKeyScalar8Index exact = catalog.CreateVarKeyScalar8Index(name, exactSlot, StringVarKeyPhysicalMaxLength, metadata);
        return new LibraDexStringScalar8Index(
            catalog,
            group,
            name,
            exact,
            exactReversed,
            folded,
            sortKeyBindings,
            foldedReversed,
            normalized,
            normalizedReversed,
            foldedCulture,
            LibraDexTextNormalization.FormC,
            effectiveStringComparisonPolicy,
            identityLookupMode);
    }

    /// <summary>
    /// Adds and backfills one culture-aware sort-key subindex beneath an existing logical string index.<br/>
    /// Exact culture name plus <see cref="CompareOptions"/> form semantic identity; adding an already persisted profile is an idempotent no-op that simply reopens the current logical facade.<br/>
    /// The physical companion is fully populated before replacement owner metadata is published, so interrupted work cannot expose a partially ready subindex.<br/>
    /// </summary>
    /// <param name="profile">The culture and comparison-options profile to add.<br/></param>
    /// <param name="stringComparisonPolicy">Optional runtime comparison policy for the returned reopened facade.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy for the returned reopened facade.<br/></param>
    /// <returns>A reopened logical string facade that includes the requested profile.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the existing catalog entry is not a string/scalar logical index with complete metadata.<br/></exception>
    public LibraDexStringScalar8Index AddSortKeyProfile(
        LibraDexStringSortKeyProfile profile,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        catalog.ValidateIdentityType(typeof(ulong), CatalogIndexIdentityFamily.Scalar, nameof(AddSortKeyProfile));
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
            throw new InvalidDataException($"String index '{group}/{name}' was not found in the catalog.");

        ValidateStringInfo(info);
        CultureInfo culture = ResolveSortKeyCulture(profile.CultureName);
        ValidateSortKeyCompareOptions(culture, profile.CompareOptions);
        string cultureName = culture.Equals(CultureInfo.InvariantCulture) ? string.Empty : culture.Name;
        IReadOnlyList<LibraDexStringSortKeyProjectionInfo> existingProfiles = ResolveOpenSortKeyProfiles(info);
        for (var i = 0; i < existingProfiles.Count; i++)
        {
            if (string.Equals(existingProfiles[i].CultureName, cultureName, StringComparison.Ordinal) &&
                existingProfiles[i].CompareOptions == profile.CompareOptions)
            {
                return Open(stringComparisonPolicy, identityLookupMode);
            }
        }

        if (!catalog.TryGetCatalogIndexMetadata(info.SlotIndex, out CatalogIndexMetadata metadata))
            throw new InvalidDataException($"String index '{group}/{name}' does not have complete catalog metadata.");

        int ordinal = existingProfiles.Count;
        string physicalName = CreateSortKeyPhysicalName(name, ordinal);
        CatalogIndexInfo[] active = owner.List();
        for (var i = 0; i < active.Length; i++)
        {
            if (string.IsNullOrEmpty(active[i].Group) &&
                string.Equals(active[i].Name, physicalName, StringComparison.Ordinal))
            {
                catalog.DropUnpublishedProjection(active[i].SlotIndex);
                break;
            }
        }

        int slot = owner.ResolveCreateSlot(slotIndex: null);
        VarKeyScalar8Index? physical = null;
        var created = false;
        var published = false;
        try
        {
            physical = catalog.CreateVarKeyScalar8Index(physicalName, slot, StringVarKeyPhysicalMaxLength);
            created = true;
            using (LibraDexStringScalar8Index exact = Open(stringComparisonPolicy, IdentityLookupMode.Explicit))
            {
                foreach (LibraDexRuntimeTuple tuple in owner[group].IterateTuples(exact))
                {
                    string? value = tuple.Key switch
                    {
                        null => null,
                        string text => text,
                        _ => throw new InvalidDataException($"String index '{group}/{name}' returned a non-string exact key while adding a sort-key profile.")
                    };
                    byte[] key = value is null
                        ? new byte[] { 0x00 }
                        : culture.CompareInfo.GetSortKey(value, profile.CompareOptions).KeyData;
                    if (tuple.Identity is not ulong identity)
                        throw new InvalidDataException($"String index '{group}/{name}' returned a non-UInt64 identity while adding a sort-key profile.");
                    _ = physical.Insert(key, identity);
                }
            }

            SortVersion version = culture.CompareInfo.Version;
            List<LibraDexStringSortKeyProjectionMetadata> profiles = new(existingProfiles.Count + 1);
            for (var i = 0; i < existingProfiles.Count; i++)
            {
                LibraDexStringSortKeyProjectionInfo existing = existingProfiles[i];
                profiles.Add(new LibraDexStringSortKeyProjectionMetadata(
                    existing.SlotIndex,
                    existing.CultureName,
                    existing.CompareOptions,
                    existing.SortVersionFullVersion,
                    existing.SortVersionId));
            }
            profiles.Add(new LibraDexStringSortKeyProjectionMetadata(
                slot,
                cultureName,
                profile.CompareOptions,
                version.FullVersion,
                version.SortId));

            IReadOnlyList<LibraDexIndexProjectionSpec> projections = metadata.Projections;
            if (!projections.Any(static projection => projection.Kind == LibraDexIndexProjectionKind.SortKey))
            {
                List<LibraDexIndexProjectionSpec> expanded = new(projections.Count + 1);
                expanded.AddRange(projections);
                expanded.Add(new LibraDexIndexProjectionSpec(
                    LibraDexIndexProjectionKind.SortKey,
                    LibraDexIndexByteDirection.Forward,
                    metadata.SortOrder));
                projections = expanded;
            }

            catalog.UpdateCatalogIndexMetadata(info.SlotIndex, metadata with
            {
                StringKeys = metadata.StringKeys | StringKeys.SortKey,
                Projections = projections,
                SortKeyProjectionSlotIndex = profiles[0].SlotIndex,
                SortKeyCulture = profiles[0].CultureName,
                SortKeyProfiles = profiles
            });
            published = true;
        }
        catch
        {
            if (created && !published)
                catalog.DropUnpublishedProjection(slot);
            throw;
        }
        finally
        {
            physical?.Dispose();
        }

        return Open(stringComparisonPolicy, identityLookupMode);
    }

    /// <summary>
    /// Opens a persisted string-key/scalar-identity index and its owned projection subindexes.<br/>
    /// The logical index metadata supplies the projection slots, cultures, and variable-key length, so callers do not need to remember projection slot numbers after catalog creation.<br/>
    /// </summary>
    /// <param name="stringComparisonPolicy">Optional runtime index-level string comparison policy for the reopened facade.</param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the opened index's inversion is complete.<br/></param>
    /// <returns>A string index facade over the reopened exact and maintained projection indexes.</returns>
    public LibraDexStringScalar8Index Open(
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        catalog.ValidateIdentityType(typeof(ulong), CatalogIndexIdentityFamily.Scalar, nameof(Open));
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
        IReadOnlyList<LibraDexStringSortKeyProjectionInfo> sortKeyProfiles = ResolveOpenSortKeyProfiles(info);
        List<LibraDexStringSortKeyProjectionBinding> sortKeyBindings = new(sortKeyProfiles.Count);
        for (int i = 0; i < sortKeyProfiles.Count; i++)
        {
            LibraDexStringSortKeyProjectionInfo profile = sortKeyProfiles[i];
            sortKeyBindings.Add(new LibraDexStringSortKeyProjectionBinding(
                catalog.OpenVarKeyScalar8Index(profile.SlotIndex, info.VarKeyMaxKeyLength),
                CreateSortKeyPhysicalName(name, i),
                profile.CultureName,
                profile.CompareOptions,
                profile.SortVersionFullVersion,
                profile.SortVersionId));
        }
        VarKeyScalar8Index? foldedReversed = info.FoldedReversedProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.FoldedReversedProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        VarKeyScalar8Index? normalized = info.NormalizedProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.NormalizedProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        VarKeyScalar8Index? normalizedReversed = info.NormalizedReversedProjectionSlotIndex >= 0
            ? catalog.OpenVarKeyScalar8Index(info.NormalizedReversedProjectionSlotIndex, info.VarKeyMaxKeyLength)
            : null;
        return new LibraDexStringScalar8Index(
            catalog,
            group,
            name,
            exact,
            exactReversed,
            folded,
            sortKeyBindings,
            foldedReversed,
            normalized,
            normalizedReversed,
            info.FoldedCulture,
            info.FoldedNormalization,
            ResolveOpenStringComparisonPolicy(info, stringComparisonPolicy),
            identityLookupMode);
    }

    /// <summary>
    /// Opens an existing string-key/scalar-identity index or creates it when the logical index is missing.<br/>
    /// Creation persists projection ownership metadata; reopen uses that metadata so generated callers can keep targeting the logical index name only.<br/>
    /// String storage uses the standard variable-key cap; callers do not tune varlen key length per index.<br/>
    /// When creation is required, the current facade requires `StringKeys.Exact` because exact storage is the primary projection used for tuple capture, mutation, and projection maintenance.<br/>
    /// </summary>
    /// <param name="stringKeys">The valid string projection profile to physically maintain on create.</param>
    /// <param name="directions">The string projection byte directions to physically maintain on create.</param>
    /// <param name="sortOrder">The key traversal order recorded for maintained string projections when the index is created.</param>
    /// <param name="foldedCulture">Optional culture name for folded-text projection values; null or empty means invariant culture.</param>
    /// <param name="sortKeyCulture">Optional culture name for sort-key projection values; null or empty means invariant culture.</param>
    /// <param name="stringComparisonPolicy">Optional runtime index-level string comparison policy for managed residual comparison and prepared membership fallback.</param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the returned index's inversion is complete.<br/></param>
    /// <param name="sortKeyProfiles">Optional explicit culture and comparison-options profiles used only when creation is required; null preserves the legacy single <paramref name="sortKeyCulture"/> behavior.<br/></param>
    /// <returns>A string index facade over the exact and maintained projection indexes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when creation is required and <paramref name="stringKeys"/> is not a valid public string-key profile.<br/></exception>
    public LibraDexStringScalar8Index CreateOrOpen(
        StringKeys stringKeys = StringKeys.ExactAndFolded,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        string? foldedCulture = null,
        string? sortKeyCulture = null,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit,
        IReadOnlyList<LibraDexStringSortKeyProfile>? sortKeyProfiles = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open(stringComparisonPolicy, identityLookupMode)
            : Create(stringKeys, directions, sortOrder, foldedCulture, sortKeyCulture, stringComparisonPolicy, identityLookupMode, sortKeyProfiles);
    }

    private CatalogIndexMetadata CreateStringMetadata(
        StringKeys stringKeys,
        int exactReversedSlot,
        int foldedSlot,
        int sortKeySlot,
        int foldedReversedSlot,
        int normalizedSlot,
        int normalizedReversedSlot,
        LibraDexProjectionDirectionSet directions,
        LibraDexIndexSortOrder sortOrder,
        string? foldedCulture,
        IReadOnlyList<LibraDexStringSortKeyProjectionMetadata> sortKeyProfiles,
        LibraDexStringComparisonPolicy? stringComparisonPolicy)
    {
        List<LibraDexIndexProjectionSpec> projections = CreateProjectionKinds(stringKeys)
            .Select(kind => new LibraDexIndexProjectionSpec(kind, LibraDexIndexByteDirection.Forward, sortOrder))
            .ToList();
        if (foldedReversedSlot >= 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Reversed, sortOrder));
        }
        if (exactReversedSlot >= 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, sortOrder));
        }
        if (normalizedReversedSlot >= 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.NormalizedText, LibraDexIndexByteDirection.Reversed, sortOrder));
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
            sortOrder,
            projections,
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            StringVarKeyPhysicalMaxLength,
            0,
            exactReversedSlot,
            foldedSlot,
            sortKeySlot,
            foldedReversedSlot,
            foldedCulture ?? string.Empty,
            sortKeyProfiles.Count == 0 ? string.Empty : sortKeyProfiles[0].CultureName,
            stringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            stringComparisonPolicy?.CompareOptions ?? CompareOptions.None,
            stringComparisonPolicy?.CultureName ?? string.Empty,
            stringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            FoldedNormalization: LibraDexTextNormalization.FormC,
            NormalizedProjectionSlotIndex: normalizedSlot,
            NormalizedReversedProjectionSlotIndex: normalizedReversedSlot,
            SortKeyProfiles: sortKeyProfiles);
    }

    private static IReadOnlyList<LibraDexIndexProjectionKind> CreateProjectionKinds(StringKeys stringKeys)
    {
        List<LibraDexIndexProjectionKind> kinds = new();
        kinds.Add(LibraDexIndexProjectionKind.Exact);

        if (HasFoldedText(stringKeys))
        {
            kinds.Add(LibraDexIndexProjectionKind.FoldedText);
        }

        if (HasSortKey(stringKeys))
        {
            kinds.Add(LibraDexIndexProjectionKind.SortKey);
        }

        if (HasNormalizedText(stringKeys))
        {
            kinds.Add(LibraDexIndexProjectionKind.NormalizedText);
        }

        return kinds;
    }

    private static void ValidateStringKeys(StringKeys stringKeys)
    {
        const StringKeys supported = StringKeys.Exact | StringKeys.Folded | StringKeys.SortKey | StringKeys.Normalized;
        if ((stringKeys & StringKeys.Exact) == 0 || (stringKeys & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stringKeys), stringKeys, "The string-key profile is not supported by the string facade.");
        }
    }

    private static bool HasFoldedText(StringKeys stringKeys)
        => (stringKeys & StringKeys.Folded) != 0;

    private static bool HasSortKey(StringKeys stringKeys)
        => (stringKeys & StringKeys.SortKey) != 0;

    private static bool HasNormalizedText(StringKeys stringKeys)
        => (stringKeys & StringKeys.Normalized) != 0;

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

    /// <summary>
    /// Resolves the compatibility single-culture arguments and the new explicit profile collection into one ordered semantic list.<br/>
    /// Explicit profiles imply `StringKeys.SortKey`; duplicate culture/options pairs and ambiguous simultaneous legacy culture input are rejected before any catalog slots are allocated.<br/>
    /// </summary>
    /// <param name="stringKeys">The requested string projection flags.<br/></param>
    /// <param name="legacyCulture">The legacy single sort-key culture argument.<br/></param>
    /// <param name="profiles">The optional explicit multi-profile collection.<br/></param>
    /// <returns>The validated profile list in caller-declared preference order.<br/></returns>
    private static IReadOnlyList<LibraDexStringSortKeyProfile> ResolveSortKeyProfiles(
        StringKeys stringKeys,
        string? legacyCulture,
        IReadOnlyList<LibraDexStringSortKeyProfile>? profiles)
    {
        if (profiles is null)
        {
            if (!HasSortKey(stringKeys))
            {
                return Array.Empty<LibraDexStringSortKeyProfile>();
            }

            CultureInfo culture = ResolveSortKeyCulture(legacyCulture);
            string cultureName = culture.Equals(CultureInfo.InvariantCulture) ? string.Empty : culture.Name;
            return new[] { new LibraDexStringSortKeyProfile(cultureName, CompareOptions.IgnoreCase) };
        }

        if (!string.IsNullOrEmpty(legacyCulture))
        {
            throw new ArgumentException("Use either sortKeyCulture or sortKeyProfiles, not both.", nameof(profiles));
        }
        if (profiles.Count == 0)
        {
            if (HasSortKey(stringKeys))
            {
                throw new ArgumentException("An explicit empty sortKeyProfiles collection conflicts with StringKeys.SortKey.", nameof(profiles));
            }

            return Array.Empty<LibraDexStringSortKeyProfile>();
        }

        LibraDexStringSortKeyProfile[] result = new LibraDexStringSortKeyProfile[profiles.Count];
        HashSet<(string Culture, CompareOptions Options)> unique = new();
        for (int i = 0; i < result.Length; i++)
        {
            LibraDexStringSortKeyProfile profile = profiles[i];
            CultureInfo culture = ResolveSortKeyCulture(profile.CultureName);
            ValidateSortKeyCompareOptions(culture, profile.CompareOptions);
            string cultureName = culture.Equals(CultureInfo.InvariantCulture) ? string.Empty : culture.Name;
            if (!unique.Add((cultureName, profile.CompareOptions)))
            {
                throw new ArgumentException(
                    $"Sort-key profile '{cultureName}' with options '{profile.CompareOptions}' is duplicated.",
                    nameof(profiles));
            }

            result[i] = new LibraDexStringSortKeyProfile(cultureName, profile.CompareOptions);
        }

        return result;
    }

    /// <summary>
    /// Resolves persisted profile metadata for reopen, including catalogs created before profile lists were introduced.<br/>
    /// Legacy metadata synthesizes its historical single ignore-case profile so existing files remain source- and storage-compatible.<br/>
    /// </summary>
    /// <param name="info">The disconnected logical index metadata.<br/></param>
    /// <returns>The persisted or synthesized profile collection.<br/></returns>
    private static IReadOnlyList<LibraDexStringSortKeyProjectionInfo> ResolveOpenSortKeyProfiles(CatalogIndexInfo info)
    {
        if (info.SortKeyProfiles is { Count: > 0 } profiles)
        {
            return profiles;
        }
        if (info.SortKeyProjectionSlotIndex < 0)
        {
            return Array.Empty<LibraDexStringSortKeyProjectionInfo>();
        }

        return new[]
        {
            new LibraDexStringSortKeyProjectionInfo(
                info.SortKeyProjectionSlotIndex,
                info.SortKeyCulture,
                CompareOptions.IgnoreCase,
                0,
                Guid.Empty)
        };
    }

    /// <summary>
    /// Resolves one optional profile culture using the same invariant-default convention as condition materialization.<br/>
    /// Culture lookup occurs before physical allocation so invalid names cannot leave partially created projection slots.<br/>
    /// </summary>
    /// <param name="cultureName">The optional culture name.<br/></param>
    /// <returns>The cached named culture or invariant culture.<br/></returns>
    private static CultureInfo ResolveSortKeyCulture(string? cultureName)
    {
        return string.IsNullOrEmpty(cultureName)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(cultureName);
    }

    /// <summary>
    /// Validates that the active runtime can generate sort keys for one culture/options profile.<br/>
    /// CompareInfo performs the authoritative option validation; this bounded empty-string probe runs before any catalog slots are allocated.<br/>
    /// </summary>
    /// <param name="culture">The resolved profile culture.<br/></param>
    /// <param name="compareOptions">The requested comparison options.<br/></param>
    private static void ValidateSortKeyCompareOptions(CultureInfo culture, CompareOptions compareOptions)
    {
        _ = culture.CompareInfo.GetSortKey(string.Empty, compareOptions);
    }

    /// <summary>
    /// Creates the deterministic owned physical name for one profile ordinal.<br/>
    /// Ordinal zero preserves the historical `#sortkey` companion name; later profiles use an ordinal suffix while semantic identity remains persisted in metadata.<br/>
    /// </summary>
    /// <param name="sourceName">The logical string index name.<br/></param>
    /// <param name="ordinal">The zero-based persisted profile ordinal.<br/></param>
    /// <returns>The deterministic owned physical companion name.<br/></returns>
    private static string CreateSortKeyPhysicalName(string sourceName, int ordinal)
    {
        return ordinal == 0 ? $"{sourceName}#sortkey" : $"{sourceName}#sortkey-{ordinal}";
    }

    private static string GetStableTypeName(Type type)
        => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
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
    private readonly string Group;
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
        this.Group = group;
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
    /// <param name="keys">The index-wide duplicate-key contract.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the created index's inversion is complete.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Create(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        if (owner.TryGetInfo(Group, name, out _))
        {
            throw new InvalidOperationException("The requested grouped LibraDex index already exists.");
        }

        IndexOptions resolvedOptions = CatalogIndexFactoryOptions.Resolve(keys, options);
        LibraDexIndexShapeSpec? logicalShape = CreateLogicalShape(resolvedOptions.Keys, resolvedOptions.SortOrder);
        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = CreateExactReversedProjection(resolvedOptions, out int exactReversedProjectionSlotIndex);
        return catalog.CreateGenericIndex<TKey, TIdentity>(
            name,
            owner.ResolveCreateSlot(null),
            resolvedOptions,
            keyWidth,
            identityWidth,
            keyFamily,
            identityFamily,
            Group,
            logicalShape,
            exactReversedProjectionSlotIndex,
            exactReversedProjection,
            identityLookupMode);
    }

    /// <summary>
    /// Opens an existing grouped index by index-set and index name.<br/>
    /// The lookup is metadata-driven and does not require the caller to know the fixed directory slot after the catalog has been created.<br/>
    /// </summary>
    /// <param name="keys">The fallback duplicate-key contract for older scaffold entries without rich metadata.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the opened index's inversion is complete.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> Open(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        if (!owner.TryGetInfo(Group, name, out CatalogIndexInfo info))
        {
            throw new InvalidDataException("The requested grouped LibraDex index does not exist.");
        }

        return catalog.OpenGenericIndex<TKey, TIdentity>(
            info.SlotIndex,
            CatalogIndexFactoryOptions.Resolve(keys, options),
            keyWidth,
            identityWidth,
            identityLookupMode);
    }

    /// <summary>
    /// Opens an existing grouped index or creates it when it does not exist.<br/>
    /// This is the low-friction setup path for applications that prefer stable index-set/index names over manual slot assignment.<br/>
    /// </summary>
    /// <param name="keys">The index-wide duplicate-key contract for a new index, or fallback contract for an older entry without rich metadata.<br/></param>
    /// <param name="options">Optional per-index options.<br/></param>
    /// <param name="identityLookupMode">The session-local identity-inversion lifecycle policy.<br/><see cref="IdentityLookupMode.BuildOnOpen"/> blocks until the returned index's inversion is complete.<br/></param>
    /// <returns>A typed index handle owned by the catalog lifetime.<br/></returns>
    public LibraDexIndex<TKey, TIdentity> CreateOrOpen(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        return owner.TryGetInfo(Group, name, out _)
            ? Open(keys, options, identityLookupMode)
            : Create(keys, options, identityLookupMode);
    }


    /// <summary>
    /// Creates logical shape metadata for fixed binary-key indexes that request maintained byte-direction projections.<br/>
    /// Non-binary typed builders return <see langword="null"/> so existing scalar metadata remains unchanged unless projection intent exists.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract selected for this index.</param>
    /// <returns>A logical shape descriptor for binary projection planning, or <see langword="null"/> for ordinary scalar indexes.</returns>
    private LibraDexIndexShapeSpec? CreateLogicalShape(IndexKeys keys, LibraDexIndexSortOrder sortOrder)
    {
        if (keyFamily != CatalogIndexKeyFamily.Blob || typeof(TKey) != typeof(byte[]))
        {
            return null;
        }

        List<LibraDexIndexProjectionSpec> projections = new()
        {
            new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Forward, sortOrder)
        };
        if ((directions & LibraDexProjectionDirectionSet.Reversed) != 0)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, sortOrder));
        }

        return new LibraDexIndexShapeSpec(
            Group,
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
            sortOrder,
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
/// <param name="ItemCount">The catalog directory's cached item count for the index, when maintained; use <see cref="IsDirectoryItemCountAuthoritative"/> before treating it as a live count.</param>
/// <param name="Generation">The index generation recorded in the directory slot.</param>
/// <param name="KeyProfileId">The persisted key profile identifier.</param>
/// <param name="IdentityProfileId">The persisted identity profile identifier.</param>
/// <param name="RouterProfileId">The persisted router profile identifier.</param>
/// <param name="AllocationClassId">The persisted allocation class identifier.</param>
/// <param name="Group">The persisted index set name when rich metadata is present; otherwise an empty string.</param>
/// <param name="KeyTypeName">The persisted CLR key type name when rich metadata is present; otherwise an empty string.</param>
/// <param name="IdentityTypeName">The persisted CLR identity type name when rich metadata is present; otherwise an empty string.</param>
/// <param name="KeyFamily">The logical key family recorded in rich metadata.</param>
/// <param name="IdentityFamily">The logical identity family recorded in rich metadata.</param>
/// <param name="KeyContract">The persisted duplicate-key contract recorded in rich metadata.</param>
/// <param name="IdentityKeyMultiplicity">The persisted identity-to-key multiplicity contract recorded in rich metadata.</param>
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
/// <param name="FoldedNormalization">The persisted canonical-normalization contract used before folding text.</param>
/// <param name="NormalizedProjectionSlotIndex">The owned case-preserving normalized-text projection slot, or -1 when not maintained.</param>
/// <param name="NormalizedReversedProjectionSlotIndex">The owned reversed case-preserving normalized-text projection slot, or -1 when not maintained.</param>
/// <param name="SortKeyProfiles">The persisted culture-aware sort-key profiles and their owned physical slots, or null for metadata created before profile lists were introduced.</param>
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
    IdentityKeyMultiplicity IdentityKeyMultiplicity,
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
    string StringComparisonCustomComparerTypeName,
    LibraDexTextNormalization FoldedNormalization = LibraDexTextNormalization.None,
    int NormalizedProjectionSlotIndex = -1,
    int NormalizedReversedProjectionSlotIndex = -1,
    IReadOnlyList<LibraDexStringSortKeyProjectionInfo>? SortKeyProfiles = null)
{
    /// <summary>
    /// Gets whether <see cref="ItemCount"/> is an authoritative live count for this catalog entry.<br/>
    /// Fixed-N BigInteger scalar and fixed-N variable-identity shapes count from shelf metadata instead of maintaining the directory count when ordinary shelf writes occur.<br/>
    /// </summary>
    public bool IsDirectoryItemCountAuthoritative =>
        Projections.Count == 0 ||
        Projections[0].Kind is not (LibraDexIndexProjectionKind.BigIntFixed or LibraDexIndexProjectionKind.BigIntFixedVarIdentity);

    /// <summary>
    /// Attempts to reconstruct the logical shape descriptor recorded for this catalog entry.<br/>
    /// Basic typed entries with durable CLR/family metadata synthesize their implicit forward exact projection; entries without those contracts, or whose CLR type names cannot be resolved in the current process, return <see langword="false"/>.<br/>
    /// </summary>
    /// <param name="shape">The reconstructed logical shape when available.</param>
    /// <returns><see langword="true"/> when a logical shape descriptor could be reconstructed; otherwise <see langword="false"/>.</returns>
    public bool TryCreateShape([NotNullWhen(true)] out LibraDexIndexShapeSpec? shape)
    {
        shape = null;
        if (string.IsNullOrWhiteSpace(KeyTypeName) ||
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

        IReadOnlyList<LibraDexIndexProjectionSpec> projections = Projections.Count == 0
            ? new[]
            {
                new LibraDexIndexProjectionSpec(
                    LibraDexIndexProjectionKind.Exact,
                    LibraDexIndexByteDirection.Forward,
                    SortOrder)
            }
            : Projections;
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
            projections,
            CompositeParts,
            IdentityKeyMultiplicity);
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
                Group: string.Empty,
                IndexName: slot.Name,
                KeyTypeName: string.Empty,
                IdentityTypeName: string.Empty,
                KeyFamily: CatalogIndexKeyFamily.Unknown,
                IdentityFamily: CatalogIndexIdentityFamily.Unknown,
                KeyContract: IndexKeys.NonUnique,
                StringKeys: StringKeys.Exact,
                GuidKeys: GuidKeys.Exact,
                DateKeys: DateKeys.Exact,
                DateTimeKeyEncoding: DateTimeKeyEncoding.CalendarSdt,
                Directions: LibraDexProjectionDirectionSet.Forward,
                SortOrder: LibraDexIndexSortOrder.Ascending,
                Projections: Array.Empty<LibraDexIndexProjectionSpec>(),
                CompositeParts: Array.Empty<LibraDexCompositeKeyPartSpec>(),
                VarKeyMaxKeyLength: 0,
                VarIdentityMaxLength: 0,
                ExactReversedProjectionSlotIndex: -1,
                FoldedProjectionSlotIndex: -1,
                SortKeyProjectionSlotIndex: -1,
                FoldedReversedProjectionSlotIndex: -1,
                FoldedCulture: string.Empty,
                SortKeyCulture: string.Empty,
                StringComparisonPolicyKind: LibraDexStringComparisonPolicyKind.Invariant,
                StringComparisonCompareOptions: CompareOptions.None,
                StringComparisonCulture: string.Empty,
                StringComparisonCustomComparerTypeName: string.Empty,
                HasShapeMetadata: false,
                IdentityKeyMultiplicity: IdentityKeyMultiplicity.MultipleKeysPerIdentity));
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
            metadata.IdentityKeyMultiplicity,
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
            metadata.StringComparisonCustomComparerTypeName,
            metadata.FoldedNormalization,
            metadata.NormalizedProjectionSlotIndex,
            metadata.NormalizedReversedProjectionSlotIndex,
            CreateSortKeyProfileInfo(metadata.SortKeyProfiles));
    }

    private static IReadOnlyList<LibraDexStringSortKeyProjectionInfo> CreateSortKeyProfileInfo(
        IReadOnlyList<LibraDexStringSortKeyProjectionMetadata>? profiles)
    {
        if (profiles is null || profiles.Count == 0)
        {
            return Array.Empty<LibraDexStringSortKeyProjectionInfo>();
        }

        LibraDexStringSortKeyProjectionInfo[] result = new LibraDexStringSortKeyProjectionInfo[profiles.Count];
        for (int i = 0; i < result.Length; i++)
        {
            LibraDexStringSortKeyProjectionMetadata profile = profiles[i];
            result[i] = new LibraDexStringSortKeyProjectionInfo(
                profile.SlotIndex,
                profile.CultureName,
                profile.CompareOptions,
                profile.SortVersionFullVersion,
                profile.SortVersionId);
        }

        return result;
    }
}

/// <summary>
/// Describes one catalog index set and the active indexes that belong to that shared identity universe.<br/>
/// The snapshot is disconnected from the catalog session, so callers can inspect it without holding catalog-directory state open.<br/>
/// </summary>
/// <param name="Name">The index-set name.</param>
/// <param name="Indexes">The active indexes declared in this index set, in catalog directory order.</param>
public readonly record struct CatalogIndexSetInfo(
    string Name,
    CatalogIndexInfo[] Indexes);

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
            CatalogIndexFactoryOptions.Resolve(keys, options),
            null,
            null,
            CatalogIndexKeyFamily.Guid,
            CatalogIndexIdentityFamily.Scalar);
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
        return catalog.CreateGenericIndex<TKey, TIdentity>(name, slotIndex, CatalogIndexFactoryOptions.Resolve(keys, options), null, null);
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
        return catalog.OpenGenericIndex<TKey, TIdentity>(slotIndex, CatalogIndexFactoryOptions.Resolve(keys, options), null, null);
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
        return catalog.CreateOrOpenGenericIndex<TKey, TIdentity>(name, slotIndex, CatalogIndexFactoryOptions.Resolve(keys, options), null, null);
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
            CatalogIndexFactoryOptions.Resolve(keys, options),
            null,
            identityWidth,
            CatalogIndexKeyFamily.Scalar,
            CatalogIndexIdentityFamily.Blob);
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
            CatalogIndexFactoryOptions.Resolve(keys, options),
            keyWidth,
            null,
            CatalogIndexKeyFamily.Blob,
            CatalogIndexIdentityFamily.Scalar);
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
            CatalogIndexFactoryOptions.Resolve(keys, options),
            null,
            null,
            CatalogIndexKeyFamily.Guid,
            CatalogIndexIdentityFamily.Scalar);
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

/// <summary>
/// Provides lifecycle verbs for one named bounded variable-length blob-key/scalar-identity index.<br/>
/// The explicit maximum payload size keeps storage bounded while allowing ordinary CLR byte arrays of different lengths to share one logical index.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type encoded into the physical 8-byte identity lane.<br/></typeparam>
public sealed class CatalogNamedVariableBlobKeyBuilder<TIdentity>
{
    private readonly Catalog catalog;
    private readonly CatalogIndexFactories owner;
    private readonly string group;
    private readonly string name;
    private readonly int maxKeyBytes;

    internal CatalogNamedVariableBlobKeyBuilder(
        Catalog catalog,
        CatalogIndexFactories owner,
        string group,
        string name,
        int maxKeyBytes)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (maxKeyBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxKeyBytes), maxKeyBytes, "Variable blob indexes require a positive maximum payload byte count.");

        _ = checked(maxKeyBytes + 1);
        this.group = group;
        this.name = name;
        this.maxKeyBytes = maxKeyBytes;
    }

    /// <summary>
    /// Creates the bounded variable-blob index and persists its logical payload cap.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract for this index.<br/></param>
    /// <param name="options">Optional per-index creation options.<br/></param>
    /// <returns>The newly created variable-blob index facade.</returns>
    public LibraDexVariableBlobScalar8Index<TIdentity> Create(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        if (owner.TryGetInfo(group, name, out _))
            throw new InvalidOperationException("The requested grouped LibraDex variable-blob index already exists.");

        return catalog.CreateVariableBlobScalar8Index<TIdentity>(
            group,
            name,
            owner.ResolveCreateSlot(null),
            maxKeyBytes,
            CatalogIndexFactoryOptions.Resolve(keys, options));
    }

    /// <summary>
    /// Opens the persisted bounded variable-blob index and validates its identity type and logical payload cap.<br/>
    /// </summary>
    /// <returns>The reopened variable-blob index facade.</returns>
    public LibraDexVariableBlobScalar8Index<TIdentity> Open()
    {
        if (!owner.TryGetInfo(group, name, out CatalogIndexInfo info))
            throw new InvalidDataException($"Variable blob index '{group}/{name}' was not found in the catalog.");

        return catalog.OpenVariableBlobScalar8Index<TIdentity>(info, maxKeyBytes);
    }

    /// <summary>
    /// Opens the bounded variable-blob index when present, or creates it with the supplied contract when missing.<br/>
    /// </summary>
    /// <param name="keys">The duplicate-key contract used only when creation is required.<br/></param>
    /// <param name="options">Optional creation options used only when creation is required.<br/></param>
    /// <returns>The created or reopened variable-blob index facade.</returns>
    public LibraDexVariableBlobScalar8Index<TIdentity> CreateOrOpen(
        IndexKeys keys = IndexKeys.NonUnique,
        IndexOptions? options = null)
    {
        return owner.TryGetInfo(group, name, out _)
            ? Open()
            : Create(keys, options);
    }
}

/// <summary>
/// Describes one persisted culture-aware sort-key projection owned by a logical string index.<br/>
/// The sort-version fields identify the globalization table that produced the stored bytes so reopen can reject incompatible runtime collation rather than mixing incomparable keys.<br/>
/// </summary>
/// <param name="SlotIndex">The fixed catalog slot containing the owned physical projection.<br/></param>
/// <param name="CultureName">The culture name, or an empty string for invariant culture.<br/></param>
/// <param name="CompareOptions">The exact comparison options used to create sort keys.<br/></param>
/// <param name="SortVersionFullVersion">The persisted .NET sort-table version, or zero for a legacy projection without a recorded signature.<br/></param>
/// <param name="SortVersionId">The persisted .NET sort identifier, or an empty GUID for a legacy projection without a recorded signature.<br/></param>
public readonly record struct LibraDexStringSortKeyProjectionInfo(
    int SlotIndex,
    string CultureName,
    CompareOptions CompareOptions,
    int SortVersionFullVersion,
    Guid SortVersionId);
